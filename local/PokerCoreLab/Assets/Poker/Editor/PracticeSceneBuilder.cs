using System;
using System.IO;
using Poker.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Editor
{
    /// <summary>Reproducible sample scene creation, never raw Unity YAML or changes to a team's unrelated scenes.</summary>
    public static class PracticeSceneBuilder
    {
        public const string ScenePath = "Assets/Poker/Samples/PracticeTable.unity";
        public static void Create()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Poker/Samples")) AssetDatabase.CreateFolder("Assets/Poker", "Samples");
            const string settingsPath = "Assets/Poker/Samples/PracticeSettings.asset";
            const string panelPath = "Assets/Poker/Samples/PracticePanel.asset";
            PracticeTableSettings settings = AssetDatabase.LoadAssetAtPath<PracticeTableSettings>(settingsPath);
            if (settings == null) { settings = ScriptableObject.CreateInstance<PracticeTableSettings>(); AssetDatabase.CreateAsset(settings, settingsPath); }
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize; panel.referenceResolution = new Vector2Int(1200, 800);
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight; panel.match = 0.5f;
                panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/Poker/Resources/PokerTheme.tss");
                AssetDatabase.CreateAsset(panel, panelPath);
            }
            // Preserve an existing scene: repeated setup is not permission to overwrite user edits.
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var go = new GameObject("Practice Table");
                var doc = go.AddComponent<UIDocument>(); doc.panelSettings = panel;
                var bootstrap = go.AddComponent<PracticeTableBootstrap>(); bootstrap.Settings = settings;
                SetPanelReference(doc, panel);
                EditorUtility.SetDirty(bootstrap); EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("POKER_PRACTICE_SCENE_READY");
        }
        public static void Build() => BuildPlayer(BuildTarget.StandaloneOSX, BuildOptions.None);
        public static void BuildSmoke() => BuildPlayer(BuildTarget.StandaloneOSX, BuildOptions.Development);
        public static void BuildWindows() => BuildPlayer(BuildTarget.StandaloneWindows64, BuildOptions.None);
        public static void BuildWindowsSmoke() => BuildPlayer(BuildTarget.StandaloneWindows64, BuildOptions.Development);
        private static void BuildPlayer(BuildTarget target, BuildOptions options)
        {
            string path = Argument("-buildOutput");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Explicit -buildOutput is required.");
            string extension = target == BuildTarget.StandaloneOSX ? ".app" : ".exe";
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Build output must end with " + extension + ".");
            path = Path.GetFullPath(path);
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new InvalidOperationException("Install the matching Unity build support module for " + target + ".");
            Create();
            ValidateSavedScene();
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1200; PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.productName = "Five Card Draw Practice";
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path,
                target = target, options = options
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Practice build failed.");
            File.Copy("Assets/Poker/Resources/Fonts/OFL.txt", Path.Combine(Path.GetDirectoryName(path), "FONT-LICENSE.txt"), true);
            Debug.Log("POKER_PRACTICE_BUILD_READY");
        }
        // Explicit repair for this generated sample only. Build never silently overwrites scene bindings.
        public static void BindMissingSampleReferences()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var doc = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            if (doc == null || doc.gameObject.name != "Practice Table") throw new InvalidOperationException("Expected generated practice document.");
            if (doc.panelSettings == null)
                SetPanelReference(doc, AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/PracticePanel.asset"));
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            ValidateSavedScene();
        }
        private static void SetPanelReference(UIDocument doc, PanelSettings panel)
        {
            if (panel == null) throw new InvalidOperationException("Practice panel asset is missing.");
            var serialized = new SerializedObject(doc);
            serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
            serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(doc);
        }
        private static void ValidateSavedScene()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var doc = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            var bootstrap = doc == null ? null : doc.GetComponent<PracticeTableBootstrap>();
            if (doc == null || doc.panelSettings == null || doc.panelSettings.themeStyleSheet == null || bootstrap == null || bootstrap.Settings == null)
                throw new InvalidOperationException("Saved practice scene has missing UI/settings references; build stopped.");
            // Validate the saved fixture and resources before producing a Player that cannot start its screen.
            bootstrap.Settings.CreateSetup();
            float delay = bootstrap.Settings.opponentDelaySeconds;
            if (float.IsNaN(delay) || float.IsInfinity(delay))
                throw new InvalidOperationException("Saved practice delay must be finite; build stopped.");
            if (Resources.Load<StyleSheet>("PokerTable") == null || Resources.Load<Font>("Fonts/NanumGothic-Regular") == null)
                throw new InvalidOperationException("Required poker style/font resource is missing; build stopped.");
        }
        private static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
