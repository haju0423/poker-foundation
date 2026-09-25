using Poker.Application;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Runtime
{
    [CreateAssetMenu(menuName = "Poker/Multiplayer Settings")]
    public sealed class HoldemMultiplayerSettings : ScriptableObject
    {
        public long startingStack = 100;
        public long smallBlind = 1;
        public long bigBlind = 2;
        [Tooltip("Separate development flow: source-text intake and manual community-card gates. No AI or card changes.")]
        public bool enableFlowPreview;
        [Tooltip("Allow remarks independently of the manual community-card preview gates.")]
        public bool enableUtterances;
        [Range(1, 4096)] public int utteranceMaximumLength = 128;
        [Range(1, 32)] public int utteranceMaximumPerStreet = 1;
        public HoldemUtteranceSeats utteranceAllowedSeats = HoldemUtteranceSeats.Active;
        public HoldemUtteranceVisibility utteranceVisibility = HoldemUtteranceVisibility.OwnOnly;
        [Tooltip("Keep closed remarks for an attached dealer consumer. Disable for public-only conversation.")]
        public HoldemUtteranceBatchRetention utteranceBatchRetention = HoldemUtteranceBatchRetention.UntilHostAcknowledges;

        public HoldemConfig CreateConfig() => CreateConfig(startingStack, smallBlind, bigBlind);

        public HoldemConfig CreateConfig(long stack, long small, long big, int seatCapacity = HoldemRoom.Capacity)
        {
            HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
            if (stack > long.MaxValue / seatCapacity)
                throw new System.ArgumentOutOfRangeException(nameof(stack), "Starting stacks must fit the chip ledger.");
            return new HoldemConfig(stack, small, big,
                enableFlowPreview ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                dealPolicy: enableFlowPreview ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic);
        }

        public HoldemUtterancePolicy CreateUtterancePolicy() => enableFlowPreview || enableUtterances
            ? new HoldemUtterancePolicy(utteranceMaximumLength, utteranceMaximumPerStreet, utteranceAllowedSeats,
                utteranceVisibility, utteranceBatchRetention) : null;
    }
}
