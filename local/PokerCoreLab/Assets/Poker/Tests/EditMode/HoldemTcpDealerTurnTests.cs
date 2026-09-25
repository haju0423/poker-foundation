using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(0)]
        [TestCase(1)]
        public void DealerCoordinatorDefersWorkerCompletionAcrossKnownSocketClosureAndReconnection(int disconnected)
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), utterancePolicy:
                new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            ConnectAll(); Start(); Assert.That(Command(1, Speech(1, "비동기 처리할 원문")).accepted, Is.True);
            for (int i = 0; i < 4; i++) ActCurrent();
            using var coordinator = new HoldemDealerTurnCoordinator(server);
            coordinator.Poll(out var work);
            var peer = DiagnosticServerPeer(disconnected + 1);
            var channel = (HoldemTcpChannel)peer.GetType().GetField("Channel").GetValue(peer);
            clients[disconnected].Dispose();
            var wait = Stopwatch.StartNew();
            while (!channel.IsClosed && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(channel.IsClosed, Is.True);
            Assert.That(Task.Run(() => coordinator.TryPostUnchanged(work)).GetAwaiter().GetResult(), Is.True);
            // No server.Pump first: the host port must observe a known socket close itself.
            Assert.That(coordinator.Poll(out var next), Is.Null); Assert.That(next, Is.Null);
            Until(() => clients.Where((_, i) => i != disconnected).All(c => c.Latest.paused));
            clients[disconnected] = null; Connect(disconnected);
            Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(server.ReadPendingTurn().Turn.ExpectedVersion, Is.EqualTo(work.ExpectedVersion));
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == work.ExpectedVersion + 1));
            Assert.That(clients.All(c => c.Latest.game.board.Length == 3), Is.True);
            Assert.That(server.ReadPendingUtterances().Single(), Is.SameAs(work.SourceUtterances));
            Assert.That(coordinator.TryPostUnchanged(work), Is.False);
            server.Dispose(); Assert.Throws<ObjectDisposedException>(() => coordinator.Poll(out _));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void DealerTimeoutExcludesRealSocketPauseAndContinuesAfterSameSeatReconnect(int disconnected)
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), utterancePolicy:
                new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            ConnectAll(); Start(); Assert.That(Command(1, Speech(1, "대기 중 보존할 원문")).accepted, Is.True);
            for (int i = 0; i < 4; i++) ActCurrent();
            double seconds = 0;
            using var timer = new HoldemDealerTurnTimeout(server, TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(seconds));
            var original = server.ReadPendingTurn().Turn;
            Assert.That(timer.Poll(), Is.Null); seconds = 2; Assert.That(timer.Poll(), Is.Null);
            clients[disconnected].Dispose();
            Until(() => clients.Where((_, i) => i != disconnected).All(c => c.Latest.paused));
            seconds = 100; Assert.That(timer.Poll(), Is.Null);
            clients[disconnected] = null; Connect(disconnected);
            Until(() => clients.All(c => !c.Latest.paused));
            seconds = 200; Assert.That(timer.Poll(), Is.Null);
            seconds = 202.999; Assert.That(timer.Poll(), Is.Null);
            Assert.That(server.ReadPendingTurn().Turn.TargetDeal.WindowId, Is.EqualTo(original.TargetDeal.WindowId));
            seconds = 203; Assert.That(timer.Poll().Accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == original.ExpectedVersion + 1));
            Assert.That(server.ReadPendingUtterances().Single(), Is.SameAs(original.SourceUtterances));
            var after = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            seconds = 1000; Assert.That(timer.Poll(), Is.Null); Tick();
            for (int i = 0; i < 4; i++) Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(after[i]));
            server.Dispose(); Assert.Throws<ObjectDisposedException>(() => timer.Poll());
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        public void HostDealerTurnObservesAlreadyClosedSocketsBeforeLocalReadOrCompletion(int disconnected, bool readFirst)
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost));
            ConnectAll(); Start(); for (int i = 0; i < 4; i++) ActCurrent();
            IHoldemDealerTurnPort port = server;
            var turn = port.ReadPendingTurn().Turn; var command = turn.CreateUnchangedCommand(Guid.NewGuid());
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var peer = DiagnosticServerPeer(disconnected + 1);
            var channel = (HoldemTcpChannel)peer.GetType().GetField("Channel").GetValue(peer);
            clients[disconnected].Dispose();
            var wait = Stopwatch.StartNew();
            while (!channel.IsClosed && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(channel.IsClosed, Is.True);
            var expected = disconnected == 0 ? HoldemRoomError.Disconnected : HoldemRoomError.Paused;
            // Deliberately no server.Pump: component Update ordering must not bypass a known closure.
            if (readFirst) Assert.That(port.ReadPendingTurn().Error, Is.EqualTo(expected));
            Assert.That(port.DealUnchanged(command).Error, Is.EqualTo(expected));
            Until(() => clients.Where((_, i) => i != disconnected).All(c => c.Latest.paused));
            for (int i = 0; i < 4; i++) if (i != disconnected)
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            clients[disconnected] = null; Connect(disconnected);
            Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(port.ReadPendingTurn().Turn.TargetDeal.WindowId, Is.EqualTo(turn.TargetDeal.WindowId));
            Assert.That(port.DealUnchanged(command).Accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == turn.ExpectedVersion + 1));
        }

        [Test]
        public void HostDealerTurnPairsRawInputsAndCompletesThroughTheSameNetworkAuthority()
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), utterancePolicy:
                new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            IHoldemDealerTurnPort port = server;
            Assert.That(port.ReadPendingTurn().Error, Is.EqualTo(HoldemRoomError.Disconnected));
            ConnectAll(); Start();
            Assert.That(port.ReadPendingTurn().Turn, Is.Null);
            var source = Speech(1, "호스트 처리용 멘트");
            Assert.That(Command(1, source).accepted, Is.True);
            Assert.That(Command(2, Speech(2, "다른 참가자의 원문")).accepted, Is.True);
            for (int i = 0; i < 4; i++) ActCurrent();
            var turn = port.ReadPendingTurn().Turn;
            Assert.That(turn, Is.Not.Null);
            Assert.That(turn.TargetDeal.WindowId.ToString("N"), Is.EqualTo(clients[0].Latest.game.dealWindowId));
            Assert.That(turn.SourceUtterances.WindowId.ToString("N"), Is.EqualTo(source.windowId));
            Assert.That(turn.SourceUtterances.Count, Is.EqualTo(2));
            Assert.That(turn.SourceUtterances.GetEntry(0).Speaker.Value, Is.EqualTo(2));
            Assert.That(turn.SourceUtterances.GetEntry(1).Speaker.Value, Is.EqualTo(3));
            Assert.That(server.AcknowledgeUtteranceBatch(turn.SourceUtterances.WindowId), Is.True);
            Assert.That(server.ReadPendingUtterances(), Is.Empty);
            Assert.That(port.ReadPendingTurn().Turn.SourceUtterances, Is.SameAs(turn.SourceUtterances));
            for (int i = 0; i < 4; i++)
            {
                string json = JsonUtility.ToJson(clients[i].Latest);
                if (i != 1) StringAssert.DoesNotContain(source.text, json);
                if (i != 2) StringAssert.DoesNotContain("다른 참가자의 원문", json);
                StringAssert.DoesNotContain("SourceUtterances", json);
            }
            foreach (int sender in new[] { 0, 1 })
                Assert.That(Command(sender, new HoldemWireRequest { type = "read-dealer-turn",
                    handId = turn.TargetDeal.HandId.ToString("N"), version = turn.ExpectedVersion }).error,
                    Is.EqualTo("UnknownMessage"));
            var command = turn.CreateUnchangedCommand(Guid.NewGuid());
            Assert.That(port.DealUnchanged(command).Accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == turn.ExpectedVersion + 1));
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            Assert.That(port.DealUnchanged(command).Accepted, Is.True);
            Tick();
            for (int i = 0; i < 4; i++) Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            Assert.That(port.ReadPendingTurn().Turn, Is.Null);
            server.Dispose();
            Assert.Throws<ObjectDisposedException>(() => port.ReadPendingTurn());
            Assert.Throws<ObjectDisposedException>(() => port.DealUnchanged(command));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void HostDealerTurnCompletionWaitsForReconnectAndDoesNotLoseCorrelation(int disconnected)
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), utterancePolicy:
                new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            ConnectAll(); Start(); Assert.That(Command(1, Speech(1, "보관할 원문")).accepted, Is.True);
            for (int i = 0; i < 4; i++) ActCurrent();
            IHoldemDealerTurnPort port = server;
            var turn = port.ReadPendingTurn().Turn;
            var command = turn.CreateUnchangedCommand(Guid.NewGuid());
            clients[disconnected].Dispose();
            Until(() => clients.Where((_, i) => i != disconnected).All(c => c.Latest.paused));
            var expected = disconnected == 0 ? HoldemRoomError.Disconnected : HoldemRoomError.Paused;
            Assert.That(port.ReadPendingTurn().Error, Is.EqualTo(expected));
            Assert.That(port.ReadPendingTurn().Turn, Is.Null);
            Assert.That(port.DealUnchanged(command).Error, Is.EqualTo(expected));
            clients[disconnected] = null; Connect(disconnected);
            Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(port.ReadPendingTurn().Turn.TargetDeal.WindowId, Is.EqualTo(turn.TargetDeal.WindowId));
            Assert.That(port.ReadPendingTurn().Turn.SourceUtterances, Is.SameAs(turn.SourceUtterances));
            Assert.That(port.DealUnchanged(command).Accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == turn.ExpectedVersion + 1));
            var state = clients[0].Latest.game;
            Assert.That(Command(0, new HoldemWireRequest { type = "reveal", handId = state.handId,
                version = state.version, street = state.street }).accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == state.version + 1));
            for (int i = 0; i < 4; i++) ActCurrent();
            var next = port.ReadPendingTurn().Turn;
            Assert.That(port.DealUnchanged(turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.False);
            Assert.That(port.DealUnchanged(command).Accepted, Is.True);
            Assert.That(port.ReadPendingTurn().Turn.TargetDeal.WindowId, Is.EqualTo(next.TargetDeal.WindowId));
            Assert.That(port.ReadPendingTurn().Turn.ExpectedVersion, Is.EqualTo(next.ExpectedVersion));
        }
    }
}
