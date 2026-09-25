using System;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Detached display data. Neither local nor packet-backed views can deal cards or settle a pot.</summary>
    public sealed class HoldemTableDisplay
    {
        private readonly Card[] board;
        private readonly HoldemSeatDisplay[] seats;
        private readonly HoldemActionDisplay[] streetActions = Array.Empty<HoldemActionDisplay>();
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public long SessionVersion { get; }
        public long HandNumber { get; }
        public long PotAmount { get; }
        public SeatId ViewerSeat { get; }
        public SeatId? CurrentSeat { get; }
        public HoldemStreet Street { get; }
        public bool CanContinue { get; }
        public bool IsOver { get; }
        public bool IsSettlementPending { get; }
        public bool IsRevealPending { get; }
        public bool IsDealPending { get; }
        public HoldemDealDisplay PendingDeal { get; }
        public HoldemAccusationView Accusations { get; }
        public HoldemOwnCardChangeDisplay OwnCardChange { get; }
        public HoldemLegalDisplay LegalActions { get; }
        public HoldemResultDisplay Result { get; }
        public HoldemActionDisplay LastAction { get; }
        public bool HasStreetActions { get; }
        public int StreetActionCount => streetActions.Length;
        public HoldemActionDisplay GetStreetAction(int index) => streetActions[index];
        public int SeatCount => seats.Length;
        public int BoardCount => board.Length;
        public long OwnStack => GetSeat(ViewerSeat).Stack;
        public long OwnStreetContribution => GetSeat(ViewerSeat).StreetContribution;
        public int OwnCardCount => GetSeat(ViewerSeat).VisibleHoleCardCount;
        public Card GetOwnCard(int index) => GetSeat(ViewerSeat).GetVisibleHoleCard(index);
        public Card GetBoardCard(int index) => board[index];
        public HoldemSeatDisplay GetSeatAt(int index) => seats[index];
        public HoldemSeatDisplay GetSeat(SeatId seat)
        {
            foreach (var item in seats) if (item.Seat == seat) return item;
            throw new ArgumentException("Unknown display seat.", nameof(seat));
        }

        public HoldemTableDisplay(HoldemSnapshot source, HoldemActionNotice lastAction = null,
            HoldemOwnCardChange ownCardChange = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SessionId = source.SessionId; HandId = source.HandId; SessionVersion = source.SessionVersion;
            HandNumber = source.HandNumber; PotAmount = source.PotAmount; ViewerSeat = source.ViewerSeat;
            CurrentSeat = source.CurrentSeat; Street = source.Street; CanContinue = source.CanContinue; IsOver = source.IsOver;
            IsSettlementPending = source.IsSettlementPending; IsRevealPending = source.IsRevealPending;
            IsDealPending = source.IsDealPending; Accusations = source.Accusations;
            PendingDeal = source.PendingDeal == null ? null : new HoldemDealDisplay(source.PendingDeal.WindowId, source.PendingDeal.Street);
            LegalActions = source.LegalActions == null ? null : new HoldemLegalDisplay(source.LegalActions);
            Result = source.Result == null ? null : new HoldemResultDisplay(source.Result);
            LastAction = lastAction == null ? null : new HoldemActionDisplay(lastAction.Seat, lastAction.Kind, lastAction.Street, lastAction.Paid);
            board = CopyCards(source.BoardCount, source.GetBoardCard);
            if (ownCardChange != null)
            {
                if (ownCardChange.HandId != HandId || ownCardChange.Street != Street || !IsRevealPending
                    || ownCardChange.BoardIndex >= board.Length || board[ownCardChange.BoardIndex] != ownCardChange.Card)
                    throw new ArgumentException("Success feedback does not match the current reveal.", nameof(ownCardChange));
                OwnCardChange = new HoldemOwnCardChangeDisplay(ownCardChange.DealWindowId,
                    ownCardChange.BoardIndex, ownCardChange.Card);
            }
            seats = new HoldemSeatDisplay[source.SeatCount];
            for (int i = 0; i < seats.Length; i++) seats[i] = new HoldemSeatDisplay(source.GetSeatAt(i));
        }

        public HoldemTableDisplay(HoldemRoomPacket packet)
        {
            HoldemPacketDisplayValidation.Validate(packet);
            var source = packet.game;
            SessionId = Guid.ParseExact(packet.sessionId, "N"); HandId = Guid.ParseExact(source.handId, "N");
            SessionVersion = source.version; HandNumber = source.handNumber; PotAmount = source.pot;
            ViewerSeat = new SeatId(packet.viewerSeat); CurrentSeat = source.currentSeat == 0 ? (SeatId?)null : new SeatId(source.currentSeat);
            Street = (HoldemStreet)source.street; CanContinue = source.canContinue; IsOver = source.isOver;
            IsSettlementPending = source.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority;
            IsRevealPending = source.revealPending; IsDealPending = source.dealPending;
            PendingDeal = !source.dealPending ? null : new HoldemDealDisplay(Guid.ParseExact(source.dealWindowId, "N"), (HoldemStreet)source.dealStreet);
            LegalActions = !source.hasLegal ? null : new HoldemLegalDisplay(source.legal);
            Result = !source.hasResult ? null : new HoldemResultDisplay(source.result);
            LastAction = !packet.hasLastAction ? null : new HoldemActionDisplay(new SeatId(packet.lastAction.seat),
                (BettingActionKind)packet.lastAction.kind, (HoldemStreet)packet.lastAction.street, packet.lastAction.paid);
            HasStreetActions = source.hasStreetActions;
            if (HasStreetActions)
            {
                streetActions = new HoldemActionDisplay[source.streetActions.Length];
                for (int i = 0; i < streetActions.Length; i++)
                {
                    var a = source.streetActions[i];
                    streetActions[i] = new HoldemActionDisplay(new SeatId(a.seat), (BettingActionKind)a.kind, (HoldemStreet)a.street, a.paid);
                }
            }
            board = DecodeCards(source.board); seats = new HoldemSeatDisplay[source.seats.Length];
            if (packet.hasOwnCardChange)
                OwnCardChange = new HoldemOwnCardChangeDisplay(Guid.ParseExact(packet.ownCardChange.dealWindowId, "N"),
                    packet.ownCardChange.boardIndex, Card.FromId(packet.ownCardChange.card));
            for (int i = 0; i < seats.Length; i++)
            {
                string name = null;
                foreach (var member in packet.members) if (member.seat == source.seats[i].seat) name = member.name;
                seats[i] = new HoldemSeatDisplay(source.seats[i], name);
            }
            Array.Sort(seats, (a, b) => a.TableIndex.CompareTo(b.TableIndex));
        }

        internal static Card[] CopyCards(int count, Func<int, Card> read)
        { var result = new Card[count]; for (int i = 0; i < count; i++) result[i] = read(i); return result; }
        internal static Card[] DecodeCards(int[] cards)
        { var result = new Card[cards.Length]; for (int i = 0; i < cards.Length; i++) result[i] = Card.FromId(cards[i]); return result; }
    }

    public sealed class HoldemOwnCardChangeDisplay
    {
        internal HoldemOwnCardChangeDisplay(Guid eventId, int boardIndex, Card card)
        { EventId = eventId; BoardIndex = boardIndex; Card = card; }
        public Guid EventId { get; }
        public int BoardIndex { get; }
        public Card Card { get; }
    }

    public sealed class HoldemSeatDisplay
    {
        private readonly Card[] holes, best;
        public SeatId Seat { get; }
        public string Name { get; }
        public int TableIndex { get; }
        public bool IsViewer { get; }
        public bool WasDealtIn { get; }
        public bool IsButton { get; }
        public bool IsSmallBlind { get; }
        public bool IsBigBlind { get; }
        public bool IsCurrentActor { get; }
        public HoldemSeatStatus Status { get; }
        public long Stack { get; }
        public long Committed { get; }
        public long StreetContribution { get; }
        public long Awarded { get; }
        public HandValue? RevealedHandValue { get; }
        public int VisibleHoleCardCount => holes.Length;
        public int RevealedBestCardCount => best.Length;
        public Card GetVisibleHoleCard(int index) => holes[index];
        public Card GetRevealedBestCard(int index) => best[index];

        internal HoldemSeatDisplay(HoldemSeatView s)
        {
            Seat = s.Seat; TableIndex = s.TableIndex; IsViewer = s.IsViewer; WasDealtIn = s.WasDealtIn;
            IsButton = s.IsButton; IsSmallBlind = s.IsSmallBlind; IsBigBlind = s.IsBigBlind; IsCurrentActor = s.IsCurrentActor;
            Status = s.Status; Stack = s.Stack; Committed = s.Committed; StreetContribution = s.StreetContribution; Awarded = s.Awarded;
            RevealedHandValue = s.RevealedHandValue;
            holes = HoldemTableDisplay.CopyCards(s.VisibleHoleCardCount, s.GetVisibleHoleCard);
            best = HoldemTableDisplay.CopyCards(s.RevealedBestCardCount, s.GetRevealedBestCard);
        }
        internal HoldemSeatDisplay(HoldemSeatPacket s, string name)
        {
            Seat = new SeatId(s.seat); Name = name; TableIndex = s.tableIndex; IsViewer = s.viewer; WasDealtIn = s.dealtIn;
            IsButton = s.button; IsSmallBlind = s.smallBlind; IsBigBlind = s.bigBlind; IsCurrentActor = s.currentActor;
            Status = (HoldemSeatStatus)s.status; Stack = s.stack; Committed = s.committed; StreetContribution = s.streetContribution; Awarded = s.awarded;
            holes = HoldemTableDisplay.DecodeCards(s.visibleCards); best = HoldemTableDisplay.DecodeCards(s.revealedBestCards);
            // Only the already-public five cards are evaluated, solely to label the hand. Payout comes from Result.
            RevealedHandValue = best.Length == 5 ? HandEvaluator.Evaluate(best) : (HandValue?)null;
        }
    }

    public sealed class HoldemLegalDisplay
    {
        public bool CanFold { get; }
        public bool CanCheck { get; }
        public bool CanCall { get; }
        public bool CanBet { get; }
        public bool CanRaise { get; }
        public long CallAmount { get; }
        public long? MinimumAggressiveTarget { get; }
        public long? MaximumAggressiveTarget { get; }
        internal HoldemLegalDisplay(LegalBettingActions source)
        {
            CanFold = source.CanFold; CanCheck = source.CanCheck; CanCall = source.CanCall; CanBet = source.CanBet; CanRaise = source.CanRaise;
            CallAmount = source.CallAmount; MinimumAggressiveTarget = source.MinimumAggressiveTarget; MaximumAggressiveTarget = source.MaximumAggressiveTarget;
        }
        internal HoldemLegalDisplay(HoldemLegalPacket source)
        {
            CanFold = source.fold; CanCheck = source.check; CanCall = source.call; CanBet = source.bet; CanRaise = source.raise;
            CallAmount = source.callAmount;
            MinimumAggressiveTarget = CanBet || CanRaise ? source.minimumTarget : (long?)null;
            MaximumAggressiveTarget = CanBet || CanRaise ? source.maximumTarget : (long?)null;
        }
        /// <summary>Display hint only. The host still validates the command against current authority.</summary>
        public bool Allows(BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            switch (action.Kind)
            {
                case BettingActionKind.Fold: return CanFold;
                case BettingActionKind.Check: return CanCheck;
                case BettingActionKind.Call: return CanCall;
                case BettingActionKind.BetTo: return CanBet && InRange(action.Target);
                case BettingActionKind.RaiseTo: return CanRaise && InRange(action.Target);
                default: return false;
            }
        }
        private bool InRange(long target) => MinimumAggressiveTarget.HasValue && target >= MinimumAggressiveTarget.Value && target <= MaximumAggressiveTarget.Value;
    }

    public sealed class HoldemResultDisplay
    {
        private readonly HoldemPotDisplay[] pots;
        public HoldemResultKind Kind { get; }
        public SeatId? WinnerSeat { get; }
        public int PotCount => pots.Length;
        public HoldemPotDisplay GetPot(int index) => pots[index];
        internal HoldemResultDisplay(HoldemPublicResult source)
        {
            Kind = source.Kind; WinnerSeat = source.WinnerSeat; pots = new HoldemPotDisplay[source.PotCount];
            for (int i = 0; i < pots.Length; i++) pots[i] = new HoldemPotDisplay(source.GetPot(i));
        }
        internal HoldemResultDisplay(HoldemResultPacket source)
        {
            Kind = (HoldemResultKind)source.kind; WinnerSeat = source.winnerSeat == 0 ? (SeatId?)null : new SeatId(source.winnerSeat);
            pots = new HoldemPotDisplay[source.pots.Length];
            for (int i = 0; i < pots.Length; i++) pots[i] = new HoldemPotDisplay(source.pots[i]);
        }
    }
    public sealed class HoldemPotDisplay
    {
        private readonly HoldemPayoutDisplay[] payouts;
        public int PayoutCount => payouts.Length;
        public HoldemPayoutDisplay GetPayout(int index) => payouts[index];
        internal HoldemPotDisplay(PotAward source)
        {
            payouts = new HoldemPayoutDisplay[source.PayoutCount];
            for (int i = 0; i < payouts.Length; i++) payouts[i] = new HoldemPayoutDisplay(source.GetPayout(i).Seat, source.GetPayout(i).Amount);
        }
        internal HoldemPotDisplay(HoldemPotPacket source)
        {
            payouts = new HoldemPayoutDisplay[source.payouts.Length];
            for (int i = 0; i < payouts.Length; i++) payouts[i] = new HoldemPayoutDisplay(new SeatId(source.payouts[i].seat), source.payouts[i].amount);
        }
    }
    public readonly struct HoldemPayoutDisplay
    {
        internal HoldemPayoutDisplay(SeatId seat, long amount) { Seat = seat; Amount = amount; }
        public SeatId Seat { get; }
        public long Amount { get; }
    }
    public sealed class HoldemDealDisplay
    {
        internal HoldemDealDisplay(Guid window, HoldemStreet street) { WindowId = window; Street = street; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
    }
    public sealed class HoldemActionDisplay
    {
        internal HoldemActionDisplay(SeatId seat, BettingActionKind kind, HoldemStreet street, long paid)
        { Seat = seat; Kind = kind; Street = street; Paid = paid; }
        public SeatId Seat { get; }
        public BettingActionKind Kind { get; }
        public HoldemStreet Street { get; }
        public long Paid { get; }
    }
}
