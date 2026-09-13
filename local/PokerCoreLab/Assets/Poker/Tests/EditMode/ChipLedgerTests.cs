using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public class ChipLedgerTests
    {
        private static readonly SeatId A = new SeatId(42);
        private static readonly SeatId B = new SeatId(7);
        private static readonly SeatId C = new SeatId(999);

        [TestCase(0L)]
        [TestCase(100L)]
        [TestCase(long.MaxValue)]
        public void StartingChipsPreserveOwnerAndNonnegativeIntegerStack(long amount)
        {
            var chips = new SeatChips(A, amount);
            Assert.That(chips.Owner, Is.EqualTo(A));
            Assert.That(chips.Stack, Is.EqualTo(amount));
            Assert.That(chips.Committed, Is.Zero);
        }

        [TestCase(-1L)]
        [TestCase(long.MinValue)]
        public void NegativeStartingStackIsRejected(long amount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SeatChips(A, amount));
        }

        [Test]
        public void InvalidOwnerIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new SeatChips(default, 100));
        }

        [Test]
        public void CreatePreservesSuppliedSeatOrderAndDifferentStacks()
        {
            ChipLedger ledger = ThreeSeats();
            Assert.That(ledger.SeatCount, Is.EqualTo(3));
            Assert.That(ledger.GetSeatAt(0), Is.EqualTo(A));
            Assert.That(ledger.GetSeatAt(1), Is.EqualTo(B));
            Assert.That(ledger.GetSeatAt(2), Is.EqualTo(C));
            AssertBalance(ledger, 175, new long[] { 100, 50, 25 }, new long[] { 0, 0, 0 });
        }

        [Test]
        public void CallerCanReplaceAndClearInputWithoutChangingLedger()
        {
            var input = new List<SeatChips> { new SeatChips(A, 100), new SeatChips(B, 50) };
            ChipLedger ledger = ChipLedger.Create(input);
            input[0] = new SeatChips(C, 500);
            input.Clear();
            Assert.That(ledger.GetSeatAt(0), Is.EqualTo(A));
            Assert.That(ledger.GetSeatAt(1), Is.EqualTo(B));
            AssertBalance(ledger, 150, new long[] { 100, 50 }, new long[] { 0, 0 });
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(6)]
        public void BookkeepingDoesNotChooseSupportedPlayerCount(int count)
        {
            var input = new SeatChips[count];
            for (int i = 0; i < count; i++) input[i] = new SeatChips(new SeatId(i + 1), 0);
            ChipLedger ledger = ChipLedger.Create(input);
            Assert.That(ledger.SeatCount, Is.EqualTo(count));
            AssertBalance(ledger, 0, new long[count], new long[count]);
        }

        [Test]
        public void NullEmptyNullEntryAndRepeatedSeatsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => ChipLedger.Create(null));
            Assert.Throws<ArgumentException>(() => ChipLedger.Create(Array.Empty<SeatChips>()));
            Assert.Throws<ArgumentException>(() => ChipLedger.Create(new SeatChips[] { new SeatChips(A, 1), null }));
            Assert.Throws<ArgumentException>(() => ChipLedger.Create(new[] { new SeatChips(A, 1), new SeatChips(A, 2) }));
        }

        [Test]
        public void LedgerCannotRestartUsingOutstandingContributionsAsStartingChips()
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 20);
            Assert.Throws<ArgumentException>(() => ChipLedger.Create(new[] { ledger.GetChips(A) }));
            AssertBalance(ledger, 175, new long[] { 80, 50, 25 }, new long[] { 20, 0, 0 });
        }

        [Test]
        public void TotalAboveInt64RangeIsRejectedWithoutPublishingLedger()
        {
            ChipLedger result = null;
            Assert.Throws<OverflowException>(() => result = ChipLedger.Create(new[] {
                new SeatChips(A, long.MaxValue), new SeatChips(B, 1) }));
            Assert.That(result, Is.Null);
        }

        [Test]
        public void ThrowingInputDoesNotPublishPartialLedgerOrAlterExistingOne()
        {
            ChipLedger existing = ThreeSeats();
            ChipLedger result = null;
            var failure = new ApplicationException("Test input read failure.");
            Assert.That(Assert.Throws<ApplicationException>(() => result = ChipLedger.Create(new ThrowingInput(failure))),
                Is.SameAs(failure));
            Assert.That(result, Is.Null);
            AssertBalance(existing, 175, new long[] { 100, 50, 25 }, new long[] { 0, 0, 0 });
        }

        [TestCase(1L)]
        [TestCase(40L)]
        [TestCase(100L)]
        public void ContributionMovesExactAmountAndKeepsOldSnapshots(long amount)
        {
            ChipLedger original = ThreeSeats();
            SeatChips oldChips = original.GetChips(A);
            ChipLedger next = original.Contribute(A, amount);
            Assert.That(next, Is.Not.SameAs(original));
            Assert.That(oldChips.Stack, Is.EqualTo(100));
            AssertBalance(original, 175, new long[] { 100, 50, 25 }, new long[] { 0, 0, 0 });
            AssertBalance(next, 175, new long[] { 100 - amount, 50, 25 }, new long[] { amount, 0, 0 });
        }

        [Test]
        public void ContributionAmountIsADeltaNotARaiseToTarget()
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 10).Contribute(A, 25);
            AssertBalance(ledger, 175, new long[] { 65, 50, 25 }, new long[] { 35, 0, 0 });
        }

        [Test]
        public void RefundMovesOnlyThatSeatsContributionBackAndCanRepeatPartially()
        {
            ChipLedger paid = ThreeSeats().Contribute(A, 100).Contribute(B, 30);
            ChipLedger partial = paid.RefundContribution(A, 60);
            ChipLedger restored = partial.RefundContribution(A, 40);
            AssertBalance(paid, 175, new long[] { 0, 20, 25 }, new long[] { 100, 30, 0 });
            AssertBalance(partial, 175, new long[] { 60, 20, 25 }, new long[] { 40, 30, 0 });
            AssertBalance(restored, 175, new long[] { 100, 20, 25 }, new long[] { 0, 30, 0 });
        }

        [Test]
        public void ReturnedChipsCanBeContributedAgainWithoutCreatingMoney()
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 100).RefundContribution(A, 60).Contribute(A, 60);
            AssertBalance(ledger, 175, new long[] { 0, 50, 25 }, new long[] { 100, 0, 0 });
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(long.MinValue)]
        public void ZeroAndNegativeMovementsAreRejectedAndDoNotMeanCheck(long amount)
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 40);
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Contribute(A, amount));
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.RefundContribution(A, amount));
            AssertBalance(ledger, 175, new long[] { 60, 50, 25 }, new long[] { 40, 0, 0 });
        }

        [TestCase(61L)]
        [TestCase(long.MaxValue)]
        public void InsufficientStackRejectsWholeContributionWithoutImplicitShortAllIn(long amount)
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 40);
            for (int attempt = 0; attempt < 3; attempt++)
                Assert.Throws<InvalidOperationException>(() => ledger.Contribute(A, amount));
            AssertBalance(ledger, 175, new long[] { 60, 50, 25 }, new long[] { 40, 0, 0 });
        }

        [TestCase(41L)]
        [TestCase(long.MaxValue)]
        public void RefundBeyondOwnContributionRejectsWholeRequest(long amount)
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 40).Contribute(B, 50);
            Assert.Throws<InvalidOperationException>(() => ledger.RefundContribution(A, amount));
            Assert.Throws<InvalidOperationException>(() => ledger.RefundContribution(C, 1));
            AssertBalance(ledger, 175, new long[] { 60, 0, 25 }, new long[] { 40, 50, 0 });
        }

        [Test]
        public void InvalidAndUnregisteredSeatsAreRejectedForEverySeatOperation()
        {
            ChipLedger ledger = ThreeSeats().Contribute(A, 40);
            Assert.Throws<ArgumentException>(() => ledger.GetChips(default));
            Assert.Throws<ArgumentException>(() => ledger.Contribute(default, 1));
            Assert.Throws<ArgumentException>(() => ledger.RefundContribution(default, 1));
            var unknown = new SeatId(1234);
            Assert.Throws<KeyNotFoundException>(() => ledger.GetChips(unknown));
            Assert.Throws<KeyNotFoundException>(() => ledger.Contribute(unknown, 1));
            Assert.Throws<KeyNotFoundException>(() => ledger.RefundContribution(unknown, 1));
            AssertBalance(ledger, 175, new long[] { 60, 50, 25 }, new long[] { 40, 0, 0 });
        }

        [TestCase(-1)]
        [TestCase(3)]
        [TestCase(int.MaxValue)]
        public void InvalidIndicesAreRejected(int index)
        {
            ChipLedger ledger = ThreeSeats();
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.GetSeatAt(index));
            AssertBalance(ledger, 175, new long[] { 100, 50, 25 }, new long[] { 0, 0, 0 });
        }

        [Test]
        public void Int64MaximumTotalCanBeFullyContributedAndRefunded()
        {
            ChipLedger original = ChipLedger.Create(new[] {
                new SeatChips(A, long.MaxValue - 1), new SeatChips(B, 1) });
            ChipLedger paid = original.Contribute(A, long.MaxValue - 1).Contribute(B, 1);
            AssertBalance(paid, long.MaxValue, new long[] { 0, 0 }, new long[] { long.MaxValue - 1, 1 });
            ChipLedger restored = paid.RefundContribution(B, 1).RefundContribution(A, long.MaxValue - 1);
            AssertBalance(restored, long.MaxValue, new long[] { long.MaxValue - 1, 1 }, new long[] { 0, 0 });
            AssertBalance(original, long.MaxValue, new long[] { long.MaxValue - 1, 1 }, new long[] { 0, 0 });
        }

        [Test]
        public void SingleMovementCanTransferTheWholeInt64Maximum()
        {
            ChipLedger original = ChipLedger.Create(new[] { new SeatChips(A, long.MaxValue) });
            ChipLedger paid = original.Contribute(A, long.MaxValue);
            AssertBalance(paid, long.MaxValue, new long[] { 0 }, new long[] { long.MaxValue });
            ChipLedger restored = paid.RefundContribution(A, long.MaxValue);
            AssertBalance(restored, long.MaxValue, new long[] { long.MaxValue }, new long[] { 0 });
        }

        [Test]
        public void CallerSuppliedBlindAmountsAreBookkeptWithoutBecomingDefaults()
        {
            ChipLedger ledger = ChipLedger.Create(new[] { new SeatChips(A, 100), new SeatChips(B, 100) });
            ledger = ledger.Contribute(A, 1).Contribute(B, 2);
            AssertBalance(ledger, 200, new long[] { 99, 98 }, new long[] { 1, 2 });
        }

        [Test]
        public void PrimitiveDoesNotDeduplicateCommandsOrChooseTheCurrentBranch()
        {
            ChipLedger original = ThreeSeats();
            ChipLedger first = original.Contribute(A, 10);
            ChipLedger repeated = first.Contribute(A, 10);
            ChipLedger branch = original.Contribute(B, 5);
            AssertBalance(first, 175, new long[] { 90, 50, 25 }, new long[] { 10, 0, 0 });
            AssertBalance(repeated, 175, new long[] { 80, 50, 25 }, new long[] { 20, 0, 0 });
            AssertBalance(branch, 175, new long[] { 100, 45, 25 }, new long[] { 0, 5, 0 });
        }

        [Test]
        public void DeterministicMovementSequenceMatchesIndependentDecimalBookkeeping()
        {
            ChipLedger ledger = ThreeSeats();
            var stacks = new decimal[] { 100, 50, 25 };
            var contributions = new decimal[] { 0, 0, 0 };
            var random = new Random(41021);
            for (int step = 0; step < 600; step++)
            {
                int index = random.Next(3);
                bool refund = random.Next(2) == 0;
                decimal available = refund ? contributions[index] : stacks[index];
                if (available == 0) continue;
                long amount = random.Next(1, (int)available + 1);
                ChipLedger previous = ledger;
                SeatChips previousSeat = previous.GetChips(previous.GetSeatAt(index));
                ledger = refund ? ledger.RefundContribution(ledger.GetSeatAt(index), amount)
                    : ledger.Contribute(ledger.GetSeatAt(index), amount);
                stacks[index] += refund ? amount : -amount;
                contributions[index] += refund ? -amount : amount;
                Assert.That(previous.GetChips(previous.GetSeatAt(index)), Is.SameAs(previousSeat));
                long[] expectedStacks = Array.ConvertAll(stacks, x => (long)x);
                long[] expectedContributions = Array.ConvertAll(contributions, x => (long)x);
                AssertBalance(ledger, 175, expectedStacks, expectedContributions);
                Assert.That(previousSeat.Stack + previousSeat.Committed,
                    Is.EqualTo(expectedStacks[index] + expectedContributions[index]));
            }
        }

        private static ChipLedger ThreeSeats() => ChipLedger.Create(new[] {
            new SeatChips(A, 100), new SeatChips(B, 50), new SeatChips(C, 25) });

        private static void AssertBalance(ChipLedger ledger, long total, long[] stacks, long[] contributions)
        {
            Assert.That(ledger.SeatCount, Is.EqualTo(stacks.Length));
            decimal stackSum = 0;
            decimal contributionSum = 0;
            for (int i = 0; i < stacks.Length; i++)
            {
                SeatId seat = ledger.GetSeatAt(i);
                SeatChips chips = ledger.GetChips(seat);
                Assert.That(chips.Owner, Is.EqualTo(seat));
                Assert.That(chips.Stack, Is.EqualTo(stacks[i]));
                Assert.That(chips.Committed, Is.EqualTo(contributions[i]));
                Assert.That(chips.Stack, Is.GreaterThanOrEqualTo(0));
                Assert.That(chips.Committed, Is.GreaterThanOrEqualTo(0));
                stackSum += chips.Stack;
                contributionSum += chips.Committed;
            }
            Assert.That(ledger.TotalChips, Is.EqualTo(total));
            Assert.That((decimal)ledger.TotalCommitted, Is.EqualTo(contributionSum));
            Assert.That(stackSum + contributionSum, Is.EqualTo((decimal)total));
        }

        private sealed class ThrowingInput : IReadOnlyList<SeatChips>
        {
            private readonly Exception failure;
            public ThrowingInput(Exception failure) { this.failure = failure; }
            public int Count => 2;
            public SeatChips this[int index] => index == 0 ? new SeatChips(A, 100) : throw failure;
            public IEnumerator<SeatChips> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
