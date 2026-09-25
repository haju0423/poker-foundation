using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemLocalDealerContractTests
    {
        private sealed class Ordered : IRandomSource { public int NextInt(int max) => max - 1; }
        private sealed class Passive : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot s, IRandomSource r) => s.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
        private sealed class TestSelection : IHoldemDealerSelectionPolicy
        {
            public bool Change;
            public Card Card = Card.FromId(45);
            public HoldemDealerSelection Select(HoldemDealerWork w, HoldemDealerInterpretationBatch b)
                => Change ? HoldemDealerSelection.Change(b.GetEntry(0).UtteranceId, 0, Card,
                    HoldemCardSourceScope.UndealtOutsideCurrentHandRunout) : HoldemDealerSelection.Unchanged();
        }

        [TestCase(3, 0)] [TestCase(4, 0)]
        [TestCase(3, 1)] [TestCase(4, 1)]
        [TestCase(3, 2)] [TestCase(4, 2)]
        [TestCase(3, 3)] [TestCase(4, 3)]
        [TestCase(3, 4)] [TestCase(4, 4)]
        public void NpcSpeechActualChangeIsTheOnlyGroundForHumanVerdict(int seats, int scenario)
        {
            // 0 actual change; 1 failed selection; 2 timeout; 3 wrong target; 4 natural card match.
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses, HoldemDealPolicy.WaitForHost), seats,
                new Ordered(), new Ordered(), new Passive(), HoldemOddChipRule.ClockwiseFromButton,
                new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
            var port = table.BindNpcUtterances(new SeatId(2)); var speech = port.ReadUtterances();
            var command = new HoldemUtteranceCommand(speech.SessionId, speech.HandId, speech.WindowId,
                Guid.NewGuid(), speech.Street, speech.ViewerSeat, "NPC 테스트 요청");
            Assert.That(port.SubmitUtterance(command).Accepted, Is.True);
            Assert.That(port.SubmitUtterance(command).Accepted, Is.True);
            Assert.That(port.ReadUtterances().Count, Is.EqualTo(1), "Replay cannot add another utterance.");
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
            var transcript = ((IHoldemPublicUtteranceSource)table.Human).ReadPublicUtterances();
            Assert.That(transcript.Count, Is.EqualTo(1));
            Assert.That(transcript.GetEntry(0).Speaker, Is.EqualTo(new SeatId(2)));
            Assert.Throws<ArgumentException>(() => table.BindNpcUtterances(new SeatId(1)));
            Assert.Throws<ArgumentException>(() => table.BindNpcUtterances(new SeatId(9)));
            var forged = new HoldemUtteranceCommand(speech.SessionId, speech.HandId, speech.WindowId,
                Guid.NewGuid(), speech.Street, new SeatId(3), "다른 좌석 사칭");
            Assert.That(port.SubmitUtterance(forged).Error, Is.EqualTo(HoldemUtteranceError.UnauthorizedSeat));
            for (int i = 0; i < 12 && table.Human.Read().CurrentSeat.HasValue; i++)
            {
                if (table.AdvanceNpc()) continue;
                var s = table.Human.Read();
                Assert.That(table.Human.Submit(HoldemCommand.Act(s.SessionId, s.HandId, Guid.NewGuid(), s.ViewerSeat,
                    s.SessionVersion, s.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            var before = table.Human.Read(); Assert.That(before.IsDealPending, Is.True);
            TimeSpan now = TimeSpan.Zero;
            // With an ordered deck: two cards per seat, one burn, then the first natural flop card.
            var requested = Card.FromId(scenario == 4 ? seats * 2 + 1 : 45);
            using var coordinator = new HoldemDealerTurnCoordinator(table,
                new TestSelection { Change = scenario == 0 || scenario == 3, Card = requested },
                TimeSpan.FromSeconds(5), () => now);
            coordinator.Poll(out var work); var d = work.TargetDeal; var source = work.SourceUtterances;
            Assert.That(source.Count, Is.EqualTo(1));
            Assert.That(source.GetEntry(0).CommandId, Is.EqualTo(command.CommandId));
            var response = new HoldemDealerInterpretationBatch(d.SessionId, d.HandId, d.WindowId,
                work.ExpectedVersion, d.Street, source.WindowId, new[] {
                    new HoldemDealerInterpretation(command.CommandId, HoldemDealerIntent.CardPreference,
                        requested.Rank, requested.Suit, .1, .9)
                });
            if (scenario == 2) now = TimeSpan.FromSeconds(5);
            else
            {
                Assert.That(coordinator.TryPostInterpretations(work, response), Is.True);
                Assert.That(coordinator.TryPostInterpretations(work, response), Is.True);
            }
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(table.ReadHumanCardChange(), Is.Null, "Human must not see NPC owner feedback.");
            var revealed = table.Human.Read(); long appliedVersion = revealed.SessionVersion;
            Assert.That(coordinator.TryPostInterpretations(work, response), Is.False, "Late completed work is inert.");
            coordinator.Poll(out _);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(appliedVersion));
            if (scenario == 0 || scenario == 3 || scenario == 4)
                Assert.That(revealed.GetBoardCard(0), Is.EqualTo(requested));
            var accuse = new HoldemAccusationChoiceCommand(revealed.SessionId, revealed.HandId,
                revealed.Accusations.WindowId, Guid.NewGuid(), revealed.SessionVersion, revealed.Street,
                revealed.ViewerSeat, new SeatId(scenario == 3 ? 3 : 2));
            var human = (IHoldemAccusationPlayerPort)table.Human;
            Assert.That(human.SubmitAccusationChoice(accuse).Accepted, Is.True);
            Assert.That(human.SubmitAccusationChoice(accuse).Accepted, Is.True);
            for (int i = 0; i < seats + 1; i++) table.AdvanceAccusationResponses();
            var claim = table.GetPendingAccusations().Single();
            Assert.That(table.ResolveRecordedAccusation(claim.ClaimId, HoldemAccusationEvidenceScope.CurrentRevealOnly),
                Is.EqualTo(HoldemEvidenceResolution.Recorded));
            var after = table.Human.Read();
            Assert.That(after.Accusations.OwnVerdict, Is.EqualTo(scenario == 0));
            Assert.That(after.Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
            for (int i = 0; i < seats; i++)
            {
                var seat = after.GetSeatAt(i); var old = before.GetSeat(seat.Seat);
                Assert.That(seat.Stack, Is.EqualTo(old.Stack)); Assert.That(seat.Status, Is.EqualTo(old.Status));
                Assert.That(seat.VisibleHoleCardCount, Is.EqualTo(seat.IsViewer ? 2 : 0));
            }
            Assert.That(after.GetSeat(after.ViewerSeat).GetVisibleHoleCard(0),
                Is.EqualTo(before.GetSeat(before.ViewerSeat).GetVisibleHoleCard(0)));
        }

        [TestCase(HoldemUtteranceVisibility.PublicRaw, true)]
        [TestCase(HoldemUtteranceVisibility.OwnOnly, false)]
        public void LocalPlayerPublicTranscriptHonorsVisibilityPolicy(HoldemUtteranceVisibility visibility, bool exposed)
        {
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new Ordered(), new Ordered(), new Passive(),
                HoldemOddChipRule.ClockwiseFromButton, new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active, visibility));
            var speech = table.HumanUtterances.ReadUtterances();
            Assert.That(table.HumanUtterances.SubmitUtterance(new HoldemUtteranceCommand(speech.SessionId,
                speech.HandId, speech.WindowId, Guid.NewGuid(), speech.Street, speech.ViewerSeat, "테스트 멘트")).Accepted, Is.True);
            var transcript = ((IHoldemPublicUtteranceSource)table.Human).ReadPublicUtterances();
            Assert.That(transcript != null, Is.EqualTo(exposed));
            if (exposed)
            {
                Assert.That(transcript.Count, Is.EqualTo(1));
                Assert.That(transcript.GetEntry(0).Text, Is.EqualTo("테스트 멘트"));
            }
        }

        [Test]
        public void LocalPlayerWithoutSpeechPolicyHasNoPublicTranscript()
        {
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new Ordered(), new Ordered(), new Passive());
            Assert.That(((IHoldemPublicUtteranceSource)table.Human).ReadPublicUtterances(), Is.Null);
        }

        [Test]
        public void PublicOnlyLocalTableCannotChangeCardsByPassingAKnownUtteranceDirectly()
        {
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), 4, new Ordered(), new Ordered(), new Passive(),
                HoldemOddChipRule.ClockwiseFromButton, new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active,
                    HoldemUtteranceVisibility.PublicRaw, HoldemUtteranceBatchRetention.None));
            var speech = table.HumanUtterances.ReadUtterances(); var utteranceId = Guid.NewGuid();
            Assert.That(table.HumanUtterances.SubmitUtterance(new HoldemUtteranceCommand(speech.SessionId,
                speech.HandId, speech.WindowId, utteranceId, speech.Street, speech.ViewerSeat, "공개 대화만 사용")).Accepted, Is.True);
            for (int i = 0; i < 12 && table.Human.Read().CurrentSeat.HasValue; i++)
            {
                if (table.AdvanceNpc()) continue;
                var state = table.Human.Read();
                Assert.That(table.Human.Submit(HoldemCommand.Act(state.SessionId, state.HandId, Guid.NewGuid(),
                    state.ViewerSeat, state.SessionVersion,
                    state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            var turn = table.ReadPendingTurn().Turn;
            Assert.That(turn.InputKind, Is.EqualTo(HoldemDealerInputKind.Disabled));
            var d = turn.TargetDeal; long version = table.Human.Read().SessionVersion;
            var changed = new HoldemDealCommand(d.SessionId, d.HandId, d.WindowId, Guid.NewGuid(),
                turn.ExpectedVersion, d.Street, new HoldemCardChange(speech.WindowId, utteranceId,
                    speech.ViewerSeat, 0, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(table.ApplyDealerDeal(changed).Error, Is.EqualTo(HoldemCommandError.InvalidCardChange));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(table.Human.Read().BoardCount, Is.Zero);
            Assert.That(table.ApplyDealerDeal(turn.CreateUnchangedCommand(Guid.NewGuid())).Accepted, Is.True);
            Assert.That(table.Human.Read().BoardCount, Is.EqualTo(3));
            Assert.That(table.ReadHumanCardChange(), Is.Null);
        }

        [TestCase(3, true)] [TestCase(4, true)] [TestCase(3, false)] [TestCase(4, false)]
        public void LocalNpcDebugUsesSameInterpretationApplicationAndEvidencePath(int seats, bool change)
        {
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses, HoldemDealPolicy.WaitForHost), seats,
                new Ordered(), new Ordered(), new Passive(), HoldemOddChipRule.ClockwiseFromButton,
                new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
            var speech = table.HumanUtterances.ReadUtterances();
            Assert.That(table.HumanUtterances.SubmitUtterance(new HoldemUtteranceCommand(speech.SessionId,
                speech.HandId, speech.WindowId, Guid.NewGuid(), speech.Street, speech.ViewerSeat, "테스트 멘트")).Accepted, Is.True);
            for (int i = 0; i < 12 && table.Human.Read().CurrentSeat.HasValue; i++)
            {
                if (table.AdvanceNpc()) continue;
                var s = table.Human.Read();
                Assert.That(table.Human.Submit(HoldemCommand.Act(s.SessionId, s.HandId, Guid.NewGuid(), s.ViewerSeat,
                    s.SessionVersion, s.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            var before = table.Human.Read(); Assert.That(before.IsDealPending, Is.True);
            Assert.That(table.Human, Is.Not.InstanceOf<IHoldemDealerDealApplicationPort>());
            using var coordinator = new HoldemDealerTurnCoordinator(table, new TestSelection { Change = change });
            coordinator.Poll(out var work); var d = work.TargetDeal; var source = work.SourceUtterances;
            var card = Card.FromId(45);
            var response = new HoldemDealerInterpretationBatch(d.SessionId, d.HandId, d.WindowId,
                work.ExpectedVersion, d.Street, source.WindowId, new[] {
                    new HoldemDealerInterpretation(source.GetEntry(0).CommandId,
                        HoldemDealerIntent.CardPreference, card.Rank, card.Suit, .1, .9)
                });
            Assert.That(coordinator.TryPostInterpretations(work, response), Is.True);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(table.ReadHumanCardChange() != null, Is.EqualTo(change));
            if (change) Assert.That(table.Human.Read().GetBoardCard(0), Is.EqualTo(table.ReadHumanCardChange().Card));
            Assert.That(table.Human.Read().PotAmount, Is.EqualTo(before.PotAmount));
            // The human's successful request must never make another player guilty.
            var own = table.Human.Read();
            var accuse = new HoldemAccusationChoiceCommand(own.SessionId, own.HandId, own.Accusations.WindowId,
                Guid.NewGuid(), own.SessionVersion, own.Street, own.ViewerSeat, new SeatId(2));
            Assert.That(((IHoldemAccusationPlayerPort)table.Human).SubmitAccusationChoice(accuse).Accepted, Is.True);
            for (int i = 0; i < seats + 1; i++) table.AdvanceAccusationResponses();
            var claim = table.GetPendingAccusations().Single();
            Assert.That(table.ResolveRecordedAccusation(claim.ClaimId, HoldemAccusationEvidenceScope.CurrentRevealOnly),
                Is.EqualTo(HoldemEvidenceResolution.Recorded));
            Assert.That(table.GetAccusationDecisions().Single().WasManipulated, Is.False);
            Assert.That(table.Human.Read().PotAmount, Is.EqualTo(before.PotAmount), "No unapproved fine or payout.");
        }
    }
}
