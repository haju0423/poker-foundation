using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerPlayerViewTests
    {
        private static readonly SeatId A = new SeatId(40), B = new SeatId(7), C = new SeatId(91);

        [Test]
        public void NullSessionIsRejected() => Assert.Throws<ArgumentNullException>(() => View(null, A));

        [Test]
        public void InvalidViewerIsRejectedWithoutChangingTheHand()
        {
            var session = New(); var before = session.State; long version = session.Version;
            Assert.Throws<ArgumentException>(() => View(session, default));
            Assert.That(session.State, Is.SameAs(before)); Assert.That(session.Version, Is.EqualTo(version));
        }

        [Test]
        public void NonparticipantIsRejectedRatherThanGivenASpectatorView()
        {
            var session = New(); var before = session.State; long version = session.Version;
            Assert.Throws<KeyNotFoundException>(() => View(session, new SeatId(999)));
            Assert.That(session.State, Is.SameAs(before)); Assert.That(session.Version, Is.EqualTo(version));
        }

        [Test]
        public void BeforeStartIsNotAnEmptyPlayableHand()
        {
            var rng = new TestRandom(); var session = new PokerHandSession(Guid.NewGuid(), Setup(100, 100, 100), rng);
            Assert.Throws<InvalidOperationException>(() => View(session, A));
            Assert.That(rng.Calls, Is.Zero); Assert.That(session.Version, Is.Zero); Assert.That(session.State, Is.Null);
        }

        [TestCase(40)]
        [TestCase(7)]
        [TestCase(91)]
        public void EveryParticipantGetsOnlyTheirOwnFiveCards(int id)
        {
            var session = New(); SeatId viewer = new SeatId(id);
            PokerPlayerView view = View(session, viewer);
            Assert.That(view.ViewerSeat, Is.EqualTo(viewer));
            Assert.That(view.OwnCards, Is.EqualTo(session.State.GetHand(viewer)));
            Assert.That(view.OwnCards, Has.Count.EqualTo(5));
            foreach (var seat in view.Seats.Where(s => s.Seat != viewer))
                Assert.That(view.OwnCards.Intersect(session.State.GetHand(seat.Seat)), Is.Empty);
            Assert.That(view.Seats.Select(s => s.Seat), Is.EqualTo(new[] { A, B, C }));
            Assert.That(view.HandId, Is.EqualTo(session.HandId)); Assert.That(view.Version, Is.EqualTo(1));
            Assert.That(view.PotAmount, Is.EqualTo(3)); Assert.That(view.CurrentBet, Is.EqualTo(2));
            Assert.That(view.TotalAwarded, Is.Null); Assert.That(view.Seats.All(s => s.Awarded == null), Is.True);
            Assert.That(view.Seats.Select(s => s.StreetContribution), Is.EqualTo(new long?[] { 0, 1, 2 }));
        }

        [Test]
        public void DealOrderIsPreservedWithoutSortingOrAssumingTableDirection()
        {
            var order = new[] { C, A, B }; var other = new[] { A, B, C };
            var setup = new HandSetup(Ledger(100, 100, 100), order, other, other, other, 1, 2);
            var session = Start(setup); var view = View(session, A);
            Array.Reverse(order);
            Assert.That(view.Seats.Select(s => s.Seat), Is.EqualTo(new[] { C, A, B }));
            Assert.That(view.CurrentSeat, Is.EqualTo(A));
        }

        [Test]
        public void ActorOptionsAreCopiedFromCoreAndUnavailableToOthers()
        {
            var session = New(); var view = View(session, A); var legal = session.State.CurrentBetting.GetLegalActions();
            AssertOptions(view.Betting, legal);
            Assert.That(view.IsOwnTurn, Is.True); Assert.That(view.CanExchange, Is.False);
            Assert.That(view.MaxExchangeCount, Is.Zero);
            Assert.That(View(session, B).Betting, Is.Null); Assert.That(View(session, C).IsOwnTurn, Is.False);
            Act(session, BettingAction.Call());
            Assert.That(View(session, A).Betting, Is.Null);
            AssertOptions(View(session, B).Betting, session.State.CurrentBetting.GetLegalActions());
            Assert.That(View(session, B).Betting.CallAmount, Is.EqualTo(1));
            Assert.That(View(session, B).Betting.MinimumAggressiveTarget, Is.EqualTo(4));
            Assert.That(View(session, B).Seats.Single(s => s.Seat == B).StreetContribution, Is.EqualTo(1));
        }

        [Test]
        public void OldViewsAndReadOnlyCollectionsStayDetachedAfterProgress()
        {
            var session = New(); var old = View(session, A); var again = View(session, A);
            Assert.That(again.OwnCards, Is.Not.SameAs(old.OwnCards)); Assert.That(again.Seats, Is.Not.SameAs(old.Seats));
            Assert.That(again.Seats[0], Is.Not.SameAs(old.Seats[0])); Assert.That(again.Betting, Is.Not.SameAs(old.Betting));
            Assert.Throws<NotSupportedException>(() => ((IList<Card>)old.OwnCards)[0] = Card.FromId(51));
            Assert.Throws<NotSupportedException>(() => ((IList<PublicSeatView>)old.Seats)[0] = old.Seats[1]);
            Assert.That(old.OwnCards, Is.Not.InstanceOf<Card[]>()); Assert.That(old.Seats, Is.Not.InstanceOf<PublicSeatView[]>());
            Act(session, BettingAction.Call());
            Assert.That(old.Version, Is.EqualTo(1)); Assert.That(old.PotAmount, Is.EqualTo(3));
            Assert.That(old.Seats.Single(s => s.Seat == A).Stack, Is.EqualTo(100));
            Assert.That(old.IsOwnTurn, Is.True); Assert.That(old.Betting.CallAmount, Is.EqualTo(2));
            Assert.That(View(session, A).PotAmount, Is.EqualTo(5));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(5)]
        public void ExchangeShowsLatestOwnCardsAndOnlyCurrentActorCanConfirm(int count)
        {
            var session = New(); FinishBetting(session); var before = View(session, A);
            Assert.That(before.CanExchange, Is.True); Assert.That(before.MaxExchangeCount, Is.EqualTo(ExchangeRound.MaxExchangeCount));
            Assert.That(before.Betting, Is.Null); Assert.That(before.CurrentBet, Is.Null);
            Assert.That(before.Seats.All(s => s.StreetContribution == null), Is.True);
            Assert.That(View(session, B).CanExchange, Is.False); Assert.That(View(session, B).MaxExchangeCount, Is.Zero);
            Card[] original = before.OwnCards.ToArray(); Draw(session, count); var after = View(session, A);
            Assert.That(after.OwnCards, Is.EqualTo(session.State.GetHand(A)));
            Assert.That(before.OwnCards, Is.EqualTo(original));
            Assert.That(after.OwnCards.Intersect(original).Count(), Is.EqualTo(5 - count));
            Assert.That(after.CanExchange, Is.False); Assert.That(after.MaxExchangeCount, Is.Zero);
            Assert.That(View(session, B).CanExchange, Is.True);
        }

        [Test]
        public void FirstStreetFoldedSeatStillGetsOwnCardsButNoSecondStreetMembershipOrActions()
        {
            var session = New(); var hand = View(session, A).OwnCards.ToArray(); Act(session, BettingAction.Fold());
            FinishBetting(session); FinishExchange(session); var view = View(session, A);
            Assert.That(view.Phase, Is.EqualTo(HandPhase.SecondBetting));
            Assert.That(view.OwnCards, Is.EqualTo(hand)); Assert.That(view.Seats, Has.Count.EqualTo(3));
            Assert.That(view.Seats.Single(s => s.Seat == A).IsFolded, Is.True);
            Assert.That(view.Seats.Single(s => s.Seat == A).StreetContribution, Is.Null);
            Assert.That(view.Seats.Single(s => s.Seat == A).IsAllIn, Is.False);
            Assert.That(view.Betting, Is.Null); Assert.That(view.CanExchange, Is.False); Assert.That(view.IsOwnTurn, Is.False);
            Assert.That(View(session, B).Betting.CanBet, Is.True); Assert.That(View(session, B).Betting.CanRaise, Is.False);
            Assert.That(View(session, B).CurrentBet, Is.Zero);
        }

        [Test]
        public void SecondStreetFoldKeepsItsPublicPaymentButRemovesActions()
        {
            var session = New(); FinishBetting(session); FinishExchange(session);
            Act(session, BettingAction.BetTo(4)); Act(session, BettingAction.Call()); Act(session, BettingAction.RaiseTo(8));
            Act(session, BettingAction.Fold()); var view = View(session, A);
            Assert.That(view.Phase, Is.EqualTo(HandPhase.SecondBetting));
            Assert.That(view.Seats.Single(s => s.Seat == A).StreetContribution, Is.EqualTo(4));
            Assert.That(view.Seats.Single(s => s.Seat == A).Committed, Is.EqualTo(6));
            Assert.That(view.Seats.Single(s => s.Seat == A).IsFolded, Is.True); Assert.That(view.Betting, Is.Null);
        }

        [Test]
        public void AllInSeatCanDrawAndShortBlindIsNotHiddenAsFolded()
        {
            var session = Start(Setup(100, 1, 100)); FinishBetting(session); Draw(session, 0);
            var view = View(session, B);
            Assert.That(view.CanExchange, Is.True); Assert.That(view.MaxExchangeCount, Is.EqualTo(5));
            Assert.That(view.Seats.Single(s => s.Seat == B).IsAllIn, Is.True);
            Assert.That(view.Seats.Single(s => s.Seat == B).IsFolded, Is.False);
            Assert.That(view.Betting, Is.Null); Draw(session, 5); Draw(session, 0);
            Assert.That(View(session, B).CanExchange, Is.False); Assert.That(View(session, B).Betting, Is.Null);
        }

        [Test]
        public void OddChipWaitHasOutstandingPotNoPayoutNoActionsAndNoReveal()
        {
            var session = Tie(false); FinishTie(session);
            foreach (SeatId viewer in new[] { A, B, C })
            {
                var view = View(session, viewer);
                Assert.That(view.Phase, Is.EqualTo(HandPhase.AwaitingSettlementRule));
                Assert.That(view.Result, Is.Null);
                Assert.That(view.LastTransition.Value.AfterPhase, Is.EqualTo(HandPhase.AwaitingSettlementRule));
                Assert.Throws<InvalidOperationException>(() => PokerHandResultProjector.Create(session, viewer));
                Assert.That(view.PotAmount, Is.EqualTo(5)); Assert.That(view.TotalAwarded, Is.Null);
                Assert.That(view.CurrentSeat, Is.Null); Assert.That(view.Betting, Is.Null); Assert.That(view.CanExchange, Is.False);
                Assert.That(view.Seats.All(s => s.Awarded == null), Is.True);
                Assert.That(view.OwnCards, Is.EqualTo(session.State.GetHand(viewer)));
            }
        }

        [Test]
        public void ExplicitTestOnlyOddOrderProducesGrossPayoutsWithoutRevealingCards()
        {
            var session = Tie(true); FinishTie(session); var view = View(session, C);
            Assert.That(view.Phase, Is.EqualTo(HandPhase.Complete)); Assert.That(view.TotalAwarded, Is.EqualTo(5));
            Assert.That(view.Result.Pots.Single().Payouts.Single(p => p.Seat == A).HasOddChip, Is.True);
            Assert.That(view.Result.Pots.Single().Payouts.Single(p => p.Seat == A).Amount, Is.EqualTo(3));
            Assert.That(view.PotAmount, Is.Zero); Assert.That(view.Seats.Sum(s => s.Committed), Is.Zero);
            Assert.That(view.Seats.Single(s => s.Seat == A).Awarded, Is.EqualTo(3));
            Assert.That(view.Seats.Single(s => s.Seat == B).Awarded, Is.EqualTo(2));
            Assert.That(view.Seats.Single(s => s.Seat == C).Awarded, Is.Zero);
            Assert.That(view.OwnCards, Is.EqualTo(session.State.GetHand(C))); AssertNoActions(view);
        }

        [Test]
        public void UncontestedResultSeparatesRefundFromPayoutAndProfit()
        {
            var session = New(); Act(session, BettingAction.Fold()); Act(session, BettingAction.Fold());
            var view = View(session, C); var winner = view.Seats.Single(s => s.Seat == C);
            Assert.That(session.State.FirstBetting.RefundedAmount, Is.EqualTo(1));
            Assert.That(view.TotalAwarded, Is.EqualTo(2)); Assert.That(view.PotAmount, Is.Zero);
            Assert.That(winner.Awarded, Is.EqualTo(2)); Assert.That(winner.Stack - 100, Is.EqualTo(1));
            Assert.That(winner.IsAllIn, Is.False); AssertNoActions(view);
        }

        [Test]
        public void RefundedAllInExcessIsNotCountedAsAPotAward()
        {
            var session = Start(Setup(100, 40)); Act(session, BettingAction.RaiseTo(100)); Act(session, BettingAction.Call());
            Assert.That(View(session, A).PotAmount, Is.EqualTo(80));
            Assert.That(View(session, A).Seats.Single(s => s.Seat == A).Stack, Is.EqualTo(60));
            FinishExchange(session); var view = View(session, A);
            Assert.That(view.TotalAwarded, Is.EqualTo(80)); Assert.That(view.Seats.Sum(s => s.Awarded.Value), Is.EqualTo(80));
            Assert.That(view.Seats.All(s => !s.IsAllIn), Is.True); AssertNoActions(view);
        }

        [Test]
        public void SidePotTotalsAndSeatAwardsAreCopiedWithoutRecomputingWinners()
        {
            var session = Start(Setup(20, 50, 100)); Act(session, BettingAction.RaiseTo(20));
            Act(session, BettingAction.RaiseTo(50)); Act(session, BettingAction.Call()); FinishExchange(session);
            Assert.That(session.State.Settlement.PotCount, Is.EqualTo(2));
            var view = View(session, A); Assert.That(view.TotalAwarded, Is.EqualTo(120));
            foreach (var seat in view.Seats) Assert.That(seat.Awarded, Is.EqualTo(session.State.Settlement.GetAwardedTo(seat.Seat)));
            Assert.That(view.Seats.Sum(s => s.Awarded.Value), Is.EqualTo(view.TotalAwarded)); AssertNoActions(view);
        }

        [Test]
        public void AcceptedRetryKeepsOldReceiptButNewViewUsesLatestSessionVersion()
        {
            var session = New(); var command = HandCommand.Bet(session.HandId, Guid.NewGuid(), A, session.Version, BettingAction.Call());
            var receipt = session.Submit(A, command); FinishBetting(session); Draw(session, 5);
            var before = session.State; long version = session.Version;
            Assert.That(session.Submit(A, command), Is.SameAs(receipt)); var view = View(session, A);
            Assert.That(receipt.AppliedVersion, Is.LessThan(view.Version)); Assert.That(view.Version, Is.EqualTo(version));
            Assert.That(view.OwnCards, Is.EqualTo(before.GetHand(A))); Assert.That(view.CanExchange, Is.False);
            Assert.That(session.State, Is.SameAs(before));
        }

        [Test]
        public void ChangingOnlyHiddenOpponentCardsDoesNotChangeTheOpeningView()
        {
            Card[][] hands = TieHands(); Card[][] other = hands.Select(h => h.ToArray()).ToArray();
            Card swap = other[1][0]; other[1][0] = other[2][0]; other[2][0] = swap;
            Guid id = Guid.NewGuid(); var first = Start(Setup(100, 100, 100), new RiggedRandom(hands), id);
            var second = Start(Setup(100, 100, 100), new RiggedRandom(other), id);
            Assert.That(first.State.GetHand(B), Is.Not.EqualTo(second.State.GetHand(B)));
            Assert.That(Signature(View(first, A)), Is.EqualTo(Signature(View(second, A))));
        }

        [Test]
        public void PublicAndPrivateFieldSurfaceContainsOnlyTheDeclaredViewContract()
        {
            AssertSurface(typeof(PokerPlayerView), "HandId Version ViewerSeat Phase CurrentSeat IsOwnTurn OwnCards Seats PotAmount CurrentBet Betting CanExchange MaxExchangeCount TotalAwarded LastTransition Result");
            AssertSurface(typeof(PublicSeatView), "Seat Stack Committed StreetContribution IsFolded IsAllIn Awarded");
            AssertSurface(typeof(PlayerBettingOptions), "CanFold CanCheck CanCall CallAmount CanBet CanRaise MinimumAggressiveTarget MaximumAggressiveTarget");
            var allowed = new HashSet<Type> { typeof(Guid), typeof(long), typeof(long?), typeof(SeatId), typeof(SeatId?),
                typeof(HandPhase), typeof(bool), typeof(int), typeof(IReadOnlyList<Card>), typeof(IReadOnlyList<PublicSeatView>), typeof(PlayerBettingOptions), typeof(PublicHandTransition?), typeof(PokerHandResultView) };
            foreach (Type type in new[] { typeof(PokerPlayerView), typeof(PublicSeatView), typeof(PlayerBettingOptions) })
            {
                Assert.That(type.GetConstructors(), Is.Empty);
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                { Assert.That(allowed.Contains(field.FieldType), Is.True, type.Name + "." + field.Name); Assert.That(field.IsInitOnly, Is.True); }
            }
        }

        [Test]
        public void ExtremeChipValuesAreCopiedAsLongsWithoutOverflowOrFloatRounding()
        {
            var session = Start(Setup(long.MaxValue - 2, 2)); var view = View(session, A);
            Assert.That(view.Seats.Single(s => s.Seat == A).Stack, Is.EqualTo(long.MaxValue - 3));
            Assert.That(view.Betting.CallAmount, Is.EqualTo(1)); Assert.That(view.Betting.CanRaise, Is.False);
        }

        [Test]
        public void SeededTracesProjectEveryParticipantAcrossAllPhasesWithoutMutatingAuthority()
        {
            for (int seed = 0; seed < 60; seed++)
            {
                var session = Start(Setup(100, 100, 100), new TestRandom(seed)); int steps = 0;
                while (true)
                {
                    var state = session.State; long version = session.Version;
                    foreach (SeatId viewer in new[] { A, B, C })
                    {
                        var view = View(session, viewer);
                        Assert.That(view.Version, Is.EqualTo(version)); Assert.That(view.Phase, Is.EqualTo(state.Phase));
                        Assert.That(view.OwnCards, Is.EqualTo(state.GetHand(viewer))); Assert.That(view.PotAmount, Is.EqualTo(state.Ledger.TotalCommitted));
                        Assert.That(view.Seats.Sum(s => s.Stack) + view.PotAmount, Is.EqualTo(300));
                        if (state.CurrentBetting != null && state.CurrentSeat == viewer) AssertOptions(view.Betting, state.CurrentBetting.GetLegalActions());
                        else Assert.That(view.Betting, Is.Null);
                        Assert.That(view.CanExchange, Is.EqualTo(state.Phase == HandPhase.Exchange && state.CurrentSeat == viewer));
                    }
                    Assert.That(session.State, Is.SameAs(state)); Assert.That(session.Version, Is.EqualTo(version));
                    if (state.CurrentSeat == null) break;
                    Assert.That(++steps, Is.LessThan(30));
                    if (state.Phase == HandPhase.Exchange) Draw(session, (seed + steps) % 6);
                    else Act(session, state.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check());
                }
            }
        }

        private static void AssertOptions(PlayerBettingOptions view, LegalBettingActions legal)
        {
            Assert.That(view, Is.Not.Null); Assert.That(view.CanFold, Is.EqualTo(legal.CanFold));
            Assert.That(view.CanCheck, Is.EqualTo(legal.CanCheck)); Assert.That(view.CanCall, Is.EqualTo(legal.CanCall));
            Assert.That(view.CallAmount, Is.EqualTo(legal.CallAmount)); Assert.That(view.CanBet, Is.EqualTo(legal.CanBet));
            Assert.That(view.CanRaise, Is.EqualTo(legal.CanRaise)); Assert.That(view.MinimumAggressiveTarget, Is.EqualTo(legal.MinimumAggressiveTarget));
            Assert.That(view.MaximumAggressiveTarget, Is.EqualTo(legal.MaximumAggressiveTarget));
        }
        private static void AssertNoActions(PokerPlayerView view)
        {
            Assert.That(view.Phase, Is.EqualTo(HandPhase.Complete)); Assert.That(view.CurrentSeat, Is.Null);
            Assert.That(view.IsOwnTurn, Is.False); Assert.That(view.Betting, Is.Null); Assert.That(view.CanExchange, Is.False);
            Assert.That(view.MaxExchangeCount, Is.Zero); Assert.That(view.CurrentBet, Is.Null);
        }
        private static void AssertSurface(Type type, string names)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Assert.That(properties.Select(p => p.Name), Is.EquivalentTo(names.Split(' ')));
            Assert.That(properties.All(p => p.SetMethod == null), Is.True);
        }
        private static string Signature(object value)
        {
            if (value == null) return "null";
            if (value is System.Collections.IEnumerable items && !(value is string))
                return "[" + string.Join(",", items.Cast<object>().Select(Signature)) + "]";
            Type type = value.GetType();
            if (type.Namespace == "Poker.Presentation") return type.Name + "{" + string.Join(",", type.GetProperties()
                .OrderBy(p => p.Name).Select(p => p.Name + "=" + Signature(p.GetValue(value)))) + "}";
            return value.ToString();
        }
        private static PokerPlayerView View(PokerHandSession session, SeatId viewer) => PokerPlayerViewProjector.Create(session, viewer);
        private static ChipLedger Ledger(params long[] stacks) => ChipLedger.Create(stacks.Select((n, i) => new SeatChips(new[] { A, B, C }[i], n)).ToArray());
        private static HandSetup Setup(params long[] stacks)
        { var ledger = Ledger(stacks); var order = Enumerable.Range(0, stacks.Length).Select(ledger.GetSeatAt).ToArray(); return new HandSetup(ledger, order, order, order, order, 1, 2); }
        private static PokerHandSession New() => Start(Setup(100, 100, 100));
        private static PokerHandSession Start(HandSetup setup, IRandomSource random = null, Guid? id = null)
        { var session = new PokerHandSession(id ?? Guid.NewGuid(), setup, random ?? new TestRandom()); Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True); return session; }
        private static void Act(PokerHandSession session, BettingAction action)
        { SeatId seat = session.State.CurrentSeat.Value; Assert.That(session.Submit(seat, HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version, action)).Accepted, Is.True); }
        private static void Draw(PokerHandSession session, int count)
        { SeatId seat = session.State.CurrentSeat.Value; Assert.That(session.Submit(seat, HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, session.State.GetHand(seat).Take(count).ToArray())).Accepted, Is.True); }
        private static void FinishBetting(PokerHandSession session)
        { while (session.State.CurrentBetting != null) Act(session, session.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check()); }
        private static void FinishExchange(PokerHandSession session)
        { while (session.State.Phase == HandPhase.Exchange) Draw(session, 0); }
        private static Card[][] TieHands() => new[] {
            new[] { new Card(Rank.Ace, Suit.Clubs), new Card(Rank.King, Suit.Diamonds), new Card(Rank.Queen, Suit.Hearts), new Card(Rank.Jack, Suit.Spades), new Card(Rank.Nine, Suit.Clubs) },
            new[] { new Card(Rank.Ace, Suit.Diamonds), new Card(Rank.King, Suit.Clubs), new Card(Rank.Queen, Suit.Spades), new Card(Rank.Jack, Suit.Hearts), new Card(Rank.Nine, Suit.Diamonds) },
            new[] { new Card(Rank.Two, Suit.Clubs), new Card(Rank.Three, Suit.Diamonds), new Card(Rank.Four, Suit.Hearts), new Card(Rank.Five, Suit.Spades), new Card(Rank.Seven, Suit.Clubs) } };
        private static PokerHandSession Tie(bool explicitTestPriority)
        { var order = new[] { A, B, C }; return Start(new HandSetup(Ledger(100, 100, 100), order, new[] { A, C, B }, order, order, 1, 2, explicitTestPriority ? order : null), new RiggedRandom(TieHands())); }
        private static void FinishTie(PokerHandSession session)
        { Act(session, BettingAction.Call()); Act(session, BettingAction.Fold()); Act(session, BettingAction.Check()); FinishExchange(session); FinishBetting(session); }
        private sealed class TestRandom : IRandomSource
        { private readonly Random random; public int Calls; public TestRandom(int? seed = null) { random = seed.HasValue ? new Random(seed.Value) : null; } public int NextInt(int upper) { Calls++; return random?.Next(upper) ?? upper - 1; } }
        // Fixture-only deck construction. No production deck injection or certified random source is introduced.
        private sealed class RiggedRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public RiggedRandom(Card[][] hands)
            {
                var target = new List<Card>(); for (int i = 0; i < 5; i++) foreach (var hand in hands) target.Add(hand[i]);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)).ToArray());
                Card[] working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                { int j = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(j); Card swap = working[i]; working[i] = working[j]; working[j] = swap; }
            }
            public int NextInt(int upper) => choices.Dequeue();
        }
    }
}
