using System;

namespace Poker.Foundation
{
    /// <summary>Heads-up payout result whose showdown comparison permits a shared community board.</summary>
    public sealed class HoldemSettlement
    {
        private readonly long firstAward;
        private readonly long secondAward;
        private readonly HoldemEvaluatedHand firstHand;
        private readonly HoldemEvaluatedHand secondHand;

        private HoldemSettlement(ChipLedger ledger, SeatId firstSeat, SeatId secondSeat,
            HoldemResultKind kind, long potAmount, long firstAward, long secondAward,
            SeatId? winnerSeat, SeatId? foldedSeat, HoldemEvaluatedHand firstHand,
            HoldemEvaluatedHand secondHand)
        {
            Ledger = ledger;
            FirstSeat = firstSeat;
            SecondSeat = secondSeat;
            Kind = kind;
            PotAmount = potAmount;
            this.firstAward = firstAward;
            this.secondAward = secondAward;
            WinnerSeat = winnerSeat;
            FoldedSeat = foldedSeat;
            this.firstHand = firstHand;
            this.secondHand = secondHand;
        }

        public ChipLedger Ledger { get; }
        public SeatId FirstSeat { get; }
        public SeatId SecondSeat { get; }
        public HoldemResultKind Kind { get; }
        public long PotAmount { get; }
        /// <summary>Null only for a tied showdown.</summary>
        public SeatId? WinnerSeat { get; }
        /// <summary>Set only when the hand ended by fold.</summary>
        public SeatId? FoldedSeat { get; }

        public long GetAwardedTo(SeatId seat)
        {
            if (seat == FirstSeat) return firstAward;
            if (seat == SecondSeat) return secondAward;
            throw new ArgumentException("The seat is not part of this heads-up result.", nameof(seat));
        }

        public HandValue GetHandValue(SeatId seat) => GetEvaluatedHand(seat).Value;

        public Card GetBestCard(SeatId seat, int index) => GetEvaluatedHand(seat).GetBestCard(index);

        internal static HoldemSettlement Showdown(ChipLedger ledger, SeatId firstSeat, SeatId secondSeat,
            HoldemEvaluatedHand firstHand, HoldemEvaluatedHand secondHand)
        {
            RequireHeadsUpLedger(ledger, firstSeat, secondSeat);
            if (firstHand == null) throw new ArgumentNullException(nameof(firstHand));
            if (secondHand == null) throw new ArgumentNullException(nameof(secondHand));
            long firstPaid = ledger.GetChips(firstSeat).Committed;
            long secondPaid = ledger.GetChips(secondSeat).Committed;
            if (firstPaid <= 0 || firstPaid != secondPaid)
                throw new SettlementException(firstPaid == 0 && secondPaid == 0
                    ? SettlementFailure.NothingToAward : SettlementFailure.UnmatchedContribution);

            long pot = checked(firstPaid + secondPaid);
            long firstAward;
            long secondAward;
            SeatId? winner;
            int comparison = firstHand.Value.CompareTo(secondHand.Value);
            if (comparison > 0) { firstAward = pot; secondAward = 0; winner = firstSeat; }
            else if (comparison < 0) { firstAward = 0; secondAward = pot; winner = secondSeat; }
            else
            {
                // A matched heads-up pot is always even, so no odd-chip policy is needed.
                firstAward = firstPaid;
                secondAward = secondPaid;
                winner = null;
            }
            ChipLedger paid = ledger.Distribute(AwardsInLedgerOrder(ledger, firstSeat, firstAward, secondSeat, secondAward));
            return new HoldemSettlement(paid, firstSeat, secondSeat, HoldemResultKind.Showdown,
                pot, firstAward, secondAward, winner, null, firstHand, secondHand);
        }

        internal static HoldemSettlement AwardUncontested(ChipLedger ledger, SeatId firstSeat,
            SeatId secondSeat, SeatId foldedSeat)
        {
            RequireHeadsUpLedger(ledger, firstSeat, secondSeat);
            if (foldedSeat != firstSeat && foldedSeat != secondSeat)
                throw new ArgumentException("The folded seat is not part of this heads-up hand.", nameof(foldedSeat));
            SeatId winner = foldedSeat == firstSeat ? secondSeat : firstSeat;
            long firstPaid = ledger.GetChips(firstSeat).Committed;
            long secondPaid = ledger.GetChips(secondSeat).Committed;
            if (firstPaid <= 0 || firstPaid != secondPaid)
                throw new SettlementException(firstPaid == 0 && secondPaid == 0
                    ? SettlementFailure.NothingToAward : SettlementFailure.UnmatchedContribution);
            long pot = checked(firstPaid + secondPaid);
            long firstAward = winner == firstSeat ? pot : 0;
            long secondAward = winner == secondSeat ? pot : 0;
            ChipLedger paid = ledger.Distribute(AwardsInLedgerOrder(ledger, firstSeat, firstAward, secondSeat, secondAward));
            return new HoldemSettlement(paid, firstSeat, secondSeat, HoldemResultKind.Fold,
                pot, firstAward, secondAward, winner, foldedSeat, null, null);
        }

        private HoldemEvaluatedHand GetEvaluatedHand(SeatId seat)
        {
            if (Kind != HoldemResultKind.Showdown)
                throw new InvalidOperationException("Folded hands do not reveal private-card evaluations.");
            if (seat == FirstSeat) return firstHand;
            if (seat == SecondSeat) return secondHand;
            throw new ArgumentException("The seat is not part of this heads-up result.", nameof(seat));
        }

        private static void RequireHeadsUpLedger(ChipLedger ledger, SeatId firstSeat, SeatId secondSeat)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (!firstSeat.IsValid || !secondSeat.IsValid || firstSeat == secondSeat || ledger.SeatCount != 2)
                throw new ArgumentException("Settlement requires exactly two distinct valid seats.");
            ledger.GetChips(firstSeat);
            ledger.GetChips(secondSeat);
        }

        private static long[] AwardsInLedgerOrder(ChipLedger ledger, SeatId firstSeat, long first,
            SeatId secondSeat, long second)
        {
            var awards = new long[2];
            for (int i = 0; i < 2; i++)
            {
                SeatId seat = ledger.GetSeatAt(i);
                awards[i] = seat == firstSeat ? first : seat == secondSeat ? second
                    : throw new InvalidOperationException("Unexpected seat in a heads-up ledger.");
            }
            return awards;
        }
    }
}
