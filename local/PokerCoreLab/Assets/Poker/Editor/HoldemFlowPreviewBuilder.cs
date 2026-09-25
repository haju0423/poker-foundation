using System;
using System.IO;
using Poker.Foundation;
using Poker.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Poker.Editor
{
    /// <summary>Separate, opt-in sample. Never rewrites the ordinary scene, settings or build scene list.</summary>
    public static class HoldemFlowPreviewBuilder
    {
        public const string ScenePath = "Assets/Poker/Samples/HoldemFlowPreview.unity";
        public const string SettingsPath = "Assets/Poker/Samples/HoldemFlowPreviewSettings.asset";

        [MenuItem("Poker/Enable Speech Intake In Flow Preview")]
        public static void EnableUtterancePreview()
        {
            Create();
            var settings = AssetDatabase.LoadAssetAtPath<HoldemTableSettings>(SettingsPath);
            // These are editable local test limits, not multiplayer or team rules.
            settings.enableUtterancePreview = true;
            settings.CreateUtterancePolicy();
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
            Debug.Log("OMC_UTTERANCE_PREVIEW_READY " + ScenePath);
        }

        [MenuItem("Poker/Create Flow Preview")]
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Create the preview outside Play mode.");
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
            if (panel == null || panel.themeStyleSheet == null)
                throw new InvalidOperationException("The existing Hold'em panel must be available.");
            var settings = AssetDatabase.LoadAssetAtPath<HoldemTableSettings>(SettingsPath);
            if (settings == null)
            {
                if (File.Exists(SettingsPath)) throw new InvalidOperationException("The preview settings path is occupied.");
                settings = ScriptableObject.CreateInstance<HoldemTableSettings>();
                settings.dealPolicy = HoldemDealPolicy.WaitForHost;
                settings.revealPolicy = HoldemRevealPolicy.PauseAfterCommunityReveal;
                settings.accusationMode = HoldemAccusationMode.Disabled;
                settings.seatCount = 4;
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssetIfDirty(settings);
            }
            settings.CreateConfig();
            if (settings.dealPolicy != HoldemDealPolicy.WaitForHost || settings.accusationMode != HoldemAccusationMode.Disabled)
                throw new InvalidOperationException("Preview settings must select host dealing without accusation simulation.");

            // Preserve existing configuration; only repair a missing panel on this exact preview.
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            try
            {
                if (openedHere)
                {
                    if (File.Exists(ScenePath)) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                    else
                    {
                        // Batch starts with an untitled placeholder which cannot host an additive new scene.
                        // Replace only that clean, headless placeholder; never discard an interactive scene.
                        bool cleanBatchStart = UnityEngine.Application.isBatchMode && SceneManager.sceneCount == 1
                            && string.IsNullOrEmpty(previous.path) && !previous.isDirty;
                        scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                            cleanBatchStart ? NewSceneMode.Single : NewSceneMode.Additive);
                        var go = new GameObject("One More Card Flow Preview");
                        SceneManager.MoveGameObjectToScene(go, scene);
                        var doc = go.AddComponent<UIDocument>();
                        var serialized = new SerializedObject(doc);
                        serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        var bootstrap = go.AddComponent<HoldemTableBootstrap>(); bootstrap.Settings = settings;
                        EditorUtility.SetDirty(doc); EditorUtility.SetDirty(bootstrap);
                        if (!EditorSceneManager.SaveScene(scene, ScenePath))
                            throw new InvalidOperationException("Could not save the flow preview scene.");
                    }
                }
                if (openedHere) scene = Reload(scene);
                var roots = scene.GetRootGameObjects();
                var table = roots.Length == 1 && roots[0].name == "One More Card Flow Preview"
                    ? roots[0].GetComponent<HoldemTableBootstrap>() : null;
                var document = table == null ? null : table.GetComponent<UIDocument>();
                if (table == null || table.Settings != settings || document == null)
                    throw new InvalidOperationException("The saved flow preview references are incomplete.");
                // A newly created UIDocument may lose its panel on its first save. Rebind after reload,
                // and verify the persisted scene again rather than trusting the in-memory reference.
                if (document.panelSettings == null)
                {
                    if (scene.isDirty && !openedHere)
                        throw new InvalidOperationException("Save your preview changes before repairing its panel reference.");
                    var serialized = new SerializedObject(document);
                    serialized.FindProperty("m_PanelSettings").objectReferenceValue = panel;
                    serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(document);
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the preview panel.");
                    if (openedHere)
                    {
                        scene = Reload(scene);
                        document = scene.GetRootGameObjects()[0].GetComponent<UIDocument>();
                    }
                }
                if (document.panelSettings != panel)
                    throw new InvalidOperationException("The saved flow preview references are incomplete.");
            }
            finally
            {
                if (openedHere && scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
            Debug.Log("OMC_FLOW_PREVIEW_READY " + ScenePath);
        }

        private static Scene Reload(Scene scene)
        {
            if (!EditorSceneManager.CloseScene(scene, true))
                throw new InvalidOperationException("Could not reload the saved preview scene.");
            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }
    }
}
