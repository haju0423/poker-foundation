using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemDealerEvidenceTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [TestCase(true)]
        [TestCase(false)]
        public void MatchingEvidenceRecordsOnceWithoutRevealingOrSettling(bool value)
        {
            var s = Pending(); var claim = s.GetPendingAccusations().Single();
            var before = s.GetSnapshot(A); int calls = 0;
            var resolver = new HoldemAccusationResolver(s, new Source(c => { calls++; return HoldemDealerEvidence.ForClaim(c, value); }));
            Assert.That(resolver.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.Recorded));
            Assert.That(resolver.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(calls, Is.EqualTo(1)); Assert.That(s.Version, Is.EqualTo(before.SessionVersion + 1));
            var after = s.GetSnapshot(A);
            Assert.That(after.Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(s.GetAccusationDecisions().Single().WasManipulated, Is.EqualTo(value));
            Assert.That(after.Result, Is.Null); Assert.That(after.OpponentCardCount, Is.Zero);
            Assert.That(after.OwnStack, Is.EqualTo(before.OwnStack)); Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
        }

        [Test]
        public void MissingOrFailedEvidenceLeavesTheClaimPendingAndCanBeRetried()
        {
            var s = Pending(); var claim = s.GetPendingAccusations().Single(); long before = s.Version;
            var missing = new HoldemAccusationResolver(s, new Source(_ => null));
            Assert.That(missing.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.EvidencePending));
            var failed = new HoldemAccusationResolver(s, new Source(_ => throw new InvalidOperationException("offline")));
            Assert.That(failed.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.EvidenceUnavailable));
            Assert.That(s.Version, Is.EqualTo(before)); Assert.That(s.GetAccusationDecisions(), Is.Empty);
            Assert.That(s.GetPendingAccusations().Count, Is.EqualTo(1));
            Assert.That(new HoldemAccusationResolver(s, new Source(c => HoldemDealerEvidence.ForClaim(c, false)))
                .Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.Recorded));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void AnyMismatchedIdentityRejectsEvidenceWithoutMutation(int field)
        {
            var s = Pending(); var c = s.GetPendingAccusations().Single(); long before = s.Version;
            var bad = new HoldemDealerEvidence(field == 0 ? Guid.NewGuid() : c.SessionId,
                field == 1 ? Guid.NewGuid() : c.HandId, field == 2 ? Guid.NewGuid() : c.WindowId,
                field == 3 ? HoldemStreet.Turn : c.Street, field == 4 ? Guid.NewGuid() : c.ClaimId,
                field == 5 ? new SeatId(3) : c.Accuser, field == 6 ? new SeatId(4) : c.Target, true);
            Assert.That(new HoldemAccusationResolver(s, new Source(_ => bad)).Resolve(c.ClaimId),
                Is.EqualTo(HoldemEvidenceResolution.EvidenceMismatch));
            Assert.That(s.Version, Is.EqualTo(before)); Assert.That(s.GetAccusationDecisions(), Is.Empty);
            Assert.That(s.GetPendingAccusations().Single().ClaimId, Is.EqualTo(c.ClaimId));
        }

        [Test]
        public void SourceCannotOverwriteAResultRecordedWhileFetching()
        {
            var s = Pending(); var claim = s.GetPendingAccusations().Single(); long before = s.Version;
            var resolver = new HoldemAccusationResolver(s, new Source(c => {
                Assert.That(s.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Verdict(c.SessionId,
                    c.HandId, c.WindowId, Guid.NewGuid(), s.Version, c.Street, c.ClaimId, false)).Accepted, Is.True);
                return HoldemDealerEvidence.ForClaim(c, true);
            }));
            Assert.That(resolver.Resolve(claim.ClaimId), Is.EqualTo(HoldemEvidenceResolution.StateChanged));
            Assert.That(s.Version, Is.EqualTo(before + 1));
            Assert.That(s.GetAccusationDecisions().Single().WasManipulated, Is.False);
        }

        [Test]
        public void UnknownClaimNeverCallsProviderAndPlayerPortHasNoResolver()
        {
            var s = Pending(); int calls = 0;
            var resolver = new HoldemAccusationResolver(s, new Source(_ => { calls++; return null; }));
            Assert.That(resolver.Resolve(Guid.NewGuid()), Is.EqualTo(HoldemEvidenceResolution.NoPendingClaim));
            Assert.That(calls, Is.Zero);
            Assert.That(typeof(IHoldemAccusationPlayerPort).GetMethod("ResolveAccusation"), Is.Null);
            Assert.That(typeof(IHoldemPlayerPort).GetMethod("ResolveAccusation"), Is.Null);
            Assert.Throws<ArgumentNullException>(() => new HoldemAccusationResolver(s, null));
            Assert.Throws<ArgumentException>(() => resolver.Resolve(Guid.Empty));
        }

        private static HoldemSession Pending()
        {
            var s = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, HoldemAccusationMode.CollectLatestChoiceUntilHostCloses),
                A, B, new FixedRandom());
            Assert.That(s.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            while (!s.GetSnapshot(A).IsRevealPending)
            {
                var v = s.GetSnapshot(A); var actor = s.GetSnapshot(v.CurrentSeat.Value);
                Assert.That(s.Submit(actor.ViewerSeat, HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(),
                    actor.ViewerSeat, v.SessionVersion, actor.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            foreach (var seat in new[] { A, B })
            {
                var v = s.GetSnapshot(seat);
                Assert.That(s.SubmitAccusationChoice(seat, new HoldemAccusationChoiceCommand(v.SessionId,
                    v.HandId, v.Accusations.WindowId, Guid.NewGuid(), v.SessionVersion, v.Street, seat,
                    seat == A ? B : (SeatId?)null)).Accepted, Is.True);
            }
            var end = s.GetSnapshot(A);
            Assert.That(s.ProcessAccusationHostCommand(HoldemAccusationHostCommand.Close(end.SessionId,
                end.HandId, end.Accusations.WindowId, Guid.NewGuid(), end.SessionVersion, end.Street)).Accepted, Is.True);
            return s;
        }

        private sealed class Source : IHoldemDealerEvidenceSource
        {
            private readonly Func<HoldemAccusationClaim, HoldemDealerEvidence> find;
            public Source(Func<HoldemAccusationClaim, HoldemDealerEvidence> find) { this.find = find; }
            public HoldemDealerEvidence FindEvidence(HoldemAccusationClaim claim) => find(claim);
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
    }
}
