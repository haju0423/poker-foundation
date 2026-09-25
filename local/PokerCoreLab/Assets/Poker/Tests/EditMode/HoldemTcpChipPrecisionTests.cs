using System;
using System.Linq;
using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [Test]
        public void WireWagersAboveDoublePrecisionKeepEveryChipAndReplayOnlyOnce()
        {
            const long target = 9007199254740993; // 2^53 + 1: a double round trip would lose one chip.
            long stack = long.MaxValue / 4;
            Cleanup(); Create(new HoldemConfig(stack, 1, 2)); ConnectAll(); Start();
            var before = clients[3].Latest.game;
            var raise = new HoldemWireRequest { type = "act", handId = before.handId, version = before.version,
                action = (int)BettingActionKind.RaiseTo, target = target };
            var reply = Command(3, raise);
            Assert.That(reply.accepted, Is.True, reply.error);
            Until(() => clients.All(c => c.Latest.game.version == reply.receipt.version));
            for (int i = 0; i < 4; i++)
            {
                var game = clients[i].Latest.game;
                var actor = game.seats.Single(s => s.seat == 4);
                Assert.That(actor.streetContribution, Is.EqualTo(target));
                Assert.That(actor.stack, Is.EqualTo(stack - target));
                Assert.That(game.pot, Is.EqualTo(target + 3));
                Assert.That(game.seats.Sum(s => s.stack) + game.pot, Is.EqualTo(stack * 4));
            }
            long revision = clients[0].Latest.revision;
            Assert.That(Command(3, raise).receipt.version, Is.EqualTo(reply.receipt.version));
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            for (int i = 0; i < 3; i++) ActCurrent();
            Assert.That(clients[0].Latest.game.street, Is.EqualTo((int)HoldemStreet.Flop));
            int bettor = clients[0].Latest.game.currentSeat - 1;
            var flop = clients[bettor].Latest.game;
            var bet = Command(bettor, new HoldemWireRequest { type = "act", handId = flop.handId, version = flop.version,
                action = (int)BettingActionKind.BetTo, target = target + 2 });
            Assert.That(bet.accepted, Is.True, bet.error);
            Until(() => clients.All(c => c.Latest.game.version == bet.receipt.version));
            for (int i = 0; i < 4; i++)
            {
                var game = clients[i].Latest.game;
                Assert.That(game.seats.Single(s => s.seat == bettor + 1).streetContribution, Is.EqualTo(target + 2));
                Assert.That(game.pot, Is.EqualTo(target * 5 + 2));
                Assert.That(game.seats.Sum(s => s.stack) + game.pot, Is.EqualTo(stack * 4));
            }
        }

        [TestCase(1L, 2L)]
        [TestCase(1L, long.MaxValue)]
        [TestCase(long.MaxValue, long.MaxValue)]
        public void MaximumFourSeatLedgerAndExtremeBlindsSettleWithoutOverflow(long small, long big)
        {
            long stack = long.MaxValue / 4, total = stack * 4;
            Cleanup(); Create(new HoldemConfig(stack, small, big)); ConnectAll(); Start();
            for (int step = 0; step < 40 && !clients[0].Latest.game.hasResult; step++)
            {
                var game = clients[0].Latest.game;
                Assert.That(game.seats.Sum(s => s.stack) + game.pot, Is.EqualTo(total));
                int actor = game.currentSeat - 1;
                Assert.That(actor, Is.InRange(0, 3));
                var own = clients[actor].Latest.game;
                var request = Passive(actor);
                if (own.legal.raise || own.legal.bet)
                {
                    request.action = own.legal.raise ? (int)BettingActionKind.RaiseTo : (int)BettingActionKind.BetTo;
                    request.target = own.legal.maximumTarget;
                }
                var accepted = Command(actor, request);
                Assert.That(accepted.accepted, Is.True, accepted.error);
                Until(() => clients.All(c => c.Latest.game.version == accepted.receipt.version));
            }
            Assert.That(clients.All(c => c.Latest.game.hasResult), Is.True);
            foreach (var client in clients)
            {
                Assert.That(client.Latest.game.seats.Sum(s => s.stack), Is.EqualTo(total));
                Assert.That(client.Latest.game.seats.All(s => s.stack >= 0), Is.True);
                Assert.That(client.IsAdmitted, Is.True);
            }
        }
    }
}
