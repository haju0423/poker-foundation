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
        CommandConflict,
        SettlementRuleRequired,
        SettlementNotPending,
        InvalidSettlementRule
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

    /// <summary>Single-authority two-to-four-seat session with a persistent clockwise roster.</summary>
    public sealed class HoldemSession
    {
        private sealed class AcceptedCommand
        {
            public AcceptedCommand(HoldemCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private sealed class SettlementCommand
        {
            public SettlementCommand(Guid commandId, Guid handId, long expectedVersion,
                HoldemOddChipRule rule)
            { CommandId = commandId; HandId = handId; ExpectedVersion = expectedVersion; Rule = rule; }
            public Guid CommandId { get; }
            public Guid HandId { get; }
            public long ExpectedVersion { get; }
            public HoldemOddChipRule Rule { get; }
            public bool HasSamePayload(SettlementCommand other) => other != null
                && CommandId == other.CommandId && HandId == other.HandId
                && ExpectedVersion == other.ExpectedVersion && Rule == other.Rule;
        }

        private readonly HoldemConfig config;
        private readonly IRandomSource random;
        private readonly SeatId[] roster;
        private readonly SeatId initialButton;
        private readonly HoldemOddChipRule oddChipRule;
        private readonly Dictionary<Guid, AcceptedCommand> accepted = new Dictionary<Guid, AcceptedCommand>();
        private readonly HashSet<Guid> usedCommandIds = new HashSet<Guid>();
        private HashSet<Guid> startedHandIds = new HashSet<Guid>();
        private ChipLedger settledLedger;
        private HoldemHand currentHand;
        private HoldemStartCommand currentStartCommand;
        private HoldemReceipt currentStartReceipt;
        private SettlementCommand currentSettlementCommand;
        private HoldemReceipt currentSettlementReceipt;
        private bool processing;

        public HoldemSession(Guid sessionId, HoldemConfig config, SeatId humanSeat,
            SeatId opponentSeat, IRandomSource random)
            : this(sessionId, config, new[] { humanSeat, opponentSeat }, humanSeat,
                random, HoldemOddChipRule.RequireExplicitPriority) { }

        public HoldemSession(Guid sessionId, HoldemConfig config,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId initialButton,
            IRandomSource random,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
            : this(sessionId, config, CreateEqualLedger(config, seatsInTableOrder),
                seatsInTableOrder, initialButton, random, oddChipRule) { }

        public HoldemSession(Guid sessionId, HoldemConfig config, ChipLedger startingLedger,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId initialButton,
            IRandomSource random,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            if (startingLedger == null) throw new ArgumentNullException(nameof(startingLedger));
            ValidateRule(oddChipRule);
            roster = HoldemSeatOrder.CopyTable(seatsInTableOrder);
            if (startingLedger.SeatCount != roster.Length || startingLedger.TotalCommitted != 0)
                throw new ArgumentException("The starting ledger must contain the full uncommitted roster.", nameof(startingLedger));
            for (int i = 0; i < roster.Length; i++) startingLedger.GetChips(roster[i]);
            HoldemSeatOrder.IndexOf(roster, initialButton);
            if (CountFunded(startingLedger, roster) < 2)
                throw new ArgumentException("A session requires at least two funded seats.", nameof(startingLedger));
            SessionId = sessionId;
            this.initialButton = initialButton;
            this.oddChipRule = oddChipRule;
            settledLedger = startingLedger;
        }

        public Guid SessionId { get; }
        public HoldemButtonPolicy ButtonPolicy => HoldemButtonPolicy.PokerStarsForwardMoving;
        public HoldemOddChipRule OddChipRule => oddChipRule;
        public int SeatCount => roster.Length;
        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= roster.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return roster[index];
        }
        public SeatId HumanSeat => CompatibilitySeat(0);
        public SeatId OpponentSeat => CompatibilitySeat(1);
        public long Version { get; private set; }
        public long HandNumber { get; private set; }
        public Guid? CurrentHandId => currentHand?.HandId;
        public bool HasActiveHand => currentHand != null && !currentHand.IsComplete;
        public int FundedSeatCount => CountFunded(settledLedger, roster);
        public bool CanContinue => (currentHand == null || currentHand.IsComplete) && FundedSeatCount >= 2;
        public bool IsOver => currentHand != null && currentHand.IsComplete && FundedSeatCount <= 1;
        public SeatId? SessionWinnerSeat
        {
            get
            {
                if (!IsOver) return null;
                for (int i = 0; i < roster.Length; i++)
                    if (settledLedger.GetChips(roster[i]).Stack > 0) return roster[i];
                return null;
            }
        }
        public SeatId? BustedSeat
        {
            get
            {
                RequireHeadsUp();
                if (!IsOver) return null;
                return settledLedger.GetChips(roster[0]).Stack == 0 ? roster[0] : roster[1];
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
            if (currentSettlementCommand != null && currentSettlementCommand.CommandId == command.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.ContainsKey(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (currentHand != null && currentHand.HandId == command.HandId)
                return Reject(command, HoldemCommandError.AlreadyStarted);
            if (startedHandIds.Contains(command.HandId))
                return Reject(command, HoldemCommandError.HandIdReused);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (currentHand != null && !currentHand.IsComplete)
                return Reject(command, currentHand.IsSettlementPending
                    ? HoldemCommandError.SettlementRuleRequired : HoldemCommandError.AlreadyStarted);
            if (!CanContinue) return Reject(command, HoldemCommandError.CannotContinue);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                long nextHandNumber = checked(HandNumber + 1);
                var nextStartedHandIds = new HashSet<Guid>(startedHandIds);
                if (!nextStartedHandIds.Add(command.HandId))
                    throw new InvalidOperationException("A prevalidated hand ID could not be reserved.");
                SeatId button = ResolveNextButton();
                HoldemHand candidate = HoldemHand.Begin(command.HandId, settledLedger,
                    roster, button, config, random, oddChipRule);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                HandNumber = nextHandNumber;
                startedHandIds = nextStartedHandIds;
                currentStartCommand = command;
                currentStartReceipt = receipt;
                currentSettlementCommand = null;
                currentSettlementReceipt = null;
                accepted.Clear();
                usedCommandIds.Add(command.CommandId);
                if (candidate.IsComplete) settledLedger = MergeSettled(candidate);
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemReceipt StartNextHand(Guid handId, Guid commandId, long expectedVersion)
            => StartNextHand(new HoldemStartCommand(SessionId, handId, commandId, expectedVersion));

        /// <summary>The adapter supplies authorizedSeat from access control, not request data.</summary>
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
            if (currentSettlementCommand != null && command.CommandId == currentSettlementCommand.CommandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.TryGetValue(command.CommandId, out AcceptedCommand previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId))
                return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (currentHand.IsSettlementPending) return Reject(command, HoldemCommandError.SettlementRuleRequired);
            if (currentHand.IsComplete) return Reject(command, HoldemCommandError.HandComplete);
            if (currentHand.CurrentSeat != command.Seat) return Reject(command, HoldemCommandError.WrongTurn);
            if (!currentHand.CurrentBetting.GetLegalActions().Allows(command.Action))
                return Reject(command, HoldemCommandError.IllegalAction);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.Apply(command.Seat, command.Action);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    command.Seat, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                accepted.Add(command.CommandId, new AcceptedCommand(command, receipt));
                usedCommandIds.Add(command.CommandId);
                if (candidate.IsComplete) settledLedger = MergeSettled(candidate);
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>Trusted host adoption of one explicit rule for the current pending hand.</summary>
        public HoldemReceipt ResolvePendingSettlement(Guid commandId, long expectedSessionVersion,
            HoldemOddChipRule rule)
        {
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (expectedSessionVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
            Guid handId = currentHand?.HandId ?? Guid.Empty;
            var command = new SettlementCommand(commandId, handId, expectedSessionVersion, rule);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (currentSettlementCommand != null && currentSettlementCommand.CommandId == commandId)
                return currentSettlementCommand.HasSamePayload(command) ? currentSettlementReceipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (currentStartReceipt != null && currentStartReceipt.CommandId == commandId)
                return Reject(command, HoldemCommandError.CommandConflict);
            if (accepted.ContainsKey(commandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(commandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (expectedSessionVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (!currentHand.IsSettlementPending) return Reject(command, HoldemCommandError.SettlementNotPending);
            if (rule != HoldemOddChipRule.ClockwiseFromButton)
                return Reject(command, HoldemCommandError.InvalidSettlementRule);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.ResolvePendingSettlement(rule);
                var receipt = new HoldemReceipt(SessionId, currentHand.HandId, commandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate;
                Version = nextVersion;
                currentSettlementCommand = command;
                currentSettlementReceipt = receipt;
                usedCommandIds.Add(commandId);
                settledLedger = MergeSettled(candidate);
                return receipt;
            }
            finally { processing = false; }
        }

        public HoldemSnapshot GetSnapshot(SeatId viewer)
        {
            if (currentHand == null) throw new InvalidOperationException("No Hold'em hand has started.");
            HoldemSeatOrder.IndexOf(roster, viewer);
            SeatId? compatibilityBusted = null;
            if (roster.Length == 2 && IsOver)
                compatibilityBusted = settledLedger.GetChips(roster[0]).Stack == 0 ? roster[0] : roster[1];
            return new HoldemSnapshot(SessionId, Version, HandNumber, ButtonPolicy,
                CanContinue, IsOver, SessionWinnerSeat, compatibilityBusted,
                roster, settledLedger, currentHand, viewer);
        }

        private SeatId ResolveNextButton()
        {
            Predicate<SeatId> funded = seat => settledLedger.GetChips(seat).Stack > 0;
            if (currentHand != null) return HoldemSeatOrder.Next(roster, currentHand.ButtonSeat, funded);
            if (funded(initialButton)) return initialButton;
            return HoldemSeatOrder.Next(roster, initialButton, funded);
        }

        private ChipLedger MergeSettled(HoldemHand hand)
        {
            if (!hand.IsComplete) throw new InvalidOperationException("Only a completed hand can update session stacks.");
            var balances = new SeatChips[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                SeatId seat = roster[i];
                long stack = hand.WasDealtIn(seat)
                    ? hand.Result.Ledger.GetChips(seat).Stack
                    : settledLedger.GetChips(seat).Stack;
                balances[i] = new SeatChips(seat, stack);
            }
            return ChipLedger.Create(balances);
        }

        private SeatId CompatibilitySeat(int index)
        {
            RequireHeadsUp();
            return roster[index];
        }

        private void RequireHeadsUp()
        {
            if (roster.Length != 2)
                throw new InvalidOperationException("The heads-up compatibility alias is unavailable at a multi-seat table.");
        }

        private HoldemReceipt Reject(HoldemCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                command.Seat, null, error);

        private HoldemReceipt Reject(HoldemStartCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId,
                null, null, error);

        private HoldemReceipt Reject(SettlementCommand command, HoldemCommandError error)
            => new HoldemReceipt(SessionId, command.HandId, command.CommandId, null, null, error);

        private static ChipLedger CreateEqualLedger(HoldemConfig config,
            IReadOnlyList<SeatId> seatsInTableOrder)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SeatId[] seats = HoldemSeatOrder.CopyTable(seatsInTableOrder);
            var balances = new SeatChips[seats.Length];
            for (int i = 0; i < seats.Length; i++) balances[i] = new SeatChips(seats[i], config.StartingStack);
            return ChipLedger.Create(balances);
        }

        private static int CountFunded(ChipLedger ledger, IReadOnlyList<SeatId> seats)
        {
            int count = 0;
            for (int i = 0; i < seats.Count; i++) if (ledger.GetChips(seats[i]).Stack > 0) count++;
            return count;
        }

        private static void ValidateRule(HoldemOddChipRule rule)
        {
            if (rule != HoldemOddChipRule.RequireExplicitPriority && rule != HoldemOddChipRule.ClockwiseFromButton)
                throw new ArgumentOutOfRangeException(nameof(rule));
        }
    }
}
