using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class HandTransitionTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [Test]
        public void StartAndStartReplayHaveNoPlayerActionTransition()
        {
            var s = New(); var command = new StartHandCommand(s.HandId, Guid.NewGuid());
            Assert.That(s.LastTransition, Is.Null);
            var receipt = s.Start(command);
            Assert.That(receipt.Transition, Is.Null); Assert.That(s.LastTransition, Is.Null);
            Assert.That(s.Start(command), Is.SameAs(receipt));
            Assert.That(View(s).LastTransition, Is.Null);
        }

        [Test]
        public void AcceptedCallHasExactVersionActorCostAndPhase()
        {
            var s = Start(); var receipt = Bet(s, BettingAction.Call()); var t = receipt.Transition.Value;
            Assert.That(t.HandId, Is.EqualTo(s.HandId)); Assert.That(t.CommandId, Is.EqualTo(receipt.CommandId));
            Assert.That(t.AppliedVersion, Is.EqualTo(2)); Assert.That(t.Seat, Is.EqualTo(A));
            Assert.That(t.BeforePhase, Is.EqualTo(HandPhase.FirstBetting)); Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(t.Kind, Is.EqualTo(HandCommandKind.Bet)); Assert.That(t.BettingAction, Is.EqualTo(BettingActionKind.Call));
            Assert.That(t.ChipsPaid, Is.EqualTo(1)); Assert.That(t.TargetTotal, Is.Null);
            Assert.That(t.ExchangeCount, Is.Zero); Assert.That(t.RefundedSeat, Is.Null); Assert.That(t.RefundedAmount, Is.Zero);
            Assert.That(s.LastTransition, Is.EqualTo(t)); Assert.That(View(s).LastTransition.Value.AppliedVersion, Is.EqualTo(s.Version));
        }

        [Test]
        public void RaiseSeparatesAdditionalPaymentFromTargetTotal()
        {
            var s = Start(); var t = Bet(s, BettingAction.RaiseTo(8)).Transition.Value;
            Assert.That(t.BettingAction, Is.EqualTo(BettingActionKind.RaiseTo));
            Assert.That(t.TargetTotal, Is.EqualTo(8)); Assert.That(t.ChipsPaid, Is.EqualTo(7));
        }

        [Test]
        public void CheckClosesFirstStreetWithoutInventingAPayment()
        {
            var s = Start(); Bet(s, BettingAction.Call()); var t = Bet(s, BettingAction.Check()).Transition.Value;
            Assert.That(t.BettingAction, Is.EqualTo(BettingActionKind.Check)); Assert.That(t.ChipsPaid, Is.Zero);
            Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.Exchange)); Assert.That(t.RefundedAmount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void ExchangeExposesCountNotCardIdentities(int count)
        {
            var s = Start(); Bet(s, BettingAction.Call()); Bet(s, BettingAction.Check());
            var receipt = Draw(s, count); var t = receipt.Transition.Value;
            Assert.That(t.Kind, Is.EqualTo(HandCommandKind.Exchange)); Assert.That(t.ExchangeCount, Is.EqualTo(count));
            Assert.That(t.BettingAction, Is.Null); Assert.That(t.TargetTotal, Is.Null); Assert.That(t.ChipsPaid, Is.Zero);
            Assert.That(t.BeforePhase, Is.EqualTo(HandPhase.Exchange)); Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(View(s).LastTransition.Value.ExchangeCount, Is.EqualTo(count));
        }

        [Test]
        public void RejectedActionHasNoTransitionAndDoesNotReplaceTheLatestOne()
        {
            var s = Start(); Bet(s, BettingAction.Call()); var before = s.LastTransition; long version = s.Version;
            var invalid = HandCommand.Bet(s.HandId, Guid.NewGuid(), B, version - 1, BettingAction.Check());
            var rejected = s.Submit(B, invalid);
            Assert.That(rejected.Error, Is.EqualTo(HandError.VersionMismatch)); Assert.That(rejected.Transition, Is.Null);
            Assert.That(s.LastTransition, Is.EqualTo(before)); Assert.That(s.Version, Is.EqualTo(version));
            Assert.That(s.Submit(A, invalid).Transition, Is.Null);
        }

        [Test]
        public void OldApprovedReplayReturnsOriginalFactsWithoutRewindingLatestView()
        {
            var s = Start(); var command = HandCommand.Bet(s.HandId, Guid.NewGuid(), A, s.Version, BettingAction.Call());
            var first = s.Submit(A, command); Bet(s, BettingAction.Check());
            var latest = s.LastTransition;
            var replay = s.Submit(A, command);
            Assert.That(replay, Is.SameAs(first)); Assert.That(replay.Transition.Value.AppliedVersion, Is.EqualTo(2));
            Assert.That(s.LastTransition, Is.EqualTo(latest)); Assert.That(s.Version, Is.EqualTo(3));
            Assert.That(View(s).LastTransition.Value.AppliedVersion, Is.EqualTo(3));
            Assert.That(View(s).LastTransition.Value.Seat, Is.EqualTo(B));
        }

        [Test]
        public void FoldReportsRefundSeparatelyFromZeroActionPaymentAndPotPayout()
        {
            var s = Start(); var t = Bet(s, BettingAction.Fold()).Transition.Value;
            Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.Complete)); Assert.That(t.ChipsPaid, Is.Zero);
            Assert.That(t.RefundedSeat, Is.EqualTo(B)); Assert.That(t.RefundedAmount, Is.EqualTo(1));
            Assert.That(s.State.Settlement.TotalAwarded, Is.EqualTo(2));
        }

        [Test]
        public void FirstStreetRefundIsVisibleImmediatelyAndNotRepeatedByExchange()
        {
            var s = Start(40); Bet(s, BettingAction.RaiseTo(100)); var t = Bet(s, BettingAction.Call()).Transition.Value;
            Assert.That(t.ChipsPaid, Is.EqualTo(38)); Assert.That(t.RefundedSeat, Is.EqualTo(A)); Assert.That(t.RefundedAmount, Is.EqualTo(60));
            Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.Exchange)); Assert.That(s.State.Settlement, Is.Null);
            var draw = Draw(s, 0).Transition.Value;
            Assert.That(draw.RefundedSeat, Is.Null); Assert.That(draw.RefundedAmount, Is.Zero);
        }

        [Test]
        public void SecondStreetShortAllInCallReportsGrossPaymentAndRefundBeforeFinalPayout()
        {
            var s = Start(40); Bet(s, BettingAction.Call()); Bet(s, BettingAction.Check()); Draw(s, 0); Draw(s, 0);
            var opening = Bet(s, BettingAction.BetTo(98)).Transition.Value;
            Assert.That(opening.BeforePhase, Is.EqualTo(HandPhase.SecondBetting)); Assert.That(opening.ChipsPaid, Is.EqualTo(98));
            var t = Bet(s, BettingAction.Call()).Transition.Value;
            Assert.That(t.BeforePhase, Is.EqualTo(HandPhase.SecondBetting)); Assert.That(t.AfterPhase, Is.EqualTo(HandPhase.Complete));
            Assert.That(t.ChipsPaid, Is.EqualTo(38)); Assert.That(t.TargetTotal, Is.Null);
            Assert.That(t.RefundedSeat, Is.EqualTo(A)); Assert.That(t.RefundedAmount, Is.EqualTo(60));
            Assert.That(s.State.Settlement.TotalAwarded, Is.EqualTo(80));
        }

        [Test]
        public void PublicProjectionDropsCorrelationIdAndKeepsOnlyImmutableValueFields()
        {
            var expected = "HandId AppliedVersion Seat BeforePhase AfterPhase Kind BettingAction TargetTotal ChipsPaid ExchangeCount RefundedSeat RefundedAmount".Split(' ');
            var properties = typeof(PublicHandTransition).GetProperties();
            Assert.That(properties.Select(p => p.Name), Is.EquivalentTo(expected));
            Assert.That(properties.All(p => p.SetMethod == null), Is.True);
            Assert.That(typeof(PublicHandTransition).GetConstructors(), Is.Empty);
            var allowed = new HashSet<Type> { typeof(Guid), typeof(long), typeof(long?), typeof(SeatId), typeof(SeatId?),
                typeof(HandPhase), typeof(HandCommandKind), typeof(BettingActionKind?), typeof(int) };
            foreach (var field in typeof(PublicHandTransition).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            { Assert.That(field.IsInitOnly, Is.True); Assert.That(allowed.Contains(field.FieldType), Is.True, field.Name); }
        }

        [Test]
        public void ChangingSecretCardsDoesNotChangeAPublicNonterminalAction()
        {
            Guid id = Guid.NewGuid(), commandId = Guid.NewGuid();
            var first = Start(100, id); var second = Start(100, id, new SeedRandom());
            Assert.That(first.State.GetHand(B).ToArray(), Is.Not.EqualTo(second.State.GetHand(B).ToArray()));
            foreach (var s in new[] { first, second })
                Assert.That(s.Submit(A, HandCommand.Bet(id, commandId, A, 1, BettingAction.Call())).Accepted, Is.True);
            Assert.That(View(first).LastTransition, Is.EqualTo(View(second).LastTransition));
        }

        private static PokerPlayerView View(PokerHandSession s) => PokerPlayerViewProjector.Create(s, A);
        private static PokerHandSession New(long otherStack = 100, Guid? id = null, IRandomSource random = null)
        {
            var order = new[] { A, B };
            var ledger = ChipLedger.Create(new[] { new SeatChips(A, 100), new SeatChips(B, otherStack) });
            return new PokerHandSession(id ?? Guid.NewGuid(), new HandSetup(ledger, order, order, order, order, 1, 2), random ?? new IdentityRandom());
        }
        private static PokerHandSession Start(long otherStack = 100, Guid? id = null, IRandomSource random = null)
        {
            var s = New(otherStack, id, random); Assert.That(s.Start(new StartHandCommand(s.HandId, Guid.NewGuid())).Accepted, Is.True); return s;
        }
        private static HandReceipt Bet(PokerHandSession s, BettingAction action)
        {
            SeatId seat = s.State.CurrentSeat.Value;
            var receipt = s.Submit(seat, HandCommand.Bet(s.HandId, Guid.NewGuid(), seat, s.Version, action));
            Assert.That(receipt.Accepted, Is.True); return receipt;
        }
        private static HandReceipt Draw(PokerHandSession s, int count)
        {
            SeatId seat = s.State.CurrentSeat.Value;
            var receipt = s.Submit(seat, HandCommand.Exchange(s.HandId, Guid.NewGuid(), seat, s.Version, s.State.GetHand(seat).Take(count).ToArray()));
            Assert.That(receipt.Accepted, Is.True); return receipt;
        }
        private sealed class IdentityRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class SeedRandom : IRandomSource
        { private readonly Random random = new Random(53); public int NextInt(int upper) => random.Next(upper); }
    }
}
