using System;
using System.Collections.Generic;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Headless-only packaged app check; drives the same host fixture buttons as a developer.</summary>
    internal sealed class HoldemAccusationAppProcessCheck : MonoBehaviour
    {
        private HoldemMultiplayerBootstrap bootstrap;
        private VisualElement root;
        private bool host, finished;
        private int capacity, reveals;
        private double deadline, readyAt, finishAt;
        private Guid speechWindow, submittedDeal, seenReveal;
        private long pendingVersion, revealPot;
        private readonly Dictionary<HoldemStreet, Guid> dealWindows = new Dictionary<HoldemStreet, Guid>();
        private long[] revealStacks;

        internal void Initialize(HoldemMultiplayerBootstrap owner) => bootstrap = owner;

        private void Start()
        {
            try
            {
                if (!UnityEngine.Application.isBatchMode || !bootstrap.EnableAccusationDebug)
                    throw new InvalidOperationException("Explicit headless accusation mode required.");
                string role = Argument("-omc-peer-check");
                host = role == "host";
                if (!host && role != "guest") throw new ArgumentException("Role");
                capacity = int.Parse(Argument("-omc-peer-capacity") ?? "4");
                HoldemRoomRules.ValidateSeatCapacity(capacity);
                root = GetComponent<UIDocument>().rootVisualElement;
                root.Q<TextField>("omc-room-name").value = host ? "검사 방장" : Argument("-omc-peer-name") ?? "검사 참가자";
                root.Q<TextField>("omc-room-port").value = Argument("-omc-peer-port");
                root.Q<DropdownField>("omc-room-capacity").index = capacity - 3;
                Press(host ? "omc-room-host" : "omc-room-join");
                if (!bootstrap.Connection.HasSession) throw new InvalidOperationException("Room startup");
                if (host) Debug.Log("OMC_PEER_LISTEN port=" + bootstrap.Connection.Port);
                deadline = Time.realtimeSinceStartupAsDouble + 120;
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private void Update()
        {
            if (finished) return;
            if (finishAt > 0)
            {
                if (Time.realtimeSinceStartupAsDouble >= finishAt)
                {
                    finished = true;
                    Debug.Log("OMC_ACCUSATION_APP_OK seat=" + bootstrap.Connection.Remote.Read().ViewerSeat.Value
                        + " capacity=" + capacity + " reveals=" + reveals + " duplicate-button=checked privacy=checked");
                    UnityEngine.Application.Quit(0);
                }
                return;
            }
            try
            {
                if (bootstrap.HasFailed || bootstrap.Connection.CanReconnect) throw new InvalidOperationException("Connection stopped");
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new InvalidOperationException("Timeout");
                var remote = bootstrap.Connection.Remote;
                if (remote?.Lobby == null || !remote.CanSend) return;
                if (!remote.RoomRules.AccusationsEnabled || remote.Lobby.SeatCapacity != capacity)
                    throw new InvalidOperationException("Debug room configuration");
                if (!host && (bootstrap.Connection.DealerTurns != null || bootstrap.Connection.Accusations != null))
                    throw new InvalidOperationException("Guest authority");
                if (!remote.HasGame)
                {
                    if (!remote.Lobby.IsReady) Press("omc-room-ready");
                    else if (host && remote.Lobby.AllReady) Press("omc-room-start");
                    return;
                }
                var view = remote.Read();
                CheckPublicState(view);
                if (view.IsDealPending)
                {
                    var pending = view.PendingDeal;
                    dealWindows[pending.Street] = pending.WindowId; pendingVersion = view.SessionVersion;
                    if (host && submittedDeal != pending.WindowId && root.Q<Button>("omc-debug-change") != null)
                    {
                        string control = pending.Street == HoldemStreet.Flop ? "omc-debug-unchanged"
                            : pending.Street == HoldemStreet.Turn ? "omc-debug-timeout" : "omc-debug-change";
                        if (pending.Street == HoldemStreet.River)
                        {
                            var source = root.Q<DropdownField>("omc-debug-source");
                            int index = source.choices.FindIndex(value => value.StartsWith("2번 멘트 #", StringComparison.Ordinal));
                            if (index < 0) throw new InvalidOperationException("Missing selected speaker");
                            source.index = index;
                        }
                        if (Press(control)) { submittedDeal = pending.WindowId; Press(control); }
                    }
                    return;
                }
                if (view.IsRevealPending) { CheckReveal(remote, view); return; }
                if (view.Street == HoldemStreet.River) throw new InvalidOperationException("Claim was incorrectly resumed");
                var speech = remote.Utterances.ReadUtterances();
                if (speechWindow != speech.WindowId)
                {
                    if (!remote.Utterances.CanInteract) return;
                    var input = new HoldemUtteranceCommand(speech.SessionId, speech.HandId, speech.WindowId,
                        Guid.NewGuid(), speech.Street, speech.ViewerSeat, "공개 검사 멘트 " + speech.ViewerSeat.Value);
                    remote.Utterances.SendOrRetry(input, out _); speechWindow = speech.WindowId;
                }
                if (remote.HasPendingUtterance) return;
                var published = remote.ReadPublicUtterances(); int count = 0;
                if (published != null)
                    for (int i = 0; i < published.Count; i++) if (published.GetEntry(i).Street == view.Street) count++;
                if (count != capacity) return; // Keep the source window open until every process's remark is observed.
                if (view.CurrentSeat == view.ViewerSeat)
                    remote.Act(view, view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private void CheckReveal(HoldemRemoteTablePort remote, HoldemTableDisplay view)
        {
            var state = view.Accusations;
            if (state == null) throw new InvalidOperationException("Missing accusation window");
            bool owner = view.Street == HoldemStreet.River && view.ViewerSeat.Value == 2;
            if ((view.OwnCardChange != null) != owner) throw new InvalidOperationException("Owner feedback privacy");
            if (!dealWindows.TryGetValue(view.Street, out var deal)) throw new InvalidOperationException("Missed deal window");
            if (owner && (view.OwnCardChange.EventId != deal || view.OwnCardChange.BoardIndex != 4
                || view.OwnCardChange.Card != Card.FromId(47))) throw new InvalidOperationException("Feedback attribution");
            if (seenReveal != state.WindowId)
            {
                if (view.SessionVersion != pendingVersion + 1) throw new InvalidOperationException("Card application count");
                seenReveal = state.WindowId; reveals++; readyAt = Time.realtimeSinceStartupAsDouble + 1;
                revealPot = view.PotAmount; revealStacks = new long[view.SeatCount];
                for (int i = 0; i < revealStacks.Length; i++) revealStacks[i] = view.GetSeatAt(i).Stack;
                var board = new List<string>();
                for (int i = 0; i < view.BoardCount; i++) board.Add(view.GetBoardCard(i).Id.ToString());
                Debug.Log("OMC_ACCUSATION_REVEAL_OK seat=" + view.ViewerSeat.Value + " street=" + view.Street
                    + " version=" + view.SessionVersion + " pot=" + view.PotAmount + " board=" + string.Join(",", board));
            }
            if (view.PotAmount != revealPot) throw new InvalidOperationException("Unexpected accusation payout");
            for (int i = 0; i < revealStacks.Length; i++)
                if (view.GetSeatAt(i).Stack != revealStacks[i]) throw new InvalidOperationException("Unexpected accusation penalty");
            if (Time.realtimeSinceStartupAsDouble < readyAt) return;
            // Sequential response order avoids treating expected-version racing as a successful submission.
            if (state.Phase == HoldemAccusationPhase.Collecting && !state.HasResponded
                && state.ResponseCount == view.ViewerSeat.Value - 1)
            {
                SeatId? target = view.Street != HoldemStreet.River ? (SeatId?)null
                    : view.ViewerSeat.Value == 1 ? new SeatId(2) : view.ViewerSeat.Value == 3 ? new SeatId(1) : (SeatId?)null;
                remote.Accuse(view, target); return;
            }
            if (host && state.Phase == HoldemAccusationPhase.Collecting && state.ResponseCount == capacity)
                Press("omc-debug-close-accusations");
            if (host && state.Phase == HoldemAccusationPhase.AwaitingVerdicts) Press("omc-debug-resolve-accusations");
            if (host && state.Phase == HoldemAccusationPhase.ClosedWithoutClaims) Press("omc-continue-reveal");
            if (state.Phase != HoldemAccusationPhase.AwaitingConsequences) return;
            bool? expected = view.ViewerSeat.Value == 1 ? true : view.ViewerSeat.Value == 3 ? false : (bool?)null;
            if (state.OwnVerdict != expected || view.LegalActions != null || reveals != 3)
                throw new InvalidOperationException("Private verdict or held settlement");
            if (host && bootstrap.Connection.ReadPendingUtterances().Count != 0) throw new InvalidOperationException("Unconsumed source input");
            finishAt = Time.realtimeSinceStartupAsDouble + (host ? 8 : 5);
        }

        private void CheckPublicState(HoldemTableDisplay view)
        {
            long chips = view.PotAmount;
            var visible = new HashSet<Card>();
            for (int i = 0; i < view.BoardCount; i++) if (!visible.Add(view.GetBoardCard(i))) throw new InvalidOperationException("Duplicate board");
            for (int i = 0; i < view.SeatCount; i++)
            {
                var seat = view.GetSeatAt(i); chips += seat.Stack;
                if (seat.Seat != view.ViewerSeat && seat.VisibleHoleCardCount != 0) throw new InvalidOperationException("Other private cards");
                for (int c = 0; c < seat.VisibleHoleCardCount; c++)
                    if (!visible.Add(seat.GetVisibleHoleCard(c))) throw new InvalidOperationException("Duplicate visible cards");
            }
            if (chips != bootstrap.Settings.startingStack * capacity) throw new InvalidOperationException("Chip conservation");
        }

        private bool Press(string name)
        {
            var button = root.Q<Button>(name);
            if (button == null || !button.enabledInHierarchy) return false;
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
            return true;
        }
        private void Fail(string reason)
        { if (finished) return; finished = true; Debug.LogError("OMC_ACCUSATION_APP_FAILED " + reason); UnityEngine.Application.Quit(1); }
        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
