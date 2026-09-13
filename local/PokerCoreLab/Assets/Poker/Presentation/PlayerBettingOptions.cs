using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Copied current-viewer options. These are hints for input, never permission to bypass Submit.</summary>
    public sealed class PlayerBettingOptions
    {
        internal PlayerBettingOptions(LegalBettingActions legal)
        {
            CanFold = legal.CanFold; CanCheck = legal.CanCheck; CanCall = legal.CanCall;
            CallAmount = legal.CallAmount; CanBet = legal.CanBet; CanRaise = legal.CanRaise;
            MinimumAggressiveTarget = legal.MinimumAggressiveTarget;
            MaximumAggressiveTarget = legal.MaximumAggressiveTarget;
        }

        public bool CanFold { get; }
        public bool CanCheck { get; }
        public bool CanCall { get; }
        /// <summary>Additional chips to pay now; may be a short all-in call.</summary>
        public long CallAmount { get; }
        public bool CanBet { get; }
        public bool CanRaise { get; }
        /// <summary>Total payment in this street, NOT additional chips. Null when aggression is unavailable.</summary>
        public long? MinimumAggressiveTarget { get; }
        public long? MaximumAggressiveTarget { get; }
    }
}
