using System;

namespace Poker.Transport
{
    // Detached wire data, never authoritative game state. No field initializers:
    // null strings/arrays let the validator distinguish missing from explicit empty.
    [Serializable]
    public sealed class PokerWireCommand
    {
        public int protocolVersion;
        public string message, handId, commandId, expectedVersion, kind, action, targetTotal;
        public int seat;
        public int[] cards;
    }

    [Serializable]
    public sealed class PokerWireReceipt
    {
        public int protocolVersion;
        public string message, handId, commandId, appliedVersion, error;
        public int seat;
        public PokerWireTransition[] transition;
    }

    [Serializable]
    public sealed class PokerWireSnapshot
    {
        public int protocolVersion;
        public string message, handId, version, phase, potAmount, currentBet, totalAwarded;
        public int viewerSeat, currentSeat, maxExchangeCount;
        public int[] ownCards;
        public bool canExchange;
        public PokerWireSeat[] seats;
        public PokerWireBetting[] betting;
        public PokerWireTransition[] lastTransition;
        public PokerWireResult[] result;
    }

    [Serializable]
    public sealed class PokerWireSeat
    {
        public int seat;
        public string stack, committed, streetContribution, awarded;
        public bool folded, allIn;
    }

    [Serializable]
    public sealed class PokerWireBetting
    {
        public bool canFold, canCheck, canCall, canBet, canRaise;
        public string callAmount, minimumAggressiveTarget, maximumAggressiveTarget;
    }

    [Serializable]
    public sealed class PokerWireTransition
    {
        public string handId, appliedVersion, beforePhase, afterPhase, kind, action;
        public string targetTotal, chipsPaid, refundedAmount;
        public int seat, exchangeCount, refundedSeat;
    }

    [Serializable]
    public sealed class PokerWireResult
    {
        public string handId, version, reason, totalAwarded;
        public int viewerSeat;
        public PokerWireSeatResult[] seats;
        public PokerWirePot[] pots;
        public PokerWireRefund[] refunds;
    }

    [Serializable]
    public sealed class PokerWireSeatResult
    {
        public int seat;
        public string finalStack, grossAward;
    }

    [Serializable]
    public sealed class PokerWirePot
    {
        public string lowerBound, contributionCap, amount;
        public int[] eligibleSeats;
        public PokerWirePayout[] payouts;
    }

    [Serializable]
    public sealed class PokerWirePayout
    {
        public int seat;
        public string amount;
        public bool hasOddChip;
    }

    [Serializable]
    public sealed class PokerWireRefund
    {
        public string bettingPhase, amount;
        public int seat;
    }

    /// <summary>Format failure. The supplied mapper/JSON adapter pass fixed field keys, never payload values.</summary>
    public sealed class PokerWireException : FormatException
    {
        public PokerWireException(string field) : base("Invalid poker wire field: " + field) { }
    }
}
