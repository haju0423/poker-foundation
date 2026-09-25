using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void LegacySpeechClientIsRejectedBeforeReservationOnlyInPublicRooms(bool isPublic)
        {
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                isPublic ? HoldemUtteranceVisibility.PublicRaw : HoldemUtteranceVisibility.OwnOnly)); Connect(0);
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            SetPublicCapability(clients[1], false);
            Until(() => clients[1].IsAdmitted || clients[1].AdmissionError != null);
            if (isPublic)
            {
                Assert.That(clients[1].AdmissionError, Is.EqualTo("ClientUpgradeRequired"));
                Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
                Assert.That(identities[1].Seat, Is.Zero);
                clients[1].Dispose(); Connect(1);
            }
            Assert.That(identities[1].Seat, Is.EqualTo(2));
            Until(() => clients[0].Latest.members.Length == 2);
            if (!isPublic) return;
            clients[1].Dispose(); Until(() => !clients[0].Latest.members[1].connected);
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            SetPublicCapability(clients[1], false);
            Until(() => clients[1].AdmissionError != null);
            Assert.That(clients[1].AdmissionError, Is.EqualTo("ClientUpgradeRequired"));
            Assert.That(clients[0].Latest.members[1].connected, Is.False);
            clients[1].Dispose(); Connect(1);
            Assert.That(identities[1].Seat, Is.EqualTo(2));
        }

        private static void SetPublicCapability(HoldemTcpClient client, bool supported)
        {
            var request = (HoldemWireRequest)typeof(HoldemTcpClient).GetField("admissionRequest", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            request.supportsPublicUtterances = supported;
        }

        [Test]
        public void PublicFrameBudgetIncludesAllSeatsAndTheSeparateOwnHistory()
        {
            using (var allowed = new HoldemTcpServer(new HoldemClientIdentity("크기 검사"), new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), utterancePolicy: new HoldemUtterancePolicy(512, 1, HoldemUtteranceSeats.Active)))
                Assert.That(allowed.Endpoint.Port, Is.GreaterThan(0));
            Assert.Throws<ArgumentException>(() => new HoldemTcpServer(new HoldemClientIdentity("크기 검사"), new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), utterancePolicy: new HoldemUtterancePolicy(512, 1,
                    HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw)));
        }

        [Test]
        public void EverySeatReceivesAllThreeStreetsAfterAckAndReconnectWithoutLeakingReceipts()
        {
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(256, 1, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
            ConnectAll(); Start();
            string text = new string('"', 256);
            for (int street = 0; street < 3; street++)
            {
                for (int seat = 0; seat < 4; seat++) Assert.That(Command(seat, Speech(seat, text)).accepted, Is.True);
                int expected = (street + 1) * 4;
                Until(() => clients.All(c => c.Latest.publicUtterances.entries.Length == expected));
                for (int action = 0; action < 4; action++) ActCurrent();
                foreach (var batch in server.ReadPendingUtterances()) Assert.That(server.AcknowledgeUtteranceBatch(batch.WindowId), Is.True);
            }
            clients[1].Dispose(); Until(() => clients[0].Latest.paused); Connect(1);
            Until(() => clients.All(c => !c.Latest.paused));
            foreach (var client in clients)
            {
                var packet = client.Latest; var feed = HoldemPublicUtterancePacketReader.Read(packet);
                Assert.That(feed.Count, Is.EqualTo(12)); Assert.That(packet.ownUtterances.entries.Length, Is.EqualTo(3));
                Assert.That(feed.HasHistoryOrder, Is.True);
                for (int i = 0; i < 12; i++) Assert.That(feed.GetEntry(i).HistoryPosition, Is.EqualTo(2 + (i / 4) * 4));
                Assert.That(Enumerable.Range(0, 12).Select(i => feed.GetEntry(i).Speaker.Value).Distinct().Count(), Is.EqualTo(4));
                Assert.That(HoldemFrameCodec.Encode(JsonUtility.ToJson(new HoldemWireResponse { type = "state", state = packet })).Length,
                    Is.LessThanOrEqualTo(HoldemFrameCodec.MaximumBytes + 4));
            }
        }
    }
}
