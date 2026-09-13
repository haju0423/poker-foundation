#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Explicit headless development-player check. Entire file is absent from normal release builds.</summary>
    public sealed class PracticeBuildSmoke : MonoBehaviour
    {
        public const string OutputArgument = "-poker-smoke-output";
        private string output;
        private double deadline;
        private int frames;
        private PracticeSmokeScenario scenario;
        private bool finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!ShouldRun(Environment.GetCommandLineArgs(), UnityEngine.Application.isEditor,
                UnityEngine.Application.isBatchMode, Debug.isDebugBuild)) return;
            try
            {
                string path = ReadOutput(Environment.GetCommandLineArgs());
                var driver = new GameObject("Development Player Smoke").AddComponent<PracticeBuildSmoke>();
                driver.output = path;
                driver.deadline = Time.realtimeSinceStartupAsDouble + 60;
                UnityEngine.Application.targetFrameRate = 60;
            }
            catch (Exception error)
            {
                Debug.LogError("POKER_SMOKE_ARGUMENT_FAILURE " + error.GetType().Name);
                UnityEngine.Application.Quit(2);
            }
        }

        public static bool ShouldRun(string[] args, bool isEditor, bool isBatchMode, bool isDevelopment)
            => args != null && !isEditor && isBatchMode && isDevelopment && args.Contains(OutputArgument);

        public static string ReadOutput(string[] args)
        {
            if (args == null || args.Count(x => x == OutputArgument) != 1 || !args.Contains("-nographics"))
                throw new ArgumentException("One output argument and nographics are required.");
            int index = Array.IndexOf(args, OutputArgument);
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("An explicit output file is required.");
            string path = args[index + 1];
            if (!Path.IsPathRooted(path) || !path.EndsWith(".json", StringComparison.Ordinal)
                || !Directory.Exists(Path.GetDirectoryName(path)) || File.Exists(path) || Directory.Exists(path))
                throw new ArgumentException("A new absolute JSON path in an existing directory is required.");
            return path;
        }

        private void Update()
        {
            if (finished || output == null) return;
            try
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException();
                if (++frames < 6) return;
                if (scenario == null)
                {
                    var tables = FindObjectsByType<PracticeTableBootstrap>(FindObjectsSortMode.None);
                    if (tables.Length != 1) throw new InvalidOperationException("Expected one saved practice table.");
                    var document = tables[0].GetComponent<UIDocument>();
                    if (document == null || document.panelSettings == null || document.panelSettings.themeStyleSheet == null
                        || tables[0].Settings == null || Resources.Load<Font>("Fonts/NanumGothic-Regular") == null)
                        throw new InvalidOperationException("Saved scene assets are unavailable.");
                    scenario = new PracticeSmokeScenario(tables[0], document.rootVisualElement);
                }
                scenario.Tick();
                if (scenario.Completed) Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        private void Finish(Exception error)
        {
            if (finished) return;
            finished = true;
            int exit = error == null ? 0 : 3;
            var report = new PracticeSmokeReport
            {
                schemaVersion = 1, kind = "poker-development-smoke",
                passed = error == null, failureType = error == null ? "" : error.GetType().Name,
                unityVersion = UnityEngine.Application.unityVersion, developmentBuild = Debug.isDebugBuild,
                batchMode = UnityEngine.Application.isBatchMode, frames = frames,
                completedHands = scenario == null ? 0 : scenario.CompletedHands,
                explicitRestarts = scenario == null ? 0 : scenario.ExplicitRestarts,
                humanActions = scenario == null ? 0 : scenario.HumanActions,
                exchangeHands = scenario == null ? 0 : scenario.ExchangeHands,
                secondBettingHands = scenario == null ? 0 : scenario.SecondBettingHands
            };
            try
            {
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(report, true));
            }
            catch (Exception writeError)
            {
                exit = 4;
                Debug.LogError("POKER_SMOKE_REPORT_FAILURE " + writeError.GetType().Name);
            }
            Debug.Log(exit == 0 ? "POKER_SMOKE_PASS" : "POKER_SMOKE_FAIL");
            UnityEngine.Application.Quit(exit);
        }
    }

    [Serializable]
    public sealed class PracticeSmokeReport
    {
        public int schemaVersion;
        public string kind;
        public bool passed, developmentBuild, batchMode;
        public string failureType, unityVersion;
        public int frames, completedHands, explicitRestarts, humanActions, exchangeHands, secondBettingHands;
    }

    /// <summary>Programmatic UI callbacks and public phase only; no deck, opponent cards, seed or authority access.</summary>
    public sealed class PracticeSmokeScenario
    {
        private readonly PracticeTableBootstrap table;
        private readonly VisualElement root;
        private int stage;
        private bool sawExchange, sawSecondBetting;
        public bool Completed { get; private set; }
        public int CompletedHands { get; private set; }
        public int ExplicitRestarts { get; private set; }
        public int HumanActions { get; private set; }
        public int ExchangeHands { get; private set; }
        public int SecondBettingHands { get; private set; }

        public PracticeSmokeScenario(PracticeTableBootstrap table, VisualElement root)
        {
            this.table = table != null ? table : throw new ArgumentNullException(nameof(table));
            this.root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public void Tick()
        {
            if (Completed) return;
            Require(table != null && table.isActiveAndEnabled, "Active scene was lost.");
            Require(root.Query<Button>(className: "card").ToList().Count == 5
                && root.Query<VisualElement>(className: "card-back").ToList().Count == 5, "Expected own cards and opponent backs.");
            Require(root.Q<Label>(className: "title")?.text == KoreanTableText.Title, "Korean title is unavailable.");
            var progress = table.Progress;
            if (stage == 0)
            {
                Require(progress.Phase == HandPhase.FirstBetting && progress.OwnTurn && progress.Version == 1, "Initial practice differs.");
                Send("help-button"); Require(root.Q(className: "help-overlay").style.display.value != DisplayStyle.None, "Help did not open.");
                Send("close-help"); Require(root.Q(className: "help-overlay").style.display.value == DisplayStyle.None, "Help did not close.");
                Act("fold"); Require(table.Progress.Phase == HandPhase.Complete, "Fold did not settle.");
                CompletedHands++; stage = 1; return;
            }
            if (stage == 1)
            {
                Restart(); stage = 2; return;
            }
            Require(progress.Phase != HandPhase.AwaitingSettlementRule, "Unexpected pending team rule in the personal fixture.");
            if (progress.Phase == HandPhase.Exchange && !sawExchange) { sawExchange = true; ExchangeHands++; }
            if (progress.Phase == HandPhase.SecondBetting && !sawSecondBetting) { sawSecondBetting = true; SecondBettingHands++; }
            if (progress.Phase == HandPhase.Complete)
            {
                Require(sawExchange && sawSecondBetting, "Normal practice skipped a required stage.");
                Require(root.Q<Label>(className: "result")?.text.Length > 0, "Result is unavailable.");
                CompletedHands++;
                Restart();
                if (CompletedHands == 6) Completed = true;
                return;
            }
            if (!progress.OwnTurn) return;
            Require(HumanActions < 128, "Human action budget exceeded.");
            if (progress.Phase == HandPhase.Exchange)
            {
                // Select one visible card, using the same callback as the player, then confirm once.
                Send(root.Query<Button>(className: "card").First());
                Require(root.Query<Button>(className: "selected").ToList().Count == 1, "Card selection was not applied.");
                Act("exchange");
            }
            else
            {
                Require(progress.Phase == HandPhase.FirstBetting || progress.Phase == HandPhase.SecondBetting, "Unexpected action phase.");
                Act(Available(root.Q<Button>("check")) ? "check" : "call");
            }
        }

        private void Restart()
        {
            Require(table.Progress.Phase == HandPhase.Complete, "Restart requested before completion.");
            Send("new-practice");
            Require(table.Progress.Phase == HandPhase.FirstBetting && table.Progress.Version == 1 && table.Progress.OwnTurn,
                "Explicit new practice did not start.");
            sawExchange = false; sawSecondBetting = false; ExplicitRestarts++;
        }
        private void Act(string name)
        {
            long before = table.Progress.Version;
            Send(name);
            Require(table.Progress.Version == before + 1, "A UI action was not applied exactly once.");
            HumanActions++;
        }
        private void Send(string name) => Send(root.Q<Button>(name));
        private static bool Available(Button button) => button != null && button.enabledInHierarchy && button.style.display.value != DisplayStyle.None;
        private static void Send(Button button)
        {
            Require(Available(button), "Requested button is unavailable.");
            using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
