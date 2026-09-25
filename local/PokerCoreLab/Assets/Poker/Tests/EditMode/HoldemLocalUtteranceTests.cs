using System;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemLocalUtteranceTests
    {
        [Test]
        public void OrdinaryTableDoesNotEnableSpeechOrChangeItsPlayerContract()
        {
            var table = Create(null);
            Assert.That(table.HumanUtterances, Is.Null);
            Assert.That(table.ReadClosedUtteranceBatches(), Is.Empty);
            Assert.That(table.Human, Is.Not.InstanceOf<IHoldemUtterancePlayerPort>());
        }

        [Test]
        public void LocalHostClosesEveryBatchBeforeNeutralDealWithoutChangingPokerHistory()
        {
            var table = Create(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active));
            var port = table.HumanUtterances;
            for (int street = 0; street < 3; street++)
            {
                var before = table.Human.Read();
                var history = ((IHoldemHistoryPort)table.Human).ReadHistory();
                var view = port.ReadUtterances();
                Assert.That(view.CanSubmit, Is.True);
                var message = new HoldemUtteranceCommand(view.SessionId, view.HandId, view.WindowId,
                    Guid.NewGuid(), view.Street, view.ViewerSeat, "접수 " + street);
                Assert.That(port.SubmitUtterance(message).Accepted, Is.True);
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
                Assert.That(((IHoldemHistoryPort)table.Human).ReadHistory(), Is.SameAs(history));
                EndBetting(table);
                var state = table.Human.Read();
                Assert.That(state.IsDealPending, Is.True);
                var batches = table.ReadClosedUtteranceBatches();
                Assert.That(batches.Count, Is.EqualTo(street + 1));
                Assert.That(batches[street].GetEntry(0).Text, Is.EqualTo(message.Text));
                Assert.That(port.ReadUtterances().CanSubmit, Is.False);
                Assert.That(table.DealUnchanged(new HoldemDealCommand(state.SessionId, state.HandId,
                    state.PendingDeal.WindowId, Guid.NewGuid(), state.SessionVersion, state.PendingDeal.Street)).Accepted, Is.True);
                state = table.Human.Read();
                Assert.That(port.ReadUtterances().CanSubmit, Is.False);
                Assert.That(table.ResumeAfterReveal(new HoldemRevealCommand(state.SessionId, state.HandId,
                    Guid.NewGuid(), state.SessionVersion, state.Street)).Accepted, Is.True);
            }
            Assert.That(port.ReadUtterances().CanSubmit, Is.False, "No river request window.");
            EndBetting(table);
            var retained = table.ReadClosedUtteranceBatches();
            Assert.That(table.Human.NextHand(table.Human.Read().SessionVersion).Accepted, Is.True);
            Assert.That(table.ReadClosedUtteranceBatches(), Is.Empty);
            Assert.That(port.ReadUtterances().Count, Is.Zero);
            Assert.That(retained.Count, Is.EqualTo(3));
            Assert.That(retained[0].GetEntry(0).Text, Is.EqualTo("접수 0"));
        }

        [Test]
        public void FoldToOneFreezesSpeechWithoutOpeningADealerGate()
        {
            var table = Create(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active), 2);
            var own = table.HumanUtterances.ReadUtterances();
            Assert.That(table.HumanUtterances.SubmitUtterance(new HoldemUtteranceCommand(own.SessionId,
                own.HandId, own.WindowId, Guid.NewGuid(), own.Street, own.ViewerSeat, "원문")).Accepted, Is.True);
            var state = table.Human.Read();
            Assert.That(table.Human.Submit(HoldemCommand.Act(state.SessionId, state.HandId, Guid.NewGuid(),
                state.ViewerSeat, state.SessionVersion, BettingAction.Fold())).Accepted, Is.True);
            Assert.That(table.Human.Read().Result, Is.Not.Null);
            Assert.That(table.Human.Read().IsDealPending, Is.False);
            Assert.That(table.HumanUtterances.ReadUtterances().CanSubmit, Is.False);
            Assert.That(table.ReadClosedUtteranceBatches().Count, Is.EqualTo(1));
        }

        private static HoldemLocalTable Create(HoldemUtterancePolicy policy, int count = 4)
            => new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), count, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton, policy);
        private static void EndBetting(HoldemLocalTable table)
        {
            int guard = 0;
            while (table.Human.Read().CurrentSeat.HasValue && guard++ < 20)
            {
                var state = table.Human.Read();
                if (state.CurrentSeat != state.ViewerSeat) Assert.That(table.AdvanceNpc(), Is.True);
                else Assert.That(table.Human.Submit(HoldemCommand.Act(state.SessionId, state.HandId, Guid.NewGuid(),
                    state.ViewerSeat, state.SessionVersion, state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            Assert.That(guard, Is.LessThan(20));
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        {
            public BettingAction Choose(HoldemSnapshot view, IRandomSource random)
                => view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call();
        }
    }
}
