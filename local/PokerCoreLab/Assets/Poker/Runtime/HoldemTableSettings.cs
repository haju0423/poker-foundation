using System;
using Poker.Application;
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
        [Tooltip("WaitForHost enables a local flow preview with a manual, unchanged community-card deal.")]
        public HoldemDealPolicy dealPolicy = HoldemDealPolicy.Automatic;
        [Tooltip("Development-only source-text intake. No AI interpretation or card changes.")]
        public bool enableUtterancePreview;
        [Range(1, 4096)] public int utteranceMaximumLength = 128;
        [Range(1, 32)] public int utteranceMaximumPerStreet = 1;
        public HoldemUtteranceSeats utteranceAllowedSeats = HoldemUtteranceSeats.Active;

        public HoldemUtterancePolicy CreateUtterancePolicy()
        {
            if (!enableUtterancePreview) return null;
            if (dealPolicy != HoldemDealPolicy.WaitForHost)
                throw new InvalidOperationException("Utterance preview requires the separate host-deal flow.");
            return new HoldemUtterancePolicy(utteranceMaximumLength, utteranceMaximumPerStreet, utteranceAllowedSeats);
        }
        public HoldemConfig CreateConfig()
        {
            if (seatCount < 2 || seatCount > 4) throw new ArgumentOutOfRangeException(nameof(seatCount));
            if (float.IsNaN(opponentDelaySeconds) || float.IsInfinity(opponentDelaySeconds) || opponentDelaySeconds < 0.1f)
                throw new ArgumentException("A finite opponent delay of at least 0.1 seconds is required.");
            return new HoldemConfig(startingStack, smallBlind, bigBlind, revealPolicy, accusationMode, dealPolicy);
        }
    }
}
