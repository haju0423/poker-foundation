using System;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Authority-side allowlist projection, not authentication, a network endpoint, or a serializer.</summary>
    public static class PokerPlayerViewProjector
    {
        /// <summary>
        /// The authority must bind authorizedViewer to the authenticated connection/local player; never copy it
        /// from an untrusted request. Serialize this call with Start/Submit on the session's owning thread.
        /// Only return the resulting view to that participant; do not expose the session/projector to a client.
        /// </summary>
        public static PokerPlayerView Create(PokerHandSession session, SeatId authorizedViewer)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (!authorizedViewer.IsValid) throw new ArgumentException("A valid authorized seat is required.", nameof(authorizedViewer));
            PokerHandState state = session.State;
            if (state == null) throw new InvalidOperationException("No hand has started; lobby views are a separate contract.");
            long version = session.Version;
            // GetHand rejects nonparticipants. Only this seat's cards are ever read by the projector.
            SeatHand ownHand = state.GetHand(authorizedViewer);
            var ownCards = new Card[ownHand.Count];
            for (int i = 0; i < ownCards.Length; i++) ownCards[i] = ownHand[i];
            BettingRound betting = state.CurrentBetting;
            var seats = new PublicSeatView[state.SeatCount];
            for (int i = 0; i < seats.Length; i++)
            {
                SeatId seat = state.GetSeatAt(i);
                SeatChips chips = state.Ledger.GetChips(seat);
                bool folded = state.IsFolded(seat);
                // First-street folds do not belong to the second betting round.
                long? street = betting == null || (state.Phase == HandPhase.SecondBetting && state.FirstBetting.IsFolded(seat))
                    ? (long?)null : betting.GetStreetContribution(seat);
                seats[i] = new PublicSeatView(seat, chips.Stack, chips.Committed, street,
                    folded, !state.IsComplete && !folded && chips.Stack == 0, state.Settlement?.GetAwardedTo(seat));
            }
            bool ownTurn = state.CurrentSeat == authorizedViewer;
            bool canExchange = ownTurn && state.Phase == HandPhase.Exchange;
            return new PokerPlayerView(session.HandId, version, authorizedViewer, state.Phase, state.CurrentSeat,
                ownCards, seats, state.Ledger.TotalCommitted, betting?.CurrentBet,
                ownTurn && betting != null ? new PlayerBettingOptions(betting.GetLegalActions()) : null,
                canExchange, canExchange ? ExchangeRound.MaxExchangeCount : 0, state.Settlement?.TotalAwarded);
        }
    }
}
