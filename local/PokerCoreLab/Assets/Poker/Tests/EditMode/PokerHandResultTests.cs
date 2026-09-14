using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerHandResultTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [Test]
        public void NotStartedOrUnfinishedHandIsNotACompletedResult()
        {
            var s = New(100, 100);
            Assert.Throws<InvalidOperationException>(() => Result(s));
            s.Start(new StartHandCommand(s.HandId, Guid.NewGuid()));
            Assert.Throws<InvalidOperationException>(() => Result(s));
            Assert.That(PokerPlayerViewProjector.Create(s, A).Result, Is.Null);
            Act(s, BettingAction.Call()); Act(s, BettingAction.Check());
            Assert.Throws<InvalidOperationException>(() => Result(s));
        }

        [Test]
        public void ProjectorRequiresAValidParticipatingViewer()
        {
            var s = Start(100, 100); Act(s, BettingAction.Fold());
            Assert.Throws<ArgumentNullException>(() => PokerHandResultProjector.Create(null, A));
            Assert.Throws<ArgumentException>(() => PokerHandResultProjector.Create(s, default));
            Assert.Throws<KeyNotFoundException>(() => PokerHandResultProjector.Create(s, new SeatId(99)));
            Assert.That(PokerHandResultProjector.Create(s, B).ViewerSeat, Is.EqualTo(B));
        }

        [Test]
        public void FoldResultSeparatesPotPayoutRefundAndFinalStack()
        {
            var s = Start(100, 100); Act(s, BettingAction.Fold()); var result = Result(s);
            Assert.That(result.Reason, Is.EqualTo(HandCompletionReason.Uncontested));
            Assert.That(result.HandId, Is.EqualTo(s.HandId)); Assert.That(result.Version, Is.EqualTo(s.Version));
            Assert.That(result.TotalAwarded, Is.EqualTo(2)); Assert.That(result.Pots, Has.Count.EqualTo(1));
            Assert.That(result.Pots[0].EligibleSeats, Is.EqualTo(new[] { B }));
            Assert.That(result.Pots[0].Payouts[0].Amount, Is.EqualTo(2));
            Assert.That(result.Seats.Single(x => x.Seat == B).FinalStack, Is.EqualTo(101));
            Assert.That(result.Seats.Single(x => x.Seat == B).GrossAward, Is.EqualTo(2));
            Assert.That(result.Refunds, Has.Count.EqualTo(1));
            Assert.That(result.Refunds[0].BettingPhase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(result.Refunds[0].Seat, Is.EqualTo(B)); Assert.That(result.Refunds[0].Amount, Is.EqualTo(1));
            AssertExactCopy(s, result);
        }

        [Test]
        public void EqualHandsShowdownRevealsBothHandsAndValues()
        {
            var s = Start(100, 100); Finish(s); var result = Result(s);
            Assert.That(result.Reason, Is.EqualTo(HandCompletionReason.Showdown));
            Assert.That(result.Pots[0].Payouts.Select(p => p.Amount), Is.EqualTo(new long[] { 2, 2 }));
            Assert.That(result.Refunds, Is.Empty); AssertExactCopy(s, result);
            Assert.That(result.RevealedHands.Select(h => h.Seat), Is.EqualTo(new[] { A, B }));
            foreach (var hand in result.RevealedHands)
            {
                Assert.That(hand.Cards, Is.EqualTo(s.State.GetHand(hand.Seat)));
                Assert.That(hand.Value, Is.EqualTo(HandEvaluator.Evaluate(hand.Cards)));
                Assert.Throws<NotSupportedException>(() => ((IList<Card>)hand.Cards)[0] = default);
            }
            Assert.Throws<NotSupportedException>(() => ((IList<RevealedHandView>)result.RevealedHands)[0] = null);
            Assert.That(PokerHandResultProjector.Create(s, B).RevealedHands.SelectMany(h => h.Cards),
                Is.EqualTo(result.RevealedHands.SelectMany(h => h.Cards)));
        }

        [Test]
        public void DifferentContributionLayersRemainDifferentPots()
        {
            var s = Start(20, 50, 100);
            Act(s, BettingAction.RaiseTo(20)); Act(s, BettingAction.RaiseTo(50)); Act(s, BettingAction.Call()); Finish(s);
            var result = Result(s);
            Assert.That(result.Pots, Has.Count.EqualTo(2));
            Assert.That(result.Pots.Select(p => p.Amount), Is.EqualTo(new long[] { 60, 60 }));
            Assert.That(result.Pots.Select(p => p.LowerBound), Is.EqualTo(new long[] { 0, 20 }));
            Assert.That(result.Pots.Select(p => p.ContributionCap), Is.EqualTo(new long[] { 20, 50 }));
            Assert.That(result.Pots[0].EligibleSeats, Has.Count.EqualTo(3));
            Assert.That(result.Pots[1].EligibleSeats, Has.Count.EqualTo(2)); AssertExactCopy(s, result);
        }

        [Test]
        public void FirstStreetRefundRemainsHistoricalAtCompletion()
        {
            var s = Start(100, 40); Act(s, BettingAction.RaiseTo(100)); Act(s, BettingAction.Call()); Finish(s);
            var result = Result(s);
            Assert.That(result.TotalAwarded, Is.EqualTo(80)); Assert.That(result.Refunds, Has.Count.EqualTo(1));
            Assert.That(result.Refunds[0].BettingPhase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(result.Refunds[0].Seat, Is.EqualTo(A)); Assert.That(result.Refunds[0].Amount, Is.EqualTo(60));
            Assert.That(result.Seats.Sum(x => x.FinalStack), Is.EqualTo(140)); AssertExactCopy(s, result);
        }

        [Test]
        public void SecondStreetRefundIsTaggedSeparatelyFromThePot()
        {
            var s = Start(100, 100); Act(s, BettingAction.Call()); Act(s, BettingAction.Check()); Draw(s); Draw(s);
            Act(s, BettingAction.BetTo(10)); Act(s, BettingAction.Fold()); var result = Result(s);
            Assert.That(result.Refunds, Has.Count.EqualTo(1));
            Assert.That(result.Refunds[0].BettingPhase, Is.EqualTo(HandPhase.SecondBetting));
            Assert.That(result.Refunds[0].Amount, Is.EqualTo(10)); Assert.That(result.TotalAwarded, Is.EqualTo(4));
            AssertExactCopy(s, result);
        }

        [Test]
        public void LargeFinalStacksAreCopiedWithoutFloatOrOverflow()
        {
            var s = Start(long.MaxValue - 2, 2); Finish(s); var result = Result(s);
            Assert.That(result.Seats.Sum(x => x.FinalStack), Is.EqualTo(long.MaxValue)); AssertExactCopy(s, result);
        }

        [Test]
        public void ResultAndNestedCollectionsAreDetachedAndReadOnly()
        {
            var s = Start(100, 100); Act(s, BettingAction.Fold()); var first = Result(s); var second = Result(s);
            Assert.That(first, Is.Not.SameAs(second)); Assert.That(first.Pots[0], Is.Not.SameAs(second.Pots[0]));
            Assert.Throws<NotSupportedException>(() => ((IList<HandSeatResult>)first.Seats)[0] = default);
            Assert.Throws<NotSupportedException>(() => ((IList<HandPotResult>)first.Pots)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<HandRefund>)first.Refunds)[0] = default);
            Assert.Throws<NotSupportedException>(() => ((IList<SeatId>)first.Pots[0].EligibleSeats)[0] = A);
            Assert.Throws<NotSupportedException>(() => ((IList<SeatPayout>)first.Pots[0].Payouts)[0] = default);
            AssertExactCopy(s, first);
        }

        [Test]
        public void ReplayingTerminalCommandDoesNotChangeTheCopiedResult()
        {
            var s = Start(100, 100); var command = HandCommand.Bet(s.HandId, Guid.NewGuid(), A, s.Version, BettingAction.Fold());
            var receipt = s.Submit(A, command); var before = Result(s);
            Assert.That(s.Submit(A, command), Is.SameAs(receipt));
            Assert.That(s.Version, Is.EqualTo(before.Version)); AssertExactCopy(s, before);
        }

        [Test]
        public void ResultSurfaceOnlyAllowsExplicitPublicHandsNotAuthorityReferences()
        {
            AssertSurface(typeof(PokerHandResultView), "HandId Version ViewerSeat Reason TotalAwarded Seats Pots Refunds RevealedHands");
            AssertSurface(typeof(RevealedHandView), "Seat Cards Value");
            AssertSurface(typeof(HandPotResult), "LowerBound ContributionCap Amount EligibleSeats Payouts");
            AssertSurface(typeof(HandSeatResult), "Seat FinalStack GrossAward");
            AssertSurface(typeof(HandRefund), "BettingPhase Seat Amount");
            AssertSurface(typeof(SeatPayout), "Seat Amount HasOddChip");
            var allowed = new HashSet<Type> { typeof(Guid), typeof(long), typeof(bool), typeof(SeatId), typeof(HandCompletionReason), typeof(HandPhase),
                typeof(IReadOnlyList<HandSeatResult>), typeof(IReadOnlyList<HandPotResult>), typeof(IReadOnlyList<HandRefund>),
                typeof(IReadOnlyList<SeatId>), typeof(IReadOnlyList<SeatPayout>), typeof(IReadOnlyList<RevealedHandView>), typeof(IReadOnlyList<Card>), typeof(HandValue) };
            foreach (var type in new[] { typeof(PokerHandResultView), typeof(HandPotResult), typeof(HandSeatResult), typeof(HandRefund), typeof(SeatPayout), typeof(RevealedHandView) })
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                { Assert.That(field.IsInitOnly, Is.True); Assert.That(allowed.Contains(field.FieldType), Is.True, type.Name + "." + field.Name); }
        }

        private static void AssertSurface(Type type, string expected)
        {
            Assert.That(type.GetConstructors(), Is.Empty);
            Assert.That(type.GetProperties().Select(p => p.Name), Is.EquivalentTo(expected.Split(' ')));
            Assert.That(type.GetProperties().All(p => p.SetMethod == null), Is.True);
        }
        private static void AssertExactCopy(PokerHandSession s, PokerHandResultView result)
        {
            if (result.Reason == HandCompletionReason.Uncontested) Assert.That(result.RevealedHands, Is.Empty);
            Assert.That(result.Seats.Select(x => x.Seat), Is.EqualTo(Enumerable.Range(0, s.State.SeatCount).Select(s.State.GetSeatAt)));
            foreach (var seat in result.Seats)
            {
                Assert.That(seat.FinalStack, Is.EqualTo(s.State.Ledger.GetChips(seat.Seat).Stack));
                Assert.That(seat.GrossAward, Is.EqualTo(s.State.Settlement.GetAwardedTo(seat.Seat)));
            }
            Assert.That(result.Pots.Count, Is.EqualTo(s.State.Settlement.PotCount));
            for (int i = 0; i < result.Pots.Count; i++)
            {
                var actual = result.Pots[i]; var source = s.State.Settlement.GetPot(i);
                Assert.That(actual.Amount, Is.EqualTo(source.Amount)); Assert.That(actual.LowerBound, Is.EqualTo(source.LowerBound));
                Assert.That(actual.ContributionCap, Is.EqualTo(source.ContributionCap));
                Assert.That(actual.EligibleSeats, Is.EqualTo(Enumerable.Range(0, source.EligibleSeatCount).Select(source.GetEligibleSeat)));
                Assert.That(actual.Payouts, Is.EqualTo(Enumerable.Range(0, source.PayoutCount).Select(source.GetPayout)));
            }
            Assert.That(result.TotalAwarded, Is.EqualTo(s.State.Settlement.TotalAwarded));
            Assert.That(PokerPlayerViewProjector.Create(s, A).Result.Version, Is.EqualTo(result.Version));
        }
        [Test]
        public void FoldedParticipantIsNeverRevealedEvenToTheFoldedViewer()
        {
            var s = Start(100, 100, 100);
            Act(s, BettingAction.Fold()); Finish(s);
            foreach (var viewer in new[] { A, B, new SeatId(3) })
            {
                var result = PokerHandResultProjector.Create(s, viewer);
                Assert.That(result.RevealedHands.Select(h => h.Seat), Is.EqualTo(new[] { B, new SeatId(3) }));
            }
        }
        private static PokerHandResultView Result(PokerHandSession s) => PokerHandResultProjector.Create(s, A);
        private static PokerHandSession New(params long[] stacks)
        {
            var order = Enumerable.Range(1, stacks.Length).Select(n => new SeatId(n)).ToArray();
            var ledger = ChipLedger.Create(stacks.Select((n, i) => new SeatChips(order[i], n)).ToArray());
            return new PokerHandSession(Guid.NewGuid(), new HandSetup(ledger, order, order, order, order, 1, 2), new IdentityRandom());
        }
        private static PokerHandSession Start(params long[] stacks)
        { var s = New(stacks); Assert.That(s.Start(new StartHandCommand(s.HandId, Guid.NewGuid())).Accepted, Is.True); return s; }
        private static void Act(PokerHandSession s, BettingAction action)
        { SeatId seat = s.State.CurrentSeat.Value; Assert.That(s.Submit(seat, HandCommand.Bet(s.HandId, Guid.NewGuid(), seat, s.Version, action)).Accepted, Is.True); }
        private static void Draw(PokerHandSession s)
        { SeatId seat = s.State.CurrentSeat.Value; Assert.That(s.Submit(seat, HandCommand.Exchange(s.HandId, Guid.NewGuid(), seat, s.Version, new Card[0])).Accepted, Is.True); }
        private static void Finish(PokerHandSession s)
        {
            int steps = 0;
            while (s.State.CurrentSeat.HasValue)
            {
                Assert.That(++steps, Is.LessThan(30));
                if (s.State.Phase == HandPhase.Exchange) Draw(s);
                else Act(s, s.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check());
            }
            Assert.That(s.State.Phase, Is.EqualTo(HandPhase.Complete));
        }
        private sealed class IdentityRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
    }
}
