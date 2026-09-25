using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed class HoldemAppPlayModeTests
    {
        private GameObject owner;
        private HoldemAppBootstrap app;
        private PanelSettings panel;
        private RenderTexture texture;
        private HoldemTableSettings solo;
        private HoldemMultiplayerSettings multi;
        private HoldemMultiplayerConnection peer;
        private bool background;
        private VisualElement Root => owner.GetComponentsInChildren<UIDocument>().Single().rootVisualElement;
        private HoldemMultiplayerBootstrap Multiplayer => owner.GetComponentInChildren<HoldemMultiplayerBootstrap>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            background = UnityEngine.Application.runInBackground;
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("PokerTheme");
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            texture = new RenderTexture(1200, 800, 0); texture.Create(); panel.targetTexture = texture;
            solo = ScriptableObject.CreateInstance<HoldemTableSettings>(); solo.opponentDelaySeconds = 0.1f;
            multi = ScriptableObject.CreateInstance<HoldemMultiplayerSettings>();
            owner = new GameObject("Unified game test"); owner.SetActive(false);
            app = owner.AddComponent<HoldemAppBootstrap>();
            app.Panel = panel; app.SoloSettings = solo; app.MultiplayerSettings = multi;
            owner.SetActive(true);
            yield return null; yield return null;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            peer?.Dispose(); peer = null;
            if (owner != null) UnityEngine.Object.Destroy(owner);
            yield return null;
            Assert.That(UnityEngine.Application.runInBackground, Is.EqualTo(background));
            UnityEngine.Object.Destroy(panel); UnityEngine.Object.Destroy(solo); UnityEngine.Object.Destroy(multi);
            texture.Release(); UnityEngine.Object.Destroy(texture);
        }

        private IEnumerator Wait(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                peer?.Poll();
                if (condition()) yield break;
                yield return null;
            }
            Assert.That(condition(), Is.True, "Timed out waiting for mode transition or room acknowledgement.");
        }

        private static void Submit(Button button)
        {
            Assert.That(button, Is.Not.Null);
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
        }

        private void Capture(string name)
        {
            string directory = Environment.GetEnvironmentVariable("OMC_APP_CAPTURE_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            System.IO.Directory.CreateDirectory(directory);
            var previous = RenderTexture.active;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name + ".png"), image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }
        }

        private IEnumerator Click(string name)
        {
            yield return null; yield return null;
            var root = Root;
            var button = root.Q<Button>(name);
            Assert.That(button, Is.Not.Null, name);
            Assert.That(button.enabledInHierarchy, Is.True, name);
            Assert.That(button.resolvedStyle.display, Is.Not.EqualTo(DisplayStyle.None), name);
            Assert.That(button.worldBound.width, Is.GreaterThan(0), name);
            var hit = root.panel.PickAll(button.worldBound.center, null);
            Assert.That(hit == button || button.Contains(hit), Is.True, "Button is covered: " + name);
            var point = button.worldBound.center;
            using (var move = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseMove, mousePosition = point }))
            { move.target = root; root.SendEvent(move); }
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { down.target = hit; hit.SendEvent(down); }
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { up.target = hit; hit.SendEvent(up); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MenuStartsNoGameAndRepeatedSelectionCreatesOnlyOneMode()
        {
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>(), Is.Null);
            Assert.That(Multiplayer, Is.Null);
            Assert.That(UnityEngine.Application.runInBackground, Is.EqualTo(background));
            Capture("menu");
            var oldSolo = Root.Q<Button>("omc-menu-solo");
            var oldMulti = Root.Q<Button>("omc-menu-multiplayer");
            Submit(oldSolo); Submit(oldSolo); Submit(oldMulti);
            yield return null; yield return null;
            Assert.That(owner.GetComponentsInChildren<HoldemTableBootstrap>().Length, Is.EqualTo(1));
            Assert.That(Multiplayer, Is.Null);
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>().Progress.SeatCount, Is.EqualTo(4));
            Capture("solo");
            yield return Click("omc-menu-return");
            yield return Click("omc-menu-confirm");
            Submit(oldSolo); Submit(oldMulti);
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>(), Is.Null);
            Assert.That(Multiplayer, Is.Null);
            Assert.That(Root.Q<Button>("omc-menu-solo"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator SoloConfirmationPausesNpcBlocksInputAndCancelKeepsTheHand()
        {
            yield return Click("omc-menu-solo");
            var boot = owner.GetComponentInChildren<HoldemTableBootstrap>();
            yield return Click("omc-menu-return");
            var version = boot.Progress.Version; var hand = boot.Progress.HandNumber;
            var oldConfirm = Root.Q<Button>("omc-menu-confirm");
            Submit(Root.Q<Button>("omc-fold")); Submit(Root.Q<Button>("omc-reset"));
            yield return new WaitForSecondsRealtime(0.35f);
            Assert.That(boot.Progress.Version, Is.EqualTo(version));
            yield return Click("omc-menu-cancel");
            Assert.That(boot.Progress.HandNumber, Is.EqualTo(hand));
            yield return Click("omc-help");
            Submit(Root.Q<Button>("omc-menu-return"));
            Assert.That(Root.Q("omc-menu-confirmation").style.display.value, Is.EqualTo(DisplayStyle.None));
            yield return Click("omc-close-help");
            yield return Click("omc-menu-return"); yield return Click("omc-menu-confirm");
            Assert.That(boot == null, Is.True);
            yield return Click("omc-menu-multiplayer");
            Submit(oldConfirm);
            Assert.That(Multiplayer, Is.Not.Null);
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>(), Is.Null);
            yield return Click("omc-room-menu");
            yield return Click("omc-menu-solo");
            Assert.That(Multiplayer, Is.Null);
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>().Progress.HandNumber, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator HostMustConfirmLeaveBeforeMenuAndCanReopenTheSamePort()
        {
            yield return Click("omc-menu-multiplayer");
            Capture("multiplayer");
            Root.Q<TextField>("omc-room-name").value = "방장";
            Root.Q<TextField>("omc-room-port").value = "0";
            yield return Click("omc-room-host");
            var boot = Multiplayer;
            yield return Wait(() => boot.Connection.Remote?.Lobby != null);
            int port = boot.Connection.Port;
            var oldMenu = Root.Q<Button>("omc-room-menu");
            Submit(oldMenu);
            Assert.That(boot.Connection.HasSession, Is.True);
            yield return Click("omc-room-leave");
            yield return Click("omc-room-stay");
            Assert.That(boot.Connection.HasSession, Is.True);
            yield return Click("omc-room-leave");
            yield return Click("omc-room-confirm-leave");
            Assert.That(boot.Connection.HasSession, Is.False);
            yield return Click("omc-room-menu");
            Assert.That(boot == null, Is.True);
            Assert.That(UnityEngine.Application.runInBackground, Is.EqualTo(background));
            yield return Click("omc-menu-multiplayer");
            Submit(oldMenu);
            Assert.That(Multiplayer, Is.Not.Null);
            Root.Q<TextField>("omc-room-name").value = "새 방장";
            Root.Q<TextField>("omc-room-port").value = port.ToString();
            yield return Click("omc-room-host");
            yield return Wait(() => Multiplayer.Connection.Remote?.Lobby != null);
            Assert.That(Multiplayer.Connection.Port, Is.EqualTo(port));
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator GuestReturnsToMenuOnlyAfterLobbySeatIsReleased()
        {
            peer = new HoldemMultiplayerConnection();
            Assert.That(peer.Host("다른 방장", "127.0.0.1", 0, false, multi.CreateConfig()), Is.True);
            yield return Wait(() => peer.Remote?.Lobby != null);
            yield return Click("omc-menu-multiplayer");
            Root.Q<TextField>("omc-room-name").value = "참가자";
            Root.Q<TextField>("omc-room-port").value = peer.Port.ToString();
            yield return Click("omc-room-join");
            var boot = Multiplayer;
            yield return Wait(() => boot.Connection.Remote?.Lobby?.MemberCount == 2);
            yield return Click("omc-room-leave"); yield return Click("omc-room-confirm-leave");
            Submit(Root.Q<Button>("omc-room-menu"));
            yield return Wait(() => !boot.Connection.HasSession && peer.Remote.Lobby.MemberCount == 1);
            Assert.That(Multiplayer, Is.SameAs(boot));
            yield return Click("omc-room-menu");
            Assert.That(Multiplayer, Is.Null);
            Assert.That(Root.Q<Button>("omc-menu-solo"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator DisablingAppDuringJoinDisposesPendingConnectionAndReenableStartsFresh()
        {
            peer = new HoldemMultiplayerConnection();
            Assert.That(peer.Host("방장", "127.0.0.1", 0, false, multi.CreateConfig()), Is.True);
            yield return Wait(() => peer.Remote?.Lobby != null);
            yield return Click("omc-menu-multiplayer");
            var connection = Multiplayer.Connection;
            Assert.That(connection.Join("취소한 참가자", "127.0.0.1", peer.Port, false), Is.True);
            Assert.That(connection.IsConnecting, Is.True);
            owner.SetActive(false);
            for (int i = 0; i < 10; i++) { peer.Poll(); yield return null; }
            Assert.That(connection.HasSession, Is.False);
            Assert.That(connection.IsConnecting, Is.False);
            Assert.That(peer.Remote.Lobby.MemberCount, Is.EqualTo(1));
            owner.SetActive(true); yield return null; yield return null;
            Assert.That(Root.Q<Button>("omc-menu-solo"), Is.Not.Null);
            Assert.That(owner.GetComponentsInChildren<UIDocument>().Length, Is.EqualTo(1));
            Assert.That(UnityEngine.Application.runInBackground, Is.EqualTo(background));
        }

        [UnityTest]
        public IEnumerator CompactWindowKeepsMenuAndSoloNavigationReachable()
        {
            texture.Release(); texture.width = 900; texture.height = 650; texture.Create();
            yield return null; yield return null;
            Capture("menu-compact");
            yield return Click("omc-menu-solo");
            yield return null;
            Capture("solo-compact");
            yield return Click("omc-menu-return");
            yield return Click("omc-menu-cancel");
            Assert.That(owner.GetComponentInChildren<HoldemTableBootstrap>(), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator UnifiedSoloRunsProductionNpcsToHandEndAndStartsNextHand()
        {
            yield return Click("omc-menu-solo");
            var boot = owner.GetComponentInChildren<HoldemTableBootstrap>();
            double deadline = Time.realtimeSinceStartupAsDouble + 30;
            while (boot.Progress.Street != HoldemStreet.Complete && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var passive = Root.Q<Button>("omc-passive");
                if (passive.enabledInHierarchy && passive.resolvedStyle.display != DisplayStyle.None) Submit(passive);
                yield return null;
            }
            Assert.That(boot.Progress.Street, Is.EqualTo(HoldemStreet.Complete));
            long hand = boot.Progress.HandNumber;
            yield return Wait(() => Root.Q<Button>("omc-next").enabledInHierarchy);
            yield return Click("omc-next");
            Assert.That(boot.Progress.HandNumber, Is.EqualTo(hand + 1));
        }
    }
}
