using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        private bool RequestApplicationExit(int viewer)
        {
            var callback = typeof(HoldemMultiplayerBootstrap).GetMethod("OnWantsToQuit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(callback, Is.Not.Null, "Normal application exit must reach the room confirmation boundary.");
            return (bool)callback.Invoke(boots[viewer], null);
        }
        private void ObserveApplicationExit(int viewer, Action action)
        {
            var field = typeof(HoldemMultiplayerBootstrap).GetField("quitApplication", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(boots[viewer], action);
        }

        [UnityTest]
        public IEnumerator ApplicationExitAtHomeNeedsNoRoomConfirmation()
        {
            Assert.That(RequestApplicationExit(0), Is.True);
            yield return null;
            Assert.That(roots[0].Q("omc-room-confirmation").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator ApplicationExitCancelPreservesHandAndDoesNotTurnLaterRoomLeaveIntoQuit()
        {
            yield return StartGame();
            int quits = 0; ObserveApplicationExit(0, () => quits++);
            var remote = boots[0].Connection.Remote;
            var before = JsonUtility.ToJson(Client(0).Latest.game);
            Assert.That(RequestApplicationExit(0), Is.False);
            Assert.That(RequestApplicationExit(0), Is.False);
            yield return null;
            Assert.That(roots[0].Q<Label>("omc-room-confirm-copy").text, Does.Contain("게임을 종료"));
            Assert.That(boots[0].Connection.Remote, Is.SameAs(remote));
            Assert.That(quits, Is.Zero);
            yield return Click(0, "omc-room-stay");
            Assert.That(JsonUtility.ToJson(Client(0).Latest.game), Is.EqualTo(before));
            Assert.That(remote.CanSend, Is.True);
            yield return Click(0, "omc-room-leave");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession);
            Assert.That(quits, Is.Zero, "Cancelled app-exit intent must not leak into later room navigation.");
        }

        [UnityTest]
        public IEnumerator HostApplicationExitRequiresConfirmationAndAuthorizesOnlyOneFinalQuit()
        {
            var old = textures[0]; textures[0] = new RenderTexture(960, 640, 0);
            textures[0].Create(); panels[0].targetTexture = textures[0]; old.Release(); UnityEngine.Object.Destroy(old);
            yield return StartGame();
            int quits = 0; ObserveApplicationExit(0, () => {
                quits++;
                Assert.That(boots[0].Connection.HasSession, Is.False);
                Assert.That(RequestApplicationExit(0), Is.True, "The final quit callback must not reopen its own confirmation.");
            });
            Assert.That(RequestApplicationExit(0), Is.False);
            yield return null; yield return null;
            Assert.That(roots[0].Q<Label>("omc-room-confirm-copy").text, Does.Contain("다른 참가자의 연결도 종료"));
            Assert.That(roots[0].Q<Button>("omc-room-confirm-leave").text, Is.EqualTo("게임 종료"));
            Assert.That(boots.Skip(1).All(b => b.Connection.Remote.CanSend), Is.True);
            Capture(0, "application-exit-small");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => quits == 1 && boots.Skip(1).All(b => !b.Connection.Remote.CanSend));
            Assert.That(RequestApplicationExit(0), Is.True);
            Assert.That(quits, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator GuestApplicationExitReturnsLobbySeatBeforeFinalQuit()
        {
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            int quits = 0; ObserveApplicationExit(1, () => {
                Assert.That(remote.LobbyLeaveConfirmed, Is.True);
                Assert.That(boots[1].Connection.HasSession, Is.False);
                quits++;
            });
            Assert.That(RequestApplicationExit(1), Is.False);
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => quits == 1);
            yield return Wait(() => boots[0].Connection.Remote.Lobby.MemberCount == 3);
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.False);
        }

        [UnityTest]
        public IEnumerator GuestApplicationExitDuringHandDoesNotInventFoldOrSettlement()
        {
            yield return StartGame();
            var before = JsonUtility.ToJson(Client(0).Latest.game);
            int quits = 0; ObserveApplicationExit(3, () => quits++);
            Assert.That(RequestApplicationExit(3), Is.False);
            yield return Click(3, "omc-room-confirm-leave");
            yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(quits, Is.EqualTo(1));
            Assert.That(JsonUtility.ToJson(Client(0).Latest.game), Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator ApplicationExitApprovalForClosedRoomDoesNotCoverANewRoom()
        {
            yield return EnterRoom();
            int quits = 0; ObserveApplicationExit(0, () => quits++);
            Assert.That(RequestApplicationExit(0), Is.False);
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => quits == 1 && !boots[0].Connection.HasSession);
            // The injected final callback models another module vetoing final application exit.
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(RequestApplicationExit(0), Is.False);
            Assert.That(boots[0].Connection.HasSession, Is.True);
            Assert.That(quits, Is.EqualTo(1));
            yield return Click(0, "omc-room-stay");
        }

        [UnityTest]
        public IEnumerator ApplicationExitCancellationDoesNotClearFatalConnectionState()
        {
            yield return StartGame();
            int quits = 0; ObserveApplicationExit(0, () => quits++);
            LogAssert.Expect(LogType.Error, "Multiplayer progression paused (InvalidOperationException).");
            typeof(HoldemMultiplayerBootstrap).GetMethod("StopProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boots[0], new object[] { new InvalidOperationException() });
            Assert.That(RequestApplicationExit(0), Is.False);
            yield return Click(0, "omc-room-stay");
            Assert.That(quits, Is.Zero);
            Assert.That(boots[0].HasFailed, Is.True);
            Assert.That(boots[0].Connection.Remote.CanSend, Is.False);
            yield return Click(0, "omc-room-leave");
            yield return null;
            Assert.That(roots[0].Q<Button>("omc-room-stay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator ApplicationExitExplainsPendingInputAndCancellationPreservesItsReceipt()
        {
            yield return StartGame();
            var remote = boots[3].Connection.Remote;
            var before = remote.Read();
            remote.Act(before, BettingAction.Call());
            Assert.That(remote.HasPendingInput, Is.True);
            Assert.That(RequestApplicationExit(3), Is.False);
            Assert.That(roots[3].Q<Label>("omc-room-confirm-copy").text, Does.Contain("처리 결과를 아직 확인하지 못했어요"));
            yield return Click(3, "omc-room-stay");
            yield return Wait(() => !remote.HasPendingInput && remote.Read().SessionVersion == before.SessionVersion + 1);
            Assert.That(remote.CanSend, Is.True);
            Assert.That(boots[3].Connection.HasSession, Is.True);
        }

        [UnityTest]
        public IEnumerator ApplicationExitLostLobbyReceiptCanRetryWithoutQuittingEarly()
        {
            yield return ExitWithLostLobbyReceipt(false);
        }

        [UnityTest]
        public IEnumerator ApplicationExitWaitsForSecondConfirmationWhenStartBeatsLobbyLeave()
        {
            yield return EnterRoom();
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            int quits = 0; ObserveApplicationExit(1, () => quits++);
            Assert.That(RequestApplicationExit(1), Is.False);
            boots[0].Connection.Remote.StartTable();
            var server = (HoldemTcpServer)typeof(HoldemMultiplayerConnection)
                .GetField("server", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(boots[0].Connection);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!Client(0).Latest.hasGame && elapsed.ElapsedMilliseconds < 5000)
            { server.Pump(); Client(0).Poll(); System.Threading.Thread.Sleep(1); }
            Assert.That(Client(0).Latest.hasGame, Is.True);
            var remote = boots[1].Connection.Remote;
            Assert.That(remote.HasGame, Is.False);
            Assert.That(remote.RequestLobbyLeave(), Is.True);
            foreach (string field in new[] { "leavingLobby", "leaveSent" })
                typeof(HoldemMultiplayerBootstrap).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(boots[1], true);
            yield return Wait(() => remote.HasGame && !remote.HasPendingInput);
            yield return null; yield return null;
            Assert.That(quits, Is.Zero);
            Assert.That(boots[1].Connection.HasSession, Is.True);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("이미 게임이 시작"));
            var before = JsonUtility.ToJson(Client(0).Latest.game);
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => quits == 1 && boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(JsonUtility.ToJson(Client(0).Latest.game), Is.EqualTo(before));
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator ApplicationExitLostLobbyReceiptNeedsExplicitAbandonBeforeQuitting()
        {
            yield return ExitWithLostLobbyReceipt(true);
        }

        private IEnumerator ExitWithLostLobbyReceipt(bool abandon)
        {
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            int quits = 0; ObserveApplicationExit(1, () => quits++);
            Assert.That(RequestApplicationExit(1), Is.False);
            BeginLeaveAndDropReceipt(1);
            yield return Wait(() => boots[1].Connection.CanReconnect);
            Assert.That(quits, Is.Zero);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            Assert.That(boots[1].Connection.HasSession, Is.True);
            Assert.That(RequestApplicationExit(1), Is.False);
            yield return null;
            Assert.That(roots[1].Q<Button>("omc-room-confirm-leave").text, Is.EqualTo("연결 정보 지우고 종료"));
            yield return Click(1, abandon ? "omc-room-confirm-leave" : "omc-room-leave-retry");
            yield return Wait(() => quits == 1);
            Assert.That(remote.LobbyLeaveConfirmed, Is.EqualTo(!abandon));
            Assert.That(boots[1].Connection.HasSession, Is.False);
        }
    }
}
