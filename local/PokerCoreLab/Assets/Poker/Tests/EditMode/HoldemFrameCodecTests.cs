using System;
using System.IO;
using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemFrameCodecTests
    {
        [Test]
        public void FragmentedFramesRoundTripKoreanTextAndSeparateCoalescedMessages()
        {
            using (var bytes = new FragmentedStream())
            {
                var first = HoldemFrameCodec.Encode("{\"name\":\"주하 ♥\"}");
                var second = HoldemFrameCodec.Encode("{\"type\":\"ping\"}");
                bytes.Write(first, 0, first.Length); bytes.Write(second, 0, second.Length); bytes.Position = 0;
                Assert.That(HoldemFrameCodec.Read(bytes), Is.EqualTo("{\"name\":\"주하 ♥\"}"));
                Assert.That(HoldemFrameCodec.Read(bytes), Is.EqualTo("{\"type\":\"ping\"}"));
                Assert.That(HoldemFrameCodec.Read(bytes), Is.Null);
            }
        }

        [TestCase(new byte[] { 0, 0 })]
        [TestCase(new byte[] { 0, 0, 0, 4, 123 })]
        public void TruncatedHeaderOrBodyNeverProducesAnInput(byte[] bytes)
        {
            Assert.Throws<EndOfStreamException>(() => HoldemFrameCodec.Read(new MemoryStream(bytes)));
        }

        [TestCase(new byte[] { 0, 0, 0, 0 })]
        [TestCase(new byte[] { 0, 1, 0, 1 })]
        [TestCase(new byte[] { 255, 255, 255, 255 })]
        public void InvalidLengthIsRejectedBeforeAllocatingItsPayload(byte[] bytes)
        {
            Assert.Throws<InvalidDataException>(() => HoldemFrameCodec.Read(new MemoryStream(bytes)));
        }

        [Test]
        public void InvalidUtf8AndOversizedUnicodeAreRejected()
        {
            Assert.Throws<System.Text.DecoderFallbackException>(() => HoldemFrameCodec.Read(new MemoryStream(new byte[] { 0, 0, 0, 1, 255 })));
            Assert.Throws<InvalidDataException>(() => HoldemFrameCodec.Encode(new string('가', 30000)));
            Assert.Throws<InvalidDataException>(() => HoldemFrameCodec.Encode(""));
        }

        private sealed class FragmentedStream : MemoryStream
        { public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1)); }
    }
}
