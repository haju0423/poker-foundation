using System;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemLobbyDisplayTests
    {
        private static HoldemRoomPacket Packet()
        {
            var packet = new HoldemRoomPacket { protocol = HoldemRoomPacketMapper.ProtocolVersion,
                sessionId = Guid.NewGuid().ToString("N"), viewerSeat = 1, revision = 4, members = new HoldemRoomMemberPacket[4] };
            for (int i = 0; i < 4; i++) packet.members[i] = new HoldemRoomMemberPacket {
                seat = i + 1, name = "참가자 " + (i + 1), connected = true, ready = true, host = i == 0 };
            return packet;
        }
        [Test]
        public void PublicLobbyViewIsADetachedCopyAndSortsSeats()
        {
            var packet = Packet(); Array.Reverse(packet.members);
            var display = new HoldemLobbyDisplay(packet);
            packet.members[3].name = "변경"; packet.members[3].host = false; packet.members[3].ready = false;
            Assert.That(display.IsHost, Is.True); Assert.That(display.AllReady, Is.True);
            Assert.That(display.GetMember(0).Name, Is.EqualTo("참가자 1"));
            Assert.That(packet.members[0].seat, Is.EqualTo(4));
        }

        [Test]
        public void RoomRulesAreDetachedAndLegacyPacketsDoNotInventDefaults()
        {
            var packet = Packet();
            packet.rules = new HoldemRoomRulesPacket { startingStack = 250, smallBlind = 5, bigBlind = 10,
                waitsForHostDeal = true, pausesAfterReveal = true, receivesUtterances = true };
            Assert.That(new HoldemLobbyDisplay(packet).Rules, Is.Null, "The presence flag, not a deserializer-created object, controls support.");
            packet.hasRules = true;
            var rules = new HoldemLobbyDisplay(packet).Rules;
            packet.rules.startingStack = 999; packet.rules.receivesUtterances = false;
            Assert.That(rules.StartingStack, Is.EqualTo(250)); Assert.That(rules.SmallBlind, Is.EqualTo(5));
            Assert.That(rules.BigBlind, Is.EqualTo(10)); Assert.That(rules.ReceivesUtterances, Is.True);
            Assert.That(rules.WaitsForHostDeal && rules.PausesAfterReveal, Is.True);
            Assert.That(rules.Matches(new HoldemLobbyDisplay(packet).Rules), Is.False);
        }

        [TestCase("missing")] [TestCase("stack")] [TestCase("small")] [TestCase("big")] [TestCase("intake")]
        public void InvalidRoomRulesAreRejectedBeforeDisplay(string change)
        {
            var packet = Packet(); packet.hasRules = true;
            packet.rules = new HoldemRoomRulesPacket { startingStack = 100, smallBlind = 1, bigBlind = 2 };
            if (change == "missing") packet.rules = null;
            else if (change == "stack") packet.rules.startingStack = 0;
            else if (change == "small") packet.rules.smallBlind = 0;
            else if (change == "big") packet.rules.bigBlind = 0;
            else { packet.hasGame = true; packet.hasOwnUtterances = true; }
            Assert.That(() => new HoldemLobbyDisplay(packet), Throws.InstanceOf<ArgumentException>());
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void IncompleteDisconnectedUnreadyOrPausedRoomCannotStart(int change)
        {
            var packet = Packet();
            if (change == 0) Array.Resize(ref packet.members, 3);
            if (change == 1) packet.members[3].connected = false;
            if (change == 2) packet.members[3].ready = false;
            if (change == 3) packet.paused = true;
            Assert.That(new HoldemLobbyDisplay(packet).AllReady, Is.False);
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void InvalidPublicMembershipIsRejected(int change)
        {
            var packet = Packet();
            if (change == 0) packet.members[1].host = true;
            if (change == 1) packet.members[0].host = false;
            if (change == 2) packet.members[2].seat = 2;
            if (change == 3) packet.members[2].name = "줄\n바꿈";
            if (change == 4) packet.members[0] = null;
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(packet));
        }
    }
}
