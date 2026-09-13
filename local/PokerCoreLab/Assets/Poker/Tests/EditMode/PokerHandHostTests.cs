using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;

namespace Poker.Foundation.Tests
{
    public sealed class PokerHandHostTests
    {
        private static readonly SeatId A = new SeatId(40), B = new SeatId(7), C = new SeatId(91), D = new SeatId(2);

        [TestCase(true)]
        [TestCase(false)]
        public void SecondIndexAppendFailureRollsBackHistoryAndPreservesCurrentHand(bool throwsDuringAppend)
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            HandStartRequest first = First(); HandStartReceipt firstReceipt = host.Start(first);
            IPokerSeatPort oldPort = host.BindSeat(first.HandId, A);
            FinishByFolding(host); PokerPlayerView before = oldPort.Read();
            HandStartRequest next = Next(host);
            FieldInfo field = typeof(PokerHandHost).GetField("usedHandIds", BindingFlags.Instance | BindingFlags.NonPublic);
            var originalIds = (HashSet<Guid>)field.GetValue(host);
            var comparer = new OneShotIndexFault(next.HandId, throwsDuringAppend);
            var injectedIds = new HashSet<Guid>(originalIds, comparer);
            // Test-only replacement of an owned index. Production always constructs the built-in Guid comparer.
            field.SetValue(host, injectedIds);
            comparer.Armed = true;
            if (throwsDuringAppend) Assert.Throws<InjectedIndexFailure>(() => host.Start(next));
            else Assert.Throws<InvalidOperationException>(() => host.Start(next));
            Assert.That(random.Calls, Is.EqualTo(102), "The failed candidate consumed one shuffle; random rollback is not promised.");
            Assert.That(host.CurrentHandId, Is.EqualTo(before.HandId)); Assert.That(host.CurrentVersion, Is.EqualTo(before.Version));
            Assert.That(host.CurrentPhase, Is.EqualTo(HandPhase.Complete));
            Assert.That(injectedIds.SetEquals(originalIds), Is.True);
            var accepted = (System.Collections.IDictionary)typeof(PokerHandHost)
                .GetField("accepted", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            Assert.That(accepted.Count, Is.EqualTo(1)); Assert.That(accepted.Contains(next.CommandId), Is.False);
            Assert.That(host.Start(first), Is.SameAs(firstReceipt));
            Assert.That(oldPort.Read().OwnCards, Is.EqualTo(before.OwnCards));
            Assert.That(oldPort.Read().Result.Seats.Select(seat => seat.FinalStack), Is.EqualTo(before.Result.Seats.Select(seat => seat.FinalStack)));
            Assert.That(random.Calls, Is.EqualTo(102));
            HandStartReceipt retried = host.Start(next);
            Assert.That(retried.Accepted, Is.True, "The same command and hand IDs remain available after rollback.");
            Assert.That(host.CurrentHandId, Is.EqualTo(next.HandId)); Assert.That(host.CurrentVersion, Is.EqualTo(1));
            Assert.That(random.Calls, Is.EqualTo(153)); Assert.That(accepted.Count, Is.EqualTo(2));
            Assert.That(host.Start(next), Is.SameAs(retried)); Assert.That(random.Calls, Is.EqualTo(153));
            Assert.That(oldPort.Read().HandId, Is.EqualTo(first.HandId)); Assert.That(oldPort.Read().Phase, Is.EqualTo(HandPhase.Complete));
        }

        [Test]
        public void HistoryIndexesGrowInPlaceWithoutLosingOldStartReceipts()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            FieldInfo acceptedField = typeof(PokerHandHost).GetField("accepted", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo idsField = typeof(PokerHandHost).GetField("usedHandIds", BindingFlags.Instance | BindingFlags.NonPublic);
            object index = acceptedField.GetValue(host), ids = idsField.GetValue(host);
            var history = new List<(HandStartRequest request, HandStartReceipt receipt)>();
            for (int hand = 0; hand < 300; hand++)
            {
                HandStartRequest request = hand == 0 ? First() : Next(host);
                HandStartReceipt receipt = host.Start(request); Assert.That(receipt.Accepted, Is.True);
                history.Add((request, receipt)); FinishByFolding(host);
                Assert.That(acceptedField.GetValue(host), Is.SameAs(index));
                Assert.That(idsField.GetValue(host), Is.SameAs(ids));
            }
            Guid current = host.CurrentHandId.Value; long version = host.CurrentVersion;
            foreach (var entry in history) Assert.That(host.Start(entry.request), Is.SameAs(entry.receipt));
            Assert.That(((System.Collections.IDictionary)index).Count, Is.EqualTo(300));
            Assert.That(((HashSet<Guid>)ids).Count, Is.EqualTo(300));
            Assert.That(random.Calls, Is.EqualTo(300 * 51));
            Assert.That(host.CurrentHandId, Is.EqualTo(current)); Assert.That(host.CurrentVersion, Is.EqualTo(version));
        }

