using System;
using System.Linq;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerBetSizingTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2), C = new SeatId(3);

        [Test]
        public void NullViewIsNotAnEmptySuggestion() => Assert.Throws<ArgumentNullException>(() => PokerBetSizing.HalfPotTarget(null));

        [Test]
        public void InitialBlindCallIsIncludedAndSuggestionDoesNotMutateTheHand()
        {
            var session = Start(100, 100); var state = session.State; long version = session.Version;
            var view = View(session, A);
            Assert.That(view.PotAmount, Is.EqualTo(3)); Assert.That(view.Betting.CallAmount, Is.EqualTo(1));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(4));
            Assert.That(session.State, Is.SameAs(state)); Assert.That(session.Version, Is.EqualTo(version));
        }

        [Test]
        public void FacingRaiseUsesPostCallPotAndCurrentStreetTotalNotAdditionalPayment()
        {
            var session = Start(100, 100); Act(session, BettingAction.RaiseTo(8)); var view = View(session, B);
            Assert.That(view.PotAmount, Is.EqualTo(10)); Assert.That(view.Betting.CallAmount, Is.EqualTo(6));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(16)); // 2 paid + 6 call + (10 + 6) / 2
        }

        [Test]
        public void UnopenedSecondStreetUsesNoFirstStreetContributionInTheTarget()
        {
            var session = Start(100, 100); Act(session, BettingAction.RaiseTo(8)); Act(session, BettingAction.Call());
            FinishDraw(session); var view = View(session, A);
            Assert.That(view.PotAmount, Is.EqualTo(16)); Assert.That(view.Seats.First().StreetContribution, Is.Zero);
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(8));
        }

        [Test]
        public void AnOddPotRoundsHalfDownWithoutInventingAnOddChipSettlementRule()
        {
            var session = Start(100, 100, 100); Act(session, BettingAction.Call()); Act(session, BettingAction.Fold());
            Act(session, BettingAction.Check()); FinishDraw(session);
            Assert.That(View(session, A).PotAmount, Is.EqualTo(5));
            Assert.That(PokerBetSizing.HalfPotTarget(View(session, A)), Is.EqualTo(2));
        }

        [Test]
        public void ShortBigBlindNominalMinimumClampsTheSmallPotSuggestion()
        {
            var session = StartWithBlinds(1, 100, 1000, 1000, 1); var view = View(session, A);
            Assert.That(view.PotAmount, Is.EqualTo(2)); Assert.That(view.Betting.CallAmount, Is.EqualTo(100));
            Assert.That(view.Betting.MinimumAggressiveTarget, Is.EqualTo(200));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(200)); // raw suggestion would be 151
        }

        [Test]
        public void ShortAllInMaximumClampsAnOtherwiseLargerSuggestion()
        {
            var session = Start(100, 10); Act(session, BettingAction.RaiseTo(8)); var view = View(session, B);
            Assert.That(view.Betting.CanRaise, Is.True); Assert.That(view.Betting.MaximumAggressiveTarget, Is.EqualTo(10));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(10));
        }

        [Test]
        public void NoAggressivePermissionMeansNoSuggestionForObserverDrawCompleteOrLoneFundedSeat()
        {
            var session = Start(100, 100);
            Assert.That(PokerBetSizing.HalfPotTarget(View(session, B)), Is.Null);
            Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            Assert.That(PokerBetSizing.HalfPotTarget(View(session, A)), Is.Null);
            FinishDraw(session); Act(session, BettingAction.Fold());
            Assert.That(PokerBetSizing.HalfPotTarget(View(session, A)), Is.Null);
            session = Start(100, 2);
            Assert.That(View(session, A).Betting.CanRaise, Is.False);
            Assert.That(PokerBetSizing.HalfPotTarget(View(session, A)), Is.Null);
        }

        [Test]
        public void LargeOddValuesRemainExactBeyondDoubleIntegerPrecision()
        {
            const long stake = 9007199254740993; // 2^53 + 1
            var session = Start(3000000000000000000, 3000000000000000000, 3000000000000000000);
            Act(session, BettingAction.RaiseTo(stake)); Act(session, BettingAction.Call()); Act(session, BettingAction.Call());
            FinishDraw(session); var view = View(session, A);
            Assert.That(view.PotAmount, Is.EqualTo(27021597764222979));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(13510798882111489));
        }

        [Test]
        public void NearInt64TotalClampsBeforeConvertingTheSuggestionBackToChips()
        {
            long first = long.MaxValue / 3;
            var session = Start(first, first, long.MaxValue - 2 * first);
            long stake = first - 100;
            Act(session, BettingAction.RaiseTo(stake)); Act(session, BettingAction.Call()); Act(session, BettingAction.Call());
            FinishDraw(session); var view = View(session, A);
            Assert.That(view.Betting.MaximumAggressiveTarget, Is.EqualTo(100));
            Assert.That(PokerBetSizing.HalfPotTarget(view), Is.EqualTo(100));
            Assert.That(session.State.Ledger.TotalChips, Is.EqualTo(long.MaxValue));
        }

        private static PokerHandSession Start(params long[] stacks) => StartWithBlinds(1, 2, stacks);
        private static PokerHandSession StartWithBlinds(long sb, long bb, params long[] stacks)
        {
            var order = new[] { A, B, C }.Take(stacks.Length).ToArray();
            var ledger = ChipLedger.Create(stacks.Select((stack, i) => new SeatChips(order[i], stack)).ToArray());
            var session = new PokerHandSession(Guid.NewGuid(), new HandSetup(ledger, order, order, order, order, sb, bb), new FixedRandom());
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True); return session;
        }
        private static PokerPlayerView View(PokerHandSession session, SeatId viewer) => PokerPlayerViewProjector.Create(session, viewer);
        private static void Act(PokerHandSession session, BettingAction action)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version, action)).Accepted, Is.True);
        }
        private static void FinishDraw(PokerHandSession session)
        {
            while (session.State.Phase == HandPhase.Exchange)
            {
                SeatId seat = session.State.CurrentSeat.Value;
                Assert.That(session.Submit(seat, HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, Array.Empty<Card>())).Accepted, Is.True);
            }
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.SecondBetting));
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
    }
}
