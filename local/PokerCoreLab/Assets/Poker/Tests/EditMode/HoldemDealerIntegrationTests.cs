using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemDealerIntegrationTests
    {
        [TestCase(HoldemStreet.Flop, true)] [TestCase(HoldemStreet.Flop, false)]
        [TestCase(HoldemStreet.Turn, true)] [TestCase(HoldemStreet.Turn, false)]
        [TestCase(HoldemStreet.River, true)] [TestCase(HoldemStreet.River, false)]
        public void PlayerClaimReachesDealerAdapterAndWaitsForConsequencesWithoutChangingPoker(HoldemStreet street, bool manipulated)
        {
            var table = Make(4); Reach(table, street);
            var before = table.Human.Read(); var history = ((IHoldemHistoryPort)table.Human).ReadHistory();
            Choose(table, new SeatId(4)); Close(table);
            var waiting = table.Human.Read();
            Assert.That(waiting.Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingVerdicts));
            Assert.That(Resume(table).Error, Is.EqualTo(HoldemCommandError.AccusationVerdictsPending));
            var claim = table.GetPendingAccusations().Single();
            var source = new RecordedDealer();
            Assert.That(table.ResolveAccusation(claim.ClaimId, source), Is.EqualTo(HoldemEvidenceResolution.EvidencePending));
            source.Unavailable = true;
            Assert.That(table.ResolveAccusation(claim.ClaimId, source), Is.EqualTo(HoldemEvidenceResolution.EvidenceUnavailable));
            source.Unavailable = false;
            source.Record = Record(claim, manipulated, Guid.NewGuid());
            Assert.That(table.ResolveAccusation(claim.ClaimId, source), Is.EqualTo(HoldemEvidenceResolution.EvidenceMismatch));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(waiting.SessionVersion));
            Assert.That(table.GetAccusationDecisions(), Is.Empty);

            source.Record = Record(claim, manipulated, before.HandId);
            Assert.That(table.ResolveAccusation(claim.ClaimId, source), Is.EqualTo(HoldemEvidenceResolution.Recorded));
            foreach (var request in source.Requests)
            {
                Assert.That(request.SessionId, Is.EqualTo(before.SessionId)); Assert.That(request.HandId, Is.EqualTo(before.HandId));
                Assert.That(request.WindowId, Is.EqualTo(before.Accusations.WindowId)); Assert.That(request.Street, Is.EqualTo(street));
                Assert.That(request.ClaimId, Is.EqualTo(claim.ClaimId)); Assert.That(request.Accuser, Is.EqualTo(before.ViewerSeat));
                Assert.That(request.Target, Is.EqualTo(new SeatId(4)));
            }
            var after = table.Human.Read();
            Assert.That(after.Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(table.GetAccusationDecisions().Single().WasManipulated, Is.EqualTo(manipulated));
            Assert.That(after.SessionVersion, Is.EqualTo(waiting.SessionVersion + 1));
            AssertUnchangedPoker(before, after);
            Assert.That(((IHoldemHistoryPort)table.Human).ReadHistory(), Is.SameAs(history), "Private intake and evidence must not enter the public betting history.");
            int calls = source.Requests.Count;
            Assert.That(table.ResolveAccusation(claim.ClaimId, source), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(source.Requests.Count, Is.EqualTo(calls));
            Assert.That(Resume(table).Error, Is.EqualTo(HoldemCommandError.AccusationConsequencesPending));
            Assert.That(table.AdvanceNpc(), Is.False);
            Assert.That(table.Human.NextHand(after.SessionVersion).Accepted, Is.False);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(after.SessionVersion));
        }

        [TestCase(2)] [TestCase(4)]
        public void AllPassWindowsResumeBettingAndCompleteWithoutCallingDealer(int seats)
        {
            var table = Make(seats); var source = new RecordedDealer();
            var windows = new HashSet<Guid>();
            foreach (var street in new[] { HoldemStreet.Flop, HoldemStreet.Turn, HoldemStreet.River })
            {
                Reach(table, street); var before = table.Human.Read();
                Assert.That(windows.Add(before.Accusations.WindowId), Is.True);
                Choose(table, null); Close(table);
                Assert.That(table.Human.Read().Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.ClosedWithoutClaims));
                Assert.That(table.GetPendingAccusations(), Is.Empty);
                Assert.That(table.ResolveAccusation(Guid.NewGuid(), source), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
                Assert.That(Resume(table).Accepted, Is.True);
                var after = table.Human.Read();
                Assert.That(after.IsRevealPending, Is.False); Assert.That(after.CurrentSeat.HasValue, Is.True);
                Assert.That(after.Street, Is.EqualTo(street)); Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
            }
            int actions = 0;
            while (table.Human.Read().Result == null && actions++ < 12) Act(table);
            Assert.That(table.Human.Read().Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(source.Requests, Is.Empty);
        }

        private static HoldemDealerEvidence Record(HoldemAccusationClaim c, bool manipulated, Guid handId)
            => new HoldemDealerEvidence(c.SessionId, handId, c.WindowId, c.Street, c.ClaimId, c.Accuser, c.Target, manipulated);

        [Test]
        public void TwoClaimsRejectCrossedRecordsAndResolveOutOfOrderWithoutCrossApplyingVerdicts()
        {
            var seats = new[] { new SeatId(1), new SeatId(2), new SeatId(3), new SeatId(4) };
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemAccusationMode.CollectLatestChoiceUntilHostCloses),
                seats, seats[0], new FixedRandom(), HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            for (int i = 0; !session.GetSnapshot(seats[0]).IsRevealPending && i < 8; i++)
            {
                var v = session.GetSnapshot(seats[0]); var actor = session.GetSnapshot(v.CurrentSeat.Value);
                Assert.That(session.Submit(actor.ViewerSeat, HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), actor.ViewerSeat,
                    v.SessionVersion, actor.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            Assert.That(session.GetSnapshot(seats[0]).IsRevealPending, Is.True);
            for (int i = 0; i < seats.Length; i++)
            {
                var v = session.GetSnapshot(seats[i]);
                SeatId? target = i == 0 ? seats[1] : i == 2 ? seats[3] : (SeatId?)null;
                Assert.That(session.SubmitAccusationChoice(seats[i], new HoldemAccusationChoiceCommand(v.SessionId, v.HandId,
                    v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, seats[i], target)).Accepted, Is.True);
            }
            var before = session.GetSnapshot(seats[0]);
            Assert.That(session.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(before.SessionId, before.HandId,
                before.Accusations.WindowId, Guid.NewGuid(), before.SessionVersion, before.Street)).Accepted, Is.True);
            var claims = session.GetPendingAccusations().OrderBy(c => c.Accuser.Value).ToArray();
            Assert.That(claims.Length, Is.EqualTo(2));
            var source = new RecordedDealer { Record = Record(claims[0], true, before.HandId) };
            var resolver = new HoldemAccusationResolver(session, source); long version = session.Version;
            Assert.That(resolver.Resolve(claims[1].ClaimId), Is.EqualTo(HoldemEvidenceResolution.EvidenceMismatch));
            Assert.That(session.Version, Is.EqualTo(version)); Assert.That(session.GetAccusationDecisions(), Is.Empty);
            Assert.That(session.GetPendingAccusations().Count, Is.EqualTo(2));
            source.Record = Record(claims[1], false, before.HandId);
            Assert.That(resolver.Resolve(claims[1].ClaimId), Is.EqualTo(HoldemEvidenceResolution.Recorded));
            Assert.That(session.GetSnapshot(seats[0]).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingVerdicts));
            Assert.That(session.GetPendingAccusations().Single().ClaimId, Is.EqualTo(claims[0].ClaimId));
            source.Record = Record(claims[0], true, before.HandId);
            Assert.That(resolver.Resolve(claims[0].ClaimId), Is.EqualTo(HoldemEvidenceResolution.Recorded));
            var decisions = session.GetAccusationDecisions();
            Assert.That(decisions.Single(d => d.Claim.ClaimId == claims[0].ClaimId).WasManipulated, Is.True);
            Assert.That(decisions.Single(d => d.Claim.ClaimId == claims[1].ClaimId).WasManipulated, Is.False);
            Assert.That(session.GetSnapshot(seats[0]).Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            AssertUnchangedPoker(before, session.GetSnapshot(seats[0]));
            int calls = source.Requests.Count;
            Assert.That(resolver.Resolve(claims[0].ClaimId), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(resolver.Resolve(claims[1].ClaimId), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(source.Requests.Count, Is.EqualTo(calls));
        }
        private static void AssertUnchangedPoker(HoldemSnapshot before, HoldemSnapshot after)
        {
            Assert.That(after.HandVersion, Is.EqualTo(before.HandVersion)); Assert.That(after.Result, Is.Null);
            Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount)); Assert.That(after.BoardCount, Is.EqualTo(before.BoardCount));
            for (int i = 0; i < before.BoardCount; i++) Assert.That(after.GetBoardCard(i), Is.EqualTo(before.GetBoardCard(i)));
            for (int i = 0; i < before.SeatCount; i++)
            {
                var a = before.GetSeatAt(i); var b = after.GetSeatAt(i);
                Assert.That(b.Stack, Is.EqualTo(a.Stack)); Assert.That(b.Committed, Is.EqualTo(a.Committed));
                Assert.That(b.Status, Is.EqualTo(a.Status)); Assert.That(b.Awarded, Is.Zero);
                Assert.That(b.VisibleHoleCardCount, Is.EqualTo(b.IsViewer ? 2 : 0));
            }
        }
        private static HoldemLocalTable Make(int seats) => new HoldemLocalTable(new HoldemConfig(100, 1, 2,
            HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemAccusationMode.CollectLatestChoiceUntilHostCloses),
            seats, new FixedRandom(), new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
        private static void Reach(HoldemLocalTable table, HoldemStreet target)
        {
            for (int i = 0; i < 60; i++)
            {
                var v = table.Human.Read();
                if (v.IsRevealPending)
                {
                    if (v.Street == target) return;
                    Choose(table, null); Close(table); Assert.That(Resume(table).Accepted, Is.True);
                }
                else Act(table);
            }
            Assert.Fail("Did not reach " + target);
        }
        private static void Act(HoldemLocalTable table)
        {
            var v = table.Human.Read();
            if (v.LegalActions == null) { Assert.That(table.AdvanceNpc(), Is.True); return; }
            Assert.That(table.Human.Submit(HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), v.ViewerSeat,
                v.SessionVersion, v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
        }
        private static void Choose(HoldemLocalTable table, SeatId? target)
        {
            var v = table.Human.Read();
            Assert.That(((IHoldemAccusationPlayerPort)table.Human).SubmitAccusationChoice(new HoldemAccusationChoiceCommand(
                v.SessionId, v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, v.ViewerSeat, target)).Accepted, Is.True);
        }
        private static void Close(HoldemLocalTable table)
        {
            int count = 0;
            while (count++ < 5 && table.AdvanceAccusationResponses()) { }
            Assert.That(table.Human.Read().Accusations.Phase, Is.Not.EqualTo(HoldemAccusationPhase.Collecting));
        }
        private static HoldemReceipt Resume(HoldemLocalTable table)
        {
            var v = table.Human.Read();
            return table.ResumeAfterReveal(new HoldemRevealCommand(v.SessionId, v.HandId, Guid.NewGuid(), v.SessionVersion, v.Street));
        }
        private sealed class RecordedDealer : IHoldemDealerEvidenceSource
        {
            public HoldemDealerEvidence Record;
            public bool Unavailable;
            public readonly List<HoldemAccusationClaim> Requests = new List<HoldemAccusationClaim>();
            public HoldemDealerEvidence FindEvidence(HoldemAccusationClaim claim)
            { Requests.Add(claim); if (Unavailable) throw new InvalidOperationException("No record available."); return Record; }
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot v, IRandomSource random) => v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
    }
}