        private sealed class InjectedIndexFailure : Exception { }
        private sealed class OneShotIndexFault : IEqualityComparer<Guid>
        {
            private readonly Guid target;
            private readonly bool throwsDuringAppend;
            private int hits;
            public bool Armed;
            public OneShotIndexFault(Guid target, bool throwsDuringAppend) { this.target = target; this.throwsDuringAppend = throwsDuringAppend; }
            public bool Equals(Guid left, Guid right)
            {
                if (!throwsDuringAppend && Armed && (left == target || right == target) && ++hits == 2)
                { Armed = false; return true; } // Simulate Add(false) after a successful precheck.
                return left == right;
            }
            public int GetHashCode(Guid value)
            {
                if (throwsDuringAppend && Armed && value == target && ++hits == 2) { Armed = false; throw new InjectedIndexFailure(); }
                return throwsDuringAppend ? value.GetHashCode() : 0;
            }
        }

        [Test]
        public void RequestFactoriesRejectMissingIdentitySetupAndPredecessorVersion()
        {
            Guid hand = Guid.NewGuid(), command = Guid.NewGuid();
            Assert.Throws<ArgumentException>(() => HandStartRequest.First(Guid.Empty, command, Setup()));
            Assert.Throws<ArgumentException>(() => HandStartRequest.First(hand, Guid.Empty, Setup()));
            Assert.Throws<ArgumentNullException>(() => HandStartRequest.First(hand, command, null));
            Assert.Throws<ArgumentException>(() => HandStartRequest.Next(hand, command, Setup(), Guid.Empty, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => HandStartRequest.Next(hand, command, Setup(), Guid.NewGuid(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HandStartRequest.Next(hand, command, Setup(), Guid.NewGuid(), -1));
            Assert.Throws<ArgumentNullException>(() => new PokerHandHost(null));
        }

        [Test]
        public void BeforeStartHasNoPlayableHandAndDoesNotUseRandom()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            Assert.That(host.CurrentHandId, Is.Null); Assert.That(host.CurrentVersion, Is.Zero);
            Assert.That(host.CurrentPhase, Is.Null); Assert.That(random.Calls, Is.Zero);
            Assert.Throws<ArgumentNullException>(() => host.Start(null));
            Assert.Throws<InvalidOperationException>(() => host.BindSeat(Guid.NewGuid(), A));
            AssertReject(host, HandStartRequest.Next(Guid.NewGuid(), Guid.NewGuid(), Setup(), Guid.NewGuid(), 1), HandStartError.NoCurrent);
            Assert.That(random.Calls, Is.Zero);
        }

        [Test]
        public void FirstStartDealsExactlyOnceAndEquivalentReconstructedSetupReplays()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            var request = First(); HandStartReceipt receipt = host.Start(request);
            Assert.That(receipt.Accepted, Is.True); Assert.That(receipt.AppliedVersion, Is.EqualTo(1));
            Assert.That(receipt.HandId, Is.EqualTo(request.HandId)); Assert.That(receipt.CommandId, Is.EqualTo(request.CommandId));
            Assert.That(host.CurrentHandId, Is.EqualTo(request.HandId)); Assert.That(host.CurrentVersion, Is.EqualTo(1));
            Assert.That(host.CurrentPhase, Is.EqualTo(HandPhase.FirstBetting)); Assert.That(random.Calls, Is.EqualTo(51));
            PokerPlayerView view = View(host);
            Assert.That(view.PotAmount, Is.EqualTo(3)); Assert.That(view.OwnCards, Has.Count.EqualTo(5));
            var clone = HandStartRequest.First(request.HandId, request.CommandId, Setup());
            Assert.That(clone.Setup, Is.Not.SameAs(request.Setup));
            Assert.That(host.Start(clone), Is.SameAs(receipt)); Assert.That(random.Calls, Is.EqualTo(51));
            AssertReject(host, First(), HandStartError.AlreadyHasCurrent);
            Assert.That(View(host).OwnCards, Is.EqualTo(view.OwnCards));
        }

        [TestCase("hand")]
        [TestCase("kind")]
        [TestCase("ledger-order")]
        [TestCase("stack")]
        [TestCase("seat-count")]
        [TestCase("seat-identity")]
        [TestCase("deal-order")]
        [TestCase("opening-order")]
        [TestCase("exchange-order")]
        [TestCase("closing-order")]
        [TestCase("small-blind")]
        [TestCase("big-blind")]
        [TestCase("odd-priority")]
        public void AcceptedStartIdRejectsEveryChangedSemanticField(string field)
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            HandStartRequest first = First(); host.Start(first);
            HandStartRequest conflict = field == "kind"
                ? HandStartRequest.Next(first.HandId, first.CommandId, Setup(), first.HandId, 1)
                : HandStartRequest.First(field == "hand" ? Guid.NewGuid() : first.HandId,
                    first.CommandId, SetupVariant(field));
            AssertReject(host, conflict, HandStartError.CommandConflict);
            Assert.That(random.Calls, Is.EqualTo(51));
        }

