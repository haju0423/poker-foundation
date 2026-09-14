using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Presentation;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    /// <summary>Relabeling participants or suits must not change the same hand's decisions or money.</summary>
    public sealed class PokerEquivarianceTests
    {
        private static readonly int[] OriginalSeats = { 3, 17, 8, 61, 42 };
        private static readonly int[] OtherSeats = { int.MaxValue, 1, 73, 5, 2000000000 };

        [TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void AllSuitPermutationsAndSeatRelabelingPreserveWholeHandTraces(int count)
        {
            var phases = new HashSet<HandPhase>();
            var actions = new HashSet<BettingActionKind>();
            var draws = new HashSet<int>();
            int pairCount = 0, approved = 0;
            foreach (int[] suits in Permutations(new[] { 1, 2, 3, 4 }))
            for (int seed = 0; seed < 12; seed++)
            {
                var random = new System.Random(1301 * count + seed);
                SeatId[] seats = OriginalSeats.Take(count).Select(id => new SeatId(id)).ToArray();
                int[] renamed = Shuffle(OtherSeats.Take(count).ToArray(), random);
                var transform = new Relabel(seats, renamed, suits);
                long[] chips = seats.Select(_ => (long)random.Next(1, 101)).ToArray();
                SeatId[][] orders = Enumerable.Range(0, 4).Select(_ => Shuffle(seats, random)).ToArray();
                SeatId[] priority = seed % 2 == 0 ? Shuffle(seats, random) : null;
                var setup = Setup(seats, chips, orders, priority);
                var changed = Setup(seats.Select(transform.Seat).ToArray(), chips,
                    orders.Select(order => order.Select(transform.Seat).ToArray()).ToArray(),
                    priority?.Select(transform.Seat).ToArray());
                Card[] deck = Shuffle(Enumerable.Range(0, 52).Select(Card.FromId).ToArray(), new System.Random(seed * 31 + count));
                Guid handId = Id(count, seed, 0);
                var first = Start(handId, setup, deck);
                var second = Start(handId, changed, deck.Select(transform.Card).ToArray());
                int step = 0;
                while (true)
                {
                    phases.Add(first.State.Phase);
                    AssertPair(first, second, transform, seats);
                    if (!first.State.CurrentSeat.HasValue) break;
                    Assert.That(++step, Is.LessThan(200), "Count " + count + ", seed " + seed);
                    SeatId actor = first.State.CurrentSeat.Value;
                    HandCommand command;
                    if (first.State.Phase == HandPhase.Exchange)
                    {
                        int drawCount = seed < 6 ? seed : random.Next(6);
                        Card[] selection = Shuffle(first.State.GetHand(actor).ToArray(), random).Take(drawCount).ToArray();
                        draws.Add(selection.Length);
                        command = HandCommand.Exchange(handId, Id(count, seed, step), actor, first.Version, selection);
                    }
                    else
                    {
                        var legal = first.State.CurrentBetting.GetLegalActions();
                        var choices = Choices(legal);
                        // The first six fixtures explicitly cover draws 0..5 and a legal second-round opening bet.
                        BettingAction action = seed < 6
                            ? (legal.CanBet ? BettingAction.BetTo(legal.MinimumAggressiveTarget.Value)
                                : legal.CanCall ? BettingAction.Call() : BettingAction.Check())
                            : choices[random.Next(choices.Count)];
                        actions.Add(action.Kind);
                        command = HandCommand.Bet(handId, Id(count, seed, step), actor, first.Version, action);
                    }
                    HandCommand mapped = transform.Command(command);
                    var receipt = first.Submit(actor, command);
                    var mappedReceipt = second.Submit(transform.Seat(actor), mapped);
                    Assert.That(receipt.Accepted, Is.True, "Count " + count + ", seed " + seed + ", step " + step);
                    AssertReceipt(receipt, mappedReceipt, transform);
                    Assert.That(first.Submit(actor, command), Is.SameAs(receipt));
                    Assert.That(second.Submit(transform.Seat(actor), mapped), Is.SameAs(mappedReceipt));
                    approved++;

                    // A changed payload under the old ID must remain a conflict in either labeling.
                    var conflict = command.Kind == HandCommandKind.Exchange || command.Action.Kind != BettingActionKind.Fold
                        ? HandCommand.Bet(handId, command.CommandId, actor, command.ExpectedVersion, BettingAction.Fold())
                        : HandCommand.Bet(handId, command.CommandId, actor, command.ExpectedVersion, BettingAction.Call());
                    PokerHandState saved = first.State, mappedSaved = second.State;
                    var rejected = first.Submit(actor, conflict);
                    var mappedRejected = second.Submit(transform.Seat(actor), transform.Command(conflict));
                    Assert.That(rejected.Accepted, Is.False);
                    Assert.That(rejected.Error, Is.EqualTo(HandError.CommandConflict));
                    AssertReceipt(rejected, mappedRejected, transform);
                    Assert.That(first.State, Is.SameAs(saved));
                    Assert.That(second.State, Is.SameAs(mappedSaved));
                }
                Assert.That(first.State.Phase, Is.EqualTo(HandPhase.Complete).Or.EqualTo(HandPhase.AwaitingSettlementRule));
                pairCount++;
            }
            Assert.That(pairCount, Is.EqualTo(288));
            Assert.That(phases, Does.Contain(HandPhase.FirstBetting));
            Assert.That(phases, Does.Contain(HandPhase.Exchange));
            Assert.That(phases, Does.Contain(HandPhase.SecondBetting));
            Assert.That(phases, Does.Contain(HandPhase.Complete));
            Assert.That(actions, Is.EquivalentTo(Enum.GetValues(typeof(BettingActionKind)).Cast<BettingActionKind>()));
            Assert.That(draws, Is.EquivalentTo(Enumerable.Range(0, 6)));
            TestContext.WriteLine("M1 count=" + count + "; pairs=" + pairCount + "; approved=" + approved);
        }

        [TestCase(false)][TestCase(true)]
        public void TiedOddPotKeepsPendingOrTheExplicitPriorityUnderEverySuitPermutation(bool explicitPriority)
        {
            SeatId[] seats = OriginalSeats.Take(3).Select(id => new SeatId(id)).ToArray();
            SeatId[] opening = { seats[0], seats[2], seats[1] };
            SeatId[][] orders = { seats, opening, seats, seats.Reverse().ToArray() };
            SeatId[] priority = explicitPriority ? new[] { seats[1], seats[0], seats[2] } : null;
            int[][] hands = { new[] { 0, 4, 8, 12, 16 }, new[] { 1, 5, 9, 13, 17 }, new[] { 2, 7, 22, 31, 42 } };
            var ids = new List<int>();
            for (int slot = 0; slot < 5; slot++) foreach (int[] hand in hands) ids.Add(hand[slot]);
            ids.AddRange(Enumerable.Range(0, 52).Where(id => !ids.Contains(id)).ToArray());
            Card[] deck = ids.Select(Card.FromId).ToArray();
            foreach (int[] suits in Permutations(new[] { 1, 2, 3, 4 }))
            {
                var map = new Relabel(seats, OtherSeats.Take(3).ToArray(), suits);
                Guid id = Id(3, 100, explicitPriority ? 1 : 0);
                var first = Start(id, Setup(seats, new long[] { 100, 100, 100 }, orders, priority), deck);
                var second = Start(id, Setup(seats.Select(map.Seat).ToArray(), new long[] { 100, 100, 100 },
                    orders.Select(order => order.Select(map.Seat).ToArray()).ToArray(), priority?.Select(map.Seat).ToArray()),
                    deck.Select(map.Card).ToArray());
                int step = 0;
                while (first.State.CurrentSeat.HasValue)
                {
                    AssertPair(first, second, map, seats);
                    Assert.That(++step, Is.LessThan(20));
                    SeatId actor = first.State.CurrentSeat.Value;
                    HandCommand command;
                    if (first.State.Phase == HandPhase.Exchange)
                        command = HandCommand.Exchange(id, Id(3, 100, step + 2), actor, first.Version, Array.Empty<Card>());
                    else
                    {
                        var legal = first.State.CurrentBetting.GetLegalActions();
                        BettingAction action = actor == seats[2] ? BettingAction.Fold()
                            : legal.CanCall ? BettingAction.Call() : BettingAction.Check();
                        command = HandCommand.Bet(id, Id(3, 100, step + 2), actor, first.Version, action);
                    }
                    var a = first.Submit(actor, command);
                    Assert.That(a.Accepted, Is.True);
                    AssertReceipt(a, second.Submit(map.Seat(actor), map.Command(command)), map);
                }
                AssertPair(first, second, map, seats);
                Assert.That(first.State.Phase, Is.EqualTo(explicitPriority ? HandPhase.Complete : HandPhase.AwaitingSettlementRule));
                if (explicitPriority)
                {
                    Assert.That(first.State.Ledger.GetChips(seats[0]).Stack, Is.EqualTo(100));
                    Assert.That(first.State.Ledger.GetChips(seats[1]).Stack, Is.EqualTo(101));
                    Assert.That(first.State.Ledger.GetChips(seats[2]).Stack, Is.EqualTo(99));
                }
                else Assert.That(first.State.Ledger.TotalCommitted, Is.EqualTo(5));
            }
        }

        private static void AssertPair(PokerHandSession first, PokerHandSession second, Relabel map, SeatId[] seats)
        {
            Assert.That(second.Version, Is.EqualTo(first.Version));
            Assert.That(second.State.Phase, Is.EqualTo(first.State.Phase));
            Assert.That(second.State.RemainingCardCount, Is.EqualTo(first.State.RemainingCardCount));
            Assert.That(second.State.DiscardedCardCount, Is.EqualTo(first.State.DiscardedCardCount));
            Assert.That(second.State.Ledger.TotalChips, Is.EqualTo(first.State.Ledger.TotalChips));
            Assert.That(first.State.RemainingCardCount + first.State.DiscardedCardCount + seats.Length * 5, Is.EqualTo(52));
            foreach (SeatId seat in seats)
            {
                Assert.That(second.State.GetHand(map.Seat(seat)), Is.EqualTo(first.State.GetHand(seat).Select(map.Card)));
                Assert.That(second.State.IsFolded(map.Seat(seat)), Is.EqualTo(first.State.IsFolded(seat)));
                Assert.That(second.State.Ledger.GetChips(map.Seat(seat)).Stack, Is.EqualTo(first.State.Ledger.GetChips(seat).Stack));
                Assert.That(second.State.Ledger.GetChips(map.Seat(seat)).Committed, Is.EqualTo(first.State.Ledger.GetChips(seat).Committed));
                var original = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(first, seat));
                var changed = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(second, map.Seat(seat)));
                Normalize(original, value => value, card => card);
                Normalize(changed, map.Unseat, map.Uncard);
                Assert.That(JsonUtility.ToJson(changed), Is.EqualTo(JsonUtility.ToJson(original)));
            }
        }

        private static void AssertReceipt(HandReceipt first, HandReceipt second, Relabel map)
        {
            var a = PokerWireMapper.ToWire(first); var b = PokerWireMapper.ToWire(second);
            b.seat = map.Unseat(b.seat);
            foreach (var row in b.transition) NormalizeTransition(row, map.Unseat);
            Assert.That(JsonUtility.ToJson(b), Is.EqualTo(JsonUtility.ToJson(a)));
        }

        private static void Normalize(PokerWireSnapshot view, Func<int, int> seat, Func<int, int> card)
        {
            view.viewerSeat = seat(view.viewerSeat); view.currentSeat = seat(view.currentSeat);
            view.ownCards = view.ownCards.Select(card).ToArray();
            foreach (var row in view.seats) row.seat = seat(row.seat);
            foreach (var row in view.lastTransition) NormalizeTransition(row, seat);
            foreach (var result in view.result)
            {
                result.viewerSeat = seat(result.viewerSeat);
                foreach (var row in result.seats) row.seat = seat(row.seat);
                foreach (var refund in result.refunds) refund.seat = seat(refund.seat);
                foreach (var hand in result.revealedHands)
                { hand.seat = seat(hand.seat); hand.cards = hand.cards.Select(card).ToArray(); }
                foreach (var pot in result.pots)
                {
                    // Preserve positions: relabeling IDs is not permission to change the visible order.
                    pot.eligibleSeats = pot.eligibleSeats.Select(seat).ToArray();
                    foreach (var payout in pot.payouts) payout.seat = seat(payout.seat);
                }
            }
        }

        private static void NormalizeTransition(PokerWireTransition value, Func<int, int> seat)
        { value.seat = seat(value.seat); value.refundedSeat = seat(value.refundedSeat); }

        private static List<BettingAction> Choices(LegalBettingActions legal)
        {
            var result = new List<BettingAction>();
            if (legal.CanFold) result.Add(BettingAction.Fold());
            if (legal.CanCheck) result.Add(BettingAction.Check());
            if (legal.CanCall) result.Add(BettingAction.Call());
            if (legal.CanBet) { result.Add(BettingAction.BetTo(legal.MinimumAggressiveTarget.Value)); result.Add(BettingAction.BetTo(legal.MaximumAggressiveTarget.Value)); }
            if (legal.CanRaise) { result.Add(BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value)); result.Add(BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)); }
            return result;
        }

        private static HandSetup Setup(SeatId[] seats, long[] chips, SeatId[][] orders, SeatId[] priority)
            => new HandSetup(ChipLedger.Create(seats.Select((seat, i) => new SeatChips(seat, chips[i])).ToArray()),
                orders[0], orders[1], orders[2], orders[3], 1, 2, priority);

        private static PokerHandSession Start(Guid id, HandSetup setup, Card[] deck)
        {
            var random = new ExactDeck(deck);
            var session = new PokerHandSession(id, setup, random);
            Assert.That(session.Start(new StartHandCommand(id, Id(99, 99, 99))).Accepted, Is.True);
            Assert.That(random.RemainingChoices, Is.Zero);
            return session;
        }

        private static T[] Shuffle<T>(T[] values, System.Random random)
        {
            var copy = (T[])values.Clone();
            for (int i = copy.Length - 1; i > 0; i--) { int j = random.Next(i + 1); T old = copy[i]; copy[i] = copy[j]; copy[j] = old; }
            return copy;
        }

        private static IEnumerable<int[]> Permutations(int[] values)
        {
            if (values.Length == 0) { yield return Array.Empty<int>(); yield break; }
            foreach (int first in values)
            foreach (int[] tail in Permutations(values.Where(value => value != first).ToArray()))
                yield return new[] { first }.Concat(tail).ToArray();
        }

        private static Guid Id(int a, int b, int c)
        {
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(a), 0, bytes, 0, 4);
            Array.Copy(BitConverter.GetBytes(b), 0, bytes, 4, 4);
            Array.Copy(BitConverter.GetBytes(c), 0, bytes, 8, 4);
            bytes[15] = 1; return new Guid(bytes);
        }

        private sealed class Relabel
        {
            private readonly Dictionary<int, int> forward, inverse;
            private readonly int[] suits, inverseSuits;
            public Relabel(SeatId[] original, int[] renamed, int[] suitPermutation)
            {
                forward = original.Select((seat, i) => new { seat = seat.Value, value = renamed[i] }).ToDictionary(pair => pair.seat, pair => pair.value);
                inverse = forward.ToDictionary(pair => pair.Value, pair => pair.Key);
                suits = suitPermutation; inverseSuits = new int[4];
                for (int i = 0; i < 4; i++) inverseSuits[suits[i] - 1] = i + 1;
            }
            public SeatId Seat(SeatId seat) => new SeatId(forward[seat.Value]);
            public int Unseat(int seat) => seat == 0 ? 0 : inverse[seat];
            public Card Card(Card card) => new Card(card.Rank, (Suit)suits[(int)card.Suit - 1]);
            public int Uncard(int id)
            { var card = Foundation.Card.FromId(id); return new Card(card.Rank, (Suit)inverseSuits[(int)card.Suit - 1]).Id; }
            public HandCommand Command(HandCommand command) => command.Kind == HandCommandKind.Exchange
                ? HandCommand.Exchange(command.HandId, command.CommandId, Seat(command.Seat), command.ExpectedVersion, command.SelectedCards.Select(Card).ToArray())
                : HandCommand.Bet(command.HandId, command.CommandId, Seat(command.Seat), command.ExpectedVersion, command.Action);
        }

        // Test-only inverse Fisher-Yates choices; no deck injection hook is added to production.
        private sealed class ExactDeck : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public ExactDeck(Card[] deck)
            {
                Assert.That(deck.Select(card => card.Id).OrderBy(id => id), Is.EqualTo(Enumerable.Range(0, 52)));
                int[] working = Enumerable.Range(0, 52).ToArray();
                for (int i = 51; i > 0; i--)
                {
                    int j = Array.IndexOf(working, deck[i].Id, 0, i + 1);
                    Assert.That(j, Is.GreaterThanOrEqualTo(0)); choices.Enqueue(j);
                    int old = working[i]; working[i] = working[j]; working[j] = old;
                }
            }
            public int RemainingChoices => choices.Count;
            public int NextInt(int exclusiveMax)
            { Assert.That(exclusiveMax, Is.EqualTo(choices.Count + 1)); return choices.Dequeue(); }
        }
    }
}
