using System.Collections;
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
        public IEnumerator ActionFeedbackToleratesUnavailableOptionalHistory()
        {
            screen.Dispose();
            screen = new HoldemTableScreen(Root, new UnavailableHistoryPort(table.Human), () => restarts++, 100,
                Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return null;
            Assert.That(screen.IsProgressPaused, Is.False);
            Submit("omc-passive");
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(2));
            Assert.That(Root.Q("omc-seat-1").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("이번 베팅 2칩"));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Does.Contain("콜"));
            screen.Render(); Assert.That(screen.IsProgressPaused, Is.False);
        }

        [UnityTest]
        public IEnumerator ActionFeedbackKeepsItsOriginalStreetWhenCardsAreRevealed()
        {
            Submit("omc-passive");
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("나 · 콜 · 1칩 추가"));
            Assert.That(Root.Q("omc-seat-1").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("콜 · 총 2칩"));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render(); yield return null;
            Assert.That(table.Human.Read().Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("프리플랍 · 상대 · 체크"));
            Assert.That(Root.Q("omc-seat-1").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("이번 베팅 0칩"));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render(); yield return null;
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("상대 · 체크"));
            Assert.That(Root.Q("omc-seat-2").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("체크 · 총 0칩"));
            yield return Resize(new Vector2Int(960, 640)); AssertLayout(); Capture("action-feedback-flop");
        }

        [UnityTest]
        public IEnumerator ActionFeedbackRemainsAtEachSeatAcrossOtherActionsAndScreenRebuild()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new FixedRandom(), new FixedRandom(), new PassivePolicy());
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render();
            Submit("omc-passive");
            Assert.That(Root.Q("omc-seat-4").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("콜 · 총 2칩"));
            Assert.That(Root.Q("omc-seat-1").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("콜 · 총 2칩"));
            Assert.That(Root.Q("omc-seat-2").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("이번 베팅 1칩"));
            screen.Dispose();
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return Resize(new Vector2Int(960, 640));
            Assert.That(Root.Q("omc-seat-4").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("콜 · 총 2칩"));
            Assert.That(Root.Q("omc-seat-1").Q<Label>(className: "omc-seat-status").text, Is.EqualTo("콜 · 총 2칩"));
            AssertLayout(); Capture("action-feedback-four-seat");
        }

        private sealed class UnavailableHistoryPort : IHoldemPlayerPort, IHoldemHistoryPort
        {
            private readonly IHoldemPlayerPort inner;
            public UnavailableHistoryPort(IHoldemPlayerPort inner) { this.inner = inner; }
            public HoldemSnapshot Read() => inner.Read();
            public HoldemActionNotice LastAction => inner.LastAction;
            public HoldemHandHistory ReadHistory() => null;
            public HoldemReceipt Submit(HoldemCommand command) => inner.Submit(command);
            public HoldemReceipt NextHand(long expectedVersion) => inner.NextHand(expectedVersion);
            public HoldemReceipt ResolvePendingSettlement(long expectedVersion) => inner.ResolvePendingSettlement(expectedVersion);
        }
    }
}
