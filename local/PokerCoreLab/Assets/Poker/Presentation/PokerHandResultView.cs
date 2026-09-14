using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Presentation
{
    public enum HandCompletionReason { Uncontested, Showdown }

    /// <summary>Completed-hand public facts for an authorized participant, not instructions for the next hand.</summary>
    public sealed class PokerHandResultView
    {
        internal PokerHandResultView(Guid handId, long version, SeatId viewerSeat, HandCompletionReason reason,
            long totalAwarded, HandSeatResult[] seats, HandPotResult[] pots, HandRefund[] refunds, RevealedHandView[] revealedHands)
        {
            HandId = handId; Version = version; ViewerSeat = viewerSeat; Reason = reason; TotalAwarded = totalAwarded;
            Seats = Array.AsReadOnly((HandSeatResult[])seats.Clone());
            Pots = Array.AsReadOnly((HandPotResult[])pots.Clone());
            Refunds = Array.AsReadOnly((HandRefund[])refunds.Clone());
            RevealedHands = Array.AsReadOnly((RevealedHandView[])revealedHands.Clone());
        }
        public Guid HandId { get; }
        public long Version { get; }
        public SeatId ViewerSeat { get; }
        public HandCompletionReason Reason { get; }
        public long TotalAwarded { get; }
        /// <summary>Original deal order, not an inferred button/display order. Includes final zero-stack/folded seats.</summary>
        public IReadOnlyList<HandSeatResult> Seats { get; }
        public IReadOnlyList<HandPotResult> Pots { get; }
        /// <summary>At most one unmatched refund per betting street; amounts are already reflected in the outcome.</summary>
        public IReadOnlyList<HandRefund> Refunds { get; }
        /// <summary>Completed showdown only. All remaining hands; never folded or uncontested hands.</summary>
        public IReadOnlyList<RevealedHandView> RevealedHands { get; }
    }

    /// <summary>Detached, explicitly public showdown cards. Never retains the authority's SeatHand.</summary>
    public sealed class RevealedHandView
    {
        internal RevealedHandView(SeatId seat, Card[] cards)
        {
            Seat = seat;
            Cards = Array.AsReadOnly((Card[])cards.Clone());
            Value = HandEvaluator.Evaluate(Cards);
        }
        public SeatId Seat { get; }
        public IReadOnlyList<Card> Cards { get; }
        public HandValue Value { get; }
    }
}
