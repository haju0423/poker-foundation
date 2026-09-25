using System;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;

namespace Poker.Runtime
{
    /// <summary>Explicit, headless-only packaged-player smoke check. Never enabled during normal play.</summary>
    internal sealed partial class HoldemMultiplayerProcessCheck : MonoBehaviour
    {
        private HoldemMultiplayerBootstrap bootstrap;
        private bool hosting, finished, finishMatch, speechCheck, extendedCheck, sawThreePlayers, sawTwoPlayers;
        private int handTarget = 3;
        private int seatCapacity = HoldemRoom.Capacity;
        private int inputIntervalMilliseconds = 150;
        private string finishMessage;
        private double deadline, finishAt, nextHandAt, nextDiagnosticAt, nextInputAt;
        private long reportedHand;
        private long expectedChips;
        private readonly HashSet<Guid> confirmedSpeech = new HashSet<Guid>();
        private readonly HashSet<Guid> consumedBatches = new HashSet<Guid>();
        private readonly HashSet<Guid> checkedDeals = new HashSet<Guid>();
        private readonly HashSet<Guid> checkedDealerTurns = new HashSet<Guid>();
        private readonly HashSet<string> checkedReveals = new HashSet<string>();
        private HoldemUtteranceCommand lastSpeech;
        private bool dealerTimeoutCheck;
        private HoldemDealerTurnTimeout dealerTimeout;
        private int timeoutCompletions;
        private bool dealerCallbackCheck;
        private Task<bool> dealerCallback;
        private int callbackCompletions;
        private long lastReportedClose;
        private bool rematchCheck;
        private Guid rematchTerminalHand;
        private long rematchTerminalVersion;
        internal static bool IsRequested => Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-peer-check") >= 0;

