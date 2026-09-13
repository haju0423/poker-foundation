using System;
using System.Collections;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>
    /// An immutable snapshot of one seat's five distinct, valid cards.
    /// Read access is not authorization: the caller must route it to the allowed seat.
    /// </summary>
    public sealed class SeatHand : IReadOnlyList<Card>
    {
        public const int CardCount = 5;
        private readonly Card[] cards;

        // Internal construction still copies its input; no array or mutable list escapes.
        internal SeatHand(SeatId owner, Card[] cards)
        {
            if (!owner.IsValid) throw new ArgumentException("A valid owner is required.", nameof(owner));
            if (cards == null) throw new ArgumentNullException(nameof(cards));
            if (cards.Length != CardCount)
                throw new ArgumentException("A hand must contain exactly five cards.", nameof(cards));
            this.cards = (Card[])cards.Clone();
            for (int index = 0; index < CardCount; index++)
            {
                if (!this.cards[index].IsValid)
                    throw new ArgumentException("A hand cannot contain an invalid card.", nameof(cards));
                for (int earlier = 0; earlier < index; earlier++)
                    if (this.cards[index] == this.cards[earlier])
                        throw new ArgumentException("A hand cannot contain duplicate cards.", nameof(cards));
            }
            Owner = owner;
        }

        public SeatId Owner { get; }
        public int Count => CardCount;
        public Card this[int index]
        {
            get
            {
                if (index < 0 || index >= CardCount) throw new ArgumentOutOfRangeException(nameof(index));
                return cards[index];
            }
        }
        public IEnumerator<Card> GetEnumerator()
        {
            for (int index = 0; index < CardCount; index++) yield return cards[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
