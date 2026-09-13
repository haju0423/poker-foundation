using System;
using System.Globalization;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>UI-only renderer and intent forwarding. Its only gameplay input is a seat-bound input controller.</summary>
    public sealed class PokerTableScreen : IDisposable
    {
        private readonly VisualElement root;
        private readonly PokerInputController input;
        private readonly Action newPractice;
        private readonly long startingStack;
        private Label phase, instruction, pot, ownStack, otherStack, ownStatus, otherStatus, result, handName, amountHint, message;
        private VisualElement ownCards, actionRow, amountRow, help;
        private Button fold, check, call, aggressive, exchange, restart, retry;
        private TextField target;
        private long renderedVersion = -1;
        private bool disposed;

        public PokerTableScreen(VisualElement root, PokerInputController input, Action newPractice, long startingStack, Font font)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.newPractice = newPractice ?? throw new ArgumentNullException(nameof(newPractice));
            this.startingStack = startingStack;
            root.Clear(); root.AddToClassList("app");
            if (font != null) root.style.unityFont = font;
            StyleSheet sheet = Resources.Load<StyleSheet>("PokerTable");
            if (sheet == null) throw new InvalidOperationException("Poker table style asset is missing.");
            root.styleSheets.Add(sheet);
            Build();
            Render();
        }
        public VisualElement Root => root;
        public void Render()
        {
            if (disposed) return;
            PokerPlayerView view = input.View;
            bool changed = view.Version != renderedVersion;
            renderedVersion = view.Version;
            phase.text = KoreanPokerText.PhaseName(view.Phase);
            instruction.text = KoreanTableText.Instruction(view, input.IsPending);
            pot.text = view.Phase == HandPhase.Complete ? "지급 완료" : KoreanPokerText.Chips(view.PotAmount);
            foreach (PublicSeatView seat in view.Seats)
            {
                bool own = seat.Seat == view.ViewerSeat;
                (own ? ownStack : otherStack).text = KoreanTableText.SeatName(own) + "  ·  " + KoreanPokerText.Chips(seat.Stack);
                (own ? ownStatus : otherStatus).text = seat.Awarded.HasValue
                    ? (seat.Awarded > 0 ? KoreanPokerText.PayoutLabel(seat.Awarded.Value) : "지급 없음")
                    : KoreanTableText.Status(seat) + (view.CurrentSeat == seat.Seat ? " · 차례" : "");
            }
            result.text = KoreanTableText.Result(view);
            handName.text = "내 패  ·  " + KoreanPokerText.HandName(HandEvaluator.Evaluate(view.OwnCards));
            ownCards.Clear();
            foreach (Card card in view.OwnCards)
            {
                var button = new Button(() => { if (input.Toggle(card)) Render(); }) { name = "card-" + card.Id };
                button.AddToClassList("card"); button.EnableInClassList("selected", input.IsSelected(card));
                button.EnableInClassList("red-card", card.Suit == Suit.Hearts || card.Suit == Suit.Diamonds);
                button.tooltip = KoreanPokerText.CardName(card);
                button.Add(Text(KoreanTableText.RankLabel(card), "rank"));
                button.Add(Text(KoreanTableText.SuitLabel(card), "suit"));
                // Retain readable cards outside exchange, but the controller rejects nonexchange selection.
                ownCards.Add(button);
            }
            bool active = !input.IsPending;
            PlayerBettingOptions options = view.Betting;
            Show(fold, options != null); fold.SetEnabled(active && options != null && options.CanFold);
            Show(check, options != null && options.CanCheck); check.SetEnabled(active);
            Show(call, options != null && options.CanCall); call.SetEnabled(active);
            if (options != null && options.CanCall) call.text = KoreanPokerText.CallLabel(options.CallAmount);
            bool canAggress = options != null && (options.CanBet || options.CanRaise);
            Show(amountRow, canAggress); Show(aggressive, canAggress); aggressive.SetEnabled(active);
            if (changed && canAggress) target.SetValueWithoutNotify(options.MinimumAggressiveTarget.Value.ToString(CultureInfo.InvariantCulture));
            UpdateAmountHint();
            Show(exchange, view.CanExchange); exchange.SetEnabled(active);
            exchange.text = KoreanPokerText.ExchangeLabel(input.SelectedCount);
            Show(restart, view.Phase == HandPhase.Complete); restart.SetEnabled(active);
            Show(retry, input.IsPending);
            if (changed) message.text = "";
        }
        private void Build()
        {
            var header = Box("header", root);
            var heading = Box("heading", header);
            heading.Add(Text(KoreanTableText.Title, "title"));
            heading.Add(Text(KoreanTableText.Subtitle, "subtitle"));
            var headingRight = Box("heading-right", header);
            phase = Text("", "phase"); headingRight.Add(phase);
            headingRight.Add(Click("도움말", "help-button", () => Show(help, help.style.display.value == DisplayStyle.None)));
            var table = Box("table", root);
            var other = Box("opponent", table);
            otherStack = Text("", "seat-title"); other.Add(otherStack);
            otherStatus = Text("", "seat-status"); other.Add(otherStatus);
            var backs = Box("backs", other);
            for (int i = 0; i < SeatHand.CardCount; i++) { var back = Box("card-back", backs); back.Add(Text("?", "back-mark")); }
            var center = Box("center-pot", table); center.Add(Text("팟", "pot-caption"));
            pot = Text("", "pot-value"); center.Add(pot);
            result = Text("", "result"); center.Add(result);
            var own = Box("player", table);
            ownStack = Text("", "seat-title"); own.Add(ownStack);
            ownStatus = Text("", "seat-status"); own.Add(ownStatus);
            ownCards = Box("hand", own);
            handName = Text("", "hand-name"); own.Add(handName);
            var controls = Box("controls", root);
            instruction = Text("", "instruction"); controls.Add(instruction);
            amountRow = Box("amount-row", controls);
            amountRow.Add(Text("베팅 총액", "amount-caption"));
            target = new TextField { name = "bet-target", isDelayed = false, maxLength = 19 };
            target.AddToClassList("target"); target.RegisterValueChangedCallback(_ => UpdateAmountHint()); amountRow.Add(target);
            amountRow.Add(Click("최소", "minimum", () => SetTarget(false)));
            amountRow.Add(Click("전액", "maximum", () => SetTarget(true)));
            amountHint = Text("", "amount-hint"); amountRow.Add(amountHint);
            actionRow = Box("actions", controls);
            fold = Click("폴드", "fold", () => Run(() => input.Bet(BettingAction.Fold()))); fold.AddToClassList("quiet"); fold.tooltip = KoreanPokerText.FoldHelp;
            check = Click("체크", "check", () => Run(() => input.Bet(BettingAction.Check()))); check.tooltip = KoreanPokerText.CheckHelp;
            call = Click("콜", "call", () => Run(() => input.Bet(BettingAction.Call())));
            aggressive = Click("베팅", "aggressive", Aggress);
            exchange = Click("", "exchange", () => Run(input.Exchange));
            restart = Click(KoreanTableText.NewPractice(startingStack), "new-practice", () => { if (!input.IsPending && input.View.Phase == HandPhase.Complete) newPractice(); });
            retry = Click(KoreanTableText.Retry, "retry", () => Run(input.RetryPending));
            foreach (Button button in new[] { fold, check, call, aggressive, exchange, restart, retry }) actionRow.Add(button);
            message = Text("", "message"); controls.Add(message);
            help = Box("help-overlay", root);
            help.Add(Text("플레이 방법", "help-title")); help.Add(Text(KoreanTableText.Rules, "help-text"));
            help.Add(Click("알겠어요", "close-help", () => Show(help, false))); Show(help, false);
        }
        private void Run(Func<bool> action)
        {
            if (disposed) return;
            try
            {
                bool accepted = action(); Render();
                if (!accepted && input.LastReceipt != null && !input.LastReceipt.Accepted)
                    message.text = KoreanPokerText.CommandErrorMessage(input.LastReceipt.Error);
            }
            catch (Exception error)
            {
                // The exact pending command stays retained. Do not log cards, requests, or entire exceptions here.
                Render(); message.text = "처리 결과를 확인하지 못했어요. 같은 요청을 다시 확인해 주세요.";
                Debug.LogError("Poker input failed (" + error.GetType().Name + "); pending request retained, no automatic reset.");
            }
        }
        private bool ReadTarget(out long amount)
        {
            PlayerBettingOptions options = input.View.Betting;
            return long.TryParse(target.value, NumberStyles.None, CultureInfo.InvariantCulture, out amount)
                && options != null && options.MinimumAggressiveTarget.HasValue
                && amount >= options.MinimumAggressiveTarget.Value && amount <= options.MaximumAggressiveTarget.Value;
        }
        private void Aggress()
        {
            if (!ReadTarget(out long amount)) { message.text = "표시된 범위 안의 정수 금액을 입력해 주세요."; return; }
            Run(() => input.Bet(input.View.Betting.CanBet ? BettingAction.BetTo(amount) : BettingAction.RaiseTo(amount)));
        }
        private void SetTarget(bool maximum)
        {
            var options = input.View.Betting;
            if (options == null || !options.MinimumAggressiveTarget.HasValue) return;
            target.value = (maximum ? options.MaximumAggressiveTarget.Value : options.MinimumAggressiveTarget.Value).ToString(CultureInfo.InvariantCulture);
        }
        private void UpdateAmountHint()
        {
            if (amountHint == null) return;
            var options = input.View.Betting;
            if (options == null || !options.MinimumAggressiveTarget.HasValue) return;
            string range = options.MinimumAggressiveTarget.Value + " ~ " + options.MaximumAggressiveTarget.Value;
            if (!ReadTarget(out long amount)) { amountHint.text = "가능한 총액  " + range; return; }
            long paid = 0;
            foreach (var seat in input.View.Seats) if (seat.Seat == input.View.ViewerSeat) paid = seat.StreetContribution ?? 0;
            amountHint.text = KoreanPokerText.AdditionalChips(checked(amount - paid)) + "  ·  범위 " + range;
            aggressive.text = options.CanBet ? KoreanPokerText.BetLabel(amount) : KoreanPokerText.RaiseLabel(amount);
        }
        private static Label Text(string text, string style) { var label = new Label(text); label.AddToClassList(style); return label; }
        private static VisualElement Box(string style, VisualElement parent) { var box = new VisualElement(); box.AddToClassList(style); parent.Add(box); return box; }
        private static Button Click(string text, string name, Action clicked) { return new Button(clicked) { text = text, name = name }; }
        private static void Show(VisualElement element, bool shown) { element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None; }
        public void Dispose() { if (disposed) return; disposed = true; root.Clear(); root.styleSheets.Clear(); }
    }
}
