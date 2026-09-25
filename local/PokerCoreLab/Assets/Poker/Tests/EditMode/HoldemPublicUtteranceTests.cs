using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemPublicUtteranceTests
    {
        private Guid[] peers;
        private HoldemRoomAdmission[] admissions;
        private HoldemRoom room;
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private void Create(HoldemUtteranceVisibility visibility = HoldemUtteranceVisibility.PublicRaw, int capacity = 4,
            long startingStack = 100, bool manualDeal = false)
        {
            peers = Enumerable.Range(0, capacity).Select(_ => Guid.NewGuid()).ToArray();
            admissions = new HoldemRoomAdmission[capacity];
            room = HoldemRoom.Create(Guid.NewGuid(), peers[0], "방장", new HoldemConfig(startingStack, 1, 2,
                dealPolicy: manualDeal ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic), new SeatId(1),
                new StableRandom(), out admissions[0], utterancePolicy: new HoldemUtterancePolicy(128, 2,
                    HoldemUtteranceSeats.Active, visibility), seatCapacity: capacity);
            for (int i = 1; i < capacity; i++) admissions[i] = room.Join(peers[i], "참가자").Admission;
            foreach (var peer in peers) room.SetReady(peer, true);
        }
        private void Start()
        {
            var state = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(state.SessionId, Guid.NewGuid(), Guid.NewGuid(),
                state.Game?.SessionVersion ?? 0)).Accepted, Is.True);
        }
        private HoldemRoomUtterance Say(int seat, string text)
        {
            var view = room.ReadUtterances(peers[seat - 1]);
            var command = new HoldemRoomUtterance(view.SessionId, view.HandId, view.WindowId, Guid.NewGuid(), view.Street, text);
            Assert.That(room.SubmitUtterance(peers[seat - 1], command).Accepted, Is.True);
            return command;
        }
        private void Act(bool fold)
        {
            var state = room.Read(peers[0]).Game;
            var peer = peers[state.CurrentSeat.Value.Value - 1]; state = room.Read(peer).Game;
            Assert.That(room.Submit(peer, new HoldemRoomAction(state.SessionId, state.HandId, Guid.NewGuid(), state.SessionVersion,
                fold ? BettingAction.Fold() : state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
        }
        private HoldemRoomPacket Packet(int seat = 1) => HoldemRoomPacketMapper.Create(room.Read(peers[seat - 1]));

        [TestCase(3)] [TestCase(4)]
        public void OptInPublishesOnlyAcceptedRawTextWithoutChangingPokerOrOwnConfirmation(int capacity)
        {
            Create(capacity: capacity);
            Assert.That(Packet().rules.publishesUtterances, Is.True);
            Assert.That(Packet().hasPublicUtterances, Is.False);
            Start();
            var before = Packet();
            Assert.That(before.hasPublicUtterances, Is.True);
            Assert.That(HoldemPublicUtterancePacketReader.Read(before).Count, Is.Zero);
            var command = Say(2, "<b>사랑</b> \\\" ♥ 원문😀");
            Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            for (int seat = 1; seat <= capacity; seat++)
            {
                var packet = Packet(seat); var view = HoldemPublicUtterancePacketReader.Read(packet);
                Assert.That(view.Count, Is.EqualTo(1));
                Assert.That(view.GetEntry(0).Speaker.Value, Is.EqualTo(2));
                Assert.That(view.GetEntry(0).Text, Is.EqualTo(command.Text));
                Assert.That(packet.ownUtterances.entries.Length, Is.EqualTo(seat == 2 ? 1 : 0));
                Assert.That(packet.game.version, Is.EqualTo(before.game.version));
                Assert.That(packet.game.pot, Is.EqualTo(before.game.pot));
                Assert.That(packet.game.currentSeat, Is.EqualTo(before.game.currentSeat));
                packet.publicUtterances.entries[0].text = "변경";
                Assert.That(view.GetEntry(0).Text, Is.EqualTo(command.Text));
            }
            Assert.That(typeof(HoldemPublicUtteranceEntryPacket).GetFields().Select(f => f.Name),
                Is.EquivalentTo(new[] { "seat", "street", "text", "historyPosition" }));
            Assert.That(typeof(HoldemPublicUtterance).GetProperties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "Speaker", "Street", "Text", "HistoryPosition" }));
        }

        [Test]
        public void DefaultOwnOnlyRemainsPrivateAndRoomRuleCannotSilentlyChange()
        {
            Assert.That(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active).Visibility, Is.EqualTo(HoldemUtteranceVisibility.OwnOnly));
            Create(HoldemUtteranceVisibility.OwnOnly); Start(); Say(2, "비공개 접수");
            Assert.That(Packet().hasPublicUtterances, Is.False);
            Assert.That(Packet().publicUtterances, Is.Null);
            Assert.That(Packet().ownUtterances.entries, Is.Empty);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet()), Is.Null);
            var own = new HoldemRoomRules(new HoldemConfig(100, 1, 2), true);
            Assert.That(own.Matches(new HoldemRoomRules(new HoldemConfig(100, 1, 2), true, 4, true)), Is.False);
            Assert.Throws<ArgumentException>(() => new HoldemRoomRules(new HoldemConfig(100, 1, 2), false, 4, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                (HoldemUtteranceVisibility)999));
        }

        [Test]
        public void PublicRemarksSurviveAckPauseReconnectSettlementAndResetOnlyOnNextHand()
        {
            Create(); Start(); Say(2, "프리플랍 멘트");
            var first = HoldemPublicUtterancePacketReader.Read(Packet());
            for (int i = 0; i < 4; i++) Act(false);
            Say(3, "플랍 멘트");
            var batches = room.ReadClosedUtteranceBatches(peers[0]);
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], batches[0].WindowId), Is.True);
            var second = HoldemPublicUtterancePacketReader.Read(Packet(), first);
            Assert.That(second.Count, Is.EqualTo(2));
            room.Disconnect(peers[1]);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(), second).Count, Is.EqualTo(2));
            peers[1] = Guid.NewGuid(); Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(2)).Count, Is.EqualTo(2));
            while (room.Read(peers[0]).Game.Result == null) Act(true);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(), second).Count, Is.EqualTo(2));
            Start();
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(), second).Count, Is.Zero);
            Assert.That(first.Count, Is.EqualTo(1)); Assert.That(second.Count, Is.EqualTo(2));
        }

        [TestCase("missing-flag")] [TestCase("missing-rules")] [TestCase("private-rule")]
        [TestCase("missing-entries")] [TestCase("bad-seat")] [TestCase("undealt")]
        [TestCase("future-street")] [TestCase("empty")] [TestCase("control")]
        [TestCase("surrogate")] [TestCase("too-long")] [TestCase("too-many")]
        [TestCase("rewrite")] [TestCase("truncate")] [TestCase("own-contradiction")]
        public void MalformedOrRewrittenPublicFeedIsRejectedBeforeRendering(string error)
        {
            Create(); Start(); Say(2, "원문");
            var packet = Packet(); var before = HoldemPublicUtterancePacketReader.Read(packet);
            switch (error)
            {
                case "missing-flag": packet.hasPublicUtterances = false; break;
                case "missing-rules": packet.hasRules = false; break;
                case "private-rule": packet.rules.publishesUtterances = false; break;
                case "missing-entries": packet.publicUtterances.entries = null; break;
                case "bad-seat": packet.publicUtterances.entries[0].seat = 5; break;
                case "undealt": packet.game.seats[1].dealtIn = false; break;
                case "future-street": packet.publicUtterances.entries[0].street = 1; break;
                case "empty": packet.publicUtterances.entries[0].text = " "; break;
                case "control": packet.publicUtterances.entries[0].text = "x\ny"; break;
                case "surrogate": packet.publicUtterances.entries[0].text = "\ud800"; break;
                case "too-long": packet.publicUtterances.entries[0].text = new string('가', 129); break;
                case "too-many": packet.publicUtterances.entries = Enumerable.Repeat(packet.publicUtterances.entries[0], 33).ToArray(); break;
                case "rewrite": packet.publicUtterances.entries[0].text = "다른 말"; break;
                case "truncate": packet.publicUtterances.entries = new HoldemPublicUtteranceEntryPacket[0]; break;
                case "own-contradiction": packet = Packet(2); packet.publicUtterances.entries[0].text = "안 보낸 말"; break;
            }
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet, before));
            Assert.That(before.GetEntry(0).Text, Is.EqualTo("원문"));
        }
    }
}
