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
        public static string NewPractice(long startingStack) => "처음부터 · 각 " + KoreanPokerText.Chips(startingStack);
        public const string Pending = "행동을 확인하고 있어요";
        public const string Retry = "같은 요청 다시 확인";
        public const string Rules = "5장으로 승부하는 포커예요.\n첫 베팅 → 카드 0~5장 교환 → 마지막 베팅 → 쇼다운\n콜: 필요한 칩을 따라 냅니다. 체크: 추가 칩 없이 넘깁니다.\n폴드: 이번 승부를 포기합니다. 레이즈: 베팅 총액을 높입니다.\n쇼다운에서는 남은 참가자의 패를 공개해요. 폴드로 끝나면 공개하지 않아요.\n다음 판은 보유 칩을 이어가고, 처음부터는 칩을 초기화해요.\n현재 연습판은 블라인드 자리·행동 순서를 고정해요. 상대는 규칙 기반 연습 상대예요.";
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
            if (view.Phase == HandPhase.Complete) return CanContinue(view)
                ? "다음 판에서도 지금 보유한 칩으로 이어가요."
                : "보유 칩이 없는 참가자가 있어요. 처음부터 다시 시작할 수 있어요.";
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

        public static bool CanContinue(PokerPlayerView view)
        {
            if (view == null || view.Result == null) return false;
            foreach (var seat in view.Result.Seats) if (seat.FinalStack <= 0) return false;
            return true;
        }

        /// <summary>Concise two-player comparison. Multi-pot tables retain their per-pot payout breakdown.</summary>
        public static string ShowdownSummary(PokerPlayerView view)
        {
            if (view.Result == null) return "";
            if (view.Result.Reason == HandCompletionReason.Uncontested)
            {
                foreach (var seat in view.Result.Seats)
                    if (seat.GrossAward > 0)
                        return ParticipantName(view, seat.Seat) + " 승리 · "
                            + (view.LastTransition.HasValue ? ParticipantName(view, view.LastTransition.Value.Seat) + " 폴드" : "폴드로 승부 종료")
                            + " · 패 비공개";
            }
            var hands = view.Result.RevealedHands;
            if (hands.Count != 2 || view.Result.Pots.Count != 1) return "쇼다운 · 공개된 패와 팟별 정산을 확인하세요.";
            var first = hands[0]; var second = hands[1];
            int comparison = first.Value.CompareTo(second.Value);
            if (comparison == 0) return "무승부 · 족보와 비교 카드가 같아요.";
            var winner = comparison > 0 ? first : second;
            var loser = comparison > 0 ? second : first;
            return ParticipantName(view, winner.Seat) + " 승리 · " + ComparisonReason(winner.Value, loser.Value);
        }

        public static string ComparisonReason(HandValue winner, HandValue loser)
        {
            if (winner.CompareTo(loser) <= 0) throw new ArgumentException("A stronger hand is required.");
            if (winner.Category != loser.Category)
                return KoreanPokerText.HandName(winner) + " > " + KoreanPokerText.HandName(loser);
            for (int i = 0; i < winner.TieBreakerCount; i++)
                if (winner.GetTieBreaker(i) != loser.GetTieBreaker(i))
                    return KoreanPokerText.HandName(winner) + " · " + ComparisonPart(winner.Category, i) + " "
                        + RankText(winner.GetTieBreaker(i)) + " > " + RankText(loser.GetTieBreaker(i));
            throw new InvalidOperationException("Different hand values require a comparison difference.");
        }
        private static string ComparisonPart(HandCategory category, int index)
        {
            switch (category)
            {
                case HandCategory.OnePair: return index == 0 ? "페어" : "키커 " + index;
                case HandCategory.TwoPair: return index == 0 ? "높은 페어" : index == 1 ? "낮은 페어" : "키커";
                case HandCategory.ThreeOfAKind: return index == 0 ? "트리플" : "키커 " + index;
                case HandCategory.FullHouse: return index == 0 ? "트리플" : "페어";
                case HandCategory.FourOfAKind: return index == 0 ? "포카드" : "키커";
                default: return index == 0 ? "가장 높은 카드" : (index + 1) + "번째 비교 카드";
            }
        }
        private static string RankText(int rank) => rank == 14 ? "A" : rank == 13 ? "K" : rank == 12 ? "Q" : rank == 11 ? "J" : rank.ToString();

        /// <summary>Opt-in payout breakdown plus explicitly revealed showdown hands.</summary>
        public static string ResultDetails(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            PokerHandResultView completed = view.Result;
            if (completed == null) return "";
            var lines = new List<string> { completed.Reason == HandCompletionReason.Uncontested
                ? "한 명만 남아 승부가 끝났어요." : "마지막 패를 비교해 승부가 끝났어요." };
            foreach (var hand in completed.RevealedHands)
                lines.Add(ParticipantName(view, hand.Seat) + " · " + KoreanPokerText.HandName(hand.Value));
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
