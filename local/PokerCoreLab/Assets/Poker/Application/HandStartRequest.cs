using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public enum HandStartKind { First, Next }

    /// <summary>Frozen, trusted lifecycle intent. Never accept this directly from a player.</summary>
    public sealed class HandStartRequest
    {
        private HandStartRequest(HandStartKind kind, Guid handId, Guid commandId, HandSetup setup,
            Guid? predecessorHandId, long? predecessorVersion)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A new hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A start command ID is required.", nameof(commandId));
            if (predecessorHandId == Guid.Empty)
                throw new ArgumentException("A predecessor hand ID is required.", nameof(predecessorHandId));
            if (predecessorVersion.HasValue && predecessorVersion.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(predecessorVersion));
            Kind = kind; HandId = handId; CommandId = commandId;
            Setup = setup ?? throw new ArgumentNullException(nameof(setup));
            PredecessorHandId = predecessorHandId; PredecessorVersion = predecessorVersion;
        }

        public HandStartKind Kind { get; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        /// <summary>Authority-only configuration, already frozen by HandSetup.</summary>
        public HandSetup Setup { get; }
        public Guid? PredecessorHandId { get; }
        public long? PredecessorVersion { get; }

        public static HandStartRequest First(Guid handId, Guid commandId, HandSetup setup)
            => new HandStartRequest(HandStartKind.First, handId, commandId, setup, null, null);

        public static HandStartRequest Next(Guid handId, Guid commandId, HandSetup setup,
            Guid completedHandId, long completedVersion)
            => new HandStartRequest(HandStartKind.Next, handId, commandId, setup, completedHandId, completedVersion);

        internal bool HasSamePayload(HandStartRequest other)
        {
            if (Kind != other.Kind || HandId != other.HandId || PredecessorHandId != other.PredecessorHandId ||
                PredecessorVersion != other.PredecessorVersion) return false;
            HandSetup left = Setup, right = other.Setup;
            if (left.SmallBlind != right.SmallBlind || left.BigBlind != right.BigBlind ||
                left.StartingLedger.SeatCount != right.StartingLedger.SeatCount) return false;
            for (int i = 0; i < left.StartingLedger.SeatCount; i++)
            {
                SeatId seat = left.StartingLedger.GetSeatAt(i);
                if (seat != right.StartingLedger.GetSeatAt(i)) return false;
                SeatChips a = left.StartingLedger.GetChips(seat), b = right.StartingLedger.GetChips(seat);
                if (a.Stack != b.Stack || a.Committed != b.Committed) return false;
            }
            return SameOrder(left.DealOrder, right.DealOrder) && SameOrder(left.OpeningOrder, right.OpeningOrder) &&
                SameOrder(left.ExchangeOrder, right.ExchangeOrder) && SameOrder(left.ClosingOrder, right.ClosingOrder) &&
                SameOrder(left.OddChipPriority, right.OddChipPriority);
        }

        private static bool SameOrder(IReadOnlyList<SeatId> left, IReadOnlyList<SeatId> right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }
}
