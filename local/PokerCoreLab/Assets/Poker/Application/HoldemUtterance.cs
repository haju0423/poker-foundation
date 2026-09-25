using System;
using Poker.Foundation;

namespace Poker.Application
{
    [Flags]
    public enum HoldemUtteranceSeats { None = 0, Active = 1, AllIn = 2, Folded = 4 }

    public enum HoldemUtteranceVisibility { OwnOnly = 0, PublicRaw = 1 }
    public enum HoldemUtteranceBatchRetention { UntilHostAcknowledges = 0, None = 1 }

    /// <summary>Explicit intake limits, not team gameplay defaults. Length is measured in UTF-16 code units.</summary>
    public sealed class HoldemUtterancePolicy
    {
        public HoldemUtterancePolicy(int maximumTextLength, int maximumPerSeatPerStreet, HoldemUtteranceSeats allowedSeats,
            HoldemUtteranceVisibility visibility = HoldemUtteranceVisibility.OwnOnly,
            HoldemUtteranceBatchRetention batchRetention = HoldemUtteranceBatchRetention.UntilHostAcknowledges)
        {
            if (maximumTextLength < 1 || maximumTextLength > 4096)
                throw new ArgumentOutOfRangeException(nameof(maximumTextLength));
            if (maximumPerSeatPerStreet < 1 || maximumPerSeatPerStreet > 32)
                throw new ArgumentOutOfRangeException(nameof(maximumPerSeatPerStreet));
            if ((allowedSeats & ~(HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn | HoldemUtteranceSeats.Folded)) != 0)
                throw new ArgumentOutOfRangeException(nameof(allowedSeats));
            if (!Enum.IsDefined(typeof(HoldemUtteranceVisibility), visibility))
                throw new ArgumentOutOfRangeException(nameof(visibility));
            if (!Enum.IsDefined(typeof(HoldemUtteranceBatchRetention), batchRetention))
                throw new ArgumentOutOfRangeException(nameof(batchRetention));
            MaximumTextLength = maximumTextLength; MaximumPerSeatPerStreet = maximumPerSeatPerStreet;
            AllowedSeats = allowedSeats; Visibility = visibility;
            BatchRetention = batchRetention;
        }
        public int MaximumTextLength { get; }
        public int MaximumPerSeatPerStreet { get; }
        public HoldemUtteranceSeats AllowedSeats { get; }
        public HoldemUtteranceVisibility Visibility { get; }
        // Host delivery is independent of public display. None does not claim AI consumption.
        public HoldemUtteranceBatchRetention BatchRetention { get; }
        internal bool Allows(HoldemSeatView seat)
        {
            if (!seat.WasDealtIn) return false;
            var flag = seat.Status == HoldemSeatStatus.Active ? HoldemUtteranceSeats.Active
                : seat.Status == HoldemSeatStatus.AllIn ? HoldemUtteranceSeats.AllIn
                : seat.Status == HoldemSeatStatus.Folded ? HoldemUtteranceSeats.Folded : HoldemUtteranceSeats.None;
            return flag != HoldemUtteranceSeats.None && (AllowedSeats & flag) != 0;
        }
    }

    /// <summary>Raw speech only. No intent, requested card, success flag, or game-state mutation authority.</summary>
    public sealed class HoldemUtteranceCommand
    {
        public HoldemUtteranceCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            HoldemStreet street, SeatId speaker, string text)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("An utterance must identify its session, hand, window and command.");
            if (!speaker.IsValid) throw new ArgumentException("A speaker seat is required.", nameof(speaker));
            if (street != HoldemStreet.Preflop && street != HoldemStreet.Flop && street != HoldemStreet.Turn)
                throw new ArgumentOutOfRangeException(nameof(street));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId;
            Street = street; Speaker = speaker;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        internal bool SamePayload(HoldemUtteranceCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && WindowId == other.WindowId
            && CommandId == other.CommandId && Street == other.Street && Speaker == other.Speaker && Text == other.Text;
    }

