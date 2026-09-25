using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemSessionTests
    {
        private static readonly SeatId Human = new SeatId(1);
        private static readonly SeatId Npc = new SeatId(2);

        [Test]
        public void StartAndActionCommandsAreVersionedIdempotentAndConflictSafe()
        {
            HoldemSession session = NewSession();
            Guid handId = Guid.NewGuid();
            Guid startId = Guid.NewGuid();
            var start = new HoldemStartCommand(session.SessionId, handId, startId, 0);
            HoldemReceipt started = session.StartNextHand(start);
            Assert.That(started.Accepted, Is.True);
            Assert.That(started.Version, Is.EqualTo(1));
            Assert.That(session.StartNextHand(start), Is.SameAs(started));
            Assert.That(session.StartNextHand(new HoldemStartCommand(session.SessionId, handId, startId, 1)).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.StartNextHand(new HoldemStartCommand(session.SessionId, Guid.NewGuid(), startId, 1)).Error,
                Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.Version, Is.EqualTo(1));

            Guid actionId = Guid.NewGuid();
            HoldemCommand action = HoldemCommand.Act(session.SessionId, handId, actionId,
                Human, session.Version, BettingAction.Call());
            HoldemReceipt accepted = session.Submit(Human, action);
            Assert.That(accepted.Accepted, Is.True);
            Assert.That(session.Submit(Human, action), Is.SameAs(accepted));
            HoldemCommand conflict = HoldemCommand.Act(session.SessionId, handId, actionId,
                Human, 1, BettingAction.Fold());
            Assert.That(session.Submit(Human, conflict).Error, Is.EqualTo(HoldemCommandError.CommandConflict));
            Assert.That(session.Version, Is.EqualTo(2));
        }

        [Test]
        public void InvalidCommandsLeaveEveryPublishedStateFieldUnchanged()
        {
            HoldemSession session = NewSession();
            Start(session);
            HoldemSnapshot before = session.GetSnapshot(Human);
            long version = session.Version;

            HoldemCommand stale = HoldemCommand.Act(session.SessionId, before.HandId, Guid.NewGuid(),
                Human, 0, BettingAction.Call());
            Assert.That(session.Submit(Human, stale).Error, Is.EqualTo(HoldemCommandError.VersionMismatch));
            HoldemCommand wrongSeat = HoldemCommand.Act(session.SessionId, before.HandId, Guid.NewGuid(),
                Npc, version, BettingAction.Check());
            Assert.That(session.Submit(Human, wrongSeat).Error, Is.EqualTo(HoldemCommandError.UnauthorizedSeat));
            HoldemCommand illegal = HoldemCommand.Act(session.SessionId, before.HandId, Guid.NewGuid(),
                Human, version, BettingAction.Check());
            Assert.That(session.Submit(Human, illegal).Error, Is.EqualTo(HoldemCommandError.IllegalAction));

            HoldemSnapshot after = session.GetSnapshot(Human);
            Assert.That(session.Version, Is.EqualTo(version));
            Assert.That(after.HandVersion, Is.EqualTo(before.HandVersion));
            Assert.That(after.CurrentSeat, Is.EqualTo(before.CurrentSeat));
            Assert.That(after.PotAmount, Is.EqualTo(before.PotAmount));
            Assert.That(after.OwnStack, Is.EqualTo(before.OwnStack));
            Assert.That(after.OpponentStack, Is.EqualTo(before.OpponentStack));
            Assert.That(after.BoardCount, Is.EqualTo(before.BoardCount));
        }

        [Test]
        public void SnapshotHidesOpponentUntilShowdownAndNeverRevealsAfterFold()
        {
            HoldemSession folded = NewSession();
            Start(folded);
            Assert.That(folded.GetSnapshot(Human).OpponentCardCount, Is.Zero);
            Submit(folded, Human, BettingAction.Call());
            Submit(folded, Npc, BettingAction.Check());
            Submit(folded, Npc, BettingAction.Fold());
            HoldemSnapshot foldView = folded.GetSnapshot(Human);
            Assert.That(foldView.OpponentCardCount, Is.Zero);
            Assert.That(foldView.Result.Kind, Is.EqualTo(HoldemResultKind.Fold));
            Assert.That(foldView.Result.OpponentHandValue, Is.Null);
            Assert.That(foldView.Result.OpponentBestCardCount, Is.Zero);

            HoldemSession showdown = NewSession();
            Start(showdown);
            Submit(showdown, Human, BettingAction.RaiseTo(100));
            Submit(showdown, Npc, BettingAction.Call());
            HoldemSnapshot showView = showdown.GetSnapshot(Human);
            Assert.That(showView.Street, Is.EqualTo(HoldemStreet.Complete));
            Assert.That(showView.OpponentCardCount, Is.EqualTo(2));
            Assert.That(showView.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(showView.Result.OpponentHandValue.HasValue, Is.True);
            Assert.That(showView.Result.OpponentBestCardCount, Is.EqualTo(5));
        }

        [Test]
        public void StacksPersistAndButtonAlternatesOnlyWhenNextHandExplicitlyStarts()
        {
            HoldemSession session = NewSession();
            Start(session);
            HoldemSnapshot first = session.GetSnapshot(Human);
            Assert.That(first.ButtonSeat, Is.EqualTo(Human));
            Submit(session, Human, BettingAction.Fold());
            Assert.That(session.HandNumber, Is.EqualTo(1));
            Assert.That(session.CanContinue, Is.True);
            Assert.That(session.GetSnapshot(Human).OwnStack, Is.EqualTo(99));
            Assert.That(session.GetSnapshot(Human).OpponentStack, Is.EqualTo(101));

            Guid nextHand = Guid.NewGuid();
            HoldemReceipt next = session.StartNextHand(nextHand, Guid.NewGuid(), session.Version);
            Assert.That(next.Accepted, Is.True);
            HoldemSnapshot second = session.GetSnapshot(Human);
            Assert.That(session.HandNumber, Is.EqualTo(2));
            Assert.That(second.ButtonSeat, Is.EqualTo(Npc));
            Assert.That(second.OwnStack, Is.EqualTo(97));
            Assert.That(second.OpponentStack, Is.EqualTo(100));
            Assert.That(second.CurrentSeat, Is.EqualTo(Npc));
            Assert.That(second.SessionVersion, Is.EqualTo(next.Version));
            Assert.That(second.HandVersion, Is.EqualTo(1));
            Assert.That(second.SessionVersion, Is.Not.EqualTo(second.HandVersion));
            Submit(session, Npc, BettingAction.Fold());
            long version = session.Version;
            Assert.That(session.StartNextHand(first.HandId, Guid.NewGuid(), version).Error,
                Is.EqualTo(HoldemCommandError.HandIdReused));
            Assert.That(session.Version, Is.EqualTo(version));
        }

        [Test]
        public void BustEndsSessionAndRestartRemainsExplicitlyUnavailable()
        {
            Card[] prefix = {
                CardOf("2c"), CardOf("Ac"), CardOf("3c"), CardOf("Ad"),
                CardOf("4c"), CardOf("5d"), CardOf("7h"), CardOf("9s"),
                CardOf("Tc"), CardOf("Jd"), CardOf("Qc"), CardOf("Qh")
            };
            var session = new HoldemSession(Guid.NewGuid(), new HoldemConfig(2, 1, 2),
                Human, Npc, new PrefixRandom(prefix));
            Start(session);
            Assert.That(session.GetSnapshot(Npc).OwnStack, Is.Zero);
            Assert.That(session.IsEliminatedAfterSettlement(Npc), Is.False, "Posting the last chips as a blind is not elimination.");
            Submit(session, Human, BettingAction.Call());
            Assert.That(session.IsOver, Is.True);
            Assert.That(session.IsEliminatedAfterSettlement(Npc), Is.True);
            Assert.That(session.IsEliminatedAfterSettlement(Human), Is.False);
            Assert.That(session.CanContinue, Is.False);
            Assert.That(session.BustedSeat, Is.EqualTo(Npc));
            HoldemSnapshot final = session.GetSnapshot(Human);
            Assert.That(final.OwnStack, Is.EqualTo(4));
            Assert.That(final.CanContinue, Is.False);
            Assert.That(final.IsOver, Is.True);
            Assert.That(final.BustedSeat, Is.EqualTo(Npc));
            long version = session.Version;
            Assert.That(session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), version).Error,
                Is.EqualTo(HoldemCommandError.CannotContinue));
            Assert.That(session.Version, Is.EqualTo(version));
            Assert.That(session.HandNumber, Is.EqualTo(1));
        }

        private static HoldemSession NewSession() => new HoldemSession(Guid.NewGuid(),
            new HoldemConfig(100, 1, 2), Human, Npc, new IdentityRandom());

        private static void Start(HoldemSession session)
        {
            HoldemReceipt receipt = session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), session.Version);
            Assert.That(receipt.Accepted, Is.True);
        }

        private static void Submit(HoldemSession session, SeatId seat, BettingAction action)
        {
            HoldemSnapshot snapshot = session.GetSnapshot(seat);
            HoldemReceipt receipt = session.Submit(seat, HoldemCommand.Act(session.SessionId,
                snapshot.HandId, Guid.NewGuid(), seat, session.Version, action));
            Assert.That(receipt.Accepted, Is.True, receipt.Error.ToString());
        }

        private static Card CardOf(string text)
        {
            const string ranks = "23456789TJQKA";
            const string suits = "cdhs";
            return new Card((Rank)(ranks.IndexOf(text[0]) + 2), (Suit)(suits.IndexOf(text[1]) + 1));
        }

        private sealed class IdentityRandom : IRandomSource
        {
            public int NextInt(int exclusiveMax) => exclusiveMax - 1;
        }

        private sealed class PrefixRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();

            public PrefixRandom(IReadOnlyList<Card> prefix)
            {
                var target = new List<Card>(prefix);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, Card.DeckSize).Select(Card.FromId)
                    .Where(card => !target.Contains(card)));
                Card[] working = Enumerable.Range(0, Card.DeckSize).Select(Card.FromId).ToArray();
                for (int i = working.Length - 1; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1);
                    choices.Enqueue(selected);
                    Card saved = working[i]; working[i] = working[selected]; working[selected] = saved;
                }
            }

            public int NextInt(int exclusiveMax) => choices.Dequeue();
        }
    }
}
