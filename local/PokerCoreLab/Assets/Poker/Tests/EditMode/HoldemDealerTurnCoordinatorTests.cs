using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemDealerTurnTests
    {
        [Test]
        public void CoordinatorIssuesOneFrozenWorkAndMarshalsConcurrentPostsWithoutWorkerPortCalls()
        {
            Create(); Start(); var speech = Speak(2, "원문은 그대로 전달"); EndBetting();
            var port = TimerPort(); int owner = Thread.CurrentThread.ManagedThreadId;
            port.Read = () => { Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(owner));
                return room.ReadPendingDealerTurn(peers[0]); };
            port.Complete = command => { Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(owner));
                return room.DealUnchanged(peers[0], command); };
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            Assert.That(coordinator.Poll(out var work), Is.Null);
            var turn = Read();
            Assert.That(work.TargetDeal, Is.SameAs(turn.TargetDeal));
            Assert.That(work.ExpectedVersion, Is.EqualTo(turn.ExpectedVersion));
            Assert.That(work.SourceUtterances, Is.SameAs(turn.SourceUtterances));
            for (int i = 0; i < 10; i++)
            { Assert.That(coordinator.Poll(out var repeated), Is.Null); Assert.That(repeated, Is.Null); }
            Assert.That(room.AcknowledgeUtteranceBatch(peers[0], speech), Is.True);
            Assert.That(work.SourceUtterances.GetEntry(0).Text, Is.EqualTo("원문은 그대로 전달"));
            var posts = Enumerable.Range(0, 16).Select(_ => Task.Run(() => coordinator.TryPostUnchanged(work))).ToArray();
            Assert.That(Task.WaitAll(posts, 3000), Is.True);
            Assert.That(posts.All(t => t.Result), Is.True); Assert.That(port.Commands, Is.Empty);
            Assert.That(State.IsDealPending, Is.True);
            Assert.That(coordinator.Poll(out var next).Accepted, Is.True); Assert.That(next, Is.Null);
            Assert.That(State.SessionVersion, Is.EqualTo(work.ExpectedVersion + 1));
            Assert.That(State.BoardCount, Is.EqualTo(3)); Assert.That(port.Commands.Count, Is.EqualTo(1));
            Assert.That(coordinator.TryPostUnchanged(work), Is.False);
            Assert.That(coordinator.Poll(out next), Is.Null); Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void CoordinatorKeepsPostedCompletionDuringDisconnectAndAppliesOnceAfterReconnect(int disconnected)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); room.Disconnect(peers[disconnected]);
            Assert.That(Task.Run(() => coordinator.TryPostUnchanged(work)).Result, Is.True);
            for (int i = 0; i < 3; i++)
            { Assert.That(coordinator.Poll(out var next), Is.Null); Assert.That(next, Is.Null); }
            Assert.That(port.Commands, Is.Empty); RestorePeer(disconnected);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(State.IsRevealPending, Is.True); Assert.That(port.Commands.Count, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void CoordinatorRetriesOriginalCommandWhenDisconnectIsObservedAtCompletion(int disconnected)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            port.Complete = command => { room.Disconnect(peers[disconnected]); return room.DealUnchanged(peers[0], command); };
            Assert.That(coordinator.Poll(out _).Error,
                Is.EqualTo(disconnected == 0 ? HoldemRoomError.Disconnected : HoldemRoomError.Paused));
            RestorePeer(disconnected); port.Complete = command => room.DealUnchanged(peers[0], command);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(port.Commands.Count, Is.EqualTo(2)); Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoordinatorReconcilesUncertainReceiptWithExactCommandEvenAfterWindowAdvanced(bool commitBeforeThrow)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            port.Complete = command => {
                if (commitBeforeThrow) Assert.That(room.DealUnchanged(peers[0], command).Accepted, Is.True);
                throw new IOException("Reply unavailable");
            };
            Assert.Throws<IOException>(() => coordinator.Poll(out _));
            if (commitBeforeThrow) { Resume(); EndBetting(); }
            var before = Read();
            port.Complete = command => room.DealUnchanged(peers[0], command);
            Assert.That(coordinator.Poll(out var next).Accepted, Is.True); Assert.That(next, Is.Null);
            Assert.That(port.Commands.Count, Is.EqualTo(2)); Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
            if (commitBeforeThrow)
            {
                Assert.That(State.SessionVersion, Is.EqualTo(before.ExpectedVersion));
                Assert.That(Read().TargetDeal.WindowId, Is.EqualTo(before.TargetDeal.WindowId));
                Assert.That(coordinator.Poll(out next), Is.Null); Assert.That(next, Is.Not.Null);
                Assert.That(next.TargetDeal.Street, Is.EqualTo(HoldemStreet.Turn));
            }
            else Assert.That(State.IsRevealPending, Is.True);
        }

        [Test]
        public void CoordinatorRecoversLostSuccessWhenReadNowContainsNoTurn()
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            port.Complete = command => { room.DealUnchanged(peers[0], command); return null; };
            Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _));
            Assert.That(room.ReadPendingDealerTurn(peers[0]).Turn, Is.Null);
            long version = State.SessionVersion;
            port.Complete = command => room.DealUnchanged(peers[0], command);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
            Assert.That(State.SessionVersion, Is.EqualTo(version));
            Assert.That(port.Commands[1], Is.SameAs(port.Commands[0]));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoordinatorDropsUnattemptedCompletionAfterAnotherOwnerOrTimeoutDealt(bool postFirst)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work);
            if (postFirst) Assert.That(coordinator.TryPostUnchanged(work), Is.True);
            double time = 0;
            using var timeout = new HoldemDealerTurnTimeout(TimerPort(), TimeSpan.FromSeconds(1), () => TimeSpan.FromSeconds(time));
            timeout.Poll(); time = 1; Assert.That(timeout.Poll().Accepted, Is.True);
            Resume(); EndBetting(); long version = State.SessionVersion;
            Assert.That(coordinator.Poll(out var next), Is.Null);
            Assert.That(next.TargetDeal.Street, Is.EqualTo(HoldemStreet.Turn));
            Assert.That(coordinator.TryPostUnchanged(work), Is.False); Assert.That(port.Commands, Is.Empty);
            Assert.That(State.SessionVersion, Is.EqualTo(version));
            Assert.That(coordinator.TryPostUnchanged(next), Is.True);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoordinatorRejectsOldHandOrSessionWorkBeforeCallingAuthority(bool newSession)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            if (newSession) Create();
            else
            {
                for (int i = 0; i < 3; i++)
                { room.DealUnchanged(peers[0], Read().CreateUnchangedCommand(Guid.NewGuid())); Resume(); EndBetting(); }
                Assert.That(State.Result, Is.Not.Null);
            }
            Start(); EndBetting(); long version = State.SessionVersion;
            coordinator.Poll(out var next);
            Assert.That(next.TargetDeal.HandId, Is.Not.EqualTo(work.TargetDeal.HandId));
            Assert.That(coordinator.TryPostUnchanged(work), Is.False); Assert.That(port.Commands, Is.Empty);
            Assert.That(State.SessionVersion, Is.EqualTo(version));
        }

        [Test]
        public void CoordinatorDoesNotRepeatDefinitiveReceiptEvenWithLaggingRead()
        {
            foreach (bool accept in new[] { false, true })
            {
                Create(); Start(); EndBetting(); var port = TimerPort();
                var frozen = room.ReadPendingDealerTurn(peers[0]); port.Read = () => frozen;
                port.Complete = command => room.DealUnchanged(peers[accept ? 0 : 1], command);
                using var coordinator = new HoldemDealerTurnCoordinator(port);
                coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
                Assert.That(coordinator.Poll(out _).Accepted, Is.EqualTo(accept));
                Assert.That(coordinator.TryPostUnchanged(work), Is.False);
                Assert.That(coordinator.Poll(out var repeated), Is.Null); Assert.That(repeated, Is.Null);
                Assert.That(port.Commands.Count, Is.EqualTo(1));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CoordinatorClosesMailboxWhenDisposedOrPortCloses(int closure)
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            if (closure == 0) coordinator.Dispose();
            else if (closure == 1) port.Read = () => throw new ObjectDisposedException("room");
            else port.Complete = _ => throw new ObjectDisposedException("room");
            Assert.Throws<ObjectDisposedException>(() => coordinator.Poll(out _));
            Assert.That(Task.Run(() => coordinator.TryPostUnchanged(work)).Result, Is.False);
            port.Read = () => room.ReadPendingDealerTurn(peers[0]);
            Assert.Throws<ObjectDisposedException>(() => coordinator.Poll(out _)); Assert.That(State.IsDealPending, Is.True);
        }

        [Test]
        public void CoordinatorRejectsForeignWorkWorkerPollingAndReentrantOwnerCalls()
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            using var other = new HoldemDealerTurnCoordinator(port);
            Assert.Throws<ArgumentNullException>(() => new HoldemDealerTurnCoordinator(null));
            coordinator.Poll(out var work); other.Poll(out _);
            Assert.That(other.TryPostUnchanged(work), Is.False); Assert.That(coordinator.TryPostUnchanged(null), Is.False);
            Task.Run(() => {
                Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _));
                Assert.Throws<InvalidOperationException>(() => coordinator.Dispose());
            }).GetAwaiter().GetResult();
            port.Read = () => { Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _));
                Assert.Throws<InvalidOperationException>(() => coordinator.Dispose()); return room.ReadPendingDealerTurn(peers[0]); };
            coordinator.Poll(out _); Assert.That(port.Commands, Is.Empty);
            Assert.That(coordinator.TryPostUnchanged(work), Is.True); Assert.That(coordinator.Poll(out _).Accepted, Is.True);
        }

        [Test]
        public void CoordinatorFailsClosedOnChangedVersionOrLostAuthority()
        {
            foreach (bool badVersion in new[] { false, true })
            {
                Create(); Start(); EndBetting(); var port = TimerPort();
                using var coordinator = new HoldemDealerTurnCoordinator(port);
                coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
                var original = Read();
                var changed = (HoldemDealerTurn)Activator.CreateInstance(typeof(HoldemDealerTurn),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] {
                        original.TargetDeal, original.ExpectedVersion + 1, true, original.SourceUtterances }, null);
                var read = (HoldemDealerTurnRead)Activator.CreateInstance(typeof(HoldemDealerTurnRead),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { HoldemRoomError.None, changed }, null);
                port.Read = () => badVersion ? read : room.ReadPendingDealerTurn(peers[1]);
                Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _));
                Assert.That(coordinator.TryPostUnchanged(work), Is.False); Assert.That(port.Commands, Is.Empty);
                Assert.Throws<ObjectDisposedException>(() => coordinator.Poll(out _));
            }
        }

        [Test]
        public void CoordinatorReadFailurePreservesPendingCompletionWithoutCallingAuthority()
        {
            Create(); Start(); EndBetting(); var port = TimerPort();
            using var coordinator = new HoldemDealerTurnCoordinator(port);
            coordinator.Poll(out var work); coordinator.TryPostUnchanged(work);
            port.Read = () => null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Poll(out _)); Assert.That(port.Commands, Is.Empty);
            port.Read = () => throw new IOException("Read unavailable");
            Assert.Throws<IOException>(() => coordinator.Poll(out _)); Assert.That(port.Commands, Is.Empty);
            port.Read = () => room.ReadPendingDealerTurn(peers[0]);
            Assert.That(coordinator.Poll(out _).Accepted, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoordinatorPreservesInputMeaningAndDoesNotAckOrInterpretSkippedAllInWindows(bool speech)
        {
            Create(speech); Start(); if (speech) Speak(1, "첫 구간만 입력"); Act(shove: true); EndBetting();
            using var coordinator = new HoldemDealerTurnCoordinator(TimerPort());
            for (int i = 0; i < 3; i++)
            {
                Assert.That(coordinator.Poll(out var work), Is.Null);
                Assert.That(work.InputKind, Is.EqualTo(!speech ? HoldemDealerInputKind.Disabled : i == 0
                    ? HoldemDealerInputKind.ClosedBettingWindow : HoldemDealerInputKind.NoBettingWindow));
                Assert.That(coordinator.TryPostUnchanged(work), Is.True);
                Assert.That(coordinator.Poll(out _).Accepted, Is.True); Resume();
            }
            Assert.That(State.Result, Is.Not.Null);
            Assert.That(room.ReadClosedUtteranceBatches(peers[0]).Count, Is.EqualTo(speech ? 1 : 0));
            Assert.That(coordinator.Poll(out var ended), Is.Null); Assert.That(ended, Is.Null);
        }

        [Test]
        public void CoordinatorWorkerInputHasNoPublicCommandFactoryOrAuthority()
        {
            Assert.That(typeof(HoldemDealerWork).GetConstructors(), Is.Empty);
            var methods = typeof(HoldemDealerWork).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.That(methods.All(m => m.IsSpecialName && m.Name.StartsWith("get_", StringComparison.Ordinal)), Is.True);
            var types = typeof(HoldemDealerWork).GetProperties().Select(p => p.PropertyType).ToArray();
            Assert.That(types, Is.EquivalentTo(new[] { typeof(HoldemDealWindow), typeof(long),
                typeof(HoldemDealerInputKind), typeof(HoldemUtteranceBatch) }));
        }
    }
}
