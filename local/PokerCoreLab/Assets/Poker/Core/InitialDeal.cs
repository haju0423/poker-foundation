using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>
    /// A complete initial deal, owned and queried by the trusted game authority.
    /// Owns its fresh deck and immutable hands; it is not a game session or StartHand command.
    /// Never send this authority object wholesale to a client or a player-controlled AI.
    /// </summary>
    public sealed class InitialDeal
    {
        private readonly SeatId[] seats;
        private readonly SeatHand[] hands;
        private readonly Deck remainingDeck;

        private InitialDeal(SeatId[] ownedSeats, SeatHand[] ownedHands, Deck ownedDeck)
        {
            seats = ownedSeats;
            hands = ownedHands;
            remainingDeck = ownedDeck;
        }

        public int SeatCount => seats.Length;
        public int RemainingCardCount => remainingDeck.RemainingCount;

        // The new exchange state gets its own cursor/storage; this deal remains unchanged.
        internal Deck CopyRemainingDeck() => remainingDeck.Copy();

        /// <summary>
        /// Freezes and validates the supplied deal order, then shuffles a new deck and
        /// deals one card per seat for five rounds. Only a complete result is returned.
        /// </summary>
        /// <remarks>
        /// The caller resolves eligibility and the first recipient; IDs do not imply order.
        /// The 1..10 capacity is only 52 / 5, not supported player counts or an exchange reserve.
        /// No existing deal is consumed. RNG state is not rolled back on failure.
        /// Input must not be modified concurrently while it is being copied.
        /// </remarks>
        /// <exception cref="ArgumentNullException">Random source or seat order is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Recipient count is outside deck capacity.</exception>
        /// <exception cref="ArgumentException">An ID is invalid or repeated.</exception>
        /// <exception cref="InvalidOperationException">Random output is out of range.</exception>
        public static InitialDeal Create(IRandomSource random, IReadOnlyList<SeatId> seatsInDealOrder)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (seatsInDealOrder == null) throw new ArgumentNullException(nameof(seatsInDealOrder));
            int count = seatsInDealOrder.Count;
            if (count < 1 || count > Card.DeckSize / SeatHand.CardCount)
                throw new ArgumentOutOfRangeException(nameof(seatsInDealOrder), count,
                    "Recipient count exceeds the capacity of an initial five-card deal.");

            // Copy and validate before calling the external RNG, which could mutate its caller's list.
            var order = new SeatId[count];
            var seen = new HashSet<SeatId>();
            for (int index = 0; index < count; index++)
            {
                SeatId seat = seatsInDealOrder[index];
                if (!seat.IsValid || !seen.Add(seat))
                    throw new ArgumentException("Seat IDs must be valid and unique.", nameof(seatsInDealOrder));
                order[index] = seat;
            }

            Deck deck = Deck.CreateShuffled(random);
            Card[] dealt = deck.Draw(count * SeatHand.CardCount);
            var hands = new SeatHand[count];
            for (int seatIndex = 0; seatIndex < count; seatIndex++)
            {
                var cards = new Card[SeatHand.CardCount];
                for (int round = 0; round < cards.Length; round++)
                    cards[round] = dealt[round * count + seatIndex];
                hands[seatIndex] = new SeatHand(order[seatIndex], cards);
            }
            return new InitialDeal(order, hands, deck);
        }

        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= seats.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return seats[index];
        }

        /// <summary>Trusted authority lookup, NOT an access-control check or client projection.</summary>
        /// <exception cref="ArgumentException">The seat ID is invalid.</exception>
        /// <exception cref="KeyNotFoundException">The seat did not receive a hand in this deal.</exception>
        public SeatHand GetHand(SeatId seat)
        {
            if (!seat.IsValid) throw new ArgumentException("A valid seat ID is required.", nameof(seat));
            for (int index = 0; index < seats.Length; index++)
                if (seats[index] == seat) return hands[index];
            throw new KeyNotFoundException("The seat is not part of this initial deal.");
        }
    }
}
