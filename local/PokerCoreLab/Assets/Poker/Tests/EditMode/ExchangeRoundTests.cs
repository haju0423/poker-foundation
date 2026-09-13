using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class ExchangeRoundTests
    {
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(5)]
        public void BeginPreservesTheInitialDealAndUsesTheSuppliedExchangeOrder(int count)
        {
            InitialDeal deal = Deal(count);
            SeatId[] order = Seats(count);
            Array.Reverse(order);
            ExchangeRound round = ExchangeRound.Begin(deal, order);
            Assert.That(round.SeatCount, Is.EqualTo(count));
            Assert.That(round.ExchangeSeatCount, Is.EqualTo(count));
            Assert.That(round.CompletedSeatCount, Is.Zero);
            Assert.That(round.CurrentSeat, Is.EqualTo(order[0]));
            Assert.That(round.IsComplete, Is.False);
            Assert.That(round.DiscardedCardCount, Is.Zero);
            for (int i = 0; i < count; i++)
            {
                Assert.That(round.GetSeatAt(i), Is.EqualTo(deal.GetSeatAt(i)));
                Assert.That(round.GetHand(deal.GetSeatAt(i)), Is.EqualTo(deal.GetHand(deal.GetSeatAt(i))));
            }
            Assert.That(round.RemainingCardCount, Is.EqualTo(52 - count * 5));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void ExchangeReplacesOnlyChosenCardsAndCommitsOneSeat(int count)
        {
            InitialDeal deal = Deal(2);
            ExchangeRound before = ExchangeRound.Begin(deal, Seats(2));
            Card[] chosen = Take(before.GetHand(new SeatId(1)), count);
            ExchangeRound after = before.Apply(new SeatId(1), chosen);
            Assert.That(after.CompletedSeatCount, Is.EqualTo(1));
            Assert.That(after.CurrentSeat, Is.EqualTo(new SeatId(2)));
            Assert.That(after.RemainingCardCount, Is.EqualTo(42 - count));
            Assert.That(after.DiscardedCardCount, Is.EqualTo(count));
            for (int i = 0; i < 5; i++)
                Assert.That(after.GetHand(new SeatId(1))[i].Id, Is.EqualTo(i < count ? 10 + i : 2 * i));
            Assert.That(after.GetHand(new SeatId(2)), Is.EqualTo(before.GetHand(new SeatId(2))));
            AssertUnchangedInitial(before, deal);
            AssertConservation(after);
        }

        [Test]
        public void SelectionOrderDoesNotChangeWhichReplacementGoesIntoEachHandSlot()
        {
            ExchangeRound before = Round(3);
            SeatHand hand = before.GetHand(new SeatId(1));
            ExchangeRound first = before.Apply(new SeatId(1), new[] { hand[4], hand[1] });
            ExchangeRound second = before.Apply(new SeatId(1), new[] { hand[1], hand[4] });
            Assert.That(first.GetHand(new SeatId(1)), Is.EqualTo(second.GetHand(new SeatId(1))));
            Assert.That(Ids(first.GetHand(new SeatId(1))), Is.EqualTo(new[] { 0, 15, 6, 9, 16 }));
        }

        [Test]
        public void StandPatConsumesTheTurnAndCannotBeRepeatedOnTheNewState()
        {
            ExchangeRound before = Round(2);
            ExchangeRound after = before.Apply(new SeatId(1), Array.Empty<Card>());
            Assert.That(after.GetHand(new SeatId(1)), Is.EqualTo(before.GetHand(new SeatId(1))));
            Assert.That(after.CompletedSeatCount, Is.EqualTo(1));
            Assert.That(after.DiscardedCardCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => after.Apply(new SeatId(1), Array.Empty<Card>()));
            Assert.That(after.CurrentSeat, Is.EqualTo(new SeatId(2)));
        }

        [Test]
        public void AllFiveSeatsCanReplaceAllFiveCardsWithoutReusingDiscards()
        {
            InitialDeal deal = Deal(5);
            ExchangeRound round = ExchangeRound.Begin(deal, Seats(5));
            var originalCards = new HashSet<Card>();
            for (int i = 0; i < 5; i++) originalCards.UnionWith(deal.GetHand(deal.GetSeatAt(i)));
            for (int i = 0; i < 5; i++)
            {
                var seat = new SeatId(i + 1);
                round = round.Apply(seat, Take(round.GetHand(seat), 5));
                AssertConservation(round);
                for (int card = 0; card < 5; card++)
                    Assert.That(round.GetHand(seat)[card].Id, Is.EqualTo(25 + i * 5 + card));
            }
            Assert.That(round.RemainingCardCount, Is.EqualTo(2));
            Assert.That(round.DiscardedCardCount, Is.EqualTo(25));
            Assert.That(round.IsComplete, Is.True);
            Assert.That(round.CurrentSeat, Is.Null);
            for (int i = 0; i < 5; i++)
                foreach (Card card in round.GetHand(round.GetSeatAt(i)))
                    Assert.That(originalCards.Contains(card), Is.False);
            Assert.Throws<InvalidOperationException>(() => round.Apply(new SeatId(5), Array.Empty<Card>()));
            Assert.That(deal.RemainingCardCount, Is.EqualTo(27));
        }

        [Test]
        public void ExcludedSeatsKeepTheirCardsAndSuppliedNonNumericOrderIsRespected()
        {
            InitialDeal deal = InitialDeal.Create(new OrderedRandom(), new[] { new SeatId(42), new SeatId(7), new SeatId(999) });
            ExchangeRound round = ExchangeRound.Begin(deal, new[] { new SeatId(999), new SeatId(42) });
            Assert.Throws<InvalidOperationException>(() => round.Apply(new SeatId(7), Array.Empty<Card>()));
            round = round.Apply(new SeatId(999), new[] { deal.GetHand(new SeatId(999))[0] });
            Assert.That(round.CurrentSeat, Is.EqualTo(new SeatId(42)));
            round = round.Apply(new SeatId(42), Array.Empty<Card>());
            Assert.That(round.IsComplete, Is.True);
            Assert.That(round.GetHand(new SeatId(7)), Is.EqualTo(deal.GetHand(new SeatId(7))));
            AssertConservation(round);
        }

        [Test]
        public void EmptyEligibleOrderIsAnAlreadyCompletedPrimitiveNotGameStartApproval()
        {
            ExchangeRound round = ExchangeRound.Begin(Deal(2), Array.Empty<SeatId>());
            Assert.That(round.IsComplete, Is.True);
            Assert.That(round.CurrentSeat, Is.Null);
            Assert.That(round.CompletedSeatCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => round.Apply(new SeatId(1), Array.Empty<Card>()));
            AssertConservation(round);
        }

        [TestCase(6)]
        [TestCase(7)]
        [TestCase(10)]
        public void OversizedInitialTableIsRejectedBeforeExchangeEvenIfSeatsAreExcluded(int count)
        {
            InitialDeal deal = Deal(count); // The existing initial-deal primitive still accepts this.
            Assert.Throws<InvalidOperationException>(() => ExchangeRound.Begin(deal, Seats(1)));
            Assert.That(deal.RemainingCardCount, Is.EqualTo(52 - 5 * count));
        }

        [Test]
        public void NullStartInputsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => ExchangeRound.Begin(null, Seats(2)));
            Assert.Throws<ArgumentNullException>(() => ExchangeRound.Begin(Deal(2), null));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void InvalidEligibleOrdersAreRejected(int kind)
        {
            SeatId[] order = kind == 0 ? new[] { default(SeatId) }
                : kind == 1 ? new[] { new SeatId(1), new SeatId(1) }
                : new[] { new SeatId(99) };
            Assert.Throws<ArgumentException>(() => ExchangeRound.Begin(Deal(2), order));
        }

        [Test]
        public void TooManyEligibleEntriesAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ExchangeRound.Begin(Deal(2), Seats(3)));
        }

        [Test]
        public void MutatingCallerOrdersAndSelectionsCannotChangePublishedStates()
        {
            SeatId[] order = Seats(2);
            ExchangeRound before = ExchangeRound.Begin(Deal(2), order);
            Array.Reverse(order);
            Card[] choice = Take(before.GetHand(new SeatId(1)), 2);
            ExchangeRound after = before.Apply(new SeatId(1), choice);
            Array.Clear(choice, 0, choice.Length);
            Assert.That(after.CurrentSeat, Is.EqualTo(new SeatId(2)));
            Assert.That(Ids(after.GetHand(new SeatId(1))), Is.EqualTo(new[] { 10, 11, 4, 6, 8 }));
            Assert.That(before.CompletedSeatCount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void InvalidSelectionsLeaveEveryPartOfTheStateUnchanged(int kind)
        {
            ExchangeRound round = Round(2);
            SeatHand hand = round.GetHand(new SeatId(1));
            Card[] choice = kind == 0 ? new[] { default(Card) }
                : kind == 1 ? new[] { hand[0], hand[0] }
                : kind == 2 ? new[] { round.GetHand(new SeatId(2))[0] }
                : new[] { Card.FromId(51) };
            for (int attempt = 0; attempt < 2; attempt++)
                Assert.Throws<ArgumentException>(() => round.Apply(new SeatId(1), choice));
            AssertUnchangedInitial(round, Deal(2));
            ExchangeRound next = round.Apply(new SeatId(1), new[] { hand[0] });
            Assert.That(next.GetHand(new SeatId(1))[0].Id, Is.EqualTo(10));
        }

        [Test]
        public void NullAndTooManySelectedCardsAreRejected()
        {
            ExchangeRound round = Round(2);
            Assert.Throws<ArgumentNullException>(() => round.Apply(new SeatId(1), null));
            Assert.Throws<ArgumentOutOfRangeException>(() => round.Apply(new SeatId(1), new Card[6]));
            AssertUnchangedInitial(round, Deal(2));
        }

        [Test]
        public void InvalidUnknownAndOutOfTurnSeatsAreRejected()
        {
            ExchangeRound round = Round(2);
            Assert.Throws<ArgumentException>(() => round.Apply(default, Array.Empty<Card>()));
            Assert.Throws<KeyNotFoundException>(() => round.Apply(new SeatId(99), Array.Empty<Card>()));
            Assert.Throws<InvalidOperationException>(() => round.Apply(new SeatId(2), Array.Empty<Card>()));
            AssertUnchangedInitial(round, Deal(2));
        }

        [Test]
        public void ThrowingRequestCollectionCannotConsumeCardsOrAdvanceTheTurn()
        {
            ExchangeRound round = Round(2);
            var request = new ThrowingSelection(round.GetHand(new SeatId(1))[0]);
            Assert.Throws<ApplicationException>(() => round.Apply(new SeatId(1), request));
            AssertUnchangedInitial(round, Deal(2));
        }

        [Test]
        public void RejectedSecondSeatRequestPreservesPreviouslyCommittedCardsAndDiscards()
        {
            ExchangeRound initial = Round(2);
            ExchangeRound before = initial.Apply(new SeatId(1), Take(initial.GetHand(new SeatId(1)), 2));
            int[] first = Ids(before.GetHand(new SeatId(1)));
            int[] second = Ids(before.GetHand(new SeatId(2)));
            Assert.Throws<ArgumentException>(() => before.Apply(new SeatId(2), new[] { before.GetHand(new SeatId(1))[0] }));
            Assert.Throws<InvalidOperationException>(() => before.Apply(new SeatId(1), Array.Empty<Card>()));
            Assert.That(before.CompletedSeatCount, Is.EqualTo(1));
            Assert.That(before.CurrentSeat, Is.EqualTo(new SeatId(2)));
            Assert.That(before.DiscardedCardCount, Is.EqualTo(2));
            Assert.That(before.RemainingCardCount, Is.EqualTo(40));
            Assert.That(Ids(before.GetHand(new SeatId(1))), Is.EqualTo(first));
            Assert.That(Ids(before.GetHand(new SeatId(2))), Is.EqualTo(second));
            ExchangeRound after = before.Apply(new SeatId(2), new[] { before.GetHand(new SeatId(2))[4] });
            Assert.That(after.GetHand(new SeatId(2))[4].Id, Is.EqualTo(12));
            Assert.That(after.DiscardedCardCount, Is.EqualTo(3));
            AssertConservation(after);
        }

        [Test]
        public void IndependentBeginsAndThrowingOrderCannotMutateTheInitialDeal()
        {
            InitialDeal deal = Deal(2);
            Assert.Throws<ApplicationException>(() => ExchangeRound.Begin(deal, new ThrowingOrder()));
            ExchangeRound first = ExchangeRound.Begin(deal, Seats(2));
            ExchangeRound second = ExchangeRound.Begin(deal, Seats(2));
            ExchangeRound advanced = first.Apply(new SeatId(1), Take(first.GetHand(new SeatId(1)), 5));
            Assert.That(advanced.RemainingCardCount, Is.EqualTo(37));
            AssertUnchangedInitial(first, deal);
            AssertUnchangedInitial(second, deal);
            Assert.That(deal.RemainingCardCount, Is.EqualTo(42));
        }

        [Test]
        public void OldSnapshotsCanBranchWithoutSharingDeckCursorsButAreNotCommandDeduplication()
        {
            ExchangeRound root = Round(2);
            ExchangeRound left = root.Apply(new SeatId(1), Take(root.GetHand(new SeatId(1)), 1));
            ExchangeRound right = root.Apply(new SeatId(1), Take(root.GetHand(new SeatId(1)), 5));
            ExchangeRound leftEnd = left.Apply(new SeatId(2), Take(left.GetHand(new SeatId(2)), 1));
            ExchangeRound rightEnd = right.Apply(new SeatId(2), Take(right.GetHand(new SeatId(2)), 1));
            Assert.That(leftEnd.GetHand(new SeatId(2))[0].Id, Is.EqualTo(11));
            Assert.That(rightEnd.GetHand(new SeatId(2))[0].Id, Is.EqualTo(15));
            Assert.That(left.RemainingCardCount, Is.EqualTo(41));
            Assert.That(root.RemainingCardCount, Is.EqualTo(42));
        }

        [Test]
        public void ExchangeDoesNotCallTheRandomSourceAgain()
        {
            var random = new OrderedRandom();
            InitialDeal deal = InitialDeal.Create(random, Seats(2));
            random.RejectCalls = true;
            ExchangeRound round = ExchangeRound.Begin(deal, Seats(2));
            round = round.Apply(new SeatId(1), Take(round.GetHand(new SeatId(1)), 5));
            round = round.Apply(new SeatId(2), Take(round.GetHand(new SeatId(2)), 5));
            Assert.That(random.Calls, Is.EqualTo(51));
            Assert.That(round.IsComplete, Is.True);
        }

        [TestCase(-1)]
        [TestCase(2)]
        [TestCase(int.MaxValue)]
        public void InvalidLookupsLeaveTheStateUnchanged(int index)
        {
            ExchangeRound round = Round(2);
            Assert.Throws<ArgumentOutOfRangeException>(() => round.GetSeatAt(index));
            Assert.Throws<ArgumentException>(() => round.GetHand(default));
            Assert.Throws<KeyNotFoundException>(() => round.GetHand(new SeatId(99)));
            AssertUnchangedInitial(round, Deal(2));
        }

        [Test]
        public void SampledCompleteRoundsPreserveCardsAndHandsAcrossMixedSelections()
        {
            for (int seed = 0; seed < 40; seed++)
            {
                var random = new Random(seed);
                int count = seed % 5 + 1;
                InitialDeal deal = InitialDeal.Create(new SeededRandom(seed), Seats(count));
                ExchangeRound round = ExchangeRound.Begin(deal, Seats(count));
                while (!round.IsComplete)
                {
                    SeatId seat = round.CurrentSeat.Value;
                    SeatHand hand = round.GetHand(seat);
                    var chosen = new List<Card>();
                    for (int i = 0; i < 5; i++) if (random.Next(2) == 0) chosen.Add(hand[i]);
                    round = round.Apply(seat, chosen);
                    AssertConservation(round);
                    Assert.That(HandEvaluator.Evaluate(round.GetHand(seat)).IsValid, Is.True);
                }
            }
        }

        private static InitialDeal Deal(int count) => InitialDeal.Create(new OrderedRandom(), Seats(count));
        private static ExchangeRound Round(int count) => ExchangeRound.Begin(Deal(count), Seats(count));
        private static SeatId[] Seats(int count)
        {
            var result = new SeatId[count];
            for (int i = 0; i < count; i++) result[i] = new SeatId(i + 1);
            return result;
        }
        private static Card[] Take(SeatHand hand, int count)
        {
            var result = new Card[count];
            for (int i = 0; i < count; i++) result[i] = hand[i];
            return result;
        }
        private static int[] Ids(SeatHand hand)
        {
            var result = new int[hand.Count];
            for (int i = 0; i < result.Length; i++) result[i] = hand[i].Id;
            return result;
        }
        private static void AssertUnchangedInitial(ExchangeRound round, InitialDeal deal)
        {
            Assert.That(round.CompletedSeatCount, Is.Zero);
            Assert.That(round.DiscardedCardCount, Is.Zero);
            Assert.That(round.RemainingCardCount, Is.EqualTo(deal.RemainingCardCount));
            Assert.That(round.CurrentSeat, Is.EqualTo(new SeatId(1)));
            for (int i = 0; i < deal.SeatCount; i++)
                Assert.That(round.GetHand(deal.GetSeatAt(i)), Is.EqualTo(deal.GetHand(deal.GetSeatAt(i))));
        }
        private static void AssertConservation(ExchangeRound round)
        {
            var cards = new HashSet<Card>();
            for (int i = 0; i < round.SeatCount; i++)
            {
                SeatHand hand = round.GetHand(round.GetSeatAt(i));
                Assert.That(hand.Count, Is.EqualTo(5));
                foreach (Card card in hand) Assert.That(card.IsValid && cards.Add(card), Is.True);
            }
            Assert.That(cards.Count + round.RemainingCardCount + round.DiscardedCardCount, Is.EqualTo(52));
        }
        private sealed class OrderedRandom : IRandomSource
        {
            public bool RejectCalls;
            public int Calls;
            public int NextInt(int exclusiveMax)
            {
                if (RejectCalls) throw new ApplicationException("Unexpected random call.");
                Calls++;
                return exclusiveMax - 1;
            }
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
        private sealed class ThrowingSelection : IReadOnlyList<Card>
        {
            private readonly Card first;
            public ThrowingSelection(Card first) { this.first = first; }
            public int Count => 2;
            public Card this[int index] => index == 0 ? first : throw new ApplicationException("Request read failure.");
            public IEnumerator<Card> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class ThrowingOrder : IReadOnlyList<SeatId>
        {
            public int Count => 2;
            public SeatId this[int index] => index == 0 ? new SeatId(1) : throw new ApplicationException("Order read failure.");
            public IEnumerator<SeatId> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
