using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>
    /// Detached, immutable view for exactly one authorized participant. Only OwnCards is private.
    /// Completed showdown cards are explicitly public through Result.RevealedHands; folded cards stay private.
    /// </summary>
    public sealed class PokerPlayerView
    {
        internal PokerPlayerView(Guid handId, long version, SeatId viewerSeat, HandPhase phase,
            SeatId? currentSeat, Card[] ownCards, PublicSeatView[] seats, long potAmount,
            long? currentBet, PlayerBettingOptions betting, bool canExchange, int maxExchangeCount,
            long? totalAwarded, PublicHandTransition? lastTransition, PokerHandResultView result)
        {
            HandId = handId; Version = version; ViewerSeat = viewerSeat; Phase = phase; CurrentSeat = currentSeat;
            OwnCards = Array.AsReadOnly((Card[])ownCards.Clone());
            Seats = Array.AsReadOnly((PublicSeatView[])seats.Clone());
            PotAmount = potAmount; CurrentBet = currentBet; Betting = betting;
            CanExchange = canExchange; MaxExchangeCount = maxExchangeCount; TotalAwarded = totalAwarded;
            LastTransition = lastTransition;
            Result = result;
        }

        public Guid HandId { get; }
        /// <summary>Latest state version at projection, not an old command receipt's AppliedVersion.</summary>
        public long Version { get; }
        public SeatId ViewerSeat { get; }
        public HandPhase Phase { get; }
        public SeatId? CurrentSeat { get; }
        public bool IsOwnTurn => CurrentSeat == ViewerSeat;
        public IReadOnlyList<Card> OwnCards { get; }
        /// <summary>Initial deal order, NOT an inferred clockwise/button/display order. Includes folded seats.</summary>
        public IReadOnlyList<PublicSeatView> Seats { get; }
        /// <summary>Outstanding pot; zero after payout. Use TotalAwarded for the result, not this field.</summary>
        public long PotAmount { get; }
        /// <summary>Current actionable street's nominal target; null outside betting.</summary>
        public long? CurrentBet { get; }
        /// <summary>Only the current viewer's actionable betting options; null otherwise.</summary>
        public PlayerBettingOptions Betting { get; }
        public bool CanExchange { get; }
        /// <summary>Zero when not actionable; otherwise the core profile limit. Zero-card confirmation is allowed.</summary>
        public int MaxExchangeCount { get; }
        /// <summary>Gross chips paid from pots; null while unresolved, including AwaitingSettlementRule.</summary>
        public long? TotalAwarded { get; }
        /// <summary>Latest card-free public action facts. Not a lossless history or an instruction to replay it.</summary>
        public PublicHandTransition? LastTransition { get; }
        /// <summary>Null until Complete, including unresolved odd-chip settlement. Showdown reveals only remaining hands.</summary>
        public PokerHandResultView Result { get; }
    }
}
