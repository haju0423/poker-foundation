using System;
using NUnit.Framework;
using Poker.Foundation;

namespace Poker.Runtime.Tests
{
    public sealed class HoldemTableOptionsTests
    {
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ValidOptionsKeepTheirValues(int seats)
        {
            var options = new HoldemTableOptions(seats, 0.7f, HoldemRevealPolicy.PauseAfterCommunityReveal);
            Assert.That(options.SeatCount, Is.EqualTo(seats));
            Assert.That(options.OpponentDelaySeconds, Is.EqualTo(0.7f));
            Assert.That(options.RevealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
        }
        [Test]
        public void InvalidOptionsFailBeforeReplacingTheGame()
        {
            foreach (int seats in new[] { -1, 0, 1, 5 })
                Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemTableOptions(seats, 0.7f, HoldemRevealPolicy.Automatic));
            foreach (float delay in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -1f, 0f, 0.09f })
                Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemTableOptions(4, delay, HoldemRevealPolicy.Automatic));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemTableOptions(4, 0.7f, (HoldemRevealPolicy)99));
        }
    }
}
