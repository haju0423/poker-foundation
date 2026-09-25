using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRoomUtteranceTests
    {
        private HoldemRoom room;
        private Guid[] peers;
        private HoldemRoomAdmission[] admissions;
        private sealed class StableRandom : IRandomSource
        {
            public int NextInt(int exclusiveMax) => exclusiveMax - 1;
        }

        private void Create(bool enabled = true, HoldemConfig config = null, HoldemUtterancePolicy policy = null)
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            admissions = new HoldemRoomAdmission[4];
            room = HoldemRoom.Create(Guid.NewGuid(), peers[0], "방장", config ?? new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), out admissions[0],
                utterancePolicy: enabled ? policy ?? new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active) : null);
            for (int i = 1; i < 4; i++) admissions[i] = room.Join(peers[i], "참가자 " + i).Admission;
            foreach (var peer in peers) room.SetReady(peer, true);
        }

        private void Start()
        {
            var view = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(view.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), view.Game?.SessionVersion ?? 0)).Accepted, Is.True);
        }
        private HoldemRoomUtterance Speech(int seat, string text = "오늘 날씨가 좋네요", Guid? commandId = null)
        {
            var view = room.ReadUtterances(peers[seat - 1]);
            return new HoldemRoomUtterance(view.SessionId, view.HandId, view.WindowId,
                commandId ?? Guid.NewGuid(), view.Street, text);
        }
        private void Act(bool fold = false)
        {
            var publicState = room.Read(peers[0]).Game;
            var actor = peers[publicState.CurrentSeat.Value.Value - 1];
            var state = room.Read(actor).Game;
            Assert.That(room.Submit(actor, new HoldemRoomAction(state.SessionId, state.HandId,
                Guid.NewGuid(), state.SessionVersion, fold ? BettingAction.Fold()
                    : state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
        }
        private void FoldToEnd()
        {
            for (int i = 0; i < 4 && room.Read(peers[0]).Game.Result == null; i++) Act(true);
            Assert.That(room.Read(peers[0]).Game.Result, Is.Not.Null);
        }

        [Test]
        public void PublicOnlyConversationContinuesBeyondDealerQueueCapacityWithoutAcknowledgements()
        {
            Create(policy: new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                HoldemUtteranceVisibility.PublicRaw, HoldemUtteranceBatchRetention.None)); Start();
            for (int hand = 0; hand < HoldemRoom.MaximumPendingUtteranceBatches + 4; hand++)
            {
                Assert.That(room.Read(peers[0]).PublicUtterances.Count, Is.Zero);
                var input = Speech(1, "공개 대화 " + hand);
                long version = room.Read(peers[0]).Game.SessionVersion;
                Assert.That(room.SubmitUtterance(peers[0], input).Accepted, Is.True);
                Assert.That(room.SubmitUtterance(peers[0], input).Accepted, Is.True);
                Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(version));
                foreach (var peer in peers)
                {
                    var feed = room.Read(peer).PublicUtterances;
                    Assert.That(feed.Count, Is.EqualTo(1)); Assert.That(feed.GetEntry(0).Text, Is.EqualTo(input.Text));
                    Assert.That(room.ReadUtterances(peer).IsBacklogged, Is.False);
                }
                FoldToEnd();
                Assert.That(room.ReadClosedUtteranceBatches(peers[0]), Is.Empty);
                Assert.That(room.AcknowledgeUtteranceBatch(peers[0], input.WindowId), Is.False);
                Assert.That(room.Read(peers[0]).PublicUtterances.Count, Is.EqualTo(1));
                Start();
            }
        }

        [Test]
        public void InvalidBatchRetentionIsRejectedAndExistingPoliciesRetainUntilAcknowledged()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemUtterancePolicy(128, 1,
                HoldemUtteranceSeats.Active, batchRetention: (HoldemUtteranceBatchRetention)77));
            Assert.That(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active).BatchRetention,
                Is.EqualTo(HoldemUtteranceBatchRetention.UntilHostAcknowledges));
        }

        [Test]
        public void DisabledAndPreHandIntakeNeverCreatePokerState()
        {
            Create(false);
            Assert.That(room.ReadUtterances(peers[0]), Is.Null);
            var view = room.Read(peers[0]);
            var input = new HoldemRoomUtterance(view.SessionId, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), HoldemStreet.Preflop, "멘트");
            Assert.That(room.SubmitUtterance(peers[0], input).Utterance.Error, Is.EqualTo(HoldemUtteranceError.Disabled));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(view.Revision));
            Start();
            Assert.That(room.ReadUtterances(peers[0]), Is.Null);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]), Is.Empty);
            Create();
            Assert.That(room.ReadUtterances(peers[0]), Is.Null);
            Assert.That(room.SubmitUtterance(peers[0], input).Utterance.Error, Is.EqualTo(HoldemUtteranceError.WindowClosed));
            Assert.That(room.Read(peers[0]).Game, Is.Null);
        }

        [Test]
        public void SeatBoundIntakeDoesNotChangePokerOrExposeOtherPlayersText()
        {
            Create(); Start();
            var before = room.Read(peers[1]);
            var command = Speech(2, "나만 보는 접수 원문");
            var receipt = room.SubmitUtterance(peers[1], command);
            Assert.That(receipt.Accepted, Is.True);
            var after = room.Read(peers[1]);
            Assert.That(after.Revision, Is.EqualTo(before.Revision + 1));
            Assert.That(after.Game.SessionVersion, Is.EqualTo(before.Game.SessionVersion));
            Assert.That(after.Game.CurrentSeat, Is.EqualTo(before.Game.CurrentSeat));
            Assert.That(after.Game.PotAmount, Is.EqualTo(before.Game.PotAmount));
            Assert.That(after.Game.BoardCount, Is.EqualTo(before.Game.BoardCount));
            Assert.That(after.Game.OwnStack, Is.EqualTo(before.Game.OwnStack));
            var own = room.ReadUtterances(peers[1]);
            Assert.That(own.Count, Is.EqualTo(1));
            Assert.That(own.GetEntry(0).Speaker, Is.EqualTo(new SeatId(2)));
            Assert.That(own.GetEntry(0).Text, Is.EqualTo(command.Text));
            foreach (int other in new[] { 0, 2, 3 }) Assert.That(room.ReadUtterances(peers[other]).Count, Is.Zero);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => room.ReadClosedUtteranceBatches(peers[1]));
            Assert.Throws<InvalidOperationException>(() => room.AcknowledgeUtteranceBatch(peers[1], command.WindowId));
        }

        [Test]
        public void RetryAndConflictAreIdempotentAcrossBettingAndReconnect()
        {
            Create(); Start();
            var command = Speech(2);
            Act(); // Another player's betting revision must not invalidate a speech window.
            Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            long revision = room.Read(peers[0]).Revision;
            Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            var conflict = Speech(2, "다른 내용", command.CommandId);
            Assert.That(room.SubmitUtterance(peers[1], conflict).Utterance.Error, Is.EqualTo(HoldemUtteranceError.CommandConflict));
            Assert.That(room.SubmitUtterance(peers[2], command).Utterance.Error, Is.EqualTo(HoldemUtteranceError.CommandConflict));
            Guid old = peers[1]; room.Disconnect(old);
            Assert.That(room.SubmitUtterance(old, command).Error, Is.EqualTo(HoldemRoomError.Disconnected));
            Assert.That(room.ReadUtterances(peers[0]).CanSubmit, Is.False);
            Assert.That(room.ReadUtterances(peers[0]).IsBacklogged, Is.False, "A disconnected player is not a full input backlog.");
            Assert.That(room.SubmitUtterance(peers[0], Speech(1)).Error, Is.EqualTo(HoldemRoomError.Paused));
            peers[1] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(room.SubmitUtterance(old, command).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            Assert.That(room.ReadUtterances(peers[1]).Count, Is.EqualTo(1));
            Assert.That(room.SubmitUtterance(Guid.NewGuid(), command).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
        }

        [Test]
        public void ConcurrentSeatsBindIndependentlyAndFreezeOneHostBatch()
        {
            Create(); Start();
            var commands = Enumerable.Range(1, 4).Select(i => Speech(i, "멘트 " + i)).ToArray();
            var results = new HoldemRoomUtteranceReceipt[4];
            Parallel.For(0, 4, i => results[i] = room.SubmitUtterance(peers[i], commands[i]));
            Assert.That(results.All(r => r.Accepted), Is.True);
            Assert.That(results.Select(r => r.Utterance.Ordinal).Distinct().Count(), Is.EqualTo(4));
            FoldToEnd();
            var batches = room.ReadClosedUtteranceBatches(peers[0]);
            Assert.That(batches.Count, Is.EqualTo(1));
            var batch = batches[0];
            Assert.That(batch.Count, Is.EqualTo(4));
            Assert.That(Enumerable.Range(0, 4).Select(i => batch.GetEntry(i).Speaker.Value), Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
            long revision = room.Read(peers[0]).Revision;
            Assert.That(room.SubmitUtterance(peers[1], commands[1]).Accepted, Is.True, "Lost receipt remains recoverable after close.");
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            Assert.That(room.ReadClosedUtteranceBatches(peers[0])[0], Is.SameAs(batch));
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], Guid.NewGuid()), Is.False);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0])[0], Is.SameAs(batch));
            Start();
            Assert.That(room.ReadUtterances(peers[1]).Count, Is.Zero);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0])[0], Is.SameAs(batch));
            Assert.That(room.SubmitUtterance(peers[1], commands[1]).Utterance.Error, Is.EqualTo(HoldemUtteranceError.WrongHand));
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], batch.WindowId), Is.True);
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], batch.WindowId), Is.False);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]), Is.Empty);
            Assert.That(batch.Count, Is.EqualTo(4), "Previously returned batches remain immutable after ack.");
        }

        [Test]
        public void BacklogBoundsOnlyNewSpeechAndNeverStopsPoker()
        {
            Create(); Start();
            for (int i = 0; i < HoldemRoom.MaximumPendingUtteranceBatches; i++)
            {
                var input = Speech(1);
                Assert.That(room.SubmitUtterance(peers[0], input).Accepted, Is.True);
                if (i == HoldemRoom.MaximumPendingUtteranceBatches - 1)
                    for (int seat = 2; seat <= 4; seat++)
                        Assert.That(room.SubmitUtterance(peers[seat - 1], Speech(seat)).Accepted, Is.True);
                FoldToEnd();
                Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(i + 1));
                if (i == HoldemRoom.MaximumPendingUtteranceBatches - 1)
                {
                    Assert.That(room.ReadClosedUtteranceBatches(peers[0])[i].Count, Is.EqualTo(4));
                    Assert.That(room.SubmitUtterance(peers[0], input).Accepted, Is.True, "Full queue must still confirm an accepted input.");
                }
                Start();
            }
            var blocked = Speech(1);
            Assert.That(room.ReadUtterances(peers[0]).CanSubmit, Is.False);
            Assert.That(room.ReadUtterances(peers[0]).IsBacklogged, Is.True);
            Assert.That(HoldemRoomPacketMapper.Create(room.Read(peers[0])).ownUtterances.backlogged, Is.True);
            long revision = room.Read(peers[0]).Revision;
            Assert.That(room.SubmitUtterance(peers[0], blocked).Utterance.Error, Is.EqualTo(HoldemUtteranceError.BacklogFull));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            Act();
            var batch = room.ReadClosedUtteranceBatches(peers[0])[0];
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], batch.WindowId), Is.True);
            Assert.That(room.ReadUtterances(peers[0]).CanSubmit, Is.True);
            Assert.That(room.ReadUtterances(peers[0]).IsBacklogged, Is.False);
            Assert.That(room.SubmitUtterance(peers[0], blocked).Accepted, Is.True);
            FoldToEnd();
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(HoldemRoom.MaximumPendingUtteranceBatches));
        }

        [Test]
        public void FullBacklogStillAllowsEveryDealRevealAndSettlementWithoutNewSpeech()
        {
            Create(config: new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost)); Start();
            for (int i = 0; i < HoldemRoom.MaximumPendingUtteranceBatches; i++)
            {
                Assert.That(room.SubmitUtterance(peers[0], Speech(1)).Accepted, Is.True);
                FoldToEnd(); Start();
            }
            int deals = 0, reveals = 0;
            for (int step = 0; step < 80 && room.Read(peers[0]).Game.Result == null; step++)
            {
                var state = room.Read(peers[0]).Game;
                Assert.That(room.ReadUtterances(peers[0]).IsBacklogged, Is.True);
                Assert.That(room.Read(peers[0]).Paused, Is.False);
                if (state.IsDealPending)
                {
                    var deal = state.PendingDeal;
                    Assert.That(room.DealUnchanged(peers[0], new HoldemDealCommand(state.SessionId, state.HandId,
                        deal.WindowId, Guid.NewGuid(), state.SessionVersion, deal.Street)).Accepted, Is.True);
                    deals++;
                }
                else if (state.IsRevealPending)
                {
                    Assert.That(room.ResumeAfterReveal(peers[0], new HoldemRevealCommand(state.SessionId, state.HandId,
                        Guid.NewGuid(), state.SessionVersion, state.Street)).Accepted, Is.True);
                    reveals++;
                }
                else if (state.IsSettlementPending)
                    Assert.That(room.ResolveSettlement(peers[0], Guid.NewGuid(), state.SessionVersion,
                        HoldemOddChipRule.ClockwiseFromButton).Accepted, Is.True);
                else Act();
            }
            var end = room.Read(peers[0]).Game;
            Assert.That(end.Result, Is.Not.Null);
            Assert.That(deals, Is.EqualTo(3)); Assert.That(reveals, Is.EqualTo(3));
            Assert.That(Enumerable.Range(0, end.SeatCount).Sum(i => end.GetSeatAt(i).Stack), Is.EqualTo(400));
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(HoldemRoom.MaximumPendingUtteranceBatches));
        }

        [Test]
        public void DealAndRevealWindowsCloseSpeechWithoutChangingTheDealingPath()
        {
            Create(config: new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost)); Start();
            var command = Speech(2); Assert.That(room.SubmitUtterance(peers[1], command).Accepted, Is.True);
            for (int i = 0; i < 4; i++) Act();
            var state = room.Read(peers[0]).Game;
            Assert.That(state.IsDealPending, Is.True);
            Assert.That(room.ReadUtterances(peers[1]).CanSubmit, Is.False);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(1));
            Assert.That(room.SubmitUtterance(peers[1], new HoldemRoomUtterance(command.SessionId, command.HandId,
                command.WindowId, Guid.NewGuid(), command.Street, "늦은 멘트")).Utterance.Error, Is.EqualTo(HoldemUtteranceError.WindowClosed));
            var deal = state.PendingDeal;
            Assert.That(room.DealUnchanged(peers[0], new HoldemDealCommand(state.SessionId, state.HandId,
                deal.WindowId, Guid.NewGuid(), state.SessionVersion, deal.Street)).Accepted, Is.True);
            state = room.Read(peers[0]).Game;
            Assert.That(state.IsRevealPending, Is.True);
            Assert.That(room.ReadUtterances(peers[1]).CanSubmit, Is.False);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(1));
        }

        [Test]
        public void PrivateBatchConsumptionHasNoPublicRevisionAndEmptyHandsConsumeNoSlots()
        {
            Create(); Start();
            for (int i = 0; i < 3; i++)
            {
                Assert.That(room.SubmitUtterance(peers[0], Speech(1, "판 " + i)).Accepted, Is.True);
                FoldToEnd(); Start();
            }
            var batches = room.ReadClosedUtteranceBatches(peers[0]);
            Assert.That(batches.Count, Is.EqualTo(3));
            Assert.That(batches.Select(b => b.HandId).Distinct().Count(), Is.EqualTo(3));
            Assert.That(batches.All(b => b.GetEntry(0).Ordinal == 1), Is.True);
            long revision = room.Read(peers[0]).Revision;
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], batches[1].WindowId), Is.True);
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]), Is.EqualTo(new[] { batches[0], batches[2] }));
            Assert.That(batches.Count, Is.EqualTo(3), "Returned list is detached from the live queue.");
            for (int i = 0; i < 20; i++) { FoldToEnd(); Start(); }
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(2));
            Assert.That(room.ReadUtterances(peers[0]).CanSubmit, Is.True);
        }

        [TestCase("session", HoldemUtteranceError.WrongSession)]
        [TestCase("hand", HoldemUtteranceError.WrongHand)]
        [TestCase("window", HoldemUtteranceError.WrongWindow)]
        [TestCase("street", HoldemUtteranceError.WrongWindow)]
        [TestCase("empty", HoldemUtteranceError.EmptyText)]
        [TestCase("long", HoldemUtteranceError.TextTooLong)]
        [TestCase("control", HoldemUtteranceError.InvalidText)]
        public void InvalidInputsDoNotChangeRoomOrPoker(string invalid, HoldemUtteranceError expected)
        {
            Create(); Start();
            var command = Speech(2);
            var input = new HoldemRoomUtterance(invalid == "session" ? Guid.NewGuid() : command.SessionId,
                invalid == "hand" ? Guid.NewGuid() : command.HandId,
                invalid == "window" ? Guid.NewGuid() : command.WindowId, command.CommandId,
                invalid == "street" ? HoldemStreet.Flop : command.Street,
                invalid == "empty" ? " " : invalid == "long" ? new string('a', 129) : invalid == "control" ? "a\nb" : command.Text);
            var before = room.Read(peers[1]);
            Assert.That(room.SubmitUtterance(peers[1], input).Utterance.Error, Is.EqualTo(expected));
            Assert.That(room.Read(peers[1]).Revision, Is.EqualTo(before.Revision));
            Assert.That(room.Read(peers[1]).Game.SessionVersion, Is.EqualTo(before.Game.SessionVersion));
            Assert.That(room.ReadUtterances(peers[1]).Count, Is.Zero);
        }
    }
}
