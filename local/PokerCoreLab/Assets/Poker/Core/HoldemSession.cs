using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HoldemCommandError
    {
        None = 0,
        WrongSession,
        WrongHand,
        UnauthorizedSeat,
        NotStarted,
        Busy,
        VersionMismatch,
        WrongTurn,
        IllegalAction,
        HandComplete,
        CannotContinue,
        AlreadyStarted,
        HandIdReused,
        CommandConflict
    }

    public sealed class HoldemStartCommand
    {
        public HoldemStartCommand(Guid sessionId, Guid handId, Guid commandId, long expectedVersion)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            ExpectedVersion = expectedVersion;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        /// <summary>The authority session version, not the per-hand version.</summary>
        public long ExpectedVersion { get; }

        internal bool HasSamePayload(HoldemStartCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId
            && CommandId == other.CommandId && ExpectedVersion == other.ExpectedVersion;
    }

    public sealed class HoldemCommand
    {
        private HoldemCommand(Guid sessionId, Guid handId, Guid commandId, SeatId seat,
            long expectedVersion, BettingAction action)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            Action = action ?? throw new ArgumentNullException(nameof(action));
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            Seat = seat;
            ExpectedVersion = expectedVersion;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        /// <summary>The authority session version, not the per-hand version.</summary>
        public long ExpectedVersion { get; }
        public BettingAction Action { get; }

        public static HoldemCommand Act(Guid sessionId, Guid handId, Guid commandId,
            SeatId seat, long expectedVersion, BettingAction action)
            => new HoldemCommand(sessionId, handId, commandId, seat, expectedVersion, action);

        internal bool HasSamePayload(HoldemCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && CommandId == other.CommandId
            && Seat == other.Seat && ExpectedVersion == other.ExpectedVersion
            && Action.Kind == other.Action.Kind && Action.Target == other.Action.Target;
    }

    public sealed class HoldemReceipt
    {
        internal HoldemReceipt(Guid sessionId, Guid handId, Guid commandId, SeatId? seat,
            long? version, HoldemCommandError error)
        {
            SessionId = sessionId;
            HandId = handId;
            CommandId = commandId;
            Seat = seat;
            Version = version;
            Error = error;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId? Seat { get; }
        public long? Version { get; }
        public HoldemCommandError Error { get; }
        public bool Accepted => Error == HoldemCommandError.None;
    }

    /// <summary>
    /// Single-authority heads-up session. It persists settled stacks, alternates the button,
    /// rejects stale commands without mutation, and requires an explicit start for each hand.
    /// Calls must be serialized; this is not authentication or durable storage.
    /// </summary>
    public sealed class HoldemSession
    {
        private sealed class AcceptedCommand
        {
            public AcceptedCommand(HoldemCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private readonly HoldemConfig config;
        private readonly IRandomSource random;
        private readonly Dictionary<Guid, AcceptedCommand> accepted = new Dictionary<Guid, AcceptedCommand>();
        private HashSet<Guid> startedHandIds = new HashSet<Guid>();
        private ChipLedger settledLedger;
        private HoldemHand currentHand;
        private SeatId nextButton;
        private HoldemStartCommand currentStartCommand;
        private HoldemReceipt currentStartReceipt;
        private bool processing;

        public HoldemSession(Guid sessionId, HoldemConfig config, SeatId humanSeat,
            SeatId opponentSeat, IRandomSource random)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            if (!humanSeat.IsValid || !opponentSeat.IsValid || humanSeat == opponentSeat)
                throw new ArgumentException("Phase 1 requires two distinct valid seats.");
            SessionId = sessionId;
            HumanSeat = humanSeat;
            OpponentSeat = opponentSeat;
            nextButton = humanSeat;
            settledLedger = ChipLedger.Create(new[] {
                new SeatChips(humanSeat, config.StartingStack),
                new SeatChips(opponentSeat, config.StartingStack)
            });
        }

        public Guid SessionId { get; }
        public SeatId HumanSeat { get; }
        public SeatId OpponentSeat { get; }
        public long Version { get; private set; }
        public long HandNumber { get; private set; }
        public Guid? CurrentHandId => currentHand?.HandId;
        public bool HasActiveHand => currentHand != null && !currentHand.IsComplete;
        public bool CanContinue => (currentHand == null || currentHand.IsComplete)
            && settledLedger.GetChips(HumanSeat).Stack > 0
            && settledLedger.GetChips(OpponentSeat).Stack > 0;
        public bool IsOver => currentHand != null && currentHand.IsComplete && !CanContinue;
        public SeatId? BustedSeat
        {
            get
            {
                if (!IsOver) return null;
                return settledLedger.GetChips(HumanSeat).Stack == 0 ? HumanSeat : OpponentSeat;
            }
        }

        public HoldemReceipt StartNextHand(HoldemStartCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentStartCommand != null && currentStartCommand.CommandId == command.CommandId)
                return currentStartCommand.HasSamePayload(command) ? currentStartReceipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (currentHand != null && currentHand.HandId == command.HandId)
            {
                return Reject(command, HoldemCommandError.AlreadyStarted);
            }
            if (startedHandIds.Contains(command.HandId))
                return Reject(command, HoldemCommandError.HandIdReused);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (currentHand != null && !currentHand.IsComplete)
                return Reject(command, HoldemCommandError.AlreadyStarted);
            if (!CanContinue) return Reject(command, HoldemCommandError.CannotContinue);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                long nextHandNumber = checked(HandNumber + 1);
                var nextStartedHandIds = new HashSet<Guid>(startedHandIds);
                if (!nextStartedHandIds.Add(command.HandId))
                    throw new InvalidOperationException("A prevalidated hand ID could not be reserved.");
                SeatId button = nextButton;
                SeatId other = button == HumanSeat ? OpponentSeat : HumanSeat;
                HoldemHand candidate = HoldemHand.Begin(command.HandId, settledLedger,
                    button, other, config, random);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                HandNumber = nextHandNumber;
                nextButton = other;
                startedHandIds = nextStartedHandIds;
                currentStartCommand = command;
                currentStartReceipt = receipt;
                accepted.Clear();
                if (candidate.IsComplete) settledLedger = candidate.Result.Ledger;
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemReceipt StartNextHand(Guid handId, Guid commandId, long expectedVersion)
            => StartNextHand(new HoldemStartCommand(SessionId, handId, commandId, expectedVersion));

        /// <summary>The adapter supplies authorizedSeat from access control, never from untrusted request data.</summary>
        public HoldemReceipt Submit(SeatId authorizedSeat, HoldemCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat);
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentStartReceipt != null && command.CommandId == currentStartReceipt.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.TryGetValue(command.CommandId, out AcceptedCommand previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (currentHand.IsComplete) return Reject(command, HoldemCommandError.HandComplete);
            if (currentHand.CurrentSeat != command.Seat) return Reject(command, HoldemCommandError.WrongTurn);
            if (!currentHand.CurrentBetting.GetLegalActions().Allows(command.Action))
                return Reject(command, HoldemCommandError.IllegalAction);

            processing = true;
            try
            {
                HoldemHand candidate = currentHand.Apply(command.Seat, command.Action);
                long nextVersion = checked(Version + 1);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    command.Seat, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                accepted.Add(command.CommandId, new AcceptedCommand(command, receipt));
                if (candidate.IsComplete) settledLedger = candidate.Result.Ledger;
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemSnapshot GetSnapshot(SeatId viewer)
        {
            if (currentHand == null) throw new InvalidOperationException("No Hold'em hand has started.");
            return new HoldemSnapshot(SessionId, Version, HandNumber, CanContinue, IsOver,
                BustedSeat, currentHand, viewer);
        }

        private HoldemReceipt Reject(HoldemCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                command.Seat, null, error);

        private HoldemReceipt Reject(HoldemStartCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                null, null, error);
    }
}
