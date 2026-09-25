using System;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        [Test]
        public void PublicOnlyRoomCannotChangeCardsByPassingAKnownUtteranceDirectly()
        {
            Create(policy: new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active,
                HoldemUtteranceVisibility.PublicRaw, HoldemUtteranceBatchRetention.None));
            Start();
            var speech = room.ReadUtterances(peers[1]); var utteranceId = Guid.NewGuid();
            Assert.That(room.SubmitUtterance(peers[1], new HoldemRoomUtterance(speech.SessionId,
                speech.HandId, speech.WindowId, utteranceId, speech.Street, "공개 대화만 사용")).Accepted, Is.True);
            EndBetting();
            var turn = Read(); var d = turn.TargetDeal; long version = State.SessionVersion;
            Assert.That(turn.InputKind, Is.EqualTo(HoldemDealerInputKind.Disabled));
            var changed = new HoldemDealCommand(d.SessionId, d.HandId, d.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, d.Street, new HoldemCardChange(speech.WindowId, utteranceId,
                    speech.ViewerSeat, 0, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(room.ApplyDealerDeal(peers[0], changed).Poker.Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(State.SessionVersion, Is.EqualTo(version));
            Assert.That(State.BoardCount, Is.Zero);
            Assert.That(room.ApplyDealerDeal(peers[0], turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            Assert.That(State.BoardCount, Is.EqualTo(3));
            Assert.That(room.Read(peers[1]).OwnCardChange, Is.Null);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)] [TestCase(9)]
        public void OwnerFeedbackPacketRejectsWrongHandStreetSlotCardOrPhase(int changed)
        {
            Create(); Start(); Speak(2, "테스트 멘트"); EndBetting();
            var turn = Read(); var entry = turn.SourceUtterances.GetEntry(0); var d = turn.TargetDeal;
            var command = new HoldemDealCommand(d.SessionId, d.HandId, d.WindowId, Guid.NewGuid(), turn.ExpectedVersion,
                d.Street, new HoldemCardChange(entry.WindowId, entry.CommandId, entry.Speaker, 0,
                    Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True);
            var packet = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[1]))));
            Assert.That(new HoldemTableDisplay(packet).OwnCardChange.Card.Id, Is.EqualTo(45));
            switch (changed)
            {
                case 0: packet.ownCardChange.handId = Guid.NewGuid().ToString("N"); break;
                case 1: packet.ownCardChange.street = 2; break;
                case 2: packet.ownCardChange.boardIndex = 4; break;
                case 3: packet.ownCardChange.card = 46; break;
                case 4: packet.game.revealPending = false; break;
                case 5: packet.hasRules = false; break;
                case 6: packet.rules.waitsForHostDeal = false; break;
                case 7: packet.rules.pausesAfterReveal = false; break;
                case 8: packet.rules.receivesUtterances = false; break;
                case 9: packet.hasOwnUtterances = false; break;
            }
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(packet));
        }

        [Test]
        public void RoomRequiresActualUtteranceAttributionAndHostBeforeChangingCards()
        {
            Create(); Start(); Speak(2, "하트가 필요해"); EndBetting();
            var turn = Read(); var source = turn.SourceUtterances.GetEntry(0); var deal = turn.TargetDeal;
            var change = new HoldemCardChange(source.WindowId, source.CommandId, source.Speaker, 0, Card.FromId(45),
                HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            var command = new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, deal.Street, change);
            long before = State.SessionVersion;
            Assert.That(room.ApplyDealerDeal(peers[1], command).Error, Is.EqualTo(Application.HoldemRoomError.HostOnly));
            var forged = new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, deal.Street, new HoldemCardChange(source.WindowId, source.CommandId,
                    new SeatId(3), 0, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(room.ApplyDealerDeal(peers[0], forged).Poker.Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(State.SessionVersion, Is.EqualTo(before));
            Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True);
            long committed = State.SessionVersion;
            Assert.That(room.ApplyDealerDeal(peers[0], command).Accepted, Is.True);
            Assert.That(State.SessionVersion, Is.EqualTo(committed));
            Assert.That(room.Read(peers[1]).OwnCardChange.Card, Is.EqualTo(Card.FromId(45)));
            Assert.That(room.Read(peers[0]).OwnCardChange, Is.Null);
            Assert.That(room.Read(peers[2]).OwnCardChange, Is.Null);
            Assert.That(room.Read(peers[3]).OwnCardChange, Is.Null);
            room.Disconnect(peers[1]); peers[1] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(room.Read(peers[1]).OwnCardChange.Card, Is.EqualTo(Card.FromId(45)));
            Resume();
            Assert.That(room.Read(peers[1]).OwnCardChange, Is.Null);
        }
    }
}
