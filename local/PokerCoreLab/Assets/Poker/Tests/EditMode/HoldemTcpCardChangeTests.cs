using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(3)] [TestCase(4)]
        public void ActualCardChangeCrossesSocketsWithOwnerOnlyFeedbackAndReconnect(int count)
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), utterancePolicy:
                new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw), seatCapacity: count);
            for (int i = 0; i < count; i++) Connect(i);
            for (int i = 0; i < count; i++) Assert.That(Command(i, new HoldemWireRequest { type = "ready", ready = true }).accepted, Is.True);
            var start = Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = 0 });
            Assert.That(start.accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.hasGame));
            Assert.That(Command(1, Speech(1, "오늘은 사랑이 필요하네요")).accepted, Is.True);
            while (clients[0].Latest.game.currentSeat != 0)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                var action = Command(actor, Passive(actor)); Assert.That(action.accepted, Is.True);
                Until(() => clients.Take(count).All(c => c.Latest.game.version == action.receipt.version));
            }
            var turn = server.ReadPendingTurn().Turn; var entry = turn.SourceUtterances.GetEntry(0);
            var deal = turn.TargetDeal;
            var command = new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, deal.Street, new HoldemCardChange(entry.WindowId, entry.CommandId,
                    entry.Speaker, 0, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(clients.Take(count).All(c => !c.Latest.hasOwnCardChange), Is.True);
            Assert.That(server.ApplyDealerDeal(command).Accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.game.version == turn.ExpectedVersion + 1));
            for (int i = 0; i < count; i++)
            {
                var packet = clients[i].Latest;
                Assert.That(packet.game.board[0], Is.EqualTo(45));
                Assert.That(packet.hasOwnCardChange, Is.EqualTo(i == 1));
                var display = new HoldemTableDisplay(packet);
                Assert.That(display.OwnCardChange != null, Is.EqualTo(i == 1));
                if (i == 1) Assert.That(display.OwnCardChange.EventId, Is.EqualTo(deal.WindowId));
                else Assert.That(packet.ownCardChange?.dealWindowId, Is.Null.Or.Empty);
                Assert.That(packet.publicUtterances.entries.Single().text, Is.EqualTo(entry.Text));
                Assert.That(packet.game.seats.Where(s => s.seat != packet.viewerSeat).All(s => s.visibleCards.Length == 0), Is.True);
                string json = JsonUtility.ToJson(packet).ToLowerInvariant();
                foreach (string forbidden in new[] { "sourcedeckindex", "sourcescope", "mutation", "before", "confidence", "explicitness" })
                    StringAssert.DoesNotContain(forbidden, json);
            }
            var snapshots = clients.Take(count).Select(c => JsonUtility.ToJson(c.Latest)).ToArray();
            Assert.That(server.ApplyDealerDeal(command).Accepted, Is.True); Tick();
            for (int i = 0; i < count; i++) Assert.That(JsonUtility.ToJson(clients[i].Latest), Is.EqualTo(snapshots[i]));
            clients[1].Dispose();
            Until(() => clients.Take(count).Where((_, i) => i != 1).All(c => c.Latest.paused));
            clients[1] = null; Connect(1);
            Until(() => clients.Take(count).All(c => !c.Latest.paused));
            Assert.That(clients[1].Latest.ownCardChange.dealWindowId, Is.EqualTo(deal.WindowId.ToString("N")));
            Assert.That(clients.Where((c, i) => c != null && i != 1).All(c => !c.Latest.hasOwnCardChange), Is.True);
            Assert.That(Command(1, new HoldemWireRequest { type = "apply-dealer-deal", handId = deal.HandId.ToString("N") }).error,
                Is.EqualTo("UnknownMessage"));
            var state = clients[0].Latest.game;
            Assert.That(Command(0, new HoldemWireRequest { type = "reveal", handId = state.handId,
                version = state.version, street = state.street }).accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.game.version == state.version + 1));
            Assert.That(clients.Take(count).All(c => !c.Latest.hasOwnCardChange), Is.True);
        }
    }
}
