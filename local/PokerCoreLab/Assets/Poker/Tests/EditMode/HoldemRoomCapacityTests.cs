using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRoomCapacityTests
    {
        private Guid[] peers;
        private HoldemRoom room;
        private void Create(int count = 3, long stack = 100, IRandomSource random = null)
        {
            peers = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
            room = new HoldemRoom(Guid.NewGuid(), peers[0], "방장", new HoldemConfig(stack, 1, 2),
                new SeatId(1), random ?? new StableRandom(), seatCapacity: count);
            for (int i = 1; i < count; i++) Assert.That(room.Join(peers[i], "친구 " + i).Accepted, Is.True);
        }
        private void Start()
        {
            foreach (var peer in peers) room.SetReady(peer, true);
            var state = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(state.SessionId, Guid.NewGuid(),
                Guid.NewGuid(), state.Game?.SessionVersion ?? 0)).Accepted, Is.True);
        }
        private HoldemRoomPacket Packet() => HoldemRoomPacketMapper.Create(room.Read(peers[0]));
        private void Settle()
        {
            for (int step = 0; step < 40 && room.Read(peers[0]).Game.Result == null; step++)
            {
                var state = room.Read(peers[0]).Game;
                var peer = peers[state.CurrentSeat.Value.Value - 1];
                state = room.Read(peer).Game;
                Assert.That(room.Submit(peer, new HoldemRoomAction(state.SessionId, state.HandId, Guid.NewGuid(),
                    state.SessionVersion, state.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call())).Accepted, Is.True);
            }
            Assert.That(room.Read(peers[0]).Game.Result, Is.Not.Null);
        }
        [TestCase(3)] [TestCase(4)]
        public void SelectedCapacityControlsReadyStartAndEveryHand(int count)
        {
            Create(count);
            Assert.That(room.SeatCapacity, Is.EqualTo(count));
            Assert.That(KoreanPokerText.RoomSettingsDescription(room.Read(peers[0]).Rules), Does.StartWith("방 인원: " + count + "인"));
            Assert.That(room.Join(Guid.NewGuid(), "초과 참가자").Error, Is.EqualTo(HoldemRoomError.Full));
            for (int hand = 0; hand < 3; hand++)
            {
                Start();
                foreach (var peer in peers)
                {
                    var p = HoldemRoomPacketMapper.Create(room.Read(peer));
                    Assert.That(new HoldemLobbyDisplay(p).SeatCapacity, Is.EqualTo(count));
                    Assert.That(new HoldemTableDisplay(p).SeatCount, Is.EqualTo(count));
                    Assert.That(p.game.seats.Count(s => s.visibleCards.Length != 0), Is.EqualTo(1));
                    Assert.That(p.game.seats.Sum(s => s.stack) + p.game.pot, Is.EqualTo(100 * count));
                }
                Settle(); var packet = Packet();
                Assert.That(packet.game.seats.Sum(s => s.stack), Is.EqualTo(100 * count));
                Assert.DoesNotThrow(() => new HoldemTableDisplay(packet));
            }
        }
        [Test]
        public void ThreeSeatRoomContinuesHeadsUpOnlyAfterSettlement()
        {
            Create(random: new SeededRandom()); Start();
            foreach (var action in new[] { BettingAction.RaiseTo(100), BettingAction.Call(), BettingAction.Fold() })
            {
                var game = room.Read(peers[0]).Game; int actor = game.CurrentSeat.Value.Value - 1;
                Assert.That(room.Submit(peers[actor], new HoldemRoomAction(game.SessionId, game.HandId,
                    Guid.NewGuid(), game.SessionVersion, action)).Accepted, Is.True);
            }
            var settled = Packet(); Assert.That(settled.game.hasResult, Is.True);
            Assert.That(settled.game.seats.Count(s => s.stack == 0), Is.EqualTo(1));
            int eliminated = settled.game.seats.Single(s => s.stack == 0).seat;
            if (eliminated != 1) room.Disconnect(peers[eliminated - 1]);
            Start(); var next = Packet();
            Assert.That(next.game.seats.Length, Is.EqualTo(3));
            Assert.That(next.game.seats.Count(s => s.dealtIn), Is.EqualTo(2));
            Assert.That(next.game.seats.Single(s => s.seat == eliminated).visibleCards, Is.Empty);
            Assert.That(next.game.seats.Sum(s => s.stack) + next.game.pot, Is.EqualTo(300));
            Assert.DoesNotThrow(() => new HoldemTableDisplay(next));
        }
        [Test]
        public void ThreeSeatDepartureAndReconnectNeverCreateAFourthSeat()
        {
            Create(); var admission = room.Join(peers[2], "친구 2").Admission;
            room.Disconnect(peers[2]);
            Assert.That(room.Join(Guid.NewGuid(), "새 친구").Error, Is.EqualTo(HoldemRoomError.Full));
            peers[2] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[2], admission.ResumeToken).Admission.Seat, Is.EqualTo(new SeatId(3)));
            room.LeaveLobby(peers[1]);
            var state = room.Read(peers[0]);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(state.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0)).Error,
                Is.EqualTo(HoldemRoomError.WaitingForPlayers));
            peers[1] = Guid.NewGuid();
            Assert.That(room.Join(peers[1], "새 친구").Admission.Seat, Is.EqualTo(new SeatId(2)));
            Start(); Assert.That(Packet().game.seats.Length, Is.EqualTo(3));
        }
        [TestCase(2)] [TestCase(5)]
        public void UnsupportedCapacityIsRejected(int count)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemRoom(Guid.NewGuid(), Guid.NewGuid(), "방장",
                new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom(), seatCapacity: count));
        }
        [Test]
        public void DefaultAndLegacyCapacityStayFour()
        {
            var defaultRoom = new HoldemRoom(Guid.NewGuid(), Guid.NewGuid(), "방장", new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom());
            Assert.That(defaultRoom.SeatCapacity, Is.EqualTo(4));
            Create(4); Start(); var p = Packet(); p.rules.seatCapacity = 0;
            Assert.That(new HoldemLobbyDisplay(p).SeatCapacity, Is.EqualTo(4));
            Assert.That(new HoldemTableDisplay(p).SeatCount, Is.EqualTo(4));
            p.hasRules = false; p.rules = null;
            Assert.That(new HoldemLobbyDisplay(p).SeatCapacity, Is.EqualTo(4));
            Assert.That(new HoldemTableDisplay(p).SeatCount, Is.EqualTo(4));
        }
        [Test]
        public void ThreeSeatMaximumChipTotalDoesNotUseFourSeatLimit()
        {
            long maximum = long.MaxValue / 3; Create(3, maximum); Start();
            Assert.DoesNotThrow(() => new HoldemTableDisplay(Packet()));
            Assert.That(Packet().game.seats.Sum(s => s.stack) + Packet().game.pot, Is.EqualTo(maximum * 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldemRoomRules(new HoldemConfig(maximum + 1, 1, 2), false, 3));
        }
        [TestCase("missing_rules")] [TestCase("legacy_capacity")] [TestCase("two")] [TestCase("five")]
        [TestCase("member")] [TestCase("viewer")] [TestCase("button")] [TestCase("small_blind")]
        [TestCase("big_blind")] [TestCase("winner")] [TestCase("chips")] [TestCase("overflow")]
        public void ThreeSeatPacketRejectsCapacityAndConservationFaults(string fault)
        {
            Create(); Start(); var p = Packet();
            switch (fault)
            {
                case "missing_rules": p.hasRules = false; break;
                case "legacy_capacity": p.rules.seatCapacity = 0; break;
                case "two": p.rules.seatCapacity = 2; break;
                case "five": p.rules.seatCapacity = 5; break;
                case "member": p.members[2].seat = 4; break;
                case "viewer": p.viewerSeat = 4; break;
                case "button": p.game.button = 4; break;
                case "small_blind": p.game.smallBlind = 4; break;
                case "big_blind": p.game.bigBlind = 4; break;
                case "winner": p.game.sessionWinner = 4; break;
                case "chips": p.game.seats[0].stack++; break;
                case "overflow": p.game.seats[0].stack = long.MaxValue; break;
            }
            Assert.Catch<ArgumentException>(() => new HoldemTableDisplay(p));
        }
        [TestCase("folded")] [TestCase("eligible")] [TestCase("duplicate_eligible")]
        [TestCase("payout")] [TestCase("duplicate_payout")] [TestCase("too_many_pots")]
        public void SettlementRejectsPhantomAndDuplicateSeats(string fault)
        {
            Create(); Start(); Settle(); var p = Packet(); var pot = p.game.result.pots[0];
            switch (fault)
            {
                case "folded": p.game.result.foldedSeat = 4; break;
                case "eligible": pot.eligibleSeats = new[] { 4 }; break;
                case "duplicate_eligible": pot.eligibleSeats = new[] { 1, 1 }; break;
                case "payout": pot.payouts[0].seat = 4; break;
                case "duplicate_payout": pot.payouts = new[] { pot.payouts[0], pot.payouts[0] }; break;
                case "too_many_pots": p.game.result.pots = new[] { pot, pot, pot, pot }; break;
            }
            Assert.Throws<ArgumentException>(() => new HoldemTableDisplay(p));
        }
        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random source = new Random(7);
            public int NextInt(int exclusiveMax) => source.Next(exclusiveMax);
        }
    }
}
