using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(3)] [TestCase(4)]
        public void LegacyAdmissionIsRejectedOnlyForThreePlayerRoomsWithoutReservingASeat(int capacity)
        {
            Cleanup(); Create(seatCapacity: capacity); Connect(0);
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            var request = (HoldemWireRequest)typeof(HoldemTcpClient).GetField("admissionRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(clients[1]);
            request.supportsThreePlayerRooms = false;
            Until(() => clients[1].IsAdmitted || clients[1].AdmissionError != null);
            if (capacity == 3)
            {
                Assert.That(clients[1].AdmissionError, Is.EqualTo("ClientUpgradeRequired"));
                Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
                Assert.That(identities[1].Seat, Is.Zero);
                clients[1].Dispose(); Connect(1);
            }
            Assert.That(clients[1].Identity.Seat, Is.EqualTo(2));
            Until(() => clients[0].Latest.members.Length == 2);
            if (capacity == 3)
            {
                Connect(2);
                clients[3] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[3]).GetAwaiter().GetResult();
                Until(() => clients[3].AdmissionError != null);
                Assert.That(clients[3].AdmissionError, Is.EqualTo("Full"));
                Assert.That(clients[0].Latest.members.Select(m => m.seat), Is.EquivalentTo(new[] { 1, 2, 3 }));
            }
        }
    }
}
