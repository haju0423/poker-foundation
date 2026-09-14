using System;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Runtime;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed class PracticeContinuationTests
    {
        [Test]
        public void TwentyHandsCarryActualFinalStacksWithoutResetOrMoneyCreation()
        {
            var settings = ScriptableObject.CreateInstance<PracticeTableSettings>();
            try
            {
                var human = new SeatId(1);
                var table = new LocalPokerTable(settings.CreateSetup(), human, new SeededRandom());
                for (int i = 0; i < 20; i++)
                {
                    var port = table.Human; var view = port.Read();
                    Assert.That(view.Seats.Sum(s => s.Stack) + view.PotAmount, Is.EqualTo(200));
                    Assert.That(port.Submit(HandCommand.Bet(view.HandId, Guid.NewGuid(), human, view.Version, BettingAction.Fold())).Accepted, Is.True);
                    var finished = port.Read();
                    Assert.That(finished.Result.Seats.Single(s => s.Seat == human).FinalStack, Is.EqualTo(99 - i));
                    var setup = settings.CreateContinuationSetup(finished.Result);
                    Assert.That(setup.StartingLedger.TotalCommitted, Is.Zero);
                    Assert.That(setup.OddChipPriority, Is.Null);
                    Assert.That(setup.OpeningOrder.Select(s => s.Value), Is.EqualTo(settings.openingOrder));
                    var receipt = table.StartNext(HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), setup, finished.HandId, finished.Version));
                    Assert.That(receipt.Accepted, Is.True);
                    Assert.That(port.Read().HandId, Is.EqualTo(finished.HandId));
                    Assert.That(table.Human.Read().Result, Is.Null);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void UnfinishedAndUnfundedSeatsCannotContinueOrSilentlyRebuy()
        {
            var settings = ScriptableObject.CreateInstance<PracticeTableSettings>();
            try
            {
                Assert.Throws<ArgumentNullException>(() => settings.CreateContinuationSetup(null));
                settings.startingStack = 1;
                var session = new PokerHandSession(Guid.NewGuid(), settings.CreateSetup(), new SeededRandom());
                session.Start(new StartHandCommand(session.HandId, Guid.NewGuid()));
                while (session.State.CurrentSeat.HasValue)
                {
                    var seat = session.State.CurrentSeat.Value;
                    var command = session.State.Phase == HandPhase.Exchange
                        ? HandCommand.Exchange(session.HandId, Guid.NewGuid(), seat, session.Version, Array.Empty<Card>())
                        : HandCommand.Bet(session.HandId, Guid.NewGuid(), seat, session.Version,
                            session.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check());
                    Assert.That(session.Submit(seat, command).Accepted, Is.True);
                }
                var view = PokerPlayerViewProjector.Create(session, new SeatId(1));
                Assert.That(view.Result.Seats.Any(s => s.FinalStack == 0), Is.True);
                Assert.That(KoreanTableText.CanContinue(view), Is.False);
                Assert.Throws<InvalidOperationException>(() => settings.CreateContinuationSetup(view.Result));
                Assert.That(view.Result.Seats.Sum(s => s.FinalStack), Is.EqualTo(2));
                settings.startingStack = 100;
                Assert.That(settings.CreateSetup().StartingLedger.GetChips(new SeatId(1)).Stack, Is.EqualTo(100));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly System.Random random = new System.Random(7);
            public int NextInt(int upper) => random.Next(upper);
        }
    }
}