        [Test]
        public void ExplicitOddPriorityOrderIsPartOfSemanticPayload()
        {
            var host = new PokerHandHost(new ControlledRandom());
            HandStartRequest first = First(Setup(odd: new[] { B, A, C })); var receipt = host.Start(first);
            Assert.That(host.Start(HandStartRequest.First(first.HandId, first.CommandId, Setup(odd: new[] { B, A, C }))), Is.SameAs(receipt));
            AssertReject(host, HandStartRequest.First(first.HandId, first.CommandId, Setup(odd: new[] { A, B, C })), HandStartError.CommandConflict);
            AssertReject(host, HandStartRequest.First(first.HandId, first.CommandId, Setup()), HandStartError.CommandConflict);
        }

        [Test]
        public void NextRequiresCurrentCompletedHandAndExactVersionWithoutConsumingRejectedIds()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random); host.Start(First());
            Guid newHand = Guid.NewGuid(), command = Guid.NewGuid();
            var early = HandStartRequest.Next(newHand, command, Setup(), host.CurrentHandId.Value, host.CurrentVersion);
            AssertReject(host, early, HandStartError.NotComplete);
            FinishByFolding(host);
            AssertReject(host, early, HandStartError.VersionMismatch);
            AssertReject(host, HandStartRequest.Next(newHand, command, Setup(), Guid.NewGuid(), host.CurrentVersion), HandStartError.WrongPredecessorHand);
            var correct = HandStartRequest.Next(newHand, command, Setup(), host.CurrentHandId.Value, host.CurrentVersion);
            Assert.That(host.Start(correct).Accepted, Is.True); Assert.That(host.CurrentHandId, Is.EqualTo(newHand));
            Assert.That(random.Calls, Is.EqualTo(102));
        }

        [Test]
        public void CallerSuppliedNextSeatsChipsOrdersAndBlindsAreUsedWithoutAutomaticTablePolicy()
        {
            var host = new PokerHandHost(new ControlledRandom()); host.Start(First());
            IPokerSeatPort old = host.BindSeat(host.CurrentHandId.Value, A); FinishByFolding(host);
            PokerPlayerView final = old.Read();
            var ledger = ChipLedger.Create(new[] { new SeatChips(D, 250), new SeatChips(B, 800) });
            var setup = new HandSetup(ledger, new[] { B, D }, new[] { D, B }, new[] { B, D }, new[] { B, D }, 3, 9);
            HandStartRequest next = Next(host, setup); Assert.That(host.Start(next).Accepted, Is.True);
            PokerPlayerView view = View(host, D);
            Assert.That(view.Seats.Select(s => s.Seat), Is.EqualTo(new[] { B, D }));
            Assert.That(view.Seats.Single(s => s.Seat == D).Stack, Is.EqualTo(247));
            Assert.That(view.Seats.Single(s => s.Seat == B).Stack, Is.EqualTo(791));
            Assert.That(view.PotAmount, Is.EqualTo(12)); Assert.That(view.CurrentSeat, Is.EqualTo(D));
            Assert.That(view.Betting.CallAmount, Is.EqualTo(6)); Assert.That(view.CurrentBet, Is.EqualTo(9));
            Act(host, BettingAction.Call(), D); Act(host, BettingAction.Check(), D);
            Assert.That(View(host, D).CurrentSeat, Is.EqualTo(B));
            Draw(host, 0, D); Draw(host, 0, D);
            Assert.That(View(host, D).CurrentSeat, Is.EqualTo(B));
            Assert.That(View(host, B).Betting.MinimumAggressiveTarget, Is.EqualTo(9));
            Assert.That(old.Read().Result.Seats.Select(s => s.FinalStack), Is.EqualTo(final.Result.Seats.Select(s => s.FinalStack)));
            Assert.Throws<KeyNotFoundException>(() => host.BindSeat(next.HandId, A));
            Assert.That(setup.OddChipPriority, Is.Null);
        }

        [Test]
        public void HistoricalFirstAndNextRetriesNeverRewindTheLatestHand()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            HandStartRequest first = First(); HandStartReceipt one = host.Start(first); FinishByFolding(host);
            HandStartRequest second = Next(host); HandStartReceipt two = host.Start(second); FinishByFolding(host);
            HandStartRequest third = Next(host); host.Start(third); Act(host, BettingAction.Call());
            var current = View(host);
            Assert.That(host.Start(HandStartRequest.First(first.HandId, first.CommandId, Setup())), Is.SameAs(one));
            Assert.That(host.Start(HandStartRequest.Next(second.HandId, second.CommandId, Setup(),
                second.PredecessorHandId.Value, second.PredecessorVersion.Value)), Is.SameAs(two));
            Assert.That(host.CurrentHandId, Is.EqualTo(third.HandId)); Assert.That(host.CurrentVersion, Is.EqualTo(current.Version));
            Assert.That(View(host).OwnCards, Is.EqualTo(current.OwnCards)); Assert.That(random.Calls, Is.EqualTo(153));
            Assert.That(one.AppliedVersion, Is.EqualTo(1)); Assert.That(two.AppliedVersion, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NextRetryRequiresTheOriginalPredecessorIdentityAndVersion(bool changeVersion)
        {
            var host = new PokerHandHost(new ControlledRandom()); host.Start(First()); FinishByFolding(host);
            HandStartRequest next = Next(host); host.Start(next);
            var changed = HandStartRequest.Next(next.HandId, next.CommandId, Setup(),
                changeVersion ? next.PredecessorHandId.Value : Guid.NewGuid(),
                next.PredecessorVersion.Value + (changeVersion ? 1 : 0));
            AssertReject(host, changed, HandStartError.CommandConflict);
        }

        [Test]
        public void UsedHandIdsAreRejectedEvenWithNewStartIdsAfterLaterHands()
        {
            var host = new PokerHandHost(new ControlledRandom()); HandStartRequest first = First(); host.Start(first);
            FinishByFolding(host); host.Start(Next(host)); FinishByFolding(host);
            AssertReject(host, HandStartRequest.Next(first.HandId, Guid.NewGuid(), Setup(), host.CurrentHandId.Value,
                host.CurrentVersion), HandStartError.HandIdAlreadyUsed);
            AssertReject(host, HandStartRequest.Next(host.CurrentHandId.Value, Guid.NewGuid(), Setup(), host.CurrentHandId.Value,
                host.CurrentVersion), HandStartError.HandIdAlreadyUsed);
        }

        [Test]
        public void BindRequiresExpectedCurrentHandAndValidMember()
        {
            var host = new PokerHandHost(new ControlledRandom()); host.Start(First()); Guid hand = host.CurrentHandId.Value;
            Assert.Throws<ArgumentException>(() => host.BindSeat(Guid.Empty, A));
            Assert.Throws<ArgumentException>(() => host.BindSeat(hand, default));
            Assert.Throws<InvalidOperationException>(() => host.BindSeat(Guid.NewGuid(), A));
            Assert.Throws<KeyNotFoundException>(() => host.BindSeat(hand, D));
            Assert.That(host.BindSeat(hand, A).Read().ViewerSeat, Is.EqualTo(A));
            FinishByFolding(host); host.Start(Next(host));
            Assert.Throws<InvalidOperationException>(() => host.BindSeat(hand, A));
        }

        [Test]
        public void OldPortAndApprovedCommandReplayStayBoundToTheOldCompletedHand()
        {
            var host = new PokerHandHost(new ControlledRandom()); host.Start(First());
            IPokerSeatPort old = host.BindSeat(host.CurrentHandId.Value, A); PokerPlayerView opening = old.Read();
            HandCommand fold = HandCommand.Bet(opening.HandId, Guid.NewGuid(), A, opening.Version, BettingAction.Fold());
            HandReceipt receipt = old.Submit(fold); FinishByFolding(host);
            PokerPlayerView complete = old.Read(); host.Start(Next(host)); PokerPlayerView current = View(host);
            Assert.That(old.Submit(fold), Is.SameAs(receipt));
            Assert.That(old.Read().HandId, Is.EqualTo(complete.HandId)); Assert.That(old.Read().Version, Is.EqualTo(complete.Version));
            Assert.That(old.Read().Result.TotalAwarded, Is.EqualTo(complete.Result.TotalAwarded));
            Assert.That(old.Submit(HandCommand.Bet(complete.HandId, Guid.NewGuid(), A, complete.Version, BettingAction.Fold())).Error,
                Is.EqualTo(HandError.Complete));
            Assert.That(old.Submit(HandCommand.Bet(current.HandId, Guid.NewGuid(), A, current.Version, BettingAction.Fold())).Error,
                Is.EqualTo(HandError.WrongHand));
            Assert.That(host.BindSeat(current.HandId, A).Submit(fold).Error, Is.EqualTo(HandError.WrongHand));
            Assert.That(View(host).Version, Is.EqualTo(current.Version)); Assert.That(View(host).PotAmount, Is.EqualTo(current.PotAmount));
            Assert.That(View(host).OwnCards, Is.EqualTo(current.OwnCards));
        }

        [TestCase(false, 1)]
        [TestCase(false, 17)]
        [TestCase(false, 51)]
        [TestCase(true, 1)]
        [TestCase(true, 17)]
        [TestCase(true, 51)]
        public void RandomFailureDoesNotPublishReserveIdsOrPreventTheSameRequestFromRetrying(bool next, int failAt)
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random); IPokerSeatPort old = null;
            if (next) { host.Start(First()); FinishByFolding(host); old = host.BindSeat(host.CurrentHandId.Value, A); }
            Guid? previousHand = host.CurrentHandId; long previousVersion = host.CurrentVersion;
            var request = next ? Next(host) : First(); int beforeCalls = random.Calls;
            random.FailOnCall = beforeCalls + failAt;
            Assert.Throws<InvalidOperationException>(() => host.Start(request));
            Assert.That(host.CurrentHandId, Is.EqualTo(previousHand)); Assert.That(host.CurrentVersion, Is.EqualTo(previousVersion));
            if (old != null) Assert.That(old.Read().Phase, Is.EqualTo(HandPhase.Complete));
            random.FailOnCall = 0;
            HandStartReceipt success = host.Start(request); Assert.That(success.Accepted, Is.True);
            Assert.That(host.CurrentHandId, Is.EqualTo(request.HandId)); Assert.That(host.CurrentVersion, Is.EqualTo(1));
            Assert.That(random.Calls, Is.EqualTo(beforeCalls + failAt + 51));
            Assert.That(host.Start(request), Is.SameAs(success)); Assert.That(random.Calls, Is.EqualTo(beforeCalls + failAt + 51));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RandomCallbackCannotReenterStartOrSeeAnUncommittedCandidate(bool next)
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            if (next) { host.Start(First()); FinishByFolding(host); }
            Guid? previousHand = host.CurrentHandId; long previousVersion = host.CurrentVersion;
            HandStartRequest request = next ? Next(host) : First(); int callbacks = 0;
            random.Callback = () =>
            {
                callbacks++;
                Assert.That(host.CurrentHandId, Is.EqualTo(previousHand)); Assert.That(host.CurrentVersion, Is.EqualTo(previousVersion));
                Assert.That(host.Start(request).Error, Is.EqualTo(HandStartError.Busy));
                Assert.That(host.Start(First()).Error, Is.EqualTo(HandStartError.Busy));
                Assert.Throws<InvalidOperationException>(() => host.BindSeat(request.HandId, A));
                if (previousHand.HasValue) Assert.That(host.BindSeat(previousHand.Value, A).Read().Phase, Is.EqualTo(HandPhase.Complete));
            };
            Assert.That(host.Start(request).Accepted, Is.True); Assert.That(callbacks, Is.EqualTo(51));
            random.Callback = null;
            Assert.That(host.Start(request).Accepted, Is.True); Assert.That(callbacks, Is.EqualTo(51));
        }

        [Test]
        public void ACallbackExceptionReleasesTheStartGuardAndPreservesThePreviousStartCache()
        {
            var random = new ControlledRandom(); var host = new PokerHandHost(random);
            var first = First(); var original = host.Start(first); FinishByFolding(host); var next = Next(host);
            random.Callback = () => throw new ApplicationException("test callback failure");
            Assert.Throws<ApplicationException>(() => host.Start(next));
            Assert.That(host.Start(first), Is.SameAs(original)); Assert.That(host.CurrentHandId, Is.EqualTo(first.HandId));
            random.Callback = null; Assert.That(host.Start(next).Accepted, Is.True);
        }

        [Test]
        public void UnresolvedOddChipSettlementDoesNotBecomePermissionToStartAnotherHand()
        {
            Card[][] hands = {
                new[] { new Card(Rank.Ace, Suit.Clubs), new Card(Rank.King, Suit.Diamonds), new Card(Rank.Queen, Suit.Hearts), new Card(Rank.Jack, Suit.Spades), new Card(Rank.Nine, Suit.Clubs) },
                new[] { new Card(Rank.Ace, Suit.Diamonds), new Card(Rank.King, Suit.Clubs), new Card(Rank.Queen, Suit.Spades), new Card(Rank.Jack, Suit.Hearts), new Card(Rank.Nine, Suit.Diamonds) },
                new[] { new Card(Rank.Two, Suit.Clubs), new Card(Rank.Three, Suit.Diamonds), new Card(Rank.Four, Suit.Hearts), new Card(Rank.Five, Suit.Spades), new Card(Rank.Seven, Suit.Clubs) }
            };
            var random = new RiggedRandom(hands); var host = new PokerHandHost(random);
            var first = First(Setup(opening: new[] { A, C, B })); var receipt = host.Start(first);
            Act(host, BettingAction.Call()); Act(host, BettingAction.Fold()); Act(host, BettingAction.Check());
            Draw(host, 0); Draw(host, 0); Act(host, BettingAction.Check()); Act(host, BettingAction.Check());
            PokerPlayerView waiting = View(host);
            Assert.That(waiting.Phase, Is.EqualTo(HandPhase.AwaitingSettlementRule)); Assert.That(waiting.Result, Is.Null);
            Assert.That(waiting.PotAmount, Is.EqualTo(5));
            AssertReject(host, Next(host), HandStartError.SettlementRuleRequired);
            AssertReject(host, Next(host, Setup(odd: new[] { A, B, C })), HandStartError.SettlementRuleRequired);
            Assert.That(View(host).PotAmount, Is.EqualTo(waiting.PotAmount));
            Assert.That(View(host).Seats.Select(s => s.Stack), Is.EqualTo(waiting.Seats.Select(s => s.Stack)));
            Assert.That(host.Start(first), Is.SameAs(receipt)); Assert.That(random.Calls, Is.EqualTo(51));
        }

        [Test]
        public void SetupComparisonSurfaceMustBeRevisitedWhenTheConfigurationGrows()
        {
            Assert.That(typeof(HandSetup).GetProperties().Select(p => p.Name), Is.EquivalentTo(new[] {
                "StartingLedger", "DealOrder", "OpeningOrder", "ExchangeOrder", "ClosingOrder", "OddChipPriority",
                "SmallBlind", "BigBlind", "SmallBlindSeat", "BigBlindSeat"
            }));
            Assert.That(typeof(HandStartRequest).GetProperties().Select(p => p.Name), Is.EquivalentTo(new[] {
                "Kind", "HandId", "CommandId", "Setup", "PredecessorHandId", "PredecessorVersion"
            }));
            foreach (var property in typeof(HandStartRequest).GetProperties()) Assert.That(property.SetMethod, Is.Null);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void FortyExplicitHandsPreserveChipsCardsRetryIsolationAndPriorResults(int seatCount)
        {
            // Test capacities, not a team player-count decision. Each fixture explicitly resets starting chips.
            SeatId[] seats = new[] { A, B, C, D, new SeatId(16) }.Take(seatCount).ToArray();
            var random = new SeededRandom(400 + seatCount); var host = new PokerHandHost(random);
            var history = new List<(HandStartRequest request, HandStartReceipt receipt, IPokerSeatPort port, PokerPlayerView result)>();
            for (int hand = 0; hand < 40; hand++)
            {
                long[] chips = Enumerable.Range(0, seatCount).Select(i => 20L + i * 30 + hand % 7).ToArray();
                // Explicit test-only odd-chip order allows the fixture to finish; production has no default.
                HandSetup setup = Setup(ledgerOrder: seats, stacks: chips, odd: seats);
                HandStartRequest request = hand == 0 ? First(setup) : Next(host, setup);
                var receipt = host.Start(request); Assert.That(receipt.Accepted, Is.True);
                var anchor = host.BindSeat(request.HandId, A); int steps = 0;
                while (host.CurrentPhase != HandPhase.Complete)
                {
                    var view = anchor.Read();
                    Assert.That(view.Seats.Sum(s => (decimal)s.Stack) + view.PotAmount, Is.EqualTo(chips.Sum(s => (decimal)s)));
                    Assert.That(view.CurrentSeat.HasValue, Is.True); Assert.That(++steps, Is.LessThan(100));
                    Card[] visibleInTest = seats.SelectMany(s => host.BindSeat(request.HandId, s).Read().OwnCards).ToArray();
                    Assert.That(visibleInTest.Distinct().Count(), Is.EqualTo(seatCount * 5));
                    var port = host.BindSeat(request.HandId, view.CurrentSeat.Value); view = port.Read();
                    HandCommand action;
                    if (view.CanExchange)
                        action = HandCommand.Exchange(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version,
                            view.OwnCards.Take((hand + steps) % 6).ToArray());
                    else
                    {
                        PlayerBettingOptions legal = view.Betting; BettingAction bet;
                        if (hand % 4 == 0 && legal.CanFold) bet = BettingAction.Fold();
                        else if (hand % 4 == 2 && steps <= 2 && (legal.CanBet || legal.CanRaise))
                            bet = legal.CanBet ? BettingAction.BetTo(legal.MaximumAggressiveTarget.Value) : BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value);
                        else if (hand % 4 == 3 && steps <= 3 && (legal.CanBet || legal.CanRaise))
                            bet = legal.CanBet ? BettingAction.BetTo(legal.MinimumAggressiveTarget.Value) : BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value);
                        else bet = legal.CanCheck ? BettingAction.Check() : BettingAction.Call();
                        action = HandCommand.Bet(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version, bet);
                    }
                    HandReceipt applied = port.Submit(action); Assert.That(applied.Accepted, Is.True);
                    long version = host.CurrentVersion;
                    Assert.That(port.Submit(action), Is.SameAs(applied)); Assert.That(host.CurrentVersion, Is.EqualTo(version));
                }
                var final = anchor.Read();
                Assert.That(final.Result, Is.Not.Null); Assert.That(final.PotAmount, Is.Zero);
                Assert.That(final.Result.Seats.Sum(s => (decimal)s.FinalStack), Is.EqualTo(chips.Sum(s => (decimal)s)));
                Assert.That(final.Result.Seats.Sum(s => s.GrossAward), Is.EqualTo(final.Result.TotalAwarded));
                Assert.That(final.Result.Pots.Sum(p => p.Amount), Is.EqualTo(final.Result.TotalAwarded));
                foreach (var old in history)
                {
                    Assert.That(host.Start(old.request), Is.SameAs(old.receipt)); Assert.That(host.CurrentHandId, Is.EqualTo(request.HandId));
                    Assert.That(old.port.Read().HandId, Is.EqualTo(old.result.HandId));
                    Assert.That(old.port.Read().Result.Seats.Select(s => s.FinalStack), Is.EqualTo(old.result.Result.Seats.Select(s => s.FinalStack)));
                }
                history.Add((request, receipt, anchor, final));
            }
            Assert.That(random.Calls, Is.EqualTo(40 * 51));
        }

        [Test]
        public void PublicHostAndReceiptSurfaceExposeNoWholeHandOrSetup()
        {
            Assert.That(typeof(PokerHandHost).GetProperties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "CurrentHandId", "CurrentVersion", "CurrentPhase" }));
            Assert.That(typeof(HandStartReceipt).GetProperties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "HandId", "CommandId", "AppliedVersion", "Error", "Accepted" }));
            Assert.That(typeof(PokerHandHost).GetFields(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            var methods = typeof(PokerHandHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).ToArray();
            Assert.That(methods.Select(m => m.Name + ":" + m.ReturnType.Name),
                Is.EquivalentTo(new[] { "Start:HandStartReceipt", "BindSeat:IPokerSeatPort" }));
            foreach (var property in typeof(HandStartReceipt).GetProperties()) Assert.That(property.SetMethod, Is.Null);
            foreach (var field in typeof(HandStartReceipt).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True);
                Assert.That(new[] { typeof(Guid), typeof(long?), typeof(HandStartError) }, Does.Contain(field.FieldType));
            }
        }

        private static HandStartRequest First(HandSetup setup = null) => HandStartRequest.First(Guid.NewGuid(), Guid.NewGuid(), setup ?? Setup());
        private static HandStartRequest Next(PokerHandHost host, HandSetup setup = null) => HandStartRequest.Next(Guid.NewGuid(),
            Guid.NewGuid(), setup ?? Setup(), host.CurrentHandId.Value, host.CurrentVersion);
        private static PokerPlayerView View(PokerHandHost host, SeatId? anchor = null) => host.BindSeat(host.CurrentHandId.Value, anchor ?? A).Read();
        private static void Act(PokerHandHost host, BettingAction action, SeatId? anchor = null)
        {
            var view = View(host, anchor); SeatId actor = view.CurrentSeat.Value;
            Assert.That(host.BindSeat(view.HandId, actor).Submit(HandCommand.Bet(view.HandId, Guid.NewGuid(), actor,
                view.Version, action)).Accepted, Is.True);
        }
        private static void Draw(PokerHandHost host, int count, SeatId? anchor = null)
        {
            var view = View(host, anchor); var port = host.BindSeat(view.HandId, view.CurrentSeat.Value); view = port.Read();
            Assert.That(port.Submit(HandCommand.Exchange(view.HandId, Guid.NewGuid(), view.ViewerSeat, view.Version,
                view.OwnCards.Take(count).ToArray())).Accepted, Is.True);
        }
        private static void FinishByFolding(PokerHandHost host)
        {
            for (int i = 0; i < 5 && host.CurrentPhase != HandPhase.Complete; i++) Act(host, BettingAction.Fold());
            Assert.That(host.CurrentPhase, Is.EqualTo(HandPhase.Complete));
        }
        private static void AssertReject(PokerHandHost host, HandStartRequest request, HandStartError error)
        {
            Guid? hand = host.CurrentHandId; long version = host.CurrentVersion; HandPhase? phase = host.CurrentPhase;
            HandStartReceipt receipt = host.Start(request);
            Assert.That(receipt.Accepted, Is.False); Assert.That(receipt.Error, Is.EqualTo(error)); Assert.That(receipt.AppliedVersion, Is.Null);
            Assert.That(receipt.HandId, Is.EqualTo(request.HandId)); Assert.That(receipt.CommandId, Is.EqualTo(request.CommandId));
            Assert.That(host.CurrentHandId, Is.EqualTo(hand)); Assert.That(host.CurrentVersion, Is.EqualTo(version));
            Assert.That(host.CurrentPhase, Is.EqualTo(phase));
        }
        private static HandSetup Setup(SeatId[] ledgerOrder = null, long[] stacks = null, SeatId[] deal = null,
            SeatId[] opening = null, SeatId[] exchange = null, SeatId[] closing = null, long sb = 1, long bb = 2, SeatId[] odd = null)
        {
            var order = ledgerOrder ?? new[] { A, B, C };
            var ledger = ChipLedger.Create(order.Select((seat, i) => new SeatChips(seat, stacks == null ? 100 : stacks[i])).ToArray());
            return new HandSetup(ledger, deal ?? order, opening ?? order, exchange ?? order, closing ?? order, sb, bb, odd);
        }
        private static HandSetup SetupVariant(string field)
        {
            var normal = new[] { A, B, C }; var reversed = new[] { C, B, A };
            switch (field)
            {
                case "ledger-order": return Setup(ledgerOrder: reversed, deal: normal, opening: normal, exchange: normal, closing: normal);
                case "stack": return Setup(stacks: new long[] { 100, 101, 100 });
                case "seat-count": return Setup(ledgerOrder: new[] { A, B });
                case "seat-identity": return Setup(ledgerOrder: new[] { A, B, D });
                case "deal-order": return Setup(deal: reversed);
                case "opening-order": return Setup(opening: reversed);
                case "exchange-order": return Setup(exchange: reversed);
                case "closing-order": return Setup(closing: reversed);
                case "small-blind": return Setup(sb: 2);
                case "big-blind": return Setup(bb: 3);
                case "odd-priority": return Setup(odd: normal);
                default: return Setup();
            }
        }
        private sealed class ControlledRandom : IRandomSource
        {
            public int Calls; public int FailOnCall; public Action Callback;
            public int NextInt(int upper)
            {
                Calls++;
                if (Calls == FailOnCall) throw new InvalidOperationException("test random failure");
                Callback?.Invoke(); return upper - 1;
            }
        }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly Random random;
            public int Calls;
            public SeededRandom(int seed) { random = new Random(seed); }
            public int NextInt(int upper) { Calls++; return random.Next(upper); }
        }
        // Test-only Fisher-Yates choices; no production deck injection or fairness claim.
        private sealed class RiggedRandom : IRandomSource
        {
            private readonly Queue<int> choices = new Queue<int>();
            public int Calls;
            public RiggedRandom(Card[][] hands)
            {
                var target = new List<Card>();
                for (int i = 0; i < 5; i++) foreach (Card[] hand in hands) target.Add(hand[i]);
                Assert.That(target.Distinct().Count(), Is.EqualTo(target.Count));
                target.AddRange(Enumerable.Range(0, 52).Select(Card.FromId).Where(c => !target.Contains(c)).ToArray());
                Card[] working = Enumerable.Range(0, 52).Select(Card.FromId).ToArray();
                for (int i = 51; i > 0; i--)
                {
                    int selected = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(selected);
                    Card swap = working[i]; working[i] = working[selected]; working[selected] = swap;
                }
            }
            public int NextInt(int upper) { Calls++; return choices.Dequeue(); }
        }
    }
}
