using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Opt-in headless packaged-player check. Drives the same UI, not a second game session.</summary>
    internal sealed class HoldemSoloAccusationProcessCheck : MonoBehaviour
    {
        private HoldemTableBootstrap owner;
        private int step;
        private double deadline, nextAction;
        private bool finished;

        internal static void AttachIfRequested(HoldemTableBootstrap owner)
        {
            if (!UnityEngine.Application.isBatchMode || !owner.EnableDealerDebug
                || Array.IndexOf(Environment.GetCommandLineArgs(), "-omc-solo-accusation-check") < 0) return;
            if (owner.GetComponent<HoldemSoloAccusationProcessCheck>() != null) return;
            var check = owner.gameObject.AddComponent<HoldemSoloAccusationProcessCheck>();
            check.owner = owner; check.deadline = Time.realtimeSinceStartupAsDouble + 45;
        }

        private void Update()
        {
            if (finished) return;
            try
            {
                if (Time.realtimeSinceStartupAsDouble > deadline) throw new InvalidOperationException("Timed out at step " + step);
                if (Time.realtimeSinceStartupAsDouble < nextAction) return;
                var root = owner.GetComponent<UIDocument>().rootVisualElement;
                switch (step)
                {
                    case 0:
                        if (Press(root, "omc-debug-npc-speech")) step++;
                        break;
                    case 1:
                        if (owner.Progress.IsDealPending) step++;
                        else if (owner.Progress.OwnTurn) Press(root, "omc-passive");
                        break;
                    case 2:
                        var source = root.Q<DropdownField>("omc-debug-source");
                        if (source == null) break;
                        Require(source.choices.Count == owner.Progress.SeatCount - 1, "Missing NPC speech sources");
                        source.index = 0;
                        if (Press(root, "omc-dealer-debug-change")) step++;
                        break;
                    case 3:
                        if (!owner.Progress.IsRevealPending) break;
                        Require(root.Q<Label>("omc-own-card-change") == null, "NPC success leaked to human");
                        var card = root.Q("omc-board")[0];
                        Require(card.Q<Label>(className: "omc-rank").text == "K"
                            && card.Q<Label>(className: "omc-suit").text == "♦", "Actual card was not changed");
                        Require(root.Q<Button>("omc-public-utterance-2").text.Contains("테스트 멘트"), "NPC raw speech absent");
                        root.Q<DropdownField>("omc-accusation-target").index = 0;
                        if (Press(root, "omc-accuse")) step++;
                        break;
                    case 4:
                        if (Press(root, "omc-debug-resolve-accusations")) step++;
                        break;
                    case 5:
                        Require(root.Q<Label>(className: "omc-prompt").text.Contains("내 고발 적중"), "Recorded verdict absent");
                        Require(root.Q<Label>("omc-own-card-change") == null, "Owner feedback leaked after verdict");
                        Require(root.Q<Button>("omc-continue-reveal").resolvedStyle.display == DisplayStyle.None,
                            "Unapproved settlement resumed play");
                        finished = true;
                        Debug.Log("OMC_SOLO_ACCUSATION_OK seats=" + owner.Progress.SeatCount
                            + " street=" + owner.Progress.Street + " changed=KD ownerBadge=false verdict=true");
                        UnityEngine.Application.Quit(0);
                        break;
                }
            }
            catch (Exception error)
            {
                finished = true;
                Debug.LogError("OMC_SOLO_ACCUSATION_FAILED step=" + step + " " + error.Message);
                UnityEngine.Application.Quit(1);
            }
        }

        private bool Press(VisualElement root, string name)
        {
            var button = root.Q<Button>(name);
            if (button == null || !button.enabledInHierarchy || button.resolvedStyle.display == DisplayStyle.None) return false;
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
            nextAction = Time.realtimeSinceStartupAsDouble + .25;
            return true;
        }

        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); }
    }
}
