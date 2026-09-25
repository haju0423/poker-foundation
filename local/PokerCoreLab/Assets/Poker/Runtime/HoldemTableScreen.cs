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
    public sealed partial class HoldemTableScreen : IDisposable
    {
        public const string HelpText = "내 카드 2장과 공용 카드 5장 중 가장 좋은 5장으로 승부해요. 내 카드를 꼭 쓸 필요는 없어요.\n\n프리플랍: 내 카드 2장을 받고 베팅\n플랍: 공용 카드 3장 공개 후 베팅\n턴·리버: 공용 카드 1장씩 공개 후 베팅\n쇼다운: 남은 참가자의 패를 공개하고 승부\n\n나만 남으면 패를 공개하지 않고 팟을 받아요. 다음 판은 현재 보유 칩으로 시작하며, 딜러 표시는 다음 참가자에게 이동해요.";
        public const string ActionHelpText = "폴드: 이번 판 포기. 이미 낸 칩은 돌려받지 못해요.\n체크: 추가로 칩을 내지 않고 차례를 넘겨요.\n콜: 상대 베팅을 따라가요. 칩이 부족하면 남은 칩만 내고 올인해요.\n베팅: 이번 단계에서 처음으로 칩을 걸어요.\n레이즈: 상대보다 높은 금액을 걸어요.\n\n입력하는 금액은 이번 단계에서 낼 총액이에요. ‘빠른 입력’은 금액만 채우며, 베팅·레이즈 버튼을 눌러야 칩을 내요.\n\n올인: 남은 칩을 모두 걸어요. 이후 베팅은 못 하지만, 내가 낸 칩에 해당하는 팟의 승부에는 남아요.";
        public const string TermsHelpText = "딜러: 차례를 정하는 기준 자리예요.\nSB·BB: 판을 시작할 때 먼저 내는 작은·큰 블라인드예요.\n팟: 참가자들이 낸 칩을 모은 금액이에요.\n사이드 팟: 올인 금액을 넘겨 낸 참가자끼리 따로 겨루는 팟이에요.\n받은 칩: 팟에서 받은 금액이며, 순이익은 아니에요.\n\n족보 순서 (강한 순)\n스트레이트 플러시 > 포카드 > 풀하우스 > 플러시 > 스트레이트 > 트리플 > 투페어 > 원페어 > 하이 카드\n\nA는 보통 가장 높지만 A·2·3·4·5에서는 가장 낮아요. 무늬로 승패를 가르지 않아요.";
        private readonly VisualElement root;
        private readonly IHoldemPlayerPort port;
        private readonly IHoldemRemoteTablePort remote;
        private readonly IHoldemConnectionPresence connectionPresence;
        private readonly IHoldemRoomInfoSource roomInfo;
        private readonly IHoldemHistoryPort historyPort;
        private readonly Action restart;
        private readonly Action abandonSession;
        private readonly Action returnToMenu;
        private readonly Func<HoldemRevealCommand, HoldemReceipt> resumeReveal;
        private readonly Func<HoldemDealCommand, HoldemReceipt> dealUnchanged;
        private readonly IHoldemAccusationPlayerPort accusationPort;
        private readonly IHoldemUtterancePlayerPort utterancePort;
        private readonly IHoldemPublicUtteranceSource publicUtteranceSource;
        private HoldemPublicUtterances publicRemarks;
        private HoldemUtteranceComposer utteranceComposer;
        private readonly HoldemTableOptions options;
        private readonly Action<HoldemTableOptions> configureTable;
        private readonly List<float> speedChoices = new List<float> { 0.25f, 0.7f, 1.25f };
        private readonly List<SeatId> accusationTargets = new List<SeatId>();
        private Guid targetWindow;
        private readonly long startingStack;
        private readonly List<Label> stages = new List<Label>();
        private HoldemTableDisplay view;
        private HoldemSeatLabels seatLabels;
        private readonly List<SeatWidgets> seatWidgets = new List<SeatWidgets>();
        private readonly Dictionary<SeatId, BettingActionKind> streetActions = new Dictionary<SeatId, BettingActionKind>();
        private Label counter, pot, result, last, prompt, error, hint, amountCaption, helpCopy, potDetailsCopy;
        private VisualElement board, amountRow, actionRow, help, resetConfirmation;
        private Button fold, passive, aggressive, next, reset, retry, resolve, continueReveal, releaseDeal, historyButton;
        private VisualElement accusationRow;
        private DropdownField accusationTarget;
        private Button accuse, passAccusation;
        private TextField amount;
        private bool disposed, paused, terminal, helpOpen, resetOpen, optionsOpen, potDetailsOpen, historyOpen;
        private VisualElement historyDialog;
        private Label historyCopy, historyTitle;
        private ScrollView historyScroll;
        private VisualElement optionsDialog, potDetailsDialog;
        private Button potDetails;
        private readonly List<Button> helpTabs = new List<Button>();
        private int helpPage;
        private ScrollView helpScroll;
        private DropdownField seatChoice, speedChoice;
        private Toggle revealChoice;
        private Label optionsError;
        private double lockedUntil;
        private IVisualElementScheduledItem unlock;
        public bool IsProgressPaused => paused || ModalOpen || disposed;
        private bool menuOpen;
        private bool ModalOpen => helpOpen || resetOpen || optionsOpen || potDetailsOpen || historyOpen || menuOpen;
        private bool NetworkCanSend => remote == null || remote.CanSend;
        private bool HostControls => remote == null || remote.IsHost;
        private bool CanReleaseDeal => dealUnchanged != null || remote?.IsHost == true;
        private bool CanResumeReveal => resumeReveal != null || remote?.IsHost == true;
        public VisualElement Root => root;

        public HoldemTableScreen(VisualElement root, IHoldemPlayerPort port, Action restart, long startingStack, Font font,
            Action abandonSession = null, Func<HoldemRevealCommand, HoldemReceipt> resumeReveal = null,
            HoldemTableOptions options = null, Action<HoldemTableOptions> configureTable = null,
            Func<HoldemDealCommand, HoldemReceipt> dealUnchanged = null,
            IHoldemUtterancePlayerPort utterancePort = null, Action returnToMenu = null)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            historyPort = port as IHoldemHistoryPort;
            accusationPort = port as IHoldemAccusationPlayerPort;
            this.restart = restart ?? throw new ArgumentNullException(nameof(restart));
            this.abandonSession = abandonSession;
            this.returnToMenu = returnToMenu;
            this.resumeReveal = resumeReveal;
            this.dealUnchanged = dealUnchanged;
            this.utterancePort = utterancePort;
            this.options = options;
            this.configureTable = configureTable;
            if (configureTable != null && options == null) throw new ArgumentNullException(nameof(options));
            this.startingStack = startingStack;
            Initialize(font);
        }

        public HoldemTableScreen(VisualElement root, IHoldemRemoteTablePort remote, Font font)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
            this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
            connectionPresence = remote as IHoldemConnectionPresence;
            roomInfo = remote as IHoldemRoomInfoSource;
            historyPort = remote as IHoldemHistoryPort;
            utterancePort = (remote as IHoldemRemoteUtteranceSource)?.Utterances;
            publicUtteranceSource = remote as IHoldemPublicUtteranceSource;
            Initialize(font);
            remote.Changed += OnRemoteChanged;
        }

        private void Initialize(Font font)
        {
            StyleSheet sheet = Resources.Load<StyleSheet>("HoldemTable");
            if (sheet == null || font == null) throw new InvalidOperationException("Hold'em UI resources are missing.");
            root.Clear(); root.AddToClassList("omc-root");
            root.EnableInClassList("with-utterance", utterancePort != null);
            root.EnableInClassList("with-public-speech", roomInfo?.RoomRules?.PublishesUtterances == true);
            root.EnableInClassList("remote-table", remote != null);
            root.style.unityFont = font; if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            root.RegisterCallback<GeometryChangedEvent>(OnGeometry);
            Build(); Render();
        }

        private void Build()
        {
            var header = Box("omc-header", root);
            var title = Box("omc-heading", header);
            title.Add(Text("One More Card", "omc-title"));
            title.Add(Text(remote != null ? ReadDisplay().SeatCount + "인 멀티플레이"
                : dealUnchanged == null ? "텍사스 홀덤" : "개발용 진행 확인", "omc-subtitle"));
            var right = Box("omc-header-right", header);
            counter = Text("", "omc-counter"); right.Add(counter);
            if (historyPort != null) { historyButton = Click("이번 판 기록", "omc-history", OpenHistory); right.Add(historyButton); }
            if (configureTable != null) right.Add(Click("새 게임 설정", "omc-options", OpenOptions));
            if (returnToMenu != null) right.Add(Click("시작 메뉴", "omc-menu-return", OpenMenuConfirmation));
            right.Add(Click("도움말", "omc-help", () => {
                if (disposed || ModalOpen) return;
                SetHelpPage(0); helpOpen = true; Show(help, true);
            }));
            var steps = Box("omc-stages", root);
            foreach (string name in new[] { "프리플랍", "플랍", "턴", "리버" })
            { var label = Text(name, "omc-stage"); stages.Add(label); steps.Add(label); }
            var table = Box("omc-table", root);
            var initial = ReadDisplay();
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
            var potRow = Box("omc-pot-row", middle);
            pot = Text("", "omc-pot"); potRow.Add(pot);
            potDetails = Click("승부 내역", "omc-pot-details", () => {
                if (!CanInteract() || view.Result == null) return;
                potDetailsCopy.text = PotDetails(); potDetailsOpen = true; Show(potDetailsDialog, true);
            });
            potRow.Add(potDetails);
            board = Box("omc-board", middle); board.AddToClassList("omc-cards"); board.name = "omc-board";
            result = Text("", "omc-result"); middle.Add(result);
            last = Text("", "omc-last"); middle.Add(last);
            var ownWidgets = CreateSeat(table, initial.ViewerSeat, true, false);
            seatWidgets.Add(ownWidgets);
            if (utterancePort != null)
                utteranceComposer = new HoldemUtteranceComposer(ownWidgets.Box, utterancePort,
                    () => !disposed && !paused && !ModalOpen, roomInfo?.RoomRules?.PublishesUtterances == true);
            var controls = Box("omc-controls", root);
            prompt = Text("", "omc-prompt"); controls.Add(prompt);
            accusationRow = Box("omc-actions", controls); accusationRow.name = "omc-accusations";
            accusationTarget = new DropdownField { name = "omc-accusation-target" };
            accusationTarget.style.minWidth = 130;
            accusationRow.Add(accusationTarget);
            accuse = Click("고발", "omc-accuse", () => ChooseAccusation(true));
            passAccusation = Click("고발 안 함", "omc-pass-accusation", () => ChooseAccusation(false));
            accusationRow.Add(accuse); accusationRow.Add(passAccusation);
            amountRow = Box("omc-amounts", controls);
            amountCaption = Text("", "omc-caption"); amountCaption.name = "omc-amount-caption";
            amountRow.Add(amountCaption);
            amount = new TextField { name = "omc-target", maxLength = ChipInput.FieldCapacity };
            amount.AddToClassList("omc-target"); amount.RegisterValueChangedCallback(_ => UpdateAmount());
            amountRow.Add(amount);
            amountRow.Add(Text("빠른 입력", "omc-caption"));
            amountRow.Add(Click("최소 금액", "omc-min", () => SetTarget(0)));
            amountRow.Add(Click("팟 절반 기준", "omc-half", () => SetTarget(1)));
            amountRow.Add(Click("올인 금액", "omc-max", () => SetTarget(2)));
            hint = Text("", "omc-amount-hint"); hint.name = "omc-amount-hint"; amountRow.Add(hint);
            actionRow = Box("omc-actions", controls);
            fold = Click("폴드", "omc-fold", () => Bet(BettingAction.Fold())); fold.AddToClassList("omc-quiet");
            passive = Click("", "omc-passive", () => { if (view.LegalActions != null) Bet(view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call()); });
            passive.AddToClassList("omc-primary");
            aggressive = Click("", "omc-aggressive", Aggress);
            next = Click("다음 판", "omc-next", Next);
            next.AddToClassList("omc-primary");
            reset = Click("새 게임", "omc-reset", Restart); reset.AddToClassList("omc-quiet");
            retry = Click("화면 다시 확인", "omc-retry", () => {
                if (ModalOpen || terminal || disposed) return;
                if (remote != null) remote.Refresh();
                Recover();
            });
            resolve = Click(KoreanPokerText.SplitRemainderLabel, "omc-resolve", () =>
            {
                if (!CanInteract() || !view.IsSettlementPending || !HostControls) return;
                if (remote != null) RunRemote(() => remote.Resolve(view));
                else Run(() => port.ResolvePendingSettlement(view.SessionVersion));
            });
            continueReveal = Click("계속", "omc-continue-reveal", () =>
            {
                if (!CanInteract() || !view.IsRevealPending || !CanResumeReveal) return;
                if (remote != null) RunRemote(() => remote.Reveal(view));
                else Run(() => resumeReveal(new HoldemRevealCommand(view.SessionId, view.HandId,
                    Guid.NewGuid(), view.SessionVersion, view.Street)));
            });
            continueReveal.AddToClassList("omc-primary");
            releaseDeal = Click("조작 없이 공개", "omc-release-deal", () =>
            {
                if (!CanInteract() || !view.IsDealPending || view.PendingDeal == null || !CanReleaseDeal) return;
                if (remote != null) { RunRemote(() => remote.Deal(view)); return; }
                var pending = view.PendingDeal;
                var command = new HoldemDealCommand(view.SessionId, view.HandId, pending.WindowId,
                    Guid.NewGuid(), view.SessionVersion, pending.Street);
                Run(() => dealUnchanged(command));
            });
            releaseDeal.AddToClassList("omc-primary");
            foreach (var button in new[] { fold, passive, aggressive, next, reset, retry, resolve, continueReveal, releaseDeal }) actionRow.Add(button);
            error = Text("", "omc-error"); controls.Add(error);
            help = Box("omc-overlay", root);
            var helpCard = Box("omc-help-card", help);
            helpCard.Add(Text("플레이 방법", "omc-help-title"));
            var tabs = Box("omc-help-tabs", helpCard);
            string[] titles = roomInfo == null ? new[] { "진행 순서", "베팅 방법", "용어·족보" }
                : new[] { "진행 순서", "베팅 방법", "용어·족보", "방 정보" };
            for (int i = 0; i < titles.Length; i++)
            {
                int page = i;
                var tab = Click(titles[i], "omc-help-page-" + i, () => { if (helpOpen) SetHelpPage(page); });
                helpTabs.Add(tab); tabs.Add(tab);
            }
            helpScroll = new ScrollView(ScrollViewMode.Vertical); helpScroll.AddToClassList("omc-help-scroll"); helpCard.Add(helpScroll);
            helpCopy = Text(HelpText, "omc-help-copy"); helpCopy.name = "omc-help-copy"; helpScroll.Add(helpCopy);
            helpCard.Add(Click("닫기", "omc-close-help", () => CloseModal(ref helpOpen, help)));
            Show(help, false);
            potDetailsDialog = Box("omc-overlay", root); potDetailsDialog.name = "omc-pot-details-dialog";
            var detailsCard = Box("omc-help-card", potDetailsDialog);
            detailsCard.Add(Text("승부 내역", "omc-help-title"));
            var detailsScroll = new ScrollView(ScrollViewMode.Vertical) { name = "omc-pot-details-scroll" };
            detailsScroll.AddToClassList("omc-help-scroll"); detailsCard.Add(detailsScroll);
            potDetailsCopy = Text("", "omc-help-copy"); potDetailsCopy.name = "omc-pot-details-copy"; detailsScroll.Add(potDetailsCopy);
            detailsCard.Add(Text("받은 칩에는 내가 팟에 낸 칩도 포함돼요. 순이익과는 달라요.", "omc-help-copy"));
            detailsCard.Add(Click("닫기", "omc-close-pot-details", () => CloseModal(ref potDetailsOpen, potDetailsDialog)));
            Show(potDetailsDialog, false);
            if (historyPort != null) BuildHistory();
            resetConfirmation = Box("omc-overlay", root);
            var resetCard = Box("omc-help-card", resetConfirmation);
            resetCard.Add(Text("새 게임을 시작할까요?", "omc-help-title"));
            resetCard.Add(Text("현재 판을 끝내고 모두 " + KoreanPokerText.Chips(startingStack) + "으로 다시 시작해요. 진행 중이던 판으로 돌아올 수는 없어요.", "omc-help-copy"));
            resetCard.Add(Click("현재 판 유지", "omc-cancel-reset", () => CloseModal(ref resetOpen, resetConfirmation)));
            resetCard.Add(Click("초기화하고 새 게임", "omc-confirm-reset", () => {
                if (disposed || !resetOpen || abandonSession == null) return;
                resetOpen = false;
                Show(resetConfirmation, false);
                try { abandonSession(); }
                catch (Exception e) { PauseProgress(); Debug.LogError("Holdem recovery start failed (" + e.GetType().Name + ")."); }
            }));
            Show(resetConfirmation, false);
            if (configureTable != null) BuildOptions();
            if (returnToMenu != null) BuildMenuConfirmation();
        }

        private VisualElement menuConfirmation;
        private void BuildMenuConfirmation()
        {
            menuConfirmation = Box("omc-overlay", root); menuConfirmation.name = "omc-menu-confirmation";
            var card = Box("omc-help-card", menuConfirmation);
            card.Add(Text("시작 메뉴로 돌아갈까요?", "omc-help-title"));
            card.Add(Text("현재 판과 보유 칩은 저장되지 않아요. 다시 시작하면 새 게임이 시작돼요.", "omc-help-copy"));
            card.Add(Click("계속 플레이", "omc-menu-cancel", () => CloseModal(ref menuOpen, menuConfirmation)));
            card.Add(Click("메뉴로 돌아가기", "omc-menu-confirm", () => {
                if (disposed || !menuOpen) return;
                menuOpen = false;
                returnToMenu();
            }));
            Show(menuConfirmation, false);
        }

        private void OpenMenuConfirmation()
        {
            if (disposed || ModalOpen) return;
            menuOpen = true; Show(menuConfirmation, true);
        }

        private void CloseModal(ref bool open, VisualElement overlay)
        {
            if (disposed || !open) return;
            open = false; Show(overlay, false);
            // A state refresh under the overlay may have disabled table controls.
            // Closing is a display refresh, not recovery from an existing game error.
            try { Render(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem display paused (" + e.GetType().Name + ")."); }
        }

        private void SetHelpPage(int page)
        {
            helpPage = page;
            helpCopy.text = page == 0 ? HelpText : page == 1 ? ActionHelpText : page == 2 ? TermsHelpText
                : KoreanPokerText.RoomSettingsDescription(roomInfo?.RoomRules);
            helpScroll.scrollOffset = Vector2.zero;
            for (int i = 0; i < helpTabs.Count; i++) helpTabs[i].EnableInClassList("omc-primary", i == page);
        }

        private void BuildHistory()
        {
            historyDialog = Box("omc-overlay", root); historyDialog.name = "omc-history-dialog";
            var card = Box("omc-help-card", historyDialog);
            historyTitle = Text("이번 판 기록", "omc-help-title"); card.Add(historyTitle);
            historyScroll = new ScrollView(ScrollViewMode.Vertical) { name = "omc-history-scroll" };
            historyScroll.AddToClassList("omc-help-scroll"); card.Add(historyScroll);
            historyCopy = Text("", "omc-help-copy"); historyCopy.name = "omc-history-copy"; historyScroll.Add(historyCopy);
            card.Add(Click("닫기", "omc-close-history", () => CloseModal(ref historyOpen, historyDialog)));
            Show(historyDialog, false);
        }

        private void OpenHistory()
        {
            // Inspecting public history sends no command, including while waiting for a reconnection.
            if (disposed || paused || ModalOpen || historyPort == null) return;
            Render();
            if (!RefreshHistoryCopy()) return;
            historyScroll.scrollOffset = Vector2.zero;
            historyOpen = true; Show(historyDialog, true);
        }

        private bool RefreshHistoryCopy()
        {
            var history = historyPort.ReadHistory();
            if (history == null || history.SessionId != view.SessionId || history.HandId != view.HandId) return false;
            historyTitle.text = history.HandNumber.ToString(CultureInfo.InvariantCulture) + (history.OmittedCount > 0 ? "번째 판 최근 기록" : "번째 판 기록");
            var lines = new List<string>();
            bool orderedRemarks = publicRemarks != null && publicRemarks.HasHistoryOrder;
            int nextRemark = 0;
            if (orderedRemarks)
            {
                lines.Add("베팅과 공개 멘트 · 방장이 접수한 순서\n");
                if (publicRemarks.Count > 0 && publicRemarks.GetEntry(0).HistoryPosition.Value < history.OmittedCount)
                {
                    lines.Add("앞부분의 공개 멘트 · 해당 베팅 기록은 생략됐어요.");
                    while (nextRemark < publicRemarks.Count && publicRemarks.GetEntry(nextRemark).HistoryPosition.Value < history.OmittedCount)
                    {
                        var remark = publicRemarks.GetEntry(nextRemark++);
                        lines.Add(KoreanPokerText.StreetName(remark.Street) + " · " + SeatName(remark.Speaker) + " · 멘트");
                        lines.Add(remark.Text + "\n");
                    }
                }
            }
            else if (publicRemarks != null)
            {
                lines.Add("공개 멘트");
                if (publicRemarks.Count == 0) lines.Add("아직 보낸 멘트가 없어요.");
                for (int i = 0; i < publicRemarks.Count; i++)
                {
                    var remark = publicRemarks.GetEntry(i);
                    lines.Add(KoreanPokerText.StreetName(remark.Street) + " · " + SeatName(remark.Speaker));
                    lines.Add(remark.Text + "\n");
                }
                lines.Add("\n베팅 기록");
            }
            if (history.OmittedCount > 0) lines.Add("최근 " + history.Count + "개 기록 · 앞의 " + history.OmittedCount + "개는 생략됐어요.\n");
            int lastStreet = view.BoardCount == 5 ? 3 : view.BoardCount == 4 ? 2 : view.BoardCount == 3 ? 1 : 0;
            string[] names = { "프리플랍", "플랍", "턴", "리버" };
            int firstStreet = history.OmittedCount > 0 && history.Count > 0 ? (int)history.GetEntry(0).Street : 0;
            if (orderedRemarks && nextRemark < publicRemarks.Count)
                firstStreet = Math.Min(firstStreet, (int)publicRemarks.GetEntry(nextRemark).Street);
            for (int stage = firstStreet; stage <= lastStreet; stage++)
            {
                lines.Add(names[stage]);
                int count = 0;
                for (int i = 0; i < history.Count; i++)
                {
                    var entry = history.GetEntry(i);
                    if ((int)entry.Street != stage) continue;
                    if (orderedRemarks)
                        while (nextRemark < publicRemarks.Count && (int)publicRemarks.GetEntry(nextRemark).Street == stage
                            && publicRemarks.GetEntry(nextRemark).HistoryPosition.Value <= history.OmittedCount + i)
                            AddOrderedRemark();
                    string text = SeatName(entry.Seat) + " · ";
                    if (entry.Kind == HoldemHistoryKind.UncalledReturn)
                        text += "아무도 따라 내지 않은 " + KoreanPokerText.Chips(entry.Amount) + " 반환";
                    else if (entry.Kind == HoldemHistoryKind.SmallBlind || entry.Kind == HoldemHistoryKind.BigBlind)
                        text += (entry.Kind == HoldemHistoryKind.SmallBlind ? "SB " : "BB ") + KoreanPokerText.Chips(entry.Amount);
                    else
                    {
                        text += KoreanPokerText.ActionName(entry.Action.Value);
                        if (entry.Amount > 0)
                            text += " · " + KoreanPokerText.AdditionalChips(entry.Amount) + " / 총 " + KoreanPokerText.Chips(entry.StreetTotal);
                    }
                    lines.Add(text + (entry.IsAllIn ? " (올인)" : "")); count++;
                }
                if (orderedRemarks)
                    while (nextRemark < publicRemarks.Count && (int)publicRemarks.GetEntry(nextRemark).Street == stage)
                        AddOrderedRemark();
                if (count == 0) lines.Add("베팅 기록 없음");
                lines.Add("");
            }
            if (view.Result != null)
            {
                lines.Add(ResultText());
                for (int i = 0; i < view.SeatCount; i++)
                {
                    var seat = view.GetSeatAt(i);
                    if (seat.Awarded > 0) lines.Add(SeatName(seat.Seat) + " · 팟에서 " + KoreanPokerText.Chips(seat.Awarded) + " 받음");
                }
            }
            else if (view.IsSettlementPending) lines.Add("팟 정산 대기 중");
            lines.Add("\n총액은 해당 베팅 단계에서 낸 금액이에요.");
            if (remote != null) lines.Add("이 창은 다른 참가자의 진행을 멈추지 않아요.");
            historyCopy.text = string.Join("\n", lines);
            return true;

            void AddOrderedRemark()
            {
                var remark = publicRemarks.GetEntry(nextRemark++);
                lines.Add(SeatName(remark.Speaker) + " · 멘트");
                lines.Add(remark.Text);
            }
        }

        private void BuildOptions()
        {
            optionsDialog = Box("omc-overlay", root); optionsDialog.name = "omc-options-dialog";
            var card = Box("omc-help-card", optionsDialog);
            card.Add(Text("새 게임 설정", "omc-help-title"));
            seatChoice = new DropdownField("참가 인원", new List<string> { "2인 · 나 + 상대 1명", "3인 · 나 + 상대 2명", "4인 · 나 + 상대 3명" }, 0)
                { name = "omc-options-seats" };
            var labels = new List<string> { "빠르게", "보통", "천천히" };
            if (!speedChoices.Contains(options.OpponentDelaySeconds))
            {
                speedChoices.Add(options.OpponentDelaySeconds);
                labels.Add("현재 간격 (" + options.OpponentDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + "초)");
            }
            speedChoice = new DropdownField("상대 행동 속도", labels, 0) { name = "omc-options-speed" };
            revealChoice = new Toggle { text = "공용 카드 공개 후 잠시 멈추기", name = "omc-options-reveal" };
            revealChoice.AddToClassList("omc-reveal-option");
            foreach (var field in new VisualElement[] { seatChoice, speedChoice, revealChoice })
            { field.AddToClassList("omc-option-field"); card.Add(field); }
            var revealDescription = Text("켜면 플랍·턴·리버가 나올 때마다 멈춰요. 카드를 확인한 뒤 ‘계속’을 누르면 진행돼요.", "omc-option-description");
            revealDescription.name = "omc-options-reveal-description"; card.Add(revealDescription);
            card.Add(Text("적용하면 현재 판과 칩을 초기화하고 새 게임을 시작해요.", "omc-help-copy"));
            optionsError = Text("", "omc-error"); card.Add(optionsError);
            card.Add(Click("취소", "omc-cancel-options", () => CloseModal(ref optionsOpen, optionsDialog)));
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
            if (terminal) { ApplyPaused(); return; }
            HoldemTableDisplay previous = view;
            view = ReadDisplay();
            var previousRemarks = publicRemarks;
            publicRemarks = publicUtteranceSource?.ReadPublicUtterances();
            seatLabels = new HoldemSeatLabels(view);
            if (helpOpen && helpPage == 3) helpCopy.text = KoreanPokerText.RoomSettingsDescription(roomInfo?.RoomRules);
            bool changed = previous == null || previous.SessionVersion != view.SessionVersion || previous.SessionId != view.SessionId;
            if (historyButton != null) Show(historyButton, historyPort.ReadHistory() != null);
            if (historyOpen && previous != null && (previous.HandId != view.HandId || previous.SessionId != view.SessionId))
            { historyOpen = false; Show(historyDialog, false); }
            else if (historyOpen && (changed || previousRemarks != publicRemarks)) RefreshHistoryCopy();
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
            PrepareBestComparison(previous);
            ReadStreetActions();
            foreach (var widgets in seatWidgets) RenderSeat(widgets, view.GetSeat(widgets.Seat));
            pot.text = (complete ? "이번 판 팟  " : "팟  ") + KoreanPokerText.Chips(view.PotAmount);
            Show(potDetails, complete); potDetails.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            board.Clear();
            var own = view.GetSeat(view.ViewerSeat);
            for (int i = 0; i < 5; i++)
                board.Add(i < view.BoardCount ? Face(view.GetBoardCard(i), highlightedBest.Contains(view.GetBoardCard(i)))
                    : Empty(i == 1 ? "플랍" : i == 3 ? "턴" : i == 4 ? "리버" : ""));
            result.text = pending ? "동률 팟의 나머지 칩 때문에 정산을 기다리고 있어요." : ResultText();
            Show(result, complete || pending);
            last.text = pending ? KoreanPokerText.SplitRemainderHelp : NoticeText();
            HoldemLegalDisplay legal = view.LegalActions;
            bool active = legal != null && !paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil;
            bool canAggress = legal != null && (legal.CanBet || legal.CanRaise);
            root.EnableInClassList("without-amount", !canAggress || paused);
            Show(amountRow, canAggress && !paused);
            amountRow.SetEnabled(active);
            Show(fold, legal != null && !paused); fold.SetEnabled(active && legal.CanFold);
            Show(passive, legal != null && !paused); passive.SetEnabled(active && (legal.CanCheck || legal.CanCall));
            if (legal != null) passive.text = legal.CanCheck ? "체크" : KoreanPokerText.CallLabel(legal.CallAmount, legal.CallAmount == view.OwnStack);
            passive.EnableInClassList("long-amount", passive.text.Length > 30);
            passive.tooltip = passive.text;
            Show(aggressive, canAggress && !paused);
            if (changed && canAggress) amount.SetValueWithoutNotify(legal.MinimumAggressiveTarget.Value.ToString(CultureInfo.InvariantCulture));
            Show(next, complete && view.CanContinue && !paused && HostControls);
            next.text = own.Status == HoldemSeatStatus.Busted ? "다음 판 관전" : "다음 판";
            Show(resolve, pending && !paused && HostControls); resolve.SetEnabled(!paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(continueReveal, view.IsRevealPending && CanResumeReveal && !paused
                && (view.Accusations == null || view.Accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims));
            continueReveal.SetEnabled(!paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(releaseDeal, view.IsDealPending && CanReleaseDeal && !paused);
            releaseDeal.SetEnabled(!paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            next.SetEnabled(!paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(reset, remote == null && (complete || abandonSession != null) && !paused); reset.SetEnabled(!paused && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            Show(retry, paused || remote?.NeedsRefresh == true);
            retry.text = remote != null ? remote.RefreshLabel : "화면 다시 확인";
            prompt.text = pending ? KoreanPokerText.SplitRemainderPrompt
                : view.IsDealPending ? (!CanReleaseDeal ? KoreanPokerText.CommandErrorMessage(HoldemCommandError.DealPending)
                    : "공개 버튼을 누르면 다음 공용 카드가 나와요.")
                : view.IsRevealPending ? RevealPrompt()
                : complete ? (view.IsOver ? (view.OwnStack > 0 ? "모든 칩을 가져왔어요!" : "테이블 승부가 끝났어요.")
                    : view.OwnStack == 0 ? "칩을 모두 잃었어요. 다음 판은 관전할 수 있어요." : "현재 보유 칩으로 다음 판을 시작해요. 블라인드는 새로 내요.")
                : own.Status == HoldemSeatStatus.Busted ? "관전 중 · " + ActorText()
                : own.Status == HoldemSeatStatus.Folded ? "이번 판은 폴드했어요 · " + ActorText()
                : legal == null ? ActorText() : "내 차례예요.";
            if (changed) error.text = "";
            if (remote != null)
            {
                if (remote.StatusText.Length > 0) prompt.text = remote.StatusText;
                else if (view.IsDealPending && !remote.IsHost) prompt.text = "방장이 다음 공용 카드를 공개할 때까지 기다려 주세요.";
                else if (view.IsRevealPending && !remote.IsHost) prompt.text = "공용 카드를 확인해 주세요. 방장이 계속을 누르면 진행해요.";
                else if (pending && !remote.IsHost) prompt.text = "방장이 남은 칩을 정산하면 결과가 나와요.";
                else if (complete && view.CanContinue && !remote.IsHost) prompt.text = view.OwnStack == 0
                    ? "칩을 모두 잃었어요. 방장이 다음 판을 시작하면 관전해요."
                    : "방장이 다음 판을 시작하면 이어서 플레이해요.";
                error.text = remote.ErrorText;
            }
            RenderAccusations();
            UpdateAmount();
            utteranceComposer?.Render();
            if (paused) ApplyPaused();
        }

        private void UpdateAmount()
        {
            if (disposed || aggressive == null || view == null) return;
            var legal = view.LegalActions;
            bool valid = legal != null && (legal.CanBet || legal.CanRaise)
                && ChipInput.TryParseInteger(amount.value, out long parsed)
                && parsed >= legal.MinimumAggressiveTarget.Value && parsed <= legal.MaximumAggressiveTarget.Value;
            aggressive.SetEnabled(valid && !paused && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil);
            if (legal == null || (!legal.CanBet && !legal.CanRaise)) { hint.text = ""; return; }
            aggressive.text = legal.CanBet ? "베팅" : "레이즈";
            amountCaption.text = legal.CanBet ? "베팅 금액" : "레이즈 총액";
            amount.tooltip = "베팅·레이즈에만 쓰는 금액이에요. 콜·체크는 아래 버튼을 바로 누르면 돼요.";
            if (!ChipInput.TryParseInteger(amount.value, out long total))
                hint.text = "금액은 숫자로 입력해 주세요.";
            else if (total < legal.MinimumAggressiveTarget.Value)
                hint.text = "최소 총액 " + KoreanPokerText.Chips(legal.MinimumAggressiveTarget.Value);
            else if (total > legal.MaximumAggressiveTarget.Value)
                hint.text = "최대 총액 " + KoreanPokerText.Chips(legal.MaximumAggressiveTarget.Value);
            hint.EnableInClassList("invalid", !valid);
            if (valid)
            {
                long additional = total - view.OwnStreetContribution;
                bool allIn = additional == view.OwnStack;
                aggressive.text = legal.CanBet ? KoreanPokerText.BetLabel(total, allIn) : KoreanPokerText.RaiseLabel(total, allIn);
                hint.text = (legal.CanBet ? "베팅 시 " : "레이즈 시 ") + KoreanPokerText.AdditionalChips(additional);
            }
            aggressive.EnableInClassList("long-amount", aggressive.text.Length > 30);
            aggressive.tooltip = aggressive.text;
            hint.tooltip = hint.text;
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
            if (view.LegalActions == null || !ChipInput.TryParseInteger(amount.value, out long target) || target <= 0) return;
            Bet(view.LegalActions.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target));
        }
        private void Bet(BettingAction action)
        {
            if (!CanInteract() || view.LegalActions == null || !view.LegalActions.Allows(action)) return;
            if (remote != null) { RunRemote(() => remote.Act(view, action)); return; }
            Run(() => port.Submit(HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(), view.ViewerSeat, view.SessionVersion, action)));
        }
        private void Next()
        {
            if (!CanInteract() || view.Result == null || !view.CanContinue || !HostControls) return;
            if (remote != null) { RunRemote(() => remote.NextHand(view)); return; }
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
        private bool CanInteract() => !disposed && !paused && !ModalOpen && NetworkCanSend && Time.realtimeSinceStartupAsDouble >= lockedUntil;
        private void RunRemote(Action send)
        {
            try
            {
                lockedUntil = Time.realtimeSinceStartupAsDouble + 0.2;
                send(); Render();
                unlock?.Pause(); unlock = root.schedule.Execute(() => { if (!disposed && !paused) Render(); }).StartingIn(220);
            }
            catch (Exception e) { PauseProgress(); Debug.LogError("Remote input paused (" + e.GetType().Name + "); command not replaced."); }
        }
        private void OnRemoteChanged()
        {
            if (disposed) return;
            try { Render(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Remote display paused (" + e.GetType().Name + ")."); }
        }
        private void Run(Func<HoldemReceipt> action)
        {
            try
            {
                var receipt = action();
                if (receipt.Accepted) lockedUntil = Time.realtimeSinceStartupAsDouble + 0.2;
                Render();
                if (!receipt.Accepted) error.text = KoreanPokerText.CommandErrorMessage(receipt.Error);
                unlock?.Pause(); unlock = root.schedule.Execute(() => { if (!disposed && !paused) Recover(); }).StartingIn(220);
            }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem input paused (" + e.GetType().Name + "); no action replay."); }
        }
        public void PauseProgress()
        {
            if (disposed) return; paused = true; ApplyPaused();
        }
        public void StopProgress()
        {
            if (disposed) return;
            terminal = true; paused = true; unlock?.Pause(); ApplyPaused();
        }
        private void ApplyPaused()
        {
            utteranceComposer?.Render();
            Show(accusationRow, false);
            Show(amountRow, false);
            foreach (var button in new[] { fold, passive, aggressive, next, reset, resolve, continueReveal, releaseDeal }) { button.SetEnabled(false); Show(button, false); }
            if (abandonSession != null && !terminal) { Show(reset, true); reset.SetEnabled(true); }
            Show(retry, !terminal); retry.SetEnabled(!terminal);
            prompt.text = terminal ? "더 이상 진행할 수 없어요." : "진행을 잠시 멈췄어요.";
            error.text = terminal ? "위쪽의 나가기를 눌러 처음 화면으로 돌아가 주세요."
                : "화면 다시 확인을 눌러 현재 진행 상태를 불러와 주세요.";
        }
        private void Recover()
        {
            if (disposed || terminal) return;
            try { paused = false; Render(); }
            catch (Exception e) { PauseProgress(); Debug.LogError("Holdem display paused (" + e.GetType().Name + ")."); }
        }
        private SeatWidgets CreateSeat(VisualElement parent, SeatId seat, bool own, bool firstOpponent)
        {
            var box = Box(own ? "omc-player" : "omc-opponent", parent); box.AddToClassList("omc-seat");
            box.name = "omc-seat-" + seat.Value;
            bool publicSpeech = !own && roomInfo?.RoomRules?.PublishesUtterances == true;
            box.EnableInClassList("with-public-utterance", publicSpeech);
            var body = publicSpeech ? Box("omc-seat-body", box) : box;
            var info = Box("omc-seat-info", body);
            var heading = remote == null ? info : Box("omc-seat-heading", info);
            var title = Text("", "omc-seat-title"); title.AddToClassList("omc-seat-name"); heading.Add(title);
            Label position = null, connection = null;
            if (remote != null)
            {
                position = Text("", "omc-seat-position"); heading.Add(position);
                connection = Text("끊김", "omc-seat-connection"); heading.Add(connection);
                connection.tooltip = "연결 끊김 · 카드와 칩 상태는 그대로예요.";
            }
            var stack = Text("", "omc-stack"); info.Add(stack);
            var status = Text("", "omc-seat-status"); info.Add(status);
            var handArea = Box("omc-hand-area", body);
            var cards = Box("omc-cards", handArea);
            cards.name = own ? "omc-own-cards" : firstOpponent ? "omc-opponent-cards" : "omc-opponent-cards-" + seat.Value;
            var hand = Text("", "omc-hand-label"); handArea.Add(hand);
            var compare = Click("5장 보기", "omc-best-seat-" + seat.Value, () => CompareBest(seat));
            compare.AddToClassList("omc-compare"); handArea.Add(compare);
            Button remark = null;
            if (publicSpeech)
            {
                remark = Click("멘트 없음", "omc-public-utterance-" + seat.Value, OpenHistory);
                remark.enableRichText = false;
                remark.displayTooltipWhenElided = false; // Preserve the speaker/seat in our full-text tooltip.
                remark.AddToClassList("omc-public-utterance"); box.Add(remark);
            }
            return new SeatWidgets { Seat = seat, Box = box, Title = title, Position = position, Connection = connection, Stack = stack, Status = status,
                Cards = cards, Hand = hand, Compare = compare, Remark = remark };
        }
        private void RenderSeat(SeatWidgets widgets, HoldemSeatDisplay seat)
        {
            string badges = seat.IsButton ? "딜러" : "";
            if (seat.IsSmallBlind) badges += badges.Length == 0 ? "SB" : " / SB";
            if (seat.IsBigBlind) badges += badges.Length == 0 ? "BB" : " / BB";
            string fullTitle = SeatName(seat.Seat) + (badges.Length > 0 ? "  ·  " + badges : "");
            widgets.Title.text = widgets.Position == null ? fullTitle : SeatName(seat.Seat);
            widgets.Title.tooltip = fullTitle;
            if (widgets.Position != null)
            {
                widgets.Position.text = badges;
                widgets.Position.tooltip = fullTitle;
                Show(widgets.Position, badges.Length > 0);
            }
            widgets.Title.EnableInClassList("acting", seat.IsCurrentActor);
            widgets.Box.EnableInClassList("acting-seat", seat.IsCurrentActor);
            widgets.Box.EnableInClassList("inactive-seat", !HasPublicBest(seat)
                && (seat.Status == HoldemSeatStatus.Folded || seat.Status == HoldemSeatStatus.Busted));
            widgets.Stack.text = KoreanPokerText.Chips(seat.Stack);
            widgets.Stack.EnableInClassList("long-amount", widgets.Stack.text.Length > 10);
            widgets.Stack.tooltip = widgets.Stack.text;
            widgets.Status.text = view.Result != null ? (seat.Status == HoldemSeatStatus.Busted ? "탈락 · " : "")
                    + "받은 칩 " + KoreanPokerText.Chips(seat.Awarded)
                : seat.Status == HoldemSeatStatus.Busted ? "탈락"
                : seat.Status == HoldemSeatStatus.Folded ? "폴드"
                : seat.Status == HoldemSeatStatus.AllIn ? "올인"
                : streetActions.TryGetValue(seat.Seat, out var action)
                    ? KoreanPokerText.ActionName(action) + " · 총 " + KoreanPokerText.Chips(seat.StreetContribution)
                    : "이번 베팅 " + KoreanPokerText.Chips(seat.StreetContribution);
            widgets.Status.tooltip = widgets.Status.text;
            if (widgets.Remark != null)
            {
                HoldemPublicUtterance latest = null;
                if (publicRemarks != null)
                    for (int i = publicRemarks.Count - 1; i >= 0; i--)
                        if (publicRemarks.GetEntry(i).Speaker == seat.Seat) { latest = publicRemarks.GetEntry(i); break; }
                widgets.Remark.text = latest == null ? "멘트 없음" : KoreanPokerText.StreetName(latest.Street) + " · " + latest.Text;
                widgets.Remark.tooltip = latest == null ? "보낸 멘트가 없어요."
                    : SeatName(seat.Seat) + " · " + widgets.Remark.text + "\n누르면 이번 판 멘트를 모두 볼 수 있어요.";
            }
            bool disconnected = connectionPresence?.IsSeatDisconnected(seat.Seat) == true;
            if (widgets.Connection != null) Show(widgets.Connection, disconnected);
            widgets.Cards.Clear();
            if (seat.WasDealtIn)
                for (int i = 0; i < 2; i++) widgets.Cards.Add(seat.VisibleHoleCardCount > i
                    ? Face(seat.GetVisibleHoleCard(i), selectedBestSeat == seat.Seat && highlightedBest.Contains(seat.GetVisibleHoleCard(i))) : Back());
            else widgets.Cards.Add(Text("관전 중", "omc-hand-label"));
            widgets.Hand.text = seat.Status == HoldemSeatStatus.Folded ? "폴드" : seat.RevealedHandValue.HasValue
                ? KoreanPokerText.HandName(seat.RevealedHandValue.Value) : seat.IsViewer ? OwnHandLabel() : "";
            bool comparable = HasPublicBest(seat);
            Show(widgets.Hand, !comparable && widgets.Hand.text.Length > 0);
            Show(widgets.Compare, comparable);
            widgets.Compare.SetEnabled(comparable && CanInteract());
            if (comparable)
            {
                widgets.Compare.text = KoreanPokerText.HandSummary(seat.RevealedHandValue.Value);
                widgets.Compare.tooltip = SeatName(seat.Seat) + ": "
                    + KoreanPokerText.HandDescription(seat.RevealedHandValue.Value)
                    + "\n누르면 최종 5장과 비교 숫자를 보여줘요.";
                widgets.Compare.EnableInClassList("selected", selectedBestSeat == seat.Seat);
            }
        }
        private string OwnHandLabel()
        {
            if (view.OwnCardCount != 2) return "";
            if (view.BoardCount < 3) return "내 카드 2장";
            var cards = new Card[view.BoardCount];
            for (int i = 0; i < cards.Length; i++) cards[i] = view.GetBoardCard(i);
            return KoreanPokerText.HandName(HoldemBestHand.Evaluate(cards, new[] { view.GetOwnCard(0), view.GetOwnCard(1) }).Value);
        }
        private void ReadStreetActions()
        {
            streetActions.Clear();
            if (view.HasStreetActions)
            {
                for (int i = 0; i < view.StreetActionCount; i++)
                {
                    var action = view.GetStreetAction(i);
                    streetActions[action.Seat] = action.Kind;
                }
            }
            else if (historyPort != null)
            {
                var history = historyPort.ReadHistory();
                if (history == null || history.SessionId != view.SessionId || history.HandId != view.HandId) return;
                for (int i = 0; i < history.Count; i++)
                {
                    var entry = history.GetEntry(i);
                    if (entry.Street == view.Street && entry.Kind == HoldemHistoryKind.Action && entry.Action.HasValue)
                        streetActions[entry.Seat] = entry.Action.Value;
                }
            }
            else
            {
                var notice = view.LastAction;
                if (notice != null && notice.Street == view.Street) streetActions[notice.Seat] = notice.Kind;
            }
        }
        private string SeatName(SeatId seat) => seatLabels.Get(seat);
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
                    : !state.HasResponded ? "고발할 상대를 고르거나 ‘고발 안 함’을 눌러 주세요."
                    : state.OwnTarget.HasValue ? SeatName(state.OwnTarget.Value) + " 고발 선택 · 접수 중"
                    : "고발 안 함 선택 · 접수 중";
                return street + " 공개 · " + choice + " (" + state.ResponseCount + "/" + state.EligibleCount + ")";
            }
            return street + " 공개 · " + (CanResumeReveal ? "카드를 확인하고 계속을 눌러 주세요." : "진행 대기 중이에요.");
        }
        private string ResultText()
        {
            var r = view.Result;
            if (r == null) return "";
            string winner = r.WinnerSeat == null ? (r.PotCount > 1 ? "팟마다 승부를 나눠 칩을 지급했어요." : "무승부 · 팟 나눔") : SeatName(r.WinnerSeat.Value) + " 승리";
            if (r.Kind != HoldemResultKind.Showdown) return winner + " · 폴드로 종료";
            if (r.WinnerSeat == null) return winner;
            var seat = view.GetSeat(r.WinnerSeat.Value);
            return winner + (HasPublicBest(seat) ? " · " + KoreanPokerText.HandSummary(seat.RevealedHandValue.Value) : "");
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
            if (view.Result.PotCount > 1)
                lines.Add("\n사이드 팟은 그 금액까지 칩을 내고 남아 있는 참가자끼리만 겨뤄요.");
            if (view.Result.Kind == HoldemResultKind.Showdown)
            {
                lines.Add("\n공개된 패 비교");
                for (int i = 0; i < view.SeatCount; i++)
                {
                    var seat = view.GetSeatAt(i);
                    if (HasPublicBest(seat))
                        lines.Add(SeatName(seat.Seat) + ": " + KoreanPokerText.HandDescription(seat.RevealedHandValue.Value));
                }
                lines.Add("\n같은 족보끼리는 표시된 숫자를 앞에서부터 차례대로 비교해요. 모두 같으면 동률이에요.");
            }
            return string.Join("\n", lines);
        }
        private sealed class SeatWidgets
        {
            public SeatId Seat;
            public VisualElement Box, Cards;
            public Label Title, Position, Connection, Stack, Status, Hand;
            public Button Compare, Remark;
        }
        private string NoticeText()
        {
            if (view.Result != null)
                return view.Result.Kind == HoldemResultKind.Showdown ? BestComparisonNotice() : "쇼다운 없이 끝난 판은 상대 패를 공개하지 않아요.";
            var notice = view.LastAction;
            if (notice == null) return "공용 카드가 차례로 열려요.";
            return (notice.Street != view.Street ? KoreanPokerText.StreetName(notice.Street) + " · " : "")
                + SeatName(notice.Seat) + " · " + KoreanPokerText.ActionName(notice.Kind)
                + (notice.Paid > 0 ? " · " + KoreanPokerText.AdditionalChips(notice.Paid) : "");
        }
        private static VisualElement Face(Card card, bool best)
        {
            var box = new VisualElement(); box.AddToClassList("omc-card"); box.AddToClassList("face-card");
            box.EnableInClassList("red", card.Suit == Suit.Diamonds || card.Suit == Suit.Hearts);
            box.EnableInClassList("best", best);
            box.Add(Text(KoreanPokerText.RankLabel(card), "omc-rank"));
            box.Add(Text(SuitSymbol(card.Suit), "omc-suit")); return box;
        }
        private HoldemTableDisplay ReadDisplay() => remote != null ? remote.Read() : new HoldemTableDisplay(port.Read(), port.LastAction);
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
        { var label = new Label(text) { enableRichText = false }; label.AddToClassList(className); return label; }
        private static VisualElement Box(string className, VisualElement parent)
        { var box = new VisualElement(); box.AddToClassList(className); parent.Add(box); return box; }
        private static void Show(VisualElement element, bool show) => element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        private void OnGeometry(GeometryChangedEvent e) => root.EnableInClassList("compact", e.newRect.height < 730 || e.newRect.width < 1050);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; unlock?.Pause(); utteranceComposer?.Dispose();
            if (remote != null) remote.Changed -= OnRemoteChanged;
            root.UnregisterCallback<GeometryChangedEvent>(OnGeometry); root.Clear();
        }
    }
}
