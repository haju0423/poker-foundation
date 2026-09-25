using System.Collections;
using System.Collections.Generic;
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
    public sealed partial class HoldemTablePlayModeTests
    {
        [UnityTest]
        public IEnumerator EliminatedShowdownHandsStayReadableAndComparisonIsNotWinnerColour()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4,
                new PrefixRandom("7h 7d Qh Kh 9h 8d As Tc Ac 7c 5h Kc Ad 2d Ah Ts"),
                new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render();
            Root.Q<TextField>("omc-target").value = "100"; Submit("omc-aggressive");
            yield return CompletePassive(); yield return Resize(new Vector2Int(960, 640));
            var settled = table.Human.Read();
            Assert.That(settled.OwnStack, Is.EqualTo(400));
            foreach (int i in new[] { 1, 2, 3 })
            {
                var seat = settled.GetSeatAt(i);
                Assert.That(seat.Status, Is.EqualTo(HoldemSeatStatus.Busted));
                var box = Root.Q("omc-seat-" + seat.Seat.Value);
                Assert.That(box.ClassListContains("inactive-seat"), Is.False, "Public showdown cards remain readable after elimination.");
                Assert.That(box.Q<Label>(className: "omc-seat-status").text, Does.StartWith("탈락 · "));
            }
            yield return WaitEnabled("omc-best-seat-4"); Submit("omc-best-seat-4"); yield return null;
            AssertHighlightedBest(settled, new SeatId(4));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("쇼다운 · 상대 3: "
                + KoreanPokerText.HandDescription(settled.GetSeat(new SeatId(4)).RevealedHandValue.Value)));
            var border = Root.Query(className: "best").ToList()[0].resolvedStyle.borderTopColor;
            Assert.That(border.b, Is.GreaterThan(border.r), "Comparison uses cool colour, not the warm win/pot colour.");
            Assert.That(Root.Q<Label>(className: "omc-result").text, Does.Contain("나 승리"));
            AssertLayout(); Capture("showdown-eliminated-comparison");
        }

        [UnityTest]
        public IEnumerator ShowdownComparisonSelectsWinnersAndLosersWithoutChangingTheGame()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4,
                new PrefixRandom("Js Jd 2c 3h Qc Qh 2d 3s Ac 3c 3d 8h Ad 9s Ah Td"),
                new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return CompletePassive(); yield return Resize(new Vector2Int(960, 640));
            var settled = table.Human.Read();
            Assert.That(settled.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Enumerable.Range(0, 4).Any(i => settled.GetSeatAt(i).Awarded == 0), Is.True);
            AssertHighlightedBest(settled, settled.ViewerSeat);
            foreach (int i in new[] { 1, 2, 3, 0 })
            {
                var seat = settled.GetSeatAt(i); string id = "omc-best-seat-" + seat.Seat.Value;
                yield return WaitEnabled(id); AssertButtonHit(id); Submit(id); yield return null;
                AssertHighlightedBest(settled, seat.Seat);
                Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(settled.SessionVersion));
                Assert.That(table.Human.Read().GetSeat(seat.Seat).Stack, Is.EqualTo(seat.Stack));
                Assert.That(Root.Q<Button>(id).ClassListContains("selected"), Is.True);
                AssertLayout(); Capture("showdown-compare-seat-" + seat.Seat.Value);
            }
            Submit("omc-help"); Submit("omc-best-seat-2");
            AssertHighlightedBest(settled, settled.ViewerSeat);
            Submit("omc-close-help");
            Submit("omc-next"); yield return null;
            Assert.That(Root.Query(className: "best").ToList(), Is.Empty);
            foreach (var button in Root.Query<Button>(className: "omc-compare").ToList())
                Assert.That(button.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(Root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
        }

        [UnityTest]
        public IEnumerator ShowdownComparisonNeverTreatsOwnVisibleCardsAsARevealedHand()
        {
            Submit("omc-fold"); yield return null;
            var settled = table.Human.Read();
            Assert.That(settled.GetSeat(settled.ViewerSeat).VisibleHoleCardCount, Is.EqualTo(2));
            Assert.That(settled.GetSeat(settled.ViewerSeat).RevealedBestCardCount, Is.Zero);
            foreach (var button in Root.Query<Button>(className: "omc-compare").ToList())
                Assert.That(button.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Submit("omc-best-seat-1"); Submit("omc-best-seat-2");
            Assert.That(Root.Query(className: "best").ToList(), Is.Empty);
            Assert.That(Root.Q("omc-opponent-cards").Query(className: "hidden").ToList().Count, Is.EqualTo(2));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(settled.SessionVersion));
        }

        [UnityTest]
        public IEnumerator ShowdownComparisonSkipsFoldedViewerButAllowsOtherRevealedHands()
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new FixedRandom(), new FixedRandom(),
                new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            Assert.That(table.AdvanceNpc(), Is.True); screen.Render(); Submit("omc-fold");
            yield return CompletePassive(); yield return Resize(new Vector2Int(960, 640));
            var settled = table.Human.Read();
            Assert.That(settled.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(Root.Q<Button>("omc-best-seat-1").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            AssertHighlightedBest(settled, new SeatId(2));
            yield return WaitEnabled("omc-best-seat-4"); Submit("omc-best-seat-4"); yield return null;
            AssertHighlightedBest(settled, new SeatId(4));
            AssertLayout(); Capture("showdown-compare-folded-viewer");
        }

        [UnityTest]
        public IEnumerator DifferentPairRanksExplainWhyTheSameCategoryLoses()
        {
            yield return ComparePairRanks("Qc 8h Qd Kc 6h Jc 9c 5s Kh Ts 2s 6d Jd 5d Js Ac",
                "상대 1 승리 · 원페어 6", "원페어 5 · 남은 카드 A·K·10", "원페어 6 · 남은 카드 A·Q·10", "showdown-pair-ranks");
        }

        [UnityTest]
        public IEnumerator EqualPairsExposeKickersWithoutPretendingTheLabelAloneDecides()
        {
            yield return ComparePairRanks("Qc Jh 9c Kc 4h 8d 7h 5s Kh 6d 6s Ac Jd Td Js 2h",
                "나 승리 · 원페어 6", "원페어 6 · 남은 카드 A·K·10", "원페어 6 · 남은 카드 A·Q·10", "showdown-pair-kickers");
        }

        private IEnumerator ComparePairRanks(string deck, string resultText, string ownDetails, string otherDetails, string capture)
        {
            screen.Dispose();
            table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), 4, new PrefixRandom(deck),
                new FixedRandom(), new PassivePolicy(), HoldemOddChipRule.ClockwiseFromButton);
            screen = new HoldemTableScreen(Root, table.Human, () => restarts++, 100, Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            yield return CompletePassive(); yield return Resize(new Vector2Int(960, 640));
            var settled = table.Human.Read();
            Assert.That(Root.Q<Label>(className: "omc-result").text, Is.EqualTo(resultText));
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("쇼다운 · 나: " + ownDetails));
            var own = Root.Q<Button>("omc-best-seat-1");
            Assert.That(own.text, Is.EqualTo(KoreanPokerText.HandSummary(settled.GetSeatAt(0).RevealedHandValue.Value)));
            Assert.That(own.tooltip, Does.Contain(ownDetails));
            yield return WaitEnabled("omc-best-seat-2"); AssertButtonHit("omc-best-seat-2"); Submit("omc-best-seat-2");
            yield return null;
            Assert.That(Root.Q<Label>(className: "omc-last").text, Is.EqualTo("쇼다운 · 상대 1: " + otherDetails));
            AssertHighlightedBest(settled, new SeatId(2));
            AssertLayout(); Capture(capture);
            Assert.That(Root.Q<Button>("omc-pot-details").text, Is.EqualTo("승부 내역"));
            AssertButtonHit("omc-pot-details"); Submit("omc-pot-details"); yield return null;
            var copy = Root.Q<Label>("omc-pot-details-copy").text;
            Assert.That(copy, Does.StartWith("메인 팟"));
            Assert.That(copy, Does.Contain("나: " + ownDetails));
            Assert.That(copy, Does.Contain("상대 1: " + otherDetails));
            Assert.That(copy, Does.Contain("앞에서부터 차례대로 비교"));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(settled.SessionVersion));
            for (int i = 0; i < settled.SeatCount; i++)
                Assert.That(table.Human.Read().GetSeatAt(i).Stack, Is.EqualTo(settled.GetSeatAt(i).Stack));
            Capture(capture + "-details");
        }

        private void AssertHighlightedBest(HoldemSnapshot snapshot, SeatId selected)
        {
            var seat = snapshot.GetSeat(selected);
            Assert.That(seat.RevealedBestCardCount, Is.EqualTo(5));
            var expected = new HashSet<Card>(Enumerable.Range(0, 5).Select(seat.GetRevealedBestCard));
            for (int i = 0; i < snapshot.BoardCount; i++)
                Assert.That(Root.Q("omc-board").ElementAt(i).ClassListContains("best"),
                    Is.EqualTo(expected.Contains(snapshot.GetBoardCard(i))), "board card " + i);
            for (int i = 0; i < snapshot.SeatCount; i++)
            {
                var participant = snapshot.GetSeatAt(i);
                var cards = Root.Q("omc-seat-" + participant.Seat.Value).Q(className: "omc-cards");
                for (int j = 0; j < participant.VisibleHoleCardCount; j++)
                    Assert.That(cards.ElementAt(j).ClassListContains("best"), Is.EqualTo(participant.Seat == selected
                        && expected.Contains(participant.GetVisibleHoleCard(j))), "seat " + participant.Seat.Value + " card " + j);
            }
            Assert.That(Root.Query(className: "best").ToList().Count, Is.EqualTo(5));
        }
    }
}