    public enum HoldemUtteranceError
    {
        None = 0, Disabled, WrongSession, WrongHand, UnauthorizedSeat, WrongWindow, WindowClosed,
        SeatNotAllowed, EmptyText, TextTooLong, InvalidText, LimitReached, CommandConflict, BacklogFull, DeliveryUnavailable
    }

    public sealed class HoldemUtteranceReceipt
    {
        internal HoldemUtteranceReceipt(HoldemUtteranceError error, Guid commandId, long ordinal = 0)
        { Error = error; CommandId = commandId; Ordinal = ordinal; }
        public bool Accepted => Error == HoldemUtteranceError.None;
        public HoldemUtteranceError Error { get; }
        public Guid CommandId { get; }
        /// <summary>Host observation order, never a game priority or manipulation entitlement.</summary>
        public long Ordinal { get; }
        /// <summary>Reconstruct a player confirmation without transmitting host observation order.</summary>
        public static HoldemUtteranceReceipt Confirmation(Guid commandId, HoldemUtteranceError error)
        {
            if (commandId == Guid.Empty || !Enum.IsDefined(typeof(HoldemUtteranceError), error))
                throw new ArgumentException("A valid command and confirmation error are required.");
            return new HoldemUtteranceReceipt(error, commandId);
        }
    }

    /// <summary>Immutable source text. Acceptance does not establish a real manipulation request.</summary>
    public sealed class HoldemUtteranceEntry
    {
        internal HoldemUtteranceEntry(HoldemUtteranceCommand command, long ordinal)
        {
            SessionId = command.SessionId; HandId = command.HandId; WindowId = command.WindowId;
            CommandId = command.CommandId; Street = command.Street; Speaker = command.Speaker;
            Text = command.Text; Ordinal = ordinal;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public SeatId Speaker { get; }
        public string Text { get; }
        public long Ordinal { get; }
    }

    /// <summary>Seat-bound intake view. Contains only this viewer's source text, not other players' speech.</summary>
    public sealed class HoldemUtteranceView
    {
        private readonly HoldemUtteranceEntry[] ownEntries;
        internal HoldemUtteranceView(HoldemSnapshot state, SeatId viewer, Guid windowId, bool canSubmit,
            int maximumTextLength, int remaining, HoldemUtteranceEntry[] entries, bool isBacklogged = false)
            : this(state.SessionId, state.HandId, state.Street, viewer, windowId, canSubmit, maximumTextLength, remaining, entries, isBacklogged) { }
        internal HoldemUtteranceView(Guid sessionId, Guid handId, HoldemStreet street, SeatId viewer, Guid windowId,
            bool canSubmit, int maximumTextLength, int remaining, HoldemUtteranceEntry[] entries, bool isBacklogged = false)
        {
            SessionId = sessionId; HandId = handId; Street = street; ViewerSeat = viewer;
            WindowId = windowId; CanSubmit = canSubmit; MaximumTextLength = maximumTextLength;
            Remaining = remaining; ownEntries = (HoldemUtteranceEntry[])entries.Clone();
            IsBacklogged = isBacklogged;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public SeatId ViewerSeat { get; }
        public bool CanSubmit { get; }
        public bool IsBacklogged { get; }
        public int MaximumTextLength { get; }
        public int Remaining { get; }
        public int Count => ownEntries.Length;
        public HoldemUtteranceEntry GetEntry(int index) => ownEntries[index];
    }

    /// <summary>Frozen host-only input batch. No game outcome or permission to manipulate cards is implied.</summary>
    public sealed class HoldemUtteranceBatch
    {
        private readonly HoldemUtteranceEntry[] entries;
        internal HoldemUtteranceBatch(Guid sessionId, Guid handId, Guid windowId, HoldemStreet street,
            HoldemUtteranceEntry[] entries)
        {
            SessionId = sessionId; HandId = handId; WindowId = windowId; Street = street;
            this.entries = (HoldemUtteranceEntry[])entries.Clone();
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public int Count => entries.Length;
        public HoldemUtteranceEntry GetEntry(int index) => entries[index];
    }

    public interface IHoldemUtterancePlayerPort
    {
        HoldemUtteranceView ReadUtterances();
        HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command);
    }
}
