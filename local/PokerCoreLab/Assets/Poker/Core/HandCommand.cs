using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HandCommandKind { Invalid, Bet, Exchange }

    /// <summary>Immutable player intent, not proof of identity. One ID namespace spans both kinds.</summary>
    public sealed class HandCommand
    {
        private HandCommand(Guid handId, Guid commandId, SeatId seat, long expectedVersion,
            BettingAction action, ExchangeCommand exchange)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            HandId = handId; CommandId = commandId; Seat = seat; ExpectedVersion = expectedVersion;
            Action = action;
            Kind = exchange == null ? HandCommandKind.Bet : HandCommandKind.Exchange;
            SelectedCards = exchange == null ? Array.AsReadOnly(Array.Empty<Card>()) : exchange.SelectedCards;
        }

        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        public long ExpectedVersion { get; }
        public HandCommandKind Kind { get; }
        public BettingAction Action { get; }
        public IReadOnlyList<Card> SelectedCards { get; }

        public static HandCommand Bet(Guid handId, Guid commandId, SeatId seat, long expectedVersion, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return new HandCommand(handId, commandId, seat, expectedVersion, action, null);
        }

        public static HandCommand Exchange(Guid handId, Guid commandId, SeatId seat, long expectedVersion,
            IReadOnlyList<Card> selectedCards)
        {
            var exchange = new ExchangeCommand(handId, commandId, seat, expectedVersion, selectedCards);
            return new HandCommand(handId, commandId, seat, expectedVersion, null, exchange);
        }

        internal bool HasSamePayload(HandCommand other)
        {
            if (HandId != other.HandId || Seat != other.Seat || ExpectedVersion != other.ExpectedVersion || Kind != other.Kind)
                return false;
            if (Kind == HandCommandKind.Bet) return Action.Kind == other.Action.Kind && Action.Target == other.Action.Target;
            if (SelectedCards.Count != other.SelectedCards.Count) return false;
            for (int i = 0; i < SelectedCards.Count; i++) if (SelectedCards[i] != other.SelectedCards[i]) return false;
            return true;
        }
    }
}
