using System;
using NUnit.Framework;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class KoreanHandTextTests
    {
        [TestCase(HandPhase.FirstBetting, "첫 베팅")]
        [TestCase(HandPhase.Exchange, "카드 교환")]
        [TestCase(HandPhase.SecondBetting, "두 번째 베팅")]
        [TestCase(HandPhase.AwaitingSettlementRule, "정산 대기")]
        [TestCase(HandPhase.Complete, "이번 판 종료")]
        public void PhaseNamesDescribeActualProgress(HandPhase phase, string expected)
            => Assert.That(KoreanPokerText.PhaseName(phase), Is.EqualTo(expected));

        [TestCase(HandError.UnauthorizedSeat, "이 좌석으로 플레이할 권한이 없습니다.")]
        [TestCase(HandError.WrongHand, "다른 판의 요청입니다. 현재 판을 확인해 주세요.")]
        [TestCase(HandError.CommandConflict, "이미 처리한 요청과 내용이 다릅니다. 현재 상태를 확인해 주세요.")]
        [TestCase(HandError.VersionMismatch, "진행 상황이 바뀌었습니다. 현재 상태를 확인해 주세요.")]
        [TestCase(HandError.NotStarted, "아직 판이 시작되지 않았습니다.")]
        [TestCase(HandError.AlreadyStarted, "이미 시작한 판입니다.")]
        [TestCase(HandError.Complete, "이번 판은 끝났습니다.")]
        [TestCase(HandError.SettlementRuleRequired, "남는 칩의 지급 기준을 정해야 정산을 마칠 수 있습니다.")]
        [TestCase(HandError.WrongPhase, "지금은 이 행동을 할 수 없습니다.")]
        [TestCase(HandError.WrongTurn, "아직 내 차례가 아닙니다.")]
        [TestCase(HandError.IllegalBet, "지금 가능한 베팅과 금액을 확인해 주세요.")]
        [TestCase(HandError.CardNotOwned, "현재 내 패에 있는 카드만 바꿀 수 있습니다.")]
        [TestCase(HandError.Busy, "처리 중입니다. 잠시 후 다시 시도해 주세요.")]
        public void FixedReasonsHaveNaturalKoreanMessages(HandError error, string expected)
            => Assert.That(KoreanPokerText.CommandErrorMessage(error), Is.EqualTo(expected));

        [Test]
        public void DefaultUnknownAndSuccessValuesAreNotDisplayedAsErrorsOrPhases()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.PhaseName(HandPhase.Invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.PhaseName((HandPhase)999));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.CommandErrorMessage(HandError.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => KoreanPokerText.CommandErrorMessage((HandError)999));
        }
    }
}
