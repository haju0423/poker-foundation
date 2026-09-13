using System;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Transport
{
    /// <summary>Explicit v1 tokens. Enum ordinals and C# names are not the protocol.</summary>
    public static class PokerWireTokens
    {
        private static readonly (HandPhase, string)[] Phases =
        {
            (HandPhase.FirstBetting, "first_betting"), (HandPhase.Exchange, "exchange"),
            (HandPhase.SecondBetting, "second_betting"),
            (HandPhase.AwaitingSettlementRule, "awaiting_settlement_rule"), (HandPhase.Complete, "complete")
        };
        private static readonly (HandCommandKind, string)[] Kinds =
        { (HandCommandKind.Bet, "bet"), (HandCommandKind.Exchange, "exchange") };
        private static readonly (BettingActionKind, string)[] Actions =
        {
            (BettingActionKind.Fold, "fold"), (BettingActionKind.Check, "check"),
            (BettingActionKind.Call, "call"), (BettingActionKind.BetTo, "bet_to"),
            (BettingActionKind.RaiseTo, "raise_to")
        };
        private static readonly (HandCompletionReason, string)[] Reasons =
        { (HandCompletionReason.Uncontested, "uncontested"), (HandCompletionReason.Showdown, "showdown") };
        private static readonly (HandError, string)[] Errors =
        {
            (HandError.None, "none"), (HandError.UnauthorizedSeat, "unauthorized_seat"),
            (HandError.WrongHand, "wrong_hand"), (HandError.CommandConflict, "command_conflict"),
            (HandError.VersionMismatch, "version_mismatch"), (HandError.NotStarted, "not_started"),
            (HandError.AlreadyStarted, "already_started"), (HandError.Complete, "complete"),
            (HandError.SettlementRuleRequired, "settlement_rule_required"), (HandError.WrongPhase, "wrong_phase"),
            (HandError.WrongTurn, "wrong_turn"), (HandError.IllegalBet, "illegal_bet"),
            (HandError.CardNotOwned, "card_not_owned"), (HandError.Busy, "busy")
        };

        public static string Phase(HandPhase value) => Encode(Phases, value, "phase");
        public static HandPhase Phase(string value) => Decode(Phases, value, "phase");
        public static string Kind(HandCommandKind value) => Encode(Kinds, value, "kind");
        public static HandCommandKind Kind(string value) => Decode(Kinds, value, "kind");
        public static string Action(BettingActionKind value) => Encode(Actions, value, "action");
        public static BettingActionKind Action(string value) => Decode(Actions, value, "action");
        public static string Reason(HandCompletionReason value) => Encode(Reasons, value, "reason");
        public static HandCompletionReason Reason(string value) => Decode(Reasons, value, "reason");
        public static string Error(HandError value) => Encode(Errors, value, "error");
        public static HandError Error(string value) => Decode(Errors, value, "error");

        private static string Encode<T>((T, string)[] entries, T value, string field) where T : struct
        {
            foreach (var entry in entries) if (entry.Item1.Equals(value)) return entry.Item2;
            throw new PokerWireException(field);
        }
        private static T Decode<T>((T, string)[] entries, string value, string field) where T : struct
        {
            foreach (var entry in entries) if (entry.Item2 == value) return entry.Item1;
            throw new PokerWireException(field);
        }
    }
}
