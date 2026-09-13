using System;
using System.Linq;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class KoreanTableTextTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [Test]
        public void StatusUsesCurrentStreetTotalRatherThanStackOrLastAddedAmount()
        {
            var session = Start();
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("이번 베팅 총 1칩"));
            Act(session, BettingAction.RaiseTo(8));
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("이번 베팅 총 8칩"));
            Act(session, BettingAction.Call());
            Draw(session); Draw(session);
            Assert.That(Seat(session, A).Committed, Is.EqualTo(8));
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("이번 베팅 총 0칩"));
            Act(session, BettingAction.BetTo(4));
            Assert.That(Seat(session, A).Committed, Is.EqualTo(12));
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("이번 베팅 총 4칩"));
        }

        [Test]
        public void ExchangeDoesNotInventACurrentBetTotal()
        {
            var session = Start(); Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            Assert.That(Seat(session, A).StreetContribution, Is.Null);
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("참가 중"));
        }

        [Test]
        public void FoldLabelTakesPriorityOverPreviousContribution()
        {
            var session = Start(); Act(session, BettingAction.Fold());
            Assert.That(KoreanTableText.Status(Seat(session, A)), Is.EqualTo("폴드"));
        }

        [Test]
        public void AllInLabelRetainsKnownStreetTotalOnlyDuringBetting()
        {
            var session = Start(2);
            Assert.That(KoreanTableText.Status(Seat(session, B)), Is.EqualTo("올인 · 이번 베팅 총 2칩"));
            Act(session, BettingAction.Call());
            Assert.That(KoreanTableText.Status(Seat(session, B)), Is.EqualTo("올인"));
        }

        [Test]
        public void MissingSeatDoesNotProducePlausibleText()
            => Assert.Throws<ArgumentNullException>(() => KoreanTableText.Status(null));

        [Test]
        public void UnfinishedHandHasNoPayoutMessage()
            => Assert.That(KoreanTableText.Result(View(Start(), A)), Is.Empty);

        [Test]
        public void SoleRecipientShowsGrossAwardNotNetProfitOrRefund()
        {
            var session = Start(); Act(session, BettingAction.Fold());
            Assert.That(Seat(session, B).Stack, Is.EqualTo(101));
            Assert.That(Seat(session, B).Awarded, Is.EqualTo(2));
            Assert.That(KoreanTableText.Result(View(session, A)), Is.EqualTo("팟 지급 완료 · 상대에게 2칩 지급"));
            Assert.That(KoreanTableText.Result(View(session, B)), Is.EqualTo("팟 지급 완료 · 나에게 2칩 지급"));
        }

        [Test]
        public void EqualHandsListBothRecipientsWithoutClaimingTheirPrivateHandRanks()
        {
            var session = Start(); Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            Draw(session); Draw(session); Act(session, BettingAction.Check()); Act(session, BettingAction.Check());
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(KoreanTableText.Result(View(session, A)), Is.EqualTo("팟 지급 완료 · 나에게 2칩 지급 / 상대에게 2칩 지급"));
        }

        [Test]
        public void ResetReminderAppearsAtCompletionAndInOptInHelp()
        {
            var session = Start(); Act(session, BettingAction.Fold());
            Assert.That(KoreanTableText.Instruction(View(session, A), false), Does.Contain("칩이 초기화"));
            Assert.That(KoreanTableText.Rules, Does.Contain("상대 패는 아직 공개하지 않아요"));
            Assert.That(KoreanTableText.Rules, Does.Contain("칩이 초기화"));
        }

        private static PokerPlayerView View(PokerHandSession session, SeatId viewer) => PokerPlayerViewProjector.Create(session, viewer);
        private static PublicSeatView Seat(PokerHandSession session, SeatId seat) => View(session, A).Seats.Single(s => s.Seat == seat);
        private static PokerHandSession Start(long otherStack = 100)
        {
            var order = new[] { A, B };
            var ledger = ChipLedger.Create(new[] { new SeatChips(A, 100), new SeatChips(B, otherStack) });
            var session = new PokerHandSession(Guid.NewGuid(), new HandSetup(ledger, order, order, order, order, 1, 2), new IdentityRandom());
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True);
            return session;
        }
        private static void Act(PokerHandSession session, BettingAction action)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version, action)).Accepted, Is.True);
        }
        private static void Draw(PokerHandSession session)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, new Card[0])).Accepted, Is.True);
        }
        private sealed class IdentityRandom : IRandomSource
        { public int NextInt(int upper) => upper - 1; }
    }
}
