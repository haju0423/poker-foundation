using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        [UnityTest]
        public IEnumerator AccusationButtonsUseOwnSeatAndKeepVerdictPrivateAfterReconnect()
        {
            yield return SetupChangedReveal(accusations: true);
            var before = ports[0].Read();
            Assert.That(roots[2].Q<Label>(className: "omc-prompt").text, Does.Contain("고발할 상대"));
            // Viewer 1's first target is seat 2, the actual source of this changed card.
            yield return Wait(() => Enabled(0, "omc-accuse")); Click(0, "omc-accuse");
            Assert.That(ports[0].HasPendingInput, Is.True);
            Assert.That(roots[0].Q<Button>("omc-pass-accusation").enabledInHierarchy, Is.False);
            yield return Wait(() => !ports[0].HasPendingInput);
            Assert.That(ports[0].Read().Accusations.OwnTarget, Is.EqualTo(new SeatId(2)));
            Assert.That(ports[1].Read().Accusations.OwnTarget, Is.Null);
            // Viewer 3 deliberately targets the innocent seat 1; both verdict values must survive projection.
            yield return Wait(() => Enabled(2, "omc-accuse")); Click(2, "omc-accuse");
            yield return Wait(() => !ports[2].HasPendingInput);
            foreach (int viewer in new[] { 1, 3 })
            {
                yield return Wait(() => Enabled(viewer, "omc-pass-accusation")); Click(viewer, "omc-pass-accusation");
                yield return Wait(() => !ports[viewer].HasPendingInput);
            }
            Assert.That(server.CloseAccusations(CloseCurrentAccusations()).Accepted, Is.True);
            Assert.That(server.ResolveAccusations().RecordedCount, Is.EqualTo(2));
            yield return Wait(() => ports.All(p => p.Read().Accusations.Phase == HoldemAccusationPhase.AwaitingConsequences));
            Assert.That(ports[0].Read().Accusations.OwnVerdict, Is.True);
            Assert.That(ports[2].Read().Accusations.OwnVerdict, Is.False);
            Assert.That(ports[1].Read().Accusations.OwnVerdict, Is.Null);
            Assert.That(ports[3].Read().Accusations.OwnVerdict, Is.Null);
            Assert.That(roots[0].Q<Label>(className: "omc-prompt").text, Does.Contain("내 고발 적중"));
            Assert.That(roots[2].Q<Label>(className: "omc-prompt").text, Does.Contain("내 고발 오적중"));
            Assert.That(roots[1].Q<Label>(className: "omc-prompt").text, Does.Not.Contain("적중"));
            Assert.That(roots[3].Q<Label>(className: "omc-prompt").text, Does.Not.Contain("적중"));
            clients[0].Dispose(); yield return Wait(() => ports[1].Lobby.Paused);
            ports[0].Refresh(); yield return Wait(() => ports.All(p => p.CanSend));
            Assert.That(ports[0].Read().Accusations.OwnVerdict, Is.True);
            Assert.That(ports[0].Read().OwnStack, Is.EqualTo(before.OwnStack));
            Assert.That(ports[0].Read().PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(ports.All(p => p.Read().IsRevealPending), Is.True);
            Assert.That(roots.All(r => r.Q<Button>("omc-accuse").parent.resolvedStyle.display == DisplayStyle.None), Is.True);
            Capture(0, "accusation-own-verdict-reconnected");
        }

        [UnityTest]
        public IEnumerator LostAccusationReceiptRequiresConfirmationEvenWhenOwnTargetMatches()
        {
            for (int attempt = 0; attempt < 3; attempt++) yield return LostAccusationReceiptScenario();
        }

        private IEnumerator LostAccusationReceiptScenario()
        {
            yield return SetupChangedReveal(accusations: true);
            var basis = ports[0].Read(); ports[0].Accuse(basis, new SeatId(2));
            yield return Wait(() => !ports[0].HasPendingInput);
            // Send the same choice again with a NEW id, and drop its receipt after it reaches authority.
            long version = ports[0].Read().SessionVersion;
            ports[0].Accuse(ports[0].Read(), new SeatId(2));
            var responses = (Queue<HoldemWireResponse>)typeof(HoldemTcpClient)
                .GetField("responses", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clients[0]);
            yield return WaitWithoutPort(0, () => {
                clients[0].Poll(); return responses.Any(r => r.type == "receipt" && r.accepted);
            });
            long committedVersion = responses.Single(r => r.type == "receipt" && r.accepted).receipt.version;
            while (clients[0].TryReadResponse(out _)) { }
            // A different client's snapshot can still precede the accepted receipt. Wait for its
            // authoritative version instead of using network arrival order as evidence of duplication.
            yield return WaitWithoutPort(0, () => ports.Skip(1).All(p => p.Read().SessionVersion == committedVersion));
            ports[0].Poll();
            Assert.That(ports[0].HasPendingInput, Is.True, "Matching target is not a command receipt.");
            Assert.That(ports[0].Read().Accusations.ResponseCount, Is.EqualTo(1));
            Assert.That(committedVersion, Is.GreaterThanOrEqualTo(version));
            clients[0].Dispose(); yield return Wait(() => ports[1].Lobby.Paused);
            ports[0].Refresh();
            yield return Wait(() => ports.All(p => p.CanSend));
            Assert.That(ports[0].Read().SessionVersion, Is.EqualTo(committedVersion));
            Assert.That(ports[0].Read().Accusations.ResponseCount, Is.EqualTo(1));
            Assert.That(ports[0].Read().Accusations.OwnTarget, Is.EqualTo(new SeatId(2)));
            Assert.That(ports[0].ErrorText, Is.Empty);
        }

        [UnityTest]
        public IEnumerator UnprocessedAccusationCanReconnectWithoutInventingASecondChoice()
        {
            yield return SetupChangedReveal(accusations: true);
            long version = ports[3].Read().SessionVersion;
            ports[3].Accuse(ports[3].Read(), new SeatId(2));
            // Do not pump the authority. The connection can close before the original is processed.
            feedbackOffsets[3] = 8000; ports[3].Poll(); ports[3].Refresh();
            Assert.That(clients[3].IsClosed, Is.True);
            Assert.That(ports[3].HasPendingInput, Is.True);
            yield return Wait(() => ports.All(p => p.CanSend));
            Assert.That(ports[3].Read().SessionVersion, Is.EqualTo(version + 1));
            Assert.That(ports[3].Read().Accusations.ResponseCount, Is.EqualTo(1));
            Assert.That(ports[3].Read().Accusations.OwnTarget, Is.EqualTo(new SeatId(2)));
            Assert.That(ports.Take(3).All(p => p.Read().Accusations.OwnTarget == null), Is.True);
        }

        [UnityTest]
        public IEnumerator ChangedOwnVerdictClosesTransportWithoutCommittingMalformedState()
        {
            yield return SetupChangedReveal(accusations: true);
            for (int viewer = 0; viewer < 4; viewer++)
            {
                ports[viewer].Accuse(ports[viewer].Read(), viewer == 0 ? new SeatId(2) : (SeatId?)null);
                yield return Wait(() => ports.All(p => !p.HasPendingInput));
            }
            Assert.That(server.CloseAccusations(CloseCurrentAccusations()).Accepted, Is.True);
            Assert.That(server.ResolveAccusations().RecordedCount, Is.EqualTo(1));
            yield return Wait(() => ports[0].Read().Accusations.OwnVerdict.HasValue);
            var before = ports[0].Read(); var lobby = ports[0].Lobby;
            var history = ports[0].ReadHistory(); var speech = ports[0].ReadPublicUtterances();
            clients[0].Latest.revision++; clients[0].Latest.game.accusations.ownVerdict = false;
            ports[0].Poll();
            Assert.That(clients[0].IsClosed, Is.True);
            Assert.That(ports[0].CanSend, Is.False);
            Assert.That(ports[0].Read(), Is.SameAs(before));
            Assert.That(ports[0].Lobby, Is.SameAs(lobby));
            Assert.That(ports[0].ReadHistory(), Is.SameAs(history));
            Assert.That(ports[0].ReadPublicUtterances(), Is.SameAs(speech));
            Assert.That(ports[0].Read().Accusations.OwnVerdict, Is.True);
        }

        private HoldemRoomAccusationClose CloseCurrentAccusations()
        {
            var basis = ports[0].Read();
            return new HoldemRoomAccusationClose(basis.SessionId, basis.HandId, basis.Accusations.WindowId,
                Guid.NewGuid(), basis.SessionVersion, basis.Street);
        }
    }
}
