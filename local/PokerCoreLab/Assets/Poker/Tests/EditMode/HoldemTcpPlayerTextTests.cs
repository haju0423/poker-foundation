using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        [TestCase(0x2028)] [TestCase(0x2029)]
        public void InvalidWireNameIsRejectedWithoutReservingItsIdentityOrSeat(int code)
        {
            Connect(0); long revision = clients[0].Latest.revision;
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            var admission = (HoldemWireRequest)typeof(HoldemTcpClient)
                .GetField("admissionRequest", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[1]);
            admission.name = "친구" + (char)code + "가짜 줄";
            Until(() => clients[1].AdmissionError != null || clients[1].IsAdmitted);
            Assert.That(clients[1].AdmissionError, Is.EqualTo("MalformedRequest"));
            Assert.That(clients[1].IsAdmitted, Is.False);
            Assert.That(identities[1].Seat, Is.Zero);
            Assert.That(identities[1].SessionId, Is.Null.Or.Empty);
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
            clients[1].Dispose(); Connect(1);
            Assert.That(identities[1].Seat, Is.EqualTo(2), "The same unused key can still join with its valid original name.");
            Until(() => clients[0].Latest.members.Length == 2);
        }

        [TestCase(0x2028)] [TestCase(0x2029)]
        public void InvalidWireRemarkLeavesPokerQuotaAndPublicHistoryUntouched(int code)
        {
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(128, 1,
                HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
            ConnectAll(); Start();
            var before = clients[1].Latest;
            var receipt = Command(1, Speech(1, "멘트" + (char)code + "다른 줄"));
            Assert.That(receipt.accepted, Is.False);
            Assert.That(receipt.utteranceError, Is.EqualTo(nameof(HoldemUtteranceError.InvalidText)));
            Assert.That(clients[1].Latest.revision, Is.EqualTo(before.revision));
            Assert.That(clients[1].Latest.game.version, Is.EqualTo(before.game.version));
            Assert.That(clients[1].Latest.ownUtterances.remaining, Is.EqualTo(before.ownUtterances.remaining));
            foreach (var client in clients) Assert.That(client.Latest.publicUtterances.entries, Is.Empty);
            const string valid = "  ♥️ 사랑 🃏 👩‍💻  ";
            Assert.That(Command(1, Speech(1, valid)).accepted, Is.True);
            Until(() => System.Array.TrueForAll(clients, c => c.Latest.publicUtterances.entries.Length == 1));
            foreach (var client in clients) Assert.That(client.Latest.publicUtterances.entries[0].text, Is.EqualTo(valid));
            Assert.That(clients[1].Latest.ownUtterances.remaining, Is.EqualTo(before.ownUtterances.remaining - 1));
            ActCurrent();
        }
    }
}
