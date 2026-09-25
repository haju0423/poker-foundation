using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Transport;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator QueuedStartWaitsForTheDisconnectedGuestAndHostConfirmation()
        {
            yield return EnterRoom();
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            var serverField = typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic);
            var authority = (HoldemTcpServer)serverField.GetValue(boots[0].Connection);
            var peers = (IEnumerable)typeof(HoldemTcpServer).GetField("peers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(authority);
            serverField.SetValue(boots[0].Connection, null);
            try
            {
                yield return Click(0, "omc-room-start");
                yield return Wait(() => authority.PendingInputCount > 0);
                Client(3).Dispose();
                yield return Wait(() => peers.Cast<object>().Any(p => ((HoldemTcpChannel)p.GetType().GetField("Channel").GetValue(p)).IsClosed));
            }
            finally { serverField.SetValue(boots[0].Connection, authority); }

            yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(boots[0].Connection.Remote.HasPendingInput, Is.True);
            Assert.That(boots[0].Connection.Remote.HasGame, Is.False);
            Assert.That(boots[0].Connection.Remote.ErrorText, Is.Empty);
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => !b.Connection.Remote.Lobby.Paused && b.Connection.Remote.Lobby.AllReady));
            Assert.That(boots.All(b => !b.Connection.Remote.HasGame), Is.True, "Room resume must not start the table automatically.");
            Assert.That(boots[0].Connection.Remote.HasPendingInput, Is.True);
            Assert.That(boots[0].Connection.Remote.RefreshLabel, Is.EqualTo("입력 확인"));
            yield return Click(0, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => b.Connection.Remote.HasGame) && !boots[0].Connection.Remote.HasPendingInput);
            Assert.That(boots.All(b => b.Connection.Remote.Read().HandNumber == 1
                && b.Connection.Remote.Read().SessionVersion == 1), Is.True);
            Assert.That(boots.Select(b => b.Connection.Remote.Read().HandId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(boots[0].Connection.Remote.ErrorText, Is.Empty);
        }

        [UnityTest]
        public IEnumerator UnansweredReadyInputCanReconnectFromTheLobbyWithoutLosingItsSeat()
        {
            yield return EnterRoom();
            var original = Client(1);
            int seat = original.Identity.Seat;
            // Disabling the bootstrap disposes its sockets. Detach only its pump owner for this
            // controlled stall, and restore the same authority even if an assertion fails.
            var serverField = typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic);
            var authority = serverField.GetValue(boots[0].Connection);
            Assert.That(authority, Is.Not.Null);
            serverField.SetValue(boots[0].Connection, null);
            try
            {
                yield return Click(1, "omc-room-ready");
                Assert.That(boots[1].Connection.Remote.HasPendingInput, Is.True);
                yield return Wait(() => boots[1].Connection.Remote.NeedsRefresh);
                yield return Click(1, "omc-room-reconnect");
                Assert.That(original.IsClosed, Is.False, "The initial input check reuses the existing transport.");
                Assert.That(roots[1].Q<Button>("omc-room-reconnect").text, Is.EqualTo("입력 확인"));
                yield return Wait(() => boots[1].Connection.Remote.RefreshLabel == "다시 연결");
                yield return Click(1, "omc-room-reconnect");
                Assert.That(original.IsClosed, Is.True);
                Assert.That(boots[1].Connection.Remote.HasPendingInput, Is.True);
            }
            finally { serverField.SetValue(boots[0].Connection, authority); }
            yield return Wait(() => boots[1].Connection.Remote.Lobby.IsReady
                && !boots[1].Connection.Remote.HasPendingInput && !boots[1].Connection.IsConnecting);
            Assert.That(Client(1), Is.Not.SameAs(original));
            Assert.That(Client(1).Identity.Seat, Is.EqualTo(seat));
            Assert.That(boots.All(b => b.Connection.Remote.Lobby.MemberCount == 4), Is.True);
            Assert.That(boots.All(b => !b.Connection.Remote.HasGame), Is.True);
            Assert.That(boots[1].Connection.Remote.ErrorText, Is.Empty);
        }
    }
}
