using System;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HoldemRoomError
    {
        None, UnknownConnection, Disconnected, Full, AlreadyStarted, HostOnly,
        WaitingForPlayers, PlayersNotReady, Paused, InvalidResumeToken,
        ConnectionInUse, MemberStillConnected, CoreRejected, HostCannotLeave, UtteranceRejected,
        MatchNotOver, StaleRematchConsent, ClientUpgradeRequired, CommandConflict, AccusationsDisabled
    }

    /// <summary>Deliver only to the admitted connection. ResumeToken is a bearer secret, not public room state.</summary>
    public sealed class HoldemRoomAdmission
    {
        internal HoldemRoomAdmission(SeatId seat, string token) { Seat = seat; ResumeToken = token; }
        public SeatId Seat { get; }
        public string ResumeToken { get; }
    }

    public sealed class HoldemRoomJoinResult
    {
        internal HoldemRoomJoinResult(HoldemRoomError error, HoldemRoomAdmission admission = null)
        { Error = error; Admission = admission; }
        public HoldemRoomError Error { get; }
        public HoldemRoomAdmission Admission { get; }
        public bool Accepted => Error == HoldemRoomError.None;
    }

    public sealed class HoldemRoomReceipt
    {
        internal HoldemRoomReceipt(HoldemRoomError error, HoldemReceipt poker = null)
        { Error = error; Poker = poker; }
        public HoldemRoomError Error { get; }
        public HoldemReceipt Poker { get; }
        public bool Accepted => Error == HoldemRoomError.None;
    }

    /// <summary>No claimed seat: the server resolves the transport connection before constructing a core command.</summary>
    public sealed class HoldemRoomAction
    {
        public HoldemRoomAction(Guid sessionId, Guid handId, Guid commandId, long expectedVersion, BettingAction action)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand and command IDs are required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId; HandId = handId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Action = action ?? throw new ArgumentNullException(nameof(action));
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public BettingAction Action { get; }
    }

    public sealed class HoldemRoomMemberView
    {
        internal HoldemRoomMemberView(SeatId seat, string name, bool connected, bool ready, bool host,
            bool rematchReady = false, long rematchRevision = 0)
        { Seat = seat; Name = name; Connected = connected; Ready = ready; IsHost = host;
            RematchReady = rematchReady; RematchRevision = rematchRevision; }
        public SeatId Seat { get; }
        public string Name { get; }
        public bool Connected { get; }
        public bool Ready { get; }
        public bool IsHost { get; }
        public bool RematchReady { get; }
        public long RematchRevision { get; }
    }

    /// <summary>Public, immutable setup for this room. No dealer state or admission data.</summary>
    public sealed class HoldemRoomRules
    {
        public HoldemRoomRules(HoldemConfig config, bool receivesUtterances, int seatCapacity = HoldemRoom.Capacity,
            bool publishesUtterances = false)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            ValidateSeatCapacity(seatCapacity);
            if (publishesUtterances && !receivesUtterances)
                throw new ArgumentException("Public speech requires enabled intake.", nameof(publishesUtterances));
            if (config.StartingStack > long.MaxValue / seatCapacity)
                throw new ArgumentOutOfRangeException(nameof(config), "Starting stacks must fit the chip ledger.");
            SeatCapacity = seatCapacity;
            StartingStack = config.StartingStack; SmallBlind = config.SmallBlind; BigBlind = config.BigBlind;
            WaitsForHostDeal = config.DealPolicy == HoldemDealPolicy.WaitForHost;
            PausesAfterReveal = config.RevealPolicy == HoldemRevealPolicy.PauseAfterCommunityReveal;
            ReceivesUtterances = receivesUtterances;
            PublishesUtterances = publishesUtterances;
            AccusationsEnabled = config.AccusationMode != HoldemAccusationMode.Disabled;
        }
        public long StartingStack { get; }
        public int SeatCapacity { get; }
        public static void ValidateSeatCapacity(int value)
        {
            if (value < 3 || value > HoldemRoom.Capacity) throw new ArgumentOutOfRangeException(nameof(value));
        }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public bool WaitsForHostDeal { get; }
        public bool PausesAfterReveal { get; }
        public bool ReceivesUtterances { get; }
        public bool PublishesUtterances { get; }
        public bool AccusationsEnabled { get; }
        public bool Matches(HoldemRoomRules other) => other != null && SeatCapacity == other.SeatCapacity && StartingStack == other.StartingStack
            && SmallBlind == other.SmallBlind && BigBlind == other.BigBlind
            && WaitsForHostDeal == other.WaitsForHostDeal && PausesAfterReveal == other.PausesAfterReveal
            && ReceivesUtterances == other.ReceivesUtterances && PublishesUtterances == other.PublishesUtterances
            && AccusationsEnabled == other.AccusationsEnabled;
    }

    /// <summary>One connection's projection. Never broadcast this object to the other three connections.</summary>
    public sealed class HoldemRoomView
    {
        private readonly HoldemRoomMemberView[] members;
        private readonly HoldemActionNotice[] streetActions;
        internal HoldemRoomView(Guid sessionId, long revision, SeatId viewer, bool paused,
            HoldemRoomMemberView[] ownedMembers, HoldemSnapshot game, HoldemActionNotice lastAction,
            HoldemActionNotice[] ownedStreetActions, HoldemUtteranceView ownUtterances = null, HoldemRoomRules rules = null,
            HoldemHandHistory history = null, HoldemPublicUtterances publicUtterances = null,
            long matchNumber = 1, bool rematchSupported = false, HoldemOwnCardChange ownCardChange = null)
        { SessionId = sessionId; Revision = revision; ViewerSeat = viewer; Paused = paused;
            members = ownedMembers; Game = game; LastAction = lastAction; streetActions = ownedStreetActions;
            OwnUtterances = ownUtterances; Rules = rules; History = history; PublicUtterances = publicUtterances;
            MatchNumber = matchNumber; RematchSupported = rematchSupported; OwnCardChange = ownCardChange; }
        public Guid SessionId { get; }
        public long Revision { get; }
        public SeatId ViewerSeat { get; }
        public bool Paused { get; }
        public HoldemSnapshot Game { get; }
        public HoldemActionNotice LastAction { get; }
        public HoldemUtteranceView OwnUtterances { get; }
        public HoldemPublicUtterances PublicUtterances { get; }
        public HoldemOwnCardChange OwnCardChange { get; }
        public HoldemRoomRules Rules { get; }
        public HoldemHandHistory History { get; }
        public long MatchNumber { get; }
        public bool RematchSupported { get; }
        // At most one last public betting action per seat, only for the current street.
        public int StreetActionCount => streetActions.Length;
        public HoldemActionNotice GetStreetAction(int index) => streetActions[index];
        public int MemberCount => members.Length;
        public HoldemRoomMemberView GetMember(int index)
        {
            if (index < 0 || index >= members.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return members[index];
        }
    }
}
