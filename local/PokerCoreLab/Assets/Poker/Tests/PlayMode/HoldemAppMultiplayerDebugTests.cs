using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemAppPlayModeTests
    {
        private readonly List<HoldemMultiplayerConnection> debugGuests = new List<HoldemMultiplayerConnection>();
        private HoldemRemoteTablePort[] DebugPorts => new[] { Multiplayer.Connection.Remote }
            .Concat(debugGuests.Select(g => g.Remote)).ToArray();

        private IEnumerator WaitDebug(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                foreach (var guest in debugGuests) guest.Poll();
                if (condition()) yield break;
                yield return null;
            }
            Assert.That(condition(), Is.True, "Timed out waiting for multiplayer debug state.");
        }

        private IEnumerator StartMultiplayerDebug(int seats)
        {
            Root.Q<Foldout>("omc-menu-development").value = true;
            yield return Click("omc-menu-accusation-test");
            Assert.That(Multiplayer.EnableAccusationDebug, Is.True);
            Root.Q<TextField>("omc-room-name").value = "방장";
            Root.Q<TextField>("omc-room-port").value = "0";
            Root.Q<DropdownField>("omc-room-capacity").index = seats - 3;
            yield return Click("omc-room-host");
            yield return WaitDebug(() => Multiplayer.Connection.Remote?.Lobby != null);
            for (int i = 1; i < seats; i++)
            {
                var guest = new HoldemMultiplayerConnection(); debugGuests.Add(guest);
                Assert.That(guest.Join("참가자" + (i + 1), "127.0.0.1", Multiplayer.Connection.Port, false), Is.True);
            }
            yield return WaitDebug(() => DebugPorts.All(p => p?.Lobby?.MemberCount == seats && p.CanSend));
            Assert.That(DebugPorts.All(p => p.RoomRules.AccusationsEnabled), Is.True);
            Assert.That(Root.Q<Label>("omc-room-rules").text, Does.Contain("실제 AI·고발 정산 없음"));
            foreach (var guest in debugGuests)
            {
                Assert.That(guest.DealerTurns, Is.Null); Assert.That(guest.Accusations, Is.Null);
                Assert.That(guest.DealerCoordinator, Is.Null); guest.Remote.SetReady(true);
            }
            yield return Click("omc-room-ready");
            yield return WaitDebug(() => DebugPorts.All(p => p.CanSend && p.Lobby.AllReady));
            yield return Click("omc-room-start");
            yield return WaitDebug(() => DebugPorts.All(p => p.HasGame && p.CanSend));
            yield return null;
            Assert.That(Root.Q("omc-multiplayer-dealer-debug"), Is.Not.Null);
            Assert.That(Root.Q<Label>(className: "omc-subtitle").text, Does.Contain("실제 AI·고발 정산 없음"));
            Assert.That(multi.enableFlowPreview, Is.False);
            Assert.That(multi.CreateConfig().AccusationMode, Is.EqualTo(HoldemAccusationMode.Disabled));
            Assert.That(multi.utteranceVisibility, Is.EqualTo(HoldemUtteranceVisibility.OwnOnly));
        }

        private IEnumerator DebugSpeech(int seat)
        {
            var remote = DebugPorts.Single(p => p.Read().ViewerSeat.Value == seat);
            yield return WaitDebug(() => remote.Utterances?.CanInteract == true && remote.Utterances.ReadUtterances().CanSubmit);
            if (seat == 1)
            {
                Root.Q<TextField>("omc-utterance-input").value = "테스트 멘트 1";
                yield return Click("omc-utterance-send");
            }
            else
            {
                var s = remote.Utterances.ReadUtterances();
                remote.Utterances.SendOrRetry(new HoldemUtteranceCommand(s.SessionId, s.HandId, s.WindowId,
                    Guid.NewGuid(), s.Street, s.ViewerSeat, "테스트 멘트 " + seat), out _);
            }
            yield return WaitDebug(() => !remote.HasPendingUtterance && remote.Utterances.ReadUtterances().Count > 0);
            yield return WaitDebug(() => DebugPorts.All(p => p.ReadPublicUtterances()?.Count >= 1));
        }

        private IEnumerator DebugBettingToDeal()
        {
            for (int n = 0; n < 30 && !DebugPorts[0].Read().IsDealPending; n++)
            {
                yield return WaitDebug(() => DebugPorts.All(p => p.CanSend));
                var acting = DebugPorts.Single(p => p.Read().CurrentSeat == p.Read().ViewerSeat);
                var before = acting.Read();
                if (before.ViewerSeat.Value == 1)
                {
                    yield return WaitDebug(() => Root.Q<Button>("omc-passive").enabledInHierarchy);
                    yield return Click("omc-passive");
                }
                else acting.Act(before, before.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
                yield return WaitDebug(() => DebugPorts.All(p => p.CanSend && p.Read().SessionVersion > before.SessionVersion));
            }
            yield return WaitDebug(() => DebugPorts.All(p => p.Read().IsDealPending)
                && Root.Q<Button>("omc-debug-unchanged") != null);
            yield return null; yield return null;
            Assert.That(Root.Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        private IEnumerator DebugAccuse(int seat, int target)
        {
            var port = DebugPorts.Single(p => p.Read().ViewerSeat.Value == seat);
            yield return WaitDebug(() => DebugPorts.All(p => p.CanSend));
            long version = port.Read().SessionVersion;
            if (seat == 1)
            {
                yield return WaitDebug(() => Root.Q<Button>(target == 0 ? "omc-pass-accusation" : "omc-accuse").enabledInHierarchy);
                yield return Click(target == 0 ? "omc-pass-accusation" : "omc-accuse");
            }
            else port.Accuse(port.Read(), target == 0 ? (SeatId?)null : new SeatId(target));
            yield return WaitDebug(() => DebugPorts.All(p => p.CanSend && p.Read().SessionVersion > version));
        }

        [UnityTest]
        public IEnumerator AppMultiplayerDebugSelectedGuestGetsFeedbackAndHostResolvesActualClaims()
        {
            texture.Release(); texture.width = 960; texture.height = 640; texture.Create();
            yield return null; yield return null;
            yield return StartMultiplayerDebug(4);
            yield return DebugSpeech(1); yield return DebugSpeech(2);
            yield return DebugBettingToDeal();
            Root.Q<DropdownField>("omc-debug-source").index = 1;
            yield return null; yield return null;
            Capture("app-multiplayer-dealer-pending-compact"); AssertDebugLayout();
            long version = DebugPorts[0].Read().SessionVersion;
            var oldChange = Root.Q<Button>("omc-debug-change");
            yield return Click("omc-debug-change"); Submit(oldChange);
            yield return WaitDebug(() => DebugPorts.All(p => p.Read().IsRevealPending));
            Assert.That(DebugPorts.All(p => p.Read().SessionVersion == version + 1), Is.True);
            foreach (var p in DebugPorts)
                Assert.That(p.Read().OwnCardChange != null, Is.EqualTo(p.Read().ViewerSeat.Value == 2));
            Assert.That(Multiplayer.Connection.ReadPendingUtterances(), Is.Empty, "Consumed batch must not accumulate.");
            var before = DebugPorts.Select(p => p.Read().OwnStack).ToArray();
            long pot = DebugPorts[0].Read().PotAmount;
            yield return DebugAccuse(1, 2); yield return DebugAccuse(2, 0);
            yield return DebugAccuse(3, 1); yield return DebugAccuse(4, 0);
            yield return WaitDebug(() => Root.Q<Button>("omc-debug-close-accusations").enabledInHierarchy);
            yield return Click("omc-debug-close-accusations");
            yield return WaitDebug(() => DebugPorts.All(p => p.Read().Accusations.Phase == HoldemAccusationPhase.AwaitingVerdicts)
                && Root.Q<Button>("omc-debug-resolve-accusations") != null);
            yield return Click("omc-debug-resolve-accusations");
            yield return WaitDebug(() => DebugPorts.All(p => p.Read().Accusations.Phase == HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(DebugPorts.Select(p => p.Read().Accusations.OwnVerdict).ToArray(), Is.EqualTo(new bool?[] { true, null, false, null }));
            Assert.That(DebugPorts.Select(p => p.Read().OwnStack).ToArray(), Is.EqualTo(before));
            Assert.That(DebugPorts.All(p => p.Read().PotAmount == pot && p.Read().IsRevealPending && p.Read().LegalActions == null), Is.True);
            yield return null; yield return null; Capture("app-multiplayer-accusation-verdict"); AssertDebugLayout();
            // A real socket disconnect/rebind retains the same private attribution and verdict.
            ((HoldemTcpClient)typeof(HoldemMultiplayerConnection).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(debugGuests[0])).Dispose();
            yield return WaitDebug(() => DebugPorts[0].Lobby.Paused);
            debugGuests[0].Remote.Refresh();
            yield return WaitDebug(() => DebugPorts.All(p => p.CanSend));
            Assert.That(DebugPorts[1].Read().OwnCardChange, Is.Not.Null);
            Assert.That(DebugPorts[1].Read().Accusations.OwnVerdict, Is.Null);
            Submit(oldChange); yield return null;
            Assert.That(DebugPorts.All(p => p.Read().PotAmount == pot), Is.True);
        }

        [UnityTest]
        public IEnumerator AppMultiplayerDebugFinishedHandsDoNotExhaustSpeechBacklog()
        {
            yield return StartMultiplayerDebug(3);
            for (int hand = 0; hand < 18; hand++)
            {
                // This checks retained batches, not transport pressure. Respect the production
                // 10 inputs/second guard while still crossing its 16-window backlog limit.
                yield return new WaitForSecondsRealtime(.6f);
                yield return DebugSpeech(1);
                while (DebugPorts[0].Read().Result == null)
                {
                    yield return WaitDebug(() => DebugPorts.All(p => p.CanSend));
                    var acting = DebugPorts.Single(p => p.Read().CurrentSeat == p.Read().ViewerSeat);
                    var basis = acting.Read(); acting.Act(basis, BettingAction.Fold());
                    yield return WaitDebug(() => DebugPorts.All(p => p.CanSend && p.Read().SessionVersion > basis.SessionVersion));
                }
                yield return WaitDebug(() => Multiplayer.Connection.ReadPendingUtterances().Count == 0);
                Assert.That(DebugPorts.All(p => !p.Read().IsOver && p.Read().CanContinue), Is.True);
                long number = DebugPorts[0].Read().HandNumber;
                DebugPorts[0].NextHand(DebugPorts[0].Read());
                yield return WaitDebug(() => DebugPorts.All(p => p.CanSend && p.Read().HandNumber == number + 1));
                Assert.That(DebugPorts[0].Utterances.ReadUtterances().IsBacklogged, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator AppMultiplayerDebugPendingDealSurvivesGuestReconnect()
        {
            yield return StartMultiplayerDebug(3);
            yield return DebugSpeech(2);
            yield return DebugBettingToDeal();
            var pending = Multiplayer.Connection.DealerTurns.ReadPendingTurn().Turn;
            var changeButton = Root.Q<Button>("omc-debug-change");
            long version = DebugPorts[0].Read().SessionVersion;
            Assert.That(Multiplayer.Connection.ReadPendingUtterances().Count, Is.EqualTo(1));

            ((HoldemTcpClient)typeof(HoldemMultiplayerConnection).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(debugGuests[0])).Dispose();
            yield return WaitDebug(() => DebugPorts[0].Lobby.Paused && !changeButton.enabledInHierarchy);
            Submit(changeButton); yield return null;
            Assert.That(DebugPorts[0].Read().SessionVersion, Is.EqualTo(version));
            Assert.That(Multiplayer.Connection.ReadPendingUtterances().Count, Is.EqualTo(1));

            debugGuests[0].Remote.Refresh();
            yield return WaitDebug(() => DebugPorts.All(p => p.CanSend) && changeButton.enabledInHierarchy);
            Assert.That(Multiplayer.Connection.DealerTurns.ReadPendingTurn().Turn.TargetDeal.WindowId,
                Is.EqualTo(pending.TargetDeal.WindowId));
            yield return Click("omc-debug-change");
            yield return WaitDebug(() => DebugPorts.All(p => p.Read().IsRevealPending));
            Assert.That(DebugPorts.All(p => p.Read().SessionVersion == version + 1), Is.True);
            Assert.That(DebugPorts.Select(p => p.Read().OwnCardChange != null).ToArray(),
                Is.EqualTo(new[] { false, true, false }));
            Assert.That(Multiplayer.Connection.ReadPendingUtterances(), Is.Empty);
            Submit(changeButton); yield return null; yield return null;
            Assert.That(DebugPorts[0].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(Multiplayer.Connection.ReadPendingUtterances(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator AppMultiplayerDebugFailureTimeoutPassModalAndNormalModeIsolation()
        {
            yield return StartMultiplayerDebug(3);
            Button stale = null;
            for (int round = 0; round < 3; round++)
            {
                if (round < 2) yield return DebugSpeech(1);
                yield return DebugBettingToDeal();
                long version = DebugPorts[0].Read().SessionVersion;
                if (stale != null) { Submit(stale); yield return null; Assert.That(DebugPorts[0].Read().SessionVersion, Is.EqualTo(version)); }
                stale = Root.Q<Button>("omc-debug-timeout");
                yield return Click("omc-help"); Submit(stale); yield return null;
                Assert.That(DebugPorts[0].Read().SessionVersion, Is.EqualTo(version));
                yield return Click("omc-close-help");
                if (round == 2) Assert.That(Root.Q<Button>("omc-debug-change").enabledInHierarchy, Is.False);
                yield return Click(round == 1 ? "omc-debug-timeout" : "omc-debug-unchanged");
                yield return WaitDebug(() => DebugPorts.All(p => p.Read().IsRevealPending));
                Assert.That(DebugPorts.All(p => p.Read().OwnCardChange == null), Is.True);
                Assert.That(Multiplayer.Connection.ReadPendingUtterances(), Is.Empty);
                for (int seat = 1; seat <= 3; seat++) yield return DebugAccuse(seat, 0);
                yield return WaitDebug(() => Root.Q<Button>("omc-debug-close-accusations").enabledInHierarchy);
                yield return Click("omc-debug-close-accusations");
                yield return WaitDebug(() => DebugPorts.All(p => p.Read().Accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims)
                    && Root.Q<Button>("omc-continue-reveal").enabledInHierarchy);
                yield return Click("omc-continue-reveal");
                yield return WaitDebug(() => DebugPorts.All(p => !p.Read().IsRevealPending && p.CanSend));
            }
            yield return Click("omc-room-leave"); yield return Click("omc-room-confirm-leave");
            yield return Click("omc-room-menu");
            yield return Click("omc-menu-multiplayer"); Submit(stale);
            Assert.That(Multiplayer.EnableAccusationDebug, Is.False);
            Assert.That(Root.Q("omc-multiplayer-dealer-debug"), Is.Null);
            Assert.That(multi.CreateConfig().RevealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
            Assert.That(multi.CreateUtterancePolicy(), Is.Null);
            Root.Q<TextField>("omc-room-name").value = "일반 방";
            Root.Q<TextField>("omc-room-port").value = "0";
            yield return Click("omc-room-host");
            yield return WaitDebug(() => Multiplayer.Connection.Remote?.Lobby != null);
            Assert.That(Multiplayer.Connection.Remote.RoomRules.AccusationsEnabled, Is.False);
        }
    }
}
