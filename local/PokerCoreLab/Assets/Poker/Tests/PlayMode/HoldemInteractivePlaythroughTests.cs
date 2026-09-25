#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    /// <summary>
    /// Opt-in, operator-driven playthrough of the saved scene. Only visible UI is exported;
    /// moves use UI events, never the table authority, private cards, or a replacement NPC policy.
    /// </summary>
    public sealed class HoldemInteractivePlaythroughTests
    {
        private Scene scene;
        private UIDocument document;
        private HoldemTableBootstrap bootstrap;
        private RenderTexture texture, originalTarget;
        private string directory;
        private VisualElement Root => document.rootVisualElement;

        [UnityTest, Timeout(1000000), Explicit("Requires OMC_PLAYTHROUGH_DIR and a local operator supplying numbered commands.")]
        public IEnumerator PlaySavedSceneThroughVisibleControls()
        {
            directory = Environment.GetEnvironmentVariable("OMC_PLAYTHROUGH_DIR");
            Assert.That(directory, Is.Not.Null.And.Not.Empty, "An output directory must be supplied explicitly.");
            Assert.That(Path.IsPathRooted(directory), Is.True);
            Directory.CreateDirectory(directory);
            Assert.That(Directory.EnumerateFileSystemEntries(directory).Any(), Is.False,
                "Use an empty directory; do not replay stale commands or overwrite a previous playthrough.");
            string mode = Environment.GetEnvironmentVariable("OMC_PLAYTHROUGH_SCENE") ?? "normal";
            Assert.That(mode, Is.EqualTo("normal").Or.EqualTo("flow"));
            scene = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
                mode == "flow" ? "Assets/Poker/Samples/HoldemFlowPreview.unity" : "Assets/Poker/Samples/HoldemTable.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;
            document = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UIDocument>()).Single();
            bootstrap = document.GetComponent<HoldemTableBootstrap>();
            originalTarget = document.panelSettings.targetTexture;
            Resize(1200, 800);
            yield return SettleUi();
            Capture(0, "ready");

            int sequence = 1, interactions = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 900;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                string path = Path.Combine(directory, sequence.ToString("D3") + "-command.json");
                // The operator writes JSON first, then a separate ready marker. File existence alone
                // is not a completed write and could expose an empty/partially written command.
                string ready = Path.Combine(directory, sequence.ToString("D3") + "-command.ready");
                if (!File.Exists(ready)) { yield return null; continue; }
                Assert.That(File.Exists(path), Is.True, "Ready marker has no command.");
                // Commands are small, immutable files created one at a time by the operator.
                Assert.That(new FileInfo(path).Length, Is.LessThanOrEqualTo(16384));
                var command = JsonUtility.FromJson<Command>(File.ReadAllText(path));
                Assert.That(command, Is.Not.Null);
                if (command.action == "stop")
                {
                    Assert.That(interactions, Is.GreaterThan(0), "A playthrough must include a UI interaction.");
                    Capture(sequence, "stopped");
                    yield break;
                }
                Apply(command);
                if (command.action == "click" || command.action == "text" || command.action == "choose" || command.action == "toggle")
                    interactions++;
                yield return SettleUi();
                Capture(sequence, command.action);
                sequence++;
            }
            Assert.Fail("Playthrough timed out before an explicit stop command.");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (document != null && document.panelSettings != null)
                document.panelSettings.targetTexture = originalTarget;
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (var go in scene.GetRootGameObjects()) go.SetActive(false);
                yield return SceneManager.UnloadSceneAsync(scene);
            }
            if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); }
        }

        private IEnumerator SettleUi()
        {
            yield return new WaitForSecondsRealtime(0.4f);
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            bool settled = false;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                var progress = bootstrap.Progress;
                if (progress.OwnTurn || progress.IsDealPending || progress.IsRevealPending
                    || progress.Street == Foundation.HoldemStreet.Complete
                    || Root.Query(className: "omc-overlay").ToList().Any(IsVisible)
                    || IsReadyButton("omc-resolve") || IsReadyButton("omc-retry"))
                { settled = true; break; }
                yield return null;
            }
            Assert.That(settled, Is.True, "Game did not reach a player decision or a visible result within 12 seconds.");
            for (int i = 0; i < 4; i++) yield return null;
        }

        private bool IsReadyButton(string name)
        { var button = Root.Q<Button>(name); return button != null && IsExposed(button) && button.enabledInHierarchy; }

        private void Apply(Command command)
        {
            switch (command.action)
            {
                case "click": Click(Find<Button>(command.control)); break;
                case "text":
                    var text = Find<TextField>(command.control);
                    Assert.That(text.isReadOnly, Is.False);
                    text.value = command.value ?? "";
                    break;
                case "choose":
                    var choice = Find<DropdownField>(command.control);
                    Assert.That(command.index, Is.InRange(0, choice.choices.Count - 1));
                    choice.index = command.index;
                    break;
                case "toggle": Find<Toggle>(command.control).value = command.flag; break;
                case "resize":
                    Assert.That(command.width, Is.InRange(960, 1920));
                    Assert.That(command.height, Is.InRange(640, 1080));
                    Resize(command.width, command.height);
                    break;
                case "wait": break;
                default: Assert.Fail("Unknown playthrough command: " + command.action); break;
            }
        }

        private T Find<T>(string name) where T : VisualElement
        {
            Assert.That(name, Does.StartWith("omc-"));
            T control = Root.Q<T>(name);
            Assert.That(control, Is.Not.Null, name);
            Assert.That(IsExposed(control), Is.True, name + " is hidden or clipped.");
            Assert.That(control.enabledInHierarchy, Is.True, name + " is disabled.");
            Assert.That(IsDescendant(Root.panel.Pick(control.worldBound.center), control), Is.True, name + " is occluded.");
            return control;
        }

        private void Click(Button button)
        {
            Vector2 point = button.worldBound.center;
            var picked = Root.panel.Pick(point);
            Assert.That(IsDescendant(picked, button), Is.True, button.name + " is occluded by " + picked?.name);
            using (var down = PointerDownEvent.GetPooled(new Event
                { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { down.target = picked; picked.SendEvent(down); }
            using (var up = PointerUpEvent.GetPooled(new Event
                { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { up.target = picked; picked.SendEvent(up); }
        }

        private void Resize(int width, int height)
        {
            var old = texture;
            texture = new RenderTexture(width, height, 0);
            texture.Create();
            document.panelSettings.targetTexture = texture;
            if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
        }

        private static bool IsVisible(VisualElement element)
        {
            if (element.panel == null || element.worldBound.width <= 0 || element.worldBound.height <= 0) return false;
            for (var current = element; current != null; current = current.parent)
                if (current.resolvedStyle.display == DisplayStyle.None || current.resolvedStyle.visibility == Visibility.Hidden
                    || current.resolvedStyle.opacity <= 0) return false;
            return true;
        }

        private static bool IsDescendant(VisualElement element, VisualElement ancestor)
        {
            for (var current = element; current != null; current = current.parent)
                if (current == ancestor) return true;
            return false;
        }

        private bool IsExposed(VisualElement element)
        {
            if (!IsVisible(element)) return false;
            var modal = Root.Query(className: "omc-overlay").ToList().LastOrDefault(IsVisible);
            if (modal != null && !IsDescendant(element, modal)) return false;
            var bounds = element.worldBound;
            // Do not export text outside a scroll viewport. The image remains the source for
            // partially clipped content; no off-screen text is used to choose a move.
            for (var ancestor = element.parent; ancestor != null; ancestor = ancestor.parent)
                if (ancestor == Root || ancestor is ScrollView)
                {
                    var clip = ancestor is ScrollView scroll ? scroll.contentViewport.worldBound : ancestor.worldBound;
                    if (bounds.xMin < clip.xMin - 1 || bounds.yMin < clip.yMin - 1
                        || bounds.xMax > clip.xMax + 1 || bounds.yMax > clip.yMax + 1) return false;
                }
            return true;
        }

        private void Capture(int sequence, string status)
        {
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            string prefix = Path.Combine(directory, sequence.ToString("D3"));
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                Assert.That(image.GetPixels32().Distinct().Count(), Is.GreaterThan(100), "The real UI must be rendered.");
                File.WriteAllBytes(prefix + ".png", image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }

            var controls = new List<VisibleControl>();
            foreach (var element in Root.Query<VisualElement>().ToList().Where(IsExposed))
            {
                if (element is Button button)
                    controls.Add(new VisibleControl("button", button, button.text));
                else if (element is TextField text)
                    controls.Add(new VisibleControl("text", text, text.value));
                else if (element is DropdownField choice)
                    controls.Add(new VisibleControl("choice", choice, choice.value) { choices = choice.choices.ToArray() });
                else if (element is Toggle toggle)
                    controls.Add(new VisibleControl("toggle", toggle, toggle.value.ToString()));
                else if (element is Label label && !string.IsNullOrWhiteSpace(label.text))
                    controls.Add(new VisibleControl("label", label, label.text));
            }
            var progress = bootstrap.Progress;
            var state = new VisibleState
            {
                sequence = sequence, status = status, width = texture.width, height = texture.height,
                hand = progress.HandNumber, street = progress.Street.ToString(), ownTurn = progress.OwnTurn,
                controls = controls.ToArray()
            };
            // Written last: appearance of the state file is the receipt for a completed capture.
            File.WriteAllText(prefix + "-state.json", JsonUtility.ToJson(state, true));
        }

        [Serializable]
        private sealed class Command
        {
            public string action, control, value;
            public int index, width, height;
            public bool flag;
        }

        [Serializable]
        private sealed class VisibleState
        {
            public int sequence, width, height;
            public long hand;
            public string status, street;
            public bool ownTurn;
            public VisibleControl[] controls;
        }

        [Serializable]
        private sealed class VisibleControl
        {
            public string type, name, value;
            public bool enabled;
            public float x, y, width, height;
            public string[] choices;
            public VisibleControl(string type, VisualElement control, string value)
            {
                this.type = type; name = control.name; this.value = value; enabled = control.enabledInHierarchy;
                var bounds = control.worldBound; x = bounds.x; y = bounds.y; width = bounds.width; height = bounds.height;
            }
        }
    }
}
#endif
