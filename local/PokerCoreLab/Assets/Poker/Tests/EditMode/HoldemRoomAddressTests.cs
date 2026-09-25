using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRoomAddressTests
    {
        [TestCase("127.0.0.1:7777", "127.0.0.1", 7777)]
        [TestCase("  192.168.1.20:65535  ", "192.168.1.20", 65535)]
        [TestCase("10.0.0.1:1", "10.0.0.1", 1)]
        [TestCase("172.31.255.254:00080", "172.31.255.254", 80)]
        public void SharedAddressIsOnlyParsedWithoutAnyConnection(string text, string expected, int expectedPort)
        {
            Assert.That(HoldemRoomAddress.TryParse(text, out string address, out int port), Is.True);
            Assert.That(address, Is.EqualTo(expected));
            Assert.That(port, Is.EqualTo(expectedPort));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("127.0.0.1")]
        [TestCase("127.1:7777")]
        [TestCase("2130706433:7777")]
        [TestCase("127.0.0.01:7777")]
        [TestCase("256.0.0.1:7777")]
        [TestCase("127.0.0.1:0")]
        [TestCase("127.0.0.1:65536")]
        [TestCase("127.0.0.1:+7777")]
        [TestCase("127.0.0.1: 7777")]
        [TestCase("127.0.0.1:7777:8")]
        [TestCase("127.0.0.1:7777/path")]
        [TestCase("127.0.0.1:7777?token=secret")]
        [TestCase("127.0.0.1:7777\n")]
        [TestCase("127.0.0.1:\t7777")]
        [TestCase("127.0.0.1:７７７７")]
        [TestCase("http://127.0.0.1:7777")]
        [TestCase("localhost:7777")]
        [TestCase("[::1]:7777")]
        public void RejectsMalformedOrAmbiguousAddressWithoutPartialOutput(string text)
        {
            Assert.That(HoldemRoomAddress.TryParse(text, out string address, out int port), Is.False);
            Assert.That(address, Is.Null); Assert.That(port, Is.Zero);
        }
    }
}
