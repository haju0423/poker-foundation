using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemCardChangeTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);
        private sealed class Ordered : IRandomSource { public int NextInt(int maximum) => maximum - 1; }
        private static HoldemSession Start(bool accusations = true, int seats = 4, long stack = 100)
        {
            var roster = Enumerable.Range(1, seats).Select(i => new SeatId(i)).ToArray();
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(stack, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal,
                accusations ? HoldemAccusationMode.CollectLatestChoiceUntilHostCloses : HoldemAccusationMode.Disabled,
                HoldemDealPolicy.WaitForHost), roster, A, new Ordered(), HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            EndBetting(session);
            return session;
        }
        private static void EndBetting(HoldemSession session)
        {
            for (int step = 0; step < 8 && session.GetSnapshot(A).CurrentSeat.HasValue; step++)
            {
                var s = session.GetSnapshot(session.GetSnapshot(A).CurrentSeat.Value);
                Assert.That(session.Submit(s.ViewerSeat, HoldemCommand.Act(s.SessionId, s.HandId, Guid.NewGuid(),
                    s.ViewerSeat, s.SessionVersion, s.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
        }
        private static HoldemDealCommand Command(HoldemSession session, int card = 45, int index = 0,
            SeatId? speaker = null, Guid? id = null)
        {
            var s = session.GetSnapshot(A); var d = s.PendingDeal;
            return new HoldemDealCommand(s.SessionId, s.HandId, d.WindowId, id ?? Guid.NewGuid(),
                s.SessionVersion, d.Street, new HoldemCardChange(Guid.NewGuid(), Guid.NewGuid(),
                    speaker ?? B, index, Card.FromId(card), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
        }
        private static HoldemDealRecord Record(HoldemSession session)
        {
            var s = session.GetSnapshot(A);
            return session.FindDealRecord(s.HandId, s.Accusations.WindowId, s.Street);
        }
        private static HoldemAccusationClaim Accuse(HoldemSession session, SeatId target)
        {
            for (int i = 0; i < session.SeatCount; i++)
            {
                var s = session.GetSnapshot(session.GetSeatAt(i));
                Assert.That(session.SubmitAccusationChoice(s.ViewerSeat, new HoldemAccusationChoiceCommand(
                    s.SessionId, s.HandId, s.Accusations.WindowId, Guid.NewGuid(), s.SessionVersion,
                    s.Street, s.ViewerSeat, s.ViewerSeat == A ? target : (SeatId?)null)).Accepted, Is.True);
            }
            var last = session.GetSnapshot(A);
            Assert.That(session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(last.SessionId,
                last.HandId, last.Accusations.WindowId, Guid.NewGuid(), last.SessionVersion, last.Street)).Accepted, Is.True);
            return session.GetPendingAccusations().Single();
        }

        [Test]
        public void ChangedCardOwnerFeedbackAndVerdictUseOneCommittedCausalRecord()
        {
            var s = Start(); var before = s.GetSnapshot(A); var command = Command(s);
            Assert.That(s.ReadCurrentRevealCardChange(B), Is.Null);
            Assert.That(s.ApplyDealerDeal(command).Accepted, Is.True);
            var after = s.GetSnapshot(A); var record = Record(s);
            Assert.That(after.GetBoardCard(0), Is.EqualTo(Card.FromId(45)));
            Assert.That(record.Mutation.Before, Is.EqualTo(Card.FromId(9)));
            Assert.That(record.Mutation.After, Is.EqualTo(after.GetBoardCard(0)));
            Assert.That(record.Mutation.UtteranceId, Is.EqualTo(command.CardChange.UtteranceId));
            Assert.That(record.Mutation.UtteranceWindowId, Is.EqualTo(command.CardChange.UtteranceWindowId));
            Assert.That(record.Mutation.Speaker, Is.EqualTo(B));
            Assert.That(record.DealWindowId, Is.EqualTo(command.WindowId));
            Assert.That(record.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(record.Version, Is.EqualTo(after.SessionVersion));
            Assert.That(s.ReadCurrentRevealCardChange(B).Card, Is.EqualTo(record.Mutation.After));
            Assert.That(s.ReadCurrentRevealCardChange(A), Is.Null);
            Assert.That(s.ReadCurrentRevealCardChange(new SeatId(3)), Is.Null);
            Assert.That(before.BoardCount, Is.Zero);
            Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
            var claim = Accuse(s, B);
            var resolver = new HoldemAccusationResolver(s, new HoldemRecordedDealerEvidence(s, HoldemAccusationEvidenceScope.CurrentRevealOnly));
            Assert.That(resolver.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.Recorded));
            Assert.That(s.GetAccusationDecisions().Single().WasManipulated, Is.True);
            Assert.That(resolver.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(s.GetSnapshot(A).Result, Is.Null, "Undecided consequences are not invented.");
            Assert.That(s.GetSnapshot(A).PotAmount, Is.EqualTo(before.PotAmount));
        }

        [TestCase(false)] [TestCase(true)]
        public void NaturalMatchOrUnchangedDealIsCompleteNegativeEvidence(bool requestSameCard)
        {
            var s = Start(); var c = Command(s, card: 9);
            if (!requestSameCard) c = new HoldemDealCommand(c.SessionId, c.HandId, c.WindowId,
                c.CommandId, c.ExpectedVersion, c.Street);
            Assert.That(s.ApplyDealerDeal(c).Accepted, Is.True);
            Assert.That(Record(s).Mutation, Is.Null);
            Assert.That(s.ReadCurrentRevealCardChange(B), Is.Null);
            var claim = Accuse(s, B);
            Assert.That(new HoldemRecordedDealerEvidence(s, HoldemAccusationEvidenceScope.CurrentRevealOnly).FindEvidence(claim).WasManipulated, Is.False);
        }

        [Test]
        public void AnotherPlayersChangeDoesNotMakeTheAccusedGuilty()
        {
            var s = Start(); Assert.That(s.ApplyDealerDeal(Command(s)).Accepted, Is.True);
            var claim = Accuse(s, new SeatId(3));
            Assert.That(new HoldemRecordedDealerEvidence(s, HoldemAccusationEvidenceScope.CurrentRevealOnly).FindEvidence(claim).WasManipulated, Is.False);
        }

        [TestCase(0)] [TestCase(7)] [TestCase(8)] [TestCase(10)] [TestCase(11)]
        [TestCase(12)] [TestCase(13)] [TestCase(14)] [TestCase(15)]
        public void HoleCardsCurrentBurnAndOtherFlopSlotsCannotBeReused(int unavailableCard)
        {
            var s = Start(); long version = s.Version; var c = Command(s, card: unavailableCard);
            Assert.That(s.ApplyDealerDeal(c).Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(s.Version, Is.EqualTo(version));
            Assert.That(s.GetSnapshot(A).BoardCount, Is.Zero);
            Assert.That(s.GetSnapshot(A).IsDealPending, Is.True);
            var neutral = new HoldemDealCommand(c.SessionId, c.HandId, c.WindowId, Guid.NewGuid(), version, c.Street);
            Assert.That(s.DealUnchanged(neutral).Accepted, Is.True);
            Assert.That(s.GetSnapshot(A).GetBoardCard(0), Is.EqualTo(Card.FromId(9)));
            Assert.That(Record(s).Mutation, Is.Null);
        }

        [TestCase(3)] [TestCase(4)]
        public void AFlopCommandCannotTargetTurnOrRiver(int index)
        {
            var s = Start(); long version = s.Version;
            Assert.That(s.ApplyDealerDeal(Command(s, index: index)).Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(s.Version, Is.EqualTo(version));
        }

        [Test]
        public void ReplayReturnsSameReceiptAndConflictingOrLateCommandsCannotReapply()
        {
            var s = Start(); var c = Command(s); var accepted = s.ApplyDealerDeal(c); var record = Record(s);
            Assert.That(s.ApplyDealerDeal(c), Is.SameAs(accepted));
            Assert.That(Record(s), Is.SameAs(record));
            var conflict = new HoldemDealCommand(c.SessionId, c.HandId, c.WindowId, c.CommandId,
                c.ExpectedVersion, c.Street, new HoldemCardChange(c.CardChange.UtteranceWindowId,
                    c.CardChange.UtteranceId, B, 0, Card.FromId(46), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(s.ApplyDealerDeal(conflict).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            var late = new HoldemDealCommand(c.SessionId, c.HandId, c.WindowId, Guid.NewGuid(), s.Version,
                c.Street, c.CardChange);
            Assert.That(s.ApplyDealerDeal(late).Accepted, Is.False);
            Assert.That(s.Version, Is.EqualTo(accepted.Version));
            Assert.That(s.DealUnchanged(c).Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void SwapPreservesAll52IdentitiesAndOldHandState(int count)
        {
            var s = Start(seats: count);
            var handField = typeof(HoldemSession).GetField("currentHand", BindingFlags.Instance | BindingFlags.NonPublic);
            var deckField = typeof(HoldemHand).GetField("deck", BindingFlags.Instance | BindingFlags.NonPublic);
            var cardsField = typeof(Deck).GetField("cards", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldHand = (HoldemHand)handField.GetValue(s);
            var oldDeck = deckField.GetValue(oldHand);
            var oldCards = ((Card[])cardsField.GetValue(oldDeck)).ToArray();
            Assert.That(s.ApplyDealerDeal(Command(s)).Accepted, Is.True);
            var nextHand = (HoldemHand)handField.GetValue(s);
            var nextCards = (Card[])cardsField.GetValue(deckField.GetValue(nextHand));
            Assert.That(nextCards.Select(c => c.Id).OrderBy(id => id), Is.EqualTo(Enumerable.Range(0, 52)));
            Assert.That((Card[])cardsField.GetValue(oldDeck), Is.EqualTo(oldCards));
            Assert.That(nextHand.RemainingCardCount, Is.EqualTo(52 - count * 2 - 4));
            for (int i = 1; i <= count; i++)
                for (int hole = 0; hole < 2; hole++)
                    Assert.That(nextHand.GetHoleCard(new SeatId(i), hole), Is.EqualTo(oldHand.GetHoleCard(new SeatId(i), hole)));
        }

        [Test]
        public void PastRevealCannotAuthorizeNextRevealAndFeedbackClearsAtItsBoundary()
        {
            var s = Start(accusations: false); var first = Command(s);
            Assert.That(s.ApplyDealerDeal(first).Accepted, Is.True);
            Assert.That(s.ReadCurrentRevealCardChange(B), Is.Not.Null);
            var view = s.GetSnapshot(A);
            Assert.That(s.ResumeAfterReveal(new HoldemRevealCommand(view.SessionId, view.HandId,
                Guid.NewGuid(), view.SessionVersion, view.Street)).Accepted, Is.True);
            Assert.That(s.ReadCurrentRevealCardChange(B), Is.Null);
            EndBetting(s);
            var next = s.GetSnapshot(A);
            var stale = new HoldemDealCommand(first.SessionId, first.HandId, first.WindowId,
                Guid.NewGuid(), next.SessionVersion, first.Street, first.CardChange);
            Assert.That(s.ApplyDealerDeal(stale).Error, Is.EqualTo(HoldemCommandError.WrongDealWindow));
            Assert.That(s.ApplyDealerDeal(Command(s, card: 46, index: 3)).Accepted, Is.True);
            Assert.That(s.GetSnapshot(A).GetBoardCard(0), Is.EqualTo(Card.FromId(45)));
            Assert.That(s.GetSnapshot(A).GetBoardCard(3), Is.EqualTo(Card.FromId(46)));
            Assert.That(s.ReadCurrentRevealCardChange(B).BoardIndex, Is.EqualTo(3));
        }

        [Test]
        public void LookupCannotBorrowEvidenceFromAnotherWindowOrHand()
        {
            var s = Start(); Assert.That(s.ApplyDealerDeal(Command(s)).Accepted, Is.True);
            var r = Record(s);
            Assert.That(s.FindDealRecord(Guid.NewGuid(), r.AccusationWindowId.Value, r.Street), Is.Null);
            Assert.That(s.FindDealRecord(r.HandId, Guid.NewGuid(), r.Street), Is.Null);
            Assert.That(s.FindDealRecord(r.HandId, r.AccusationWindowId.Value, HoldemStreet.Turn), Is.Null);
        }

        [Test]
        public void AutomaticRevealCannotAcceptChangeWithoutAnOwnerFeedbackWindow()
        {
            var s = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2,
                dealPolicy: HoldemDealPolicy.WaitForHost), A, B, new Ordered());
            Assert.That(s.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            EndBetting(s); long before = s.Version;
            Assert.That(s.ApplyDealerDeal(Command(s)).Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(s.Version, Is.EqualTo(before));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ReserveSwapLeavesLaterBoardUnchangedAndSettlementConservesChips(int count)
        {
            var s = Start(accusations: false, seats: count, stack: 2);
            Assert.That(s.ApplyDealerDeal(Command(s)).Accepted, Is.True);
            for (int i = 1; i <= count; i++) Assert.That(s.IsEliminatedAfterSettlement(new SeatId(i)), Is.False);
            for (int step = 0; step < 20 && s.GetSnapshot(A).Result == null; step++)
            {
                var v = s.GetSnapshot(A);
                if (v.IsRevealPending)
                    Assert.That(s.ResumeAfterReveal(new HoldemRevealCommand(v.SessionId, v.HandId,
                        Guid.NewGuid(), v.SessionVersion, v.Street)).Accepted, Is.True);
                else if (v.IsDealPending)
                    Assert.That(s.DealUnchanged(new HoldemDealCommand(v.SessionId, v.HandId, v.PendingDeal.WindowId,
                        Guid.NewGuid(), v.SessionVersion, v.PendingDeal.Street)).Accepted, Is.True);
                else EndBetting(s);
            }
            var end = s.GetSnapshot(A);
            Assert.That(end.Result, Is.Not.Null);
            Assert.That(end.GetBoardCard(3).Id, Is.EqualTo(count * 2 + 5));
            Assert.That(end.GetBoardCard(4).Id, Is.EqualTo(count * 2 + 7));
            Assert.That(Enumerable.Range(0, count).Sum(i => end.GetSeatAt(i).Stack), Is.EqualTo(count * 2));
            Assert.That(s.ReadCurrentRevealCardChange(B), Is.Null);
        }
    }
}
