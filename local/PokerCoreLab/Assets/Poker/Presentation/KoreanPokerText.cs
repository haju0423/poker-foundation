using System;
using System.Globalization;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>
    /// Korean-first text only. Inputs are verified amounts/reasons from the authority, not a second rules engine.
    /// No global culture changes, exception-message parsing, player names, card ownership or UI side effects.
    /// </summary>
    public static class KoreanPokerText
    {
        public static string ActionName(BettingActionKind kind)
        {
            switch (kind)
            {
                case BettingActionKind.Fold: return "폴드";
                case BettingActionKind.Check: return "체크";
                case BettingActionKind.Call: return "콜";
                case BettingActionKind.BetTo: return "베팅";
                case BettingActionKind.RaiseTo: return "레이즈";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static string FoldHelp => "이번 판의 승부를 포기합니다. 이미 팟에 확정된 칩은 돌려받지 않습니다.";
        public static string CheckHelp => "추가로 칩을 내지 않고 차례를 넘깁니다.";
        public static string AllInHelp => "남은 칩을 모두 냅니다. 낸 칩에 해당하는 팟의 승부에는 계속 참여합니다.";

        public static string Chips(long amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            return amount.ToString("N0", CultureInfo.InvariantCulture) + "칩";
        }
        public static string CallLabel(long additionalAmount, bool allIn = false)
        {
            RequirePositive(additionalAmount);
            return "콜 · " + Chips(additionalAmount) + " 추가" + (allIn ? " (올인)" : "");
        }
        public static string BetLabel(long amount, bool allIn = false)
        {
            RequirePositive(amount);
            return "베팅 · " + Chips(amount) + (allIn ? " (올인)" : "");
        }
        public static string RaiseLabel(long totalTarget, bool allIn = false)
        {
            RequirePositive(totalTarget);
            return "레이즈 · 총 " + Chips(totalTarget) + (allIn ? " (올인)" : "");
        }
        public static string AdditionalChips(long amount) => Chips(amount) + " 추가";
        public static string PotName(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            return index == 0 ? "메인 팟" : "사이드 팟 " + index.ToString(CultureInfo.InvariantCulture);
        }
        public static string PotLabel(int index, long amount) => PotName(index) + " · " + Chips(amount);
        public static string PayoutLabel(long amount, bool hasOddChip = false)
        {
            if (hasOddChip) RequirePositive(amount);
            return "팟 지급액 · " + Chips(amount) + (hasOddChip ? " (나머지 1칩 포함)" : "");
        }
        public static string RefundMessage(long amount)
        {
            RequirePositive(amount);
            return "다른 사람이 따라 내지 않은 " + Chips(amount) + "을 돌려받습니다.";
        }
        public static string SettlementMessage(SettlementFailure reason)
        {
            switch (reason)
            {
                case SettlementFailure.MissingOddChipOrder: return "남는 칩의 지급 기준이 아직 정해지지 않았습니다.";
                case SettlementFailure.UnmatchedContribution: return "돌려줄 칩을 먼저 처리해야 정산할 수 있습니다.";
                case SettlementFailure.NoEligibleWinner: return "팟을 받을 수 있는 참가자 정보가 맞지 않습니다.";
                case SettlementFailure.NothingToAward: return "정산할 칩이 없습니다.";
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
        public static string HandName(HandValue value)
        {
            if (!value.IsValid) throw new ArgumentException("An evaluated hand is required.", nameof(value));
            if (value.Category == HandCategory.StraightFlush && value.GetTieBreaker(0) == (int)Rank.Ace)
                return "로열 스트레이트 플러시";
            switch (value.Category)
            {
                case HandCategory.HighCard: return "하이 카드";
                case HandCategory.OnePair: return "원페어";
                case HandCategory.TwoPair: return "투페어";
                case HandCategory.ThreeOfAKind: return "트리플";
                case HandCategory.Straight: return "스트레이트";
                case HandCategory.Flush: return "플러시";
                case HandCategory.FullHouse: return "풀하우스";
                case HandCategory.FourOfAKind: return "포카드";
                case HandCategory.StraightFlush: return "스트레이트 플러시";
                default: throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
        public static string CardName(Card card)
        {
            if (!card.IsValid) throw new ArgumentException("A valid card is required.", nameof(card));
            string suit;
            switch (card.Suit)
            {
                case Suit.Clubs: suit = "클로버"; break;
                case Suit.Diamonds: suit = "다이아"; break;
                case Suit.Hearts: suit = "하트"; break;
                case Suit.Spades: suit = "스페이드"; break;
                default: throw new ArgumentOutOfRangeException(nameof(card));
            }
            return suit + " " + RankLabel(card);
        }

        public static string RankLabel(Card card)
        {
            if (!card.IsValid) throw new ArgumentException("A valid card is required.", nameof(card));
            switch (card.Rank)
            {
                case Rank.Ace: return "A";
                case Rank.King: return "K";
                case Rank.Queen: return "Q";
                case Rank.Jack: return "J";
                default: return ((int)card.Rank).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void RequirePositive(long amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        }
    }
}
