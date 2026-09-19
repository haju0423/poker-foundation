using System;

namespace Poker.Foundation
{
    public abstract class HoldemWindowCommand
    {
        protected HoldemWindowCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand, window and command IDs are required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street != HoldemStreet.Flop && street != HoldemStreet.Turn && street != HoldemStreet.River)
                throw new ArgumentOutOfRangeException(nameof(street));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId;
            ExpectedVersion = expectedVersion; Street = street;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        /// <summary>Correlation only, never proof of host or seat authorization.</summary>
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        protected bool SameEnvelope(HoldemWindowCommand other) => other != null
            && SessionId == other.SessionId && HandId == other.HandId && WindowId == other.WindowId
            && CommandId == other.CommandId && ExpectedVersion == other.ExpectedVersion && Street == other.Street;
    }

    /// <summary>A private player choice. A null target means pass, not withdrawal from poker.</summary>
    public sealed class HoldemAccusationChoiceCommand : HoldemWindowCommand
    {
        public HoldemAccusationChoiceCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId seat, SeatId? target)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        {
            if (!seat.IsValid || (target.HasValue && !target.Value.IsValid))
                throw new ArgumentException("A valid seat is required.");
            Seat = seat; Target = target;
        }
        public SeatId Seat { get; }
        public SeatId? Target { get; }
        internal bool HasSamePayload(HoldemAccusationChoiceCommand other) => SameEnvelope(other)
            && Seat == other.Seat && Target == other.Target;
    }

    public enum HoldemAccusationHostAction { CloseWindow = 1, RecordVerdict = 2 }

    /// <summary>Host-only command. Verdicts describe observed manipulation, never an AI guess or a payout.</summary>
    public sealed class HoldemAccusationHostCommand : HoldemWindowCommand
    {
        private HoldemAccusationHostCommand(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, HoldemAccusationHostAction action,
            Guid claimId, bool wasManipulated)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        { Action = action; ClaimId = claimId; WasManipulated = wasManipulated; }

        public HoldemAccusationHostAction Action { get; }
        public Guid ClaimId { get; }
        public bool WasManipulated { get; }
        public static HoldemAccusationHostCommand Close(Guid sessionId, Guid handId, Guid windowId,
            Guid commandId, long version, HoldemStreet street) => new HoldemAccusationHostCommand(
                sessionId, handId, windowId, commandId, version, street, HoldemAccusationHostAction.CloseWindow, Guid.Empty, false);
        public static HoldemAccusationHostCommand Verdict(Guid sessionId, Guid handId, Guid windowId,
            Guid commandId, long version, HoldemStreet street, Guid claimId, bool wasManipulated)
        {
            if (claimId == Guid.Empty) throw new ArgumentException("An accepted accusation ID is required.", nameof(claimId));
            return new HoldemAccusationHostCommand(sessionId, handId, windowId, commandId, version, street,
                HoldemAccusationHostAction.RecordVerdict, claimId, wasManipulated);
        }
        internal bool HasSamePayload(HoldemAccusationHostCommand other) => SameEnvelope(other)
            && Action == other.Action && ClaimId == other.ClaimId && WasManipulated == other.WasManipulated;
    }
}
