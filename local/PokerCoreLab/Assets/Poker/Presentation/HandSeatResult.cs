using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Public final chips for one participant. GrossAward is neither profit nor a refund.</summary>
    public readonly struct HandSeatResult
    {
        internal HandSeatResult(SeatId seat, long finalStack, long grossAward)
        { Seat = seat; FinalStack = finalStack; GrossAward = grossAward; }
        public SeatId Seat { get; }
        public long FinalStack { get; }
        public long GrossAward { get; }
    }

    /// <summary>A historical unmatched refund, already reflected in final stacks; never pay it again.</summary>
    public readonly struct HandRefund
    {
        internal HandRefund(HandPhase bettingPhase, SeatId seat, long amount)
        { BettingPhase = bettingPhase; Seat = seat; Amount = amount; }
        public HandPhase BettingPhase { get; }
        public SeatId Seat { get; }
        public long Amount { get; }
    }
}
