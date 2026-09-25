using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        private void CreateAccusationPreview(int count = 4)
        {
            Cleanup();
            Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses, HoldemDealPolicy.WaitForHost),
                utterancePolicy: new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                    HoldemUtteranceVisibility.PublicRaw), seatCapacity: count,
                accusationEvidenceScope: HoldemAccusationEvidenceScope.CurrentRevealOnly);
        }

        private void BeginAccusationPreview(int count, bool changed)
        {
            CreateAccusationPreview(count);
            for (int i = 0; i < count; i++) Connect(i);
            for (int i = 0; i < count; i++) Assert.That(Command(i, new HoldemWireRequest { type = "ready", ready = true }).accepted, Is.True);
            Assert.That(Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N") }).accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.hasGame));
            Assert.That(Command(1, Speech(1, "오늘은 사랑이 필요하네요")).accepted, Is.True);
            FinishAccusationBetting(count);
            var turn = server.ReadPendingTurn().Turn; var deal = turn.TargetDeal;
            HoldemCardChange change = null;
            if (changed)
            {
                var entry = turn.SourceUtterances.GetEntry(0);
                change = new HoldemCardChange(entry.WindowId, entry.CommandId, entry.Speaker, 0,
                    Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            }
            Assert.That(server.ApplyDealerDeal(new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId,
                Guid.NewGuid(), turn.ExpectedVersion, deal.Street, change)).Accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.game.revealPending));
        }

        private void FinishAccusationBetting(int count)
        {
            for (int n = 0; n < 20 && clients[0].Latest.game.currentSeat != 0; n++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                var ack = Command(actor, Passive(actor)); Assert.That(ack.accepted, Is.True);
                Until(() => clients.Take(count).All(c => c.Latest.game.version == ack.receipt.version));
            }
            Assert.That(clients[0].Latest.game.dealPending, Is.True);
        }

        private HoldemWireRequest Accusation(int index, int target)
        {
            var g = clients[index].Latest.game;
            return new HoldemWireRequest { type = "accusation", id = Guid.NewGuid().ToString("N"),
                handId = g.handId, windowId = g.accusations.windowId, version = g.version,
                street = g.street, accusationTarget = target };
        }

        private HoldemWireResponse Choose(int count, int index, int target)
        {
            var receipt = Command(index, Accusation(index, target)); Assert.That(receipt.accepted, Is.True, receipt.error);
            Until(() => clients.Take(count).All(c => c.Latest.game.version == receipt.receipt.version));
            return receipt;
        }

        private HoldemRoomAccusationClose CloseInput()
        {
            var p = clients[0].Latest; var g = p.game;
            return new HoldemRoomAccusationClose(Guid.ParseExact(p.sessionId, "N"), Guid.ParseExact(g.handId, "N"),
                Guid.ParseExact(g.accusations.windowId, "N"), Guid.NewGuid(), g.version, (HoldemStreet)g.street);
        }

        [TestCase(3, true)] [TestCase(4, true)] [TestCase(3, false)] [TestCase(4, false)]
        public void AccusationsUseAppliedCardRecordAndReturnOnlyTheAccusersOwnResult(int count, bool changed)
        {
            BeginAccusationPreview(count, changed);
            var basis = clients[0].Latest;
            var stacks = basis.game.seats.Select(s => s.stack).ToArray();
            var statuses = basis.game.seats.Select(s => s.status).ToArray();
            var board = basis.game.board.ToArray(); long pot = basis.game.pot;
            string history = JsonUtility.ToJson(basis.history);
            Assert.That(new HoldemLobbyDisplay(basis).Rules.AccusationsEnabled, Is.True);
            Assert.That(server.CloseAccusations(CloseInput()).Poker.Error, Is.EqualTo(HoldemCommandError.AccusationResponsesPending));
            for (int i = 0; i < count; i++) Choose(count, i, i == 0 ? 2 : i == 2 ? 1 : 0);
            for (int i = 0; i < count; i++)
            {
                var a = new HoldemTableDisplay(clients[i].Latest).Accusations;
                Assert.That(a.OwnVerdict, Is.Null);
                Assert.That(a.OwnTarget?.Value ?? 0, Is.EqualTo(i == 0 ? 2 : i == 2 ? 1 : 0));
            }
            var close = CloseInput(); Assert.That(server.CloseAccusations(close).Accepted, Is.True);
            Until(() => clients.Take(count).All(c => c.Latest.game.accusations.phase == 3));
            Assert.That(clients.Take(count).All(c => !c.Latest.game.accusations.hasOwnVerdict), Is.True);
            var result = server.ResolveAccusations();
            Assert.That(result.Error, Is.EqualTo(HoldemRoomError.None));
            Assert.That(result.RecordedCount, Is.EqualTo(2)); Assert.That(result.PendingCount, Is.Zero);
            Until(() => clients.Take(count).All(c => c.Latest.game.accusations.phase == 4));
            for (int i = 0; i < count; i++)
            {
                var p = clients[i].Latest; var view = new HoldemTableDisplay(p);
                Assert.That(view.Accusations.OwnVerdict, Is.EqualTo(i == 0 ? (bool?)changed : i == 2 ? false : (bool?)null));
                Assert.That(p.hasOwnCardChange, Is.EqualTo(changed && i == 1));
                Assert.That(p.game.board, Is.EqualTo(board)); Assert.That(p.game.pot, Is.EqualTo(pot));
                Assert.That(p.game.seats.Select(s => s.stack), Is.EqualTo(stacks));
                Assert.That(p.game.seats.Select(s => s.status), Is.EqualTo(statuses));
                Assert.That(JsonUtility.ToJson(p.history), Is.EqualTo(history));
                Assert.That(p.game.seats.Where(s => s.seat != p.viewerSeat).All(s => s.visibleCards.Length == 0), Is.True);
                string json = JsonUtility.ToJson(p).ToLowerInvariant();
                foreach (string forbidden in new[] { "claimid", "decisions", "mutation", "confidence", "explicitness", "sourcedeckindex" })
                    StringAssert.DoesNotContain(forbidden, json);
            }
            long revision = clients[0].Latest.revision;
            Assert.That(server.ResolveAccusations().RecordedCount, Is.Zero);
            Assert.That(server.CloseAccusations(close).Accepted, Is.True);
            Tick(); Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            var game = clients[0].Latest.game;
            Assert.That(Command(0, new HoldemWireRequest { type = "reveal", handId = game.handId,
                version = game.version, street = game.street }).receipt.error, Is.EqualTo("AccusationConsequencesPending"));
            foreach (string forbidden in new[] { "accusation-close", "accusation-verdict", "resolve-accusations" })
                Assert.That(Command(0, new HoldemWireRequest { type = forbidden, handId = game.handId }).error, Is.EqualTo("UnknownMessage"));
            clients[0].Dispose(); Until(() => clients[1].Latest.paused);
            clients[0] = null; Connect(0); Until(() => clients.Take(count).All(c => !c.Latest.paused));
            Assert.That(new HoldemTableDisplay(clients[0].Latest).Accusations.OwnVerdict, Is.EqualTo(changed));
            Assert.That(new HoldemTableDisplay(clients[1].Latest).Accusations.OwnVerdict, Is.Null);
        }

        [Test]
        public void AccusationRetriesDoNotDuplicateAndConnectionsCannotChooseForAnotherSeat()
        {
            BeginAccusationPreview(4, false);
            var input = Accusation(1, 3); var ack = Command(1, input); Assert.That(ack.accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == ack.receipt.version));
            long revision = clients[1].Latest.revision;
            Assert.That(Command(1, input).receipt.version, Is.EqualTo(ack.receipt.version)); Tick();
            Assert.That(clients[1].Latest.revision, Is.EqualTo(revision));
            Assert.That(Command(0, input).receipt.error, Is.EqualTo("CommandConflict"));
            input.accusationTarget = 4;
            Assert.That(Command(1, input).receipt.error, Is.EqualTo("CommandConflict"));
            input.accusationTarget = 3;
            Choose(4, 1, 4);
            Assert.That(clients[1].Latest.game.accusations.responseCount, Is.EqualTo(1));
            Assert.That(Command(1, input).accepted, Is.True); Tick();
            Assert.That(clients[1].Latest.game.accusations.ownTarget, Is.EqualTo(4));
            Assert.That(clients.Where((_, i) => i != 1).All(c => !c.Latest.game.accusations.hasOwnTarget), Is.True);
            Assert.That(Command(1, Accusation(1, 2)).receipt.error, Is.EqualTo("InvalidAccusationTarget"));
            var stale = Accusation(1, 3); stale.windowId = Guid.NewGuid().ToString("N");
            Assert.That(Command(1, stale).receipt.error, Is.EqualTo("WrongRevealWindow"));
            clients[1].Dispose(); Until(() => clients[0].Latest.paused);
            Assert.That(server.ResolveAccusations().Error, Is.EqualTo(HoldemRoomError.Paused));
            clients[1] = null; Connect(1); Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(Command(1, input).accepted, Is.True);
            Assert.That(clients[1].Latest.game.accusations.ownTarget, Is.EqualTo(4));
            Assert.That(clients[1].Latest.game.accusations.responseCount, Is.EqualTo(1));
        }

        [TestCase(3)] [TestCase(4)]
        public void AllPassNeedsExplicitCloseAndResumeOnEveryReveal(int count)
        {
            BeginAccusationPreview(count, false);
            string previousWindow = null;
            for (int street = 1; street <= 3; street++)
            {
                var g = clients[0].Latest.game;
                Assert.That(g.street, Is.EqualTo(street));
                Assert.That(g.accusations.windowId, Is.Not.EqualTo(previousWindow));
                if (previousWindow != null)
                {
                    var old = Accusation(0, 2); old.windowId = previousWindow;
                    Assert.That(Command(0, old).receipt.error, Is.EqualTo("WrongRevealWindow"));
                }
                previousWindow = g.accusations.windowId;
                for (int i = 0; i < count; i++) Choose(count, i, 0);
                var close = CloseInput(); Assert.That(server.CloseAccusations(close).Accepted, Is.True);
                Until(() => clients.Take(count).All(c => c.Latest.game.accusations.phase == 2));
                Assert.That(server.ResolveAccusations().RecordedCount, Is.Zero);
                g = clients[0].Latest.game;
                Assert.That(Command(0, Accusation(0, 2)).receipt.error, Is.EqualTo("AccusationWindowClosed"));
                var resumed = Command(0, new HoldemWireRequest { type = "reveal", handId = g.handId,
                    version = g.version, street = g.street }); Assert.That(resumed.accepted, Is.True);
                Until(() => clients.Take(count).All(c => c.Latest.game.version == resumed.receipt.version));
                if (street == 3) break;
                FinishAccusationBetting(count);
                var turn = server.ReadPendingTurn().Turn; var d = turn.TargetDeal;
                Assert.That(server.DealUnchanged(new HoldemDealCommand(d.SessionId, d.HandId, d.WindowId,
                    Guid.NewGuid(), turn.ExpectedVersion, d.Street)).Accepted, Is.True);
                Until(() => clients.Take(count).All(c => c.Latest.game.revealPending));
            }
        }

        [Test]
        public void LegacyAccusationClientIsRejectedBeforeSeatReservation()
        {
            CreateAccusationPreview(); Connect(0);
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            var request = (HoldemWireRequest)typeof(HoldemTcpClient).GetField("admissionRequest", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[1]);
            request.supportsAccusations = false;
            Until(() => clients[1].AdmissionError != null);
            Assert.That(clients[1].AdmissionError, Is.EqualTo("ClientUpgradeRequired"));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
            Assert.That(identities[1].Seat, Is.Zero);
            clients[1].Dispose(); Connect(1);
            Assert.That(clients[1].Identity.Seat, Is.EqualTo(2));
        }

        [Test]
        public void AccusationPacketsRejectInvalidTargetsResultsAndFrozenStateChanges()
        {
            BeginAccusationPreview(4, false);
            var original = clients[0].Latest;
            Action<Action<HoldemRoomPacket>> invalid = mutate =>
            {
                var p = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(original)); mutate(p);
                Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(p));
            };
            invalid(p => p.game.accusations.targets = new[] { 1, 2, 3 });
            invalid(p => p.game.accusations.targets = new[] { 2, 2, 3 });
            invalid(p => p.game.accusations.responseCount = 5);
            invalid(p => p.game.accusations.hasOwnVerdict = true);
            invalid(p => p.game.accusations.ownVerdict = true);
            invalid(p => p.game.hasAccusations = false);
            invalid(p => p.rules.accusationsEnabled = false);
            invalid(p => p.game.accusations.ownTarget = 2);
            invalid(p => { p.rules.receivesUtterances = false; p.hasOwnUtterances = false; });
            var noIntake = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(original));
            noIntake.rules.receivesUtterances = false; noIntake.hasOwnUtterances = false;
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(noIntake));
            Choose(4, 0, 2); for (int i = 1; i < 4; i++) Choose(4, i, 0);
            Assert.That(server.CloseAccusations(CloseInput()).Accepted, Is.True);
            Until(() => clients[0].Latest.game.accusations.phase == 3);
            var closed = new HoldemTableDisplay(clients[0].Latest);
            var tampered = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(clients[0].Latest));
            tampered.game.accusations.ownTarget = 3;
            Assert.Throws<ArgumentException>(() => HoldemAccusationDisplay.ValidateTransition(closed, new HoldemTableDisplay(tampered)));
            server.ResolveAccusations(); Until(() => clients[0].Latest.game.accusations.phase == 4);
            var recorded = new HoldemTableDisplay(clients[0].Latest);
            tampered = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(clients[0].Latest));
            tampered.game.accusations.ownVerdict = true;
            Assert.Throws<ArgumentException>(() => HoldemAccusationDisplay.ValidateTransition(recorded, new HoldemTableDisplay(tampered)));
            Assert.Throws<ArgumentException>(() => HoldemAccusationDisplay.ValidateTransition(recorded, closed));
        }
    }
}
