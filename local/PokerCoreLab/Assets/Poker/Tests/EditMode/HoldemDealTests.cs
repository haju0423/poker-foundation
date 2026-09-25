using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemDealTests
    {
        private static readonly SeatId A = new SeatId(1);
        private static readonly SeatId B = new SeatId(2);

        private static HoldemConfig Config(long stack = 100, bool revealPause = true, bool accusations = false) =>
            new HoldemConfig(stack, 1, 2,
                revealPause ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                accusations ? HoldemAccusationMode.CollectLatestChoiceUntilHostCloses : HoldemAccusationMode.Disabled,
                HoldemDealPolicy.WaitForHost);

        [TestCase(2, false, false)] [TestCase(2, false, true)]
        [TestCase(2, true, false)] [TestCase(2, true, true)]
        [TestCase(3, false, false)] [TestCase(3, false, true)]
        [TestCase(3, true, false)] [TestCase(3, true, true)]
        [TestCase(4, false, false)] [TestCase(4, false, true)]
        [TestCase(4, true, false)] [TestCase(4, true, true)]
        public void NeutralDealsMatchAutomaticCardsStacksAndSidePots(int count, bool allIn, bool revealPause)
        {
            var roster = Enumerable.Range(1, count).Select(i => new SeatId(i)).ToArray();
            var ledger = ChipLedger.Create(roster.Select((s, i) => new SeatChips(s, 10 + 5 * i)).ToArray());
            var hand = HoldemHand.Begin(Guid.NewGuid(), ledger, roster, A, Config(revealPause: revealPause),
                new SeededRandom(81), HoldemOddChipRule.ClockwiseFromButton);
            var baseline = HoldemHand.Begin(Guid.NewGuid(), ledger, roster, A, new HoldemConfig(100, 1, 2),
                new SeededRandom(81), HoldemOddChipRule.ClockwiseFromButton);
            int deals = 0, reveals = 0, steps = 0;
            while (!hand.IsComplete && steps++ < 100)
            {
                Assert.That(hand.Ledger.TotalChips, Is.EqualTo(ledger.TotalChips));
                if (hand.IsDealPending)
                {
                    Assert.That((int)hand.Street, Is.EqualTo(deals));
                    Assert.That(hand.BoardCount, Is.EqualTo(deals == 0 ? 0 : deals + 2));
                    Assert.That(hand.RemainingCardCount, Is.EqualTo(52 - 2 * count - hand.BoardCount - deals));
                    Assert.That(hand.CurrentSeat, Is.Null);
                    Assert.That(hand.IsRevealPending, Is.False);
                    Assert.That(hand.IsSettlementPending, Is.False);
                    Assert.Throws<InvalidOperationException>(() => hand.Apply(A, BettingAction.Check()));
                    Assert.Throws<InvalidOperationException>(() => hand.ResumeAfterReveal(HoldemStreet.Flop));
                    var before = hand;
                    hand = hand.DealUnchanged(hand.PendingDealStreet.Value);
                    Assert.That(hand.RemainingCardCount, Is.EqualTo(before.RemainingCardCount - (deals == 0 ? 4 : 2)));
                    Assert.That(hand.Version, Is.EqualTo(before.Version + 1));
                    Assert.That(before.BoardCount, Is.EqualTo(deals == 0 ? 0 : deals + 2), "Old state remains immutable.");
                    deals++;
                    continue;
                }
                if (hand.IsRevealPending)
                {
                    reveals++;
                    Assert.That(hand.PendingDealStreet, Is.Null);
                    hand = hand.ResumeAfterReveal(hand.Street);
                    continue;
                }
                var legal = hand.CurrentBetting.GetLegalActions();
                var action = allIn && (legal.CanBet || legal.CanRaise)
                    ? legal.CanBet ? BettingAction.BetTo(legal.MaximumAggressiveTarget.Value)
                        : BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)
                    : legal.CanCheck ? BettingAction.Check() : BettingAction.Call();
                Assert.That(hand.CurrentSeat, Is.EqualTo(baseline.CurrentSeat));
                baseline = baseline.Apply(baseline.CurrentSeat.Value, action);
                hand = hand.Apply(hand.CurrentSeat.Value, action);
            }
            Assert.That(hand.IsComplete, Is.True);
            Assert.That(baseline.IsComplete, Is.True);
            Assert.That(deals, Is.EqualTo(3));
            Assert.That(reveals, Is.EqualTo(revealPause ? 3 : 0));
            Assert.That(hand.Result.PotCount, Is.EqualTo(baseline.Result.PotCount));
            foreach (var seat in roster)
                Assert.That(hand.Ledger.GetChips(seat).Stack, Is.EqualTo(baseline.Ledger.GetChips(seat).Stack));
            for (int i = 0; i < 5; i++) Assert.That(hand.GetBoardCard(i), Is.EqualTo(baseline.GetBoardCard(i)));
        }

        [Test]
        public void HostReleaseOpensAccusationsOnlyAfterRevealAndKeepsCardsPrivate()
        {
            var session = Start(Config(accusations: true));
            ToFirstDeal(session);
            var before = session.GetSnapshot(A);
            Assert.That(before.IsDealPending, Is.True);
            Assert.That(before.Street, Is.EqualTo(HoldemStreet.Preflop));
            Assert.That(before.BoardCount, Is.Zero);
            Assert.That(before.Accusations, Is.Null);
            Assert.That(before.LegalActions, Is.Null);
            Assert.That(before.OpponentCardCount, Is.Zero);
            Assert.That(session.GetSnapshot(B).PendingDeal, Is.SameAs(before.PendingDeal));
            Assert.That(session.Submit(A, Act(before, BettingAction.Check())).Error, Is.EqualTo(HoldemCommandError.DealPending));
            Assert.That(session.ResumeAfterReveal(Resume(before, HoldemStreet.Flop)).Error, Is.EqualTo(HoldemCommandError.RevealNotPending));
            Assert.That(session.DealUnchanged(Deal(before)).Accepted, Is.True);
            var revealed = session.GetSnapshot(A);
            Assert.That(revealed.IsDealPending, Is.False);
            Assert.That(revealed.PendingDeal, Is.Null);
            Assert.That(revealed.IsRevealPending, Is.True);
            Assert.That(revealed.BoardCount, Is.EqualTo(3));
            Assert.That(revealed.Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.Collecting));
            Assert.That(session.ResumeAfterReveal(Resume(revealed)).Error, Is.EqualTo(HoldemCommandError.AccusationWindowNotClosed));
            foreach (var seat in new[] { A, B })
            {
                var v = session.GetSnapshot(seat);
                Assert.That(session.SubmitAccusationChoice(seat, new HoldemAccusationChoiceCommand(v.SessionId, v.HandId,
                    v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, seat, null)).Accepted, Is.True);
            }
            var ready = session.GetSnapshot(A);
            session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(ready.SessionId, ready.HandId,
                ready.Accusations.WindowId, Guid.NewGuid(), ready.SessionVersion, ready.Street));
            Assert.That(session.ResumeAfterReveal(Resume(session.GetSnapshot(A))).Accepted, Is.True);
            Assert.That(session.GetSnapshot(B).CurrentSeat, Is.EqualTo(B));
            Assert.That(session.GetSnapshot(B).OpponentCardCount, Is.Zero);
            Assert.That(typeof(IHoldemPlayerPort).GetMethod("DealUnchanged"), Is.Null);
        }

        [Test]
        public void StaleCrossTypeAndWrongWindowCommandsCannotChangeDeckOrChips()
        {
            var session = Start(Config()); ToFirstDeal(session);
            var view = session.GetSnapshot(A); var pending = view.PendingDeal;
            var bad = new[] {
                new HoldemDealCommand(Guid.NewGuid(), view.HandId, pending.WindowId, Guid.NewGuid(), view.SessionVersion, pending.Street),
                new HoldemDealCommand(view.SessionId, Guid.NewGuid(), pending.WindowId, Guid.NewGuid(), view.SessionVersion, pending.Street),
                new HoldemDealCommand(view.SessionId, view.HandId, Guid.NewGuid(), Guid.NewGuid(), view.SessionVersion, pending.Street),
                new HoldemDealCommand(view.SessionId, view.HandId, pending.WindowId, Guid.NewGuid(), view.SessionVersion, HoldemStreet.Turn),
                new HoldemDealCommand(view.SessionId, view.HandId, pending.WindowId, Guid.NewGuid(), view.SessionVersion - 1, pending.Street)
            };
            var errors = new[] { HoldemCommandError.WrongSession, HoldemCommandError.WrongHand,
                HoldemCommandError.WrongDealWindow, HoldemCommandError.WrongDealWindow, HoldemCommandError.VersionMismatch };
            for (int i = 0; i < bad.Length; i++) Assert.That(session.DealUnchanged(bad[i]).Error, Is.EqualTo(errors[i]));
            Assert.That(session.Version, Is.EqualTo(view.SessionVersion));
            Assert.That(session.GetSnapshot(A).BoardCount, Is.Zero);
            Assert.That(session.GetSnapshot(A).PotAmount, Is.EqualTo(view.PotAmount));
            var command = Deal(view); var receipt = session.DealUnchanged(command);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.DealUnchanged(command), Is.SameAs(receipt));
            Assert.That(session.DealUnchanged(new HoldemDealCommand(view.SessionId, view.HandId, pending.WindowId,
                command.CommandId, view.SessionVersion, HoldemStreet.Turn)).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            var revealed = session.GetSnapshot(A);
            Assert.That(session.ResumeAfterReveal(new HoldemRevealCommand(view.SessionId, view.HandId,
                command.CommandId, revealed.SessionVersion, revealed.Street)).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.Submit(A, HoldemCommand.Act(view.SessionId, view.HandId, command.CommandId, A,
                revealed.SessionVersion, BettingAction.Check())).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.DealUnchanged(Deal(view, version: revealed.SessionVersion)).Error, Is.EqualTo(HoldemCommandError.DealNotPending));
            Assert.That(session.Version, Is.EqualTo(view.SessionVersion + 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void BlindAllInVisitsThreeFreshWindowsAndEliminatesOnlyAfterSettlement(bool revealPause)
        {
            var session = Start(Config(1, revealPause));
            var first = Deal(session.GetSnapshot(A)); HoldemReceipt firstReceipt = null;
            Guid oldWindow = Guid.Empty;
            for (int street = 1; street <= 3; street++)
            {
                var v = session.GetSnapshot(A);
                Assert.That(v.OwnStack, Is.Zero);
                Assert.That(v.IsOver, Is.False);
                Assert.That(v.GetSeat(A).Status, Is.EqualTo(HoldemSeatStatus.AllIn));
                Assert.That((int)v.PendingDeal.Street, Is.EqualTo(street));
                Assert.That(v.PendingDeal.WindowId, Is.Not.EqualTo(oldWindow));
                oldWindow = v.PendingDeal.WindowId;
                if (street > 1)
                {
                    Assert.That(session.DealUnchanged(first), Is.SameAs(firstReceipt));
                    Assert.That(session.Version, Is.EqualTo(v.SessionVersion));
                    Assert.That(session.DealUnchanged(new HoldemDealCommand(v.SessionId, v.HandId, first.WindowId,
                        Guid.NewGuid(), v.SessionVersion, v.PendingDeal.Street)).Error, Is.EqualTo(HoldemCommandError.WrongDealWindow));
                }
                var receipt = session.DealUnchanged(street == 1 ? first : Deal(v));
                Assert.That(receipt.Accepted, Is.True);
                if (street == 1) firstReceipt = receipt;
                if (revealPause) session.ResumeAfterReveal(Resume(session.GetSnapshot(A)));
            }
            var complete = session.GetSnapshot(A);
            Assert.That(complete.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(complete.OpponentCardCount, Is.EqualTo(2));
            Assert.That(complete.PendingDeal, Is.Null);
            Assert.That(complete.GetSeat(A).Stack + complete.GetSeat(B).Stack, Is.EqualTo(2));
        }

        [Test]
        public void FoldToOneSkipsDealAndNextHandDoesNotReuseOldWindow()
        {
            var session = Start(Config());
            var v = session.GetSnapshot(A);
            session.Submit(A, Act(v, BettingAction.Fold()));
            Assert.That(session.PendingDeal, Is.Null);
            Assert.That(session.GetSnapshot(A).Result.Kind, Is.EqualTo(HoldemResultKind.Fold));
            Assert.That(session.GetSnapshot(A).BoardCount, Is.Zero);
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version).Accepted, Is.True);
            ToFirstDeal(session);
            var first = Deal(session.GetSnapshot(A));
            session.DealUnchanged(first); session.ResumeAfterReveal(Resume(session.GetSnapshot(A)));
            var actor = session.GetSnapshot(A).CurrentSeat.Value;
            session.Submit(actor, Act(session.GetSnapshot(actor), BettingAction.Fold()));
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version).Accepted, Is.True);
            ToFirstDeal(session);
            Assert.That(session.DealUnchanged(first).Error, Is.EqualTo(HoldemCommandError.WrongHand));
            var latest = session.GetSnapshot(A);
            Assert.That(latest.PendingDeal.WindowId, Is.Not.EqualTo(first.WindowId));
            Assert.That(session.DealUnchanged(Deal(latest, id: first.CommandId)).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
        }

        [Test]
        public void InvalidConfigAndEnvelopeFailBeforeAnyPlay()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemConfig(100, 1, 2, dealPolicy: (HoldemDealPolicy)99));
            Assert.Throws<ArgumentException>(() => new HoldemDealCommand(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, HoldemStreet.Flop));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), -1, HoldemStreet.Flop));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, HoldemStreet.Complete));
            var session = Start(new HoldemConfig(100, 1, 2));
            Assert.That(session.PendingDeal, Is.Null);
        }

        [Test]
        public void WatchdogUsesExactDeadlineAndRearmsForEachAllInStreet()
        {
            var table = new HoldemLocalTable(Config(1, revealPause: false), new SeededRandom(9), new SeededRandom(2));
            TimeSpan now = TimeSpan.Zero;
            var timeout = new HoldemDealTimeout(table.Human.Read, table.DealUnchanged, TimeSpan.FromSeconds(5), () => now);
            for (int street = 1; street <= 3; street++)
            {
                var pending = table.Human.Read();
                Assert.That((int)pending.PendingDeal.Street, Is.EqualTo(street));
                Assert.That(table.AdvanceNpc(), Is.False);
                Assert.That(timeout.Poll(), Is.Null, "A new window gets its own wait.");
                now += TimeSpan.FromMilliseconds(4999);
                Assert.That(timeout.Poll(), Is.Null);
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(pending.SessionVersion));
                now += TimeSpan.FromMilliseconds(1);
                Assert.That(timeout.Poll().Accepted, Is.True);
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(pending.SessionVersion + 1));
            }
            Assert.That(table.Human.Read().Result, Is.Not.Null);
            Assert.That(timeout.Poll(), Is.Null);
            Assert.That(table.GetAccusationDecisions(), Is.Empty, "A timeout must not fabricate a verdict.");
            Assert.That(((IHoldemHistoryPort)table.Human).ReadHistory().Count, Is.EqualTo(2), "No fake bets or refunds.");
        }

        [Test]
        public void WatchdogDoesNotReleaseManuallyCompletedOrLaterWindowEarly()
        {
            var session = Start(Config(1, revealPause: false)); TimeSpan now = TimeSpan.Zero;
            var timeout = new HoldemDealTimeout(() => session.GetSnapshot(A), session.DealUnchanged, TimeSpan.FromSeconds(5), () => now);
            Assert.That(timeout.Poll(), Is.Null);
            var old = Deal(session.GetSnapshot(A));
            now = TimeSpan.FromSeconds(4);
            Assert.That(session.DealUnchanged(old).Accepted, Is.True);
            now = TimeSpan.FromSeconds(5);
            Assert.That(timeout.Poll(), Is.Null, "The expired first deadline cannot release the second deal.");
            now = TimeSpan.FromSeconds(9);
            Assert.That(timeout.Poll(), Is.Null);
            now = TimeSpan.FromSeconds(10);
            Assert.That(timeout.Poll().Accepted, Is.True);
            Assert.That(session.PendingDeal.Street, Is.EqualTo(HoldemStreet.River));
        }

        [Test]
        public void WatchdogStaleSnapshotCannotSkipADealWhenAnotherCompletionWins()
        {
            var session = Start(Config(1, revealPause: false)); TimeSpan now = TimeSpan.Zero;
            var timeout = new HoldemDealTimeout(() => session.GetSnapshot(A), command =>
            {
                Assert.That(session.DealUnchanged(Deal(session.GetSnapshot(A))).Accepted, Is.True);
                return session.DealUnchanged(command);
            }, TimeSpan.FromSeconds(1), () => now);
            timeout.Poll(); now = TimeSpan.FromSeconds(1);
            Assert.That(timeout.Poll().Error, Is.EqualTo(HoldemCommandError.VersionMismatch));
            Assert.That(session.PendingDeal.Street, Is.EqualTo(HoldemStreet.Turn));
            Assert.That(session.GetSnapshot(A).BoardCount, Is.EqualTo(3));
        }

        [Test]
        public void WatchdogRejectsInvalidDurationAndBackwardClockWithoutChangingGame()
        {
            var session = Start(Config(1)); TimeSpan now = TimeSpan.Zero;
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemDealTimeout(() => session.GetSnapshot(A), session.DealUnchanged, TimeSpan.Zero, () => now));
            var timeout = new HoldemDealTimeout(() => session.GetSnapshot(A), session.DealUnchanged, TimeSpan.FromSeconds(5), () => now);
            timeout.Poll(); now = TimeSpan.FromSeconds(2); timeout.Poll();
            now = TimeSpan.FromSeconds(1);
            Assert.Throws<InvalidOperationException>(() => timeout.Poll());
            Assert.That(session.Version, Is.EqualTo(1));
            Assert.That(session.GetSnapshot(A).BoardCount, Is.Zero);
        }

        private static HoldemSession Start(HoldemConfig config)
        {
            var session = new HoldemSession(Guid.NewGuid(), config, A, B, new SeededRandom(9));
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            return session;
        }
        private static void ToFirstDeal(HoldemSession session)
        {
            int steps = 0;
            while (!session.GetSnapshot(A).IsDealPending && steps++ < 10)
            {
                var actor = session.GetSnapshot(A).CurrentSeat.Value;
                var v = session.GetSnapshot(actor);
                Assert.That(session.Submit(actor, Act(v, v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            Assert.That(session.PendingDeal, Is.Not.Null);
        }
        private static HoldemCommand Act(HoldemSnapshot v, BettingAction action) =>
            HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), v.ViewerSeat, v.SessionVersion, action);
        private static HoldemDealCommand Deal(HoldemSnapshot v, Guid? id = null, long? version = null) =>
            new HoldemDealCommand(v.SessionId, v.HandId, v.PendingDeal.WindowId, id ?? Guid.NewGuid(),
                version ?? v.SessionVersion, v.PendingDeal.Street);
        private static HoldemRevealCommand Resume(HoldemSnapshot v, HoldemStreet? street = null) =>
            new HoldemRevealCommand(v.SessionId, v.HandId, Guid.NewGuid(), v.SessionVersion, street ?? v.Street);

        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int upper) => random.Next(upper);
        }
    }
}
