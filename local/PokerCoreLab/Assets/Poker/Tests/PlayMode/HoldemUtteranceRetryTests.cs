using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemTablePlayModeTests
    {
        [UnityTest]
        public IEnumerator AsyncSpeechRetryGuardsReentryAndUncertainEnqueueBeforeSending()
        {
            DelayedAsyncSpeech fake = null;
            UseUtteranceTable(port => fake = new DelayedAsyncSpeech(port));
            yield return Resize(new Vector2Int(960, 640));
            var composer = SpeechComposer();
            fake.OnSend = () => InvokeSpeechSubmit(composer);
            fake.ThrowOnSend = true;
            var field = Root.Q<TextField>("omc-utterance-input");
            field.value = "전송 결과를 기다리는 멘트";
            PointerClick(Root, Root.Q<Button>("omc-utterance-send"));
            for (int i = 0; i < 4; i++) InvokeSpeechSubmit(composer);
            Assert.That(fake.Commands.Count, Is.EqualTo(1), "The guard must precede a reentrant/throwing send delegate.");
            Assert.That(field.isReadOnly, Is.True);
            Assert.That(field.value, Is.EqualTo(fake.Commands[0].Text));
            Assert.That(Root.Q<Button>("omc-utterance-send").enabledInHierarchy, Is.False);
            Assert.That(Root.Q<Label>("omc-utterance-status").text, Does.Contain("잠시 후"));
            yield return null;
            AssertLayout(); Capture("speech-retry-waiting-960");
            Assert.That(Root.Q<Button>("omc-utterance-send").worldBound.xMax,
                Is.LessThanOrEqualTo(Root.Q("omc-utterance").worldBound.xMax + 1));
            fake.Complete(HoldemUtteranceError.None);
            screen.Render();
            Assert.That(field.value, Is.Empty);
            Assert.That(field.enabledInHierarchy, Is.False,
                "This one-message local fixture has exhausted its street quota, even though confirmation is complete.");
            Assert.That(composer.GetType().GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(composer), Is.Null);
            Assert.That(table.HumanUtterances.ReadUtterances().Count, Is.EqualTo(1));
            AssertSpeechRetryCancelled(composer);
            Assert.That(fake.Commands.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AsyncSpeechFinalRejectionAndDisposeCancelTheirRetrySchedules()
        {
            foreach (var error in new[] { HoldemUtteranceError.WindowClosed, HoldemUtteranceError.LimitReached })
            {
                DelayedAsyncSpeech fake = null;
                UseUtteranceTable(port => fake = new DelayedAsyncSpeech(port));
                yield return null;
                var composer = SpeechComposer();
                Root.Q<TextField>("omc-utterance-input").value = "마감 전에 보낸 멘트";
                Submit("omc-utterance-send");
                fake.Complete(error); screen.Render();
                AssertSpeechRetryCancelled(composer);
                Assert.That(Root.Q<TextField>("omc-utterance-input").isReadOnly, Is.False);
                Assert.That(Root.Q<Label>("omc-utterance-status").text,
                    Does.Contain(error == HoldemUtteranceError.WindowClosed ? "입력 시간이 지나" : "모두 보냈어요"));
                Assert.That(fake.Commands.Count, Is.EqualTo(1));
            }
            DelayedAsyncSpeech pending = null;
            UseUtteranceTable(port => pending = new DelayedAsyncSpeech(port));
            yield return null;
            var disposedComposer = SpeechComposer();
            Root.Q<TextField>("omc-utterance-input").value = "종료할 화면의 멘트";
            Submit("omc-utterance-send");
            screen.Dispose();
            AssertSpeechRetryCancelled(disposedComposer);
            InvokeSpeechSubmit(disposedComposer);
            Assert.That(pending.Commands.Count, Is.EqualTo(1), "Detached controls must not retry.");
        }

        [UnityTest]
        public IEnumerator AsyncSpeechRetryExpiryCannotUnlockAPausedScreenOrSendByItself()
        {
            DelayedAsyncSpeech fake = null;
            UseUtteranceTable(port => fake = new DelayedAsyncSpeech(port));
            yield return null;
            Root.Q<TextField>("omc-utterance-input").value = "중단 중에는 보내지 않을 멘트";
            Submit("omc-utterance-send");
            var composer = SpeechComposer();
            screen.PauseProgress();
            yield return new WaitForSecondsRealtime(3.2f);
            Assert.That(Root.Q<Button>("omc-utterance-send").enabledInHierarchy, Is.False);
            InvokeSpeechSubmit(composer);
            Assert.That(fake.Commands.Count, Is.EqualTo(1));
            Assert.That(Root.Q<TextField>("omc-utterance-input").value, Is.EqualTo(fake.Commands[0].Text));
        }

        private object SpeechComposer() => typeof(HoldemTableScreen)
            .GetField("utteranceComposer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screen);

        private static void InvokeSpeechSubmit(object composer) => composer.GetType()
            .GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(composer, null);

        private static void AssertSpeechRetryCancelled(object composer)
        {
            Assert.That(composer.GetType().GetField("retrySchedule", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(composer), Is.Null);
            Assert.That(composer.GetType().GetField("retryAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(composer), Is.EqualTo(0d));
        }

        private sealed class DelayedAsyncSpeech : IHoldemAsyncUtterancePort
        {
            private readonly IHoldemUtterancePlayerPort inner;
            private HoldemUtteranceReceipt receipt;
            public readonly List<HoldemUtteranceCommand> Commands = new List<HoldemUtteranceCommand>();
            public Action OnSend;
            public bool ThrowOnSend;
            public bool CanInteract => true;
            public DelayedAsyncSpeech(IHoldemUtterancePlayerPort inner) { this.inner = inner; }
            public HoldemUtteranceView ReadUtterances() => inner.ReadUtterances();
            public HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command)
                => throw new InvalidOperationException("Async path required.");
            public bool SendOrRetry(HoldemUtteranceCommand command, out HoldemUtteranceReceipt result)
            {
                Commands.Add(command);
                // One callback is enough to detect missing pre-send guards, without recursion overflow.
                var callback = OnSend; OnSend = null; callback?.Invoke();
                if (ThrowOnSend) throw new InvalidOperationException("Uncertain enqueue.");
                result = null; return false;
            }
            public bool TryReadReceipt(HoldemUtteranceCommand command, out HoldemUtteranceReceipt result)
            { result = receipt != null && receipt.CommandId == command.CommandId ? receipt : null; return result != null; }
            public void Complete(HoldemUtteranceError error)
            {
                var command = Commands[Commands.Count - 1];
                receipt = error == HoldemUtteranceError.None ? inner.SubmitUtterance(command)
                    : HoldemUtteranceReceipt.Confirmation(command.CommandId, error);
            }
        }
    }
}
