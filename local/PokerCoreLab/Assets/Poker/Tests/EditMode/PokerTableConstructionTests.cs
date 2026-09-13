using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Runtime;
using UnityEngine.UIElements;

namespace Poker.Foundation.Tests
{
    public sealed class PokerTableConstructionTests
    {
        [TestCase(3)][TestCase(4)][TestCase(5)]
        public void UnsupportedSeatCountIsRejectedBeforeChangingAnExistingRoot(int count)
        {
            var seats = Enumerable.Range(1, count).Select(value => new SeatId(value)).ToArray();
            var setup = new HandSetup(ChipLedger.Create(seats.Select(seat => new SeatChips(seat, 100)).ToArray()),
                seats, seats, seats, seats, 1, 2);
            var table = new LocalPokerTable(setup, seats[0], new FixedRandom());
            var input = new PokerInputController(table.Human);
            var root = new VisualElement(); var sentinel = new Label("Keep the team's existing UI");
            root.Add(sentinel); root.AddToClassList("existing-team-root");
            var before = input.View;
            PokerTableScreen created = null;
            try
            {
                Assert.Throws<InvalidOperationException>(() => created = new PokerTableScreen(root, input,
                    () => Assert.Fail("Construction must not start a new hand."), 100, null));
                Assert.That(root.childCount, Is.EqualTo(1));
                Assert.That(root[0], Is.SameAs(sentinel));
                Assert.That(root.ClassListContains("existing-team-root"), Is.True);
                Assert.That(root.ClassListContains("app"), Is.False);
                Assert.That(root.styleSheets.count, Is.Zero);
                Assert.That(input.View, Is.SameAs(before));
                Assert.That(input.IsPending, Is.False);
            }
            finally { created?.Dispose(); }
        }

        private sealed class FixedRandom : IRandomSource
        { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }

        [TestCase(0L)][TestCase(-1L)][TestCase(long.MinValue)]
        public void InvalidNextPracticeStackIsRejectedBeforeChangingAnExistingRoot(long startingStack)
        {
            SeatId[] seats = { new SeatId(1), new SeatId(2) };
            var setup = new HandSetup(ChipLedger.Create(seats.Select(seat => new SeatChips(seat, 100)).ToArray()),
                seats, seats, seats, seats, 1, 2);
            var table = new LocalPokerTable(setup, seats[0], new FixedRandom());
            var input = new PokerInputController(table.Human);
            var root = new VisualElement(); var sentinel = new Label("Keep the team's existing UI"); root.Add(sentinel);
            PokerTableScreen created = null;
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => created = new PokerTableScreen(root, input,
                    () => Assert.Fail("No restart during construction."), startingStack, null));
                Assert.That(root.childCount, Is.EqualTo(1));
                Assert.That(root[0], Is.SameAs(sentinel));
                Assert.That(root.ClassListContains("app"), Is.False);
                Assert.That(root.styleSheets.count, Is.Zero);
            }
            finally { created?.Dispose(); }
        }
    }
}
