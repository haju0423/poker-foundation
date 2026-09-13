using System;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Public action facts only. Correlation command IDs stay in the requester's receipt.</summary>
    public readonly struct PublicHandTransition
    {
        internal PublicHandTransition(HandTransition source)
        {
            HandId = source.HandId; AppliedVersion = source.AppliedVersion; Seat = source.Seat;
            BeforePhase = source.BeforePhase; AfterPhase = source.AfterPhase; Kind = source.Kind;
            BettingAction = source.BettingAction; TargetTotal = source.TargetTotal;
            ChipsPaid = source.ChipsPaid; ExchangeCount = source.ExchangeCount;
            RefundedSeat = source.RefundedSeat; RefundedAmount = source.RefundedAmount;
        }
        public Guid HandId { get; }
        public long AppliedVersion { get; }
        public SeatId Seat { get; }
        public HandPhase BeforePhase { get; }
        public HandPhase AfterPhase { get; }
        public HandCommandKind Kind { get; }
        public BettingActionKind? BettingAction { get; }
        public long? TargetTotal { get; }
        public long ChipsPaid { get; }
        public int ExchangeCount { get; }
        public SeatId? RefundedSeat { get; }
        public long RefundedAmount { get; }
    }
}
