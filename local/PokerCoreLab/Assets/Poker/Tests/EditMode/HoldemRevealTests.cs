using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRevealTests
    {
        private static readonly SeatId A = new SeatId(1);
        private static readonly SeatId B = new SeatId(2);
        private static HoldemConfig Config(long stack = 100) =>
            new HoldemConfig(stack, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal);

        [TestCase(2, false)]
        [TestCase(4, false)]
        [TestCase(2, true)]
        [TestCase(4, true)]
        public void EachRevealStopsOnceWithoutChangingCardsOrPayouts(int seats, bool allIn)
        {
            var roster = Enumerable.Range(1, seats).Select(i => new SeatId(i)).ToArray();
            var ledger = ChipLedger.Create(roster.Select((s, i) => new SeatChips(s, 10 + 5 * i)).ToArray());
            var hand = HoldemHand.Begin(Guid.NewGuid(), ledger, roster, A, Config(), new SeededRandom(81), HoldemOddChipRule.ClockwiseFromButton);
            var automatic = HoldemHand.Begin(Guid.NewGuid(), ledger, roster, A,
                new HoldemConfig(100, 1, 2), new SeededRandom(81), HoldemOddChipRule.ClockwiseFromButton);
            int windows = 0, steps = 0;
            while (!hand.IsComplete && steps++ < 100)
            {
                Assert.That(hand.Ledger.TotalChips, Is.EqualTo(ledger.TotalChips));
                if (hand.IsRevealPending)
                {
                    windows++;
                    Assert.That((int)hand.Street, Is.EqualTo(windows));
                    Assert.That(hand.BoardCount, Is.EqualTo(windows + 2));
                    Assert.That(hand.RemainingCardCount, Is.EqualTo(52 - 2 * seats - hand.BoardCount - windows));
                    Assert.That(hand.CurrentSeat, Is.Null);
                    Assert.That(hand.IsSettlementPending, Is.False);
                    Assert.That(hand.Result, Is.Null);
                    Assert.Throws<InvalidOperationException>(() => hand.Apply(A, BettingAction.Fold()));
                    var before = hand;
                    hand = hand.ResumeAfterReveal(hand.Street);
                    Assert.That(hand.Version, Is.EqualTo(before.Version + 1));
                    for (int i = 0; i < before.BoardCount; i++)
                        Assert.That(hand.GetBoardCard(i), Is.EqualTo(before.GetBoardCard(i)));
                    if (!hand.IsRevealPending && !hand.IsComplete)
                    {
                        Assert.That(hand.CurrentSeat, Is.EqualTo(before.CurrentBetting.CurrentSeat));
                        Assert.That(hand.RemainingCardCount, Is.EqualTo(before.RemainingCardCount));
                        Assert.That(hand.Ledger, Is.SameAs(before.Ledger));
                        Assert.Throws<InvalidOperationException>(() => hand.ResumeAfterReveal(hand.Street));
                    }
                    continue;
                }
                var legal = hand.CurrentBetting.GetLegalActions();
                BettingAction action = allIn && (legal.CanBet || legal.CanRaise)
                    ? legal.CanBet ? BettingAction.BetTo(legal.MaximumAggressiveTarget.Value) : BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)
                    : legal.CanCheck ? BettingAction.Check() : BettingAction.Call();
                Assert.That(automatic.CurrentSeat, Is.EqualTo(hand.CurrentSeat));
                automatic = automatic.Apply(automatic.CurrentSeat.Value, action);
                hand = hand.Apply(hand.CurrentSeat.Value, action);
            }
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(automatic.IsComplete, Is.True);
            Assert.That(windows, Is.EqualTo(3));
            Assert.That(hand.Result.PotCount, Is.EqualTo(automatic.Result.PotCount));
            foreach (var seat in roster)
                Assert.That(hand.Ledger.GetChips(seat).Stack, Is.EqualTo(automatic.Ledger.GetChips(seat).Stack));
            for (int i = 0; i < 5; i++) Assert.That(hand.GetBoardCard(i), Is.EqualTo(automatic.GetBoardCard(i)));
        }

        [Test]
        public void BlindAllInStillWaitsAtAllThreeRevealsAndKeepsOpponentsPrivate()
        {
            var session = new HoldemSession(Guid.NewGuid(), Config(1), A, B, new SeededRandom(9));
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            HoldemRevealCommand first = null;
            HoldemReceipt firstReceipt = null;
            for (int window = 1; window <= 3; window++)
            {
                var view = session.GetSnapshot(A);
                Assert.That(view.IsRevealPending, Is.True);
                Assert.That((int)view.Street, Is.EqualTo(window));
                Assert.That(view.OwnStack, Is.Zero);
                Assert.That(view.IsOver, Is.False, "An all-in stack is not a loss before showdown.");
                AssertPrivate(session);
                var command = Resume(view);
                var receipt = session.ResumeAfterReveal(command);
                Assert.That(receipt.Accepted, Is.True);
                if (window == 1) { first = command; firstReceipt = receipt; }
                long version = session.Version;
                Assert.That(session.ResumeAfterReveal(first), Is.SameAs(firstReceipt));
                Assert.That(session.Version, Is.EqualTo(version), "Old accepted windows must not resume a later window.");
            }
            Assert.That(session.GetSnapshot(A).Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(session.GetSnapshot(A).OpponentCardCount, Is.EqualTo(2));
        }

        [Test]
        public void StaleWrongWindowAndCrossCommandRequestsCannotMutatePendingHand()
        {
            var session = new HoldemSession(Guid.NewGuid(), Config(), A, B, new SeededRandom(4));
            Guid startId = Guid.NewGuid();
            session.StartNextHand(Guid.NewGuid(), startId, 0);
            Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            var view = session.GetSnapshot(A);
            Assert.That(view.IsRevealPending, Is.True);
            AssertPrivate(session);
            var bad = new[] {
                new HoldemRevealCommand(Guid.NewGuid(), view.HandId, Guid.NewGuid(), view.SessionVersion, view.Street),
                new HoldemRevealCommand(view.SessionId, Guid.NewGuid(), Guid.NewGuid(), view.SessionVersion, view.Street),
                Resume(view, version: view.SessionVersion - 1),
                Resume(view, street: HoldemStreet.Turn),
                Resume(view, id: startId)
            };
            var errors = new[] { HoldemCommandError.WrongSession, HoldemCommandError.WrongHand,
                HoldemCommandError.VersionMismatch, HoldemCommandError.WrongRevealWindow, HoldemCommandError.CommandConflict };
            for (int i = 0; i < bad.Length; i++) Assert.That(session.ResumeAfterReveal(bad[i]).Error, Is.EqualTo(errors[i]));
            Assert.That(session.Submit(A, HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), A,
                view.SessionVersion, BettingAction.Check())).Error, Is.EqualTo(HoldemCommandError.RevealPending));
            Assert.That(session.ResolvePendingSettlement(Guid.NewGuid(), view.SessionVersion,
                HoldemOddChipRule.ClockwiseFromButton).Error, Is.EqualTo(HoldemCommandError.SettlementNotPending));
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), view.SessionVersion).Accepted, Is.False);
            Assert.That(session.Version, Is.EqualTo(view.SessionVersion));
            Assert.That(session.GetSnapshot(A).PotAmount, Is.EqualTo(view.PotAmount));
            Assert.That(session.GetSnapshot(A).OwnStack, Is.EqualTo(view.OwnStack));
            for (int i = 0; i < 3; i++) Assert.That(session.GetSnapshot(A).GetBoardCard(i), Is.EqualTo(view.GetBoardCard(i)));

            var command = Resume(view);
            var receipt = session.ResumeAfterReveal(command);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.ResumeAfterReveal(command), Is.SameAs(receipt));
            Assert.That(session.ResumeAfterReveal(Resume(view, id: command.CommandId, street: HoldemStreet.Turn)).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            var live = session.GetSnapshot(B);
            Assert.That(session.Submit(B, HoldemCommand.Act(live.SessionId, live.HandId, command.CommandId, B,
                live.SessionVersion, BettingAction.Check())).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.ResumeAfterReveal(Resume(live)).Error, Is.EqualTo(HoldemCommandError.RevealNotPending));
            Assert.That(session.Version, Is.EqualTo(view.SessionVersion + 1));
        }

        [Test]
        public void LocalNpcWaitsAndOnlyHostControlCanResume()
        {
            var table = new HoldemLocalTable(Config(), new SeededRandom(3), new SeededRandom(1), new PassivePolicy());
            var view = table.Human.Read();
            table.Human.Submit(HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), A, view.SessionVersion, BettingAction.Call()));
            Assert.That(table.AdvanceNpc(), Is.True);
            view = table.Human.Read();
            Assert.That(view.IsRevealPending, Is.True);
            Assert.That(table.AdvanceNpc(), Is.False);
            Assert.That(typeof(IHoldemPlayerPort).GetMethod("ResumeAfterReveal"), Is.Null);
            Assert.That(table.ResumeAfterReveal(Resume(view)).Accepted, Is.True);
            Assert.That(table.AdvanceNpc(), Is.True);
            Assert.That(table.Human.Read().CurrentSeat, Is.EqualTo(A));
        }

        [Test]
        public void FoldDoesNotDealOrWaitAndInvalidPoliciesAreRejected()
        {
            var hand = HoldemHand.Begin(Guid.NewGuid(), ChipLedger.Create(new[] { new SeatChips(A, 100), new SeatChips(B, 100) }),
                A, B, Config(), new SeededRandom(3));
            hand = hand.Apply(A, BettingAction.Fold());
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(hand.IsRevealPending, Is.False);
            Assert.That(hand.BoardCount, Is.Zero);
            Assert.That(hand.RemainingCardCount, Is.EqualTo(48));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemConfig(100, 1, 2, (HoldemRevealPolicy)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemRevealCommand(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), 0, HoldemStreet.Preflop));
        }

        private static void AssertPrivate(HoldemSession session)
        {
            foreach (var seat in new[] { A, B })
            {
                var view = session.GetSnapshot(seat);
                Assert.That(view.OwnCardCount, Is.EqualTo(2));
                Assert.That(view.OpponentCardCount, Is.Zero);
                Assert.That(view.CurrentSeat, Is.Null);
                Assert.That(view.LegalActions, Is.Null);
                Assert.That(view.Result, Is.Null);
                for (int i = 0; i < view.SeatCount; i++)
                {
                    Assert.That(view.GetSeatAt(i).RevealedBestCardCount, Is.Zero);
                    Assert.That(view.GetSeatAt(i).RevealedHandValue, Is.Null);
                    Assert.That(view.GetSeatAt(i).Awarded, Is.Zero);
                }
            }
        }
        private static HoldemRevealCommand Resume(HoldemSnapshot v, Guid? id = null, long? version = null, HoldemStreet? street = null)
            => new HoldemRevealCommand(v.SessionId, v.HandId, id ?? Guid.NewGuid(), version ?? v.SessionVersion, street ?? v.Street);
        private static void Act(HoldemSession session, BettingAction action)
        {
            var v = session.GetSnapshot(A);
            Assert.That(session.Submit(v.CurrentSeat.Value, HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(),
                v.CurrentSeat.Value, v.SessionVersion, action)).Accepted, Is.True);
        }
        private sealed class SeededRandom : IRandomSource
        { private readonly Random random; public SeededRandom(int seed) { random = new Random(seed); } public int NextInt(int maximum) => random.Next(maximum); }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot v, IRandomSource random) => v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
    }
}
