using System.Collections.Generic;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Runtime
{
    public sealed partial class HoldemTableScreen
    {
        private SeatId selectedBestSeat;
        private bool bestComparisonChosen;
        private readonly HashSet<Card> highlightedBest = new HashSet<Card>();

        // A visible own hole card is not permission to expose a best hand. The snapshot grants it explicitly.
        private static bool HasPublicBest(HoldemSeatDisplay seat)
            => seat.RevealedBestCardCount == 5 && seat.RevealedHandValue.HasValue;

        private void PrepareBestComparison(HoldemTableDisplay previous)
        {
            if (previous == null || previous.SessionId != view.SessionId || previous.HandId != view.HandId)
            { selectedBestSeat = view.ViewerSeat; bestComparisonChosen = false; }
            var own = view.GetSeat(view.ViewerSeat);
            if (!bestComparisonChosen && !HasPublicBest(own) && view.Result?.WinnerSeat != null)
            {
                var winner = view.GetSeat(view.Result.WinnerSeat.Value);
                if (HasPublicBest(winner)) selectedBestSeat = winner.Seat;
            }
            highlightedBest.Clear();
            HoldemSeatDisplay selected = null;
            for (int i = 0; i < view.SeatCount; i++)
            {
                var candidate = view.GetSeatAt(i);
                if (candidate.Seat == selectedBestSeat && HasPublicBest(candidate)) selected = candidate;
            }
            if (selected == null)
            {
                if (HasPublicBest(own)) selected = own;
                else for (int i = 0; i < view.SeatCount && selected == null; i++)
                {
                    var candidate = view.GetSeatAt(i);
                    if (HasPublicBest(candidate)) selected = candidate;
                }
            }
            if (selected == null) return;
            selectedBestSeat = selected.Seat;
            for (int i = 0; i < 5; i++) highlightedBest.Add(selected.GetRevealedBestCard(i));
        }

        private void CompareBest(SeatId seat)
        {
            if (!CanInteract() || !HasPublicBest(view.GetSeat(seat))) return;
            selectedBestSeat = seat; bestComparisonChosen = true;
            Render();
        }

        private string BestComparisonNotice()
        {
            if (highlightedBest.Count == 0) return "쇼다운 · 끝까지 남은 참가자의 패를 공개해요.";
            return "쇼다운 · " + SeatName(selectedBestSeat) + ": "
                + KoreanPokerText.HandDescription(view.GetSeat(selectedBestSeat).RevealedHandValue.Value);
        }
    }
}
