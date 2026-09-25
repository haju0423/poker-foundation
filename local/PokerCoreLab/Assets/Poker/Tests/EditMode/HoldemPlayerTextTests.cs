using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemPlayerTextTests
    {
        private readonly Guid[] peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        private sealed class StableRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }

        private HoldemRoom Create(bool start = false)
        {
            var room = new HoldemRoom(Guid.NewGuid(), peers[0], "방장", new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), utterancePolicy: new HoldemUtterancePolicy(128, 2,
                    HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
            if (start)
            {
                for (int i = 1; i < 4; i++) Assert.That(room.Join(peers[i], "참가자 " + i).Accepted, Is.True);
                foreach (var peer in peers) room.SetReady(peer, true);
                var before = room.Read(peers[0]);
                Assert.That(room.StartHand(peers[0], new HoldemStartCommand(before.SessionId,
                    Guid.NewGuid(), Guid.NewGuid(), 0)).Accepted, Is.True);
            }
            return room;
        }

        private HoldemRoomUtterance Say(HoldemRoom room, string text)
        {
            var own = room.ReadUtterances(peers[1]);
            return new HoldemRoomUtterance(own.SessionId, own.HandId, own.WindowId, Guid.NewGuid(), own.Street, text);
        }

        [Test]
        public void StructuralValidationKeepsContentPolicyAndUtf16LimitsSeparate()
        {
            foreach (var value in new[] { null, "\ud800", "\udc00", "\ud800\ud800", "\udc00\ud800", "\ud800x", "x\udc00", "x\n", "x\t", "x\0", "x\u0085" })
                Assert.That(HoldemPlayerText.IsValidSingleLine(value), Is.False);
            foreach (var value in new[] { "", "  ", "\ud83c\udccf", "👩‍💻", "♥️", "e\u0301", "مرحبا שלום" })
                Assert.That(HoldemPlayerText.IsValidSingleLine(value), Is.True);
            var room = Create(true);
            Assert.That(room.SubmitUtterance(peers[1], Say(room, "\u2028")).Utterance.Error,
                Is.EqualTo(HoldemUtteranceError.EmptyText), "Existing whitespace-first error precedence is unchanged.");
            Assert.That(new HoldemClientIdentity(new string('가', 22) + "🃏").Name.Length, Is.EqualTo(24));
            Assert.Throws<ArgumentException>(() => new HoldemClientIdentity(new string('가', 23) + "🃏"));
        }

        [TestCase(0x2028)] [TestCase(0x2029)] [TestCase(0xD800)] [TestCase(0xDC00)]
        public void InvalidNamesDoNotReserveASeatOrConstructClientIdentity(int code)
        {
            string name = "친구" + (char)code + "이름";
            var room = Create(); var before = room.Read(peers[0]);
            Assert.Throws<ArgumentException>(() => new HoldemClientIdentity(name));
            Assert.Throws<ArgumentException>(() => room.Join(peers[1], name));
            Assert.That(room.Read(peers[0]).MemberCount, Is.EqualTo(1));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(before.Revision));
            Assert.That(room.Join(peers[1], "정상 이름").Admission.Seat, Is.EqualTo(new SeatId(2)));
            Assert.Throws<ArgumentException>(() => new HoldemRoom(Guid.NewGuid(), Guid.NewGuid(), name,
                new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom()));
        }

        [TestCase(0x2028)] [TestCase(0x2029)] [TestCase(0xD800)] [TestCase(0xDC00)]
        public void InvalidNamesAreRejectedByBothPublicDisplays(int code)
        {
            var room = Create(true);
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            packet.members[1].name = "친구" + (char)code + "이름";
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(packet));
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(packet));
        }

        [TestCase(0x2028)] [TestCase(0x2029)] [TestCase(0xD800)] [TestCase(0xDC00)]
        public void InvalidRemarksDoNotConsumeQuotaChangePokerOrEnterPublicFeed(int code)
        {
            var room = Create(true); var before = room.Read(peers[1]);
            var result = room.SubmitUtterance(peers[1], Say(room, "멘트" + (char)code + "끝"));
            Assert.That(result.Utterance.Error, Is.EqualTo(HoldemUtteranceError.InvalidText));
            var after = room.Read(peers[1]);
            Assert.That(after.Revision, Is.EqualTo(before.Revision));
            Assert.That(after.Game.SessionVersion, Is.EqualTo(before.Game.SessionVersion));
            Assert.That(after.Game.PotAmount, Is.EqualTo(before.Game.PotAmount));
            Assert.That(after.OwnUtterances.Remaining, Is.EqualTo(before.OwnUtterances.Remaining));
            Assert.That(after.OwnUtterances.Count, Is.Zero);
            Assert.That(after.PublicUtterances.Count, Is.Zero);
            Assert.That(room.SubmitUtterance(peers[1], Say(room, "정상 멘트")).Accepted, Is.True);
        }

        [TestCase(0x2028)] [TestCase(0x2029)] [TestCase(0xD800)] [TestCase(0xDC00)]
        public void InvalidRemarksAreRejectedByOwnAndPublicPacketReaders(int code)
        {
            var room = Create(true);
            Assert.That(room.SubmitUtterance(peers[1], Say(room, "정상 멘트")).Accepted, Is.True);
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[1]));
            packet.ownUtterances.entries[0].text = "멘트" + (char)code + "끝";
            packet.publicUtterances.entries[0].text = packet.ownUtterances.entries[0].text;
            Assert.Throws<ArgumentException>(() => HoldemUtterancePacketReader.Read(packet));
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));

            // Another viewer has no own entry: only the public reader can reject this text.
            packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            packet.publicUtterances.entries[0].text = "멘트" + (char)code + "끝";
            Assert.That(HoldemUtterancePacketReader.Read(packet).Count, Is.Zero);
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
        }

        [TestCase("  한글 🃏  ")] [TestCase("👩‍💻 ♥️")] [TestCase("e\u0301 한")]
        [TestCase("مرحبا שלום")] [TestCase("<b>원문</b>")]
        public void OrdinaryUnicodeAndLiteralTextStayUnchanged(string text)
        {
            var room = Create();
            Assert.That(new HoldemClientIdentity(text).Name, Is.EqualTo(text));
            Assert.That(room.Join(peers[1], text).Accepted, Is.True);
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            Assert.That(new HoldemLobbyDisplay(packet).GetMember(1).Name, Is.EqualTo(text));
            room = Create(true);
            Assert.That(room.SubmitUtterance(peers[1], Say(room, text)).Accepted, Is.True);
            packet = HoldemRoomPacketMapper.Create(room.Read(peers[1]));
            Assert.That(HoldemUtterancePacketReader.Read(packet).GetEntry(0).Text, Is.EqualTo(text));
            Assert.That(HoldemPublicUtterancePacketReader.Read(packet).GetEntry(0).Text, Is.EqualTo(text));
        }
    }
}
