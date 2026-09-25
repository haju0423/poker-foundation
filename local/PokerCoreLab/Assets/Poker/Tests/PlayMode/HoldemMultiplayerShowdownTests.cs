using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator ClosingReadOnlyDialogsRestoresShowdownComparisonWithoutAnotherNetworkUpdate()
        {
            yield return StartGame(new PresenceRandom(1295));
            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            yield return Click(0, "omc-fold"); yield return Wait(() => Client(0).Latest.game.currentSeat == 2);
            yield return Click(1, "omc-passive"); yield return Wait(() => Client(0).Latest.game.currentSeat == 3);
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null && b.Connection.Remote.CanSend));
            yield return new UnityEngine.WaitForSecondsRealtime(0.3f);
            var screen = (HoldemTableScreen)typeof(HoldemMultiplayerBootstrap)
                .GetField("screen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(boots[1]);
            long version = boots[1].Connection.Remote.Read().SessionVersion;
            foreach (var dialog in new[] { new[] { "omc-help", "omc-close-help" },
                new[] { "omc-history", "omc-close-history" }, new[] { "omc-pot-details", "omc-close-pot-details" } })
            {
                yield return Click(1, dialog[0]);
                // Simulate a remote refresh while the dialog covers the table, then stop changing state.
                screen.Render();
                Assert.That(roots[1].Q<Button>("omc-best-seat-4").enabledInHierarchy, Is.False);
                yield return Click(1, dialog[1]);
                yield return Wait(() => Pickable(1, "omc-best-seat-4"));
                yield return Click(1, "omc-best-seat-4");
                Assert.That(roots[1].Q<Button>("omc-best-seat-4").ClassListContains("selected"), Is.True);
                yield return Click(1, "omc-best-seat-2");
                Assert.That(roots[1].Q<Button>("omc-best-seat-2").ClassListContains("selected"), Is.True);
                Assert.That(boots[1].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
                Assert.That(boots[1].Connection.Remote.HasPendingInput, Is.False);
            }
            yield return Click(1, "omc-help");
            screen.Render(); screen.PauseProgress();
            yield return Click(1, "omc-close-help");
            Assert.That(screen.IsProgressPaused, Is.True, "Closing help is not an error-recovery command.");
            Assert.That(roots[1].Q<Button>("omc-best-seat-4").enabledInHierarchy, Is.False);
            Assert.That(boots[1].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
        }

        [UnityTest]
        public IEnumerator FoldedViewerInitiallyComparesTheWinnerButKeepsAManualLoserSelection()
        {
            yield return StartGame(new PresenceRandom(1295));
            yield return Click(3, "omc-max"); yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            yield return Click(0, "omc-fold"); yield return Wait(() => Client(0).Latest.game.currentSeat == 2);
            yield return Click(1, "omc-passive"); yield return Wait(() => Client(0).Latest.game.currentSeat == 3);
            yield return Click(2, "omc-fold");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            Assert.That(Client(0).Latest.game.result.winnerSeat, Is.EqualTo(4));
            foreach (int viewer in new[] { 0, 2, 3 })
                Assert.That(roots[viewer].Q<Button>("omc-best-seat-4").ClassListContains("selected"), Is.True);
            // An active showdown participant still starts with their own comparison, including a losing hand.
            Assert.That(roots[1].Q<Button>("omc-best-seat-2").ClassListContains("selected"), Is.True);
            Assert.That(roots[0].Q<Label>(className: "omc-last").text, Does.Contain("참가자 4"));
            yield return null; yield return null; Capture(0, "folded-viewer-winner-default");
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            yield return Click(0, "omc-best-seat-2");
            Assert.That(roots[0].Q<Button>("omc-best-seat-2").ClassListContains("selected"), Is.True);
            Client(1).Dispose();
            yield return Wait(() => ShowsDisconnected(0, 2));
            Assert.That(roots[0].Q<Button>("omc-best-seat-2").ClassListContains("selected"), Is.True,
                "A membership refresh must not override the user's chosen comparison.");
            Assert.That(boots[0].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            yield return Click(0, "omc-next");
            yield return Wait(() => boots[0].Connection.Remote.Read().HandNumber == 2);
            Assert.That(roots[0].Query(className: "best").ToList(), Is.Empty);
        }
    }
}
