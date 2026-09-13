using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Concise practice-table wording, separate from input/rules and all Unity components.</summary>
    public static class KoreanTableText
    {
        public const string Title = "파이브 카드 드로우";
        public const string Subtitle = "1:1 연습 테이블 · 기본 포커";
        public static string NewPractice(long startingStack) => "새 연습 · 각 " + KoreanPokerText.Chips(startingStack);
        public const string Pending = "행동을 확인하고 있어요";
        public const string Retry = "같은 요청 다시 확인";
        public const string Rules = "5장으로 승부하는 포커예요.\n첫 베팅 → 원하는 카드 0~5장 교환 → 마지막 베팅 → 정산\n콜: 필요한 칩을 따라 냅니다. 체크: 추가 칩 없이 넘깁니다.\n폴드: 이번 승부를 포기합니다. 레이즈: 베팅 총액을 높입니다.\n지금은 단순 테스트 상대와 진행하며, 상대 패 공개는 팀 규칙 확정 전까지 제외했어요.";
        public static string SeatName(bool own) => own ? "나" : "테스트 상대";
        public static string Status(PublicSeatView seat)
            => seat.IsFolded ? "폴드" : seat.IsAllIn ? "올인" : "참가 중";
        public static string Instruction(PokerPlayerView view, bool pending)
        {
            if (pending) return Pending;
            if (view.Phase == HandPhase.AwaitingSettlementRule) return "남는 칩의 지급 기준을 정해야 마칠 수 있어요.";
            if (view.Phase == HandPhase.Complete) return "이번 연습이 끝났어요.";
            if (!view.IsOwnTurn) return "상대의 차례예요";
            if (view.CanExchange) return "바꿀 카드를 누른 뒤 교환을 확정하세요.";
            return view.Betting.CanCheck ? "내 차례 · 추가 칩 없이 체크할 수 있어요."
                : "내 차례 · 콜하려면 " + KoreanPokerText.Chips(view.Betting.CallAmount) + "이 필요해요.";
        }
        public static string Result(PokerPlayerView view)
        {
            if (view.Phase != HandPhase.Complete) return "";
            var parts = new List<string>();
            foreach (PublicSeatView seat in view.Seats)
                if (seat.Awarded > 0) parts.Add(SeatName(seat.Seat == view.ViewerSeat) + " +" + KoreanPokerText.Chips(seat.Awarded.Value));
            return "팟 지급  ·  " + string.Join("  /  ", parts);
        }
        public static string RankLabel(Card card)
        {
            switch (card.Rank)
            {
                case Rank.Ace: return "A";
                case Rank.King: return "K";
                case Rank.Queen: return "Q";
                case Rank.Jack: return "J";
                default: return ((int)card.Rank).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        public static string SuitLabel(Card card)
        {
            switch (card.Suit)
            {
                case Suit.Clubs: return "클로버";
                case Suit.Diamonds: return "다이아";
                case Suit.Hearts: return "하트";
                case Suit.Spades: return "스페이드";
                default: throw new ArgumentException("A valid card is required.", nameof(card));
            }
        }
    }
}
