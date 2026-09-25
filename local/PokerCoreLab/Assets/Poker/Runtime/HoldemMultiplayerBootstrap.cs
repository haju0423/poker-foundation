using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using Poker.Presentation;
using Poker.Transport;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class HoldemMultiplayerBootstrap : MonoBehaviour
    {
        [SerializeField] private HoldemMultiplayerSettings settings;
        private HoldemMultiplayerConnection connection;
        private HoldemTableScreen screen;
        private HoldemMultiplayerDealerDebug dealerDebug;
        private bool accusationDebug;
        public bool EnableAccusationDebug { get; set; }
        private VisualElement root, lobby, form, tableSurface, connectionBar, confirmation;
        private TextField nickname, address, port, startingStack, smallBlind, bigBlind;
        private Foldout setup;
        private Toggle lan;
        private VisualElement lanPicker;
        private DropdownField localAddress, seatCapacity;
        private Button useLocalAddress, refreshAddresses;
        private Label lanHint;
        private VisualElement roomShare;
        private TextField shareAddress;
        private Label shareHint;
        private Button copyRoomAddress;
        private string copyFeedback = "";
        private Action<string> copyAddress = value => GUIUtility.systemCopyBuffer = value;
        private VisualElement rematch;
        private Label rematchCopy;
        private Button rematchReady, rematchStart, openRematch;
        private bool rematchOpen;
        private HoldemLanAddress[] localAddresses = Array.Empty<HoldemLanAddress>();
        private Button host, join, ready, start, reconnect, editConnection, leave, stay, confirmLeave, leaveRetry;
        private Label status, members, endpoint, confirmationCopy, roomRules, previewNotice, subtitle;
        private Font font;
        private bool confirming, failed, leavingLobby, leaveSent;
        private string leaveError = "";
        private string formError = "";
        private static int backgroundOwners;
        private static bool previousBackground;
        private bool ownsBackground;
        public HoldemMultiplayerSettings Settings
        {
            get => settings;
            set { settings = value; if (connection?.HasSession != true) ResetSetupFields(); }
        }
        public HoldemMultiplayerConnection Connection => connection;
        public bool HasFailed => failed;
        public Action ReturnToMenu { get; set; }
        private Button menuReturn;

        private void OnEnable()
        {
            accusationDebug = EnableAccusationDebug;
            var document = GetComponent<UIDocument>();
            font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            var sheet = Resources.Load<StyleSheet>("HoldemLobby");
            if (settings == null || document.panelSettings == null || font == null || sheet == null)
                throw new InvalidOperationException("Multiplayer scene resources are incomplete.");
            settings.CreateConfig();
            settings.CreateUtterancePolicy();
            if (!UnityEngine.Application.isBatchMode && Screen.fullScreenMode != FullScreenMode.Windowed)
                Screen.fullScreenMode = FullScreenMode.Windowed;
            root = document.rootVisualElement; root.Clear(); root.AddToClassList("omc-multiplayer");
            root.style.unityFont = font;
            if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            connection = new HoldemMultiplayerConnection(); failed = false;
            Build(); Render();
            AttachApplicationExitGuard();
            if (backgroundOwners++ == 0) previousBackground = UnityEngine.Application.runInBackground;
            UnityEngine.Application.runInBackground = true; ownsBackground = true;
            HoldemMultiplayerProcessCheck.AttachIfRequested(this);
#if !UNITY_EDITOR
            if (UnityEngine.Application.isBatchMode && !HoldemMultiplayerProcessCheck.IsRequested
                && Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-check-startup") >= 0)
            { Debug.Log("OMC_MULTIPLAYER_STARTUP_OK"); UnityEngine.Application.Quit(0); }
#endif
        }

        private void Build()
        {
            connectionBar = Box(root, "omc-room-bar");
            endpoint = Text(connectionBar, "", "omc-room-endpoint");
            reconnect = Button(connectionBar, "다시 연결", "omc-room-reconnect", RetryConnection);
            editConnection = Button(connectionBar, "연결 정보 수정", "omc-room-edit-connection", () => {
                if (!confirming && connection.CanEditFailedJoin) ReturnHome();
            });
            openRematch = Button(connectionBar, "한 경기 더", "omc-room-rematch", () => {
                if (!confirming && connection.Remote?.HasGame == true && connection.Remote.Read().IsOver)
                { rematchOpen = true; Render(); }
            });
            leave = Button(connectionBar, "나가기", "omc-room-leave", AskLeave);
            rematch = Box(root, "omc-rematch");
            var rematchCard = Box(rematch, "omc-rematch-card");
            rematchCopy = Text(rematchCard, "", "omc-rematch-copy");
            var rematchActions = Box(rematchCard, "omc-lobby-buttons");
            rematchReady = Button(rematchActions, "한 경기 더 · 준비", "omc-rematch-ready", () => {
                var remote = connection.Remote;
                if (!confirming && remote?.Lobby != null) remote.SetRematchReady(!remote.Lobby.IsRematchReady);
            });
            rematchStart = Button(rematchActions, "새 경기 시작", "omc-rematch-start",
                () => { if (!confirming) connection.Remote?.RestartMatch(); });
            Button(rematchCard, "결과 보기", "omc-rematch-close", () => { rematchOpen = false; Render(); });
            var lobbyScroll = new ScrollView(ScrollViewMode.Vertical) { name = "omc-lobby",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            lobbyScroll.AddToClassList("omc-lobby"); lobbyScroll.contentContainer.AddToClassList("omc-lobby-content");
            root.Add(lobbyScroll); lobby = lobbyScroll;
            Text(lobby, "One More Card", "omc-lobby-title");
            subtitle = Text(lobby, "4인 텍사스 홀덤", "omc-lobby-subtitle");
            if (settings.enableFlowPreview || accusationDebug)
            {
                previewNotice = Text(lobby, accusationDebug
                    ? "멀티 고발 테스트 · 고정 카드 / 방장 수동 처리 / 실제 AI·고발 정산 없음"
                    : "개발용 진행 확인 · 멘트 공개 범위는 방 설정을 따라요. AI나 카드에는 반영되지 않아요.", "omc-lobby-hint");
                previewNotice.name = "omc-flow-preview-notice";
            }
            form = Box(lobby, "omc-lobby-form");
            nickname = Field(form, "이름", "omc-room-name", "", 24);
            address = Field(form, "IP / 방 주소", "omc-room-address", "127.0.0.1", 64);
            port = Field(form, "포트", "omc-room-port", "7777", 5);
            lan = new Toggle("신뢰하는 같은 네트워크에서 연결") { name = "omc-room-lan" }; form.Add(lan);
            lanPicker = Box(form, "omc-lan-picker");
            localAddress = new DropdownField("내 IP") { name = "omc-room-local-address" }; lanPicker.Add(localAddress);
            var addressButtons = Box(lanPicker, "omc-lobby-buttons");
            useLocalAddress = Button(addressButtons, "내 IP 사용", "omc-room-use-address", () => {
                if (!connection.HasSession && lan.value && localAddress.index > 0 && localAddress.index <= localAddresses.Length)
                    address.value = localAddresses[localAddress.index - 1].Address;
            });
            refreshAddresses = Button(addressButtons, "주소 새로고침", "omc-room-refresh-addresses", RefreshAddresses);
            lanHint = Text(lanPicker, "", "omc-lobby-hint");
            localAddress.RegisterValueChangedCallback(_ => UpdateAddressChoice());
            lan.RegisterValueChangedCallback(change => { if (change.newValue) RefreshAddresses(); Render(); });
            Text(form, "방 주소(IP:포트)를 붙여넣거나 IP와 포트를 따로 입력해 주세요.\n다른 컴퓨터라면 같은 네트워크 연결을 선택해 주세요.", "omc-lobby-hint")
                .name = "omc-room-instructions";
            setup = new Foldout { name = "omc-room-setup", text = "방 설정 (방장용)", value = false };
            setup.AddToClassList("omc-room-setup"); form.Add(setup);
            seatCapacity = new DropdownField("인원", new List<string> { "3인", "4인" }, 1) { name = "omc-room-capacity" };
            setup.Add(seatCapacity);
            seatCapacity.RegisterValueChangedCallback(_ => Render());
            startingStack = Field(setup, "시작 칩", "omc-room-stack", "", ChipInput.FieldCapacity);
            smallBlind = Field(setup, "스몰 블라인드", "omc-room-small-blind", "", ChipInput.FieldCapacity);
            bigBlind = Field(setup, "빅 블라인드", "omc-room-big-blind", "", ChipInput.FieldCapacity);
            ResetSetupFields();
            Text(setup, "방을 만든 뒤에는 바꿀 수 없어요. 참가자는 방장 설정을 따라요.", "omc-lobby-hint");
            var buttons = Box(form, "omc-lobby-buttons");
            host = Button(buttons, "방 만들기", "omc-room-host", () => Begin(true));
            join = Button(buttons, "참가하기", "omc-room-join", () => Begin(false));
            if (ReturnToMenu != null)
                menuReturn = Button(form, "시작 메뉴", "omc-room-menu", () => {
                    if (!isActiveAndEnabled || connection == null || connection.HasSession || confirming || failed) return;
                    ReturnToMenu();
                });
            roomShare = Box(lobby, "omc-room-share");
            var shareRow = Box(roomShare, "omc-room-share-row");
            shareAddress = Field(shareRow, "방 주소", "omc-room-share-address", "", 21);
            shareAddress.AddToClassList("omc-room-share-address");
            shareAddress.isReadOnly = true;
            copyRoomAddress = Button(shareRow, "주소 복사", "omc-room-copy-address", CopyRoomAddress);
            shareHint = Text(roomShare, "", "omc-lobby-hint"); shareHint.name = "omc-room-share-hint";
            members = Text(lobby, "", "omc-room-members");
            var actions = Box(lobby, "omc-lobby-buttons");
            ready = Button(actions, "준비", "omc-room-ready", () => {
                var remote = connection.Remote;
                if (!confirming && remote?.Lobby != null) remote.SetReady(!remote.Lobby.IsReady);
            });
            start = Button(actions, "게임 시작", "omc-room-start", () => { if (!confirming) connection.Remote?.StartTable(); });
            status = Text(lobby, "이름을 입력하고 방을 만들거나 참가해 주세요.", "omc-room-status");
            Text(lobby, "모두 준비하면 방장이 시작할 수 있어요. 다음 판은 남은 칩으로 이어져요.", "omc-lobby-hint");
            roomRules = Text(lobby, "", "omc-lobby-hint"); roomRules.name = "omc-room-rules";
            tableSurface = Box(root, "omc-network-table");
            rematch.BringToFront();
            confirmation = Box(root, "omc-room-confirmation");
            var card = Box(confirmation, "omc-room-confirm-card");
            confirmationCopy = Text(card, "", "omc-room-confirm-copy");
            stay = Button(card, "계속 플레이", "omc-room-stay", CancelLeaveConfirmation);
            confirmLeave = Button(card, "나가기", "omc-room-confirm-leave", LeaveConfirmed);
            leaveRetry = Button(card, "응답 확인", "omc-room-leave-retry", () => {
                if (!leavingLobby) return;
                RetryConnection();
            });
        }
        private void Begin(bool hosting)
        {
            if (confirming || failed || connection.HasSession) return;
            formError = "";
            string selectedAddress = address.value ?? "";
            int selectedPort;
            if (selectedAddress.IndexOf(':') >= 0)
            {
                if (hosting)
                { formError = "방을 만들 때는 이 컴퓨터의 IP와 포트를 따로 입력해 주세요. 받은 방 주소는 참가할 때 사용해요."; Render(); return; }
                if (!HoldemRoomAddress.TryParse(selectedAddress, out selectedAddress, out selectedPort))
                { formError = "방 주소를 IP:포트 형식으로 확인해 주세요. 예: 192.168.1.20:7777"; Render(); return; }
            }
            else if (!int.TryParse(port.value, NumberStyles.None, CultureInfo.InvariantCulture, out selectedPort))
            { formError = "포트 번호를 숫자로 입력해 주세요."; Render(); return; }
            if (hosting)
            {
                if (!TrySetupConfig(out var config, out formError)) { Render(); return; }
                connection.Host(nickname.value, selectedAddress, selectedPort, lan.value, config,
                    deckRandom: accusationDebug ? new HoldemLocalDealerDebug.OrderedDeck() : null,
                    utterancePolicy: accusationDebug ? HoldemMultiplayerDealerDebug.CreateUtterancePolicy(settings) : settings.CreateUtterancePolicy(),
                    seatCapacity: SelectedCapacity,
                    accusationEvidenceScope: accusationDebug ? HoldemAccusationEvidenceScope.CurrentRevealOnly : (HoldemAccusationEvidenceScope?)null);
            }
            else connection.Join(nickname.value, selectedAddress, selectedPort, lan.value);
            if (connection.HasSession)
            {
                address.SetValueWithoutNotify(selectedAddress);
                port.SetValueWithoutNotify(connection.Port.ToString(CultureInfo.InvariantCulture));
            }
            Render();
        }
        private void CopyRoomAddress()
        {
            if (confirming || failed || connection.Remote?.Lobby == null || connection.Remote.HasGame
                || connection.Remote.HasUnrecoverableAdmissionError) return;
            try { copyAddress(connection.EndpointText); copyFeedback = "복사했어요."; }
            catch (Exception) { copyFeedback = "복사하지 못했어요. 위 주소를 선택해 직접 복사해 주세요."; }
            Render();
        }
        private void RetryConnection()
        {
            if (connection.Remote != null) connection.Remote.Refresh();
            else if (connection.CanReconnect) connection.Reconnect();
        }
        private void ResetSetupFields()
        {
            if (settings == null || startingStack == null) return;
            startingStack.SetValueWithoutNotify(settings.startingStack.ToString(CultureInfo.InvariantCulture));
            smallBlind.SetValueWithoutNotify(settings.smallBlind.ToString(CultureInfo.InvariantCulture));
            bigBlind.SetValueWithoutNotify(settings.bigBlind.ToString(CultureInfo.InvariantCulture));
        }
        private bool TrySetupConfig(out Poker.Foundation.HoldemConfig config, out string error)
        {
            config = null; error = "";
            if (SelectedCapacity < 3 || SelectedCapacity > 4) error = "참가 인원을 3인 또는 4인으로 선택해 주세요.";
            else if (!PositiveChips(startingStack.value, out var stack)) error = "시작 칩은 1 이상의 정수로 입력해 주세요.";
            else if (!PositiveChips(smallBlind.value, out var small)) error = "스몰 블라인드는 1 이상의 정수로 입력해 주세요.";
            else if (!PositiveChips(bigBlind.value, out var big)) error = "빅 블라인드는 1 이상의 정수로 입력해 주세요.";
            else if (small > big) error = "빅 블라인드는 스몰 블라인드 이상이어야 해요.";
            else if (stack > long.MaxValue / SelectedCapacity) error = "시작 칩이 너무 커요. 더 작은 금액을 입력해 주세요.";
            else config = accusationDebug ? new HoldemConfig(stack, small, big, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses, HoldemDealPolicy.WaitForHost)
                : settings.CreateConfig(stack, small, big, SelectedCapacity);
            return config != null;
        }
        private int SelectedCapacity => seatCapacity?.index == 0 ? 3 : seatCapacity?.index == 1 ? 4 : 0;
        private static bool PositiveChips(string value, out long amount)
            => ChipInput.TryParseInteger(value, out amount) && amount > 0;
        private void RefreshAddresses()
        {
            if (connection.HasSession || !lan.value) return;
            localAddresses = HoldemLanAddresses.ReadLocal();
            var labels = new List<string> { "방을 만들 때 사용할 내 주소 선택" };
            foreach (var candidate in localAddresses) labels.Add(candidate.Label);
            localAddress.choices = labels; localAddress.index = 0;
            lanHint.text = localAddresses.Length == 0 ? "사용 가능한 사설 IP를 찾지 못했어요. 네트워크 연결을 확인하거나 주소를 직접 입력해 주세요."
                : "방장만 내 IP를 선택해 위 주소 칸에 넣어 주세요. 참가자는 방장에게 받은 주소를 입력해요.\nVPN·공용 와이파이에서는 서로 연결되지 않을 수 있어요.";
            UpdateAddressChoice();
        }
        private void UpdateAddressChoice()
        {
            if (useLocalAddress == null) return;
            useLocalAddress.SetEnabled(!connection.HasSession && lan.value && localAddress.index > 0 && localAddress.index <= localAddresses.Length);
        }
        private void Update()
        {
            if (connection == null || failed) return;
            try
            {
                connection.Poll();
                if (leavingLobby && connection.Remote?.LobbyLeaveConfirmed == true) { ReturnHome(); return; }
                if (leavingLobby)
                {
                    var remote = connection.Remote;
                    if (remote?.HasUnrecoverableAdmissionError == true)
                    { leavingLobby = false; leaveError = remote.UnconfirmedLeaveMessage; }
                    else if (!leaveSent && remote?.HasGame == true)
                    { leavingLobby = false; leaveError = "이미 게임이 시작됐어요. 현재 판에서 나갈지 다시 확인해 주세요."; }
                    else if (!leaveSent && remote?.CanLeaveLobby == true) leaveSent = remote.RequestLobbyLeave();
                    else if (leaveSent && remote?.IsLobbyLeavePending == false)
                    { leavingLobby = false; leaveError = remote.ErrorText; }
                }
                if (screen == null && connection.Remote?.HasGame == true)
                {
                    bool debugHost = accusationDebug && connection.IsHosting;
                    screen = new HoldemTableScreen(tableSurface, connection.Remote, font, hostDealerControlled: debugHost);
                    if (debugHost) dealerDebug = new HoldemMultiplayerDealerDebug(connection, screen,
                        () => isActiveAndEnabled && !confirming && !rematchOpen && !failed);
                }
                dealerDebug?.Tick();
                Render();
            }
            catch (Exception e)
            {
                StopProgress(e);
            }
        }
        private void StopProgress(Exception error)
        {
            failed = true; leavingLobby = false; dealerDebug?.Dispose(); dealerDebug = null;
            connection.StopForError(); screen?.StopProgress();
            Render();
            Debug.LogError("Multiplayer progression paused (" + error.GetType().Name + ").");
        }
        private void Render()
        {
            bool session = connection.HasSession;
            var remote = connection.Remote; var room = remote?.Lobby;
            bool playing = screen != null;
            bool exitOnly = failed || remote?.HasUnrecoverableAdmissionError == true;
            bool canAbandonLeave = CanAbandonUnconfirmedLeave;
            bool matchEnded = !exitOnly && remote?.HasGame == true && remote.Read().IsOver;
            if (!matchEnded) rematchOpen = false;
            Show(openRematch, matchEnded && room?.HasRematch == true);
            openRematch.SetEnabled(!confirming && !rematchOpen);
            Show(rematch, rematchOpen);
            rematch.SetEnabled(!confirming && !failed);
            rematchReady.text = room?.IsRematchReady == true ? "다시 하기 취소" : "한 경기 더 · 준비";
            rematchReady.SetEnabled(remote?.CanSend == true && (room?.RematchSupported == true || room?.IsRematchReady == true));
            Show(rematchStart, room?.IsHost == true);
            rematchStart.SetEnabled(remote?.CanSend == true && room?.AllRematchReady == true);
            if (matchEnded && room?.HasRematch == true)
            {
                var names = new StringBuilder(); int readyCount = 0;
                for (int i = 0; i < room.MemberCount; i++)
                {
                    var m = room.GetMember(i);
                    if (m.RematchReady) readyCount++;
                    if (names.Length > 0) names.Append(" · ");
                    names.Append(m.Name).Append(!m.Connected ? " (연결 끊김)" : m.RematchReady ? " (준비)" : " (대기)");
                }
                rematchCopy.text = !room.RematchSupported ? "같은 방에서 다시 하려면 모두 새 버전으로 실행해 주세요."
                    : "한 경기 더? " + readyCount + "/" + room.SeatCapacity + "명 준비 · 전원 준비 후 방장이 시작해요.\n"
                        + "시작하면 모두 " + (room.Rules == null ? "방 설정의 시작 칩" : room.Rules.StartingStack + "칩")
                        + "으로 새 경기를 해요.\n" + names;
            }
            Show(lobby, !playing); Show(tableSurface, playing); Show(connectionBar, session || failed); Show(confirmation, confirming);
            Show(form, room == null);
            if (menuReturn != null) Show(menuReturn, !session && !failed && !confirming);
            bool canShare = room != null && !room.HasGame && !exitOnly;
            Show(roomShare, canShare);
            string sharedEndpoint = canShare ? connection.EndpointText : "";
            if (shareAddress.value != sharedEndpoint) shareAddress.SetValueWithoutNotify(sharedEndpoint);
            copyRoomAddress.SetEnabled(canShare && !confirming);
            shareHint.text = copyFeedback + (copyFeedback.Length > 0 ? "\n" : "")
                + (connection.EndpointText.StartsWith("127.", StringComparison.Ordinal)
                    ? "이 주소는 같은 컴퓨터에서만 쓸 수 있어요."
                    : "같은 네트워크의 친구에게 보내 주세요. 친구도 같은 네트워크 연결을 선택해야 해요.");
            if (previewNotice != null) Show(previewNotice, room == null);
            subtitle.text = (room?.SeatCapacity ?? SelectedCapacity) + "인 텍사스 홀덤";
            if (screen != null && room?.Rules?.AccusationsEnabled == true)
                screen.Root.Q<Label>(className: "omc-subtitle").text = "개발용 · 방장 수동 카드 처리 / 실제 AI·고발 정산 없음";
            roomRules.text = room == null
                ? TrySetupConfig(out var previewConfig, out _) ? SelectedCapacity + "인 방 · 처음 " + previewConfig.StartingStack
                    + "칩 · 블라인드 " + previewConfig.SmallBlind + "/" + previewConfig.BigBlind : "참가자는 방장 설정을 따라요."
                : room.Rules == null ? "이 방의 설정 정보는 제공되지 않아요. 시작 칩과 진행 방식은 방장에게 확인해 주세요."
                : room.SeatCapacity + "인 방 · 처음 " + room.Rules.StartingStack + "칩 · 블라인드 " + room.Rules.SmallBlind + "/" + room.Rules.BigBlind
                    + "\n공용 카드: " + (room.Rules.WaitsForHostDeal ? "방장이 공개" : "자동 공개")
                    + (room.Rules.PausesAfterReveal ? " · 확인 후 방장이 계속" : " · 별도 확인 없이 계속")
                    + "\n멘트: " + KoreanPokerText.UtteranceVisibilityDescription(room.Rules);
            if (room?.Rules?.AccusationsEnabled == true)
                roomRules.text += "\n개발용 고발 테스트 · 실제 AI·고발 정산 없음";
            Show(lanPicker, lan.value && !session);
            refreshAddresses.SetEnabled(!session); localAddress.SetEnabled(!session && localAddresses.Length > 0);
            UpdateAddressChoice();
            tableSurface.SetEnabled(!confirming && !rematchOpen && !failed); lobby.SetEnabled(!confirming && !failed);
            Show(stay, (!exitOnly || quittingApplication) && !leavingLobby);
            stay.SetEnabled((!exitOnly || quittingApplication) && !leavingLobby);
            stay.text = quittingApplication ? "종료 취소" : matchEnded ? "결과 더 보기" : "계속 플레이";
            Show(confirmLeave, !leavingLobby || canAbandonLeave);
            confirmLeave.SetEnabled(!leavingLobby || canAbandonLeave);
            confirmLeave.text = quittingApplication ? canAbandonLeave ? "연결 정보 지우고 종료" : "게임 종료"
                : canAbandonLeave ? "연결 정보 지우고 나가기"
                : exitOnly ? "처음 화면으로"
                : matchEnded ? connection.IsHosting ? "방 닫고 돌아가기" : "처음 화면으로" : "나가기";
            bool canRefresh = remote != null ? remote.NeedsRefresh : connection.CanReconnect;
            Show(leaveRetry, leavingLobby && canRefresh);
            leaveRetry.SetEnabled(!connection.IsConnecting);
            leaveRetry.text = remote?.RefreshLabel ?? "다시 연결";
            if (confirming)
                confirmationCopy.text = canAbandonLeave ? "퇴장이 처리됐는지 확인하지 못했어요.\n다시 연결하면 같은 요청을 확인할 수 있어요.\n연결 정보를 지우면 같은 자리로 돌아올 수 없고, 방에 자리가 남아 있을 수 있어요."
                    : leavingLobby ? "자리를 비운 뒤 처음 화면으로 돌아갈게요.\n"
                    + (connection.IsConnecting ? "다시 연결하고 있어요." : connection.Remote?.StatusText ?? "응답을 확인하고 있어요.")
                    : !string.IsNullOrEmpty(leaveError) ? leaveError
                    : failed ? "게임 연결이 종료됐어요. 나가기를 누르면 처음 화면으로 돌아가요."
                    : matchEnded ? connection.IsHosting
                        ? "게임이 끝났어요. 방을 닫고 처음 화면으로 돌아갈까요? 다른 참가자의 연결도 종료돼요."
                        : "게임이 끝났어요. 처음 화면으로 돌아가면 이 방의 결과를 다시 볼 수 없어요."
                    : connection.IsHosting ? "방을 닫을까요? 다른 참가자의 연결도 종료되고, 진행 중인 게임은 복구할 수 없어요."
                    : CanReturnLobbySeat ? "방에서 나갈까요? 자리를 비우면 다른 친구가 참가할 수 있어요."
                    : "방에서 나갈까요? 이 자리의 연결 정보가 지워져 같은 판으로 돌아올 수 없어요. 잠시 끊긴 경우에는 나가지 말고 다시 연결해 주세요.";
            if (confirming && quittingApplication)
                confirmationCopy.text = ApplicationExitCopy(confirmationCopy.text, canAbandonLeave, matchEnded);
            if (confirming && !leavingLobby && (remote?.HasPendingInput == true || remote?.HasPendingUtterance == true))
                confirmationCopy.text += "\n보낸 입력의 처리 결과를 아직 확인하지 못했어요.";
            nickname.SetEnabled(!session); address.SetEnabled(!session); port.SetEnabled(!session); lan.SetEnabled(!session);
            setup.SetEnabled(!session);
            host.SetEnabled(!session && !failed); join.SetEnabled(!session && !failed);
            Show(host, !session); Show(join, !session);
            endpoint.text = failed ? connection.ErrorText : (connection.IsHosting ? "방장 · " : "연결 · ") + connection.EndpointText
                + (room?.HasRematch == true && playing ? " · " + room.MatchNumber + "번째 경기" : "")
                + (connection.IsConnecting ? " · 연결 중" : "");
            Show(reconnect, !failed && session && canRefresh);
            Show(editConnection, connection.CanEditFailedJoin);
            editConnection.SetEnabled(!confirming && connection.CanEditFailedJoin);
            reconnect.text = remote?.RefreshLabel ?? "다시 연결";
            reconnect.SetEnabled(!confirming && !rematchOpen && !failed && !connection.IsConnecting);
            leave.text = matchEnded ? "처음 화면으로" : "나가기";
            leave.SetEnabled(!confirming && !rematchOpen);
            Show(ready, !failed && room != null && !room.HasGame); Show(start, !failed && room?.IsHost == true && !room.HasGame);
            ready.text = room?.IsReady == true ? "준비 취소" : "준비";
            ready.SetEnabled(remote?.CanSend == true); start.SetEnabled(remote?.CanSend == true && room?.AllReady == true);
            if (room == null) members.text = "";
            else
            {
                var text = new StringBuilder();
                for (int seat = 1; seat <= room.SeatCapacity; seat++)
                {
                    Poker.Presentation.HoldemLobbyMember member = null;
                    for (int i = 0; i < room.MemberCount; i++) if (room.GetMember(i).Seat == seat) member = room.GetMember(i);
                    if (text.Length > 0) text.Append('\n');
                    if (member == null) text.Append(seat).Append("번 · 참가 대기");
                    else text.Append(seat).Append("번 · ").Append(member.Name).Append(member.IsHost ? " (방장)" : "")
                        .Append(member.Seat == room.ViewerSeat ? " (나)" : "")
                        .Append(" · ").Append(!member.Connected ? "재접속 대기" : member.Ready ? "준비 완료" : "준비 전");
                }
                members.text = text.ToString();
            }
            status.text = !string.IsNullOrEmpty(formError) ? formError
                : !string.IsNullOrEmpty(connection.ErrorText) ? connection.ErrorText
                : connection.IsConnecting ? "방에 연결하고 있어요."
                : !string.IsNullOrEmpty(remote?.ErrorText) ? remote.ErrorText
                : !string.IsNullOrEmpty(remote?.StatusText) ? remote.StatusText
                : room != null ? room.AllReady ? "모두 준비됐어요. 방장이 시작해 주세요." : room.SeatCapacity + "명이 모이면 준비를 눌러 주세요."
                : "이름을 입력하고 방을 만들거나 참가해 주세요.";
        }
        private void AskLeave()
        {
            if ((!connection.HasSession && !failed) || confirming) return;
            confirming = true; leaveError = "";
            Render();
        }
        private bool CanReturnLobbySeat => !failed && connection.Remote?.Lobby?.SupportsLobbyLeave == true
            && !connection.Remote.HasUnrecoverableAdmissionError
            && !connection.Remote.Lobby.IsHost && !connection.Remote.Lobby.HasGame;
        private bool CanAbandonUnconfirmedLeave => leavingLobby && connection.CanReconnect
            && connection.Remote?.LobbyLeaveConfirmed != true;
        private void LeaveConfirmed()
        {
            if (!confirming) return;
            if (leavingLobby)
            {
                if (CanAbandonUnconfirmedLeave) ReturnHome();
                return;
            }
            if (CanReturnLobbySeat)
            { leavingLobby = true; leaveSent = false; leaveError = ""; Render(); return; }
            ReturnHome();
        }
        private void ReturnHome()
        {
            dealerDebug?.Dispose(); dealerDebug = null;
            screen?.Dispose(); screen = null; tableSurface.Clear(); connection.CloseSession();
            rematchOpen = false;
            copyFeedback = "";
            confirming = false; failed = false; leavingLobby = false; leaveSent = false; leaveError = ""; formError = ""; Render();
            ((ScrollView)lobby).scrollOffset = Vector2.zero;
            FinishApplicationExitIfRequested();
        }
        private static VisualElement Box(VisualElement parent, string name)
        { var element = new VisualElement { name = name }; element.AddToClassList(name); parent.Add(element); return element; }
        private static Label Text(VisualElement parent, string value, string name)
        { var label = new Label(value) { name = name, enableRichText = false }; label.AddToClassList(name); parent.Add(label); return label; }
        private static Button Button(VisualElement parent, string text, string name, Action action)
        { var button = new Button(action) { text = text, name = name }; parent.Add(button); return button; }
        private static TextField Field(VisualElement parent, string label, string name, string value, int length)
        { var field = new TextField(label) { name = name, value = value, maxLength = length }; parent.Add(field); return field; }
        private static void Show(VisualElement element, bool visible) => element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        private void OnDisable()
        {
            DetachApplicationExitGuard();
            dealerDebug?.Dispose(); dealerDebug = null;
            screen?.Dispose(); screen = null; connection?.Dispose(); connection = null;
            root?.Clear();
            if (ownsBackground)
            {
                ownsBackground = false;
                if (--backgroundOwners == 0) UnityEngine.Application.runInBackground = previousBackground;
            }
        }
    }
}
