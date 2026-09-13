using System;

namespace Poker.Application
{
    /// <summary>Immutable practice tuning, not poker rules or calculated winning probabilities.</summary>
    public sealed class PracticeOpponentSettings
    {
        public static PracticeOpponentSettings Default { get; } = new PracticeOpponentSettings();

        public PracticeOpponentSettings(decimal beforeDrawHighCardCallLimit = 0.34m,
            decimal afterDrawHighCardCallLimit = 0.20m, decimal beforeDrawPairCallLimit = 0.45m,
            decimal afterDrawPairCallLimit = 0.35m, decimal twoPairCallLimit = 0.50m,
            decimal aggressionBankrollShare = 0.35m, int afterDrawBluffPercent = 12)
        {
            ValidateShare(beforeDrawHighCardCallLimit, nameof(beforeDrawHighCardCallLimit));
            ValidateShare(afterDrawHighCardCallLimit, nameof(afterDrawHighCardCallLimit));
            ValidateShare(beforeDrawPairCallLimit, nameof(beforeDrawPairCallLimit));
            ValidateShare(afterDrawPairCallLimit, nameof(afterDrawPairCallLimit));
            ValidateShare(twoPairCallLimit, nameof(twoPairCallLimit));
            ValidateShare(aggressionBankrollShare, nameof(aggressionBankrollShare));
            if (afterDrawBluffPercent < 0 || afterDrawBluffPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(afterDrawBluffPercent));
            BeforeDrawHighCardCallLimit = beforeDrawHighCardCallLimit;
            AfterDrawHighCardCallLimit = afterDrawHighCardCallLimit;
            BeforeDrawPairCallLimit = beforeDrawPairCallLimit;
            AfterDrawPairCallLimit = afterDrawPairCallLimit;
            TwoPairCallLimit = twoPairCallLimit;
            AggressionBankrollShare = aggressionBankrollShare;
            AfterDrawBluffPercent = afterDrawBluffPercent;
        }

        public decimal BeforeDrawHighCardCallLimit { get; }
        public decimal AfterDrawHighCardCallLimit { get; }
        public decimal BeforeDrawPairCallLimit { get; }
        public decimal AfterDrawPairCallLimit { get; }
        public decimal TwoPairCallLimit { get; }
        public decimal AggressionBankrollShare { get; }
        public int AfterDrawBluffPercent { get; }

        private static void ValidateShare(decimal value, string name)
        {
            if (value < 0m || value > 1m) throw new ArgumentOutOfRangeException(name);
        }
    }
}
