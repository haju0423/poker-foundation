using System;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class HoldemTableBootstrap : MonoBehaviour
    {
        [SerializeField] private HoldemTableSettings settings;
        private HoldemLocalTable table;
        private PracticeRandom deckRandom, opponentRandom;
        private HoldemTableScreen screen;
        private HoldemLocalDealerDebug dealerDebug;
        private double nextOpponentAt, opponentDelay;
        private long observedVersion;
        public HoldemTableOptions ActiveOptions { get; private set; }
        public HoldemTableSettings Settings { get => settings; set => settings = value; }
        public Action ReturnToMenu { get; set; }
        public bool EnableDealerDebug { get; set; }
        public HoldemProgressInfo Progress
        {
            get
            {
                if (table == null) return default;
                var view = table.Human.Read();
                return new HoldemProgressInfo(view.Street, view.LegalActions != null, view.SessionVersion,
                    view.HandNumber, view.SeatCount, view.IsDealPending, view.IsRevealPending);
            }
        }
        private void OnEnable()
        {
            if (!UnityEngine.Application.isBatchMode && Screen.fullScreenMode != FullScreenMode.Windowed)
                Screen.fullScreenMode = FullScreenMode.Windowed;
            StartNewSession();
        }
        private void StartNewSession() => StartNewSession(false);
        private void StartNewSession(bool abandonActive) => ReplaceSession(abandonActive, ActiveOptions);
        private void ConfigureSession(HoldemTableOptions options) => ReplaceSession(true, options);
        private void ReplaceSession(bool abandonActive, HoldemTableOptions options)
        {
            if (table != null && table.Human.Read().Result == null && !abandonActive) return;
            if (settings == null) throw new InvalidOperationException("Assign the Hold'em table settings.");
            HoldemConfig original = settings.CreateConfig();
            options = options ?? new HoldemTableOptions(settings.seatCount, settings.opponentDelaySeconds, settings.revealPolicy);
            if (EnableDealerDebug) options = new HoldemTableOptions(options.SeatCount, options.OpponentDelaySeconds,
                HoldemRevealPolicy.PauseAfterCommunityReveal);
            HoldemConfig config = new HoldemConfig(original.StartingStack, original.SmallBlind, original.BigBlind,
                options.RevealPolicy, EnableDealerDebug ? HoldemAccusationMode.CollectLatestChoiceUntilHostCloses : original.AccusationMode,
                EnableDealerDebug ? HoldemDealPolicy.WaitForHost : original.DealPolicy);
            var doc = GetComponent<UIDocument>();
            Font font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (doc == null || doc.panelSettings == null || font == null || Resources.Load<StyleSheet>("HoldemTable") == null)
                throw new InvalidOperationException("The saved Hold'em scene is missing UI resources.");
            var nextDeck = new PracticeRandom();
            var nextOpponent = new PracticeRandom();
            HoldemLocalTable candidate;
            HoldemTableScreen candidateScreen = null;
            HoldemLocalDealerDebug candidateDebug = null;
            var surface = new VisualElement();
            try
            {
                candidate = new HoldemLocalTable(config, options.SeatCount,
                    EnableDealerDebug ? (IRandomSource)new HoldemLocalDealerDebug.OrderedDeck() : nextDeck, nextOpponent,
                    opponent: EnableDealerDebug ? new HoldemLocalDealerDebug.PassiveOpponent() : (IHoldemOpponentPolicy)null,
                    utterancePolicy: EnableDealerDebug ? new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active,
                        HoldemUtteranceVisibility.PublicRaw) : settings.CreateUtterancePolicy());
                // Construct and render off-tree first. A failed replacement must leave the old table visible.
                candidateScreen = new HoldemTableScreen(surface, candidate.Human, StartNewSession, config.StartingStack, font,
                    () => StartNewSession(true), candidate.ResumeAfterReveal, options,
                    !EnableDealerDebug && original.AccusationMode == HoldemAccusationMode.Disabled ? ConfigureSession : (Action<HoldemTableOptions>)null,
                    dealUnchanged: !EnableDealerDebug && original.DealPolicy == HoldemDealPolicy.WaitForHost
                        ? candidate.DealUnchanged : (Func<HoldemDealCommand, HoldemReceipt>)null,
                    utterancePort: candidate.HumanUtterances, returnToMenu: ReturnToMenu,
                    dealPendingPrompt: EnableDealerDebug ? "위의 테스트 결과를 고르면 공용 카드를 공개해요." : null);
                if (EnableDealerDebug) candidateDebug = new HoldemLocalDealerDebug(candidate, candidateScreen);
            }
            catch { candidateDebug?.Dispose(); candidateScreen?.Dispose(); nextDeck.Dispose(); nextOpponent.Dispose(); throw; }
            dealerDebug?.Dispose(); screen?.Dispose(); deckRandom?.Dispose(); opponentRandom?.Dispose();
            doc.rootVisualElement.Clear(); doc.rootVisualElement.Add(surface);
            table = candidate; deckRandom = nextDeck; opponentRandom = nextOpponent;
            ActiveOptions = options;
            opponentDelay = options.OpponentDelaySeconds;
            screen = candidateScreen;
            dealerDebug = candidateDebug;
            Schedule();
#if !UNITY_EDITOR
            HoldemSoloAccusationProcessCheck.AttachIfRequested(this);
            // Explicit headless startup check for the packaged player; never used during normal play.
            if (UnityEngine.Application.isBatchMode
                && Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-check-startup") >= 0)
            {
                Debug.Log("OMC_STARTUP_OK seats=" + options.SeatCount + " hand=" + table.Human.Read().HandNumber
                    + " mode=" + (EnableDealerDebug ? "dealer-test" : "solo"));
                UnityEngine.Application.Quit(0);
            }
#endif
        }
        private void Update()
        {
            if (table == null || screen == null) return;
            try
            {
                dealerDebug?.Tick();
                if (screen.IsProgressPaused) return;
                var view = table.Human.Read();
                if (view.SessionVersion != observedVersion) Schedule();
                if (view.Accusations != null && view.Accusations.Phase == HoldemAccusationPhase.Collecting)
                {
                    if (Time.realtimeSinceStartupAsDouble < nextOpponentAt) return;
                    if (table.AdvanceAccusationResponses()) screen.Render();
                    Schedule();
                    return;
                }
                if (!view.CurrentSeat.HasValue || view.CurrentSeat == view.ViewerSeat || Time.realtimeSinceStartupAsDouble < nextOpponentAt) return;
                if (table.AdvanceNpc()) screen.Render();
                Schedule();
            }
            catch (Exception e)
            {
                screen.PauseProgress();
                Debug.LogError("Holdem progression paused (" + e.GetType().Name + "); current hand retained.");
            }
        }
        private void Schedule()
        {
            observedVersion = table.Human.Read().SessionVersion;
            nextOpponentAt = Time.realtimeSinceStartupAsDouble + opponentDelay;
        }
        private void OnDisable()
        {
            dealerDebug?.Dispose(); dealerDebug = null;
            screen?.Dispose(); screen = null; table = null;
            deckRandom?.Dispose(); deckRandom = null;
            opponentRandom?.Dispose(); opponentRandom = null;
        }
    }

    /// <summary>Non-sensitive progress for scene tests; carries no private cards or authority access.</summary>
    public readonly struct HoldemProgressInfo
    {
        public HoldemProgressInfo(HoldemStreet street, bool ownTurn, long version, long handNumber, int seatCount = 2,
            bool isDealPending = false, bool isRevealPending = false)
        {
            Street = street; OwnTurn = ownTurn; Version = version; HandNumber = handNumber; SeatCount = seatCount;
            IsDealPending = isDealPending; IsRevealPending = isRevealPending;
        }
        public HoldemStreet Street { get; }
        public bool OwnTurn { get; }
        public long Version { get; }
        public long HandNumber { get; }
        public int SeatCount { get; }
        public bool IsDealPending { get; }
        public bool IsRevealPending { get; }
    }
}
