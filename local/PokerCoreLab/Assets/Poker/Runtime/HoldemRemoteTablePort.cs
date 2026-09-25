using System;
using System.Diagnostics;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using Poker.Transport;

namespace Poker.Runtime
{
    public interface IHoldemRemoteTablePort
    {
        HoldemTableDisplay Read();
        bool IsHost { get; }
        bool CanSend { get; }
        bool NeedsRefresh { get; }
        string RefreshLabel { get; }
        string StatusText { get; }
        string ErrorText { get; }
        event Action Changed;
        void Act(HoldemTableDisplay basis, BettingAction action);
        void NextHand(HoldemTableDisplay basis);
        void Resolve(HoldemTableDisplay basis);
        void Deal(HoldemTableDisplay basis);
        void Reveal(HoldemTableDisplay basis);
        void Refresh();
    }

    /// <summary>Optional display information only. A disconnected seat is not a folded or eliminated seat.</summary>
    public interface IHoldemConnectionPresence
    {
        bool IsSeatDisconnected(SeatId seat);
    }

    /// <summary>Confirmed, immutable room setup; null means the sender did not provide it.</summary>
    public interface IHoldemRoomInfoSource
    {
        HoldemRoomRules RoomRules { get; }
    }

    /// <summary>Owns its current client. One outstanding intent, no optimistic chip state. Poll/events stay on the UI thread.</summary>
    public sealed class HoldemRemoteTablePort : IHoldemRemoteTablePort, IHoldemRemoteUtteranceSource, IHoldemHistoryPort,
        IHoldemConnectionPresence, IHoldemRoomInfoSource, IHoldemPublicUtteranceSource, IDisposable
    {
        private HoldemTcpClient client;
        private readonly Action reconnect;
        private readonly Func<long> milliseconds;
        private HoldemTableDisplay display;
        private HoldemHandHistory history;
        private HoldemPublicUtterances publicUtterances;
        private HoldemWireRequest pending;
        private Guid pendingBasisHandId;
        private long observedRevision = -1, sentAt, pendingStartedAt, acceptedVersion = -1;
        private long pendingPauseRevision = -1;
        private bool disposed, invalidState, retryAfterReconnect, awaitingFreshState = true;
        private string previousStatus;
        private readonly HoldemRemoteUtterancePort speech;
        public IHoldemAsyncUtterancePort Utterances => speech.Available ? speech : null;
        public bool HasPendingUtterance => speech.HasPending;
        private bool CanCommunicate => !disposed && !invalidState && !awaitingFreshState && !LobbyLeaveConfirmed
            && client.IsAdmitted && Lobby != null && !Lobby.Paused;
        public event Action Changed;
        public string ErrorText { get; private set; } = "";
        public bool HasGame => display != null;
        public HoldemLobbyDisplay Lobby { get; private set; }
        public HoldemRoomRules RoomRules => Lobby?.Rules;
        public bool HasPendingInput => pending != null;
        public bool IsLobbyLeavePending => pending?.type == "leave";
        public bool LobbyLeaveConfirmed { get; private set; }
        public bool HasUnrecoverableAdmissionError => client.AdmissionError != null;
        public bool IsSeatDisconnected(SeatId seat)
        {
            if (seat.Value == client.Identity.Seat && client.IsClosed) return true;
            // The last snapshot cannot establish another participant's current presence while this client is offline.
            if (!client.IsAdmitted || awaitingFreshState || invalidState || Lobby == null) return false;
            for (int i = 0; i < Lobby.MemberCount; i++)
                if (Lobby.GetMember(i).Seat == seat.Value) return !Lobby.GetMember(i).Connected;
            return false;
        }
        public string UnconfirmedLeaveMessage => client.AdmissionError == "SessionGone"
            ? "기존 방이 종료됐어요. 처음 화면으로 돌아가 새 방에 참가해 주세요."
            : "퇴장이 처리됐는지 확인하지 못했어요. 기존 연결 정보로 다시 접속할 수 없어 처음 화면으로 돌아가야 해요. 방장에게 빈자리를 확인해 주세요.";
        public bool CanLeaveLobby => !disposed && !invalidState && !awaitingFreshState && !LobbyLeaveConfirmed
            && client.IsAdmitted && Lobby?.SupportsLobbyLeave == true && !Lobby.IsHost && !Lobby.HasGame && pending == null;
        public bool IsHost
        {
            get
            {
                return Lobby?.IsHost == true;
            }
        }
        public bool CanSend => !disposed && !invalidState && !awaitingFreshState && !LobbyLeaveConfirmed && client.IsAdmitted && Lobby != null && !Lobby.Paused && pending == null;
        private bool IsPauseGuardedInput => pending != null && (pending.type == "act" || pending.type == "start"
            || pending.type == "deal" || pending.type == "reveal" || pending.type == "settle");
        private bool HasPauseDeferredInput => pending != null && pendingPauseRevision >= 0;
        private bool CanConfirmDeferredInput => HasPauseDeferredInput && !invalidState && !awaitingFreshState
            && client.IsAdmitted && Lobby?.Paused == false && observedRevision > pendingPauseRevision;
        private bool CanReconnectStalledInput => !disposed && !invalidState && !awaitingFreshState && !HasPauseDeferredInput
            && reconnect != null && client.IsAdmitted && Lobby?.Paused == false && pending != null
            && milliseconds() - pendingStartedAt >= 8000;
        public bool NeedsRefresh => !disposed && (client.IsClosed ? reconnect != null && client.AdmissionError == null
            : !invalidState && !awaitingFreshState && client.IsAdmitted && (Lobby?.Paused == false || IsLobbyLeavePending)
                && (CanConfirmDeferredInput || !HasPauseDeferredInput
                    && (CanReconnectStalledInput || pending != null && milliseconds() - sentAt >= 3000)));
        public string RefreshLabel => client.IsClosed || CanReconnectStalledInput ? "다시 연결" : "입력 확인";
        public string StatusText => LobbyLeaveConfirmed ? "방에서 나왔어요." : client.AdmissionError != null ? AdmissionMessage(client.AdmissionError)
            : invalidState ? "화면 정보를 확인하지 못했어요. 연결을 다시 확인해 주세요."
            : client.IsClosed ? "연결이 끊겼어요. 다시 연결하면 같은 자리로 돌아와요."
            : !client.IsAdmitted ? "방에 연결하고 있어요."
            : awaitingFreshState ? "현재 방 상태를 확인하고 있어요."
            : client.Latest?.paused == true ? "다른 참가자의 재접속을 기다리고 있어요."
            : HasPauseDeferredInput ? (CanConfirmDeferredInput ? "다시 진행할 수 있어요. 보낸 입력을 확인해 주세요."
                : "보낸 입력을 잠시 보류했어요. 방 상태를 확인하고 있어요.")
            : pending != null ? (CanReconnectStalledInput ? "응답이 없어요. 다시 연결해 보낸 입력을 확인해 주세요."
                : NeedsRefresh ? "응답이 늦어지고 있어요. 입력 확인을 눌러 주세요."
                : IsLobbyLeavePending ? "방에서 자리를 비우고 있어요." : "보낸 입력을 확인하고 있어요.") : "";

        public HoldemRemoteTablePort(HoldemTcpClient client, Action reconnect = null, Func<long> monotonicMilliseconds = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client)); this.reconnect = reconnect;
            var clock = Stopwatch.StartNew();
            milliseconds = monotonicMilliseconds ?? (() => clock.ElapsedMilliseconds);
            speech = new HoldemRemoteUtterancePort(() => CanCommunicate, request => this.client.Send(request),
                () => Changed?.Invoke(), () => observedRevision);
        }
        public HoldemTableDisplay Read() => display ?? throw new InvalidOperationException("The table has not started.");
        public HoldemHandHistory ReadHistory() => history;
        public HoldemPublicUtterances ReadPublicUtterances() => publicUtterances;

        public void Poll()
        {
            if (disposed) return;
            bool changed = false;
            client.Poll();
            while (client.TryReadResponse(out var response))
            {
                try { if (speech.Handle(response)) { changed = true; continue; } }
                catch (ArgumentException) { invalidState = true; client.Dispose(); Changed?.Invoke(); return; }
                if (pending == null || response.id != pending.id) continue;
                changed = true;
                if (response.type == "receipt" && response.accepted && IsLobbyLeavePending)
                { LobbyLeaveConfirmed = true; ClearPending(); ErrorText = ""; }
                else if (response.type == "receipt" && response.accepted && response.hasReceipt && response.receipt.hasVersion)
                    acceptedVersion = response.receipt.version;
                else if (response.type == "receipt" && response.accepted && (pending.type == "ready" || pending.type == "rematch-ready"))
                    acceptedVersion = -2;
                else if (response.type == "receipt" && !response.accepted && !response.hasReceipt
                    && response.error == nameof(HoldemRoomError.Paused) && IsPauseGuardedInput)
                {
                    // Pause is checked before command deduplication, so this is not a verdict on
                    // the original intent. Preserve it (and any earlier accepted version) until resume.
                    pendingPauseRevision = Math.Max(pendingPauseRevision, observedRevision);
                }
                else if (response.type == "receipt" || response.type == "rejected")
                {
                    ErrorText = Message(response); ClearPending();
                }
            }
            if (IsLobbyLeavePending && client.AdmissionError == "LobbyLeft")
            { LobbyLeaveConfirmed = true; ClearPending(); ErrorText = ""; changed = true; }
            var latest = client.Latest;
            if (latest != null && latest.revision != observedRevision)
            {
                try
                {
                    var nextLobby = new HoldemLobbyDisplay(latest);
                    if (Lobby != null && Lobby.SeatCapacity != nextLobby.SeatCapacity)
                        throw new ArgumentException("Confirmed room capacity changed.");
                    if (Lobby != null && ((Lobby.Rules == null) != (nextLobby.Rules == null)
                        || Lobby.Rules != null && !Lobby.Rules.Matches(nextLobby.Rules)))
                        throw new ArgumentException("Confirmed room rules changed.");
                    var nextDisplay = latest.hasGame ? new HoldemTableDisplay(latest) : null;
                    if (Lobby != null && (Lobby.HasRematch != nextLobby.HasRematch
                        || nextLobby.MatchNumber < Lobby.MatchNumber
                        || nextLobby.MatchNumber > Lobby.MatchNumber && (display?.IsOver != true || nextDisplay == null
                            || nextLobby.MatchNumber != Lobby.MatchNumber + 1
                            || nextDisplay.HandId == display.HandId
                            || nextLobby.MatchNumber - Lobby.MatchNumber > nextDisplay.HandNumber - display.HandNumber)))
                        throw new ArgumentException("Match state regressed or changed without a new hand.");
                    if (Lobby?.HasGame == true)
                        for (int i = 0; i < Lobby.MemberCount; i++)
                            if (nextLobby.GetMember(i).RematchRevision < Lobby.GetMember(i).RematchRevision)
                                throw new ArgumentException("Consent revision regressed.");
                    var nextSpeech = HoldemUtterancePacketReader.Read(latest);
                    var nextHistory = HoldemHistoryPacketReader.Read(latest, history);
                    var nextPublicSpeech = HoldemPublicUtterancePacketReader.Read(latest, publicUtterances);
                    if (display != null && (nextDisplay == null || nextDisplay.SessionVersion < display.SessionVersion
                        || nextDisplay.HandNumber < display.HandNumber
                        || nextDisplay.HandNumber == display.HandNumber && nextDisplay.HandId != display.HandId))
                        throw new ArgumentException("Game state regressed.");
                    speech.Observe(nextSpeech);
                    if (nextLobby.Paused) speech.DeferForPause(latest.revision);
                    history = nextHistory;
                    publicUtterances = nextPublicSpeech;
                    Lobby = nextLobby;
                    if (nextLobby.Paused && IsPauseGuardedInput) pendingPauseRevision = latest.revision;
                    if (nextDisplay != null) display = nextDisplay;
                    invalidState = false; awaitingFreshState = false;
                }
                catch (ArgumentException) { invalidState = true; client.Dispose(); }
                observedRevision = latest.revision; changed = true;
            }
            if (pending != null && acceptedVersion >= 0 && display != null && display.SessionVersion >= acceptedVersion)
            {
                ClearPending(); ErrorText = ""; changed = true;
            }
            if (pending?.type == "ready" && acceptedVersion == -2 && Lobby != null && Lobby.IsReady == pending.ready)
            { ClearPending(); ErrorText = ""; changed = true; }
            if (pending?.type == "rematch-ready" && Lobby != null && display != null
                && pending.handId == display.HandId.ToString("N") && Lobby.RematchRevision > pending.rematchRevision)
            {
                bool confirmed = Lobby.RematchRevision == pending.rematchRevision + 1 && Lobby.IsRematchReady == pending.ready;
                ErrorText = confirmed ? "" : "다시 하려면 준비를 다시 눌러 주세요.";
                ClearPending(); changed = true;
            }
            if (!awaitingFreshState && pending != null && display != null)
            {
                bool priorHand = (pending.type == "act" || pending.type == "deal" || pending.type == "reveal" || pending.type == "settle")
                    && pending.handId != display.HandId.ToString("N");
                bool readyAfterStart = pending.type == "ready" && Lobby.HasGame
                    || pending.type == "rematch-ready" && pending.handId != display.HandId.ToString("N");
                bool startsHand = pending.type == "start" || pending.type == "rematch";
                bool startConfirmed = startsHand && pending.handId == display.HandId.ToString("N");
                bool anotherHandStarted = startsHand && !startConfirmed && display.HandId != pendingBasisHandId;
                if (priorHand || readyAfterStart || startConfirmed || anotherHandStarted)
                {
                    ErrorText = priorHand || anotherHandStarted ? "이전 판의 입력 결과는 확인하지 못했어요. 현재 판을 확인해 주세요." : "";
                    ClearPending(); changed = true;
                }
            }
            if (retryAfterReconnect && client.IsAdmitted && latest != null && (!latest.paused || IsLobbyLeavePending) && !invalidState)
            {
                retryAfterReconnect = false;
                // Same command ID, hand and version: this is receipt recovery, not a new action in the new state.
                if (pending != null && !HasPauseDeferredInput) { client.Send(pending); pendingStartedAt = sentAt = milliseconds(); }
                speech.RetryPending();
            }
            string status = StatusText;
            if (status != previousStatus) { previousStatus = status; changed = true; }
            if (changed) Changed?.Invoke();
        }

        public void ReplaceConnection(HoldemTcpClient replacement)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (!ReferenceEquals(replacement.Identity, client.Identity)) throw new ArgumentException("Keep the original private seat identity.");
            client.Dispose(); client = replacement; observedRevision = -1; invalidState = false; retryAfterReconnect = true; awaitingFreshState = true;
            pendingStartedAt = sentAt = milliseconds();
            Changed?.Invoke();
        }

        public void Act(HoldemTableDisplay basis, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            var request = Request("act", basis); request.action = (int)action.Kind; request.target = action.Target;
            Submit(request);
        }
        public void SetReady(bool ready)
        {
            if (Lobby == null || Lobby.HasGame) return;
            Submit(new HoldemWireRequest { type = "ready", id = Guid.NewGuid().ToString("N"), ready = ready });
        }
        public void SetRematchReady(bool ready)
        {
            if (display?.IsOver != true || Lobby?.HasRematch != true || ready && !Lobby.RematchSupported) return;
            var request = Request("rematch-ready", display);
            request.ready = ready; request.rematchRevision = Lobby.RematchRevision; Submit(request);
        }
        public void RestartMatch()
        {
            if (!IsHost || display?.IsOver != true || Lobby?.AllRematchReady != true) return;
            var request = Request("rematch", display);
            request.completedHandId = display.HandId.ToString("N");
            request.handId = Guid.NewGuid().ToString("N"); Submit(request);
        }
        public bool RequestLobbyLeave()
        {
            if (!CanLeaveLobby) return false;
            return Submit(new HoldemWireRequest { type = "leave", id = Guid.NewGuid().ToString("N") });
        }
        public void StartTable()
        {
            if (!IsHost || Lobby.HasGame || !Lobby.AllReady) return;
            Submit(new HoldemWireRequest { type = "start", id = Guid.NewGuid().ToString("N"),
                handId = Guid.NewGuid().ToString("N"), version = 0 });
        }
        public void NextHand(HoldemTableDisplay basis)
        {
            if (!IsHost) return;
            var request = Request("start", basis); request.handId = Guid.NewGuid().ToString("N"); Submit(request);
        }
        public void Resolve(HoldemTableDisplay basis)
        {
            if (!IsHost) return;
            // The existing screen explicitly explains this one-hand priority before the host confirms it.
            var request = Request("settle", basis); request.oddChipRule = (int)HoldemOddChipRule.ClockwiseFromButton; Submit(request);
        }
        public void Deal(HoldemTableDisplay basis)
        {
            if (!IsHost || basis.PendingDeal == null) return;
            var request = Request("deal", basis); request.windowId = basis.PendingDeal.WindowId.ToString("N");
            request.street = (int)basis.PendingDeal.Street; Submit(request);
        }
        public void Reveal(HoldemTableDisplay basis)
        {
            if (!IsHost) return;
            var request = Request("reveal", basis); request.street = (int)basis.Street; Submit(request);
        }
        public void Refresh()
        {
            if (disposed) return;
            if (client.IsClosed) { if (client.AdmissionError == null) reconnect?.Invoke(); return; }
            if (invalidState || awaitingFreshState || !client.IsAdmitted) return;
            // A paused authority rejects before looking up old receipts. Wait without reclassifying
            // an already-applied, unconfirmed action as a rejection.
            if (Lobby?.Paused == true && !IsLobbyLeavePending) return;
            if (HasPauseDeferredInput)
            {
                if (!CanConfirmDeferredInput) return;
                if (client.Send(pending) != null)
                {
                    pendingPauseRevision = -1;
                    pendingStartedAt = sentAt = milliseconds();
                }
                speech.RetryPending(); Changed?.Invoke(); return;
            }
            if (CanReconnectStalledInput)
            {
                // Close our transport only. Keep the original pending ID and private seat identity.
                client.Dispose(); reconnect(); Changed?.Invoke(); return;
            }
            if (pending != null)
            {
                if (milliseconds() - sentAt < 3000) return;
                if (client.Send(pending) != null) sentAt = milliseconds();
            }
            else client.Send(new HoldemWireRequest { type = "sync" });
            speech.RetryPending();
            Changed?.Invoke();
        }

        private static HoldemWireRequest Request(string type, HoldemTableDisplay basis)
        {
            if (basis == null) throw new ArgumentNullException(nameof(basis));
            return new HoldemWireRequest { type = type, id = Guid.NewGuid().ToString("N"),
                sessionId = basis.SessionId.ToString("N"), handId = basis.HandId.ToString("N"), version = basis.SessionVersion };
        }
        private bool Submit(HoldemWireRequest request)
        {
            if (request.type == "leave" ? !CanLeaveLobby : !CanSend) return false;
            ErrorText = "";
            if (client.Send(request) == null) { ErrorText = "입력을 보내지 못했어요. 연결을 확인해 주세요."; Changed?.Invoke(); return false; }
            pending = request; acceptedVersion = -1; pendingPauseRevision = -1; pendingStartedAt = sentAt = milliseconds();
            pendingBasisHandId = display?.HandId ?? Guid.Empty;
            Changed?.Invoke(); return true;
        }
        private void ClearPending()
        { pending = null; acceptedVersion = -1; pendingPauseRevision = -1; }
        private static string Message(HoldemWireResponse response)
        {
            if (response.hasReceipt && Enum.TryParse(response.receipt.error, out HoldemCommandError error))
                return KoreanPokerText.CommandErrorMessage(error);
            switch (response.error)
            {
                case "Paused": return "다른 참가자의 재접속을 기다리고 있어요.";
                case "HostOnly": return "방장만 진행할 수 있어요.";
                case "SessionGone": return "기존 방이 종료됐어요. 방에 다시 참가해 주세요.";
                case "PlayersNotReady": return "모두 준비하면 시작할 수 있어요.";
                case "AlreadyStarted": return "이미 게임이 시작됐어요. 나갈지 다시 확인해 주세요.";
                case "StaleRematchConsent": return "다시 하려면 준비를 다시 눌러 주세요.";
                case "MatchNotOver": return "최종 승부가 끝난 뒤에 다시 시작할 수 있어요.";
                case "ClientUpgradeRequired": return "같은 방에서 다시 하려면 모두 새 버전으로 실행해 주세요.";
                default: return "입력이 반영되지 않았어요. 현재 화면을 확인해 주세요.";
            }
        }
        private static string AdmissionMessage(string error)
        {
            switch (error)
            {
                case "Full": return "방에 빈자리가 없어요. 다른 방을 확인해 주세요.";
                case "ClientUpgradeRequired": return "이 방은 새 버전이 필요해요. 같은 버전으로 실행해 주세요.";
                case "AlreadyStarted": return "이미 시작한 방이에요. 진행 중인 판에는 새로 참가할 수 없어요.";
                case "SessionGone": return "기존 방이 종료됐어요. 처음 화면에서 새 방에 참가해 주세요.";
                case "InvalidIdentity": case "IdentityConflict": return "기존 자리의 연결 정보를 확인하지 못했어요.";
                case "LobbyLeft": return "이미 나간 자리예요. 처음 화면에서 다시 참가해 주세요.";
                case "AdmissionLimit": return "이 방의 접속 한도에 도달했어요. 방장에게 새 방을 부탁해 주세요.";
                default: return "방에 참가하지 못했어요. 주소와 방 상태를 확인해 주세요.";
            }
        }
        public void Dispose() { if (disposed) return; disposed = true; client.Dispose(); Changed = null; }
    }
}
