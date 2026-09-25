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
            public HoldemDealerSelection Select(HoldemDealerWork w, HoldemDealerInterpretationBatch b)
                => Change ? HoldemDealerSelection.Change(b.GetEntry(0).UtteranceId, 0, Card.FromId(45),
                    HoldemCardSourceScope.UndealtOutsideCurrentHandRunout) : HoldemDealerSelection.Unchanged();
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
