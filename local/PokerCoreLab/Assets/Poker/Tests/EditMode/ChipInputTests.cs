using System.Globalization;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class ChipInputTests
    {
        [TestCase("0", 0L)]
        [TestCase("001", 1L)]
        [TestCase("0,001", 1L)]
        [TestCase("000,001", 1L)]
        [TestCase("12", 12L)]
        [TestCase("1000", 1000L)]
        [TestCase("1,000", 1000L)]
        [TestCase("12,345", 12345L)]
        [TestCase("123,456,789", 123456789L)]
        [TestCase("  1,000  ", 1000L)]
        [TestCase("9,007,199,254,740,993", 9007199254740993L)]
        [TestCase("9223372036854775807", long.MaxValue)]
        [TestCase("9,223,372,036,854,775,807", long.MaxValue)]
        public void AcceptsExactWholeAmounts(string text, long expected)
        {
            Assert.That(ChipInput.TryParseInteger(text, out var value), Is.True);
            Assert.That(value, Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("-1")]
        [TestCase("+1")]
        [TestCase("1.5")]
        [TestCase("1e3")]
        [TestCase("1 000")]
        [TestCase("1\t000")]
        [TestCase("1, 000")]
        [TestCase("1,00")]
        [TestCase("1,0000")]
        [TestCase("1000,000")]
        [TestCase(",100")]
        [TestCase("100,")]
        [TestCase("1,,000")]
        [TestCase("1,000,00")]
        [TestCase("1,000칩")]
        [TestCase("１,０００")]
        [TestCase("1，000")]
        [TestCase("1.000")]
        [TestCase("9,223,372,036,854,775,808")]
        [TestCase("9223372036854775808")]
        [TestCase("9,223,372,036,854,775,8070")]
        [TestCase("00000000000000000001")]
        public void RejectsMalformedAndOverflowingAmountsWithoutPartialValue(string text)
        {
            Assert.That(ChipInput.TryParseInteger(text, out var value), Is.False);
            Assert.That(value, Is.Zero);
        }

        [TestCase("ko-KR")]
        [TestCase("en-US")]
        [TestCase("de-DE")]
        [TestCase("fr-FR")]
        public void InputMatchesDisplayedGroupingRegardlessOfSystemCulture(string culture)
        {
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                Assert.That(ChipInput.TryParseInteger("1,234", out var value), Is.True);
                Assert.That(value, Is.EqualTo(1234));
                Assert.That(ChipInput.TryParseInteger("1.234", out _), Is.False);
            }
            finally { CultureInfo.CurrentCulture = before; }
        }

        [Test]
        public void InputWorkIsBoundedAndAnOverlongPasteCannotBeSilentlyTruncatedByTheParser()
        {
            Assert.That(ChipInput.TryParseInteger(new string('1', 100000), out var value), Is.False);
            Assert.That(value, Is.Zero);
            Assert.That(ChipInput.FieldCapacity, Is.GreaterThan("9,223,372,036,854,775,8070".Length));
        }
    }
}
