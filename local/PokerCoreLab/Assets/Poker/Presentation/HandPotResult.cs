using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Copied public payout layer. Contains no evaluator values or authority objects.</summary>
    public sealed class HandPotResult
    {
        internal HandPotResult(PotAward source)
        {
            LowerBound = source.LowerBound; ContributionCap = source.ContributionCap; Amount = source.Amount;
            var seats = new SeatId[source.EligibleSeatCount];
            for (int i = 0; i < seats.Length; i++) seats[i] = source.GetEligibleSeat(i);
            EligibleSeats = Array.AsReadOnly(seats);
            var payouts = new SeatPayout[source.PayoutCount];
            for (int i = 0; i < payouts.Length; i++) payouts[i] = source.GetPayout(i);
            Payouts = Array.AsReadOnly(payouts);
        }
        public long LowerBound { get; }
        public long ContributionCap { get; }
        public long Amount { get; }
        public IReadOnlyList<SeatId> EligibleSeats { get; }
        public IReadOnlyList<SeatPayout> Payouts { get; }
    }
}
