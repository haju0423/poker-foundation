using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Immutable authority state for one two-to-four-seat Hold'em hand.</summary>
    public sealed class HoldemHand
    {
        private readonly SeatId[] seats;
        private readonly Card[][] holeCards;
        private readonly SeatId[] foldedSeats;
        private readonly Card[] board;
        private readonly Deck deck;
        private readonly HoldemOddChipRule oddChipRule;
        private readonly SeatId[] pendingSeats;
        private readonly HoldemEvaluatedHand[] pendingHands;

        private HoldemHand(Guid handId, long version, HoldemConfig config,
            SeatId[] ownedSeats, Card[][] ownedHoleCards, SeatId[] ownedFoldedSeats,
            SeatId buttonSeat, SeatId smallBlindSeat, SeatId bigBlindSeat,
            Card[] ownedBoard, Deck ownedDeck, HoldemStreet street,
            BettingRound betting, HoldemOddChipRule oddChipRule,
            HoldemSettlement result, SeatId[] ownedPendingSeats,
            HoldemEvaluatedHand[] ownedPendingHands, bool isRevealPending = false)
        {
            HandId = handId;
            Version = version;
            Config = config;
            seats = ownedSeats;
            holeCards = ownedHoleCards;
            foldedSeats = ownedFoldedSeats;
            ButtonSeat = buttonSeat;
            SmallBlindSeat = smallBlindSeat;
            BigBlindSeat = bigBlindSeat;
            board = ownedBoard;
            deck = ownedDeck;
            Street = street;
            CurrentBetting = betting;
            this.oddChipRule = oddChipRule;
            Result = result;
            pendingSeats = ownedPendingSeats;
            pendingHands = ownedPendingHands;
            IsRevealPending = isRevealPending;
        }

        public Guid HandId { get; }
        public long Version { get; }
        public HoldemConfig Config { get; }
        public SeatId ButtonSeat { get; }
        public SeatId SmallBlindSeat { get; }
        public SeatId BigBlindSeat { get; }
        public HoldemStreet Street { get; }
        public BettingRound CurrentBetting { get; }
        public ChipLedger Ledger => Result?.Ledger ?? CurrentBetting.Ledger;
        public HoldemSettlementState SettlementState => Result != null
            ? HoldemSettlementState.Settled
            : pendingHands != null ? HoldemSettlementState.AwaitingOddChipPriority
            : HoldemSettlementState.None;
        public bool IsSettlementPending => SettlementState == HoldemSettlementState.AwaitingOddChipPriority;
        public bool IsRevealPending { get; }
        public SeatId? CurrentSeat => SettlementState == HoldemSettlementState.None && !IsRevealPending
            ? CurrentBetting.CurrentSeat : (SeatId?)null;
        public bool IsComplete => Result != null;
        public HoldemSettlement Result { get; }
        public int BoardCount => board.Length;
        public int RemainingCardCount => deck.RemainingCount;
        public int DealtInSeatCount => seats.Length;

        public SeatId GetDealtInSeatAt(int index)
        {
            if (index < 0 || index >= seats.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return seats[index];
        }

        public bool WasDealtIn(SeatId seat) => IndexOfSeat(seat, false) >= 0;

        public bool IsFolded(SeatId seat)
        {
            RequireSeat(seat);
            for (int i = 0; i < foldedSeats.Length; i++) if (foldedSeats[i] == seat) return true;
            return false;
        }

        public bool IsAllIn(SeatId seat)
        {
            RequireSeat(seat);
            return !IsFolded(seat) && CurrentBetting.Ledger.GetChips(seat).Stack == 0;
        }

        public long GetStreetContribution(SeatId seat)
        {
            RequireSeat(seat);
            for (int i = 0; i < CurrentBetting.SeatCount; i++)
                if (CurrentBetting.GetSeatAt(i) == seat) return CurrentBetting.GetStreetContribution(seat);
            return 0;
        }

        public Card GetBoardCard(int index)
        {
            if (index < 0 || index >= board.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return board[index];
        }

        public int GetHoleCardCount(SeatId seat)
        {
            RequireSeat(seat);
            return 2;
        }

        /// <summary>Authority-only private-card access. Player-facing code must use HoldemSnapshot.</summary>
        public Card GetHoleCard(SeatId seat, int index)
        {
            if (index < 0 || index >= 2) throw new ArgumentOutOfRangeException(nameof(index));
            return holeCards[IndexOfSeat(seat, true)][index];
        }

        public static HoldemHand Begin(Guid handId, ChipLedger ledger, SeatId buttonSeat,
            SeatId otherSeat, HoldemConfig config, IRandomSource random)
            => Begin(handId, ledger, new[] { buttonSeat, otherSeat }, buttonSeat,
                config, random, HoldemOddChipRule.RequireExplicitPriority);

        public static HoldemHand Begin(Guid handId, ChipLedger rosterLedger,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId buttonSeat,
            HoldemConfig config, IRandomSource random,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            return BeginWithDeck(handId, rosterLedger, seatsInTableOrder, buttonSeat,
                config, Deck.CreateShuffled(random), oddChipRule);
        }

        internal static HoldemHand BeginWithDeck(Guid handId, ChipLedger rosterLedger,
            IReadOnlyList<SeatId> seatsInTableOrder, SeatId buttonSeat,
            HoldemConfig config, Deck sourceDeck, HoldemOddChipRule oddChipRule)
        {
            ValidateRule(oddChipRule);
            SeatId[] table = ValidateAndCopyTable(handId, rosterLedger, seatsInTableOrder,
                buttonSeat, config, sourceDeck);
            SeatId[] active = FilterFunded(table, rosterLedger);
            ChipLedger activeLedger = CreateActiveLedger(active, rosterLedger);
            Deck candidateDeck = sourceDeck.Copy();
            var cards = new Card[active.Length][];
            for (int i = 0; i < cards.Length; i++) cards[i] = new Card[2];
            SeatId[] dealOrder = HoldemSeatOrder.RotateAfter(active, buttonSeat, _ => true);
            for (int round = 0; round < 2; round++)
                for (int i = 0; i < dealOrder.Length; i++)
                    cards[IndexOf(active, dealOrder[i])][round] = candidateDeck.Draw(1)[0];

            SeatId smallBlind, bigBlind;
            SeatId[] openingOrder = HoldemSeatOrder.OpeningOrder(active, buttonSeat,
                out smallBlind, out bigBlind);
            BettingRound opening = BettingRound.BeginOpening(activeLedger, openingOrder,
                smallBlind, bigBlind, config.SmallBlind, config.BigBlind);
            var started = new HoldemHand(handId, 1, config, active, cards,
                Array.Empty<SeatId>(), buttonSeat, smallBlind, bigBlind,
                Array.Empty<Card>(), candidateDeck, HoldemStreet.Preflop, opening,
                oddChipRule, null, null, null);
            return opening.IsComplete ? started.AdvanceAfterCompletedBetting(null, 1) : started;
        }

        /// <summary>Validates and returns an atomic candidate including automatic runout and settlement.</summary>
        public HoldemHand Apply(SeatId seat, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            RequireSeat(seat);
            if (IsRevealPending) throw new InvalidOperationException("The community-card reveal must be resolved first.");
            if (SettlementState != HoldemSettlementState.None)
                throw new InvalidOperationException(IsComplete ? "The hand is complete." : "Settlement priority is required.");
            if (CurrentSeat != seat) throw new InvalidOperationException("This seat cannot act now.");
            if (!CurrentBetting.GetLegalActions().Allows(action))
                throw new InvalidOperationException("The action is not legal for this Hold'em state.");

            BettingRound nextBetting = CurrentBetting.Apply(seat, action);
            SeatId[] nextFolded = action.Kind == BettingActionKind.Fold
                ? AddFolded(seat) : foldedSeats;
            long nextVersion = checked(Version + 1);
            var candidate = NewState(nextVersion, nextFolded, board, deck.Copy(),
                Street, nextBetting, null, null, null);
            return nextBetting.IsComplete
                ? candidate.AdvanceAfterCompletedBetting(action.Kind == BettingActionKind.Fold ? seat : (SeatId?)null,
                    nextVersion)
                : candidate;
        }

        /// <summary>Host closes exactly one reveal window. All-in runouts still stop at the next reveal.</summary>
        public HoldemHand ResumeAfterReveal(HoldemStreet expectedStreet)
        {
            if (!IsRevealPending) throw new InvalidOperationException("No community-card reveal is pending.");
            if (expectedStreet != Street) throw new InvalidOperationException("The reveal window has changed.");
            long nextVersion = checked(Version + 1);
            var resumed = NewState(nextVersion, foldedSeats, board, deck.Copy(),
                Street, CurrentBetting, null, null, null);
            return CurrentBetting.IsComplete ? resumed.AdvanceAfterCompletedBetting(null, nextVersion) : resumed;
        }

        /// <summary>Trusted host resolution; players never supply a payout order.</summary>
        public HoldemHand ResolvePendingSettlement(HoldemOddChipRule rule)
        {
            if (!IsSettlementPending) throw new InvalidOperationException("No odd-chip settlement is pending.");
            if (rule != HoldemOddChipRule.ClockwiseFromButton)
                throw new ArgumentException("Pending settlement can only adopt the explicit clockwise rule.", nameof(rule));
            SeatId[] priority = ClockwiseLiveOrder();
            HoldemSettlement settlement = HoldemSettlement.Showdown(CurrentBetting.Ledger,
                pendingSeats, pendingHands, priority, ButtonSeat, BigBlindSeat);
            return WithComplete(checked(Version + 1), board, deck, CurrentBetting, settlement);
        }

        internal bool HasRevealedHand(SeatId seat)
        {
            if (!WasDealtIn(seat)) return false;
            if (IsFolded(seat)) return false;
            if (Result != null) return Result.Kind == HoldemResultKind.Showdown && Result.HasHandValue(seat);
            if (pendingSeats == null) return false;
            for (int i = 0; i < pendingSeats.Length; i++) if (pendingSeats[i] == seat) return true;
            return false;
        }

        internal HoldemEvaluatedHand GetRevealedHand(SeatId seat)
        {
            if (!HasRevealedHand(seat)) throw new InvalidOperationException("This hand is not publicly revealed.");
            if (Result != null)
            {
                var cards = new Card[HandEvaluator.HandSize];
                for (int i = 0; i < cards.Length; i++) cards[i] = Result.GetBestCard(seat, i);
                return new HoldemEvaluatedHand(Result.GetHandValue(seat), cards);
            }
            for (int i = 0; i < pendingSeats.Length; i++) if (pendingSeats[i] == seat) return pendingHands[i];
            throw new InvalidOperationException("Revealed showdown hand lookup failed.");
        }

        private HoldemHand AdvanceAfterCompletedBetting(SeatId? lastFoldedSeat, long version)
        {
            SeatId[] live = LiveSeats();
            if (live.Length == 1)
            {
                HoldemSettlement uncontested = HoldemSettlement.AwardUncontested(
                    CurrentBetting.Ledger, live[0], lastFoldedSeat, ButtonSeat, BigBlindSeat);
                return WithComplete(version, board, deck, CurrentBetting, uncontested);
            }

            Card[] nextBoard = board;
            Deck nextDeck = deck;
            HoldemStreet completedStreet = Street;
            BettingRound completedBetting = CurrentBetting;
            while (true)
            {
                if (completedStreet == HoldemStreet.River)
                    return CompleteOrAwaitPriority(version, nextBoard, nextDeck, completedBetting);

                nextDeck = nextDeck.Copy();
                nextDeck.Draw(1);
                HoldemStreet nextStreet;
                int cardsToDeal;
                if (completedStreet == HoldemStreet.Preflop)
                { nextStreet = HoldemStreet.Flop; cardsToDeal = 3; }
                else if (completedStreet == HoldemStreet.Flop)
                { nextStreet = HoldemStreet.Turn; cardsToDeal = 1; }
                else
                { nextStreet = HoldemStreet.River; cardsToDeal = 1; }
                Card[] dealt = nextDeck.Draw(cardsToDeal);
                var expanded = new Card[nextBoard.Length + dealt.Length];
                Array.Copy(nextBoard, expanded, nextBoard.Length);
                Array.Copy(dealt, 0, expanded, nextBoard.Length, dealt.Length);
                nextBoard = expanded;

                SeatId[] postflopOrder = HoldemSeatOrder.RotateAfter(seats, ButtonSeat, seat => !Contains(foldedSeats, seat));
                BettingRound nextBetting = BettingRound.BeginUnopened(completedBetting.Ledger,
                    postflopOrder, Config.BigBlind);
                if (Config.RevealPolicy == HoldemRevealPolicy.PauseAfterCommunityReveal)
                    return NewState(version, foldedSeats, nextBoard, nextDeck,
                        nextStreet, nextBetting, null, null, null, true);
                if (!nextBetting.IsComplete)
                    return NewState(version, foldedSeats, nextBoard, nextDeck,
                        nextStreet, nextBetting, null, null, null);
                completedStreet = nextStreet;
                completedBetting = nextBetting;
            }
        }

        private HoldemHand CompleteOrAwaitPriority(long version, Card[] finalBoard,
            Deck finalDeck, BettingRound finalBetting)
        {
            SeatId[] live = LiveSeats();
            var values = new HoldemEvaluatedHand[live.Length];
            for (int i = 0; i < live.Length; i++)
                values[i] = HoldemBestHand.Evaluate(finalBoard, holeCards[IndexOf(seats, live[i])]);
            IReadOnlyList<SeatId> priority = oddChipRule == HoldemOddChipRule.ClockwiseFromButton
                ? ClockwiseLiveOrder() : null;
            try
            {
                HoldemSettlement settlement = HoldemSettlement.Showdown(finalBetting.Ledger,
                    live, values, priority, ButtonSeat, BigBlindSeat);
                return WithComplete(version, finalBoard, finalDeck, finalBetting, settlement);
            }
            catch (SettlementException error) when (error.Reason == SettlementFailure.MissingOddChipOrder
                && oddChipRule == HoldemOddChipRule.RequireExplicitPriority)
            {
                return NewState(version, foldedSeats, finalBoard, finalDeck,
                    HoldemStreet.River, finalBetting, null, live, values);
            }
        }

        private HoldemHand WithComplete(long version, Card[] finalBoard, Deck finalDeck,
            BettingRound finalBetting, HoldemSettlement settlement)
            => NewState(version, foldedSeats, finalBoard, finalDeck,
                HoldemStreet.Complete, finalBetting, settlement, null, null);

        private HoldemHand NewState(long version, SeatId[] nextFolded, Card[] nextBoard,
            Deck nextDeck, HoldemStreet street, BettingRound betting,
            HoldemSettlement result, SeatId[] nextPendingSeats,
            HoldemEvaluatedHand[] nextPendingHands, bool isRevealPending = false)
            => new HoldemHand(HandId, version, Config, seats, holeCards, nextFolded,
                ButtonSeat, SmallBlindSeat, BigBlindSeat, nextBoard, nextDeck, street,
                betting, oddChipRule, result, nextPendingSeats, nextPendingHands, isRevealPending);

        private SeatId[] LiveSeats()
        {
            var result = new List<SeatId>();
            for (int i = 0; i < seats.Length; i++) if (!Contains(foldedSeats, seats[i])) result.Add(seats[i]);
            return result.ToArray();
        }

        private SeatId[] ClockwiseLiveOrder()
            => HoldemSeatOrder.RotateAfter(seats, ButtonSeat, seat => !Contains(foldedSeats, seat));

        private SeatId[] AddFolded(SeatId seat)
        {
            if (Contains(foldedSeats, seat)) throw new InvalidOperationException("The seat already folded.");
            var result = new SeatId[foldedSeats.Length + 1];
            Array.Copy(foldedSeats, result, foldedSeats.Length);
            result[result.Length - 1] = seat;
            return result;
        }

        private void RequireSeat(SeatId seat) { IndexOfSeat(seat, true); }

        private int IndexOfSeat(SeatId seat, bool throwIfMissing)
        {
            for (int i = 0; i < seats.Length; i++) if (seats[i] == seat) return i;
            if (throwIfMissing) throw new ArgumentException("The seat was not dealt into this hand.", nameof(seat));
            return -1;
        }

        private static int IndexOf(IReadOnlyList<SeatId> source, SeatId seat)
        {
            for (int i = 0; i < source.Count; i++) if (source[i] == seat) return i;
            throw new InvalidOperationException("Seat lookup failed.");
        }

        private static bool Contains(IReadOnlyList<SeatId> source, SeatId seat)
        {
            for (int i = 0; i < source.Count; i++) if (source[i] == seat) return true;
            return false;
        }

        private static void ValidateRule(HoldemOddChipRule rule)
        {
            if (rule != HoldemOddChipRule.RequireExplicitPriority && rule != HoldemOddChipRule.ClockwiseFromButton)
                throw new ArgumentOutOfRangeException(nameof(rule));
        }

        private static SeatId[] ValidateAndCopyTable(Guid handId, ChipLedger ledger,
            IReadOnlyList<SeatId> tableOrder, SeatId buttonSeat, HoldemConfig config, Deck deck)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            SeatId[] table = HoldemSeatOrder.CopyTable(tableOrder);
            if (ledger.SeatCount != table.Length)
                throw new ArgumentException("The table order must contain every ledger seat.", nameof(tableOrder));
            for (int i = 0; i < table.Length; i++) ledger.GetChips(table[i]);
            if (ledger.TotalCommitted != 0)
                throw new ArgumentException("A new hand cannot inherit contributions.", nameof(ledger));
            SeatId[] active = FilterFunded(table, ledger);
            if (active.Length < 2) throw new InvalidOperationException("At least two funded seats are required.");
            if (!Contains(active, buttonSeat)) throw new ArgumentException("The button must be a funded seat.", nameof(buttonSeat));
            if (deck.RemainingCount < active.Length * 2 + 8)
                throw new ArgumentException("The deck cannot complete this Hold'em hand.", nameof(deck));
            return table;
        }

        private static SeatId[] FilterFunded(IReadOnlyList<SeatId> table, ChipLedger ledger)
        {
            var active = new List<SeatId>();
            for (int i = 0; i < table.Count; i++)
                if (ledger.GetChips(table[i]).Stack > 0) active.Add(table[i]);
            return active.ToArray();
        }

        private static ChipLedger CreateActiveLedger(IReadOnlyList<SeatId> active, ChipLedger source)
        {
            var balances = new SeatChips[active.Count];
            for (int i = 0; i < active.Count; i++)
                balances[i] = new SeatChips(active[i], source.GetChips(active[i]).Stack);
            return ChipLedger.Create(balances);
        }
    }
}
