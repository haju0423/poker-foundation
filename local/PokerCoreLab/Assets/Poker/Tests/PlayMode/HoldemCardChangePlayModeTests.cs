using System;
using System.Collections;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemTablePlayModeTests
    {
        [UnityTest]
        public IEnumerator ActualOwnerCardChangeIsMarkedOnItsCardOnlyDuringReveal()
        {
            UseUtteranceTable(); yield return Resize(new Vector2Int(960, 640));
            Root.Q<TextField>("omc-utterance-input").value = "오늘은 사랑이 필요하네요";
            Submit("omc-utterance-send");
            Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null);
            yield return FinishUtteranceBetting();
            var source = table.ReadClosedUtteranceBatches()[0].GetEntry(0);
            var before = table.Human.Read(); var deal = before.PendingDeal;
            var command = new HoldemDealCommand(before.SessionId, before.HandId, deal.WindowId, Guid.NewGuid(),
                before.SessionVersion, deal.Street, new HoldemCardChange(source.WindowId, source.CommandId,
                    source.Speaker, 0, Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(table.ApplyDealerDeal(command).Accepted, Is.True);
            screen.Render(); yield return null;
            var badge = Root.Q<Label>("omc-own-card-change");
            Assert.That(badge, Is.Not.Null);
            Assert.That(badge.text, Is.EqualTo("변경됨"));
            Assert.That(badge.tooltip, Does.Contain("내 멘트로 바뀐 카드"));
            Assert.That(badge.parent, Is.SameAs(Root.Q("omc-board")[0]));
            Assert.That(badge.worldBound.width, Is.GreaterThan(0));
            Assert.That(badge.MeasureTextSize(badge.text, 0, VisualElement.MeasureMode.Undefined,
                0, VisualElement.MeasureMode.Undefined).x, Is.LessThanOrEqualTo(badge.contentRect.width + 1));
            Assert.That(badge.worldBound.xMax, Is.LessThanOrEqualTo(badge.parent.worldBound.xMax + 1));
            Capture("own-card-change-960x640");
            yield return WaitEnabled("omc-continue-reveal"); Submit("omc-continue-reveal");
            Assert.That(Root.Q<Label>("omc-own-card-change"), Is.Null);
        }
    }
}
