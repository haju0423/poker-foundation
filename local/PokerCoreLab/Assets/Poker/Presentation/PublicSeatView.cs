using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Public chip/status data only. No cards, hand value or authority object is retained.</summary>
    public sealed class PublicSeatView
    {
        internal PublicSeatView(SeatId seat, long stack, long committed, long? streetContribution,
            bool folded, bool allIn, long? awarded)
        {
            Seat = seat; Stack = stack; Committed = committed; StreetContribution = streetContribution;
            IsFolded = folded; IsAllIn = allIn; Awarded = awarded;
        }

        public SeatId Seat { get; }
        public long Stack { get; }
        /// <summary>Outstanding contribution across the hand, after refunds; zero after payout.</summary>
        public long Committed { get; }
        /// <summary>Current actionable betting street payment, or null outside that street's participants.</summary>
        public long? StreetContribution { get; }
        public bool IsFolded { get; }
        /// <summary>Nonfolded with no spendable chips during an unfinished hand. False after settlement.</summary>
        public bool IsAllIn { get; }
        /// <summary>Gross pot payout, not profit or a refund. Null until settlement; zero for a nonrecipient.</summary>
        public long? Awarded { get; }
    }
}
