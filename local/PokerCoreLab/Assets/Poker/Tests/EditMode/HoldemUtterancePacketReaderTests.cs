using System;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemUtterancePacketReaderTests
    {
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private HoldemRoomPacket Packet()
        {
            var peers = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var room = new HoldemRoom(Guid.NewGuid(), peers[0], "방장", new HoldemConfig(100, 1, 2), new SeatId(1),
                new StableRandom(), utterancePolicy: new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            for (int i = 1; i < 4; i++) room.Join(peers[i], "참가자 " + i);
            foreach (var peer in peers) room.SetReady(peer, true);
            room.StartHand(peers[0], new HoldemStartCommand(room.Read(peers[0]).SessionId, Guid.NewGuid(), Guid.NewGuid(), 0));
            var speech = room.ReadUtterances(peers[1]);
            room.SubmitUtterance(peers[1], new HoldemRoomUtterance(speech.SessionId, speech.HandId, speech.WindowId,
                Guid.NewGuid(), speech.Street, "원문 <b>그대로</b>"));
            return HoldemRoomPacketMapper.Create(room.Read(peers[1]));
        }

        [Test]
        public void ReadOwnCreatesDetachedTextOnlyValuesAndDisabledObjectsAreIgnored()
        {
            var packet = Packet();
            var view = HoldemUtterancePacketReader.Read(packet);
            Assert.That(view.Count, Is.EqualTo(1));
            Assert.That(view.ViewerSeat, Is.EqualTo(new SeatId(2)));
            Assert.That(view.GetEntry(0).Speaker, Is.EqualTo(view.ViewerSeat));
            Assert.That(view.GetEntry(0).Ordinal, Is.Zero);
            packet.ownUtterances.entries[0].text = "변경됨";
            Assert.That(view.GetEntry(0).Text, Is.EqualTo("원문 <b>그대로</b>"));
            packet.hasOwnUtterances = false;
            Assert.That(HoldemUtterancePacketReader.Read(packet), Is.Null);
        }

        [TestCase("no-game")]
        [TestCase("wrong-viewer")]
        [TestCase("empty-window")]
        [TestCase("bad-window")]
        [TestCase("wrong-window")]
        [TestCase("bad-command")]
        [TestCase("future-street")]
        [TestCase("missing-entries")]
        [TestCase("duplicate")]
        [TestCase("oversize")]
        [TestCase("empty")]
        [TestCase("control")]
        [TestCase("surrogate")]
        [TestCase("paused")]
        [TestCase("river")]
        [TestCase("no-remaining")]
        [TestCase("excess-remaining")]
        [TestCase("backlogged-open")]
        public void MalformedOwnProjectionIsRejectedBeforeRendering(string error)
        {
            var packet = Packet();
            var input = packet.ownUtterances;
            switch (error)
            {
                case "no-game": packet.hasGame = false; break;
                case "wrong-viewer": packet.viewerSeat = 1; break;
                case "empty-window": input.windowId = Guid.Empty.ToString("N"); break;
                case "bad-window": input.windowId = "bad"; break;
                case "wrong-window": input.entries[0].windowId = Guid.NewGuid().ToString("N"); break;
                case "bad-command": input.entries[0].commandId = Guid.Empty.ToString("N"); break;
                case "future-street": input.entries[0].street = 1; break;
                case "missing-entries": input.entries = null; break;
                case "duplicate": input.entries = new[] { input.entries[0], input.entries[0] }; break;
                case "oversize": input.entries[0].text = new string('a', 129); break;
                case "empty": input.entries[0].text = " "; break;
                case "control": input.entries[0].text = "a\nb"; break;
                case "surrogate": input.entries[0].text = "\ud800"; break;
                case "paused": packet.paused = true; break;
                case "river": packet.game.street = 3; break;
                case "no-remaining": input.remaining = 0; break;
                case "excess-remaining": input.remaining = 32; break;
                case "backlogged-open": input.backlogged = true; break;
            }
            Assert.Throws<ArgumentException>(() => HoldemUtterancePacketReader.Read(packet));
        }
    }
}
