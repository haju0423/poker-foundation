using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemSessionSimulationTests
    {
        [TestCase(2, HoldemRevealPolicy.Automatic, HoldemDealPolicy.Automatic)]
        [TestCase(3, HoldemRevealPolicy.Automatic, HoldemDealPolicy.Automatic)]
        [TestCase(4, HoldemRevealPolicy.Automatic, HoldemDealPolicy.Automatic)]
        [TestCase(2, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.Automatic)]
        [TestCase(3, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.Automatic)]
        [TestCase(4, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.Automatic)]
        [TestCase(2, HoldemRevealPolicy.Automatic, HoldemDealPolicy.WaitForHost)]
        [TestCase(3, HoldemRevealPolicy.Automatic, HoldemDealPolicy.WaitForHost)]
        [TestCase(4, HoldemRevealPolicy.Automatic, HoldemDealPolicy.WaitForHost)]
        [TestCase(2, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.WaitForHost)]
        [TestCase(3, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.WaitForHost)]
        [TestCase(4, HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemDealPolicy.WaitForHost)]
        public void SeededSessionsPreserveChipsCardsAndPrivacyAcrossHands(int seatCount, HoldemRevealPolicy policy, HoldemDealPolicy dealPolicy)
        {
            int completedHands = 0, decisionsMade = 0, eliminated = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var decisions = new Random(seed * 37 + seatCount);
                var seats = new SeatId[seatCount];
                var entries = new SeatChips[seatCount];
                var priorStacks = new long[seatCount];
                long total = 0;
                for (int i = 0; i < seatCount; i++)
                {
                    seats[i] = new SeatId(i + 1);
                    priorStacks[i] = 3 + decisions.Next(98);
                    entries[i] = new SeatChips(seats[i], priorStacks[i]); total += priorStacks[i];
                }
                int id = 1;
                var session = new HoldemSession(Id(id++), new HoldemConfig(100, 1, 2, policy, dealPolicy: dealPolicy),
                    ChipLedger.Create(entries), seats, seats[seed % seatCount], new SeededRandom(seed),
                    HoldemOddChipRule.ClockwiseFromButton);
                for (int hand = 0; hand < 30 && session.CanContinue; hand++)
                {
                    string context = "seats=" + seatCount + " policy=" + policy + " deal=" + dealPolicy + " seed=" + seed + " hand=" + hand;
                    Assert.That(session.StartNextHand(Id(id++), Id(id++), session.Version).Accepted, Is.True, context);
                    var view = session.GetSnapshot(seats[0]);
                    for (int i = 0; i < seatCount; i++)
                    {
                        var seat = view.GetSeatAt(i);
                        Assert.That(seat.WasDealtIn, Is.EqualTo(priorStacks[i] > 0), context);
                        // An automatic all-in runout can settle immediately when posting blinds.
                        if (view.Result == null)
                            Assert.That(seat.Stack + seat.Committed, Is.EqualTo(priorStacks[i]), context);
                    }
                    int steps = 0;
                    while (true)
                    {
                        AssertState(session, seats, total, context + " step=" + steps);
                        view = session.GetSnapshot(seats[0]);
                        if (view.Result != null) break;
                        Assert.That(++steps, Is.LessThanOrEqualTo(256), context + " hand did not finish");
                        if (view.IsDealPending)
                        {
                            var dealCommand = new HoldemDealCommand(view.SessionId, view.HandId, view.PendingDeal.WindowId,
                                Id(id++), view.SessionVersion, view.PendingDeal.Street);
                            var dealReceipt = session.DealUnchanged(dealCommand);
                            Assert.That(dealReceipt.Accepted, Is.True, context);
                            Assert.That(session.DealUnchanged(dealCommand), Is.SameAs(dealReceipt), context);
                            Assert.That(session.Version, Is.EqualTo(view.SessionVersion + 1), context);
                            continue;
                        }
                        if (view.IsRevealPending)
                        {
                            Assert.That(session.ResumeAfterReveal(new HoldemRevealCommand(view.SessionId, view.HandId,
                                Id(id++), view.SessionVersion, view.Street)).Accepted, Is.True, context);
                            continue;
                        }
                        Assert.That(view.CurrentSeat.HasValue, Is.True, context);
                        var actor = session.GetSnapshot(view.CurrentSeat.Value);
                        BettingAction action = Choose(actor.LegalActions, decisions);
                        var command = HoldemCommand.Act(actor.SessionId, actor.HandId, Id(id++), actor.ViewerSeat,
                            actor.SessionVersion, action);
                        var receipt = session.Submit(actor.ViewerSeat, command);
                        Assert.That(receipt.Accepted, Is.True, context + " " + action.Kind);
                        Assert.That(session.Version, Is.EqualTo(actor.SessionVersion + 1), context);
                        if (steps % 5 == 0)
                        {
                            Assert.That(session.Submit(actor.ViewerSeat, command), Is.SameAs(receipt), context);
                            Assert.That(session.Version, Is.EqualTo(actor.SessionVersion + 1), context + " duplicate acted twice");
                        }
                        decisionsMade++;
                    }
                    completedHands++;
                    int funded = 0;
                    for (int i = 0; i < seatCount; i++)
                    {
                        long stack = view.GetSeatAt(i).Stack;
                        if (priorStacks[i] > 0 && stack == 0) eliminated++;
                        if (stack > 0) funded++;
                        priorStacks[i] = stack;
                    }
                    Assert.That(session.CanContinue, Is.EqualTo(funded >= 2), context);
                    Assert.That(session.IsOver, Is.EqualTo(funded <= 1), context);
                    if (session.IsOver)
                        Assert.That(view.GetSeat(session.SessionWinnerSeat.Value).Stack, Is.EqualTo(total), context);
                }
            }
            TestContext.WriteLine("Sessions=200 seats=" + seatCount + " policy=" + policy + " deal=" + dealPolicy + " hands=" + completedHands
                + " actions=" + decisionsMade + " eliminations=" + eliminated);
        }

        private static void AssertState(HoldemSession session, SeatId[] seats, long total, string context)
        {
            var baseline = session.GetSnapshot(seats[0]);
            var cards = new HashSet<Card>();
            for (int i = 0; i < baseline.BoardCount; i++)
                Assert.That(cards.Add(baseline.GetBoardCard(i)), Is.True, context + " duplicate board");
            long observed = 0, awards = 0;
            foreach (SeatId viewer in seats)
            {
                var view = session.GetSnapshot(viewer);
                var own = view.GetSeat(viewer);
                observed += own.Stack + own.Committed; awards += own.Awarded;
                Assert.That(own.Stack, Is.GreaterThanOrEqualTo(0), context);
                Assert.That(own.Committed, Is.GreaterThanOrEqualTo(0), context);
                Assert.That(view.SessionVersion, Is.EqualTo(baseline.SessionVersion), context);
                if (view.IsDealPending)
                {
                    Assert.That(view.CurrentSeat, Is.Null, context);
                    Assert.That(view.LegalActions, Is.Null, context);
                    Assert.That(view.IsRevealPending, Is.False, context);
                    Assert.That(view.Accusations, Is.Null, context);
                    Assert.That(view.PendingDeal, Is.SameAs(baseline.PendingDeal), context);
                }
                Assert.That(view.BoardCount, Is.EqualTo(baseline.BoardCount), context);
                for (int i = 0; i < view.BoardCount; i++)
                    Assert.That(view.GetBoardCard(i), Is.EqualTo(baseline.GetBoardCard(i)), context);
                for (int i = 0; i < own.VisibleHoleCardCount; i++)
                    Assert.That(cards.Add(own.GetVisibleHoleCard(i)), Is.True, context + " duplicate dealt card");
                for (int i = 0; i < view.SeatCount; i++)
                {
                    var seat = view.GetSeatAt(i);
                    bool showdown = view.Result != null && view.Result.Kind == HoldemResultKind.Showdown;
                    bool publicHand = showdown && seat.Status != HoldemSeatStatus.Folded && seat.WasDealtIn;
                    int visible = seat.WasDealtIn && (seat.IsViewer || publicHand) ? 2 : 0;
                    Assert.That(seat.VisibleHoleCardCount, Is.EqualTo(visible), context + " card privacy");
                    Assert.That(seat.RevealedHandValue.HasValue, Is.EqualTo(publicHand), context);
                    Assert.That(seat.Stack, Is.EqualTo(baseline.GetSeat(seat.Seat).Stack), context);
                }
                if (view.Result != null) Assert.That(own.Committed, Is.Zero, context);
            }
            Assert.That(observed, Is.EqualTo(total), context + " chip conservation");
            if (baseline.Result == null) return;
            long potTotal = 0;
            var payouts = new Dictionary<SeatId, long>();
            foreach (SeatId seat in seats) payouts[seat] = 0;
            for (int i = 0; i < baseline.Result.PotCount; i++)
            {
                var pot = baseline.Result.GetPot(i);
                long paid = 0;
                for (int j = 0; j < pot.PayoutCount; j++)
                {
                    var payout = pot.GetPayout(j); paid += payout.Amount; payouts[payout.Seat] += payout.Amount;
                }
                Assert.That(paid, Is.EqualTo(pot.Amount), context + " pot payout"); potTotal += pot.Amount;
            }
            Assert.That(potTotal, Is.EqualTo(baseline.Result.PotAmount), context);
            Assert.That(awards, Is.EqualTo(potTotal), context);
            foreach (SeatId seat in seats)
                Assert.That(payouts[seat], Is.EqualTo(baseline.Result.GetAwardedTo(seat)), context);
        }

        private static BettingAction Choose(LegalBettingActions legal, Random random)
        {
            int choice = random.Next(10);
            if (choice == 0 && legal.CanFold) return BettingAction.Fold();
            if (choice <= 3 && (legal.CanBet || legal.CanRaise))
            {
                long min = legal.MinimumAggressiveTarget.Value, max = legal.MaximumAggressiveTarget.Value;
                long target = choice == 1 ? max : choice == 2 ? min : min + (max - min) / 2;
                return legal.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target);
            }
            return legal.CanCheck ? BettingAction.Check() : BettingAction.Call();
        }

        private static Guid Id(int value)
        {
            var bytes = new byte[16]; Array.Copy(BitConverter.GetBytes(value), bytes, 4); bytes[15] = 1;
            return new Guid(bytes);
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
    }
}
