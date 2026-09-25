using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Owns one mode at a time. Poker state remains in the existing mode controllers.</summary>
    public sealed class HoldemAppBootstrap : MonoBehaviour
    {
        [SerializeField] private PanelSettings panel;
        [SerializeField] private HoldemTableSettings soloSettings;
        [SerializeField] private HoldemMultiplayerSettings multiplayerSettings;
        private GameObject menuObject, modeObject;
        private UIDocument menuDocument;
        private int menuGeneration;

        public PanelSettings Panel { get => panel; set => panel = value; }
        public HoldemTableSettings SoloSettings { get => soloSettings; set => soloSettings = value; }
        public HoldemMultiplayerSettings MultiplayerSettings { get => multiplayerSettings; set => multiplayerSettings = value; }

        private void OnEnable()
        {
            if (panel == null || soloSettings == null || multiplayerSettings == null
                || Resources.Load<Font>("Fonts/NanumGothic-Regular") == null
                || Resources.Load<StyleSheet>("HoldemMenu") == null)
                throw new InvalidOperationException("The start menu resources are incomplete.");
            soloSettings.CreateConfig(); multiplayerSettings.CreateConfig(); multiplayerSettings.CreateUtterancePolicy();
            if (!UnityEngine.Application.isBatchMode && Screen.fullScreenMode != FullScreenMode.Windowed)
                Screen.fullScreenMode = FullScreenMode.Windowed;
            menuObject = CreateSurface("Start menu", out menuDocument);
            menuObject.SetActive(true);
            ShowMenu();
#if !UNITY_EDITOR
            // Explicit process checks bypass the menu only in a headless player.
            if (UnityEngine.Application.isBatchMode)
            {
                string requested = Argument("-omc-start-mode");
                if (HoldemMultiplayerProcessCheck.IsRequested || requested == "multiplayer") StartMode(true, menuGeneration);
                else if (requested == "solo") StartMode(false, menuGeneration);
                else if (Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-check-startup") >= 0)
                { Debug.Log("OMC_APP_STARTUP_OK mode=menu"); UnityEngine.Application.Quit(0); }
            }
#endif
        }

        private GameObject CreateSurface(string label, out UIDocument document)
        {
            var surface = new GameObject(label);
            surface.SetActive(false); surface.transform.SetParent(transform, false);
            document = surface.AddComponent<UIDocument>(); document.panelSettings = panel;
            return surface;
        }

        private void ShowMenu()
        {
            int generation = ++menuGeneration;
            menuObject.SetActive(true);
            var root = menuDocument.rootVisualElement; root.Clear();
            root.AddToClassList("omc-menu");
            root.style.unityFont = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            var sheet = Resources.Load<StyleSheet>("HoldemMenu");
            if (!root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            var card = new VisualElement(); card.AddToClassList("omc-menu-card"); root.Add(card);
            var title = new Label("One More Card"); title.AddToClassList("omc-menu-title"); card.Add(title);
            var subtitle = new Label("텍사스 홀덤"); subtitle.AddToClassList("omc-menu-subtitle"); card.Add(subtitle);
            Choice(card, "혼자 하기", "omc-menu-solo", "NPC와 플레이 · 상대 수와 속도 조절", () => StartMode(false, generation));
            Choice(card, "함께 하기", "omc-menu-multiplayer", "3~4인 · 같은 네트워크에서 방 만들기 / 참가", () => StartMode(true, generation));
        }

        private static void Choice(VisualElement parent, string label, string name, string description, Action action)
        {
            var button = new Button(action) { name = name, text = label }; parent.Add(button);
            var hint = new Label(description); hint.AddToClassList("omc-menu-hint"); parent.Add(hint);
        }

        private void StartMode(bool multiplayer, int generation)
        {
            if (!isActiveAndEnabled || modeObject != null || generation != menuGeneration) return;
            var candidate = CreateSurface(multiplayer ? "Multiplayer" : "Solo", out _);
            if (multiplayer)
            {
                var bootstrap = candidate.AddComponent<HoldemMultiplayerBootstrap>();
                bootstrap.Settings = multiplayerSettings;
                bootstrap.ReturnToMenu = () => ReturnToMenu(candidate);
            }
            else
            {
                var bootstrap = candidate.AddComponent<HoldemTableBootstrap>();
                bootstrap.Settings = soloSettings;
                bootstrap.ReturnToMenu = () => ReturnToMenu(candidate);
            }
            modeObject = candidate;
            ++menuGeneration;
            menuObject.SetActive(false);
            candidate.SetActive(true);
        }

        private void ReturnToMenu(GameObject source)
        {
            if (!isActiveAndEnabled || modeObject == null || modeObject != source) return;
            Release(ref modeObject);
            ShowMenu();
        }

        private static void Release(ref GameObject owned)
        {
            var old = owned; owned = null;
            if (old == null) return;
            // OnDisable disposes the mode now, not at the end-of-frame Destroy boundary.
            old.SetActive(false); Destroy(old);
        }

        private void OnDisable()
        {
            ++menuGeneration;
            Release(ref modeObject); Release(ref menuObject); menuDocument = null;
        }

        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
