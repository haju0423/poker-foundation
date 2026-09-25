using System;

namespace Poker.Foundation
{
    /// <summary>Two-to-four-seat no-limit session settings. Antes remain outside this ruleset.</summary>
    public sealed class HoldemConfig
    {
        public HoldemConfig(long startingStack, long smallBlind, long bigBlind,
            HoldemRevealPolicy revealPolicy = HoldemRevealPolicy.Automatic,
            HoldemAccusationMode accusationMode = HoldemAccusationMode.Disabled,
            HoldemDealPolicy dealPolicy = HoldemDealPolicy.Automatic)
        {
            if (startingStack <= 0) throw new ArgumentOutOfRangeException(nameof(startingStack));
            if (smallBlind <= 0) throw new ArgumentOutOfRangeException(nameof(smallBlind));
            if (bigBlind < smallBlind) throw new ArgumentOutOfRangeException(nameof(bigBlind));
            if (revealPolicy != HoldemRevealPolicy.Automatic && revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal)
                throw new ArgumentOutOfRangeException(nameof(revealPolicy));
            if (accusationMode != HoldemAccusationMode.Disabled && accusationMode != HoldemAccusationMode.CollectLatestChoiceUntilHostCloses)
                throw new ArgumentOutOfRangeException(nameof(accusationMode));
            if (accusationMode != HoldemAccusationMode.Disabled && revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal)
                throw new ArgumentException("Accusation input requires community-card reveal windows.", nameof(accusationMode));
            if (dealPolicy != HoldemDealPolicy.Automatic && dealPolicy != HoldemDealPolicy.WaitForHost)
                throw new ArgumentOutOfRangeException(nameof(dealPolicy));
            StartingStack = startingStack;
            SmallBlind = smallBlind;
            BigBlind = bigBlind;
            RevealPolicy = revealPolicy;
            AccusationMode = accusationMode;
            DealPolicy = dealPolicy;
        }

        public long StartingStack { get; }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public HoldemRevealPolicy RevealPolicy { get; }
        public HoldemAccusationMode AccusationMode { get; }
        public HoldemDealPolicy DealPolicy { get; }
    }

    public enum HoldemDealPolicy
    {
        Automatic = 0,
        WaitForHost = 1
    }

    public enum HoldemAccusationMode
    {
        Disabled = 0,
        CollectLatestChoiceUntilHostCloses = 1
    }

    public enum HoldemRevealPolicy
    {
        Automatic = 0,
        PauseAfterCommunityReveal = 1
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
