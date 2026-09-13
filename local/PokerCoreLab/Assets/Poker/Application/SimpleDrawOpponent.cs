using System;
using System.Collections.Generic;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Deterministic test opponent, not an LLM or competitive poker AI. Receives only its own view.</summary>
    public sealed class SimpleDrawOpponent : IPokerOpponent
    {
        public HandCommand Choose(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.IsOwnTurn) throw new InvalidOperationException("This opponent cannot act now.");
            if (view.CanExchange)
            {
                var counts = new Dictionary<Rank, int>();
                foreach (Card card in view.OwnCards)
                    counts[card.Rank] = counts.TryGetValue(card.Rank, out int n) ? n + 1 : 1;
                var discards = new List<Card>();
                HandValue value = HandEvaluator.Evaluate(view.OwnCards);
                if (value.Category < HandCategory.Straight)
                {
                    // Keep pairs/three-of-a-kind; with no pair keep the two highest ranks.
                    var singletons = new List<Card>();
                    foreach (Card card in view.OwnCards) if (counts[card.Rank] == 1) singletons.Add(card);
                    singletons.Sort((a, b) => a.Rank.CompareTo(b.Rank));
                    int limit = Math.Min(view.MaxExchangeCount, value.Category == HandCategory.HighCard ? 3 : singletons.Count);
                    for (int i = 0; i < Math.Min(limit, singletons.Count); i++) discards.Add(singletons[i]);
                }
                return HandCommand.Exchange(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, discards);
            }
            if (view.Betting == null) throw new InvalidOperationException("No legal betting turn.");
            // Passive by design, so the human can exercise every bet size without hidden strategy.
            BettingAction action = view.Betting.CanCheck ? BettingAction.Check()
                : view.Betting.CanCall ? BettingAction.Call() : BettingAction.Fold();
            return HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, action);
        }
    }
}
