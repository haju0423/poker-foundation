using System;
using System.Collections.Generic;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed class HoldemLocalTableTests
    {
        [Test]
        public void HumanPortCannotActAsOpponentAndOldViewCannotActInNextHand()
        {
            var table = Make();
            var first = table.Human.Read();
            var forged = HoldemCommand.Act(first.SessionId, first.HandId, Guid.NewGuid(), first.OpponentSeat, first.SessionVersion, BettingAction.Call());
            Assert.That(table.Human.Submit(forged).Error, Is.EqualTo(HoldemCommandError.UnauthorizedSeat));
            Act(table, BettingAction.Fold());
            Assert.That(table.Human.NextHand(table.Human.Read().SessionVersion).Accepted, Is.True);
            var stale = HoldemCommand.Act(first.SessionId, first.HandId, Guid.NewGuid(), first.ViewerSeat, first.SessionVersion, BettingAction.Fold());
            long version = table.Human.Read().SessionVersion;
            Assert.That(table.Human.Submit(stale).Accepted, Is.False);
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(version));
        }

        [Test]
        public void CompletedNoticeReplayDoesNotDuplicateChipPaymentOrReplaceLastAction()
        {
            var table = Make(); var v = table.Human.Read();
            var command = HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), v.ViewerSeat, v.SessionVersion, BettingAction.Call());
            Assert.That(table.Human.Submit(command).Accepted, Is.True);
            var notice = table.Human.LastAction;
            Assert.That(notice.Paid, Is.EqualTo(1));
            Assert.That(table.Human.Submit(command).Accepted, Is.True);
            Assert.That(table.Human.LastAction, Is.SameAs(notice));
            Assert.That(table.Human.Read().OwnStack, Is.EqualTo(98));
        }

        [Test]
        public void PassiveLocalLoopReachesShowdownThenAlternatesButtonWithoutRebuy()
        {
            var table = Make(); var streets = new HashSet<HoldemStreet>();
            for (int step = 0; step < 30 && table.Human.Read().Result == null; step++)
            {
                var v = table.Human.Read(); streets.Add(v.Street);
                if (v.LegalActions != null) Act(table, v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
                else Assert.That(table.AdvanceOpponent(), Is.True);
            }
            var result = table.Human.Read();
            Assert.That(result.Result.Kind, Is.EqualTo(HoldemResultKind.Showdown));
            Assert.That(streets, Is.EquivalentTo(new[] { HoldemStreet.Preflop, HoldemStreet.Flop, HoldemStreet.Turn, HoldemStreet.River }));
            Assert.That(result.OpponentCardCount, Is.EqualTo(2));
            Assert.That(result.OwnStack + result.OpponentStack, Is.EqualTo(200));
            Assert.That(table.Human.NextHand(result.SessionVersion).Accepted, Is.True);
            var next = table.Human.Read();
            Assert.That(next.ButtonSeat, Is.EqualTo(result.OpponentSeat));
            Assert.That(next.OwnStack + 2, Is.EqualTo(result.OwnStack));
            Assert.That(next.OpponentStack + 1, Is.EqualTo(result.OpponentStack));
            Assert.That(next.OpponentCardCount, Is.Zero);
        }

        [Test]
        public void NpcPolicyOnlyReceivesItsOwnCardsAndCannotReadAnAuthority()
        {
            var spy = new InspectPolicy();
            var table = new HoldemLocalTable(new HoldemConfig(100, 1, 2), new FixedRandom(), new FixedRandom(), spy);
            var human = table.Human.Read(); Act(table, BettingAction.Call());
            Assert.That(table.AdvanceOpponent(), Is.True);
            Assert.That(spy.View.ViewerSeat, Is.EqualTo(human.OpponentSeat));
            Assert.That(spy.View.OpponentCardCount, Is.Zero);
            Assert.That(spy.View.Result, Is.Null);
            Assert.That(spy.View.GetOwnCard(0), Is.Not.EqualTo(human.GetOwnCard(0)));
            Assert.That(spy.View.GetOwnCard(1), Is.Not.EqualTo(human.GetOwnCard(1)));
        }

        [Test]
        public void ProductionNpcCompletesSeveralSeededSessionsWithLegalActionsAndConservedChips()
        {
            for (int seed = 0; seed < 12; seed++)
            {
                var table = new HoldemLocalTable(new HoldemConfig(30, 1, 2), new Seeded(seed), new Seeded(1000 + seed), new HoldemNpcPolicy(12));
                for (int hand = 0; hand < 3; hand++)
                {
                    int steps = 0;
                    while (table.Human.Read().Result == null && steps++ < 100)
                    {
                        var v = table.Human.Read();
                        if (v.LegalActions != null) Act(table, v.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call());
                        else Assert.That(table.AdvanceOpponent(), Is.True);
                        v = table.Human.Read();
                        Assert.That(v.OwnStack + v.OpponentStack + v.OwnCommitted + v.OpponentCommitted, Is.EqualTo(60));
                    }
                    var result = table.Human.Read(); Assert.That(result.Result, Is.Not.Null, "Session stalled.");
                    if (result.OwnStack == 0 || result.OpponentStack == 0) break;
                    Assert.That(table.Human.NextHand(result.SessionVersion).Accepted, Is.True);
                }
            }
        }

        [Test]
        public void PolicyRejectsOutOfRangeRandomWithoutChangingHand()
        {
            var table = Make(); var view = table.Human.Read();
            Assert.Throws<InvalidOperationException>(() => new HoldemNpcPolicy(1).Choose(view, new BadRandom()));
            Assert.That(table.Human.Read().SessionVersion, Is.EqualTo(view.SessionVersion));
        }
        private static HoldemLocalTable Make() => new HoldemLocalTable(new HoldemConfig(100, 1, 2), new FixedRandom(), new FixedRandom(), new PassivePolicy());
        private static void Act(HoldemLocalTable table, BettingAction action)
        {
            var v = table.Human.Read();
            Assert.That(table.Human.Submit(HoldemCommand.Act(v.SessionId, v.HandId, Guid.NewGuid(), v.ViewerSeat, v.SessionVersion, action)).Accepted, Is.True);
        }
        private sealed class FixedRandom : IRandomSource { public int NextInt(int upper) => upper - 1; }
        private sealed class BadRandom : IRandomSource { public int NextInt(int upper) => upper; }
        private sealed class Seeded : IRandomSource
        { private readonly Random random; public Seeded(int seed) { random = new Random(seed); } public int NextInt(int upper) => random.Next(upper); }
        private sealed class PassivePolicy : IHoldemOpponentPolicy
        { public BettingAction Choose(HoldemSnapshot view, IRandomSource random) => view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
        private sealed class InspectPolicy : IHoldemOpponentPolicy
        {
            public HoldemSnapshot View;
            public BettingAction Choose(HoldemSnapshot view, IRandomSource random)
            { View = view; return view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call(); }
        }
    }
}
