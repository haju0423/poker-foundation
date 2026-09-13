using System;
using System.Globalization;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public class KoreanPokerTextTests
    {
        [TestCase(BettingActionKind.Fold, "폴드")]
        [TestCase(BettingActionKind.Check, "체크")]
        [TestCase(BettingActionKind.Call, "콜")]
        [TestCase(BettingActionKind.BetTo, "베팅")]
        [TestCase(BettingActionKind.RaiseTo, "레이즈")]
        public void FamiliarActionNamesStayKorean(BettingActionKind kind, string expected) =>
            Assert.That(KoreanPokerText.ActionName(kind), Is.EqualTo(expected));

        [Test]
        public void CallShowsAdditionalAmountAndDoesNotRenameAnAllInCallToRaise()
        {
            Assert.That(KoreanPokerText.CallLabel(20), Is.EqualTo("콜 · 20칩 추가"));
            Assert.That(KoreanPokerText.CallLabel(6, true), Is.EqualTo("콜 · 6칩 추가 (올인)"));
        }

        [Test]
        public void RaiseTotalAndAdditionalAmountAreSeparateForCompactLayout()
        {
            Assert.That(KoreanPokerText.RaiseLabel(25), Is.EqualTo("레이즈 · 총 25칩"));
            Assert.That(KoreanPokerText.AdditionalChips(15), Is.EqualTo("15칩 추가"));
            Assert.That(KoreanPokerText.RaiseLabel(100, true), Is.EqualTo("레이즈 · 총 100칩 (올인)"));
            Assert.That(KoreanPokerText.BetLabel(100, true), Is.EqualTo("베팅 · 100칩 (올인)"));
        }

        [TestCase(0, "그대로 유지하고 확정")]
        [TestCase(3, "선택한 3장 교환")]
        [TestCase(5, "선택한 5장 교환")]
        public void ExchangeMakesZeroSelectionAnExplicitConfirmation(int count, string label) =>
            Assert.That(KoreanPokerText.ExchangeLabel(count), Is.EqualTo(label));

        [Test]
        public void PotAndPayoutLabelsDoNotCallGrossReceiptsProfit()
        {
            Assert.That(KoreanPokerText.PotLabel(0, 120), Is.EqualTo("메인 팟 · 120칩"));
            Assert.That(KoreanPokerText.PotLabel(1, 60), Is.EqualTo("사이드 팟 1 · 60칩"));
            Assert.That(KoreanPokerText.PayoutLabel(120), Is.EqualTo("팟 지급액 · 120칩"));
            Assert.That(KoreanPokerText.PayoutLabel(6, true), Is.EqualTo("팟 지급액 · 6칩 (나머지 1칩 포함)"));
            Assert.That(KoreanPokerText.RefundMessage(60), Is.EqualTo("다른 사람이 따라 내지 않은 60칩을 돌려받습니다."));
        }

        [TestCase(SettlementFailure.MissingOddChipOrder, "남는 칩의 지급 기준이 아직 정해지지 않았습니다.")]
        [TestCase(SettlementFailure.UnmatchedContribution, "돌려줄 칩을 먼저 처리해야 정산할 수 있습니다.")]
        [TestCase(SettlementFailure.NoEligibleWinner, "팟을 받을 수 있는 참가자 정보가 맞지 않습니다.")]
        [TestCase(SettlementFailure.NothingToAward, "정산할 칩이 없습니다.")]
        public void SettlementReasonsHaveClearKoreanMessages(SettlementFailure reason, string message) =>
            Assert.That(KoreanPokerText.SettlementMessage(reason), Is.EqualTo(message));

        [TestCase("Ac Kd Qh 9s 7c", "하이 카드")]
        [TestCase("Ac Ad Qh 9s 7c", "원페어")]
        [TestCase("Ac Ad Qh Qs 7c", "투페어")]
        [TestCase("Ac Ad Ah 9s 7c", "트리플")]
        [TestCase("Ac 2d 3h 4s 5c", "스트레이트")]
        [TestCase("Ac Kc Qc 9c 7c", "플러시")]
        [TestCase("Ac Ad Ah 9s 9c", "풀하우스")]
        [TestCase("Ac Ad Ah As 7c", "포카드")]
        [TestCase("9c Tc Jc Qc Kc", "스트레이트 플러시")]
        [TestCase("Tc Jc Qc Kc Ac", "로열 스트레이트 플러시")]
        public void HandNamesUseEvaluatedRanksWithoutAddingNewStrength(string cards, string expected)
        {
            HandValue value = HandEvaluator.Evaluate(Parse(cards));
            Assert.That(KoreanPokerText.HandName(value), Is.EqualTo(expected));
            if (expected.StartsWith("로열")) Assert.That(value.Category, Is.EqualTo(HandCategory.StraightFlush));
        }

        [TestCase(Suit.Clubs, Rank.Ten, "클로버 10")]
        [TestCase(Suit.Diamonds, Rank.Jack, "다이아 J")]
        [TestCase(Suit.Hearts, Rank.Queen, "하트 Q")]
        [TestCase(Suit.Spades, Rank.Ace, "스페이드 A")]
        public void CardsHaveReadableKoreanSuitNames(Suit suit, Rank rank, string expected) =>
            Assert.That(KoreanPokerText.CardName(new Card(rank, suit)), Is.EqualTo(expected));

        [Test]
        public void ChipFormattingIsIndependentOfOperatingSystemCultureAndDoesNotChangeIt()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.That(KoreanPokerText.Chips(12345), Is.EqualTo("12,345칩"));
                Assert.That(KoreanPokerText.Chips(long.MaxValue), Is.EqualTo("9,223,372,036,854,775,807칩"));
                Assert.That(KoreanPokerText.Chips(0), Is.EqualTo("0칩"));
                Assert.That(CultureInfo.CurrentCulture.Name, Is.EqualTo("de-DE"));
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        [TestCase(-1L)]
        [TestCase(0L)]
        public void ActionAndRefundAmountsMustBePositive(long amount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.CallLabel(amount));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.BetLabel(amount));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.RaiseLabel(amount));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.RefundMessage(amount));
        }

        [Test]
        public void InvalidDataNeverSilentlyBecomesAPlausibleLabel()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.Chips(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.PotName(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.ExchangeLabel(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.ExchangeLabel(6));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.ActionName((BettingActionKind)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.SettlementMessage((SettlementFailure)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.SettlementMessage(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.HandName(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.CardName(default));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.PayoutLabel(0, true));
        }

        [Test]
        public void HelpTextPreservesFoldKeepAndAllInExchangeMeaning()
        {
            Assert.That(KoreanPokerText.FoldHelp, Does.Contain("확정된 칩"));
            Assert.That(KoreanPokerText.CheckHelp, Does.Contain("추가로 칩을 내지 않고"));
            Assert.That(KoreanPokerText.KeepHandHelp, Does.Contain("교환 차례를 마칩니다"));
            Assert.That(KoreanPokerText.AllInHelp, Does.Contain("폴드하지 않았다면"));
            Assert.That(KoreanPokerText.ExchangeHelp, Does.Contain("한 장도 고르지 않으면"));
        }

        private static Card[] Parse(string notation)
        {
            string[] tokens = notation.Split(' ');
            var cards = new Card[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                int rank = "23456789TJQKA".IndexOf(tokens[i][0]) + 2;
                int suit = "cdhs".IndexOf(tokens[i][1]) + 1;
                cards[i] = new Card((Rank)rank, (Suit)suit);
            }
            return cards;
        }
    }
}
