using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class RuleBasedDrawOpponentTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);
        private static Card[] HighCard => Cards("2c", "5d", "8h", "Js", "Ac");
        private static Card[] Pair => Cards("9c", "9d", "4h", "7s", "Kc");
        private static Card[] TwoPair => Cards("Tc", "Td", "8h", "8s", "Ac");
        private readonly RuleBasedDrawOpponent bot = new RuleBasedDrawOpponent();

        [TestCase(false)]
        [TestCase(true)]
        public void StrongFreeActionUsesTheCorrectBetOrRaiseKind(bool afterDraw)
        {
            var session = Start(TwoPair);
            if (afterDraw) { ReachSecondStreet(session); Act(session, BettingAction.Check()); }
            else Act(session, BettingAction.Call());
            AssertChoice(session, afterDraw ? BettingActionKind.BetTo : BettingActionKind.RaiseTo, afterDraw ? 2 : 4);
        }

        [TestCase(0, BettingActionKind.Check)]
        [TestCase(100, BettingActionKind.BetTo)]
        public void BluffSettingCanDisableOrForceAnEligibleSmallProbe(int percent, BettingActionKind expected)
        {
            var policy = new RuleBasedDrawOpponent(new PracticeOpponentSettings(afterDrawBluffPercent: percent));
            var session = Start(HighCard); ReachSecondStreet(session); Act(session, BettingAction.Check());
            var view = View(session); var before = session.State;
            var choice = policy.Choose(view);
            Assert.That(choice.Action.Kind, Is.EqualTo(expected));
            Assert.That(choice.Action.Target, Is.EqualTo(percent == 100 ? 2 : 0));
            Assert.That(session.State, Is.SameAs(before));
            Assert.That(session.Submit(B, choice).Accepted, Is.True);
        }

        [Test]
        public void EvenForcedBluffDoesNotBetHighCardBeforeDrawOrAWeakPairAfterDraw()
        {
            var policy = new RuleBasedDrawOpponent(new PracticeOpponentSettings(afterDrawBluffPercent: 100));
            var first = Start(HighCard); Act(first, BettingAction.Call());
            Assert.That(policy.Choose(View(first)).Action.Kind, Is.EqualTo(BettingActionKind.Check));
            var second = Start(Pair); ReachSecondStreet(second); Act(second, BettingAction.Check());
            Assert.That(policy.Choose(View(second)).Action.Kind, Is.EqualTo(BettingActionKind.Check));
        }

        [TestCase(11, BettingActionKind.Check)]
        [TestCase(12, BettingActionKind.RaiseTo)]
        public void FreeRaiseChecksInsteadOfGoingBelowMinimumWhenBudgetIsSmall(long stack, BettingActionKind expected)
        {
            var session = Start(TwoPair, stack); Act(session, BettingAction.Call());
            AssertChoice(session, expected, expected == BettingActionKind.Check ? 0 : 4);
        }

        [TestCase(38, BettingActionKind.Check)]
        [TestCase(39, BettingActionKind.BetTo)]
        public void FreeBetChecksWhenHalfPotExceedsTheStreetBudget(long stack, BettingActionKind expected)
        {
            var session = Start(TwoPair, stack);
            Act(session, BettingAction.RaiseTo(10)); Act(session, BettingAction.Call());
            Exchange(session); Exchange(session); Act(session, BettingAction.Check());
            AssertChoice(session, expected, expected == BettingActionKind.Check ? 0 : 10);
        }

        [Test]
        public void ZeroAggressionBudgetDisablesBothValueBetsAndBluffs()
        {
            var policy = new RuleBasedDrawOpponent(new PracticeOpponentSettings(aggressionBankrollShare: 0m, afterDrawBluffPercent: 100));
            foreach (Card[] cards in new[] { HighCard, TwoPair })
            {
                var session = Start(cards); ReachSecondStreet(session); Act(session, BettingAction.Check());
                Assert.That(policy.Choose(View(session)).Action.Kind, Is.EqualTo(BettingActionKind.Check));
            }
        }

        [TestCase(false, BettingActionKind.Check)]
        [TestCase(true, BettingActionKind.BetTo)]
        public void BluffCountsOnlyNonfoldedPlayers(bool thirdFolds, BettingActionKind expected)
        {
            var third = new SeatId(3); var order = new[] { A, B, third };
            var setup = new HandSetup(ChipLedger.Create(order.Select(s => new SeatChips(s, 100)).ToArray()), order, order, order, order, 1, 2);
            Card[] remaining = Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !HighCard.Contains(c)).ToArray();
            var session = new PokerHandSession(Guid.NewGuid(), setup,
                new RiggedRandom(new[] { remaining.Take(5).ToArray(), HighCard, remaining.Skip(5).Take(5).ToArray() }));
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True);
            // With three seats, this opening order makes B the small blind and the third seat the big blind.
            Act(session, BettingAction.Call()); Act(session, BettingAction.Call());
            Act(session, thirdFolds ? BettingAction.Fold() : BettingAction.Check());
            while (session.State.Phase == HandPhase.Exchange) Exchange(session);
            Act(session, BettingAction.Check());
            var policy = new RuleBasedDrawOpponent(new PracticeOpponentSettings(afterDrawBluffPercent: 100));
            var choice = policy.Choose(View(session));
            Assert.That(choice.Action.Kind, Is.EqualTo(expected));
            Assert.That(session.Submit(B, choice).Accepted, Is.True);
        }

        [Test]
        public void DefaultVariationIsRepeatableAndIncludesChecksAndBluffsWithoutConsumingState()
        {
            var session = Start(HighCard, handId: Guid.Parse("90250000-0300-0000-0100-000000000001"));
            ReachSecondStreet(session); Act(session, BettingAction.Check());
            var view = View(session); var before = session.State; var seen = new HashSet<BettingActionKind>();
            for (int seed = 0; seed < 200; seed++)
            {
                var policy = new RuleBasedDrawOpponent(PracticeOpponentSettings.Default, seed);
                var choice = policy.Choose(view); var again = policy.Choose(view);
                Assert.That(again.Action.Kind, Is.EqualTo(choice.Action.Kind));
                Assert.That(again.Action.Target, Is.EqualTo(choice.Action.Target));
                Assert.That(again.CommandId, Is.Not.EqualTo(choice.CommandId));
                Assert.That(before.CurrentBetting.GetLegalActions().Allows(choice.Action), Is.True);
                seen.Add(choice.Action.Kind);
            }
            Assert.That(seen, Is.EquivalentTo(new[] { BettingActionKind.Check, BettingActionKind.BetTo }));
            Assert.That(session.State, Is.SameAs(before));
        }

        [Test]
        public void DefaultRuntimeSeedVariesAcrossHandIdsButRepeatsForTheSameHand()
        {
            var seen = new HashSet<BettingActionKind>();
            for (int id = 1; id <= 200; id++)
            {
                var session = Start(HighCard, handId: new Guid(id, 1, 2, new byte[8]));
                ReachSecondStreet(session); Act(session, BettingAction.Check());
                var view = View(session); var before = session.State;
                var choice = bot.Choose(view); var again = bot.Choose(view);
                Assert.That(again.Action.Kind, Is.EqualTo(choice.Action.Kind));
                Assert.That(again.Action.Target, Is.EqualTo(choice.Action.Target));
                Assert.That(again.CommandId, Is.Not.EqualTo(choice.CommandId));
                Assert.That(session.State, Is.SameAs(before));
                seen.Add(choice.Action.Kind);
            }
            Assert.That(seen, Is.EquivalentTo(new[] { BettingActionKind.Check, BettingActionKind.BetTo }));
        }

        [Test]
        public void HiddenCardsCannotChangeASeededBluffWhenItsPublicDecisionKeyMatches()
        {
            Guid id = Guid.Parse("aa000000-0000-0000-0000-000000000001");
            var first = Start(HighCard, handId: id); var second = Start(HighCard, highHuman: true, handId: id);
            Assert.That(first.State.GetHand(A).ToArray(), Is.Not.EqualTo(second.State.GetHand(A).ToArray()));
            ReachSecondStreet(first); Act(first, BettingAction.Check());
            ReachSecondStreet(second); Act(second, BettingAction.Check());
            for (int seed = 0; seed < 100; seed++)
            {
                var policy = new RuleBasedDrawOpponent(PracticeOpponentSettings.Default, seed);
                var x = policy.Choose(View(first)); var y = policy.Choose(View(second));
                Assert.That(y.Action.Kind, Is.EqualTo(x.Action.Kind));
                Assert.That(y.Action.Target, Is.EqualTo(x.Action.Target));
            }
        }

        [TestCase(-0.01)]
        [TestCase(1.01)]
        public void SettingsRejectEveryOutOfRangeShare(double invalid)
        {
            decimal value = (decimal)invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(beforeDrawHighCardCallLimit: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(afterDrawHighCardCallLimit: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(beforeDrawPairCallLimit: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(afterDrawPairCallLimit: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(twoPairCallLimit: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(aggressionBankrollShare: value));
        }

        [TestCase(-1)]
        [TestCase(101)]
        public void SettingsRejectInvalidBluffPercent(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PracticeOpponentSettings(afterDrawBluffPercent: value));
        }

        [Test]
        public void SettingsAreImmutableAndNullPolicySettingsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new RuleBasedDrawOpponent(null));
            Assert.That(typeof(PracticeOpponentSettings).GetProperties().All(property => property.SetMethod == null), Is.True);
            var minimum = new PracticeOpponentSettings(0, 0, 0, 0, 0, 0, 0);
            var maximum = new PracticeOpponentSettings(1, 1, 1, 1, 1, 1, 100);
            Assert.That(minimum.AggressionBankrollShare, Is.Zero);
            Assert.That(maximum.AggressionBankrollShare, Is.EqualTo(1));
            Assert.That(PracticeOpponentSettings.Default.AfterDrawBluffPercent, Is.EqualTo(12));
        }

        [TestCase(4, BettingActionKind.Call)]
        [TestCase(6, BettingActionKind.Call)]
        [TestCase(7, BettingActionKind.Fold)]
        [TestCase(8, BettingActionKind.Fold)]
        public void HighCardRespondsToThePriceOfCalling(long target, BettingActionKind expected)
        {
            var session = Start(HighCard); Act(session, BettingAction.RaiseTo(target));
            AssertChoice(session, expected);
        }

        [TestCase(8, BettingActionKind.Call)]
        [TestCase(20, BettingActionKind.Call)]
        [TestCase(21, BettingActionKind.Fold)]
        public void PairCallsSmallPressureButCanFoldToLargePressure(long target, BettingActionKind expected)
        {
            var session = Start(Pair); Act(session, BettingAction.RaiseTo(target));
            AssertChoice(session, expected);
        }

        [Test]
        public void SameQuarterPotCallIsAcceptedBeforeDrawButRejectedAfterDrawWithHighCard()
        {
            var first = Start(HighCard); Act(first, BettingAction.RaiseTo(4));
            AssertChoice(first, BettingActionKind.Call);
            var second = Start(HighCard); ReachSecondStreet(second); Act(second, BettingAction.BetTo(2));
            AssertChoice(second, BettingActionKind.Fold);
        }

        [TestCase(4, BettingActionKind.Call)]
        [TestCase(5, BettingActionKind.Fold)]
        public void AfterDrawPairHasItsOwnCallPriceLimit(long target, BettingActionKind expected)
        {
            var session = Start(Pair); ReachSecondStreet(session);
            Act(session, BettingAction.BetTo(target));
            AssertChoice(session, expected);
        }

        [Test]
        public void TwoPairRaisesToTheCopiedMinimumTotalNotTheAdditionalCallAmount()
        {
            var session = Start(TwoPair); Act(session, BettingAction.RaiseTo(8));
            Assert.That(View(session).Betting.CallAmount, Is.EqualTo(6));
            AssertChoice(session, BettingActionKind.RaiseTo, 14);
        }

        [TestCase(80, BettingActionKind.RaiseTo, 28)]
        [TestCase(79, BettingActionKind.Call, 0)]
        public void RaiseBudgetIncludesAlreadyPaidStreetChipsAndHonorsItsBoundary(
            long stack, BettingActionKind expected, long target)
        {
            var session = Start(TwoPair, stack); Act(session, BettingAction.RaiseTo(15));
            AssertChoice(session, expected, target);
        }

        [Test]
        public void ReRaiseAboveBudgetFallsBackToCallInsteadOfClampingAnIllegalTarget()
        {
            var session = Start(TwoPair); Act(session, BettingAction.RaiseTo(8));
            AssertChoice(session, BettingActionKind.RaiseTo, 14);
            Act(session, BettingAction.RaiseTo(30));
            Assert.That(View(session).Betting.MinimumAggressiveTarget, Is.EqualTo(46));
            AssertChoice(session, BettingActionKind.Call);
        }

        [Test]
        public void ShortAllInRaiseOptionDoesNotForceAggressionPastBudget()
        {
            var session = Start(TwoPair, 5); Act(session, BettingAction.RaiseTo(4));
            Assert.That(View(session).Betting.MaximumAggressiveTarget, Is.EqualTo(5));
            AssertChoice(session, BettingActionKind.Call);
        }

        [Test]
        public void Int64SizedChipSupplyDoesNotOverflowTheBudget()
        {
            var session = Start(TwoPair, long.MaxValue / 2); Act(session, BettingAction.RaiseTo(8));
            AssertChoice(session, BettingActionKind.RaiseTo, 14);
            Assert.That(session.State.Ledger.TotalChips, Is.EqualTo(long.MaxValue - 1));
        }

        [Test]
        public void NullNonactingAndCompletedViewsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => bot.Choose(null));
            var session = Start(HighCard);
            Assert.Throws<InvalidOperationException>(() => bot.Choose(View(session)));
            Act(session, BettingAction.Fold());
            Assert.Throws<InvalidOperationException>(() => bot.Choose(View(session)));
        }

        [Test]
        public void ChoosingTwiceDoesNotSpendBudgetOrChangeTheHand()
        {
            var session = Start(TwoPair); Act(session, BettingAction.RaiseTo(8));
            var view = View(session); var state = session.State;
            var first = bot.Choose(view); var again = bot.Choose(view);
            Assert.That(again.Action.Kind, Is.EqualTo(first.Action.Kind));
            Assert.That(again.Action.Target, Is.EqualTo(first.Action.Target));
            Assert.That(again.CommandId, Is.Not.EqualTo(first.CommandId));
            Assert.That(session.State, Is.SameAs(state));
            Assert.That(view.Version, Is.EqualTo(session.Version));
        }

        [Test]
        public void HiddenHumanCardsDoNotChangeAnOtherwiseIdenticalDecision()
        {
            var first = Start(TwoPair); var second = Start(TwoPair, 100, true);
            Assert.That(first.State.GetHand(A).ToArray(), Is.Not.EqualTo(second.State.GetHand(A).ToArray()));
            Act(first, BettingAction.RaiseTo(8)); Act(second, BettingAction.RaiseTo(8));
            var x = bot.Choose(View(first)); var y = bot.Choose(View(second));
            Assert.That(y.Action.Kind, Is.EqualTo(x.Action.Kind));
            Assert.That(y.Action.Target, Is.EqualTo(x.Action.Target));
        }

        [Test]
        public void ExchangeReusesExistingSelectionAndOnlyDiscardsOwnedCards()
        {
            var session = Start(TwoPair); Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            Exchange(session);
            var view = View(session);
            var actual = bot.Choose(view); var expected = new SimpleDrawOpponent().Choose(view);
            Assert.That(actual.Kind, Is.EqualTo(HandCommandKind.Exchange));
            Assert.That(actual.SelectedCards, Is.EqualTo(expected.SelectedCards));
            Assert.That(actual.SelectedCards.All(c => view.OwnCards.Contains(c)), Is.True);
            Assert.That(session.Submit(B, actual).Accepted, Is.True);
        }

        [Test]
        public void DefaultTableUsesResponsivePolicyAndAdvancesExactlyOneAcceptedAction()
        {
            var table = new LocalPokerTable(Setup(100), A, RandomFor(TwoPair, false));
            var input = new PokerInputController(table.Human);
            Assert.That(table.AdvanceOpponent(), Is.False);
            Assert.That(input.Bet(BettingAction.RaiseTo(8)), Is.True);
            long before = input.View.Version;
            Assert.That(table.AdvanceOpponent(), Is.True); input.Refresh();
            Assert.That(input.View.Version, Is.EqualTo(before + 1));
            Assert.That(input.View.CurrentBet, Is.EqualTo(14));
            Assert.That(input.View.IsOwnTurn, Is.True);
            Assert.That(table.AdvanceOpponent(), Is.False);
            Assert.That(input.Bet(BettingAction.Call()), Is.True);
            for (int step = 0; input.View.CurrentSeat.HasValue; step++)
            {
                Assert.That(step, Is.LessThan(30));
                if (!input.View.IsOwnTurn) table.AdvanceOpponent();
                else if (input.View.CanExchange) input.Exchange();
                else input.Bet(input.View.Betting.CanCheck ? BettingAction.Check() : BettingAction.Call());
                input.Refresh();
            }
            Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(input.View.Seats.Sum(s => s.Stack), Is.EqualTo(200));
        }

        [Test]
        public void NullPolicyIsRejectedBeforeDealing()
        {
            var random = new CountingRandom();
            Assert.Throws<ArgumentNullException>(() => new LocalPokerTable(Setup(100), A, random, null));
            Assert.That(random.Calls, Is.Zero);
        }

        [Test]
        public void SeededResponseTracesFinishLegallyAndPreserveChipsAndCommandRetries()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                var session = new PokerHandSession(Guid.NewGuid(), Setup(10 + seed), new SeededRandom(seed));
                Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True);
                int steps = 0;
                while (session.State.CurrentSeat.HasValue)
                {
                    Assert.That(++steps, Is.LessThan(100), "Seed " + seed);
                    SeatId seat = session.State.CurrentSeat.Value;
                    var view = PokerPlayerViewProjector.Create(session, seat);
                    HandCommand command;
                    if (seat == B) command = bot.Choose(view);
                    else if (view.CanExchange)
                        command = HandCommand.Exchange(view.HandId, Guid.NewGuid(), seat, view.Version, view.OwnCards.Take(seed % 6).ToArray());
                    else
                    {
                        var legal = view.Betting;
                        BettingAction action = steps < 10 && seed % 3 != 0 && (legal.CanRaise || legal.CanBet)
                            ? (legal.CanRaise ? BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value) : BettingAction.BetTo(legal.MinimumAggressiveTarget.Value))
                            : (legal.CanCheck ? BettingAction.Check() : BettingAction.Call());
                        command = HandCommand.Bet(view.HandId, Guid.NewGuid(), seat, view.Version, action);
                    }
                    var receipt = session.Submit(seat, command);
                    Assert.That(receipt.Accepted, Is.True, "Seed " + seed + ", step " + steps);
                    long version = session.Version;
                    Assert.That(session.Submit(seat, command), Is.SameAs(receipt));
                    Assert.That(session.Version, Is.EqualTo(version));
                    Assert.That(session.State.Ledger.TotalChips, Is.EqualTo(2 * (10 + seed)));
                }
                Assert.That(session.State.Phase, Is.EqualTo(HandPhase.Complete), "Seed " + seed);
                Assert.That(session.State.Ledger.TotalCommitted, Is.Zero);
            }
        }

        private void AssertChoice(PokerHandSession session, BettingActionKind kind, long target = 0)
        {
            var view = View(session); var state = session.State; var cards = view.OwnCards.ToArray();
            HandCommand command = bot.Choose(view);
            Assert.That(command.HandId, Is.EqualTo(view.HandId));
            Assert.That(command.Seat, Is.EqualTo(B)); Assert.That(command.ExpectedVersion, Is.EqualTo(view.Version));
            Assert.That(command.CommandId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(command.Action.Kind, Is.EqualTo(kind)); Assert.That(command.Action.Target, Is.EqualTo(target));
            Assert.That(session.State, Is.SameAs(state)); Assert.That(view.OwnCards, Is.EqualTo(cards));
            Assert.That(state.CurrentBetting.GetLegalActions().Allows(command.Action), Is.True);
            Assert.That(session.Submit(B, command).Accepted, Is.True);
        }
        private static PokerPlayerView View(PokerHandSession session) => PokerPlayerViewProjector.Create(session, B);
        private static HandSetup Setup(long stack)
        {
            var order = new[] { A, B };
            return new HandSetup(ChipLedger.Create(new[] { new SeatChips(A, stack), new SeatChips(B, stack) }),
                order, order, order, order, 1, 2);
        }
        private static PokerHandSession Start(Card[] hand, long stack = 100, bool highHuman = false, Guid? handId = null)
        {
            var session = new PokerHandSession(handId ?? Guid.NewGuid(), Setup(stack), RandomFor(hand, highHuman));
            Assert.That(session.Start(new StartHandCommand(session.HandId, Guid.NewGuid())).Accepted, Is.True);
            return session;
        }
        private static RiggedRandom RandomFor(Card[] hand, bool highHuman)
        {
            IEnumerable<Card> remaining = Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !hand.Contains(c));
            if (highHuman) remaining = remaining.Reverse();
            return new RiggedRandom(new[] { remaining.Take(5).ToArray(), hand });
        }
        private static void Act(PokerHandSession session, BettingAction action)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            var receipt = session.Submit(seat, HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version, action));
            Assert.That(receipt.Accepted, Is.True, "Seat " + seat.Value + ", " + action.Kind + ": " + receipt.Error);
        }
        private static void Exchange(PokerHandSession session)
        {
            SeatId seat = session.State.CurrentSeat.Value;
            Assert.That(session.Submit(seat, HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, Array.Empty<Card>())).Accepted, Is.True);
        }
        private static void ReachSecondStreet(PokerHandSession session)
        {
            Act(session, BettingAction.Call()); Act(session, BettingAction.Check());
            Exchange(session); Exchange(session);
            Assert.That(session.State.Phase, Is.EqualTo(HandPhase.SecondBetting));
        }
        private static Card[] Cards(params string[] cards) => cards.Select(c =>
            new Card((Rank)("23456789TJQKA".IndexOf(c[0]) + 2), (Suit)("cdhs".IndexOf(c[1]) + 1))).ToArray();
        private sealed class CountingRandom : IRandomSource
        {
            public int Calls;
            public int NextInt(int upper) { Calls++; return upper - 1; }
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int upper) => random.Next(upper);
        }
        // Test-only Fisher-Yates choices. No production API allows a hand to be injected.
        private sealed class RiggedRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public RiggedRandom(Card[][] hands)
            {
                var target = new List<Card>();
                for (int i = 0; i < 5; i++) foreach (Card[] hand in hands) target.Add(hand[i]);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)).ToArray());
                Card[] working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1);
                    choices.Enqueue(selected);
                    Card swap = working[i]; working[i] = working[selected]; working[selected] = swap;
                }
            }
            public int NextInt(int upper) => choices.Dequeue();
        }
    }
}
