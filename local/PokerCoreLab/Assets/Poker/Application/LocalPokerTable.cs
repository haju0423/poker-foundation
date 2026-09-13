using System;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Local single-hand authority composition. The renderer receives Human, never this table.</summary>
    public sealed class LocalPokerTable
    {
        private readonly PokerHandSession session;
        private readonly SeatId humanSeat;
        private readonly IPokerOpponent opponent;

        public LocalPokerTable(HandSetup setup, SeatId humanSeat, IRandomSource random)
            : this(setup, humanSeat, random, new RuleBasedDrawOpponent()) { }

        public LocalPokerTable(HandSetup setup, SeatId humanSeat, IRandomSource random, IPokerOpponent opponent)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (opponent == null) throw new ArgumentNullException(nameof(opponent));
            this.opponent = opponent;
            setup.StartingLedger.GetChips(humanSeat);
            this.humanSeat = humanSeat;
            session = new PokerHandSession(Guid.NewGuid(), setup, random);
            HandReceipt start = session.Start(new StartHandCommand(session.HandId, Guid.NewGuid()));
            if (!start.Accepted) throw new InvalidOperationException("Failed to start a new local hand.");
            Human = new BoundSeatPort(session, humanSeat);
        }
        public IPokerSeatPort Human { get; }

        /// <summary>One bot action per call. Timing belongs to the outer runtime, not the poker rules.</summary>
        public bool AdvanceOpponent()
        {
            SeatId? actor = session.State.CurrentSeat;
            if (!actor.HasValue || actor == humanSeat) return false;
            PokerPlayerView view = PokerPlayerViewProjector.Create(session, actor.Value);
            HandReceipt receipt = session.Submit(actor.Value, opponent.Choose(view));
            if (!receipt.Accepted) throw new InvalidOperationException("The test opponent produced an invalid action: " + receipt.Error);
            return true;
        }

        private sealed class BoundSeatPort : IPokerSeatPort
        {
            private readonly PokerHandSession session;
            private readonly SeatId seat;
            public BoundSeatPort(PokerHandSession session, SeatId seat) { this.session = session; this.seat = seat; }
            public PokerPlayerView Read() => PokerPlayerViewProjector.Create(session, seat);
            public HandReceipt Submit(HandCommand command) => session.Submit(seat, command);
        }
    }
}