        internal static void AttachIfRequested(HoldemMultiplayerBootstrap owner)
        {
            if (!UnityEngine.Application.isBatchMode || !IsRequested) return;
            if (Argument("-omc-peer-scenario") == "accusation-app")
            {
                owner.gameObject.AddComponent<HoldemAccusationAppProcessCheck>().Initialize(owner);
                return;
            }
            var check = owner.gameObject.AddComponent<HoldemMultiplayerProcessCheck>(); check.bootstrap = owner;
        }
        private void Start()
        {
            try
            {
                string mode = Argument("-omc-peer-check"); hosting = mode == "host";
                if (mode != "host" && mode != "guest") throw new ArgumentException();
                string scenario = Argument("-omc-peer-scenario");
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-peer-scenario") >= 0
                    && scenario != "finish-match" && scenario != "speech-intake" && scenario != "dealer-timeout"
                    && scenario != "dealer-callback" && scenario != "dealer-card-change" && scenario != "rematch")
                    throw new ArgumentException();
                rematchCheck = scenario == "rematch";
                finishMatch = scenario == "finish-match" || rematchCheck;
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-peer-capacity") >= 0)
                {
                    if (!int.TryParse(Argument("-omc-peer-capacity"), NumberStyles.None, CultureInfo.InvariantCulture,
                        out seatCapacity)) throw new ArgumentException();
                    HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
                }
                if (finishMatch && seatCapacity != 4) throw new ArgumentException();
                dealerTimeoutCheck = scenario == "dealer-timeout";
                dealerCallbackCheck = scenario == "dealer-callback";
                cardChangeCheck = scenario == "dealer-card-change";
                speechCheck = scenario == "speech-intake" || dealerTimeoutCheck || dealerCallbackCheck || cardChangeCheck;
                if ((dealerTimeoutCheck || dealerCallbackCheck || cardChangeCheck) && !bootstrap.Settings.enableFlowPreview) throw new ArgumentException();
                if (cardChangeCheck && (bootstrap.Settings.utteranceVisibility != HoldemUtteranceVisibility.PublicRaw
                    || bootstrap.Settings.utteranceBatchRetention != HoldemUtteranceBatchRetention.UntilHostAcknowledges))
                    throw new ArgumentException("Card-change checks require public remarks and retained host input.");
                extendedCheck = Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-peer-hands") >= 0;
                if (extendedCheck && (scenario != null || bootstrap.Settings.enableFlowPreview
                    || !int.TryParse(Argument("-omc-peer-hands"), NumberStyles.None, CultureInfo.InvariantCulture, out handTarget)
                    || handTarget < 1 || handTarget > 1024)) throw new ArgumentException();
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-peer-input-ms") >= 0
                    && (!int.TryParse(Argument("-omc-peer-input-ms"), NumberStyles.None, CultureInfo.InvariantCulture,
                        out inputIntervalMilliseconds) || inputIntervalMilliseconds < 0 || inputIntervalMilliseconds > 1000))
                    throw new ArgumentException();
                if (!int.TryParse(Argument("-omc-peer-port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    || port < (hosting ? 0 : 1) || port > 65535) throw new ArgumentException();
                expectedChips = checked(bootstrap.Settings.startingStack * seatCapacity);
                bool started = hosting
                    ? bootstrap.Connection.Host("검사 방장", "127.0.0.1", port, false, bootstrap.Settings.CreateConfig(
                        bootstrap.Settings.startingStack, bootstrap.Settings.smallBlind, bootstrap.Settings.bigBlind, seatCapacity),
                        cardChangeCheck ? new OrderedCardChangeDeck() : finishMatch || extendedCheck ? new CheckRandom() : null,
                        bootstrap.Settings.CreateUtterancePolicy(), seatCapacity)
                    : bootstrap.Connection.Join(Argument("-omc-peer-name") ?? "검사 참가자", "127.0.0.1", port, false);
                if (!started) throw new InvalidOperationException();
                deadline = Time.realtimeSinceStartupAsDouble + (finishMatch ? 180 : extendedCheck ? handTarget * 5 + 120 : 120);
                nextDiagnosticAt = Time.realtimeSinceStartupAsDouble + 10;
                Debug.Log("OMC_PEER_INPUT_INTERVAL_MS " + inputIntervalMilliseconds);
                Debug.Log("OMC_PEER_CAPACITY " + seatCapacity);
                if (hosting) Debug.Log("OMC_PEER_LISTEN port=" + bootstrap.Connection.Port);
            }
            catch (Exception e) { Fail(e.GetType().Name); }
        }
        private void Update()
        {
            if (finished) return;
            if (bootstrap.HasFailed) { Fail("ProgressionStopped"); return; }
            // This scenario does not exercise reconnect. Report the original failure instead of
            // retrying it away, including while waiting for the other peers to finish.
            if (bootstrap.Connection.CanReconnect) { Fail("ConnectionClosed"); return; }
            if (finishAt > 0)
            {
                // Remain connected so every independent process can observe the final authoritative state.
                if (Time.realtimeSinceStartupAsDouble >= finishAt)
                { finished = true; Debug.Log(finishMessage); UnityEngine.Application.Quit(0); }
                return;
            }
            if (Time.realtimeSinceStartupAsDouble > deadline) { Fail("Timeout"); return; }
            if (Time.realtimeSinceStartupAsDouble >= nextDiagnosticAt)
            { ReportState(); nextDiagnosticAt = Time.realtimeSinceStartupAsDouble + 10; }
            try
            {
                var remote = bootstrap.Connection.Remote;
                if (remote?.Lobby == null || !remote.CanSend) return;
                if (remote.Lobby.SeatCapacity != seatCapacity) throw new CheckFailure("RoomCapacity");
                if (!remote.HasGame)
                {
                    if (!remote.Lobby.IsReady)
                    { if (TryBeginInput()) remote.SetReady(true); }
                    else if (hosting && remote.Lobby.AllReady && TryBeginInput()) remote.StartTable();
                    return;
                }
                var view = remote.Read(); long chips = 0; int dealt = 0, funded = 0, survivor = 0;
                for (int i = 0; i < view.SeatCount; i++)
                {
                    var seat = view.GetSeatAt(i); chips = checked(chips + seat.Stack);
                    if (seat.WasDealtIn) dealt++;
                    if (seat.Stack > 0) { funded++; survivor = seat.Seat.Value; }
                    if (view.Result == null && !view.IsSettlementPending && !seat.IsViewer && seat.VisibleHoleCardCount != 0)
                        throw new CheckFailure("PrivateCardExposure");
                }
                if (dealt < seatCapacity && view.Result == null)
                {
                    if (dealt == 3 && !sawThreePlayers || dealt == 2 && !sawTwoPlayers)
                        Debug.Log("OMC_PEER_SHORT_TABLE_OK seat=" + view.ViewerSeat.Value + " players=" + dealt + " hand=" + view.HandNumber);
                    if (dealt == 3) sawThreePlayers = true;
                    if (dealt == 2) sawTwoPlayers = true;
                }
                var own = view.GetSeat(view.ViewerSeat);
                if (!own.WasDealtIn && (view.OwnCardCount != 0 || view.LegalActions != null || own.Stack != 0))
                    throw new CheckFailure("EliminatedSeatInputOrCards");
                // A settled display keeps the previous pot amount for explanation, not as unawarded chips.
                if (checked(chips + (view.Result == null ? view.PotAmount : 0)) != expectedChips)
                    throw new CheckFailure("ChipConservation");
                if (rematchCheck && remote.Lobby.MatchNumber == 2)
                {
                    if (rematchTerminalHand == Guid.Empty || view.HandId == rematchTerminalHand
                        || view.SessionVersion != rematchTerminalVersion + 1 || view.HandNumber != reportedHand + 1
                        || view.IsOver || view.OwnCardCount != 2 || dealt != seatCapacity || !view.GetSeat(new SeatId(1)).IsButton
                        || remote.ReadHistory()?.Count != 2 || remote.Lobby.IsRematchReady)
                        throw new CheckFailure("RematchBoundary");
                    for (int i = 0; i < view.SeatCount; i++)
                        if (view.GetSeatAt(i).Stack + view.GetSeatAt(i).Committed != bootstrap.Settings.startingStack)
                            throw new CheckFailure("RematchInitialChips");
                    finishMessage = "OMC_PEER_REMATCH_OK seat=" + view.ViewerSeat.Value + " match=2 hand=" + view.HandNumber
                        + " chips=" + expectedChips + " version=" + view.SessionVersion;
                    finishAt = Time.realtimeSinceStartupAsDouble + (hosting ? 15 : 10); return;
                }
                if (speechCheck && !CheckSpeech(remote, view)) return;
                if (cardChangeCheck) CheckCardChangeDisplay(view);
                if (view.Result != null)
                {
                    if (reportedHand != view.HandNumber)
                    {
                        CheckPublicHistory(remote, view);
                        if (speechCheck && remote.RoomRules.PublishesUtterances) CheckPublicRemarks(remote, view);
                        reportedHand = view.HandNumber;
                        nextHandAt = Time.realtimeSinceStartupAsDouble + 0.5;
                        Debug.Log("OMC_PEER_HAND_OK seat=" + view.ViewerSeat.Value + " hand=" + view.HandNumber
                            + " chips=" + chips + " version=" + view.SessionVersion);
                    }
                    if (finishMatch ? view.IsOver : view.HandNumber >= handTarget || extendedCheck && view.IsOver)
                    {
                        if (rematchCheck)
                        {
                            if (remote.Lobby.MatchNumber != 1 || !remote.Lobby.RematchSupported)
                                throw new CheckFailure("RematchCapability");
                            rematchTerminalHand = view.HandId; rematchTerminalVersion = view.SessionVersion;
                            if (!remote.Lobby.IsRematchReady)
                            { if (TryBeginInput()) remote.SetRematchReady(true); }
                            else if (hosting && remote.Lobby.AllRematchReady && TryBeginInput()) remote.RestartMatch();
                            return;
                        }
                        if (finishMatch && (funded != 1 || view.CanContinue || !sawThreePlayers || !sawTwoPlayers))
                            throw new CheckFailure("FinalMatchState");
                        int expectedBatches = bootstrap.Settings.CreateUtterancePolicy()?.BatchRetention
                            == HoldemUtteranceBatchRetention.UntilHostAcknowledges ? 9 : 0;
                        if (speechCheck && (confirmedSpeech.Count != 9 || hosting && consumedBatches.Count != expectedBatches))
                            throw new CheckFailure("SpeechCoverage");
                        if (bootstrap.Settings.enableFlowPreview && hosting && !finishMatch
                            && (checkedDeals.Count != 9 || checkedReveals.Count != 9))
                            throw new CheckFailure("FlowGateCoverage");
                        if (cardChangeCheck) CheckCardChangeCompletion(view);
                        finishMessage = finishMatch ? "OMC_PEER_MATCH_OK seat=" + view.ViewerSeat.Value + " hands=" + view.HandNumber
                            + " survivor=" + survivor + " chips=" + chips + " version=" + view.SessionVersion
                            : cardChangeCheck ? "OMC_PEER_CARD_CHANGE_OK seat=" + view.ViewerSeat.Value
                                + " hands=3 reveals=" + observedCardReveals.Count + " own=" + ownCardReveals
                                + " chips=" + chips + " version=" + view.SessionVersion
                            : speechCheck ? "OMC_PEER_SPEECH_OK seat=" + view.ViewerSeat.Value
                                + " hands=3 accepted=" + confirmedSpeech.Count + " batches=" + (hosting ? consumedBatches.Count.ToString() : "host-only")
                                + " chips=" + chips + " version=" + view.SessionVersion
                            : extendedCheck ? "OMC_PEER_LONG_SESSION_OK seat=" + view.ViewerSeat.Value
                                + " hands=" + view.HandNumber + " target=" + handTarget + " chips=" + chips
                                + " version=" + view.SessionVersion + " complete=" + view.IsOver
                            : "OMC_PEER_CHECK_OK hands=3";
                        if (bootstrap.Settings.enableFlowPreview && hosting)
                            Debug.Log("OMC_PEER_FLOW_GATES_OK deals=" + checkedDeals.Count + " reveals=" + checkedReveals.Count);
                        if (speechCheck && bootstrap.Settings.enableFlowPreview && hosting)
                        {
                            if (checkedDealerTurns.Count != 9) throw new CheckFailure("DealerTurnCoverage");
                            Debug.Log("OMC_PEER_DEALER_TURNS_OK turns=" + checkedDealerTurns.Count);
                            if (dealerTimeoutCheck)
                            {
                                if (timeoutCompletions != 9) throw new CheckFailure("DealerTimeoutCoverage");
                                Debug.Log("OMC_PEER_DEALER_TIMEOUT_OK completions=" + timeoutCompletions);
                            }
                            if (dealerCallbackCheck)
                            {
                                if (callbackCompletions != 9) throw new CheckFailure("DealerCallbackCoverage");
                                Debug.Log("OMC_PEER_DEALER_CALLBACK_OK completions=" + callbackCompletions);
                            }
                        }
                        finishAt = Time.realtimeSinceStartupAsDouble + (hosting ? 15 : 10); return;
                    }
                    if (finishMatch && view.HandNumber >= 12) throw new CheckFailure("AllInHandLimit");
                    if (hosting && view.CanContinue && Time.realtimeSinceStartupAsDouble >= nextHandAt && TryBeginInput())
                    {
                        if (own.Stack == 0) Debug.Log("OMC_PEER_ELIMINATED_HOST_NEXT hand=" + view.HandNumber);
                        remote.NextHand(view);
                    }
                }
                else if (view.IsDealPending && hosting)
                {
                    if (!TryBeginInput()) return;
                    if (speechCheck && bootstrap.Settings.enableFlowPreview)
                    {
                        var dealer = bootstrap.Connection.DealerTurns;
                        if (dealer == null) throw new CheckFailure("DealerTurnCapability");
                        var read = dealer.ReadPendingTurn();
                        if (read.Error == HoldemRoomError.Paused || read.Error == HoldemRoomError.Disconnected) return;
                        if (read.Error != HoldemRoomError.None) throw new CheckFailure("DealerTurnRead");
                        var turn = read.Turn;
                        if (turn == null) return; // Authority may have advanced before its state packet reaches this client.
                        if (turn.TargetDeal.WindowId != view.PendingDeal.WindowId || turn.TargetDeal.HandId != view.HandId
                            || turn.ExpectedVersion != view.SessionVersion || turn.SourceUtterances == null
                            || turn.SourceUtterances.Count != seatCapacity || !consumedBatches.Contains(turn.SourceUtterances.WindowId))
                            throw new CheckFailure("DealerTurnCorrelation");
                        if (cardChangeCheck)
                        {
                            if (!CompleteScriptedCardChange(dealer, view)) return;
                        }
                        else if (dealerCallbackCheck)
                        {
                            var coordinator = bootstrap.Connection.DealerCoordinator;
                            if (dealerCallback != null)
                            {
                                if (!dealerCallback.IsCompleted) return;
                                if (!dealerCallback.GetAwaiter().GetResult()) throw new CheckFailure("DealerCallbackPost");
                                dealerCallback = null;
                            }
                            var receipt = coordinator.Poll(out var work);
                            if (work != null)
                            {
                                if (dealerCallback != null) throw new CheckFailure("DealerCallbackOverlap");
                                // Test-only worker: posts twice, never accesses Unity or the authoritative port.
                                dealerCallback = Task.Run(() => coordinator.TryPostUnchanged(work)
                                    && coordinator.TryPostUnchanged(work));
                            }
                            if (receipt == null) return;
                            if (!receipt.Accepted) throw new CheckFailure("DealerCallbackCompletion");
                            callbackCompletions++;
                        }
                        else if (dealerTimeoutCheck)
                        {
                            // Explicit headless fixture value only. Normal rooms have no default timer.
                            if (dealerTimeout == null) dealerTimeout = new HoldemDealerTurnTimeout(dealer,
                                TimeSpan.FromSeconds(0.25), () => TimeSpan.FromSeconds(Time.realtimeSinceStartupAsDouble));
                            var receipt = dealerTimeout.Poll();
                            if (receipt == null) return;
                            if (!receipt.Accepted) throw new CheckFailure("DealerTimeoutCompletion");
                            timeoutCompletions++;
                        }
                        else if (!dealer.DealUnchanged(turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted)
                                throw new CheckFailure("DealerTurnCompletion");
                        checkedDealerTurns.Add(turn.TargetDeal.WindowId);
                    }
                    else remote.Deal(view);
                    checkedDeals.Add(view.PendingDeal.WindowId);
                }
                else if (view.IsRevealPending && hosting)
                {
                    if (cardChangeCheck && Time.realtimeSinceStartupAsDouble < cardRevealAdvanceAt) return;
                    if (!TryBeginInput()) return;
                    remote.Reveal(view);
                    checkedReveals.Add(view.HandId.ToString("N") + ":" + (int)view.Street);
                }
                else if (view.IsSettlementPending && hosting)
                { if (TryBeginInput()) remote.Resolve(view); } // Explicit test-only one-hand priority.
                else if (view.LegalActions != null)
                {
                    if (!TryBeginInput()) return;
                    var action = finishMatch ? MatchAction(view, dealt) : Passive(view);
                    if (!view.LegalActions.Allows(action)) throw new CheckFailure("ScenarioIllegalAction");
                    if (finishMatch) Debug.Log("OMC_PEER_ACTION seat=" + view.ViewerSeat.Value + " hand=" + view.HandNumber
                        + " street=" + view.Street + " action=" + action.Kind + " target=" + action.Target);
                    remote.Act(view, action);
                }
            }
            catch (Exception e) { Fail(e is CheckFailure ? e.Message : e.GetType().Name); }
        }

        private void CheckPublicRemarks(HoldemRemoteTablePort remote, HoldemTableDisplay game)
        {
            var feed = remote.ReadPublicUtterances();
            if (feed == null || !feed.HasHistoryOrder || feed.SessionId != game.SessionId || feed.HandId != game.HandId || feed.Count != seatCapacity * 3)
                throw new CheckFailure("PublicSpeechCoverage");
            var source = new StringBuilder();
            var counts = new int[seatCapacity, 3];
            long previousPosition = 2;
            var history = remote.ReadHistory();
            for (int i = 0; i < feed.Count; i++)
            {
                var entry = feed.GetEntry(i);
                if (entry.Text != "접수 검사 " + entry.Speaker.Value + " " + game.HandNumber + " " + entry.Street)
                    throw new CheckFailure("PublicSpeechContent");
                if (++counts[entry.Speaker.Value - 1, (int)entry.Street] != 1) throw new CheckFailure("PublicSpeechDuplicate");
                if (!entry.HistoryPosition.HasValue || entry.HistoryPosition.Value < previousPosition
                    || entry.HistoryPosition.Value > history.OmittedCount + history.Count) throw new CheckFailure("PublicSpeechOrder");
                previousPosition = entry.HistoryPosition.Value;
                source.Append(entry.Speaker.Value).Append('|').Append((int)entry.Street).Append('|').Append(entry.Text)
                    .Append('|').Append(entry.HistoryPosition.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            using (var sha = SHA256.Create())
                Debug.Log("OMC_PEER_PUBLIC_SPEECH_OK seat=" + game.ViewerSeat.Value + " hand=" + game.HandNumber
                    + " count=" + feed.Count + " digest=" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()))).Replace("-", "").ToLowerInvariant());
        }

        private static void CheckPublicHistory(HoldemRemoteTablePort remote, HoldemTableDisplay game)
        {
            var history = remote.ReadHistory();
            if (history == null || history.SessionId != game.SessionId || history.HandId != game.HandId
                || history.HandNumber != game.HandNumber || history.Count < 2 || history.Count > HoldemRoom.MaximumHistoryEntries)
                throw new CheckFailure("PublicHistoryIdentity");
            long paid = 0;
            var source = new StringBuilder();
            source.AppendFormat(CultureInfo.InvariantCulture, "{0}|", history.OmittedCount);
            for (int i = 0; i < history.Count; i++)
            {
                var entry = history.GetEntry(i);
                paid = checked(paid + (entry.Kind == HoldemHistoryKind.UncalledReturn ? -entry.Amount : entry.Amount));
                source.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6};", (int)entry.Kind,
                    (int)entry.Street, entry.Seat.Value, (int)(entry.Action ?? 0), entry.Amount, entry.StreetTotal, entry.IsAllIn ? 1 : 0);
            }
            if (history.OmittedCount == 0 && paid != game.PotAmount) throw new CheckFailure("PublicHistoryAccounting");
            using (var sha = SHA256.Create())
                Debug.Log("OMC_PEER_HISTORY_OK seat=" + game.ViewerSeat.Value + " hand=" + game.HandNumber
                    + " count=" + history.Count + " omitted=" + history.OmittedCount
                    + " digest=" + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()))));
        }

        private bool CheckSpeech(HoldemRemoteTablePort remote, HoldemTableDisplay game)
        {
            var port = remote.Utterances as IHoldemAsyncUtterancePort;
            if (port == null) throw new CheckFailure("SpeechCapabilityMissing");
            var own = port.ReadUtterances();
            bool sentThisStreet = false;
            for (int i = 0; i < own.Count; i++)
            {
                var entry = own.GetEntry(i);
                if (entry.Speaker != game.ViewerSeat || entry.SessionId != game.SessionId || entry.HandId != game.HandId
                    || entry.Ordinal != 0) throw new CheckFailure("SpeechPrivacy");
                confirmedSpeech.Add(entry.CommandId);
                if (entry.WindowId == own.WindowId) sentThisStreet = true;
            }
            if (hosting)
                foreach (var batch in bootstrap.Connection.ReadPendingUtterances())
                {
                    var seats = new HashSet<int>();
                    if (batch.Count != seatCapacity || batch.SessionId != game.SessionId || !consumedBatches.Add(batch.WindowId))
                        throw new CheckFailure("HostSpeechBatch");
                    for (int i = 0; i < batch.Count; i++) seats.Add(batch.GetEntry(i).Speaker.Value);
                    if (seats.Count != seatCapacity || !bootstrap.Connection.AcknowledgeUtteranceBatch(batch.WindowId))
                        throw new CheckFailure("HostSpeechConsumption");
                }
            if (lastSpeech != null && port.TryReadReceipt(lastSpeech, out var receipt) && !receipt.Accepted)
                throw new CheckFailure("SpeechRejected");
            if (own.WindowId == Guid.Empty || sentThisStreet) return true;
            if (lastSpeech == null || lastSpeech.WindowId != own.WindowId)
            {
                if (!own.CanSubmit || !port.CanInteract || !TryBeginInput()) return false;
                var command = new HoldemUtteranceCommand(own.SessionId, own.HandId, own.WindowId, Guid.NewGuid(),
                    own.Street, own.ViewerSeat, "접수 검사 " + own.ViewerSeat.Value + " " + game.HandNumber + " " + own.Street);
                port.SendOrRetry(command, out _);
                lastSpeech = command;
            }
            // Every player confirms its own one-per-street input before acting, so the table's
            // check/call round cannot close until every sender is represented in the host batch.
            return false;
        }
        private bool TryBeginInput()
        {
            // Pace only this checker. An explicit zero interval reproduces a burst without
            // weakening the production limit or silently recovering a disconnected client.
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextInputAt) return false;
            nextInputAt = now + inputIntervalMilliseconds / 1000.0;
            return true;
        }
        private static BettingAction Passive(HoldemTableDisplay view)
            => view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call();
        private static BettingAction MatchAction(HoldemTableDisplay view, int dealt)
        {
            int seat = view.ViewerSeat.Value;
            var legal = view.LegalActions;
            if (view.HandNumber == 1)
            {
                if (view.Street != HoldemStreet.Preflop) return Passive(view);
                if (seat == 1) return BettingAction.Fold();
                if (seat == 4 && view.OwnStreetContribution == 0) return Aggress(view, 5);
                if (seat == 2 && view.OwnStreetContribution == 1) return Aggress(view, 9);
                return Passive(view);
            }
            // Keep a funded host and one other player out of the first all-in contest so an
            // eliminated guest is observed in a later, shorter-handed game before the final survivor.
            if (dealt == 4 && (seat == 1 || seat == 3)) return BettingAction.Fold();
            return legal.CanBet || legal.CanRaise ? Aggress(view, legal.MaximumAggressiveTarget.Value) : Passive(view);
        }
        private static BettingAction Aggress(HoldemTableDisplay view, long target)
        {
            var legal = view.LegalActions;
            if (!legal.CanBet && !legal.CanRaise) return Passive(view);
            long bounded = Math.Min(legal.MaximumAggressiveTarget.Value, Math.Max(legal.MinimumAggressiveTarget.Value, target));
            return legal.CanBet ? BettingAction.BetTo(bounded) : BettingAction.RaiseTo(bounded);
        }
        private sealed class CheckFailure : Exception { public CheckFailure(string code) : base(code) { } }
        private sealed class CheckRandom : IRandomSource
        {
            private readonly System.Random random = new System.Random(1295);
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
        private void Fail(string reason)
        { finished = true; ReportState(); Debug.LogError("OMC_PEER_CHECK_FAILED " + reason); UnityEngine.Application.Quit(1); }
        private void ReportState()
        {
            ReportCloseRecords();
            var remote = bootstrap?.Connection?.Remote;
            var game = remote?.HasGame == true ? remote.Read() : null;
            Debug.Log("OMC_PEER_STATE seat=" + game?.ViewerSeat.Value + " hand=" + game?.HandNumber
                + " version=" + game?.SessionVersion + " street=" + game?.Street
                + " paused=" + remote?.Lobby?.Paused + " pending=" + remote?.HasPendingInput
                + " speechPending=" + remote?.HasPendingUtterance + " canSend=" + remote?.CanSend
                + " status=" + remote?.StatusText + " error=" + remote?.ErrorText
                + " closeReason=" + bootstrap?.Connection?.ClientCloseReason
                + " serverHostCloseObserved=" + (bootstrap?.Connection?.LatestHostClose != null)
                + " connectionError=" + bootstrap?.Connection?.ErrorText
                + " confirmedSpeech=" + confirmedSpeech.Count + " batches=" + consumedBatches.Count);
        }
        private void ReportCloseRecords()
        {
            var connection = bootstrap?.Connection;
            if (connection == null || !connection.IsHosting) return;
            var records = connection.ReadCloseRecords();
            var hostClose = connection.LatestHostClose;
            if (hostClose != null && hostClose.Sequence > lastReportedClose
                && (records.Count == 0 || hostClose.Sequence < records[0].Sequence))
                ReportClose(hostClose);
            foreach (var record in records)
                if (record.Sequence > lastReportedClose) ReportClose(record);
        }
        private void ReportClose(Poker.Transport.HoldemPeerCloseRecord record)
        {
            Debug.Log("OMC_PEER_SERVER_CLOSE sequence=" + record.Sequence + " elapsedMs=" + record.ElapsedMilliseconds
                + " seat=" + record.Seat + " admitted=" + record.WasAdmitted + " host=" + record.WasHost
                + " trigger=" + record.Trigger + " channel=" + record.ChannelReason);
            lastReportedClose = record.Sequence;
        }
        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
        private void OnDestroy()
        { dealerTimeout?.Dispose(); cardChangeCoordinator?.Dispose(); }
    }
}
