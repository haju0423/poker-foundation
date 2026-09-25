#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using Poker.Foundation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed class HoldemMultiplayerSavedSceneTests
    {
        private Scene scene;
        [UnityTest]
        public IEnumerator SavedSceneStartsInLobbyWithoutOpeningAServerOrCreatingNpcs()
        {
            scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Poker/Samples/HoldemMultiplayer.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null;
            var roots = scene.GetRootGameObjects(); Assert.That(roots.Length, Is.EqualTo(1));
            var boot = roots[0].GetComponent<HoldemMultiplayerBootstrap>();
            Assert.That(boot, Is.Not.Null); Assert.That(boot.Settings, Is.Not.Null);
            Assert.That(boot.Connection.HasSession, Is.False); Assert.That(boot.Connection.IsHosting, Is.False);
            Assert.That(roots[0].GetComponent<HoldemTableBootstrap>(), Is.Null);
            Assert.That(boot.Settings.CreateUtterancePolicy(), Is.Not.Null,
                "The regular multiplayer scene must include the blueprint's public remarks.");
            Assert.That(boot.Settings.CreateUtterancePolicy().Visibility, Is.EqualTo(Poker.Application.HoldemUtteranceVisibility.PublicRaw));
            Assert.That(boot.Settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.Automatic));
            Assert.That(boot.Settings.CreateConfig().RevealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
            var document = roots[0].GetComponent<UIDocument>();
            Assert.That(document.panelSettings.themeStyleSheet, Is.Not.Null);
            Assert.That(document.rootVisualElement.Q<Button>("omc-room-host"), Is.Not.Null);
            Assert.That(document.rootVisualElement.Q<Button>("omc-passive"), Is.Null);
        }
        [UnityTest]
        public IEnumerator SavedFlowPreviewIsSeparateAndStartsWithoutNetworkActivity()
        {
            scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Poker/Samples/HoldemMultiplayerFlowPreview.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null;
            var roots = scene.GetRootGameObjects(); Assert.That(roots.Length, Is.EqualTo(1));
            var boot = roots[0].GetComponent<HoldemMultiplayerBootstrap>();
            Assert.That(boot.Settings.enableFlowPreview, Is.True);
            Assert.That(boot.Settings.CreateUtterancePolicy(), Is.Not.Null);
            Assert.That(boot.Settings.CreateUtterancePolicy().Visibility, Is.EqualTo(Poker.Application.HoldemUtteranceVisibility.PublicRaw));
            Assert.That(boot.Settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.WaitForHost));
            Assert.That(boot.Settings.CreateConfig().RevealPolicy, Is.EqualTo(HoldemRevealPolicy.PauseAfterCommunityReveal));
            Assert.That(boot.Settings.CreateConfig().AccusationMode, Is.EqualTo(HoldemAccusationMode.Disabled));
            Assert.That(boot.Connection.HasSession, Is.False);
            Assert.That(boot.Connection.IsHosting, Is.False);
            var root = roots[0].GetComponent<UIDocument>().rootVisualElement;
            Assert.That(root.Q<Label>("omc-flow-preview-notice"), Is.Not.Null);
            Assert.That(root.Q<Button>("omc-room-host"), Is.Not.Null);
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        { if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene); }
    }
}
#endif
