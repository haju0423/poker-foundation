using System;

namespace Poker.Foundation
{
    /// <summary>Card-free accepted-action facts for authority/requester. Project before sharing with other seats.</summary>
    public readonly struct HandTransition
    {
        private HandTransition(Guid handId, Guid commandId, long appliedVersion, SeatId seat,
            HandPhase beforePhase, HandPhase afterPhase, HandCommandKind kind,
            BettingActionKind? bettingAction, long? targetTotal, long chipsPaid, int exchangeCount,
            SeatId? refundedSeat, long refundedAmount)
        {
            HandId = handId; CommandId = commandId; AppliedVersion = appliedVersion; Seat = seat;
            BeforePhase = beforePhase; AfterPhase = afterPhase; Kind = kind;
            BettingAction = bettingAction; TargetTotal = targetTotal; ChipsPaid = chipsPaid;
            ExchangeCount = exchangeCount;
            RefundedSeat = refundedSeat; RefundedAmount = refundedAmount;
        }

        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long AppliedVersion { get; }
        public SeatId Seat { get; }
        public HandPhase BeforePhase { get; }
        public HandPhase AfterPhase { get; }
        public HandCommandKind Kind { get; }
        public BettingActionKind? BettingAction { get; }
        /// <summary>Street contribution target for BetTo/RaiseTo only; never the added amount.</summary>
        public long? TargetTotal { get; }
        /// <summary>Additional payment before any unmatched refund or settlement payout.</summary>
        public long ChipsPaid { get; }
        /// <summary>Public confirmed draw count. No exchanged card identities are retained.</summary>
        public int ExchangeCount { get; }
        /// <summary>Unmatched payment returned when this action closes its betting street.</summary>
        public SeatId? RefundedSeat { get; }
        public long RefundedAmount { get; }

        internal static HandTransition FromAccepted(HandCommand command, long version,
            PokerHandState before, PokerHandState after)
        {
            BettingActionKind? action = null;
            long? target = null;
            long paid = 0;
            int exchanged = 0;
            SeatId? refundedSeat = null;
            long refundedAmount = 0;
            if (command.Kind == HandCommandKind.Exchange)
                exchanged = command.SelectedCards.Count;
            else
            {
                action = command.Action.Kind;
                if (action == BettingActionKind.Call)
                    paid = before.CurrentBetting.GetLegalActions().CallAmount;
                else if (action == BettingActionKind.BetTo || action == BettingActionKind.RaiseTo)
                {
                    target = command.Action.Target;
                    paid = checked(target.Value - before.CurrentBetting.GetStreetContribution(command.Seat));
                }
                BettingRound finished = before.Phase == HandPhase.FirstBetting ? after.FirstBetting : after.SecondBetting;
                if (finished.IsComplete && finished.RefundedAmount > 0)
                {
                    refundedSeat = finished.RefundedSeat;
                    refundedAmount = finished.RefundedAmount;
                }
            }
            if (paid < 0) throw new InvalidOperationException("An accepted action cannot have a negative payment.");
            return new HandTransition(command.HandId, command.CommandId, version, command.Seat,
                before.Phase, after.Phase, command.Kind, action, target, paid, exchanged, refundedSeat, refundedAmount);
        }
    }
}
