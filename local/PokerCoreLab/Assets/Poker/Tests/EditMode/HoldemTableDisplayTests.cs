using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTableDisplayTests
    {
        private HoldemRoom room;
        private Guid[] peers;

        [SetUp]
        public void Setup()
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            room = new HoldemRoom(Guid.NewGuid(), peers[0], "주하", new HoldemConfig(100, 1, 2,
                HoldemRevealPolicy.PauseAfterCommunityReveal, dealPolicy: HoldemDealPolicy.WaitForHost), new SeatId(1), new StableRandom());
            for (int i = 0; i < 4; i++) { room.Join(peers[i], "참가자 " + i); room.SetReady(peers[i], true); }
            var lobby = room.Read(peers[0]);
            room.StartHand(peers[0], new HoldemStartCommand(lobby.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LocalAndPacketDisplaysAgreeAcrossEveryPhaseIncludingPublicShowdown(bool fold)
        {
            for (int step = 0; step < 100; step++)
            {
                CompareAll(); var game = room.Read(peers[0]).Game;
                if (game.Result != null) break;
                if (game.IsDealPending)
                    room.DealUnchanged(peers[0], new HoldemDealCommand(game.SessionId, game.HandId,
                        game.PendingDeal.WindowId, Guid.NewGuid(), game.SessionVersion, game.PendingDeal.Street));
                else if (game.IsRevealPending)
                    room.ResumeAfterReveal(peers[0], new HoldemRevealCommand(game.SessionId, game.HandId,
                        Guid.NewGuid(), game.SessionVersion, game.Street));
                else
                {
                    int actor = game.CurrentSeat.Value.Value - 1;
                    var own = room.Read(peers[actor]).Game;
                    var action = fold ? BettingAction.Fold() : own.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call();
                    Assert.That(room.Submit(peers[actor], new HoldemRoomAction(game.SessionId, game.HandId,
                        Guid.NewGuid(), game.SessionVersion, action)).Accepted, Is.True);
                }
            }
            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.Result, Is.Not.Null);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(settled.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), settled.SessionVersion)).Accepted, Is.True);
            CompareAll();
        }

        [Test]
        public void DisplayOwnsCopiesAndRestoresTableOrderWithoutModifyingThePacket()
        {
            var packet = Packet(0);
            Array.Reverse(packet.game.seats);
            var display = new HoldemTableDisplay(packet);
            var card = display.GetOwnCard(0);
            packet.members[0].name = "바뀐 이름";
            packet.game.seats.Single(s => s.viewer).stack = 999;
            packet.game.seats.Single(s => s.viewer).visibleCards[0] = 51;
            Assert.That(display.GetSeatAt(0).Seat, Is.EqualTo(new SeatId(1)));
            Assert.That(display.GetSeat(new SeatId(1)).Name, Is.EqualTo("주하"));
            Assert.That(display.OwnStack, Is.EqualTo(100));
            Assert.That(display.GetOwnCard(0), Is.EqualTo(card));
            Assert.That(packet.game.seats[0].tableIndex, Is.EqualTo(3));
        }

        [Test]
        public void PublicStreetActionsAreDetachedAndLegacyPacketsRemainSupported()
        {
            var game = room.Read(peers[3]).Game;
            room.Submit(peers[3], new HoldemRoomAction(game.SessionId, game.HandId, Guid.NewGuid(), game.SessionVersion, BettingAction.Call()));
            var packet = Packet(0);
            var display = new HoldemTableDisplay(packet);
            Assert.That(display.HasStreetActions, Is.True);
            Assert.That(display.StreetActionCount, Is.EqualTo(1));
            packet.game.streetActions[0].kind = 0; packet.game.streetActions[0].paid = 999;
            Assert.That(display.GetStreetAction(0).Kind, Is.EqualTo(BettingActionKind.Call));
            Assert.That(display.GetStreetAction(0).Paid, Is.EqualTo(2));
            packet.game.hasStreetActions = false; packet.game.streetActions = null;
            var legacy = new HoldemTableDisplay(packet);
            Assert.That(legacy.HasStreetActions, Is.False);
            Assert.That(legacy.StreetActionCount, Is.Zero);
            Assert.That(legacy.LastAction.Kind, Is.EqualTo(BettingActionKind.Call));
        }

        [TestCase("seat")]
        [TestCase("kind")]
        [TestCase("street")]
        [TestCase("paid")]
        [TestCase("target")]
        [TestCase("duplicate")]
        [TestCase("missing")]
        [TestCase("null_entry")]
        [TestCase("different_last")]
        [TestCase("missing_last")]
        [TestCase("missing_latest_seat")]
        public void InvalidPublicStreetActionsAreRejected(string fault)
        {
            var packet = Packet(0);
            packet.game.streetActions = new[] { new HoldemActionPacket { seat = 4, kind = 2, street = 0, paid = 2 } };
            packet.hasLastAction = true; packet.lastAction = new HoldemActionPacket { seat = 4, kind = 2, street = 0, paid = 2 };
            switch (fault)
            {
                case "seat": packet.game.streetActions[0].seat = 5; break;
                case "kind": packet.game.streetActions[0].kind = 99; break;
                case "street": packet.game.streetActions[0].street = 1; break;
                case "paid": packet.game.streetActions[0].paid = -1; break;
                case "target": packet.game.streetActions[0].target = -1; break;
                case "duplicate": packet.game.streetActions = new[] { packet.game.streetActions[0], packet.game.streetActions[0] }; break;
                case "missing": packet.game.streetActions = null; break;
                case "null_entry": packet.game.streetActions[0] = null; break;
                case "different_last": packet.lastAction.paid = 999; break;
                case "missing_last": packet.hasLastAction = false; break;
                case "missing_latest_seat": packet.game.streetActions = Array.Empty<HoldemActionPacket>(); break;
            }
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(packet));
        }

        [Test]
        public void ARevealedBestHandWithoutItsPublicHoleCardsIsRejected()
        {
            var packet = Packet(3);
            packet.game.board = new[] { 20, 21, 22, 23, 24 };
            packet.game.street = 4; packet.game.settlementState = 1; packet.game.hasLegal = false; packet.game.currentSeat = 0;
            packet.game.seats[0].revealedBestCards = (int[])packet.game.board.Clone();
            Assert.That(packet.game.seats[0].visibleCards, Is.Empty);
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(packet));
        }

        [TestCase("protocol")]
        [TestCase("card")]
        [TestCase("duplicate_card")]
        [TestCase("private_opponent")]
        [TestCase("duplicate_seat")]
        [TestCase("viewer")]
        [TestCase("negative_chips")]
        [TestCase("legal_range")]
        [TestCase("early_best")]
        [TestCase("name")]
        public void StructurallyInvalidPacketsNeverReachTheGameScreen(string fault)
        {
            var packet = Packet(3); var own = packet.game.seats[3];
            switch (fault)
            {
                case "protocol": packet.protocol = 99; break;
                case "card": own.visibleCards[0] = 52; break;
                case "duplicate_card": own.visibleCards[1] = own.visibleCards[0]; break;
                case "private_opponent": packet.game.seats[0].visibleCards = new[] { 50, 51 }; break;
                case "duplicate_seat": packet.game.seats[0].seat = 4; break;
                case "viewer": packet.viewerSeat = 2; break;
                case "negative_chips": own.stack = -1; break;
                case "legal_range": packet.game.legal.minimumTarget = -1; break;
                case "early_best": own.revealedBestCards = new[] { 0, 1, 2, 3, 4 }; break;
                case "name": packet.members[0].name = "\n"; break;
            }
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(packet));
        }

        private void CompareAll()
        {
            for (int i = 0; i < 4; i++)
            {
                var source = room.Read(peers[i]);
                var local = new HoldemTableDisplay(source.Game, source.LastAction);
                var remote = new HoldemTableDisplay(Packet(i));
                Assert.That(remote.SessionId, Is.EqualTo(local.SessionId)); Assert.That(remote.HandId, Is.EqualTo(local.HandId));
                Assert.That(remote.SessionVersion, Is.EqualTo(local.SessionVersion)); Assert.That(remote.HandNumber, Is.EqualTo(local.HandNumber));
                Assert.That(remote.ViewerSeat, Is.EqualTo(local.ViewerSeat)); Assert.That(remote.CurrentSeat, Is.EqualTo(local.CurrentSeat));
                Assert.That(remote.Street, Is.EqualTo(local.Street)); Assert.That(remote.PotAmount, Is.EqualTo(local.PotAmount));
                Assert.That(remote.IsDealPending, Is.EqualTo(local.IsDealPending)); Assert.That(remote.IsRevealPending, Is.EqualTo(local.IsRevealPending));
                Assert.That(remote.CanContinue, Is.EqualTo(local.CanContinue)); Assert.That(remote.IsOver, Is.EqualTo(local.IsOver));
                Assert.That(remote.IsSettlementPending, Is.EqualTo(local.IsSettlementPending));
                Assert.That(remote.BoardCount, Is.EqualTo(local.BoardCount));
                for (int c = 0; c < local.BoardCount; c++) Assert.That(remote.GetBoardCard(c), Is.EqualTo(local.GetBoardCard(c)));
                if (local.PendingDeal != null)
                { Assert.That(remote.PendingDeal.WindowId, Is.EqualTo(local.PendingDeal.WindowId)); Assert.That(remote.PendingDeal.Street, Is.EqualTo(local.PendingDeal.Street)); }
                if (local.LastAction != null)
                {
                    Assert.That(remote.LastAction.Kind, Is.EqualTo(local.LastAction.Kind)); Assert.That(remote.LastAction.Seat, Is.EqualTo(local.LastAction.Seat));
                    Assert.That(remote.LastAction.Paid, Is.EqualTo(local.LastAction.Paid)); Assert.That(remote.LastAction.Street, Is.EqualTo(local.LastAction.Street));
                }
                for (int s = 0; s < local.SeatCount; s++)
                {
                    var a = local.GetSeatAt(s); var b = remote.GetSeatAt(s);
                    Assert.That(b.Seat, Is.EqualTo(a.Seat)); Assert.That(b.Stack, Is.EqualTo(a.Stack));
                    Assert.That(b.Status, Is.EqualTo(a.Status)); Assert.That(b.Committed, Is.EqualTo(a.Committed));
                    Assert.That(b.StreetContribution, Is.EqualTo(a.StreetContribution)); Assert.That(b.Awarded, Is.EqualTo(a.Awarded));
                    Assert.That(b.IsButton, Is.EqualTo(a.IsButton)); Assert.That(b.IsCurrentActor, Is.EqualTo(a.IsCurrentActor));
                    Assert.That(b.IsSmallBlind, Is.EqualTo(a.IsSmallBlind)); Assert.That(b.IsBigBlind, Is.EqualTo(a.IsBigBlind));
                    Assert.That(b.WasDealtIn, Is.EqualTo(a.WasDealtIn)); Assert.That(b.TableIndex, Is.EqualTo(a.TableIndex));
                    Assert.That(b.VisibleHoleCardCount, Is.EqualTo(a.VisibleHoleCardCount));
                    for (int c = 0; c < a.VisibleHoleCardCount; c++) Assert.That(b.GetVisibleHoleCard(c), Is.EqualTo(a.GetVisibleHoleCard(c)));
                    Assert.That(b.RevealedHandValue, Is.EqualTo(a.RevealedHandValue));
                    Assert.That(b.RevealedBestCardCount, Is.EqualTo(a.RevealedBestCardCount));
                    for (int c = 0; c < a.RevealedBestCardCount; c++) Assert.That(b.GetRevealedBestCard(c), Is.EqualTo(a.GetRevealedBestCard(c)));
                }
                Assert.That(remote.LegalActions == null, Is.EqualTo(local.LegalActions == null));
                if (local.LegalActions != null)
                {
                    var a = local.LegalActions; var b = remote.LegalActions;
                    Assert.That(b.CallAmount, Is.EqualTo(a.CallAmount));
                    Assert.That(b.MinimumAggressiveTarget, Is.EqualTo(a.MinimumAggressiveTarget));
                    Assert.That(b.MaximumAggressiveTarget, Is.EqualTo(a.MaximumAggressiveTarget));
                    foreach (var action in new[] { BettingAction.Fold(), BettingAction.Check(), BettingAction.Call(),
                        BettingAction.BetTo(1), BettingAction.RaiseTo(1), BettingAction.BetTo(100), BettingAction.RaiseTo(100), BettingAction.RaiseTo(long.MaxValue) })
                        Assert.That(b.Allows(action), Is.EqualTo(source.Game.LegalActions.Allows(action)));
                }
                Assert.That(remote.Result == null, Is.EqualTo(local.Result == null));
                if (local.Result != null)
                {
                    Assert.That(remote.Result.Kind, Is.EqualTo(local.Result.Kind));
                    Assert.That(remote.Result.WinnerSeat, Is.EqualTo(local.Result.WinnerSeat));
                    Assert.That(remote.Result.PotCount, Is.EqualTo(local.Result.PotCount));
                    for (int p = 0; p < local.Result.PotCount; p++)
                    {
                        var a = local.Result.GetPot(p); var b = remote.Result.GetPot(p);
                        Assert.That(b.PayoutCount, Is.EqualTo(a.PayoutCount));
                        for (int j = 0; j < a.PayoutCount; j++)
                        { Assert.That(b.GetPayout(j).Seat, Is.EqualTo(a.GetPayout(j).Seat)); Assert.That(b.GetPayout(j).Amount, Is.EqualTo(a.GetPayout(j).Amount)); }
                    }
                }
            }
        }
        private HoldemRoomPacket Packet(int viewer) => JsonUtility.FromJson<HoldemRoomPacket>(
            JsonUtility.ToJson(HoldemRoomPacketMapper.Create(room.Read(peers[viewer]))));
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
    }
}
