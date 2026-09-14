using System;
using System.Collections.Generic;
using System.Linq;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Transport
{
    public static partial class PokerWireMapper
    {
        public static PokerWireSnapshot ToWire(PokerPlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            var value = new PokerWireSnapshot
            {
                protocolVersion = SnapshotProtocolVersion, message = "snapshot", handId = view.HandId.ToString("D"),
                version = Number(view.Version), viewerSeat = view.ViewerSeat.Value, phase = PokerWireTokens.Phase(view.Phase),
                currentSeat = view.CurrentSeat?.Value ?? 0, ownCards = view.OwnCards.Select(card => card.Id).ToArray(),
                potAmount = Number(view.PotAmount), currentBet = Optional(view.CurrentBet),
                canExchange = view.CanExchange, maxExchangeCount = view.MaxExchangeCount, totalAwarded = Optional(view.TotalAwarded),
                seats = view.Seats.Select(seat => new PokerWireSeat
                {
                    seat = seat.Seat.Value, stack = Number(seat.Stack), committed = Number(seat.Committed),
                    streetContribution = Optional(seat.StreetContribution), awarded = Optional(seat.Awarded),
                    folded = seat.IsFolded, allIn = seat.IsAllIn
                }).ToArray(),
                betting = view.Betting == null ? Array.Empty<PokerWireBetting>() : new[] { new PokerWireBetting
                {
                    canFold = view.Betting.CanFold, canCheck = view.Betting.CanCheck, canCall = view.Betting.CanCall,
                    canBet = view.Betting.CanBet, canRaise = view.Betting.CanRaise, callAmount = Number(view.Betting.CallAmount),
                    minimumAggressiveTarget = Optional(view.Betting.MinimumAggressiveTarget),
                    maximumAggressiveTarget = Optional(view.Betting.MaximumAggressiveTarget)
                } },
                lastTransition = view.LastTransition.HasValue ? new[] { ToWire(view.LastTransition.Value) } : Array.Empty<PokerWireTransition>(),
                result = view.Result == null ? Array.Empty<PokerWireResult>() : new[] { ToWire(view.Result) }
            };
            Validate(value);
            return value;
        }

        public static void Validate(PokerWireSnapshot value)
        {
            Need(value != null, "snapshot"); Header(value.protocolVersion, value.message, "snapshot", SnapshotProtocolVersion);
            Id(value.handId); long version = Number(value.version, true); Seat(value.viewerSeat); Seat(value.currentSeat, true);
            var phase = PokerWireTokens.Phase(value.phase); long pot = Number(value.potAmount);
            long? currentBet = Optional(value.currentBet), award = Optional(value.totalAwarded);
            Cards(value.ownCards, SeatHand.CardCount, SeatHand.CardCount);
            ArraySize(value.seats, 2, MaximumSeatCount, "snapshot.seats");
            var seats = new HashSet<int>(); decimal committed = 0, chips = 0;
            bool complete = phase == HandPhase.Complete;
            foreach (var seat in value.seats)
            {
                Need(seat != null, "snapshot.seat"); Seat(seat.seat); Need(seats.Add(seat.seat), "snapshot.duplicate_seat");
                long stack = Number(seat.stack), paid = Number(seat.committed);
                long? street = Optional(seat.streetContribution), seatAward = Optional(seat.awarded);
                committed += paid; chips += (decimal)stack + paid;
                Need(!street.HasValue || (IsBetting(phase) && street <= paid), "snapshot.street");
                Need(seatAward.HasValue == complete, "snapshot.awarded");
                Need(seat.allIn == (!complete && !seat.folded && stack == 0), "snapshot.allIn");
            }
            Need(committed == pot && chips <= long.MaxValue, "snapshot.chips");
            Need(seats.Contains(value.viewerSeat) && (value.currentSeat == 0 || seats.Contains(value.currentSeat)), "snapshot.membership");
            Need(currentBet.HasValue == IsBetting(phase), "snapshot.currentBet");
            Need((value.currentSeat == 0) == (complete || phase == HandPhase.AwaitingSettlementRule), "snapshot.currentSeat");
            bool ownTurn = value.currentSeat == value.viewerSeat;
            Need(value.canExchange == (ownTurn && phase == HandPhase.Exchange), "snapshot.canExchange");
            Need(value.maxExchangeCount == (value.canExchange ? ExchangeRound.MaxExchangeCount : 0), "snapshot.maxExchangeCount");
            One(value.betting, "snapshot.betting"); One(value.lastTransition, "snapshot.lastTransition"); One(value.result, "snapshot.result");
            Need((value.betting.Length == 1) == (ownTurn && IsBetting(phase)), "snapshot.betting");
            if (value.betting.Length == 1) Validate(value.betting[0]);
            if (value.lastTransition.Length == 1)
            {
                var transition = value.lastTransition[0]; Validate(transition);
                Need(transition.handId == value.handId && Number(transition.appliedVersion) == version
                    && transition.afterPhase == value.phase && seats.Contains(transition.seat)
                    && (transition.refundedSeat == 0 || seats.Contains(transition.refundedSeat)), "snapshot.transition");
            }
            else Need(version == 1, "snapshot.missing_transition");
            Need(award.HasValue == complete && (value.result.Length == 1) == complete, "snapshot.completion");
            if (complete)
            {
                Need(pot == 0, "snapshot.completed_pot");
                Validate(value.result[0], value);
            }
        }

        private static void Validate(PokerWireBetting value)
        {
            Need(value != null, "betting"); long call = Number(value.callAmount);
            long? min = Optional(value.minimumAggressiveTarget), max = Optional(value.maximumAggressiveTarget);
            Need(value.canCall == (call > 0) && !(value.canCheck && value.canCall), "betting.call");
            Need(!(value.canBet && value.canRaise), "betting.aggression");
            bool sized = value.canBet || value.canRaise;
            Need(sized == min.HasValue && sized == max.HasValue, "betting.targets");
            if (sized) Need(min > 0 && min <= max, "betting.targets");
        }

        private static PokerWireResult ToWire(PokerHandResultView source) => new PokerWireResult
        {
            handId = source.HandId.ToString("D"), version = Number(source.Version), viewerSeat = source.ViewerSeat.Value,
            reason = PokerWireTokens.Reason(source.Reason), totalAwarded = Number(source.TotalAwarded),
            seats = source.Seats.Select(seat => new PokerWireSeatResult
            { seat = seat.Seat.Value, finalStack = Number(seat.FinalStack), grossAward = Number(seat.GrossAward) }).ToArray(),
            pots = source.Pots.Select(pot => new PokerWirePot
            {
                lowerBound = Number(pot.LowerBound), contributionCap = Number(pot.ContributionCap), amount = Number(pot.Amount),
                eligibleSeats = pot.EligibleSeats.Select(seat => seat.Value).ToArray(),
                payouts = pot.Payouts.Select(payout => new PokerWirePayout
                { seat = payout.Seat.Value, amount = Number(payout.Amount), hasOddChip = payout.HasOddChip }).ToArray()
            }).ToArray(),
            refunds = source.Refunds.Select(refund => new PokerWireRefund
            { bettingPhase = PokerWireTokens.Phase(refund.BettingPhase), seat = refund.Seat.Value, amount = Number(refund.Amount) }).ToArray(),
            revealedHands = source.RevealedHands.Select(hand => new PokerWireRevealedHand
            { seat = hand.Seat.Value, cards = hand.Cards.Select(card => card.Id).ToArray() }).ToArray()
        };

        private static void Validate(PokerWireResult result, PokerWireSnapshot snapshot)
        {
            Need(result.handId == snapshot.handId && result.version == snapshot.version && result.viewerSeat == snapshot.viewerSeat
                && result.totalAwarded == snapshot.totalAwarded, "result.identity");
            var reason = PokerWireTokens.Reason(result.reason); long total = Number(result.totalAwarded, true);
            int live = snapshot.seats.Count(seat => !seat.folded);
            Need(live > 0 && (reason == HandCompletionReason.Uncontested) == (live == 1), "result.reason");
            int revealCount = reason == HandCompletionReason.Showdown ? live : 0;
            ArraySize(result.revealedHands, revealCount, revealCount, "result.revealedHands");
            var revealedSeats = new HashSet<int>();
            int revealIndex = 0;
            var liveOrder = snapshot.seats.Where(seat => !seat.folded).Select(seat => seat.seat).ToArray();
            // Include the viewer's private cards too so a folded viewer cannot receive a duplicated card.
            var knownCards = new HashSet<int>(snapshot.ownCards);
            foreach (var hand in result.revealedHands)
            {
                Need(hand != null && snapshot.seats.Any(seat => seat.seat == hand.seat && !seat.folded)
                    && hand.seat == liveOrder[revealIndex++] && revealedSeats.Add(hand.seat), "result.revealed_seat");
                Cards(hand.cards, SeatHand.CardCount, SeatHand.CardCount);
                if (hand.seat == snapshot.viewerSeat)
                    Need(hand.cards.SequenceEqual(snapshot.ownCards), "result.revealed_own_cards");
                else foreach (int card in hand.cards) Need(knownCards.Add(card), "result.revealed_duplicate_card");
            }
            ArraySize(result.seats, snapshot.seats.Length, snapshot.seats.Length, "result.seats");
            var awards = new Dictionary<int, decimal>(); decimal gross = 0;
            for (int i = 0; i < result.seats.Length; i++)
            {
                var seat = result.seats[i]; var outer = snapshot.seats[i];
                Need(seat != null && seat.seat == outer.seat && seat.finalStack == outer.stack
                    && seat.grossAward == outer.awarded, "result.seat_identity");
                Number(seat.finalStack); long amount = Number(seat.grossAward); gross += amount; awards.Add(seat.seat, 0);
            }
            Need(gross == total, "result.gross_total");
            ArraySize(result.pots, 1, snapshot.seats.Length, "result.pots"); decimal potsTotal = 0; long previousCap = 0;
            foreach (var pot in result.pots)
            {
                Need(pot != null, "result.pot"); long lower = Number(pot.lowerBound), cap = Number(pot.contributionCap, true);
                long amount = Number(pot.amount, true); Need(lower == previousCap && cap > lower, "result.pot_bounds"); previousCap = cap;
                var eligible = Members(pot.eligibleSeats, awards.Keys, "result.eligibleSeats");
                ArraySize(pot.payouts, 1, eligible.Count, "result.payouts"); var paid = new HashSet<int>(); decimal sum = 0;
                long share = amount / pot.payouts.Length, remainder = amount % pot.payouts.Length;
                int oddRecipients = 0;
                foreach (var payout in pot.payouts)
                {
                    Need(payout != null && eligible.Contains(payout.seat) && paid.Add(payout.seat), "result.payout_seat");
                    long awarded = Number(payout.amount, true); sum += awarded; awards[payout.seat] += awarded;
                    if (payout.hasOddChip) oddRecipients++;
                    Need((decimal)awarded == (decimal)share + (payout.hasOddChip ? 1 : 0), "result.payout_share");
                }
                Need(sum == amount && oddRecipients == remainder, "result.payout_total"); potsTotal += amount;
            }
            Need(potsTotal == total, "result.pot_total");
            foreach (var seat in result.seats) Need(awards[seat.seat] == Number(seat.grossAward), "result.seat_payout_total");
            ArraySize(result.refunds, 0, 2, "result.refunds"); var phases = new HashSet<HandPhase>();
            foreach (var refund in result.refunds)
            {
                Need(refund != null && awards.ContainsKey(refund.seat), "result.refund_seat");
                var phase = PokerWireTokens.Phase(refund.bettingPhase);
                Need(IsBetting(phase) && phases.Add(phase), "result.refund_phase"); Number(refund.amount, true);
            }
        }

        private static HashSet<int> Members(int[] values, IEnumerable<int> members, string field)
        {
            ArraySize(values, 1, MaximumSeatCount, field); var allowed = new HashSet<int>(members); var found = new HashSet<int>();
            foreach (int seat in values) Need(allowed.Contains(seat) && found.Add(seat), field);
            return found;
        }
    }
}
