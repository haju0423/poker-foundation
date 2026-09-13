using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Authority-side completed-result allowlist. Serialize calls with the owning session.</summary>
    public static class PokerHandResultProjector
    {
        public static PokerHandResultView Create(PokerHandSession session, SeatId authorizedViewer)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!authorizedViewer.IsValid) throw new ArgumentException("A valid authorized seat is required.", nameof(authorizedViewer));
            PokerHandState state = session.State;
            if (state == null) throw new InvalidOperationException("No hand has started.");
            // Validate membership without copying or examining that participant's cards.
            state.Ledger.GetChips(authorizedViewer);
            if (!state.IsComplete || state.Settlement == null)
                throw new InvalidOperationException("A completed settlement is required.");

            var seats = new HandSeatResult[state.SeatCount];
            int live = 0;
            for (int i = 0; i < seats.Length; i++)
            {
                SeatId seat = state.GetSeatAt(i);
                seats[i] = new HandSeatResult(seat, state.Ledger.GetChips(seat).Stack, state.Settlement.GetAwardedTo(seat));
                if (!state.IsFolded(seat)) live++;
            }
            var pots = new HandPotResult[state.Settlement.PotCount];
            for (int i = 0; i < pots.Length; i++) pots[i] = new HandPotResult(state.Settlement.GetPot(i));
            var refunds = new List<HandRefund>(2);
            AddRefund(refunds, state.FirstBetting, HandPhase.FirstBetting);
            AddRefund(refunds, state.SecondBetting, HandPhase.SecondBetting);
            return new PokerHandResultView(session.HandId, session.Version, authorizedViewer,
                live == 1 ? HandCompletionReason.Uncontested : HandCompletionReason.Showdown,
                state.Settlement.TotalAwarded, seats, pots, refunds.ToArray());
        }
        private static void AddRefund(List<HandRefund> result, BettingRound round, HandPhase phase)
        {
            if (round == null || round.RefundedAmount == 0) return;
            if (!round.RefundedSeat.HasValue) throw new InvalidOperationException("Refund amount has no recipient.");
            result.Add(new HandRefund(phase, round.RefundedSeat.Value, round.RefundedAmount));
        }
    }
}
