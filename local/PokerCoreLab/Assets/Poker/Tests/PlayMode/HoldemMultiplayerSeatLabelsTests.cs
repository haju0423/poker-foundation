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
        [UnityTest]
        public IEnumerator LongDuplicateNamesKeepTheSeatPrefixAndFullTooltipAtSmallSize()
        {
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); Object.Destroy(old);
            }
            string longName = new string('가', 24);
            yield return StartGame(new PresenceRandom(1295), new[] { "주하", longName, longName, "친구" });
            yield return null; yield return null;
            for (int viewer = 0; viewer < 4; viewer++)
                foreach (int seat in new[] { 2, 3 })
                {
                    if (viewer + 1 == seat) continue;
                    var box = roots[viewer].Q("omc-seat-" + seat);
                    var title = box.Q<Label>(className: "omc-seat-title");
                    Assert.That(title.text, Is.EqualTo(seat + "번 · " + longName));
                    Assert.That(title.isElided, Is.True);
                    Assert.That(title.tooltip, Does.StartWith(seat + "번 · " + longName));
                    Assert.That(title.worldBound.xMax, Is.LessThanOrEqualTo(box.worldBound.xMax));
                }
            Capture(0, "duplicate-long-names-small");
        }

        [UnityTest]
        public IEnumerator DuplicateNamesStayDistinctThroughReconnectShowdownHistoryAndElimination()
        {
            string[] names = { "본인", "친구 2번", "친구 2번", "나" };
            string[] labels = { "본인", "2번 · 친구 2번", "3번 · 친구 2번", "4번 · 나" };
            yield return StartGame(new PresenceRandom(1295), names);
            AssertSeatTitles(labels);
            for (int viewer = 0; viewer < 3; viewer++)
                Assert.That(roots[viewer].Q<Label>(className: "omc-prompt").text, Does.Contain("4번 · 나"));

            long version = boots[2].Connection.Remote.Read().SessionVersion;
            var cards = Enumerable.Range(0, 2).Select(boots[2].Connection.Remote.Read().GetOwnCard).ToArray();
            Client(2).Dispose();
            yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            AssertSeatTitles(labels);
            yield return Click(2, "omc-room-reconnect");
            yield return Wait(() => boots.All(b => b.Connection.Remote.CanSend));
            AssertSeatTitles(labels);
            Assert.That(boots[2].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            Assert.That(Enumerable.Range(0, 2).Select(boots[2].Connection.Remote.Read().GetOwnCard), Is.EqualTo(cards));
            for (int i = 0; i < 4; i++)
                Assert.That(boots[0].Connection.Remote.Read().GetSeat(new SeatId(i + 1)).Name, Is.EqualTo(names[i]));

            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            Assert.That(roots[0].Q<Label>(className: "omc-last").text, Does.Contain("4번 · 나"));
            yield return Click(0, "omc-fold");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 2);
            Assert.That(roots[0].Q<Label>(className: "omc-prompt").text, Does.Contain("2번 · 친구 2번"));
            yield return Click(1, "omc-passive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 3);
            Assert.That(roots[0].Q<Label>(className: "omc-last").text, Does.Contain("2번 · 친구 2번"));
            Assert.That(roots[0].Q<Label>(className: "omc-prompt").text, Does.Contain("3번 · 친구 2번"));
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            AssertSeatTitles(labels);
            Assert.That(Client(0).Latest.game.result.winnerSeat, Is.EqualTo(4));

            for (int viewer = 0; viewer < 4; viewer++)
            {
                string winner = viewer == 3 ? "나" : labels[3];
                Assert.That(roots[viewer].Q<Label>(className: "omc-result").text, Does.StartWith(winner + " 승리"));
                yield return Click(viewer, "omc-history");
                string history = roots[viewer].Q<Label>("omc-history-copy").text;
                Assert.That(history, Does.Contain((viewer == 1 ? "나" : labels[1]) + " · SB"));
                Assert.That(history, Does.Contain((viewer == 2 ? "나" : labels[2]) + " · BB"));
                Assert.That(history, Does.Contain(winner + " · 팟에서"));
                yield return Click(viewer, "omc-close-history");
                yield return Click(viewer, "omc-pot-details");
                string details = roots[viewer].Q<Label>("omc-pot-details-copy").text;
                Assert.That(details, Does.Contain(winner));
                Assert.That(details, Does.Contain((viewer == 1 ? "나" : labels[1]) + ":"));
                yield return Click(viewer, "omc-close-pot-details");
                yield return Click(viewer, "omc-best-seat-2");
                Assert.That(roots[viewer].Q<Label>(className: "omc-last").text,
                    Does.Contain("쇼다운 · " + (viewer == 1 ? "나" : labels[1]) + ":"));
            }
            Assert.That(boots[0].Connection.Remote.Read().GetSeat(new SeatId(2)).Status, Is.EqualTo(HoldemSeatStatus.Busted));
            Client(1).Dispose(); yield return Wait(() => ShowsDisconnected(0, 2));
            AssertSeatTitles(labels);
            yield return Click(0, "omc-next");
            yield return Wait(() => new[] { 0, 2, 3 }.All(i => boots[i].Connection.Remote.Read().HandNumber == 2));
            AssertSeatTitles(labels);
            Assert.That(boots[0].Connection.Remote.Read().GetSeat(new SeatId(2)).WasDealtIn, Is.False);
            yield return null; yield return null; Capture(0, "duplicate-names-eliminated");
        }

        private void AssertSeatTitles(string[] labels)
        {
            for (int viewer = 0; viewer < 4; viewer++)
                for (int i = 0; i < 4; i++)
                {
                    var title = roots[viewer].Q("omc-seat-" + (i + 1)).Q<Label>(className: "omc-seat-title");
                    string expected = viewer == i ? "나" : labels[i];
                    Assert.That(title.text, Does.StartWith(expected));
                    Assert.That(title.tooltip, Does.StartWith(expected));
                    Assert.That(title.enableRichText, Is.False);
                }
        }
    }
}
