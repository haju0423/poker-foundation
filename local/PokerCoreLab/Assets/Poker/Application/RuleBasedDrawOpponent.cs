using System;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>A small, stateless practice policy. Thresholds are tuning values, not win probabilities.</summary>
    public sealed class RuleBasedDrawOpponent : IPokerOpponent
    {
        private const decimal BeforeDrawHighCardCallLimit = 0.34m;
        private const decimal AfterDrawHighCardCallLimit = 0.20m;
        private const decimal BeforeDrawPairCallLimit = 0.45m;
        private const decimal AfterDrawPairCallLimit = 0.35m;
        private const decimal TwoPairCallLimit = 0.50m;
        private const decimal RaiseBankrollShare = 0.35m;
        private readonly SimpleDrawOpponent exchangePolicy = new SimpleDrawOpponent();

        public HandCommand Choose(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.IsOwnTurn) throw new InvalidOperationException("This opponent cannot act now.");
            if (view.CanExchange) return exchangePolicy.Choose(view);
            PlayerBettingOptions legal = view.Betting;
            if (legal == null) throw new InvalidOperationException("No legal betting turn.");

            BettingAction action;
            if (legal.CanCheck)
            {
                // Preserve the check-through path while adding responses to a player's wager.
                action = BettingAction.Check();
            }
            else
            {
                HandCategory category = HandEvaluator.Evaluate(view.OwnCards).Category;
                decimal price = legal.CanCall
                    ? (decimal)legal.CallAmount / ((decimal)view.PotAmount + legal.CallAmount)
                    : 0m;
                if (legal.CanFold && legal.CanCall && price > CallLimit(category, view.Phase))
                    action = BettingAction.Fold();
                else if (category >= HandCategory.TwoPair && CanAffordMinimumRaise(view, legal))
                    action = BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value);
                else if (legal.CanCall)
                    action = BettingAction.Call();
                else if (legal.CanFold)
                    action = BettingAction.Fold();
                else
                    throw new InvalidOperationException("No supported legal response.");
            }
            return HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, action);
        }

        private static decimal CallLimit(HandCategory category, HandPhase phase)
        {
            bool beforeDraw = phase == HandPhase.FirstBetting;
            switch (category)
            {
                case HandCategory.HighCard:
                    return beforeDraw ? BeforeDrawHighCardCallLimit : AfterDrawHighCardCallLimit;
                case HandCategory.OnePair:
                    return beforeDraw ? BeforeDrawPairCallLimit : AfterDrawPairCallLimit;
                case HandCategory.TwoPair:
                    return TwoPairCallLimit;
                default:
                    return 1m;
            }
        }

        private static bool CanAffordMinimumRaise(PokerPlayerView view, PlayerBettingOptions legal)
        {
            if (!legal.CanRaise || !legal.MinimumAggressiveTarget.HasValue ||
                !legal.MaximumAggressiveTarget.HasValue)
                return false;

            foreach (PublicSeatView seat in view.Seats)
            {
                if (seat.Seat != view.ViewerSeat) continue;
                if (!seat.StreetContribution.HasValue)
                    throw new InvalidOperationException("The acting seat needs its street contribution.");
                // This amount stays fixed during the street. Do not reset the budget on each re-raise.
                // Convert before adding so even a valid Int64-sized table remains safe.
                decimal streetBankroll = (decimal)seat.Stack + seat.StreetContribution.Value;
                long minimum = legal.MinimumAggressiveTarget.Value;
                return minimum > 0 && minimum <= legal.MaximumAggressiveTarget.Value &&
                    minimum <= decimal.Floor(streetBankroll * RaiseBankrollShare);
            }
            throw new InvalidOperationException("The acting seat is missing from its view.");
        }
    }
}
