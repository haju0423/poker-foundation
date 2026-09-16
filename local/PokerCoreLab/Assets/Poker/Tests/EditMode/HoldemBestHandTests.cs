using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemBestHandTests
    {
        [Test]
        public void BoardOnlyRoyalFlushUsesNoHoleCards()
        {
            Card[] board = Cards("Th", "Jh", "Qh", "Kh", "Ah");
            HoldemEvaluatedHand result = HoldemBestHand.Evaluate(board, Cards("2c", "3d"));
            Assert.That(result.Value.Category, Is.EqualTo(HandCategory.StraightFlush));
            Assert.That(result.Value.GetTieBreaker(0), Is.EqualTo(14));
            Assert.That(Best(result), Is.EquivalentTo(board));
        }

        [Test]
        public void BestFullHouseUsesHigherTripsWhenSevenCardsContainTwoTrips()
        {
            HoldemEvaluatedHand result = HoldemBestHand.Evaluate(
                Cards("Ac", "Ad", "Ah", "Kc", "2d"), Cards("Kd", "Kh"));
            Assert.That(result.Value.Category, Is.EqualTo(HandCategory.FullHouse));
            Assert.That(result.Value.GetTieBreaker(0), Is.EqualTo(14));
            Assert.That(result.Value.GetTieBreaker(1), Is.EqualTo(13));
        }

        [Test]
        public void SixCardFlushDropsTheLowestSuitedCard()
        {
            HoldemEvaluatedHand result = HoldemBestHand.Evaluate(
                Cards("2h", "4h", "6h", "8h", "Th"), Cards("Ah", "Ks"));
            Assert.That(result.Value.Category, Is.EqualTo(HandCategory.Flush));
            Assert.That(Enumerable.Range(0, 5).Select(result.Value.GetTieBreaker),
                Is.EqualTo(new[] { 14, 10, 8, 6, 4 }));
            CollectionAssert.DoesNotContain(Best(result), CardOf("2h"));
        }

        [Test]
        public void FlopAndTurnCanAlreadyProduceACompleteBestFive()
        {
            Assert.That(HoldemBestHand.Evaluate(Cards("2c", "3d", "4h"), Cards("5s", "6c")).Value.Category,
                Is.EqualTo(HandCategory.Straight));
            Assert.That(HoldemBestHand.Evaluate(Cards("2c", "2d", "4h", "4s"), Cards("2h", "9c")).Value.Category,
                Is.EqualTo(HandCategory.FullHouse));
        }

        [Test]
        public void ResultMatchesBruteForceTwentyOneCombinationMaximum()
        {
            Card[] board = Cards("As", "Kd", "9h", "9c", "3d");
            Card[] holes = Cards("Ah", "7s");
            Card[] all = board.Concat(holes).ToArray();
            HandValue expected = default;
            bool hasExpected = false;
            for (int omittedA = 0; omittedA < 7; omittedA++)
            for (int omittedB = omittedA + 1; omittedB < 7; omittedB++)
            {
                Card[] five = all.Where((card, index) => index != omittedA && index != omittedB).ToArray();
                HandValue value = HandEvaluator.Evaluate(five);
                if (!hasExpected || value > expected) { expected = value; hasExpected = true; }
            }
            Assert.That(HoldemBestHand.Evaluate(board, holes).Value, Is.EqualTo(expected));
        }

        [Test]
        public void PhysicalInputShapeAndDuplicatesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => HoldemBestHand.Evaluate(Cards("2c", "3d"), Cards("4h", "5s")));
            Assert.Throws<ArgumentException>(() => HoldemBestHand.Evaluate(Cards("2c", "3d", "4h"), Cards("5s")));
            Assert.Throws<ArgumentException>(() => HoldemBestHand.Evaluate(
                Cards("2c", "3d", "4h"), Cards("5s", "2c")));
        }

        private static Card[] Best(HoldemEvaluatedHand hand)
            => Enumerable.Range(0, hand.BestCardCount).Select(hand.GetBestCard).ToArray();

        private static Card[] Cards(params string[] cards) => cards.Select(CardOf).ToArray();

        private static Card CardOf(string text)
        {
            const string ranks = "23456789TJQKA";
            const string suits = "cdhs";
            return new Card((Rank)(ranks.IndexOf(text[0]) + 2), (Suit)(suits.IndexOf(text[1]) + 1));
        }
    }
}
