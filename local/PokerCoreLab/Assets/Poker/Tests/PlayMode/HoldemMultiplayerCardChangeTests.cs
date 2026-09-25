using System;
using System.Collections;
using System.Linq;
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
        public IEnumerator OwnerFeedbackSurvivesReconnectWithoutRevealingItToOtherSeats()
        {
            yield return SetupChangedReveal();
            var own = ports[1].Read().OwnCardChange;
            Assert.That(own.Card.Id, Is.EqualTo(45));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(ports[i].Read().OwnCardChange != null, Is.EqualTo(i == 1));
                Assert.That(roots[i].Q<Label>("omc-own-card-change") != null, Is.EqualTo(i == 1));
            }
            clients[1].Dispose(); yield return Wait(() => clients[0].Latest.paused);
            var replacement = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]);
            yield return Wait(() => replacement.IsCompleted);
            clients[1] = replacement.GetAwaiter().GetResult(); ports[1].ReplaceConnection(clients[1]);
            yield return Wait(() => ports.All(p => p.CanSend));
            Assert.That(ports[1].Read().OwnCardChange.EventId, Is.EqualTo(own.EventId));
            Assert.That(ports[1].Read().OwnCardChange.Card, Is.EqualTo(own.Card));
            Capture(1, "own-card-change-reconnected");
            var basis = ports[0].Read(); ports[0].Reveal(basis);
            yield return Wait(() => ports.All(p => !p.Read().IsRevealPending));
            Assert.That(ports.All(p => p.Read().OwnCardChange == null), Is.True);
            Assert.That(roots.All(r => r.Q<Label>("omc-own-card-change") == null), Is.True);
        }

        [UnityTest]
        public IEnumerator RevealedOwnerFeedbackCannotDisappearChangeOrAppearLate()
        {
            foreach (string fault in new[] { "remove", "event", "card", "add" })
            {
                yield return SetupChangedReveal();
                int viewer = fault == "add" ? 2 : 1;
                var before = ports[viewer].Read(); var lobby = ports[viewer].Lobby;
                var history = ports[viewer].ReadHistory(); var speech = ports[viewer].ReadPublicUtterances();
                var packet = clients[viewer].Latest; packet.revision++;
                if (fault == "remove") packet.hasOwnCardChange = false;
                else if (fault == "event") packet.ownCardChange.dealWindowId = Guid.NewGuid().ToString("N");
                else if (fault == "card")
                { packet.ownCardChange.boardIndex = 1; packet.ownCardChange.card = packet.game.board[1]; }
                else
                {
                    packet.hasOwnCardChange = true;
                    packet.ownCardChange = JsonUtility.FromJson<HoldemOwnCardChangePacket>(
                        JsonUtility.ToJson(clients[1].Latest.ownCardChange));
                }
                ports[viewer].Poll();
                Assert.That(clients[viewer].IsClosed, Is.True, fault);
                Assert.That(ports[viewer].CanSend, Is.False, fault);
                Assert.That(ports[viewer].Read(), Is.SameAs(before), fault);
                Assert.That(ports[viewer].Lobby, Is.SameAs(lobby), fault);
                Assert.That(ports[viewer].ReadHistory(), Is.SameAs(history), fault);
                Assert.That(ports[viewer].ReadPublicUtterances(), Is.SameAs(speech), fault);
                Assert.That(roots[viewer].Q<Label>("omc-own-card-change") != null, Is.EqualTo(viewer == 1), fault);
            }
        }

        private IEnumerator SetupChangedReveal()
        {
            yield return Cleanup();
            yield return SetupTable(new HoldemUtterancePolicy(64, 2, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw),
                new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal, dealPolicy: HoldemDealPolicy.WaitForHost));
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 24);
                textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); UnityEngine.Object.Destroy(old);
            }
            roots[1].Q<TextField>("omc-utterance-input").value = "오늘은 사랑이 필요하네요";
            yield return Wait(() => Enabled(1, "omc-utterance-send")); Click(1, "omc-utterance-send");
            yield return Wait(() => !ports[1].HasPendingUtterance && ports.All(p => p.ReadPublicUtterances()?.Count == 1));
            for (int step = 0; step < 8 && !clients[0].Latest.game.dealPending; step++)
            {
                int actor = clients[0].Latest.game.currentSeat - 1; var basis = ports[actor].Read();
                ports[actor].Act(basis, basis.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
                yield return Wait(() => ports.All(p => p.Read().SessionVersion > basis.SessionVersion));
            }
            var turn = server.ReadPendingTurn().Turn; Assert.That(turn, Is.Not.Null);
            var source = turn.SourceUtterances.GetEntry(0); var deal = turn.TargetDeal;
            var command = new HoldemDealCommand(deal.SessionId, deal.HandId, deal.WindowId, Guid.NewGuid(), turn.ExpectedVersion,
                deal.Street, new HoldemCardChange(source.WindowId, source.CommandId, source.Speaker, 0,
                    Card.FromId(45), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout));
            Assert.That(server.ApplyDealerDeal(command).Accepted, Is.True);
            yield return Wait(() => ports.All(p => p.Read().IsRevealPending));
            yield return null; yield return null;
        }
    }
}
