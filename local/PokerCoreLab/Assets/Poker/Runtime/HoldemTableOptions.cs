using System;
using Poker.Foundation;

namespace Poker.Runtime
{
    /// <summary>Local presentation/session choices. Does not change betting or accusation rules.</summary>
    public sealed class HoldemTableOptions
    {
        public HoldemTableOptions(int seatCount, float opponentDelaySeconds, HoldemRevealPolicy revealPolicy)
        {
            if (seatCount < 2 || seatCount > 4) throw new ArgumentOutOfRangeException(nameof(seatCount));
            if (float.IsNaN(opponentDelaySeconds) || float.IsInfinity(opponentDelaySeconds) || opponentDelaySeconds < 0.1f)
                throw new ArgumentOutOfRangeException(nameof(opponentDelaySeconds));
            if (revealPolicy != HoldemRevealPolicy.Automatic && revealPolicy != HoldemRevealPolicy.PauseAfterCommunityReveal)
                throw new ArgumentOutOfRangeException(nameof(revealPolicy));
            SeatCount = seatCount;
            OpponentDelaySeconds = opponentDelaySeconds;
            RevealPolicy = revealPolicy;
        }

        public int SeatCount { get; }
        public float OpponentDelaySeconds { get; }
        public HoldemRevealPolicy RevealPolicy { get; }
    }
}
