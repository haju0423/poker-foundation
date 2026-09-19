using System;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Runtime
{
    /// <summary>Default local table configuration.</summary>
    [CreateAssetMenu(menuName = "Poker/Holdem Table Settings")]
    public sealed class HoldemTableSettings : ScriptableObject
    {
        public long startingStack = 100;
        public long smallBlind = 1;
        public long bigBlind = 2;
        [Range(2, 4)] public int seatCount = 4;
        [Min(0.1f)] public float opponentDelaySeconds = 0.7f;
        public HoldemRevealPolicy revealPolicy = HoldemRevealPolicy.Automatic;
        public HoldemAccusationMode accusationMode = HoldemAccusationMode.Disabled;
        public HoldemConfig CreateConfig()
        {
            if (seatCount < 2 || seatCount > 4) throw new ArgumentOutOfRangeException(nameof(seatCount));
            if (float.IsNaN(opponentDelaySeconds) || float.IsInfinity(opponentDelaySeconds) || opponentDelaySeconds < 0.1f)
                throw new ArgumentException("A finite opponent delay of at least 0.1 seconds is required.");
            return new HoldemConfig(startingStack, smallBlind, bigBlind, revealPolicy, accusationMode);
        }
    }
}
