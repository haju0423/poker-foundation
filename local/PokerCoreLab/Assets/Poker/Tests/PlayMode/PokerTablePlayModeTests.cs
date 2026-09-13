using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed class PokerTablePlayModeTests
    {
        private GameObject go;
        private PanelSettings panel;
        private PracticeTableSettings settings;
        private PracticeTableBootstrap bootstrap;
        private UIDocument document;
        private RenderTexture texture;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("PokerTheme");
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            texture = new RenderTexture(1200, 800, 0); texture.Create(); panel.targetTexture = texture;
            settings = ScriptableObject.CreateInstance<PracticeTableSettings>(); settings.opponentDelaySeconds = 0.1f;
            go = new GameObject("Poker PlayMode Test"); go.SetActive(false);
            document = go.AddComponent<UIDocument>(); document.panelSettings = panel;
            bootstrap = go.AddComponent<PracticeTableBootstrap>(); bootstrap.Settings = settings;
            go.SetActive(true);
            for (int i = 0; i < 5; i++) yield return null;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UnityEngine.Object.Destroy(go); yield return null;
            UnityEngine.Object.Destroy(panel); UnityEngine.Object.Destroy(settings);
            texture.Release(); UnityEngine.Object.Destroy(texture);
        }
        [UnityTest]
        public IEnumerator KoreanFontAndInitialScreenContainFiveOwnCardsAndOnlyBacksForOpponent()
        {
            Font font = Resources.Load<Font>("Fonts/NanumGothic-Regular"); Assert.That(font, Is.Not.Null);
            foreach (char c in (KoreanTableText.Title + KoreanTableText.Rules).Where(c => c >= 0xAC00 && c <= 0xD7A3).Distinct())
                Assert.That(font.HasCharacter(c), Is.True, "Missing Korean glyph " + c);
            Assert.That(document.rootVisualElement.Query<Button>(className: "card").ToList().Count, Is.EqualTo(5));
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
            Assert.That(document.rootVisualElement.Query<Button>("call").First().text, Does.Contain("1칩 추가"));
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            AssertLayout();
            var inputText = document.rootVisualElement.Q<TextField>("bet-target").Q<TextElement>();
            Assert.That(inputText.resolvedStyle.color.grayscale, Is.LessThan(0.4f), "Bet input needs dark, readable text.");
            Assert.That(inputText.worldBound.height, Is.GreaterThanOrEqualTo(inputText.resolvedStyle.fontSize), "Bet text needs its natural line height.");
            Assert.That(inputText.worldBound.yMax, Is.LessThanOrEqualTo(document.rootVisualElement.Q<TextField>("bet-target").worldBound.yMax), "Bet text must fit within its field.");
            Capture("initial");
            yield return null;
        }
        [UnityTest]
        public IEnumerator ButtonCallbacksCompleteHandAndExplicitNewPracticeRebinds()
        {
            long before = bootstrap.Progress.Version; Submit("call"); Submit("call");
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(before + 1), "Rapid repeat must not send a second action.");
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.Exchange && bootstrap.Progress.OwnTurn);
            var first = document.rootVisualElement.Query<Button>(className: "card").ToList()[0];
            Submit(first);
            Assert.That(first.ClassListContains("selected") || document.rootVisualElement.Query<Button>(className: "selected").ToList().Count == 1, Is.True);
            Assert.That(document.rootVisualElement.Q<Button>("exchange").text, Does.Contain("1장"));
            yield return null; Capture("exchange"); Submit("exchange");
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.SecondBetting && bootstrap.Progress.OwnTurn);
            Submit("check");
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.Complete);
            Assert.That(document.rootVisualElement.Q<Button>("new-practice").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            yield return null; AssertLayout(); Capture("result");
            Submit("new-practice");
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting)); Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
        }
        [UnityTest]
        public IEnumerator ZeroCardConfirmationIsVisibleAndCompletesExchange()
        {
            Submit("call"); yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.Exchange && bootstrap.Progress.OwnTurn);
            Assert.That(document.rootVisualElement.Q<Button>("exchange").text, Is.EqualTo(KoreanPokerText.ExchangeLabel(0)));
            Submit("exchange"); Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.SecondBetting));
        }
        [UnityTest]
        public IEnumerator InvalidBetTextDoesNotAlterHandButValidRaiseDoes()
        {
            TextField target = document.rootVisualElement.Q<TextField>("bet-target");
            foreach (string value in new[] { "", "-2", "3.5", "abc", "9999999999999999999", "1" })
            {
                long version = bootstrap.Progress.Version; target.value = value; Submit("aggressive");
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
            }
            target.value = "8"; Submit("aggressive"); Assert.That(bootstrap.Progress.Version, Is.EqualTo(2));
            yield return null;
        }
        [UnityTest]
        public IEnumerator FoldSettlesAndShowsGrossPayoutWithoutOpponentCards()
        {
            Submit("fold"); Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(document.rootVisualElement.Query<Label>(className: "result").First().text, Does.Contain("테스트 상대"));
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
            yield return null;
        }
        [UnityTest]
        public IEnumerator HelpIsOptInAndCanCloseWithoutAdvancingHand()
        {
            long before = bootstrap.Progress.Version; Submit("help-button"); yield return null;
            Assert.That(document.rootVisualElement.Q<VisualElement>(className: "help-overlay").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Submit("close-help"); yield return null;
            Assert.That(document.rootVisualElement.Q<VisualElement>(className: "help-overlay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(before));
        }
        [UnityTest]
        public IEnumerator DisableEnableDisposesOldUiAndCreatesOneFreshPracticeBinding()
        {
            go.SetActive(false); yield return null; go.SetActive(true); yield return null;
            Assert.That(document.rootVisualElement.Query<Button>("call").ToList().Count, Is.EqualTo(1));
            Submit("call"); Assert.That(bootstrap.Progress.Version, Is.EqualTo(2));
        }
        private static IEnumerator WaitFor(Func<bool> condition)
        {
            double timeout = Time.realtimeSinceStartupAsDouble + 6;
            while (!condition() && Time.realtimeSinceStartupAsDouble < timeout) yield return null;
            Assert.That(condition(), Is.True, "Poker UI did not reach the expected phase.");
        }
#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator SavedSampleSceneLoadsItsSerializedPanelAndSettings()
        {
            go.SetActive(false);
            var saved = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Poker/Samples/PracticeTable.unity", new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
            for (int i = 0; i < 5; i++) yield return null;
            var savedDocument = saved.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<UIDocument>()).Single();
            var savedBootstrap = savedDocument.GetComponent<PracticeTableBootstrap>();
            Assert.That(savedDocument.panelSettings, Is.Not.Null, "Stored scene, not just a test fixture, needs a panel.");
            Assert.That(savedBootstrap.Settings, Is.Not.Null);
            savedDocument.panelSettings.targetTexture = texture;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(savedBootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(savedDocument.rootVisualElement.Query<Button>(className: "card").ToList().Count, Is.EqualTo(5));
            Capture("saved-scene");
            savedDocument.panelSettings.targetTexture = null;
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }
#endif
        private void AssertLayout()
        {
            var root = document.rootVisualElement;
            var opponent = root.Q<VisualElement>(className: "opponent");
            var center = root.Q<VisualElement>(className: "center-pot");
            var player = root.Q<VisualElement>(className: "player");
            Assert.That(opponent.worldBound.yMax, Is.LessThanOrEqualTo(center.worldBound.yMin + 1), "Opponent and pot must not overlap.");
            Assert.That(center.worldBound.yMax, Is.LessThanOrEqualTo(player.worldBound.yMin + 1), "Pot and own hand must not overlap.");
            Assert.That(player.worldBound.yMax, Is.LessThan(root.Q<VisualElement>(className: "table").worldBound.yMax - 8), "Own hand label must fit inside the table.");
        }
        private void Submit(string name) => Submit(document.rootVisualElement.Q<Button>(name));
        private static void Submit(Button button)
        {
            Assert.That(button, Is.Not.Null);
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }
        private void Capture(string name)
        {
            string directory = Environment.GetEnvironmentVariable("POKER_CAPTURE_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            RenderTexture prior = RenderTexture.active;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
                Color32[] pixels = image.GetPixels32();
                Assert.That(pixels.Distinct().Count(), Is.GreaterThan(100), "Capture must contain rendered content.");
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, name + ".png"), image.EncodeToPNG());
            }
            finally { RenderTexture.active = prior; UnityEngine.Object.Destroy(image); }
        }
    }
}
