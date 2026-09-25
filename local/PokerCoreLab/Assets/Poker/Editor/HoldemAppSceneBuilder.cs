using System;
using System.IO;
using Poker.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Editor
{
    public static class HoldemAppSceneBuilder
    {
        public const string ScenePath = "Assets/Poker/Samples/OneMoreCard.unity";
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Create outside Play mode.");
            HoldemSceneBuilder.Create();
            HoldemMultiplayerSceneBuilder.Create();
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var app = new GameObject("One More Card").AddComponent<HoldemAppBootstrap>();
                app.Panel = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/Poker/Samples/HoldemPanel.asset");
                app.SoloSettings = AssetDatabase.LoadAssetAtPath<HoldemTableSettings>("Assets/Poker/Samples/HoldemSettings.asset");
                app.MultiplayerSettings = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>(HoldemMultiplayerSceneBuilder.SettingsPath);
                EditorUtility.SetDirty(app);
                if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Could not save start menu scene.");
            }
            var saved = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var roots = saved.GetRootGameObjects();
            var bootstrap = roots.Length == 1 ? roots[0].GetComponent<HoldemAppBootstrap>() : null;
            if (bootstrap == null || bootstrap.Panel == null || bootstrap.Panel.themeStyleSheet == null
                || bootstrap.SoloSettings == null || bootstrap.MultiplayerSettings == null
                || Resources.Load<StyleSheet>("HoldemMenu") == null)
                throw new InvalidOperationException("Saved start menu scene is incomplete.");
            bootstrap.SoloSettings.CreateConfig(); bootstrap.MultiplayerSettings.CreateConfig();
            Debug.Log("OMC_APP_SCENE_READY");
        }
    }
}
