using System;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.NetworkInformation;
using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemLanAddressesTests
    {
        [TestCase("10.0.0.1", true)]
        [TestCase("172.16.0.1", true)]
        [TestCase("172.31.255.254", true)]
        [TestCase("192.168.1.20", true)]
        [TestCase("172.15.255.255", false)]
        [TestCase("172.32.0.1", false)]
        [TestCase("127.0.0.1", false)]
        [TestCase("169.254.1.1", false)]
        [TestCase("0.0.0.0", false)]
        [TestCase("8.8.8.8", false)]
        [TestCase("::1", false)]
        [TestCase("fd00::1", false)]
        public void OnlyPrivateIpv4AddressesAreSuggested(string address, bool expected)
            => Assert.That(HoldemLanAddresses.IsCandidate(IPAddress.Parse(address), OperationalStatus.Up, NetworkInterfaceType.Ethernet), Is.EqualTo(expected));

        [TestCase(OperationalStatus.Down, NetworkInterfaceType.Ethernet)]
        [TestCase(OperationalStatus.Dormant, NetworkInterfaceType.Wireless80211)]
        [TestCase(OperationalStatus.Up, NetworkInterfaceType.Loopback)]
        [TestCase(OperationalStatus.Up, NetworkInterfaceType.Tunnel)]
        public void InactiveLoopbackAndTunnelAdaptersAreNotSuggested(OperationalStatus status, NetworkInterfaceType type)
            => Assert.That(HoldemLanAddresses.IsCandidate(IPAddress.Parse("192.168.1.2"), status, type), Is.False);

        [Test]
        public void MissingOrFailedInterfaceLookupRejectsBindingWithoutChangingTheNetwork()
        {
            var read = typeof(HoldemLanAddresses).GetMethod("IsAssignedLocally", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (Func<NetworkInterface[]> provider in new Func<NetworkInterface[]>[] {
                () => Array.Empty<NetworkInterface>(),
                () => throw new NetworkInformationException(),
                () => throw new PlatformNotSupportedException() })
                Assert.That((bool)read.Invoke(null, new object[] { IPAddress.Parse("192.168.10.20"), provider }), Is.False);
        }

        [Test]
        public void LocalEnumerationReturnsUniquePrivateHintsWithoutRequiringAnyInterface()
        {
            var addresses = HoldemLanAddresses.ReadLocal();
            Assert.That(addresses.Select(a => a.Address).Distinct().Count(), Is.EqualTo(addresses.Length));
            foreach (var address in addresses)
            {
                Assert.That(HoldemLanAddresses.IsCandidate(IPAddress.Parse(address.Address), OperationalStatus.Up, NetworkInterfaceType.Ethernet), Is.True);
                Assert.That(address.Label, Does.Contain(address.Address));
            }
        }
    }
}
