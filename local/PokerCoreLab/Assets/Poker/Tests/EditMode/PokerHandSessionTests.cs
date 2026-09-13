using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class PokerHandSessionTests
    {
        private static readonly SeatId A = new SeatId(40), B = new SeatId(7), C = new SeatId(91), D = new SeatId(2), E = new SeatId(16);

        [Test]
        public void ConstructionDoesNotDealOrPostBlindsAndStartDoesBothOnce()
        {
            var rng = new CountingRandom();
            HandSetup setup = Setup(100, 100, 100);
            var session = new PokerHandSession(Guid.NewGuid(), setup, rng);
            Assert.That(session.State, Is.Null);
            Assert.That(session.Version, Is.Zero);
            Assert.That(rng.Calls, Is.Zero);
            var start = new StartHandCommand(session.HandId, Guid.NewGuid());
            HandReceipt receipt = session.Start(start);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(receipt.AppliedVersion, Is.EqualTo(1));
            Assert.That(receipt.Seat, Is.Null);
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(session.State.CurrentSeat, Is.EqualTo(A));
            Assert.That(session.State.Ledger.TotalCommitted, Is.EqualTo(3));
            Assert.That(session.State.GetHand(A).Count, Is.EqualTo(5));
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(37));
            Assert.That(rng.Calls, Is.EqualTo(51));
            PokerHandState before = session.State;
            Assert.That(session.Start(start), Is.SameAs(receipt));
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Error, Is.EqualTo(HandError.AlreadyStarted));
            Assert.That(session.State, Is.SameAs(before));
            Assert.That(rng.Calls, Is.EqualTo(51));
            Assert.That(setup.StartingLedger.TotalCommitted, Is.Zero);
        }

        [Test]
        public void ExplicitOrdersAreIndependentAndFrozenBeforeRandomCodeRuns()
        {
            ChipLedger ledger = Ledger(100, 100, 100);
            var deal = new[] { C, A, B };
            var opening = new[] { A, B, C };
            var exchange = new[] { B, C, A };
            var closing = new[] { C, A, B };
            var odd = new[] { B, A, C };
            var setup = new HandSetup(ledger, deal, opening, exchange, closing, 3, 7, odd);
            foreach (var order in new[] { deal, opening, exchange, closing, odd }) Array.Reverse(order);
            var session = New(setup);
            Assert.That(session.State.GetSeatAt(0), Is.EqualTo(C));
            Assert.That(session.State.CurrentBetting.GetLegalActions().CallAmount, Is.EqualTo(7));
            FinishBetting(session);
            Assert.That(session.State.CurrentSeat, Is.EqualTo(B));
            FinishExchange(session);
            Assert.That(session.State.CurrentSeat, Is.EqualTo(C));
            Assert.That(session.State.CurrentBetting.MinimumBet, Is.EqualTo(7));
            Assert.That(setup.OddChipPriority, Is.EqualTo(new[] { B, A, C }));
            Assert.Throws<NotSupportedException>(() => ((IList<SeatId>)setup.DealOrder)[0] = B);
        }

        [Test]
        public void HeadsUpUsesSuppliedBlindAndPostDrawOrdersAndCompletesExactlyOnce()
        {
            var setup = new HandSetup(Ledger(100, 100), new[] { B, A }, new[] { A, B }, new[] { B, A }, new[] { B, A }, 1, 2);
            var session = New(setup);
            Assert.That(session.State.CurrentSeat, Is.EqualTo(A));
            Act(session, BettingAction.Call());
            Act(session, BettingAction.Check());
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(session.State.CurrentSeat, Is.EqualTo(B));
            FinishExchange(session);
            Assert.That(session.State.CurrentSeat, Is.EqualTo(B));
            Act(session, BettingAction.Check());
            var last = Bet(session, BettingAction.Check());
            HandReceipt receipt = session.Submit(last.Seat, last);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.Version, Is.EqualTo(7));
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(4));
            PokerHandState final = session.State;
            Assert.That(session.Submit(last.Seat, last), Is.SameAs(receipt));
            Assert.That(session.State, Is.SameAs(final));
            Assert.That(session.State.CurrentSeat, Is.Null);
            Assert.That(session.State.CurrentBetting, Is.Null);
            Reject(session, HandCommand.Bet(session.HandId, Guid.NewGuid(), A, session.Version, BettingAction.Fold()), HandError.Complete);
            AssertConserved(session.State);
        }

        [Test]
        public void FirstStreetFoldWinSkipsDrawAndSecondStreetAndPreservesRefund()
        {
            var session = New(Setup(100, 100, 100));
            Act(session, BettingAction.Fold());
            Act(session, BettingAction.Fold());
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Exchange, Is.Null);
            Assert.That(session.State.SecondBetting, Is.Null);
            Assert.That(session.State.FirstBetting.RefundedSeat, Is.EqualTo(C));
            Assert.That(session.State.FirstBetting.RefundedAmount, Is.EqualTo(1));
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(2));
            Assert.That(session.State.Ledger.GetChips(A).Stack, Is.EqualTo(100));
            Assert.That(session.State.Ledger.GetChips(B).Stack, Is.EqualTo(99));
            Assert.That(session.State.Ledger.GetChips(C).Stack, Is.EqualTo(101));
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(37));
        }

        [Test]
        public void FirstStreetFoldIsExcludedEverywhereButCardsAndDeadMoneyRemain()
        {
            var setup = Setup(100, 100, 100);
            var session = New(setup);
            SeatHand folded = session.State.GetHand(B);
            Act(session, BettingAction.Call());
            Act(session, BettingAction.Fold());
            Act(session, BettingAction.Check());
            Assert.That(session.State.Exchange.ExchangeSeatCount, Is.EqualTo(2));
            Assert.That(session.State.Ledger.GetChips(B).Committed, Is.EqualTo(1));
            Assert.That(session.State.IsFolded(B), Is.True);
            FinishExchange(session);
            Assert.That(session.State.SecondBetting.SeatCount, Is.EqualTo(2));
            Assert.That(session.State.GetHand(B), Is.SameAs(folded));
            Act(session, BettingAction.BetTo(10));
            Act(session, BettingAction.Fold());
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.SecondBetting.RefundedAmount, Is.EqualTo(10));
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(5));
            Assert.That(session.State.Settlement.GetAwardedTo(B), Is.Zero);
            Assert.That(session.State.IsFolded(B), Is.True);
            AssertConserved(session.State);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void AllInPlayersStillExchangeAndLastDrawAutomaticallySettles(int count)
        {
            var session = New(Setup(1, 1));
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(session.State.FirstBetting.IsComplete, Is.True);
            Assert.That(session.State.FirstBetting.IsAllIn(A), Is.True);
            Assert.That(session.State.FirstBetting.IsAllIn(B), Is.True);
            Draw(session, count);
            var last = Exchange(session, count);
            long version = session.Version;
            var receipt = session.Submit(last.Seat, last);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.Version, Is.EqualTo(version + 1));
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.SecondBetting.IsComplete, Is.True);
            Assert.That(session.State.DiscardedCardCount, Is.EqualTo(2 * count));
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(42 - 2 * count));
            PokerHandState final = session.State;
            Assert.That(session.Submit(last.Seat, last), Is.SameAs(receipt));
            Assert.That(session.State, Is.SameAs(final));
            AssertConserved(final);
        }

        [Test]
        public void ShortBlindAndOnlySolventSeatSkipOnlyBettingNotDraw()
        {
            var session = New(Setup(100, 1));
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Exchange));
            FinishExchange(session);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.FirstBetting.Ledger.GetChips(A).Stack, Is.EqualTo(99));
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(2));
        }

        [Test]
        public void LargeUncalledBetIsReturnedBeforeDrawAndNotPaidTwice()
        {
            var session = New(Setup(100, 40));
            Act(session, BettingAction.RaiseTo(100));
            Act(session, BettingAction.Call());
            Assert.That(session.State.FirstBetting.RefundedAmount, Is.EqualTo(60));
            Assert.That(session.State.Ledger.GetChips(A).Stack, Is.EqualTo(60));
            FinishExchange(session);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(80));
            Assert.That(session.State.Ledger.TotalChips, Is.EqualTo(140));
            AssertConserved(session.State);
        }

        [Test]
        public void ThreePotSettlementUsesBothStreetTotalsAndOwnedFinalHands()
        {
            Card[][] hands = {
                Cards(Suit.Hearts, 10, 11, 12, 13, 14), Cards(Suit.Spades, 9, 10, 11, 12, 13),
                Cards(Suit.Clubs, 8, 9, 10, 11, 12), Cards(Suit.Diamonds, 2, 4, 6, 8, 10) };
            var order = new[] { A, B, C, D };
            var session = New(new HandSetup(Ledger(30, 70, 100, 100), order, order, order, new[] { B, C, D, A }, 1, 2),
                new RiggedTestRandom(hands));
            Act(session, BettingAction.RaiseTo(30));
            Act(session, BettingAction.Call()); Act(session, BettingAction.Call()); Act(session, BettingAction.Call());
            FinishExchange(session);
            Act(session, BettingAction.BetTo(40)); Act(session, BettingAction.Call());
            Act(session, BettingAction.RaiseTo(70)); Act(session, BettingAction.Call());
            Assert.That(session.State.IsComplete, Is.True);
            PotSettlement paid = session.State.Settlement;
            Assert.That(paid.PotCount, Is.EqualTo(3));
            Assert.That(Enumerable.Range(0, 3).Select(i => paid.GetPot(i).Amount), Is.EqualTo(new long[] { 120, 120, 60 }));
            Assert.That(new[] { paid.GetAwardedTo(A), paid.GetAwardedTo(B), paid.GetAwardedTo(C), paid.GetAwardedTo(D) },
                Is.EqualTo(new long[] { 120, 120, 60, 0 }));
            Assert.That(session.State.GetHand(A).Owner, Is.EqualTo(A));
            AssertConserved(session.State);
        }

        [Test]
        public void MissingOddPolicyCommitsFinalActionAndFreezesUnpaidResult()
        {
            var session = TieSession(null);
            PrepareTie(session);
            var last = Bet(session, BettingAction.Check());
            long version = session.Version;
            var receipt = session.Submit(last.Seat, last);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.Version, Is.EqualTo(version + 1));
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.AwaitingSettlementRule));
            Assert.That(session.State.IsComplete, Is.False);
            Assert.That(session.State.Settlement, Is.Null);
            Assert.That(session.State.Ledger.TotalCommitted, Is.EqualTo(5));
            Assert.That(session.State.CurrentSeat, Is.Null);
            Assert.That(session.State.SecondBetting.IsComplete, Is.True);
            PokerHandState pending = session.State;
            Assert.That(session.Submit(last.Seat, last), Is.SameAs(receipt));
            Reject(session, HandCommand.Bet(session.HandId, last.CommandId, last.Seat, last.ExpectedVersion, BettingAction.Fold()), HandError.CommandConflict);
            Reject(session, HandCommand.Bet(session.HandId, Guid.NewGuid(), A, session.Version, BettingAction.Fold()), HandError.SettlementRuleRequired);
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Error, Is.EqualTo(HandError.AlreadyStarted));
            Assert.That(session.State, Is.SameAs(pending));
            AssertConserved(pending);
        }

        [TestCase(true, 3, 2)]
        [TestCase(false, 2, 3)]
        public void ExplicitOddOrderIsFilteredForFoldsWithoutUsingNumericSeatOrder(bool aFirst, long aPaid, long bPaid)
        {
            var priority = aFirst ? new[] { C, A, B } : new[] { C, B, A };
            var session = TieSession(priority);
            Array.Reverse(priority);
            PrepareTie(session);
            Act(session, BettingAction.Check());
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Settlement.GetAwardedTo(A), Is.EqualTo(aPaid));
            Assert.That(session.State.Settlement.GetAwardedTo(B), Is.EqualTo(bPaid));
            Assert.That(session.State.Settlement.GetAwardedTo(C), Is.Zero);
        }

        [Test]
        public void LastAllInExchangeCanCommitIntoOddPolicyWaitAndCannotBeRevised()
        {
            var session = TieSession(null, 2, 2, 100);
            Act(session, BettingAction.Call());
            Act(session, BettingAction.Fold());
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(session.State.FirstBetting.IsAllIn(A), Is.True);
            Assert.That(session.State.FirstBetting.IsAllIn(B), Is.True);
            Draw(session, 0);
            var last = Exchange(session, 0);
            long version = session.Version;
            var receipt = session.Submit(last.Seat, last);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.AwaitingSettlementRule));
            Assert.That(session.Version, Is.EqualTo(version + 1));
            Assert.That(session.State.Ledger.TotalCommitted, Is.EqualTo(5));
            Assert.That(session.State.Exchange.IsComplete, Is.True);
            Assert.That(session.State.SecondBetting.IsComplete, Is.True);
            Assert.That(session.Submit(last.Seat, last), Is.SameAs(receipt));
            Reject(session, HandCommand.Exchange(session.HandId, last.CommandId, last.Seat, last.ExpectedVersion,
                new[] { session.State.GetHand(last.Seat)[0] }), HandError.CommandConflict);
            AssertConserved(session.State);
        }

        [Test]
        public void ShortSmallBlindKeepsDrawAndMainPotEligibilityAcrossBothStreets()
        {
            var session = New(Setup(100, 1, 100));
            Assert.That(session.State.FirstBetting.IsAllIn(B), Is.True);
            Act(session, BettingAction.Call());
            Assert.That(session.State.CurrentSeat, Is.EqualTo(C));
            Act(session, BettingAction.Check());
            Assert.That(session.State.Exchange.ExchangeSeatCount, Is.EqualTo(3));
            FinishExchange(session);
            Assert.That(session.State.SecondBetting.SeatCount, Is.EqualTo(3));
            Assert.That(session.State.SecondBetting.IsAllIn(B), Is.True);
            FinishBetting(session);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Settlement.PotCount, Is.EqualTo(2));
            Assert.That(session.State.Settlement.GetPot(0).EligibleSeatCount, Is.EqualTo(3));
            Assert.That(session.State.Settlement.GetPot(1).EligibleSeatCount, Is.EqualTo(2));
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(5));
            AssertConserved(session.State);
        }

        [Test]
        public void AcceptedBetRetryDoesNotRestoreOldStateAndConflictingPayloadIsRejected()
        {
            var session = New(Setup(100, 100));
            var command = Bet(session, BettingAction.Call());
            HandReceipt receipt = session.Submit(command.Seat, command);
            Act(session, BettingAction.Check()); Draw(session, 0);
            PokerHandState current = session.State;
            long version = session.Version;
            Assert.That(session.Submit(A, HandCommand.Bet(session.HandId, command.CommandId, A, command.ExpectedVersion, BettingAction.Call())), Is.SameAs(receipt));
            Assert.That(receipt.AppliedVersion, Is.LessThan(version));
            Reject(session, HandCommand.Bet(session.HandId, command.CommandId, A, command.ExpectedVersion, BettingAction.Fold()), HandError.CommandConflict);
            Reject(session, HandCommand.Bet(session.HandId, command.CommandId, A, version, BettingAction.Call()), HandError.CommandConflict);
            Reject(session, HandCommand.Bet(session.HandId, command.CommandId, B, command.ExpectedVersion, BettingAction.Call()), HandError.CommandConflict);
            Reject(session, HandCommand.Exchange(session.HandId, command.CommandId, A, command.ExpectedVersion, Array.Empty<Card>()), HandError.CommandConflict);
            Assert.That(session.State, Is.SameAs(current));
            Assert.That(session.Version, Is.EqualTo(version));
        }

        [Test]
        public void ExchangeSelectionOrderIsASetAndRetryAfterSettlementNeverDrawsAgain()
        {
            var session = New(Setup(100, 100));
            FinishBetting(session);
            var command = Exchange(session, 3);
            HandReceipt receipt = session.Submit(command.Seat, command);
            FinishExchange(session); FinishBetting(session);
            var reversed = HandCommand.Exchange(session.HandId, command.CommandId, command.Seat, command.ExpectedVersion,
                command.SelectedCards.Reverse().ToArray());
            PokerHandState final = session.State;
            Assert.That(session.Submit(command.Seat, reversed), Is.SameAs(receipt));
            Assert.That(session.State, Is.SameAs(final));
            Assert.That(final.DiscardedCardCount, Is.EqualTo(3));
        }

        [Test]
        public void RetryWithDifferentRaiseTargetOrExchangeSelectionConflicts()
        {
            var session = New(Setup(100, 100));
            var command = Bet(session, BettingAction.RaiseTo(4));
            session.Submit(command.Seat, command);
            Reject(session, HandCommand.Bet(session.HandId, command.CommandId, command.Seat, command.ExpectedVersion,
                BettingAction.RaiseTo(5)), HandError.CommandConflict);
            FinishBetting(session);
            var draw = Exchange(session, 1);
            session.Submit(draw.Seat, draw);
            Reject(session, HandCommand.Exchange(session.HandId, draw.CommandId, draw.Seat, draw.ExpectedVersion,
                Array.Empty<Card>()), HandError.CommandConflict);
        }

        [Test]
        public void ExactTieDividesWithoutAnOddPolicy()
        {
            var original = TieSession(null);
            Card[][] hands = { original.State.GetHand(A).ToArray(), original.State.GetHand(B).ToArray() };
            var session = New(Setup(100, 100), new RiggedTestRandom(hands));
            FinishBetting(session); FinishExchange(session); FinishBetting(session);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Settlement.GetAwardedTo(A), Is.EqualTo(2));
            Assert.That(session.State.Settlement.GetAwardedTo(B), Is.EqualTo(2));
        }

        [Test]
        public void FiveSeatsCanExchangeAllFiveWithoutReusingCards()
        {
            var session = New(Setup(100, 100, 100, 100, 100));
            FinishBetting(session);
            for (int i = 0; i < 5; i++) Draw(session, 5);
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.SecondBetting));
            Assert.That(session.State.DiscardedCardCount, Is.EqualTo(25));
            Assert.That(session.State.RemainingCardCount, Is.EqualTo(2));
            FinishBetting(session);
            Assert.That(session.State.IsComplete, Is.True);
            AssertConserved(session.State);
        }

        [Test]
        public void AuthenticationAndHandRoutingPrecedeRetryLookup()
        {
            var session = New(Setup(100, 100));
            var command = Bet(session, BettingAction.Call());
            session.Submit(A, command);
            foreach (SeatId unauthorized in new[] { B, default(SeatId) })
            {
                HandReceipt denied = session.Submit(unauthorized, command);
                Assert.That(denied.Error, Is.EqualTo(HandError.UnauthorizedSeat));
                Assert.That(denied.AppliedVersion, Is.Null);
            }
            Reject(session, HandCommand.Bet(Guid.NewGuid(), command.CommandId, A, command.ExpectedVersion, BettingAction.Call()), HandError.WrongHand);
            Assert.That(session.Start(new StartHandCommand(Guid.NewGuid(), Guid.NewGuid())).Error, Is.EqualTo(HandError.WrongHand));
        }

        [Test]
        public void RejectedIdsAreNotReservedAndIllegalActionsLeaveExactStateReference()
        {
            var session = New(Setup(100, 100));
            Guid id = Guid.NewGuid();
            Reject(session, HandCommand.Bet(session.HandId, id, A, 0, BettingAction.Call()), HandError.VersionMismatch);
            Reject(session, HandCommand.Exchange(session.HandId, id, A, session.Version, Array.Empty<Card>()), HandError.WrongPhase);
            Reject(session, HandCommand.Bet(session.HandId, id, B, session.Version, BettingAction.Check()), HandError.WrongTurn);
            Reject(session, HandCommand.Bet(session.HandId, id, C, session.Version, BettingAction.Call()), HandError.WrongTurn);
            Reject(session, HandCommand.Bet(session.HandId, id, A, session.Version, BettingAction.Check()), HandError.IllegalBet);
            Reject(session, HandCommand.Bet(session.HandId, id, A, session.Version, BettingAction.RaiseTo(3)), HandError.IllegalBet);
            Assert.That(session.Submit(A, HandCommand.Bet(session.HandId, id, A, session.Version, BettingAction.Call())).Accepted, Is.True);
        }

        [Test]
        public void ExchangeRejectsOtherPlayersCardsWrongPhaseWrongTurnAndStaleVersion()
        {
            var session = New(Setup(100, 100));
            FinishBetting(session);
            Guid id = Guid.NewGuid();
            Reject(session, HandCommand.Exchange(session.HandId, id, A, session.Version, new[] { session.State.GetHand(B)[0] }), HandError.CardNotOwned);
            Reject(session, HandCommand.Exchange(session.HandId, id, B, session.Version, Array.Empty<Card>()), HandError.WrongTurn);
            Reject(session, HandCommand.Bet(session.HandId, id, A, session.Version, BettingAction.Check()), HandError.WrongPhase);
            Reject(session, HandCommand.Exchange(session.HandId, id, A, session.Version - 1, Array.Empty<Card>()), HandError.VersionMismatch);
            Assert.That(session.Submit(A, HandCommand.Exchange(session.HandId, id, A, session.Version, Array.Empty<Card>())).Accepted, Is.True);
        }

        [Test]
        public void StartAndPlayerCommandsCannotReuseEachOthersAcceptedIds()
        {
            var session = new PokerHandSession(Guid.NewGuid(), Setup(100, 100), new CountingRandom());
            Guid startId = Guid.NewGuid();
            var start = new StartHandCommand(session.HandId, startId);
            HandReceipt receipt = session.Start(start);
            Reject(session, HandCommand.Bet(session.HandId, startId, A, session.Version, BettingAction.Call()), HandError.CommandConflict);
            var bet = Bet(session, BettingAction.Call()); session.Submit(A, bet);
            Assert.That(session.Start(new StartHandCommand(session.HandId, bet.CommandId)).Error, Is.EqualTo(HandError.CommandConflict));
            FinishBetting(session); FinishExchange(session); FinishBetting(session);
            PokerHandState final = session.State;
            Assert.That(session.Start(start), Is.SameAs(receipt));
            Assert.That(session.State, Is.SameAs(final));
        }

        [Test]
        public void FailedRandomDoesNotPublishBlindsOrConsumeCommandIdAndMayRetry()
        {
            var failure = new ApplicationException("test RNG failure");
            var rng = new CountingRandom { Failure = failure };
            var session = new PokerHandSession(Guid.NewGuid(), Setup(100, 100), rng);
            var command = new StartHandCommand(session.HandId, Guid.NewGuid());
            Assert.That(Assert.Throws<ApplicationException>(() => session.Start(command)), Is.SameAs(failure));
            Assert.That(session.State, Is.Null);
            Assert.That(session.Version, Is.Zero);
            rng.Failure = null;
            Assert.That(session.Start(command).Accepted, Is.True);
            Assert.That(session.State.Ledger.TotalCommitted, Is.EqualTo(3));
        }

        [Test]
        public void ReentrantRandomCannotCommitNestedStartOrPlayerAction()
        {
            var rng = new CountingRandom();
            var session = new PokerHandSession(Guid.NewGuid(), Setup(100, 100), rng);
            rng.Callback = () =>
            {
                Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Error, Is.EqualTo(HandError.Busy));
                Assert.That(session.Submit(A, HandCommand.Bet(session.HandId, Guid.NewGuid(), A, 0, BettingAction.Call())).Error, Is.EqualTo(HandError.Busy));
                Assert.That(session.State, Is.Null);
            };
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True);
            Assert.That(rng.Calls, Is.EqualTo(51));
            Assert.That(session.Version, Is.EqualTo(1));
        }

        [Test]
        public void WrongStartHandBeforeDealNeverUsesRandom()
        {
            var rng = new CountingRandom();
            var session = new PokerHandSession(Guid.NewGuid(), Setup(100, 100), rng);
            Assert.That(session.Start(new StartHandCommand(Guid.NewGuid(), Guid.NewGuid())).Error, Is.EqualTo(HandError.WrongHand));
            Reject(session, HandCommand.Bet(session.HandId, Guid.NewGuid(), A, 0, BettingAction.Call()), HandError.NotStarted);
            Assert.That(rng.Calls, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(6)]
        public void HandCapacityIsRejectedBeforeAnyDeal(int count)
        {
            SeatId[] order = Enumerable.Range(1, count).Select(i => new SeatId(i)).ToArray();
            var ledger = ChipLedger.Create(order.Select(s => new SeatChips(s, 100)).ToArray());
            Assert.Throws<ArgumentException>(() => new HandSetup(ledger, order, order, order, order, 1, 2));
        }

        [Test]
        public void MalformedSetupAndEveryOrderAreRejected()
        {
            var order = new[] { A, B };
            var ledger = Ledger(100, 100);
            Assert.Throws<ArgumentNullException>(() => new HandSetup(null, order, order, order, order, 1, 2));
            Assert.Throws<ArgumentException>(() => new HandSetup(Ledger(0, 100), order, order, order, order, 1, 2));
            Assert.Throws<ArgumentException>(() => new HandSetup(ledger.Contribute(A, 1), order, order, order, order, 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HandSetup(ledger, order, order, order, order, 0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HandSetup(ledger, order, order, order, order, 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HandSetup(ledger, order, order, order, order, 1, 0));
            for (int slot = 0; slot < 5; slot++)
            {
                foreach (var bad in new[] { Array.Empty<SeatId>(), new[] { A, A }, new[] { A, C }, new[] { A, default(SeatId) } })
                {
                    IReadOnlyList<SeatId>[] orders = { order, order, order, order, order };
                    orders[slot] = bad;
                    Assert.Throws<ArgumentException>(() => new HandSetup(ledger, orders[0], orders[1], orders[2], orders[3], 1, 2, orders[4]));
                }
                if (slot < 4)
                {
                    IReadOnlyList<SeatId>[] orders = { order, order, order, order };
                    orders[slot] = null;
                    Assert.Throws<ArgumentNullException>(() => new HandSetup(ledger, orders[0], orders[1], orders[2], orders[3], 1, 2));
                }
            }
        }

        [Test]
        public void MalformedCommandsAndSessionArgumentsCannotEnterDomain()
        {
            Guid h = Guid.NewGuid(), c = Guid.NewGuid();
            Assert.Throws<ArgumentException>(() => new PokerHandSession(Guid.Empty, Setup(1, 1), new CountingRandom()));
            Assert.Throws<ArgumentNullException>(() => new PokerHandSession(h, null, new CountingRandom()));
            Assert.Throws<ArgumentNullException>(() => new PokerHandSession(h, Setup(1, 1), null));
            Assert.Throws<ArgumentException>(() => new StartHandCommand(Guid.Empty, c));
            Assert.Throws<ArgumentException>(() => new StartHandCommand(h, Guid.Empty));
            Assert.Throws<ArgumentNullException>(() => New(Setup(1, 1)).Start(null));
            Assert.Throws<ArgumentNullException>(() => New(Setup(1, 1)).Submit(A, null));
            Assert.Throws<ArgumentException>(() => HandCommand.Bet(Guid.Empty, c, A, 0, BettingAction.Call()));
            Assert.Throws<ArgumentException>(() => HandCommand.Bet(h, Guid.Empty, A, 0, BettingAction.Call()));
            Assert.Throws<ArgumentException>(() => HandCommand.Bet(h, c, default, 0, BettingAction.Call()));
            Assert.Throws<ArgumentOutOfRangeException>(() => HandCommand.Bet(h, c, A, -1, BettingAction.Call()));
            Assert.Throws<ArgumentNullException>(() => HandCommand.Bet(h, c, A, 0, null));
            Assert.Throws<ArgumentNullException>(() => HandCommand.Exchange(h, c, A, 0, null));
            Assert.Throws<ArgumentException>(() => HandCommand.Exchange(h, c, A, 0, new[] { default(Card) }));
            Assert.Throws<ArgumentException>(() => HandCommand.Exchange(h, c, A, 0, new[] { Card.FromId(0), Card.FromId(0) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => HandCommand.Exchange(h, c, A, 0, Enumerable.Range(0, 6).Select(Card.FromId).ToArray()));
        }

        [Test]
        public void CommandOwnsNormalizedSelectionAndQueriesGuardInvalidSeats()
        {
            var session = New(Setup(100, 100));
            var cards = session.State.GetHand(A).Take(2).ToArray();
            var expected = cards.OrderBy(c => c.Id).ToArray();
            HandCommand command = HandCommand.Exchange(session.HandId, Guid.NewGuid(), A, session.Version, cards);
            cards[0] = session.State.GetHand(B)[0];
            Assert.That(command.SelectedCards, Is.EqualTo(expected));
            Assert.Throws<NotSupportedException>(() => ((IList<Card>)command.SelectedCards)[0] = cards[0]);
            Assert.Throws<KeyNotFoundException>(() => session.State.IsFolded(C));
            Assert.Throws<KeyNotFoundException>(() => session.State.GetHand(C));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.State.GetSeatAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.State.GetSeatAt(2));
        }

        [Test]
        public void NewHandUsesPriorFinalChipsWithoutAutomaticResetAndRejectsOldHandRequest()
        {
            var first = New(Setup(100, 100));
            var old = Bet(first, BettingAction.Fold());
            first.Submit(old.Seat, old);
            ChipLedger ended = first.State.Ledger;
            var order = new[] { B, A };
            var second = New(new HandSetup(ended, order, order, order, order, 1, 2));
            Assert.That(second.State.Ledger.TotalChips, Is.EqualTo(200));
            Assert.That(second.State.Ledger.GetChips(A).Stack, Is.EqualTo(97));
            Assert.That(second.State.Ledger.GetChips(B).Stack, Is.EqualTo(100));
            Reject(second, old, HandError.WrongHand);
            Assert.That(first.State.Ledger, Is.SameAs(ended));
        }

        [Test]
        public void FullHandPreservesInt64BoundaryTotal()
        {
            var session = New(Setup(long.MaxValue - 2, 2));
            Act(session, BettingAction.Call());
            FinishExchange(session);
            Assert.That(session.State.IsComplete, Is.True);
            Assert.That(session.State.Settlement.TotalAwarded, Is.EqualTo(4));
            Assert.That(session.State.Ledger.TotalChips, Is.EqualTo(long.MaxValue));
            AssertConserved(session.State);
        }

        [Test]
        public void AllowsMatchesApplyAndCompletedRoundAllowsNothing()
        {
            var session = New(Setup(100, 100));
            LegalBettingActions legal = session.State.CurrentBetting.GetLegalActions();
            Assert.Throws<ArgumentNullException>(() => legal.Allows(null));
            Assert.That(legal.Allows(BettingAction.RaiseTo(4)), Is.True);
            Assert.That(legal.Allows(BettingAction.RaiseTo(3)), Is.False);
            Act(session, BettingAction.Fold());
            legal = session.State.FirstBetting.GetLegalActions();
            foreach (BettingAction action in new[] { BettingAction.Fold(), BettingAction.Check(), BettingAction.Call(), BettingAction.BetTo(2), BettingAction.RaiseTo(4) })
                Assert.That(legal.Allows(action), Is.False);
        }

        [Test]
        public void SeededFullHandTracesPreserveCardsChipsSnapshotsAndAcceptedRetries()
        {
            var random = new Random(20260912);
            for (int run = 0; run < 300; run++)
            {
                int count = random.Next(2, 6);
                var stacks = Enumerable.Range(0, count).Select(_ => (long)random.Next(1, 101)).ToArray();
                var setup = Setup(stacks);
                var session = New(setup, new SeededRandom(run));
                int steps = 0;
                while (!session.State.IsComplete && session.State.Phase != HandPhase.AwaitingSettlementRule)
                {
                    Assert.That(++steps, Is.LessThan(2000));
                    PokerHandState before = session.State;
                    long oldPot = before.Ledger.TotalCommitted;
                    Card[] oldHand = before.GetHand(before.CurrentSeat.Value).ToArray();
                    HandCommand command;
                    if (before.Phase == HandPhase.Exchange) command = Exchange(session, random.Next(0, 6));
                    else
                    {
                        LegalBettingActions legal = before.CurrentBetting.GetLegalActions();
                        var choices = new List<BettingAction> { BettingAction.Fold() };
                        if (legal.CanCall) choices.Add(BettingAction.Call());
                        if (legal.CanCheck) choices.Add(BettingAction.Check());
                        if (legal.CanBet) { choices.Add(BettingAction.BetTo(legal.MinimumAggressiveTarget.Value)); choices.Add(BettingAction.BetTo(legal.MaximumAggressiveTarget.Value)); }
                        if (legal.CanRaise) { choices.Add(BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value)); choices.Add(BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)); }
                        command = Bet(session, choices[random.Next(choices.Count)]);
                    }
                    long oldVersion = session.Version;
                    HandReceipt receipt = session.Submit(command.Seat, command);
                    Assert.That(receipt.Accepted, Is.True, "Run " + run + " step " + steps);
                    Assert.That(session.Version, Is.EqualTo(oldVersion + 1));
                    Assert.That(before.Ledger.TotalCommitted, Is.EqualTo(oldPot));
                    Assert.That(before.GetHand(command.Seat), Is.EqualTo(oldHand));
                    PokerHandState after = session.State;
                    Assert.That(session.Submit(command.Seat, command), Is.SameAs(receipt));
                    Assert.That(session.State, Is.SameAs(after));
                    AssertConserved(after);
                }
                Assert.That(session.State.CurrentSeat, Is.Null);
                if (session.State.IsComplete) Assert.That(session.State.Ledger.TotalCommitted, Is.Zero);
                else Assert.That(session.State.Settlement, Is.Null);
            }
        }

        private static PokerHandSession TieSession(SeatId[] priority, params long[] stacks)
        {
            Card[][] hands = {
                new[] { new Card(Rank.Ace, Suit.Clubs), new Card(Rank.King, Suit.Diamonds), new Card(Rank.Queen, Suit.Hearts), new Card(Rank.Jack, Suit.Spades), new Card(Rank.Nine, Suit.Clubs) },
                new[] { new Card(Rank.Ace, Suit.Diamonds), new Card(Rank.King, Suit.Clubs), new Card(Rank.Queen, Suit.Spades), new Card(Rank.Jack, Suit.Hearts), new Card(Rank.Nine, Suit.Diamonds) },
                new[] { new Card(Rank.Two, Suit.Clubs), new Card(Rank.Three, Suit.Diamonds), new Card(Rank.Four, Suit.Hearts), new Card(Rank.Five, Suit.Spades), new Card(Rank.Seven, Suit.Clubs) } };
            var order = new[] { A, B, C };
            return New(new HandSetup(Ledger(stacks.Length == 0 ? new long[] { 100, 100, 100 } : stacks), order,
                new[] { A, C, B }, order, order, 1, 2, priority), new RiggedTestRandom(hands));
        }
        private static void PrepareTie(PokerHandSession session)
        { Act(session, BettingAction.Call()); Act(session, BettingAction.Fold()); Act(session, BettingAction.Check()); FinishExchange(session); Act(session, BettingAction.Check()); }
        private static Card[] Cards(Suit suit, params int[] ranks) => ranks.Select(r => new Card((Rank)r, suit)).ToArray();
        private static HandSetup Setup(params long[] stacks)
        {
            var ledger = Ledger(stacks);
            var order = Enumerable.Range(0, stacks.Length).Select(ledger.GetSeatAt).ToArray();
            return new HandSetup(ledger, order, order, order, order, 1, 2);
        }
        private static ChipLedger Ledger(params long[] stacks)
        {
            SeatId[] order = { A, B, C, D, E };
            return ChipLedger.Create(stacks.Select((v, i) => new SeatChips(order[i], v)).ToArray());
        }
        private static PokerHandSession New(HandSetup setup, IRandomSource rng = null)
        {
            var result = new PokerHandSession(Guid.NewGuid(), setup, rng ?? new CountingRandom());
            Assert.That(result.Start(new StartHandCommand(result.HandId, Guid.NewGuid())).Accepted, Is.True);
            return result;
        }
        private static HandCommand Bet(PokerHandSession session, BettingAction action) =>
            HandCommand.Bet(session.HandId, Guid.NewGuid(), session.State.CurrentSeat.Value, session.Version, action);
        private static HandCommand Exchange(PokerHandSession session, int count) => HandCommand.Exchange(session.HandId,
            Guid.NewGuid(), session.State.CurrentSeat.Value, session.Version, session.State.GetHand(session.State.CurrentSeat.Value).Take(count).ToArray());
        private static void Act(PokerHandSession session, BettingAction action)
        { HandCommand command = Bet(session, action); Assert.That(session.Submit(command.Seat, command).Accepted, Is.True); }
        private static void Draw(PokerHandSession session, int count)
        { HandCommand command = Exchange(session, count); Assert.That(session.Submit(command.Seat, command).Accepted, Is.True); }
        private static void FinishBetting(PokerHandSession session)
        {
            while (session.State.CurrentBetting != null)
                Act(session, session.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check());
        }
        private static void FinishExchange(PokerHandSession session)
        { while (session.State.Phase == HandPhase.Exchange) Draw(session, 0); }
        private static void Reject(PokerHandSession session, HandCommand command, HandError error)
        {
            PokerHandState before = session.State; long version = session.Version;
            HandReceipt receipt = session.Submit(command.Seat, command);
            Assert.That(receipt.Accepted, Is.False); Assert.That(receipt.Error, Is.EqualTo(error));
            Assert.That(receipt.AppliedVersion, Is.Null); Assert.That(session.State, Is.SameAs(before));
            Assert.That(session.Version, Is.EqualTo(version));
        }
        private static void AssertConserved(PokerHandState state)
        {
            decimal total = state.Ledger.TotalCommitted;
            var seen = new HashSet<Card>();
            for (int i = 0; i < state.SeatCount; i++)
            {
                SeatId seat = state.GetSeatAt(i);
                total += state.Ledger.GetChips(seat).Stack;
                Assert.That(state.Ledger.GetChips(seat).Stack, Is.GreaterThanOrEqualTo(0));
                Assert.That(state.GetHand(seat).Owner, Is.EqualTo(seat));
                foreach (Card card in state.GetHand(seat)) Assert.That(seen.Add(card), Is.True);
            }
            Assert.That(total, Is.EqualTo((decimal)state.Ledger.TotalChips));
            Assert.That(state.SeatCount * 5 + state.RemainingCardCount + state.DiscardedCardCount, Is.EqualTo(52));
        }
        private sealed class CountingRandom : IRandomSource
        {
            public int Calls; public Exception Failure; public Action Callback;
            public int NextInt(int upper)
            { Calls++; if (Failure != null) throw Failure; Callback?.Invoke(); return upper - 1; }
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int upper) => random.Next(upper);
        }
        // Test-only Fisher-Yates choices, not a production deck injection or fairness-certified RNG.
        private sealed class RiggedTestRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public RiggedTestRandom(Card[][] hands)
            {
                var target = new List<Card>();
                for (int i = 0; i < 5; i++) foreach (Card[] hand in hands) target.Add(hand[i]);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)).ToArray());
                Card[] working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1);
                    choices.Enqueue(selected);
                    Card swap = working[i]; working[i] = working[selected]; working[selected] = swap;
                }
            }
            public int NextInt(int upper) => choices.Dequeue();
        }
    }
}
