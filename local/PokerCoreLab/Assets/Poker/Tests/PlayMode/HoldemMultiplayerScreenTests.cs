using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerScreenTests
    {
        private HoldemTcpServer server;
        private readonly HoldemClientIdentity[] identities = new HoldemClientIdentity[4];
        private readonly HoldemTcpClient[] clients = new HoldemTcpClient[4];
        private readonly HoldemRemoteTablePort[] ports = new HoldemRemoteTablePort[4];
        private readonly HoldemTableScreen[] screens = new HoldemTableScreen[4];
        private readonly GameObject[] objects = new GameObject[4];
        private readonly PanelSettings[] panels = new PanelSettings[4];
        private readonly RenderTexture[] textures = new RenderTexture[4];
        private readonly VisualElement[] roots = new VisualElement[4];
        private readonly Task<HoldemTcpClient>[] reconnecting = new Task<HoldemTcpClient>[4];
        private readonly int[] reconnectClicks = new int[4];
        private readonly long[] feedbackOffsets = new long[4];

        [UnitySetUp]
        public IEnumerator Setup() => SetupTable();

        private IEnumerator SetupTable(HoldemUtterancePolicy speechPolicy = null, HoldemConfig config = null, bool legacyRules = false)
        {
            for (int i = 0; i < 4; i++) identities[i] = new HoldemClientIdentity(i == 1 ? "<b>참가자 2</b>" : "참가자 " + (i + 1));
            server = new HoldemTcpServer(identities[0], config ?? new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom(),
                utterancePolicy: speechPolicy);
            for (int i = 0; i < 4; i++)
            {
                var connect = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[i]);
                yield return Wait(() => connect.IsCompleted);
                clients[i] = connect.GetAwaiter().GetResult();
                int seat = i;
                yield return Wait(() => clients[seat].IsAdmitted && clients[seat].Latest != null);
                clients[i].Send(new HoldemWireRequest { type = "ready", ready = true });
            }
            yield return Wait(() => clients.All(c => c.Latest.members.Length == 4 && c.Latest.members.All(m => m.ready)));
            clients[0].Send(new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = 0 });
            yield return Wait(() => clients.All(c => c.Latest.hasGame));
            for (int i = 0; i < 4; i++)
            {
                int seat = i; reconnectClicks[i] = 0; feedbackOffsets[i] = 0;
                if (legacyRules) clients[i].Latest.hasRules = false;
                ports[i] = new HoldemRemoteTablePort(clients[i], () => {
                    reconnectClicks[seat]++;
                    if (reconnecting[seat] == null) reconnecting[seat] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[seat]);
                }, () => (long)(Time.realtimeSinceStartupAsDouble * 1000) + feedbackOffsets[seat]); ports[i].Poll();
                panels[i] = ScriptableObject.CreateInstance<PanelSettings>();
                panels[i].themeStyleSheet = Resources.Load<ThemeStyleSheet>("PokerTheme");
                panels[i].scaleMode = PanelScaleMode.ConstantPixelSize;
                textures[i] = new RenderTexture(960, 640, 0); textures[i].Create(); panels[i].targetTexture = textures[i];
                objects[i] = new GameObject("Network screen " + (i + 1));
                var document = objects[i].AddComponent<UIDocument>(); document.panelSettings = panels[i];
                roots[i] = document.rootVisualElement;
                screens[i] = new HoldemTableScreen(roots[i], ports[i], Resources.Load<Font>("Fonts/NanumGothic-Regular"));
            }
            for (int i = 0; i < 5; i++) { Tick(); yield return null; }
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            for (int i = 0; i < 4; i++)
            {
                var remaining = reconnecting[i]; reconnecting[i] = null;
                if (remaining != null) remaining.ContinueWith(task => {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else { var observed = task.Exception; }
                }, TaskScheduler.Default);
                screens[i]?.Dispose(); ports[i]?.Dispose(); clients[i]?.Dispose();
                if (objects[i] != null) UnityEngine.Object.Destroy(objects[i]);
                screens[i] = null; ports[i] = null; clients[i] = null; objects[i] = null; roots[i] = null;
            }
            server?.Dispose(); server = null; yield return null;
            for (int i = 0; i < 4; i++)
            {
                if (panels[i] != null) UnityEngine.Object.Destroy(panels[i]);
                if (textures[i] != null) { textures[i].Release(); UnityEngine.Object.Destroy(textures[i]); }
                panels[i] = null; textures[i] = null; identities[i] = null;
            }
        }

        private void Tick()
        {
            server?.Pump();
            for (int i = 0; i < 4; i++)
            {
                if (reconnecting[i]?.IsCompleted == true)
                {
                    clients[i] = reconnecting[i].GetAwaiter().GetResult(); reconnecting[i] = null;
                    ports[i].ReplaceConnection(clients[i]);
                }
                if (ports[i] != null) ports[i].Poll(); else clients[i]?.Poll();
            }
        }
        private IEnumerator Wait(Func<bool> condition, double timeout = 6)
        {
            double end = Time.realtimeSinceStartupAsDouble + timeout;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) { Tick(); yield return null; }
            Assert.That(condition(), Is.True, "Network UI condition exceeded its bounded wait.\n"
                + string.Join("\n", Enumerable.Range(0, 4).Select(i => "seat=" + (i + 1)
                    + " version=" + clients[i]?.Latest?.game?.version + " hand=" + clients[i]?.Latest?.game?.handNumber
                    + " actor=" + clients[i]?.Latest?.game?.currentSeat + " closed=" + clients[i]?.IsClosed
                    + " pending=" + ports[i]?.HasPendingInput + " status=" + ports[i]?.StatusText + " error=" + ports[i]?.ErrorText)));
        }
        private bool Enabled(int seat, string name)
        {
            var button = roots[seat].Q<Button>(name);
            if (!button.enabledInHierarchy || button.resolvedStyle.display == DisplayStyle.None || button.worldBound.width <= 0) return false;
            // A state event may have scheduled layout without completing it in this frame.
            var picked = roots[seat].panel.Pick(button.worldBound.center);
            return picked == button || button.Contains(picked);
        }
        private IEnumerator WaitWithoutPort(int viewer, Func<bool> condition)
        {
            double end = Time.realtimeSinceStartupAsDouble + 6;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end)
            {
                server.Pump();
                for (int i = 0; i < 4; i++) if (i != viewer) ports[i].Poll();
                yield return null;
            }
            Assert.That(condition(), Is.True, "Authority did not reach the expected state while one viewer was stale.");
        }
        private void Click(int seat, string name)
        {
            var button = roots[seat].Q<Button>(name);
            Assert.That(Enabled(seat, name), Is.True, name);
            Vector2 point = button.worldBound.center;
            var picked = roots[seat].panel.Pick(point);
            Assert.That(picked == button || button.Contains(picked), Is.True, "Button is covered: " + name);
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { down.target = picked; picked.SendEvent(down); }
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { up.target = picked; picked.SendEvent(up); }
        }

        [UnityTest]
        public IEnumerator SpeechPendingDoesNotBlockBettingAndOnlyTheSenderSeesConfirmation()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            var field = roots[3].Q<TextField>("omc-utterance-input");
            field.value = "오늘은 <b>빨간색</b>이 좋네요";
            yield return Wait(() => Enabled(3, "omc-utterance-send") && Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            Click(3, "omc-utterance-send");
            Assert.That(ports[3].HasPendingUtterance, Is.True);
            Assert.That(ports[3].HasPendingInput, Is.False);
            Assert.That(ports[3].CanSend, Is.True);
            Assert.That(field.value, Is.Not.Empty);
            Click(3, "omc-passive");
            Assert.That(ports[3].HasPendingInput, Is.True);
            Assert.That(ports[3].HasPendingUtterance, Is.True);
            yield return Wait(() => !ports[3].HasPendingInput && !ports[3].HasPendingUtterance && field.value.Length == 0);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Utterances.ReadUtterances().Count, Is.EqualTo(1));
            var status = roots[3].Q<Label>("omc-utterance-status");
            Assert.That(status.enableRichText, Is.False);
            Assert.That(status.text, Does.Contain("<b>빨간색</b>"));
            foreach (int i in new[] { 0, 1, 2 }) Assert.That(ports[i].Utterances.ReadUtterances().Count, Is.Zero);
            for (int i = 0; i < 4; i++)
            {
                var box = roots[i].Q("omc-utterance");
                Assert.That(box.worldBound.xMin, Is.GreaterThanOrEqualTo(0));
                Assert.That(box.worldBound.xMax, Is.LessThanOrEqualTo(960));
                Assert.That(box.worldBound.yMax, Is.LessThanOrEqualTo(640));
            }
        }

        [UnityTest]
        public IEnumerator SpeechConfirmationRecoversFromAnUnreadReceiptAndReconnect()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            var field = roots[1].Q<TextField>("omc-utterance-input"); field.value = "한 번만 접수";
            yield return Wait(() => Enabled(1, "omc-utterance-send"));
            Click(1, "omc-utterance-send");
            double until = Time.realtimeSinceStartupAsDouble + 3;
            while (server.PendingInputCount == 0 && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(server.PendingInputCount, Is.GreaterThan(0));
            server.Pump();
            clients[1].Dispose();
            // Replace before polling the old client, deliberately abandoning its unread confirmation.
            var replacement = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]);
            while (!replacement.IsCompleted) yield return null;
            clients[1] = replacement.GetAwaiter().GetResult(); ports[1].ReplaceConnection(clients[1]);
            yield return Wait(() => !ports[1].HasPendingUtterance && ports[1].CanSend && field.value.Length == 0);
            Assert.That(ports[1].Utterances.ReadUtterances().Count, Is.EqualTo(1));
            Assert.That(roots[1].Q<Label>("omc-utterance-status").text, Does.Contain("한 번만 접수"));
        }

        [UnityTest]
        public IEnumerator SpeechDraftSentFromAStaleStreetIsNotMovedToTheNewWindow()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            var field = roots[1].Q<TextField>("omc-utterance-input"); field.value = "이전 단계 원문";
            for (int step = 0; step < 3; step++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                yield return Wait(() => Enabled(actor, "omc-passive"));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, "omc-passive");
                yield return Wait(() => clients.All(c => c.Latest.game.version > version));
            }
            int lastActor = clients[0].Latest.game.currentSeat - 1;
            yield return Wait(() => Enabled(lastActor, "omc-passive") && Enabled(1, "omc-utterance-send"));
            Click(lastActor, "omc-passive");
            double until = Time.realtimeSinceStartupAsDouble + 3;
            while (server.PendingInputCount == 0 && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(server.PendingInputCount, Is.GreaterThan(0));
            server.Pump(); // Flop exists on the host; clients have deliberately not polled that state.
            Assert.That(ports[1].Read().Street, Is.EqualTo(HoldemStreet.Preflop));
            Click(1, "omc-utterance-send");
            yield return Wait(() => ports[1].Read().Street == HoldemStreet.Flop && !ports[1].HasPendingUtterance);
            Assert.That(ports[1].Utterances.ReadUtterances().Count, Is.Zero);
            Assert.That(field.value, Is.Empty);
            field.value = "새 단계 원문";
            for (int i = 0; i < 5; i++) { Tick(); yield return null; }
            Assert.That(field.value, Is.EqualTo("새 단계 원문"), "Late old-window responses must not clear the new draft.");
            yield return Wait(() => Enabled(1, "omc-utterance-send"));
            Click(1, "omc-utterance-send");
            yield return Wait(() => !ports[1].HasPendingUtterance && field.value.Length == 0
                && ports[1].Utterances.ReadUtterances().Count == 1);
            var own = ports[1].Utterances.ReadUtterances();
            Assert.That(own.Count, Is.EqualTo(1));
            Assert.That(own.GetEntry(0).Street, Is.EqualTo(HoldemStreet.Flop));
            Assert.That(own.GetEntry(0).Text, Is.EqualTo("새 단계 원문"));
        }

        [UnityTest]
        public IEnumerator SpeechWithLostReceiptIsUnknownAfterANewHandAndNeverAutomaticallyResubmitted()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            var field = roots[1].Q<TextField>("omc-utterance-input"); field.value = "지난 판 접수";
            yield return Wait(() => Enabled(1, "omc-utterance-send"));
            Click(1, "omc-utterance-send");
            double until = Time.realtimeSinceStartupAsDouble + 3;
            while (server.PendingInputCount == 0 && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(server.PendingInputCount, Is.GreaterThan(0));
            server.Pump(); clients[1].Dispose();
            var retainedPort = ports[1]; ports[1] = null;
            try
            {
                var replacement = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]);
                while (!replacement.IsCompleted) yield return null;
                clients[1] = replacement.GetAwaiter().GetResult();
                // Keep the real seat connected and polling, but intentionally leave its old UI unrefreshed.
                yield return Wait(() => clients[1].IsAdmitted && clients.All(c => !c.Latest.paused));
                for (int i = 0; i < 3; i++)
                {
                    var game = clients[0].Latest.game;
                    clients[game.currentSeat - 1].Send(new HoldemWireRequest
                    { type = "act", handId = game.handId, version = game.version, action = 0 });
                    yield return Wait(() => clients.All(c => c.Latest.game.version > game.version));
                }
                Assert.That(clients[0].Latest.game.hasResult, Is.True);
                var settled = clients[0].Latest.game;
                clients[0].Send(new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = settled.version });
                yield return Wait(() => clients.All(c => c.Latest.game.handNumber == 2));
                ports[1] = retainedPort; retainedPort.ReplaceConnection(clients[1]); Tick();
                yield return Wait(() => ports[1].Read().HandNumber == 2 && !ports[1].HasPendingUtterance);
                Assert.That(field.value, Is.Empty);
                Assert.That(roots[1].Q<Label>("omc-utterance-status").text, Does.Contain("이전 판의 접수 결과는 확인하지 못"));
                Assert.That(ports[1].Utterances.ReadUtterances().Count, Is.Zero);
                Assert.That(server.ReadPendingUtterances().Count, Is.EqualTo(1));
                Assert.That(server.ReadPendingUtterances()[0].Count, Is.EqualTo(1));
            }
            finally { ports[1] = retainedPort; }
        }

        [UnityTest]
        public IEnumerator SpeechConfirmedReceiptAndNextHandInOnePollPreserveTheKnownResult()
        {
            foreach (bool reject in new[] { false, true })
            {
                yield return Cleanup();
                yield return SetupTable(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active));
                if (reject)
                {
                    var current = clients[1].Latest;
                    clients[1].Send(new HoldemWireRequest { type = "utterance", handId = current.game.handId,
                        windowId = current.ownUtterances.windowId, street = current.game.street, text = "선행 접수" });
                    double until = Time.realtimeSinceStartupAsDouble + 3;
                    while (server.PendingInputCount == 0 && Time.realtimeSinceStartupAsDouble < until) yield return null;
                    Assert.That(server.PendingInputCount, Is.GreaterThan(0));
                    server.Pump(); // Keep the UI stale so its next valid-looking send is rejected by the host limit.
                }
                var field = roots[1].Q<TextField>("omc-utterance-input"); field.value = "결과 확인 멘트";
                // Do not pump the network here: the rejected path intentionally holds a stale permission view.
                yield return null;
                Click(1, "omc-utterance-send");
                var retainedPort = ports[1]; ports[1] = null;
                try
                {
                    yield return Wait(() => clients[1].Latest.ownUtterances.entries.Length == 1);
                    // Let the socket collect its receipt and latest state, but postpone its single UI consumer.
                    for (int i = 0; i < 3; i++)
                    {
                        var game = clients[0].Latest.game;
                        clients[game.currentSeat - 1].Send(new HoldemWireRequest
                        { type = "act", handId = game.handId, version = game.version, action = 0 });
                        yield return Wait(() => clients.All(c => c.Latest.game.version > game.version));
                    }
                    var settled = clients[0].Latest.game;
                    clients[0].Send(new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = settled.version });
                    yield return Wait(() => clients.All(c => c.Latest.game.handNumber == 2));
                    ports[1] = retainedPort; retainedPort.Poll();
                    Assert.That(field.value, Is.Empty);
                    Assert.That(ports[1].HasPendingUtterance, Is.False);
                    var status = roots[1].Q<Label>("omc-utterance-status").text;
                    Assert.That(status, Does.Contain(reject ? "이전 판 멘트: 이번 단계에서" : "이전 판의 멘트 접수가 확인"));
                    Assert.That(status, Does.Not.Contain("확인하지 못"));
                }
                finally { ports[1] = retainedPort; }
            }
        }

        [UnityTest]
        public IEnumerator FullIntakeStatusDisablesOnlySpeechNotThePokerAction()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 1, HoldemUtteranceSeats.Active));
            var packet = clients[3].Latest; // Inject an already validated authority projection at the UI boundary.
            packet.revision++; packet.ownUtterances.canSubmit = false; packet.ownUtterances.backlogged = true;
            ports[3].Poll();
            yield return null; yield return null;
            Assert.That(ports[3].Utterances.ReadUtterances().IsBacklogged, Is.True);
            Assert.That(roots[3].Q<Button>("omc-utterance-send").enabledInHierarchy, Is.False);
            Assert.That(roots[3].Q<Label>("omc-utterance-status").text, Does.Contain("접수함이 가득"));
            Assert.That(ports[3].CanSend, Is.True);
            Assert.That(Enabled(3, "omc-passive"), Is.True);
        }

        [UnityTest]
        public IEnumerator ANewMatchCannotReplaceAnOngoingMatchProjection()
        {
            var before = ports[1].Read();
            var lobby = ports[1].Lobby;
            var packet = clients[1].Latest;
            packet.revision++; packet.matchNumber++;
            packet.game.handNumber++; packet.game.version++; packet.game.handId = Guid.NewGuid().ToString("N");
            ports[1].Poll();
            Assert.That(clients[1].IsClosed, Is.True);
            Assert.That(ports[1].Read(), Is.SameAs(before));
            Assert.That(ports[1].Lobby, Is.SameAs(lobby));
            Assert.That(ports[1].CanSend, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ConfirmedRoomRulesCannotChangeOrDisappearDuringAConnection()
        {
            foreach (string change in new[] { "stack", "blind", "deal", "reveal", "intake", "missing" })
            {
                if (change != "stack") { yield return Cleanup(); yield return SetupTable(); }
                var before = ports[1].Read(); var rules = ports[1].Lobby.Rules;
                var packet = clients[1].Latest; packet.revision++;
                if (change == "stack") packet.rules.startingStack++;
                else if (change == "blind") packet.rules.bigBlind++;
                else if (change == "deal") packet.rules.waitsForHostDeal = true;
                else if (change == "reveal") packet.rules.pausesAfterReveal = true;
                else if (change == "intake") packet.rules.receivesUtterances = true;
                else packet.hasRules = false;
                ports[1].Poll();
                Assert.That(clients[1].IsClosed, Is.True, change);
                Assert.That(ports[1].Read(), Is.SameAs(before), change);
                Assert.That(ports[1].Lobby.Rules, Is.SameAs(rules), change);
                Assert.That(ports[1].CanSend, Is.False, change);
            }
        }

        [UnityTest]
        public IEnumerator PublicHistoryRejectsChangedOrRemovedConfirmedEntriesBeforeUpdatingTheView()
        {
            foreach (string fault in new[] { "rewrite", "remove", "seat", "offset" })
            {
                if (fault != "rewrite") { yield return Cleanup(); yield return SetupTable(); }
                var before = ports[1].Read(); var history = ports[1].ReadHistory();
                var packet = clients[1].Latest; packet.revision++;
                if (fault == "rewrite") { packet.history.entries[0].amount++; packet.history.entries[0].streetTotal++; }
                else if (fault == "remove") packet.hasHistory = false;
                else if (fault == "seat") packet.history.entries[0].seat = 4;
                else packet.history.omittedCount++;
                ports[1].Poll();
                Assert.That(clients[1].IsClosed, Is.True, fault);
                Assert.That(ports[1].Read(), Is.SameAs(before), fault);
                Assert.That(ports[1].ReadHistory(), Is.SameAs(history), fault);
                Assert.That(ports[1].CanSend, Is.False, fault);
            }
        }

        [UnityTest]
        public IEnumerator PublicHistoryTailShowsOmissionAfterManyLegalRaises()
        {
            yield return Cleanup(); yield return SetupTable(config: new HoldemConfig(10000, 1, 2));
            for (int i = 0; i < 72; i++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                var basis = ports[actor].Read();
                ports[actor].Act(basis, BettingAction.RaiseTo(basis.LegalActions.MinimumAggressiveTarget.Value));
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > basis.SessionVersion));
            }
            Assert.That(ports.All(p => p.ReadHistory().Count == 64 && p.ReadHistory().OmittedCount == 10), Is.True);
            yield return Wait(() => Enabled(1, "omc-history")); Click(1, "omc-history");
            yield return null; yield return null;
            string copy = roots[1].Q<Label>("omc-history-copy").text;
            Assert.That(copy, Does.Contain("앞의 10개는 생략"));
            Assert.That(copy, Does.Not.Contain("SB 1칩"));
            Assert.That(copy, Does.Contain("레이즈"));
            Capture(1, "recent-public-history");
        }

        [UnityTest]
        public IEnumerator SpeechHistoryRegressionKeepsTheLastGoodViewAndClosesTheConnection()
        {
            foreach (string change in new[] { "text", "remove", "disable", "limit" })
            {
                yield return Cleanup();
                yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
                var field = roots[1].Q<TextField>("omc-utterance-input"); field.value = "유지할 원문";
                yield return Wait(() => Enabled(1, "omc-utterance-send"));
                Click(1, "omc-utterance-send");
                yield return Wait(() => !ports[1].HasPendingUtterance && ports[1].Utterances.ReadUtterances().Count == 1);
                var packet = clients[1].Latest; // Fault injection at the public wire ingress; no authority mutation.
                packet.revision++;
                if (change == "text") packet.ownUtterances.entries[0].text = "변조된 원문";
                else if (change == "remove") packet.ownUtterances.entries = new HoldemOwnUtteranceEntryPacket[0];
                else if (change == "disable") packet.hasOwnUtterances = false;
                else packet.ownUtterances.maximumTextLength++;
                ports[1].Poll();
                Assert.That(clients[1].IsClosed, Is.True, change);
                Assert.That(ports[1].CanSend, Is.False, change);
                Assert.That(ports[1].Utterances.ReadUtterances().GetEntry(0).Text, Is.EqualTo("유지할 원문"), change);
                Assert.That(roots[1].Q<Label>("omc-utterance-status").text, Does.Not.Contain("변조된 원문"), change);
            }
        }

        [UnityTest]
        public IEnumerator FourPacketBackedScreensPlayAHandAndOnlyHostCanStartTheNext()
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ports[i].Utterances, Is.Null);
                Assert.That(roots[i].Q("omc-utterance"), Is.Null);
                Assert.That(roots[i].Query(className: "face-card").ToList().Count, Is.EqualTo(2));
                Assert.That(roots[i].Query(className: "hidden").ToList().Count, Is.EqualTo(6));
                Assert.That(roots[i].Q<Button>("omc-reset").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Capture(i, "initial");
            }
            var title = roots[0].Q("omc-seat-2").Q<Label>(className: "omc-seat-title");
            Assert.That(title.text, Does.Contain("<b>참가자 2</b>")); Assert.That(title.enableRichText, Is.False);
            for (int step = 0; step < 100 && !clients[0].Latest.game.hasResult; step++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                yield return Wait(() => Enabled(actor, "omc-passive"));
                long version = clients[actor].Latest.game.version;
                Click(actor, "omc-passive");
                Assert.That(ports[actor].HasPendingInput, Is.True);
                Assert.That(ports[actor].Read().SessionVersion, Is.EqualTo(version), "No optimistic local game update.");
                yield return Wait(() => clients.All(c => c.Latest.game.version > version) && !ports[actor].HasPendingInput);
            }
            Assert.That(clients.All(c => c.Latest.game.hasResult), Is.True);
            for (int i = 0; i < 4; i++)
            {
                Assert.That(roots[i].Query(className: "face-card").ToList().Count, Is.EqualTo(13));
                Assert.That(clients[i].Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
                if (i > 0) Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Capture(i, "showdown");
            }
            yield return Wait(() => Enabled(0, "omc-next"));
            Click(0, "omc-next");
            yield return Wait(() => clients.All(c => c.Latest.game.handNumber == 2));
            foreach (var root in roots) Assert.That(root.Query(className: "hidden").ToList().Count, Is.EqualTo(6));
        }

        [UnityTest]
        public IEnumerator OddChipSettlementUsesTheHostButtonAndUnblocksEveryScreen()
        {
            // Create unequal stacks through real play; then force a tied side-pot fixture with the stable deck.
            for (int step = 0; step < 3; step++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1;
                yield return Wait(() => Enabled(actor, "omc-fold"));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, "omc-fold");
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
            }
            yield return Wait(() => Enabled(0, "omc-next"));
            Click(0, "omc-next");
            yield return Wait(() => ports.All(p => p.Read().HandNumber == 2));
            for (int actor = 0; actor < 4; actor++)
            {
                int current = actor;
                string button = actor == 0 ? "omc-aggressive" : actor == 3 ? "omc-fold" : "omc-passive";
                if (actor == 0) roots[actor].Q<TextField>("omc-target").value = "100";
                yield return Wait(() => Enabled(current, button));
                Assert.That(clients[actor].Latest.game.currentSeat, Is.EqualTo(actor + 1));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, button);
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
            }
            yield return Wait(() => ports.All(p => p.Read().IsSettlementPending) && Enabled(0, "omc-resolve"));
            long pendingVersion = ports[0].Read().SessionVersion;
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ports[i].Read().Result, Is.Null);
                Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(roots[i].Q<Button>("omc-passive").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                if (i > 0)
                {
                    Assert.That(roots[i].Q<Button>("omc-resolve").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                    Assert.That(roots[i].Q<Label>(className: "omc-prompt").text, Does.Contain("방장이 남은 칩을 정산"));
                }
                Capture(i, "odd-chip-pending");
            }
            Click(0, "omc-resolve");
            Assert.That(ports[0].HasPendingInput, Is.True);
            Assert.That(ports[0].Read().SessionVersion, Is.EqualTo(pendingVersion), "No optimistic payout.");
            yield return Wait(() => ports.All(p => p.Read().Result != null) && !ports[0].HasPendingInput);
            string result = JsonUtility.ToJson(clients[0].Latest.game.result);
            for (int i = 0; i < 4; i++)
            {
                var game = clients[i].Latest.game;
                Assert.That(game.version, Is.EqualTo(pendingVersion + 1));
                Assert.That(JsonUtility.ToJson(game.result), Is.EqualTo(result));
                Assert.That(game.result.pots.Length, Is.GreaterThan(1));
                Assert.That(game.result.pots.SelectMany(p => p.payouts).Any(p => p.includesOddChip), Is.True);
                Assert.That(game.seats.Sum(s => s.stack), Is.EqualTo(400));
                Assert.That(roots[i].Q<Button>("omc-resolve").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                if (i > 0) Assert.That(roots[i].Q<Button>("omc-next").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            }
            yield return Wait(() => Enabled(0, "omc-next"));
            Capture(0, "odd-chip-settled");
            Click(0, "omc-next");
            yield return Wait(() => ports.All(p => p.Read().HandNumber == 3));
            Assert.That(ports.All(p => !p.Read().IsSettlementPending && p.Read().Result == null), Is.True);
        }

        [UnityTest]
        public IEnumerator EveryViewerKeepsBothPublicCallsUntilTheStreetChanges()
        {
            foreach (int actor in new[] { 3, 0 })
            {
                yield return Wait(() => Enabled(actor, "omc-passive"));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, "omc-passive");
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
            }
            foreach (var root in roots)
                foreach (int seat in new[] { 1, 4 })
                    Assert.That(root.Q("omc-seat-" + seat).Q<Label>(className: "omc-seat-status").text, Does.StartWith("콜"));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ports[i].Read().LastAction.Seat.Value, Is.EqualTo(1));
                Assert.That(ports[i].Read().StreetActionCount, Is.EqualTo(2));
                Assert.That(roots[i].Query(className: "hidden").ToList().Count, Is.EqualTo(6));
            }
            foreach (int actor in new[] { 1, 2 })
            {
                yield return Wait(() => Enabled(actor, "omc-passive"));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, "omc-passive");
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
            }
            foreach (var p in ports)
            { Assert.That(p.Read().Street, Is.EqualTo(HoldemStreet.Flop)); Assert.That(p.Read().StreetActionCount, Is.Zero); }
            foreach (var root in roots)
                Assert.That(root.Q("omc-seat-4").Q<Label>(className: "omc-seat-status").text, Does.StartWith("이번 베팅"));
        }

        [UnityTest]
        public IEnumerator PendingClickCannotDoubleBetAndLostReceiptRecoversAfterReconnect()
        {
            yield return Wait(() => Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            Click(3, "omc-passive");
            Assert.That(ports[3].HasPendingInput, Is.True);
            var button = roots[3].Q<Button>("omc-passive");
            using (var repeated = NavigationSubmitEvent.GetPooled()) { repeated.target = button; button.SendEvent(repeated); }
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version));

            // Let the authority and other viewers update, but deliberately discard this player's receipt.
            double end = Time.realtimeSinceStartupAsDouble + 6;
            while (clients[0].Latest.game.version == version && Time.realtimeSinceStartupAsDouble < end)
            {
                server.Pump(); for (int i = 0; i < 3; i++) ports[i].Poll(); yield return null;
            }
            Assert.That(clients[0].Latest.game.version, Is.EqualTo(version + 1));
            clients[3].Poll(); while (clients[3].TryReadResponse(out _)) { }
            clients[3].Dispose();
            yield return Wait(() => clients[0].Latest.paused);
            Assert.That(ports[3].CanSend, Is.False);
            Assert.That(roots[3].Q<Label>(className: "omc-prompt").text, Does.Contain("연결이 끊겼어요"));

            yield return Wait(() => Enabled(3, "omc-retry"));
            Click(3, "omc-retry");
            Assert.That(reconnectClicks[3], Is.EqualTo(1));
            yield return Wait(() => clients.All(c => c.IsAdmitted && !c.Latest.paused) && !ports[3].HasPendingInput);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Read().OwnStack, Is.EqualTo(98));
            Assert.That(clients[3].Latest.members.Length, Is.EqualTo(4));
            Capture(3, "reconnected");
        }

        [UnityTest]
        public IEnumerator UnconfirmedAppliedInputCanReconnectEarlyWithoutChargingTwice()
        {
            yield return Wait(() => Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            Click(3, "omc-passive");
            Assert.That(ports[3].HasPendingInput, Is.True);
            Assert.That(ports[3].NeedsRefresh, Is.False);

            double until = Time.realtimeSinceStartupAsDouble + 3;
            while (clients[0].Latest.game.version == version && Time.realtimeSinceStartupAsDouble < until)
            {
                server.Pump(); for (int i = 0; i < 3; i++) ports[i].Poll(); yield return null;
            }
            Assert.That(clients[0].Latest.game.version, Is.EqualTo(version + 1));
            clients[3].Poll(); while (clients[3].TryReadResponse(out _)) { }
            Assert.That(clients[3].IsClosed, Is.False);

            feedbackOffsets[3] = 3000; ports[3].Poll();
            Assert.That(ports[3].RefreshLabel, Is.EqualTo("입력 확인"));
            ports[3].Refresh();
            Assert.That(clients[3].IsClosed, Is.False, "An initial confirmation retry must keep the connection.");
            Assert.That(reconnectClicks[3], Is.Zero);
            // Retrying must not reset the age of the original, still-unconfirmed intent.
            feedbackOffsets[3] = 8000; ports[3].Poll();
            Assert.That(ports[3].NeedsRefresh, Is.True);
            Assert.That(ports[3].RefreshLabel, Is.EqualTo("다시 연결"));
            Assert.That(roots[3].Q<Label>(className: "omc-prompt").text, Does.Contain("다시 연결"));
            yield return null; yield return null;
            Click(3, "omc-retry");
            Assert.That(clients[3].IsClosed, Is.True);
            Assert.That(ports[3].HasPendingInput, Is.True);
            Assert.That(reconnectClicks[3], Is.EqualTo(1));
            yield return Wait(() => clients.All(c => c.IsAdmitted && !c.Latest.paused) && !ports[3].HasPendingInput);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Read().OwnStack, Is.EqualTo(98));
            Assert.That(clients[3].Identity.Seat, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator UnprocessedInputSurvivesManualReconnectAsTheSameAction()
        {
            yield return Wait(() => Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            Click(3, "omc-passive");
            // Do not pump the authority: simulate an open transport without any new response.
            feedbackOffsets[3] = 8000; ports[3].Poll();
            Assert.That(ports[3].RefreshLabel, Is.EqualTo("다시 연결"));
            Assert.That(clients[3].IsClosed, Is.False);
            ports[3].Refresh();
            Assert.That(clients[3].IsClosed, Is.True);
            Assert.That(ports[3].HasPendingInput, Is.True);
            yield return Wait(() => clients.All(c => c.IsAdmitted && !c.Latest.paused) && !ports[3].HasPendingInput);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Read().OwnStack, Is.EqualTo(98));
            Assert.That(ports[3].Read().LastAction.Seat.Value, Is.EqualTo(4));
        }

        [UnityTest]
        public IEnumerator KnownOtherPlayerDisconnectDoesNotOfferToReplaceHealthyTransport()
        {
            yield return Wait(() => Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            Click(3, "omc-passive");
            double until = Time.realtimeSinceStartupAsDouble + 3;
            while (clients[0].Latest.game.version == version && Time.realtimeSinceStartupAsDouble < until)
            {
                server.Pump(); for (int i = 0; i < 3; i++) ports[i].Poll(); yield return null;
            }
            clients[3].Poll(); while (clients[3].TryReadResponse(out _)) { }
            clients[2].Dispose();
            yield return Wait(() => ports[3].Lobby.Paused);
            feedbackOffsets[3] = 8000; ports[3].Poll();
            Assert.That(ports[3].HasPendingInput, Is.True);
            Assert.That(ports[3].RefreshLabel, Is.EqualTo("입력 확인"));
            Assert.That(ports[3].StatusText, Does.Contain("다른 참가자"));
            ports[3].Refresh();
            Assert.That(clients[3].IsClosed, Is.False);
            Assert.That(reconnectClicks[3], Is.Zero);
            for (int i = 0; i < 8; i++) { Tick(); yield return null; }
            Assert.That(ports[3].HasPendingInput, Is.True, "A pause must not turn receipt recovery into rejection.");
            Assert.That(ports[3].ErrorText, Is.Empty);
            Assert.That(ports[3].NeedsRefresh, Is.False);
        }

        [UnityTest]
        public IEnumerator PausedRetryKeepsAnAppliedButUnconfirmedInput()
            => PauseBetweenStaleInputAndAuthorityReply(true, false);

        [UnityTest]
        public IEnumerator PausedFirstAttemptKeepsTheOriginalUnprocessedInput()
            => PauseBetweenStaleInputAndAuthorityReply(false, false);

        [UnityTest]
        public IEnumerator PausedRetryCannotOverwriteAnEarlierAcceptedReceipt()
            => PauseBetweenStaleInputAndAuthorityReply(true, true);

        [UnityTest]
        public IEnumerator PausedInputRequiresConfirmationAfterItsOwnConnectionAlsoRecovers()
            => PauseBetweenStaleInputAndAuthorityReply(false, false, reconnectWhilePaused: true);

        [UnityTest]
        public IEnumerator PausedInputStillAcceptsAFinalCoreRejectionAfterResume()
            => PauseBetweenStaleInputAndAuthorityReply(false, false, changeBeforeConfirmation: true);

        private IEnumerator PauseBetweenStaleInputAndAuthorityReply(bool applied, bool keepAcceptedReceipt,
            bool reconnectWhilePaused = false, bool changeBeforeConfirmation = false)
        {
            yield return Wait(() => Enabled(3, "omc-passive"));
            long version = ports[3].Read().SessionVersion;
            var responses = (Queue<HoldemWireResponse>)typeof(HoldemTcpClient)
                .GetField("responses", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[3]);
            if (applied)
            {
                Click(3, "omc-passive");
                yield return WaitWithoutPort(3, () => ports[0].Read().SessionVersion == version + 1);
                if (!keepAcceptedReceipt)
                {
                    yield return WaitWithoutPort(3, () => {
                        clients[3].Poll(); return responses.Any(r => r.type == "receipt" && r.accepted);
                    });
                    while (clients[3].TryReadResponse(out _)) { }
                }
            }

            // The authority knows about this disconnect; this player's displayed lobby does not yet.
            clients[2].Dispose();
            yield return WaitWithoutPort(3, () => ports[0].Lobby.Paused);
            Assert.That(ports[3].Lobby.Paused, Is.False);
            if (applied) { feedbackOffsets[3] = 3000; ports[3].Refresh(); }
            else ports[3].Act(ports[3].Read(), BettingAction.Call());
            yield return WaitWithoutPort(3, () => {
                clients[3].Poll(); return responses.Any(r => r.type == "receipt" && r.error == "Paused");
            });
            if (keepAcceptedReceipt)
                Assert.That(responses.Any(r => r.type == "receipt" && r.accepted), Is.True);

            ports[3].Poll();
            Assert.That(ports[3].Lobby.Paused, Is.True);
            Assert.That(ports[3].ErrorText, Is.Empty, "A room pause is not a verdict on the original input.");
            Assert.That(ports[3].HasPendingInput, Is.EqualTo(!keepAcceptedReceipt));
            Assert.That(ports[3].NeedsRefresh, Is.False);
            Assert.That(ports[3].CanSend, Is.False);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + (applied ? 1 : 0)));
            Assert.That(ports[3].Read().OwnStack, Is.EqualTo(applied ? 98 : 100));

            feedbackOffsets[3] += 60000; // Waiting for somebody else must not consume our next confirmation's timeout.
            if (reconnectWhilePaused)
            {
                clients[3].Dispose(); ports[3].Refresh();
                yield return Wait(() => clients[3].IsAdmitted && ports[3].Lobby.Paused);
                Assert.That(ports[3].HasPendingInput, Is.True);
                Assert.That(ports[3].NeedsRefresh, Is.False);
            }
            ports[2].Refresh();
            yield return Wait(() => clients.All(c => c.IsAdmitted) && ports.All(p => !p.Lobby.Paused));
            if (!keepAcceptedReceipt)
            {
                Assert.That(ports[3].HasPendingInput, Is.True, "Resuming does not silently invent or confirm an action.");
                Assert.That(ports[3].NeedsRefresh, Is.True);
                Assert.That(ports[3].RefreshLabel, Is.EqualTo("입력 확인"), "A healthy transport does not need replacing after a room pause.");
                if (changeBeforeConfirmation)
                {
                    // An authoritative action supersedes this unprocessed intent, still in the same hand.
                    clients[3].Send(new HoldemWireRequest { type = "act", handId = ports[3].Read().HandId.ToString("N"),
                        version = version, action = (int)BettingActionKind.Fold });
                    yield return Wait(() => ports[3].Read().SessionVersion == version + 1);
                    Assert.That(ports[3].HasPendingInput, Is.True);
                }
                yield return Wait(() => Enabled(3, "omc-retry"));
                Click(3, "omc-retry");
                Assert.That(ports[3].HasPendingInput, Is.True);
                Assert.That(ports[3].NeedsRefresh, Is.False, "A manual confirmation starts a fresh response wait.");
                Assert.That(clients[3].IsClosed, Is.False);
                yield return Wait(() => !ports[3].HasPendingInput);
            }
            Assert.That(ports[3].CanSend, Is.True);
            if (changeBeforeConfirmation) Assert.That(ports[3].ErrorText,
                Is.EqualTo(Poker.Presentation.KoreanPokerText.CommandErrorMessage(HoldemCommandError.VersionMismatch)));
            else Assert.That(ports[3].ErrorText, Is.Empty);
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Read().OwnStack, Is.EqualTo(changeBeforeConfirmation ? 100 : 98));
            var history = ports[3].ReadHistory();
            Assert.That(Enumerable.Range(0, history.Count).Select(history.GetEntry)
                .Count(e => e.Kind == HoldemHistoryKind.Action && e.Seat.Value == 4), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator OldUnconfirmedFoldIsNotReportedAsRejectedAfterTheNextHand()
        {
            yield return Wait(() => Enabled(3, "omc-fold"));
            Click(3, "omc-fold");
            yield return WaitWithoutPort(3, () => ports[0].Read().SessionVersion == 2);
            foreach (int actor in new[] { 0, 1 })
            {
                long version = ports[actor].Read().SessionVersion;
                ports[actor].Act(ports[actor].Read(), BettingAction.Fold());
                yield return WaitWithoutPort(3, () => ports[0].Read().SessionVersion > version && ports[actor].CanSend);
            }
            Assert.That(ports[0].Read().Result, Is.Not.Null);
            ports[0].NextHand(ports[0].Read());
            yield return WaitWithoutPort(3, () => ports[0].Read().HandNumber == 2);
            Assert.That(ports[3].HasPendingInput, Is.True);
            clients[3].Poll(); while (clients[3].TryReadResponse(out _)) { }
            ports[3].Poll();
            Assert.That(ports[3].Read().HandNumber, Is.EqualTo(2));
            Assert.That(ports[3].HasPendingInput, Is.False);
            Assert.That(ports[3].ErrorText, Does.Contain("이전 판의 입력 결과는 확인하지 못했어요"));
            long current = ports[3].Read().SessionVersion;
            ports[3].Refresh();
            for (int i = 0; i < 8; i++) { Tick(); yield return null; }
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(current));
            Assert.That(ports[3].ErrorText, Does.Contain("이전 판의 입력 결과는 확인하지 못했어요"));
        }

        [UnityTest]
        public IEnumerator AStartedHandConfirmsTheHostRequestEvenWhenItsReceiptIsLost()
        {
            foreach (int actor in new[] { 3, 0, 1 })
            {
                yield return Wait(() => Enabled(actor, "omc-fold"));
                long version = ports[actor].Read().SessionVersion;
                Click(actor, "omc-fold");
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
            }
            yield return Wait(() => ports[0].CanSend);
            Assert.That(ports[0].Read().Result, Is.Not.Null);
            ports[0].NextHand(ports[0].Read());
            ports[0].Poll();
            Assert.That(ports[0].HasPendingInput, Is.True, "The previous completed hand cannot retire a next-hand intent.");
            yield return WaitWithoutPort(0, () => ports[1].Read().HandNumber == 2);
            clients[0].Poll(); while (clients[0].TryReadResponse(out _)) { }
            ports[0].Poll();
            Assert.That(ports[0].Read().HandNumber, Is.EqualTo(2));
            Assert.That(ports[0].HasPendingInput, Is.False);
            Assert.That(ports[0].ErrorText, Is.Empty);
            Assert.That(ports[0].CanSend, Is.True);
        }

        private void Capture(int seat, string phase)
        {
            string directory = Environment.GetEnvironmentVariable("OMC_CAPTURE_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            var prior = RenderTexture.active; var texture = textures[seat];
            var capture = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture; capture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); capture.Apply();
                Assert.That(capture.GetPixels32().Distinct().Count(), Is.GreaterThan(100));
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "multiplayer-" + phase + "-seat" + (seat + 1) + ".png"), capture.EncodeToPNG());
            }
            finally { RenderTexture.active = prior; UnityEngine.Object.Destroy(capture); }
        }
        [UnityTest]
        public IEnumerator DisposingRemotePortClosesItsBindingAndPausesTheRoom()
        {
            ports[3].Dispose();
            yield return Wait(() => clients[0].Latest.paused);
            Assert.That(clients[3].IsClosed, Is.True);
            Assert.That(ports[0].CanSend, Is.False);
        }
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
    }
}
