using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Frozen authority inputs for one B1 hand. Does not resolve a button or adopt house rules.</summary>
    public sealed class HandSetup
    {
        public HandSetup(ChipLedger startingLedger, IReadOnlyList<SeatId> dealOrder,
            IReadOnlyList<SeatId> openingOrder, IReadOnlyList<SeatId> exchangeOrder, IReadOnlyList<SeatId> closingOrder,
            long smallBlind, long bigBlind, IReadOnlyList<SeatId> oddChipPriority = null)
        {
            if (startingLedger == null) throw new ArgumentNullException(nameof(startingLedger));
            int count = startingLedger.SeatCount;
            if (count < 2 || count > Card.DeckSize / (SeatHand.CardCount + ExchangeRound.MaxExchangeCount))
                throw new ArgumentException("Starting seats exceed the supported draw profile capacity.", nameof(startingLedger));
            if (startingLedger.TotalCommitted != 0)
                throw new ArgumentException("Starting contributions must be zero.", nameof(startingLedger));
            for (int i = 0; i < count; i++)
                if (startingLedger.GetChips(startingLedger.GetSeatAt(i)).Stack == 0)
                    throw new ArgumentException("Every starting seat must be funded.", nameof(startingLedger));
            if (bigBlind <= 0) throw new ArgumentOutOfRangeException(nameof(bigBlind));
            if (smallBlind <= 0 || smallBlind > bigBlind) throw new ArgumentOutOfRangeException(nameof(smallBlind));
            StartingLedger = startingLedger;
            DealOrder = CopyPermutation(dealOrder, nameof(dealOrder));
            OpeningOrder = CopyPermutation(openingOrder, nameof(openingOrder));
            ExchangeOrder = CopyPermutation(exchangeOrder, nameof(exchangeOrder));
            ClosingOrder = CopyPermutation(closingOrder, nameof(closingOrder));
            OddChipPriority = oddChipPriority == null ? null : CopyPermutation(oddChipPriority, nameof(oddChipPriority));
            SmallBlind = smallBlind;
            BigBlind = bigBlind;
        }

        public ChipLedger StartingLedger { get; }
        public IReadOnlyList<SeatId> DealOrder { get; }
        public IReadOnlyList<SeatId> OpeningOrder { get; }
        public IReadOnlyList<SeatId> ExchangeOrder { get; }
        public IReadOnlyList<SeatId> ClosingOrder { get; }
        public IReadOnlyList<SeatId> OddChipPriority { get; }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public SeatId SmallBlindSeat => OpeningOrder[OpeningOrder.Count - 2];
        public SeatId BigBlindSeat => OpeningOrder[OpeningOrder.Count - 1];

        private IReadOnlyList<SeatId> CopyPermutation(IReadOnlyList<SeatId> source, string name)
        {
            if (source == null) throw new ArgumentNullException(name);
            if (source.Count != StartingLedger.SeatCount) throw new ArgumentException("An order must include all starting seats.", name);
            var copy = new SeatId[source.Count];
            var seen = new HashSet<SeatId>();
            for (int i = 0; i < copy.Length; i++)
            {
                SeatId seat = source[i];
                bool registered = false;
                for (int j = 0; j < StartingLedger.SeatCount; j++)
                    if (seat == StartingLedger.GetSeatAt(j)) { registered = true; break; }
                if (!registered || !seen.Add(seat)) throw new ArgumentException("An order must be a permutation of starting seats.", name);
                copy[i] = seat;
            }
            return Array.AsReadOnly(copy);
        }
    }
}
