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
    public sealed partial class HoldemTablePlayModeTests
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
            foreach (char c in (HoldemTableScreen.HelpText + HoldemTableScreen.ActionHelpText + HoldemTableScreen.TermsHelpText + "♣♦♥♠◇").Where(c => !char.IsWhiteSpace(c)).Distinct())
                Assert.That(font.HasCharacter(c), Is.True, "Missing font glyph " + c);
            Assert.That(Root.Q("omc-own-cards").childCount, Is.EqualTo(2));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Assert.That(Root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(5));
            Assert.That(Root.Q<Button>("exchange"), Is.Null);
            Assert.That(Root.Q("omc-utterance"), Is.Null);
            Assert.That(Root.Q<Label>(className: "omc-title").text, Is.EqualTo("One More Card"));
            Assert.That(Root.Q<Label>(className: "omc-subtitle").text, Is.EqualTo("텍사스 홀덤"));
            Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("콜 · 1칩 추가"));
            Assert.That(Root.Q<Button>("omc-pot-details").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
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
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("쇼다운 · 나: "
                + Poker.Presentation.KoreanPokerText.HandDescription(settled.GetSeat(settled.ViewerSeat).RevealedHandValue.Value)));
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
            Submit("omc-pot-details"); yield return null;
            Assert.That(Root.Q<Label>("omc-pot-details-copy").text, Does.Not.Contain("공개된 패"));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Submit("omc-close-pot-details");
            Submit("omc-reset"); Assert.That(restarts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator InvalidRaiseAndRapidDuplicateCannotAlterStateTwice()
        {
            var input = Root.Q<TextField>("omc-target");
            Assert.That(input.maxLength, Is.EqualTo(64));
            foreach (string value in new[] { "", "-2", "3.5", "abc", "3", "101", "9999999999999999999",
                "1,00", "1,0000", "1,,000", "9,223,372,036,854,775,8070" })
            {
                input.value = value;
                Assert.That(input.value, Is.EqualTo(value), "Invalid input must not become another amount through truncation.");
                Submit("omc-aggressive");
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(1));
                Assert.That(Root.Q<Button>("omc-aggressive").enabledInHierarchy, Is.False);
                string expected = value == "3" ? "최소 총액 4칩" : value == "101" ? "최대 총액 100칩" : "금액은 숫자로 입력해 주세요.";
                Assert.That(Root.Q<Label>("omc-amount-hint").text, Is.EqualTo(expected));
            }
            input.value = "1,000"; Submit("omc-aggressive");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(1));
            Assert.That(Root.Q<Button>("omc-aggressive").enabledInHierarchy, Is.False);
            Assert.That(Root.Q<Label>("omc-amount-hint").text, Is.EqualTo("최대 총액 100칩"));
            input.value = "8"; Submit("omc-aggressive"); Submit("omc-aggressive");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(2));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(92));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AmountPresetsOnlyFillInputAndRaiseShowsTotalAdditionalAndAllIn()
        {
            var input = Root.Q<TextField>("omc-target");
            input.value = "8";
            Assert.That(Root.Q<Button>("omc-aggressive").text, Is.EqualTo("레이즈 · 총 8칩"));
            Assert.That(Root.Q<Label>("omc-amount-caption").text, Is.EqualTo("레이즈 총액"));
            Assert.That(Root.Q<Label>("omc-amount-hint").text, Is.EqualTo("레이즈 시 7칩 추가"));
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("콜 · 1칩 추가"), "Raise input does not change call cost.");
            foreach (string id in new[] { "omc-min", "omc-half", "omc-max" })
            {
                Submit(id);
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(1), "Presets must not place a bet.");
                Assert.That(table.Human.Read().OwnStack, Is.EqualTo(99));
            }
            Assert.That(input.value, Is.EqualTo("100"));
            Assert.That(Root.Q<Button>("omc-aggressive").text, Is.EqualTo("레이즈 · 총 100칩 (올인)"));
            Assert.That(Root.Q<Label>("omc-amount-hint").text, Is.EqualTo("레이즈 시 99칩 추가"));
            yield return Resize(new Vector2Int(960, 640)); AssertLayout(); Capture("all-in-amount");
            Submit("omc-help"); Submit("omc-min");
            Assert.That(input.value, Is.EqualTo("100"), "Help blocks presets too.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator BetInputDescribesItsActionWithoutChangingCheck()
        {
            Submit("omc-passive");
            Assert.That(table.AdvanceNpc(), Is.True);
            Assert.That(table.AdvanceNpc(), Is.True);
            screen.Render(); yield return null;
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("체크"));
            Assert.That(Root.Q<Label>("omc-amount-caption").text, Is.EqualTo("베팅 금액"));
            Root.Q<TextField>("omc-target").value = "8";
            Assert.That(Root.Q<Label>("omc-amount-hint").text, Is.EqualTo("베팅 시 8칩 추가"));
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("체크"));
            yield return Resize(new Vector2Int(960, 640)); AssertLayout(); Capture("bet-input-purpose");
        }

        [UnityTest]
        public IEnumerator ShortStackCallIsLabelledAllInNotRaise()
        {
            screen.Dispose();
            var seats = new[] { new SeatId(1), new SeatId(2) };
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                ChipLedger.Create(new[] { new SeatChips(seats[0], 3), new SeatChips(seats[1], 100) }), seats, seats[0], new FixedRandom());
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            var view = session.GetSnapshot(seats[0]);
            Assert.That(session.Submit(seats[0], HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), seats[0], view.SessionVersion, BettingAction.Call())).Accepted, Is.True);
            view = session.GetSnapshot(seats[0]);
            Assert.That(session.Submit(seats[1], HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), seats[1], view.SessionVersion, BettingAction.RaiseTo(4))).Accepted, Is.True);
            screen = new HoldemTableScreen(Root, new SessionPort(session, seats[0]), () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(Root.Q<Button>("omc-passive").text, Is.EqualTo("콜 · 1칩 추가 (올인)"));
            Assert.That(Root.Q<Button>("omc-aggressive").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Capture("all-in-call");
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
            Assert.That(Root.Q<Label>("omc-help-copy").text, Is.EqualTo(HoldemTableScreen.HelpText));
            Assert.That(Root.Q<Button>("omc-help-page-3"), Is.Null, "A local game has no remote room setup.");
            Capture("help");
            yield return Resize(new Vector2Int(960, 640));
            foreach (int page in new[] { 1, 2, 0 })
            {
                Submit("omc-help-page-" + page);
                for (int i = 0; i < 3; i++) yield return null;
                string expected = page == 0 ? HoldemTableScreen.HelpText : page == 1 ? HoldemTableScreen.ActionHelpText : HoldemTableScreen.TermsHelpText;
                Assert.That(Root.Q<Label>("omc-help-copy").text, Is.EqualTo(expected));
                AssertButtonHit("omc-close-help"); AssertLayout(); Capture("help-page-" + page);
            }
            Submit("omc-close-help"); yield return null;
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
        public IEnumerator ActiveHandResetDialogBlocksInputAndCancelKeepsTheSameHand()
        {
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), () => restarts++);
            var before = table.Human.Read();
            yield return Resize(new Vector2Int(960, 640));
            AssertButtonHit("omc-reset"); Submit("omc-reset"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.True);
            Submit("omc-fold"); Submit("omc-passive"); Submit("omc-confirm-reset"); Submit("omc-confirm-reset");
            Assert.That(restarts, Is.EqualTo(1), "One explicit confirmation must invoke one reset only.");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(before.OwnStack));
            Submit("omc-reset"); Submit("omc-cancel-reset"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.False);
            Assert.That(table.Human.Read().HandId, Is.EqualTo(before.HandId));
            Assert.That(restarts, Is.EqualTo(1)); AssertLayout();
        }

        [UnityTest]
        public IEnumerator OptionsDialogCancelsWithoutChangesAndAppliesOnce()
        {
            screen.Dispose();
            var original = new HoldemTableOptions(2, 0.7f, HoldemRevealPolicy.Automatic);
            HoldemTableOptions received = null; int changes = 0;
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), options: original,
                configureTable: value => { changes++; received = value; });
            yield return Resize(new Vector2Int(960, 640));
            long version = table.Human.Read().SessionVersion;
            AssertButtonHit("omc-options"); Submit("omc-options"); yield return null;
            Root.Q<DropdownField>("omc-options-seats").index = 2;
            Root.Q<DropdownField>("omc-options-speed").index = 0;
            Root.Q<Toggle>("omc-options-reveal").value = true;
            Assert.That(Root.Q<Toggle>("omc-options-reveal").text, Is.EqualTo("공용 카드 공개 후 잠시 멈추기"));
            var explanation = Root.Q<Label>("omc-options-reveal-description");
            Assert.That(explanation.text, Is.EqualTo("켜면 플랍·턴·리버가 나올 때마다 멈춰요. 카드를 확인한 뒤 ‘계속’을 누르면 진행돼요."));
            Assert.That(explanation.worldBound.height, Is.GreaterThan(0));
            Assert.That(explanation.worldBound.yMax, Is.LessThan(Root.worldBound.yMax));
            for (int i = 0; i < 3; i++) yield return null;
            Capture("options-960x640");
            Assert.That(screen.IsProgressPaused, Is.True);
            Submit("omc-help"); Submit("omc-fold"); Submit("omc-passive");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
            AssertButtonHit("omc-cancel-options"); AssertButtonHit("omc-apply-options"); AssertLayout();
            Submit("omc-cancel-options"); Assert.That(changes, Is.Zero);
            Assert.That(screen.IsProgressPaused, Is.False);
            Submit("omc-options");
            Assert.That(Root.Q<DropdownField>("omc-options-seats").index, Is.Zero, "Discard un-applied choices.");
            Root.Q<DropdownField>("omc-options-seats").index = 2;
            Root.Q<DropdownField>("omc-options-speed").index = 0;
            Root.Q<Toggle>("omc-options-reveal").value = true;
            Submit("omc-apply-options"); Submit("omc-apply-options");
            Assert.That(changes, Is.EqualTo(1)); Assert.That(received.SeatCount, Is.EqualTo(4));
            Assert.That(received.OpponentDelaySeconds, Is.EqualTo(0.25f));
            Assert.That(received.RevealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
            Assert.That(original.SeatCount, Is.EqualTo(2));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
        }

        [UnityTest]
        public IEnumerator FailedOptionsReplacementKeepsTheOriginalGameAndModal()
        {
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"),
                options: new HoldemTableOptions(2, 0.7f, HoldemRevealPolicy.Automatic),
                configureTable: _ => throw new InvalidOperationException("test failure"));
            var before = table.Human.Read();
            Submit("omc-options");
            LogAssert.Expect(LogType.Error, "Holdem options were not applied (InvalidOperationException).");
            Submit("omc-apply-options"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.True);
            Assert.That(table.Human.Read().HandId, Is.EqualTo(before.HandId));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Submit("omc-cancel-options"); Assert.That(screen.IsProgressPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator RejectedStaleActionShowsReasonWithoutReplayingTheBet()
        {
            screen.Dispose();
            screen = new HoldemTableScreen(Root, new StaleCommandPort(table.Human), () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            var before = table.Human.Read();
            Submit("omc-passive");
            Assert.That(Root.Q<Label>(className: "omc-error").text, Is.EqualTo("진행 상태가 바뀌었어요. 화면을 확인하고 다시 선택해 주세요."));
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(before.OwnStack));
            Assert.That(Root.Q<Label>(className: "omc-error").text, Does.Contain("진행 상태가 바뀌었어요"));
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
                Is.EqualTo(winner + " · " + Poker.Presentation.KoreanPokerText.HandSummary(value)));
            Assert.That(Root.Query<Label>(className: "current").ToList(), Is.Empty);
            Assert.That(Root.Query<Label>(className: "past").ToList().Count, Is.EqualTo(4));
            yield return WaitEnabled("omc-next"); Capture("winning-showdown");
        }


        [UnityTest]
        public IEnumerator FourSeatsFitSmallWindowAndRevealOnlyAtShowdown()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(Root.Query(className: "omc-opponent").ToList().Count, Is.EqualTo(3));
            Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
            Assert.That(Root.Query(className: "face-card").ToList().Count, Is.EqualTo(2));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render();
            for (int i = 0; i < 3; i++) yield return null;
            AssertButtonHit("omc-passive"); AssertLayout(); Capture("four-seat-initial");
            yield return Resize(new Vector2Int(960, 640));
            AssertLayout(); AssertButtonHit("omc-passive"); Capture("four-seat-960x640");
            var opponents = Root.Query(className: "omc-opponent").ToList();
            for (int i = 0; i < opponents.Count - 1; i++)
                Assert.That(opponents[i].worldBound.xMax, Is.LessThanOrEqualTo(opponents[i + 1].worldBound.xMin + 1));
            yield return CompletePassive();
            Assert.That(Root.Query(className: "face-card").ToList().Count, Is.EqualTo(13));
            Assert.That(Root.Query(className: "best").ToList().Count, Is.EqualTo(5));
            AssertLayout(); Capture("four-seat-showdown");
            yield return WaitEnabled("omc-next");
            Submit("omc-next"); yield return null;
            Assert.That(table.Human.Read().HandNumber, Is.EqualTo(2));
            Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
        }


        [UnityTest]
        public IEnumerator PendingOddChipRequiresAnExplicitChoiceThenShowsBothPotAwards()
        {
            screen.Dispose();
            var seats = Enumerable.Range(1, 4).Select(i => new SeatId(i)).ToArray();
            var ledger = ChipLedger.Create(seats.Select((seat, i) => new SeatChips(seat, i == 0 ? 5 : 10)).ToArray());
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2), ledger, seats, seats[0],
                new PrefixRandom("Js Jd 2c 3h Qc Qh 2d 3s Ac 3c 3d 8h Ad 9s Ah Td"));
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0).Accepted, Is.True);
            foreach (var action in new[] { BettingAction.RaiseTo(10), BettingAction.Call(), BettingAction.Call(), BettingAction.Call() })
            {
                var v = session.GetSnapshot(seats[0]);
                Assert.That(session.Submit(v.CurrentSeat.Value, HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(),
                    v.CurrentSeat.Value, v.SessionVersion, action)).Accepted, Is.True);
            }
            var port = new SessionPort(session, seats[0]);
            screen = new HoldemTableScreen(Root, port, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(port.Read().IsSettlementPending, Is.True);
            Assert.That(Root.Q<Button>("omc-resolve").text, Is.EqualTo("나머지 칩 배분 확인"));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Does.Contain("이번 판에만 적용"));
            Assert.That(Root.Q<Label>(className: "omc-prompt").text, Does.Contain("모든 팟을 정산"));
            Assert.That(Root.Q<Button>("omc-pot-details").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(Root.Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            AssertButtonHit("omc-resolve"); AssertLayout(); Capture("four-seat-odd-chip-pending");
            long version = port.Read().SessionVersion;
            Assert.That(port.Read().Result, Is.Null);
            Submit("omc-best-seat-3"); yield return null;
            AssertHighlightedBest(port.Read(), new SeatId(3));
            Assert.That(port.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(port.Read().IsSettlementPending, Is.True);
            Assert.That(port.Read().GetSeatAt(2).Awarded, Is.Zero);
            Submit("omc-resolve"); Submit("omc-resolve");
            Assert.That(port.Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(port.Read().IsSettlementPending, Is.False);
            Assert.That(port.Read().Result.PotCount, Is.EqualTo(2));
            Assert.That(port.Read().GetSeatAt(0).Stack, Is.EqualTo(20));
            Assert.That(port.Read().GetSeatAt(1).Stack, Is.EqualTo(8));
            Assert.That(port.Read().GetSeatAt(2).Stack, Is.EqualTo(7));
            AssertHighlightedBest(port.Read(), new SeatId(3));
            Assert.That(Root.Q<Button>("omc-best-seat-3").ClassListContains("selected"), Is.True,
                "Settling the odd chip must preserve a comparison the player selected while waiting.");
            Assert.That(Root.Q<Label>(className: "omc-result").text, Is.EqualTo("팟마다 승부를 나눠 칩을 지급했어요."));
            yield return WaitEnabled("omc-pot-details"); AssertLayout(); Capture("four-seat-side-pots");
            yield return Resize(new Vector2Int(960, 640));
            AssertButtonHit("omc-pot-details"); Submit("omc-pot-details");
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(screen.IsProgressPaused, Is.True);
            Assert.That(Root.Q("omc-pot-details-dialog").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            string details = Root.Q<Label>("omc-pot-details-copy").text;
            Assert.That(details, Does.StartWith("메인 팟 · 나 20칩\n사이드 팟 1 · 상대 1 8칩, 상대 2 7칩"));
            Assert.That(details, Does.Contain("공개된 패"));
            Assert.That(details, Does.Contain("나: 포카드 3 · 남은 카드 10"));
            Assert.That(details, Does.Contain("상대 1: 스트레이트 · Q까지 연속"));
            var nextButton = Root.Q<Button>("omc-next");
            Assert.That(IsDescendant(Root.panel.Pick(nextButton.worldBound.center), nextButton), Is.False);
            Submit("omc-next"); Submit("omc-reset"); Submit("omc-help");
            Assert.That(port.Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(restarts, Is.Zero);
            AssertButtonHit("omc-close-pot-details"); AssertLayout(); Capture("pot-details-960x640");
            var scroll = Root.Q<ScrollView>("omc-pot-details-scroll");
            scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(Root.Q<Label>("omc-pot-details-copy").worldBound.yMax,
                Is.LessThanOrEqualTo(scroll.contentViewport.worldBound.yMax + 1), "The complete comparison explanation must be reachable.");
            AssertButtonHit("omc-close-pot-details"); Capture("pot-details-scrolled");
            Submit("omc-close-pot-details"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.False);
            Assert.That(Root.Q("omc-pot-details-dialog").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Submit("omc-next"); Assert.That(port.Read().HandNumber, Is.EqualTo(2));
        }

#if UNITY_EDITOR

        [UnityTest]
        public IEnumerator SavedFourSeatSceneReceivesPickedPointerDownAndUpThroughItsNestedSurface()
        {
            screen.Dispose(); go.SetActive(false);
            var saved = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Poker/Samples/HoldemTable.unity",
                new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
            try
            {
                for (int i = 0; i < 5; i++) yield return null;
                var doc = saved.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UIDocument>()).Single();
                var bootstrap = doc.GetComponent<HoldemTableBootstrap>();
                var docRoot = doc.rootVisualElement;
                var surface = docRoot.Q(className: "omc-root");
                Assert.That(doc.panelSettings.targetTexture, Is.Null, "Keep the production screen-space panel in this pointer test.");
                Assert.That(bootstrap.Progress.SeatCount, Is.EqualTo(4));
                Assert.That(surface.parent, Is.SameAs(docRoot));
                Assert.That(surface.panel, Is.Not.Null);
                Assert.That(surface.worldBound.width, Is.GreaterThan(0));
                Assert.That(surface.worldBound.height, Is.GreaterThan(0));
                Assert.That(surface.worldBound.xMin, Is.GreaterThanOrEqualTo(docRoot.worldBound.xMin - 1));
                Assert.That(surface.worldBound.xMax, Is.LessThanOrEqualTo(docRoot.worldBound.xMax + 1));
                var helpButton = docRoot.Q<Button>("omc-help");
                int downCount = 0, upCount = 0, clicks = 0;
                helpButton.RegisterCallback<PointerDownEvent>(_ => downCount++, TrickleDown.TrickleDown);
                helpButton.RegisterCallback<PointerUpEvent>(_ => upCount++, TrickleDown.TrickleDown);
                helpButton.RegisterCallback<ClickEvent>(_ => clicks++, TrickleDown.TrickleDown);
                long before = bootstrap.Progress.Version;
                PointerClick(docRoot, helpButton);
                yield return null;
                Assert.That(downCount, Is.EqualTo(1)); Assert.That(upCount, Is.EqualTo(1)); Assert.That(clicks, Is.EqualTo(1));
                Assert.That(docRoot.Q<Button>("omc-close-help").worldBound.height, Is.GreaterThan(0));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(before));
                PointerClick(docRoot, docRoot.Q<Button>("omc-close-help"));
                double deadline = Time.realtimeSinceStartupAsDouble + 10;
                while (!bootstrap.Progress.OwnTurn && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(bootstrap.Progress.OwnTurn, Is.True);
                // Render changes display in Update; wait for UI Toolkit's layout pass before picking.
                for (int i = 0; i < 3; i++) yield return null;
                var passiveButton = docRoot.Q<Button>("omc-passive");
                before = bootstrap.Progress.Version;
                PointerClick(docRoot, passiveButton);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(before + 1), "Picked pointer input must invoke exactly one game command.");
            }
            finally
            {
                foreach (var root in saved.GetRootGameObjects()) root.SetActive(false);
            }
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }

#endif
        private static void PointerClick(VisualElement docRoot, Button button)
        {
            Assert.That(button.enabledInHierarchy, Is.True);
            Assert.That(button.pickingMode, Is.EqualTo(PickingMode.Position));
            Vector2 point = button.worldBound.center;
            var picked = docRoot.panel.Pick(point);
            Assert.That(IsDescendant(picked, button), Is.True, button.name + " is occluded on the saved scene. Button="
                + button.worldBound + ", root=" + docRoot.worldBound + ", picked=" + picked?.name);
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { down.target = picked; picked.SendEvent(down); }
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { up.target = picked; picked.SendEvent(up); }
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
                Assert.That(bootstrap.Settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.Automatic));
                Assert.That(savedDoc.rootVisualElement.Q<Button>("omc-release-deal").style.display.value, Is.EqualTo(DisplayStyle.None));
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
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Complete));
                Submit(savedDoc.rootVisualElement.Q<Button>("omc-confirm-reset"));
                Assert.That(bootstrap.Progress.HandNumber, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Preflop));
                Assert.That(savedDoc.rootVisualElement.Q("omc-own-cards").childCount, Is.EqualTo(2));
                Capture("saved-scene");
            }
            finally { if (savedDoc != null && savedDoc.panelSettings != null) savedDoc.panelSettings.targetTexture = null; }
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }

        [UnityTest]
        public IEnumerator SavedSceneOptionsPauseNpcsAndReplaceTableWithoutMutatingSettingsAsset()
        {
            screen.Dispose(); go.SetActive(false);
            var saved = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Poker/Samples/HoldemTable.unity",
                new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
            UIDocument doc = null;
            try
            {
                for (int i = 0; i < 5; i++) yield return null;
                doc = saved.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UIDocument>()).Single();
                doc.panelSettings.targetTexture = texture;
                var bootstrap = doc.GetComponent<HoldemTableBootstrap>();
                var root = doc.rootVisualElement;
                int assetSeats = bootstrap.Settings.seatCount;
                float assetDelay = bootstrap.Settings.opponentDelaySeconds;
                var assetReveal = bootstrap.Settings.revealPolicy;
                Submit(root.Q<Button>("omc-options"));
                long version = bootstrap.Progress.Version;
                yield return new WaitForSecondsRealtime(1f);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version), "Options pause NPC progress.");
                root.Q<DropdownField>("omc-options-seats").index = 1;
                root.Q<DropdownField>("omc-options-speed").index = 0;
                root.Q<Toggle>("omc-options-reveal").value = true;
                Button oldApply = root.Q<Button>("omc-apply-options");
                Submit(oldApply); Submit(oldApply);
                Assert.That(bootstrap.Progress.SeatCount, Is.EqualTo(3));
                Assert.That(bootstrap.Progress.HandNumber, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Assert.That(bootstrap.ActiveOptions.OpponentDelaySeconds, Is.EqualTo(0.25f));
                Assert.That(bootstrap.ActiveOptions.RevealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
                Assert.That(bootstrap.Settings.seatCount, Is.EqualTo(assetSeats));
                Assert.That(bootstrap.Settings.opponentDelaySeconds, Is.EqualTo(assetDelay));
                Assert.That(bootstrap.Settings.revealPolicy, Is.EqualTo(assetReveal));
                Submit(root.Q<Button>("omc-reset"));
                version = bootstrap.Progress.Version;
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version), "Confirmation pauses NPC progress.");
                Submit(root.Q<Button>("omc-cancel-reset"));
                double deadline = Time.realtimeSinceStartupAsDouble + 10;
                while (bootstrap.Progress.Street == HoldemStreet.Preflop && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    var passiveButton = root.Q<Button>("omc-passive");
                    if (passiveButton.enabledInHierarchy && passiveButton.style.display.value != DisplayStyle.None) Submit(passiveButton);
                    yield return null;
                }
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Flop));
                for (int i = 0; i < 3; i++) yield return null;
                Assert.That(root.Q<Button>("omc-continue-reveal").style.display.value, Is.EqualTo(DisplayStyle.Flex));
                version = bootstrap.Progress.Version;
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
                Submit(root.Q<Button>("omc-continue-reveal"));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version + 1));
            }
            finally { if (doc != null && doc.panelSettings != null) doc.panelSettings.targetTexture = null; }
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }
        [UnityTest]
        public IEnumerator SavedFlowPreviewKeepsHostDealingAcrossOptionsAndRestartWithoutEditingAssets()
        {
            screen.Dispose(); go.SetActive(false);
            var saved = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Poker/Samples/HoldemFlowPreview.unity",
                new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Additive));
            UIDocument doc = null; HoldemTableSettings copy = null;
            RenderTexture originalTarget = null;
            try
            {
                for (int i = 0; i < 5; i++) yield return null;
                doc = saved.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UIDocument>()).Single();
                originalTarget = doc.panelSettings.targetTexture; doc.panelSettings.targetTexture = texture;
                var bootstrap = doc.GetComponent<HoldemTableBootstrap>();
                var original = bootstrap.Settings;
                Assert.That(original.dealPolicy, Is.EqualTo(HoldemDealPolicy.WaitForHost));
                Assert.That(original.accusationMode, Is.EqualTo(HoldemAccusationMode.Disabled));
                Assert.That(original.revealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
                Assert.That(bootstrap.Progress.SeatCount, Is.EqualTo(4));
                var root = doc.rootVisualElement;
                Assert.That(root.Q<Label>(className: "omc-subtitle").text, Is.EqualTo("개발용 진행 확인"));
                // Only a runtime clone gets the all-in fixture; the saved preview stays a normal 100-chip table.
                copy = UnityEngine.Object.Instantiate(original); copy.startingStack = 1; bootstrap.Settings = copy;
                Submit(root.Q<Button>("omc-options"));
                root.Q<DropdownField>("omc-options-seats").index = 0;
                root.Q<Toggle>("omc-options-reveal").value = false;
                Submit(root.Q<Button>("omc-apply-options")); yield return null;
                Assert.That(bootstrap.Progress.SeatCount, Is.EqualTo(2));
                Assert.That(bootstrap.Progress.IsDealPending, Is.True);
                Assert.That(bootstrap.ActiveOptions.RevealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
                long version = bootstrap.Progress.Version;
                Submit(root.Q<Button>("omc-options"));
                Submit(root.Q<Button>("omc-release-deal"));
                yield return new WaitForSecondsRealtime(0.8f);
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version));
                Submit(root.Q<Button>("omc-cancel-options"));
                for (int i = 0; i < 5; i++) yield return null;
                PointerClick(root, root.Q<Button>("omc-release-deal")); Submit(root.Q<Button>("omc-release-deal"));
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(version + 1));
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Flop));
                Assert.That(bootstrap.Progress.IsDealPending, Is.True);
                Assert.That(bootstrap.Progress.IsRevealPending, Is.False);
                yield return new WaitForSecondsRealtime(0.3f);
                Submit(root.Q<Button>("omc-reset")); Submit(root.Q<Button>("omc-confirm-reset")); yield return null;
                Assert.That(bootstrap.Progress.Version, Is.EqualTo(1));
                Assert.That(bootstrap.Progress.Street, Is.EqualTo(HoldemStreet.Preflop));
                Assert.That(bootstrap.Progress.IsDealPending, Is.True);
                Assert.That(root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(5));
                Assert.That(original.startingStack, Is.EqualTo(100));
                Assert.That(original.seatCount, Is.EqualTo(4));
                Assert.That(original.revealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
                Assert.That(original.dealPolicy, Is.EqualTo(HoldemDealPolicy.WaitForHost));
                bootstrap.Settings = original;
            }
            finally
            {
                if (doc != null && doc.panelSettings != null) doc.panelSettings.targetTexture = originalTarget;
                foreach (var root in saved.GetRootGameObjects()) root.SetActive(false);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
            yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(saved);
        }
