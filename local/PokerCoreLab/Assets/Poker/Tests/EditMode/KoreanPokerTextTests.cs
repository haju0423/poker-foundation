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

        [Test]
        public void PotAndPayoutLabelsDoNotCallGrossReceiptsProfit()
        {
            Assert.That(KoreanPokerText.PotLabel(0, 120), Is.EqualTo("메인 팟 · 120칩"));
            Assert.That(KoreanPokerText.PotLabel(1, 60), Is.EqualTo("사이드 팟 1 · 60칩"));
            Assert.That(KoreanPokerText.PayoutLabel(120), Is.EqualTo("팟 지급액 · 120칩"));
            Assert.That(KoreanPokerText.PayoutLabel(6, true), Is.EqualTo("팟 지급액 · 6칩 (나머지 1칩 포함)"));
            Assert.That(KoreanPokerText.RefundMessage(60), Is.EqualTo("다른 사람이 따라 내지 않은 60칩을 돌려받습니다."));
        }

        [TestCase(SettlementFailure.MissingOddChipOrder, "동률로 남은 칩을 나눠야 정산을 마칠 수 있습니다.")]
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

        [TestCase("Ac Kd Qh 9s 7c", "하이 카드 A")]
        [TestCase("6c 6d Ah Qs Tc", "원페어 6")]
        [TestCase("Ac Ad Qh Qs 7c", "투페어 A·Q")]
        [TestCase("Ac Ad Ah 9s 7c", "트리플 A")]
        [TestCase("Ac 2d 3h 4s 5c", "스트레이트 5")]
        [TestCase("Ac Kc Qc 9c 7c", "플러시 A")]
        [TestCase("Qc Qd Qh 7s 7c", "풀하우스 Q·7")]
        [TestCase("Ac Ad Ah As 7c", "포카드 A")]
        [TestCase("9c Tc Jc Qc Kc", "스트레이트 플러시 K")]
        [TestCase("Tc Jc Qc Kc Ac", "로열 스트레이트 플러시")]
        public void CompactHandSummaryUsesMainRanksButDoesNotReplaceFullComparison(string cards, string expected) =>
            Assert.That(KoreanPokerText.HandSummary(HandEvaluator.Evaluate(Parse(cards))), Is.EqualTo(expected));

        [Test]
        public void EqualCompactPairLabelsCanStillHaveDifferentKickers()
        {
            var stronger = HandEvaluator.Evaluate(Parse("6c 6d Ah Ks Tc"));
            var weaker = HandEvaluator.Evaluate(Parse("6c 6d Ah Qs Tc"));
            Assert.That(stronger.CompareTo(weaker), Is.GreaterThan(0));
            Assert.That(KoreanPokerText.HandSummary(stronger), Is.EqualTo(KoreanPokerText.HandSummary(weaker)));
            Assert.That(KoreanPokerText.HandDescription(stronger), Is.EqualTo("원페어 6 · 남은 카드 A·K·10"));
            Assert.That(KoreanPokerText.HandDescription(weaker), Is.EqualTo("원페어 6 · 남은 카드 A·Q·10"));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.HandSummary(default));
        }

        [TestCase("Ac Kd Qh 9s 7c", "하이 카드 · A·K·Q·9·7")]
        [TestCase("Ac Ad Qh 9s 7c", "원페어 A · 남은 카드 Q·9·7")]
        [TestCase("Ac Ad Qh Qs 7c", "투페어 A·Q · 남은 카드 7")]
        [TestCase("Ac Ad Ah 9s 7c", "트리플 A · 남은 카드 9·7")]
        [TestCase("Ac 2d 3h 4s 5c", "스트레이트 · 5까지 연속")]
        [TestCase("Ac Kc Qc 9c 7c", "플러시 · A·K·Q·9·7")]
        [TestCase("Ac Ad Ah 9s 9c", "풀하우스 · A 3장, 9 2장")]
        [TestCase("Ac Ad Ah As 7c", "포카드 A · 남은 카드 7")]
        [TestCase("9c Tc Jc Qc Kc", "스트레이트 플러시 · K까지 연속")]
        [TestCase("Tc Jc Qc Kc Ac", "로열 스트레이트 플러시")]
        public void HandDescriptionsExposeEvaluatedRanksInComparisonOrder(string cards, string expected) =>
            Assert.That(KoreanPokerText.HandDescription(HandEvaluator.Evaluate(Parse(cards))), Is.EqualTo(expected));

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

        [TestCase(false, false, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void RoomDescriptionUsesOnlyTheCapturedRules(bool dealGate, bool revealGate, bool speech)
        {
            var rules = new Poker.Application.HoldemRoomRules(new HoldemConfig(250, 5, 10,
                revealGate ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                dealPolicy: dealGate ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic), speech);
            string text = KoreanPokerText.RoomSettingsDescription(rules);
            Assert.That(text, Does.StartWith("방 인원: 4인\n시작 칩: 250칩\n스몰 블라인드: 5칩\n빅 블라인드: 10칩"));
            Assert.That(text, Does.Contain("공용 카드: " + (dealGate ? "방장이 공개" : "자동 공개")));
            Assert.That(text, Does.Contain("카드 공개 후: " + (revealGate ? "확인 후 방장이 계속" : "별도 확인 없이 계속")));
            Assert.That(text, Does.Contain(speech ? "접수 확인용" : "멘트: 사용 안 함"));
            Assert.That(text, Does.Contain("현재 보유 칩으로 이어져요"));
        }

        [Test]
        public void MissingRoomDescriptionDoesNotInventDefaultsAndLargeAmountsStayExact()
        {
            string missing = KoreanPokerText.RoomSettingsDescription(null);
            Assert.That(missing, Does.Contain("방장에게 확인"));
            Assert.That(missing, Does.Not.Contain("100").And.Not.Contain("자동 공개"));
            var rules = new Poker.Application.HoldemRoomRules(new HoldemConfig(long.MaxValue / 4, 1, long.MaxValue), false);
            string text = KoreanPokerText.RoomSettingsDescription(rules);
            Assert.That(text, Does.Contain("시작 칩: 2,305,843,009,213,693,951칩"));
            Assert.That(text, Does.Contain("빅 블라인드: 9,223,372,036,854,775,807칩"));
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
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.ActionName((BettingActionKind)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.SettlementMessage((SettlementFailure)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.SettlementMessage(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.HandName(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.HandDescription(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.CardName(default));
            Assert.Throws<ArgumentException>(() => KoreanPokerText.RankLabel(default));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.PayoutLabel(0, true));
        }

        [Test]
        public void HelpTextExplainsFoldCheckAndAllInWithoutCardExchange()
        {
            Assert.That(KoreanPokerText.FoldHelp, Does.Contain("확정된 칩"));
            Assert.That(KoreanPokerText.CheckHelp, Does.Contain("추가로 칩을 내지 않고"));
            Assert.That(KoreanPokerText.AllInHelp, Does.Contain("해당하는 팟"));
            Assert.That(KoreanPokerText.AllInHelp, Does.Not.Contain("교환"));
        }

        [Test]
        public void RemainderActionExplainsThePayoutAndItsScope()
        {
            Assert.That(KoreanPokerText.SplitRemainderLabel, Is.EqualTo("나머지 칩 배분 확인"));
            Assert.That(KoreanPokerText.SplitRemainderHelp, Does.Contain("동률인 승자 중 딜러 다음 자리"));
            Assert.That(KoreanPokerText.SplitRemainderHelp, Does.Contain("이번 판에만 적용"));
            Assert.That(KoreanPokerText.SplitRemainderPrompt, Does.Contain("모든 팟을 정산"));
        }

        [TestCase(HoldemCommandError.WrongTurn, "다른 참가자의 차례")]
        [TestCase(HoldemCommandError.IllegalAction, "가능한 행동과 금액")]
        [TestCase(HoldemCommandError.VersionMismatch, "진행 상태가 바뀌었어요")]
        [TestCase(HoldemCommandError.RevealPending, "‘계속’")]
        [TestCase(HoldemCommandError.SettlementRuleRequired, "배분 순서")]
        [TestCase(HoldemCommandError.WrongSession, "현재 테이블에 연결하지 못했어요")]
        [TestCase(HoldemCommandError.AccusationConsequencesPending, "고발 결과를 처리")]
        public void RejectedActionsExplainTheRelevantNextStep(HoldemCommandError reason, string expected) =>
            Assert.That(KoreanPokerText.CommandErrorMessage(reason), Does.Contain(expected));

        [TestCase(Rank.Ace, "A")]
        [TestCase(Rank.King, "K")]
        [TestCase(Rank.Queen, "Q")]
        [TestCase(Rank.Jack, "J")]
        [TestCase(Rank.Ten, "10")]
        [TestCase(Rank.Two, "2")]
        public void CardFacesUseTheSharedRankLabel(Rank rank, string expected) =>
            Assert.That(KoreanPokerText.RankLabel(new Card(rank, Suit.Spades)), Is.EqualTo(expected));

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
