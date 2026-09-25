using System;
using NUnit.Framework;
using Poker.Application;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemPublicUtteranceTests
    {
        [TestCase(3)] [TestCase(4)]
        public void PublicRemarksKeepHostBettingOrderAcrossRetriesWireCopiesAndReconnect(int capacity)
        {
            Create(capacity: capacity); Start();
            var command = Say(2, "행동 전");
            var before = HoldemPublicUtterancePacketReader.Read(Packet());
            Act(false); Say(3, "행동 후");
            Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            for (int i = 1; i <= capacity; i++)
            {
                var packet = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(Packet(i)));
                var speech = HoldemPublicUtterancePacketReader.Read(packet, before);
                Assert.That(speech.HasHistoryOrder, Is.True);
                Assert.That(speech.Count, Is.EqualTo(2));
                Assert.That(speech.GetEntry(0).HistoryPosition, Is.EqualTo(2));
                Assert.That(speech.GetEntry(1).HistoryPosition, Is.EqualTo(3));
            }
            room.Disconnect(peers[1]); peers[1] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(2), before).GetEntry(0).HistoryPosition, Is.EqualTo(2));
            Assert.That(before.Count, Is.EqualTo(1));
        }

        [Test]
        public void SuccessfulNewHandAloneResetsRemarkOrder()
        {
            Create();
            var state = room.Read(peers[0]);
            var start = new HoldemStartCommand(state.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0);
            Assert.That(room.StartHand(peers[0], start).Accepted, Is.True);
            Say(2, "시작 재전송에도 유지");
            Assert.That(room.StartHand(peers[0], start).Accepted, Is.True);
            var current = room.Read(peers[0]).Game;
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(current.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), current.SessionVersion)).Accepted, Is.False);
            var before = HoldemPublicUtterancePacketReader.Read(Packet());
            Assert.That(before.Count, Is.EqualTo(1));
            while (room.Read(peers[0]).Game.Result == null) Act(true);
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet(), before).Count, Is.EqualTo(1));
            Start();
            var after = HoldemPublicUtterancePacketReader.Read(Packet(), before);
            Assert.That(after.HasHistoryOrder, Is.True); Assert.That(after.Count, Is.Zero);
            Say(3, "새 판");
            Assert.That(HoldemPublicUtterancePacketReader.Read(Packet()).GetEntry(0).HistoryPosition, Is.EqualTo(2));
        }

        [TestCase("negative")] [TestCase("before-blinds")] [TestCase("future")]
        [TestCase("decreasing")] [TestCase("rewrite")] [TestCase("missing-history")]
        [TestCase("downgrade")] [TestCase("unmarked-anchor")]
        public void InvalidPublicHistoryAnchorIsRejected(string fault)
        {
            Create(); Start(); Say(2, "첫 말"); Act(false); Say(3, "둘째 말");
            var packet = Packet(); var before = HoldemPublicUtterancePacketReader.Read(packet);
            switch (fault)
            {
                case "negative": packet.publicUtterances.entries[0].historyPosition = -1; break;
                case "before-blinds": packet.publicUtterances.entries[0].historyPosition = 1; break;
                case "future": packet.publicUtterances.entries[1].historyPosition = long.MaxValue; break;
                case "decreasing": packet.publicUtterances.entries[0].historyPosition = 3;
                    packet.publicUtterances.entries[1].historyPosition = 2; break;
                case "rewrite": packet.publicUtterances.entries[0].historyPosition = 3; break;
                case "missing-history": packet.hasHistory = false; break;
                case "downgrade": packet.publicUtterances.hasHistoryOrder = false;
                    foreach (var entry in packet.publicUtterances.entries) entry.historyPosition = 0; break;
                case "unmarked-anchor": packet.publicUtterances.hasHistoryOrder = false; before = null; break;
            }
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet, before));
        }

        [Test]
        public void LegacyOrderStaysUnknownAndCannotBeAddedHalfwayThroughSession()
        {
            Create(); Start(); Say(2, "예전 서버 멘트");
            var packet = Packet(); packet.publicUtterances.hasHistoryOrder = false;
            packet.publicUtterances.entries[0].historyPosition = 0;
            var legacy = HoldemPublicUtterancePacketReader.Read(packet);
            Assert.That(legacy.HasHistoryOrder, Is.False);
            Assert.That(legacy.GetEntry(0).HistoryPosition, Is.Null);
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(Packet(), legacy));
            while (room.Read(peers[0]).Game.Result == null) Act(true);
            Start();
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(Packet(), legacy));
        }

        [Test]
        public void AnchorsAgreeWithBothAdjacentBettingStreets()
        {
            Create(); Start(); Say(2, "프리플랍");
            for (int i = 0; i < 4; i++) Act(false);
            Say(3, "플랍"); Act(false);
            var packet = Packet();
            var speech = HoldemPublicUtterancePacketReader.Read(packet);
            Assert.That(speech.GetEntry(1).HistoryPosition, Is.EqualTo(6));
            packet.publicUtterances.entries[1].historyPosition = 5;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
            packet = Packet(); packet.publicUtterances.entries[0].historyPosition = 7;
            packet.publicUtterances.entries[1].historyPosition = 7;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
            packet = Packet(); packet.publicUtterances.entries[0].historyPosition = 6;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet),
                "A preflop remark cannot arrive after the action that closed preflop.");
        }

        [Test]
        public void FinalFoldCannotAcquireAPostResultRemark()
        {
            Create(); Start(); Say(2, "종료 전 멘트");
            while (room.Read(peers[0]).Game.Result == null) Act(true);
            var packet = Packet();
            Assert.That(HoldemPublicUtterancePacketReader.Read(packet).Count, Is.EqualTo(1));
            packet.publicUtterances.entries[0].historyPosition = packet.history.entries.Length;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
        }

        [TestCase(false)] [TestCase(true)]
        public void ClosingRoundCannotAcquireAnEarlierStreetRemarkAtHistoryEnd(bool manualDeal)
        {
            Create(manualDeal: manualDeal); Start(); Say(2, "공개 전 멘트");
            for (int i = 0; i < 4; i++) Act(false);
            var packet = Packet();
            Assert.That(HoldemPublicUtterancePacketReader.Read(packet).Count, Is.EqualTo(1));
            packet.publicUtterances.entries[0].historyPosition = packet.history.entries.Length;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
            if (manualDeal)
            {
                var game = room.Read(peers[0]).Game;
                Assert.That(room.DealUnchanged(peers[0], new HoldemDealCommand(game.SessionId, game.HandId,
                    game.PendingDeal.WindowId, Guid.NewGuid(), game.SessionVersion, game.PendingDeal.Street)).Accepted, Is.True);
            }
            Say(3, "새 단계의 멘트");
            var next = HoldemPublicUtterancePacketReader.Read(Packet());
            Assert.That(next.GetEntry(1).Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(next.GetEntry(1).HistoryPosition, Is.EqualTo(6));
        }

        [Test]
        public void RemarkCannotSplitAnActionFromItsUncalledReturn()
        {
            Create(); Start();
            var game = room.Read(peers[3]).Game;
            Assert.That(room.Submit(peers[3], new HoldemRoomAction(game.SessionId, game.HandId,
                Guid.NewGuid(), game.SessionVersion, BettingAction.RaiseTo(100))).Accepted, Is.True);
            Say(2, "올인 뒤 폴드 전");
            while (room.Read(peers[0]).Game.Result == null) Act(true);
            var packet = Packet();
            Assert.That(packet.history.entries[packet.history.entries.Length - 1].kind, Is.EqualTo((int)HoldemHistoryKind.UncalledReturn));
            Assert.That(HoldemPublicUtterancePacketReader.Read(packet).GetEntry(0).HistoryPosition, Is.EqualTo(3));
            packet.publicUtterances.entries[0].historyPosition = packet.history.entries.Length - 1;
            Assert.Throws<ArgumentException>(() => HoldemPublicUtterancePacketReader.Read(packet));
        }

        [Test]
        public void HistoryTailTrimmingDoesNotReanchorEarlierRemarks()
        {
            Create(startingStack: 1000); Start(); Say(2, "처음 멘트");
            var before = HoldemPublicUtterancePacketReader.Read(Packet());
            for (int i = 0; i < 100; i++)
            {
                var game = room.Read(peers[0]).Game; int actor = game.CurrentSeat.Value.Value - 1;
                game = room.Read(peers[actor]).Game;
                Assert.That(room.Submit(peers[actor], new HoldemRoomAction(game.SessionId, game.HandId,
                    Guid.NewGuid(), game.SessionVersion, BettingAction.RaiseTo(game.LegalActions.MinimumAggressiveTarget.Value))).Accepted, Is.True);
            }
            Say(3, "최근 멘트");
            var packet = Packet(); var after = HoldemPublicUtterancePacketReader.Read(packet, before);
            Assert.That(packet.history.omittedCount, Is.EqualTo(38));
            Assert.That(after.GetEntry(0).HistoryPosition, Is.EqualTo(2));
            Assert.That(after.GetEntry(1).HistoryPosition, Is.EqualTo(102));
        }
    }
}
