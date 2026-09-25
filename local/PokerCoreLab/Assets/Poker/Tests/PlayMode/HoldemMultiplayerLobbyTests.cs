using System;
using System.Collections;
using System.IO;
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
        private readonly GameObject[] objects = new GameObject[4];
        private readonly HoldemMultiplayerBootstrap[] boots = new HoldemMultiplayerBootstrap[4];
        private readonly PanelSettings[] panels = new PanelSettings[4];
        private readonly RenderTexture[] textures = new RenderTexture[4];
        private readonly VisualElement[] roots = new VisualElement[4];
        private HoldemMultiplayerSettings settings;
        private string stage;
        private bool originalBackground;

        [UnitySetUp]
        public IEnumerator Setup() => SetupLobby(false);

        private IEnumerator SetupLobby(bool flowPreview)
        {
            originalBackground = UnityEngine.Application.runInBackground;
            settings = ScriptableObject.CreateInstance<HoldemMultiplayerSettings>();
            settings.enableFlowPreview = flowPreview;
            for (int i = 0; i < 4; i++)
            {
                panels[i] = ScriptableObject.CreateInstance<PanelSettings>();
                panels[i].themeStyleSheet = Resources.Load<ThemeStyleSheet>("PokerTheme");
                panels[i].scaleMode = PanelScaleMode.ConstantPixelSize;
                textures[i] = new RenderTexture(1200, 800, 0); textures[i].Create(); panels[i].targetTexture = textures[i];
                objects[i] = new GameObject("Multiplayer lobby " + i); objects[i].SetActive(false);
                var document = objects[i].AddComponent<UIDocument>(); document.panelSettings = panels[i];
                boots[i] = objects[i].AddComponent<HoldemMultiplayerBootstrap>(); boots[i].Settings = settings;
                objects[i].SetActive(true); roots[i] = document.rootVisualElement;
            }
            yield return null; yield return null;
            Assert.That(UnityEngine.Application.runInBackground, Is.True);
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            for (int i = 0; i < 4; i++) if (objects[i] != null) UnityEngine.Object.Destroy(objects[i]);
            yield return null;
            Assert.That(UnityEngine.Application.runInBackground, Is.EqualTo(originalBackground));
            for (int i = 0; i < 4; i++)
            {
                if (panels[i] != null) UnityEngine.Object.Destroy(panels[i]);
                if (textures[i] != null) { textures[i].Release(); UnityEngine.Object.Destroy(textures[i]); }
                objects[i] = null; boots[i] = null; panels[i] = null; textures[i] = null; roots[i] = null;
            }
            if (settings != null) UnityEngine.Object.Destroy(settings); settings = null;
        }
        private IEnumerator Wait(Func<bool> condition)
        {
            double end = Time.realtimeSinceStartupAsDouble + 8;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(condition(), Is.True, "Lobby condition exceeded its bounded wait after " + stage + "\n"
                + string.Join("\n", roots.Select(r => r?.Q<Label>("omc-room-status")?.text)));
        }
        private bool Pickable(int viewer, string name)
        {
            var b = roots[viewer].Q<Button>(name);
            if (b == null || !b.enabledInHierarchy || b.resolvedStyle.display == DisplayStyle.None || b.worldBound.width <= 0) return false;
            // Separate RenderTexture panels share pointer 0 in this fixture. PickAll recomputes the hit
            // instead of reusing another panel's pointer cache (Unity Panel.Pick caches the mouse hit).
            var picked = roots[viewer].panel.PickAll(b.worldBound.center, null);
            return picked == b || b.Contains(picked);
        }
        private IEnumerator Click(int viewer, string name)
        {
            stage = "click " + viewer + " " + name;
            double end = Time.realtimeSinceStartupAsDouble + 8;
            while (!Pickable(viewer, name) && Time.realtimeSinceStartupAsDouble < end) yield return null;
            if (!Pickable(viewer, name))
            {
                Capture(viewer, "blocked");
                var blocked = roots[viewer].Q<Button>(name);
                var pickedElements = new System.Collections.Generic.List<VisualElement>();
                roots[viewer].panel.PickAll(blocked.worldBound.center, pickedElements);
                Assert.Fail(stage + " enabled=" + blocked?.enabledInHierarchy + " bound=" + blocked?.worldBound
                    + " picked=" + Describe(roots[viewer].panel.Pick(blocked.worldBound.center))
                    + " all=" + string.Join(";", pickedElements.Select(Describe))
                    + " canSend=" + boots[viewer].Connection.Remote?.CanSend
                    + " pending=" + boots[viewer].Connection.Remote?.HasPendingInput
                    + " status=" + roots[viewer].Q<Label>("omc-room-status").text);
            }
            var b = roots[viewer].Q<Button>(name); var point = b.worldBound.center; var picked = roots[viewer].panel.PickAll(point, null);
            // Emulate entering this panel before pressing. Without a move, another offscreen panel's
            // mouse-pointer cache can retain null at the identical coordinates during event dispatch.
            foreach (var hover in new[] { point + new Vector2(3, 0), point })
                using (var e = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = hover }))
                { e.target = roots[viewer]; roots[viewer].SendEvent(e); }
            using (var e = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { e.target = picked; picked.SendEvent(e); }
            using (var e = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { e.target = picked; picked.SendEvent(e); }
        }
        private static string Describe(VisualElement element) => element == null ? "null"
            : element.GetType().Name + ":" + element.name + ":" + string.Join(",", element.GetClasses());
        private void Fields(int viewer, int selectedPort, string nickname = null)
        {
            roots[viewer].Q<TextField>("omc-room-name").value = nickname ?? "참가자 " + (viewer + 1);
            roots[viewer].Q<TextField>("omc-room-address").value = "127.0.0.1";
            roots[viewer].Q<TextField>("omc-room-port").value = selectedPort.ToString();
        }
        private IEnumerator EnterRoom(IRandomSource random = null, string[] names = null)
        {
            Fields(0, 0, names?[0]);
            if (random == null) yield return Click(0, "omc-room-host");
            else Assert.That(boots[0].Connection.Host(names?[0] ?? "참가자 1", "127.0.0.1", 0, false, settings.CreateConfig(), random,
                settings.CreateUtterancePolicy()), Is.True);
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Port, Is.GreaterThan(0));
            Assert.That(roots[0].Q<Label>("omc-room-endpoint").text, Does.Contain(boots[0].Connection.Port.ToString()));
            for (int i = 1; i < 4; i++)
            {
                Fields(i, boots[0].Connection.Port, names?[i]); yield return Click(i, "omc-room-join");
                int viewer = i; yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.MemberCount == 4));
        }
        private IEnumerator StartGame(IRandomSource random = null, string[] names = null)
        {
            yield return EnterRoom(random, names);
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => roots.All(r => r.Q<Button>("omc-passive") != null));
        }

        [UnityTest]
        public IEnumerator RealLobbyButtonsReadyStartPlayAndReconnectWithoutLocalNpcs()
        {
            Capture(0, "entry");
            yield return EnterRoom();
            Assert.That(roots[0].Q<Button>("omc-room-start").enabledInHierarchy, Is.False);
            foreach (var boot in boots) Assert.That(boot.Connection.Remote.HasGame, Is.False);
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return null; yield return null;
            Capture(0, "ready");
            yield return Click(0, "omc-room-start");
            yield return Wait(() => roots.All(r => r.Q<Button>("omc-passive") != null));
            yield return null; yield return null;
            Assert.That(objects.All(o => o.GetComponent<HoldemTableBootstrap>() == null), Is.True);
            for (int i = 0; i < 4; i++)
            { Assert.That(boots[i].Connection.Remote.Read().ViewerSeat.Value, Is.EqualTo(i + 1)); Capture(i, "table"); }

            long version = boots[0].Connection.Remote.Read().SessionVersion;
            var remote = boots[3].Connection.Remote;
            var originalCards = Enumerable.Range(0, 2).Select(remote.Read().GetOwnCard).ToArray();
            // Fault injection only: the recovery path below is the real visible button and owner callback.
            ((HoldemTcpClient)typeof(HoldemMultiplayerConnection).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(boots[3].Connection)).Dispose();
            yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(remote.CanSend, Is.False);
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
            Assert.That(boots[3].Connection.Remote, Is.SameAs(remote));
            Assert.That(remote.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(Enumerable.Range(0, 2).Select(remote.Read().GetOwnCard), Is.EqualTo(originalCards));
            yield return Click(3, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion == version + 1));
            yield return null; yield return null;
            Capture(3, "reconnected");
        }

        [UnityTest]
        public IEnumerator ExtendedMatchUsesRealButtonsThroughEliminationAndTheLastSurvivor()
        {
            var random = new OwnedByCallerRandom();
            yield return StartGame(random);
            var choose = typeof(HoldemMultiplayerBootstrap).Assembly.GetType("Poker.Runtime.HoldemMultiplayerProcessCheck")
                .GetMethod("MatchAction", BindingFlags.Static | BindingFlags.NonPublic);
            var actions = new System.Collections.Generic.HashSet<BettingActionKind>();
            bool sawShortTable = false, sawEliminatedHostNext = false;
            for (int hand = 1; hand <= 12; hand++)
            {
                var current = Client(0).Latest.game;
                int dealt = current.seats.Count(s => s.dealtIn);
                sawShortTable |= dealt < 4;
                for (int step = 0; step < 80 && !Client(0).Latest.game.hasResult; step++)
                {
                    int viewer = Client(0).Latest.game.currentSeat - 1;
                    yield return Wait(() => boots[viewer].Connection.Remote.CanSend && boots[viewer].Connection.Remote.Read().LegalActions != null);
                    var basis = boots[viewer].Connection.Remote.Read();
                    var action = (BettingAction)choose.Invoke(null, new object[] { basis, dealt });
                    Assert.That(basis.LegalActions.Allows(action), Is.True);
                    actions.Add(action.Kind);
                    string button = action.Kind == BettingActionKind.Fold ? "omc-fold"
                        : action.Kind == BettingActionKind.BetTo || action.Kind == BettingActionKind.RaiseTo ? "omc-aggressive" : "omc-passive";
                    if (button == "omc-aggressive") roots[viewer].Q<TextField>("omc-target").value = action.Target.ToString();
                    long version = basis.SessionVersion;
                    yield return Click(viewer, button);
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > version)
                        && !boots[viewer].Connection.Remote.HasPendingInput);
                }
                var result = Client(0).Latest.game;
                Assert.That(result.hasResult, Is.True);
                Assert.That(result.seats.Sum(s => s.stack), Is.EqualTo(400));
                if (result.isOver) break;
                Assert.That(result.canContinue, Is.True);
                sawEliminatedHostNext |= result.seats.Single(s => s.seat == 1).stack == 0;
                yield return Click(0, "omc-next");
                int nextHand = hand + 1;
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == nextHand));
                foreach (var boot in boots)
                {
                    var view = boot.Connection.Remote.Read();
                    if (!view.GetSeat(view.ViewerSeat).WasDealtIn)
                    { Assert.That(view.OwnCardCount, Is.Zero); Assert.That(view.LegalActions, Is.Null); }
                }
            }
            var final = Client(0).Latest.game;
            Assert.That(final.isOver, Is.True, "The extended scenario must terminate within twelve hands.");
            Assert.That(final.canContinue, Is.False);
            Assert.That(final.seats.Count(s => s.stack > 0), Is.EqualTo(1));
            Assert.That(sawShortTable, Is.True);
            Assert.That(actions, Does.Contain(BettingActionKind.RaiseTo));
            Assert.That(actions, Does.Contain(BettingActionKind.Fold));
            Assert.That(actions, Does.Contain(BettingActionKind.Call));
            Debug.Log("OMC_EXTENDED_UI_OK hands=" + final.handNumber + " survivor=" + final.sessionWinner
                + " eliminatedHostNext=" + sawEliminatedHostNext);
            Assert.That(random.Calls, Is.GreaterThan(51));
            var finalStates = Enumerable.Range(0, 4).Select(i => JsonUtility.ToJson(Client(i).Latest.game)).ToArray();
            for (int i = 0; i < 4; i++)
            {
                Assert.That(roots[i].Q<Button>("omc-room-leave").text, Is.EqualTo("처음 화면으로"));
                Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                yield return Click(i, "omc-history");
                string historyCopy = roots[i].Q<Label>("omc-history-copy").text;
                Assert.That(historyCopy, Is.Not.Empty);
                yield return Click(i, "omc-room-leave");
                Assert.That(roots[i].Q<Label>("omc-room-confirm-copy").text, Does.Contain("게임이 끝났어요"));
                Assert.That(roots[i].Q<Label>("omc-room-confirm-copy").text, Does.Not.Contain("진행 중"));
                Assert.That(roots[i].Q<Button>("omc-room-stay").text, Is.EqualTo("결과 더 보기"));
                Assert.That(roots[i].Q<Button>("omc-room-confirm-leave").text,
                    Is.EqualTo(i == 0 ? "방 닫고 돌아가기" : "처음 화면으로"));
                yield return Click(i, "omc-room-stay");
                Assert.That(JsonUtility.ToJson(Client(i).Latest.game), Is.EqualTo(finalStates[i]));
                Assert.That(boots[i].Connection.HasSession, Is.True);
                Assert.That(roots[i].Q("omc-history-dialog").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(roots[i].Q<Label>("omc-history-copy").text, Is.EqualTo(historyCopy));
                yield return Click(i, "omc-close-history");
            }
            Assert.That(final.sessionWinner, Is.EqualTo(2), "This fixture must exercise the funded winner leaving.");
            yield return Click(1, "omc-room-leave"); yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => !boots[1].Connection.HasSession
                && !Client(0).Latest.members.Single(m => m.seat == 2).connected);
            Assert.That(roots[1].Q<Button>("omc-room-host").enabledInHierarchy, Is.True);
            foreach (int i in new[] { 0, 2, 3 })
            {
                Assert.That(boots[i].Connection.HasSession, Is.True, "One guest returning home must not close the room.");
                Assert.That(JsonUtility.ToJson(Client(i).Latest.game), Is.EqualTo(finalStates[i]));
                Assert.That(boots[i].Connection.Remote.Lobby.Paused, Is.False);
                Assert.That(roots[i].Q<Label>(className: "omc-prompt").text, Is.EqualTo("테이블 승부가 끝났어요."));
                Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                yield return Click(i, "omc-history");
                Assert.That(roots[i].Q<Label>("omc-history-copy").text, Is.Not.Empty);
                yield return Click(i, "omc-close-history");
            }
            Capture(0, "completed-match-winner-left");
            yield return Click(0, "omc-room-leave"); yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession
                && new[] { 2, 3 }.All(i => !boots[i].Connection.Remote.CanSend));
            Assert.That(roots[0].Q<Button>("omc-room-host").enabledInHierarchy, Is.True);
            Assert.That(random.Disposed, Is.False, "The connection must not dispose caller-owned randomness.");
            random.Dispose();
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Remote.HasGame, Is.False);
            Assert.That(roots[0].Q<Button>("omc-room-leave").text, Is.EqualTo("나가기"));
        }

        private sealed class OwnedByCallerRandom : IRandomSource, IDisposable
        {
            private readonly System.Random random = new System.Random(1295);
            public int Calls { get; private set; }
            public bool Disposed { get; private set; }
            public int NextInt(int exclusiveMax) { Calls++; return random.Next(exclusiveMax); }
            public void Dispose() { Disposed = true; }
        }

        [UnityTest]
        public IEnumerator AnEliminatedGuestCanExitWithoutBlockingTheRemainingThreePlayers()
        {
            yield return StartGame(new SeededRandom(1295));
            roots[3].Q<TextField>("omc-target").value = "100";
            yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1
                && boots[3].Connection.Remote.Read().OwnStack == 0);
            Assert.That(boots[3].Connection.Remote.Read().OwnStack, Is.Zero);
            Assert.That(boots[3].Connection.Remote.Read().IsOver, Is.False);
            Assert.That(roots[3].Q<Button>("omc-room-leave").text, Is.EqualTo("나가기"),
                "An unsettled all-in is not the end of the match.");
            yield return Click(0, "omc-fold");
            yield return Wait(() => Client(1).Latest.game.currentSeat == 2);
            yield return Click(1, "omc-passive");
            yield return Wait(() => Client(2).Latest.game.currentSeat == 3);
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            int eliminated = Client(0).Latest.game.seats.Single(s => s.stack == 0).seat - 1;
            Assert.That(eliminated, Is.Not.Zero);
            Assert.That(boots[eliminated].Connection.Remote.Read().IsOver, Is.False);
            Assert.That(roots[eliminated].Q<Button>("omc-room-leave").text, Is.EqualTo("나가기"));
            Assert.That(roots[eliminated].Q<Label>(className: "omc-prompt").text, Does.Contain("관전해요"));
            Assert.That(roots[eliminated].Q<Label>(className: "omc-prompt").text, Does.Not.Contain("이어서 플레이"));
            yield return Click(eliminated, "omc-room-leave");
            yield return Click(eliminated, "omc-room-confirm-leave");
            yield return Wait(() => !boots[eliminated].Connection.HasSession
                && !Client(0).Latest.members.Single(m => m.seat == eliminated + 1).connected);
            Assert.That(boots[0].Connection.Remote.CanSend, Is.True);
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.Where((b, i) => i != eliminated).All(b => b.Connection.Remote.Read().HandNumber == 2));
            Assert.That(Client(0).Latest.game.seats.Count(s => s.dealtIn), Is.EqualTo(3));
            int actor = Client(0).Latest.game.currentSeat - 1;
            long version = Client(0).Latest.game.version;
            yield return Click(actor, "omc-passive");
            yield return Wait(() => boots.Where((b, i) => i != eliminated).All(b => b.Connection.Remote.Read().SessionVersion > version));
        }

        [UnityTest]
        public IEnumerator GuestCanLeaveLobbyAndAFriendCanTakeOnlyThatSeat()
        {
            yield return EnterRoom();
            yield return Click(1, "omc-room-ready");
            yield return Wait(() => boots[1].Connection.Remote.Lobby.IsReady && !boots[1].Connection.Remote.HasPendingInput);
            yield return Click(1, "omc-room-leave");
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("다른 친구가 참가"));
            yield return Click(1, "omc-room-stay");
            Assert.That(boots[1].Connection.Remote.Lobby.IsReady, Is.True);
            yield return Click(1, "omc-room-leave");
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => !boots[1].Connection.HasSession && boots[0].Connection.Remote.Lobby.MemberCount == 3);
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.False);
            Assert.That(roots[0].Q<Button>("omc-room-start").enabledInHierarchy, Is.False);
            Fields(1, boots[0].Connection.Port); roots[1].Q<TextField>("omc-room-name").value = "새 친구";
            yield return Click(1, "omc-room-join");
            yield return Wait(() => boots.All(b => b.Connection.Remote?.Lobby?.MemberCount == 4));
            Assert.That(boots[1].Connection.Remote.Lobby.ViewerSeat, Is.EqualTo(2));
            Assert.That(boots[1].Connection.Remote.Lobby.IsReady, Is.False);
            Assert.That(boots[2].Connection.Remote.Lobby.ViewerSeat, Is.EqualTo(3));
            Assert.That(boots[3].Connection.Remote.Lobby.ViewerSeat, Is.EqualTo(4));
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => boots.All(b => b.Connection.Remote.HasGame));
            Assert.That(boots[0].Connection.Remote.Read().HandNumber, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator GuestCanLeaveLobbyWhileAnotherGuestIsDisconnected()
        {
            yield return EnterRoom();
            Client(3).Dispose();
            yield return Wait(() => boots[1].Connection.Remote.Lobby.Paused);
            Assert.That(boots[1].Connection.Remote.CanSend, Is.False);
            Assert.That(boots[1].Connection.Remote.CanLeaveLobby, Is.True);
            yield return Click(1, "omc-room-leave");
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => !boots[1].Connection.HasSession && boots[0].Connection.Remote.Lobby.MemberCount == 3);
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.True);
            Assert.That(boots[0].Connection.Remote.Lobby.GetMember(2).Seat, Is.EqualTo(4));
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => !boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(boots[3].Connection.Remote.Lobby.ViewerSeat, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator LostLobbyLeaveReceiptRecoversThroughTheConfirmationButton()
        {
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            BeginLeaveAndDropReceipt(1);
            yield return Wait(() => boots[1].Connection.CanReconnect);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            yield return null; yield return null;
            Assert.That(roots[1].Q<Button>("omc-room-stay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[1].Q<Button>("omc-room-confirm-leave").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(roots[1].Q<Button>("omc-room-confirm-leave").text, Is.EqualTo("연결 정보 지우고 나가기"));
            Assert.That(boots[1].Connection.HasSession, Is.True, "Offering local exit must not discard the reconnect identity.");
            yield return Click(1, "omc-room-leave-retry");
            yield return Wait(() => !boots[1].Connection.HasSession);
            Assert.That(remote.LobbyLeaveConfirmed, Is.True);
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(3));
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.False);
        }

        [UnityTest]
        public IEnumerator AnUnrecoverableIdentityDuringLeaveDoesNotTrapTheConfirmation()
        {
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            BeginLeaveAndDropReceipt(1);
            var server = (HoldemTcpServer)typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(boots[0].Connection);
            var admissions = (System.Collections.IDictionary)typeof(HoldemTcpServer)
                .GetField("admissions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            object retiredKey = null;
            foreach (System.Collections.DictionaryEntry entry in admissions)
                if ((Guid)entry.Value.GetType().GetField("LeftCommandId").GetValue(entry.Value) != Guid.Empty) retiredKey = entry.Key;
            Assert.That(retiredKey, Is.Not.Null);
            admissions.Remove(retiredKey); // Fault: authority can no longer identify the departing guest.
            yield return Wait(() => boots[1].Connection.CanReconnect);
            yield return Click(1, "omc-room-leave-retry");
            yield return Wait(() => remote.HasUnrecoverableAdmissionError);
            yield return null; yield return null;
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("퇴장이 처리됐는지 확인하지 못했어요"));
            Assert.That(roots[1].Q<Button>("omc-room-stay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[1].Q<Button>("omc-room-leave-retry").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            yield return Click(1, "omc-room-confirm-leave");
            Assert.That(boots[1].Connection.HasSession, Is.False);
        }

        [UnityTest]
        public IEnumerator AReplacedRoomDuringLostLeaveRecoveryOffersExplicitExitWithoutClaimingSuccess()
        {
            yield return EnterRoom();
            var remote = boots[1].Connection.Remote;
            BeginLeaveAndDropReceipt(1);
            var serverField = typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic);
            var previous = (HoldemTcpServer)serverField.GetValue(boots[0].Connection);
            var endpoint = previous.Endpoint; previous.Dispose();
            var replacement = new HoldemTcpServer(new HoldemClientIdentity("새 방장"), settings.CreateConfig(), new SeatId(1), new StableRandom(), endpoint);
            serverField.SetValue(boots[0].Connection, replacement);
            yield return Wait(() => boots[1].Connection.CanReconnect);
            yield return Click(1, "omc-room-leave-retry");
            yield return Wait(() => remote.HasUnrecoverableAdmissionError);
            yield return null; yield return null;
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            Assert.That(boots[1].Connection.HasSession, Is.True, "Exit requires the player's explicit confirmation.");
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("기존 방이 종료"));
            Assert.That(roots[1].Q<Button>("omc-room-stay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[1].Q<Button>("omc-room-confirm-leave").text, Is.EqualTo("처음 화면으로"));
            yield return Click(1, "omc-room-confirm-leave");
            Assert.That(boots[1].Connection.HasSession, Is.False);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
        }

        private HoldemTcpClient Client(int viewer) => (HoldemTcpClient)typeof(HoldemMultiplayerConnection)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(boots[viewer].Connection);

        [UnityTest]
        public IEnumerator AGameStartingBeforeLobbyLeaveRequiresASecondConfirmation()
        {
            yield return EnterRoom();
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(1, "omc-room-leave");
            // Force the host's start to win at authority while the guest still sees the preceding lobby.
            boots[0].Connection.Remote.StartTable();
            var server = (HoldemTcpServer)typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(boots[0].Connection);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!Client(0).Latest.hasGame && elapsed.ElapsedMilliseconds < 5000)
            { server.Pump(); Client(0).Poll(); System.Threading.Thread.Sleep(1); }
            Assert.That(Client(0).Latest.hasGame, Is.True);
            var remote = boots[1].Connection.Remote;
            Assert.That(remote.HasGame, Is.False);
            Assert.That(remote.RequestLobbyLeave(), Is.True);
            foreach (string field in new[] { "leavingLobby", "leaveSent" })
                typeof(HoldemMultiplayerBootstrap).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(boots[1], true);
            yield return Wait(() => remote.HasGame && !remote.HasPendingInput);
            yield return null; yield return null;
            Assert.That(boots[1].Connection.HasSession, Is.True);
            Assert.That(remote.LobbyLeaveConfirmed, Is.False);
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Contain("이미 게임이 시작"));
            yield return Click(1, "omc-room-stay");
            Assert.That(boots[1].Connection.HasSession, Is.True);
            Assert.That(remote.Read().HandNumber, Is.EqualTo(1));
            yield return Click(1, "omc-room-leave");
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => !boots[1].Connection.HasSession && boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(4), "Started seats remain reserved; local exit is not a fold.");
        }

        [UnityTest]
        public IEnumerator AServerWithoutLobbyLeaveCapabilityUsesTheExplicitLocalExit()
        {
            yield return EnterRoom();
            Client(1).Latest.supportsLobbyLeave = false;
            typeof(HoldemRemoteTablePort).GetField("observedRevision", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(boots[1].Connection.Remote, -1L);
            boots[1].Connection.Remote.Poll();
            Assert.That(boots[1].Connection.Remote.CanLeaveLobby, Is.False);
            yield return Click(1, "omc-room-leave");
            Assert.That(roots[1].Q<Label>("omc-room-confirm-copy").text, Does.Not.Contain("다른 친구가 참가"));
            yield return Click(1, "omc-room-confirm-leave");
            yield return Wait(() => !boots[1].Connection.HasSession && boots[0].Connection.Remote.Lobby.Paused);
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator FullBootstrapJourneyFinishesThreeHandsAndCarriesChipsAndButtonForward()
        {
            yield return StartGame();
            for (int hand = 1; hand <= 3; hand++)
            {
                int expectedHand = hand;
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == expectedHand && b.Connection.Remote.CanSend));
                var first = Client(0).Latest.game;
                int initialButton = first.button;
                for (int action = 0; action < 80 && !Client(0).Latest.game.hasResult; action++)
                {
                    int viewer = Client(0).Latest.game.currentSeat - 1;
                    Assert.That(viewer, Is.InRange(0, 3));
                    long version = Client(0).Latest.game.version;
                    yield return Click(viewer, "omc-passive");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > version)
                        && !boots[viewer].Connection.Remote.HasPendingInput);
                }
                Assert.That(Client(0).Latest.game.hasResult, Is.True);
                Assert.That(boots.All(b => b.Connection.Remote.Read().HandNumber == hand), Is.True);
                Assert.That(Client(0).Latest.game.seats.Sum(p => p.stack), Is.EqualTo(400));
                for (int i = 1; i < 4; i++)
                    Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                if (hand == 3) break;
                var endingStacks = Client(0).Latest.game.seats.ToDictionary(p => p.seat, p => p.stack);
                yield return Click(0, "omc-next");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == expectedHand + 1));
                var next = Client(0).Latest.game;
                Assert.That(next.button, Is.Not.EqualTo(initialButton));
                foreach (var player in next.seats)
                    Assert.That(player.stack + player.committed, Is.EqualTo(endingStacks[player.seat]));
            }
        }

        private void BeginLeaveAndDropReceipt(int viewer)
        {
            // Fault injection pauses owner polling just long enough to consume and lose its acknowledgement.
            // Normal leave/cancel uses panel pointer events in the other regression tests.
            foreach (string field in new[] { "confirming", "leavingLobby", "leaveSent" })
                typeof(HoldemMultiplayerBootstrap).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(boots[viewer], true);
            var remote = boots[viewer].Connection.Remote;
            var client = Client(viewer);
            Assert.That(remote.RequestLobbyLeave(), Is.True);
            var server = (HoldemTcpServer)typeof(HoldemMultiplayerConnection).GetField("server", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(boots[0].Connection);
            bool discarded = false;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!discarded && elapsed.ElapsedMilliseconds < 5000)
            {
                server.Pump(); client.Poll();
                while (client.TryReadResponse(out var response)) if (response.type == "receipt" && response.accepted) discarded = true;
                System.Threading.Thread.Sleep(1);
            }
            Assert.That(discarded, Is.True, "The accepted leave receipt must actually be lost.");
            Assert.That(remote.IsLobbyLeavePending, Is.True);
            client.Dispose();
        }
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly System.Random random;
            public SeededRandom(int seed) { random = new System.Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }

        [UnityTest]
        public IEnumerator LeaveIsExplicitCancelKeepsGameAndHostCloseStopsEveryone()
        {
            yield return StartGame();
            var room = boots[0].Connection.Remote.Lobby.SessionId;
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            Assert.That(roots[0].Q<Button>("omc-room-leave").text, Is.EqualTo("나가기"));
            yield return Click(0, "omc-room-leave");
            Assert.That(boots[0].Connection.HasSession, Is.True);
            Assert.That(roots[0].Q<Label>("omc-room-confirm-copy").text, Does.Contain("다른 참가자의 연결도 종료"));
            Assert.That(roots[0].Q<Label>("omc-room-confirm-copy").text, Does.Contain("진행 중인 게임"));
            Assert.That(roots[0].Q<Button>("omc-room-stay").text, Is.EqualTo("계속 플레이"));
            yield return Click(0, "omc-room-stay");
            Assert.That(boots[0].Connection.Remote.Lobby.SessionId, Is.EqualTo(room));
            Assert.That(boots[0].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            yield return Click(0, "omc-room-leave");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession && boots.Skip(1).All(b => !b.Connection.Remote.CanSend));
            Assert.That(roots[0].Q<Button>("omc-passive"), Is.Null);
            Assert.That(roots[0].Q<Button>("omc-room-host").enabledInHierarchy, Is.True);
        }

        [UnityTest]
        public IEnumerator FatalErrorKeepsExitOnlyStateUntilExplicitReturnHome()
        {
            yield return StartGame();
            var remote = boots[0].Connection.Remote;
            long version = remote.Read().SessionVersion;
            LogAssert.Expect(LogType.Error, "Multiplayer progression paused (InvalidOperationException).");
            typeof(HoldemMultiplayerBootstrap).GetMethod("StopProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boots[0], new object[] { new InvalidOperationException() });
            yield return null; yield return null;
            Assert.That(boots[0].HasFailed, Is.True);
            Assert.That(remote.CanSend, Is.False);
            Assert.That(boots[0].Connection.CanReconnect, Is.False);
            Assert.That(roots[0].Q<Button>("omc-retry").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[0].Q<Button>("omc-room-reconnect").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[0].Q<Label>("omc-room-endpoint").text, Does.Contain("진행을 멈췄어요"));
            yield return Click(0, "omc-room-leave");
            yield return null;
            Assert.That(roots[0].Q<Button>("omc-room-stay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(roots[0].Q<Label>("omc-room-endpoint").text, Does.Contain("진행을 멈췄어요"));
            Assert.That(remote.Read().SessionVersion, Is.EqualTo(version));
            Capture(0, "terminal");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession);
            Assert.That(boots[0].HasFailed, Is.False);
            Assert.That(roots[0].Q<Button>("omc-passive"), Is.Null);
            Assert.That(roots[0].Q<Button>("omc-room-host").enabledInHierarchy, Is.True);
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Remote.HasGame, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-endpoint").text, Does.Not.Contain("진행을 멈췄어요"));
        }

        [UnityTest]
        public IEnumerator FatalErrorBeforeJoiningStillOffersReturnHome()
        {
            LogAssert.Expect(LogType.Error, "Multiplayer progression paused (InvalidOperationException).");
            typeof(HoldemMultiplayerBootstrap).GetMethod("StopProgress", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(boots[0], new object[] { new InvalidOperationException() });
            yield return Click(0, "omc-room-leave");
            yield return Click(0, "omc-room-confirm-leave");
            Assert.That(boots[0].HasFailed, Is.False);
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Button>("omc-room-host").enabledInHierarchy, Is.True);
        }

        [UnityTest]
        public IEnumerator LanSetupFitsSmallWindowWithoutCoveringPrimaryButtons()
        {
            var old = textures[0];
            textures[0] = new RenderTexture(960, 640, 0); textures[0].Create(); panels[0].targetTexture = textures[0];
            old.Release(); UnityEngine.Object.Destroy(old);
            roots[0].Q<Toggle>("omc-room-lan").value = true;
            yield return null; yield return null; yield return null;
            Capture(0, "lan-small");
            Assert.That(roots[0].Q("omc-lan-picker").worldBound.yMax,
                Is.LessThanOrEqualTo(roots[0].Q("omc-room-instructions").worldBound.yMin), "LAN instructions cannot overlap the address buttons.");
            foreach (string name in new[] { "omc-room-host", "omc-room-join", "omc-room-refresh-addresses" })
            {
                var button = roots[0].Q<Button>(name);
                roots[0].Q<ScrollView>("omc-lobby").ScrollTo(button);
                yield return null; yield return null;
                Assert.That(Pickable(0, name), Is.True, name);
                Assert.That(button.worldBound.yMax, Is.LessThanOrEqualTo(roots[0].worldBound.yMax), name);
                Assert.That(button.worldBound.xMax, Is.LessThanOrEqualTo(roots[0].worldBound.xMax), name);
            }
            roots[0].Q<ScrollView>("omc-lobby").ScrollTo(roots[0].Q<Label>("omc-room-status"));
            yield return null; yield return null;
            Assert.That(roots[0].Q<Label>("omc-room-status").worldBound.yMax, Is.LessThanOrEqualTo(roots[0].worldBound.yMax));
        }

        [UnityTest]
        public IEnumerator LanSelectionOnlyFillsTheAddressAndRejectsALoopbackHost()
        {
            Fields(0, 7777);
            roots[0].Q<Toggle>("omc-room-lan").value = true;
            yield return null; yield return null;
            Assert.That(boots[0].Connection.HasSession, Is.False);
            var choices = roots[0].Q<DropdownField>("omc-room-local-address");
            Assert.That(choices.index, Is.Zero, "Do not select a network on the user's behalf.");
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(boots[0].Connection.IsHosting, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("사설 IP"));
            if (choices.choices.Count > 1)
            {
                choices.index = 1;
                yield return Click(0, "omc-room-use-address");
                Assert.That(roots[0].Q<TextField>("omc-room-address").value, Is.EqualTo(choices.value.Split(' ')[0]));
                Assert.That(boots[0].Connection.HasSession, Is.False, "Selecting a hint must not open a socket.");
            }
            yield return Click(0, "omc-room-refresh-addresses");
            Assert.That(choices.index, Is.Zero);
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Capture(0, "lan");
            roots[0].Q<Toggle>("omc-room-lan").value = false;
            roots[0].Q<TextField>("omc-room-address").value = "127.0.0.1";
            roots[0].Q<TextField>("omc-room-port").value = "0";
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
        }

        [UnityTest]
        public IEnumerator InvalidInputNeverStartsAListenerAndReportsAnActionableMessage()
        {
            Fields(0, 7777); roots[0].Q<TextField>("omc-room-port").value = "oops";
            yield return Click(0, "omc-room-host"); yield return null;
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("숫자"));
            roots[0].Q<TextField>("omc-room-port").value = "7777";
            roots[0].Q<TextField>("omc-room-address").value = "0.0.0.0";
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(boots[0].Connection.IsHosting, Is.False);
            Capture(0, "invalid");
        }

        private void Capture(int viewer, string label)
        {
            string directory = Environment.GetEnvironmentVariable("OMC_CAPTURE_DIR"); if (string.IsNullOrEmpty(directory)) return;
            var old = RenderTexture.active; var source = textures[viewer]; var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source; image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); image.Apply();
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "lobby-" + label + "-" + (viewer + 1) + ".png"), image.EncodeToPNG());
            }
            finally { RenderTexture.active = old; UnityEngine.Object.Destroy(image); }
        }
    }
}
