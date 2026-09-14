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
        private readonly Action nextHand;
        private readonly Action retryProgress;
        private readonly long startingStack;
        private Label phase, instruction, pot, ownStack, otherStack, ownStatus, otherStatus, result, handName, amountHint, message, lastAction, breakdown, otherHandName, showdown;
        private VisualElement ownCards, otherCards, actionRow, amountRow, help, resultOverlay;
        private Button fold, check, call, aggressive, exchange, restart, continueHand, retry, resultDetails, minimum, halfPot, maximum, progressRetry;
        private TextField target;
        private long renderedVersion = -1;
        private bool disposed;
        private bool recovering;
        private bool localDisplayRecovery;
        private PracticeProgressFailure progressFailure;

        public PokerTableScreen(VisualElement root, PokerInputController input, Action newPractice, long startingStack, Font font,
            Action retryProgress = null, Action nextHand = null)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.newPractice = newPractice ?? throw new ArgumentNullException(nameof(newPractice));
            this.retryProgress = retryProgress;
            this.nextHand = nextHand;
            this.startingStack = startingStack;
            if (startingStack <= 0) throw new ArgumentOutOfRangeException(nameof(startingStack));
            if (input.View.Seats.Count != 2)
                throw new InvalidOperationException("This practice screen requires exactly two participants.");
            StyleSheet sheet = Resources.Load<StyleSheet>("PokerTable");
            if (sheet == null) throw new InvalidOperationException("Poker table style asset is missing.");
            // Reject unsupported input or missing assets before touching another screen's visual tree.
            root.Clear(); root.AddToClassList("app");
            if (font != null) root.style.unityFont = font;
            root.styleSheets.Add(sheet);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            Build();
            Render();
        }
        public VisualElement Root => root;
        /// <summary>Presentation stop only. The runtime pauses its timer; this is not a poker state or rule.</summary>
        public bool IsProgressPaused => !disposed && progressFailure != PracticeProgressFailure.None;
        public void Render()
        {
            if (disposed) return;
            PokerPlayerView view = input.View;
            bool changed = view.Version != renderedVersion;
            root.EnableInClassList("completed", view.Result != null);
            phase.text = KoreanPokerText.PhaseName(view.Phase);
            instruction.text = KoreanTableText.Instruction(view, input.IsPending);
            pot.text = "팟 " + KoreanPokerText.Chips(view.PotAmount);
            Show(pot, view.Result == null);
            foreach (PublicSeatView seat in view.Seats)
            {
                bool own = seat.Seat == view.ViewerSeat;
                (own ? ownStack : otherStack).text = KoreanTableText.SeatName(own) + "  ·  " + KoreanPokerText.Chips(seat.Stack);
                (own ? ownStatus : otherStatus).text = seat.Awarded.HasValue
                    ? (seat.Awarded > 0 ? KoreanPokerText.PayoutLabel(seat.Awarded.Value) : "지급 없음")
                    : KoreanTableText.Status(seat) + (view.CurrentSeat == seat.Seat ? " · 차례" : "");
            }
            result.text = KoreanTableText.Result(view);
            Show(ownStatus, view.Result == null); Show(otherStatus, view.Result == null);
            lastAction.text = KoreanTableText.LastAction(view);
            Show(lastAction, lastAction.text.Length > 0 && view.Result == null);
            showdown.text = KoreanTableText.ShowdownSummary(view);
            Show(showdown, view.Result != null);
            RenderOtherHand(view);
            Show(result, view.Result != null);
            Show(resultDetails, view.Result != null);
            breakdown.text = KoreanTableText.ResultDetails(view);
            if (view.Result == null) Show(resultOverlay, false);
            handName.text = "내 패  ·  " + KoreanPokerText.HandName(HandEvaluator.Evaluate(view.OwnCards));
            ownCards.Clear();
            foreach (Card card in view.OwnCards)
            {
                var button = new Button(() =>
                {
                    if (!disposed && progressFailure == PracticeProgressFailure.None && input.Toggle(card))
                        RenderInputFeedback(false, false, null);
                }) { name = "card-" + card.Id };
                button.AddToClassList("card"); button.EnableInClassList("selected", input.IsSelected(card));
                button.EnableInClassList("red-card", card.Suit == Suit.Hearts || card.Suit == Suit.Diamonds);
                button.tooltip = KoreanPokerText.CardName(card);
                button.Add(Text(KoreanTableText.RankLabel(card), "rank"));
                button.Add(Text(KoreanTableText.SuitLabel(card), "suit"));
                // Retain readable cards outside exchange, but the controller rejects nonexchange selection.
                ownCards.Add(button);
            }
            bool active = !input.IsPending && progressFailure == PracticeProgressFailure.None;
            PlayerBettingOptions options = view.Betting;
            Show(fold, options != null); fold.SetEnabled(active && options != null && options.CanFold);
            Show(check, options != null && options.CanCheck); check.SetEnabled(active);
            Show(call, options != null && options.CanCall); call.SetEnabled(active);
            if (options != null && options.CanCall) call.text = KoreanPokerText.CallLabel(options.CallAmount);
            bool canAggress = options != null && (options.CanBet || options.CanRaise);
            Show(amountRow, canAggress); Show(aggressive, canAggress); aggressive.SetEnabled(active);
            target.SetEnabled(active && canAggress);
            minimum.SetEnabled(active && canAggress); halfPot.SetEnabled(active && canAggress); maximum.SetEnabled(active && canAggress);
            if (changed && canAggress) target.SetValueWithoutNotify(options.MinimumAggressiveTarget.Value.ToString(CultureInfo.InvariantCulture));
            UpdateAmountHint();
            Show(exchange, view.CanExchange); exchange.SetEnabled(active);
            exchange.text = KoreanPokerText.ExchangeLabel(input.SelectedCount);
            Show(restart, view.Phase == HandPhase.Complete); restart.SetEnabled(active);
            Show(continueHand, nextHand != null && KoreanTableText.CanContinue(view)); continueHand.SetEnabled(active);
            Show(retry, input.IsPending);
            retry.SetEnabled(input.IsPending && progressFailure == PracticeProgressFailure.None);
            if (changed) message.text = "";
            ApplyProgressFailure();
            // A partial failed render must not consume one-time updates (for example the next bet's minimum).
            renderedVersion = view.Version;
        }

        /// <summary>UI-only stop state; keeps the last known view and never resets or changes the table.</summary>
        public void PauseProgress(PracticeProgressFailure failure)
        {
            if (disposed) return;
            if (failure != PracticeProgressFailure.OpponentAction && failure != PracticeProgressFailure.Display)
                throw new ArgumentOutOfRangeException(nameof(failure));
            localDisplayRecovery = false;
            progressFailure = failure;
            // Do not rerun a possibly failing full Render just to display the stop state.
            ApplyProgressFailure();
        }

        /// <summary>Called inside the runner's display step, so a rendering failure remains a display failure.</summary>
        public void ResumeProgress()
        {
            if (disposed) return;
            progressFailure = PracticeProgressFailure.None; message.text = ""; Render();
            localDisplayRecovery = false;
        }

        private void ApplyProgressFailure()
        {
            bool stopped = progressFailure != PracticeProgressFailure.None;
            Show(progressRetry, stopped);
            progressRetry.SetEnabled(stopped && (localDisplayRecovery || retryProgress != null) && !recovering);
            if (!stopped) return;
            instruction.text = "진행을 잠시 멈췄어요.";
            bool action = progressFailure == PracticeProgressFailure.OpponentAction;
            progressRetry.text = action ? "상대 진행 다시 시도" : "화면 다시 확인";
            message.text = action ? "상대 행동을 처리하지 못했어요. 현재 판을 유지한 채 다시 시도할 수 있어요."
                : "화면을 갱신하지 못했어요. 행동을 다시 실행하지 않고 현재 상태만 확인해요.";
            foreach (var button in new[] { fold, check, call, aggressive, exchange, restart, continueHand, retry })
            { Show(button, false); button.SetEnabled(false); }
            Show(amountRow, false); target.SetEnabled(false);
            minimum.SetEnabled(false); halfPot.SetEnabled(false); maximum.SetEnabled(false);
        }
        private void Build()
        {
            var header = Box("header", root);
            var heading = Box("heading", header);
            heading.Add(Text(KoreanTableText.Title, "title"));
            heading.Add(Text(KoreanTableText.Subtitle, "subtitle"));
            var headingRight = Box("heading-right", header);
            phase = Text("", "phase"); headingRight.Add(phase);
            headingRight.Add(Click("도움말", "help-button", () => { Show(resultOverlay, false); Show(help, help.style.display.value == DisplayStyle.None); }));
            var table = Box("table", root);
            var other = Box("opponent", table);
            otherStack = Text("", "seat-title"); other.Add(otherStack);
            otherStatus = Text("", "seat-status"); other.Add(otherStatus);
            otherCards = Box("backs", other);
            otherHandName = Text("", "hand-name"); other.Add(otherHandName);
            var center = Box("center-pot", table);
            pot = Text("", "pot-value"); center.Add(pot);
            showdown = Text("", "showdown-result"); center.Add(showdown);
            showdown.style.fontSize = 19; showdown.style.unityFontStyleAndWeight = FontStyle.Bold;
            showdown.style.marginTop = 0; showdown.style.marginBottom = 0;
            result = Text("", "result"); center.Add(result);
            result.style.marginTop = 0; result.style.marginBottom = 0;
            lastAction = Text("", "last-action"); center.Add(lastAction);
            resultDetails = Click("정산 내역", "result-details", () =>
            {
                if (input.View.Result == null) return;
                Show(help, false); Show(resultOverlay, true);
            });
            center.Add(resultDetails);
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
            minimum = Click("최소", "minimum", () => SetTarget(false)); amountRow.Add(minimum);
            halfPot = Click("절반 팟", "half-pot", SetHalfPot); amountRow.Add(halfPot);
            halfPot.tooltip = "콜 비용이 있으면 먼저 포함하고, 그 뒤 팟의 절반을 더 거는 총액이에요. 가능한 최소·최대 금액에 맞춰 조정해요. 자동으로 베팅하지 않아요.";
            maximum = Click("전액", "maximum", () => SetTarget(true)); amountRow.Add(maximum);
            amountHint = Text("", "amount-hint"); amountRow.Add(amountHint);
            actionRow = Box("actions", controls);
            fold = Click("폴드", "fold", () => Run(() => input.Bet(BettingAction.Fold()))); fold.AddToClassList("quiet"); fold.tooltip = KoreanPokerText.FoldHelp;
            check = Click("체크", "check", () => Run(() => input.Bet(BettingAction.Check()))); check.tooltip = KoreanPokerText.CheckHelp;
            call = Click("콜", "call", () => Run(() => input.Bet(BettingAction.Call())));
            aggressive = Click("베팅", "aggressive", Aggress);
            exchange = Click("", "exchange", () => Run(input.Exchange));
            restart = Click(KoreanTableText.NewPractice(startingStack), "new-practice", RestartPractice);
            continueHand = Click("다음 판 · 칩 유지", "next-hand", () => StartCompletedHand(nextHand));
            continueHand.style.backgroundColor = (Color)new Color32(210, 180, 121, 255);
            continueHand.style.color = (Color)new Color32(20, 47, 52, 255);
            restart.style.backgroundColor = (Color)new Color32(38, 49, 56, 255);
            restart.style.color = (Color)new Color32(189, 197, 190, 255);
            restart.tooltip = "양쪽 보유 칩을 초기화하고 처음부터 시작해요. 현재 칩을 유지하려면 다음 판을 누르세요.";
            retry = Click(KoreanTableText.Retry, "retry", () => Run(input.RetryPending));
            progressRetry = Click("진행 다시 확인", "progress-retry", RetryProgress);
            foreach (Button button in new[] { fold, check, call, aggressive, exchange, continueHand, restart, retry, progressRetry }) actionRow.Add(button);
            message = Text("", "message"); controls.Add(message);
            help = Box("help-overlay", root);
            help.Add(Text("플레이 방법", "help-title")); help.Add(Text(KoreanTableText.Rules, "help-text"));
            help.Add(Click("알겠어요", "close-help", () => Show(help, false))); Show(help, false);
            resultOverlay = Box("result-overlay", root); resultOverlay.AddToClassList("help-overlay");
            resultOverlay.Add(Text("이번 판 정산", "help-title"));
            breakdown = Text("", "help-text"); resultOverlay.Add(breakdown);
            resultOverlay.Add(Click("닫기", "close-result", () => Show(resultOverlay, false))); Show(resultOverlay, false);
        }
        private void RenderOtherHand(PokerPlayerView view)
        {
            RevealedHandView revealed = null;
            if (view.Result != null)
                foreach (var hand in view.Result.RevealedHands)
                    if (hand.Seat != view.ViewerSeat) { revealed = hand; break; }
            otherCards.Clear();
            otherHandName.text = revealed == null ? "" : "상대 패 · " + KoreanPokerText.HandName(revealed.Value);
            Show(otherHandName, revealed != null);
            for (int i = 0; i < SeatHand.CardCount; i++)
            {
                if (revealed == null)
                {
                    var back = Box("card-back", otherCards); back.Add(Text("?", "back-mark"));
                    continue;
                }
                Card card = revealed.Cards[i];
                var face = Box("card", otherCards); face.AddToClassList("revealed-card");
                face.name = "revealed-card-" + card.Id; face.tooltip = KoreanPokerText.CardName(card);
                face.style.width = 62; face.style.minWidth = 62; face.style.height = 64;
                face.style.marginLeft = 4; face.style.marginRight = 4; face.style.paddingTop = 3; face.style.paddingBottom = 3;
                face.EnableInClassList("red-card", card.Suit == Suit.Hearts || card.Suit == Suit.Diamonds);
                var rank = Text(KoreanTableText.RankLabel(card), "rank"); rank.style.fontSize = 25;
                rank.style.height = 30; rank.style.flexShrink = 0; rank.style.marginTop = 0; rank.style.marginBottom = 0; face.Add(rank);
                var suit = Text(KoreanTableText.SuitLabel(card), "suit"); suit.style.fontSize = 12;
                suit.style.height = 18; suit.style.flexShrink = 0; suit.style.marginTop = 0; suit.style.marginBottom = 0; face.Add(suit);
            }
        }
        private void Run(Func<bool> action)
        {
            if (disposed || progressFailure != PracticeProgressFailure.None) return;
            bool accepted = false; Exception inputError = null;
            try { accepted = action(); }
            catch (Exception error)
            {
                inputError = error;
                // Only uncertain input failures preserve a pending command. Rendering does not resend it.
                Debug.LogError("Poker input failed (" + error.GetType().Name + "); "
                    + (input.IsPending ? "pending request retained, no automatic reset." : "no pending request, no automatic reset."));
            }
            RenderInputFeedback(true, accepted, inputError);
        }
        private void RenderInputFeedback(bool showReceipt, bool accepted, Exception inputError)
        {
            if (disposed) return;
            try
            {
                Render();
                if (inputError != null) message.text = input.IsPending
                    ? "처리 결과를 확인하지 못했어요. 같은 요청을 다시 확인해 주세요."
                    : "입력을 처리하지 못했어요. 현재 상태를 확인해 주세요.";
                else if (showReceipt && !accepted && input.LastReceipt != null && !input.LastReceipt.Accepted)
                    message.text = KoreanPokerText.CommandErrorMessage(input.LastReceipt.Error);
            }
            catch (Exception error)
            {
                // A human action may already be committed, or its exact request may still be pending.
                // Stop the UI without calling the broken Render again; recovery only reads and renders.
                localDisplayRecovery = true; progressFailure = PracticeProgressFailure.Display;
                ApplyProgressFailure();
                Debug.LogError("Poker input display failed (" + error.GetType().Name + "); action was not replayed.");
            }
        }
        private void RestartPractice() => StartCompletedHand(newPractice);
        private void StartCompletedHand(Action start)
        {
            if (start == null || disposed || progressFailure != PracticeProgressFailure.None || input.IsPending || input.View.Phase != HandPhase.Complete) return;
            if (start == nextHand && !KoreanTableText.CanContinue(input.View)) return;
            try { start(); }
            catch (Exception error)
            {
                // Only report the failure; do not invent a successful next hand or automatically reset.
                if (!disposed) message.text = (start == nextHand ? "다음 판을 시작하지 못했어요." : "처음부터 시작하지 못했어요.")
                    + " 현재 정산 결과를 유지하고 있어요.";
                Debug.LogError("Poker practice start failed (" + error.GetType().Name + "); no automatic retry was performed.");
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
            if (disposed || progressFailure != PracticeProgressFailure.None) return;
            if (!ReadTarget(out long amount)) { message.text = "표시된 범위 안의 정수 금액을 입력해 주세요."; return; }
            Run(() => input.Bet(input.View.Betting.CanBet ? BettingAction.BetTo(amount) : BettingAction.RaiseTo(amount)));
        }
        private void SetTarget(bool maximum)
        {
            if (disposed || progressFailure != PracticeProgressFailure.None || input.IsPending) return;
            var options = input.View.Betting;
            if (options == null || !options.MinimumAggressiveTarget.HasValue) return;
            target.value = (maximum ? options.MaximumAggressiveTarget.Value : options.MinimumAggressiveTarget.Value).ToString(CultureInfo.InvariantCulture);
        }
        private void SetHalfPot()
        {
            if (disposed || progressFailure != PracticeProgressFailure.None || input.IsPending) return;
            long? amount = PokerBetSizing.HalfPotTarget(input.View);
            if (amount.HasValue) target.value = amount.Value.ToString(CultureInfo.InvariantCulture);
        }
        private void UpdateAmountHint()
        {
            if (disposed || amountHint == null) return;
            var options = input.View.Betting;
            if (options == null || !options.MinimumAggressiveTarget.HasValue) return;
            string range = options.MinimumAggressiveTarget.Value + " ~ " + options.MaximumAggressiveTarget.Value;
            target.tooltip = "가능한 베팅 총액: " + range;
            bool valid = ReadTarget(out long amount);
            aggressive.SetEnabled(!input.IsPending && progressFailure == PracticeProgressFailure.None && valid);
            if (!valid)
            {
                aggressive.text = options.CanBet ? "베팅" : "레이즈";
                amountHint.text = "가능한 총액  " + range; return;
            }
            long paid = 0;
            foreach (var seat in input.View.Seats) if (seat.Seat == input.View.ViewerSeat) paid = seat.StreetContribution ?? 0;
            amountHint.text = KoreanPokerText.AdditionalChips(checked(amount - paid));
            aggressive.text = options.CanBet ? KoreanPokerText.BetLabel(amount) : KoreanPokerText.RaiseLabel(amount);
        }
        private static Label Text(string text, string style) { var label = new Label(text); label.AddToClassList(style); return label; }
        private void RetryProgress()
        {
            if (disposed || recovering || progressFailure == PracticeProgressFailure.None
                || (!localDisplayRecovery && retryProgress == null)) return;
            recovering = true; progressRetry.SetEnabled(false);
            bool displayOnly = localDisplayRecovery;
            try
            {
                if (displayOnly) { input.Refresh(); ResumeProgress(); }
                else retryProgress();
            }
            catch (Exception error)
            {
                // A callback failure never clears the stop or starts another hand.
                if (!disposed) progressFailure = PracticeProgressFailure.Display;
                Debug.LogError(displayOnly ? "Poker display recovery failed (" + error.GetType().Name + "); no action was replayed."
                    : "Poker progress recovery callback failed (" + error.GetType().Name + "); no automatic retry.");
            }
            finally { recovering = false; if (!disposed) ApplyProgressFailure(); }
        }
        private static VisualElement Box(string style, VisualElement parent) { var box = new VisualElement(); box.AddToClassList(style); parent.Add(box); return box; }
        private Button Click(string text, string name, Action clicked) { return new Button(() => { if (!disposed) clicked(); }) { text = text, name = name }; }
        private static void Show(VisualElement element, bool shown) { element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None; }
        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (disposed) return;
            root.EnableInClassList("compact", evt.newRect.width < 1050 || evt.newRect.height < 760);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; root.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            root.Clear(); root.styleSheets.Clear();
        }
    }
}
