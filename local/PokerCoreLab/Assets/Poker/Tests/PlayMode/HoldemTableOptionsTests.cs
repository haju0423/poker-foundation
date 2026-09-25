using System;
using NUnit.Framework;
using Poker.Foundation;

namespace Poker.Runtime.Tests
{
    public sealed class HoldemTableOptionsTests
    {
        [Test]
        public void TableSettingsRequireExplicitOptInForHostDealing()
        {
            var settings = UnityEngine.ScriptableObject.CreateInstance<HoldemTableSettings>();
            try
            {
                Assert.That(settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.Automatic));
                Assert.That(settings.CreateUtterancePolicy(), Is.Null);
                settings.enableUtterancePreview = true;
                Assert.Throws<InvalidOperationException>(() => settings.CreateUtterancePolicy());
                settings.dealPolicy = HoldemDealPolicy.WaitForHost;
                Assert.That(settings.CreateUtterancePolicy().MaximumPerSeatPerStreet, Is.EqualTo(1));
                Assert.That(settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.WaitForHost));
                Assert.That(settings.CreateConfig().AccusationMode, Is.EqualTo(HoldemAccusationMode.Disabled));
                settings.dealPolicy = (HoldemDealPolicy)99;
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.CreateConfig());
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

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
