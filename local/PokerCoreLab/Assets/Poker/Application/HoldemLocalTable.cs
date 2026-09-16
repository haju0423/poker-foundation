using System;
using Poker.Foundation;

namespace Poker.Application
{
    public interface IHoldemPlayerPort
    {
        HoldemSnapshot Read();
        HoldemReceipt Submit(HoldemCommand command);
        HoldemReceipt NextHand(long expectedVersion);
        HoldemReceipt ResolvePendingSettlement(long expectedVersion);
        HoldemActionNotice LastAction { get; }
    }

    public sealed class HoldemActionNotice
    {
        internal HoldemActionNotice(SeatId seat, BettingAction action, long paid)
        { Seat = seat; Kind = action.Kind; Paid = paid; Target = action.Target; }
        public SeatId Seat { get; }
        public BettingActionKind Kind { get; }
        public long Paid { get; }
        public long Target { get; }
    }

    /// <summary>Trusted single-player host. UI receives a bound human port, never the session or opponent view.</summary>
    public sealed class HoldemLocalTable
    {
        private readonly HoldemSession session;
        private readonly IHoldemOpponentPolicy opponent;
        private readonly IRandomSource opponentRandom;
        private readonly SeatId humanSeat = new SeatId(1);
        private HoldemActionNotice lastAction;

        public HoldemLocalTable(HoldemConfig config, IRandomSource deckRandom,
            IRandomSource opponentRandom, IHoldemOpponentPolicy opponent = null)
            : this(config, 2, deckRandom, opponentRandom, opponent) { }

        public HoldemLocalTable(HoldemConfig config, int seatCount, IRandomSource deckRandom,
            IRandomSource opponentRandom, IHoldemOpponentPolicy opponent = null,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority)
        {
            if (seatCount < 2 || seatCount > 4) throw new ArgumentOutOfRangeException(nameof(seatCount));
            this.opponentRandom = opponentRandom ?? throw new ArgumentNullException(nameof(opponentRandom));
            this.opponent = opponent ?? new HoldemNpcPolicy();
            var seats = new SeatId[seatCount];
            for (int i = 0; i < seatCount; i++) seats[i] = new SeatId(i + 1);
            session = new HoldemSession(Guid.NewGuid(), config, seats, humanSeat, deckRandom, oddChipRule);
            HoldemReceipt start = session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), 0);
            if (!start.Accepted) throw new InvalidOperationException("Could not start the local Hold'em table.");
            Human = new HumanPort(this);
        }
        public IHoldemPlayerPort Human { get; }

        public bool AdvanceOpponent() => AdvanceNpc();

        public bool AdvanceNpc()
        {
            SeatId? actor = Human.Read().CurrentSeat;
            if (!actor.HasValue || actor.Value == humanSeat) return false;
            HoldemSnapshot view = session.GetSnapshot(actor.Value);
            if (view.LegalActions == null) return false;
            BettingAction action = opponent.Choose(view, opponentRandom);
            var command = HoldemCommand.Act(view.SessionId, view.HandId, Guid.NewGuid(),
                view.ViewerSeat, view.SessionVersion, action);
            HoldemReceipt receipt = Apply(actor.Value, command);
            if (!receipt.Accepted) throw new InvalidOperationException("The opponent returned an illegal action.");
            return true;
        }

        private HoldemReceipt Apply(SeatId authorizedSeat, HoldemCommand command)
        {
            HoldemSnapshot before = session.GetSnapshot(authorizedSeat);
            HoldemReceipt receipt = session.Submit(authorizedSeat, command);
            if (receipt.Accepted && receipt.Version > before.SessionVersion)
            {
                long paid = command.Action.Kind == BettingActionKind.Call ? before.LegalActions.CallAmount
                    : command.Action.Kind == BettingActionKind.BetTo || command.Action.Kind == BettingActionKind.RaiseTo
                    ? command.Action.Target - before.OwnStreetContribution : 0;
                lastAction = new HoldemActionNotice(authorizedSeat, command.Action, paid);
            }
            return receipt;
        }

        private sealed class HumanPort : IHoldemPlayerPort
        {
            private readonly HoldemLocalTable table;
            public HumanPort(HoldemLocalTable table) { this.table = table; }
            public HoldemSnapshot Read() => table.session.GetSnapshot(table.humanSeat);
            public HoldemActionNotice LastAction => table.lastAction;
            public HoldemReceipt Submit(HoldemCommand command) => table.Apply(table.humanSeat, command);
            // The local host exposes one explicit rule, never a player-supplied payout or winner.
            public HoldemReceipt ResolvePendingSettlement(long expectedVersion) =>
                table.session.ResolvePendingSettlement(Guid.NewGuid(), expectedVersion, HoldemOddChipRule.ClockwiseFromButton);
            public HoldemReceipt NextHand(long expectedVersion)
            {
                HoldemReceipt receipt = table.session.StartNextHand(Guid.NewGuid(), Guid.NewGuid(), expectedVersion);
                if (receipt.Accepted) table.lastAction = null;
                return receipt;
            }
        }
    }
}
