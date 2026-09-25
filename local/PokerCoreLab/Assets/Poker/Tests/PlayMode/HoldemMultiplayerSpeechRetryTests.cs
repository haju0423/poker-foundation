using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerScreenTests
    {
        [UnityTest]
        public IEnumerator SpeechRetryClicksWaitWithoutResendingOrBlockingPoker()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            const int viewer = 3;
            var field = roots[viewer].Q<TextField>("omc-utterance-input");
            var button = roots[viewer].Q<Button>("omc-utterance-send");
            field.value = "응답이 늦어도 한 번만 접수";
            yield return Wait(() => Enabled(viewer, "omc-utterance-send"));
            var before = Json(viewer);
            var retained = ports[viewer];
            var responses = (Queue<HoldemWireResponse>)typeof(HoldemTcpClient)
                .GetField("responses", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[viewer]);
            while (clients[viewer].TryReadResponse(out _)) { }
            // Keep real TCP/server traffic running, but hold the UI consumer's confirmations.
            // No connection disposal, frame mutation or production rate-limit change is involved.
            ports[viewer] = null;
            try
            {
                double sentAt = Time.realtimeSinceStartupAsDouble;
                Click(viewer, "omc-utterance-send");
                for (int i = 0; i < 12; i++) ClickSpeechRegardlessOfEnabled(viewer, button);
                Assert.That(button.enabledInHierarchy, Is.False);
                Assert.That(button.text, Is.EqualTo("확인 중…"));
                Assert.That(field.isReadOnly, Is.True);
                Assert.That(retained.HasPendingUtterance, Is.True);
                Assert.That(retained.CanSend, Is.True);
                Assert.That(Enabled(viewer, "omc-passive"), Is.True, "Speech waiting must not block poker.");

                Click(viewer, "omc-help");
                yield return Wait(() => responses.Any(r => r.type == "utterance-receipt"));
                double drainedAt = Time.realtimeSinceStartupAsDouble + 0.2;
                while (Time.realtimeSinceStartupAsDouble < drainedAt) { Tick(); yield return null; }
                Assert.That(responses.Count(r => r.type == "utterance-receipt"), Is.EqualTo(1),
                    "Pointer retries while awaiting a response must not enqueue duplicate sends.");
                while (Time.realtimeSinceStartupAsDouble < sentAt + 3.2) { Tick(); yield return null; }
                Assert.That(button.enabledInHierarchy, Is.False, "Elapsed delay must not bypass a modal.");
                Assert.That(responses.Count(r => r.type == "utterance-receipt"), Is.EqualTo(1),
                    "Elapsed delay refreshes the control but must never send automatically.");
                Click(viewer, "omc-close-help");
                yield return Wait(() => Enabled(viewer, "omc-utterance-send"));
                Assert.That(button.text, Is.EqualTo("접수 확인"));
                Click(viewer, "omc-utterance-send");
                yield return Wait(() => responses.Count(r => r.type == "utterance-receipt") == 2);
                var receipts = responses.Where(r => r.type == "utterance-receipt").ToArray();
                Assert.That(receipts.All(r => r.accepted), Is.True);
                Assert.That(receipts[1].id, Is.EqualTo(receipts[0].id));
                Assert.That(clients[viewer].Latest.ownUtterances.entries.Length, Is.EqualTo(1));

                ports[viewer] = retained;
                yield return Wait(() => !retained.HasPendingUtterance && field.value.Length == 0);
                Assert.That(retained.Utterances.ReadUtterances().Count, Is.EqualTo(1));
                Assert.That(Json(viewer), Is.EqualTo(before));
                Assert.That(clients.All(c => !c.IsClosed), Is.True);
                field.value = "확인 후 새 멘트";
                Assert.That(button.enabledInHierarchy, Is.True,
                    "A real receipt clears the retry delay immediately, without waiting another three seconds.");
                Assert.That(button.text, Is.EqualTo("보내기"));
            }
            finally { ports[viewer] = retained; }
        }

        private void ClickSpeechRegardlessOfEnabled(int viewer, Button button)
        {
            Vector2 point = button.worldBound.center;
            var picked = roots[viewer].panel.Pick(point);
            Assert.That(picked == button || button.Contains(picked), Is.True);
            using (var down = PointerDownEvent.GetPooled(new Event
                { type = EventType.MouseDown, button = 0, mousePosition = point, clickCount = 1 }))
            { down.target = picked; picked.SendEvent(down); }
            using (var up = PointerUpEvent.GetPooled(new Event
                { type = EventType.MouseUp, button = 0, mousePosition = point, clickCount = 1 }))
            { up.target = picked; picked.SendEvent(up); }
        }
    }
}
