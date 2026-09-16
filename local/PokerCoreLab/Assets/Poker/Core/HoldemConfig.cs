using System;

namespace Poker.Foundation
{
    /// <summary>Two-to-four-seat no-limit session settings. Antes remain outside this ruleset.</summary>
    public sealed class HoldemConfig
    {
        public HoldemConfig(long startingStack, long smallBlind, long bigBlind)
        {
            if (startingStack <= 0) throw new ArgumentOutOfRangeException(nameof(startingStack));
            if (smallBlind <= 0) throw new ArgumentOutOfRangeException(nameof(smallBlind));
            if (bigBlind < smallBlind) throw new ArgumentOutOfRangeException(nameof(bigBlind));
            StartingStack = startingStack;
            SmallBlind = smallBlind;
            BigBlind = bigBlind;
        }

        public long StartingStack { get; }
        public long SmallBlind { get; }
        public long BigBlind { get; }
    }

    public enum HoldemStreet
    {
        Preflop = 0,
        Flop = 1,
        Turn = 2,
        River = 3,
        Complete = 4
    }

    public enum HoldemResultKind
    {
        Fold = 1,
        Showdown = 2
    }

    public enum HoldemButtonPolicy
    {
        PokerStarsForwardMoving = 1
    }

    public enum HoldemOddChipRule
    {
        RequireExplicitPriority = 0,
        ClockwiseFromButton = 1
    }

    public enum HoldemSettlementState
    {
        None = 0,
        AwaitingOddChipPriority = 1,
        Settled = 2
    }

    public enum HoldemSeatStatus
    {
        Busted = 0,
        Active = 1,
        Folded = 2,
        AllIn = 3
    }
}
