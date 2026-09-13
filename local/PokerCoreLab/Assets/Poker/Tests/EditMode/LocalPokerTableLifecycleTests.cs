using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class LocalPokerTableLifecycleTests
    {
        private static readonly SeatId A = new SeatId(1), B = new SeatId(2);

        [Test]
        public void LiveHandAndInvalidStartKindsCannotReplaceTheHumanPort()
        {
            var random = new CountingRandom(); var table = Table(random); var port = table.Human;
            var request = Next(table, Setup());
            Assert.That(table.StartNext(request).Error, Is.EqualTo(HandStartError.NotComplete));
            Assert.Throws<ArgumentNullException>(() => table.StartNext(null));
            Assert.Throws<ArgumentException>(() => table.StartNext(HandStartRequest.First(Guid.NewGuid(), Guid.NewGuid(), Setup())));
            Assert.That(table.Human, Is.SameAs(port)); Assert.That(random.Calls, Is.EqualTo(51));
            Assert.That(port.Read().Version, Is.EqualTo(1));
        }

        [Test]
        public void CompletedNextUsesCallerSetupAndOldCommandsStayOnTheOldHand()
        {
            var random = new CountingRandom(); var table = Table(random); var old = table.Human;
            HandCommand fold = Fold(old); var oldResult = old.Read().Result;
            var request = Next(table, Setup(80)); var started = table.StartNext(request); var current = table.Human;
            Assert.That(started.Accepted, Is.True); Assert.That(current, Is.Not.SameAs(old));
            var view = current.Read(); Assert.That(view.HandId, Is.EqualTo(request.HandId));
            Assert.That(view.Version, Is.EqualTo(1)); Assert.That(view.Seats.Sum(s => s.Stack) + view.PotAmount, Is.EqualTo(160));
            Assert.That(view.Seats.Single(s => s.Seat == A).Stack, Is.EqualTo(79));
            Assert.That(table.StartNext(request), Is.SameAs(started)); Assert.That(table.Human, Is.SameAs(current));
            Assert.That(random.Calls, Is.EqualTo(102));
            Assert.That(old.Submit(fold).Accepted, Is.True);
            Assert.That(old.Read().HandId, Is.EqualTo(oldResult.HandId));
            Assert.That(old.Read().Result.TotalAwarded, Is.EqualTo(oldResult.TotalAwarded));
            Assert.That(current.Read().Version, Is.EqualTo(1));
            Assert.That(current.Submit(fold).Error, Is.EqualTo(HandError.WrongHand));
        }

        [Test]
        public void HistoricalStartRetryDoesNotRebindTheCurrentHumanPort()
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            var second = Next(table, Setup()); var secondReceipt = table.StartNext(second); var secondPort = table.Human;
            Fold(secondPort); var third = Next(table, Setup(70)); Assert.That(table.StartNext(third).Accepted, Is.True);
            var thirdPort = table.Human;
            Assert.That(table.StartNext(second), Is.SameAs(secondReceipt));
            Assert.That(table.Human, Is.SameAs(thirdPort)); Assert.That(thirdPort.Read().HandId, Is.EqualTo(third.HandId));
            Assert.That(secondPort.Read().Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(random.Calls, Is.EqualTo(153));
        }

        [Test]
        public void LostOldFoldReceiptCanRecoverAfterNextStartWithoutTouchingTheNewHand()
        {
            var table = Table(new CountingRandom()); var transport = new LostOncePort(table.Human);
            var oldInput = new PokerInputController(transport);
            Assert.Throws<IOException>(() => oldInput.Bet(BettingAction.Fold()));
            Assert.That(oldInput.IsPending, Is.True); Assert.That(table.Human.Read().Phase, Is.EqualTo(HandPhase.Complete));
            var nextRequest = Next(table, Setup()); Assert.That(table.StartNext(nextRequest).Accepted, Is.True);
            var newInput = new PokerInputController(table.Human); var current = newInput.View;
            Assert.That(oldInput.RetryPending(), Is.True); Assert.That(oldInput.IsPending, Is.False);
            Assert.That(transport.Commands[1], Is.SameAs(transport.Commands[0]));
            Assert.That(oldInput.View.Phase, Is.EqualTo(HandPhase.Complete));
            Assert.That(newInput.Receive(oldInput.View), Is.False); Assert.That(newInput.View, Is.SameAs(current));
            Assert.That(table.Human.Read().Version, Is.EqualTo(1)); Assert.That(table.Human.Read().PotAmount, Is.EqualTo(3));
        }

        [TestCase(false, HandStartError.WrongPredecessorHand)]
        [TestCase(true, HandStartError.VersionMismatch)]
        public void WrongCompletedBoundaryIsRejectedBeforeShuffle(bool wrongVersion, HandStartError expected)
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            var port = table.Human; var view = port.Read();
            var request = HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), Setup(),
                wrongVersion ? view.HandId : Guid.NewGuid(), wrongVersion ? view.Version + 1 : view.Version);
            Assert.That(table.StartNext(request).Error, Is.EqualTo(expected));
            Assert.That(table.Human, Is.SameAs(port)); Assert.That(random.Calls, Is.EqualTo(51));
        }

        [Test]
        public void AcceptedIdWithDifferentSettingsIsAConflictWithoutRebinding()
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            var request = Next(table, Setup()); Assert.That(table.StartNext(request).Accepted, Is.True);
            var port = table.Human;
            var changed = HandStartRequest.Next(request.HandId, request.CommandId, Setup(75),
                request.PredecessorHandId.Value, request.PredecessorVersion.Value);
            Assert.That(table.StartNext(changed).Error, Is.EqualTo(HandStartError.CommandConflict));
            Assert.That(table.Human, Is.SameAs(port)); Assert.That(random.Calls, Is.EqualTo(102));
        }

        [Test]
        public void MissingFixedHumanIsRejectedBeforeHostCanCommit()
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human); var port = table.Human;
            var seats = new[] { B, new SeatId(3) };
            var setup = new HandSetup(ChipLedger.Create(seats.Select(s => new SeatChips(s, 100)).ToArray()), seats, seats, seats, seats, 1, 2);
            Assert.Throws<KeyNotFoundException>(() => table.StartNext(Next(table, setup)));
            Assert.That(table.Human, Is.SameAs(port)); Assert.That(random.Calls, Is.EqualTo(51));
            Assert.That(table.StartNext(Next(table, Setup())).Accepted, Is.True);
        }

        [TestCase(1)]
        [TestCase(17)]
        [TestCase(51)]
        public void FailedNextShufflePreservesResultAndCanRetryTheExactStart(int throwAtNextCall)
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            var old = table.Human; var result = old.Read().Result; var request = Next(table, Setup(90));
            random.ThrowAt = random.Calls + throwAtNextCall;
            Assert.Throws<IOException>(() => table.StartNext(request));
            Assert.That(table.Human, Is.SameAs(old)); Assert.That(old.Read().Result.HandId, Is.EqualTo(result.HandId));
            Assert.That(old.Read().Result.TotalAwarded, Is.EqualTo(result.TotalAwarded));
            Assert.That(table.AdvanceOpponent(), Is.False);
            Assert.That(table.StartNext(request).Accepted, Is.True); Assert.That(table.Human.Read().HandId, Is.EqualTo(request.HandId));
        }

        [Test]
        public void NestedOperationsDuringShuffleCannotStartOrAdvanceAnotherHand()
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            var request = Next(table, Setup()); var nested = Next(table, Setup()); var old = table.Human;
            random.Callback = () =>
            {
                random.Callback = null;
                Assert.That(table.AdvanceOpponent(), Is.False);
                Assert.That(table.StartNext(nested).Error, Is.EqualTo(HandStartError.Busy));
                Assert.That(table.Human, Is.SameAs(old));
            };
            Assert.That(table.StartNext(request).Accepted, Is.True); Assert.That(random.Calls, Is.EqualTo(102));
            Assert.That(table.Human.Read().HandId, Is.EqualTo(request.HandId));
        }

        [Test]
        public void NestedOperationsDuringChoiceStillApplyOnlyOneBotAction()
        {
            var policy = new CallbackOpponent(); var table = Table(new CountingRandom(), policy);
            var input = new PokerInputController(table.Human); Assert.That(input.Bet(BettingAction.Call()), Is.True);
            long before = table.Human.Read().Version;
            policy.Callback = view =>
            {
                Assert.That(view.ViewerSeat, Is.EqualTo(B));
                Assert.That(view.OwnCards.Intersect(table.Human.Read().OwnCards), Is.Empty);
                Assert.That(table.AdvanceOpponent(), Is.False);
                Assert.That(table.StartNext(Next(table, Setup())).Error, Is.EqualTo(HandStartError.Busy));
            };
            Assert.That(table.AdvanceOpponent(), Is.True); Assert.That(table.Human.Read().Version, Is.EqualTo(before + 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedOrInvalidOpponentChoiceReleasesTheOperationGuard(bool invalidCommand)
        {
            var policy = new CallbackOpponent { ThrowOnce = !invalidCommand, InvalidOnce = invalidCommand };
            var table = Table(new CountingRandom(), policy); var input = new PokerInputController(table.Human);
            input.Bet(BettingAction.Call()); long before = table.Human.Read().Version;
            if (invalidCommand) Assert.Throws<InvalidOperationException>(() => table.AdvanceOpponent());
            else Assert.Throws<IOException>(() => table.AdvanceOpponent());
            Assert.That(table.Human.Read().Version, Is.EqualTo(before));
            Assert.That(table.AdvanceOpponent(), Is.True); Assert.That(table.Human.Read().Version, Is.EqualTo(before + 1));
        }

        [Test]
        public void TableDoesNotOwnOrDisposeTheInjectedRandomService()
        {
            var random = new CountingRandom(); var table = Table(random); Fold(table.Human);
            Assert.That(table.StartNext(Next(table, Setup())).Accepted, Is.True);
            Assert.That(random.DisposeCalls, Is.Zero); random.Dispose(); Fold(table.Human);
            var old = table.Human;
            Assert.Throws<ObjectDisposedException>(() => table.StartNext(Next(table, Setup())));
            Assert.That(table.Human, Is.SameAs(old)); Assert.That(random.DisposeCalls, Is.EqualTo(1));
        }

        [Test]
        public void RepeatedLocalHandsKeepOldPortsAndConserveEachExplicitSupply()
        {
            var random = new CountingRandom(); var table = Table(random, new RuleBasedDrawOpponent());
            var oldPorts = new List<IPokerSeatPort>(); var ids = new HashSet<Guid>();
            for (int hand = 0; hand < 50; hand++)
            {
                long stack = hand == 0 ? 100 : 20 + hand;
                if (hand > 0) Assert.That(table.StartNext(Next(table, Setup(stack))).Accepted, Is.True);
                var input = new PokerInputController(table.Human); Assert.That(ids.Add(input.View.HandId), Is.True);
                for (int step = 0; input.View.CurrentSeat.HasValue; step++)
                {
                    Assert.That(step, Is.LessThan(100));
                    if (!input.View.IsOwnTurn) Assert.That(table.AdvanceOpponent(), Is.True);
                    else if (input.View.CanExchange) Assert.That(input.Exchange(), Is.True);
                    else Assert.That(input.Bet(input.View.Betting.CanCheck ? BettingAction.Check() : BettingAction.Call()), Is.True);
                    input.Refresh();
                    Assert.That(input.View.Seats.Sum(s => s.Stack) + input.View.PotAmount, Is.EqualTo(2 * stack));
                }
                Assert.That(input.View.Phase, Is.EqualTo(HandPhase.Complete)); oldPorts.Add(table.Human);
                foreach (var old in oldPorts) Assert.That(old.Read().Result, Is.Not.Null);
            }
            Assert.That(random.Calls, Is.EqualTo(51 * 50)); Assert.That(oldPorts.Select(p => p.Read().HandId).Distinct().Count(), Is.EqualTo(50));
        }

        [Test]
        public void PublicTableSurfaceExposesNoHostSessionOrUnboundSeatPort()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            Assert.That(typeof(LocalPokerTable).GetProperties(flags).Select(p => p.Name), Is.EquivalentTo(new[] { "Human" }));
            Assert.That(typeof(LocalPokerTable).GetProperty("Human").PropertyType, Is.EqualTo(typeof(IPokerSeatPort)));
            Assert.That(typeof(LocalPokerTable).GetProperty("Human").GetSetMethod(), Is.Null);
            Assert.That(typeof(LocalPokerTable).GetFields(flags), Is.Empty);
            var methods = typeof(LocalPokerTable).GetMethods(flags).Where(m => !m.IsSpecialName).ToArray();
            Assert.That(methods.Select(m => m.Name), Is.EquivalentTo(new[] { "StartNext", "AdvanceOpponent" }));
            Assert.That(methods.Single(m => m.Name == "StartNext").ReturnType, Is.EqualTo(typeof(HandStartReceipt)));
            Assert.That(methods.Single(m => m.Name == "AdvanceOpponent").ReturnType, Is.EqualTo(typeof(bool)));
        }

        private static HandSetup Setup(long stack = 100)
        {
            var order = new[] { A, B }; var after = new[] { B, A };
            return new HandSetup(ChipLedger.Create(new[] { new SeatChips(A, stack), new SeatChips(B, stack) }),
                order, order, after, after, 1, 2);
        }
        private static LocalPokerTable Table(CountingRandom random, IPokerOpponent opponent = null)
            => new LocalPokerTable(Setup(), A, random, opponent ?? new SimpleDrawOpponent());
        private static HandStartRequest Next(LocalPokerTable table, HandSetup setup)
        {
            var view = table.Human.Read();
            return HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), setup, view.HandId, view.Version);
        }
        private static HandCommand Fold(IPokerSeatPort port)
        {
            var view = port.Read(); var command = HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, BettingAction.Fold());
            Assert.That(port.Submit(command).Accepted, Is.True); Assert.That(port.Read().Phase, Is.EqualTo(HandPhase.Complete)); return command;
        }
        private sealed class CountingRandom : IRandomSource, IDisposable
        {
            public int Calls, ThrowAt = -1, DisposeCalls;
            public Action Callback;
            public int NextInt(int upper)
            {
                if (DisposeCalls > 0) throw new ObjectDisposedException(nameof(CountingRandom));
                Calls++; Callback?.Invoke();
                if (Calls == ThrowAt) throw new IOException("Test shuffle failure.");
                return upper - 1;
            }
            public void Dispose() { DisposeCalls++; }
        }
        private sealed class CallbackOpponent : IPokerOpponent
        {
            public Action<PokerPlayerView> Callback;
            public bool ThrowOnce, InvalidOnce;
            public HandCommand Choose(PokerPlayerView view)
            {
                Callback?.Invoke(view);
                if (ThrowOnce) { ThrowOnce = false; throw new IOException("Test policy failure."); }
                if (InvalidOnce)
                {
                    InvalidOnce = false;
                    return HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version + 1, BettingAction.Check());
                }
                return new SimpleDrawOpponent().Choose(view);
            }
        }
        private sealed class LostOncePort : IPokerSeatPort
        {
            private readonly IPokerSeatPort inner;
            public readonly List<HandCommand> Commands = new List<HandCommand>();
            public LostOncePort(IPokerSeatPort inner) { this.inner = inner; }
            public PokerPlayerView Read() => inner.Read();
            public HandReceipt Submit(HandCommand command)
            {
                Commands.Add(command); var receipt = inner.Submit(command);
                if (Commands.Count == 1) throw new IOException("Test response lost after the fold committed.");
                return receipt;
            }
        }
    }
}
