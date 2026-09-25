using System;
using System.Collections.Generic;
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
        private void StartGatedRoom()
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost));
            ConnectAll(); Start();
        }

        private HoldemWireRequest CurrentDeal()
        {
            var game = clients[0].Latest.game;
            Assert.That(game.dealPending, Is.True);
            return new HoldemWireRequest { type = "deal", id = Guid.NewGuid().ToString("N"),
                handId = game.handId, windowId = game.dealWindowId, version = game.version, street = game.dealStreet };
        }

        private void ResumeCurrentReveal()
        {
            var game = clients[0].Latest.game;
            Assert.That(game.revealPending, Is.True);
            var reply = Command(0, new HoldemWireRequest { type = "reveal", handId = game.handId,
                version = game.version, street = game.street });
            Assert.That(reply.accepted, Is.True, reply.error);
            Until(() => clients.All(c => c.Latest.game.version == reply.receipt.version));
        }

        private void ReachNextDeal()
        {
            for (int i = 0; i < 8 && !clients[0].Latest.game.dealPending; i++) ActCurrent();
            Assert.That(clients[0].Latest.game.dealPending, Is.True);
        }

        private void AssertNoPrivateOrFutureCards(int boardCount)
        {
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var game = clients[viewer].Latest.game;
                Assert.That(game.board.Length, Is.EqualTo(boardCount));
                Assert.That(game.board, Is.EqualTo(clients[0].Latest.game.board));
                Assert.That(game.hasLegal, Is.False);
                foreach (var seat in game.seats)
                    Assert.That(seat.visibleCards.Length, Is.EqualTo(seat.seat == viewer + 1 ? 2 : 0));
            }
        }

        private int[] FinishNeutralHand()
        {
            for (int step = 0; step < 64 && !clients[0].Latest.game.hasResult; step++)
            {
                var game = clients[0].Latest.game;
                if (game.dealPending)
                {
                    var reply = Command(0, CurrentDeal());
                    Assert.That(reply.accepted, Is.True, reply.error);
                    Until(() => clients.All(c => c.Latest.game.version == reply.receipt.version));
                    AssertNoPrivateOrFutureCards(game.dealStreet + 2);
                }
                else if (game.revealPending) ResumeCurrentReveal();
                else ActCurrent();
            }
            Assert.That(clients.All(c => c.Latest.game.hasResult), Is.True);
            return (int[])clients[0].Latest.game.board.Clone();
        }

        [Test]
        public void RejectedWireDealsCannotConsumeCardsOrChangeThePendingWindow()
        {
            StartGatedRoom(); var expectedBoard = FinishNeutralHand();
            StartGatedRoom(); ReachNextDeal(); AssertNoPrivateOrFutureCards(0);
            var basis = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            var revision = clients.Select(c => c.Latest.revision).ToArray();
            var mutations = new Action<HoldemWireRequest>[] {
                r => r.sessionId = Guid.NewGuid().ToString("N"),
                r => r.handId = Guid.NewGuid().ToString("N"),
                r => r.windowId = Guid.NewGuid().ToString("N"),
                r => r.windowId = "invalid",
                r => r.street = (int)HoldemStreet.Turn,
                r => r.street = (int)HoldemStreet.Preflop,
                r => r.version--,
                r => r.version++
            };
            foreach (var mutate in mutations)
            {
                var request = CurrentDeal(); mutate(request);
                var rejected = Command(0, request);
                Assert.That(rejected.accepted, Is.False);
                for (int i = 0; i < 4; i++)
                {
                    Assert.That(clients[i].IsAdmitted, Is.True);
                    Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(basis[i]));
                    Assert.That(clients[i].Latest.revision, Is.EqualTo(revision[i]));
                }
            }
            Assert.That(FinishNeutralHand(), Is.EqualTo(expectedBoard), "Rejected commands must not draw or burn any card.");
        }

        [Test]
        public void LostDealReceiptCanBeRecoveredAfterHostReconnectWithoutDealingTwice()
        {
            StartGatedRoom(); var expectedBoard = FinishNeutralHand();
            StartGatedRoom(); ReachNextDeal();
            var deal = CurrentDeal(); long oldVersion = deal.version;
            Assert.That(clients[0].Send(deal), Is.EqualTo(deal.id));
            var wait = Stopwatch.StartNew();
            while (server.PendingInputCount == 0 && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(server.PendingInputCount, Is.GreaterThan(0));
            server.Pump(); // Accept, then lose the host's response before the client can poll it.
            clients[0].Dispose(); clients[0] = null;
            Until(() => clients[1].Latest.paused && clients[1].Latest.game.version == oldVersion + 1);
            var after = JsonUtility.ToJson(clients[1].Latest.game);
            Connect(0); Until(() => clients.All(c => !c.Latest.paused));
            AssertNoPrivateOrFutureCards(3);
            long revision = clients[0].Latest.revision;
            var replay = Command(0, deal);
            Assert.That(replay.accepted, Is.True);
            Assert.That(replay.receipt.version, Is.EqualTo(oldVersion + 1));
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(JsonUtility.ToJson(clients[1].Latest.game), Is.EqualTo(after));
            Assert.That(FinishNeutralHand(), Is.EqualTo(expectedBoard));
        }

        [Test]
        public void AnAcceptedFlopDealRetryCannotConsumeTheTurnWindowOrCrossHands()
        {
            StartGatedRoom(); var expectedBoard = FinishNeutralHand();
            StartGatedRoom(); ReachNextDeal();
            var flop = CurrentDeal(); var accepted = Command(0, flop);
            Assert.That(accepted.accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == accepted.receipt.version));
            ResumeCurrentReveal(); ReachNextDeal();
            AssertNoPrivateOrFutureCards(3);
            var turn = CurrentDeal(); Assert.That(turn.street, Is.EqualTo((int)HoldemStreet.Turn));
            var unchanged = clients.Select(c => JsonUtility.ToJson(c.Latest.game)).ToArray();
            long revision = clients[0].Latest.revision;
            var retry = Command(0, flop);
            Assert.That(retry.accepted, Is.True);
            Assert.That(retry.receipt.version, Is.EqualTo(accepted.receipt.version));
            Assert.That(Command(1, flop).error, Is.EqualTo("HostOnly"), "The retry cache must not grant guest authority.");
            turn.id = flop.id;
            var conflict = Command(0, turn);
            Assert.That(conflict.accepted, Is.False);
            Assert.That(conflict.receipt.error, Is.EqualTo("CommandConflict"));
            for (int i = 0; i < 4; i++) Assert.That(JsonUtility.ToJson(clients[i].Latest.game), Is.EqualTo(unchanged[i]));
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(FinishNeutralHand(), Is.EqualTo(expectedBoard));
            Start(); ReachNextDeal();
            var newHand = JsonUtility.ToJson(clients[0].Latest.game);
            var wrongHand = Command(0, flop);
            Assert.That(wrongHand.accepted, Is.False);
            Assert.That(wrongHand.receipt.error, Is.EqualTo("WrongHand"));
            Assert.That(JsonUtility.ToJson(clients[0].Latest.game), Is.EqualTo(newHand));
            AssertNoPrivateOrFutureCards(0);
        }
    }
}
