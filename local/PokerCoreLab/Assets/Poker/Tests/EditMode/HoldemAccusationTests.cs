using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemAccusationTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2), C = new SeatId(3), D = new SeatId(4);
        private static readonly SeatId[] Seats = { A, B, C, D };
        private static HoldemConfig Config(long stack = 100) => new HoldemConfig(stack, 1, 2,
            HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemAccusationMode.CollectLatestChoiceUntilHostCloses);

        [TestCase(2, 100)]
        [TestCase(4, 100)]
        [TestCase(2, 1)]
        [TestCase(4, 1)]
        public void AllPassWindowsNeedHostCloseAndPreserveNormalShowdown(int count, long stack)
        {
            var session = Start(count, stack);
            int windows = 0, operations = 0;
            var windowIds = new HashSet<Guid>();
            while (Read(session).Result == null && operations++ < 100)
            {
                var view = Read(session);
                if (!view.IsRevealPending) { Act(session); continue; }
                windows++;
                Assert.That(windowIds.Add(view.Accusations.WindowId), Is.True);
                Assert.That(view.Accusations.EligibleCount, Is.EqualTo(count));
                Assert.That(Resume(session).Error, Is.EqualTo(HoldemCommandError.AccusationWindowNotClosed));
                Assert.That(Close(session).Error, Is.EqualTo(HoldemCommandError.AccusationResponsesPending));
                foreach (var seat in Seats.Take(count)) Choose(session, seat, null);
                Assert.That(Read(session).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.Collecting));
                Assert.That(Resume(session).Accepted, Is.False, "All pass is not an implicit host close.");
                long version = session.Version;
                Assert.That(Close(session).Accepted, Is.True);
                Assert.That(session.Version, Is.EqualTo(version + 1));
                Assert.That(Read(session).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.ClosedWithoutClaims));
                Assert.That(Resume(session).Accepted, Is.True);
            }
            Assert.That(windows, Is.EqualTo(3));
            Assert.That(Read(session).Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Seats.Take(count).Sum(s => session.GetSnapshot(s).OwnStack), Is.EqualTo(count * stack));
            Assert.That(Read(session).Accusations, Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ClaimsFreezeForHostVerdictAndNeverInventConsequences(bool verdict)
        {
            var session = AtFlop();
            var initial = Read(session);
            var accusation = Choose(session, A, B);
            Choose(session, B, null); Choose(session, C, null); Choose(session, D, null);
            Assert.That(session.GetPendingAccusations(), Is.Empty, "Choices must freeze before adjudication.");
            var beforeDecisions = session.GetAccusationDecisions();
            Assert.That(beforeDecisions, Is.Empty);
            var close = CloseCommand(Read(session));
            var closed = session.ProcessAccusationHostCommand(close);
            Assert.That(closed.Accepted, Is.True);
            Assert.That(session.ProcessAccusationHostCommand(close), Is.SameAs(closed));
            var claim = session.GetPendingAccusations().Single();
            Assert.That(claim.ClaimId, Is.EqualTo(accusation.CommandId));
            Assert.That(claim.Accuser, Is.EqualTo(A)); Assert.That(claim.Target, Is.EqualTo(B));
            Assert.That(claim.WindowId, Is.EqualTo(initial.Accusations.WindowId));
            Assert.That(Resume(session).Error, Is.EqualTo(HoldemCommandError.AccusationVerdictsPending));
            var v = Read(session);
            Assert.That(session.SubmitAccusationChoice(A, Choice(v, A, null)).Error,
                Is.EqualTo(HoldemCommandError.AccusationWindowClosed));
            var record = Verdict(v, claim.ClaimId, verdict);
            var receipt = session.ProcessAccusationHostCommand(record);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.ProcessAccusationHostCommand(record), Is.SameAs(receipt));
            var decision = session.GetAccusationDecisions().Single();
            Assert.That(decision.Claim.ClaimId, Is.EqualTo(claim.ClaimId));
            Assert.That(decision.Claim.Accuser, Is.EqualTo(A)); Assert.That(decision.Claim.Target, Is.EqualTo(B));
            Assert.That(decision.WasManipulated, Is.EqualTo(verdict));
            Assert.That(beforeDecisions, Is.Empty, "Previously returned host records must stay immutable.");
            Assert.That(Read(session).Accusations.OwnVerdict, Is.EqualTo(verdict));
            Assert.That(v.Accusations.OwnVerdict, Is.Null, "Earlier snapshots stay immutable.");
            foreach (var other in new[] { B, C, D })
                Assert.That(session.GetSnapshot(other).Accusations.OwnVerdict, Is.Null,
                    "A verdict belongs to the accuser, not the target or observers.");
            Assert.That(typeof(HoldemAccusationView).GetProperty("Decisions"), Is.Null);
            Assert.That(typeof(HoldemSnapshot).GetProperty("AccusationDecisions"), Is.Null);
            Assert.That(Read(session).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(Resume(session).Error, Is.EqualTo(HoldemCommandError.AccusationConsequencesPending));
            Assert.That(session.ProcessAccusationHostCommand(Verdict(Read(session), claim.ClaimId, !verdict)).Error,
                Is.EqualTo(HoldemCommandError.AccusationVerdictRecorded));
            AssertUnchangedPoker(initial, Read(session));
            Assert.That(session.GetPendingAccusations(), Is.Empty);
            Assert.That(Read(session).CurrentSeat, Is.Null); Assert.That(Read(session).LegalActions, Is.Null);
        }

        [Test]
        public void OpenChoiceCanBeRevisedWithoutLeakingTargetsOrMakingFirstArrivalWin()
        {
            var session = AtFlop();
            var old = Choose(session, A, B);
            var oldReceipt = session.SubmitAccusationChoice(A, old);
            Choose(session, B, A); Choose(session, A, null);
            var final = Choose(session, A, C);
            Choose(session, C, D); Choose(session, D, null);
            var v = Read(session);
            Assert.That(v.Accusations.ResponseCount, Is.EqualTo(4));
            Assert.That(v.Accusations.OwnTarget, Is.EqualTo(C));
            Assert.That(session.GetSnapshot(D).Accusations.OwnTarget, Is.Null);
            Assert.That(session.GetSnapshot(D).Accusations.HasResponded, Is.True);
            Assert.That(session.SubmitAccusationChoice(A, old), Is.SameAs(oldReceipt));
            Assert.That(Read(session).Accusations.OwnTarget, Is.EqualTo(C));
            Assert.That(Read(session).SessionVersion, Is.EqualTo(v.SessionVersion));
            Close(session);
            var claims = session.GetPendingAccusations();
            Assert.That(claims.Count, Is.EqualTo(3));
            Assert.That(claims.Single(c => c.Accuser == A).ClaimId, Is.EqualTo(final.CommandId));
            Assert.That(session.ProcessAccusationHostCommand(Verdict(Read(session), old.CommandId, true)).Error,
                Is.EqualTo(HoldemCommandError.UnknownAccusation));
            int recorded = 0;
            foreach (var claim in claims.Reverse())
            {
                bool verdict = claim.Accuser != B;
                var earlier = session.GetAccusationDecisions();
                Assert.That(session.ProcessAccusationHostCommand(Verdict(Read(session), claim.ClaimId, verdict)).Accepted, Is.True);
                Assert.That(earlier.Count, Is.EqualTo(recorded));
                recorded++;
                Assert.That(session.GetAccusationDecisions().Count, Is.EqualTo(recorded));
                Assert.That(session.GetPendingAccusations().Count, Is.EqualTo(3 - recorded));
                Assert.That(session.GetAccusationDecisions().Single(d => d.Claim.ClaimId == claim.ClaimId).WasManipulated,
                    Is.EqualTo(verdict));
            }
            Assert.That(Read(session).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(Read(session).Result, Is.Null, "No first-claim or last-claim winner is inferred.");
            Assert.That(Read(session).PotAmount, Is.EqualTo(8));
            Assert.That(typeof(HoldemAccusationView).GetProperty("Claims"), Is.Null);
        }

        [Test]
        public void HostVerdictRejectsWrongEnvelopeStaleVersionAndUnfrozenClaims()
        {
            var session = AtFlop(); var choice = Choose(session, A, B);
            Assert.That(session.ProcessAccusationHostCommand(Verdict(Read(session), choice.CommandId, true)).Error,
                Is.EqualTo(HoldemCommandError.AccusationWindowNotClosed));
            foreach (var seat in new[] { B, C, D }) Choose(session, seat, null);
            var beforeClose = Read(session);
            Assert.That(Close(session).Accepted, Is.True);
            var v = Read(session);
            var bad = new[] {
                HoldemAccusationHostCommand.Verdict(Guid.NewGuid(), v.HandId, v.Accusations.WindowId,
                    Guid.NewGuid(), v.SessionVersion, v.Street, choice.CommandId, true),
                HoldemAccusationHostCommand.Verdict(v.SessionId, Guid.NewGuid(), v.Accusations.WindowId,
                    Guid.NewGuid(), v.SessionVersion, v.Street, choice.CommandId, true),
                HoldemAccusationHostCommand.Verdict(v.SessionId, v.HandId, Guid.NewGuid(),
                    Guid.NewGuid(), v.SessionVersion, v.Street, choice.CommandId, true),
                HoldemAccusationHostCommand.Verdict(v.SessionId, v.HandId, v.Accusations.WindowId,
                    Guid.NewGuid(), v.SessionVersion, HoldemStreet.Turn, choice.CommandId, true),
                Verdict(beforeClose, choice.CommandId, true), Verdict(v, Guid.NewGuid(), true)
            };
            var errors = new[] { HoldemCommandError.WrongSession, HoldemCommandError.WrongHand,
                HoldemCommandError.WrongRevealWindow, HoldemCommandError.WrongRevealWindow,
                HoldemCommandError.VersionMismatch, HoldemCommandError.UnknownAccusation };
            for (int i = 0; i < bad.Length; i++)
                Assert.That(session.ProcessAccusationHostCommand(bad[i]).Error, Is.EqualTo(errors[i]));
            Assert.That(session.GetAccusationDecisions(), Is.Empty);
            Assert.That(session.GetPendingAccusations().Count, Is.EqualTo(1));
            Assert.That(session.Version, Is.EqualTo(v.SessionVersion));
            AssertUnchangedPoker(v, Read(session));
        }

        [Test]
        public void MissingIntakeStateFailsClosedInsteadOfSkippingAccusations()
        {
            var session = AtFlop(); var before = Read(session);
            var field = typeof(HoldemSession).GetField("accusations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(session, null); // Fault injection: not reachable through valid commands.
            Assert.That(Resume(session).Error, Is.EqualTo(HoldemCommandError.AccusationWindowNotClosed));
            Assert.That(session.Version, Is.EqualTo(before.SessionVersion));
            AssertUnchangedPoker(before, Read(session));
        }

        [Test]
        public void ForgedStaleAndCrossWindowRequestsNeverMutateIntakeOrChips()
        {
            var session = AtFlop(); var v = Read(session);
            var command = Choice(v, A, B);
            Assert.That(session.SubmitAccusationChoice(B, command).Error, Is.EqualTo(HoldemCommandError.UnauthorizedSeat));
            var bad = new[] {
                new HoldemAccusationChoiceCommand(Guid.NewGuid(), v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, A, B),
                new HoldemAccusationChoiceCommand(v.SessionId, Guid.NewGuid(), v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, A, B),
                new HoldemAccusationChoiceCommand(v.SessionId, v.HandId, Guid.NewGuid(), Guid.NewGuid(), v.SessionVersion, v.Street, A, B),
                new HoldemAccusationChoiceCommand(v.SessionId, v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, HoldemStreet.Turn, A, B),
                Choice(v, A, B, version: v.SessionVersion - 1), Choice(v, A, A), Choice(v, A, new SeatId(99))
            };
            var expected = new[] { HoldemCommandError.WrongSession, HoldemCommandError.WrongHand,
                HoldemCommandError.WrongRevealWindow, HoldemCommandError.WrongRevealWindow,
                HoldemCommandError.VersionMismatch, HoldemCommandError.InvalidAccusationTarget, HoldemCommandError.InvalidAccusationTarget };
            for (int i = 0; i < bad.Length; i++) Assert.That(session.SubmitAccusationChoice(A, bad[i]).Error, Is.EqualTo(expected[i]));
            Assert.That(Read(session).SessionVersion, Is.EqualTo(v.SessionVersion));
            Assert.That(Read(session).Accusations.ResponseCount, Is.Zero); AssertUnchangedPoker(v, Read(session));
            var accepted = session.SubmitAccusationChoice(A, command);
            Assert.That(accepted.Accepted, Is.True);
            Assert.That(session.SubmitAccusationChoice(A, command), Is.SameAs(accepted));
            Assert.That(session.SubmitAccusationChoice(A, Choice(v, A, C, id: command.CommandId)).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            var current = Read(session);
            Assert.That(session.Submit(A, HoldemCommand.Act(v.SessionId, v.HandId, command.CommandId, A,
                current.SessionVersion, BettingAction.Check())).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(v.SessionId, v.HandId,
                v.Accusations.WindowId, command.CommandId, current.SessionVersion, v.Street)).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.StartNextHand(Guid.NewGuid(), command.CommandId, current.SessionVersion).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
        }

        [Test]
        public void FoldedAndBustedSeatsCannotParticipateButAllInSeatsCan()
        {
            var ledger = ChipLedger.Create(new[] { new SeatChips(A, 0), new SeatChips(B, 1), new SeatChips(C, 100), new SeatChips(D, 100) });
            var session = new HoldemSession(Guid.NewGuid(), Config(), ledger, Seats, B, new SeededRandom(13));
            session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0);
            while (!Read(session).IsRevealPending) Act(session);
            Assert.That(session.GetSnapshot(A).Accusations.CanRespond, Is.False);
            Assert.That(session.GetSnapshot(B).Accusations.CanRespond, Is.True);
            Assert.That(session.GetSnapshot(B).GetSeat(B).Status, Is.EqualTo(HoldemSeatStatus.AllIn));
            Assert.That(session.SubmitAccusationChoice(A, Choice(Read(session), A, C)).Error, Is.EqualTo(HoldemCommandError.UnauthorizedSeat));
            Assert.That(session.SubmitAccusationChoice(C, Choice(Read(session), C, A)).Error, Is.EqualTo(HoldemCommandError.InvalidAccusationTarget));

            session = Start(4); Act(session, BettingAction.Fold());
            while (!Read(session).IsRevealPending) Act(session);
            Assert.That(session.GetSnapshot(D).Accusations.CanRespond, Is.False);
            Assert.That(session.GetSnapshot(D).Accusations.TargetCount, Is.Zero);
            Assert.That(session.SubmitAccusationChoice(A, Choice(Read(session), A, D)).Error, Is.EqualTo(HoldemCommandError.InvalidAccusationTarget));
            Assert.That(Read(session).Accusations.EligibleCount, Is.EqualTo(3));
        }

        [Test]
        public void FreshTurnRejectsAnOldWindowButAcceptedReplayStaysIdempotent()
        {
            var session = AtFlop(); var first = Read(session);
            var firstChoice = Choose(session, A, null);
            var firstReceipt = session.SubmitAccusationChoice(A, firstChoice);
            foreach (var seat in new[] { B, C, D }) Choose(session, seat, null);
            Close(session); Resume(session);
            while (!Read(session).IsRevealPending) Act(session);
            var turn = Read(session);
            Assert.That(turn.Street, Is.EqualTo(HoldemStreet.Turn));
            var staleWindow = new HoldemAccusationChoiceCommand(turn.SessionId, turn.HandId, first.Accusations.WindowId,
                Guid.NewGuid(), turn.SessionVersion, turn.Street, A, B);
            Assert.That(session.SubmitAccusationChoice(A, staleWindow).Error, Is.EqualTo(HoldemCommandError.WrongRevealWindow));
            Assert.That(session.SubmitAccusationChoice(A, firstChoice), Is.SameAs(firstReceipt));
            Assert.That(Read(session).Accusations.ResponseCount, Is.Zero);
            Assert.That(session.Version, Is.EqualTo(turn.SessionVersion));
        }

        [Test]
        public void LocalHostCannotResumeAroundPendingClaimAndPlayerPortHasNoHostMethods()
        {
            var table = new HoldemLocalTable(Config(), new SeededRandom(13), new SeededRandom(7), new PassivePolicy());
            var v = table.Human.Read();
            table.Human.Submit(HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), A, v.SessionVersion, BettingAction.Call()));
            table.AdvanceNpc(); v = table.Human.Read();
            var port = (IHoldemAccusationPlayerPort)table.Human;
            Assert.That(port.SubmitAccusationChoice(Choice(v, B, A)).Error, Is.EqualTo(HoldemCommandError.UnauthorizedSeat));
            Assert.That(port.SubmitAccusationChoice(Choice(v, A, B)).Accepted, Is.True);
            Assert.That(table.AdvanceAccusationResponses(), Is.True);
            Assert.That(table.AdvanceAccusationResponses(), Is.True);
            Assert.That(table.AdvanceAccusationResponses(), Is.False);
            v = table.Human.Read();
            Assert.That(table.ResumeAfterReveal(new HoldemRevealCommand(v.SessionId, v.HandId, Guid.NewGuid(), v.SessionVersion, v.Street)).Error,
                Is.EqualTo(HoldemCommandError.AccusationVerdictsPending));
            Assert.That(table.AdvanceNpc(), Is.False);
            Assert.That(typeof(IHoldemAccusationPlayerPort).GetMethods().Select(m => m.Name), Is.EquivalentTo(new[] { "SubmitAccusationChoice" }));
        }

        [Test]
        public void ConfigurationAndCommandValidationRejectInvalidCombinations()
        {
            Assert.Throws<ArgumentException>(() => new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.Automatic, HoldemAccusationMode.CollectLatestChoiceUntilHostCloses));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal, (HoldemAccusationMode)99));
            Assert.Throws<ArgumentException>(() => new HoldemAccusationChoiceCommand(Guid.NewGuid(), Guid.NewGuid(),
                Guid.Empty, Guid.NewGuid(), 1, HoldemStreet.Flop, A, B));
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2), A, B, new SeededRandom(1));
            session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0); var v = Read(session);
            Assert.That(session.SubmitAccusationChoice(A, new HoldemAccusationChoiceCommand(v.SessionId, v.HandId,
                Guid.NewGuid(), Guid.NewGuid(), v.SessionVersion, HoldemStreet.Flop, A, B)).Error, Is.EqualTo(HoldemCommandError.AccusationNotEnabled));
        }

        [Test]
        public void ExactStackTransferPreservesPotAndRejectsShortfallAtomically()
        {
            var initial = ChipLedger.Create(new[] { new SeatChips(A, 10), new SeatChips(B, 20) });
            var ledger = initial.Contribute(A, 4).Contribute(B, 4);
            var transfer = ledger.TransferStack(A, B, 5);
            Assert.That(transfer.GetChips(A).Stack, Is.EqualTo(1)); Assert.That(transfer.GetChips(B).Stack, Is.EqualTo(21));
            Assert.That(transfer.TotalCommitted, Is.EqualTo(8)); Assert.That(transfer.TotalChips, Is.EqualTo(30));
            Assert.That(transfer.GetChips(A).Committed, Is.EqualTo(4)); Assert.That(transfer.GetChips(B).Committed, Is.EqualTo(4));
            Assert.That(ledger.GetChips(A).Stack, Is.EqualTo(6)); Assert.That(ledger.GetChips(B).Stack, Is.EqualTo(16));
            Assert.Throws<InvalidOperationException>(() => ledger.TransferStack(A, B, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TransferStack(A, B, 0));
            Assert.Throws<ArgumentException>(() => ledger.TransferStack(A, A, 1));
            Assert.Throws<KeyNotFoundException>(() => ledger.TransferStack(A, C, 1));
            Assert.That(ledger.TransferStack(A, B, 6).GetChips(A).Stack, Is.Zero, "Zero is bookkeeping, not a game-loss verdict.");
        }

        private static HoldemSession Start(int count = 4, long stack = 100)
        {
            var s = new HoldemSession(Guid.NewGuid(), Config(stack), Seats.Take(count).ToArray(), A,
                new SeededRandom(18), HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(s.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True); return s;
        }
        private static HoldemSession AtFlop()
        { var s = Start(); while (!Read(s).IsRevealPending) Act(s); return s; }
        private static HoldemSnapshot Read(HoldemSession session) => session.GetSnapshot(A);
        private static void Act(HoldemSession session, BettingAction action = null)
        {
            var v = Read(session); var actor = session.GetSnapshot(v.CurrentSeat.Value);
            action = action ?? (actor.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
            Assert.That(session.Submit(actor.ViewerSeat, HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), actor.ViewerSeat,
                v.SessionVersion, action)).Accepted, Is.True);
        }
        private static HoldemAccusationChoiceCommand Choice(HoldemSnapshot v, SeatId seat, SeatId? target, Guid? id = null, long? version = null)
            => new HoldemAccusationChoiceCommand(v.SessionId, v.HandId, v.Accusations.WindowId, id ?? Guid.NewGuid(),
                version ?? v.SessionVersion, v.Street, seat, target);
        private static HoldemAccusationChoiceCommand Choose(HoldemSession session, SeatId seat, SeatId? target)
        { var c = Choice(Read(session), seat, target); Assert.That(session.SubmitAccusationChoice(seat, c).Accepted, Is.True); return c; }
        private static HoldemAccusationHostCommand CloseCommand(HoldemSnapshot v) => HoldemAccusationHostCommand.Close(
            v.SessionId, v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street);
        private static HoldemReceipt Close(HoldemSession session) => session.ProcessAccusationHostCommand(CloseCommand(Read(session)));
        private static HoldemReceipt Resume(HoldemSession session)
        { var v = Read(session); return session.ResumeAfterReveal(new HoldemRevealCommand(v.SessionId, v.HandId, Guid.NewGuid(), v.SessionVersion, v.Street)); }
        private static HoldemAccusationHostCommand Verdict(HoldemSnapshot v, Guid claimId, bool value) => HoldemAccusationHostCommand.Verdict(
            v.SessionId, v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, claimId, value);
        private static void AssertUnchangedPoker(HoldemSnapshot before, HoldemSnapshot after)
        {
            Assert.That(after.HandVersion, Is.EqualTo(before.HandVersion));
            Assert.That(after.BoardCount, Is.EqualTo(before.BoardCount)); Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(after.Result, Is.Null); Assert.That(after.IsOver, Is.False);
            for (int i = 0; i < before.BoardCount; i++) Assert.That(after.GetBoardCard(i), Is.EqualTo(before.GetBoardCard(i)));
            for (int i = 0; i < before.SeatCount; i++)
            {
                var a = before.GetSeatAt(i); var b = after.GetSeatAt(i);
                Assert.That(b.Stack, Is.EqualTo(a.Stack)); Assert.That(b.Committed, Is.EqualTo(a.Committed));
                Assert.That(b.Status, Is.EqualTo(a.Status)); Assert.That(b.VisibleHoleCardCount, Is.EqualTo(a.VisibleHoleCardCount));
            }
        }
        private sealed class SeededRandom : IRandomSource
        { private readonly Random random; public SeededRandom(int seed) { random = new Random(seed); } public int NextInt(int maximum) => random.Next(maximum); }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot v, IRandomSource random) => v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
    }
}
