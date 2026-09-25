#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator StandardSavedRulesSupportPublicRemarksAndAutomaticPokerForThreeAndFourPlayers()
        {
            foreach (int count in new[] { 3, 4 })
            {
                if (count == 4) { yield return Cleanup(); yield return SetupLobby(false); }
                LoadStandardSpeechSettings();
                for (int i = 0; i < count; i++)
                {
                    var old = textures[i]; textures[i] = new RenderTexture(960, 640, 24);
                    textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); Object.Destroy(old);
                }
                yield return StartStandardRoom(count);
                var players = boots.Take(count).ToArray();
                foreach (int viewer in Enumerable.Range(0, count))
                {
                    Assert.That(roots[viewer].Q<Label>(className: "omc-subtitle").text, Is.EqualTo(count + "인 멀티플레이"));
                    Assert.That(roots[viewer].Q<Label>("omc-utterance-scope").text, Does.Contain("모두에게"));
                    Assert.That(roots[viewer].Q("omc-utterance").tooltip, Does.Contain("AI나 카드에 반영되지"));
                }

                int accepted = 0;
                foreach (var street in new[] { HoldemStreet.Preflop, HoldemStreet.Flop, HoldemStreet.Turn })
                {
                    Assert.That(players[0].Connection.Remote.Read().Street, Is.EqualTo(street));
                    for (int speaker = 0; speaker < count; speaker++)
                    {
                        string raw = street + " · 참가자 " + (speaker + 1) + " <b>사랑이 필요하네요</b>";
                        var before = Client(0).Latest.game;
                        string ledger = JsonUtility.ToJson(before);
                        roots[speaker].Q<TextField>("omc-utterance-input").value = raw;
                        yield return Click(speaker, "omc-utterance-send");
                        accepted++;
                        yield return Wait(() => players.All(b => b.Connection.Remote.ReadPublicUtterances()?.Count == accepted));
                        Assert.That(JsonUtility.ToJson(Client(0).Latest.game), Is.EqualTo(ledger), "Speech must not mutate poker state.");
                        foreach (var player in players)
                        {
                            var feed = player.Connection.Remote.ReadPublicUtterances();
                            var entry = feed.GetEntry(accepted - 1);
                            Assert.That(entry.Speaker.Value, Is.EqualTo(speaker + 1));
                            Assert.That(entry.Street, Is.EqualTo(street));
                            Assert.That(entry.Text, Is.EqualTo(raw));
                            Assert.That(feed.HasHistoryOrder, Is.True);
                            var own = player.Connection.Remote.Utterances.ReadUtterances();
                            for (int n = 0; n < own.Count; n++) Assert.That(own.GetEntry(n).Speaker, Is.EqualTo(own.ViewerSeat));
                        }
                    }
                    yield return null; yield return null;
                    for (int i = 0; i < count; i++) AssertFlowLayout(i);
                    Capture(0, "standard-public-" + count + "-" + street);
                    for (int n = 0; n < count + 2 && players[0].Connection.Remote.Read().Street == street; n++)
                        yield return StandardPassiveStep(count);
                    Assert.That(players[0].Connection.Remote.Read().Street, Is.Not.EqualTo(street));
                }
                Assert.That(players[0].Connection.Remote.Read().Street, Is.EqualTo(HoldemStreet.River));
                yield return null;
                for (int i = 0; i < count; i++)
                {
                    Assert.That(roots[i].Q<TextField>("omc-utterance-input").enabledInHierarchy, Is.False);
                    Assert.That(Client(i).Latest.game.seats.Count(s => s.visibleCards.Length != 0), Is.EqualTo(1));
                }
                for (int n = 0; n < count + 2 && players[0].Connection.Remote.Read().Result == null; n++)
                    yield return StandardPassiveStep(count);
                yield return Wait(() => players.All(b => b.Connection.Remote.Read().Result != null));
                Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(count * 100));
                Assert.That(players.All(b => b.Connection.Remote.ReadPublicUtterances().Count == count * 3), Is.True);
                var batches = boots[0].Connection.ReadPendingUtterances();
                Assert.That(batches, Is.Empty, "Public-only conversation must not accumulate unconsumed dealer work.");
                yield return null; Capture(0, "standard-public-" + count + "-result");
                yield return Click(0, "omc-next");
                yield return Wait(() => players.All(b => b.Connection.Remote.Read().HandNumber == 2));
                Assert.That(players.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 0), Is.True);
                Assert.That(boots[0].Connection.ReadPendingUtterances(), Is.Empty);
            }
        }

        [UnityTest]
        public IEnumerator StandardSavedRulesDoNotRequireAnyoneToSpeakBeforeFinishingAHand()
        {
            LoadStandardSpeechSettings(); yield return StartStandardRoom(4);
            for (int n = 0; n < 3; n++)
            {
                var game = Client(0).Latest.game;
                yield return Click(game.currentSeat - 1, "omc-fold");
                yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > game.version));
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            Assert.That(boots.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 0), Is.True);
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(400));
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().HandNumber == 2));
        }

        private void LoadStandardSpeechSettings()
        {
            var saved = AssetDatabase.LoadAssetAtPath<HoldemMultiplayerSettings>("Assets/Poker/Samples/HoldemMultiplayerSettings.asset");
            Assert.That(saved, Is.Not.Null);
            // The fixture owns its settings; never destroy or mutate the saved asset at teardown.
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(saved), settings);
            Assert.That(settings.enableFlowPreview, Is.False);
            Assert.That(settings.CreateUtterancePolicy().Visibility, Is.EqualTo(HoldemUtteranceVisibility.PublicRaw));
            Assert.That(settings.CreateUtterancePolicy().BatchRetention, Is.EqualTo(HoldemUtteranceBatchRetention.None));
            Assert.That(settings.CreateConfig().DealPolicy, Is.EqualTo(HoldemDealPolicy.Automatic));
            Assert.That(settings.CreateConfig().RevealPolicy, Is.EqualTo(HoldemRevealPolicy.Automatic));
        }

        private IEnumerator StartStandardRoom(int count)
        {
            roots[0].Q<DropdownField>("omc-room-capacity").index = count - 3;
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            for (int i = 1; i < count; i++)
            {
                Fields(i, boots[0].Connection.Port); yield return Click(i, "omc-room-join");
                int viewer = i; yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
            }
            for (int i = 0; i < count; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.Take(count).All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => roots.Take(count).All(r => r.Q<Button>("omc-passive") != null));
        }

        private IEnumerator StandardPassiveStep(int count)
        {
            var game = Client(0).Latest.game;
            Assert.That(game.dealPending, Is.False); Assert.That(game.revealPending, Is.False);
            if (game.currentSeat == 0) yield return Click(0, "omc-resolve");
            else yield return Click(game.currentSeat - 1, "omc-passive");
            yield return Wait(() => boots.Take(count).All(b => b.Connection.Remote.Read().SessionVersion > game.version));
        }
    }
}
#endif
