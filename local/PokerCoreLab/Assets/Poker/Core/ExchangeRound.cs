using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>
    /// Authority-only immutable state for the approved personal prototype profile:
    /// one draw per eligible seat, zero through five cards, no burns or discard reuse.
    /// Not authentication, a betting phase, or a network command handler.
    /// </summary>
    /// <remarks>
    /// Apply returns a new state; the authority must commit only its current branch.
    /// Old snapshots may branch and Begin may be called twice for one InitialDeal.
    /// Once-per-hand entry, stale requests and command deduplication belong to the hand controller.
    /// Never serialize this object wholesale to a client or a player-controlled AI.
    /// </remarks>
    public sealed class ExchangeRound
    {
        public const int MaxExchangeCount = SeatHand.CardCount;
        private readonly SeatId[] seats;
        private readonly SeatId[] exchangeOrder;
        private readonly SeatHand[] hands;
        private readonly Deck deck;
        private readonly Card[] discarded;

        // Arrays supplied here are private candidate storage or already immutable snapshot storage.
        private ExchangeRound(SeatId[] seats, SeatId[] exchangeOrder, SeatHand[] hands,
            Deck deck, Card[] discarded, int completed)
        {
            this.seats = seats;
            this.exchangeOrder = exchangeOrder;
            this.hands = hands;
            this.deck = deck;
            this.discarded = discarded;
            CompletedSeatCount = completed;
            VerifyInventory();
        }

        public int SeatCount => seats.Length;
        public int ExchangeSeatCount => exchangeOrder.Length;
        public int CompletedSeatCount { get; }
        public bool IsComplete => CompletedSeatCount == exchangeOrder.Length;
        public SeatId? CurrentSeat => IsComplete ? (SeatId?)null : exchangeOrder[CompletedSeatCount];
        public int RemainingCardCount => deck.RemainingCount;
        public int DiscardedCardCount => discarded.Length;

        /// <summary>Copies the initial state and freezes the caller-supplied eligible seat order.</summary>
        /// <remarks>
        /// The caller resolves phase, button, folded seats and all-in eligibility; all-in alone
        /// must not exclude a seat. An empty order is a completed primitive, not a legal-game verdict.
        /// The full initial table must fit 5*N + 5*N within 52 (at most five recipients).
        /// This conservative profile gate applies even if some seats are excluded now; the future
        /// StartHand must enforce it before dealing. It does not approve actual supported player counts.
        /// Do not modify request collections concurrently while they are being copied.
        /// </remarks>
        public static ExchangeRound Begin(InitialDeal deal, IReadOnlyList<SeatId> eligibleSeatsInOrder)
        {
            if (deal == null) throw new ArgumentNullException(nameof(deal));
            if (eligibleSeatsInOrder == null) throw new ArgumentNullException(nameof(eligibleSeatsInOrder));
            if (deal.SeatCount > Card.DeckSize / (SeatHand.CardCount + MaxExchangeCount))
                throw new InvalidOperationException("Initial table exceeds this no-reuse exchange profile's capacity.");
            int count = eligibleSeatsInOrder.Count;
            if (count < 0 || count > deal.SeatCount)
                throw new ArgumentOutOfRangeException(nameof(eligibleSeatsInOrder));
            var order = new SeatId[count];
            for (int i = 0; i < count; i++) order[i] = eligibleSeatsInOrder[i];

            var seats = new SeatId[deal.SeatCount];
            var hands = new SeatHand[deal.SeatCount];
            for (int i = 0; i < seats.Length; i++)
            {
                seats[i] = deal.GetSeatAt(i);
                hands[i] = deal.GetHand(seats[i]); // SeatHand is immutable.
            }
            var seen = new HashSet<SeatId>();
            foreach (SeatId seat in order)
                if (!seat.IsValid || !seen.Add(seat) || Array.IndexOf(seats, seat) < 0)
                    throw new ArgumentException("Exchange seats must be unique members of the initial deal.", nameof(eligibleSeatsInOrder));

            return new ExchangeRound(seats, order, hands, deal.CopyRemainingDeck(), Array.Empty<Card>(), 0);
        }

        /// <summary>
        /// Replaces selected owned cards and returns the next snapshot. Empty selection confirms
        /// stand-pat and consumes this seat's turn. The receiver is unchanged on success or failure.
        /// </summary>
        /// <remarks>
        /// Selection is by card value, not a UI index. Replacements fill original hand slots in
        /// ascending index order, independent of selection order. No RNG is called during exchange.
        /// </remarks>
        public ExchangeRound Apply(SeatId seat, IReadOnlyList<Card> discardCards)
        {
            if (discardCards == null) throw new ArgumentNullException(nameof(discardCards));
            int ownerIndex = FindSeatIndex(seat);
            if (IsComplete) throw new InvalidOperationException("The exchange round is complete.");
            if (exchangeOrder[CompletedSeatCount] != seat)
                throw new InvalidOperationException("Only the current exchange seat may act.");
            int count = discardCards.Count;
            if (count < 0 || count > MaxExchangeCount)
                throw new ArgumentOutOfRangeException(nameof(discardCards), "Select zero through five cards.");
            var selection = new Card[count];
            for (int i = 0; i < count; i++) selection[i] = discardCards[i];

            SeatHand oldHand = hands[ownerIndex];
            var replace = new bool[SeatHand.CardCount];
            foreach (Card card in selection)
            {
                int slot = -1;
                if (card.IsValid)
                    for (int i = 0; i < oldHand.Count; i++) if (oldHand[i] == card) { slot = i; break; }
                if (slot < 0 || replace[slot])
                    throw new ArgumentException("Select distinct valid cards owned by this seat.", nameof(discardCards));
                replace[slot] = true;
            }
            if (count > deck.RemainingCount)
                throw new InvalidOperationException("Not enough cards remain for this exchange.");

            // Only private candidate state is consumed. No published object's cursor is advanced.
            Deck nextDeck = deck.Copy();
            Card[] replacements = nextDeck.Draw(count);
            var nextHands = (SeatHand[])hands.Clone();
            var nextDiscards = new Card[discarded.Length + count];
            Array.Copy(discarded, nextDiscards, discarded.Length);
            var nextCards = new Card[SeatHand.CardCount];
            int replacementIndex = 0;
            for (int slot = 0; slot < oldHand.Count; slot++)
            {
                nextCards[slot] = oldHand[slot];
                if (!replace[slot]) continue;
                nextDiscards[discarded.Length + replacementIndex] = oldHand[slot];
                nextCards[slot] = replacements[replacementIndex++];
            }
            nextHands[ownerIndex] = new SeatHand(seat, nextCards);
            return new ExchangeRound(seats, exchangeOrder, nextHands, nextDeck, nextDiscards, CompletedSeatCount + 1);
        }

        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= seats.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return seats[index];
        }

        /// <summary>Trusted authority lookup, not authorization or a client-safe projection.</summary>
        public SeatHand GetHand(SeatId seat) => hands[FindSeatIndex(seat)];

        private int FindSeatIndex(SeatId seat)
        {
            if (!seat.IsValid) throw new ArgumentException("A valid seat ID is required.", nameof(seat));
            int index = Array.IndexOf(seats, seat);
            if (index < 0) throw new KeyNotFoundException("The seat is not part of the initial deal.");
            return index;
        }

        private void VerifyInventory()
        {
            if (hands.Length * SeatHand.CardCount + discarded.Length + deck.RemainingCount != Card.DeckSize)
                throw new InvalidOperationException("Card inventory must total 52.");
            var seen = new bool[Card.DeckSize];
            foreach (SeatHand hand in hands) foreach (Card card in hand) AddUnique(card, seen);
            foreach (Card card in discarded) AddUnique(card, seen);
            // Validation must consume only an inspection copy, never the candidate deck itself.
            foreach (Card card in deck.Copy().Draw(deck.RemainingCount)) AddUnique(card, seen);
        }

        private static void AddUnique(Card card, bool[] seen)
        {
            if (!card.IsValid || seen[card.Id]) throw new InvalidOperationException("Invalid or repeated card in inventory.");
            seen[card.Id] = true;
        }
    }
}
