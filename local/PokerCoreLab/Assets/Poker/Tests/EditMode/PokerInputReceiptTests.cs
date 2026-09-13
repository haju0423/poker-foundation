using System;
using System.Collections.Generic;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerInputReceiptTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [TestCase("other-seat", false)]
        [TestCase("other-seat", true)]
        [TestCase("start-receipt", true)]
        [TestCase("action", true)]
        [TestCase("target", true)]
        [TestCase("applied-version", true)]
        [TestCase("kind", true)]
        [TestCase("exchange-count", true)]
        [TestCase("null", true)]
        [TestCase("hand", true)]
        [TestCase("command", true)]
        public void MisroutedReceiptKeepsExactRequestUntilConfirmed(string fault, bool committedBeforeWrongReply)
        {
            var host = new PokerHandHost(new FixedRandom()); Guid hand = Guid.NewGuid();
            Assert.That(host.Start(HandStartRequest.First(hand, Guid.NewGuid(), Setup())).Accepted, Is.True);
            var port = new MisroutingPort(host.BindSeat(hand, A));
            var input = new PokerInputController(port);
            bool exchange = fault == "kind" || fault == "exchange-count";
            if (exchange)
            {
                Assert.That(input.Bet(BettingAction.Call()), Is.True);
                Act(host.BindSeat(hand, B), BettingAction.Check());
                Keep(host.BindSeat(hand, B));
                input.Refresh();
                Assert.That(input.Toggle(input.View.OwnCards[0]), Is.True);
            }
            PokerPlayerView before = input.View; HandReceipt priorReceipt = input.LastReceipt;
            int reads = port.ReadCount, calls = port.Commands.Count, selected = input.SelectedCount;
            port.Fault = fault; port.CommitBeforeWrongReply = committedBeforeWrongReply;
            TestDelegate submit = exchange ? (TestDelegate)(() => input.Exchange())
                : () => input.Bet(fault == "target" ? BettingAction.RaiseTo(4) : BettingAction.Call());

            Assert.Throws<InvalidOperationException>(submit);
            Assert.That(input.IsPending, Is.True);
            Assert.That(input.LastReceipt, Is.SameAs(priorReceipt));
            Assert.That(input.View, Is.SameAs(before));
            Assert.That(input.SelectedCount, Is.EqualTo(selected));
            Assert.That(port.ReadCount, Is.EqualTo(reads), "Do not read/display state based on an unrelated receipt.");
            Assert.That(input.Bet(BettingAction.Fold()), Is.False);
            Assert.That(input.Exchange(), Is.False);
            Assert.That(port.Commands.Count, Is.EqualTo(calls + 1));
            Assert.That(host.CurrentVersion, Is.EqualTo(before.Version + (committedBeforeWrongReply ? 1 : 0)));

            Assert.That(input.RetryPending(), Is.True);
            Assert.That(port.Commands[calls + 1], Is.SameAs(port.Commands[calls]));
            Assert.That(input.IsPending, Is.False);
            Assert.That(input.LastReceipt, Is.SameAs(port.CorrectReceipt));
            Assert.That(input.View.Version, Is.EqualTo(before.Version + 1));
            Assert.That(host.CurrentVersion, Is.EqualTo(before.Version + 1), "Retry applies at most one real action.");
            Assert.That(input.SelectedCount, Is.Zero);
        }

        [Test]
        public void MatchingOldReceiptCanResolvePendingAfterViewHasAdvancedToAnotherPhase()
        {
            var host = new PokerHandHost(new FixedRandom()); Guid hand = Guid.NewGuid();
            host.Start(HandStartRequest.First(hand, Guid.NewGuid(), Setup()));
            var port = new MisroutingPort(host.BindSeat(hand, A)) { Fault = "action", CommitBeforeWrongReply = true };
            var input = new PokerInputController(port);
            Assert.Throws<InvalidOperationException>(() => input.Bet(BettingAction.Call()));
            Act(host.BindSeat(hand, B), BettingAction.Check());
            Assert.That(input.Refresh(), Is.True);
            Assert.That(input.View.Version, Is.EqualTo(3));
            Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(input.IsPending, Is.True);
            Assert.That(input.RetryPending(), Is.True);
            Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
            Assert.That(input.LastReceipt.AppliedVersion, Is.EqualTo(2));
            Assert.That(input.View.Version, Is.EqualTo(3));
            Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(input.IsPending, Is.False);
            Assert.That(input.View.PotAmount, Is.EqualTo(4));
        }

        private sealed class MisroutingPort : IPokerSeatPort
        {
            private readonly IPokerSeatPort inner;
            public MisroutingPort(IPokerSeatPort inner) { this.inner = inner; }
            public string Fault;
            public bool CommitBeforeWrongReply;
            public int ReadCount;
            public HandReceipt CorrectReceipt;
            public readonly List<HandCommand> Commands = new List<HandCommand>();
            public PokerPlayerView Read() { ReadCount++; return inner.Read(); }
            public HandReceipt Submit(HandCommand command)
            {
                Commands.Add(command);
                if (Fault == null) return CorrectReceipt = inner.Submit(command);
                string fault = Fault; Fault = null;
                if (CommitBeforeWrongReply) CorrectReceipt = inner.Submit(command);
                return DecoyReceipt(command, fault);
            }
        }

        // Build unrelated but real Core receipts. No production constructors or hidden state are opened for tests.
        private static HandReceipt DecoyReceipt(HandCommand request, string fault)
        {
            if (fault == "null") return null;
            Guid hand = fault == "hand" ? Guid.NewGuid() : request.HandId;
            Guid command = fault == "command" ? Guid.NewGuid() : request.CommandId;
            var session = new PokerHandSession(hand, Setup(fault == "kind"), new FixedRandom());
            HandReceipt start = session.Start(new StartHandCommand(hand, fault == "start-receipt" ? command : Guid.NewGuid()));
            if (fault == "start-receipt") return start;
            if (fault == "other-seat")
                return session.Submit(B, HandCommand.Bet(hand, command, B, 1, BettingAction.Call()));
            if (fault == "kind")
            {
                Act(session, BettingAction.RaiseTo(4));
                Act(session, BettingAction.RaiseTo(6));
                Act(session, BettingAction.RaiseTo(8));
                return session.Submit(A, HandCommand.Bet(hand, command, A, session.Version, BettingAction.Call()));
            }
            if (fault == "exchange-count" || fault == "applied-version")
            {
                Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
                Keep(session);
                if (fault == "exchange-count")
                    return session.Submit(A, HandCommand.Exchange(hand, command, A, session.Version, Array.Empty<Card>()));
                Keep(session); Act(session, BettingAction.BetTo(2));
            }
            BettingAction action = fault == "action" ? BettingAction.Fold()
                : fault == "target" ? BettingAction.RaiseTo(6) : BettingAction.Call();
            HandReceipt receipt = session.Submit(A, HandCommand.Bet(hand, command, A, session.Version, action));
            Assert.That(receipt.Accepted, Is.True, "Decoy fixture must produce a genuine unrelated approval.");
            return receipt;
        }

        private static HandSetup Setup(bool reverseOpening = false)
        {
            var ledger = ChipLedger.Create(new[] { new SeatChips(A, 100), new SeatChips(B, 100) });
            return new HandSetup(ledger, new[] { A, B }, reverseOpening ? new[] { B, A } : new[] { A, B },
                new[] { B, A }, new[] { B, A }, 1, 2);
        }
        private static void Act(IPokerSeatPort port, BettingAction action)
        {
            PokerPlayerView view = port.Read();
            Assert.That(port.Submit(HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, action)).Accepted, Is.True);
        }
        private static void Keep(IPokerSeatPort port)
        {
            PokerPlayerView view = port.Read();
            Assert.That(port.Submit(HandCommand.Exchange(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, Array.Empty<Card>())).Accepted, Is.True);
        }
        private static void Act(PokerHandSession session, BettingAction action)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version, action)).Accepted, Is.True);
        }
        private static void Keep(PokerHandSession session)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, Array.Empty<Card>())).Accepted, Is.True);
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
    }
}
