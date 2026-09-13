using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Application;
using Poker.Presentation;
using Poker.Runtime;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed class PokerWireTests
    {
        private static readonly SeatId A = new SeatId(40), B = new SeatId(7);

        [TestCase("fold")][TestCase("check")][TestCase("call")][TestCase("bet_to")][TestCase("raise_to")]
        public void EveryBetActionRoundTripsWithoutChangingIdsVersionOrTarget(string action)
        {
            var dto = Command(); dto.action = action;
            dto.targetTotal = action.EndsWith("_to", StringComparison.Ordinal) ? long.MaxValue.ToString(CultureInfo.InvariantCulture) : "";
            var command = PokerWireMapper.ToCommand(dto);
            var decoded = PokerWireJson.ReadCommand(PokerWireJson.Serialize(command));
            Assert.That(PokerWireJson.Serialize(decoded), Is.EqualTo(PokerWireJson.Serialize(command)));
            Assert.That(decoded.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(decoded.ExpectedVersion, Is.EqualTo(long.MaxValue));
            Assert.That(decoded.Action.Target, Is.EqualTo(dto.targetTotal == "" ? 0 : long.MaxValue));
        }

        [TestCase(0)][TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void ExchangeRoundTripCanonicalizesOrderAndCopiesSelectedCards(int count)
        {
            var dto = Command(); dto.kind = "exchange"; dto.action = "";
            dto.cards = Enumerable.Range(0, count).Select(n => 51 - n).ToArray();
            var command = PokerWireMapper.ToCommand(dto);
            var decoded = PokerWireJson.ReadCommand(PokerWireJson.Serialize(command));
            Assert.That(decoded.SelectedCards.Select(card => card.Id), Is.EqualTo(dto.cards.OrderBy(n => n)));
            Assert.That(decoded.SelectedCards.Count, Is.EqualTo(count));
            if (count > 0) dto.cards[0] = 0;
            Assert.That(decoded.SelectedCards, Is.EqualTo(command.SelectedCards));
        }

        [TestCase(null)][TestCase("")][TestCase("-1")][TestCase("+1")][TestCase("01")]
        [TestCase(" 1")][TestCase("1 ")][TestCase("1.0")][TestCase("1e2")]
        [TestCase("9223372036854775808")][TestCase("١")][TestCase("１")]
        public void NoncanonicalOrOverflowingVersionsAreRejected(string version)
        { var dto = Command(); dto.expectedVersion = version; Reject(dto); }

        [TestCase("version")][TestCase("message")][TestCase("hand-empty")][TestCase("command-empty")]
        [TestCase("upper-guid")][TestCase("compact-guid")][TestCase("seat")][TestCase("kind")]
        [TestCase("action")][TestCase("target-passive")][TestCase("cards-bet")][TestCase("cards-null")]
        [TestCase("draw-action")][TestCase("draw-target")][TestCase("draw-duplicate")]
        [TestCase("draw-negative")][TestCase("draw-52")][TestCase("draw-six")][TestCase("zero-bet")]
        public void MalformedCommandsNeverReachDomainSubmission(string field)
        {
            var dto = Command();
            switch (field)
            {
                case "version": dto.protocolVersion = 2; break;
                case "message": dto.message = "snapshot"; break;
                case "hand-empty": dto.handId = Guid.Empty.ToString("D"); break;
                case "command-empty": dto.commandId = ""; break;
                case "upper-guid": dto.handId = "ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB"; break;
                case "compact-guid": dto.handId = Guid.NewGuid().ToString("N"); break;
                case "seat": dto.seat = 0; break;
                case "kind": dto.kind = "start"; break;
                case "action": dto.action = "all_in"; break;
                case "target-passive": dto.targetTotal = "0"; break;
                case "cards-bet": dto.cards = new[] { 1 }; break;
                case "cards-null": dto.cards = null; break;
                case "zero-bet": dto.action = "bet_to"; dto.targetTotal = "0"; break;
                default:
                    dto.kind = "exchange"; dto.action = "";
                    if (field == "draw-action") dto.action = "fold";
                    if (field == "draw-target") dto.targetTotal = "2";
                    if (field == "draw-duplicate") dto.cards = new[] { 1, 1 };
                    if (field == "draw-negative") dto.cards = new[] { -1 };
                    if (field == "draw-52") dto.cards = new[] { 52 };
                    if (field == "draw-six") dto.cards = Enumerable.Range(0, 6).ToArray();
                    break;
            }
            Reject(dto);
        }

        [Test]
        public void DecodeDoesNotAuthenticateSeatOrApproveVersionAndRejectedCommandsDoNotAdvance()
        {
            var host = new PokerHandHost(new IdentityRandom()); var id = Guid.NewGuid();
            host.Start(HandStartRequest.First(id, Guid.NewGuid(), Setup(100, 100)));
            var port = host.BindSeat(id, A); var before = port.Read();
            var forged = HandCommand.Bet(id, Guid.NewGuid(), B, before.Version, BettingAction.Fold());
            var forgedReceipt = port.Submit(PokerWireJson.ReadCommand(PokerWireJson.Serialize(forged)));
            Assert.That(forgedReceipt.Error, Is.EqualTo(HandError.UnauthorizedSeat));
            var stale = HandCommand.Bet(id, Guid.NewGuid(), A, 0, BettingAction.Fold());
            Assert.That(PokerWireJson.ReadCommand(PokerWireJson.Serialize(stale)).ExpectedVersion, Is.Zero);
            Assert.That(port.Submit(PokerWireJson.ReadCommand(PokerWireJson.Serialize(stale))).Error, Is.EqualTo(HandError.VersionMismatch));
            Assert.That(port.Read().Version, Is.EqualTo(before.Version)); Assert.That(port.Read().PotAmount, Is.EqualTo(before.PotAmount));
            var rejectedDto = PokerWireJson.ReadReceipt(PokerWireJson.Serialize(forgedReceipt));
            Assert.That(rejectedDto.error, Is.EqualTo("unauthorized_seat"));
            Assert.That(rejectedDto.appliedVersion, Is.Empty); Assert.That(rejectedDto.transition, Is.Empty);
        }

        [Test]
        public void JsonReconstructedAcceptedCommandReplaysOriginalReceiptNotLatestSnapshot()
        {
            var s = Start(100, 100); var command = Bet(s, BettingAction.Call());
            var receipt = s.Submit(A, command); Act(s, BettingAction.Check());
            var replay = s.Submit(A, PokerWireJson.ReadCommand(PokerWireJson.Serialize(command)));
            Assert.That(replay, Is.SameAs(receipt));
            var dto = PokerWireJson.ReadReceipt(PokerWireJson.Serialize(replay));
            Assert.That(long.Parse(dto.appliedVersion), Is.LessThan(s.Version));
            Assert.That(dto.transition[0].action, Is.EqualTo("call"));
            Assert.That(dto.transition[0].chipsPaid, Is.EqualTo("1"));
        }

        [Test]
        public void ReconstructedIdConflictAndReorderedExchangeUseCoreSemanticIdentity()
        {
            var s = Start(100, 100); var call = Bet(s, BettingAction.Call()); var first = s.Submit(A, call);
            var changed = PokerWireMapper.ToWire(call); changed.action = "fold";
            Assert.That(s.Submit(A, PokerWireJson.ReadCommand(JsonUtility.ToJson(changed))).Error, Is.EqualTo(HandError.CommandConflict));
            Assert.That(s.Version, Is.EqualTo(first.AppliedVersion)); Act(s, BettingAction.Check());
            var originalCards = s.State.GetHand(A).ToArray();
            var draw = HandCommand.Exchange(s.HandId, Guid.NewGuid(), A, s.Version, originalCards.Take(2).ToArray());
            var receipt = s.Submit(A, draw); var reordered = PokerWireMapper.ToWire(draw); Array.Reverse(reordered.cards);
            Assert.That(s.Submit(A, PokerWireJson.ReadCommand(JsonUtility.ToJson(reordered))), Is.SameAs(receipt));
            reordered.cards[0] = originalCards[2].Id;
            Assert.That(s.Submit(A, PokerWireJson.ReadCommand(JsonUtility.ToJson(reordered))).Error, Is.EqualTo(HandError.CommandConflict));
            Assert.That(s.Version, Is.EqualTo(receipt.AppliedVersion));
        }

        [TestCase(false)][TestCase(true)]
        public void AllInLastExchangeCanSkipSecondBettingToCompleteOrPendingRule(bool pendingRule)
        {
            var s = pendingRule ? TiedSession(false, 1) : Start(1, 1);
            if (s.State.Phase == HandPhase.FirstBetting) Act(s, BettingAction.Call());
            Assert.That(s.State.Phase, Is.EqualTo(HandPhase.Exchange));
            while (s.State.CurrentSeat.HasValue) { Assert.That(s.State.Phase, Is.EqualTo(HandPhase.Exchange)); Passive(s); }
            Assert.That(s.State.Phase, Is.EqualTo(pendingRule ? HandPhase.AwaitingSettlementRule : HandPhase.Complete));
            for (int i = 0; i < s.State.SeatCount; i++) AssertSnapshot(s, s.State.GetSeatAt(i));
        }

        [Test]
        public void AuthorityStartReceiptHasNoPlayerActionAndIsNotAnAcceptedPlayerCommand()
        {
            var s = new PokerHandSession(Guid.NewGuid(), Setup(100, 100), new IdentityRandom());
            var receipt = s.Start(new StartHandCommand(s.HandId, Guid.NewGuid()));
            var dto = PokerWireJson.ReadReceipt(PokerWireJson.Serialize(receipt));
            Assert.That(dto.seat, Is.Zero); Assert.That(dto.appliedVersion, Is.EqualTo("1")); Assert.That(dto.transition, Is.Empty);
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(JsonUtility.ToJson(dto)));
        }

        [TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void EveryStageAndEveryViewerRoundTripsOnlyItsOwnCards(int count)
        {
            var s = Start(Enumerable.Repeat(100L, count).ToArray()); int steps = 0; var phases = new HashSet<HandPhase>();
            while (true)
            {
                phases.Add(s.State.Phase);
                for (int i = 0; i < count; i++) AssertSnapshot(s, s.State.GetSeatAt(i));
                if (!s.State.CurrentSeat.HasValue) break;
                Assert.That(++steps, Is.LessThan(60)); Passive(s);
            }
            Assert.That(phases, Does.Contain(HandPhase.FirstBetting)); Assert.That(phases, Does.Contain(HandPhase.Exchange));
            Assert.That(phases, Does.Contain(HandPhase.SecondBetting)); Assert.That(phases, Does.Contain(HandPhase.Complete));
        }

        [TestCase("fold")][TestCase("side-pots")][TestCase("refund")][TestCase("large")][TestCase("second-fold")]
        public void CompletedResultsPreserveLayersRefundsAndLargeChipValues(string scenario)
        {
            var s = scenario == "side-pots" ? Start(20, 50, 100) : scenario == "refund" ? Start(100, 40)
                : scenario == "large" ? Start(long.MaxValue - 2, 2) : Start(100, 100);
            if (scenario == "fold") Act(s, BettingAction.Fold());
            if (scenario == "side-pots") { Act(s, BettingAction.RaiseTo(20)); Act(s, BettingAction.RaiseTo(50)); Act(s, BettingAction.Call()); }
            if (scenario == "refund") { Act(s, BettingAction.RaiseTo(100)); Act(s, BettingAction.Call()); }
            if (scenario == "second-fold")
            { Act(s, BettingAction.Call()); Act(s, BettingAction.Check()); Passive(s); Passive(s); Act(s, BettingAction.BetTo(10)); Act(s, BettingAction.Fold()); }
            int steps = 0; while (s.State.CurrentSeat.HasValue) { Assert.That(++steps, Is.LessThan(40)); Passive(s); }
            for (int i = 0; i < s.State.SeatCount; i++) AssertSnapshot(s, s.State.GetSeatAt(i));
            var result = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(s, A)).result[0];
            Assert.That(result.totalAwarded, Is.EqualTo(s.State.Settlement.TotalAwarded.ToString(CultureInfo.InvariantCulture)));
            if (scenario == "side-pots") Assert.That(result.pots.Length, Is.EqualTo(2));
            if (scenario == "refund") Assert.That(result.refunds[0].amount, Is.EqualTo("60"));
        }

        [Test]
        public void DtoMutationCannotChangeDomainCommandViewSessionOrAnotherCopy()
        {
            var s = Start(100, 100); var view = PokerPlayerViewProjector.Create(s, A); string original = PokerWireJson.Serialize(view);
            var dto = PokerWireMapper.ToWire(view); dto.ownCards[0] = 51; dto.seats[0].stack = "1";
            dto.betting[0].callAmount = "77"; dto.handId = Guid.NewGuid().ToString("D");
            Assert.That(PokerWireJson.Serialize(view), Is.EqualTo(original));
            Assert.That(PokerWireJson.Serialize(PokerPlayerViewProjector.Create(s, A)), Is.EqualTo(original));
            Act(s, BettingAction.Fold()); view = PokerPlayerViewProjector.Create(s, A); original = PokerWireJson.Serialize(view);
            dto = PokerWireMapper.ToWire(view); dto.result[0].pots[0].payouts[0].amount = "99";
            dto.result[0].refunds[0].seat = 99; dto.lastTransition[0].chipsPaid = "99";
            Assert.That(PokerWireJson.Serialize(view), Is.EqualTo(original));
        }

        [TestCase("seat-duplicate")][TestCase("seat-null")][TestCase("viewer")][TestCase("own-cards")]
        [TestCase("pot")][TestCase("number-overflow")][TestCase("phase")][TestCase("betting-null")]
        [TestCase("transition-null")][TestCase("result-null")][TestCase("exchange-flag")]
        public void InconsistentSnapshotStructureIsRejected(string field)
        {
            var dto = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(Start(100, 100), A));
            switch (field)
            {
                case "seat-duplicate": dto.seats[1].seat = dto.seats[0].seat; break;
                case "seat-null": dto.seats[0] = null; break;
                case "viewer": dto.viewerSeat = 99; break;
                case "own-cards": dto.ownCards[0] = dto.ownCards[1]; break;
                case "pot": dto.potAmount = "4"; break;
                case "number-overflow": dto.seats[0].stack = "9223372036854775808"; break;
                case "phase": dto.phase = "Invalid"; break;
                case "betting-null": dto.betting = null; break;
                case "transition-null": dto.lastTransition = null; break;
                case "result-null": dto.result = null; break;
                case "exchange-flag": dto.canExchange = true; break;
            }
            Assert.Throws<PokerWireException>(() => PokerWireMapper.Validate(dto));
        }

        [TestCase("identity")][TestCase("seat-stack")][TestCase("award")][TestCase("pot-sum")]
        [TestCase("payout")][TestCase("eligible")][TestCase("refund")][TestCase("phase")]
        [TestCase("odd-flag")][TestCase("zero-payout")][TestCase("empty-pots")][TestCase("reason")]
        public void CompletedSnapshotCannotContradictItsOwnResult(string field)
        {
            var s = Start(100, 100); Act(s, BettingAction.Fold()); var dto = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(s, A));
            var result = dto.result[0];
            switch (field)
            {
                case "identity": result.handId = Guid.NewGuid().ToString("D"); break;
                case "seat-stack": result.seats[0].finalStack = "999"; break;
                case "award": result.totalAwarded = "99"; break;
                case "pot-sum": result.pots[0].amount = "99"; break;
                case "payout": result.pots[0].payouts[0].amount = "99"; break;
                case "eligible": result.pots[0].eligibleSeats[0] = 999; break;
                case "refund": result.refunds[0].seat = 999; break;
                case "phase": dto.phase = "awaiting_settlement_rule"; break;
                case "odd-flag": result.pots[0].payouts[0].hasOddChip = true; break;
                case "zero-payout": result.pots[0].payouts[0].amount = "0"; break;
                case "empty-pots": result.pots = Array.Empty<PokerWirePot>(); break;
                case "reason": result.reason = "showdown"; break;
            }
            Assert.Throws<PokerWireException>(() => PokerWireMapper.Validate(dto));
        }

        [TestCase(null)][TestCase("")][TestCase(" ")][TestCase("[]")][TestCase("null")][TestCase("{broken}")]
        public void BadJsonRootsOrMissingEnvelopeAreRejected(string json)
        { Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(json)); }

        [Test]
        public void ByteLimitCountsUtf8AndMalformedSurrogatesAreRejectedWithoutPayloadInError()
        {
            string json = JsonUtility.ToJson(Command());
            string oversized = json.Substring(0, json.Length - 1) + ",\"extra\":\"" + new string('가', 12000) + "\"}";
            Assert.That(oversized.Length, Is.LessThan(PokerWireJson.MaximumUtf8Bytes));
            var error = Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(oversized));
            Assert.That(error.Message, Does.Not.Contain("가"));
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand("{\"extra\":\"\ud800\"}"));
        }

        [Test]
        public void JsonUtilityUnknownFieldsAreIgnoredAndRequiredStringsRemainMissing()
        {
            var dto = Command(); string json = JsonUtility.ToJson(dto);
            var original = PokerWireJson.ReadCommand(json);
            string unknown = json.Substring(0, json.Length - 1) + ",\"admin\":true,\"deck\":[51,50]}";
            Assert.That(PokerWireJson.Serialize(PokerWireJson.ReadCommand(unknown)), Is.EqualTo(PokerWireJson.Serialize(original)));
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(json.Replace("\"targetTotal\":\"\",", "")));
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(json.Replace("\"cards\":[]", "\"unused\":[]")));
            var empty = JsonUtility.FromJson<PokerWireCommand>("{}");
            Assert.That(empty.targetTotal, Is.Null); Assert.That(empty.cards, Is.Null);
        }

        [Test]
        public void JsonUtilityOmittedFalseFlagIsAllowedButDoesNotOverrideValueConsistency()
        {
            var s = Start(100, 100); string json = PokerWireJson.Serialize(PokerPlayerViewProjector.Create(s, A));
            string omitted = json.Replace("\"canExchange\":false,", "");
            Assert.That(omitted, Is.Not.EqualTo(json));
            Assert.That(PokerWireJson.ReadSnapshot(omitted).canExchange, Is.False);
            Act(s, BettingAction.Call()); Act(s, BettingAction.Check());
            json = PokerWireJson.Serialize(PokerPlayerViewProjector.Create(s, A));
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadSnapshot(json.Replace("\"canExchange\":true,", "")));
        }

        [Test]
        public void ExactUtf8LimitIsAcceptedButOneMoreByteIsRejected()
        {
            string json = JsonUtility.ToJson(Command());
            string boundary = json + new string(' ', PokerWireJson.MaximumUtf8Bytes - json.Length);
            Assert.DoesNotThrow(() => PokerWireJson.ReadCommand(boundary));
            Assert.Throws<PokerWireException>(() => PokerWireJson.ReadCommand(boundary + " "));
        }

        [TestCase("version")][TestCase("seat")][TestCase("transition-hand")][TestCase("transition-version")]
        [TestCase("transition-seat")][TestCase("no-transition")][TestCase("zero-paid")][TestCase("rejection-with-approval")]
        public void ReceiptApprovalFieldsMustAgree(string field)
        {
            var s = Start(100, 100); var command = Bet(s, BettingAction.Call());
            var dto = PokerWireMapper.ToWire(s.Submit(command.Seat, command));
            switch (field)
            {
                case "version": dto.appliedVersion = ""; break;
                case "seat": dto.seat = 0; break;
                case "transition-hand": dto.transition[0].handId = Guid.NewGuid().ToString("D"); break;
                case "transition-version": dto.transition[0].appliedVersion = "3"; break;
                case "transition-seat": dto.transition[0].seat = B.Value; break;
                case "no-transition": dto.transition = Array.Empty<PokerWireTransition>(); break;
                case "zero-paid": dto.transition[0].chipsPaid = "0"; break;
                case "rejection-with-approval": dto.error = "wrong_turn"; break;
            }
            Assert.Throws<PokerWireException>(() => PokerWireMapper.Validate(dto));
        }

        [TestCase(false)][TestCase(true)]
        public void PendingOddRuleAndExplicitTestOnlyOddAwardRemainDistinct(bool explicitTestPriority)
        {
            var s = TiedSession(explicitTestPriority);
            Act(s, BettingAction.Call()); Act(s, BettingAction.Fold()); Act(s, BettingAction.Check());
            int steps = 0; while (s.State.CurrentSeat.HasValue) { Assert.That(++steps, Is.LessThan(20)); Passive(s); }
            Assert.That(s.State.Phase, Is.EqualTo(explicitTestPriority ? HandPhase.Complete : HandPhase.AwaitingSettlementRule));
            for (int i = 0; i < s.State.SeatCount; i++) AssertSnapshot(s, s.State.GetSeatAt(i));
            var dto = PokerWireMapper.ToWire(PokerPlayerViewProjector.Create(s, A));
            if (!explicitTestPriority)
            { Assert.That(dto.result, Is.Empty); Assert.That(dto.totalAwarded, Is.Empty); Assert.That(dto.potAmount, Is.EqualTo("5")); }
            else
            {
                Assert.That(dto.result[0].pots[0].payouts.Count(p => p.hasOddChip), Is.EqualTo(1));
                foreach (var payout in dto.result[0].pots[0].payouts) payout.hasOddChip = false;
                Assert.Throws<PokerWireException>(() => PokerWireMapper.Validate(dto));
            }
        }

        [TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)]
        public void FiftySeededHandsPerSeatCountRoundTripEveryReceiptAndViewer(int count)
        {
            for (int seed = 0; seed < 50; seed++)
            {
                var random = new System.Random(seed * 97 + count);
                var s = new PokerHandSession(Guid.NewGuid(), Setup(Enumerable.Range(0, count).Select(_ => (long)random.Next(5, 80)).ToArray()), new SeededRandom(seed));
                s.Start(new StartHandCommand(s.HandId, Guid.NewGuid())); int steps = 0;
                while (true)
                {
                    for (int i = 0; i < count; i++) AssertSnapshot(s, s.State.GetSeatAt(i));
                    if (!s.State.CurrentSeat.HasValue) break;
                    Assert.That(++steps, Is.LessThan(200), "Seed " + seed);
                    SeatId seat = s.State.CurrentSeat.Value; HandCommand command;
                    if (s.State.Phase == HandPhase.Exchange)
                        command = HandCommand.Exchange(s.HandId, Guid.NewGuid(), seat, s.Version, s.State.GetHand(seat).Take(random.Next(6)).ToArray());
                    else
                    {
                        var legal = s.State.CurrentBetting.GetLegalActions(); var choices = new List<BettingAction>();
                        if (legal.CanFold) choices.Add(BettingAction.Fold());
                        if (legal.CanCheck) choices.Add(BettingAction.Check());
                        if (legal.CanCall) choices.Add(BettingAction.Call());
                        if (legal.CanBet) { choices.Add(BettingAction.BetTo(legal.MinimumAggressiveTarget.Value)); choices.Add(BettingAction.BetTo(legal.MaximumAggressiveTarget.Value)); }
                        if (legal.CanRaise) { choices.Add(BettingAction.RaiseTo(legal.MinimumAggressiveTarget.Value)); choices.Add(BettingAction.RaiseTo(legal.MaximumAggressiveTarget.Value)); }
                        command = Bet(s, choices[random.Next(choices.Count)]);
                    }
                    var decoded = PokerWireJson.ReadCommand(PokerWireJson.Serialize(command));
                    var receipt = s.Submit(seat, decoded); Assert.That(receipt.Accepted, Is.True, "Seed " + seed);
                    PokerWireJson.ReadReceipt(PokerWireJson.Serialize(receipt));
                    Assert.That(s.Submit(seat, PokerWireJson.ReadCommand(PokerWireJson.Serialize(command))), Is.SameAs(receipt));
                }
            }
        }

        [Test]
        public void EverySupportedEnumHasUniqueExplicitTokenAndInvalidValuesAreRejected()
        {
            foreach (HandPhase phase in Enum.GetValues(typeof(HandPhase)))
                if (phase != HandPhase.Invalid) Assert.That(PokerWireTokens.Phase(PokerWireTokens.Phase(phase)), Is.EqualTo(phase));
            foreach (HandCommandKind kind in new[] { HandCommandKind.Bet, HandCommandKind.Exchange })
                Assert.That(PokerWireTokens.Kind(PokerWireTokens.Kind(kind)), Is.EqualTo(kind));
            foreach (BettingActionKind action in Enum.GetValues(typeof(BettingActionKind)))
                Assert.That(PokerWireTokens.Action(PokerWireTokens.Action(action)), Is.EqualTo(action));
            foreach (HandError error in Enum.GetValues(typeof(HandError)))
                Assert.That(PokerWireTokens.Error(PokerWireTokens.Error(error)), Is.EqualTo(error));
            foreach (HandCompletionReason reason in Enum.GetValues(typeof(HandCompletionReason)))
                Assert.That(PokerWireTokens.Reason(PokerWireTokens.Reason(reason)), Is.EqualTo(reason));
            Assert.Throws<PokerWireException>(() => PokerWireTokens.Phase(HandPhase.Invalid));
            Assert.Throws<PokerWireException>(() => PokerWireTokens.Kind(HandCommandKind.Invalid));
            Assert.Throws<PokerWireException>(() => PokerWireTokens.Error((HandError)999));
            Assert.Throws<PokerWireException>(() => PokerWireTokens.Action("Check"));
        }

        [Test]
        public void PublicWireFieldsAreExactlyAllowlistedAndSnapshotCannotCarryOtherCardsOrCommandIds()
        {
            Surface<PokerWireCommand>("protocolVersion message handId commandId expectedVersion kind action targetTotal seat cards");
            Surface<PokerWireReceipt>("protocolVersion message handId commandId appliedVersion error seat transition");
            Surface<PokerWireSnapshot>("protocolVersion message handId version phase potAmount currentBet totalAwarded viewerSeat currentSeat maxExchangeCount ownCards canExchange seats betting lastTransition result");
            Surface<PokerWireSeat>("seat stack committed streetContribution awarded folded allIn");
            Surface<PokerWireBetting>("canFold canCheck canCall canBet canRaise callAmount minimumAggressiveTarget maximumAggressiveTarget");
            Surface<PokerWireTransition>("handId appliedVersion beforePhase afterPhase kind action targetTotal chipsPaid refundedAmount seat exchangeCount refundedSeat");
            Surface<PokerWireResult>("handId version reason totalAwarded viewerSeat seats pots refunds");
            Surface<PokerWireSeatResult>("seat finalStack grossAward");
            Surface<PokerWirePot>("lowerBound contributionCap amount eligibleSeats payouts");
            Surface<PokerWirePayout>("seat amount hasOddChip"); Surface<PokerWireRefund>("bettingPhase amount seat");
            Assert.That(typeof(PokerWireSnapshot).Assembly.GetReferencedAssemblies().Any(a => a.Name.StartsWith("UnityEngine", StringComparison.Ordinal)), Is.False);
        }

        private static void Surface<T>(string fields)
        {
            Assert.That(typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.Name), Is.EquivalentTo(fields.Split(' ')));
            Assert.That(typeof(T).GetFields(BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(T).GetProperties(), Is.Empty);
        }
        private static void AssertSnapshot(PokerHandSession s, SeatId seat)
        {
            var view = PokerPlayerViewProjector.Create(s, seat); string json = PokerWireJson.Serialize(view);
            var dto = PokerWireJson.ReadSnapshot(json);
            Assert.That(JsonUtility.ToJson(dto), Is.EqualTo(json));
            Assert.That(dto.ownCards, Is.EqualTo(s.State.GetHand(seat).Select(card => card.Id)));
            Assert.That(dto.seats.Select(row => row.seat), Is.EqualTo(view.Seats.Select(row => row.Seat.Value)));
            Assert.That(json, Does.Not.Contain("commandId")); Assert.That(json, Does.Not.Contain("SelectedCards"));
            Assert.That(dto.version, Is.EqualTo(s.Version.ToString(CultureInfo.InvariantCulture)));
        }
        private static PokerWireCommand Command() => new PokerWireCommand
        {
            protocolVersion = 1, message = "command", handId = Guid.NewGuid().ToString("D"), commandId = Guid.NewGuid().ToString("D"),
            expectedVersion = long.MaxValue.ToString(CultureInfo.InvariantCulture), kind = "bet", action = "check", targetTotal = "", seat = A.Value, cards = Array.Empty<int>()
        };
        private static void Reject(PokerWireCommand dto) => Assert.Throws<PokerWireException>(() => PokerWireMapper.ToCommand(dto));
        private static HandSetup Setup(params long[] stacks)
        {
            var order = new[] { A, B, new SeatId(91), new SeatId(2), new SeatId(int.MaxValue) }.Take(stacks.Length).ToArray();
            return new HandSetup(ChipLedger.Create(stacks.Select((stack, i) => new SeatChips(order[i], stack)).ToArray()), order, order, order, order, 1, 2);
        }
        private static PokerHandSession Start(params long[] stacks)
        {
            var s = new PokerHandSession(Guid.NewGuid(), Setup(stacks), new IdentityRandom());
            Assert.That(s.Start(new StartHandCommand(s.HandId, Guid.NewGuid())).Accepted, Is.True); return s;
        }
        private static HandCommand Bet(PokerHandSession s, BettingAction action) => HandCommand.Bet(s.HandId, Guid.NewGuid(), s.State.CurrentSeat.Value, s.Version, action);
        private static void Act(PokerHandSession s, BettingAction action)
        { var command = Bet(s, action); var receipt = s.Submit(command.Seat, command); Assert.That(receipt.Accepted, Is.True); PokerWireJson.ReadReceipt(PokerWireJson.Serialize(receipt)); }
        private static void Passive(PokerHandSession s)
        {
            if (s.State.Phase != HandPhase.Exchange)
            { Act(s, s.State.CurrentBetting.GetLegalActions().CanCall ? BettingAction.Call() : BettingAction.Check()); return; }
            var command = HandCommand.Exchange(s.HandId, Guid.NewGuid(), s.State.CurrentSeat.Value, s.Version, Array.Empty<Card>());
            var receipt = s.Submit(command.Seat, command); Assert.That(receipt.Accepted, Is.True); PokerWireJson.ReadReceipt(PokerWireJson.Serialize(receipt));
        }
        private sealed class IdentityRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private sealed class SeededRandom : IRandomSource
        { private readonly System.Random random; public SeededRandom(int seed) { random = new System.Random(seed); } public int NextInt(int exclusiveMax) => random.Next(exclusiveMax); }

        // Test-only physical deck: A/B equal straights, C folds after its one-chip blind.
        private static PokerHandSession TiedSession(bool explicitTestPriority, long startingStack = 100)
        {
            var order = new[] { A, B, new SeatId(91) };
            var setup = new HandSetup(ChipLedger.Create(order.Select(seat => new SeatChips(seat, startingStack)).ToArray()),
                order, new[] { order[0], order[2], order[1] }, order, order, 1, 2, explicitTestPriority ? order : null);
            int[][] hands = { new[] { 0, 4, 8, 12, 16 }, new[] { 1, 5, 9, 13, 17 }, new[] { 2, 7, 22, 31, 42 } };
            var target = new List<int>(); for (int i = 0; i < 5; i++) foreach (var hand in hands) target.Add(hand[i]);
            target.AddRange(Enumerable.Range(0, 52).Where(id => !target.Contains(id)).ToArray());
            var working = Enumerable.Range(0, 52).ToArray(); var choices = new Queue<int>();
            for (int i = 51; i > 0; i--)
            { int j = Array.IndexOf(working, target[i], 0, i + 1); choices.Enqueue(j); int swap = working[i]; working[i] = working[j]; working[j] = swap; }
            var s = new PokerHandSession(Guid.NewGuid(), setup, new QueuedRandom(choices));
            s.Start(new StartHandCommand(s.HandId, Guid.NewGuid())); return s;
        }
        private sealed class QueuedRandom : IRandomSource
        { private readonly Queue<int> choices; public QueuedRandom(Queue<int> choices) { this.choices = choices; } public int NextInt(int exclusiveMax) => choices.Dequeue(); }
    }
}
