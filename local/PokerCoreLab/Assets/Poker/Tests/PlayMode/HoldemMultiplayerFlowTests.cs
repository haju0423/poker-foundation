using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator PublicHistoryUpdatesDuringOtherPlayersActionsAndSurvivesReconnect()
        {
            yield return StartGame(new StableRandom());
            yield return Click(3, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.ReadHistory().Count == 3));
            yield return Click(1, "omc-history");
            Assert.That(roots[1].Q<Label>("omc-history-copy").text, Does.Contain("참가자 4 · 콜"));
            yield return Click(0, "omc-passive");
            yield return Wait(() => roots[1].Q<Label>("omc-history-copy").text.Contains("참가자 1 · 콜"));
            yield return Click(1, "omc-close-history");
            Client(3).Dispose(); yield return Wait(() => Client(0).Latest.paused);
            yield return Click(1, "omc-history");
            Assert.That(roots[1].Q<Label>("omc-history-copy").text, Does.Contain("참가자 1 · 콜"));
            yield return null; yield return null; Capture(1, "history-during-pause");
            yield return Click(1, "omc-close-history");
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
            string shared = JsonUtility.ToJson(Client(0).Latest.history);
            Assert.That(Enumerable.Range(0, 4).All(i => JsonUtility.ToJson(Client(i).Latest.history) == shared), Is.True);
            for (int step = 0; step < 50 && !Client(0).Latest.game.hasResult; step++)
            {
                var game = Client(0).Latest.game; int actor = game.currentSeat - 1;
                yield return Click(actor, "omc-passive");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > game.version));
            }
            Assert.That(boots.All(b => b.Connection.Remote.Read().Result != null), Is.True);
            yield return Click(1, "omc-history"); yield return null; yield return null;
            Capture(1, "history-settled");
            Assert.That(roots[1].Q<Label>("omc-history-copy").text, Does.Contain("팟에서"));
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == 2));
            Assert.That(roots[1].Q("omc-history-dialog").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(boots.All(b => b.Connection.Remote.ReadHistory().Count == 2), Is.True);
            yield return Click(1, "omc-history");
            Assert.That(roots[1].Q<Label>("omc-history-copy").text, Does.Not.Contain("콜").And.Not.Contain("팟에서"));
        }

        [UnityTest]
        public IEnumerator LongNicknamesDoNotCoverCardsOrHidePositionAtSmallSize()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                textures[i].Create(); panels[i].targetTexture = textures[i];
                old.Release(); UnityEngine.Object.Destroy(old);
            }
            var names = new[] { new string('가', 24), new string('나', 24), new string('W', 24), "<b>긴이름입니다</b>" };
            yield return StartGame(new StableRandom(), names);
            yield return null; yield return null;
            Capture(0, "long-names-initial");
            for (int viewer = 0; viewer < 4; viewer++)
            {
                foreach (var seat in roots[viewer].Query<VisualElement>(className: "omc-opponent").ToList())
                {
                    var title = seat.Q<Label>(className: "omc-seat-title");
                    Assert.That(title.enableRichText, Is.False);
                    Assert.That(title.isElided, Is.True, "Long names must not paint over the private card backs.");
                    var position = seat.Q<Label>(className: "omc-seat-position");
                    Assert.That(position, Is.Not.Null, "The position must not be truncated with the name.");
                    Assert.That(position.worldBound.xMax, Is.LessThanOrEqualTo(seat.worldBound.xMax));
                    Assert.That(title.tooltip, Does.Contain(names[int.Parse(seat.name.Substring("omc-seat-".Length)) - 1]));
                }
                AssertFlowLayout(viewer);
            }
            yield return CompleteFlowHand(false, false);
            Capture(0, "long-names-result");
        }

        [UnityTest]
        public IEnumerator MixedExecutablesShowTheHostsRulesRatherThanGuestDefaults()
        {
            foreach (bool hostFlow in new[] { false, true })
            {
                yield return Cleanup(); yield return SetupLobby(!hostFlow);
                for (int i = 0; i < 4; i++)
                {
                    var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                    textures[i].Create(); panels[i].targetTexture = textures[i];
                    old.Release(); UnityEngine.Object.Destroy(old);
                }
                yield return null; yield return null;
                var hostSettings = ScriptableObject.CreateInstance<HoldemMultiplayerSettings>();
                hostSettings.startingStack = 250; hostSettings.smallBlind = 5; hostSettings.bigBlind = 10;
                hostSettings.enableFlowPreview = hostFlow; boots[0].Settings = hostSettings;
                try
                {
                    yield return EnterRoom(); yield return null; yield return null;
                    for (int i = 0; i < 4; i++)
                    {
                        var rules = boots[i].Connection.Remote.Lobby.Rules;
                        Assert.That(rules.StartingStack, Is.EqualTo(250));
                        Assert.That(rules.WaitsForHostDeal, Is.EqualTo(hostFlow));
                        Assert.That(rules.ReceivesUtterances, Is.EqualTo(hostFlow));
                        string copy = roots[i].Q<Label>("omc-room-rules").text;
                        Assert.That(copy, Does.Contain("250칩").And.Contain("5/10"));
                        Assert.That(copy, Does.Contain(hostFlow ? "방장이 공개" : "자동 공개"));
                        Assert.That(copy, Does.Contain(hostFlow ? "접수 확인용" : "사용 안 함"));
                        Assert.That(copy, Does.Not.Contain("100칩"));
                        var notice = roots[i].Q<Label>("omc-flow-preview-notice");
                        if (notice != null) Assert.That(notice.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    }
                    var rulesLabel = roots[1].Q<Label>("omc-room-rules");
                    roots[1].Q<ScrollView>("omc-lobby").ScrollTo(rulesLabel);
                    yield return null; yield return null;
                    Capture(1, hostFlow ? "host-flow-rules" : "host-normal-rules");
                    Assert.That(rulesLabel.worldBound.xMin, Is.GreaterThanOrEqualTo(roots[1].worldBound.xMin));
                    Assert.That(rulesLabel.worldBound.xMax, Is.LessThanOrEqualTo(roots[1].worldBound.xMax));
                    roots[1].Q<ScrollView>("omc-lobby").ScrollTo(roots[1].Q<Button>("omc-room-ready"));
                    yield return null; yield return null;
                    for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
                    yield return Click(0, "omc-room-start");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.HasGame));
                    Assert.That(boots.All(b => (b.Connection.Remote.Utterances != null) == hostFlow), Is.True);
                    Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack) + Client(0).Latest.game.pot, Is.EqualTo(1000));
                }
                finally { UnityEngine.Object.Destroy(hostSettings); }
            }
        }

        [UnityTest]
        public IEnumerator FlowPreviewWithoutSpeechStillCompletesEveryGateAndTheNextHand()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            Assert.That(roots.All(r => r.Q<Label>("omc-flow-preview-notice") != null), Is.True);
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i];
                textures[i] = new RenderTexture(960, 640, 0); textures[i].Create(); panels[i].targetTexture = textures[i];
                old.Release(); UnityEngine.Object.Destroy(old);
            }
            yield return null; yield return null;
            // Use the actual host button to verify that the scene setting reaches the server.
            yield return StartGame();
            yield return null; yield return null;
            Capture(3, "flow-initial-action");
            yield return CompleteFlowHand(false, false);
            Assert.That(boots[0].Connection.ReadPendingUtterances(), Is.Empty);
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == 2));
            yield return null; yield return null;
            for (int i = 0; i < 4; i++) AssertFlowLayout(i);
            Assert.That(boots.All(b => b.Connection.Remote.Utterances.ReadUtterances().Count == 0), Is.True);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack) + Client(0).Latest.game.pot, Is.EqualTo(400));
        }

        [UnityTest]
        public IEnumerator FlowPreviewKeepsRawBatchesSeparateFromDealsAcrossHostReconnectAndNewHand()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            yield return StartGame(new StableRandom());
            yield return CompleteFlowHand(true, true);
            var retained = boots[0].Connection.ReadPendingUtterances().ToArray();
            Assert.That(retained.Length, Is.EqualTo(3));
            Guid priorHand = retained[0].HandId;
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == 2));
            Assert.That(boots.All(b => b.Connection.Remote.Utterances.ReadUtterances().Count == 0), Is.True);
            Assert.That(boots[0].Connection.ReadPendingUtterances(), Is.EqualTo(retained));
            Assert.That(boots[0].Connection.Remote.Read().HandId, Is.Not.EqualTo(priorHand));
            foreach (var batch in retained)
                Assert.That(boots[0].Connection.AcknowledgeUtteranceBatch(batch.WindowId), Is.True);
            Assert.That(boots[0].Connection.ReadPendingUtterances(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator FlowPreviewAllInRetainsAllThreePublicCardGates()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            yield return StartGame(new StableRandom());
            roots[3].Q<TextField>("omc-target").value = "100";
            yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            for (int actor = 0; actor < 3; actor++)
            {
                long version = Client(actor).Latest.game.version;
                yield return Click(actor, "omc-passive");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > version));
            }
            Assert.That(Client(0).Latest.game.seats.All(s => s.stack == 0), Is.True);
            Assert.That(Client(0).Latest.game.hasResult, Is.False, "All-in is not elimination or settlement.");
            yield return CompleteFlowHand(false, false);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
            yield return null; yield return null;
            for (int i = 0; i < 4; i++) AssertFlowLayout(i);
            Capture(0, "flow-result");
        }

        [UnityTest]
        public IEnumerator HostDealerTurnPortConnectsThreeGatesToFourScreensAndExpiresWithTheRoom()
        {
            Assert.That(boots.All(b => b.Connection == null || b.Connection.DealerTurns == null), Is.True);
            yield return Cleanup(); yield return SetupLobby(true);
            yield return StartGame(new StableRandom());
            var hostConnection = boots[0].Connection;
            var port = hostConnection.DealerTurns;
            Assert.That(port, Is.Not.Null);
            Assert.That(boots.Skip(1).All(b => b.Connection.DealerTurns == null), Is.True);
            Assert.That(port.ReadPendingTurn().Turn, Is.Null);
            yield return CompleteFlowHand(true, true, true);
            Assert.That(boots.All(b => b.Connection.Remote.Read().Result != null), Is.True);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
            Assert.That(hostConnection.ReadPendingUtterances().Count, Is.EqualTo(3));
            Assert.That(port.ReadPendingTurn().Turn, Is.Null);
            hostConnection.CloseSession();
            Assert.That(hostConnection.DealerTurns, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => port.ReadPendingTurn());
        }

        [UnityTest]
        public IEnumerator HostDealerCoordinatorReturnsWorkerCompletionToFourScreensAndClosesWithRoom()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            yield return StartGame(new StableRandom());
            var connection = boots[0].Connection;
            var coordinator = connection.DealerCoordinator;
            Assert.That(coordinator, Is.SameAs(connection.DealerCoordinator));
            Assert.That(boots.Skip(1).All(b => b.Connection.DealerCoordinator == null), Is.True);
            yield return CompleteFlowHand(true, true, useCoordinator: true);
            Assert.That(boots.All(b => b.Connection.Remote.Read().Result != null), Is.True);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
            Assert.That(connection.ReadPendingUtterances().Count, Is.EqualTo(3));
            connection.CloseSession();
            Assert.That(connection.DealerCoordinator, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => coordinator.Poll(out _));
        }

        [Test]
        public void DealerCoordinatorIsOwnedByOneRoomAndClosesOnErrorOrSessionExit()
        {
            using var connection = new HoldemMultiplayerConnection();
            Assert.That(connection.DealerCoordinator, Is.Null);
            Assert.That(connection.Host("방장", "127.0.0.1", 0, false, new HoldemConfig(100, 1, 2)), Is.True);
            Task.Run(() => Assert.Throws<InvalidOperationException>(() => {
                var ignored = connection.DealerCoordinator;
            })).GetAwaiter().GetResult();
            var first = connection.DealerCoordinator;
            connection.StopForError();
            Assert.That(connection.DealerCoordinator, Is.Null);
            Assert.Throws<ObjectDisposedException>(() => first.Poll(out _));
            connection.CloseSession();
            Assert.That(connection.Host("새 방", "127.0.0.1", 0, false, new HoldemConfig(100, 1, 2)), Is.True);
            var second = connection.DealerCoordinator;
            Assert.That(second, Is.Not.SameAs(first));
            connection.CloseSession();
            Assert.Throws<ObjectDisposedException>(() => second.Poll(out _));
        }

        private IEnumerator CompleteFlowHand(bool sendSpeech, bool reconnectHost, bool useDealerPort = false,
            bool useCoordinator = false)
        {
            int dealCount = 0, revealCount = 0, lastSentStreet = -1;
            for (int step = 0; step < 80 && !Client(0).Latest.game.hasResult; step++)
            {
                yield return null; yield return null;
                for (int i = 0; i < 4; i++) AssertFlowLayout(i);
                var game = Client(0).Latest.game;
                if (game.dealPending)
                {
                    var batches = boots[0].Connection.ReadPendingUtterances();
                    Assert.That(batches.Count, Is.EqualTo(sendSpeech ? dealCount + 1 : 0));
                    if (sendSpeech)
                    {
                        var batch = batches[dealCount];
                        Assert.That(batch.HandId.ToString("N"), Is.EqualTo(game.handId));
                        Assert.That((int)batch.Street + 1, Is.EqualTo(game.dealStreet));
                        Assert.That(batch.WindowId.ToString("N"), Is.Not.EqualTo(game.dealWindowId));
                        Assert.That(batch.Count, Is.EqualTo(4));
                        Assert.That(Enumerable.Range(0, 4).Select(i => batch.GetEntry(i).Speaker.Value).Distinct().Count(), Is.EqualTo(4));
                    }
                    int oldCards = game.board.Length;
                    Assert.That(boots.All(b => b.Connection.Remote.Read().LegalActions == null), Is.True);
                    for (int i = 1; i < 4; i++)
                    {
                        Assert.That(roots[i].Q<Button>("omc-release-deal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                        Assert.That(roots[i].Q<Label>(className: "omc-prompt").text, Does.Contain("방장이 다음 공용 카드"));
                    }
                    if (reconnectHost && dealCount == 0)
                    {
                        string window = game.dealWindowId;
                        Client(0).Dispose(); yield return Wait(() => Client(1).Latest.paused);
                        yield return Click(0, "omc-room-reconnect");
                        yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
                        Assert.That(Client(0).Latest.game.dealWindowId, Is.EqualTo(window));
                        Assert.That(Client(0).Latest.game.board.Length, Is.EqualTo(oldCards));
                    }
                    yield return Wait(() => Pickable(0, "omc-release-deal"));
                    Capture(0, "flow-before-deal-" + dealCount);
                    if (useCoordinator)
                    {
                        var coordinator = boots[0].Connection.DealerCoordinator;
                        Assert.That(coordinator.Poll(out var work), Is.Null);
                        Assert.That(work.TargetDeal.WindowId.ToString("N"), Is.EqualTo(game.dealWindowId));
                        Assert.That(work.SourceUtterances, Is.SameAs(batches[dealCount]));
                        var post = Task.Run(() => coordinator.TryPostUnchanged(work));
                        yield return Wait(() => post.IsCompleted);
                        Assert.That(post.GetAwaiter().GetResult(), Is.True);
                        Assert.That(coordinator.TryPostUnchanged(work), Is.True);
                        Assert.That(boots[0].Connection.Remote.Read().IsDealPending, Is.True);
                        Assert.That(coordinator.Poll(out _).Accepted, Is.True);
                        Assert.That(coordinator.TryPostUnchanged(work), Is.False);
                        Assert.That(boots[0].Connection.Remote.HasPendingInput, Is.False);
                    }
                    else if (useDealerPort)
                    {
                        var port = boots[0].Connection.DealerTurns;
                        var turn = port.ReadPendingTurn().Turn;
                        Assert.That(turn.TargetDeal.WindowId.ToString("N"), Is.EqualTo(game.dealWindowId));
                        Assert.That(turn.SourceUtterances, Is.SameAs(batches[dealCount]));
                        var command = turn.CreateUnchangedCommand(Guid.NewGuid());
                        Assert.That(port.DealUnchanged(command).Accepted, Is.True);
                        Assert.That(port.DealUnchanged(command).Accepted, Is.True);
                        Assert.That(boots[0].Connection.Remote.HasPendingInput, Is.False,
                            "Host module completion is not a pending player button command.");
                    }
                    else yield return Click(0, "omc-release-deal");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().IsRevealPending));
                    Assert.That(Client(0).Latest.game.board.Length, Is.EqualTo(++dealCount + 2));
                }
                else if (game.revealPending)
                {
                    Assert.That(boots.All(b => b.Connection.Remote.Read().LegalActions == null), Is.True);
                    Assert.That(roots.All(r => !r.Q<Button>("omc-utterance-send").enabledInHierarchy), Is.True);
                    for (int i = 1; i < 4; i++)
                    {
                        Assert.That(roots[i].Q<Button>("omc-continue-reveal").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                        Assert.That(roots[i].Q<Label>(className: "omc-prompt").text, Does.Contain("방장이 계속"));
                    }
                    Capture(1, "flow-after-reveal-" + revealCount);
                    yield return Click(0, "omc-continue-reveal");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > game.version));
                    revealCount++;
                }
                else if (game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority)
                {
                    yield return Click(0, "omc-resolve");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
                }
                else
                {
                    if (sendSpeech && game.street < 3 && game.street != lastSentStreet)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            int viewer = i, expected = game.street + 1;
                            roots[i].Q<TextField>("omc-utterance-input").value = "진행 멘트 " + i + " " + game.street;
                            yield return Click(i, "omc-utterance-send");
                            yield return Wait(() => boots[viewer].Connection.Remote.Utterances.ReadUtterances().Count == expected);
                        }
                        lastSentStreet = game.street;
                    }
                    int actor = game.currentSeat - 1;
                    yield return Click(actor, "omc-passive");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > game.version));
                }
            }
            Assert.That(dealCount, Is.EqualTo(3)); Assert.That(revealCount, Is.EqualTo(3));
            Assert.That(boots.All(b => b.Connection.Remote.Read().Result != null), Is.True);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
            yield return null; yield return null;
            for (int i = 0; i < 4; i++) AssertFlowLayout(i);
            Capture(0, "flow-settled-" + textures[0].width);
        }

        private void AssertFlowLayout(int viewer)
        {
            var table = roots[viewer].Q(className: "omc-table");
            var own = roots[viewer].Q(className: "omc-player");
            var middle = roots[viewer].Q(className: "omc-middle");
            var opponents = roots[viewer].Q(className: "omc-opponents");
            var controls = roots[viewer].Q(className: "omc-controls");
            string detail = "viewer=" + viewer + " table=" + table.worldBound + " opponents=" + opponents.worldBound
                + " middle=" + middle.worldBound + " own=" + own.worldBound + " controls=" + controls.worldBound
                + " info=" + string.Join(";", own.Q(className: "omc-seat-info").Children().Select(e =>
                    e.name + ":" + e.worldBound + " mt=" + e.resolvedStyle.marginTop + " mb=" + e.resolvedStyle.marginBottom));
            Assert.That(opponents.worldBound.yMax, Is.LessThanOrEqualTo(middle.worldBound.yMin + 1), detail);
            Assert.That(middle.worldBound.yMax, Is.LessThanOrEqualTo(own.worldBound.yMin + 1), detail);
            Assert.That(own.worldBound.yMax, Is.LessThanOrEqualTo(table.worldBound.yMax - 5), detail);
            Assert.That(own.worldBound.yMax, Is.LessThanOrEqualTo(controls.worldBound.yMin), detail);
            Assert.That(controls.worldBound.yMax, Is.LessThanOrEqualTo(roots[viewer].worldBound.yMax + 1), detail);
        }
    }
}
