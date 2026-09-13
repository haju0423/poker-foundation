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
        public const string Rules = "5장으로 승부하는 포커예요.\n첫 베팅 → 원하는 카드 0~5장 교환 → 마지막 베팅 → 정산\n콜: 필요한 칩을 따라 냅니다. 체크: 추가 칩 없이 넘깁니다.\n폴드: 이번 승부를 포기합니다. 레이즈: 베팅 총액을 높입니다.\n상대는 자기 패와 콜 비용에 따라 반응하는 연습 상대예요.\n상대 패는 아직 공개하지 않아요. 새 연습을 누르면 칩이 초기화돼요.";
        public static string SeatName(bool own) => own ? "나" : "상대";
        public static string Status(PublicSeatView seat)
        {
            if (seat == null) throw new ArgumentNullException(nameof(seat));
            if (seat.IsFolded) return "폴드";
            string total = seat.StreetContribution.HasValue
                ? "이번 베팅 총 " + KoreanPokerText.Chips(seat.StreetContribution.Value) : "";
            if (seat.IsAllIn) return total.Length == 0 ? "올인" : "올인 · " + total;
            return total.Length == 0 ? "참가 중" : total;
        }
        public static string Instruction(PokerPlayerView view, bool pending)
        {
            if (pending) return Pending;
            if (view.Phase == HandPhase.AwaitingSettlementRule) return "남는 칩의 지급 기준을 정해야 마칠 수 있어요.";
            if (view.Phase == HandPhase.Complete) return "한 판 끝! 새 연습을 누르면 칩이 초기화돼요.";
            if (!view.IsOwnTurn) return "상대의 차례예요";
            if (view.CanExchange) return "바꿀 카드를 누른 뒤 교환을 확정하세요.";
            return view.Betting.CanCheck ? "내 차례 · 추가 칩 없이 체크할 수 있어요."
                : "내 차례 · 콜하려면 " + KoreanPokerText.Chips(view.Betting.CallAmount) + "이 필요해요.";
        }
        public static string Result(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (view.Result == null) return "";
            var parts = new List<string>();
            foreach (HandSeatResult seat in view.Result.Seats)
                if (seat.GrossAward > 0) parts.Add(ParticipantName(view, seat.Seat) + "에게 " + KoreanPokerText.Chips(seat.GrossAward) + " 지급");
            return "팟 지급 완료 · " + string.Join(" / ", parts);
        }

        /// <summary>Formats authoritative public facts only, never inferred from stack differences.</summary>
        public static string LastAction(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.LastTransition.HasValue) return "";
            PublicHandTransition action = view.LastTransition.Value;
            string text = ParticipantName(view, action.Seat) + " · ";
            if (action.Kind == HandCommandKind.Exchange)
                text += action.ExchangeCount == 0 ? "교환 없이 패 유지" : action.ExchangeCount + "장 교환";
            else
            {
                switch (action.BettingAction)
                {
                    case BettingActionKind.Fold: text += "폴드"; break;
                    case BettingActionKind.Check: text += "체크"; break;
                    case BettingActionKind.Call: text += "콜 · " + KoreanPokerText.Chips(action.ChipsPaid) + " 추가"; break;
                    case BettingActionKind.BetTo: text += KoreanPokerText.Chips(action.TargetTotal.Value) + " 베팅"; break;
                    case BettingActionKind.RaiseTo: text += "총 " + KoreanPokerText.Chips(action.TargetTotal.Value) + "으로 레이즈"; break;
                    default: throw new ArgumentException("An accepted public action is required.", nameof(view));
                }
            }
            if (action.RefundedSeat.HasValue)
                text += " · " + ParticipantName(view, action.RefundedSeat.Value) + "에게 " + KoreanPokerText.Chips(action.RefundedAmount) + " 반환";
            return text;
        }

        /// <summary>Opt-in breakdown of already settled values; contains no opponent hand or rank.</summary>
        public static string ResultDetails(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            PokerHandResultView completed = view.Result;
            if (completed == null) return "";
            var lines = new List<string> { completed.Reason == HandCompletionReason.Uncontested
                ? "한 명만 남아 승부가 끝났어요." : "마지막 패를 비교해 승부가 끝났어요." };
            for (int i = 0; i < completed.Pots.Count; i++)
            {
                HandPotResult pot = completed.Pots[i]; var payouts = new List<string>();
                foreach (SeatPayout payout in pot.Payouts)
                    payouts.Add(ParticipantName(view, payout.Seat) + " " + KoreanPokerText.Chips(payout.Amount)
                        + (payout.HasOddChip ? " (남은 1칩 포함)" : ""));
                lines.Add(KoreanPokerText.PotLabel(i, pot.Amount) + " → " + string.Join(" / ", payouts));
            }
            foreach (HandRefund refund in completed.Refunds)
                lines.Add(KoreanPokerText.PhaseName(refund.BettingPhase) + " 반환 · " + ParticipantName(view, refund.Seat)
                    + "에게 " + KoreanPokerText.Chips(refund.Amount));
            var stacks = new List<string>();
            foreach (HandSeatResult seat in completed.Seats)
                stacks.Add(ParticipantName(view, seat.Seat) + " " + KoreanPokerText.Chips(seat.FinalStack));
            lines.Add("최종 보유 · " + string.Join(" / ", stacks));
            lines.Add("지급액은 순이익과 달라요." + (completed.Refunds.Count > 0
                ? " 반환된 칩은 최종 보유 칩에 이미 포함돼요." : ""));
            return string.Join("\n", lines);
        }

        private static string ParticipantName(PokerPlayerView view, SeatId seat)
        {
            if (seat == view.ViewerSeat) return "나";
            int other = 0;
            foreach (PublicSeatView participant in view.Seats)
            {
                if (participant.Seat != view.ViewerSeat) other++;
                if (participant.Seat == seat) return view.Seats.Count == 2 ? "상대" : "상대 " + other;
            }
            throw new ArgumentException("The public result refers to a nonparticipant.", nameof(seat));
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
