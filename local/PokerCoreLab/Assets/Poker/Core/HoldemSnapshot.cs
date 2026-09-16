using System;

namespace Poker.Foundation
{
    /// <summary>Seat-relative completed result. Folded private cards and evaluations never enter this view.</summary>
    public sealed class HoldemPublicResult
    {
        private readonly Card[] ownBestCards;
        private readonly Card[] opponentBestCards;

        internal HoldemPublicResult(HoldemSettlement settlement, SeatId viewer, SeatId opponent)
        {
            if (settlement == null) throw new ArgumentNullException(nameof(settlement));
            Kind = settlement.Kind;
            PotAmount = settlement.PotAmount;
            WinnerSeat = settlement.WinnerSeat;
            FoldedSeat = settlement.FoldedSeat;
            OwnPayout = settlement.GetAwardedTo(viewer);
            OpponentPayout = settlement.GetAwardedTo(opponent);
            if (settlement.Kind == HoldemResultKind.Showdown)
            {
                OwnHandValue = settlement.GetHandValue(viewer);
                OpponentHandValue = settlement.GetHandValue(opponent);
                ownBestCards = CopyBest(settlement, viewer);
                opponentBestCards = CopyBest(settlement, opponent);
            }
            else
            {
                ownBestCards = Array.Empty<Card>();
                opponentBestCards = Array.Empty<Card>();
            }
        }

        public HoldemResultKind Kind { get; }
        public long PotAmount { get; }
        public SeatId? WinnerSeat { get; }
        public SeatId? FoldedSeat { get; }
        public long OwnPayout { get; }
        public long OpponentPayout { get; }
        public HandValue? OwnHandValue { get; }
        public HandValue? OpponentHandValue { get; }
        public int OwnBestCardCount => ownBestCards.Length;
        public int OpponentBestCardCount => opponentBestCards.Length;

        public Card GetOwnBestCard(int index) => GetCard(ownBestCards, index);
        public Card GetOpponentBestCard(int index) => GetCard(opponentBestCards, index);

        private static Card[] CopyBest(HoldemSettlement settlement, SeatId seat)
        {
            var result = new Card[HandEvaluator.HandSize];
            for (int i = 0; i < result.Length; i++) result[i] = settlement.GetBestCard(seat, i);
            return result;
        }

        private static Card GetCard(Card[] cards, int index)
        {
            if (index < 0 || index >= cards.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return cards[index];
        }
    }

    /// <summary>
    /// The only player-facing hand state. It contains the viewer's two private cards and reveals
    /// the opponent's two cards only after an actual showdown, never after a fold.
    /// </summary>
    public sealed class HoldemSnapshot
    {
        private readonly Card[] board;
        private readonly Card[] ownCards;
        private readonly Card[] opponentCards;

        internal HoldemSnapshot(Guid sessionId, long sessionVersion, long handNumber,
            bool canContinue, bool isOver, SeatId? bustedSeat, HoldemHand hand, SeatId viewer)
        {
            if (hand == null) throw new ArgumentNullException(nameof(hand));
            if (viewer != hand.ButtonSeat && viewer != hand.BigBlindSeat)
                throw new ArgumentException("The viewer is not part of this heads-up hand.", nameof(viewer));
            SessionId = sessionId;
            SessionVersion = sessionVersion;
            HandNumber = handNumber;
            CanContinue = canContinue;
            IsOver = isOver;
            BustedSeat = bustedSeat;
            HandId = hand.HandId;
            HandVersion = hand.Version;
            ViewerSeat = viewer;
            OpponentSeat = viewer == hand.ButtonSeat ? hand.BigBlindSeat : hand.ButtonSeat;
            Street = hand.Street;
            ButtonSeat = hand.ButtonSeat;
            BigBlindSeat = hand.BigBlindSeat;
            CurrentSeat = hand.CurrentSeat;
            board = CopyBoard(hand);
            ownCards = CopyHoles(hand, ViewerSeat);
            bool revealOpponent = hand.IsComplete && hand.Result.Kind == HoldemResultKind.Showdown;
            opponentCards = revealOpponent ? CopyHoles(hand, OpponentSeat) : Array.Empty<Card>();
            SeatChips own = hand.Ledger.GetChips(ViewerSeat);
            SeatChips opponent = hand.Ledger.GetChips(OpponentSeat);
            OwnStack = own.Stack;
            OpponentStack = opponent.Stack;
            OwnCommitted = own.Committed;
            OpponentCommitted = opponent.Committed;
            OwnStreetContribution = hand.CurrentBetting.GetStreetContribution(ViewerSeat);
            OpponentStreetContribution = hand.CurrentBetting.GetStreetContribution(OpponentSeat);
            PotAmount = hand.IsComplete ? hand.Result.PotAmount : hand.Ledger.TotalCommitted;
            LegalActions = hand.CurrentSeat == viewer ? hand.CurrentBetting.GetLegalActions() : null;
            Result = hand.IsComplete ? new HoldemPublicResult(hand.Result, ViewerSeat, OpponentSeat) : null;
        }

        public Guid SessionId { get; }
        public Guid HandId { get; }
        public long SessionVersion { get; }
        /// <summary>Informational per-hand revision only. Commands must use SessionVersion.</summary>
        public long HandVersion { get; }
        public long HandNumber { get; }
        /// <summary>True only when the authority can explicitly start the next hand.</summary>
        public bool CanContinue { get; }
        public bool IsOver { get; }
        public SeatId? BustedSeat { get; }
        public HoldemStreet Street { get; }
        public SeatId ButtonSeat { get; }
        public SeatId BigBlindSeat { get; }
        public SeatId ViewerSeat { get; }
        public SeatId OpponentSeat { get; }
        public SeatId? CurrentSeat { get; }
        public long OwnStack { get; }
        public long OpponentStack { get; }
        public long OwnCommitted { get; }
        public long OpponentCommitted { get; }
        public long OwnStreetContribution { get; }
        public long OpponentStreetContribution { get; }
        public long PotAmount { get; }
        public LegalBettingActions LegalActions { get; }
        public HoldemPublicResult Result { get; }
        public int BoardCount => board.Length;
        public int OwnCardCount => ownCards.Length;
        public int OpponentCardCount => opponentCards.Length;

        public Card GetBoardCard(int index) => GetCard(board, index);
        public Card GetOwnCard(int index) => GetCard(ownCards, index);
        public Card GetOpponentCard(int index) => GetCard(opponentCards, index);

        private static Card[] CopyBoard(HoldemHand hand)
        {
            var result = new Card[hand.BoardCount];
            for (int i = 0; i < result.Length; i++) result[i] = hand.GetBoardCard(i);
            return result;
        }

        private static Card[] CopyHoles(HoldemHand hand, SeatId seat)
        {
            var result = new Card[hand.GetHoleCardCount(seat)];
            for (int i = 0; i < result.Length; i++) result[i] = hand.GetHoleCard(seat, i);
            return result;
        }

        private static Card GetCard(Card[] cards, int index)
        {
            if (index < 0 || index >= cards.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return cards[index];
        }
    }
}
