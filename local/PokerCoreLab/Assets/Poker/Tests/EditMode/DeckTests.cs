using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class DeckTests
    {
        [Test]
        public void NewDeckContainsAll52CardsAndStartsDrawingAtIndexZero()
        {
            Deck deck = OrderedDeck();
            Assert.That(deck.RemainingCount, Is.EqualTo(Card.DeckSize));
            Card[] cards = deck.Draw(Card.DeckSize);
            Assert.That(cards, Is.EqualTo(CanonicalCards()));
            Assert.That(new HashSet<Card>(cards).Count, Is.EqualTo(Card.DeckSize));
            Assert.That(deck.RemainingCount, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(5)]
        [TestCase(51)]
        [TestCase(52)]
        public void DrawingAdvancesByExactlyTheRequestedCount(int count)
        {
            Deck deck = OrderedDeck();
            Card[] cards = deck.Draw(count);
            Assert.That(cards.Length, Is.EqualTo(count));
            Assert.That(deck.RemainingCount, Is.EqualTo(Card.DeckSize - count));
            AssertRange(cards, 0);
            if (deck.RemainingCount > 0)
                Assert.That(deck.Draw(1)[0], Is.EqualTo(Card.FromId(count)));
        }

        [TestCase(0)]
        [TestCase(49)]
        [TestCase(52)]
        public void ZeroDrawIsAnEmptyNoOpBeforeAndAfterExhaustion(int alreadyDrawn)
        {
            Deck deck = OrderedDeck();
            deck.Draw(alreadyDrawn);
            int remaining = deck.RemainingCount;
            Assert.That(deck.Draw(0), Is.Empty);
            Assert.That(deck.Draw(0), Is.Empty);
            Assert.That(deck.RemainingCount, Is.EqualTo(remaining));
            AssertRange(deck.Draw(remaining), alreadyDrawn);
        }

        [TestCase(-1)]
        [TestCase(int.MinValue)]
        public void NegativeDrawIsRejectedWithoutConsumingCards(int count)
        {
            Deck deck = OrderedDeck();
            deck.Draw(5);
            Assert.Throws<ArgumentOutOfRangeException>(() => deck.Draw(count));
            Assert.That(deck.RemainingCount, Is.EqualTo(47));
            AssertRange(deck.Draw(47), 5);
        }

        [TestCase(53)]
        [TestCase(int.MaxValue)]
        public void OversizedDrawIsRejectedBeforeAllocationOrConsumption(int count)
        {
            Deck deck = OrderedDeck();
            Assert.Throws<InvalidOperationException>(() => deck.Draw(count));
            Assert.That(deck.RemainingCount, Is.EqualTo(52));
            Assert.That(deck.Draw(52), Is.EqualTo(CanonicalCards()));
        }

        [Test]
        public void RepeatedShortDeckFailuresPreserveTheLastThreeCards()
        {
            Deck deck = OrderedDeck();
            deck.Draw(49);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Assert.Throws<InvalidOperationException>(() => deck.Draw(5));
                Assert.That(deck.RemainingCount, Is.EqualTo(3));
            }
            AssertRange(deck.Draw(3), 49);
            Assert.That(deck.RemainingCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => deck.Draw(1));
            Assert.Throws<InvalidOperationException>(() => deck.Draw(1));
            Assert.That(deck.RemainingCount, Is.Zero);
        }

        [Test]
        public void MultipleDrawsPreserveOrderAndThe52CardInventory()
        {
            Deck deck = OrderedDeck();
            var dealt = new List<Card>();
            foreach (int count in new[] { 1, 5, 0, 46 })
            {
                Card[] batch = deck.Draw(count);
                Assert.That(batch.Length, Is.EqualTo(count));
                dealt.AddRange(batch);
                Assert.That(dealt.Count + deck.RemainingCount, Is.EqualTo(Card.DeckSize));
            }
            Assert.That(dealt, Is.EqualTo(CanonicalCards()));
            Assert.That(new HashSet<Card>(dealt).Count, Is.EqualTo(Card.DeckSize));
        }

        [Test]
        public void ReturnedArraysDoNotAliasTheDeckOrOtherDraws()
        {
            Deck deck = OrderedDeck();
            Card[] first = deck.Draw(5);
            Card[] second = deck.Draw(5);
            Card[] firstBefore = (Card[])first.Clone();
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first, Is.EqualTo(firstBefore));
            Array.Reverse(first);
            for (int i = 0; i < first.Length; i++) first[i] = default;
            AssertRange(second, 5);
            Array.Reverse(second);
            second[0] = default;
            Assert.That(deck.RemainingCount, Is.EqualTo(42));
            AssertRange(deck.Draw(42), 10);
        }

        [Test]
        public void SeparateDecksHaveIndependentCursorsAndStorage()
        {
            Deck first = OrderedDeck(), second = OrderedDeck();
            Card[] firstHand = first.Draw(5);
            firstHand[0] = default;
            Assert.That(first.RemainingCount, Is.EqualTo(47));
            Assert.That(second.RemainingCount, Is.EqualTo(52));
            Assert.That(second.Draw(52), Is.EqualTo(CanonicalCards()));
            AssertRange(first.Draw(47), 5);
        }

        [Test]
        public void SelfSwapsAreAllowedAndRequestBounds52DownTo2()
        {
            var random = new ScriptedRandom(bound => bound - 1);
            Deck deck = Deck.CreateShuffled(random);
            Assert.That(random.Bounds.Count, Is.EqualTo(51));
            for (int i = 0; i < random.Bounds.Count; i++)
                Assert.That(random.Bounds[i], Is.EqualTo(52 - i));
            // An unchanged order is a possible permutation, not a shuffle failure.
            Assert.That(deck.Draw(52), Is.EqualTo(CanonicalCards()));
        }

        [Test]
        public void AlwaysChoosingZeroProducesTheExpectedRotation()
        {
            Card[] cards = Deck.CreateShuffled(new ScriptedRandom(bound => 0)).Draw(52);
            for (int i = 0; i < 51; i++) Assert.That(cards[i], Is.EqualTo(Card.FromId(i + 1)));
            Assert.That(cards[51], Is.EqualTo(Card.FromId(0)));
        }

        [Test]
        public void EveryLegalSingleSwapUsesTheRequestedIndex()
        {
            // All other iterations self-swap, so the expected order needs only one swap.
            // This also checks interior choices, not just zero and the upper endpoint.
            for (int position = 1; position < Card.DeckSize; position++)
                for (int selected = 0; selected <= position; selected++)
                {
                    Card[] expected = CanonicalCards();
                    expected[position] = Card.FromId(selected);
                    expected[selected] = Card.FromId(position);
                    var random = new ScriptedRandom(bound => bound == position + 1 ? selected : bound - 1);
                    Assert.That(Deck.CreateShuffled(random).Draw(Card.DeckSize), Is.EqualTo(expected));
                }
        }

        [Test]
        public void DrawNeverRequestsMoreRandomValues()
        {
            bool allowRandom = true;
            var random = new ScriptedRandom(bound => allowRandom ? bound - 1
                : throw new InvalidOperationException("Random source must not be called during Draw."));
            Deck deck = Deck.CreateShuffled(random);
            allowRandom = false;
            deck.Draw(5);
            deck.Draw(0);
            Assert.Throws<InvalidOperationException>(() => deck.Draw(48));
            deck.Draw(47);
            Assert.That(random.Bounds.Count, Is.EqualTo(51));
        }

        [Test]
        public void NullRandomSourceIsRejected() => Assert.Throws<ArgumentNullException>(
            () => Deck.CreateShuffled(null));

        [TestCase(0, false)]
        [TestCase(50, false)]
        [TestCase(0, true)]
        [TestCase(50, true)]
        public void OutOfRangeRandomValuesAbortCreation(int badCall, bool returnUpperBound)
        {
            int call = 0;
            var random = new ScriptedRandom(bound => call++ == badCall
                ? (returnUpperBound ? bound : -1) : bound - 1);
            Deck result = null;
            Assert.Throws<InvalidOperationException>(() => result = Deck.CreateShuffled(random));
            Assert.That(result, Is.Null);
            Assert.That(random.Bounds.Count, Is.EqualTo(badCall + 1));
        }

        [TestCase(0)]
        [TestCase(25)]
        [TestCase(50)]
        public void RandomSourceExceptionIsPropagatedWithoutReturningAPartialDeck(int badCall)
        {
            int call = 0;
            var failure = new InvalidOperationException("Scripted source failure.");
            var random = new ScriptedRandom(bound => call++ == badCall ? throw failure : 0);
            Deck result = null;
            var actual = Assert.Throws<InvalidOperationException>(() => result = Deck.CreateShuffled(random));
            Assert.That(actual, Is.SameAs(failure));
            Assert.That(result, Is.Null);
            Assert.That(random.Bounds.Count, Is.EqualTo(badCall + 1));
        }

        [Test]
        public void EqualSeedAndEqualDrawScheduleReproduceOnThisRuntime()
        {
            Deck first = Deck.CreateShuffled(new SeededTestRandom(20260910));
            Deck second = Deck.CreateShuffled(new SeededTestRandom(20260910));
            foreach (int count in new[] { 5, 0, 2, 45 })
            {
                Assert.That(first.Draw(count), Is.EqualTo(second.Draw(count)));
                Assert.That(first.RemainingCount, Is.EqualTo(second.RemainingCount));
            }
        }

        [Test]
        public void ASeededSetOfShufflesAndDrawSchedulesPreservesEveryCard()
        {
            // A conservation sample, not statistical proof of uniformity or security.
            for (int seed = 0; seed < 100; seed++)
            {
                Deck deck = Deck.CreateShuffled(new SeededTestRandom(seed));
                var schedule = new Random(seed);
                var seen = new HashSet<Card>();
                while (deck.RemainingCount > 0)
                {
                    int count = schedule.Next(1, deck.RemainingCount + 1);
                    foreach (Card card in deck.Draw(count))
                    {
                        Assert.That(card.IsValid, Is.True);
                        Assert.That(seen.Add(card), Is.True, "Duplicate card at seed " + seed);
                    }
                    Assert.That(seen.Count + deck.RemainingCount, Is.EqualTo(Card.DeckSize));
                }
                Assert.That(seen.SetEquals(CanonicalCards()), Is.True);
            }
        }

        [Test]
        public void DrawnFiveCardBatchesCanBePassedDirectlyToEvaluator()
        {
            Deck deck = Deck.CreateShuffled(new SeededTestRandom(17));
            var seen = new HashSet<Card>();
            for (int batch = 0; batch < 5; batch++)
            {
                Card[] hand = deck.Draw(HandEvaluator.HandSize);
                Assert.That(HandEvaluator.Evaluate(hand).IsValid, Is.True);
                foreach (Card card in hand) Assert.That(seen.Add(card), Is.True);
            }
            Assert.That(deck.RemainingCount, Is.EqualTo(27));
        }

        private static Deck OrderedDeck() => Deck.CreateShuffled(new ScriptedRandom(bound => bound - 1));

        private static Card[] CanonicalCards()
        {
            var cards = new Card[Card.DeckSize];
            for (int id = 0; id < cards.Length; id++) cards[id] = Card.FromId(id);
            return cards;
        }

        private static void AssertRange(Card[] cards, int startId)
        {
            for (int i = 0; i < cards.Length; i++)
                Assert.That(cards[i], Is.EqualTo(Card.FromId(startId + i)));
        }

        private sealed class ScriptedRandom : IRandomSource
        {
            private readonly Func<int, int> next;
            public readonly List<int> Bounds = new List<int>();

            public ScriptedRandom(Func<int, int> next) { this.next = next; }
            public int NextInt(int exclusiveMax)
            {
                Bounds.Add(exclusiveMax);
                return next(exclusiveMax);
            }
        }

        // Deliberately test-only. No production RNG or cross-runtime seed protocol is selected.
        private sealed class SeededTestRandom : IRandomSource
        {
            private readonly Random random;
            public SeededTestRandom(int seed) { random = new Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
    }
}
