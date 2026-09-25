using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Poker.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [Test]
        public void OptionalWireReceiptPresenceIsDefinedByItsMarkerNotItsObjectReference()
        {
            var response = JsonUtility.FromJson<HoldemWireResponse>(JsonUtility.ToJson(new HoldemWireResponse
                { protocol = 1, type = "utterance-receipt", error = "Paused" }));
            Assert.That(response.hasReceipt, Is.False);
            Assert.That(response.hasUtteranceReceipt, Is.False);
            Assert.That(response.accepted, Is.False);
            // JsonUtility can materialize the default nested object during round-trip even
            // though there is no receipt. The protocol's explicit presence marker is authoritative.
            Assert.That(response.receipt, Is.Not.Null);
            Assert.That(response.receipt.hasVersion, Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void RequestBurstClosesOnlyItsBindingAndReconnectPreservesTheHand(int sender)
        {
            ConnectAll(); Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var oldIdentity = identities[sender];
            var oldClient = clients[sender];
            LogAssert.Expect(LogType.Warning, "Poker connection closed: input rate limit exceeded.");

            // Drain each small group before sending another. Neither channel's 32-message
            // queue nor the client's 64-response queue is the intended failure condition.
            var elapsed = Stopwatch.StartNew();
            for (int group = 0; group < 16 && !oldClient.IsClosed; group++)
            {
                var ids = new HashSet<string>();
                for (int i = 0; i < 8; i++)
                {
                    string id = oldClient.Send(new HoldemWireRequest { type = "sync" });
                    Assert.That(id, Is.Not.Null);
                    ids.Add(id);
                }
                Until(() => oldClient.IsClosed || replies[sender].Count(r => ids.Contains(r.id)) == ids.Count);
            }
            Assert.That(oldClient.IsClosed, Is.True, "The bounded burst did not exceed the rate budget in "
                + elapsed.ElapsedMilliseconds + "ms; do not interpret a slow fixture as a transport failure.");
            Assert.That(oldClient.CloseReason, Is.Not.EqualTo(HoldemTransportCloseReason.Backpressure));
            Until(() => clients.Where((_, i) => i != sender).All(c => c.Latest.paused
                && !c.Latest.members.Single(m => m.seat == oldIdentity.Seat).connected));
            var close = server.ReadCloseRecords().Single();
            Assert.That(close.Trigger, Is.EqualTo(HoldemPeerCloseTrigger.RateLimit));
            Assert.That(close.ChannelReason, Is.EqualTo(HoldemTransportCloseReason.None),
                "Capture the observation before intentional disposal changes the channel to LocalClosed.");
            Assert.That(close.Seat, Is.EqualTo(sender + 1));
            Assert.That(close.WasAdmitted, Is.True);
            Assert.That(close.WasHost, Is.EqualTo(sender == 0));
            Assert.That(server.LatestHostClose, sender == 0 ? Is.SameAs(close) : Is.Null);
            Tick(); Tick();
            Assert.That(server.ReadCloseRecords().Count, Is.EqualTo(1), "Repeated close cleanup must not replace the first trigger.");
            for (int i = 0; i < 4; i++)
            {
                if (i == sender) continue;
                Assert.That(clients[i].IsAdmitted, Is.True, "Only the over-budget binding may close.");
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            }

            oldClient.Dispose(); clients[sender] = null;
            Connect(sender);
            Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(identities[sender], Is.SameAs(oldIdentity));
            Assert.That(clients[sender].Latest.viewerSeat, Is.EqualTo(sender + 1));
            for (int i = 0; i < 4; i++)
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]),
                    "Cards, contributions, chips and action ownership must survive the transport replacement.");
            ActCurrent();
            Assert.That(clients.All(c => c.Latest.game.version == 2), Is.True);
            Assert.That(server.ReadCloseRecords().Single(), Is.SameAs(close));
        }
    }
}
