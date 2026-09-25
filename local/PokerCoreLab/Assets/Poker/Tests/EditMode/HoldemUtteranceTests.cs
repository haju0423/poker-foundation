using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemUtteranceTests
    {
        private readonly SeatId a = new SeatId(1), b = new SeatId(2);
        private HoldemSession session;
        private HoldemUtteranceInbox inbox;
        private IHoldemUtterancePlayerPort own, other;
        private HoldemSnapshot State => session.GetSnapshot(a);

        private void Start(int count = 4, HoldemUtteranceSeats allowed = HoldemUtteranceSeats.Active,
            int limit = 2, int textLength = 128, bool manual = true)
        {
            var seats = Enumerable.Range(1, count).Select(i => new SeatId(i)).ToArray();
            session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, dealPolicy: manual ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic),
                seats, a, new FixedRandom(), HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            inbox = new HoldemUtteranceInbox(() => State, new HoldemUtterancePolicy(textLength, limit, allowed));
            own = inbox.Bind(a); other = inbox.Bind(b);
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void RawSpeechIsIndependentOfPokerStateAndAnotherPlayersTurn(int count)
        {
            Start(count); var before = State;
            var second = other.ReadUtterances();
            var message = Command(second, "오늘은 빨간색이 좋네 <b>♥</b>");
            var receipt = other.SubmitUtterance(message);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(State.SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(State.HandVersion, Is.EqualTo(before.HandVersion));
            Assert.That(State.PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(State.CurrentSeat, Is.EqualTo(before.CurrentSeat));
            Assert.That(State.BoardCount, Is.EqualTo(before.BoardCount));
            Assert.That(own.ReadUtterances().Count, Is.Zero, "Another player's raw text is not broadcast by intake.");
            Assert.That(other.ReadUtterances().GetEntry(0).Text, Is.EqualTo(message.Text));
            for (int i = 0; i < count; i++)
            {
                Assert.That(State.GetSeatAt(i).Stack, Is.EqualTo(before.GetSeatAt(i).Stack));
                Assert.That(State.GetSeatAt(i).Committed, Is.EqualTo(before.GetSeatAt(i).Committed));
            }
        }

        [Test]
        public void BettingRevisionDoesNotInvalidateAnOpenUtteranceWindow()
        {
            Start(); var view = own.ReadUtterances(); var command = Command(view, "타이핑하던 멘트");
            Act(BettingAction.Call());
            Assert.That(State.SessionVersion, Is.GreaterThan(1));
            Assert.That(own.ReadUtterances().WindowId, Is.EqualTo(view.WindowId));
            Assert.That(own.SubmitUtterance(command).Accepted, Is.True);
        }

        [Test]
        public void SameCommandIsAcknowledgedOnceAndConflictingPayloadIsRejected()
        {
            Start(limit: 1); var view = own.ReadUtterances(); var command = Command(view, "원문");
            var first = own.SubmitUtterance(command);
            Assert.That(own.SubmitUtterance(command), Is.SameAs(first));
            Assert.That(own.ReadUtterances().Count, Is.EqualTo(1));
            Assert.That(own.SubmitUtterance(Command(view, "다른 원문", command.CommandId)).Error,
                Is.EqualTo(HoldemUtteranceError.CommandConflict));
            Assert.That(own.SubmitUtterance(Command(view, "새 멘트")).Error, Is.EqualTo(HoldemUtteranceError.LimitReached));
            Assert.That(other.SubmitUtterance(Command(other.ReadUtterances(), "다른 사람 멘트")).Accepted, Is.True);
            Assert.That(own.ReadUtterances().Remaining, Is.Zero);
        }

        [Test]
        public void SessionHandWindowAndBoundSeatMustMatch()
        {
            Start(); var view = own.ReadUtterances();
            var command = Command(view, "메시지");
            Assert.That(other.SubmitUtterance(command).Error, Is.EqualTo(HoldemUtteranceError.UnauthorizedSeat));
            Assert.That(own.SubmitUtterance(new HoldemUtteranceCommand(Guid.NewGuid(), view.HandId, view.WindowId,
                Guid.NewGuid(), view.Street, a, "말")).Error, Is.EqualTo(HoldemUtteranceError.WrongSession));
            Assert.That(own.SubmitUtterance(new HoldemUtteranceCommand(view.SessionId, Guid.NewGuid(), view.WindowId,
                Guid.NewGuid(), view.Street, a, "말")).Error, Is.EqualTo(HoldemUtteranceError.WrongHand));
            Assert.That(own.SubmitUtterance(new HoldemUtteranceCommand(view.SessionId, view.HandId, Guid.NewGuid(),
                Guid.NewGuid(), view.Street, a, "말")).Error, Is.EqualTo(HoldemUtteranceError.WrongWindow));
            Assert.That(own.SubmitUtterance(new HoldemUtteranceCommand(view.SessionId, view.HandId, view.WindowId,
                Guid.NewGuid(), HoldemStreet.Flop, a, "말")).Error, Is.EqualTo(HoldemUtteranceError.WrongWindow));
            Assert.Throws<ArgumentException>(() => inbox.Bind(new SeatId(99)));
            Assert.That(own.ReadUtterances().Count, Is.Zero);
        }

        [TestCase("")] [TestCase("   ")]
        public void WhitespaceOnlyInputDoesNotConsumeQuota(string text)
        {
            Start(); Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), text)).Error,
                Is.EqualTo(HoldemUtteranceError.EmptyText));
            Assert.That(own.ReadUtterances().Remaining, Is.EqualTo(2));
        }

        [TestCase("줄\n바꿈")] [TestCase("탭\t")]
        [TestCase("널\0")]
        public void ControlCharactersAreRejected(string text)
        {
            Start(); Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), text)).Error,
                Is.EqualTo(HoldemUtteranceError.InvalidText));
        }

        [Test]
        public void UnicodeIsPreservedAndMalformedSurrogatesAreRejected()
        {
            Start(textLength: 8);
            Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), "  한글🃏 ")).Accepted, Is.True);
            Assert.That(own.ReadUtterances().GetEntry(0).Text, Is.EqualTo("  한글🃏 "));
            foreach (string invalid in new[] { "\ud800", "\udc00", "\ud800가" })
                Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), invalid)).Error, Is.EqualTo(HoldemUtteranceError.InvalidText));
            Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), new string('가', 9))).Error,
                Is.EqualTo(HoldemUtteranceError.TextTooLong));
        }

        [Test]
        public void ClosedBatchIsFrozenAndLateNewMessagesCannotEnterIt()
        {
            Start(); var view = own.ReadUtterances(); var command = Command(view, "플랍 전 멘트");
            var receipt = own.SubmitUtterance(command); var oldView = own.ReadUtterances();
            Assert.That(inbox.ReadClosedBatches(), Is.Empty);
            EndBetting();
            var batch = inbox.ReadClosedBatches().Single();
            Assert.That(batch.Count, Is.EqualTo(1)); Assert.That(batch.WindowId, Is.EqualTo(view.WindowId));
            Assert.That(batch.GetEntry(0).Text, Is.EqualTo(command.Text));
            Assert.That(own.SubmitUtterance(Command(view, "너무 늦은 멘트")).Error, Is.EqualTo(HoldemUtteranceError.WindowClosed));
            Assert.That(own.SubmitUtterance(command), Is.SameAs(receipt), "An accepted retry only acknowledges the existing source text.");
            DealAndResume();
            var next = own.ReadUtterances(); Assert.That(next.WindowId, Is.Not.EqualTo(view.WindowId));
            Assert.That(own.SubmitUtterance(Command(view, "이전 창")).Error, Is.EqualTo(HoldemUtteranceError.WrongWindow));
            Assert.That(own.SubmitUtterance(Command(next, "턴 전 멘트")).Accepted, Is.True);
            Assert.That(batch.Count, Is.EqualTo(1)); Assert.That(oldView.Count, Is.EqualTo(1));
            Assert.That(own.ReadUtterances().Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void ThreeBettingWindowsCloseBeforeRevealsAndRiverHasNoNewSpeech(bool manual)
        {
            Start(manual: manual); Guid previous = Guid.Empty;
            for (int street = 0; street < 3; street++)
            {
                var view = own.ReadUtterances(); Assert.That(view.CanSubmit, Is.True);
                Assert.That((int)view.Street, Is.EqualTo(street)); Assert.That(view.WindowId, Is.Not.EqualTo(previous));
                Assert.That(own.SubmitUtterance(Command(view, "단계 " + street)).Accepted, Is.True);
                previous = view.WindowId; EndBetting();
                Assert.That(own.ReadUtterances().CanSubmit, Is.False);
                Assert.That(inbox.ReadClosedBatches().Count, Is.EqualTo(street + 1));
                DealAndResume();
            }
            Assert.That(State.Street, Is.EqualTo(HoldemStreet.River));
            Assert.That(own.ReadUtterances().WindowId, Is.EqualTo(Guid.Empty));
            Assert.That(own.ReadUtterances().CanSubmit, Is.False);
            Assert.That(own.ReadUtterances().Count, Is.EqualTo(3));
            var batches = inbox.ReadClosedBatches();
            EndBetting(); Assert.That(State.Result, Is.Not.Null);
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), State.SessionVersion).Accepted, Is.True);
            inbox.Synchronize();
            Assert.That(own.ReadUtterances().Count, Is.Zero);
            Assert.That(batches.Count, Is.EqualTo(3)); Assert.That(batches[0].Count, Is.EqualTo(1));
            var entry = batches[0].GetEntry(0);
            Assert.That(own.SubmitUtterance(new HoldemUtteranceCommand(entry.SessionId, entry.HandId, entry.WindowId,
                entry.CommandId, entry.Street, entry.Speaker, entry.Text)).Error, Is.EqualTo(HoldemUtteranceError.WrongHand));
        }

        [TestCase(HoldemUtteranceSeats.Active, false)]
        [TestCase(HoldemUtteranceSeats.Active | HoldemUtteranceSeats.Folded, true)]
        public void FoldedSeatEligibilityIsAnExplicitIntakePolicy(HoldemUtteranceSeats allowed, bool accepted)
        {
            Start(allowed: allowed); var actor = State.CurrentSeat.Value; var port = inbox.Bind(actor);
            var view = port.ReadUtterances(); Act(BettingAction.Fold());
            Assert.That(State.Street, Is.EqualTo(HoldemStreet.Preflop));
            Assert.That(port.ReadUtterances().CanSubmit, Is.EqualTo(accepted));
            Assert.That(port.SubmitUtterance(Command(view, "관찰 멘트")).Accepted, Is.EqualTo(accepted));
        }

        [TestCase(HoldemUtteranceSeats.Active, false)]
        [TestCase(HoldemUtteranceSeats.Active | HoldemUtteranceSeats.AllIn, true)]
        public void AllInSeatEligibilityIsExplicitWhileOthersStillBet(HoldemUtteranceSeats allowed, bool accepted)
        {
            Start(allowed: allowed); var actor = State.CurrentSeat.Value; var port = inbox.Bind(actor);
            var view = port.ReadUtterances(); Act(BettingAction.RaiseTo(100));
            Assert.That(State.GetSeat(actor).Status, Is.EqualTo(HoldemSeatStatus.AllIn));
            Assert.That(port.ReadUtterances().CanSubmit, Is.EqualTo(accepted));
            Assert.That(port.SubmitUtterance(Command(view, "올인 멘트")).Accepted, Is.EqualTo(accepted));
        }

        [Test]
        public void LimitsAndEligibilityMustBeExplicitAndValid()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(0, 1, HoldemUtteranceSeats.Active));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(4097, 1, HoldemUtteranceSeats.Active));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(128, 0, HoldemUtteranceSeats.Active));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(128, 33, HoldemUtteranceSeats.Active));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(128, 1, (HoldemUtteranceSeats)8));
            Start(allowed: HoldemUtteranceSeats.None);
            Assert.That(own.ReadUtterances().CanSubmit, Is.False);
            Assert.That(own.SubmitUtterance(Command(own.ReadUtterances(), "말")).Error, Is.EqualTo(HoldemUtteranceError.SeatNotAllowed));
        }

        private static HoldemUtteranceCommand Command(HoldemUtteranceView view, string text, Guid? id = null)
            => new HoldemUtteranceCommand(view.SessionId, view.HandId, view.WindowId, id ?? Guid.NewGuid(), view.Street, view.ViewerSeat, text);
        private void Act(BettingAction action)
        {
            var state = State; var actor = state.CurrentSeat.Value;
            Assert.That(session.Submit(actor, HoldemCommand.Act(state.SessionId, state.HandId, Guid.NewGuid(),
                actor, state.SessionVersion, action)).Accepted, Is.True);
            inbox.Synchronize();
        }
        private void EndBetting()
        {
            int guard = 0;
            while (State.CurrentSeat.HasValue && guard++ < 20)
            {
                var view = session.GetSnapshot(State.CurrentSeat.Value);
                Act(view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
            }
            Assert.That(guard, Is.LessThan(20));
        }
        private void DealAndResume()
        {
            if (State.IsDealPending)
            {
                var state = State;
                Assert.That(session.DealUnchanged(new HoldemDealCommand(state.SessionId, state.HandId, state.PendingDeal.WindowId,
                    Guid.NewGuid(), state.SessionVersion, state.PendingDeal.Street)).Accepted, Is.True);
                inbox.Synchronize();
            }
            Assert.That(State.IsRevealPending, Is.True);
            Assert.That(own.ReadUtterances().CanSubmit, Is.False);
            var revealed = State;
            Assert.That(session.ResumeAfterReveal(new HoldemRevealCommand(revealed.SessionId, revealed.HandId,
                Guid.NewGuid(), revealed.SessionVersion, revealed.Street)).Accepted, Is.True);
            inbox.Synchronize();
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
    }
}
