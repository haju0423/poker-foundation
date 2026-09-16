using System;

namespace Poker.Foundation
{
    /// <summary>Heads-up no-limit session settings. Antes and more than two seats are outside this ruleset.</summary>
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
}
