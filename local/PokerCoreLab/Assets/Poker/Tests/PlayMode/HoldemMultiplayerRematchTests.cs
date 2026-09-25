using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator FourFriendsRematchOnlyAfterFreshConsentAndReconnect()
            => RematchJourney(4, true);

        [UnityTest]
        public IEnumerator ThreeFriendsRematchKeepsTheirSeatsAndCanPlayAgain()
            => RematchJourney(3, false);

        private IEnumerator RematchJourney(int count, bool disconnect)
        {
            var active = Enumerable.Range(0, count).ToArray();
            for (int i = 0; i < count; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); UnityEngine.Object.Destroy(old);
            }
            Assert.That(boots[0].Connection.Host("친구 1", "127.0.0.1", 0, false, settings.CreateConfig(),
                new RematchDeckRandom(count), settings.CreateUtterancePolicy(), count), Is.True);
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            for (int i = 1; i < count; i++)
            {
                Fields(i, boots[0].Connection.Port); yield return Click(i, "omc-room-join");
                int viewer = i; yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
            }
            foreach (int i in active) yield return Click(i, "omc-room-ready");
            yield return Wait(() => active.All(i => boots[i].Connection.Remote.Lobby.AllReady && !boots[i].Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => active.All(i => boots[i].Connection.Remote.HasGame));
            for (int step = 0; step < 8 && !boots[0].Connection.Remote.Read().IsOver; step++)
            {
                int viewer = boots[0].Connection.Remote.Read().CurrentSeat.Value.Value - 1;
                yield return Wait(() => boots[viewer].Connection.Remote.CanSend);
                var basis = boots[viewer].Connection.Remote.Read();
                bool aggressive = basis.LegalActions.CanRaise || basis.LegalActions.CanBet;
                if (aggressive) yield return Click(viewer, "omc-max");
                yield return Click(viewer, aggressive ? "omc-aggressive" : "omc-passive");
                yield return Wait(() => active.All(i => boots[i].Connection.Remote.Read().SessionVersion > basis.SessionVersion));
            }
            Assert.That(active.All(i => boots[i].Connection.Remote.Read().IsOver), Is.True);
            var final = boots[0].Connection.Remote.Read();
            var histories = active.Select(i => boots[i].Connection.Remote.ReadHistory()).ToArray();
            var historyCounts = histories.Select(h => h.Count).ToArray();
            Capture(0, "rematch-result-" + count + "p");
            foreach (int i in active) yield return Click(i, "omc-room-rematch");
            Assert.That(roots[0].Q<Button>("omc-rematch-start").enabledInHierarchy, Is.False);
            foreach (int i in active) yield return Click(i, "omc-rematch-ready");
            yield return Wait(() => active.All(i => boots[i].Connection.Remote.Lobby.AllRematchReady
                && !boots[i].Connection.Remote.HasPendingInput));
            yield return Click(1, "omc-rematch-ready");
            yield return Wait(() => !boots[0].Connection.Remote.Lobby.AllRematchReady);
            Assert.That(roots[0].Q<Button>("omc-rematch-start").enabledInHierarchy, Is.False);
            Assert.That(boots[0].Connection.Remote.Read().HandId, Is.EqualTo(final.HandId));
            yield return Click(1, "omc-rematch-ready");
            yield return Wait(() => boots[0].Connection.Remote.Lobby.AllRematchReady);
            if (disconnect)
            {
                Client(1).Dispose();
                yield return Wait(() => !boots[0].Connection.Remote.Lobby.AllRematchReady);
                Assert.That(boots[0].Connection.Remote.Read().HandId, Is.EqualTo(final.HandId));
                yield return Click(1, "omc-rematch-close");
                yield return Click(1, "omc-room-reconnect");
                yield return Wait(() => boots[1].Connection.Remote.CanSend);
                Assert.That(boots[1].Connection.Remote.Lobby.IsRematchReady, Is.False);
                yield return Click(1, "omc-room-rematch");
                yield return Click(1, "omc-rematch-ready");
                yield return Wait(() => active.All(i => boots[i].Connection.Remote.Lobby.AllRematchReady));
            }
            yield return null; yield return null;
            Capture(0, "rematch-ready-" + count + "p"); Capture(1, "rematch-guest-" + count + "p");
            foreach (int i in active)
            {
                var button = roots[i].Q<Button>("omc-rematch-ready");
                Assert.That(button.worldBound.yMax, Is.LessThan(roots[i].worldBound.yMax));
                Assert.That(Pickable(i, "omc-rematch-ready"), Is.True);
                Assert.That(boots[i].Connection.Remote.Read().HandId, Is.EqualTo(final.HandId));
            }
            yield return Click(0, "omc-rematch-start");
            yield return Wait(() => active.All(i => boots[i].Connection.Remote.Lobby.MatchNumber == 2
                && !boots[i].Connection.Remote.HasPendingInput));
            foreach (int i in active)
            {
                var remote = boots[i].Connection.Remote; var view = remote.Read();
                Assert.That(view.SessionId, Is.EqualTo(final.SessionId));
                Assert.That(view.HandNumber, Is.EqualTo(final.HandNumber + 1));
                Assert.That(view.SessionVersion, Is.EqualTo(final.SessionVersion + 1));
                Assert.That(view.ViewerSeat.Value, Is.EqualTo(i + 1));
                Assert.That(view.OwnCardCount, Is.EqualTo(2));
                Assert.That(view.OwnStack + view.GetSeat(view.ViewerSeat).Committed, Is.EqualTo(100));
                Assert.That(remote.Lobby.IsRematchReady, Is.False);
                Assert.That(remote.ReadHistory().Count, Is.EqualTo(2));
                Assert.That(histories[i].Count, Is.EqualTo(historyCounts[i]), "Old display history is immutable.");
                Assert.That(roots[i].Q("omc-rematch").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                Assert.That(remote.ErrorText, Is.Empty);
            }
            int actor = boots[0].Connection.Remote.Read().CurrentSeat.Value.Value - 1;
            long version = boots[actor].Connection.Remote.Read().SessionVersion;
            yield return Click(actor, "omc-passive");
            yield return Wait(() => active.All(i => boots[i].Connection.Remote.Read().SessionVersion > version));
            yield return null; yield return null; Capture(0, "rematch-playing-" + count + "p");
        }

        private sealed class RematchDeckRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public RematchDeckRandom(int count)
            {
                string holes = count == 4 ? "Kh Qh Jh Ah Kd Qd Jd Ad" : "Kh Qh Ah Kd Qd Ad";
                var target = (holes + " 2c 3c 5d 8s 4h 9s 6h Tc").Split(' ')
                    .Select(s => new Card((Rank)("23456789TJQKA".IndexOf(s[0]) + 2), (Suit)("cdhs".IndexOf(s[1]) + 1))).ToList();
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)));
                var working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                { int j = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(j); var old = working[i]; working[i] = working[j]; working[j] = old; }
            }
            public int NextInt(int exclusiveMax) => choices.Count > 0 ? choices.Dequeue() : exclusiveMax - 1;
        }
    }
}
