using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemRoomTests
    {
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void SeededRoomHistoriesKeepAccountingAndPrivacyThroughShortStacksAndGates(bool manualDeal, bool manualReveal)
        {
            int hands = 0, actions = 0, refunds = 0;
            for (int seed = 0; seed < 24; seed++)
            {
                long starting = 1 + seed * 3;
                Create(new HoldemConfig(starting, 1, 2,
                    manualReveal ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                    dealPolicy: manualDeal ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic), new HistoryRandom(seed));
                Start();
                var random = new System.Random(1701 + seed);
                var prior = new HoldemHandHistory[4];
                for (int hand = 0; hand < 12; hand++)
                {
                    for (int step = 0; step < 256; step++)
                    {
                        var view = room.Read(peers[0]).Game;
                        string context = "seed=" + seed + " hand=" + hand + " step=" + step + " deal=" + manualDeal + " reveal=" + manualReveal;
                        string baseline = null;
                        for (int i = 0; i < 4; i++)
                        {
                            var wire = HoldemRoomPacketMapper.Create(room.Read(peers[i]));
                            var copy = JsonUtility.FromJson<HoldemRoomPacket>(JsonUtility.ToJson(wire));
                            prior[i] = HoldemHistoryPacketReader.Read(copy, prior[i]);
                            string publicJson = JsonUtility.ToJson(copy.history);
                            if (baseline == null) baseline = publicJson;
                            else Assert.That(publicJson, Is.EqualTo(baseline), context);
                            if (view.Result == null && !view.IsSettlementPending)
                                Assert.That(copy.game.seats.Where(s => !s.viewer).Sum(s => s.visibleCards.Length), Is.Zero, context);
                        }
                        var entries = Enumerable.Range(0, prior[0].Count).Select(prior[0].GetEntry).ToArray();
                        if (prior[0].OmittedCount == 0)
                            Assert.That(entries.Sum(e => e.Kind == HoldemHistoryKind.UncalledReturn ? -e.Amount : e.Amount), Is.EqualTo(view.PotAmount), context);
                        Assert.That(Enumerable.Range(0, 4).Sum(i => view.GetSeatAt(i).Stack) + (view.Result == null ? view.PotAmount : 0), Is.EqualTo(starting * 4), context);
                        if (view.Result != null)
                        {
                            hands++; refunds += entries.Count(e => e.Kind == HoldemHistoryKind.UncalledReturn); break;
                        }
                        Assert.That(step, Is.LessThan(255), context);
                        if (step == 2)
                        {
                            var retained = room.Read(peers[0]).History;
                            room.Disconnect(peers[2]); ReconnectSeat(3);
                            Assert.That(room.Read(peers[2]).History, Is.SameAs(retained), context);
                        }
                        if (view.IsDealPending)
                        {
                            var command = new HoldemDealCommand(view.SessionId, view.HandId, view.PendingDeal.WindowId,
                                Guid.NewGuid(), view.SessionVersion, view.PendingDeal.Street);
                            Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True, context);
                            var retained = room.Read(peers[0]).History;
                            Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True, context);
                            Assert.That(room.Read(peers[0]).History, Is.SameAs(retained), context);
                        }
                        else if (view.IsRevealPending)
                        {
                            var command = new HoldemRevealCommand(view.SessionId, view.HandId, Guid.NewGuid(), view.SessionVersion, view.Street);
                            Assert.That(room.ResumeAfterReveal(peers[0], command).Accepted, Is.True, context);
                        }
                        else if (view.IsSettlementPending)
                            Assert.That(room.ResolveSettlement(peers[0], Guid.NewGuid(), view.SessionVersion, HoldemOddChipRule.ClockwiseFromButton).Accepted, Is.True, context);
                        else
                        {
                            int actor = view.CurrentSeat.Value.Value - 1;
                            var basis = room.Read(peers[actor]).Game; var legal = basis.LegalActions;
                            var choices = new List<BettingAction>();
                            if (legal.CanFold) choices.Add(BettingAction.Fold());
                            if (legal.CanCheck) choices.Add(BettingAction.Check());
                            if (legal.CanCall) { choices.Add(BettingAction.Call()); choices.Add(BettingAction.Call()); }
                            if (legal.CanBet || legal.CanRaise)
                            {
                                long target = random.Next(4) == 0 ? legal.MaximumAggressiveTarget.Value : legal.MinimumAggressiveTarget.Value;
                                choices.Add(legal.CanBet ? BettingAction.BetTo(target) : BettingAction.RaiseTo(target));
                            }
                            var command = new HoldemRoomAction(basis.SessionId, basis.HandId, Guid.NewGuid(), basis.SessionVersion, choices[random.Next(choices.Count)]);
                            Assert.That(room.Submit(peers[actor], command).Accepted, Is.True, context); actions++;
                            var retained = room.Read(peers[0]).History;
                            Assert.That(room.Submit(peers[actor], command).Accepted, Is.True, context);
                            Assert.That(room.Read(peers[0]).History, Is.SameAs(retained), context);
                        }
                    }
                    if (!room.Read(peers[0]).Game.CanContinue) break;
                    NextHand();
                }
            }
            TestContext.WriteLine("Room history: hands=" + hands + " actions=" + actions + " refunds=" + refunds);
            Assert.That(hands, Is.GreaterThan(24)); Assert.That(refunds, Is.GreaterThan(0));
        }

        private sealed class HistoryRandom : IRandomSource
        {
            private readonly System.Random random;
            public HistoryRandom(int seed) { random = new System.Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }

        [Test]
        public void PublicHistoryRecordsOnlyNewAcceptedActionsAndKeepsImmutableCopies()
        {
            Assert.That(room.Read(peers[0]).History, Is.Null);
            var start = Start(); var initial = room.Read(peers[0]).History;
            Assert.That(initial.Count, Is.EqualTo(2));
            Assert.That(initial.GetEntry(0).Kind, Is.EqualTo(HoldemHistoryKind.SmallBlind));
            Assert.That(initial.GetEntry(1).Kind, Is.EqualTo(HoldemHistoryKind.BigBlind));
            Assert.That(initial.GetEntry(0).Amount, Is.EqualTo(1)); Assert.That(initial.GetEntry(1).Amount, Is.EqualTo(2));
            Assert.That(room.StartHand(peers[0], start).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).History, Is.SameAs(initial));
            var state = room.Read(peers[3]).Game;
            var command = new HoldemRoomAction(state.SessionId, state.HandId, Guid.NewGuid(), state.SessionVersion, BettingAction.RaiseTo(10));
            Assert.That(room.Submit(peers[3], command).Accepted, Is.True);
            var after = room.Read(peers[0]).History;
            Assert.That(after.Count, Is.EqualTo(3)); Assert.That(initial.Count, Is.EqualTo(2));
            var raise = after.GetEntry(2);
            Assert.That(raise.Amount, Is.EqualTo(10)); Assert.That(raise.StreetTotal, Is.EqualTo(10));
            Assert.That(raise.Action, Is.EqualTo(BettingActionKind.RaiseTo)); Assert.That(raise.IsAllIn, Is.False);
            Assert.That(room.Submit(peers[3], command).Accepted, Is.True);
            Assert.That(room.Submit(peers[3], Passive(state)).Accepted, Is.False);
            foreach (var peer in peers) Assert.That(room.Read(peer).History, Is.SameAs(after));
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            var immutable = HoldemHistoryPacketReader.Read(packet);
            packet.history.entries[2].amount = 99;
            Assert.That(immutable.GetEntry(2).Amount, Is.EqualTo(10)); Assert.That(after.GetEntry(2).Amount, Is.EqualTo(10));
        }

        [Test]
        public void PublicHistorySeparatesUncalledReturnFromAwardsAndResetsOnlyOnSuccessfulNewHand()
        {
            Start(); ActSeat(4, BettingAction.RaiseTo(100));
            Assert.That(room.Read(peers[0]).History.GetEntry(2).IsAllIn, Is.True);
            ActSeat(1, BettingAction.Fold()); ActSeat(2, BettingAction.Fold()); ActSeat(3, BettingAction.Fold());
            var end = room.Read(peers[0]); var history = end.History;
            var entries = Enumerable.Range(0, history.Count).Select(history.GetEntry).ToArray();
            Assert.That(entries.Count(e => e.Kind == HoldemHistoryKind.UncalledReturn), Is.EqualTo(1));
            Assert.That(entries.Last().Amount, Is.EqualTo(98)); Assert.That(entries.Last().Seat.Value, Is.EqualTo(4));
            long net = entries.Sum(e => e.Kind == HoldemHistoryKind.UncalledReturn ? -e.Amount : e.Amount);
            Assert.That(net, Is.EqualTo(end.Game.Result.PotAmount));
            Assert.That(HoldemHistoryPacketReader.Read(HoldemRoomPacketMapper.Create(end)).Count, Is.EqualTo(7));
            var wrong = new HoldemStartCommand(end.SessionId, Guid.NewGuid(), Guid.NewGuid(), end.Game.SessionVersion - 1);
            Assert.That(room.StartHand(peers[0], wrong).Accepted, Is.False);
            Assert.That(room.Read(peers[0]).History, Is.SameAs(history));
            var next = new HoldemStartCommand(end.SessionId, Guid.NewGuid(), Guid.NewGuid(), end.Game.SessionVersion);
            Assert.That(room.StartHand(peers[0], next).Accepted, Is.True);
            var current = room.Read(peers[0]).History;
            Assert.That(current.Count, Is.EqualTo(2)); Assert.That(current.OmittedCount, Is.Zero);
            Assert.That(current.HandId, Is.Not.EqualTo(history.HandId)); Assert.That(history.Count, Is.EqualTo(7));
            Assert.That(HoldemHistoryPacketReader.Read(HoldemRoomPacketMapper.Create(room.Read(peers[0])), history).HandNumber, Is.EqualTo(2));
        }

        [Test]
        public void PublicHistoryTailIsBoundedWithoutLimitingLegalRaisesAndSurvivesReconnect()
        {
            Create(new HoldemConfig(10000, 1, 2)); Start();
            HoldemHandHistory earlier = null;
            for (int i = 0; i < 100; i++)
            {
                var state = room.Read(peers[0]).Game;
                int seat = state.CurrentSeat.Value.Value;
                var actor = room.Read(peers[seat - 1]).Game;
                ActSeat(seat, BettingAction.RaiseTo(actor.LegalActions.MinimumAggressiveTarget.Value));
                var decoded = HoldemHistoryPacketReader.Read(HoldemRoomPacketMapper.Create(room.Read(peers[0])), earlier);
                earlier = decoded;
            }
            var tail = room.Read(peers[0]).History;
            Assert.That(tail.Count, Is.EqualTo(64)); Assert.That(tail.OmittedCount, Is.EqualTo(38));
            room.Disconnect(peers[1]); ReconnectSeat(2);
            Assert.That(room.Read(peers[1]).History, Is.SameAs(tail));
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[1]));
            Assert.That(HoldemHistoryPacketReader.Read(packet).OmittedCount, Is.EqualTo(38));
            Assert.That(room.Read(peers[0]).Game.PotAmount + Enumerable.Range(0, 4).Sum(i => room.Read(peers[0]).Game.GetSeatAt(i).Stack), Is.EqualTo(40000));
            // Leave half the wire budget for the independently bounded own-text projection.
            foreach (var entry in packet.history.entries) { entry.amount = long.MaxValue; entry.streetTotal = long.MaxValue; }
            foreach (var member in packet.members) member.name = new string('한', 24);
            var bytes = HoldemFrameCodec.Encode(JsonUtility.ToJson(new HoldemWireResponse { type = "state", state = packet }));
            Assert.That(bytes.Length, Is.LessThan(HoldemFrameCodec.MaximumBytes / 2));
        }

        [TestCase("remove")] [TestCase("rewrite")] [TestCase("negative")]
        [TestCase("unknown-seat")] [TestCase("kind")] [TestCase("future-street")]
        [TestCase("missing-action")] [TestCase("zero-payment")] [TestCase("too-many")]
        [TestCase("missing-body")] [TestCase("overflow")]
        [TestCase("reuse-hand")] [TestCase("missing-latest")] [TestCase("missing-check")]
        [TestCase("pot-mismatch")] [TestCase("sum-overflow")]
        public void MalformedOrRegressedPublicHistoryIsRejected(string fault)
        {
            Start(); ActSeat(4, BettingAction.Call());
            var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            var initial = HoldemHistoryPacketReader.Read(packet);
            if (fault == "remove") packet.hasHistory = false;
            else if (fault == "rewrite") packet.history.entries[2].amount++;
            else if (fault == "negative") packet.history.omittedCount = -1;
            else if (fault == "unknown-seat") packet.history.entries[2].seat = 9;
            else if (fault == "kind") packet.history.entries[2].kind = 99;
            else if (fault == "future-street") packet.history.entries[2].street = 3;
            else if (fault == "missing-action") packet.history.entries[2].hasAction = false;
            else if (fault == "zero-payment") packet.history.entries[2].amount = 0;
            else if (fault == "too-many") packet.history.entries = Enumerable.Repeat(packet.history.entries[2], 65).ToArray();
            else if (fault == "missing-body") packet.history = null;
            else if (fault == "overflow") packet.history.omittedCount = long.MaxValue;
            else if (fault == "reuse-hand") packet.game.handNumber++;
            else if (fault == "missing-latest") packet.history.entries = packet.history.entries.Take(2).ToArray();
            else if (fault == "missing-check") { packet.lastAction.kind = (int)BettingActionKind.Check; packet.lastAction.paid = 0; }
            else if (fault == "pot-mismatch") packet.game.pot++;
            else { packet.history.entries[0].amount = long.MaxValue; packet.history.entries[0].streetTotal = long.MaxValue; }
            Assert.Throws<ArgumentException>(() => HoldemHistoryPacketReader.Read(packet, initial));
            Assert.That(initial.Count, Is.EqualTo(3)); Assert.That(initial.GetEntry(2).Amount, Is.EqualTo(2));
        }

        [Test]
        public void HistoryDtoContainsOnlyPublicChipMovementScalarsAndLegacyIgnoresMissingFlag()
        {
            CollectionAssert.AreEquivalent(new[] { "kind", "street", "seat", "action", "amount", "streetTotal", "hasAction", "allIn" },
                typeof(HoldemHistoryEntryPacket).GetFields().Select(f => f.Name));
            CollectionAssert.AreEquivalent(new[] { "omittedCount", "entries" }, typeof(HoldemHistoryPacket).GetFields().Select(f => f.Name));
            Start(); var packet = HoldemRoomPacketMapper.Create(room.Read(peers[0]));
            packet.hasHistory = false; packet.history = new HoldemHistoryPacket();
            Assert.That(HoldemHistoryPacketReader.Read(packet), Is.Null);
        }
    }
}
