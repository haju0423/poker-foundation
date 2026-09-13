using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class ExchangeSessionTests
    {
        private static readonly Guid Hand = new Guid("b7f788b5-2511-4491-8446-0ed32aa45210");
        private static readonly Guid Command = new Guid("63052793-68ea-4591-9fa4-740d7372dd22");
        private static readonly SeatId First = new SeatId(42);
        private static readonly SeatId Second = new SeatId(7);

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void AValidRequestCommitsExactlyOnceAndLeavesOldSnapshotUnchanged(int count)
        {
            ExchangeSession session = Session();
            ExchangeRound before = session.State;
            ExchangeCommand command = Request(session, count);
            ExchangeReceipt result = session.Submit(First, command);
            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Error, Is.EqualTo(ExchangeError.None));
            Assert.That(result.HandId, Is.EqualTo(Hand));
            Assert.That(result.CommandId, Is.EqualTo(Command));
            Assert.That(result.Seat, Is.EqualTo(First));
            Assert.That(result.AppliedVersion, Is.EqualTo(1));
            Assert.That(session.Version, Is.EqualTo(1));
            Assert.That(session.State.CompletedSeatCount, Is.EqualTo(1));
            Assert.That(session.State.CurrentSeat, Is.EqualTo(Second));
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(42 - count));
            Assert.That(session.State.DiscardedCardCount, Is.EqualTo(count));
            Assert.That(before.CompletedSeatCount, Is.Zero);
            Assert.That(before.RemainingCardCount, Is.EqualTo(42));
            ExchangeRound committed = session.State;
            Assert.That(session.Submit(First, command), Is.SameAs(result));
            Assert.That(session.State, Is.SameAs(committed));
            Assert.That(session.Version, Is.EqualTo(1));
        }

        [Test]
        public void EquivalentReorderedSelectionReturnsOriginalReceipt()
        {
            ExchangeSession session = Session();
            SeatHand hand = session.State.GetHand(First);
            var a = new ExchangeCommand(Hand, Command, First, 0, new[] { hand[4], hand[1] });
            var b = new ExchangeCommand(Hand, Command, First, 0, new[] { hand[1], hand[4] });
            ExchangeReceipt receipt = session.Submit(First, a);
            Assert.That(session.Submit(First, b), Is.SameAs(receipt));
            Assert.That(session.Version, Is.EqualTo(1));
        }

        [Test]
        public void RetriesAfterOtherActionsAndCompletionCannotRollBackCurrentState()
        {
            ExchangeSession session = Session();
            ExchangeCommand first = Request(session, 2);
            ExchangeReceipt receipt = session.Submit(First, first);
            var second = new ExchangeCommand(Hand, Guid.NewGuid(), Second, 1, Array.Empty<Card>());
            ExchangeReceipt last = session.Submit(Second, second);
            ExchangeRound finished = session.State;
            Assert.That(finished.IsComplete, Is.True);
            Assert.That(session.Submit(First, first), Is.SameAs(receipt));
            Assert.That(session.Submit(Second, second), Is.SameAs(last));
            Assert.That(receipt.AppliedVersion, Is.EqualTo(1));
            Assert.That(session.Version, Is.EqualTo(2));
            Assert.That(session.State, Is.SameAs(finished));
            Reject(session, First, new ExchangeCommand(Hand, Guid.NewGuid(), First, 2, Array.Empty<Card>()), ExchangeError.Complete);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void AcceptedIdWithDifferentPayloadConflictsBeforeStaleOrTurnChecks(int kind)
        {
            ExchangeSession session = Session();
            ExchangeCommand original = Request(session, 1);
            ExchangeReceipt accepted = session.Submit(First, original);
            SeatId seat = kind == 0 ? Second : First;
            var changed = new ExchangeCommand(Hand, Command, seat, kind == 1 ? 1 : 0,
                kind == 2 ? Array.Empty<Card>() : original.SelectedCards);
            Reject(session, seat, changed, ExchangeError.CommandConflict);
            Assert.That(session.Submit(First, original), Is.SameAs(accepted));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void AuthorizationIsRecheckedBeforeAnyReceiptLookup(int kind)
        {
            ExchangeSession session = Session();
            ExchangeCommand command = Request(session, 1);
            ExchangeReceipt accepted = session.Submit(First, command);
            SeatId unauthorized = kind == 0 ? Second : kind == 1 ? default(SeatId) : new SeatId(999);
            Reject(session, unauthorized, command, ExchangeError.UnauthorizedSeat);
            Assert.That(session.Submit(First, command), Is.SameAs(accepted));
        }

        [Test]
        public void WrongHandCannotReadCachedReceiptOrPoisonAnUnusedId()
        {
            ExchangeSession session = Session();
            var wrong = new ExchangeCommand(Guid.NewGuid(), Command, First, 0, Array.Empty<Card>());
            Reject(session, First, wrong, ExchangeError.WrongHand);
            var right = Request(session, 0);
            ExchangeReceipt accepted = session.Submit(First, right);
            Reject(session, First, wrong, ExchangeError.WrongHand);
            Assert.That(session.Submit(First, right), Is.SameAs(accepted));
        }

        [TestCase(0)] [TestCase(2)] [TestCase(long.MaxValue)]
        public void NewIdWithStaleOrFutureVersionCannotChangeState(long version)
        {
            ExchangeSession session = Session();
            session.Submit(First, Request(session, 1));
            Guid id = Guid.NewGuid();
            Reject(session, Second, new ExchangeCommand(Hand, id, Second, version, Array.Empty<Card>()), ExchangeError.VersionMismatch);
            Assert.That(session.Submit(Second, new ExchangeCommand(Hand, id, Second, 1, Array.Empty<Card>())).Accepted, Is.True);
        }

        [Test]
        public void WrongTurnAndNonparticipantDoNotChangeStateOrReserveIds()
        {
            ExchangeSession session = Session();
            Reject(session, Second, new ExchangeCommand(Hand, Command, Second, 0, Array.Empty<Card>()), ExchangeError.WrongTurn);
            var unknown = new SeatId(999);
            Reject(session, unknown, new ExchangeCommand(Hand, Command, unknown, 0, Array.Empty<Card>()), ExchangeError.WrongTurn);
            Assert.That(session.Submit(First, Request(session, 0)).Accepted, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void UnownedCardsDoNotPartiallyApplyOrReserveId(bool otherHand)
        {
            ExchangeSession session = Session();
            Card invalid = otherHand ? session.State.GetHand(Second)[0] : Card.FromId(51);
            var bad = new ExchangeCommand(Hand, Command, First, 0, new[] { session.State.GetHand(First)[0], invalid });
            Reject(session, First, bad, ExchangeError.CardNotOwned);
            Assert.That(session.Submit(First, Request(session, 1)).Accepted, Is.True);
            Assert.That(session.State.GetHand(First)[0].Id, Is.EqualTo(10));
        }

        [Test]
        public void MutatedInputOrBranchedOldSnapshotCannotReplaceOwnedState()
        {
            var order = new[] { First, Second };
            var session = new ExchangeSession(Hand, Deal(), order);
            ExchangeRound old = session.State;
            var cards = new[] { old.GetHand(First)[0] };
            var command = new ExchangeCommand(Hand, Command, First, 0, cards);
            order[0] = Second;
            cards[0] = Card.FromId(51);
            Assert.Throws<NotSupportedException>(() => ((IList<Card>)command.SelectedCards)[0] = Card.FromId(51));
            ExchangeRound branch = old.Apply(First, Array.Empty<Card>());
            Assert.That(session.Version, Is.Zero);
            Assert.That(session.State, Is.SameAs(old));
            session.Submit(First, command);
            Assert.That(session.State.DiscardedCardCount, Is.EqualTo(1));
            Assert.That(session.State, Is.Not.SameAs(branch));
        }

        [Test]
        public void EmptyOrderIsCompletedWithoutAnyAcceptedRequest()
        {
            var session = new ExchangeSession(Hand, Deal(), Array.Empty<SeatId>());
            Assert.That(session.State.IsComplete, Is.True);
            Reject(session, First, Request(session, 0), ExchangeError.Complete);
        }

        [Test]
        public void AllFiveSeatsFinishAndAllReceiptsRemainReplayable()
        {
            var seats = new SeatId[5];
            for (int i = 0; i < seats.Length; i++) seats[i] = new SeatId(i + 1);
            var rng = new OrderedRandom();
            var session = new ExchangeSession(Hand, InitialDeal.Create(rng, seats), seats);
            var commands = new ExchangeCommand[5];
            var receipts = new ExchangeReceipt[5];
            for (int i = 0; i < seats.Length; i++)
            {
                commands[i] = new ExchangeCommand(Hand, Guid.NewGuid(), seats[i], i, session.State.GetHand(seats[i]));
                receipts[i] = session.Submit(seats[i], commands[i]);
                Assert.That(receipts[i].Accepted, Is.True);
            }
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(2));
            Assert.That(session.State.DiscardedCardCount, Is.EqualTo(25));
            ExchangeRound final = session.State;
            for (int i = 4; i >= 0; i--) Assert.That(session.Submit(seats[i], commands[i]), Is.SameAs(receipts[i]));
            Assert.That(session.Version, Is.EqualTo(5));
            Assert.That(session.State, Is.SameAs(final));
            Assert.That(rng.Calls, Is.EqualTo(51));
        }

        [Test]
        public void NullSubmitIsAnArgumentErrorWithoutStateMutation()
        {
            ExchangeSession session = Session();
            ExchangeRound before = session.State;
            Assert.Throws<ArgumentNullException>(() => session.Submit(First, null));
            Assert.That(session.State, Is.SameAs(before));
            Assert.That(session.Version, Is.Zero);
        }

        [Test]
        public void CommandRejectsMalformedEnvelopeAndSelection()
        {
            Card[] none = Array.Empty<Card>();
            Assert.Throws<ArgumentException>(() => new ExchangeCommand(Guid.Empty, Command, First, 0, none));
            Assert.Throws<ArgumentException>(() => new ExchangeCommand(Hand, Guid.Empty, First, 0, none));
            Assert.Throws<ArgumentException>(() => new ExchangeCommand(Hand, Command, default(SeatId), 0, none));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExchangeCommand(Hand, Command, First, -1, none));
            Assert.Throws<ArgumentNullException>(() => new ExchangeCommand(Hand, Command, First, 0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExchangeCommand(Hand, Command, First, 0, new Card[6]));
            Assert.Throws<ArgumentException>(() => new ExchangeCommand(Hand, Command, First, 0, new[] { default(Card) }));
            Assert.Throws<ArgumentException>(() => new ExchangeCommand(Hand, Command, First, 0, new[] { Card.FromId(0), Card.FromId(0) }));
            Assert.Throws<ApplicationException>(() => new ExchangeCommand(Hand, Command, First, 0, new ThrowingCards()));
        }

        [Test]
        public void SessionValidatesItsIdentityAndDelegatesInitialStateValidation()
        {
            Assert.Throws<ArgumentException>(() => new ExchangeSession(Guid.Empty, Deal(), new[] { First }));
            Assert.Throws<ArgumentNullException>(() => new ExchangeSession(Hand, null, new[] { First }));
            Assert.Throws<ArgumentNullException>(() => new ExchangeSession(Hand, Deal(), null));
            Assert.Throws<ArgumentException>(() => new ExchangeSession(Hand, Deal(), new[] { First, First }));
        }

        [Test]
        public void StaleNewRequestIsRejectedBeforeCompletionAndDoesNotReserveId()
        {
            var session = new ExchangeSession(Hand, Deal(), new[] { First });
            session.Submit(First, Request(session, 0));
            Guid id = Guid.NewGuid();
            Reject(session, First, new ExchangeCommand(Hand, id, First, 0, Array.Empty<Card>()), ExchangeError.VersionMismatch);
            Reject(session, First, new ExchangeCommand(Hand, id, First, 1, Array.Empty<Card>()), ExchangeError.Complete);
        }

        [Test]
        public void TwoInstancesWithSameHandIdRemainIndependentNotAGlobalRegistry()
        {
            InitialDeal deal = Deal();
            var a = new ExchangeSession(Hand, deal, new[] { First, Second });
            var b = new ExchangeSession(Hand, deal, new[] { First, Second });
            ExchangeCommand command = Request(a, 1);
            ExchangeReceipt first = a.Submit(First, command);
            Assert.That(b.Version, Is.Zero);
            Assert.That(b.State.CompletedSeatCount, Is.Zero);
            Assert.That(b.Submit(First, command), Is.Not.SameAs(first));
            Assert.That(a.Version, Is.EqualTo(1));
            Assert.That(b.Version, Is.EqualTo(1));
            Assert.That(deal.RemainingCardCount, Is.EqualTo(42));
        }

        [Test]
        public void ExcludedSeatCannotActAndItsHandIsPreserved()
        {
            InitialDeal deal = Deal();
            var session = new ExchangeSession(Hand, deal, new[] { Second });
            Reject(session, First, Request(session, 1), ExchangeError.WrongTurn);
            var command = new ExchangeCommand(Hand, Command, Second, 0, deal.GetHand(Second));
            Assert.That(session.Submit(Second, command).Accepted, Is.True);
            Assert.That(session.State.GetHand(First), Is.EqualTo(deal.GetHand(First)));
            Assert.That(session.State.IsComplete, Is.True);
        }

        private static ExchangeSession Session() => new ExchangeSession(Hand, Deal(), new[] { First, Second });
        private static InitialDeal Deal() => InitialDeal.Create(new OrderedRandom(), new[] { First, Second });
        private static ExchangeCommand Request(ExchangeSession session, int count)
        {
            var cards = new Card[count];
            for (int i = 0; i < count; i++) cards[i] = session.State.GetHand(First)[i];
            return new ExchangeCommand(Hand, Command, First, session.Version, cards);
        }
        private static void Reject(ExchangeSession session, SeatId seat, ExchangeCommand command, ExchangeError error)
        {
            ExchangeRound before = session.State;
            long version = session.Version;
            ExchangeReceipt receipt = session.Submit(seat, command);
            Assert.That(receipt.Accepted, Is.False);
            Assert.That(receipt.Error, Is.EqualTo(error));
            Assert.That(receipt.AppliedVersion, Is.Null);
            Assert.That(session.State, Is.SameAs(before));
            Assert.That(session.Version, Is.EqualTo(version));
        }
        private sealed class OrderedRandom : IRandomSource
        {
            public int Calls;
            public int NextInt(int exclusiveMax) { Calls++; return exclusiveMax - 1; }
        }
        private sealed class ThrowingCards : IReadOnlyList<Card>
        {
            public int Count => 1;
            public Card this[int index] => throw new ApplicationException("Read failure");
            public IEnumerator<Card> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
