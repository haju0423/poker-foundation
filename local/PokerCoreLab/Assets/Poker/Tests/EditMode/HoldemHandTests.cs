using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemHandTests
    {
        private static readonly SeatId A = new SeatId(1);
        private static readonly SeatId B = new SeatId(2);

        [Test]
        public void OrderedDeckDealsBigBlindFirstAndButtonActsFirstPreflop()
        {
            HoldemHand hand = Begin(100, 100);
            Assert.That(hand.ButtonSeat, Is.EqualTo(A));
            Assert.That(hand.BigBlindSeat, Is.EqualTo(B));
            Assert.That(hand.CurrentSeat, Is.EqualTo(A));
            Assert.That(hand.GetHoleCard(B, 0), Is.EqualTo(Card.FromId(0)));
            Assert.That(hand.GetHoleCard(A, 0), Is.EqualTo(Card.FromId(1)));
            Assert.That(hand.GetHoleCard(B, 1), Is.EqualTo(Card.FromId(2)));
            Assert.That(hand.GetHoleCard(A, 1), Is.EqualTo(Card.FromId(3)));
            Assert.That(hand.Ledger.GetChips(A).Committed, Is.EqualTo(1));
            Assert.That(hand.Ledger.GetChips(B).Committed, Is.EqualTo(2));
        }

        [Test]
        public void EachStreetBurnsOneAndBigBlindActsFirstPostflop()
        {
            HoldemHand hand = Begin(100, 100);
            hand = Act(hand, BettingAction.Call());
            hand = Act(hand, BettingAction.Check());
            Assert.That(hand.Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(hand.CurrentSeat, Is.EqualTo(B));
            Assert.That(Board(hand), Is.EqualTo(new[] { Card.FromId(5), Card.FromId(6), Card.FromId(7) }));
            Assert.That(hand.RemainingCardCount, Is.EqualTo(44));

            hand = CheckRound(hand);
            Assert.That(hand.Street, Is.EqualTo(HoldemStreet.Turn));
            Assert.That(Board(hand).Last(), Is.EqualTo(Card.FromId(9)));
            Assert.That(hand.RemainingCardCount, Is.EqualTo(42));
            hand = CheckRound(hand);
            Assert.That(hand.Street, Is.EqualTo(HoldemStreet.River));
            Assert.That(Board(hand).Last(), Is.EqualTo(Card.FromId(11)));
            Assert.That(hand.RemainingCardCount, Is.EqualTo(40));
            hand = CheckRound(hand);
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(hand.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
        }

        [Test]
        public void AllInCallRefundsUnmatchedExcessAndRunsBoardAtomically()
        {
            HoldemHand original = Begin(100, 40);
            HoldemHand shove = Act(original, BettingAction.RaiseTo(100));
            HoldemHand completed = Act(shove, BettingAction.Call());
            Assert.That(original.Ledger.TotalCommitted, Is.EqualTo(3));
            Assert.That(shove.Ledger.GetChips(A).Stack, Is.Zero);
            Assert.That(completed.IsComplete, Is.True);
            Assert.That(completed.BoardCount, Is.EqualTo(5));
            Assert.That(completed.Result.PotAmount, Is.EqualTo(80));
            Assert.That(completed.Ledger.TotalCommitted, Is.Zero);
            Assert.That(completed.Ledger.TotalChips, Is.EqualTo(140));
            Assert.That(completed.Ledger.GetChips(A).Stack + completed.Ledger.GetChips(B).Stack,
                Is.EqualTo(140));
        }

        [Test]
        public void BlindOnlyShortAllInRefundsBigBlindAndRunsOut()
        {
            HoldemHand hand = Begin(100, 1);
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(hand.BoardCount, Is.EqualTo(5));
            Assert.That(hand.Result.PotAmount, Is.EqualTo(2));
            Assert.That(hand.Ledger.TotalChips, Is.EqualTo(101));
            Assert.That(hand.Ledger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void FoldDoesNotDealAnotherStreetOrExposeAnEvaluation()
        {
            HoldemHand hand = Begin(100, 100);
            hand = Act(hand, BettingAction.Call());
            hand = Act(hand, BettingAction.Check());
            Assert.That(hand.BoardCount, Is.EqualTo(3));
            hand = Act(hand, BettingAction.Fold());
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(hand.BoardCount, Is.EqualTo(3));
            Assert.That(hand.Result.Kind, Is.EqualTo(HoldemResultKind.Fold));
            Assert.That(hand.Result.FoldedSeat, Is.EqualTo(B));
            Assert.Throws<InvalidOperationException>(() => hand.Result.GetHandValue(A));
        }

        [Test]
        public void HoleBoardAndBurnInventoryContainsNoDuplicateCards()
        {
            HoldemHand hand = Begin(100, 100);
            hand = Act(hand, BettingAction.RaiseTo(100));
            hand = Act(hand, BettingAction.Call());
            var visible = new List<Card>();
            visible.Add(hand.GetHoleCard(A, 0)); visible.Add(hand.GetHoleCard(A, 1));
            visible.Add(hand.GetHoleCard(B, 0)); visible.Add(hand.GetHoleCard(B, 1));
            visible.AddRange(Board(hand));
            Assert.That(visible.Distinct().Count(), Is.EqualTo(9));
            Assert.That(hand.RemainingCardCount, Is.EqualTo(40));
            Assert.That(4 + 5 + 3 + hand.RemainingCardCount, Is.EqualTo(52));
        }

        [Test]
        public void RejectedActionCannotMutateOriginalHandOrDeckCursor()
        {
            HoldemHand hand = Begin(100, 100);
            long committed = hand.Ledger.TotalCommitted;
            int remaining = hand.RemainingCardCount;
            Assert.Throws<InvalidOperationException>(() => hand.Apply(B, BettingAction.Check()));
            Assert.Throws<InvalidOperationException>(() => hand.Apply(A, BettingAction.Check()));
            Assert.That(hand.CurrentSeat, Is.EqualTo(A));
            Assert.That(hand.Ledger.TotalCommitted, Is.EqualTo(committed));
            Assert.That(hand.RemainingCardCount, Is.EqualTo(remaining));
            Assert.That(hand.BoardCount, Is.Zero);
        }

        private static HoldemHand Begin(long a, long b) => HoldemHand.Begin(Guid.NewGuid(),
            ChipLedger.Create(new[] { new SeatChips(A, a), new SeatChips(B, b) }),
            A, B, new HoldemConfig(100, 1, 2), new IdentityRandom());

        private static HoldemHand Act(HoldemHand hand, BettingAction action)
            => hand.Apply(hand.CurrentSeat.Value, action);

        private static HoldemHand CheckRound(HoldemHand hand)
            => Act(Act(hand, BettingAction.Check()), BettingAction.Check());

        private static Card[] Board(HoldemHand hand)
            => Enumerable.Range(0, hand.BoardCount).Select(hand.GetBoardCard).ToArray();

        private sealed class IdentityRandom : IRandomSource
        {
            public int NextInt(int exclusiveMax) => exclusiveMax - 1;
        }
    }
}
