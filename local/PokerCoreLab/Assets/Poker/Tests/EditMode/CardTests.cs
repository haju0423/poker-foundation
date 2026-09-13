using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class CardTests
    {
        [Test]
        public void All52IdentitiesRoundTripAndAreDistinct()
        {
            var cards = new HashSet<Card>();
            for (int id = 0; id < Card.DeckSize; id++)
            {
                Card card = Card.FromId(id);
                Assert.That(card.IsValid, Is.True);
                Assert.That(card.Id, Is.EqualTo(id));
                Assert.That(new Card(card.Rank, card.Suit), Is.EqualTo(card));
                Assert.That(cards.Add(card), Is.True);
            }
            Assert.That(cards.Count, Is.EqualTo(52));
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(15)]
        [TestCase(int.MaxValue)]
        public void InvalidRanksAreRejected(int rank) => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Card((Rank)rank, Suit.Clubs));

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(5)]
        [TestCase(int.MaxValue)]
        public void InvalidSuitsAreRejected(int suit) => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Card(Rank.Ace, (Suit)suit));

        [TestCase(-1)]
        [TestCase(52)]
        [TestCase(int.MaxValue)]
        public void InvalidIdsAreRejected(int id) => Assert.Throws<ArgumentOutOfRangeException>(
            () => Card.FromId(id));

        [Test]
        public void DefaultCardIsInvalidAndHasNoDeckIdentity()
        {
            Card card = default;
            Assert.That(card.IsValid, Is.False);
            Assert.Throws<InvalidOperationException>(() => { _ = card.Id; });
        }

        [Test]
        public void EqualityIncludesSuitAndHashAgrees()
        {
            var ace = new Card(Rank.Ace, Suit.Spades);
            var same = new Card(Rank.Ace, Suit.Spades);
            var different = new Card(Rank.Ace, Suit.Hearts);
            Assert.That(ace == same, Is.True);
            Assert.That(ace.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(ace != different, Is.True);
            Assert.That(ace.ToString(), Is.EqualTo("As"));
        }
    }
}
