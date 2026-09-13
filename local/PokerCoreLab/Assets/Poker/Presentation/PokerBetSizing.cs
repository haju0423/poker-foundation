using System;

namespace Poker.Presentation
{
    /// <summary>Input suggestions within the authority's legal range. Does not place bets or define betting rules.</summary>
    public static class PokerBetSizing
    {
        /// <summary>
        /// Street total after calling, plus half of the resulting whole pot (rounded down), clamped to legal limits.
        /// This is an input shortcut, not a strategy recommendation or a pot-limit rule.
        /// </summary>
        public static long? HalfPotTarget(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            PlayerBettingOptions legal = view.Betting;
            if (legal == null || (!legal.CanBet && !legal.CanRaise)
                || !legal.MinimumAggressiveTarget.HasValue || !legal.MaximumAggressiveTarget.HasValue) return null;
            long? paid = null;
            foreach (PublicSeatView seat in view.Seats)
                if (seat.Seat == view.ViewerSeat) { paid = seat.StreetContribution; break; }
            if (!paid.HasValue) throw new InvalidOperationException("An actionable betting view must include its street contribution.");
            decimal afterCall = (decimal)view.PotAmount + legal.CallAmount;
            decimal target = (decimal)paid.Value + legal.CallAmount + decimal.Floor(afterCall / 2m);
            return (long)Math.Min((decimal)legal.MaximumAggressiveTarget.Value,
                Math.Max((decimal)legal.MinimumAggressiveTarget.Value, target));
        }
    }
}
