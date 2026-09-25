namespace Poker.Presentation
{
    /// <summary>Whole chip amounts, optionally grouped with ASCII thousands separators.</summary>
    public static class ChipInput
    {
        // Leave room to show and reject an overlong paste instead of clipping it into a valid amount.
        public const int FieldCapacity = 64;

        public static bool TryParseInteger(string text, out long amount)
        {
            amount = 0;
            if (text == null || text.Length > FieldCapacity) return false;
            text = text.Trim();
            if (text.Length == 0 || text.Length > 25) return false;
            long value = 0;
            int groupDigits = 0, digits = 0;
            bool grouped = false;
            foreach (char character in text)
            {
                if (character == ',')
                {
                    if (grouped ? groupDigits != 3 : groupDigits < 1 || groupDigits > 3) return false;
                    grouped = true; groupDigits = 0;
                    continue;
                }
                if (character < '0' || character > '9' || ++digits > 19) return false;
                int digit = character - '0';
                if (value > (long.MaxValue - digit) / 10) return false;
                value = value * 10 + digit;
                groupDigits++;
            }
            if (grouped && groupDigits != 3) return false;
            amount = value;
            return true;
        }
    }
}
