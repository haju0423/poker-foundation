using System;
using System.Collections.Generic;
using System.IO;
using Poker.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Editor
{
    /// <summary>Creates and builds the Hold'em table with its own scene, panel and settings.</summary>
    public static class HoldemSceneBuilder
    {
        public const string ScenePath = "Assets/Poker/Samples/HoldemTable.unity";
        public static void Create()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Poker/Samples")) AssetDatabase.CreateFolder("Assets/Poker", "Samples");
            const string settingsPath = "Assets/Poker/Samples/HoldemSettings.asset";
            const string panelPath = "Assets/Poker/Samples/HoldemPanel.asset";
            var settings = AssetDatabase.LoadAssetAtPath<HoldemTableSettings>(settingsPath);
            if (settings == null) { settings = ScriptableObject.CreateInstance<HoldemTableSettings>(); AssetDatabase.CreateAsset(settings, settingsPath); }
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1200, 800);
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight; panel.match = 0.5f;
                panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>("Assets/Poker/Resources/PokerTheme.tss");
                AssetDatabase.CreateAsset(panel, panelPath);
            }
            AssetDatabase.SaveAssets();
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var go = new GameObject("One More Card");
                var doc = go.AddComponent<UIDocument>(); doc.panelSettings = panel;
                var serialized = new SerializedObject(doc);
                serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
                serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(doc);
                var bootstrap = go.AddComponent<HoldemTableBootstrap>(); bootstrap.Settings = settings;
                EditorUtility.SetDirty(bootstrap); EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                BindMissingPanelReference();
            }
            AssetDatabase.SaveAssets();
            Validate();
            Debug.Log("OMC_HOLDEM_SCENE_READY");
        }
        public static void Build() => BuildPlayer(BuildTarget.StandaloneOSX, BuildOptions.None);
        public static void BuildWindows() => BuildPlayer(BuildTarget.StandaloneWindows64, BuildOptions.None);
        public static void ConfigureFourSeatSample()
        {
            Create();
            var settings = AssetDatabase.LoadAssetAtPath<HoldemTableSettings>("Assets/Poker/Samples/HoldemSettings.asset");
            settings.seatCount = 4;
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets();
            Validate();
        }
        public static void BindMissingPanelReference()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var table = UnityEngine.Object.FindFirstObjectByType<HoldemTableBootstrap>();
            var doc = table == null ? null : table.GetComponent<UIDocument>();
            if (doc == null || table.gameObject.name != "One More Card" || scene.rootCount != 1)
                throw new InvalidOperationException("Expected the generated Hold'em sample only.");
            if (doc.panelSettings == null)
            {
                var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
                if (panel == null) throw new InvalidOperationException("The generated panel asset is missing.");
                var serialized = new SerializedObject(doc);
                serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
                serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(doc);
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            Validate();
        }
        private static void BuildPlayer(BuildTarget target, BuildOptions options)
        {
            string path = Argument("-buildOutput");
            string extension = target == BuildTarget.StandaloneOSX ? ".app" : ".exe";
            if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An explicit build output ending with " + extension + " is required.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new InvalidOperationException("Matching desktop build support is unavailable.");
            Create();
            string product = PlayerSettings.productName;
            FullScreenMode mode = PlayerSettings.fullScreenMode;
            int width = PlayerSettings.defaultScreenWidth, height = PlayerSettings.defaultScreenHeight;
            bool resizable = PlayerSettings.resizableWindow;
            string[] oldArgs = PlayerSettings.GetAdditionalCompilerArguments(NamedBuildTarget.Standalone);
            BuildReport report;
            try
            {
                PlayerSettings.productName = "One More Card";
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 1200; PlayerSettings.defaultScreenHeight = 800;
                PlayerSettings.resizableWindow = true;
                string projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath).Replace('\\', '/');
                var args = new List<string>(oldArgs ?? Array.Empty<string>());
                args.Add("-pathmap:\"" + projectRoot + "=/_/Poker\"");
                PlayerSettings.SetAdditionalCompilerArguments(NamedBuildTarget.Standalone, args.ToArray());
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { ScenePath }, locationPathName = Path.GetFullPath(path), target = target, options = options
                });
            }
            finally
            {
                PlayerSettings.SetAdditionalCompilerArguments(NamedBuildTarget.Standalone, oldArgs ?? Array.Empty<string>());
                PlayerSettings.productName = product; PlayerSettings.fullScreenMode = mode;
                PlayerSettings.defaultScreenWidth = width; PlayerSettings.defaultScreenHeight = height;
                PlayerSettings.resizableWindow = resizable;
            }
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Hold'em build failed.");
            File.Copy("Assets/Poker/Resources/Fonts/OFL.txt", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "FONT-LICENSE.txt"), true);
            Debug.Log("OMC_HOLDEM_BUILD_READY");
        }
        private static void Validate()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var table = UnityEngine.Object.FindFirstObjectByType<HoldemTableBootstrap>();
            var doc = table == null ? null : table.GetComponent<UIDocument>();
            if (table == null || table.Settings == null || doc == null || doc.panelSettings == null || doc.panelSettings.themeStyleSheet == null)
                throw new InvalidOperationException("Saved Hold'em scene references are incomplete.");
            table.Settings.CreateConfig();
            if (Resources.Load<StyleSheet>("HoldemTable") == null || Resources.Load<Font>("Fonts/NanumGothic-Regular") == null)
                throw new InvalidOperationException("Hold'em style or Korean font is missing.");
        }
        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
