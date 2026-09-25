using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemRematchTests
    {
        private static readonly SeatId Host = new SeatId(1);
        private HoldemRoom room;
        private Guid[] connections;
        private HoldemRoomAdmission[] admissions;

        private void StartRoom(int count, bool speech = false, bool legacy = false)
        {
            connections = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
            room = HoldemRoom.Create(Guid.NewGuid(), connections[0], "친구 1", new HoldemConfig(100, 1, 2),
                Host, new DeckRandom(count), out var host, HoldemOddChipRule.ClockwiseFromButton,
                speech ? new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw) : null, count);
            admissions = new HoldemRoomAdmission[count]; admissions[0] = host;
            for (int i = 1; i < count; i++)
                admissions[i] = room.Join(connections[i], "친구 " + (i + 1), !legacy || i != count - 1).Admission;
            foreach (var c in connections) room.SetReady(c, true);
            Assert.That(room.StartHand(connections[0], NewStart(room.Read(connections[0]).SessionId, 0)).Accepted, Is.True);
        }

        private void FinishRoom()
        {
            for (int step = 0; step < 32 && !room.Read(connections[0]).Game.IsOver; step++)
            {
                var game = room.Read(connections[0]).Game;
                Assert.That(game.CurrentSeat.HasValue, Is.True);
                Guid actor = connections[game.CurrentSeat.Value.Value - 1];
                game = room.Read(actor).Game;
                Assert.That(room.Submit(actor, new HoldemRoomAction(game.SessionId, game.HandId, Guid.NewGuid(),
                    game.SessionVersion, AllIn(game.LegalActions))).Accepted, Is.True);
            }
            Assert.That(room.Read(connections[0]).Game.IsOver, Is.True);
        }

        private HoldemRoomError Vote(int index, bool ready = true, long? revision = null)
        {
            var view = room.Read(connections[index]);
            return room.SetRematchReady(connections[index], view.Game.HandId, view.Game.SessionVersion,
                revision ?? view.GetMember(index).RematchRevision, ready);
        }
        private void VoteAll()
        { for (int i = 0; i < connections.Length; i++) Assert.That(Vote(i), Is.EqualTo(HoldemRoomError.None)); }
        private static HoldemStartCommand NewStart(Guid session, long version, Guid? id = null)
            => new HoldemStartCommand(session, Guid.NewGuid(), id ?? Guid.NewGuid(), version);
        private static BettingAction AllIn(LegalBettingActions legal) => legal.CanRaise
            ? BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)
            : legal.CanBet ? BettingAction.BetTo(legal.MaximumAggressiveTarget.Value)
            : legal.CanCall ? BettingAction.Call() : BettingAction.Check();

        [TestCase(3)] [TestCase(4)]
        public void RematchNeedsFreshConsentThenRestoresChipsOnceWithoutReplacingTheRoom(int count)
        {
            StartRoom(count, true);
            var first = room.Read(connections[0]);
            var input = new HoldemRoomUtterance(first.SessionId, first.Game.HandId, first.OwnUtterances.WindowId,
                Guid.NewGuid(), first.Game.Street, "원래 경기의 멘트");
            Assert.That(room.SubmitUtterance(connections[0], input).Accepted, Is.True);
            Assert.That(Vote(0), Is.EqualTo(HoldemRoomError.MatchNotOver));
            FinishRoom();
            var final = room.Read(connections[0]);
            var command = NewStart(final.SessionId, final.Game.SessionVersion);
            Assert.That(final.Game.GetSeat(Host).Stack, Is.EqualTo(count * 100));
            Assert.That(final.GetMember(0).Ready, Is.True);
            Assert.That(final.GetMember(0).RematchReady, Is.False, "Initial lobby readiness must never imply rematch consent.");
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId).Error, Is.EqualTo(HoldemRoomError.PlayersNotReady));
            var batches = room.ReadClosedUtteranceBatches(connections[0]).ToArray();
            Assert.That(batches.Length, Is.EqualTo(1));
            VoteAll();
            Assert.That(room.RestartMatch(connections[1], command, final.Game.HandId).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.Read(connections[0]).Game.HandId, Is.EqualTo(final.Game.HandId));
            Assert.That(room.Read(connections[0]).Game.Result, Is.Not.Null, "Voting keeps the final result.");
            var receipt = room.RestartMatch(connections[0], command, final.Game.HandId);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId), Is.SameAs(receipt));
            var next = room.Read(connections[0]);
            Assert.That(next.SessionId, Is.EqualTo(final.SessionId));
            Assert.That(next.MatchNumber, Is.EqualTo(2));
            Assert.That(next.Game.HandNumber, Is.EqualTo(final.Game.HandNumber + 1));
            Assert.That(next.Game.SessionVersion, Is.EqualTo(final.Game.SessionVersion + 1));
            Assert.That(next.Game.ButtonSeat, Is.EqualTo(Host));
            Assert.That(next.Game.HandId, Is.EqualTo(command.HandId));
            Assert.That(next.Game.IsOver, Is.False);
            for (int i = 0; i < count; i++)
            {
                var seat = next.Game.GetSeatAt(i);
                Assert.That(seat.Stack + seat.Committed, Is.EqualTo(100));
                Assert.That(next.GetMember(i).RematchReady, Is.False);
                Assert.That(room.Read(connections[i]).Game.OwnCardCount, Is.EqualTo(2));
                Assert.That(room.Read(connections[i]).OwnUtterances.Count, Is.Zero);
            }
            Assert.That(next.History.Count, Is.EqualTo(2), "Only new blinds, never fake uncalled returns from restored stacks.");
            Assert.That(next.PublicUtterances.Count, Is.Zero);
            Assert.That(room.ReadClosedUtteranceBatches(connections[0])[0], Is.SameAs(batches[0]));
            Assert.That(room.SubmitUtterance(connections[0], input).Accepted, Is.False);
            Assert.That(room.Submit(connections[0], new HoldemRoomAction(final.SessionId, final.Game.HandId, Guid.NewGuid(),
                final.Game.SessionVersion, BettingAction.Fold())).Poker.Error, Is.EqualTo(HoldemCommandError.WrongHand));
            Assert.That(room.RestartMatch(connections[0], NewStart(final.SessionId, final.Game.SessionVersion, command.CommandId),
                final.Game.HandId).Error, Is.EqualTo(HoldemRoomError.CommandConflict));
            Assert.That(new HoldemLobbyDisplay(HoldemRoomPacketMapper.Create(next)).MatchNumber, Is.EqualTo(2));
        }

        [TestCase(3)] [TestCase(4)]
        public void CancelAndDisconnectInvalidateEarlierConsentEvenWhenItsReplyWasLost(int count)
        {
            StartRoom(count); FinishRoom();
            var final = room.Read(connections[0]); long voteRevision = final.GetMember(1).RematchRevision;
            Assert.That(Vote(1, false, -1), Is.EqualTo(HoldemRoomError.StaleRematchConsent));
            Assert.That(Vote(1, true, voteRevision), Is.EqualTo(HoldemRoomError.None));
            long acceptedRevision = room.Read(connections[0]).Revision;
            Assert.That(Vote(1, true, voteRevision), Is.EqualTo(HoldemRoomError.None));
            Assert.That(room.Read(connections[0]).Revision, Is.EqualTo(acceptedRevision), "Retry is idempotent.");
            Assert.That(Vote(1, false), Is.EqualTo(HoldemRoomError.None));
            Assert.That(Vote(1, true, voteRevision), Is.EqualTo(HoldemRoomError.StaleRematchConsent));
            Assert.That(room.Read(connections[0]).GetMember(1).RematchReady, Is.False);
            VoteAll();
            long pendingBasis = room.Read(connections[0]).GetMember(1).RematchRevision - 1;
            room.Disconnect(connections[1]);
            Assert.That(room.Read(connections[0]).Paused, Is.False, "The settled result stays available.");
            var command = NewStart(final.SessionId, final.Game.SessionVersion);
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId).Accepted, Is.False);
            connections[1] = Guid.NewGuid();
            Assert.That(room.Reconnect(connections[1], admissions[1].ResumeToken).Accepted, Is.True);
            Assert.That(Vote(1, true, pendingBasis), Is.EqualTo(HoldemRoomError.StaleRematchConsent));
            Assert.That(room.Read(connections[0]).GetMember(1).RematchReady, Is.False);
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId).Error, Is.EqualTo(HoldemRoomError.PlayersNotReady));
            Assert.That(Vote(1), Is.EqualTo(HoldemRoomError.None));
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId).Accepted, Is.True);
            room.Disconnect(connections[1]);
            Assert.That(room.Read(connections[0]).Paused, Is.True);
            Assert.That(room.RestartMatch(connections[0], command, final.Game.HandId).Accepted, Is.True,
                "Lost restart acknowledgment is recoverable without requiring consent a second time.");
            Assert.That(room.Read(connections[0]).MatchNumber, Is.EqualTo(2));
        }

        [Test]
        public void LegacyParticipantsKeepTheFirstMatchButCannotBeResetWithoutConsentSupport()
        {
            StartRoom(4, legacy: true); FinishRoom();
            var before = room.Read(connections[0]);
            Assert.That(before.RematchSupported, Is.False);
            Assert.That(Vote(0), Is.EqualTo(HoldemRoomError.ClientUpgradeRequired));
            Assert.That(room.RestartMatch(connections[0], NewStart(before.SessionId, before.Game.SessionVersion),
                before.Game.HandId).Error, Is.EqualTo(HoldemRoomError.ClientUpgradeRequired));
            Assert.That(room.Read(connections[0]).Revision, Is.EqualTo(before.Revision));
        }

        [Test]
        public void AcceptedConsentRetryKeepsItsReceiptWhenAnotherClientLosesCapability()
        {
            StartRoom(4); FinishRoom();
            long basis = room.Read(connections[0]).GetMember(0).RematchRevision;
            Assert.That(Vote(0, true, basis), Is.EqualTo(HoldemRoomError.None));
            room.Disconnect(connections[3]); connections[3] = Guid.NewGuid();
            Assert.That(room.Reconnect(connections[3], admissions[3].ResumeToken, false).Accepted, Is.True);
            long revision = room.Read(connections[0]).Revision;
            Assert.That(Vote(0, true, basis), Is.EqualTo(HoldemRoomError.None));
            Assert.That(room.Read(connections[0]).Revision, Is.EqualTo(revision));
            Assert.That(Vote(1), Is.EqualTo(HoldemRoomError.ClientUpgradeRequired));
        }

        [Test]
        public void APlayerCanWithdrawConsentWhileAnotherFriendUsesAnOlderClient()
        {
            StartRoom(4); FinishRoom(); VoteAll();
            long oldVote = room.Read(connections[0]).GetMember(0).RematchRevision - 1;
            room.Disconnect(connections[3]); connections[3] = Guid.NewGuid();
            Assert.That(room.Reconnect(connections[3], admissions[3].ResumeToken, false).Accepted, Is.True);
            Assert.That(room.Read(connections[0]).RematchSupported, Is.False);
            Assert.That(Vote(0, false), Is.EqualTo(HoldemRoomError.None));
            room.Disconnect(connections[3]); connections[3] = Guid.NewGuid();
            Assert.That(room.Reconnect(connections[3], admissions[3].ResumeToken, true).Accepted, Is.True);
            Assert.That(Vote(0, true, oldVote), Is.EqualTo(HoldemRoomError.StaleRematchConsent));
            Assert.That(room.Read(connections[0]).GetMember(0).RematchReady, Is.False);
        }

        [Test]
        public void CoreRestoresExactCustomLedgerAndKeepsCommandKindsSeparate()
        {
            var seats = new[] { Host, new SeatId(2), new SeatId(3) };
            var random = new DeckRandom(2);
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(100, 1, 2),
                ChipLedger.Create(new[] { new SeatChips(Host, 100), new SeatChips(seats[1], 40), new SeatChips(seats[2], 0) }),
                seats, seats[2], random, HoldemOddChipRule.ClockwiseFromButton);
            var first = NewStart(session.SessionId, 0); session.StartNextHand(first);
            var active = session.GetSnapshot(Host);
            Assert.That(session.RestartMatch(first, active.HandId).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.RestartMatch(NewStart(session.SessionId, session.Version), active.HandId).Error,
                Is.EqualTo(HoldemCommandError.MatchNotOver));
            for (int step = 0; step < 8 && !session.IsOver; step++)
            {
                var actor = session.GetSnapshot(Host).CurrentSeat.Value;
                var game = session.GetSnapshot(actor);
                Assert.That(session.Submit(actor, HoldemCommand.Act(session.SessionId, game.HandId, Guid.NewGuid(), actor,
                    session.Version, AllIn(game.LegalActions))).Accepted, Is.True);
            }
            Assert.That(session.IsOver, Is.True);
            var final = session.GetSnapshot(Host);
            var command = NewStart(session.SessionId, session.Version);
            Assert.That(session.StartNextHand(command).Error, Is.EqualTo(HoldemCommandError.CannotContinue));
            random.Fail = true;
            Assert.Throws<InvalidOperationException>(() => session.RestartMatch(command, final.HandId));
            Assert.That(session.Version, Is.EqualTo(final.SessionVersion));
            Assert.That(session.MatchNumber, Is.EqualTo(1));
            Assert.That(session.GetSnapshot(Host).OwnStack, Is.EqualTo(140));
            random.Fail = false;
            var receipt = session.RestartMatch(command, final.HandId);
            Assert.That(receipt.Accepted, Is.True);
            Assert.That(session.RestartMatch(command, final.HandId), Is.SameAs(receipt));
            Assert.That(session.StartNextHand(command).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            var next = session.GetSnapshot(Host);
            Assert.That(next.ButtonSeat, Is.EqualTo(Host));
            Assert.That(next.GetSeat(Host).Stack + next.GetSeat(Host).Committed, Is.EqualTo(100));
            Assert.That(next.GetSeat(seats[1]).Stack + next.GetSeat(seats[1]).Committed, Is.EqualTo(40));
            Assert.That(next.GetSeat(seats[2]).Stack, Is.Zero);
            Assert.That(session.RestartMatch(NewStart(session.SessionId, session.Version), final.HandId).Error,
                Is.EqualTo(HoldemCommandError.WrongHand));
        }

        [Test]
        public void LobbyRejectsImpossibleRematchVotesAndKeepsLegacyPacketsReadable()
        {
            StartRoom(4);
            var packet = HoldemRoomPacketMapper.Create(room.Read(connections[0]));
            packet.members[1].rematchReady = true;
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(packet));
            packet.members[1].rematchReady = false; packet.hasRematch = false;
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(packet));
            packet.rematchSupported = false; packet.matchNumber = 0;
            Assert.That(new HoldemLobbyDisplay(packet).HasRematch, Is.False);
            packet.members[1].rematchRevision = -1;
            Assert.Throws<ArgumentException>(() => new HoldemLobbyDisplay(packet));
        }

        internal sealed class DeckRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public bool Fail;
            public DeckRandom(int count)
            {
                string holes = count == 4 ? "Kh Qh Jh Ah Kd Qd Jd Ad" : count == 3 ? "Kh Qh Ah Kd Qd Ad" : "Kh Ah Kd Ad";
                var target = (holes + " 2c 3c 5d 8s 4h 9s 6h Tc").Split(' ')
                    .Select(s => new Card((Rank)("23456789TJQKA".IndexOf(s[0]) + 2), (Suit)("cdhs".IndexOf(s[1]) + 1))).ToList();
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)));
                var working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                { int j = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(j); var old = working[i]; working[i] = working[j]; working[j] = old; }
            }
            public int NextInt(int exclusiveMax)
            { if (Fail) throw new InvalidOperationException("Injected shuffle failure."); return choices.Count > 0 ? choices.Dequeue() : exclusiveMax - 1; }
        }
    }
}
