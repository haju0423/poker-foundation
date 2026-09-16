using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemFuzzTests
    {
        private static readonly SeatId A = new SeatId(1);
        private static readonly SeatId B = new SeatId(2);

        [Test]
        public void SeededLegalActionFuzzAlwaysFinishesConservesChipsAndHasNoDuplicateCards()
        {
            for (int seed = 0; seed < 128; seed++)
            {
                var decisions = new Random(seed * 37 + 11);
                long firstStack = 3 + decisions.Next(98);
                long secondStack = 3 + decisions.Next(98);
                long total = firstStack + secondStack;
                HoldemHand hand = HoldemHand.Begin(Guid.NewGuid(), ChipLedger.Create(new[] {
                    new SeatChips(A, firstStack), new SeatChips(B, secondStack) }),
                    A, B, new HoldemConfig(100, 1, 2), new SeededRandom(seed));

                int actions = 0;
                while (!hand.IsComplete && actions++ < 256)
                {
                    AssertConserved(hand.Ledger, total);
                    LegalBettingActions legal = hand.CurrentBetting.GetLegalActions();
                    BettingAction action;
                    int choice = decisions.Next(6);
                    if (legal.CanCall && choice == 0) action = BettingAction.Fold();
                    else if (legal.CanCall) action = BettingAction.Call();
                    else if (legal.CanCheck && choice < 4) action = BettingAction.Check();
                    else if (legal.CanBet || legal.CanRaise)
                    {
                        long minimum = legal.MinimumAggressiveTarget.Value;
                        long maximum = legal.MaximumAggressiveTarget.Value;
                        long target = choice == 5 ? maximum : minimum;
                        action = legal.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target);
                    }
                    else action = BettingAction.Check();
                    HoldemHand before = hand;
                    hand = hand.Apply(hand.CurrentSeat.Value, action);
                    Assert.That(hand.Version, Is.EqualTo(before.Version + 1));
                }

                Assert.That(actions, Is.LessThanOrEqualTo(256), "seed " + seed);
                Assert.That(hand.IsComplete, Is.True, "seed " + seed);
                Assert.That(hand.Ledger.TotalCommitted, Is.Zero);
                AssertConserved(hand.Ledger, total);
                var cards = new HashSet<Card> {
                    hand.GetHoleCard(A, 0), hand.GetHoleCard(A, 1),
                    hand.GetHoleCard(B, 0), hand.GetHoleCard(B, 1)
                };
                for (int i = 0; i < hand.BoardCount; i++)
                    Assert.That(cards.Add(hand.GetBoardCard(i)), Is.True, "duplicate at seed " + seed);
                Assert.That(hand.Result.PotAmount, Is.GreaterThan(0));
            }
        }

        private static void AssertConserved(ChipLedger ledger, long total)
        {
            long observed = ledger.TotalCommitted;
            for (int i = 0; i < ledger.SeatCount; i++)
                observed += ledger.GetChips(ledger.GetSeatAt(i)).Stack;
            Assert.That(observed, Is.EqualTo(total));
            Assert.That(ledger.TotalChips, Is.EqualTo(total));
        }

        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
    }
}
