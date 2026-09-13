using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Transport
{
    /// <summary>Explicit v1 copies and syntax checks. Decoding never approves a player command.</summary>
    public static partial class PokerWireMapper
    {
        public const int ProtocolVersion = 1;
        public const int MaximumSeatCount = Card.DeckSize / (SeatHand.CardCount + ExchangeRound.MaxExchangeCount);

        public static PokerWireCommand ToWire(HandCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var value = new PokerWireCommand
            {
                protocolVersion = ProtocolVersion, message = "command",
                handId = command.HandId.ToString("D"), commandId = command.CommandId.ToString("D"),
                seat = command.Seat.Value, expectedVersion = Number(command.ExpectedVersion),
                kind = PokerWireTokens.Kind(command.Kind),
                action = command.Action == null ? "" : PokerWireTokens.Action(command.Action.Kind),
                targetTotal = command.Action != null && IsSized(command.Action.Kind) ? Number(command.Action.Target) : "",
                cards = command.SelectedCards.Select(card => card.Id).ToArray()
            };
            return value;
        }

        public static HandCommand ToCommand(PokerWireCommand value)
        {
            Need(value != null, "command");
            Header(value.protocolVersion, value.message, "command");
            var hand = Id(value.handId); var command = Id(value.commandId);
            Seat(value.seat); var seat = new SeatId(value.seat);
            long version = Number(value.expectedVersion);
            var kind = PokerWireTokens.Kind(value.kind);
            Need(value.action != null && value.targetTotal != null, "command.action");
            Cards(value.cards, 0, ExchangeRound.MaxExchangeCount);
            if (kind == HandCommandKind.Exchange)
            {
                Need(value.action == "" && value.targetTotal == "", "exchange.unused_fields");
                return HandCommand.Exchange(hand, command, seat, version, value.cards.Select(Card.FromId).ToArray());
            }
            Need(value.cards.Length == 0, "bet.cards");
            var action = PokerWireTokens.Action(value.action);
            long target = IsSized(action) ? Number(value.targetTotal, positive: true) : 0;
            if (!IsSized(action)) Need(value.targetTotal == "", "bet.targetTotal");
            BettingAction intent;
            switch (action)
            {
                case BettingActionKind.Fold: intent = BettingAction.Fold(); break;
                case BettingActionKind.Check: intent = BettingAction.Check(); break;
                case BettingActionKind.Call: intent = BettingAction.Call(); break;
                case BettingActionKind.BetTo: intent = BettingAction.BetTo(target); break;
                case BettingActionKind.RaiseTo: intent = BettingAction.RaiseTo(target); break;
                default: throw new PokerWireException("action");
            }
            return HandCommand.Bet(hand, command, seat, version, intent);
        }

        public static PokerWireReceipt ToWire(HandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var value = new PokerWireReceipt
            {
                protocolVersion = ProtocolVersion, message = "receipt", handId = receipt.HandId.ToString("D"),
                commandId = receipt.CommandId.ToString("D"), seat = receipt.Seat?.Value ?? 0,
                appliedVersion = Optional(receipt.AppliedVersion), error = PokerWireTokens.Error(receipt.Error),
                transition = receipt.Transition.HasValue ? new[] { ToWire(receipt.Transition.Value) } : Array.Empty<PokerWireTransition>()
            };
            Validate(value);
            return value;
        }

        public static void Validate(PokerWireReceipt value)
        {
            Need(value != null, "receipt"); Header(value.protocolVersion, value.message, "receipt");
            Id(value.handId); Id(value.commandId); Seat(value.seat, optional: true);
            long? version = Optional(value.appliedVersion); var error = PokerWireTokens.Error(value.error);
            One(value.transition, "receipt.transition");
            if (error != HandError.None)
            {
                Need(!version.HasValue && value.transition.Length == 0, "receipt.rejection");
                return;
            }
            Need(version.HasValue && version.Value > 0, "receipt.appliedVersion");
            if (value.seat == 0)
            {
                // Authority-only Start acknowledgement, never a player command.
                Need(version == 1 && value.transition.Length == 0, "receipt.start");
                return;
            }
            Need(value.transition.Length == 1, "receipt.transition");
            var transition = value.transition[0]; Validate(transition);
            Need(transition.handId == value.handId && transition.seat == value.seat
                && transition.appliedVersion == value.appliedVersion, "receipt.transition_identity");
        }

        private static PokerWireTransition ToWire(HandTransition source) => Transition(source.HandId,
            source.AppliedVersion, source.Seat, source.BeforePhase, source.AfterPhase, source.Kind,
            source.BettingAction, source.TargetTotal, source.ChipsPaid, source.ExchangeCount,
            source.RefundedSeat, source.RefundedAmount);
        private static PokerWireTransition ToWire(PublicHandTransition source) => Transition(source.HandId,
            source.AppliedVersion, source.Seat, source.BeforePhase, source.AfterPhase, source.Kind,
            source.BettingAction, source.TargetTotal, source.ChipsPaid, source.ExchangeCount,
            source.RefundedSeat, source.RefundedAmount);
        private static PokerWireTransition Transition(Guid hand, long version, SeatId seat,
            HandPhase before, HandPhase after, HandCommandKind kind, BettingActionKind? action,
            long? target, long chips, int count, SeatId? refundSeat, long refund) => new PokerWireTransition
        {
            handId = hand.ToString("D"), appliedVersion = Number(version), seat = seat.Value,
            beforePhase = PokerWireTokens.Phase(before), afterPhase = PokerWireTokens.Phase(after),
            kind = PokerWireTokens.Kind(kind), action = action.HasValue ? PokerWireTokens.Action(action.Value) : "",
            targetTotal = Optional(target), chipsPaid = Number(chips), exchangeCount = count,
            refundedSeat = refundSeat?.Value ?? 0, refundedAmount = Number(refund)
        };

        private static void Validate(PokerWireTransition value)
        {
            Need(value != null, "transition"); Id(value.handId); Number(value.appliedVersion, true); Seat(value.seat);
            var before = PokerWireTokens.Phase(value.beforePhase); PokerWireTokens.Phase(value.afterPhase);
            var kind = PokerWireTokens.Kind(value.kind); long? target = Optional(value.targetTotal);
            long chips = Number(value.chipsPaid); long refund = Number(value.refundedAmount);
            Seat(value.refundedSeat, true); Need((value.refundedSeat == 0) == (refund == 0), "transition.refund");
            if (kind == HandCommandKind.Exchange)
            {
                Need(before == HandPhase.Exchange && value.action == "" && !target.HasValue && chips == 0 && refund == 0
                    && value.exchangeCount >= 0 && value.exchangeCount <= ExchangeRound.MaxExchangeCount,
                    "transition.exchange");
            }
            else
            {
                Need(IsBetting(before) && value.exchangeCount == 0, "transition.bet");
                var action = PokerWireTokens.Action(value.action);
                Need(IsSized(action) ? target.HasValue && target.Value > 0 : !target.HasValue, "transition.target");
                if (action == BettingActionKind.Check || action == BettingActionKind.Fold)
                    Need(chips == 0, "transition.passive_chips");
                else Need(chips > 0, "transition.paid_chips");
            }
        }

        private static bool IsSized(BettingActionKind action) => action == BettingActionKind.BetTo || action == BettingActionKind.RaiseTo;
        private static bool IsBetting(HandPhase phase) => phase == HandPhase.FirstBetting || phase == HandPhase.SecondBetting;
        private static void Header(int version, string message, string expected)
        { Need(version == ProtocolVersion, "protocolVersion"); Need(message == expected, "message"); }
        private static void Need(bool condition, string field) { if (!condition) throw new PokerWireException(field); }
        private static Guid Id(string value)
        {
            Need(value != null && Guid.TryParseExact(value, "D", out var parsed)
                && parsed != Guid.Empty && parsed.ToString("D") == value, "id");
            return Guid.ParseExact(value, "D");
        }
        private static void Seat(int value, bool optional = false) => Need(value > 0 || (optional && value == 0), "seat");
        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Optional(long? value) => value.HasValue ? Number(value.Value) : "";
        private static long? Optional(string value)
        { Need(value != null, "optional_number"); return value == "" ? (long?)null : Number(value); }
        private static long Number(string value, bool positive = false)
        {
            Need(value != null && value.Length > 0 && value.Length <= 19, "number");
            Need(long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                && parsed >= (positive ? 1 : 0) && Number(parsed) == value, "number");
            return parsed;
        }
        private static void ArraySize<T>(T[] value, int min, int max, string field)
        { Need(value != null && value.Length >= min && value.Length <= max, field); }
        private static void One<T>(T[] value, string field) where T : class
        { ArraySize(value, 0, 1, field); Need(value.Length == 0 || value[0] != null, field); }
        private static void Cards(int[] cards, int min, int max)
        {
            ArraySize(cards, min, max, "cards"); var found = new HashSet<int>();
            foreach (int card in cards) Need(card >= 0 && card < Card.DeckSize && found.Add(card), "cards");
        }
    }
}
