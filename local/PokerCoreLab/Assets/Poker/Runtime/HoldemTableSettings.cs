using System;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Runtime
{
    /// <summary>Phase 1 demo settings, not final team betting or economy rules.</summary>
    [CreateAssetMenu(menuName = "Poker/Holdem Table Settings")]
    public sealed class HoldemTableSettings : ScriptableObject
    {
        public long startingStack = 100;
        public long smallBlind = 1;
        public long bigBlind = 2;
        [Min(0.1f)] public float opponentDelaySeconds = 0.7f;
        public HoldemConfig CreateConfig()
        {
            if (float.IsNaN(opponentDelaySeconds) || float.IsInfinity(opponentDelaySeconds) || opponentDelaySeconds < 0.1f)
                throw new ArgumentException("A finite opponent delay of at least 0.1 seconds is required.");
            return new HoldemConfig(startingStack, smallBlind, bigBlind);
        }
    }
}
