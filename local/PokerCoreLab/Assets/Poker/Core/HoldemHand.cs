using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>
    /// Immutable authority state for one heads-up Hold'em hand. Applying an action creates a
    /// new candidate; the original hand, deck cursor, cards, betting state and ledger stay unchanged.
    /// </summary>
    public sealed class HoldemHand
    {
        private readonly Card[] buttonCards;
        private readonly Card[] bigBlindCards;
        private readonly Card[] board;
        private readonly Deck deck;

        private HoldemHand(Guid handId, long version, HoldemConfig config, SeatId buttonSeat,
            SeatId bigBlindSeat, Card[] buttonCards, Card[] bigBlindCards, Card[] board,
            Deck deck, HoldemStreet street, BettingRound betting, HoldemSettlement result)
        {
            HandId = handId;
            Version = version;
            Config = config;
            ButtonSeat = buttonSeat;
            BigBlindSeat = bigBlindSeat;
            this.buttonCards = buttonCards;
            this.bigBlindCards = bigBlindCards;
            this.board = board;
            this.deck = deck;
            Street = street;
            CurrentBetting = betting;
            Result = result;
        }

        public Guid HandId { get; }
        public long Version { get; }
        public HoldemConfig Config { get; }
        /// <summary>In heads-up play the button is also the small blind.</summary>
        public SeatId ButtonSeat { get; }
        public SeatId SmallBlindSeat => ButtonSeat;
        public SeatId BigBlindSeat { get; }
        public HoldemStreet Street { get; }
        public BettingRound CurrentBetting { get; }
        public ChipLedger Ledger => Result?.Ledger ?? CurrentBetting.Ledger;
        public SeatId? CurrentSeat => IsComplete ? (SeatId?)null : CurrentBetting.CurrentSeat;
        public bool IsComplete => Result != null;
        public HoldemSettlement Result { get; }
        public int BoardCount => board.Length;
        public int RemainingCardCount => deck.RemainingCount;

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
            Card[] cards = CardsFor(seat);
            return cards[index];
        }

        public static HoldemHand Begin(Guid handId, ChipLedger ledger, SeatId buttonSeat,
            SeatId otherSeat, HoldemConfig config, IRandomSource random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            return BeginWithDeck(handId, ledger, buttonSeat, otherSeat, config, Deck.CreateShuffled(random));
        }

        internal static HoldemHand BeginWithDeck(Guid handId, ChipLedger ledger, SeatId buttonSeat,
            SeatId otherSeat, HoldemConfig config, Deck sourceDeck)
        {
            ValidateSetup(handId, ledger, buttonSeat, otherSeat, config, sourceDeck);
            Deck candidateDeck = sourceDeck.Copy();
            var bigBlindCards = new Card[2];
            var buttonCards = new Card[2];
            // Heads-up deal starts at the big blind, one card at a time around the table.
            bigBlindCards[0] = candidateDeck.Draw(1)[0];
            buttonCards[0] = candidateDeck.Draw(1)[0];
            bigBlindCards[1] = candidateDeck.Draw(1)[0];
            buttonCards[1] = candidateDeck.Draw(1)[0];

            BettingRound opening = BettingRound.BeginOpening(ledger,
                new[] { buttonSeat, otherSeat }, buttonSeat, otherSeat,
                config.SmallBlind, config.BigBlind);
            var started = new HoldemHand(handId, 1, config, buttonSeat, otherSeat,
                buttonCards, bigBlindCards, Array.Empty<Card>(), candidateDeck,
                HoldemStreet.Preflop, opening, null);
            return opening.IsComplete ? started.AdvanceAfterCompletedBetting(null, 1) : started;
        }

        /// <summary>Validates and returns an atomic candidate including automatic street runout and settlement.</summary>
        public HoldemHand Apply(SeatId seat, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            RequireSeat(seat);
            if (IsComplete) throw new InvalidOperationException("The hand is complete.");
            if (CurrentSeat != seat) throw new InvalidOperationException("This seat cannot act now.");
            if (!CurrentBetting.GetLegalActions().Allows(action))
                throw new InvalidOperationException("The action is not legal for this Hold'em state.");

            BettingRound nextBetting = CurrentBetting.Apply(seat, action);
            long nextVersion = checked(Version + 1);
            var candidate = new HoldemHand(HandId, nextVersion, Config, ButtonSeat, BigBlindSeat,
                buttonCards, bigBlindCards, board, deck.Copy(), Street, nextBetting, null);
            if (!nextBetting.IsComplete) return candidate;
            SeatId? folded = action.Kind == BettingActionKind.Fold ? seat : (SeatId?)null;
            return candidate.AdvanceAfterCompletedBetting(folded, nextVersion);
        }

        private HoldemHand AdvanceAfterCompletedBetting(SeatId? foldedSeat, long version)
        {
            if (foldedSeat.HasValue || CurrentBetting.ActiveSeatCount == 1)
            {
                if (!foldedSeat.HasValue)
                    foldedSeat = CurrentBetting.IsFolded(ButtonSeat) ? ButtonSeat : BigBlindSeat;
                HoldemSettlement folded = HoldemSettlement.AwardUncontested(CurrentBetting.Ledger,
                    ButtonSeat, BigBlindSeat, foldedSeat.Value);
                return WithComplete(version, board, deck, CurrentBetting, folded);
            }

            Card[] nextBoard = board;
            Deck nextDeck = deck;
            HoldemStreet completedStreet = Street;
            BettingRound completedBetting = CurrentBetting;
            while (true)
            {
                if (completedStreet == HoldemStreet.River)
                {
                    HoldemEvaluatedHand buttonValue = HoldemBestHand.Evaluate(nextBoard, buttonCards);
                    HoldemEvaluatedHand bigBlindValue = HoldemBestHand.Evaluate(nextBoard, bigBlindCards);
                    HoldemSettlement showdown = HoldemSettlement.Showdown(completedBetting.Ledger,
                        ButtonSeat, BigBlindSeat, buttonValue, bigBlindValue);
                    return WithComplete(version, nextBoard, nextDeck, completedBetting, showdown);
                }

                nextDeck = nextDeck.Copy();
                nextDeck.Draw(1); // Burn exactly one before flop, turn and river.
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

                // The big blind acts first on every postflop street in heads-up play.
                BettingRound nextBetting = BettingRound.BeginUnopened(completedBetting.Ledger,
                    new[] { BigBlindSeat, ButtonSeat }, Config.BigBlind);
                if (!nextBetting.IsComplete)
                    return new HoldemHand(HandId, version, Config, ButtonSeat, BigBlindSeat,
                        buttonCards, bigBlindCards, nextBoard, nextDeck, nextStreet, nextBetting, null);
                completedStreet = nextStreet;
                completedBetting = nextBetting;
            }
        }

        private HoldemHand WithComplete(long version, Card[] finalBoard, Deck finalDeck,
            BettingRound finalBetting, HoldemSettlement settlement)
            => new HoldemHand(HandId, version, Config, ButtonSeat, BigBlindSeat,
                buttonCards, bigBlindCards, finalBoard, finalDeck, HoldemStreet.Complete,
                finalBetting, settlement);

        private Card[] CardsFor(SeatId seat)
        {
            if (seat == ButtonSeat) return buttonCards;
            if (seat == BigBlindSeat) return bigBlindCards;
            throw new ArgumentException("The seat is not part of this heads-up hand.", nameof(seat));
        }

        private void RequireSeat(SeatId seat) { CardsFor(seat); }

        private static void ValidateSetup(Guid handId, ChipLedger ledger, SeatId buttonSeat,
            SeatId otherSeat, HoldemConfig config, Deck deck)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            if (!buttonSeat.IsValid || !otherSeat.IsValid || buttonSeat == otherSeat || ledger.SeatCount != 2)
                throw new ArgumentException("Hold'em Phase 1 requires exactly two distinct valid seats.");
            SeatChips button = ledger.GetChips(buttonSeat);
            SeatChips other = ledger.GetChips(otherSeat);
            if (ledger.TotalCommitted != 0) throw new ArgumentException("A new hand cannot inherit contributions.", nameof(ledger));
            if (button.Stack == 0 || other.Stack == 0) throw new InvalidOperationException("A busted seat cannot start another hand.");
            if (deck.RemainingCount < 12) throw new ArgumentException("The deck cannot complete a Hold'em board.", nameof(deck));
        }
    }
}
