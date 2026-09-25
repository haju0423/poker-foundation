using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemRoomTests
    {
        private HoldemRoom room;
        private Guid[] peers;
        private HoldemRoomAdmission[] admissions;

        [SetUp]
        public void Setup() => Create();

        private void Create(HoldemConfig config = null, IRandomSource random = null)
        {
            peers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            room = new HoldemRoom(Guid.NewGuid(), peers[0], "주하", config ?? new HoldemConfig(100, 1, 2),
                new SeatId(1), random ?? new StableRandom());
            admissions = new HoldemRoomAdmission[4];
            admissions[0] = room.Join(peers[0], "주하").Admission;
        }

        private void Fill()
        {
            for (int i = 1; i < 4; i++) admissions[i] = room.Join(peers[i], "참가자 " + i).Admission;
        }

        private HoldemStartCommand Start()
        {
            Fill();
            foreach (var peer in peers) Assert.That(room.SetReady(peer, true), Is.EqualTo(HoldemRoomError.None));
            var command = new HoldemStartCommand(room.Read(peers[0]).SessionId, Guid.NewGuid(), Guid.NewGuid(), 0);
            Assert.That(room.StartHand(peers[0], command).Accepted, Is.True);
            return command;
        }

        private HoldemRoomAction Passive(HoldemSnapshot snapshot, Guid? id = null) => new HoldemRoomAction(snapshot.SessionId,
            snapshot.HandId, id ?? Guid.NewGuid(), snapshot.SessionVersion,
            snapshot.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());

        [Test]
        public void EveryParticipantReadsTheHostsImmutableRoomRulesBeforeAndAfterStart()
        {
            Create(new HoldemConfig(250, 5, 10, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost));
            var rules = room.Read(peers[0]).Rules;
            Start();
            foreach (var peer in peers)
            {
                var state = room.Read(peer); Assert.That(state.Rules, Is.SameAs(rules));
                var packet = HoldemRoomPacketMapper.Create(state);
                Assert.That(packet.hasRules, Is.True);
                Assert.That(packet.rules.startingStack, Is.EqualTo(250));
                Assert.That(packet.rules.smallBlind, Is.EqualTo(5)); Assert.That(packet.rules.bigBlind, Is.EqualTo(10));
                Assert.That(packet.rules.waitsForHostDeal && packet.rules.pausesAfterReveal, Is.True);
                Assert.That(packet.rules.receivesUtterances, Is.False);
                packet.rules.startingStack = 999;
                Assert.That(room.Read(peer).Rules.StartingStack, Is.EqualTo(250));
            }
        }

        [Test]
        public void OnlySettledEliminatedGuestsCanDisconnectWithoutPausingTheNextHand()
        {
            Create(random: new PrefixRandom("Ah 3c Kh 4c Ad 5d Kd 6d 2c 7c 9d Js 3h Qs 4h 2d")); Start();
            ActSeat(4, BettingAction.RaiseTo(100));
            var allIn = room.Read(peers[0]).Game;
            Assert.That(allIn.GetSeat(new SeatId(4)).Stack, Is.Zero);
            Assert.That(allIn.Result, Is.Null);
            room.Disconnect(peers[3]);
            Assert.That(room.Read(peers[0]).Paused, Is.True);
            Assert.That(room.Submit(peers[0], Passive(room.Read(peers[0]).Game)).Error, Is.EqualTo(HoldemRoomError.Paused));
            ReconnectSeat(4);
            ActSeat(1, BettingAction.Fold());
            room.Disconnect(peers[0]);
            Assert.That(room.Read(peers[1]).Paused, Is.True, "A folded funded player is still required.");
            ReconnectSeat(1);
            ActSeat(2, BettingAction.Call()); ActSeat(3, BettingAction.Fold());
            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.Result, Is.Not.Null);
            Assert.That(settled.GetSeat(new SeatId(4)).Stack, Is.Zero);
            Assert.That(settled.CanContinue, Is.True);
            room.Disconnect(peers[2]);
            Assert.That(room.Read(peers[0]).Paused, Is.True, "A folded non-host with remaining chips is still required.");
            ReconnectSeat(3);
            room.Disconnect(peers[3]);
            Assert.That(room.Read(peers[0]).Paused, Is.False);
            AssertSameGame(settled, room.Read(peers[0]).Game);
            Assert.That(room.Join(Guid.NewGuid(), "대체 참가자").Error, Is.EqualTo(HoldemRoomError.AlreadyStarted));
            NextHand();
            var next = room.Read(peers[0]).Game;
            Assert.That(next.GetSeat(new SeatId(4)).WasDealtIn, Is.False);
            Assert.That(Enumerable.Range(0, 4).Count(i => next.GetSeatAt(i).WasDealtIn), Is.EqualTo(3));
            Guid actor = peers[next.CurrentSeat.Value.Value - 1];
            Assert.That(room.Submit(actor, Passive(room.Read(actor).Game)).Accepted, Is.True);
            ReconnectSeat(4);
            var returned = room.Read(peers[3]);
            Assert.That(returned.ViewerSeat, Is.EqualTo(new SeatId(4)));
            Assert.That(returned.Game.OwnCardCount, Is.Zero);
            Assert.That(returned.Game.LegalActions, Is.Null);
            Assert.That(returned.MemberCount, Is.EqualTo(4));
        }

        [Test]
        public void MultipleEliminatedGuestsDoNotBlockHeadsUpAndASettledHostStillOwnsProgress()
        {
            Create(random: new PrefixRandom("Ah 3c Kh 4c Ad 5d Kd 6d 2c 7c 9d Js 3h Qs 4h 2d")); Start();
            ActSeat(4, BettingAction.RaiseTo(100)); ActSeat(1, BettingAction.Fold());
            ActSeat(2, BettingAction.Call()); ActSeat(3, BettingAction.Call());
            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.GetSeat(new SeatId(3)).Stack, Is.Zero);
            Assert.That(settled.GetSeat(new SeatId(4)).Stack, Is.Zero);
            room.Disconnect(peers[2]); room.Disconnect(peers[3]);
            Assert.That(room.Read(peers[0]).Paused, Is.False);
            NextHand();
            var next = room.Read(peers[0]).Game;
            Assert.That(Enumerable.Range(0, 4).Count(i => next.GetSeatAt(i).WasDealtIn), Is.EqualTo(2));
            Assert.That(next.GetSeatAt(2).VisibleHoleCardCount, Is.Zero);
            Assert.That(next.GetSeatAt(3).VisibleHoleCardCount, Is.Zero);
            Assert.That(room.Read(peers[0]).MemberCount, Is.EqualTo(4));

            Create(random: new PrefixRandom("Ah 3c 4c Kh Ad 5d 6d Kd 2c 7c 9d Js 3h Qs 4h 2d")); Start();
            ActSeat(4, BettingAction.Fold()); ActSeat(1, BettingAction.RaiseTo(100));
            ActSeat(2, BettingAction.Call()); ActSeat(3, BettingAction.Fold());
            Assert.That(room.Read(peers[0]).Game.OwnStack, Is.Zero);
            room.Disconnect(peers[0]);
            Assert.That(room.Read(peers[1]).Paused, Is.True, "The host remains required even after elimination.");
            ReconnectSeat(1); NextHand();
            Assert.That(room.Read(peers[0]).Game.OwnCardCount, Is.Zero);
            Assert.That(room.Read(peers[0]).Game.HandNumber, Is.EqualTo(2));
        }

        private void ActSeat(int seat, BettingAction action)
        {
            var view = room.Read(peers[seat - 1]).Game;
            Assert.That(view.CurrentSeat, Is.EqualTo(new SeatId(seat)));
            Assert.That(room.Submit(peers[seat - 1], new HoldemRoomAction(view.SessionId, view.HandId,
                Guid.NewGuid(), view.SessionVersion, action)).Accepted, Is.True);
        }

        [Test]
        public void ALastSurvivorDoesNotRestartOrResetAfterEliminatedGuestsDisconnect()
        {
            Create(random: new PrefixRandom("Kh Qh Jh Ah Kd Qd Jd Ad 2c 3c 5d 8s 4h 9s 6h Tc")); Start();
            ActSeat(4, BettingAction.RaiseTo(100)); ActSeat(1, BettingAction.Call());
            ActSeat(2, BettingAction.Call()); ActSeat(3, BettingAction.Call());
            var settled = room.Read(peers[0]).Game;
            Assert.That(settled.OwnStack, Is.EqualTo(400));
            for (int i = 1; i < 4; i++) room.Disconnect(peers[i]);
            Assert.That(room.Read(peers[0]).Paused, Is.False);
            Assert.That(settled.IsOver, Is.True);
            Assert.That(settled.CanContinue, Is.False);
            Assert.That(settled.SessionWinnerSeat, Is.EqualTo(new SeatId(1)));
            var attempted = room.StartHand(peers[0], new HoldemStartCommand(settled.SessionId,
                Guid.NewGuid(), Guid.NewGuid(), settled.SessionVersion));
            Assert.That(attempted.Accepted, Is.False);
            Assert.That(attempted.Poker.Error, Is.EqualTo(HoldemCommandError.CannotContinue));
            AssertSameGame(settled, room.Read(peers[0]).Game);
        }
        [TestCase(1, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        public void CompletedMatchDoesNotWaitForHostOrWinnerReconnection(int winner, int departing)
        {
            string prefix = winner == 1 ? "Kh Qh Jh Ah Kd Qd Jd Ad 2c 3c 5d 8s 4h 9s 6h Tc"
                : "Ah Qh Jh Kh Ad Qd Jd Kd 2c 3c 5d 8s 4h 9s 6h Tc";
            Create(random: new PrefixRandom(prefix)); Start();
            ActSeat(4, BettingAction.RaiseTo(100)); ActSeat(1, BettingAction.Call());
            ActSeat(2, BettingAction.Call()); ActSeat(3, BettingAction.Call());
            int observer = departing == 0 ? 2 : 0;
            var before = room.Read(peers[observer]).Game;
            Assert.That(before.IsOver, Is.True);
            Assert.That(before.SessionWinnerSeat, Is.EqualTo(new SeatId(winner)));
            room.Disconnect(peers[departing]);
            var after = room.Read(peers[observer]);
            Assert.That(after.Paused, Is.False, "A completed match has no action to wait for.");
            AssertSameGame(before, after.Game);
            ReconnectSeat(departing + 1);
            Assert.That(room.Read(peers[observer]).Paused, Is.False);
            AssertSameGame(before, room.Read(peers[observer]).Game);
            var start = room.StartHand(peers[0], new HoldemStartCommand(before.SessionId,
                Guid.NewGuid(), Guid.NewGuid(), before.SessionVersion));
            Assert.That(start.Accepted, Is.False);
            Assert.That(start.Poker.Error, Is.EqualTo(HoldemCommandError.CannotContinue));
            AssertSameGame(before, room.Read(peers[observer]).Game);
        }

        private void ReconnectSeat(int seat)
        {
            peers[seat - 1] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[seat - 1], admissions[seat - 1].ResumeToken).Accepted, Is.True);
        }
        private void NextHand()
        {
            var view = room.Read(peers[0]).Game;
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(view.SessionId,
                Guid.NewGuid(), Guid.NewGuid(), view.SessionVersion)).Accepted, Is.True);
        }

        private sealed class PrefixRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public PrefixRandom(string prefix)
            {
                var target = prefix.Split(' ').Select(text => new Card((Rank)("23456789TJQKA".IndexOf(text[0]) + 2),
                    (Suit)("cdhs".IndexOf(text[1]) + 1))).ToList();
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).Where(c => !target.Contains(c)));
                var working = Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).ToArray();
                for (int i = working.Length - 1; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(selected);
                    Card saved = working[i]; working[i] = working[selected]; working[selected] = saved;
                }
            }
            public int NextInt(int exclusiveMax) => choices.Count == 0 ? exclusiveMax - 1 : choices.Dequeue();
        }

        [Test]
        public void FourDistinctMembersAndReadyGateDoNotDealBeforeHostStarts()
        {
            var command = new HoldemStartCommand(room.Read(peers[0]).SessionId, Guid.NewGuid(), Guid.NewGuid(), 0);
            Assert.That(room.StartHand(peers[0], command).Error, Is.EqualTo(HoldemRoomError.WaitingForPlayers));
            Fill();
            Assert.That(room.Read(peers[0]).MemberCount, Is.EqualTo(4));
            Assert.That(admissions.Select(x => x.Seat).Distinct().Count(), Is.EqualTo(4));
            Assert.That(admissions.Select(x => x.ResumeToken).Distinct().Count(), Is.EqualTo(4));
            Assert.That(room.Join(Guid.NewGuid(), "다섯 번째").Error, Is.EqualTo(HoldemRoomError.Full));
            Assert.That(room.StartHand(peers[1], command).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.StartHand(peers[0], command).Error, Is.EqualTo(HoldemRoomError.PlayersNotReady));
            foreach (var peer in peers) room.SetReady(peer, true);
            var before = room.Read(peers[0]);
            Assert.That(before.Game, Is.Null);
            Assert.That(room.StartHand(peers[0], command).Accepted, Is.True);
            Assert.That(before.Game, Is.Null, "Earlier lobby projections stay immutable.");
            Assert.That(room.Read(peers[0]).Game.OwnCardCount, Is.EqualTo(2));
            Assert.That(room.SetReady(peers[0], false), Is.EqualTo(HoldemRoomError.AlreadyStarted));
            Assert.That(room.Join(Guid.NewGuid(), "새 참가자").Error, Is.EqualTo(HoldemRoomError.AlreadyStarted));
            long revision = room.Read(peers[0]).Revision;
            Assert.That(room.StartHand(peers[0], command).Accepted, Is.True, "Lost start receipt can be retried.");
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
        }

        [Test]
        public void ExplicitLobbyDepartureReusesOnlyTheVacatedSeatAndRequiresNewReadiness()
        {
            Fill(); foreach (var peer in peers) room.SetReady(peer, true);
            var before = room.Read(peers[0]); string oldToken = admissions[1].ResumeToken;
            Assert.That(room.LeaveLobby(peers[1]), Is.EqualTo(HoldemRoomError.None));
            var after = room.Read(peers[0]);
            Assert.That(after.MemberCount, Is.EqualTo(3)); Assert.That(after.Paused, Is.False);
            Assert.That(before.MemberCount, Is.EqualTo(4));
            Assert.That(room.Read(peers[2]).ViewerSeat, Is.EqualTo(new SeatId(3)));
            Assert.That(room.Read(peers[3]).ViewerSeat, Is.EqualTo(new SeatId(4)));
            Assert.That(room.Reconnect(Guid.NewGuid(), oldToken).Error, Is.EqualTo(HoldemRoomError.InvalidResumeToken));
            var start = new HoldemStartCommand(after.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0);
            Assert.That(room.StartHand(peers[0], start).Error, Is.EqualTo(HoldemRoomError.WaitingForPlayers));
            peers[1] = Guid.NewGuid();
            Assert.That(room.Join(peers[1], "새 친구").Admission.Seat, Is.EqualTo(new SeatId(2)));
            Assert.That(room.StartHand(peers[0], start).Error, Is.EqualTo(HoldemRoomError.PlayersNotReady));
            room.SetReady(peers[1], true);
            Assert.That(room.StartHand(peers[0], start).Accepted, Is.True);
            Assert.That(room.Read(peers[1]).Game.ViewerSeat, Is.EqualTo(new SeatId(2)));
            Assert.That(room.Read(peers[0]).GetMember(0).IsHost, Is.True);
        }

        [Test]
        public void HostAndStartedSeatsCannotUseLobbyDepartureAndDisconnectStillReservesTheSeat()
        {
            Fill(); var initial = room.Read(peers[0]);
            Assert.That(room.LeaveLobby(peers[0]), Is.EqualTo(HoldemRoomError.HostCannotLeave));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(initial.Revision));
            room.Disconnect(peers[1]);
            Assert.That(room.Join(Guid.NewGuid(), "새 친구").Error, Is.EqualTo(HoldemRoomError.Full));
            Assert.That(room.LeaveLobby(peers[1]), Is.EqualTo(HoldemRoomError.Disconnected));
            peers[1] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            foreach (var peer in peers) room.SetReady(peer, true);
            room.StartHand(peers[0], new HoldemStartCommand(initial.SessionId, Guid.NewGuid(), Guid.NewGuid(), 0));
            long version = room.Read(peers[0]).Game.SessionVersion;
            Assert.That(room.LeaveLobby(peers[1]), Is.EqualTo(HoldemRoomError.AlreadyStarted));
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(version));
            Assert.That(room.Read(peers[0]).Paused, Is.False);
        }

        [Test]
        public void SimultaneousStartAndLobbyLeaveCannotCreateAPartialGame()
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Create(); Fill(); foreach (var peer in peers) room.SetReady(peer, true);
                var command = new HoldemStartCommand(room.Read(peers[0]).SessionId, Guid.NewGuid(), Guid.NewGuid(), 0);
                HoldemRoomReceipt started = null; HoldemRoomError left = HoldemRoomError.None;
                Parallel.Invoke(() => started = room.StartHand(peers[0], command), () => left = room.LeaveLobby(peers[1]));
                var view = room.Read(peers[0]);
                if (started.Accepted)
                { Assert.That(left, Is.EqualTo(HoldemRoomError.AlreadyStarted)); Assert.That(view.MemberCount, Is.EqualTo(4)); Assert.That(view.Game, Is.Not.Null); }
                else
                { Assert.That(left, Is.EqualTo(HoldemRoomError.None)); Assert.That(started.Error, Is.EqualTo(HoldemRoomError.WaitingForPlayers)); Assert.That(view.MemberCount, Is.EqualTo(3)); Assert.That(view.Game, Is.Null); }
                Assert.That(view.Paused, Is.False);
            }
        }

        [Test]
        public void EveryConnectionIncludingHostGetsOnlyItsOwnCardsAndLegalTurn()
        {
            Start();
            for (int i = 0; i < 4; i++)
            {
                var view = room.Read(peers[i]);
                Assert.That(view.ViewerSeat, Is.EqualTo(admissions[i].Seat));
                Assert.That(view.Game.ViewerSeat, Is.EqualTo(admissions[i].Seat));
                Assert.That(view.Game.LegalActions != null, Is.EqualTo(view.Game.CurrentSeat == admissions[i].Seat));
                Assert.That(view.Game.OwnCardCount, Is.EqualTo(2));
                for (int j = 0; j < 4; j++)
                {
                    Assert.That(view.Game.GetSeatAt(j).VisibleHoleCardCount, Is.EqualTo(i == j ? 2 : 0));
                    Assert.That(view.Game.GetSeatAt(j).RevealedBestCardCount, Is.Zero);
                }
            }
            Assert.That(typeof(HoldemRoomView).GetProperties().Any(p => p.Name.Contains("Token") || p.Name.Contains("SessionAuthority")), Is.False);
            Assert.That(typeof(HoldemRoomMemberView).GetProperties().Any(p => p.Name.Contains("Token") || p.Name.Contains("Connection")), Is.False);
        }

        [Test]
        public void ConnectionBindingCannotActAsAnotherSeatAndRetriesNeverPayTwice()
        {
            Start();
            var acting = room.Read(peers[3]).Game;
            Assert.That(acting.CurrentSeat, Is.EqualTo(new SeatId(4)));
            var input = Passive(acting);
            var wrong = room.Submit(peers[0], input);
            Assert.That(wrong.Error, Is.EqualTo(HoldemRoomError.CoreRejected));
            Assert.That(wrong.Poker.Error, Is.EqualTo(HoldemCommandError.WrongTurn));
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(acting.SessionVersion));
            Assert.That(room.Submit(peers[3], input).Accepted, Is.True);
            var after = room.Read(peers[3]);
            Assert.That(after.Game.OwnStack, Is.EqualTo(98));
            Assert.That(room.Submit(peers[3], input).Accepted, Is.True);
            Assert.That(room.Read(peers[3]).Game.OwnStack, Is.EqualTo(after.Game.OwnStack));
            Assert.That(room.Read(peers[3]).Revision, Is.EqualTo(after.Revision));
            Assert.That(room.Submit(peers[0], input).Poker.Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            var changed = new HoldemRoomAction(input.SessionId, input.HandId, input.CommandId, input.ExpectedVersion, BettingAction.Fold());
            Assert.That(room.Submit(peers[3], changed).Poker.Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(room.Submit(Guid.NewGuid(), input).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
        }

        [Test]
        public void StaleVersionAndPreviousHandDoNotChangeTheAuthoritativeGame()
        {
            Start(); var first = room.Read(peers[3]).Game; var input = Passive(first);
            Assert.That(room.Submit(peers[3], input).Accepted, Is.True);
            var stale = new HoldemRoomAction(first.SessionId, first.HandId, Guid.NewGuid(), first.SessionVersion, BettingAction.Call());
            long version = room.Read(peers[0]).Game.SessionVersion;
            Assert.That(room.Submit(peers[0], stale).Poker.Error, Is.EqualTo(HoldemCommandError.VersionMismatch));
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(version));
            Complete(); var settled = room.Read(peers[0]).Game;
            var next = new HoldemStartCommand(settled.SessionId, Guid.NewGuid(), Guid.NewGuid(), settled.SessionVersion);
            Assert.That(room.StartHand(peers[1], next).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.StartHand(peers[0], next).Accepted, Is.True);
            var current = room.Read(peers[0]).Game;
            Assert.That(current.HandNumber, Is.EqualTo(2));
            var late = new HoldemRoomAction(first.SessionId, first.HandId, Guid.NewGuid(), current.SessionVersion, BettingAction.Call());
            Assert.That(room.Submit(peers[0], late).Poker.Error, Is.EqualTo(HoldemCommandError.WrongHand));
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(current.SessionVersion));
            Assert.That(Enumerable.Range(0, 4).Sum(i => current.GetSeatAt(i).Stack + current.GetSeatAt(i).Committed), Is.EqualTo(400));
        }

        [Test]
        public void DisconnectPausesEveryPlayerAndHostMutationWithoutFoldingOrResetting()
        {
            Start(); var before = room.Read(peers[3]).Game;
            Assert.That(room.Disconnect(peers[1]), Is.EqualTo(HoldemRoomError.None));
            Assert.That(room.Read(peers[0]).Paused, Is.True);
            Assert.That(room.Submit(peers[3], Passive(before)).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.Submit(peers[1], Passive(before)).Error, Is.EqualTo(HoldemRoomError.Disconnected));
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(before.SessionId, Guid.NewGuid(), Guid.NewGuid(), before.SessionVersion)).Error,
                Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.DealUnchanged(peers[0], null).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.ResumeAfterReveal(peers[0], null).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.ResolveSettlement(peers[0], Guid.NewGuid(), before.SessionVersion, HoldemOddChipRule.ClockwiseFromButton).Error,
                Is.EqualTo(HoldemRoomError.Paused));
            Assert.Throws<InvalidOperationException>(() => room.Read(peers[1]));
            var after = room.Read(peers[0]).Game;
            AssertSameGame(before, after);
            Assert.That(after.GetSeatAt(1).Status, Is.EqualTo(HoldemSeatStatus.Active));
        }

        [Test]
        public void ReconnectNeedsPrivateCapabilityRestoresSameSeatAndInvalidatesOldConnection()
        {
            Start(); var before = room.Read(peers[1]).Game;
            Guid replacement = Guid.NewGuid(), old = peers[1];
            Assert.That(room.Reconnect(replacement, admissions[1].ResumeToken).Error, Is.EqualTo(HoldemRoomError.MemberStillConnected));
            room.Disconnect(old);
            Assert.That(room.Reconnect(replacement, "fake").Error, Is.EqualTo(HoldemRoomError.InvalidResumeToken));
            Assert.That(room.Reconnect(peers[0], admissions[1].ResumeToken).Error, Is.EqualTo(HoldemRoomError.ConnectionInUse));
            var result = room.Reconnect(replacement, admissions[1].ResumeToken);
            Assert.That(result.Accepted, Is.True); Assert.That(result.Admission.Seat, Is.EqualTo(admissions[1].Seat));
            long revision = room.Read(replacement).Revision;
            Assert.That(room.Reconnect(replacement, admissions[1].ResumeToken).Accepted, Is.True, "A lost reconnect acknowledgment is retryable.");
            Assert.That(room.Read(replacement).Revision, Is.EqualTo(revision));
            Assert.That(room.Read(replacement).Paused, Is.False);
            AssertSameGame(before, room.Read(replacement).Game);
            Assert.That(room.Read(replacement).Game.GetOwnCard(0), Is.EqualTo(before.GetOwnCard(0)));
            Assert.That(room.Read(replacement).Game.GetOwnCard(1), Is.EqualTo(before.GetOwnCard(1)));
            Assert.That(room.Disconnect(old), Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.Submit(old, Passive(room.Read(peers[3]).Game)).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.Submit(peers[3], Passive(room.Read(peers[3]).Game)).Accepted, Is.True);
        }

        [Test]
        public void HostReconnectKeepsHostRoleAndOtherSeatsCannotUseHostControls()
        {
            var start = Start(); Guid old = peers[0]; room.Disconnect(old);
            peers[0] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[0], admissions[0].ResumeToken).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).GetMember(0).IsHost, Is.True);
            Assert.That(room.StartHand(peers[0], start).Accepted, Is.True);
            Assert.That(room.DealUnchanged(peers[1], null).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.ResumeAfterReveal(peers[1], null).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.ResolveSettlement(peers[1], Guid.NewGuid(), 0, HoldemOddChipRule.ClockwiseFromButton).Error,
                Is.EqualTo(HoldemRoomError.HostOnly));
        }

        [Test]
        public void ConcurrentDuplicateInputsCommitOnceAndDisconnectedTableStaysPaused()
        {
            Start(); var before = room.Read(peers[3]).Game; var input = Passive(before);
            var outcomes = new HoldemRoomReceipt[16];
            Parallel.For(0, outcomes.Length, i => outcomes[i] = room.Submit(peers[3], input));
            Assert.That(outcomes.All(x => x.Accepted), Is.True);
            Assert.That(room.Read(peers[3]).Game.SessionVersion, Is.EqualTo(before.SessionVersion + 1));
            Assert.That(room.Read(peers[3]).Game.OwnStack, Is.EqualTo(98));
            var next = room.Read(peers[0]).Game; var nextInput = Passive(next);
            Parallel.Invoke(() => room.Disconnect(peers[2]), () => room.Submit(peers[0], nextInput));
            var paused = room.Read(peers[0]);
            Assert.That(paused.Paused, Is.True);
            Assert.That(paused.Game.SessionVersion, Is.InRange(next.SessionVersion, next.SessionVersion + 1));
            Assert.That(room.Submit(peers[0], nextInput).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.Read(peers[0]).Game.SessionVersion, Is.EqualTo(paused.Game.SessionVersion));
        }

        [Test]
        public void FourPlayerCompleteHandShowsPublicShowdownAndConservesChips()
        {
            Start(); Complete();
            for (int i = 0; i < 4; i++)
            {
                var game = room.Read(peers[i]).Game;
                Assert.That(game.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
                Assert.That(game.BoardCount, Is.EqualTo(5));
                Assert.That(Enumerable.Range(0, 4).Sum(j => game.GetSeatAt(j).Stack), Is.EqualTo(400));
                for (int j = 0; j < 4; j++)
                {
                    Assert.That(game.GetSeatAt(j).VisibleHoleCardCount, Is.EqualTo(2));
                    Assert.That(game.GetSeatAt(j).RevealedBestCardCount, Is.EqualTo(5));
                }
            }
        }

        [Test]
        public void PublicRoomProjectionIsImmutableAcrossDisconnectAndResume()
        {
            Start(); var before = room.Read(peers[0]); room.Disconnect(peers[1]);
            Assert.That(before.GetMember(1).Connected, Is.True);
            Assert.That(before.Paused, Is.False);
            Assert.That(room.Read(peers[0]).GetMember(1).Connected, Is.False);
            room.Reconnect(Guid.NewGuid(), admissions[1].ResumeToken);
            Assert.That(before.Revision, Is.LessThan(room.Read(peers[0]).Revision));
        }

        [Test]
        public void PublicFoldResultNeverRevealsAnyOpponentsHoleCards()
        {
            Start();
            for (int i = 0; i < 3; i++)
            {
                var game = room.Read(peers[0]).Game;
                Guid actor = peers[game.CurrentSeat.Value.Value - 1];
                Assert.That(room.Submit(actor, new HoldemRoomAction(game.SessionId, game.HandId, Guid.NewGuid(),
                    game.SessionVersion, BettingAction.Fold())).Accepted, Is.True);
            }
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var game = room.Read(peers[viewer]).Game;
                Assert.That(game.Result.Kind, Is.EqualTo(HoldemResultKind.Fold));
                for (int seat = 0; seat < 4; seat++)
                {
                    Assert.That(game.GetSeatAt(seat).VisibleHoleCardCount, Is.EqualTo(viewer == seat ? 2 : 0));
                    Assert.That(game.GetSeatAt(seat).RevealedHandValue, Is.Null);
                }
            }
        }

        [Test]
        public void AllDisconnectedMembersMustReturnBeforeTheOriginalActionResumes()
        {
            Start(); var before = room.Read(peers[3]).Game; var input = Passive(before);
            room.Disconnect(peers[1]); room.Disconnect(peers[2]);
            peers[1] = Guid.NewGuid(); peers[2] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Paused, Is.True);
            Assert.That(room.Submit(peers[3], input).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.Reconnect(peers[2], admissions[2].ResumeToken).Accepted, Is.True);
            Assert.That(room.Submit(peers[3], input).Accepted, Is.True);
            Assert.That(room.Read(peers[3]).Game.SessionVersion, Is.EqualTo(before.SessionVersion + 1));
        }

        [Test]
        public void LostActionReceiptCanBeRetriedAfterTheSamePlayerReconnects()
        {
            Start(); var before = room.Read(peers[3]).Game; var input = Passive(before);
            var accepted = room.Submit(peers[3], input);
            room.Disconnect(peers[3]); peers[3] = Guid.NewGuid();
            Assert.That(room.Reconnect(peers[3], admissions[3].ResumeToken).Accepted, Is.True);
            var resumed = room.Read(peers[3]);
            var retry = room.Submit(peers[3], input);
            Assert.That(retry.Poker, Is.SameAs(accepted.Poker));
            Assert.That(room.Read(peers[3]).Revision, Is.EqualTo(resumed.Revision));
            Assert.That(room.Read(peers[3]).Game.OwnStack, Is.EqualTo(resumed.Game.OwnStack));
        }

        [Test]
        public void HostDealAndRevealSurviveDisconnectAndNeverExposeFutureCards()
        {
            Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost));
            Start();
            while (!room.Read(peers[0]).Game.IsDealPending)
            {
                var game = room.Read(peers[0]).Game;
                Guid actor = peers[game.CurrentSeat.Value.Value - 1];
                Assert.That(room.Submit(actor, Passive(room.Read(actor).Game)).Accepted, Is.True);
            }
            var before = room.Read(peers[0]).Game; var pending = before.PendingDeal;
            var deal = new HoldemDealCommand(before.SessionId, before.HandId, pending.WindowId,
                Guid.NewGuid(), before.SessionVersion, pending.Street);
            room.Disconnect(peers[1]);
            Assert.That(room.DealUnchanged(peers[0], deal).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.Read(peers[0]).Game.BoardCount, Is.Zero);
            peers[1] = Guid.NewGuid(); room.Reconnect(peers[1], admissions[1].ResumeToken);
            Assert.That(room.DealUnchanged(peers[0], deal).Accepted, Is.True);
            var revealed = room.Read(peers[0]);
            Assert.That(revealed.Game.BoardCount, Is.EqualTo(3));
            Assert.That(revealed.Game.IsRevealPending, Is.True);
            Assert.That(room.DealUnchanged(peers[0], deal).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revealed.Revision));
            var resume = new HoldemRevealCommand(before.SessionId, before.HandId, Guid.NewGuid(),
                revealed.Game.SessionVersion, HoldemStreet.Flop);
            room.Disconnect(peers[2]);
            Assert.That(room.ResumeAfterReveal(peers[0], resume).Error, Is.EqualTo(HoldemRoomError.Paused));
            peers[2] = Guid.NewGuid(); room.Reconnect(peers[2], admissions[2].ResumeToken);
            Assert.That(room.ResumeAfterReveal(peers[0], resume).Accepted, Is.True);
            Assert.That(room.Read(peers[0]).Game.IsRevealPending, Is.False);
            Assert.That(room.Read(peers[0]).Game.BoardCount, Is.EqualTo(3));
        }

        [Test]
        public void InvalidAdmissionDataAndUnavailableCheatingModeAreRejectedExplicitly()
        {
            Assert.Throws<ArgumentException>(() => room.Join(Guid.Empty, "이름"));
            Assert.Throws<ArgumentException>(() => room.Join(Guid.NewGuid(), "\n"));
            Assert.Throws<ArgumentException>(() => room.Join(Guid.NewGuid(), new string('가', 25)));
            Assert.That(room.Reconnect(Guid.NewGuid(), new string('A', 100000)).Error, Is.EqualTo(HoldemRoomError.InvalidResumeToken));
            Assert.That(room.Reconnect(Guid.NewGuid(), new string('!', 44)).Error, Is.EqualTo(HoldemRoomError.InvalidResumeToken));
            Assert.That(room.SetReady(Guid.NewGuid(), true), Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.Throws<ArgumentException>(() => new HoldemRoom(Guid.NewGuid(), Guid.NewGuid(), "호스트",
                new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                    HoldemAccusationMode.CollectLatestChoiceUntilHostCloses), new SeatId(1), new StableRandom()));
        }

        [Test]
        public void CreationReturnsHostAdmissionWithoutAnotherJoinHandshake()
        {
            Guid connection = Guid.NewGuid();
            var created = HoldemRoom.Create(Guid.NewGuid(), connection, "호스트", new HoldemConfig(100, 1, 2),
                new SeatId(1), new StableRandom(), out var admission);
            Assert.That(admission.Seat, Is.EqualTo(new SeatId(1)));
            Assert.That(admission.ResumeToken.Length, Is.EqualTo(44));
            created.Disconnect(connection);
            var resumed = created.Reconnect(Guid.NewGuid(), admission.ResumeToken);
            Assert.That(resumed.Accepted, Is.True);
            Assert.That(resumed.Admission.Seat, Is.EqualTo(admission.Seat));
        }

        [Test]
        public void OddChipResolutionRemainsAnExplicitIdempotentHostOperation()
        {
            Start();
            for (int step = 0; step < 100 && !room.Read(peers[0]).Game.IsSettlementPending; step++)
            {
                var view = room.Read(peers[0]).Game; int actor = view.CurrentSeat.Value.Value - 1;
                var own = room.Read(peers[actor]).Game;
                var input = actor == 1 && own.Street == HoldemStreet.Preflop
                    ? new HoldemRoomAction(own.SessionId, own.HandId, Guid.NewGuid(), own.SessionVersion, BettingAction.Fold())
                    : Passive(own);
                Assert.That(room.Submit(peers[actor], input).Accepted, Is.True);
            }
            var pending = room.Read(peers[0]);
            Assert.That(pending.Game.IsSettlementPending, Is.True);
            Assert.That(pending.Game.Result, Is.Null);
            Guid command = Guid.NewGuid();
            var accepted = room.ResolveSettlement(peers[0], command, pending.Game.SessionVersion, HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(accepted.Accepted, Is.True);
            var settled = room.Read(peers[0]);
            Assert.That(settled.Game.IsSettlementPending, Is.False);
            Assert.That(settled.Game.Result.PotAmount, Is.EqualTo(7));
            Assert.That(settled.Game.Result.WinnerSeat, Is.Null, "One extra chip is not a stronger poker hand.");
            Assert.That(Enumerable.Range(0, 4).Sum(i => settled.Game.GetSeatAt(i).Stack), Is.EqualTo(400));
            var retry = room.ResolveSettlement(peers[0], command, pending.Game.SessionVersion, HoldemOddChipRule.ClockwiseFromButton);
            Assert.That(retry.Poker, Is.SameAs(accepted.Poker));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(settled.Revision));
        }

        private void Complete()
        {
            for (int step = 0; step < 100; step++)
            {
                var view = room.Read(peers[0]).Game;
                if (view.Result != null) return;
                Assert.That(view.CurrentSeat.HasValue, Is.True, "No automatic NPC or hidden host operation should be required.");
                Guid actor = peers[view.CurrentSeat.Value.Value - 1];
                Assert.That(room.Submit(actor, Passive(room.Read(actor).Game)).Accepted, Is.True);
            }
            Assert.Fail("The four-player hand did not finish.");
        }

        private static void AssertSameGame(HoldemSnapshot before, HoldemSnapshot after)
        {
            Assert.That(after.HandId, Is.EqualTo(before.HandId));
            Assert.That(after.SessionVersion, Is.EqualTo(before.SessionVersion));
            Assert.That(after.CurrentSeat, Is.EqualTo(before.CurrentSeat));
            Assert.That(after.BoardCount, Is.EqualTo(before.BoardCount));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(after.GetSeatAt(i).Stack, Is.EqualTo(before.GetSeatAt(i).Stack));
                Assert.That(after.GetSeatAt(i).Committed, Is.EqualTo(before.GetSeatAt(i).Committed));
            }
        }

        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
    }
}