#endif
        [UnityTest]
        public IEnumerator DealWaitHidesFutureCardsAndOnlyHostCanReleaseIt()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, dealPolicy: HoldemDealPolicy.WaitForHost),
                new FixedRandom(), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), resumeReveal: table.ResumeAfterReveal);
            yield return Resize(new Vector2Int(960, 640));
            Submit("omc-passive"); Assert.That(table.AdvanceNpc(), Is.True);
            screen.Render(); yield return null;
            var waiting = table.Human.Read();
            Assert.That(waiting.IsDealPending, Is.True);
            Assert.That(Root.Q<Label>(className: "omc-prompt").text, Is.EqualTo("딜러가 다음 공용 카드를 준비하고 있어요."));
            Assert.That(Root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(5));
            foreach (string id in new[] { "omc-passive", "omc-aggressive", "omc-fold", "omc-continue-reveal", "omc-release-deal" })
            {
                Assert.That(Root.Q<Button>(id).resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Submit(id);
            }
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(waiting.SessionVersion));
            Assert.That(table.AdvanceNpc(), Is.False);
            AssertLayout(); Capture("deal-wait");
            var deal = new HoldemDealCommand(waiting.SessionId, waiting.HandId, waiting.PendingDeal.WindowId,
                Guid.NewGuid(), waiting.SessionVersion, waiting.PendingDeal.Street);
            Assert.That(table.DealUnchanged(deal).Accepted, Is.True);
            screen.Render(); yield return null;
            Assert.That(table.Human.Read().IsRevealPending, Is.True);
            Assert.That(Root.Q("omc-board").Query(className: "empty").ToList().Count, Is.EqualTo(2));
            Assert.That(Root.Q<Button>("omc-continue-reveal").resolvedStyle.display, Is.Not.EqualTo(DisplayStyle.None));
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator FlowPreviewReleaseIsBlockedByModalsAndCannotDoubleDealAllInWindows()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(1, 1, 2, dealPolicy: HoldemDealPolicy.WaitForHost),
                new FixedRandom(), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 1,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"),
                options: new HoldemTableOptions(2, 0.7f, HoldemRevealPolicy.Automatic),
                configureTable: _ => { }, dealUnchanged: table.DealUnchanged);
            yield return Resize(new Vector2Int(960, 640));
            Assert.That(Root.Q<Label>(className: "omc-subtitle").text, Is.EqualTo("개발용 진행 확인"));
            var initial = table.Human.Read();
            Assert.That(initial.IsDealPending, Is.True);
            foreach (var modal in new[] { new[] { "omc-help", "omc-close-help" },
                new[] { "omc-history", "omc-close-history" }, new[] { "omc-options", "omc-cancel-options" } })
            {
                Submit(modal[0]);
                Assert.That(screen.IsProgressPaused, Is.True, modal[0]);
                Submit("omc-release-deal");
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(initial.SessionVersion));
                Assert.That(table.Human.Read().PendingDeal.WindowId, Is.EqualTo(initial.PendingDeal.WindowId));
                Submit(modal[1]); Assert.That(screen.IsProgressPaused, Is.False);
            }
            for (int street = 1; street <= 3; street++)
            {
                yield return WaitEnabled("omc-release-deal");
                var before = table.Human.Read();
                Assert.That(before.IsDealPending, Is.True);
                Assert.That(before.Accusations, Is.Null);
                AssertLayout(); AssertButtonHit("omc-release-deal");
                // Use a real pointer hit for the host control, then immediately submit it again.
                PointerClick(Root, Root.Q<Button>("omc-release-deal")); Submit("omc-release-deal");
                var after = table.Human.Read();
                Assert.That(after.SessionVersion, Is.EqualTo(before.SessionVersion + 1));
                Assert.That(after.BoardCount, Is.EqualTo(street + 2));
                Assert.That(after.IsDealPending, Is.EqualTo(street < 3));
                if (street < 3) Assert.That(after.PendingDeal.WindowId, Is.Not.EqualTo(before.PendingDeal.WindowId));
                yield return null;
            }
            Assert.That(table.Human.Read().Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator FlowPreviewCompletesFourSeatHandThroughThreeDealAndRevealGates()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), 4, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), resumeReveal: table.ResumeAfterReveal,
                dealUnchanged: table.DealUnchanged);
            yield return Resize(new Vector2Int(960, 640));
            int deals = 0, reveals = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (table.Human.Read().Result == null && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var view = table.Human.Read();
                if (view.IsDealPending)
                {
                    deals++;
                    Assert.That(view.BoardCount, Is.EqualTo(deals == 1 ? 0 : deals + 1));
                    Assert.That(view.Accusations, Is.Null);
                    Assert.That(table.AdvanceNpc(), Is.False);
                    yield return WaitEnabled("omc-release-deal");
                    Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
                    AssertLayout(); AssertButtonHit("omc-release-deal"); Capture("flow-before-" + deals);
                    Submit("omc-release-deal"); Submit("omc-release-deal");
                    Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(view.SessionVersion + 1));
                    Assert.That(table.Human.Read().IsRevealPending, Is.True);
                }
                else if (view.IsRevealPending)
                {
                    reveals++;
                    yield return WaitEnabled("omc-continue-reveal");
                    Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Capture("flow-after-" + reveals); Submit("omc-continue-reveal");
                }
                else if (view.LegalActions == null) { table.AdvanceNpc(); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(deals, Is.EqualTo(3)); Assert.That(reveals, Is.EqualTo(3));
            var settled = table.Human.Read();
            Assert.That(settled.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Root.Query(className: "face-card").ToList().Count, Is.EqualTo(13));
            yield return WaitEnabled("omc-next"); Capture("flow-showdown"); Submit("omc-next"); yield return null;
            Assert.That(table.Human.Read().HandNumber, Is.EqualTo(2));
            Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
            Assert.That(table.Human.Read().OwnStack + table.Human.Read().OwnStreetContribution, Is.EqualTo(settled.OwnStack));
        }

        [UnityTest]
        public IEnumerator PausedFlowPreviewDoesNotReleaseCards()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(1, 1, 2, dealPolicy: HoldemDealPolicy.WaitForHost),
                new FixedRandom(), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 1,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), dealUnchanged: table.DealUnchanged);
            yield return null;
            long version = table.Human.Read().SessionVersion;
            screen.PauseProgress(); screen.Render(); yield return null; Submit("omc-release-deal");
            Assert.That(screen.IsProgressPaused, Is.True);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator RevealWindowsHideBettingAndResumeOnceThroughHostButton()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal),
                4, new FixedRandom(), new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), resumeReveal: table.ResumeAfterReveal);
            yield return Resize(new Vector2Int(960, 640));
            int windows = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (table.Human.Read().Result == null && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var v = table.Human.Read();
                if (v.IsRevealPending)
                {
                    windows++;
                    Assert.That((int)v.Street, Is.EqualTo(windows));
                    yield return WaitEnabled("omc-continue-reveal");
                    Assert.That(Root.Q<Button>("omc-passive").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
                    Assert.That(Root.Q<Label>(className: "omc-prompt").text, Does.Contain("공개"));
                    AssertLayout(); AssertButtonHit("omc-continue-reveal");
                    long version = v.SessionVersion;
                    Submit("omc-help"); Submit("omc-continue-reveal");
                    Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
                    Submit("omc-close-help");
                    Submit("omc-continue-reveal"); Submit("omc-continue-reveal");
                    Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version + 1));
                    Capture("reveal-" + windows);
                }
                else if (v.LegalActions == null) { table.AdvanceNpc(); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(windows, Is.EqualTo(3));
            Assert.That(table.Human.Read().Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Root.Query(className: "face-card").ToList().Count, Is.EqualTo(13));
        }

        [UnityTest]
        public IEnumerator AccusationPassButtonsCompleteThreeWindowsWithoutFoldingPlayer()
        {
            UseAccusationTable();
            yield return Resize(new Vector2Int(960, 640));
            var windows = new System.Collections.Generic.HashSet<Guid>();
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (table.Human.Read().Result == null && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var v = table.Human.Read(); var intake = v.Accusations;
                if (intake != null)
                {
                    windows.Add(intake.WindowId);
                    if (intake.Phase == HoldemAccusationPhase.Collecting)
                    {
                        if (!intake.HasResponded)
                        {
                            yield return WaitEnabled("omc-pass-accusation");
                            AssertButtonHit("omc-pass-accusation"); AssertButtonHit("omc-accuse"); AssertLayout();
                            long version = table.Human.Read().SessionVersion;
                            Submit("omc-pass-accusation"); Submit("omc-pass-accusation");
                            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version + 1));
                            Assert.That(table.Human.Read().GetSeat(v.ViewerSeat).Status, Is.EqualTo(HoldemSeatStatus.Active));
                        }
                        table.AdvanceAccusationResponses(); screen.Render();
                    }
                    else
                    {
                        Assert.That(intake.Phase, Is.EqualTo(HoldemAccusationPhase.ClosedWithoutClaims));
                        yield return WaitEnabled("omc-continue-reveal"); Submit("omc-continue-reveal");
                    }
                }
                else if (v.LegalActions == null) { table.AdvanceNpc(); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(windows.Count, Is.EqualTo(3));
            Assert.That(table.Human.Read().Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(table.Human.Read().GetSeatAt(0).RevealedBestCardCount, Is.EqualTo(5));
            Assert.That(Root.Q("omc-accusations").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            AssertLayout();
        }

        [UnityTest]
        public IEnumerator AccusationUiSelectsTargetAndWaitsForHostWithoutExposingHands()
        {
            UseAccusationTable();
            yield return ReachAccusationWindow();
            yield return Resize(new Vector2Int(960, 640));
            yield return WaitEnabled("omc-accuse");
            Root.Q<DropdownField>("omc-accusation-target").index = 2;
            var before = table.Human.Read();
            Submit("omc-help"); Submit("omc-accuse");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Submit("omc-close-help");
            AssertButtonHit("omc-accuse"); AssertLayout();
            Submit("omc-accuse"); Submit("omc-accuse");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion + 1));
            Assert.That(table.Human.Read().Accusations.OwnTarget, Is.EqualTo(new SeatId(4)));
            while (table.AdvanceAccusationResponses()) { screen.Render(); yield return null; }
            screen.Render(); yield return null;
            Assert.That(table.Human.Read().Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingVerdicts));
            Assert.That(Root.Q("omc-accusations").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(Root.Q<Button>("omc-continue-reveal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(Root.Q<Label>(className: "omc-prompt").text, Does.Contain("판정을 기다리고"));
            Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
            var v = table.Human.Read();
            var claim = table.GetPendingAccusations().Single();
            Assert.That(claim.Target, Is.EqualTo(new SeatId(4)));
            Assert.That(table.ResolveAccusation(claim.ClaimId, new FixedEvidence(HoldemDealerEvidence.ForClaim(claim, false))),
                Is.EqualTo(HoldemEvidenceResolution.Recorded));
            screen.Render(); yield return null;
            Assert.That(Root.Q<Label>(className: "omc-prompt").text, Does.Contain("결과 처리를 기다리고"));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(before.OwnStack));
            Assert.That(table.Human.Read().PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(table.Human.Read().Result, Is.Null);
            Assert.That(table.AdvanceNpc(), Is.False);
        }

        [UnityTest]
        public IEnumerator AccusationWaitingCanBeAbandonedOnlyAfterConfirmation()
        {
            UseAccusationTable();
            yield return ReachAccusationWindow();
            yield return WaitEnabled("omc-accuse"); Submit("omc-accuse");
            while (table.AdvanceAccusationResponses()) { screen.Render(); yield return null; }
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), () => restarts++, table.ResumeAfterReveal);
            var before = table.Human.Read();
            Submit("omc-reset"); Submit("omc-continue-reveal"); Submit("omc-cancel-reset");
            Assert.That(restarts, Is.Zero);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(table.Human.Read().Accusations.Phase, Is.EqualTo(HoldemAccusationPhase.AwaitingVerdicts));
            Submit("omc-reset"); Submit("omc-confirm-reset"); Submit("omc-confirm-reset");
            Assert.That(restarts, Is.EqualTo(1));
            Assert.That(table.Human.Read().Result, Is.Null);
        }

        private void UseAccusationTable()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses), 4,
                new FixedRandom(), new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), resumeReveal: table.ResumeAfterReveal);
        }

        [UnityTest]
        public IEnumerator HandHistoryIsCollapsedByDefaultAndBlocksActionsWhileReading()
        {
            Assert.That(Root.Q("omc-history-dialog").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            yield return Resize(new Vector2Int(960, 640));
            var before = table.Human.Read(); string amount = Root.Q<TextField>("omc-target").value;
            AssertButtonHit("omc-history"); Submit("omc-history"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.True);
            Assert.That(Root.Q<Label>("omc-history-copy").text, Does.Contain("나 · SB 1칩"));
            Assert.That(Root.Q<Label>("omc-history-copy").text, Does.Contain("상대 · BB 2칩"));
            Submit("omc-passive"); Submit("omc-fold"); Submit("omc-aggressive"); Submit("omc-max"); Submit("omc-help");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(Root.Q<TextField>("omc-target").value, Is.EqualTo(amount));
            AssertButtonHit("omc-close-history"); AssertLayout(); Capture("history-initial-960x640");
            Submit("omc-close-history"); yield return null;
            Assert.That(screen.IsProgressPaused, Is.False);
            Submit("omc-passive"); Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion + 1));
        }

        [UnityTest]
        public IEnumerator CompletedHistorySurvivesScreenRebuildScrollsAndClearsForNextHand()
        {
            yield return CompletePassive();
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return Resize(new Vector2Int(960, 640));
            Submit("omc-history");
            for (int i = 0; i < 4; i++) yield return null;
            string copy = Root.Q<Label>("omc-history-copy").text;
            foreach (string stage in new[] { "프리플랍", "플랍", "턴", "리버" }) Assert.That(copy, Does.Contain(stage));
            Assert.That(copy, Does.Contain("나 · 콜 · 1칩 추가 / 총 2칩"));
            Assert.That(copy, Does.Contain("상대 · 체크"));
            Assert.That(copy, Does.Contain("팟에서 2칩 받음"));
            var before = table.Human.Read();
            Submit("omc-next"); Submit("omc-reset"); Assert.That(table.Human.Read().HandId, Is.EqualTo(before.HandId));
            Assert.That(restarts, Is.Zero); AssertButtonHit("omc-close-history"); Capture("history-complete-960x640");
            var scroll = Root.Q<ScrollView>("omc-history-scroll");
            scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(Root.Q<Label>("omc-history-copy").worldBound.yMax, Is.LessThanOrEqualTo(scroll.contentViewport.worldBound.yMax + 1));
            AssertButtonHit("omc-close-history"); AssertLayout(); Capture("history-scrolled");
            Submit("omc-close-history"); Submit("omc-next");
            yield return new WaitForSecondsRealtime(0.3f);
            Submit("omc-history"); yield return null;
            Assert.That(table.Human.Read().HandNumber, Is.EqualTo(2));
            Assert.That(Root.Q<Label>("omc-history-copy").text, Does.Not.Contain("콜"));
            Assert.That(Root.Q<Label>("omc-history-copy").text, Does.Not.Contain("팟에서"));
        }

        [UnityTest]
        public IEnumerator HistoryShowsFoldRefundWithoutAnyOpponentCards()
        {
            Submit("omc-fold"); yield return new WaitForSecondsRealtime(0.3f);
            Submit("omc-history"); yield return null;
            string copy = Root.Q<Label>("omc-history-copy").text;
            Assert.That(copy, Does.Contain("나 · 폴드"));
            Assert.That(copy, Does.Contain("상대 · 아무도 따라 내지 않은 1칩 반환"));
            Assert.That(copy, Does.Contain("상대 · 팟에서 2칩 받음"));
            Assert.That(copy, Does.Not.Contain("공개된 패"));
            Assert.That(table.Human.Read().OpponentCardCount, Is.Zero);
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
        }
        private IEnumerator ReachAccusationWindow()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (table.Human.Read().Accusations == null && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (table.Human.Read().LegalActions == null) { table.AdvanceNpc(); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(table.Human.Read().Accusations, Is.Not.Null);
        }

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
        private sealed class FixedEvidence : IHoldemDealerEvidenceSource
        {
            private readonly HoldemDealerEvidence evidence;
            public FixedEvidence(HoldemDealerEvidence evidence) { this.evidence = evidence; }
            public HoldemDealerEvidence FindEvidence(HoldemAccusationClaim claim) => evidence;
        }
        private sealed class SessionPort : IHoldemPlayerPort
        {
            private readonly HoldemSession session; private readonly SeatId viewer;
            public SessionPort(HoldemSession session, SeatId viewer) { this.session = session; this.viewer = viewer; }
            public HoldemSnapshot Read() => session.GetSnapshot(viewer);
            public HoldemActionNotice LastAction => null;
            public HoldemReceipt Submit(HoldemCommand command) => session.Submit(viewer, command);
            public HoldemReceipt NextHand(long version) => session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), version);
            public HoldemReceipt ResolvePendingSettlement(long version) => session.ResolvePendingSettlement(Guid.NewGuid(), version, HoldemOddChipRule.ClockwiseFromButton);
        }
        private sealed class StaleCommandPort : IHoldemPlayerPort
        {
            private readonly IHoldemPlayerPort inner;
            public StaleCommandPort(IHoldemPlayerPort inner) { this.inner = inner; }
            public HoldemSnapshot Read() => inner.Read();
            public HoldemActionNotice LastAction => inner.LastAction;
            public HoldemReceipt Submit(HoldemCommand command) => inner.Submit(HoldemCommand.Act(command.SessionId,
                command.HandId, command.CommandId, command.Seat, command.ExpectedVersion - 1, command.Action));
            public HoldemReceipt NextHand(long version) => inner.NextHand(version);
            public HoldemReceipt ResolvePendingSettlement(long version) => inner.ResolvePendingSettlement(version);
        }
        private sealed class PrefixRandom : IRandomSource
        {
            private readonly System.Collections.Generic.Queue<int> choices = new System.Collections.Generic.Queue<int>();
            public PrefixRandom(string codes)
            {
                var target = codes.Split(' ').Select(s => new Card((Rank)("23456789TJQKA".IndexOf(s[0]) + 2),
                    (Suit)("cdhs".IndexOf(s[1]) + 1))).ToList();
                target.AddRange(Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).Where(c => !target.Contains(c)));
                var working = Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).ToArray();
                for (int i = working.Length - 1; i > 0; i--)
                {
                    int pick = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(pick);
                    Card card = working[i]; working[i] = working[pick]; working[pick] = card;
                }
            }
            public int NextInt(int upper) => choices.Count > 0 ? choices.Dequeue() : upper - 1;
        }
    }
}
