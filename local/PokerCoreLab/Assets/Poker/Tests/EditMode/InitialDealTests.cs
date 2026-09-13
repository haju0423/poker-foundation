using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class InitialDealTests
    {
        [TestCase(1)]
        [TestCase(42)]
        [TestCase(int.MaxValue)]
        public void SeatIdsAcceptPositiveValuesWithoutImplyingOrder(int value)
        {
            var seat = new SeatId(value);
            Assert.That(seat.IsValid, Is.True);
            Assert.That(seat.Value, Is.EqualTo(value));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void InvalidSeatIdsAreRejected(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatId(value));
        }

        [Test]
        public void DefaultSeatIsInvalidAndEqualityIsValueBased()
        {
            SeatId empty = default;
            Assert.That(empty.IsValid, Is.False);
            Assert.That(empty.Value, Is.Zero);
            Assert.That(empty == default(SeatId), Is.True);
            var a = new SeatId(42);
            var equal = new SeatId(42);
            var different = new SeatId(7);
            Assert.That(a == equal, Is.True);
            Assert.That(a.Equals((object)equal), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(equal.GetHashCode()));
            Assert.That(a != different, Is.True);
            Assert.That(a != empty, Is.True);
            Assert.That(a.Equals(null), Is.False);
            Assert.That(a.Equals((object)42), Is.False);
            Assert.That(new HashSet<SeatId> { a, equal, different, empty }.Count, Is.EqualTo(3));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(10)]
        public void InitialDealGivesFiveCardsPerSeatInRoundRobinOrder(int count)
        {
            SeatId[] seats = Seats(count);
            var random = OrderedRandom();
            InitialDeal deal = InitialDeal.Create(random, seats);
            Assert.That(deal.SeatCount, Is.EqualTo(count));
            Assert.That(deal.RemainingCardCount, Is.EqualTo(52 - 5 * count));
            var unique = new HashSet<Card>();
            for (int seatIndex = 0; seatIndex < count; seatIndex++)
            {
                Assert.That(deal.GetSeatAt(seatIndex), Is.EqualTo(seats[seatIndex]));
                SeatHand hand = deal.GetHand(seats[seatIndex]);
                Assert.That(hand.Owner, Is.EqualTo(seats[seatIndex]));
                Assert.That(hand.Count, Is.EqualTo(5));
                for (int round = 0; round < 5; round++)
                {
                    Assert.That(hand[round], Is.EqualTo(Card.FromId(round * count + seatIndex)));
                    Assert.That(unique.Add(hand[round]), Is.True);
                }
                Assert.That(HandEvaluator.Evaluate(hand).IsValid, Is.True);
            }
            Assert.That(unique.Count + deal.RemainingCardCount, Is.EqualTo(52));
            Assert.That(random.Calls, Is.EqualTo(51));
        }

        [Test]
        public void NonMonotonicSeatIdsDoNotChangeTheCallerSuppliedOrder()
        {
            var seats = new[] { new SeatId(42), new SeatId(7), new SeatId(999) };
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), seats);
            Assert.That(Ids(deal.GetHand(seats[0])), Is.EqualTo(new[] { 0, 3, 6, 9, 12 }));
            Assert.That(Ids(deal.GetHand(seats[1])), Is.EqualTo(new[] { 1, 4, 7, 10, 13 }));
            Assert.That(Ids(deal.GetHand(seats[2])), Is.EqualTo(new[] { 2, 5, 8, 11, 14 }));
        }

        [Test]
        public void ShuffledOrderIsDistributedRatherThanRecreatedOrSorted()
        {
            InitialDeal deal = InitialDeal.Create(new ScriptedRandom(bound => 0), Seats(4));
            for (int seat = 0; seat < 4; seat++)
                for (int round = 0; round < 5; round++)
                    Assert.That(deal.GetHand(deal.GetSeatAt(seat))[round].Id,
                        Is.EqualTo(round * 4 + seat + 1));
        }

        [Test]
        public void InputIsFrozenBeforeRandomCallbackCanClearIt()
        {
            var seats = new List<SeatId>(Seats(3));
            SeatId[] expected = seats.ToArray();
            var random = new ScriptedRandom(bound => { seats.Clear(); return bound - 1; });
            InitialDeal deal = InitialDeal.Create(random, seats);
            Assert.That(seats, Is.Empty);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.That(deal.GetSeatAt(index), Is.EqualTo(expected[index]));
                Assert.That(deal.GetHand(expected[index])[0].Id, Is.EqualTo(index));
            }
        }

        [Test]
        public void MutatingCallerOrderAfterCreationDoesNotChangeAnyHand()
        {
            SeatId[] seats = Seats(3);
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), seats);
            Array.Reverse(seats);
            seats[0] = default;
            AssertOrderedSnapshot(deal, 3);
        }

        [Test]
        public void HandSupportsReadOnlyEnumerationAndDoesNotExposeMutableStorage()
        {
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), Seats(2));
            SeatHand hand = deal.GetHand(new SeatId(1));
            Assert.That((object)hand, Is.Not.InstanceOf<Card[]>());
            Assert.That((object)hand, Is.Not.InstanceOf<IList<Card>>());
            Assert.That((object)hand, Is.Not.InstanceOf<IList>());
            var copy = new List<Card>(hand);
            Assert.That(copy, Is.EqualTo(new[] { Card.FromId(0), Card.FromId(2),
                Card.FromId(4), Card.FromId(6), Card.FromId(8) }));
            copy.Clear();
            int index = 0;
            foreach (Card card in (IEnumerable)hand)
                Assert.That(card, Is.EqualTo(hand[index++]));
            Assert.That(index, Is.EqualTo(5));
            AssertOrderedSnapshot(deal, 2);
        }

        [TestCase(-1)]
        [TestCase(5)]
        [TestCase(int.MaxValue)]
        public void InvalidHandIndicesFailWithoutChangingTheDeal(int index)
        {
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), Seats(2));
            SeatHand hand = deal.GetHand(new SeatId(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = hand[index]; });
            AssertOrderedSnapshot(deal, 2);
        }

        [TestCase(-1)]
        [TestCase(2)]
        [TestCase(int.MaxValue)]
        public void InvalidSeatIndicesFailWithoutChangingTheDeal(int index)
        {
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), Seats(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => deal.GetSeatAt(index));
            AssertOrderedSnapshot(deal, 2);
        }

        [Test]
        public void UnknownAndDefaultSeatLookupsFailWithoutChangingTheDeal()
        {
            InitialDeal deal = InitialDeal.Create(OrderedRandom(), Seats(2));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Assert.Throws<ArgumentException>(() => deal.GetHand(default));
                Assert.Throws<KeyNotFoundException>(() => deal.GetHand(new SeatId(99)));
                AssertOrderedSnapshot(deal, 2);
            }
        }

        [Test]
        public void NullRandomIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => InitialDeal.Create(null, Seats(2)));
        }

        [Test]
        public void NullOrderIsRejectedBeforeRandomIsCalled()
        {
            var random = OrderedRandom();
            Assert.Throws<ArgumentNullException>(() => InitialDeal.Create(random, null));
            Assert.That(random.Calls, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(11)]
        [TestCase(52)]
        public void InvalidRecipientCountsAreRejectedBeforeRandomIsCalled(int count)
        {
            var random = OrderedRandom();
            Assert.Throws<ArgumentOutOfRangeException>(() => InitialDeal.Create(random, Seats(count)));
            Assert.That(random.Calls, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void DefaultSeatAnywhereIsRejectedBeforeRandomIsCalled(int index)
        {
            SeatId[] seats = Seats(3);
            seats[index] = default;
            var random = OrderedRandom();
            Assert.Throws<ArgumentException>(() => InitialDeal.Create(random, seats));
            Assert.That(random.Calls, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        public void DuplicateSeatsAreRejectedBeforeRandomIsCalled(int duplicateIndex)
        {
            SeatId[] seats = Seats(3);
            seats[duplicateIndex] = seats[0];
            var random = OrderedRandom();
            Assert.Throws<ArgumentException>(() => InitialDeal.Create(random, seats));
            Assert.That(random.Calls, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(25)]
        [TestCase(51)]
        public void RandomFailureDoesNotPublishAPartialDealOrChangeAnExistingOne(int failureCall)
        {
            InitialDeal existing = InitialDeal.Create(OrderedRandom(), Seats(3));
            InitialDeal result = null;
            int calls = 0;
            var failure = new ApplicationException("Test random failure.");
            var random = new ScriptedRandom(bound =>
            {
                if (++calls == failureCall) throw failure;
                return bound - 1;
            });
            Assert.That(Assert.Throws<ApplicationException>(() =>
                result = InitialDeal.Create(random, Seats(2))), Is.SameAs(failure));
            Assert.That(result, Is.Null);
            Assert.That(random.Calls, Is.EqualTo(failureCall)); // No RNG rollback promise.
            AssertOrderedSnapshot(existing, 3);
        }

        [TestCase(-1)]
        [TestCase(52)]
        public void OutOfRangeRandomOutputDoesNotPublishADeal(int output)
        {
            InitialDeal result = null;
            Assert.Throws<InvalidOperationException>(() =>
                result = InitialDeal.Create(new ScriptedRandom(bound => output), Seats(2)));
            Assert.That(result, Is.Null);
        }

        [Test]
        public void SeparateSuccessfulDealsDoNotShareHandsOrOverwriteExistingState()
        {
            InitialDeal first = InitialDeal.Create(OrderedRandom(), Seats(3));
            InitialDeal second = InitialDeal.Create(new ScriptedRandom(bound => 0), Seats(3));
            Assert.That(first.GetHand(new SeatId(1)), Is.Not.SameAs(second.GetHand(new SeatId(1))));
            Assert.That(second.GetHand(new SeatId(1))[0].Id, Is.EqualTo(1));
            AssertOrderedSnapshot(first, 3);
        }

        [Test]
        public void ReadingTheDealNeverUsesTheRandomSourceAgain()
        {
            bool created = false;
            var random = new ScriptedRandom(bound =>
            {
                if (created) throw new ApplicationException("Random used during a read.");
                return bound - 1;
            });
            InitialDeal deal = InitialDeal.Create(random, Seats(3));
            created = true;
            AssertOrderedSnapshot(deal, 3);
            AssertOrderedSnapshot(deal, 3);
            Assert.That(random.Calls, Is.EqualTo(51));
        }

        private static SeatId[] Seats(int count)
        {
            var result = new SeatId[count];
            for (int index = 0; index < count; index++) result[index] = new SeatId(index + 1);
            return result;
        }

        private static int[] Ids(SeatHand hand)
        {
            var ids = new int[hand.Count];
            for (int index = 0; index < ids.Length; index++) ids[index] = hand[index].Id;
            return ids;
        }

        private static void AssertOrderedSnapshot(InitialDeal deal, int seatCount)
        {
            Assert.That(deal.SeatCount, Is.EqualTo(seatCount));
            Assert.That(deal.RemainingCardCount, Is.EqualTo(52 - 5 * seatCount));
            for (int seatIndex = 0; seatIndex < seatCount; seatIndex++)
            {
                var owner = new SeatId(seatIndex + 1);
                Assert.That(deal.GetSeatAt(seatIndex), Is.EqualTo(owner));
                SeatHand hand = deal.GetHand(owner);
                Assert.That(hand.Owner, Is.EqualTo(owner));
                Assert.That(hand.Count, Is.EqualTo(5));
                for (int round = 0; round < 5; round++)
                    Assert.That(hand[round].Id, Is.EqualTo(round * seatCount + seatIndex));
            }
        }

        private static ScriptedRandom OrderedRandom() => new ScriptedRandom(bound => bound - 1);

        private sealed class ScriptedRandom : IRandomSource
        {
            private readonly Func<int, int> next;
            public ScriptedRandom(Func<int, int> next) { this.next = next; }
            public int Calls { get; private set; }
            public int NextInt(int exclusiveMax) { Calls++; return next(exclusiveMax); }
        }
    }
}
