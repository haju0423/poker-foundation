using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void NoPlayerInputBeyondTransportDeadlineKeepsLobbyOrHandAndAllowsNextAction(bool inHand)
        {
            ConnectAll();
            if (inHand) Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest)).ToArray();
            // Real sockets and the production 2-second heartbeat / 30-second frame deadline.
            // Never substitute a faster clock: this regression is specifically about waiting humans.
            PumpFor(TimeSpan.FromSeconds(35), () =>
            {
                Tick();
                Assert.That(clients.All(c => c.IsAdmitted && !c.Latest.paused), Is.True);
            });
            Assert.That(server.ReadCloseRecords(), Is.Empty);
            for (int i = 0; i < 4; i++)
                Assert.That(JsonUtility.ToJson(clients[i].Latest), Is.EqualTo(before[i]),
                    "Heartbeats must not change readiness, chips, cards, turn, history or revision.");
            if (inHand) ActOnceAfterWaiting();
            else Start();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BriefClientOrHostPumpStallResumesWithoutReplacingConnectionOrChangingHand(bool hostStalls)
        {
            ConnectAll(); Start();
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var original = clients.ToArray();
            const int waitingPlayer = 3;
            PumpFor(TimeSpan.FromSeconds(5), () =>
            {
                if (!hostStalls) server.Pump();
                for (int i = 0; i < 4; i++)
                {
                    if (!hostStalls && i == waitingPlayer) continue;
                    clients[i].Poll();
                    while (clients[i].TryReadResponse(out var response)) replies[i].Add(response);
                }
            });
            // A correlated sync proves traffic resumed, not merely that an old snapshot is still visible.
            Assert.That(Command(waitingPlayer, new HoldemWireRequest { type = "sync" }).type, Is.EqualTo("state"));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(clients[i], Is.SameAs(original[i]));
                Assert.That(clients[i].IsAdmitted, Is.True);
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            }
            Assert.That(server.ReadCloseRecords(), Is.Empty);
            ActOnceAfterWaiting();
        }

        [Test]
        public void SilentCurrentPlayerTimesOutWithoutAutoFoldAndReconnectsToUnchangedHand()
        {
            ConnectAll(); Start();
            int actor = clients[0].Latest.game.currentSeat - 1;
            Assert.That(actor, Is.Not.Zero, "Keep the authority running while the current guest stops polling.");
            var before = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var silent = clients[actor]; clients[actor] = null;
            try
            {
                // Stop the client's owner loop, not its socket: no explicit FIN or disposal shortcuts.
                PumpFor(TimeSpan.FromSeconds(35), Tick);
                Assert.That(silent.IsClosed, Is.True);
                Until(() => clients.Where(c => c != null).All(c => c.Latest.paused
                    && !c.Latest.members.Single(m => m.seat == actor + 1).connected));
                Assert.That(server.ReadCloseRecords().Count, Is.EqualTo(1));
                Assert.That(server.ReadCloseRecords()[0].Seat, Is.EqualTo(actor + 1));
                for (int i = 0; i < 4; i++)
                    if (i != actor) Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]));
            }
            finally { silent.Dispose(); }
            Connect(actor);
            Until(() => clients.All(c => !c.Latest.paused));
            for (int i = 0; i < 4; i++)
                Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(before[i]),
                    "A connection timeout is not a poker fold, loss, redeal or rebuy.");
            ActOnceAfterWaiting();
        }

        private void ActOnceAfterWaiting()
        {
            long version = clients[0].Latest.game.version;
            ActCurrent();
            Assert.That(clients.All(c => c.Latest.game.version == version + 1), Is.True,
                "The first resumed input must apply exactly once.");
        }

        private static void PumpFor(TimeSpan duration, Action pump)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < duration) { pump(); Thread.Sleep(10); }
        }
    }
}
