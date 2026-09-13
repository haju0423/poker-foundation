using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Runtime;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    /// <summary>Runnable handoff examples. These local tests are not a network receiver or authentication service.</summary>
    public sealed class TeamIntegrationExampleTests
    {
        private static readonly SeatId Alice = new SeatId(10), Bob = new SeatId(20);

        [Test]
        public void LocalInput_UsesItsSeatPort_ForOneHandAndExplicitNextHand()
        {
            PokerHandHost host = StartExampleHand(out Guid handId);
            IPokerSeatPort alicePort = host.BindSeat(handId, Alice);
            IPokerSeatPort bobPort = host.BindSeat(handId, Bob);
            // Give only this controller/view to Alice's UI, not host or Bob's port.
            var input = new PokerInputController(alicePort);
            Assert.That(input.View.Betting.CanCall, Is.True);
            Assert.That(input.Bet(BettingAction.Call()), Is.True);
            SubmitBet(bobPort, BettingAction.Check());

            // The controller refreshes after other participants act.
            Assert.That(input.Refresh(), Is.True);
            Assert.That(input.View.CanExchange, Is.True);
            Assert.That(input.Toggle(input.View.OwnCards[0]), Is.True);
            Assert.That(input.Exchange(), Is.True);
            PokerPlayerView bob = bobPort.Read();
            Assert.That(bobPort.Submit(HandCommand.Exchange(bob.HandId, Guid.NewGuid(), Bob,
                bob.Version, Array.Empty<Card>())).Accepted, Is.True); // Explicitly keep all five.

            Assert.That(input.Refresh(), Is.True);
            Assert.That(input.View.Betting.CanCheck, Is.True);
            Assert.That(input.Bet(BettingAction.Check()), Is.True);
            SubmitBet(bobPort, BettingAction.Check());
            Assert.That(input.Refresh(), Is.True);
            PokerPlayerView completed = input.View;
            Assert.That(completed.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(completed.Result, Is.Not.Null);
            Assert.That(completed.Seats.Sum(seat => seat.Stack), Is.EqualTo(200));

            // Authority chooses this test's fresh 100-chip setup. This is not an automatic table policy.
            var next = HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), ExampleSetup(),
                completed.HandId, completed.Version);
            Assert.That(host.Start(next).Accepted, Is.True);
            var nextInput = new PokerInputController(host.BindSeat(next.HandId, Alice));
            Assert.That(nextInput.View.HandId, Is.EqualTo(next.HandId));
            Assert.That(nextInput.View.Version, Is.EqualTo(1));
            Assert.That(input.Refresh(), Is.False); // Old input remains attached to the completed hand.
            Assert.That(input.View, Is.SameAs(completed));
            // The old controller rejects next-hand snapshots; replace the controller/screen binding explicitly.
            Assert.That(input.Receive(nextInput.View), Is.False);
            Assert.That(input.View, Is.SameAs(completed));
        }

        [Test]
        public void WireRoundTrip_AnOldAcceptedReceiptDoesNotReplaceTheCurrentSnapshot()
        {
            PokerHandHost host = StartExampleHand(out Guid handId);
            IPokerSeatPort alicePort = host.BindSeat(handId, Alice);
            IPokerSeatPort bobPort = host.BindSeat(handId, Bob);
            PokerPlayerView before = alicePort.Read();
            HandCommand call = HandCommand.Bet(before.HandId, Guid.NewGuid(), Alice, before.Version, BettingAction.Call());
            string outgoing = PokerWireJson.Serialize(call);

            // Local roundtrip only. Resolve authenticated membership before choosing this port.
            HandReceipt accepted = alicePort.Submit(PokerWireJson.ReadCommand(outgoing));
            Assert.That(accepted.Accepted, Is.True);
            SubmitBet(bobPort, BettingAction.Check());

            HandReceipt replayed = alicePort.Submit(PokerWireJson.ReadCommand(outgoing));
            Assert.That(replayed, Is.SameAs(accepted));
            PokerWireReceipt receipt = PokerWireJson.ReadReceipt(PokerWireJson.Serialize(replayed));
            PokerWireSnapshot snapshot = PokerWireJson.ReadSnapshot(PokerWireJson.Serialize(alicePort.Read()));
            Assert.That(receipt.error, Is.EqualTo("none"));
            Assert.That(receipt.appliedVersion, Is.EqualTo("2"));
            Assert.That(snapshot.version, Is.EqualTo("3"));
            Assert.That(snapshot.phase, Is.EqualTo("exchange"));
            Assert.That(snapshot.viewerSeat, Is.EqualTo(Alice.Value));
            Assert.That(snapshot.ownCards, Is.EqualTo(alicePort.Read().OwnCards.Select(card => card.Id)));
            // Test authority only: Bob's private view is read here to check isolation, never by Alice's client.
            Assert.That(snapshot.ownCards.Intersect(bobPort.Read().OwnCards.Select(card => card.Id)), Is.Empty);
            Assert.That(host.CurrentVersion, Is.EqualTo(3));
        }

        [Test]
        public void WireRoundTrip_ASeatFieldCannotChangeThePortsAuthorizedParticipant()
        {
            PokerHandHost host = StartExampleHand(out Guid handId);
            IPokerSeatPort alicePort = host.BindSeat(handId, Alice);
            PokerPlayerView before = alicePort.Read();
            HandCommand claimedBob = HandCommand.Bet(handId, Guid.NewGuid(), Bob, before.Version, BettingAction.Fold());

            // Decoding succeeds; identity/turn/legality are still the authority's responsibility.
            HandCommand decoded = PokerWireJson.ReadCommand(PokerWireJson.Serialize(claimedBob));
            HandReceipt denied = alicePort.Submit(decoded);
            Assert.That(denied.Error, Is.EqualTo(HandError.UnauthorizedSeat));
            Assert.That(denied.AppliedVersion, Is.Null);
            Assert.That(denied.Transition, Is.Null);
            PokerWireReceipt receipt = PokerWireJson.ReadReceipt(PokerWireJson.Serialize(denied));
            PokerWireSnapshot current = PokerWireJson.ReadSnapshot(PokerWireJson.Serialize(alicePort.Read()));
            Assert.That(receipt.error, Is.EqualTo("unauthorized_seat"));
            Assert.That(current.viewerSeat, Is.EqualTo(Alice.Value));
            Assert.That(current.version, Is.EqualTo("1"));
            Assert.That(current.potAmount, Is.EqualTo("3"));
            Assert.That(alicePort.Read().OwnCards, Is.EqualTo(before.OwnCards));
            Assert.That(host.CurrentVersion, Is.EqualTo(before.Version));
        }

        private static PokerHandHost StartExampleHand(out Guid handId)
        {
            // NoShuffle is a deterministic test fixture, never a release shuffle recommendation.
            var host = new PokerHandHost(new NoShuffle());
            handId = Guid.NewGuid();
            Assert.That(host.Start(HandStartRequest.First(handId, Guid.NewGuid(), ExampleSetup())).Accepted, Is.True);
            return host;
        }

        private static HandSetup ExampleSetup()
        {
            var ledger = ChipLedger.Create(new[] { new SeatChips(Alice, 100), new SeatChips(Bob, 100) });
            // Four explicit orders; no inferred button/clockwise policy or odd-chip default.
            return new HandSetup(ledger, new[] { Alice, Bob }, new[] { Alice, Bob },
                new[] { Alice, Bob }, new[] { Alice, Bob }, 1, 2);
        }

        private static void SubmitBet(IPokerSeatPort port, BettingAction action)
        {
            PokerPlayerView view = port.Read();
            Assert.That(port.Submit(HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat,
                view.Version, action)).Accepted, Is.True);
        }

        private sealed class NoShuffle : IRandomSource
        {
            public int NextInt(int exclusiveUpperBound) => exclusiveUpperBound - 1;
        }
    }
}
