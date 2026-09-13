using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PracticeProgressRunnerTests
    {
        [Test]
        public void ConstructorRequiresBothCallbacks()
        {
            Assert.Throws<ArgumentNullException>(() => new PracticeProgressRunner(null, () => { }));
            Assert.Throws<ArgumentNullException>(() => new PracticeProgressRunner(() => false, null));
        }

        [Test]
        public void NoOpDoesNotRefreshOrPauseAndRetryWithoutFailureDoesNothing()
        {
            int actions = 0, displays = 0;
            var runner = new PracticeProgressRunner(() => { actions++; return false; }, () => displays++);
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.None)); Assert.That(actions, Is.Zero);
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.None));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(displays, Is.Zero);
            Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.None)); Assert.That(runner.FailureType, Is.Empty);
        }

        [Test]
        public void SuccessfulTickInvokesOneActionThenOneDisplay()
        {
            string order = "";
            var runner = new PracticeProgressRunner(() => { order += "A"; return true; }, () => order += "D");
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Advanced)); Assert.That(order, Is.EqualTo("AD"));
        }

        [Test]
        public void ActionFailureStopsAutomaticTicksUntilExplicitRetry()
        {
            int actions = 0, displays = 0; bool fail = true;
            var runner = new PracticeProgressRunner(() => { actions++; if (fail) throw new IOException("PRIVATE MESSAGE"); return true; }, () => displays++);
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.OpponentAction)); Assert.That(runner.FailureType, Is.EqualTo("IOException"));
            for (int i = 0; i < 50; i++) Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Stopped));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(displays, Is.Zero);
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Failed)); Assert.That(actions, Is.EqualTo(2));
            fail = false;
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Advanced));
            Assert.That(actions, Is.EqualTo(3)); Assert.That(displays, Is.EqualTo(1));
            Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.None)); Assert.That(runner.FailureType, Is.Empty);
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.None)); Assert.That(actions, Is.EqualTo(3));
        }

        [Test]
        public void DisplayFailureRetriesDisplayOnlyEvenWhenItFailsMoreThanOnce()
        {
            int actions = 0, displays = 0; bool fail = true;
            var runner = new PracticeProgressRunner(() => { actions++; return true; }, () => { displays++; if (fail) throw new IOException(); });
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.Display));
            for (int i = 0; i < 20; i++) Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Stopped));
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(displays, Is.EqualTo(2));
            fail = false; Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Refreshed));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(displays, Is.EqualTo(3));
        }

        [Test]
        public void ActionRetryThatCommitsButCannotDisplaySwitchesToDisplayOnlyRecovery()
        {
            int actions = 0, displays = 0;
            var runner = new PracticeProgressRunner(() => { if (++actions == 1) throw new InvalidOperationException(); return true; },
                () => { if (++displays == 1) throw new IOException(); });
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.Display));
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Refreshed));
            Assert.That(actions, Is.EqualTo(2)); Assert.That(displays, Is.EqualTo(2));
        }

        [Test]
        public void NoOpAfterFailedActionStillRefreshesTheCurrentViewOnExplicitRetry()
        {
            int actions = 0, displays = 0;
            var runner = new PracticeProgressRunner(() => { if (++actions == 1) throw new IOException(); return false; }, () => displays++);
            runner.Tick(); Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Refreshed));
            Assert.That(displays, Is.EqualTo(1)); Assert.That(runner.Failure, Is.EqualTo(PracticeProgressFailure.None));
        }

        [TestCase(false)][TestCase(true)]
        public void NestedTickAndRetryAreBusyInBothCallbacks(bool displayCallback)
        {
            PracticeProgressRunner runner = null; int actions = 0, displays = 0;
            Action nested = () =>
            { Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Busy)); Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Busy)); };
            runner = new PracticeProgressRunner(() => { actions++; if (!displayCallback) nested(); return true; },
                () => { displays++; if (displayCallback) nested(); });
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Advanced));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(displays, Is.EqualTo(1));
        }

        [Test]
        public void FailedLocalOpponentRetriesExactlyOneActionWithoutResettingHand()
        {
            var bot = new ThrowOnceOpponent(); var table = Table(bot); var input = new PokerInputController(table.Human);
            input.Bet(BettingAction.Call()); Guid hand = input.View.HandId; long before = input.View.Version;
            var runner = new PracticeProgressRunner(table.AdvanceOpponent, () => input.Refresh());
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(table.Human.Read().Version, Is.EqualTo(before));
            for (int i = 0; i < 30; i++) runner.Tick(); Assert.That(bot.Calls, Is.EqualTo(1));
            Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Advanced));
            Assert.That(input.View.HandId, Is.EqualTo(hand)); Assert.That(input.View.Version, Is.EqualTo(before + 1));
            Assert.That(bot.Calls, Is.EqualTo(2)); Assert.That(input.View.PotAmount, Is.EqualTo(4));
        }

        [TestCase(false)][TestCase(true)]
        public void AlreadyAppliedLocalActionIsNotRepeatedAfterFailureBeforeOrAfterRefresh(bool afterRead)
        {
            var table = Table(new SimpleDrawOpponent()); var input = new PokerInputController(table.Human);
            input.Bet(BettingAction.Call()); Guid hand = input.View.HandId; long before = input.View.Version;
            int actions = 0; bool fail = true;
            var runner = new PracticeProgressRunner(() => { actions++; return table.AdvanceOpponent(); }, () =>
            { if (afterRead) input.Refresh(); if (fail) throw new IOException(); input.Refresh(); });
            Assert.That(runner.Tick(), Is.EqualTo(PracticeProgressOutcome.Failed));
            Assert.That(table.Human.Read().Version, Is.EqualTo(before + 1));
            Assert.That(input.View.Version, Is.EqualTo(before + (afterRead ? 1 : 0)));
            fail = false; Assert.That(runner.Retry(), Is.EqualTo(PracticeProgressOutcome.Refreshed));
            Assert.That(actions, Is.EqualTo(1)); Assert.That(input.View.HandId, Is.EqualTo(hand));
            Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Exchange));
        }

        [Test]
        public void RunnerFieldsContainCallbacksAndStateButNoExceptionObjectOrPublicAuthority()
        {
            Type[] allowed = { typeof(Func<bool>), typeof(Action), typeof(bool), typeof(PracticeProgressFailure), typeof(string) };
            foreach (var field in typeof(PracticeProgressRunner).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Assert.That(allowed.Contains(field.FieldType), Is.True, field.Name);
            Assert.That(typeof(PracticeProgressRunner).GetProperties().Select(property => property.Name), Is.EquivalentTo(new[] { "Failure", "FailureType" }));
        }

        private static LocalPokerTable Table(IPokerOpponent bot)
        {
            var order = new[] { new SeatId(1), new SeatId(2) };
            var setup = new HandSetup(ChipLedger.Create(order.Select(seat => new SeatChips(seat, 100)).ToArray()), order, order, order, order, 1, 2);
            return new LocalPokerTable(setup, order[0], new FixedRandom(), bot);
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private sealed class ThrowOnceOpponent : IPokerOpponent
        { public int Calls; public HandCommand Choose(PokerPlayerView view) { if (++Calls == 1) throw new IOException(); return new SimpleDrawOpponent().Choose(view); } }
    }
}
