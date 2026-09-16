using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed class HoldemTablePlayModeTests
    {
        private GameObject go;
        private UIDocument document;
        private PanelSettings panel;
        private RenderTexture texture;
        private HoldemLocalTable table;
        private HoldemTableScreen screen;
        private int restarts;
        private VisualElement Root => document.rootVisualElement;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            restarts = 0;
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("PokerTheme");
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            texture = new RenderTexture(1200, 800, 0); texture.Create(); panel.targetTexture = texture;
            go = new GameObject("Holdem UI test");
            document = go.AddComponent<UIDocument>(); document.panelSettings = panel;
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), new FixedRandom(), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            for (int i = 0; i < 5; i++) yield return null;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            screen?.Dispose(); UnityEngine.Object.Destroy(go); yield return null;
            UnityEngine.Object.Destroy(panel);
            texture.Release(); UnityEngine.Object.Destroy(texture);
        }

        [UnityTest]
        public IEnumerator InitialKoreanScreenHasTwoPrivateCardsFiveBoardSlotsAndNoExchange()
        {
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            foreach (char c in (HoldemTableScreen.HelpText + "♣♦♥♠◇").Where(c => !char.IsWhiteSpace(c)).Distinct())
                Assert.That(font.HasCharacter(c), Is.True, "Missing font glyph " + c);
            Assert.That(Root.Q("omc-own-cards").childCount, Is.EqualTo(2));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Assert.That(Root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(5));
            Assert.That(Root.Q<Button>("exchange"), Is.Null);
            Assert.That(Root.Q<Label>(className: "omc-title").text, Is.EqualTo("One More Card"));
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("콜  1칩"));
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.Preflop));
            AssertLayout(); AssertButtonHit("omc-passive"); Capture("initial");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RealCallbacksReachShowdownAndNextHandPreservesChipsAndHidesOpponentAgain()
        {
            yield return CompletePassive();
            var settled = table.Human.Read();
            Assert.That(settled.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Root.Query(className: "face-card").ToList().Count, Is.EqualTo(9));
            Assert.That(Root.Query(className: "best").ToList().Count, Is.EqualTo(5));
            Assert.That(Root.Q<Label>(className: "omc-result").text, Does.Contain("무승부"));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Does.Contain("쇼다운"));
            yield return WaitEnabled("omc-next");
            AssertLayout(); Capture("showdown");
            foreach (var size in new[] { new Vector2Int(960, 640), new Vector2Int(1280, 720) })
            {
                yield return Resize(size); AssertLayout(); AssertButtonHit("omc-next");
                Capture("showdown-" + size.x + "x" + size.y);
            }
            Submit("omc-next"); yield return null;
            var next = table.Human.Read();
            Assert.That(next.HandNumber, Is.EqualTo(2));
            Assert.That(next.OwnStack + 2, Is.EqualTo(settled.OwnStack));
            Assert.That(next.OpponentStack + 1, Is.EqualTo(settled.OpponentStack));
            Assert.That(next.ButtonSeat, Is.EqualTo(next.OpponentSeat));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Assert.That(Root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(5));
            Assert.That(Root.Query(className: "best").ToList(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator FoldNeverRevealsOpponentAndOnlyExplicitRestartCallsReset()
        {
            Submit("omc-reset"); Assert.That(restarts, Is.Zero);
            Submit("omc-fold"); Submit("omc-fold");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(2));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(99));
            Assert.That(table.Human.Read().OpponentStack, Is.EqualTo(101));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Does.Contain("공개하지"));
            yield return WaitEnabled("omc-reset");
            AssertLayout(); Capture("fold");
            Submit("omc-reset"); Assert.That(restarts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator InvalidRaiseAndRapidDuplicateCannotAlterStateTwice()
        {
            var input = Root.Q<TextField>("omc-target");
            foreach (string value in new[] { "", "-2", "3.5", "abc", "3", "101", "9999999999999999999" })
            {
                input.value = value; Submit("omc-aggressive");
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(1));
                Assert.That(Root.Q<Button>("omc-aggressive").enabledInHierarchy, Is.False);
            }
            input.value = "8"; Submit("omc-aggressive"); Submit("omc-aggressive");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(2));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(92));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ModalHelpBlocksUnderlyingInputWithoutChangingHandAndDisposedButtonsDoNothing()
        {
            long before = table.Human.Read().SessionVersion;
            Submit("omc-help"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.True);
            Submit("omc-fold"); Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before));
            var passive = Root.Q<Button>("omc-passive");
            var picked = Root.panel.Pick(passive.worldBound.center);
            Assert.That(IsDescendant(picked, passive), Is.False, "Help overlay must intercept pointer hit testing.");
            Capture("help"); Submit("omc-close-help"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.False);
            Button oldFold = Root.Q<Button>("omc-fold"); screen.Dispose(); Submit(oldFold);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator NarrowInitialScreenKeepsCardsTextAndActionsWithinPanel()
        {
            yield return Resize(new Vector2Int(960, 640));
            AssertLayout(); AssertButtonHit("omc-aggressive"); Capture("initial-960x640");
            Submit("omc-help"); yield return null;
            Assert.That(Root.Q<VisualElement>(className: "omc-help-card").worldBound.yMax, Is.LessThanOrEqualTo(Root.worldBound.yMax));
            Assert.That(Root.Q<VisualElement>(className: "omc-help-card").worldBound.yMin, Is.GreaterThanOrEqualTo(Root.worldBound.yMin));
        }

        [UnityTest]
        public IEnumerator PausedRecoveryRequiresExplicitResetConfirmationAndCanKeepCurrentHand()
        {
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), () => restarts++);
            screen.PauseProgress(); Submit("omc-reset"); yield return null;
            Assert.That(restarts, Is.Zero);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(1));
            Submit("omc-cancel-reset"); Assert.That(restarts, Is.Zero);
            Submit("omc-reset"); Submit("omc-confirm-reset"); Assert.That(restarts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator NonTieShowdownShowsTheActualWinnerAndEvaluatedHand()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), new SeededRandom(41), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return CompletePassive();
            var v = table.Human.Read(); Assert.That(v.Result.WinnerSeat, Is.Not.Null);
            bool own = v.Result.WinnerSeat == v.ViewerSeat;
            string winner = own ? "나 승리" : "상대 승리";
            var value = own ? v.Result.OwnHandValue.Value : v.Result.OpponentHandValue.Value;
            Assert.That(Root.Q<Label>(className: "omc-result").text,
                Is.EqualTo(winner + " · " + Poker.Presentation.KoreanPokerText.HandName(value)));
            Assert.That(Root.Query<Label>(className: "current").ToList(), Is.Empty);
            Assert.That(Root.Query<Label>(className: "past").ToList().Count, Is.EqualTo(4));
            yield return WaitEnabled("omc-next"); Capture("winning-showdown");
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator SavedHoldemSceneRunsProductionOpponentCompletesAndExplicitlyRestarts()
        {
            screen.Dispose(); go.SetActive(false);
            var saved = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Poker/Samples/HoldemTable.unity",
                new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
            UIDocument savedDoc = null;
            try
            {
                for (int i = 0; i < 5; i++) yield return null;
                savedDoc = saved.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UIDocument>()).Single();
                var bootstrap = savedDoc.GetComponent<HoldemTableBootstrap>();
                Assert.That(bootstrap.Settings, Is.Not.Null); Assert.That(savedDoc.panelSettings, Is.Not.Null);
                savedDoc.panelSettings.targetTexture = texture;
                double deadline = Time.realtimeSinceStartupAsDouble + 35;
                while (bootstrap.Progress.Street != HoldemStreet.Complete && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    var button = savedDoc.rootVisualElement.Q<Button>("omc-passive");
                    if (button.enabledInHierarchy && button.style.display.value != DisplayStyle.None) Submit(button);
                    yield return null;
                }
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Complete));
                Assert.That(savedDoc.rootVisualElement.Q("omc-board").Query(className: "face-card").ToList().Count, Is.EqualTo(5));
                double enabledAt = Time.realtimeSinceStartupAsDouble + 2;
                while (!savedDoc.rootVisualElement.Q<Button>("omc-reset").enabledInHierarchy && Time.realtimeSinceStartupAsDouble < enabledAt) yield return null;
                Submit(savedDoc.rootVisualElement.Q<Button>("omc-reset")); yield return null;
                Assert.That(bootstrap.Progress.HandNumber, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Preflop));
                Assert.That(savedDoc.rootVisualElement.Q("omc-own-cards").childCount, Is.EqualTo(2));
                Capture("saved-scene");
            }
            finally { if (savedDoc != null && savedDoc.panelSettings != null) savedDoc.panelSettings.targetTexture = null; }
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }
#endif
        private IEnumerator CompletePassive()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (table.Human.Read().Result == null && Time.realtimeSinceStartupAsDouble < deadline)
            {
                string[] names = { "프리플랍", "플랍", "턴", "리버" };
                var current = Root.Query<Label>(className: "current").ToList();
                Assert.That(current.Count, Is.EqualTo(1));
                Assert.That(current[0].text, Is.EqualTo(names[(int)table.Human.Read().Street]));
                if (table.Human.Read().LegalActions == null) { table.AdvanceOpponent(); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(table.Human.Read().Result, Is.Not.Null); yield return null;
        }
        private IEnumerator WaitEnabled(string name)
        {
            double end = Time.realtimeSinceStartupAsDouble + 2;
            while (!Root.Q<Button>(name).enabledInHierarchy && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(Root.Q<Button>(name).enabledInHierarchy, Is.True); yield return null;
        }
        private IEnumerator Resize(Vector2Int size)
        {
            var old = texture; texture = new RenderTexture(size.x, size.y, 0); texture.Create(); panel.targetTexture = texture;
            for (int i = 0; i < 5; i++) yield return null;
            old.Release(); UnityEngine.Object.Destroy(old);
        }
        private void AssertLayout()
        {
            var opponent = Root.Q(className: "omc-opponent"); var middle = Root.Q(className: "omc-middle"); var own = Root.Q(className: "omc-player");
            Assert.That(opponent.worldBound.yMax, Is.LessThanOrEqualTo(middle.worldBound.yMin + 1));
            Assert.That(middle.worldBound.yMax, Is.LessThanOrEqualTo(own.worldBound.yMin + 1));
            Assert.That(own.worldBound.yMax, Is.LessThanOrEqualTo(Root.Q(className: "omc-table").worldBound.yMax - 5));
            foreach (var element in Root.Query<Button>().ToList().Cast<VisualElement>().Concat(Root.Query(className: "omc-card").ToList()))
            {
                if (element.resolvedStyle.display == DisplayStyle.None || !element.visible || !AncestorsDisplayed(element)) continue;
                Assert.That(element.worldBound.xMin, Is.GreaterThanOrEqualTo(Root.worldBound.xMin - 1), element.name);
                Assert.That(element.worldBound.xMax, Is.LessThanOrEqualTo(Root.worldBound.xMax + 1), element.name);
                Assert.That(element.worldBound.yMax, Is.LessThanOrEqualTo(Root.worldBound.yMax + 1), element.name);
            }
            foreach (var card in Root.Query(className: "omc-card").ToList())
                foreach (var label in card.Query<Label>().ToList())
                {
                    Assert.That(label.worldBound.xMax, Is.LessThanOrEqualTo(card.worldBound.xMax + 1));
                    Assert.That(label.worldBound.yMax, Is.LessThanOrEqualTo(card.worldBound.yMax + 1));
                }
        }
        private static bool AncestorsDisplayed(VisualElement element)
        {
            for (var current = element.parent; current != null; current = current.parent)
                if (current.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }
        private void AssertButtonHit(string name)
        { var button = Root.Q<Button>(name); Assert.That(IsDescendant(Root.panel.Pick(button.worldBound.center), button), Is.True, name + " is occluded."); }
        private static bool IsDescendant(VisualElement element, VisualElement parent)
        { for (var current = element; current != null; current = current.parent) if (current == parent) return true; return false; }
        private void Submit(string name) => Submit(Root.Q<Button>(name));
        private static void Submit(Button button)
        { Assert.That(button, Is.Not.Null); using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); } }
        private void Capture(string name)
        {
            string directory = Environment.GetEnvironmentVariable("OMC_CAPTURE_DIR"); if (string.IsNullOrEmpty(directory)) return;
            var prior = RenderTexture.active;
            var capture = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture; capture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); capture.Apply();
                Assert.That(capture.GetPixels32().Distinct().Count(), Is.GreaterThan(100));
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, name + ".png"), capture.EncodeToPNG());
            }
            finally { RenderTexture.active = prior; UnityEngine.Object.Destroy(capture); }
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class SeededRandom : IRandomSource
        { private readonly System.Random random; public SeededRandom(int seed) { random = new System.Random(seed); } public int NextInt(int upper) => random.Next(upper); }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot view, IRandomSource random) => view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
    }
}
