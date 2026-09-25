using System;
using System.Collections;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemAppPlayModeTests
    {
        private HoldemTableBootstrap Solo => owner.GetComponentInChildren<HoldemTableBootstrap>();

        private IEnumerator StartDealerDebug()
        {
            Assert.That(Root.Q<Foldout>("omc-menu-development").value, Is.False);
            Root.Q<Foldout>("omc-menu-development").value = true;
            yield return Click("omc-menu-dealer-test");
            Assert.That(Solo.EnableDealerDebug, Is.True);
            Assert.That(Root.Q<Label>(className: "omc-subtitle").text, Does.Contain("실제 AI 아님"));
            Assert.That(Root.Q<Button>("omc-options"), Is.Null);
            Assert.That(Root.Q(className: "omc-root").ClassListContains("with-public-speech"), Is.True);
            Assert.That(Root.Q<Label>("omc-utterance-scope").text, Does.Contain("모두에게"));
        }

        private IEnumerator FinishDebugBetting(bool withSpeech)
        {
            if (withSpeech)
            {
                yield return Wait(() => Root.Q<TextField>("omc-utterance-input").enabledInHierarchy);
                Root.Q<TextField>("omc-utterance-input").value = "테스트 멘트";
                yield return Click("omc-utterance-send");
                Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Contain("테스트 멘트"));
            }
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (!Solo.Progress.IsDealPending && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (Solo.Progress.OwnTurn && Root.Q<Button>("omc-passive").enabledInHierarchy)
                    yield return Click("omc-passive");
                else yield return null;
            }
            Assert.That(Solo.Progress.IsDealPending, Is.True);
            yield return Wait(() => Root.Q<Button>("omc-dealer-debug-unchanged") != null);
            yield return null; yield return null;
            Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator DebugDealerSuccessFailureTimeoutUseThreeDistinctRevealWindows()
        {
            yield return StartDealerDebug();
            string[] controls = { "omc-dealer-debug-change", "omc-dealer-debug-unchanged", "omc-dealer-debug-timeout" };
            string[] outcomes = { "변경 성공", "요청 실패", "시간 초과 처리" };
            Button oldChange = null;
            for (int i = 0; i < controls.Length; i++)
            {
                yield return FinishDebugBetting(true);
                if (i == 0) oldChange = Root.Q<Button>("omc-dealer-debug-change");
                else
                {
                    long oldVersion = Solo.Progress.Version;
                    Submit(oldChange); yield return null;
                    Assert.That(Solo.Progress.Version, Is.EqualTo(oldVersion), "Detached previous-window button is inert.");
                }
                Capture("debug-dealer-pending-" + i);
                long version = Solo.Progress.Version;
                var button = Root.Q<Button>(controls[i]);
                yield return Click(controls[i]); Submit(button);
                yield return Wait(() => Solo.Progress.IsRevealPending);
                Assert.That(Solo.Progress.Version, Is.EqualTo(version + 1));
                Assert.That(Solo.Progress.Street, Is.EqualTo((HoldemStreet)(i + 1)));
                Assert.That(Root.Q<Label>("omc-own-card-change") != null, Is.EqualTo(i == 0));
                if (i == 0)
                {
                    var changed = Root.Q("omc-board")[0];
                    Assert.That(Root.Q<Label>("omc-own-card-change").parent, Is.SameAs(changed));
                    Assert.That(changed.Q<Label>(className: "omc-rank").text, Is.EqualTo("K"));
                    Assert.That(changed.Q<Label>(className: "omc-suit").text, Is.EqualTo("♦"));
                }
                Assert.That(Root.Q<Label>("omc-dealer-debug-status").text, Does.Contain(outcomes[i]));
                yield return null; yield return null;
                Capture("debug-dealer-reveal-" + i);
                yield return Wait(() => Root.Q<Button>("omc-pass-accusation").enabledInHierarchy);
                yield return Click("omc-pass-accusation");
                yield return Wait(() => Root.Q<Button>("omc-continue-reveal").resolvedStyle.display != DisplayStyle.None);
                yield return Click("omc-continue-reveal");
                Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator DebugDealerCompactWindowKeepsTestAndPokerControlsReachable()
        {
            texture.Release(); texture.width = 960; texture.height = 640; texture.Create();
            yield return null; yield return null;
            yield return StartDealerDebug(); yield return FinishDebugBetting(true);
            foreach (string name in new[] { "omc-dealer-debug-change", "omc-dealer-debug-unchanged", "omc-dealer-debug-timeout", "omc-reset" })
            {
                var button = Root.Q<Button>(name);
                Assert.That(button.worldBound.yMax, Is.LessThanOrEqualTo(texture.height), name);
                Assert.That(button.worldBound.xMax, Is.LessThanOrEqualTo(texture.width), name);
                Assert.That(button.worldBound.yMin, Is.GreaterThanOrEqualTo(0), name);
            }
            var remark = Root.Q<Button>("omc-public-utterance-2");
            Assert.That(remark.worldBound.width, Is.GreaterThan(90));
            Capture("debug-dealer-compact-pending");
            Assert.That(remark.resolvedStyle.backgroundColor, Is.EqualTo((Color)new Color32(25, 58, 50, 255)), "Public remark background");
            Assert.That(remark.resolvedStyle.color, Is.EqualTo((Color)new Color32(214, 227, 217, 255)), "Public remark text color");
            AssertDebugLayout();
            yield return Click("omc-dealer-debug-change");
            yield return Wait(() => Solo.Progress.IsRevealPending);
            yield return null; yield return null;
            Capture("debug-dealer-compact-reveal");
            Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Not.Null);
            var badge = Root.Q<Label>("omc-own-card-change");
            Assert.That(badge.worldBound.yMax, Is.LessThanOrEqualTo(badge.parent.worldBound.yMax), "Change badge must fit its card.");
            AssertDebugLayout();
            yield return Wait(() => Root.Q<Button>("omc-pass-accusation").enabledInHierarchy);
            yield return Click("omc-pass-accusation");
            yield return Wait(() => Root.Q<Button>("omc-continue-reveal").resolvedStyle.display != DisplayStyle.None);
            yield return Click("omc-continue-reveal");
        }

        [UnityTest]
        public IEnumerator DebugNpcActualChangeAndWrongTargetsResolveWithoutLeakingOwnerFeedback()
        {
            yield return StartDealerDebug();
            string[] results = { "change", "unchanged", "timeout", "change", "change" };
            for (int sample = 0; sample < results.Length; sample++)
            {
                if (sample > 0)
                {
                    yield return Click("omc-reset"); yield return Click("omc-confirm-reset");
                    yield return null;
                }
                var inject = Root.Q<Button>("omc-debug-npc-speech");
                yield return Click("omc-debug-npc-speech"); Submit(inject); yield return null;
                Assert.That(inject.enabledInHierarchy, Is.False, "NPC speech is accepted once per window.");
                Assert.That(Root.Q<Button>("omc-public-utterance-2").text, Does.Contain("테스트 멘트"));
                yield return FinishDebugBetting(false);
                Assert.That(Root.Q<DropdownField>("omc-debug-source").choices[0], Is.EqualTo("NPC 1 멘트"));
                if (sample == 4) Root.Q<DropdownField>("omc-debug-source").index = 2;
                yield return Click("omc-dealer-debug-" + results[sample]);
                yield return Wait(() => Solo.Progress.IsRevealPending);
                yield return null; yield return null;
                Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null, "NPC success must not produce a human owner badge.");
                if (results[sample] == "change")
                    Assert.That(Root.Q("omc-board")[0].Q<Label>(className: "omc-rank").text, Is.EqualTo("K"));
                Root.Q<DropdownField>("omc-accusation-target").index = sample == 4 ? 2 : sample == 3 ? 1 : 0;
                yield return Wait(() => Root.Q<Button>("omc-accuse").enabledInHierarchy);
                Capture("solo-before-accuse-" + sample);
                yield return Click("omc-accuse");
                yield return Wait(() => Root.Q<Button>("omc-debug-resolve-accusations") != null);
                var resolve = Root.Q<Button>("omc-debug-resolve-accusations");
                if (sample == 0)
                {
                    long version = Solo.Progress.Version;
                    yield return Click("omc-help"); Submit(resolve); yield return null;
                    Assert.That(Solo.Progress.Version, Is.EqualTo(version));
                    yield return Click("omc-close-help"); yield return null;
                }
                yield return Click("omc-debug-resolve-accusations");
                Assert.That(Root.Q<Label>(className: "omc-prompt").text,
                    Does.Contain(sample == 0 || sample == 4 ? "내 고발 적중" : "내 고발 오적중"));
                long settledVersion = Solo.Progress.Version;
                Submit(resolve); Submit(inject); yield return null;
                Assert.That(Solo.Progress.Version, Is.EqualTo(settledVersion));
                Assert.That(Root.Q<Button>("omc-continue-reveal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(Root.Q<Label>("omc-dealer-debug-status").text, Does.Contain("벌금·팟 지급은 미적용"));
                Capture("solo-accusation-" + sample);
            }
        }

        private void AssertDebugLayout()
        {
            var player = Root.Q("omc-seat-1");
            var tableArea = Root.Q(className: "omc-table");
            var controls = Root.Q(className: "omc-controls");
            Assert.That(player.worldBound.yMax, Is.LessThanOrEqualTo(tableArea.worldBound.yMax),
                "Player must fit inside the table: " + player.worldBound + " / " + tableArea.worldBound);
            Assert.That(controls.worldBound.yMin, Is.GreaterThanOrEqualTo(player.worldBound.yMax),
                "Poker controls must not cover cards or speech.");
            Assert.That(controls.worldBound.yMax, Is.LessThanOrEqualTo(texture.height), "Controls must fit viewport.");
        }

        [UnityTest]
        public IEnumerator DebugDealerWithoutSpeechCannotInventAttributionAndModalBlocksControls()
        {
            yield return StartDealerDebug(); yield return FinishDebugBetting(false);
            var change = Root.Q<Button>("omc-dealer-debug-change");
            var timeout = Root.Q<Button>("omc-dealer-debug-timeout");
            Assert.That(change.enabledInHierarchy, Is.False);
            long version = Solo.Progress.Version;
            Submit(change); yield return null;
            Assert.That(Solo.Progress.Version, Is.EqualTo(version));
            yield return Click("omc-help"); Submit(timeout);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.That(Solo.Progress.Version, Is.EqualTo(version));
            Assert.That(timeout.enabledInHierarchy, Is.False);
            yield return Click("omc-close-help");
            yield return Wait(() => timeout.enabledInHierarchy);
            Assert.That(change.enabledInHierarchy, Is.False);
            yield return Click("omc-dealer-debug-unchanged");
            yield return Wait(() => Solo.Progress.IsRevealPending);
            Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null);
            Assert.That(Root.Q<Label>("omc-dealer-debug-status").text, Does.Contain("멘트 없음"));
        }

        [UnityTest]
        public IEnumerator DebugDealerResetAndMenuReturnDisposeOldWorkAndPreserveNormalDefaults()
        {
            yield return StartDealerDebug(); yield return FinishDebugBetting(true);
            var oldChange = Root.Q<Button>("omc-dealer-debug-change");
            yield return Click("omc-reset"); yield return Click("omc-confirm-reset");
            yield return null;
            Submit(oldChange); yield return null;
            Assert.That(Solo.Progress.IsDealPending, Is.False);
            Assert.That(Solo.Progress.Street, Is.EqualTo(HoldemStreet.Preflop));
            Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null);
            yield return Click("omc-menu-return"); yield return Click("omc-menu-confirm");
            Assert.That(Root.Q<Foldout>("omc-menu-development").value, Is.False);
            Assert.That(solo.dealPolicy, Is.EqualTo(HoldemDealPolicy.Automatic));
            Assert.That(solo.revealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
            Assert.That(solo.enableUtterancePreview, Is.False);
            yield return Click("omc-menu-solo"); Submit(oldChange); yield return null;
            Assert.That(Solo.EnableDealerDebug, Is.False);
            Assert.That(Solo.ActiveOptions.RevealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
            Assert.That(Root.Q("omc-dealer-debug"), Is.Null);
            Assert.That(Root.Q("omc-utterance-input"), Is.Null);
            Assert.That(Root.Q<Button>("omc-options"), Is.Not.Null);
        }
    }
}
