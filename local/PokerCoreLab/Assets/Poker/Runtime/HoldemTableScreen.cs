using System;
using System.Collections.Generic;
using System.Globalization;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Seat-bound view and intents only; no deck, session authority or hidden opponent state.</summary>
    public sealed class HoldemTableScreen : IDisposable
    {
        public const string HelpText = "내 카드 2장과 공용 카드 5장 중 가장 좋은 5장으로 승부해요. 내 카드를 꼭 쓸 필요는 없어요.\n\n체크는 추가 베팅 없이 넘기기, 콜은 상대 금액 따라가기, 폴드는 이번 판 포기예요.\n\n베팅 입력값은 이번 단계에서 낼 총액이에요. 다음 판에도 칩은 유지되고, 버튼 위치는 바뀌어요.\n\n현재는 포커 기본형이에요. 대화·카드 조작 기능은 아직 없어요.";
        private readonly VisualElement root;
        private readonly IHoldemPlayerPort port;
        private readonly Action restart;
        private readonly Action abandonSession;
        private readonly long startingStack;
        private readonly List<Label> stages = new List<Label>();
        private HoldemSnapshot view;
        private readonly List<SeatWidgets> seatWidgets = new List<SeatWidgets>();
        private Label counter, pot, result, last, prompt, error, hint;
        private VisualElement board, amountRow, actionRow, help, resetConfirmation;
        private Button fold, passive, aggressive, next, reset, retry, resolve;
        private TextField amount;
        private bool disposed, paused, helpOpen;
        private double lockedUntil;
        private IVisualElementScheduledItem unlock;
        public bool IsProgressPaused => paused || helpOpen || disposed;
        public VisualElement Root => root;

        public HoldemTableScreen(VisualElement root, IHoldemPlayerPort port, Action restart, long startingStack, Font font, Action abandonSession = null)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.restart = restart ?? throw new ArgumentNullException(nameof(restart));
            this.abandonSession = abandonSession;
            this.startingStack = startingStack;
            StyleSheet sheet = Resources.Load<StyleSheet>("HoldemTable");
            if (sheet == null || font == null) throw new InvalidOperationException("Hold'em UI resources are missing.");
            root.Clear(); root.AddToClassList("omc-root");
            root.style.unityFont = font; if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometry);
            Build(); Render();
        }

        private void Build()
        {
            var header = Box("omc-header", root);
            var title = Box("omc-heading", header);
            title.Add(Text("One More Card", "omc-title"));
            title.Add(Text("텍사스 홀덤 · 포커 기본형", "omc-subtitle"));
            var right = Box("omc-header-right", header);
            counter = Text("", "omc-counter"); right.Add(counter);
            right.Add(Click("도움말", "omc-help", () => { helpOpen = true; Show(help, true); }));
            var steps = Box("omc-stages", root);
            foreach (string name in new[] { "프리플랍", "플랍", "턴", "리버" })
            { var label = Text(name, "omc-stage"); stages.Add(label); steps.Add(label); }
            var table = Box("omc-table", root);
            var initial = port.Read();
            root.EnableInClassList("multi-seat", initial.SeatCount > 2);
            var opponents = Box("omc-opponents", table);
            bool firstOpponent = true;
            for (int i = 0; i < initial.SeatCount; i++)
            {
                var seat = initial.GetSeatAt(i);
                if (seat.IsViewer) continue;
                seatWidgets.Add(CreateSeat(opponents, seat.Seat, false, firstOpponent));
                firstOpponent = false;
            }
            var middle = Box("omc-middle", table);
            pot = Text("", "omc-pot"); middle.Add(pot);
            board = Box("omc-board", middle); board.AddToClassList("omc-cards"); board.name = "omc-board";
            result = Text("", "omc-result"); middle.Add(result);
            last = Text("", "omc-last"); middle.Add(last);
            seatWidgets.Add(CreateSeat(table, initial.ViewerSeat, true, false));
            var controls = Box("omc-controls", root);
            prompt = Text("", "omc-prompt"); controls.Add(prompt);
            amountRow = Box("omc-amounts", controls);
            amountRow.Add(Text("이번 베팅 총액", "omc-caption"));
            amount = new TextField { name = "omc-target", maxLength = 19 };
            amount.AddToClassList("omc-target"); amount.RegisterValueChangedCallback(_ => UpdateAmount());
            amountRow.Add(amount);
            amountRow.Add(Click("최소", "omc-min", () => SetTarget(0)));
            amountRow.Add(Click("절반 팟", "omc-half", () => SetTarget(1)));
            var all = Click("전액", "omc-max", () => SetTarget(2));
            all.tooltip = "전액을 입력해요. 베팅 버튼을 눌러야 실제로 칩을 냅니다."; amountRow.Add(all);
            hint = Text("", "omc-amount-hint"); amountRow.Add(hint);
            actionRow = Box("omc-actions", controls);
            fold = Click("폴드", "omc-fold", () => Bet(BettingAction.Fold())); fold.AddToClassList("omc-quiet");
            passive = Click("", "omc-passive", () => { if (view.LegalActions != null) Bet(view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call()); });
            passive.AddToClassList("omc-primary");
            aggressive = Click("", "omc-aggressive", Aggress);
            next = Click("다음 판", "omc-next", Next);
            next.AddToClassList("omc-primary");
            reset = Click("처음부터", "omc-reset", Restart); reset.AddToClassList("omc-quiet");
            retry = Click("진행 다시 확인", "omc-retry", Recover);
            resolve = Click("이번 판에 임시 규칙 적용", "omc-resolve", () =>
            {
                if (CanInteract() && view.IsSettlementPending) Run(() => port.ResolvePendingSettlement(view.SessionVersion));
            });
            resolve.tooltip = "동률인 승자 중 버튼 다음 자리부터 남는 칩을 지급해요. 이번 판에만 적용하며 팀의 최종 규칙을 정하지 않아요.";
            foreach (var button in new[] { fold, passive, aggressive, next, reset, retry, resolve }) actionRow.Add(button);
            error = Text("", "omc-error"); controls.Add(error);
            help = Box("omc-overlay", root);
            var helpCard = Box("omc-help-card", help);
            helpCard.Add(Text("한 판만 해보면 돼요", "omc-help-title"));
            helpCard.Add(Text(HelpText, "omc-help-copy"));
            helpCard.Add(Click("닫기", "omc-close-help", () => { helpOpen = false; Show(help, false); }));
            Show(help, false);
            resetConfirmation = Box("omc-overlay", root);
            var resetCard = Box("omc-help-card", resetConfirmation);
            resetCard.Add(Text("새 게임을 시작할까요?", "omc-help-title"));
            resetCard.Add(Text("현재 판과 보유 칩을 초기화해요. 진행 중이던 판으로 돌아올 수는 없어요.", "omc-help-copy"));
            resetCard.Add(Click("현재 판 유지", "omc-cancel-reset", () => Show(resetConfirmation, false)));
            resetCard.Add(Click("초기화하고 새 게임", "omc-confirm-reset", () => {
                if (!paused || abandonSession == null) return;
                Show(resetConfirmation, false);
                try { abandonSession(); }
                catch (Exception e) { PauseProgress(); Debug.LogError("Holdem recovery start failed (" + e.GetType().Name + ")."); }
            }));
            Show(resetConfirmation, false);
        }

        public void Render()
        {
            if (disposed) return;
            HoldemSnapshot previous = view;
            view = port.Read();
            bool changed = previous == null || previous.SessionVersion != view.SessionVersion || previous.SessionId != view.SessionId;
            bool complete = view.Result != null;
            bool pending = view.IsSettlementPending;
            root.EnableInClassList("complete", complete);
            root.EnableInClassList("pending", pending);
            counter.text = view.SeatCount + "인 테이블 · " + view.HandNumber.ToString(CultureInfo.InvariantCulture) + "번째 판";
            int stageIndex = complete ? 4 : (int)view.Street;
            for (int i = 0; i < stages.Count; i++)
            {
                stages[i].EnableInClassList("current", i == stageIndex);
                stages[i].EnableInClassList("past", i < stageIndex && (i == 0 || view.BoardCount >= i + 2));
            }
            foreach (var widgets in seatWidgets) RenderSeat(widgets, view.GetSeat(widgets.Seat));
            pot.text = (complete ? "정산 팟  " : "팟  ") + KoreanPokerText.Chips(view.PotAmount);
            pot.tooltip = PotDetails();
            board.Clear();
            var own = view.GetSeat(view.ViewerSeat);
            var ownBest = new HashSet<Card>();
            for (int i = 0; i < own.RevealedBestCardCount; i++) ownBest.Add(own.GetRevealedBestCard(i));
            for (int i = 0; i < 5; i++)
                board.Add(i < view.BoardCount ? Face(view.GetBoardCard(i), ownBest.Contains(view.GetBoardCard(i)))
                    : Empty(i == 1 ? "플랍" : i == 3 ? "턴" : i == 4 ? "리버" : ""));
            result.text = pending ? "팟 분배 · 남는 칩 지급 대기" : ResultText();
            Show(result, complete || pending);
            last.text = pending ? "동률인 승자 중 버튼 다음 자리부터 남는 칩을 지급해요." : NoticeText();
            LegalBettingActions legal = view.LegalActions;
            bool active = legal != null && !paused && Time.realtimeSinceStartupAsDouble >= lockedUntil;
            bool canAggress = legal != null && (legal.CanBet || legal.CanRaise);
            Show(amountRow, canAggress && !paused);
            amountRow.SetEnabled(active);
            Show(fold, legal != null && !paused); fold.SetEnabled(active && legal.CanFold);
            Show(passive, legal != null && !paused); passive.SetEnabled(active && (legal.CanCheck || legal.CanCall));
            if (legal != null) passive.text = legal.CanCheck ? "체크" : "콜  " + KoreanPokerText.Chips(legal.CallAmount);
            Show(aggressive, canAggress && !paused);
            if (changed && canAggress) amount.SetValueWithoutNotify(legal.MinimumAggressiveTarget.Value.ToString(CultureInfo.InvariantCulture));
            Show(next, complete && view.CanContinue && !paused);
            next.text = own.Status == HoldemSeatStatus.Busted ? "다음 판 관전" : "다음 판";
            Show(resolve, pending && !paused); resolve.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            next.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(reset, complete && !paused); reset.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            reset.tooltip = "모든 참가자의 칩을 " + KoreanPokerText.Chips(startingStack) + "으로 초기화합니다.";
            Show(retry, paused);
            prompt.text = pending ? "이번 판의 남는 칩 지급 방법을 선택해 주세요."
                : complete ? (view.IsOver ? (view.OwnStack > 0 ? "모든 칩을 가져왔어요!" : "테이블 승부가 끝났어요.")
                    : view.OwnStack == 0 ? "칩을 모두 잃었어요. 다음 판은 관전할 수 있어요." : "다음 판에도 칩은 그대로 이어져요.")
                : own.Status == HoldemSeatStatus.Busted ? "관전 중 · " + ActorText()
                : own.Status == HoldemSeatStatus.Folded ? "이번 판은 폴드했어요 · " + ActorText()
                : legal == null ? ActorText() : "내 차례예요.";
            if (changed) error.text = "";
            UpdateAmount();
            if (paused) ApplyPaused();
        }

        private void UpdateAmount()
        {
            if (disposed || aggressive == null || view == null) return;
            var legal = view.LegalActions;
            bool valid = legal != null && (legal.CanBet || legal.CanRaise)
                && long.TryParse(amount.value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                && parsed >= legal.MinimumAggressiveTarget.Value && parsed <= legal.MaximumAggressiveTarget.Value;
            aggressive.SetEnabled(valid && !paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            if (legal == null || (!legal.CanBet && !legal.CanRaise)) { hint.text = ""; return; }
            aggressive.text = legal.CanBet ? "베팅" : "레이즈";
            hint.text = legal.MinimumAggressiveTarget.Value + "–" + legal.MaximumAggressiveTarget.Value + "칩";
            if (valid)
            {
                long total = long.Parse(amount.value, CultureInfo.InvariantCulture);
                aggressive.text += "  " + KoreanPokerText.Chips(total);
                hint.text = KoreanPokerText.Chips(total - view.OwnStreetContribution) + " 추가";
            }
        }

        private void SetTarget(int mode)
        {
            if (disposed || paused || view.LegalActions == null) return;
            var legal = view.LegalActions;
            if (!legal.MinimumAggressiveTarget.HasValue) return;
            decimal target = mode == 0 ? legal.MinimumAggressiveTarget.Value : mode == 2 ? legal.MaximumAggressiveTarget.Value
                : view.OwnStreetContribution + (decimal)legal.CallAmount + decimal.Floor(((decimal)view.PotAmount + legal.CallAmount) / 2m);
            target = Math.Max(legal.MinimumAggressiveTarget.Value, Math.Min(legal.MaximumAggressiveTarget.Value, target));
            amount.value = ((long)target).ToString(CultureInfo.InvariantCulture);
        }
        private void Aggress()
        {
            if (view.LegalActions == null || !long.TryParse(amount.value, NumberStyles.None, CultureInfo.InvariantCulture, out long target) || target <= 0) return;
            Bet(view.LegalActions.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target));
        }
        private void Bet(BettingAction action)
        {
            if (!CanInteract() || view.LegalActions == null || !view.LegalActions.Allows(action)) return;
            Run(() => port.Submit(HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), view.ViewerSeat, view.SessionVersion, action)));
        }
        private void Next()
        {
            if (!CanInteract() || view.Result == null || !view.CanContinue) return;
            Run(() => port.NextHand(view.SessionVersion));
        }
        private void Restart()
        {
            if (!disposed && paused && abandonSession != null) { Show(resetConfirmation, true); return; }
            if (!CanInteract() || view.Result == null) return;
            try { restart(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem restart failed (" + e.GetType().Name + ")."); }
        }
        private bool CanInteract() => !disposed && !paused && !helpOpen && Time.realtimeSinceStartupAsDouble >= lockedUntil;
        private void Run(Func<HoldemReceipt> action)
        {
            try
            {
                var receipt = action();
                if (receipt.Accepted) lockedUntil = Time.realtimeSinceStartupAsDouble + 0.2;
                Render();
                if (!receipt.Accepted) error.text = "진행 상황이 바뀌었어요. 현재 가능한 행동을 다시 골라 주세요.";
                unlock?.Pause(); unlock = root.schedule.Execute(() => { if (!disposed && !paused) Recover(); }).StartingIn(220);
            }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem input paused (" + e.GetType().Name + "); no action replay."); }
        }
        public void PauseProgress()
        {
            if (disposed) return; paused = true; ApplyPaused();
        }
        private void ApplyPaused()
        {
            Show(amountRow, false);
            foreach (var button in new[] { fold, passive, aggressive, next, reset, resolve }) { button.SetEnabled(false); Show(button, false); }
            if (abandonSession != null) { Show(reset, true); reset.SetEnabled(true); }
            Show(retry, true); prompt.text = "진행을 잠시 멈췄어요.";
            error.text = "현재 판은 유지돼요. 다시 확인해 주세요.";
        }
        private void Recover()
        {
            if (disposed) return;
            try { paused = false; Render(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem display paused (" + e.GetType().Name + ")."); }
        }
        private SeatWidgets CreateSeat(VisualElement parent, SeatId seat, bool own, bool firstOpponent)
        {
            var box = Box(own ? "omc-player" : "omc-opponent", parent); box.AddToClassList("omc-seat");
            box.name = "omc-seat-" + seat.Value;
            var info = Box("omc-seat-info", box);
            var title = Text("", "omc-seat-title"); title.AddToClassList("omc-seat-name"); info.Add(title);
            var stack = Text("", "omc-stack"); info.Add(stack);
            var status = Text("", "omc-seat-status"); info.Add(status);
            var handArea = Box("omc-hand-area", box);
            var cards = Box("omc-cards", handArea);
            cards.name = own ? "omc-own-cards" : firstOpponent ? "omc-opponent-cards" : "omc-opponent-cards-" + seat.Value;
            var hand = Text("", "omc-hand-label"); handArea.Add(hand);
            return new SeatWidgets { Seat = seat, Box = box, Title = title, Stack = stack, Status = status, Cards = cards, Hand = hand };
        }
        private void RenderSeat(SeatWidgets widgets, HoldemSeatView seat)
        {
            string badges = seat.IsButton ? "버튼" : "";
            if (seat.IsSmallBlind) badges += badges.Length == 0 ? "SB" : " / SB";
            if (seat.IsBigBlind) badges += badges.Length == 0 ? "BB" : " / BB";
            widgets.Title.text = SeatName(seat.Seat) + (badges.Length > 0 ? "  ·  " + badges : "");
            widgets.Title.EnableInClassList("acting", seat.IsCurrentActor);
            widgets.Box.EnableInClassList("acting-seat", seat.IsCurrentActor);
            widgets.Box.EnableInClassList("inactive-seat", seat.Status == HoldemSeatStatus.Folded || seat.Status == HoldemSeatStatus.Busted);
            widgets.Stack.text = KoreanPokerText.Chips(seat.Stack);
            widgets.Status.text = view.Result != null ? "팟에서 " + KoreanPokerText.Chips(seat.Awarded) + " 지급"
                : seat.Status == HoldemSeatStatus.Busted ? "탈락"
                : seat.Status == HoldemSeatStatus.Folded ? "폴드"
                : seat.Status == HoldemSeatStatus.AllIn ? "올인"
                : "이번 베팅 " + KoreanPokerText.Chips(seat.StreetContribution);
            widgets.Cards.Clear();
            var best = new HashSet<Card>();
            if (seat.IsViewer)
                for (int i = 0; i < seat.RevealedBestCardCount; i++) best.Add(seat.GetRevealedBestCard(i));
            if (seat.WasDealtIn)
                for (int i = 0; i < 2; i++) widgets.Cards.Add(seat.VisibleHoleCardCount > i
                    ? Face(seat.GetVisibleHoleCard(i), best.Contains(seat.GetVisibleHoleCard(i))) : Back());
            else widgets.Cards.Add(Text("관전 중", "omc-hand-label"));
            widgets.Hand.text = seat.Status == HoldemSeatStatus.Folded ? "폴드" : seat.RevealedHandValue.HasValue
                ? KoreanPokerText.HandName(seat.RevealedHandValue.Value) : seat.IsViewer ? OwnHandLabel() : "";
            Show(widgets.Hand, widgets.Hand.text.Length > 0);
        }
        private string OwnHandLabel()
        {
            if (view.OwnCardCount != 2) return "";
            if (view.BoardCount < 3) return "내 카드 2장";
            var cards = new Card[view.BoardCount];
            for (int i = 0; i < cards.Length; i++) cards[i] = view.GetBoardCard(i);
            return KoreanPokerText.HandName(HoldemBestHand.Evaluate(cards, new[] { view.GetOwnCard(0), view.GetOwnCard(1) }).Value);
        }
        private string SeatName(SeatId seat) => seat == view.ViewerSeat ? "나" : view.SeatCount == 2 ? "상대" : "상대 " + view.GetSeat(seat).TableIndex;
        private string ActorText() => view.CurrentSeat.HasValue ? SeatName(view.CurrentSeat.Value) + " 차례예요." : "진행을 확인하고 있어요.";
        private string ResultText()
        {
            var r = view.Result;
            if (r == null) return "";
            string winner = r.WinnerSeat == null ? (r.PotCount > 1 ? "팟별 정산 완료" : "무승부 · 팟 나눔") : SeatName(r.WinnerSeat.Value) + " 승리";
            if (r.Kind != HoldemResultKind.Showdown) return winner + " · 폴드로 종료";
            if (r.WinnerSeat == null) return winner;
            var value = view.GetSeat(r.WinnerSeat.Value).RevealedHandValue;
            return winner + (value.HasValue ? " · " + KoreanPokerText.HandName(value.Value) : "");
        }
        private string PotDetails()
        {
            if (view.Result == null) return "";
            var lines = new List<string>();
            for (int i = 0; i < view.Result.PotCount; i++)
            {
                var award = view.Result.GetPot(i);
                var recipients = new List<string>();
                for (int j = 0; j < award.PayoutCount; j++)
                {
                    var paid = award.GetPayout(j);
                    recipients.Add(SeatName(paid.Seat) + " " + KoreanPokerText.Chips(paid.Amount));
                }
                lines.Add((i == 0 ? "메인 팟" : "사이드 팟 " + i) + " · " + string.Join(", ", recipients));
            }
            return string.Join("\n", lines);
        }
        private sealed class SeatWidgets
        {
            public SeatId Seat;
            public VisualElement Box, Cards;
            public Label Title, Stack, Status, Hand;
        }
        private string NoticeText()
        {
            if (view.Result != null)
                return view.Result.Kind == HoldemResultKind.Showdown ? (view.GetSeat(view.ViewerSeat).RevealedBestCardCount > 0 ? "쇼다운 · 내 최종 조합 5장을 금색으로 표시했어요." : "쇼다운 · 끝까지 남은 참가자의 패를 공개해요.") + (view.Result.PotCount > 1 ? " 팟 금액에 마우스를 올리면 분배를 볼 수 있어요." : "") : "쇼다운 없이 끝난 판은 상대 패를 공개하지 않아요.";
            var notice = port.LastAction;
            if (notice == null) return "공용 카드가 차례로 열려요.";
            return SeatName(notice.Seat) + " · " + KoreanPokerText.ActionName(notice.Kind)
                + (notice.Paid > 0 ? " · " + KoreanPokerText.Chips(notice.Paid) + " 추가" : "");
        }
        private static VisualElement Face(Card card, bool best)
        {
            var box = new VisualElement(); box.AddToClassList("omc-card"); box.AddToClassList("face-card");
            box.EnableInClassList("red", card.Suit == Suit.Diamonds || card.Suit == Suit.Hearts);
            box.EnableInClassList("best", best); box.tooltip = KoreanPokerText.CardName(card);
            box.Add(Text(KoreanPokerText.RankLabel(card), "omc-rank"));
            box.Add(Text(SuitSymbol(card.Suit), "omc-suit")); return box;
        }
        public static string SuitSymbol(Suit suit)
        {
            switch (suit)
            {
                case Suit.Clubs: return "♣";
                case Suit.Diamonds: return "♦";
                case Suit.Hearts: return "♥";
                case Suit.Spades: return "♠";
                default: throw new ArgumentOutOfRangeException(nameof(suit));
            }
        }
        private static VisualElement Back()
        { var b = new VisualElement(); b.AddToClassList("omc-card"); b.AddToClassList("hidden"); b.Add(Text("◇", "omc-back")); return b; }
        private static VisualElement Empty(string caption)
        { var b = new VisualElement(); b.AddToClassList("omc-card"); b.AddToClassList("empty"); b.Add(Text(caption, "omc-placeholder")); return b; }
        private Button Click(string text, string name, Action action)
        { return new Button(() => { if (!disposed) action(); }) { name = name, text = text }; }
        private static Label Text(string text, string className)
        { var label = new Label(text); label.AddToClassList(className); return label; }
        private static VisualElement Box(string className, VisualElement parent)
        { var box = new VisualElement(); box.AddToClassList(className); parent.Add(box); return box; }
        private static void Show(VisualElement element, bool show) => element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        private void OnGeometry(GeometryChangedEvent e) => root.EnableInClassList("compact", e.newRect.height < 730 || e.newRect.width < 1050);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; unlock?.Pause(); root.UnregisterCallback<GeometryChangedEvent>(OnGeometry); root.Clear();
        }
    }
}
