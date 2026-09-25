using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
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
        public void ObservedPeerClosureIsRecordedOnceAndReconnectPreservesPoker(int sender)
        {
            ConnectAll(); Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            clients[sender].Dispose();
            Until(() => server.ReadCloseRecords().Count == 1);
            var record = server.ReadCloseRecords().Single();
            Assert.That(record.Trigger, Is.EqualTo(HoldemPeerCloseTrigger.ChannelObserved));
            Assert.That(record.ChannelReason, Is.EqualTo(HoldemTransportCloseReason.RemoteClosed)
                .Or.EqualTo(HoldemTransportCloseReason.ConnectionLost));
            Assert.That(record.Seat, Is.EqualTo(sender + 1));
            Assert.That(record.WasHost, Is.EqualTo(sender == 0));
            AssertCloseReconnectPreservesPoker(sender, before, record);
        }

        [TestCase(0, "id")]
        [TestCase(1, "protocol")]
        [TestCase(1, "json")]
        public void InvalidEnvelopeHasItsOwnCloseTriggerWithoutChangingPoker(int sender, string fault)
        {
            ConnectAll(); Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var request = new HoldemWireRequest { type = "sync", protocol = HoldemRoomPacketMapper.ProtocolVersion,
                id = Guid.NewGuid().ToString("N"), sessionId = identities[sender].SessionId,
                name = "진단에 남기지 않을 이름", text = "진단에 남기지 않을 멘트", admissionKey = "private-diagnostic-fixture-key" };
            if (fault == "id") request.id = "invalid-id";
            if (fault == "protocol") request.protocol++;
            Assert.That(DiagnosticClientChannel(sender).TrySend(fault == "json" ? "{broken" : JsonUtility.ToJson(request)), Is.True);
            Until(() => server.ReadCloseRecords().Count == 1);
            var record = server.ReadCloseRecords().Single();
            Assert.That(record.Trigger, Is.EqualTo(HoldemPeerCloseTrigger.InvalidEnvelope));
            Assert.That(record.ChannelReason, Is.EqualTo(HoldemTransportCloseReason.None));
            AssertCloseReconnectPreservesPoker(sender, before, record);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChannelFrameAndQueueFailuresKeepTheirOwnReason(bool overflow)
        {
            ConnectAll(); Start();
            const int sender = 1;
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var channel = DiagnosticClientChannel(sender);
            var socket = (TcpClient)typeof(HoldemTcpChannel)
                .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(channel);
            byte[] bytes = new byte[4]; // A zero-length frame is invalid before JSON parsing.
            if (overflow)
            {
                var frame = HoldemFrameCodec.Encode(JsonUtility.ToJson(new HoldemWireRequest { type = "sync",
                    protocol = HoldemRoomPacketMapper.ProtocolVersion, id = Guid.NewGuid().ToString("N"),
                    sessionId = identities[sender].SessionId }));
                bytes = new byte[frame.Length * 34];
                for (int i = 0; i < 34; i++) Buffer.BlockCopy(frame, 0, bytes, i * frame.Length, frame.Length);
            }
            var peer = DiagnosticServerPeer(sender + 1);
            var serverChannel = (HoldemTcpChannel)peer.GetType().GetField("Channel").GetValue(peer);
            socket.GetStream().Write(bytes, 0, bytes.Length);
            // Let the actual byte reader reach its frame/queue bound before authority drains input.
            var wait = Stopwatch.StartNew();
            while (!serverChannel.IsClosed && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(serverChannel.IsClosed, Is.True);
            Until(() => server.ReadCloseRecords().Count == 1);
            var record = server.ReadCloseRecords().Single();
            Assert.That(record.Trigger, Is.EqualTo(HoldemPeerCloseTrigger.ChannelObserved));
            Assert.That(record.ChannelReason, Is.EqualTo(overflow
                ? HoldemTransportCloseReason.Backpressure : HoldemTransportCloseReason.InvalidFrame));
            AssertCloseReconnectPreservesPoker(sender, before, record);
        }

        [Test]
        public void SendAfterConcurrentRemoteCloseRetainsTheObservedChannelReason()
        {
            ConnectAll(); Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var peer = DiagnosticServerPeer(2);
            var channel = (HoldemTcpChannel)peer.GetType().GetField("Channel").GetValue(peer);
            clients[1].Dispose();
            var wait = Stopwatch.StartNew();
            while (!channel.IsClosed && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(channel.IsClosed, Is.True);
            var observed = channel.CloseReason;
            // Simulate EOF between Pump's initial close scan and a later state send.
            bool sent = (bool)typeof(HoldemTcpServer).GetMethod("Send", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(server, new[] { peer, (object)new HoldemWireResponse { type = "pong" } });
            Assert.That(sent, Is.False);
            var record = server.ReadCloseRecords().Single();
            Assert.That(record.Trigger, Is.EqualTo(HoldemPeerCloseTrigger.SendFailed));
            Assert.That(record.ChannelReason, Is.EqualTo(observed));
            AssertCloseReconnectPreservesPoker(1, before, record);
        }

        [Test]
        public void CloseRecordsAreBoundedImmutableAndHostObservationSurvivesUnauthenticatedNoise()
        {
            Connect(0); clients[0].Dispose(); clients[0] = null;
            Until(() => server.LatestHostClose != null);
            var host = server.LatestHostClose;
            var saved = server.ReadCloseRecords();
            for (int i = 0; i < HoldemTcpServer.MaximumCloseRecords + 2; i++)
            {
                long sequence = host.Sequence + i + 1;
                using (var raw = new TcpClient())
                {
                    raw.Connect(server.Endpoint);
                    var frame = HoldemFrameCodec.Encode("{}");
                    raw.GetStream().Write(frame, 0, frame.Length);
                    Until(() => server.ReadCloseRecords().Last().Sequence == sequence);
                }
            }
            var recent = server.ReadCloseRecords();
            Assert.That(recent.Count, Is.EqualTo(HoldemTcpServer.MaximumCloseRecords));
            Assert.That(recent.All(r => r.Trigger == HoldemPeerCloseTrigger.InvalidEnvelope
                && !r.WasAdmitted && !r.WasHost && r.Seat == 0), Is.True);
            Assert.That(recent.First().Sequence, Is.GreaterThan(host.Sequence));
            Assert.That(saved.Count, Is.EqualTo(1));
            Assert.That(saved[0], Is.SameAs(host));
            Assert.That(server.LatestHostClose, Is.SameAs(host));
            Assert.Throws<NotSupportedException>(() => ((IList<HoldemPeerCloseRecord>)recent).Clear());
            foreach (var property in typeof(HoldemPeerCloseRecord).GetProperties())
            {
                Assert.That(property.CanWrite, Is.False, property.Name);
                Assert.That(property.PropertyType.IsEnum || property.PropertyType == typeof(long)
                    || property.PropertyType == typeof(int) || property.PropertyType == typeof(bool), Is.True,
                    "Diagnostic data must not acquire an identity, secret, text, payload or card field: " + property.Name);
            }
            server.Dispose(); server.Dispose();
            Assert.That(server.ReadCloseRecords(), Is.EqualTo(recent));
            Assert.That(server.LatestHostClose, Is.SameAs(host));
        }

        [Test]
        public void NormalServerShutdownDoesNotCreatePeerFailureRecords()
        {
            ConnectAll(); Start();
            Assert.That(server.ReadCloseRecords(), Is.Empty);
            server.Dispose(); server.Dispose();
            Assert.That(server.ReadCloseRecords(), Is.Empty);
            Assert.That(server.LatestHostClose, Is.Null);
        }

        private void AssertCloseReconnectPreservesPoker(int sender, string[] before, HoldemPeerCloseRecord record)
        {
            Until(() => clients.Where((_, i) => i != sender).All(c => c.Latest.paused));
            Tick(); Tick();
            Assert.That(server.ReadCloseRecords().Single(), Is.SameAs(record));
            Assert.That(record.WasAdmitted, Is.True);
            Assert.That(record.Seat, Is.EqualTo(sender + 1));
            for (int i = 0; i < 4; i++)
                if (i != sender) Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            clients[sender].Dispose(); clients[sender] = null; Connect(sender);
            Until(() => clients.All(c => !c.Latest.paused));
            for (int i = 0; i < 4; i++)
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            ActCurrent();
            Assert.That(clients.All(c => c.Latest.game.version == 2), Is.True);
            Assert.That(server.ReadCloseRecords().Single(), Is.SameAs(record));
        }

        private HoldemTcpChannel DiagnosticClientChannel(int index) => (HoldemTcpChannel)typeof(HoldemTcpClient)
            .GetField("channel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[index]);

        private object DiagnosticServerPeer(int seat)
        {
            var peers = (IEnumerable)typeof(HoldemTcpServer).GetField("peers", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(server);
            return peers.Cast<object>().Single(p => (int)p.GetType().GetField("Seat").GetValue(p) == seat);
        }
    }
}
