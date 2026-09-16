using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public interface IHoldemOpponentPolicy
    {
        BettingAction Choose(HoldemSnapshot view, IRandomSource random);
    }

    /// <summary>Local poker opponent, not an LLM. Samples unknown cards from its own perspective only.</summary>
    public sealed class HoldemNpcPolicy : IHoldemOpponentPolicy
    {
        private readonly int samples;
        public HoldemNpcPolicy(int samples = 48)
        {
            if (samples < 1 || samples > 512) throw new ArgumentOutOfRangeException(nameof(samples));
            this.samples = samples;
        }

        public BettingAction Choose(HoldemSnapshot view, IRandomSource random)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (random == null) throw new ArgumentNullException(nameof(random));
            LegalBettingActions legal = view.LegalActions;
            if (legal == null) throw new InvalidOperationException("The opponent is not the current actor.");
            double equity = EstimateEquity(view, random, samples);
            double odds = legal.CanCall ? legal.CallAmount / ((double)view.PotAmount + legal.CallAmount) : 0;
            int roll = Next(random, 100);
            int contenders = CountOpponents(view) + 1;
            double valueThreshold = Math.Max(0.38, 1.0 / contenders + 0.16);
            bool valueBet = equity >= valueThreshold && roll < 78;
            bool smallBluff = roll < 5 && (!legal.CanCall || odds < 0.18);
            if ((legal.CanBet || legal.CanRaise) && (valueBet || smallBluff))
            {
                decimal afterCall = (decimal)view.PotAmount + legal.CallAmount;
                decimal desired = view.OwnStreetContribution + (decimal)legal.CallAmount + decimal.Floor(afterCall / 2m);
                long target = (long)Math.Max(legal.MinimumAggressiveTarget.Value,
                    Math.Min(legal.MaximumAggressiveTarget.Value, desired));
                return legal.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target);
            }
            if (legal.CanCheck) return BettingAction.Check();
            if (legal.CanCall && (equity + 0.10 >= odds || !legal.CanFold)) return BettingAction.Call();
            if (legal.CanFold) return BettingAction.Fold();
            throw new InvalidOperationException("No supported legal opponent action.");
        }

        // No access to session, deck, burn cards or actual opponent holes. Estimates are not exact odds.
        public static double EstimateEquity(HoldemSnapshot view, IRandomSource random, int samples)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (samples < 1 || samples > 512) throw new ArgumentOutOfRangeException(nameof(samples));
            if (view.OwnCardCount != 2) throw new InvalidOperationException("Only a dealt-in player can estimate equity.");
            var own = new[] { view.GetOwnCard(0), view.GetOwnCard(1) };
            var known = new HashSet<Card>(own);
            var board = new Card[5];
            for (int i = 0; i < view.BoardCount; i++) { board[i] = view.GetBoardCard(i); known.Add(board[i]); }
            var unseen = new List<Card>();
            for (int id = 0; id < Card.DeckSize; id++) if (!known.Contains(Card.FromId(id))) unseen.Add(Card.FromId(id));
            Card[] population = unseen.ToArray();
            var other = new Card[2];
            int opponents = CountOpponents(view);
            if (opponents == 0) return 1;
            double score = 0;
            for (int trial = 0; trial < samples; trial++)
            {
                var shuffled = (Card[])population.Clone();
                int needed = 2 * opponents + 5 - view.BoardCount;
                for (int i = 0; i < needed; i++)
                {
                    int pick = i + Next(random, shuffled.Length - i);
                    Card swap = shuffled[i]; shuffled[i] = shuffled[pick]; shuffled[pick] = swap;
                }
                for (int i = view.BoardCount; i < 5; i++) board[i] = shuffled[2 * opponents + i - view.BoardCount];
                HandValue ownValue = HoldemBestHand.Evaluate(board, own).Value;
                bool beaten = false; int tied = 1;
                for (int otherIndex = 0; otherIndex < opponents; otherIndex++)
                {
                    other[0] = shuffled[2 * otherIndex]; other[1] = shuffled[2 * otherIndex + 1];
                    int comparison = ownValue.CompareTo(HoldemBestHand.Evaluate(board, other).Value);
                    if (comparison < 0) { beaten = true; break; }
                    if (comparison == 0) tied++;
                }
                if (!beaten) score += 1.0 / tied;
            }
            return score / samples;
        }

        private static int CountOpponents(HoldemSnapshot view)
        {
            int count = 0;
            for (int i = 0; i < view.SeatCount; i++)
            {
                var seat = view.GetSeatAt(i);
                if (!seat.IsViewer && seat.WasDealtIn && seat.Status != HoldemSeatStatus.Folded) count++;
            }
            return count;
        }

        private static int Next(IRandomSource random, int bound)
        {
            int value = random.NextInt(bound);
            if (value < 0 || value >= bound) throw new InvalidOperationException("Invalid random sample.");
            return value;
        }
    }
}
