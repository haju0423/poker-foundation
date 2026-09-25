using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(3)] [TestCase(4)]
        public void TcpRematchPreservesEveryIdentityAndRejectsOldHandInput(int count)
        {
            Cleanup(); Create(random: new HoldemRematchTests.DeckRandom(count), seatCapacity: count);
            var active = Enumerable.Range(0, count).ToArray();
            foreach (int i in active) Connect(i);
            foreach (int i in active) Assert.That(Command(i, new HoldemWireRequest { type = "ready", ready = true }).accepted, Is.True);
            var start = Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = 0 });
            Until(() => active.All(i => clients[i].Latest.game?.version == start.receipt.version));
            for (int step = 0; step < 32 && !clients[0].Latest.game.isOver; step++)
            {
                var current = clients[0].Latest.game;
                if (current.hasResult)
                {
                    var next = Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = current.version });
                    Until(() => active.All(i => clients[i].Latest.game.version == next.receipt.version)); continue;
                }
                int actor = current.currentSeat - 1;
                var game = clients[actor].Latest.game;
                var ack = Command(actor, new HoldemWireRequest { type = "act", handId = game.handId, version = game.version,
                    action = game.legal.raise ? 4 : game.legal.bet ? 3 : game.legal.call ? 2 : 1,
                    target = game.legal.raise || game.legal.bet ? game.legal.maximumTarget : 0 });
                Assert.That(ack.accepted, Is.True);
                Until(() => active.All(i => clients[i].Latest.game.version == ack.receipt.version));
            }
            var final = clients[0].Latest;
            Assert.That(final.game.isOver, Is.True);
            var rematch = new HoldemWireRequest { type = "rematch", handId = Guid.NewGuid().ToString("N"),
                completedHandId = final.game.handId, version = final.game.version };
            Assert.That(Command(1, rematch).error, Is.EqualTo("HostOnly"));
            Assert.That(Command(0, rematch).error, Is.EqualTo("PlayersNotReady"));
            foreach (int i in active)
                Assert.That(Command(i, new HoldemWireRequest { type = "rematch-ready", handId = final.game.handId,
                    version = final.game.version, rematchRevision = clients[i].Latest.members.Single(m => m.seat == i + 1).rematchRevision,
                    ready = true }).accepted, Is.True);
            var receipt = Command(0, rematch);
            Assert.That(receipt.accepted, Is.True);
            Until(() => active.All(i => clients[i].Latest.matchNumber == 2));
            Assert.That(Command(0, rematch).receipt.version, Is.EqualTo(receipt.receipt.version));
            foreach (int i in active)
            {
                var next = clients[i].Latest;
                Assert.That(clients[i].IsAdmitted, Is.True);
                Assert.That(identities[i].SessionId, Is.EqualTo(final.sessionId));
                Assert.That(identities[i].Seat, Is.EqualTo(i + 1));
                Assert.That(next.game.handId, Is.EqualTo(rematch.handId));
                Assert.That(next.game.version, Is.EqualTo(final.game.version + 1));
                Assert.That(next.game.handNumber, Is.EqualTo(final.game.handNumber + 1));
                Assert.That(next.game.seats.All(s => s.stack + s.committed == 100), Is.True);
                Assert.That(next.members.All(m => !m.rematchReady), Is.True);
                Assert.That(next.history.entries.Length, Is.EqualTo(2));
            }
            var stale = Command(1, new HoldemWireRequest { type = "act", handId = final.game.handId, version = final.game.version, action = 0 });
            Assert.That(stale.receipt.error, Is.EqualTo("WrongHand"));
            Assert.That(clients[0].Latest.game.version, Is.EqualTo(receipt.receipt.version));
        }

        [Test]
        public void RematchCapabilityIsRecheckedWhenTheSameIdentityReconnects()
        {
            ConnectAll();
            Assert.That(clients[0].Latest.rematchSupported, Is.True);
            clients[1].Dispose(); Until(() => !clients[0].Latest.members.Single(m => m.seat == 2).connected);
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            var request = (HoldemWireRequest)typeof(HoldemTcpClient).GetField("admissionRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(clients[1]);
            request.supportsRematch = false;
            Until(() => clients[1].IsAdmitted && !clients[0].Latest.rematchSupported);
            Assert.That(identities[1].Seat, Is.EqualTo(2));
            Start(); // Legacy peers still play the original match.
            clients[1].Dispose(); Until(() => !clients[0].Latest.members.Single(m => m.seat == 2).connected);
            Connect(1); Until(() => clients[0].Latest.rematchSupported);
            Assert.That(clients[0].Latest.members.Single(m => m.seat == 2).rematchReady, Is.False);
        }
    }
}
