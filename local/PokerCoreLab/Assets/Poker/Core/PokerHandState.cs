using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HandPhase { Invalid, FirstBetting, Exchange, SecondBetting, AwaitingSettlementRule, Complete }

    /// <summary>Immutable authority-only snapshot. Never serialize wholesale to clients or player-controlled AI.</summary>
    public sealed class PokerHandState
    {
        private readonly InitialDeal deal;
        private readonly HandSetup setup;

        private PokerHandState(HandSetup setup, InitialDeal deal, BettingRound first, ExchangeRound exchange,
            BettingRound second, PotSettlement settlement, HandPhase phase)
        {
            this.setup = setup; this.deal = deal;
            FirstBetting = first; Exchange = exchange; SecondBetting = second; Settlement = settlement; Phase = phase;
        }

        public HandPhase Phase { get; }
        public BettingRound FirstBetting { get; }
        public ExchangeRound Exchange { get; }
        public BettingRound SecondBetting { get; }
        public PotSettlement Settlement { get; }
        public ChipLedger Ledger => Settlement?.Ledger ?? SecondBetting?.Ledger ?? FirstBetting.Ledger;
        public bool IsComplete => Phase == HandPhase.Complete;
        public SeatId? CurrentSeat => Phase == HandPhase.Exchange ? Exchange.CurrentSeat : CurrentBetting?.CurrentSeat;
        /// <summary>Null outside an actionable betting phase. History is in FirstBetting/SecondBetting.</summary>
        public BettingRound CurrentBetting => Phase == HandPhase.FirstBetting ? FirstBetting
            : Phase == HandPhase.SecondBetting ? SecondBetting : null;
        public int SeatCount => deal.SeatCount;
        public int RemainingCardCount => Exchange?.RemainingCardCount ?? deal.RemainingCardCount;
        public int DiscardedCardCount => Exchange?.DiscardedCardCount ?? 0;
        public SeatId GetSeatAt(int index) => deal.GetSeatAt(index);
        public SeatHand GetHand(SeatId seat) => Exchange == null ? deal.GetHand(seat) : Exchange.GetHand(seat);

        public bool IsFolded(SeatId seat)
        {
            if (FirstBetting.IsFolded(seat)) return true;
            return SecondBetting != null && SecondBetting.IsFolded(seat);
        }

        internal static PokerHandState Begin(HandSetup setup, IRandomSource random)
        {
            BettingRound first = BettingRound.BeginOpening(setup.StartingLedger, setup.OpeningOrder,
                setup.SmallBlindSeat, setup.BigBlindSeat, setup.SmallBlind, setup.BigBlind);
            InitialDeal deal = InitialDeal.Create(random, setup.DealOrder);
            return AfterBetting(setup, deal, first, null, null);
        }

        internal PokerHandState ApplyBet(SeatId seat, BettingAction action)
        {
            BettingRound next = CurrentBetting.Apply(seat, action);
            return Phase == HandPhase.FirstBetting ? AfterBetting(setup, deal, next, null, null)
                : AfterBetting(setup, deal, FirstBetting, Exchange, next);
        }

        internal PokerHandState ApplyExchange(SeatId seat, IReadOnlyList<Card> cards)
        {
            ExchangeRound next = Exchange.Apply(seat, cards);
            if (!next.IsComplete)
                return new PokerHandState(setup, deal, FirstBetting, next, null, null, HandPhase.Exchange);
            BettingRound second = BettingRound.BeginUnopened(FirstBetting.Ledger,
                FilterLive(setup.ClosingOrder, FirstBetting, null), setup.BigBlind);
            return AfterBetting(setup, deal, FirstBetting, next, second);
        }

        private static PokerHandState AfterBetting(HandSetup setup, InitialDeal deal, BettingRound first,
            ExchangeRound exchange, BettingRound second)
        {
            BettingRound current = second ?? first;
            if (!current.IsComplete)
                return new PokerHandState(setup, deal, first, exchange, second, null,
                    second == null ? HandPhase.FirstBetting : HandPhase.SecondBetting);
            SeatId[] live = FilterLive(setup.ExchangeOrder, first, second);
            if (live.Length == 1)
                return new PokerHandState(setup, deal, first, exchange, second,
                    PotSettlement.AwardUncontested(current.Ledger, live[0]), HandPhase.Complete);
            if (second == null)
                return new PokerHandState(setup, deal, first, ExchangeRound.Begin(deal, live), null, null, HandPhase.Exchange);

            var hands = new ShowdownHand[live.Length];
            for (int i = 0; i < live.Length; i++) hands[i] = new ShowdownHand(exchange.GetHand(live[i]));
            SeatId[] priority = setup.OddChipPriority == null ? null : FilterLive(setup.OddChipPriority, first, second);
            try
            {
                PotSettlement settlement = PotSettlement.Showdown(current.Ledger, hands, priority);
                return new PokerHandState(setup, deal, first, exchange, second, settlement, HandPhase.Complete);
            }
            catch (SettlementException failure) when (failure.Reason == SettlementFailure.MissingOddChipOrder)
            {
                // The final action stays committed; only payout waits. Never let a player revise it after seeing a result.
                return new PokerHandState(setup, deal, first, exchange, second, null, HandPhase.AwaitingSettlementRule);
            }
        }

        private static SeatId[] FilterLive(IReadOnlyList<SeatId> order, BettingRound first, BettingRound second)
        {
            var result = new List<SeatId>();
            foreach (SeatId seat in order)
                if (!first.IsFolded(seat) && (second == null || !second.IsFolded(seat))) result.Add(seat);
            return result.ToArray();
        }
    }
}
