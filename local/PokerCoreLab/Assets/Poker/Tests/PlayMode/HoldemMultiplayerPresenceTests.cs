using System.Collections;
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
        private Label ConnectionSeatStatus(int viewer, int seat)
            => roots[viewer].Q<VisualElement>("omc-seat-" + seat).Q<Label>(className: "omc-seat-status");
        private Label ConnectionSeatBadge(int viewer, int seat)
            => roots[viewer].Q<VisualElement>("omc-seat-" + seat).Q<Label>(className: "omc-seat-connection");
        private bool ShowsDisconnected(int viewer, int seat)
            => ConnectionSeatBadge(viewer, seat).resolvedStyle.display != DisplayStyle.None;

        [UnityTest]
        public IEnumerator DisconnectedAllInSeatIsIdentifiableWithoutFoldingOrLosingItsCards()
        {
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); UnityEngine.Object.Destroy(old);
            }
            yield return StartGame(new StableRandom());
            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().GetSeat(new SeatId(4)).Status == HoldemSeatStatus.AllIn));
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            var cards = Enumerable.Range(0, 2).Select(boots[3].Connection.Remote.Read().GetOwnCard).ToArray();
            Client(3).Dispose(); yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            yield return Wait(() => Enumerable.Range(0, 4).All(i => ShowsDisconnected(i, 4)));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ConnectionSeatStatus(i, 4).text, Is.EqualTo("올인"));
                Assert.That(boots[i].Connection.Remote.Read().GetSeat(new SeatId(4)).Status, Is.EqualTo(HoldemSeatStatus.AllIn));
                Assert.That(boots[i].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            }
            yield return null; yield return null;
            Capture(0, "disconnected-all-in-small"); Capture(3, "own-disconnected-small");
            var status = ConnectionSeatBadge(0, 4);
            Assert.That(status.worldBound.xMax, Is.LessThanOrEqualTo(roots[0].Q("omc-seat-4").worldBound.xMax));
            yield return Click(3, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
            yield return Wait(() => Enumerable.Range(0, 4).All(i => !ShowsDisconnected(i, 4)));
            Assert.That(Enumerable.Range(0, 2).Select(boots[3].Connection.Remote.Read().GetOwnCard), Is.EqualTo(cards));
            Assert.That(boots[3].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(ConnectionSeatStatus(0, 4).text, Is.EqualTo("올인"));
        }

        [UnityTest]
        public IEnumerator AnOfflineViewerDoesNotPresentStalePeerPresenceAsCurrent()
        {
            yield return StartGame(new StableRandom());
            Client(2).Dispose(); yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            yield return Wait(() => boots[0].Connection.Remote.IsSeatDisconnected(new SeatId(3)));
            Client(0).Dispose();
            yield return Wait(() => boots[0].Connection.Remote.IsSeatDisconnected(new SeatId(1)));
            Assert.That(boots[0].Connection.Remote.IsSeatDisconnected(new SeatId(3)), Is.False,
                "Without a fresh connection, the old snapshot cannot certify another participant's current presence.");
            Assert.That(boots[1].Connection.Remote.Read().GetSeat(new SeatId(3)).Status, Is.EqualTo(HoldemSeatStatus.Active));
        }

        [UnityTest]
        public IEnumerator DisconnectedEliminatedSeatStaysEliminatedAndDoesNotBlockTheNextHand()
        {
            yield return StartGame(new PresenceRandom(1295));
            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            yield return Click(0, "omc-fold");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 2);
            yield return Click(1, "omc-passive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 3);
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            int eliminated = Client(0).Latest.game.seats.Single(s => s.stack == 0).seat;
            Assert.That(eliminated, Is.Not.EqualTo(1));
            Client(eliminated - 1).Dispose();
            yield return Wait(() => ShowsDisconnected(0, eliminated));
            Assert.That(ConnectionSeatStatus(0, eliminated).text, Does.StartWith("탈락 · 받은 칩"));
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.False);
            // Let the existing short result/reveal input lock finish before asserting next-hand availability.
            yield return Wait(() => roots[0].Q<Button>("omc-next").enabledInHierarchy);
            yield return Click(0, "omc-next");
            yield return Wait(() => boots[0].Connection.Remote.Read().HandNumber == 2);
            Assert.That(ConnectionSeatStatus(0, eliminated).text, Is.EqualTo("탈락"));
            Assert.That(ShowsDisconnected(0, eliminated), Is.True);
            Assert.That(boots[0].Connection.Remote.Read().GetSeat(new SeatId(eliminated)).WasDealtIn, Is.False);
            Assert.That(boots[0].Connection.Remote.CanSend, Is.True);
        }

        [UnityTest]
        public IEnumerator DisconnectedWinnerKeepsItsVisiblePayoutAlongsideTheConnectionBadge()
        {
            var old = textures[0]; textures[0] = new RenderTexture(960, 640, 0);
            textures[0].Create(); panels[0].targetTexture = textures[0]; old.Release(); UnityEngine.Object.Destroy(old);
            yield return StartGame(new PresenceRandom(1295));
            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            yield return Click(0, "omc-fold"); yield return Wait(() => Client(0).Latest.game.currentSeat == 2);
            yield return Click(1, "omc-passive"); yield return Wait(() => Client(0).Latest.game.currentSeat == 3);
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            var winner = Client(0).Latest.game.seats.First(s => s.awarded > 0 && s.seat != 1);
            string payout = ConnectionSeatStatus(0, winner.seat).text;
            Assert.That(payout, Is.EqualTo("받은 칩 " + Poker.Presentation.KoreanPokerText.Chips(winner.awarded)));
            Client(winner.seat - 1).Dispose();
            yield return Wait(() => ShowsDisconnected(0, winner.seat));
            Assert.That(ConnectionSeatStatus(0, winner.seat).text, Is.EqualTo(payout));
            Assert.That(boots[0].Connection.Remote.Lobby.Paused, Is.True);
            yield return null; yield return null; Capture(0, "disconnected-winner-small");
            var badge = ConnectionSeatBadge(0, winner.seat);
            Assert.That(badge.worldBound.xMax, Is.LessThanOrEqualTo(roots[0].Q("omc-seat-" + winner.seat).worldBound.xMax));
        }

        private sealed class PresenceRandom : IRandomSource
        {
            private readonly System.Random source;
            public PresenceRandom(int seed) { source = new System.Random(seed); }
            public int NextInt(int exclusiveMax) => source.Next(exclusiveMax);
        }
    }
}
