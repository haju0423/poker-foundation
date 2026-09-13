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
        private PokerInputController input;
        private PokerTableScreen screen;
        private double nextOpponentAt;
        private long scheduledVersion;
        private bool fatal;
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
            screen?.Dispose();
            using (var random = new PracticeRandom()) table = new LocalPokerTable(settings.CreateSetup(), new SeatId(settings.humanSeat), random);
            input = new PokerInputController(table.Human);
            var document = GetComponent<UIDocument>();
            Font font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (font == null) throw new InvalidOperationException("Bundled Korean font is missing.");
            screen = new PokerTableScreen(document.rootVisualElement, input, StartPractice, settings.startingStack, font);
            scheduledVersion = input.View.Version;
            nextOpponentAt = Time.realtimeSinceStartupAsDouble + Math.Max(0.1, settings.opponentDelaySeconds);
            fatal = false;
        }
        private void Update()
        {
            if (fatal || input == null || input.IsPending) return;
            if (scheduledVersion != input.View.Version)
            {
                scheduledVersion = input.View.Version;
                nextOpponentAt = Time.realtimeSinceStartupAsDouble + Math.Max(0.1, settings.opponentDelaySeconds);
            }
            if (Time.realtimeSinceStartupAsDouble < nextOpponentAt) return;
            try
            {
                if (table.AdvanceOpponent()) { input.Refresh(); screen.Render(); }
                scheduledVersion = input.View.Version;
                nextOpponentAt = Time.realtimeSinceStartupAsDouble + Math.Max(0.1, settings.opponentDelaySeconds);
            }
            catch (Exception)
            {
                fatal = true;
                Debug.LogError("Poker practice stopped because its authority could not advance. No automatic reset was performed.");
            }
        }
        private void OnDisable() { screen?.Dispose(); screen = null; input = null; table = null; }
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
