using System;
using System.Collections;
using System.Collections.Generic;
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
        [UnityTest]
        public IEnumerator LongUtteranceKeepsInputAndSendInsideComposer()
        {
            UseUtteranceTable();
            var input = Root.Q<TextField>("omc-utterance-input");
            var send = Root.Q<Button>("omc-utterance-send");
            var box = Root.Q("omc-utterance");
            foreach (var size in new[] { new Vector2Int(1200, 800), new Vector2Int(960, 640) })
            {
                yield return Resize(size);
                input.value = ""; for (int i = 0; i < 3; i++) yield return null;
                var initialInput = input.worldBound; var initialSend = send.worldBound;
                input.value = new string('가', 128); for (int i = 0; i < 3; i++) yield return null;
                Capture("utterance-long-" + size.x);
                Assert.That(input.value.Length, Is.EqualTo(128));
                Assert.That(input.worldBound.xMax, Is.LessThanOrEqualTo(box.worldBound.xMax + 1), "input within composer");
                Assert.That(send.worldBound.xMax, Is.LessThanOrEqualTo(box.worldBound.xMax + 1), "send within composer");
                Assert.That(input.worldBound.xMax, Is.LessThanOrEqualTo(send.worldBound.xMin), "input and send do not overlap");
                Assert.That(input.worldBound.width, Is.EqualTo(initialInput.width).Within(1), "Typing must not change input width.");
                Assert.That(send.worldBound.x, Is.EqualTo(initialSend.x).Within(1), "Typing must not move the send button.");
                AssertButtonHit("omc-utterance-send"); AssertLayout();
            }
        }

        private void UseUtteranceTable(Func<IHoldemUtterancePlayerPort, IHoldemUtterancePlayerPort> wrap = null)
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost), 4, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton,
                new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active));
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"), resumeReveal: table.ResumeAfterReveal,
                options: new HoldemTableOptions(4, 0.7f, HoldemRevealPolicy.PauseAfterCommunityReveal), configureTable: _ => {},
                dealUnchanged: table.DealUnchanged, utterancePort: wrap == null ? table.HumanUtterances : wrap(table.HumanUtterances));
        }

        [UnityTest]
        public IEnumerator UtteranceDraftSurvivesNpcActionAndPointerSendDoesNotBet()
        {
            UseUtteranceTable(); yield return Resize(new Vector2Int(960, 640));
            var scope = Root.Q<Label>("omc-utterance-scope");
            Assert.That(scope.text, Does.Contain("AI나 카드에는 반영되지"));
            Assert.That(scope.worldBound.height, Is.GreaterThan(0));
            var field = Root.Q<TextField>("omc-utterance-input");
            field.value = "오늘은 <b>빨간색</b>이 좋네요";
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render(); yield return null;
            Assert.That(field.value, Is.EqualTo("오늘은 <b>빨간색</b>이 좋네요"));
            var before = table.Human.Read();
            AssertLayout(); AssertButtonHit("omc-utterance-send"); Capture("utterance-draft-960x640");
            PointerClick(Root, Root.Q<Button>("omc-utterance-send")); Submit("omc-utterance-send");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(table.Human.Read().PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.EqualTo(1));
            Assert.That(field.value, Is.Empty);
            var status = Root.Q<Label>("omc-utterance-status");
            Assert.That(status.enableRichText, Is.False);
            Assert.That(status.text, Does.Contain("<b>빨간색</b>"));
            Assert.That(Root.Q<Button>("omc-utterance-send").enabledInHierarchy, Is.False);
            yield return null; Capture("utterance-received-960x640");
        }

        [UnityTest]
        public IEnumerator UtteranceDraftCannotCrossTheNextCommunityCardWindow()
        {
            UseUtteranceTable(); yield return Resize(new Vector2Int(960, 640));
            var field = Root.Q<TextField>("omc-utterance-input"); field.value = "늦은 멘트";
            yield return FinishUtteranceBetting();
            Assert.That(table.Human.Read().IsDealPending, Is.True);
            Assert.That(field.value, Is.EqualTo("늦은 멘트"));
            Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Contain("보내지지"));
            Submit("omc-utterance-send"); Assert.That(table.ReadClosedUtteranceBatches()[0].Count, Is.Zero);
            yield return WaitEnabled("omc-release-deal"); Submit("omc-release-deal");
            yield return WaitEnabled("omc-continue-reveal"); Submit("omc-continue-reveal");
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(field.value, Is.Empty);
            Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Contain("미전송 멘트"));
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
            yield return null; AssertLayout(); Capture("utterance-new-street");
        }

        [UnityTest]
        public IEnumerator UtteranceModalPauseAndDisposedControlsCannotSend()
        {
            UseUtteranceTable(); yield return null;
            Root.Q<TextField>("omc-utterance-input").value = "멘트";
            foreach (var pair in new[] { new[] { "omc-help", "omc-close-help" },
                new[] { "omc-history", "omc-close-history" }, new[] { "omc-options", "omc-cancel-options" } })
            {
                Submit(pair[0]); Submit("omc-utterance-send");
                Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
                Submit(pair[1]);
                Assert.That(Root.Q<TextField>("omc-utterance-input").value, Is.EqualTo("멘트"));
            }
            screen.PauseProgress(); Submit("omc-utterance-send");
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
            var oldSend = Root.Q<Button>("omc-utterance-send"); screen.Dispose(); Submit(oldSend);
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
        }

        [UnityTest]
        public IEnumerator UtteranceUnknownDeliveryRetriesTheSameCommandWithoutDuplicating()
        {
            LostUtteranceReceipt wrapper = null;
            UseUtteranceTable(port => wrapper = new LostUtteranceReceipt(port)); yield return null;
            var field = Root.Q<TextField>("omc-utterance-input"); field.value = "중복되면 안 되는 멘트";
            Submit("omc-utterance-send");
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.EqualTo(1));
            Assert.That(field.isReadOnly, Is.True);
            Assert.That(Root.Q<Button>("omc-utterance-send").text, Is.EqualTo("접수 확인"));
            Submit("omc-utterance-send");
            Assert.That(wrapper.Commands.Count, Is.EqualTo(2));
            Assert.That(wrapper.Commands[0], Is.EqualTo(wrapper.Commands[1]));
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.EqualTo(1));
            Assert.That(field.value, Is.Empty);
            Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Contain("프리플랍에 보낸 멘트"));
        }

        [UnityTest]
        public IEnumerator UtteranceFailureBeforeDeliveryRetriesTheOriginalDraftOnce()
        {
            LostUtteranceReceipt wrapper = null;
            UseUtteranceTable(port => wrapper = new LostUtteranceReceipt(port, failBeforeDelivery: true)); yield return null;
            Root.Q<TextField>("omc-utterance-input").value = "첫 전송이 실패한 멘트";
            Submit("omc-utterance-send");
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
            Assert.That(Root.Q<TextField>("omc-utterance-input").isReadOnly, Is.True);
            Submit("omc-utterance-send");
            Assert.That(wrapper.Commands.Count, Is.EqualTo(2));
            Assert.That(wrapper.Commands[1], Is.EqualTo(wrapper.Commands[0]));
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator UtteranceThreeWindowsReachShowdownThenClearForNextHand()
        {
            UseUtteranceTable(); yield return Resize(new Vector2Int(960, 640));
            for (int stage = 0; stage < 3; stage++)
            {
                Root.Q<TextField>("omc-utterance-input").value = "멘트 " + stage;
                AssertButtonHit("omc-utterance-send"); Submit("omc-utterance-send");
                yield return FinishUtteranceBetting();
                Assert.That(table.ReadClosedUtteranceBatches().Count, Is.EqualTo(stage + 1));
                yield return WaitEnabled("omc-release-deal"); AssertLayout(); Submit("omc-release-deal");
                yield return WaitEnabled("omc-continue-reveal"); Submit("omc-continue-reveal");
                Assert.That(Root.Q<Label>("omc-utterance-status").text,
                    Does.StartWith(new[] { "프리플랍", "플랍", "턴" }[stage] + "에 보낸 멘트: 멘트 " + stage),
                    "A prior street's message must not look like it was submitted on the new street.");
            }
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.River));
            Assert.That(Root.Q<TextField>("omc-utterance-input").enabledInHierarchy, Is.False);
            yield return FinishUtteranceBetting();
            Assert.That(table.Human.Read().Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            yield return WaitEnabled("omc-next"); AssertLayout(); Capture("utterance-showdown"); Submit("omc-next");
            Assert.That(table.Human.Read().HandNumber, Is.EqualTo(2));
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.Zero);
            Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Not.Contain("멘트 2"));
        }

        private IEnumerator FinishUtteranceBetting()
        {
            double end = Time.realtimeSinceStartupAsDouble + 8;
            while (table.Human.Read().CurrentSeat.HasValue && Time.realtimeSinceStartupAsDouble < end)
            {
                if (table.Human.Read().LegalActions == null) { Assert.That(table.AdvanceNpc(), Is.True); screen.Render(); }
                else if (Root.Q<Button>("omc-passive").enabledInHierarchy) Submit("omc-passive");
                yield return null;
            }
            Assert.That(table.Human.Read().CurrentSeat.HasValue, Is.False);
        }

        private sealed class LostUtteranceReceipt : IHoldemUtterancePlayerPort
        {
            private readonly IHoldemUtterancePlayerPort inner;
            private readonly bool failBeforeDelivery;
            public readonly List<Guid> Commands = new List<Guid>();
            public LostUtteranceReceipt(IHoldemUtterancePlayerPort inner, bool failBeforeDelivery = false)
            { this.inner = inner; this.failBeforeDelivery = failBeforeDelivery; }
            public HoldemUtteranceView ReadUtterances() => inner.ReadUtterances();
            public HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command)
            {
                Commands.Add(command.CommandId);
                if (failBeforeDelivery && Commands.Count == 1) throw new InvalidOperationException("Simulated delivery failure.");
                var receipt = inner.SubmitUtterance(command);
                if (Commands.Count == 1) throw new InvalidOperationException("Simulated lost receipt.");
                return receipt;
            }
        }
    }
}
