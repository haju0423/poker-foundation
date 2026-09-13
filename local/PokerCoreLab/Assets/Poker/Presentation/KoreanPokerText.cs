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
        public static string PhaseName(HandPhase phase)
        {
            switch (phase)
            {
                case HandPhase.FirstBetting: return "첫 베팅";
                case HandPhase.Exchange: return "카드 교환";
                case HandPhase.SecondBetting: return "두 번째 베팅";
                case HandPhase.AwaitingSettlementRule: return "정산 대기";
                case HandPhase.Complete: return "이번 판 종료";
                default: throw new ArgumentOutOfRangeException(nameof(phase));
            }
        }

        public static string CommandErrorMessage(HandError error)
        {
            switch (error)
            {
                case HandError.UnauthorizedSeat: return "이 좌석으로 플레이할 권한이 없습니다.";
                case HandError.WrongHand: return "다른 판의 요청입니다. 현재 판을 확인해 주세요.";
                case HandError.CommandConflict: return "이미 처리한 요청과 내용이 다릅니다. 현재 상태를 확인해 주세요.";
                case HandError.VersionMismatch: return "진행 상황이 바뀌었습니다. 현재 상태를 확인해 주세요.";
                case HandError.NotStarted: return "아직 판이 시작되지 않았습니다.";
                case HandError.AlreadyStarted: return "이미 시작한 판입니다.";
                case HandError.Complete: return "이번 판은 끝났습니다.";
                case HandError.SettlementRuleRequired: return "남는 칩의 지급 기준을 정해야 정산을 마칠 수 있습니다.";
                case HandError.WrongPhase: return "지금은 이 행동을 할 수 없습니다.";
                case HandError.WrongTurn: return "아직 내 차례가 아닙니다.";
                case HandError.IllegalBet: return "지금 가능한 베팅과 금액을 확인해 주세요.";
                case HandError.CardNotOwned: return "현재 내 패에 있는 카드만 바꿀 수 있습니다.";
                case HandError.Busy: return "처리 중입니다. 잠시 후 다시 시도해 주세요.";
                default: throw new ArgumentOutOfRangeException(nameof(error));
            }
        }

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
        public static string AllInHelp => "남은 칩을 모두 냅니다. 폴드하지 않았다면 카드 교환은 할 수 있습니다.";
        public static string ExchangeHelp => "바꿀 카드를 고른 뒤 확정하세요. 한 장도 고르지 않으면 현재 패를 유지합니다.";
        public static string KeepHandHelp => "카드를 바꾸지 않고 교환 차례를 마칩니다.";

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
        public static string ExchangeLabel(int selectedCount)
        {
            if (selectedCount < 0 || selectedCount > SeatHand.CardCount)
                throw new ArgumentOutOfRangeException(nameof(selectedCount));
            return selectedCount == 0 ? "그대로 유지하고 확정" :
                "선택한 " + selectedCount.ToString(CultureInfo.InvariantCulture) + "장 교환";
        }
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
            string rank;
            switch (card.Rank)
            {
                case Rank.Ace: rank = "A"; break;
                case Rank.King: rank = "K"; break;
                case Rank.Queen: rank = "Q"; break;
                case Rank.Jack: rank = "J"; break;
                default: rank = ((int)card.Rank).ToString(CultureInfo.InvariantCulture); break;
            }
            return suit + " " + rank;
        }

        private static void RequirePositive(long amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        }
    }
}
