using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        public IEnumerator LostLeaveReceiptOffersExplicitLocalExitWhenTheHostIsGone()
            => UnconfirmedLeaveCanExitAfterHostDisappears(false);

        [UnityTest]
        public IEnumerator LeaveWaitingForAReadyReceiptAlsoOffersExplicitLocalExit()
            => UnconfirmedLeaveCanExitAfterHostDisappears(true);

        private IEnumerator UnconfirmedLeaveCanExitAfterHostDisappears(bool queuedReady)
        {
            UseSmallRecoveryPanel(1);
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            var identity = Client(1).Identity;
            if (queuedReady)
            {
                var field = typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic);
                var authority = (HoldemTcpServer)field.GetValue(boots[0].Connection);
                field.SetValue(boots[0].Connection, null);
                try
                {
                    yield return Click(1, "omc-room-ready");
                    yield return Click(1, "omc-room-leave");
                    yield return Click(1, "omc-room-confirm-leave");
                    yield return null;
                    Assert.That(remote.HasPendingInput, Is.True);
                    Assert.That(remote.IsLobbyLeavePending, Is.False);
                    Assert.That(roots[1].Q<Button>("omc-room-confirm-leave").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                }
                finally { field.SetValue(boots[0].Connection, authority); }
            }
            else BeginLeaveAndDropReceipt(1);
            boots[0].Connection.CloseSession();
            yield return Wait(() => boots[1].Connection.CanReconnect);
            yield return null; yield return null;
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            var exit = roots[1].Q<Button>("omc-room-confirm-leave");
            Assert.That(exit.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                "An unconfirmed leave must not trap a player when the authority has gone away.");
            Assert.That(exit.text, Is.EqualTo("연결 정보 지우고 나가기"));
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("퇴장이 처리됐는지 확인하지 못했어요"));
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("같은 자리로 돌아올 수 없"));
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("방에 자리가 남아"));
            Assert.That(boots[1].Connection.HasSession, Is.True);
            Capture(1, queuedReady ? "leave-ready-unconfirmed" : "leave-unconfirmed");
            yield return Wait(() => Pickable(1, "omc-room-leave-retry"));
            // Dispatch within this frame: a yielded nested iterator may return after a fast refusal.
            Assert.That(Click(1, "omc-room-leave-retry").MoveNext(), Is.False);
            Assert.That(boots[1].Connection.IsConnecting, Is.True);
            // Sample the connecting state before a loopback refusal can finish in the next frame.
            typeof(HoldemMultiplayerBootstrap).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(boots[1], null);
            Assert.That(exit.style.display.value, Is.EqualTo(DisplayStyle.None));
            yield return Wait(() => !boots[1].Connection.IsConnecting && !string.IsNullOrEmpty(boots[1].Connection.ErrorText));
            Assert.That(boots[1].HasFailed, Is.False);
            Assert.That(Client(1).Identity, Is.SameAs(identity));
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            yield return Click(1, "omc-room-confirm-leave");
            Assert.That(boots[1].Connection.HasSession, Is.False);
            Assert.That(roots[1].Q<TextField>("omc-room-address").enabledInHierarchy, Is.True);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False, "Local abandonment is not a confirmed seat return.");
        }

        [UnityTest]
        public IEnumerator InitialConnectionFailureKeepsAnUnadmittedIdentityForRetry()
        {
            using (var closedEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (var connection = new HoldemMultiplayerConnection())
            {
                closedEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Assert.That(connection.Join("친구", "127.0.0.1", ((IPEndPoint)closedEndpoint.LocalEndPoint).Port, false), Is.True);
                double end = UnityEngine.Time.realtimeSinceStartupAsDouble + 8;
                while (connection.IsConnecting && UnityEngine.Time.realtimeSinceStartupAsDouble < end)
                { connection.Poll(); yield return null; }
                Assert.That(connection.IsConnecting, Is.False);
                Assert.That(connection.ErrorText, Is.Not.Empty);
                Assert.That(connection.Remote, Is.Null);
                Assert.That(connection.CanReconnect, Is.True);
                Assert.That(connection.CanEditFailedJoin, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator FailedInitialConnectionCanEditAddressThenJoinAndPlay()
        {
            UseSmallRecoveryPanel(1);
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            int realPort = boots[0].Connection.Port;
            // Reserve a loopback port without listening: it cannot be claimed by another test.
            using (var closedEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                closedEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int wrongPort = ((IPEndPoint)closedEndpoint.LocalEndPoint).Port;
                Fields(1, wrongPort, "친구"); yield return Click(1, "omc-room-join");
                yield return Wait(() => !boots[1].Connection.IsConnecting && !string.IsNullOrEmpty(boots[1].Connection.ErrorText));
                Assert.That(boots[1].Connection.Remote, Is.Null);
                Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(1));
                var edit = roots[1].Q<Button>("omc-room-edit-connection");
                Assert.That(edit, Is.Not.Null, "A failed first connection needs a direct path back to its address fields.");
                yield return Wait(() => Pickable(1, "omc-room-edit-connection") && Pickable(1, "omc-room-reconnect"));
                Capture(1, "join-address-failed");
                yield return Click(1, "omc-room-edit-connection");
                Assert.That(boots[1].Connection.HasSession, Is.False);
                Assert.That(roots[1].Q<TextField>("omc-room-name").value, Is.EqualTo("친구"));
                Assert.That(roots[1].Q<TextField>("omc-room-port").value, Is.EqualTo(wrongPort.ToString()));
                Assert.That(roots[1].Q<TextField>("omc-room-address").enabledInHierarchy, Is.True);
                Assert.That(roots[1].Q("omc-room-confirmation").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                roots[1].Q<TextField>("omc-room-port").value = realPort.ToString();
                yield return Click(1, "omc-room-join");
                yield return Wait(() => boots[1].Connection.Remote?.Lobby != null);
            }
            for (int viewer = 2; viewer < 4; viewer++)
            {
                Fields(viewer, realPort); yield return Click(viewer, "omc-room-join");
                int current = viewer; yield return Wait(() => boots[current].Connection.Remote?.Lobby != null);
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.MemberCount == 4));
            for (int viewer = 0; viewer < 4; viewer++)
            {
                Assert.That(roots[viewer].Q<Button>("omc-room-edit-connection").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                yield return Click(viewer, "omc-room-ready");
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => boots.All(b => b.Connection.Remote.HasGame));
            yield return Click(3, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion == 2));
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator FailedReconnectCannotDiscardAnAdmittedSeatThroughAddressEditing()
        {
            yield return StartGame();
            var connection = boots[3].Connection;
            var remote = connection.Remote;
            var identity = Client(3).Identity;
            var before = remote.Read();
            var cards = Enumerable.Range(0, before.OwnCardCount).Select(before.GetOwnCard).ToArray();
            boots[0].Connection.CloseSession();
            yield return Wait(() => connection.CanReconnect);
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => !connection.IsConnecting && !string.IsNullOrEmpty(connection.ErrorText));
            Assert.That(boots[3].HasFailed, Is.False);
            Assert.That(connection.HasSession, Is.True);
            Assert.That(connection.CanEditFailedJoin, Is.False);
            Assert.That(connection.Remote, Is.SameAs(remote));
            Assert.That(Client(3).Identity, Is.SameAs(identity));
            Assert.That(remote.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(Enumerable.Range(0, remote.Read().OwnCardCount).Select(remote.Read().GetOwnCard), Is.EqualTo(cards));
            Assert.That(roots[3].Q<Button>("omc-room-edit-connection").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[3].Q<TextField>("omc-room-address").enabledSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator MissingAdmissionReceiptPreservesTheOriginalIdentityAndReservedSeat()
        {
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            using (var connection = new HoldemMultiplayerConnection())
            {
                Assert.That(connection.Join("친구", "127.0.0.1", boots[0].Connection.Port, false), Is.True);
                double end = UnityEngine.Time.realtimeSinceStartupAsDouble + 8;
                while (connection.Remote == null && UnityEngine.Time.realtimeSinceStartupAsDouble < end)
                { connection.Poll(); yield return null; }
                Assert.That(connection.Remote, Is.Not.Null);
                var remote = connection.Remote;
                var clientField = typeof(HoldemMultiplayerConnection).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic);
                var client = (HoldemTcpClient)clientField.GetValue(connection);
                var identity = client.Identity;
                // The host reserves the seat, but this owner does not poll the admission receipt.
                yield return Wait(() => boots[0].Connection.Remote.Lobby.MemberCount == 2);
                Assert.That(identity.Seat, Is.Zero);
                Assert.That(remote.Lobby, Is.Null);
                client.Dispose();
                Assert.That(connection.CanReconnect, Is.True);
                Assert.That(connection.CanEditFailedJoin, Is.False);
                yield return Wait(() => !boots[0].Connection.Remote.Lobby.GetMember(1).Connected);
                connection.Reconnect();
                end = UnityEngine.Time.realtimeSinceStartupAsDouble + 8;
                while ((remote.Lobby == null || !remote.CanSend) && UnityEngine.Time.realtimeSinceStartupAsDouble < end)
                { connection.Poll(); yield return null; }
                Assert.That(remote.Lobby, Is.Not.Null);
                Assert.That(remote.CanSend, Is.True);
                Assert.That(connection.Remote, Is.SameAs(remote));
                Assert.That(((HoldemTcpClient)clientField.GetValue(connection)).Identity, Is.SameAs(identity));
                Assert.That(identity.Seat, Is.EqualTo(2));
                Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(2));
                Assert.That(connection.CanEditFailedJoin, Is.False);
            }
        }

        private void UseSmallRecoveryPanel(int viewer)
        {
            var old = textures[viewer];
            textures[viewer] = new UnityEngine.RenderTexture(960, 640, 0);
            textures[viewer].Create(); panels[viewer].targetTexture = textures[viewer];
            old.Release(); UnityEngine.Object.Destroy(old);
        }
    }
}
