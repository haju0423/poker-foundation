using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Authority-supplied live hand, copied and evaluated here. Not a client claim or public view.</summary>
    public sealed class ShowdownHand
    {
        private readonly SeatHand hand;

        /// <summary>Preferred connection from InitialDeal/ExchangeRound; preserves the immutable hand's owner.</summary>
        public ShowdownHand(SeatHand hand)
        {
            if (hand == null) throw new ArgumentNullException(nameof(hand));
            this.hand = hand;
            Value = HandEvaluator.Evaluate(hand);
        }

        /// <summary>Trusted snapshot import/test input. The caller must prove ownership and that these are the final cards.</summary>
        public ShowdownHand(SeatId owner, IReadOnlyList<Card> cards)
        {
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (cards.Count != SeatHand.CardCount) throw new ArgumentException("Exactly five cards are required.", nameof(cards));
            var copy = new Card[SeatHand.CardCount];
            for (int i = 0; i < copy.Length; i++) copy[i] = cards[i];
            hand = new SeatHand(owner, copy);
            Value = HandEvaluator.Evaluate(hand);
        }

        public SeatId Owner => hand.Owner;
        public HandValue Value { get; }
        internal Card GetCard(int index) => hand[index];
    }
}
