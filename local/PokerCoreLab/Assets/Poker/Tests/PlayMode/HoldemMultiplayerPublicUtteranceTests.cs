using System;
using System.Collections;
using System.Linq;
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
        public IEnumerator PublicRemarksShowLiterallyUpdateOpenHistoryAndSurviveReconnectAtBothSizes()
        {
            foreach (int width in new[] { 1200, 960 })
            {
                yield return Cleanup(); yield return SetupLobby(true);
                settings.utteranceVisibility = HoldemUtteranceVisibility.PublicRaw;
                for (int i = 0; i < 4; i++)
                {
                    // Rounded, clipped public buttons require a stencil buffer in offscreen captures.
                    var old = textures[i]; textures[i] = new RenderTexture(width, width == 960 ? 640 : 800, 24);
                    textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); UnityEngine.Object.Destroy(old);
                }
                yield return StartGame(new StableRandom(), new[] { "방장", "같은 이름", "같은 이름", "나" });
                Assert.That(boots.All(b => b.Connection.Remote.RoomRules.PublishesUtterances), Is.True);
                for (int i = 0; i < 4; i++)
                    Assert.That(roots[i].Q<Label>("omc-utterance-scope").text, Does.Contain("모두에게"));
                string raw = "<b>오늘은 사랑이 좀 필요하네요</b> " + new string('가', 80);
                roots[1].Q<TextField>("omc-utterance-input").value = raw;
                long version = boots[0].Connection.Remote.Read().SessionVersion;
                yield return Click(1, "omc-utterance-send");
                yield return Wait(() => boots.All(b => b.Connection.Remote.ReadPublicUtterances()?.Count == 1));
                Assert.That(boots.All(b => b.Connection.Remote.Read().SessionVersion == version), Is.True);
                yield return null; yield return null;
                var remark = roots[0].Q<Button>("omc-public-utterance-2");
                Assert.That(remark.text, Is.EqualTo("프리플랍 · " + raw));
                Assert.That(remark.enableRichText, Is.False); Assert.That(remark.isElided, Is.True);
                Assert.That(remark.tooltip, Does.Contain("같은 이름").And.Contain("2"));
                foreach (var root in roots)
                    foreach (var seat in root.Query<VisualElement>(className: "omc-opponent").ToList())
                    {
                        var button = seat.Q<Button>(className: "omc-public-utterance");
                        Assert.That(button.worldBound.xMin, Is.GreaterThanOrEqualTo(seat.worldBound.xMin));
                        Assert.That(button.worldBound.xMax, Is.LessThanOrEqualTo(seat.worldBound.xMax));
                        Assert.That(seat.worldBound.xMax, Is.LessThanOrEqualTo(root.worldBound.xMax));
                    }
                for (int i = 0; i < 4; i++) AssertFlowLayout(i);
                Capture(0, "public-remarks-table-" + width);
                yield return Click(0, "omc-public-utterance-2");
                var copy = roots[0].Q<Label>("omc-history-copy");
                Assert.That(copy.text, Does.Contain(raw)); Assert.That(copy.enableRichText, Is.False);
                var actionState = boots[3].Connection.Remote.Read();
                yield return Click(3, "omc-passive");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > actionState.SessionVersion));
                roots[2].Q<TextField>("omc-utterance-input").value = "의미 없는 말도 원문 그대로";
                yield return Click(2, "omc-utterance-send");
                yield return Wait(() => copy.text.Contains("의미 없는 말도 원문 그대로"));
                Assert.That(copy.text, Does.Contain("베팅과 공개 멘트 · 방장이 접수한 순서"));
                Assert.That(copy.text.IndexOf("BB "), Is.LessThan(copy.text.IndexOf(raw)));
                Assert.That(copy.text.IndexOf(raw), Is.LessThan(copy.text.IndexOf("콜 ·")));
                Assert.That(copy.text.IndexOf("콜 ·"), Is.LessThan(copy.text.IndexOf("의미 없는 말도 원문 그대로")));
                yield return null; yield return null; Capture(0, "public-remarks-history-" + width);
                yield return Click(0, "omc-close-history");
                Client(1).Dispose(); yield return Wait(() => Client(0).Latest.paused);
                yield return Click(0, "omc-history"); Assert.That(copy.text, Does.Contain(raw));
                yield return Click(0, "omc-close-history");
                yield return Click(1, "omc-room-reconnect");
                yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
                Assert.That(boots[1].Connection.Remote.ReadPublicUtterances().Count, Is.EqualTo(2));
                for (int step = 0; step < 60 && !Client(0).Latest.game.hasResult; step++)
                {
                    var game = Client(0).Latest.game;
                    if (game.dealPending) yield return Click(0, "omc-release-deal");
                    else if (game.revealPending) yield return Click(0, "omc-continue-reveal");
                    else if (game.currentSeat == 0) yield return Click(0, "omc-resolve");
                    else yield return Click(game.currentSeat - 1, "omc-passive");
                    yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > game.version));
                }
                Assert.That(Client(0).Latest.game.hasResult, Is.True);
                yield return null; yield return null;
                for (int i = 0; i < 4; i++) AssertFlowLayout(i);
                foreach (var batch in boots[0].Connection.ReadPendingUtterances())
                    Assert.That(boots[0].Connection.AcknowledgeUtteranceBatch(batch.WindowId), Is.True);
                Assert.That(boots.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 2), Is.True);
                yield return Click(0, "omc-next");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == 2));
                Assert.That(boots.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 0), Is.True);
                Assert.That(roots[0].Q<Button>("omc-public-utterance-2").text, Is.EqualTo("멘트 없음"));
            }
        }
    }

    public sealed partial class HoldemMultiplayerScreenTests
    {
        [UnityTest]
        public IEnumerator OrderedHistorySeparatesOmittedRemarksAndKeepsExactTailBoundary()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(64, 2, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw),
                new HoldemConfig(10000, 1, 2));
            yield return SendRemark("처음에 보낸 멘트", 1);
            for (int i = 0; i < 72; i++)
            {
                if (i == 8) yield return SendRemark("남은 기록 바로 앞 멘트", 2);
                int actor = clients[0].Latest.game.currentSeat - 1;
                var basis = ports[actor].Read();
                ports[actor].Act(basis, BettingAction.RaiseTo(basis.LegalActions.MinimumAggressiveTarget.Value));
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > basis.SessionVersion));
            }
            yield return Wait(() => Enabled(0, "omc-history")); Click(0, "omc-history");
            yield return null; yield return null;
            string copy = roots[0].Q<Label>("omc-history-copy").text;
            Assert.That(copy, Does.Contain("앞부분의 공개 멘트 · 해당 베팅 기록은 생략"));
            Assert.That(copy.IndexOf("처음에 보낸 멘트"), Is.LessThan(copy.IndexOf("최근 64개 기록")));
            Assert.That(copy.IndexOf("남은 기록 바로 앞 멘트"), Is.GreaterThan(copy.IndexOf("최근 64개 기록")));
            Assert.That(copy.IndexOf("남은 기록 바로 앞 멘트"), Is.LessThan(copy.IndexOf("레이즈")));
            Assert.That(ports[0].ReadPublicUtterances().GetEntry(1).HistoryPosition, Is.EqualTo(ports[0].ReadHistory().OmittedCount));
            Capture(0, "public-history-order-tail");

            IEnumerator SendRemark(string text, int expected)
            {
                roots[1].Q<TextField>("omc-utterance-input").value = text;
                yield return Wait(() => Enabled(1, "omc-utterance-send")); Click(1, "omc-utterance-send");
                yield return Wait(() => ports.All(p => p.ReadPublicUtterances().Count == expected) && !ports[1].HasPendingUtterance);
            }
        }

        [UnityTest]
        public IEnumerator LegacyWithoutRulesCannotSilentlyAcquirePublicSpeechRules()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active), legacyRules: true);
            var before = ports[1].Read(); var own = ports[1].Utterances.ReadUtterances();
            Assert.That(ports[1].RoomRules, Is.Null);
            var packet = clients[1].Latest; packet.revision++; packet.hasRules = true;
            packet.rules.publishesUtterances = true; packet.hasPublicUtterances = true;
            packet.publicUtterances = new HoldemPublicUtterancePacket { entries = new HoldemPublicUtteranceEntryPacket[0] };
            ports[1].Poll();
            Assert.That(clients[1].IsClosed, Is.True);
            Assert.That(ports[1].Read(), Is.SameAs(before));
            Assert.That(ports[1].RoomRules, Is.Null);
            Assert.That(ports[1].ReadPublicUtterances(), Is.Null);
            Assert.That(ports[1].Utterances.ReadUtterances().Count, Is.EqualTo(own.Count));
        }

        [UnityTest]
        public IEnumerator InvalidPublicFeedCannotPartiallyCommitOwnTextHistoryRulesOrTable()
        {
            foreach (string fault in new[] { "rewrite", "remove", "disable", "private", "own-contradiction", "anchor", "order-flag" })
            {
                yield return Cleanup();
                yield return SetupTable(new HoldemUtterancePolicy(64, 2, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw));
                roots[1].Q<TextField>("omc-utterance-input").value = "유지할 원문";
                yield return Wait(() => Enabled(1, "omc-utterance-send")); Click(1, "omc-utterance-send");
                yield return Wait(() => ports.All(p => p.ReadPublicUtterances()?.Count == 1) && !ports[1].HasPendingUtterance);
                var before = ports[1].Read(); var remarks = ports[1].ReadPublicUtterances();
                var own = ports[1].Utterances.ReadUtterances(); var history = ports[1].ReadHistory(); var rules = ports[1].RoomRules;
                var packet = clients[1].Latest; packet.revision++;
                if (fault == "rewrite")
                {
                    packet.publicUtterances.entries[0].text = "바뀐 원문";
                    var added = new HoldemOwnUtteranceEntryPacket { commandId = Guid.NewGuid().ToString("N"),
                        windowId = packet.ownUtterances.windowId, street = 0, text = "부분 반영되면 안 되는 입력" };
                    packet.ownUtterances.entries = new[] { packet.ownUtterances.entries[0], added };
                    packet.ownUtterances.remaining = 0; packet.ownUtterances.canSubmit = false;
                }
                else if (fault == "remove") packet.publicUtterances.entries = new HoldemPublicUtteranceEntryPacket[0];
                else if (fault == "disable") packet.hasPublicUtterances = false;
                else if (fault == "private") { packet.rules.publishesUtterances = false; packet.hasPublicUtterances = false; }
                else if (fault == "anchor") packet.publicUtterances.entries[0].historyPosition = long.MaxValue;
                else if (fault == "order-flag") packet.publicUtterances.hasHistoryOrder = false;
                else packet.ownUtterances.entries[0].text = "공개 원문과 다른 말";
                ports[1].Poll();
                Assert.That(clients[1].IsClosed, Is.True, fault);
                Assert.That(ports[1].Read(), Is.SameAs(before), fault);
                Assert.That(ports[1].ReadPublicUtterances(), Is.SameAs(remarks), fault);
                Assert.That(ports[1].Utterances.ReadUtterances().Count, Is.EqualTo(own.Count), fault);
                Assert.That(ports[1].Utterances.ReadUtterances().GetEntry(0).Text, Is.EqualTo("유지할 원문"), fault);
                Assert.That(ports[1].ReadHistory(), Is.SameAs(history), fault);
                Assert.That(ports[1].RoomRules, Is.SameAs(rules), fault);
                Assert.That(ports[1].CanSend, Is.False, fault);
            }
        }
    }
}
