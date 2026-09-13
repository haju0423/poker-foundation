using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerInputControllerTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);
        private static HandSetup Setup(long stack = 100)
        {
            var order = new[] { A, B }; var after = new[] { B, A };
            return new HandSetup(ChipLedger.Create(new[] { new SeatChips(A, stack), new SeatChips(B, stack) }),
                order, order, after, after, 1, 2);
        }
        // These tests pin the passive fixture; the default practice policy has its own response tests.
        private static LocalPokerTable Table(SeatId? human = null) => new LocalPokerTable(Setup(), human ?? A, new FixedRandom(), new SimpleDrawOpponent());

        [Test]
        public void PortRejectsSpoofedSeatEvenWithCorrectHandVersion()
        {
            var table = Table(); var view = table.Human.Read();
            var receipt = table.Human.Submit(HandCommand.Bet(view.HandId, Guid.NewGuid(), B, view.Version, BettingAction.Check()));
            Assert.That(receipt.Error, Is.EqualTo(HandError.UnauthorizedSeat)); Assert.That(table.Human.Read().Version, Is.EqualTo(view.Version));
        }
        [Test]
        public void ControllerRejectsNullPortAndDoesNotChooseAnotherSeat()
        { Assert.Throws<ArgumentNullException>(() => new PokerInputController(null)); Assert.That(new PokerInputController(Table(B).Human).View.ViewerSeat, Is.EqualTo(B)); }

        [Test]
        public void LocalActionRefreshesViewAndRepeatedClickDoesNotActForOpponent()
        {
            var table = Table(); var input = new PokerInputController(table.Human);
            Assert.That(input.Bet(BettingAction.Call()), Is.True); long version = input.View.Version;
            Assert.That(input.Bet(BettingAction.Call()), Is.False); Assert.That(input.View.Version, Is.EqualTo(version));
            Assert.That(input.View.IsOwnTurn, Is.False); Assert.That(input.IsPending, Is.False);
        }
        [Test]
        public void SelectionRequiresCurrentOwnedCardsAndExchangeTurn()
        {
            var table = Table(); var input = new PokerInputController(table.Human); Card own = input.View.OwnCards[0];
            Assert.That(input.Toggle(own), Is.False); Assert.That(input.Exchange(), Is.False);
            input.Bet(BettingAction.Call()); table.AdvanceOpponent(); table.AdvanceOpponent(); input.Refresh();
            Assert.That(input.View.CanExchange, Is.True);
            Assert.That(input.Toggle(own), Is.True); Assert.That(input.SelectedCount, Is.EqualTo(1));
            Assert.That(input.Toggle(own), Is.True); Assert.That(input.SelectedCount, Is.Zero);
            Card notOwned = Enumerable.Range(0, 52).Select(Card.FromId).First(c => !input.View.OwnCards.Contains(c));
            Assert.That(input.Toggle(notOwned), Is.False); Assert.That(input.Toggle(default), Is.False);
        }
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void ExchangeUsesSelectedCardValuesAndClearsAfterApprovedChange(int count)
        {
            var table = Table(); var input = new PokerInputController(table.Human);
            input.Bet(BettingAction.Call()); table.AdvanceOpponent(); table.AdvanceOpponent(); input.Refresh();
            Card[] old = input.View.OwnCards.ToArray();
            foreach (Card card in old.Take(count)) Assert.That(input.Toggle(card), Is.True);
            Assert.That(input.Exchange(), Is.True); Assert.That(input.SelectedCount, Is.Zero);
            Assert.That(input.View.OwnCards.Intersect(old).Count(), Is.EqualTo(5 - count)); Assert.That(input.Exchange(), Is.False);
        }
        [Test]
        public void SameOrOlderViewCannotResetCurrentSelection()
        {
            var table = Table(); var input = new PokerInputController(table.Human); var old = input.View;
            input.Bet(BettingAction.Call()); table.AdvanceOpponent(); table.AdvanceOpponent(); input.Refresh();
            input.Toggle(input.View.OwnCards[0]); var current = input.View;
            Assert.That(input.Receive(old), Is.False); Assert.That(input.Receive(current), Is.False);
            Assert.That(input.View, Is.SameAs(current)); Assert.That(input.SelectedCount, Is.EqualTo(1));
        }
        [Test]
        public void OtherHandOrViewerCannotReplaceBoundDisplay()
        {
            var table = Table(); var input = new PokerInputController(table.Human); var current = input.View;
            Assert.That(input.Receive(Table().Human.Read()), Is.False);
            var setup = Setup(); var s = new PokerHandSession(current.HandId, setup, new FixedRandom());
            s.Start(new StartHandCommand(s.HandId, Guid.NewGuid()));
            s.Submit(A, HandCommand.Bet(s.HandId, Guid.NewGuid(), A, 1, BettingAction.Call()));
            Assert.That(input.Receive(PokerPlayerViewProjector.Create(s, B)), Is.False);
            Assert.That(input.View, Is.SameAs(current)); Assert.Throws<ArgumentNullException>(() => input.Receive(null));
        }
        [Test]
        public void ConfirmedRejectionUnlocksAtSameVersionAndCanBeCorrected()
        {
            var input = new PokerInputController(Table().Human); long version = input.View.Version;
            Assert.That(input.Bet(BettingAction.Check()), Is.False);
            Assert.That(input.LastReceipt.Error, Is.EqualTo(HandError.IllegalBet)); Assert.That(input.IsPending, Is.False);
            Assert.That(input.View.Version, Is.EqualTo(version)); Assert.That(input.Bet(BettingAction.Call()), Is.True);
        }
        [Test]
        public void LostReceiptRetriesSameCommandWithoutDoublePayment()
        {
            var port = new FaultPort(Table().Human) { ThrowAfterCommit = true }; var input = new PokerInputController(port);
            Assert.Throws<TimeoutException>(() => input.Bet(BettingAction.Call()));
            Assert.That(input.IsPending, Is.True); Assert.That(input.Bet(BettingAction.Call()), Is.False);
            Assert.That(input.RetryPending(), Is.True); Assert.That(input.IsPending, Is.False);
            Assert.That(port.Commands.Count, Is.EqualTo(2)); Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
            Assert.That(input.View.PotAmount, Is.EqualTo(4)); Assert.That(input.View.Version, Is.EqualTo(2));
        }
        [Test]
        public void LostFreshReadAlsoRetainsExactRequestUntilRecovered()
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port); port.FailNextRead = true;
            Assert.Throws<TimeoutException>(() => input.Bet(BettingAction.Call())); Assert.That(input.IsPending, Is.True);
            Assert.That(input.RetryPending(), Is.True); Assert.That(port.Commands[0], Is.SameAs(port.Commands[1]));
            Assert.That(input.View.PotAmount, Is.EqualTo(4)); Assert.That(input.IsPending, Is.False);
        }
        [Test]
        public void OldReadAfterApprovalKeepsInputLockedUntilAppliedVersionArrives()
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port); port.StaleRead = input.View;
            Assert.That(input.Bet(BettingAction.Call()), Is.True); Assert.That(input.IsPending, Is.True);
            Assert.That(input.View.Version, Is.EqualTo(1)); port.StaleRead = null;
            Assert.That(input.RetryPending(), Is.True); Assert.That(input.IsPending, Is.False);
        }
        [Test]
        public void ReentrantInputDuringSubmitCannotSendSecondCommand()
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port);
            port.Callback = () => Assert.That(input.Bet(BettingAction.Call()), Is.False);
            input.Bet(BettingAction.Call()); Assert.That(port.Commands, Has.Count.EqualTo(1));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void RetryDuringActiveDispatchDoesNotRecursivelyResubmit(bool duringRead)
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port);
            bool? nestedResult = null;
            Action callback = () =>
            {
                port.Callback = null; port.ReadCallback = null;
                nestedResult = input.RetryPending();
            };
            if (duringRead) port.ReadCallback = callback; else port.Callback = callback;
            Assert.That(input.Bet(BettingAction.Call()), Is.True);
            Assert.That(nestedResult, Is.False);
            Assert.That(port.Commands, Has.Count.EqualTo(1));
            Assert.That(input.View.Version, Is.EqualTo(2));
            Assert.That(input.IsPending, Is.False);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void RefreshDuringActiveDispatchDoesNotReenterThePort(bool duringRead)
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port);
            bool? nestedResult = null;
            Action callback = () =>
            {
                port.Callback = null; port.ReadCallback = null;
                nestedResult = input.Refresh();
            };
            if (duringRead) port.ReadCallback = callback; else port.Callback = callback;
            Assert.That(input.Bet(BettingAction.Call()), Is.True);
            Assert.That(nestedResult, Is.False);
            Assert.That(port.ReadCount, Is.EqualTo(2), "Only the constructor and outer dispatch should read the port.");
            Assert.That(input.View.Version, Is.EqualTo(2));
            Assert.That(input.IsPending, Is.False);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ReceiveDuringDispatchCannotReplaceTheActiveSnapshot(bool duringRead)
        {
            var port = new FaultPort(Table().Human); var input = new PokerInputController(port);
            PokerPlayerView before = input.View;
            PokerPlayerView later = ViewAtVersion(before.HandId, 3);
            Action callback = () =>
            {
                Assert.That(input.Receive(later), Is.False);
                Assert.That(input.View, Is.SameAs(before));
                Assert.That(input.IsPending, Is.True);
            };
            if (duringRead) port.ReadCallback = callback; else port.Callback = callback;
            Assert.That(input.Bet(BettingAction.Call()), Is.True);
            Assert.That(input.View.Version, Is.EqualTo(2));
            Assert.That(input.IsPending, Is.False);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ReentrantReceivePreservesSelectionAndAnOuterFailureCanRetry(bool failOnce)
        {
            var table = Table(); var port = new FaultPort(table.Human); var input = new PokerInputController(port);
            input.Bet(BettingAction.Call()); table.AdvanceOpponent(); table.AdvanceOpponent(); input.Refresh();
            input.Toggle(input.View.OwnCards[0]); PokerPlayerView before = input.View;
            PokerPlayerView later = ViewAtVersion(before.HandId, 6);
            port.Callback = () =>
            {
                port.Callback = null;
                Assert.That(input.Receive(later), Is.False);
                Assert.That(input.View, Is.SameAs(before));
                Assert.That(input.SelectedCount, Is.EqualTo(1));
                Assert.That(input.IsPending, Is.True);
                if (failOnce) throw new TimeoutException();
            };
            if (failOnce)
            {
                Assert.Throws<TimeoutException>(() => input.Exchange());
                Assert.That(input.IsPending, Is.True);
                Assert.That(input.RetryPending(), Is.True);
                Assert.That(port.Commands[1], Is.SameAs(port.Commands[2]));
            }
            else Assert.That(input.Exchange(), Is.True);
            Assert.That(input.IsPending, Is.False);
            Assert.That(input.View.Version, Is.EqualTo(5));
            Assert.That(input.SelectedCount, Is.Zero);
        }
        [Test]
        public void OpponentOnlyActsOnItsTurnAndOneActionPerStep()
        {
            var table = Table(); Assert.That(table.AdvanceOpponent(), Is.False);
            var input = new PokerInputController(table.Human); input.Bet(BettingAction.Call());
            long old = input.View.Version; Assert.That(table.AdvanceOpponent(), Is.True); input.Refresh();
            Assert.That(input.View.Version, Is.EqualTo(old + 1)); Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Exchange));
        }
        [Test]
        public void OneHandCompletesThroughOnlyPlayerPortAndOpponentSteps()
        {
            var table = Table(); var input = new PokerInputController(table.Human); int steps = 0;
            while (input.View.CurrentSeat.HasValue)
            {
                Assert.That(++steps, Is.LessThan(20));
                if (!input.View.IsOwnTurn) table.AdvanceOpponent();
                else if (input.View.CanExchange) input.Exchange();
                else input.Bet(input.View.Betting.CanCall ? BettingAction.Call() : BettingAction.Check());
                input.Refresh();
            }
            Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Complete)); Assert.That(input.View.PotAmount, Is.Zero);
            Assert.That(input.View.Seats.Sum(s => s.Stack), Is.EqualTo(200)); Assert.That(table.AdvanceOpponent(), Is.False);
        }
        [Test]
        public void NewPracticeUsesDistinctHandIdAndExplicitInitialStackNotOldPayout()
        {
            var first = new PokerInputController(Table().Human); first.Bet(BettingAction.Fold());
            Assert.That(first.View.Phase, Is.EqualTo(HandPhase.Complete)); var next = new PokerInputController(Table().Human);
            Assert.That(first.View.HandId, Is.Not.EqualTo(next.View.HandId)); Assert.That(next.View.Version, Is.EqualTo(1));
            Assert.That(next.View.Seats[0].Stack, Is.EqualTo(99)); Assert.That(next.Receive(first.View), Is.False);
        }
        [Test]
        public void TestOpponentRejectsNonactingViewAndNeverSelectsNonownedCards()
        {
            var table = Table(B); var bot = new SimpleDrawOpponent();
            Assert.Throws<InvalidOperationException>(() => bot.Choose(table.Human.Read()));
            table.AdvanceOpponent(); var input = new PokerInputController(table.Human); input.Bet(BettingAction.Check());
            var view = input.View; var command = bot.Choose(view);
            Assert.That(command.Seat, Is.EqualTo(B)); Assert.That(command.Kind, Is.EqualTo(HandCommandKind.Exchange));
            Assert.That(command.SelectedCards.All(c => view.OwnCards.Contains(c)), Is.True);
        }
        [Test]
        public void PracticeRandomValidatesBoundsAndProducesOnlyInRangeValues()
        {
            using (var random = new PracticeRandom())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt(-1));
                foreach (int upper in new[] { 1, 2, 52, int.MaxValue })
                    for (int i = 0; i < 100; i++) Assert.That(random.NextInt(upper), Is.InRange(0, upper - 1));
            }
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private static PokerPlayerView ViewAtVersion(Guid handId, long version)
        {
            var session = new PokerHandSession(handId, Setup(), new FixedRandom());
            session.Start(new StartHandCommand(handId, Guid.NewGuid()));
            while (session.Version < version)
            {
                SeatId seat = session.State.CurrentSeat.Value;
                HandCommand command = session.State.Phase == HandPhase.Exchange
                    ? HandCommand.Exchange(handId, Guid.NewGuid(), seat, session.Version, new Card[0])
                    : HandCommand.Bet(handId, Guid.NewGuid(), seat, session.Version,
                        session.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check());
                Assert.That(session.Submit(seat, command).Accepted, Is.True);
            }
            return PokerPlayerViewProjector.Create(session, A);
        }
        private sealed class FaultPort : IPokerSeatPort
        {
            private readonly IPokerSeatPort inner;
            public FaultPort(IPokerSeatPort inner) { this.inner = inner; }
            public bool ThrowAfterCommit, FailNextRead; public Action Callback, ReadCallback; public PokerPlayerView StaleRead;
            public int ReadCount;
            public readonly List<HandCommand> Commands = new List<HandCommand>();
            public PokerPlayerView Read()
            {
                ReadCount++; ReadCallback?.Invoke();
                if (FailNextRead) { FailNextRead = false; throw new TimeoutException(); }
                return StaleRead ?? inner.Read();
            }
            public HandReceipt Submit(HandCommand command)
            {
                Commands.Add(command); Callback?.Invoke(); HandReceipt receipt = inner.Submit(command);
                if (ThrowAfterCommit) { ThrowAfterCommit = false; throw new TimeoutException(); } return receipt;
            }
        }
    }
}
