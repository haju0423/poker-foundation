using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemHistoryTests
    {
        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void HistoryStartsWithActualBlindsInPostingOrder(int seats)
        {
            var table = Make(seats); var v = table.Human.Read(); var h = Read(table);
            Assert.That(h.HandId, Is.EqualTo(v.HandId)); Assert.That(h.SessionId, Is.EqualTo(v.SessionId));
            Assert.That(h.HandNumber, Is.EqualTo(1)); Assert.That(h.Count, Is.EqualTo(2));
            Assert.That(h.GetEntry(0).Seat, Is.EqualTo(v.SmallBlindSeat));
            Assert.That(h.GetEntry(0).Kind, Is.EqualTo(HoldemHistoryKind.SmallBlind));
            Assert.That(h.GetEntry(0).Amount, Is.EqualTo(1));
            Assert.That(h.GetEntry(1).Seat, Is.EqualTo(v.BigBlindSeat));
            Assert.That(h.GetEntry(1).Amount, Is.EqualTo(2));
            Assert.That(h.GetEntry(1).IsAllIn, Is.False);
        }

        [Test]
        public void AcceptedActionsAppendOnceAndPreviouslyReadHistoryDoesNotChange()
        {
            var table = Make(); var v = table.Human.Read(); var initial = Read(table);
            var call = Command(v, BettingAction.Call());
            Assert.That(table.Human.Submit(call).Accepted, Is.True);
            var after = Read(table); var entry = after.GetEntry(2);
            Assert.That(initial.Count, Is.EqualTo(2)); Assert.That(after.Count, Is.EqualTo(3));
            Assert.That(entry.Amount, Is.EqualTo(1)); Assert.That(entry.StreetTotal, Is.EqualTo(2));
            Assert.That(entry.Street, Is.EqualTo(HoldemStreet.Preflop)); Assert.That(entry.Action, Is.EqualTo(BettingActionKind.Call));
            Assert.That(table.Human.Submit(call).Accepted, Is.True);
            Assert.That(Read(table), Is.SameAs(after));
            Assert.That(table.Human.Submit(Command(v, BettingAction.Fold())).Accepted, Is.False);
            Assert.That(Read(table), Is.SameAs(after));
            Assert.That(table.AdvanceNpc(), Is.True);
            Assert.That(Read(table).GetEntry(3).Street, Is.EqualTo(HoldemStreet.Preflop), "Use the street before the action opens the flop.");
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(after.Count, Is.EqualTo(3));
        }

        [Test]
        public void RaisesKeepAdditionalAndTotalAmountsDistinctAndRefundsAreNotPotAwards()
        {
            var table = Make(); Act(table, BettingAction.RaiseTo(10));
            var raise = Read(table).GetEntry(2);
            Assert.That(raise.Amount, Is.EqualTo(9)); Assert.That(raise.StreetTotal, Is.EqualTo(10));
            Assert.That(raise.IsAllIn, Is.False);

            table = Make(); Act(table, BettingAction.Fold());
            var h = Read(table); var refund = h.GetEntry(3);
            Assert.That(h.Count, Is.EqualTo(4)); Assert.That(refund.Kind, Is.EqualTo(HoldemHistoryKind.UncalledReturn));
            Assert.That(refund.Seat, Is.EqualTo(table.Human.Read().BigBlindSeat));
            Assert.That(refund.Amount, Is.EqualTo(1));
            Assert.That(table.Human.Read().Result.PotAmount, Is.EqualTo(2));
            AssertAccounting(table);
        }

        [Test]
        public void NewHandClearsOnlyAfterSuccessfulStartAndUsesRemainingStackForBlinds()
        {
            var table = Make(stack: 2); var initial = Read(table);
            Assert.That(table.Human.NextHand(table.Human.Read().SessionVersion).Accepted, Is.False);
            Assert.That(Read(table), Is.SameAs(initial));
            Act(table, BettingAction.Fold()); var previous = Read(table); var end = table.Human.Read();
            Assert.That(table.Human.NextHand(end.SessionVersion - 1).Accepted, Is.False);
            Assert.That(Read(table), Is.SameAs(previous));
            Assert.That(table.Human.NextHand(end.SessionVersion).Accepted, Is.True);
            var next = Read(table);
            Assert.That(next.HandId, Is.Not.EqualTo(previous.HandId)); Assert.That(next.HandNumber, Is.EqualTo(2));
            Assert.That(next.GetEntry(1).Amount, Is.EqualTo(1)); Assert.That(next.GetEntry(1).IsAllIn, Is.True);
            Assert.That(Entries(next).Any(e => e.Kind == HoldemHistoryKind.Action), Is.False);
            Assert.That(previous.GetEntry(2).Action, Is.EqualTo(BettingActionKind.Fold));
        }

        [TestCase(HoldemRevealPolicy.Automatic)]
        [TestCase(HoldemRevealPolicy.PauseAfterCommunityReveal)]
        public void AllInRunoutRecordsNoInventedBettingActionsOrAwardAsRefund(HoldemRevealPolicy policy)
        {
            var table = Make(stack: 2, policy: policy);
            Act(table, BettingAction.Call());
            var h = Read(table); Assert.That(h.GetEntry(2).IsAllIn, Is.True);
            while (table.Human.Read().IsRevealPending) Resume(table);
            Assert.That(table.Human.Read().Result, Is.Not.Null);
            Assert.That(Read(table).Count, Is.EqualTo(3));
            AssertAccounting(table);
        }

        [Test]
        public void BlindsAloneCanFinishAHandWithoutFakeRefunds()
        {
            var table = Make(stack: 1);
            Assert.That(table.Human.Read().Result, Is.Not.Null);
            Assert.That(Read(table).Count, Is.EqualTo(2)); AssertAccounting(table);
        }

        [TestCase(HoldemRevealPolicy.Automatic)]
        [TestCase(HoldemRevealPolicy.PauseAfterCommunityReveal)]
        public void SeededHandsReconcileHistoryToLiveCommitmentsAndSettledPots(HoldemRevealPolicy policy)
        {
            for (int seed = 0; seed < 12; seed++)
            {
                var random = new Random(seed);
                var table = new HoldemLocalTable(new HoldemConfig(30, 1, 2, policy), 4,
                    new Seeded(seed), new Seeded(seed + 100), new HoldemNpcPolicy(4), HoldemOddChipRule.ClockwiseFromButton);
                for (int hand = 0; hand < 5; hand++)
                {
                    int steps = 0;
                    while (table.Human.Read().Result == null && steps++ < 200)
                    {
                        var v = table.Human.Read(); var legal = v.LegalActions;
                        if (v.IsRevealPending) Resume(table);
                        else if (legal == null) Assert.That(table.AdvanceNpc(), Is.True);
                        else if (random.Next(4) == 0 && (legal.CanBet || legal.CanRaise))
                            Act(table, legal.CanBet ? BettingAction.BetTo(legal.MaximumAggressiveTarget.Value) : BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value));
                        else if (random.Next(5) == 0) Act(table, BettingAction.Fold());
                        else Act(table, legal.CanCheck ? BettingAction.Check() : BettingAction.Call());
                        AssertAccounting(table);
                    }
                    var end = table.Human.Read(); Assert.That(end.Result, Is.Not.Null, "seed=" + seed);
                    if (!end.CanContinue) break;
                    Assert.That(table.Human.NextHand(end.SessionVersion).Accepted, Is.True); AssertAccounting(table);
                }
            }
        }

        private static void AssertAccounting(HoldemLocalTable table)
        {
            var v = table.Human.Read(); long paid = 0, returned = 0;
            foreach (var e in Entries(Read(table)))
                if (e.Kind == HoldemHistoryKind.UncalledReturn) returned += e.Amount; else paid += e.Amount;
            Assert.That(paid - returned, Is.EqualTo(v.PotAmount));
        }
        private static HoldemHistoryEntry[] Entries(HoldemHandHistory h) => Enumerable.Range(0, h.Count).Select(h.GetEntry).ToArray();
        private static HoldemHandHistory Read(HoldemLocalTable table) => ((IHoldemHistoryPort)table.Human).ReadHistory();
        private static HoldemLocalTable Make(int seats = 2, long stack = 100, HoldemRevealPolicy policy = HoldemRevealPolicy.Automatic)
            => new HoldemLocalTable(new HoldemConfig(stack, 1, 2, policy), seats, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
        private static HoldemCommand Command(HoldemSnapshot v, BettingAction action)
            => HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), v.ViewerSeat, v.SessionVersion, action);
        private static void Act(HoldemLocalTable table, BettingAction action) => Assert.That(table.Human.Submit(Command(table.Human.Read(), action)).Accepted, Is.True);
        private static void Resume(HoldemLocalTable table)
        {
            var v = table.Human.Read();
            Assert.That(table.ResumeAfterReveal(new HoldemRevealCommand(v.SessionId, v.HandId, Guid.NewGuid(), v.SessionVersion, v.Street)).Accepted, Is.True);
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class Seeded : IRandomSource
        { private readonly Random random; public Seeded(int seed) { random = new Random(seed); } public int NextInt(int upper) => random.Next(upper); }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot v, IRandomSource random) => v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
    }
}
