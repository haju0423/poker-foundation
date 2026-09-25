using System;
using System.Linq;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTableDisplayTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void DuplicateAndSelfAliasNamesUseStableSeatLabelsForEveryViewer(int viewer)
        {
            AssertSeatLabels(viewer,
                new[] { "본인", "친구 2번", "친구 2번", "나" },
                new[] { "본인", "2번 · 친구 2번", "3번 · 친구 2번", "4번 · 나" });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void NicknamesThatMatchGeneratedLabelsAreAlsoDisambiguated(int viewer)
        {
            AssertSeatLabels(viewer,
                new[] { "민수", "민수", "2번 · 민수", "3번 · 2번 · 민수" },
                new[] { "1번 · 민수", "2번 · 민수", "3번 · 2번 · 민수", "4번 · 3번 · 2번 · 민수" });
        }

        [Test]
        public void DistinctNamesKeepTheirSpellingAndAreNotParsedOrNormalized()
        {
            AssertSeatLabels(0, new[] { "Host", "친구 2번", "ABC", "abc" },
                new[] { "Host", "친구 2번", "ABC", "abc" });
            AssertSeatLabels(0, new[] { "Host", "가", "\u1100\u1161", "<b>나</b>" },
                new[] { "Host", "가", "\u1100\u1161", "<b>나</b>" });
        }

        [Test]
        public void AllPlayersMayUseTheSameNameWithoutChangingTheirIdentity()
        {
            for (int viewer = 0; viewer < 4; viewer++)
                AssertSeatLabels(viewer, Enumerable.Repeat("친구", 4).ToArray(),
                    new[] { "1번 · 친구", "2번 · 친구", "3번 · 친구", "4번 · 친구" });
        }

        [Test]
        public void UnnamedLocalSeatsKeepExistingOpponentLabels()
        {
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var table = new HoldemTableDisplay(room.Read(peers[viewer]).Game);
                var names = new HoldemSeatLabels(table);
                for (int i = 0; i < 4; i++)
                    Assert.That(names.Get(table.GetSeatAt(i).Seat),
                        Is.EqualTo(i == viewer ? "나" : "상대 " + i));
            }
        }

        [Test]
        public void UnnamedHeadsUpOpponentKeepsTheShortLabel()
        {
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                new SeatId(1), new SeatId(2), new StableRandom());
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            for (int viewer = 1; viewer <= 2; viewer++)
            {
                var labels = new HoldemSeatLabels(new HoldemTableDisplay(session.GetSnapshot(new SeatId(viewer))));
                Assert.That(labels.Get(new SeatId(viewer)), Is.EqualTo("나"));
                Assert.That(labels.Get(new SeatId(3 - viewer)), Is.EqualTo("상대"));
            }
        }

        [Test]
        public void SeatLabelsRejectMissingTablesAndUnknownSeats()
        {
            Assert.Throws<ArgumentNullException>(() => new HoldemSeatLabels(null));
            var labels = new HoldemSeatLabels(new HoldemTableDisplay(Packet(0)));
            Assert.Throws<ArgumentException>(() => labels.Get(new SeatId(99)));
        }

        private void AssertSeatLabels(int viewer, string[] names, string[] expected)
        {
            var packet = Packet(viewer);
            foreach (var member in packet.members) member.name = names[member.seat - 1];
            var display = new HoldemTableDisplay(packet);
            var labels = new HoldemSeatLabels(display);
            Assert.That(Enumerable.Range(1, 4).Select(i => labels.Get(new SeatId(i))).Distinct().Count(), Is.EqualTo(4));
            for (int i = 0; i < 4; i++)
            {
                var seat = new SeatId(i + 1);
                Assert.That(labels.Get(seat), Is.EqualTo(i == viewer ? "나" : expected[i]));
                Assert.That(display.GetSeat(seat).Name, Is.EqualTo(names[i]));
                Assert.That(packet.members.Single(m => m.seat == seat.Value).name, Is.EqualTo(names[i]));
            }
            packet.members[0].name = "수정";
            Assert.That(labels.Get(new SeatId(1)), Is.EqualTo(viewer == 0 ? "나" : expected[0]));
        }
    }
}
