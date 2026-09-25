using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRoomPacketTests
    {
        private Guid[] peers;
        private HoldemRoom room;
        private string[] secrets;

        [SetUp]
        public void Setup()
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            room = new HoldemRoom(Guid.NewGuid(), peers[0], "주하", new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom());
            secrets = new string[4];
            for (int i = 0; i < 4; i++)
            {
                secrets[i] = room.Join(peers[i], "참가자 " + i).Admission.ResumeToken;
                room.SetReady(peers[i], true);
            }
        }

        private void Start()
        {
            var v = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(v.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0)).Accepted, Is.True);
        }

        [Test]
        public void LobbyJsonContainsPublicRosterButNoAdmissionSecretsOrPrivateGame()
        {
            var packet = RoundTrip(0);
            Assert.That(packet.protocol, Is.EqualTo(1));
            Assert.That(packet.hasGame, Is.False);
            Assert.That(packet.members.Length, Is.EqualTo(4));
            Assert.That(packet.members[0].name, Is.EqualTo("주하"));
            Assert.That(packet.members[0].host, Is.True);
        }

        [Test]
        public void FourSerializedPayloadsEachContainOnlyTheirOwnHoleCards()
        {
            Start();
            for (int i = 0; i < 4; i++)
            {
                var source = room.Read(peers[i]); var packet = RoundTrip(i);
                Assert.That(packet.viewerSeat, Is.EqualTo(i + 1));
                Assert.That(packet.game.board, Is.Empty);
                Assert.That(packet.game.hasLegal, Is.EqualTo(source.Game.LegalActions != null));
                Assert.That(packet.game.seats.Sum(s => s.visibleCards.Length), Is.EqualTo(2));
                for (int j = 0; j < 4; j++)
                {
                    Assert.That(packet.game.seats[j].visibleCards.Length, Is.EqualTo(i == j ? 2 : 0));
                    Assert.That(packet.game.seats[j].revealedBestCards, Is.Empty);
                }
                Assert.That(packet.game.seats[i].visibleCards, Is.EqualTo(new[] { source.Game.GetOwnCard(0).Id, source.Game.GetOwnCard(1).Id }));
            }
        }

        [Test]
        public void MutatingWireDataCannotChangeAuthorityOrOtherRecipients()
        {
            Start(); var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0])); var other = RoundTrip(1);
            packet.game.seats[0].stack = 9999; packet.game.seats[0].visibleCards[0] = 51;
            packet.members[0].name = "변경";
            Assert.That(room.Read(peers[0]).Game.OwnStack, Is.EqualTo(100));
            Assert.That(room.Read(peers[0]).GetMember(0).Name, Is.EqualTo("주하"));
            Assert.That(other.game.seats[0].stack, Is.EqualTo(100));
            Assert.That(other.game.seats[0].visibleCards, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompletedPacketsUseActualPublicRevealAndPayouts(bool fold)
        {
            Start();
            for (int step = 0; step < 100 && room.Read(peers[0]).Game.Result == null; step++)
            {
                var game = room.Read(peers[0]).Game;
                int actor = game.CurrentSeat.Value.Value - 1; var own = room.Read(peers[actor]).Game;
                var action = fold ? BettingAction.Fold() : own.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call();
                Assert.That(room.Submit(peers[actor], new HoldemRoomAction(own.SessionId, own.HandId, Guid.NewGuid(), own.SessionVersion, action)).Accepted, Is.True);
            }
            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.Result, Is.Not.Null);
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var packet = RoundTrip(viewer);
                Assert.That(packet.game.hasResult, Is.True); Assert.That(packet.game.hasLegal, Is.False);
                Assert.That(packet.hasLastAction, Is.True);
                Assert.That(packet.game.result.pots.Sum(p => p.payouts.Sum(a => a.amount)), Is.EqualTo(settled.Result.PotAmount));
                for (int seat = 0; seat < 4; seat++)
                {
                    Assert.That(packet.game.seats[seat].visibleCards.Length, Is.EqualTo(!fold || viewer == seat ? 2 : 0));
                    Assert.That(packet.game.seats[seat].revealedBestCards.Length, Is.EqualTo(fold ? 0 : 5));
                }
            }
        }

        [Test]
        public void CurrentStreetKeepsEachSeatsLastActionAndSnapshotsStayDetached()
        {
            Start();
            var initial = room.Read(peers[3]).Game;
            var first = new HoldemRoomAction(initial.SessionId, initial.HandId, Guid.NewGuid(), initial.SessionVersion, BettingAction.Call());
            Assert.That(room.Submit(peers[3], first).Accepted, Is.True);
            Act(BettingAction.RaiseTo(6));
            var saved = room.Read(peers[0]);
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var packet = RoundTrip(viewer);
                Assert.That(packet.game.hasStreetActions, Is.True);
                Assert.That(packet.game.streetActions.Select(a => a.seat), Is.EqualTo(new[] { 1, 4 }));
                Assert.That(packet.game.streetActions.Select(a => a.kind), Is.EqualTo(new[] { 4, 2 }));
                Assert.That(packet.game.seats.Sum(s => s.visibleCards.Length), Is.EqualTo(2));
                packet.game.streetActions[0].kind = 0;
                packet.game.streetActions[1] = null;
            }
            Assert.That(room.Submit(peers[3], first).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(saved.Revision), "An old accepted command is not another public action.");
            Act(BettingAction.Call()); Act(BettingAction.Call()); Act(BettingAction.RaiseTo(10));
            var updated = RoundTrip(0);
            Assert.That(updated.game.streetActions.Length, Is.EqualTo(4));
            Assert.That(updated.game.streetActions.Single(a => a.seat == 4).kind, Is.EqualTo(4));
            Assert.That(updated.game.streetActions.Single(a => a.seat == 4).target, Is.EqualTo(10));
            Assert.That(updated.lastAction.seat, Is.EqualTo(4));
            Assert.That(saved.StreetActionCount, Is.EqualTo(2));
            Assert.That(saved.GetStreetAction(1).Kind, Is.EqualTo(BettingActionKind.Call));
        }

        [Test]
        public void RejectedInputCannotChangePublicActionOrRoomRevision()
        {
            Start(); Act(BettingAction.RaiseTo(6));
            var source = room.Read(peers[0]).Game;
            var before = JsonUtility.ToJson(RoundTrip(0));
            Assert.That(room.Submit(peers[0], new HoldemRoomAction(source.SessionId, source.HandId,
                Guid.NewGuid(), source.SessionVersion - 1, BettingAction.Call())).Accepted, Is.False);
            Assert.That(room.Submit(peers[0], new HoldemRoomAction(source.SessionId, source.HandId,
                Guid.NewGuid(), source.SessionVersion, BettingAction.Check())).Accepted, Is.False);
            Assert.That(JsonUtility.ToJson(RoundTrip(0)), Is.EqualTo(before));
        }

        [Test]
        public void StreetActionsClearOnStreetAdvanceAndNextHand()
        {
            Start(); Act(BettingAction.Call()); Act(BettingAction.Call()); Act(BettingAction.Call()); Act(BettingAction.Check());
            Assert.That(RoundTrip(0).game.street, Is.EqualTo((int)HoldemStreet.Flop));
            Assert.That(RoundTrip(0).game.streetActions, Is.Empty);
            Act(BettingAction.Check());
            Assert.That(RoundTrip(0).game.streetActions.Single().street, Is.EqualTo((int)HoldemStreet.Flop));
            Act(BettingAction.Fold()); Act(BettingAction.Fold()); Act(BettingAction.Fold());
            var done = room.Read(peers[0]).Game;
            Assert.That(done.Result, Is.Not.Null);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(done.SessionId, Guid.NewGuid(), Guid.NewGuid(), done.SessionVersion)).Accepted, Is.True);
            Assert.That(RoundTrip(0).game.streetActions, Is.Empty);
            Assert.That(RoundTrip(0).hasLastAction, Is.False);
        }

        [Test]
        public void PausedPacketPreservesGameRevisionAndDoesNotInventNewActions()
        {
            Start(); var before = RoundTrip(0); room.Disconnect(peers[1]); var after = RoundTrip(0);
            Assert.That(after.paused, Is.True); Assert.That(after.revision, Is.GreaterThan(before.revision));
            Assert.That(after.game.version, Is.EqualTo(before.game.version));
            Assert.That(after.game.currentSeat, Is.EqualTo(before.game.currentSeat));
            Assert.That(after.members[1].connected, Is.False);
        }

        [Test]
        public void ProtocolOneNumericValuesDoNotChangeWhenDomainEnumsAreEdited()
        {
            Assert.That(HoldemRoomPacketMapper.ProtocolVersion, Is.EqualTo(1));
            Assert.That(new[] { (int)BettingActionKind.Fold, (int)BettingActionKind.Check, (int)BettingActionKind.Call,
                (int)BettingActionKind.BetTo, (int)BettingActionKind.RaiseTo }, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(new[] { (int)HoldemStreet.Preflop, (int)HoldemStreet.Flop, (int)HoldemStreet.Turn,
                (int)HoldemStreet.River, (int)HoldemStreet.Complete }, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            Assert.That(new[] { (int)HoldemSeatStatus.Busted, (int)HoldemSeatStatus.Active,
                (int)HoldemSeatStatus.Folded, (int)HoldemSeatStatus.AllIn }, Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(new[] { (int)HoldemSettlementState.None, (int)HoldemSettlementState.AwaitingOddChipPriority,
                (int)HoldemSettlementState.Settled }, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(new[] { (int)HoldemResultKind.Fold, (int)HoldemResultKind.Showdown }, Is.EqualTo(new[] { 1, 2 }));
            for (int id = 0; id < 52; id++) Assert.That(Card.FromId(id).Id, Is.EqualTo(id));
        }

        [Test]
        public void AsymmetricAllInsPreserveEverySidePotAndOddChipAcrossSerialization()
        {
            Start();
            for (int i = 0; i < 3; i++) Act(BettingAction.Fold());
            var first = room.Read(peers[0]).Game;
            Assert.That(Enumerable.Range(0, 4).Select(i => first.GetSeatAt(i).Stack), Is.EqualTo(new long[] { 100, 99, 101, 100 }));
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(first.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), first.SessionVersion)).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Game.CurrentSeat, Is.EqualTo(new SeatId(1)));
            Act(BettingAction.RaiseTo(100));
            Act(BettingAction.Call());
            Act(BettingAction.Call());
            Act(BettingAction.Fold());
            var pending = room.Read(peers[0]).Game;
            Assert.That(pending.IsSettlementPending, Is.True);
            Assert.That(RoundTrip(0).game.hasResult, Is.False);
            Assert.That(RoundTrip(0).game.seats[3].revealedBestCards, Is.Empty);
            Assert.That(room.ResolveSettlement(peers[0], Guid.NewGuid(), pending.SessionVersion,
                HoldemOddChipRule.ClockwiseFromButton).Accepted, Is.True);

            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.Result.PotCount, Is.GreaterThan(1));
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var wire = RoundTrip(viewer).game;
                Assert.That(wire.currentSeat, Is.Zero);
                Assert.That(wire.result.winnerSeat, Is.Zero);
                Assert.That(wire.seats.Sum(s => s.stack), Is.EqualTo(400));
                Assert.That(wire.result.pots.SelectMany(p => p.payouts).Any(p => p.includesOddChip), Is.True);
                Assert.That(wire.result.pots.Sum(p => p.amount), Is.EqualTo(settled.Result.PotAmount));
                for (int i = 0; i < settled.Result.PotCount; i++)
                {
                    var source = settled.Result.GetPot(i); var mapped = wire.result.pots[i];
                    Assert.That(mapped.lowerBound, Is.EqualTo(source.LowerBound));
                    Assert.That(mapped.contributionCap, Is.EqualTo(source.ContributionCap));
                    Assert.That(mapped.amount, Is.EqualTo(source.Amount));
                    Assert.That(mapped.eligibleSeats, Is.EqualTo(Enumerable.Range(0, source.EligibleSeatCount).Select(j => source.GetEligibleSeat(j).Value)));
                    for (int j = 0; j < source.PayoutCount; j++)
                    {
                        Assert.That(mapped.payouts[j].seat, Is.EqualTo(source.GetPayout(j).Seat.Value));
                        Assert.That(mapped.payouts[j].amount, Is.EqualTo(source.GetPayout(j).Amount));
                        Assert.That(mapped.payouts[j].includesOddChip, Is.EqualTo(source.GetPayout(j).HasOddChip));
                    }
                }
                // These cards are already public; local evaluation is presentation, not a new settlement.
                for (int i = 0; i < 3; i++)
                    Assert.That(HandEvaluator.Evaluate(wire.seats[i].revealedBestCards.Select(Card.FromId).ToArray()),
                        Is.EqualTo(settled.GetSeatAt(i).RevealedHandValue.Value));
                Assert.That(wire.seats[3].visibleCards.Length, Is.EqualTo(viewer == 3 ? 2 : 0));
            }
        }

        private void Act(BettingAction action)
        {
            var game = room.Read(peers[0]).Game;
            int actor = game.CurrentSeat.Value.Value - 1;
            Assert.That(room.Submit(peers[actor], new HoldemRoomAction(game.SessionId, game.HandId,
                Guid.NewGuid(), game.SessionVersion, action)).Accepted, Is.True);
        }

        private HoldemRoomPacket RoundTrip(int viewer)
        {
            string json = JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[viewer])));
            foreach (string secret in secrets) Assert.That(json.Contains(secret), Is.False, "A resume capability entered a state packet.");
            Assert.That(json.Contains("ResumeToken"), Is.False);
            Assert.That(json.Contains("Secret"), Is.False);
            Assert.That(json.Contains("deck"), Is.False);
            var packet = JsonUtility.FromJson<HoldemRoomPacket>(json);
            Assert.That(packet.sessionId, Is.EqualTo(room.Read(peers[viewer]).SessionId.ToString("N")));
            return packet;
        }

        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
    }
}
