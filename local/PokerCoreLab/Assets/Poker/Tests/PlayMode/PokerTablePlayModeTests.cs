using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
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
            Assert.That(document.rootVisualElement.Query<Label>(className: "seat-status").ToList().Select(label => label.text),
                Does.Contain("이번 베팅 총 2칩"));
            Assert.That(document.rootVisualElement.Q<Label>(className: "footnote"), Is.Null);
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
            yield return ReachOwnExchangeThroughLegalResponses();
            var first = document.rootVisualElement.Query<Button>(className: "card").ToList()[0];
            Submit(first);
            Assert.That(first.ClassListContains("selected") || document.rootVisualElement.Query<Button>(className: "selected").ToList().Count == 1, Is.True);
            Assert.That(document.rootVisualElement.Q<Button>("exchange").text, Does.Contain("1장"));
            yield return null; Capture("exchange"); Submit("exchange");
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.SecondBetting && bootstrap.Progress.OwnTurn);
            SubmitPassiveLegalResponse();
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.Complete);
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "revealed-card").ToList().Count, Is.EqualTo(5));
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "card-back").ToList(), Is.Empty);
            Assert.That(document.rootVisualElement.Q<Label>(className: "showdown-result").text, Does.Match("승리|무승부"));
            Assert.That(document.rootVisualElement.Q<Button>("new-practice").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            yield return null; AssertLayout(); Capture("result");
            foreach (var size in new[] { new Vector2Int(960, 640), new Vector2Int(1280, 720) })
            {
                yield return Resize(size); Capture("showdown-" + size.x + "x" + size.y);
                AssertLayout(); AssertHorizontalBounds();
                foreach (var card in document.rootVisualElement.Query<VisualElement>(className: "revealed-card").ToList())
                    foreach (var label in card.Query<Label>().ToList())
                    {
                        Assert.That(label.worldBound.yMin, Is.GreaterThanOrEqualTo(card.worldBound.yMin));
                        Assert.That(label.worldBound.yMax, Is.LessThanOrEqualTo(card.worldBound.yMax));
                    }
                Capture("showdown-" + size.x + "x" + size.y);
            }
            Submit("new-practice");
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting)); Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
        }
        [UnityTest]
        public IEnumerator NextHandCarriesSettledChipsAndOnlyExplicitRestartResetsThem()
        {
            Submit("fold"); Submit("next-hand"); yield return null;
            var root = document.rootVisualElement;
            Assert.That(root.Query<Label>(className: "seat-title").ToList().Select(x => x.text),
                Is.EquivalentTo(new[] { "나  ·  98칩", "상대  ·  99칩" }));
            Assert.That(root.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
            Assert.That(root.Query<VisualElement>(className: "revealed-card").ToList(), Is.Empty);
            Submit("fold"); Submit("next-hand"); yield return null;
            Assert.That(root.Query<Label>(className: "seat-title").ToList().Select(x => x.text),
                Is.EquivalentTo(new[] { "나  ·  97칩", "상대  ·  100칩" }));
            Submit("fold"); Submit("new-practice"); yield return null;
            Assert.That(root.Query<Label>(className: "seat-title").ToList().Select(x => x.text),
                Is.EquivalentTo(new[] { "나  ·  99칩", "상대  ·  98칩" }));
        }
        [UnityTest]
        public IEnumerator ZeroCardConfirmationIsVisibleAndCompletesExchange()
        {
            Submit("call"); yield return ReachOwnExchangeThroughLegalResponses();
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
                Assert.That(document.rootVisualElement.Q<Button>("aggressive").enabledInHierarchy, Is.False);
            }
            target.value = "8";
            Assert.That(document.rootVisualElement.Q<Button>("aggressive").enabledInHierarchy, Is.True);
            Submit("aggressive"); Assert.That(bootstrap.Progress.Version, Is.EqualTo(2));
            yield return null;
        }
        [UnityTest]
        public IEnumerator FoldSettlesAndShowsGrossPayoutWithoutOpponentCards()
        {
            Submit("fold"); Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(document.rootVisualElement.Query<Label>(className: "result").First().text, Is.EqualTo("팟 지급 완료 · 상대에게 2칩 지급"));
            Assert.That(document.rootVisualElement.Q<Label>(className: "last-action").text, Is.EqualTo("나 · 폴드 · 상대에게 1칩 반환"));
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
            yield return null;
            Assert.That(document.rootVisualElement.Q<Label>(className: "last-action").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(document.rootVisualElement.Q<Label>(className: "showdown-result").text, Is.EqualTo("상대 승리 · 나 폴드 · 패 비공개"));
        }
        [UnityTest]
        public IEnumerator DefaultOpponentRespondsToHumanRaiseWithoutStalling()
        {
            document.rootVisualElement.Q<TextField>("bet-target").value = "8";
            Submit("aggressive");
            long submittedVersion = bootstrap.Progress.Version;
            yield return WaitFor(() => bootstrap.Progress.OwnTurn || bootstrap.Progress.Phase == HandPhase.Complete);
            Assert.That(bootstrap.Progress.Version, Is.GreaterThan(submittedVersion));
            Assert.That(bootstrap.Progress.Phase,
                Is.EqualTo(HandPhase.FirstBetting).Or.EqualTo(HandPhase.Exchange).Or.EqualTo(HandPhase.Complete));
            if (bootstrap.Progress.Phase == HandPhase.FirstBetting)
                Assert.That(document.rootVisualElement.Q<Button>("call").enabledInHierarchy, Is.True);
            Assert.That(document.rootVisualElement.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
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

        [UnityTest]
        public IEnumerator LastActionUsesAcceptedFactsAndDoesNotAdvanceWhenRendered()
        {
            var root = document.rootVisualElement;
            Assert.That(root.Q<Label>(className: "last-action").text, Is.Empty);
            Submit("call");
            Assert.That(root.Q<Label>(className: "last-action").text, Is.EqualTo("나 · 콜 · 1칩 추가"));
            yield return null; AssertLayout();
            yield return ReachOwnExchangeThroughLegalResponses();
            Assert.That(root.Q<Label>(className: "last-action").text, Does.StartWith("상대 · "));
            Submit("exchange");
            Assert.That(root.Q<Label>(className: "last-action").text, Is.EqualTo("나 · 교환 없이 패 유지"));
            yield return null; AssertLayout();
            long version = bootstrap.Progress.Version;
            Submit("help-button"); Submit("close-help");
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
        }

        [UnityTest]
        public IEnumerator ResultBreakdownIsOptInAndNeverRevealsOpponentCards()
        {
            var root = document.rootVisualElement;
            Submit("result-details"); yield return null;
            Assert.That(root.Q<VisualElement>(className: "result-overlay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Submit("fold"); yield return null;
            Assert.That(root.Q<VisualElement>(className: "result-overlay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            long version = bootstrap.Progress.Version; Submit("result-details"); yield return null;
            var overlay = root.Q<VisualElement>(className: "result-overlay");
            Assert.That(overlay.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            string details = overlay.Q<Label>(className: "help-text").text;
            Assert.That(details, Does.Contain("메인 팟 · 2칩 → 상대 2칩"));
            Assert.That(details, Does.Contain("첫 베팅 반환 · 상대에게 1칩"));
            Assert.That(details, Does.Contain("최종 보유 · 나 99칩 / 상대 101칩"));
            Assert.That(root.Query<VisualElement>(className: "card-back").ToList().Count, Is.EqualTo(5));
            Capture("result-details");
            Submit("help-button"); yield return null;
            Assert.That(overlay.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Submit("result-details"); yield return null;
            Assert.That(root.Q<VisualElement>(className: "help-overlay").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Submit("close-result"); yield return null;
            Assert.That(overlay.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
        }

        [UnityTest]
        public IEnumerator RetainedButtonFromDisposedScreenCannotRestartALaterCompletedPractice()
        {
            Submit("fold"); Button oldRestart = document.rootVisualElement.Q<Button>("new-practice");
            Submit(oldRestart); Submit("fold");
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
            long before = bootstrap.Progress.Version; Submit(oldRestart);
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(before));
            Submit("new-practice"); Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            yield return null;
        }

        [UnityTest]
        public IEnumerator RepeatedNewPracticesUseTheLiveRandomServiceAndOnlyOneCurrentUi()
        {
            for (int hand = 0; hand < 12; hand++)
            {
                Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
                Submit("fold"); Button oldFold = document.rootVisualElement.Q<Button>("fold");
                Button oldRestart = document.rootVisualElement.Q<Button>("new-practice");
                Submit(oldRestart);
                Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Submit(oldRestart); Submit(oldFold);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Assert.That(document.rootVisualElement.Query<Button>("call").ToList().Count, Is.EqualTo(1));
                Assert.That(document.rootVisualElement.Query<Button>(className: "card").ToList().Count, Is.EqualTo(5));
                yield return null;
            }
            yield return ReachOwnExchangeThroughLegalResponses(); Submit("exchange");
            yield return WaitFor(() => bootstrap.Progress.Phase == HandPhase.SecondBetting && bootstrap.Progress.OwnTurn);
            SubmitPassiveLegalResponse(); Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
        }

        [UnityTest]
        public IEnumerator InvalidNewPracticeSettingsKeepTheCompletedScreenAndCanBeCorrected()
        {
            Submit("fold"); long before = bootstrap.Progress.Version;
            Button restart = document.rootVisualElement.Q<Button>("new-practice");
            string result = document.rootVisualElement.Q<Label>(className: "result").text;
            settings.startingStack = 0;
            LogAssert.Expect(LogType.Error, "Poker practice start failed (ArgumentException); no automatic retry was performed.");
            Submit(restart); yield return null;
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete)); Assert.That(bootstrap.Progress.Version, Is.EqualTo(before));
            Assert.That(document.rootVisualElement.Q<Button>("new-practice"), Is.SameAs(restart));
            Assert.That(document.rootVisualElement.Q<Label>(className: "result").text, Is.EqualTo(result));
            Assert.That(document.rootVisualElement.Q<Label>(className: "message").text, Does.Contain("현재 정산 결과를 유지"));
            AssertLayout(); Capture("restart-settings-error");
            settings.startingStack = 100; Submit(restart);
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(document.rootVisualElement.Q<Label>(className: "message").text, Is.Empty);
        }

        [UnityTest]
        public IEnumerator ChangingTheHumanSeatCannotSilentlyRebindAnActivePracticeHost()
        {
            Submit("fold"); long before = bootstrap.Progress.Version;
            settings.humanSeat = 2;
            LogAssert.Expect(LogType.Error, "Poker practice start failed (InvalidOperationException); no automatic retry was performed.");
            Submit("new-practice");
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete)); Assert.That(bootstrap.Progress.Version, Is.EqualTo(before));
            settings.humanSeat = 1; Submit("new-practice");
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            yield return null;
        }

        [UnityTest]
        public IEnumerator BetPresetsOnlyChangeTheInputUntilThePlayerConfirms()
        {
            long version = bootstrap.Progress.Version;
            TextField target = document.rootVisualElement.Q<TextField>("bet-target");
            Submit("maximum"); Assert.That(target.value, Is.EqualTo("100"));
            Submit("half-pot"); Assert.That(target.value, Is.EqualTo("4"));
            target.value = "25"; Submit("minimum"); Assert.That(target.value, Is.EqualTo("4"));
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
            Assert.That(document.rootVisualElement.Q<Button>("half-pot").tooltip, Does.Contain("자동으로 베팅하지 않아요"));
            Submit("aggressive"); Assert.That(bootstrap.Progress.Version, Is.EqualTo(version + 1));
            yield return null;
        }

        [UnityTest]
        public IEnumerator PendingReceiptLocksAmountPresetsUntilTheExactRequestIsRecovered()
        {
            bootstrap.enabled = false;
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var port = new LostOncePort(table.Human); var input = new PokerInputController(port);
            var screen = new PokerTableScreen(document.rootVisualElement, input, () => { }, settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            try
            {
                TextField target = document.rootVisualElement.Q<TextField>("bet-target"); target.value = "8";
                LogAssert.Expect(LogType.Error, "Poker input failed (IOException); pending request retained, no automatic reset.");
                Submit("aggressive"); Assert.That(input.IsPending, Is.True);
                foreach (string name in new[] { "minimum", "half-pot", "maximum" })
                {
                    Assert.That(document.rootVisualElement.Q<Button>(name).enabledInHierarchy, Is.False);
                    Submit(name); Assert.That(target.value, Is.EqualTo("8"));
                }
                Assert.That(target.enabledInHierarchy, Is.False); Assert.That(port.Submissions, Is.EqualTo(1));
                Submit("retry"); Assert.That(input.IsPending, Is.False); Assert.That(input.View.Version, Is.EqualTo(2));
                Assert.That(port.Submissions, Is.EqualTo(2));
                yield return null;
            }
            finally { screen.Dispose(); }
        }

        [UnityTest]
        public IEnumerator ActiveOpponentRaisesAndBetsThroughTheVisibleCallButton()
        {
            bootstrap.enabled = false;
            // Identity shuffle gives both players two pairs: deterministic value aggression, not a lucky random deal.
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var input = new PokerInputController(table.Human);
            var screen = new PokerTableScreen(document.rootVisualElement, input, () => { }, settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            try
            {
                Submit("call"); Assert.That(table.AdvanceOpponent(), Is.True);
                input.Refresh(); screen.Render();
                Assert.That(input.View.LastTransition.HasValue, Is.True);
                Assert.That(input.View.LastTransition.Value.BettingAction, Is.EqualTo(BettingActionKind.RaiseTo));
                Assert.That(document.rootVisualElement.Q<Button>("check").style.display.value, Is.EqualTo(DisplayStyle.None));
                SubmitPassiveLegalResponse(); Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Exchange));
                Assert.That(table.AdvanceOpponent(), Is.True); input.Refresh(); screen.Render();
                Submit("exchange"); Assert.That(table.AdvanceOpponent(), Is.True);
                input.Refresh(); screen.Render();
                Assert.That(input.View.LastTransition.HasValue, Is.True);
                Assert.That(input.View.LastTransition.Value.BettingAction, Is.EqualTo(BettingActionKind.BetTo));
                Assert.That(document.rootVisualElement.Q<Button>("call").style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(document.rootVisualElement.Q<Button>("check").style.display.value, Is.EqualTo(DisplayStyle.None));
                yield return null; AssertLayout(); Capture("facing-value-bet");
                SubmitPassiveLegalResponse(); Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Complete));
            }
            finally { screen.Dispose(); }
        }

        [UnityTest]
        public IEnumerator CommonWindowSizesKeepCardsControlsAndResultInsideThePanel()
        {
            var sizes = new[] { new Vector2Int(960, 640), new Vector2Int(1024, 768), new Vector2Int(1280, 720), new Vector2Int(1440, 900) };
            foreach (Vector2Int size in sizes)
            {
                yield return Resize(size); AssertLayout(); AssertHorizontalBounds();
                Capture("initial-" + size.x + "x" + size.y);
            }
            Submit("fold");
            foreach (Vector2Int size in sizes)
            {
                yield return Resize(size); Capture("result-" + size.x + "x" + size.y);
                AssertLayout(); AssertHorizontalBounds();
                Submit("result-details"); yield return null;
                var overlay = document.rootVisualElement.Q<VisualElement>(className: "result-overlay");
                Assert.That(overlay.worldBound.yMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(overlay.worldBound.yMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.yMax));
                Capture("details-" + size.x + "x" + size.y);
                Submit("close-result");
            }
        }

        private IEnumerator Resize(Vector2Int size)
        {
            RenderTexture previous = texture;
            texture = new RenderTexture(size.x, size.y, 0); texture.Create(); panel.targetTexture = texture;
            for (int i = 0; i < 5; i++) yield return null;
            previous.Release(); UnityEngine.Object.Destroy(previous);
        }

        [UnityTest]
        public IEnumerator PausedScreenBlocksOldActionsAndPresetsButKeepsHelpAndExplicitRecovery()
        {
            bootstrap.enabled = false;
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var input = new PokerInputController(table.Human); PokerTableScreen screen = null; int recoveries = 0;
            screen = new PokerTableScreen(document.rootVisualElement, input, () => Assert.Fail("Paused screen cannot restart."),
                settings.startingStack, Resources.Load<Font>("Fonts/NanumGothic-Regular"), () =>
                { recoveries++; Submit("progress-retry"); screen.ResumeProgress(); });
            try
            {
                long version = input.View.Version; var target = document.rootVisualElement.Q<TextField>("bet-target");
                target.value = "8"; screen.PauseProgress(PracticeProgressFailure.OpponentAction);
                string stopped = document.rootVisualElement.Q<Label>(className: "message").text;
                foreach (string name in new[] { "fold", "check", "call", "aggressive", "exchange", "new-practice", "retry", "minimum", "half-pot", "maximum" })
                { Assert.That(document.rootVisualElement.Q<Button>(name).enabledInHierarchy, Is.False, name); Submit(name); }
                Submit(document.rootVisualElement.Query<Button>(className: "card").ToList()[0]);
                Assert.That(input.View.Version, Is.EqualTo(version)); Assert.That(input.SelectedCount, Is.Zero); Assert.That(target.value, Is.EqualTo("8"));
                Submit("help-button"); yield return null; Submit("close-help");
                // Trusted external progress may update the copied view; rendering must not dismiss the pause.
                input.Bet(BettingAction.Call()); screen.Render();
                Assert.That(document.rootVisualElement.Q<Label>(className: "message").text, Is.EqualTo(stopped));
                foreach (var size in new[] { new Vector2Int(960, 640), new Vector2Int(1200, 800) })
                {
                    yield return Resize(size); AssertLayout(); AssertHorizontalBounds();
                    var message = document.rootVisualElement.Q<Label>(className: "message");
                    Assert.That(message.worldBound.yMax, Is.LessThanOrEqualTo(document.rootVisualElement.worldBound.yMax));
                    Capture("paused-action-" + size.x + "x" + size.y);
                }
                Submit("progress-retry"); Submit("progress-retry"); Assert.That(recoveries, Is.EqualTo(1));
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
                Button oldRetry = document.rootVisualElement.Q<Button>("progress-retry"); screen.PauseProgress(PracticeProgressFailure.Display);
                screen.Dispose(); Submit(oldRetry); Assert.That(recoveries, Is.EqualTo(1));
            }
            finally { screen.Dispose(); }
        }

        [UnityTest]
        public IEnumerator FailedRecoveryCallbackKeepsTheScreenPausedAndDoesNotRetryAutomatically()
        {
            bootstrap.enabled = false; var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var input = new PokerInputController(table.Human); int calls = 0; PokerTableScreen screen = null;
            screen = new PokerTableScreen(document.rootVisualElement, input, () => { }, settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), () => { calls++; screen.ResumeProgress(); throw new IOException("not logged"); });
            try
            {
                screen.PauseProgress(PracticeProgressFailure.OpponentAction);
                LogAssert.Expect(LogType.Error, "Poker progress recovery callback failed (IOException); no automatic retry.");
                Submit("progress-retry");
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").text, Is.EqualTo("화면 다시 확인"));
                Assert.That(document.rootVisualElement.Q<Button>("call").enabledInHierarchy, Is.False);
                for (int i = 0; i < 10; i++) yield return null;
                Assert.That(calls, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(1));
            }
            finally { screen.Dispose(); }
        }

        [UnityTest]
        public IEnumerator BootstrapShowsOpponentFailureAndOnlyRetriesAfterTheActualButton()
        {
            // Test-only private composition replacement. No runtime fault controls or authority API are added.
            var table = BootstrapField<LocalPokerTable>("table"); var input = BootstrapField<PokerInputController>("input");
            var screen = BootstrapField<PokerTableScreen>("screen"); int attempts = 0;
            SetBootstrapProgress(new PracticeProgressRunner(() => { if (++attempts == 1) throw new IOException(); return table.AdvanceOpponent(); },
                () => { input.Refresh(); screen.ResumeProgress(); }));
            Submit("call"); long before = input.View.Version; Guid hand = input.View.HandId;
            LogAssert.Expect(LogType.Error, "Poker practice paused at OpponentAction (IOException); no automatic retry or reset.");
            yield return WaitFor(() => document.rootVisualElement.Q<Button>("progress-retry").style.display.value == DisplayStyle.Flex);
            Assert.That(document.rootVisualElement.Q<Button>("progress-retry").text, Is.EqualTo("상대 진행 다시 시도"));
            for (int i = 0; i < 20; i++) yield return null;
            Assert.That(attempts, Is.EqualTo(1)); Assert.That(table.Human.Read().Version, Is.EqualTo(before));
            Assert.That(document.rootVisualElement.Q<Button>("retry").style.display.value, Is.EqualTo(DisplayStyle.None));
            Submit("progress-retry"); Submit("progress-retry");
            Assert.That(attempts, Is.EqualTo(2)); Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.View.HandId, Is.EqualTo(hand));
            Assert.That(document.rootVisualElement.Q<Label>(className: "message").text, Is.Empty);
        }

        [UnityTest]
        public IEnumerator BootstrapReadRecoveryDoesNotRepeatTheAppliedOpponentAction()
        { yield return BootstrapDisplayFailure(false); }

        [UnityTest]
        public IEnumerator BootstrapPostRefreshDisplayRecoveryDoesNotRepeatTheAppliedOpponentAction()
        { yield return BootstrapDisplayFailure(true); }

        private IEnumerator BootstrapDisplayFailure(bool afterRead)
        {
            var table = BootstrapField<LocalPokerTable>("table"); var input = BootstrapField<PokerInputController>("input");
            var screen = BootstrapField<PokerTableScreen>("screen"); int actions = 0; bool fail = true;
            SetBootstrapProgress(new PracticeProgressRunner(() => { actions++; return table.AdvanceOpponent(); }, () =>
            { if (afterRead) input.Refresh(); if (fail) throw new IOException(); input.Refresh(); screen.ResumeProgress(); }));
            Submit("call"); long before = input.View.Version; Guid hand = input.View.HandId;
            LogAssert.Expect(LogType.Error, "Poker practice paused at Display (IOException); no automatic retry or reset.");
            yield return WaitFor(() => document.rootVisualElement.Q<Button>("progress-retry").style.display.value == DisplayStyle.Flex);
            Assert.That(table.Human.Read().Version, Is.EqualTo(before + 1));
            Assert.That(input.View.Version, Is.EqualTo(before + (afterRead ? 1 : 0)));
            Assert.That(document.rootVisualElement.Q<Button>("progress-retry").text, Is.EqualTo("화면 다시 확인"));
            yield return null; AssertLayout(); Capture(afterRead ? "paused-post-refresh" : "paused-read");
            for (int i = 0; i < 10; i++) yield return null; Assert.That(actions, Is.EqualTo(1));
            fail = false; Submit("progress-retry"); Submit("progress-retry");
            Assert.That(actions, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.View.HandId, Is.EqualTo(hand));
            Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator NonfiniteNextDelayIsRejectedWithoutRemovingTheCompletedHand()
        {
            Submit("fold"); long version = bootstrap.Progress.Version; Button old = document.rootVisualElement.Q<Button>("new-practice");
            foreach (float delay in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                settings.opponentDelaySeconds = delay;
                LogAssert.Expect(LogType.Error, "Poker practice start failed (ArgumentException); no automatic retry was performed.");
                Submit(old); Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
                Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Complete));
                Assert.That(document.rootVisualElement.Q<Button>("new-practice"), Is.SameAs(old));
            }
            settings.opponentDelaySeconds = 0.1f; Submit(old);
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting)); yield return null;
        }

        [UnityTest]
        public IEnumerator ActivePracticeUsesCachedDelayEvenWhenNextSettingsAreUnavailable()
        {
            bootstrap.Settings = null; settings.opponentDelaySeconds = float.NaN;
            Submit("call"); long before = bootstrap.Progress.Version;
            yield return WaitFor(() => bootstrap.Progress.Version > before);
            Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
            bootstrap.Settings = settings;
        }

        [UnityTest]
        public IEnumerator LifecycleChangeDuringProgressCannotScheduleOrPauseTheDisposedTable()
        {
            int attempts = 0;
            SetBootstrapProgress(new PracticeProgressRunner(() => { attempts++; go.SetActive(false); return false; }, () => Assert.Fail("No display after no-op.")));
            Submit("call"); yield return WaitFor(() => !go.activeSelf);
            Assert.That(attempts, Is.EqualTo(1)); Assert.That(bootstrap.Progress.Version, Is.Zero);
            go.SetActive(true); for (int i = 0; i < 5; i++) yield return null;
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
            Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(attempts, Is.EqualTo(1));
        }

        private T BootstrapField<T>(string name)
        {
            var field = typeof(PracticeTableBootstrap).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null); return (T)field.GetValue(bootstrap);
        }

        [UnityTest]
        public IEnumerator ActualRenderExceptionPausesWithoutRerenderingAndRecoversDisplayOnly()
        {
            var table = BootstrapField<LocalPokerTable>("table"); var input = BootstrapField<PokerInputController>("input");
            var screen = BootstrapField<PokerTableScreen>("screen"); int actions = 0;
            // Test-only missing UI reference: the actual Render method throws, not an artificial post-read callback.
            var field = typeof(PokerTableScreen).GetField("handName", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null); object label = field.GetValue(screen); Assert.That(label, Is.Not.Null);
            SetBootstrapProgress(new PracticeProgressRunner(() => { actions++; return table.AdvanceOpponent(); },
                () => { input.Refresh(); screen.ResumeProgress(); }));
            Submit("call"); long before = input.View.Version; Guid hand = input.View.HandId;
            field.SetValue(screen, null);
            try
            {
                LogAssert.Expect(LogType.Error, "Poker practice paused at Display (NullReferenceException); no automatic retry or reset.");
                yield return WaitFor(() => document.rootVisualElement.Q<Button>("progress-retry").style.display.value == DisplayStyle.Flex);
                Assert.That(actions, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(before + 1));
                Assert.That(document.rootVisualElement.Q<Button>("call").enabledInHierarchy, Is.False);
                yield return null; Capture("paused-render-component");
                field.SetValue(screen, label); Submit("progress-retry");
                Assert.That(actions, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.View.HandId, Is.EqualTo(hand));
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(document.rootVisualElement.Query<Button>(className: "card").ToList().Count, Is.EqualTo(5));
            }
            finally { field.SetValue(screen, label); }
        }
        private void SetBootstrapProgress(PracticeProgressRunner runner)
        {
            var field = typeof(PracticeTableBootstrap).GetField("progress", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null); field.SetValue(bootstrap, runner);
        }

        [UnityTest]
        public IEnumerator HumanApprovedInputRenderFailureStopsAutomaticProgressAndRecoversDisplayOnly()
        {
            var table = BootstrapField<LocalPokerTable>("table"); var input = BootstrapField<PokerInputController>("input");
            var screen = BootstrapField<PokerTableScreen>("screen"); int opponentCalls = 0;
            SetBootstrapProgress(new PracticeProgressRunner(() => { opponentCalls++; return table.AdvanceOpponent(); },
                () => { input.Refresh(); screen.ResumeProgress(); }));
            var field = typeof(PokerTableScreen).GetField("handName", BindingFlags.NonPublic | BindingFlags.Instance);
            object label = field.GetValue(screen); long before = input.View.Version; Guid hand = input.View.HandId;
            field.SetValue(screen, null);
            try
            {
                LogAssert.Expect(LogType.Error, "Poker input display failed (NullReferenceException); action was not replayed.");
                Submit("call"); Submit("call");
                Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.IsPending, Is.False);
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").text, Is.EqualTo("화면 다시 확인"));
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").enabledInHierarchy, Is.True);
                Assert.That(document.rootVisualElement.Q<Button>("call").enabledInHierarchy, Is.False);
                double until = Time.realtimeSinceStartupAsDouble + 0.25;
                while (Time.realtimeSinceStartupAsDouble < until) yield return null;
                Assert.That(opponentCalls, Is.Zero); Assert.That(table.Human.Read().Version, Is.EqualTo(before + 1));
                Capture("human-display-paused");
                field.SetValue(screen, label); Submit("progress-retry"); Submit("progress-retry");
                Assert.That(opponentCalls, Is.Zero); Assert.That(input.View.Version, Is.EqualTo(before + 1)); Assert.That(input.View.HandId, Is.EqualTo(hand));
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(input.IsPending, Is.False);
                yield return null;
                Assert.That(opponentCalls, Is.Zero, "A restored screen schedules the new version's delay before another action.");
                Assert.That(BootstrapField<long>("scheduledVersion"), Is.EqualTo(input.View.Version));
            }
            finally { field.SetValue(screen, label); }
        }

        [UnityTest]
        public IEnumerator HumanPendingBeforeSubmitSurvivesDisplayRecoveryAndUsesTheSameRequest()
        { yield return PendingDisplayRecovery(InputFault.BeforeSubmit); }

        [UnityTest]
        public IEnumerator HumanPendingLostReceiptSurvivesDisplayRecoveryAndUsesTheSameRequest()
        { yield return PendingDisplayRecovery(InputFault.AfterSubmit); }

        [UnityTest]
        public IEnumerator HumanPendingFailedReadSurvivesDisplayRecoveryAndUsesTheSameRequest()
        { yield return PendingDisplayRecovery(InputFault.AfterRead); }

        private IEnumerator PendingDisplayRecovery(InputFault fault)
        {
            bootstrap.enabled = false;
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var port = new FaultedInputPort(table.Human, fault); var input = new PokerInputController(port);
            var screen = new PokerTableScreen(document.rootVisualElement, input, () => Assert.Fail("No new hand."), settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular")); // No external recovery callback.
            var field = typeof(PokerTableScreen).GetField("handName", BindingFlags.NonPublic | BindingFlags.Instance);
            object label = field.GetValue(screen); Guid hand = input.View.HandId; field.SetValue(screen, null);
            try
            {
                LogAssert.Expect(LogType.Error, "Poker input failed (IOException); pending request retained, no automatic reset.");
                LogAssert.Expect(LogType.Error, "Poker input display failed (NullReferenceException); action was not replayed.");
                Submit("call"); Submit("call"); Submit("retry");
                Assert.That(port.Submissions, Is.EqualTo(1)); Assert.That(input.IsPending, Is.True);
                long approvedVersion = fault == InputFault.BeforeSubmit ? 1 : 2;
                Assert.That(table.Human.Read().Version, Is.EqualTo(approvedVersion));
                Assert.That(screen.IsProgressPaused, Is.True);
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").enabledInHierarchy, Is.True);
                field.SetValue(screen, label); Submit("progress-retry"); Submit("progress-retry");
                Assert.That(port.Submissions, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(approvedVersion));
                Assert.That(input.IsPending, Is.True, "A display read is not an acknowledgement of the pending command.");
                Assert.That(screen.IsProgressPaused, Is.False); Assert.That(input.View.HandId, Is.EqualTo(hand));
                Assert.That(document.rootVisualElement.Q<Button>("retry").enabledInHierarchy, Is.True);
                yield return null; Capture("human-pending-" + fault);
                Submit("retry"); Submit("retry");
                Assert.That(port.Submissions, Is.EqualTo(2)); Assert.That(port.LastCommand, Is.SameAs(port.FirstCommand));
                Assert.That(table.Human.Read().Version, Is.EqualTo(2)); Assert.That(input.IsPending, Is.False);
            }
            finally { field.SetValue(screen, label); screen.Dispose(); }
        }

        [UnityTest]
        public IEnumerator StandaloneDisplayRecoveryRemainsPausedAfterRepeatedReadAndRenderFailures()
        {
            bootstrap.enabled = false;
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var port = new FaultedInputPort(table.Human, InputFault.None); var input = new PokerInputController(port);
            var screen = new PokerTableScreen(document.rootVisualElement, input, () => Assert.Fail("No new hand."), settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            var field = typeof(PokerTableScreen).GetField("handName", BindingFlags.NonPublic | BindingFlags.Instance);
            object label = field.GetValue(screen); field.SetValue(screen, null);
            try
            {
                LogAssert.Expect(LogType.Error, "Poker input display failed (NullReferenceException); action was not replayed.");
                Submit("call"); Assert.That(input.IsPending, Is.False);
                for (int i = 0; i < 2; i++)
                {
                    LogAssert.Expect(LogType.Error, "Poker display recovery failed (NullReferenceException); no action was replayed.");
                    Submit("progress-retry"); Assert.That(screen.IsProgressPaused, Is.True);
                    Assert.That(document.rootVisualElement.Q<Button>("progress-retry").enabledInHierarchy, Is.True);
                }
                field.SetValue(screen, label); port.FailNextRead = true;
                LogAssert.Expect(LogType.Error, "Poker display recovery failed (IOException); no action was replayed.");
                Submit("progress-retry"); Assert.That(screen.IsProgressPaused, Is.True);
                Assert.That(document.rootVisualElement.Q<Button>("progress-retry").text, Is.EqualTo("화면 다시 확인"));
                Submit("progress-retry"); Submit("progress-retry");
                Assert.That(port.Submissions, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(2));
                Assert.That(screen.IsProgressPaused, Is.False); Assert.That(input.IsPending, Is.False);
                screen.PauseProgress(PracticeProgressFailure.Display);
                Button old = document.rootVisualElement.Q<Button>("progress-retry"); screen.Dispose(); Submit(old);
                Assert.That(port.Submissions, Is.EqualTo(1));
            }
            finally { field.SetValue(screen, label); screen.Dispose(); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CardSelectionAndExchangeDisplayRecoveryPreserveSelectionAndRefreshBettingTarget()
        {
            bootstrap.enabled = false; settings.closingOrder = new[] { 1, 2 }; // Explicit test order, not a new practice default.
            var table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), new FixedRandom());
            var port = new FaultedInputPort(table.Human, InputFault.None); var input = new PokerInputController(port);
            int steps = 0;
            while (!input.View.CanExchange)
            {
                Assert.That(++steps, Is.LessThan(30));
                if (input.View.IsOwnTurn) input.Bet(input.View.Betting.CanCall ? BettingAction.Call() : BettingAction.Check());
                else table.AdvanceOpponent();
                input.Refresh();
            }
            var screen = new PokerTableScreen(document.rootVisualElement, input, () => Assert.Fail("No new hand."), settings.startingStack,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            var field = typeof(PokerTableScreen).GetField("handName", BindingFlags.NonPublic | BindingFlags.Instance);
            object label = field.GetValue(screen); long version = input.View.Version; int submissions = port.Submissions;
            try
            {
                field.SetValue(screen, null);
                LogAssert.Expect(LogType.Error, "Poker input display failed (NullReferenceException); action was not replayed.");
                Submit(document.rootVisualElement.Query<Button>(className: "card").ToList()[0]);
                Assert.That(input.SelectedCount, Is.EqualTo(1)); Assert.That(input.View.Version, Is.EqualTo(version));
                Assert.That(port.Submissions, Is.EqualTo(submissions));
                field.SetValue(screen, label); Submit("progress-retry");
                Assert.That(input.SelectedCount, Is.EqualTo(1));
                Assert.That(document.rootVisualElement.Query<Button>(className: "selected").ToList().Count, Is.EqualTo(1));
                document.rootVisualElement.Q<TextField>("bet-target").SetValueWithoutNotify("77");
                field.SetValue(screen, null);
                LogAssert.Expect(LogType.Error, "Poker input display failed (NullReferenceException); action was not replayed.");
                Submit("exchange");
                Assert.That(input.View.Phase, Is.EqualTo(HandPhase.SecondBetting)); Assert.That(input.View.IsOwnTurn, Is.True);
                Assert.That(port.Submissions, Is.EqualTo(submissions + 1)); Assert.That(input.SelectedCount, Is.Zero);
                field.SetValue(screen, label); Submit("progress-retry");
                Assert.That(document.rootVisualElement.Q<TextField>("bet-target").value, Is.EqualTo("2"),
                    "An unsuccessful Render must not consume the new version's minimum-target initialization.");
                Assert.That(port.Submissions, Is.EqualTo(submissions + 1)); Assert.That(input.View.Version, Is.EqualTo(version + 1));
                yield return null; Capture("human-exchange-display-recovered"); AssertLayout();
            }
            finally { field.SetValue(screen, label); screen.Dispose(); }
        }

        private enum InputFault { None, BeforeSubmit, AfterSubmit, AfterRead }
        private sealed class FaultedInputPort : IPokerSeatPort
        {
            private readonly IPokerSeatPort inner; private readonly InputFault fault; private bool failed;
            public int Submissions; public bool FailNextRead;
            public HandCommand FirstCommand, LastCommand;
            public FaultedInputPort(IPokerSeatPort inner, InputFault fault) { this.inner = inner; this.fault = fault; }
            public PokerPlayerView Read()
            {
                if (FailNextRead) { FailNextRead = false; throw new IOException("Synthetic read failure; do not log payloads."); }
                return inner.Read();
            }
            public HandReceipt Submit(HandCommand command)
            {
                Submissions++; if (FirstCommand == null) FirstCommand = command; LastCommand = command;
                if (!failed && fault == InputFault.BeforeSubmit) { failed = true; throw new IOException(); }
                HandReceipt receipt = inner.Submit(command);
                if (!failed && fault == InputFault.AfterSubmit) { failed = true; throw new IOException(); }
                if (!failed && fault == InputFault.AfterRead) { failed = true; FailNextRead = true; }
                return receipt;
            }
        }

        private void AssertHorizontalBounds()
        {
            var root = document.rootVisualElement;
            foreach (Button card in root.Query<Button>(className: "card").ToList())
            {
                Assert.That(card.worldBound.xMin, Is.GreaterThanOrEqualTo(root.worldBound.xMin));
                Assert.That(card.worldBound.xMax, Is.LessThanOrEqualTo(root.worldBound.xMax));
            }
            foreach (string name in new[] { "instruction", "amount-row", "actions", "last-action", "result", "showdown-result" })
            {
                VisualElement element = root.Q<VisualElement>(className: name);
                if (element.resolvedStyle.display == DisplayStyle.None) continue;
                Assert.That(element.worldBound.xMin, Is.GreaterThanOrEqualTo(root.worldBound.xMin), name);
                Assert.That(element.worldBound.xMax, Is.LessThanOrEqualTo(root.worldBound.xMax), name);
            }
        }
        private static IEnumerator WaitFor(Func<bool> condition)
        {
            double timeout = Time.realtimeSinceStartupAsDouble + 6;
            while (!condition() && Time.realtimeSinceStartupAsDouble < timeout) yield return null;
            Assert.That(condition(), Is.True, "Poker UI did not reach the expected phase.");
        }

        private IEnumerator ReachOwnExchangeThroughLegalResponses()
        {
            // The production opponent may now raise before the draw. Drive real legal UI responses,
            // rather than treating every random deal as a passive check-through fixture.
            double timeout = Time.realtimeSinceStartupAsDouble + 6;
            while ((bootstrap.Progress.Phase != HandPhase.Exchange || !bootstrap.Progress.OwnTurn)
                && Time.realtimeSinceStartupAsDouble < timeout)
            {
                if (bootstrap.Progress.Phase == HandPhase.FirstBetting && bootstrap.Progress.OwnTurn)
                    SubmitPassiveLegalResponse();
                yield return null;
            }
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.Exchange));
            Assert.That(bootstrap.Progress.OwnTurn, Is.True);
        }

        private void SubmitPassiveLegalResponse()
        {
            Button check = document.rootVisualElement.Q<Button>("check");
            Button call = document.rootVisualElement.Q<Button>("call");
            Button legal = check.enabledInHierarchy && check.style.display.value != DisplayStyle.None ? check : call;
            Assert.That(legal.enabledInHierarchy, Is.True, "The active human needs a legal check or call.");
            Assert.That(legal.style.display.value, Is.Not.EqualTo(DisplayStyle.None), "A hidden button is not an available player action.");
            Submit(legal);
        }

        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class LostOncePort : IPokerSeatPort
        {
            private readonly IPokerSeatPort inner;
            public int Submissions;
            public LostOncePort(IPokerSeatPort inner) { this.inner = inner; }
            public PokerPlayerView Read() => inner.Read();
            public HandReceipt Submit(HandCommand command)
            {
                HandReceipt receipt = inner.Submit(command);
                if (++Submissions == 1) throw new IOException("Test receipt lost after approval.");
                return receipt;
            }
        }
#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator DevelopmentSmokeScenarioUsesActualButtonsAcrossSixHands()
        {
            var scenario = new PracticeSmokeScenario(bootstrap, document.rootVisualElement);
            double end = Time.realtimeSinceStartupAsDouble + 20;
            while (!scenario.Completed && Time.realtimeSinceStartupAsDouble < end)
            {
                scenario.Tick();
                yield return null;
            }
            Assert.That(scenario.Completed, Is.True);
            Assert.That(scenario.CompletedHands, Is.EqualTo(6));
            Assert.That(scenario.ExplicitRestarts, Is.EqualTo(6));
            Assert.That(scenario.ExchangeHands, Is.EqualTo(5));
            Assert.That(scenario.SecondBettingHands, Is.EqualTo(5));
            Assert.That(scenario.HumanActions, Is.GreaterThanOrEqualTo(16));
            Assert.That(bootstrap.Progress.Phase, Is.EqualTo(HandPhase.FirstBetting));
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
            scenario.Tick(); scenario.Tick();
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
            Capture("development-smoke-restarted");
        }
        [UnityTest]
        public IEnumerator DevelopmentSmokeRejectsAHiddenActionWithoutSubmittingIt()
        {
            var scenario = new PracticeSmokeScenario(bootstrap, document.rootVisualElement);
            document.rootVisualElement.Q<Button>("fold").style.display = DisplayStyle.None;
            Assert.Throws<InvalidOperationException>(() => scenario.Tick());
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
            Assert.That(scenario.HumanActions, Is.Zero);
            yield return null;
        }
        [UnityTest]
        public IEnumerator DevelopmentSmokeRejectsLostSceneAndMissingCards()
        {
            var scenario = new PracticeSmokeScenario(bootstrap, document.rootVisualElement);
            document.rootVisualElement.Query<Button>(className: "card").First().RemoveFromHierarchy();
            Assert.Throws<InvalidOperationException>(() => scenario.Tick());
            Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
            bootstrap.enabled = false;
            Assert.Throws<InvalidOperationException>(() => scenario.Tick());
            Assert.That(scenario.HumanActions, Is.Zero);
            yield return null;
        }
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
            Assert.That(player.worldBound.yMax, Is.LessThan(root.Q<VisualElement>(className: "table").worldBound.yMax - 8),
                "Own hand label must fit inside the table. Opponent=" + opponent.worldBound + ", pot=" + center.worldBound
                + ", player=" + player.worldBound + ", table=" + root.Q<VisualElement>(className: "table").worldBound);
            var actions = root.Q<VisualElement>(className: "actions");
            var amount = root.Q<VisualElement>(className: "amount-row");
            if (amount.resolvedStyle.display != DisplayStyle.None)
                Assert.That(amount.worldBound.yMax, Is.LessThanOrEqualTo(actions.worldBound.yMin), "Bet amount row and action buttons must not overlap.");
            Assert.That(actions.worldBound.yMax, Is.LessThanOrEqualTo(root.worldBound.yMax), "Action buttons must stay inside the screen.");
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
