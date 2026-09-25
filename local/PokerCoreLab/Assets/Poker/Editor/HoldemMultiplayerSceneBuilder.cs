using System;
using System.IO;
using Poker.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Poker.Editor
{
    public static class HoldemMultiplayerSceneBuilder
    {
        public const string ScenePath = "Assets/Poker/Samples/HoldemMultiplayer.unity";
        public const string SettingsPath = "Assets/Poker/Samples/HoldemMultiplayerSettings.asset";
        public const string FlowScenePath = "Assets/Poker/Samples/HoldemMultiplayerFlowPreview.unity";
        public const string FlowSettingsPath = "Assets/Poker/Samples/HoldemMultiplayerFlowPreviewSettings.asset";
        public static void Create() => Create(false);
        public static void CreateFlowPreview() => Create(true);
        // Explicitly opt the regular scene into the public-remarks blueprint without manual dealing.
        public static void EnablePublicUtterancesInMultiplayer()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Configure outside Play mode.");
            var settings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(SettingsPath);
            if (settings == null || settings.enableFlowPreview) throw new InvalidOperationException("Regular multiplayer settings are required.");
            settings.enableUtterances = true;
            settings.utteranceVisibility = Poker.Application.HoldemUtteranceVisibility.PublicRaw;
            settings.utteranceBatchRetention = Poker.Application.HoldemUtteranceBatchRetention.None;
            settings.CreateConfig(); settings.CreateUtterancePolicy();
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
            Debug.Log("OMC_STANDARD_PUBLIC_UTTERANCES_READY");
        }
        // Explicit migration, never an implicit overwrite performed by an ordinary scene build.
        public static void EnablePublicUtterancesInFlowPreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Configure outside Play mode.");
            var settings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(FlowSettingsPath);
            if (settings == null || !settings.enableFlowPreview) throw new InvalidOperationException("Flow preview settings are required.");
            settings.utteranceVisibility = Poker.Application.HoldemUtteranceVisibility.PublicRaw;
            settings.CreateUtterancePolicy();
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
            Debug.Log("OMC_PUBLIC_UTTERANCE_PREVIEW_READY");
        }
        private static void Create(bool flowPreview)
        {
            string scenePath = flowPreview ? FlowScenePath : ScenePath;
            string settingsPath = flowPreview ? FlowSettingsPath : SettingsPath;
            string rootName = flowPreview ? "One More Card Multiplayer Flow Preview" : "One More Card Multiplayer";
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Create outside Play mode.");
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
            if (panel == null || panel.themeStyleSheet == null || Resources.Load<StyleSheet>("HoldemLobby") == null
                || Resources.Load<StyleSheet>("HoldemTable") == null || Resources.Load<Font>("Fonts/NanumGothic-Regular") == null)
                throw new InvalidOperationException("Multiplayer UI resources are missing.");
            var settings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(settingsPath);
            if (settings == null)
            {
                if (File.Exists(settingsPath)) throw new InvalidOperationException("Settings path is occupied.");
                settings = ScriptableObject.CreateInstance<HoldemMultiplayerSettings>();
                settings.enableFlowPreview = flowPreview;
                if (!flowPreview)
                {
                    settings.enableUtterances = true;
                    settings.utteranceVisibility = Poker.Application.HoldemUtteranceVisibility.PublicRaw;
                    settings.utteranceBatchRetention = Poker.Application.HoldemUtteranceBatchRetention.None;
                }
                AssetDatabase.CreateAsset(settings, settingsPath); AssetDatabase.SaveAssetIfDirty(settings);
            }
            settings.CreateConfig();
            settings.CreateUtterancePolicy();
            if (settings.enableFlowPreview != flowPreview) throw new InvalidOperationException("The saved scene settings belong to another flow.");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            try
            {
                if (openedHere)
                {
                    if (File.Exists(scenePath)) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    else
                    {
                        bool cleanPlaceholder = UnityEngine.Application.isBatchMode && SceneManager.sceneCount == 1
                            && string.IsNullOrEmpty(previous.path) && !previous.isDirty;
                        scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                            cleanPlaceholder ? NewSceneMode.Single : NewSceneMode.Additive);
                        // Single mode unloads assets that were only retained by local editor variables.
                        panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
                        settings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(settingsPath);
                        if (panel == null || settings == null) throw new InvalidOperationException("Scene resources could not be reloaded.");
                        var go = new GameObject(rootName); SceneManager.MoveGameObjectToScene(go, scene);
                        var doc = go.AddComponent<UIDocument>(); AssignPanel(doc, panel);
                        var bootstrap = go.AddComponent<HoldemMultiplayerBootstrap>(); bootstrap.Settings = settings;
                        EditorUtility.SetDirty(bootstrap);
                        if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new InvalidOperationException("Could not save multiplayer scene.");
                    }
                    scene = Reload(scene, scenePath);
                    panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
                    settings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(settingsPath);
                }
                var roots = scene.GetRootGameObjects();
                var table = roots.Length == 1 && roots[0].name == rootName
                    ? roots[0].GetComponent<HoldemMultiplayerBootstrap>() : null;
                var document = table == null ? null : table.GetComponent<UIDocument>();
                if (table == null || table.Settings != settings || document == null)
                    throw new InvalidOperationException("Saved multiplayer scene is incomplete.");
                if (document.panelSettings == null)
                {
                    if (scene.isDirty && !openedHere) throw new InvalidOperationException("Save scene edits first.");
                    AssignPanel(document, panel); EditorSceneManager.MarkSceneDirty(scene);
                    if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save panel reference.");
                    if (openedHere)
                    {
                        scene = Reload(scene, scenePath); document = scene.GetRootGameObjects()[0].GetComponent<UIDocument>();
                        panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
                    }
                }
                if (panel == null || document.panelSettings == null || document.panelSettings != panel)
                    throw new InvalidOperationException("Saved panel mismatch.");
            }
            finally
            {
                if (openedHere && scene.IsValid() && scene.isLoaded && SceneManager.sceneCount > 1)
                    EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
            Debug.Log("OMC_MULTIPLAYER_SCENE_READY");
        }
        private static void AssignPanel(UIDocument doc, PanelSettings panel)
        {
            // Keep UIDocument's previous-panel state in sync with its serialized reference.
            doc.panelSettings = panel;
            var serialized = new SerializedObject(doc); serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
            serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(doc);
        }
        private static Scene Reload(Scene scene, string scenePath)
        {
            if (scene.isDirty) throw new InvalidOperationException("Save the scene before reloading.");
            // Opening an already-loaded sole scene in Single mode can keep its in-memory references.
            // Keep a temporary empty scene so the target can be fully closed before checking its saved data.
            Scene placeholder = default;
            try
            {
                if (SceneManager.sceneCount == 1)
                    placeholder = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                if (!EditorSceneManager.CloseScene(scene, true)) throw new InvalidOperationException("Could not reload saved scene.");
                return EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            finally
            {
                if (placeholder.IsValid() && placeholder.isLoaded) EditorSceneManager.CloseScene(placeholder, true);
            }
        }
    }
}
