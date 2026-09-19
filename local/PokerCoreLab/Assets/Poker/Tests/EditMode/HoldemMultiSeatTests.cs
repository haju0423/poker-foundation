using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemMultiSeatTests
    {
        private static readonly SeatId A = new SeatId(1);
        private static readonly SeatId B = new SeatId(2);
        private static readonly SeatId C = new SeatId(3);
        private static readonly SeatId D = new SeatId(4);
        private static readonly SeatId[] Table = { A, B, C, D };

        [Test]
        public void FourSeatFoldPersistsAcrossStreetsAndSnapshotsRemainSeatRelative()
        {
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                Table, A, new IdentityRandom());
            Start(session);

            HoldemSnapshot opening = session.GetSnapshot(A);
            Assert.That(opening.SeatCount, Is.EqualTo(4));
            Assert.That(opening.ButtonSeat, Is.EqualTo(A));
            Assert.That(opening.SmallBlindSeat, Is.EqualTo(B));
            Assert.That(opening.BigBlindSeat, Is.EqualTo(C));
            Assert.That(opening.CurrentSeat, Is.EqualTo(D));
            Assert.That(opening.GetSeat(A).VisibleHoleCardCount, Is.EqualTo(2));
            Assert.That(opening.GetSeat(B).VisibleHoleCardCount, Is.Zero);
            Assert.That(opening.GetSeat(C).VisibleHoleCardCount, Is.Zero);
            Assert.That(opening.GetSeat(D).VisibleHoleCardCount, Is.Zero);

            Submit(session, D, BettingAction.Fold());
            Submit(session, A, BettingAction.Call());
            Submit(session, B, BettingAction.Call());
            Submit(session, C, BettingAction.Check());

            HoldemSnapshot aView = session.GetSnapshot(A);
            Assert.That(aView.Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(aView.BoardCount, Is.EqualTo(3));
            Assert.That(aView.CurrentSeat, Is.EqualTo(B));
            Assert.That(aView.GetSeat(D).Status, Is.EqualTo(HoldemSeatStatus.Folded));
            Assert.That(aView.GetSeat(D).VisibleHoleCardCount, Is.Zero);
            Assert.That(aView.GetSeat(D).StreetContribution, Is.Zero);

            HoldemSnapshot dView = session.GetSnapshot(D);
            Assert.That(dView.GetSeat(D).VisibleHoleCardCount, Is.EqualTo(2));
            Assert.That(dView.GetSeat(A).VisibleHoleCardCount, Is.Zero);
            Assert.That(dView.GetSeat(B).VisibleHoleCardCount, Is.Zero);
            Assert.That(dView.GetSeat(C).VisibleHoleCardCount, Is.Zero);
            Assert.That(dView.LegalActions, Is.Null);
            Assert.Throws<InvalidOperationException>(() => { var ignored = aView.OpponentSeat; });
        }

        [TestCase(HoldemRevealPolicy.Automatic)]
        [TestCase(HoldemRevealPolicy.PauseAfterCommunityReveal)]
        public void OddSidePotWaitsAtomicallyThenTrustedClockwiseResolutionIsIdempotent(HoldemRevealPolicy policy)
        {
            Card[] prefix = {
                CardOf("Js"), CardOf("Jd"), CardOf("2c"), CardOf("3h"),
                CardOf("Qc"), CardOf("Qh"), CardOf("2d"), CardOf("3s"),
                CardOf("Ac"), CardOf("3c"), CardOf("3d"), CardOf("8h"),
                CardOf("Ad"), CardOf("9s"), CardOf("Ah"), CardOf("Td")
            };
            ChipLedger ledger = ChipLedger.Create(new[] {
                new SeatChips(A, 5), new SeatChips(B, 10),
                new SeatChips(C, 10), new SeatChips(D, 10)
            });
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2, policy),
                ledger, Table, A, new PrefixRandom(prefix));
            Start(session);

            Submit(session, D, BettingAction.RaiseTo(10));
            Submit(session, A, BettingAction.Call());
            Submit(session, B, BettingAction.Call());
            Submit(session, C, BettingAction.Call());

            if (policy == HoldemRevealPolicy.PauseAfterCommunityReveal)
                for (int stage = 1; stage <= 3; stage++)
                {
                    var reveal = session.GetSnapshot(A);
                    Assert.That(reveal.IsRevealPending, Is.True);
                    Assert.That((int)reveal.Street, Is.EqualTo(stage));
                    Assert.That(reveal.GetSeat(B).VisibleHoleCardCount, Is.Zero);
                    Assert.That(reveal.PotAmount, Is.EqualTo(35));
                    Assert.That(session.ResumeAfterReveal(new HoldemRevealCommand(reveal.SessionId, reveal.HandId,
                        Guid.NewGuid(), reveal.SessionVersion, reveal.Street)).Accepted, Is.True);
                }

            HoldemSnapshot pending = session.GetSnapshot(A);
            Assert.That(pending.Street, Is.EqualTo(HoldemStreet.River));
            Assert.That(pending.SettlementState,
                Is.EqualTo(HoldemSettlementState.AwaitingOddChipPriority));
            Assert.That(pending.IsSettlementPending, Is.True);
            Assert.That(pending.CurrentSeat, Is.Null);
            Assert.That(pending.LegalActions, Is.Null);
            Assert.That(pending.Result, Is.Null);
            Assert.That(pending.PotAmount, Is.EqualTo(35));
            for (int i = 0; i < pending.SeatCount; i++)
            {
                Assert.That(pending.GetSeatAt(i).VisibleHoleCardCount, Is.EqualTo(2));
                Assert.That(pending.GetSeatAt(i).RevealedHandValue.HasValue, Is.True);
                Assert.That(pending.GetSeatAt(i).Awarded, Is.Zero);
            }

            long pendingVersion = session.Version;
            Guid badId = Guid.NewGuid();
            HoldemReceipt rejected = session.ResolvePendingSettlement(badId, pendingVersion,
                HoldemOddChipRule.RequireExplicitPriority);
            Assert.That(rejected.Error, Is.EqualTo(HoldemCommandError.InvalidSettlementRule));
            Assert.That(session.Version, Is.EqualTo(pendingVersion));
            Assert.That(session.GetSnapshot(B).IsSettlementPending, Is.True);

            Guid resolveId = Guid.NewGuid();
            HoldemReceipt resolved = session.ResolvePendingSettlement(resolveId, pendingVersion,
                HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(resolved.Accepted, Is.True);
            Assert.That(session.ResolvePendingSettlement(resolveId, pendingVersion,
                HoldemOddChipRule.ClockwiseFromButton), Is.SameAs(resolved));
            Assert.That(session.ResolvePendingSettlement(resolveId, resolved.Version.Value,
                HoldemOddChipRule.ClockwiseFromButton).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));

            HoldemSnapshot completed = session.GetSnapshot(D);
            Assert.That(completed.Street, Is.EqualTo(HoldemStreet.Complete));
            Assert.That(completed.SettlementState, Is.EqualTo(HoldemSettlementState.Settled));
            Assert.That(completed.Result.PotCount, Is.EqualTo(2));
            Assert.That(completed.Result.GetPot(0).Amount, Is.EqualTo(20));
            Assert.That(completed.Result.GetPot(1).Amount, Is.EqualTo(15));
            Assert.That(completed.Result.GetAwardedTo(A), Is.EqualTo(20));
            Assert.That(completed.Result.GetAwardedTo(B), Is.EqualTo(8));
            Assert.That(completed.Result.GetAwardedTo(C), Is.EqualTo(7));
            Assert.That(completed.Result.GetAwardedTo(D), Is.Zero);
            Assert.That(completed.Result.GetPot(1).GetPayout(0).Seat, Is.EqualTo(B));
            Assert.That(completed.Result.GetPot(1).GetPayout(0).HasOddChip, Is.True);
            Assert.That(completed.Result.GetPot(1).GetPayout(1).Seat, Is.EqualTo(C));
            Assert.That(completed.Result.GetPot(1).GetPayout(1).HasOddChip, Is.False);
            Assert.That(Enumerable.Range(0, completed.SeatCount)
                .Sum(i => completed.GetSeatAt(i).Stack), Is.EqualTo(35));

            HoldemReceipt next = session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version);
            Assert.That(next.Accepted, Is.True);
            HoldemSnapshot threeHanded = session.GetSnapshot(D);
            Assert.That(threeHanded.ButtonSeat, Is.EqualTo(B));
            Assert.That(threeHanded.GetSeat(D).WasDealtIn, Is.False);
            Assert.That(threeHanded.GetSeat(D).Status, Is.EqualTo(HoldemSeatStatus.Busted));
            Assert.That(threeHanded.GetSeat(D).VisibleHoleCardCount, Is.Zero);
        }

        [Test]
        public void ForwardButtonSkipsSpectatorsAndThreeToTwoMayRepeatTheBigBlind()
        {
            Card[] prefix = {
                CardOf("Kc"), CardOf("Ac"), CardOf("2c"),
                CardOf("Kd"), CardOf("Ad"), CardOf("3c"),
                CardOf("4c"), CardOf("5d"), CardOf("7h"), CardOf("9s"),
                CardOf("4d"), CardOf("Jd"), CardOf("4h"), CardOf("Qh")
            };
            ChipLedger ledger = ChipLedger.Create(new[] {
                new SeatChips(A, 0), new SeatChips(B, 1),
                new SeatChips(C, 10), new SeatChips(D, 10)
            });
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                ledger, Table, B, new PrefixRandom(prefix), HoldemOddChipRule.ClockwiseFromButton);
            Start(session);

            HoldemSnapshot first = session.GetSnapshot(A);
            Assert.That(first.GetSeat(A).WasDealtIn, Is.False);
            Assert.That(first.GetSeat(A).Status, Is.EqualTo(HoldemSeatStatus.Busted));
            Assert.That(first.GetSeat(A).VisibleHoleCardCount, Is.Zero);
            Assert.That(first.ButtonSeat, Is.EqualTo(B));
            Assert.That(first.SmallBlindSeat, Is.EqualTo(C));
            Assert.That(first.BigBlindSeat, Is.EqualTo(D));
            Assert.That(first.CurrentSeat, Is.EqualTo(B));

            Submit(session, B, BettingAction.Call());
            Submit(session, C, BettingAction.Call());
            Submit(session, D, BettingAction.Check());
            CheckStreet(session, C, D);
            CheckStreet(session, C, D);
            CheckStreet(session, C, D);

            HoldemSnapshot completed = session.GetSnapshot(A);
            Assert.That(completed.GetSeat(B).Stack, Is.Zero);
            Assert.That(completed.GetSeat(D).Stack, Is.EqualTo(13));
            Assert.That(completed.Result.OwnPayout, Is.Zero);
            Assert.That(completed.Result.OwnHandValue, Is.Null);
            Assert.That(completed.Result.OwnBestCardCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => completed.Result.GetOwnBestCard(0));
            Assert.That(session.FundedSeatCount, Is.EqualTo(2));
            Assert.That(session.CanContinue, Is.True);

            HoldemReceipt next = session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version);
            Assert.That(next.Accepted, Is.True);
            HoldemSnapshot headsUp = session.GetSnapshot(A);
            Assert.That(headsUp.ButtonSeat, Is.EqualTo(C));
            Assert.That(headsUp.SmallBlindSeat, Is.EqualTo(C));
            Assert.That(headsUp.BigBlindSeat, Is.EqualTo(D));
            Assert.That(headsUp.CurrentSeat, Is.EqualTo(C));
            Assert.That(headsUp.GetSeat(A).WasDealtIn, Is.False);
            Assert.That(headsUp.GetSeat(B).WasDealtIn, Is.False);
            Assert.That(headsUp.GetSeat(C).WasDealtIn, Is.True);
            Assert.That(headsUp.GetSeat(D).WasDealtIn, Is.True);
        }

        [Test]
        public void AcceptedCommandIdsCannotBeReassignedInALaterHand()
        {
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                A, B, new IdentityRandom());
            Guid firstStartId = Guid.NewGuid();
            Guid firstHandId = Guid.NewGuid();
            Assert.That(session.StartNextHand(firstHandId, firstStartId, 0).Accepted, Is.True);

            Guid firstActionId = Guid.NewGuid();
            HoldemReceipt folded = session.Submit(A, HoldemCommand.Act(session.SessionId,
                firstHandId, firstActionId, A, session.Version, BettingAction.Fold()));
            Assert.That(folded.Accepted, Is.True);

            Guid secondHandId = Guid.NewGuid();
            Assert.That(session.StartNextHand(secondHandId, Guid.NewGuid(), session.Version).Accepted, Is.True);
            long version = session.Version;
            Assert.That(session.StartNextHand(Guid.NewGuid(), firstStartId, version).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            SeatId actor = session.GetSnapshot(A).CurrentSeat.Value;
            HoldemCommand reassignedAction = HoldemCommand.Act(session.SessionId, secondHandId,
                firstActionId, actor, version, BettingAction.Fold());
            Assert.That(session.Submit(actor, reassignedAction).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.Version, Is.EqualTo(version));
        }

        private static void CheckStreet(HoldemSession session, SeatId first, SeatId second)
        {
            Submit(session, first, BettingAction.Check());
            Submit(session, second, BettingAction.Check());
        }

        private static void Start(HoldemSession session)
        {
            HoldemReceipt receipt = session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version);
            Assert.That(receipt.Accepted, Is.True, receipt.Error.ToString());
        }

        private static void Submit(HoldemSession session, SeatId seat, BettingAction action)
        {
            HoldemSnapshot snapshot = session.GetSnapshot(seat);
            HoldemReceipt receipt = session.Submit(seat, HoldemCommand.Act(session.SessionId,
                snapshot.HandId, Guid.NewGuid(), seat, session.Version, action));
            Assert.That(receipt.Accepted, Is.True, receipt.Error.ToString());
        }

        private static Card CardOf(string text)
        {
            const string ranks = "23456789TJQKA";
            const string suits = "cdhs";
            return new Card((Rank)(ranks.IndexOf(text[0]) + 2), (Suit)(suits.IndexOf(text[1]) + 1));
        }

        private sealed class IdentityRandom : IRandomSource
        {
            public int NextInt(int exclusiveMax) => exclusiveMax - 1;
        }

        private sealed class PrefixRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();

            public PrefixRandom(IReadOnlyList<Card> prefix)
            {
                var target = new List<Card>(prefix);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, Card.DeckSize).Select(Card.FromId)
                    .Where(card => !target.Contains(card)));
                Card[] working = Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).ToArray();
                for (int i = working.Length - 1; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1);
                    choices.Enqueue(selected);
                    Card saved = working[i]; working[i] = working[selected]; working[selected] = saved;
                }
            }

            public int NextInt(int exclusiveMax)
                => choices.Count > 0 ? choices.Dequeue() : exclusiveMax - 1;
        }
    }
}
