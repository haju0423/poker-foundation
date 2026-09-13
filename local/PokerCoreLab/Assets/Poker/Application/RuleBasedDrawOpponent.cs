using System;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>A small, stateless practice policy. Thresholds are tuning values, not win probabilities.</summary>
    public sealed class RuleBasedDrawOpponent : IPokerOpponent
    {
        private readonly PracticeOpponentSettings settings;
        private readonly int variationSeed;
        private readonly SimpleDrawOpponent exchangePolicy = new SimpleDrawOpponent();

        public RuleBasedDrawOpponent() : this(PracticeOpponentSettings.Default) { }

        public RuleBasedDrawOpponent(PracticeOpponentSettings settings, int variationSeed = 0)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.variationSeed = variationSeed;
        }

        public HandCommand Choose(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.IsOwnTurn) throw new InvalidOperationException("This opponent cannot act now.");
            if (view.CanExchange) return exchangePolicy.Choose(view);
            PlayerBettingOptions legal = view.Betting;
            if (legal == null) throw new InvalidOperationException("No legal betting turn.");

            HandCategory category = HandEvaluator.Evaluate(view.OwnCards).Category;
            BettingAction action;
            if (legal.CanCheck)
            {
                bool wantsAggression = category >= HandCategory.TwoPair || WantsBluff(view, category);
                long? target = wantsAggression ? PokerBetSizing.HalfPotTarget(view) : null;
                // If even this modest size exceeds the budget, check. Never clamp below the legal minimum.
                action = target.HasValue && target.Value <= AggressionBudget(view)
                    ? (legal.CanBet ? BettingAction.BetTo(target.Value) : BettingAction.RaiseTo(target.Value))
                    : BettingAction.Check();
            }
            else
            {
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

        private decimal CallLimit(HandCategory category, HandPhase phase)
        {
            bool beforeDraw = phase == HandPhase.FirstBetting;
            switch (category)
            {
                case HandCategory.HighCard:
                    return beforeDraw ? settings.BeforeDrawHighCardCallLimit : settings.AfterDrawHighCardCallLimit;
                case HandCategory.OnePair:
                    return beforeDraw ? settings.BeforeDrawPairCallLimit : settings.AfterDrawPairCallLimit;
                case HandCategory.TwoPair:
                    return settings.TwoPairCallLimit;
                default:
                    return 1m;
            }
        }

        private bool CanAffordMinimumRaise(PokerPlayerView view, PlayerBettingOptions legal)
        {
            if (!legal.CanRaise || !legal.MinimumAggressiveTarget.HasValue ||
                !legal.MaximumAggressiveTarget.HasValue)
                return false;

            long minimum = legal.MinimumAggressiveTarget.Value;
            return minimum > 0 && minimum <= legal.MaximumAggressiveTarget.Value && minimum <= AggressionBudget(view);
        }

        private long AggressionBudget(PokerPlayerView view)
        {
            foreach (PublicSeatView seat in view.Seats)
            {
                if (seat.Seat != view.ViewerSeat) continue;
                if (!seat.StreetContribution.HasValue)
                    throw new InvalidOperationException("The acting seat needs its street contribution.");
                // This amount stays fixed during the street. Do not reset the budget on each re-raise.
                // Convert before adding so even a valid Int64-sized table remains safe.
                decimal streetBankroll = (decimal)seat.Stack + seat.StreetContribution.Value;
                return (long)decimal.Floor(streetBankroll * settings.AggressionBankrollShare);
            }
            throw new InvalidOperationException("The acting seat is missing from its view.");
        }

        private bool WantsBluff(PokerPlayerView view, HandCategory category)
        {
            if (category != HandCategory.HighCard || view.Phase != HandPhase.SecondBetting
                || !view.Betting.CanBet || settings.AfterDrawBluffPercent == 0) return false;
            int remaining = 0;
            foreach (PublicSeatView seat in view.Seats) if (!seat.IsFolded) remaining++;
            return remaining == 2 && RollPercent(view) < settings.AfterDrawBluffPercent;
        }

        // Explicit, stateless non-cryptographic variation. It never reads/depletes the deck RNG.
        // This is reproducibility, not a fairness guarantee or a secret strategy seed.
        private int RollPercent(PokerPlayerView view)
        {
            unchecked
            {
                uint hash = 2166136261u ^ (uint)variationSeed;
                foreach (byte value in view.HandId.ToByteArray()) hash = (hash ^ value) * 16777619u;
                uint seat = (uint)view.ViewerSeat.Value;
                for (int i = 0; i < 4; i++) { hash = (hash ^ (byte)seat) * 16777619u; seat >>= 8; }
                ulong version = (ulong)view.Version;
                for (int i = 0; i < 8; i++) { hash = (hash ^ (byte)version) * 16777619u; version >>= 8; }
                hash ^= hash >> 16; hash *= 0x85ebca6bu;
                hash ^= hash >> 13; hash *= 0xc2b2ae35u; hash ^= hash >> 16;
                return (int)(hash % 100u);
            }
        }
    }
}
