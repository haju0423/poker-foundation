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
        private readonly Func<HoldemRevealCommand, HoldemReceipt> resumeReveal;
        private readonly IHoldemAccusationPlayerPort accusationPort;
        private readonly HoldemTableOptions options;
        private readonly Action<HoldemTableOptions> configureTable;
        private readonly List<float> speedChoices = new List<float> { 0.25f, 0.7f, 1.25f };
        private readonly List<SeatId> accusationTargets = new List<SeatId>();
        private Guid targetWindow;
        private readonly long startingStack;
        private readonly List<Label> stages = new List<Label>();
        private HoldemSnapshot view;
        private readonly List<SeatWidgets> seatWidgets = new List<SeatWidgets>();
        private Label counter, pot, result, last, prompt, error, hint;
        private VisualElement board, amountRow, actionRow, help, resetConfirmation;
        private Button fold, passive, aggressive, next, reset, retry, resolve, continueReveal;
        private VisualElement accusationRow;
        private DropdownField accusationTarget;
        private Button accuse, passAccusation;
        private TextField amount;
        private bool disposed, paused, helpOpen, resetOpen, optionsOpen;
        private VisualElement optionsDialog;
        private DropdownField seatChoice, speedChoice;
        private Toggle revealChoice;
        private Label optionsError;
        private double lockedUntil;
        private IVisualElementScheduledItem unlock;
        public bool IsProgressPaused => paused || ModalOpen || disposed;
        private bool ModalOpen => helpOpen || resetOpen || optionsOpen;
        public VisualElement Root => root;

        public HoldemTableScreen(VisualElement root, IHoldemPlayerPort port, Action restart, long startingStack, Font font,
            Action abandonSession = null, Func<HoldemRevealCommand, HoldemReceipt> resumeReveal = null,
            HoldemTableOptions options = null, Action<HoldemTableOptions> configureTable = null)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            accusationPort = port as IHoldemAccusationPlayerPort;
            this.restart = restart ?? throw new ArgumentNullException(nameof(restart));
            this.abandonSession = abandonSession;
            this.resumeReveal = resumeReveal;
            this.options = options;
            this.configureTable = configureTable;
            if (configureTable != null && options == null) throw new ArgumentNullException(nameof(options));
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
            if (configureTable != null) right.Add(Click("게임 설정", "omc-options", OpenOptions));
            right.Add(Click("도움말", "omc-help", () => {
                if (disposed || ModalOpen) return;
                helpOpen = true; Show(help, true);
            }));
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
            accusationRow = Box("omc-actions", controls); accusationRow.name = "omc-accusations";
            accusationTarget = new DropdownField { name = "omc-accusation-target" };
            accusationTarget.style.minWidth = 130;
            accusationRow.Add(accusationTarget);
            accuse = Click("고발", "omc-accuse", () => ChooseAccusation(true));
            passAccusation = Click("넘기기", "omc-pass-accusation", () => ChooseAccusation(false));
            accusationRow.Add(accuse); accusationRow.Add(passAccusation);
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
            resolve = Click(KoreanPokerText.SplitRemainderLabel, "omc-resolve", () =>
            {
                if (CanInteract() && view.IsSettlementPending) Run(() => port.ResolvePendingSettlement(view.SessionVersion));
            });
            resolve.tooltip = KoreanPokerText.SplitRemainderHelp;
            continueReveal = Click("계속", "omc-continue-reveal", () =>
            {
                if (CanInteract() && view.IsRevealPending && resumeReveal != null)
                    Run(() => resumeReveal(new HoldemRevealCommand(view.SessionId, view.HandId,
                        Guid.NewGuid(), view.SessionVersion, view.Street)));
            });
            continueReveal.AddToClassList("omc-primary");
            foreach (var button in new[] { fold, passive, aggressive, next, reset, retry, resolve, continueReveal }) actionRow.Add(button);
            error = Text("", "omc-error"); controls.Add(error);
            help = Box("omc-overlay", root);
            var helpCard = Box("omc-help-card", help);
            helpCard.Add(Text("플레이 방법", "omc-help-title"));
            helpCard.Add(Text(HelpText, "omc-help-copy"));
            helpCard.Add(Click("닫기", "omc-close-help", () => { helpOpen = false; Show(help, false); }));
            Show(help, false);
            resetConfirmation = Box("omc-overlay", root);
            var resetCard = Box("omc-help-card", resetConfirmation);
            resetCard.Add(Text("새 게임을 시작할까요?", "omc-help-title"));
            resetCard.Add(Text("현재 판과 보유 칩을 초기화해요. 진행 중이던 판으로 돌아올 수는 없어요.", "omc-help-copy"));
            resetCard.Add(Click("현재 판 유지", "omc-cancel-reset", () => {
                if (disposed || !resetOpen) return;
                resetOpen = false; Show(resetConfirmation, false);
            }));
            resetCard.Add(Click("초기화하고 새 게임", "omc-confirm-reset", () => {
                if (disposed || !resetOpen || abandonSession == null) return;
                resetOpen = false;
                Show(resetConfirmation, false);
                try { abandonSession(); }
                catch (Exception e) { PauseProgress(); Debug.LogError("Holdem recovery start failed (" + e.GetType().Name + ")."); }
            }));
            Show(resetConfirmation, false);
            if (configureTable != null) BuildOptions();
        }

        private void BuildOptions()
        {
            optionsDialog = Box("omc-overlay", root); optionsDialog.name = "omc-options-dialog";
            var card = Box("omc-help-card", optionsDialog);
            card.Add(Text("게임 설정", "omc-help-title"));
            seatChoice = new DropdownField("참가 인원", new List<string> { "2인 · 나 + 상대 1명", "3인 · 나 + 상대 2명", "4인 · 나 + 상대 3명" }, 0)
                { name = "omc-options-seats" };
            var labels = new List<string> { "빠르게", "보통", "천천히" };
            if (!speedChoices.Contains(options.OpponentDelaySeconds))
            {
                speedChoices.Add(options.OpponentDelaySeconds);
                labels.Add("현재 간격 (" + options.OpponentDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + "초)");
            }
            speedChoice = new DropdownField("상대 진행", labels, 0) { name = "omc-options-speed" };
            revealChoice = new Toggle("공용 카드 확인 후 진행") { name = "omc-options-reveal" };
            revealChoice.tooltip = "플랍·턴·리버가 공개되면 멈춰요. 계속을 누르면 다음 베팅으로 이어집니다.";
            foreach (var field in new VisualElement[] { seatChoice, speedChoice, revealChoice })
            { field.AddToClassList("omc-option-field"); card.Add(field); }
            card.Add(Text("적용하면 현재 판과 칩을 초기화하고 새 게임을 시작해요.", "omc-help-copy"));
            optionsError = Text("", "omc-error"); card.Add(optionsError);
            card.Add(Click("취소", "omc-cancel-options", () => {
                if (disposed || !optionsOpen) return;
                optionsOpen = false; Show(optionsDialog, false);
            }));
            card.Add(Click("적용하고 새 게임", "omc-apply-options", ApplyOptions));
            Show(optionsDialog, false);
        }

        private void OpenOptions()
        {
            if (disposed || ModalOpen || configureTable == null || Time.realtimeSinceStartupAsDouble < lockedUntil) return;
            seatChoice.index = options.SeatCount - 2;
            speedChoice.index = speedChoices.IndexOf(options.OpponentDelaySeconds);
            revealChoice.SetValueWithoutNotify(options.RevealPolicy == HoldemRevealPolicy.PauseAfterCommunityReveal);
            optionsError.text = "";
            optionsOpen = true; Show(optionsDialog, true);
        }

        private void ApplyOptions()
        {
            if (disposed || !optionsOpen || configureTable == null) return;
            if (seatChoice.index < 0 || seatChoice.index > 2 || speedChoice.index < 0 || speedChoice.index >= speedChoices.Count) return;
            var requested = new HoldemTableOptions(seatChoice.index + 2, speedChoices[speedChoice.index],
                revealChoice.value ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic);
            // Keep the old table and modal until replacement succeeds.
            try { configureTable(requested); optionsOpen = false; Show(optionsDialog, false); }
            catch (Exception e)
            {
                optionsError.text = "새 게임을 시작하지 못했어요. 현재 판은 그대로 유지돼요.";
                Debug.LogError("Holdem options were not applied (" + e.GetType().Name + ").");
            }
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
            last.text = pending ? KoreanPokerText.SplitRemainderHelp : NoticeText();
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
            Show(continueReveal, view.IsRevealPending && resumeReveal != null && !paused
                && (view.Accusations == null || view.Accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims));
            continueReveal.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            next.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(reset, (complete || abandonSession != null) && !paused); reset.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            reset.tooltip = "모든 참가자의 칩을 " + KoreanPokerText.Chips(startingStack) + "으로 초기화합니다.";
            Show(retry, paused);
            prompt.text = pending ? KoreanPokerText.SplitRemainderPrompt
                : view.IsRevealPending ? RevealPrompt()
                : complete ? (view.IsOver ? (view.OwnStack > 0 ? "모든 칩을 가져왔어요!" : "테이블 승부가 끝났어요.")
                    : view.OwnStack == 0 ? "칩을 모두 잃었어요. 다음 판은 관전할 수 있어요." : "다음 판에도 칩은 그대로 이어져요.")
                : own.Status == HoldemSeatStatus.Busted ? "관전 중 · " + ActorText()
                : own.Status == HoldemSeatStatus.Folded ? "이번 판은 폴드했어요 · " + ActorText()
                : legal == null ? ActorText() : "내 차례예요.";
            if (changed) error.text = "";
            RenderAccusations();
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
                hint.text = KoreanPokerText.AdditionalChips(total - view.OwnStreetContribution);
            }
        }

        private void RenderAccusations()
        {
            var state = view.Accusations;
            bool visible = state != null && state.CanRespond && accusationPort != null && !paused;
            Show(accusationRow, visible);
            bool enabled = visible && Time.realtimeSinceStartupAsDouble >= lockedUntil;
            accusationRow.SetEnabled(enabled);
            if (!visible) return;
            if (targetWindow != state.WindowId)
            {
                targetWindow = state.WindowId; accusationTargets.Clear();
                var names = new List<string>();
                for (int i = 0; i < state.TargetCount; i++)
                {
                    var target = state.GetTargetAt(i); accusationTargets.Add(target); names.Add(SeatName(target));
                }
                accusationTarget.choices = names;
                if (names.Count > 0) accusationTarget.index = 0;
            }
            accuse.SetEnabled(enabled && accusationTargets.Count > 0);
            accuse.tooltip = "접수가 끝나기 전에는 선택을 바꿀 수 있어요.";
            passAccusation.tooltip = "고발하지 않고 넘겨요. 포커에서 폴드하는 것은 아니에요.";
        }

        private void ChooseAccusation(bool claim)
        {
            if (!CanInteract() || accusationPort == null || view.Accusations == null || !view.Accusations.CanRespond) return;
            SeatId? target = null;
            if (claim)
            {
                int index = accusationTarget.index;
                if (index < 0 || index >= accusationTargets.Count) return;
                target = accusationTargets[index];
            }
            var command = new HoldemAccusationChoiceCommand(view.SessionId, view.HandId, view.Accusations.WindowId,
                Guid.NewGuid(), view.SessionVersion, view.Street, view.ViewerSeat, target);
            Run(() => accusationPort.SubmitAccusationChoice(command));
        }

        private void SetTarget(int mode)
        {
            if (!CanInteract() || view.LegalActions == null) return;
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
            if (disposed || ModalOpen) return;
            if (abandonSession != null && (paused || CanInteract()))
            { resetOpen = true; Show(resetConfirmation, true); return; }
            if (!CanInteract() || view.Result == null) return;
            try { restart(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem restart failed (" + e.GetType().Name + ")."); }
        }
        private bool CanInteract() => !disposed && !paused && !ModalOpen && Time.realtimeSinceStartupAsDouble >= lockedUntil;
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
            Show(accusationRow, false);
            Show(amountRow, false);
            foreach (var button in new[] { fold, passive, aggressive, next, reset, resolve, continueReveal }) { button.SetEnabled(false); Show(button, false); }
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
        private string RevealPrompt()
        {
            string street = view.Street == HoldemStreet.Flop ? "플랍" : view.Street == HoldemStreet.Turn ? "턴" : "리버";
            var state = view.Accusations;
            if (state != null && state.Phase == HoldemAccusationPhase.AwaitingVerdicts)
                return "고발 접수 완료 · 판정을 기다리고 있어요.";
            if (state != null && state.Phase == HoldemAccusationPhase.AwaitingConsequences)
                return "판정 완료 · 결과 처리를 기다리고 있어요.";
            if (state != null && state.Phase == HoldemAccusationPhase.Collecting)
            {
                string choice = !state.CanRespond ? "다른 참가자의 선택을 기다리고 있어요."
                    : !state.HasResponded ? "고발할 상대를 고르거나 넘겨 주세요."
                    : state.OwnTarget.HasValue ? SeatName(state.OwnTarget.Value) + " 고발 선택 · 접수 중"
                    : "넘기기 선택 · 접수 중";
                return street + " 공개 · " + choice + " (" + state.ResponseCount + "/" + state.EligibleCount + ")";
            }
            return street + " 공개 · " + (resumeReveal != null ? "카드를 확인하고 계속을 눌러 주세요." : "진행 대기 중이에요.");
        }
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
                lines.Add(KoreanPokerText.PotName(i) + " · " + string.Join(", ", recipients));
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
                + (notice.Paid > 0 ? " · " + KoreanPokerText.AdditionalChips(notice.Paid) : "");
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
