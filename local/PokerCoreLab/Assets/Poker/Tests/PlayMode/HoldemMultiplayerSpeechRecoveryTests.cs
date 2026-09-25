using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerScreenTests
    {
        [UnityTest]
        public IEnumerator PausedSpeechRetryRecoversAnAlreadyAcceptedUtterance()
            => PauseDuringSpeechConfirmation(true, false);

        [UnityTest]
        public IEnumerator PausedSpeechKeepsItsOriginalTextUntilExplicitConfirmation()
            => PauseDuringSpeechConfirmation(false, false);

        [UnityTest]
        public IEnumerator PausedSpeechStillWaitsForConfirmationAfterSenderReconnects()
            => PauseDuringSpeechConfirmation(false, true);

        [UnityTest]
        public IEnumerator PausedSpeechWaitsForFreshStateWhenTheReceiptArrivesFirst()
            => PauseDuringSpeechConfirmation(false, false, receiptBeforeState: true);

        [UnityTest]
        public IEnumerator PausedSpeechCanStillReceiveAFinalLimitRejection()
            => PauseDuringSpeechConfirmation(false, false, afterResume: "limit");

        [UnityTest]
        public IEnumerator PausedSpeechIsNotCarriedIntoTheNextInputWindow()
            => PauseDuringSpeechConfirmation(false, false, afterResume: "window");

        [UnityTest]
        public IEnumerator PausedSpeechRejectsContradictoryReceiptFields()
            => PauseDuringSpeechConfirmation(false, false, malformedReceipt: true);

        private IEnumerator PauseDuringSpeechConfirmation(bool accepted, bool reconnectSender,
            bool receiptBeforeState = false, string afterResume = null, bool malformedReceipt = false)
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            var field = roots[3].Q<TextField>("omc-utterance-input");
            field.value = "한 번만 보내는 멘트";
            yield return Wait(() => Enabled(3, "omc-utterance-send"));
            var before = Json(3);
            var stalePacket = clients[3].Latest;
            var responses = (Queue<HoldemWireResponse>)typeof(HoldemTcpClient)
                .GetField("responses", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[3]);
            if (accepted)
            {
                Click(3, "omc-utterance-send");
                yield return WaitWithoutPort(3, () => {
                    clients[3].Poll();
                    return responses.Any(r => r.type == "utterance-receipt" && r.accepted);
                });
                while (clients[3].TryReadResponse(out _)) { }
                Assert.That(ports[3].HasPendingUtterance, Is.True);
            }
            clients[2].Dispose();
            yield return WaitWithoutPort(3, () => ports[0].Lobby.Paused);
            Assert.That(ports[3].Lobby.Paused, Is.False);
            yield return WaitWithoutPort(3, () => Enabled(3, "omc-utterance-send"));
            Click(3, "omc-utterance-send");
            yield return WaitWithoutPort(3, () => {
                clients[3].Poll();
                return responses.Any(r => r.type == "utterance-receipt" && r.error == "Paused");
            });
            string commandId = responses.Last(r => r.type == "utterance-receipt" && r.error == "Paused").id;
            if (malformedReceipt)
            {
                responses.Last(r => r.type == "utterance-receipt" && r.error == "Paused").hasReceipt = true;
                ports[3].Poll();
                Assert.That(clients[3].IsClosed, Is.True);
                Assert.That(ports[3].HasPendingUtterance, Is.True);
                Assert.That(ports[3].Utterances.CanInteract, Is.False);
                Assert.That(field.value, Is.EqualTo("한 번만 보내는 멘트"));
                yield break;
            }
            if (receiptBeforeState)
            {
                // Isolate the legal delivery order where the pause receipt reaches the consumer
                // before the corresponding state frame, without altering the authoritative room.
                var latestField = typeof(HoldemTcpClient).GetField("<Latest>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var pausedPacket = clients[3].Latest;
                latestField.SetValue(clients[3], stalePacket);
                try
                {
                    ports[3].Poll();
                    Assert.That(ports[3].Lobby.Paused, Is.False);
                    Assert.That(ports[3].HasPendingUtterance, Is.True);
                    Assert.That(ports[3].Utterances.CanInteract, Is.False,
                        "The old unpaused display cannot authorize another send after a pause receipt.");
                }
                finally { latestField.SetValue(clients[3], pausedPacket); }
            }
            ports[3].Poll();
            Assert.That(ports[3].Lobby.Paused, Is.True);
            Assert.That(ports[3].HasPendingUtterance, Is.EqualTo(!accepted));
            Assert.That(field.value, Is.EqualTo(accepted ? "" : "한 번만 보내는 멘트"));
            Assert.That(roots[3].Q<Label>("omc-utterance-status").text, Does.Not.Contain("접수하지 못"));
            Assert.That(Json(3), Is.EqualTo(before), "Speech confirmation must not change the poker hand.");
            if (reconnectSender)
            {
                clients[3].Dispose(); ports[3].Refresh();
                yield return Wait(() => clients[3].IsAdmitted && ports[3].Lobby.Paused);
                Assert.That(ports[3].HasPendingUtterance, Is.True);
            }
            ports[2].Refresh();
            yield return Wait(() => clients.All(c => c.IsAdmitted) && ports.All(p => !p.Lobby.Paused));
            for (int i = 0; i < 5; i++) { Tick(); yield return null; }
            if (afterResume == "window")
            {
                for (int step = 0; step < 4; step++)
                {
                    int actor = clients[0].Latest.game.currentSeat - 1;
                    long version = clients[0].Latest.game.version;
                    yield return Wait(() => Enabled(actor, "omc-passive"));
                    Click(actor, "omc-passive");
                    yield return Wait(() => ports.All(p => p.Read().SessionVersion > version));
                }
                yield return Wait(() => !ports[3].HasPendingUtterance && field.value.Length == 0);
                Assert.That(ports[3].Utterances.ReadUtterances().Count, Is.Zero);
                Assert.That(roots[3].Q<Label>("omc-utterance-status").text, Does.Contain("입력 시간이 지나"));
                yield break;
            }
            if (!accepted)
            {
                Assert.That(ports[3].HasPendingUtterance, Is.True);
                Assert.That(ports[3].Utterances.ReadUtterances().Count, Is.Zero,
                    "Resume/reconnect must not silently send an explicitly paused utterance.");
                if (afterResume == "limit")
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var ownView = ports[3].Utterances.ReadUtterances();
                        clients[3].Send(new HoldemWireRequest { type = "utterance", handId = ownView.HandId.ToString("N"),
                            windowId = ownView.WindowId.ToString("N"), street = (int)ownView.Street, text = "다른 접수 " + i });
                        int count = i + 1;
                        yield return Wait(() => ports[3].Utterances.ReadUtterances().Count == count);
                    }
                }
                yield return Wait(() => Enabled(3, "omc-utterance-send"));
                Assert.That(roots[3].Q<Button>("omc-utterance-send").text, Is.EqualTo("접수 확인"));
                Click(3, "omc-utterance-send");
                yield return Wait(() => !ports[3].HasPendingUtterance);
                if (afterResume == "limit")
                {
                    Assert.That(roots[3].Q<Label>("omc-utterance-status").text, Does.Contain("모두 보냈어요"));
                    Assert.That(ports[3].Utterances.ReadUtterances().Count, Is.EqualTo(2));
                    Assert.That(roots[3].Q<Button>("omc-utterance-send").text, Is.EqualTo("보내기"));
                    Assert.That(field.enabledInHierarchy, Is.False, "The real quota still applies after a final rejection.");
                    Assert.That(Json(3), Is.EqualTo(before));
                    yield break;
                }
                Assert.That(field.value, Is.Empty);
            }
            // A correlated success receipt may arrive before the following own-history state frame.
            yield return Wait(() => ports[3].Utterances.ReadUtterances().Count == 1);
            var own = ports[3].Utterances.ReadUtterances();
            Assert.That(own.Count, Is.EqualTo(1));
            Assert.That(own.GetEntry(0).CommandId.ToString("N"), Is.EqualTo(commandId));
            Assert.That(own.GetEntry(0).Text, Is.EqualTo("한 번만 보내는 멘트"));
            Assert.That(Json(3), Is.EqualTo(before));
            foreach (int i in new[] { 0, 1, 2 })
                Assert.That(ports[i].Utterances.ReadUtterances().Count, Is.Zero);
        }

        private string Json(int viewer) => UnityEngine.JsonUtility.ToJson(clients[viewer].Latest.game);
    }
}
