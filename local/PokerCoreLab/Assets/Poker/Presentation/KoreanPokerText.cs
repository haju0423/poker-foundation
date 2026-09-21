using System;
using System.Globalization;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Korean labels and formatting for poker actions, cards and settlement.</summary>
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
        public static string SplitRemainderLabel => "나머지 칩 배분 확인";
        public static string SplitRemainderHelp => "동률인 승자 중 딜러 다음 자리부터 시계 방향으로 나머지 칩을 지급해요. 이번 판에만 적용해요.";
        public static string SplitRemainderPrompt => "배분 순서를 확인하면 이번 판의 모든 팟을 정산해요.";

        public static string CommandErrorMessage(HoldemCommandError error)
        {
            switch (error)
            {
                case HoldemCommandError.WrongTurn: return "지금은 다른 참가자의 차례예요.";
                case HoldemCommandError.IllegalAction: return "지금 가능한 행동과 금액을 다시 확인해 주세요.";
                case HoldemCommandError.Busy: return "앞선 행동을 처리하고 있어요. 잠시 뒤 다시 눌러 주세요.";
                case HoldemCommandError.HandComplete: return "이번 판은 끝났어요. 결과를 확인해 주세요.";
                case HoldemCommandError.CannotContinue: return "테이블 승부가 끝났어요. 새 게임을 시작해 주세요.";
                case HoldemCommandError.SettlementRuleRequired:
                case HoldemCommandError.InvalidSettlementRule: return "나머지 칩의 배분 순서를 먼저 확인해 주세요.";
                case HoldemCommandError.RevealPending: return "공용 카드를 확인한 뒤 ‘계속’을 눌러 주세요.";
                case HoldemCommandError.AccusationResponsesPending: return "다른 참가자의 고발 선택을 기다리고 있어요.";
                case HoldemCommandError.AccusationVerdictsPending: return "고발 판정을 기다리고 있어요.";
                case HoldemCommandError.AccusationConsequencesPending: return "고발 결과를 처리하고 있어요.";
                case HoldemCommandError.InvalidAccusationTarget: return "고발할 상대를 다시 골라 주세요.";
                case HoldemCommandError.UnauthorizedSeat:
                case HoldemCommandError.WrongSession:
                case HoldemCommandError.NotStarted: return "현재 테이블에 연결하지 못했어요. 화면을 다시 확인해 주세요.";
                default: return "진행 상태가 바뀌었어요. 화면을 확인하고 다시 선택해 주세요.";
            }
        }

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
                case SettlementFailure.MissingOddChipOrder: return "동률로 남은 칩을 나눠야 정산을 마칠 수 있습니다.";
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

        public static string HandDescription(HandValue value)
        {
            string name = HandName(value);
            string first = RankText(value.GetTieBreaker(0));
            switch (value.Category)
            {
                case HandCategory.OnePair:
                case HandCategory.ThreeOfAKind:
                case HandCategory.FourOfAKind:
                    return name + " " + first + " · 남은 카드 " + TieRanks(value, 1);
                case HandCategory.TwoPair:
                    return name + " " + first + "·" + RankText(value.GetTieBreaker(1))
                        + " · 남은 카드 " + RankText(value.GetTieBreaker(2));
                case HandCategory.FullHouse:
                    return name + " · " + first + " 3장, " + RankText(value.GetTieBreaker(1)) + " 2장";
                case HandCategory.Straight:
                case HandCategory.StraightFlush:
                    return value.Category == HandCategory.StraightFlush && value.GetTieBreaker(0) == (int)Rank.Ace
                        ? name : name + " · " + first + "까지 연속";
                default: return name + " · " + TieRanks(value, 0);
            }
        }

        private static string TieRanks(HandValue value, int start)
        {
            var ranks = new string[value.TieBreakerCount - start];
            for (int i = start; i < value.TieBreakerCount; i++) ranks[i - start] = RankText(value.GetTieBreaker(i));
            return string.Join("·", ranks);
        }

        private static string RankText(int rank)
        {
            switch ((Rank)rank)
            {
                case Rank.Ace: return "A";
                case Rank.King: return "K";
                case Rank.Queen: return "Q";
                case Rank.Jack: return "J";
                default: return rank.ToString(CultureInfo.InvariantCulture);
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
            return RankText((int)card.Rank);
        }

        private static void RequirePositive(long amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        }
    }
}
