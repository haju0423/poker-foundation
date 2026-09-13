using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Immutable exchange request. IDs are identities, not authorization credentials.</summary>
    public sealed class ExchangeCommand
    {
        public ExchangeCommand(Guid handId, Guid commandId, SeatId seat, long expectedVersion,
            IReadOnlyList<Card> selectedCards)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (selectedCards == null) throw new ArgumentNullException(nameof(selectedCards));
            int count = selectedCards.Count;
            if (count < 0 || count > ExchangeRound.MaxExchangeCount)
                throw new ArgumentOutOfRangeException(nameof(selectedCards));
            var copy = new Card[count];
            for (int i = 0; i < count; i++) copy[i] = selectedCards[i];
            var seen = new HashSet<Card>();
            foreach (Card card in copy)
                if (!card.IsValid || !seen.Add(card))
                    throw new ArgumentException("Selection must contain distinct valid cards.", nameof(selectedCards));
            // The request means a set of cards, not the order in which the UI selected them.
            Array.Sort(copy, (left, right) => left.Id.CompareTo(right.Id));
            HandId = handId;
            CommandId = commandId;
            Seat = seat;
            ExpectedVersion = expectedVersion;
            SelectedCards = Array.AsReadOnly(copy);
        }

        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        public long ExpectedVersion { get; }
        public IReadOnlyList<Card> SelectedCards { get; }

        internal bool HasSamePayload(ExchangeCommand other)
        {
            if (HandId != other.HandId || Seat != other.Seat || ExpectedVersion != other.ExpectedVersion
                || SelectedCards.Count != other.SelectedCards.Count) return false;
            for (int i = 0; i < SelectedCards.Count; i++)
                if (SelectedCards[i] != other.SelectedCards[i]) return false;
            return true;
        }
    }
}
