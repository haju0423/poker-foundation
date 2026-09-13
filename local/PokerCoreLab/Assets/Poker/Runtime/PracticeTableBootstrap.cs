using System;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Trusted sample composition; UI renderer never receives the table authority.</summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PracticeTableBootstrap : MonoBehaviour
    {
        [SerializeField] private PracticeTableSettings settings;
        private LocalPokerTable table;
        private PracticeRandom random;
        private PokerInputController input;
        private PokerTableScreen screen;
        private double nextOpponentAt;
        private double opponentDelay;
        private long scheduledVersion;
        private PracticeProgressRunner progress;
        public PracticeTableSettings Settings { get => settings; set => settings = value; }
        public PokerPlayerPhaseInfo Progress => input == null ? default : new PokerPlayerPhaseInfo(input.View.Phase, input.View.IsOwnTurn, input.View.Version);

        private void OnEnable()
        {
            if (settings == null) throw new InvalidOperationException("Assign the explicit practice settings asset.");
            if (!UnityEngine.Application.isBatchMode) Screen.SetResolution(1200, 800, FullScreenMode.Windowed);
            StartPractice();
        }
        private void StartPractice()
        {
            // Never reset a live/awaiting-rule hand. Only the initial start or an explicit completed-practice restart.
            if (input != null && input.View.Phase != HandPhase.Complete) return;
            // Validate caller settings/assets before disposing the currently displayed completed result.
            if (settings == null) throw new InvalidOperationException("Assign the explicit practice settings asset.");
            HandSetup setup = settings.CreateSetup();
            double nextDelay = settings.opponentDelaySeconds;
            if (double.IsNaN(nextDelay) || double.IsInfinity(nextDelay))
                throw new ArgumentException("The practice delay must be finite.");
            nextDelay = Math.Max(0.1, nextDelay);
            var humanSeat = new SeatId(settings.humanSeat);
            var document = GetComponent<UIDocument>();
            if (document == null || document.rootVisualElement == null)
                throw new InvalidOperationException("The practice UI document is unavailable.");
            Font font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (font == null) throw new InvalidOperationException("Bundled Korean font is missing.");
            if (Resources.Load<StyleSheet>("PokerTable") == null)
                throw new InvalidOperationException("Poker table style asset is missing.");
            if (table == null)
            {
                var candidateRandom = new PracticeRandom();
                try
                {
                    table = new LocalPokerTable(setup, humanSeat, candidateRandom);
                    random = candidateRandom;
                }
                catch { candidateRandom.Dispose(); throw; }
            }
            else
            {
                if (input.View.ViewerSeat != humanSeat)
                    throw new InvalidOperationException("The human seat cannot change within an active practice host.");
                var request = HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), setup, input.View.HandId, input.View.Version);
                HandStartReceipt receipt = table.StartNext(request);
                if (!receipt.Accepted) throw new InvalidOperationException("The completed practice could not start its explicitly configured next hand: " + receipt.Error);
            }
            var nextInput = new PokerInputController(table.Human);
            progress = null;
            screen?.Dispose();
            input = nextInput;
            screen = new PokerTableScreen(document.rootVisualElement, input, StartPractice, settings.startingStack, font, RetryProgress);
            var currentScreen = screen;
            progress = new PracticeProgressRunner(table.AdvanceOpponent, () => { nextInput.Refresh(); currentScreen.ResumeProgress(); });
            opponentDelay = nextDelay;
            ScheduleNextOpponent();
        }
        private void Update()
        {
            if (progress == null || progress.Failure != PracticeProgressFailure.None || input == null || input.IsPending
                || screen == null || screen.IsProgressPaused) return;
            if (scheduledVersion != input.View.Version)
            {
                scheduledVersion = input.View.Version;
                nextOpponentAt = Time.realtimeSinceStartupAsDouble + opponentDelay;
            }
            if (Time.realtimeSinceStartupAsDouble < nextOpponentAt) return;
            var currentProgress = progress;
            PracticeProgressOutcome outcome = currentProgress.Tick();
            if (progress != currentProgress) return;
            HandleProgress(outcome);
            ScheduleNextOpponent();
        }
        private void RetryProgress()
        {
            if (progress == null || input == null || input.IsPending) return;
            var currentProgress = progress;
            PracticeProgressOutcome outcome = currentProgress.Retry();
            if (progress != currentProgress) return;
            HandleProgress(outcome);
            ScheduleNextOpponent();
        }
        private void HandleProgress(PracticeProgressOutcome outcome)
        {
            if (outcome != PracticeProgressOutcome.Failed) return;
            screen.PauseProgress(progress.Failure);
            Debug.LogError("Poker practice paused at " + progress.Failure + " (" + progress.FailureType + "); no automatic retry or reset.");
        }
        private void ScheduleNextOpponent()
        {
            if (input == null || progress == null) return;
            scheduledVersion = input.View.Version;
            nextOpponentAt = Time.realtimeSinceStartupAsDouble + opponentDelay;
        }
        private void OnDisable()
        {
            try { screen?.Dispose(); }
            finally
            {
                screen = null; input = null; table = null; progress = null;
                random?.Dispose(); random = null;
            }
        }
    }

    /// <summary>Public smoke-test progress only: no cards, private identities, or authority references.</summary>
    public readonly struct PokerPlayerPhaseInfo
    {
        public PokerPlayerPhaseInfo(HandPhase phase, bool ownTurn, long version) { Phase = phase; OwnTurn = ownTurn; Version = version; }
        public HandPhase Phase { get; }
        public bool OwnTurn { get; }
        public long Version { get; }
    }
}
