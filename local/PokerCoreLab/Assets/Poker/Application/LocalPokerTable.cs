using System;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Trusted local host composition. The renderer receives a bound Human port, never this table.</summary>
    /// <remarks>
    /// Every nonhuman seat uses the supplied local opponent; this is not a multiplayer authority adapter.
    /// The caller keeps random alive for this table's lifetime and disposes it. Calls must be serialized.
    /// </remarks>
    public sealed class LocalPokerTable
    {
        private readonly PokerHandHost host;
        private readonly SeatId humanSeat;
        private readonly IPokerOpponent opponent;
        private Guid boundHandId;
        private bool operating;

        public LocalPokerTable(HandSetup setup, SeatId humanSeat, IRandomSource random)
            : this(setup, humanSeat, random, new RuleBasedDrawOpponent()) { }

        public LocalPokerTable(HandSetup setup, SeatId humanSeat, IRandomSource random, IPokerOpponent opponent)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            if (opponent == null) throw new ArgumentNullException(nameof(opponent));
            this.opponent = opponent;
            setup.StartingLedger.GetChips(humanSeat);
            this.humanSeat = humanSeat;
            host = new PokerHandHost(random);
            boundHandId = Guid.NewGuid();
            HandStartReceipt start = host.Start(HandStartRequest.First(boundHandId, Guid.NewGuid(), setup));
            if (!start.Accepted) throw new InvalidOperationException("Failed to start a new local hand.");
            Human = host.BindSeat(boundHandId, humanSeat);
        }
        public IPokerSeatPort Human { get; private set; }

        /// <summary>
        /// Trusted caller only. Supplies a complete next-hand setup, never infers carry/reset/button policies.
        /// A successful next hand replaces Human; previously returned ports stay bound to their original hand.
        /// Retry the same request, not newly generated IDs, after an uncertain start result.
        /// </summary>
        public HandStartReceipt StartNext(HandStartRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Kind != HandStartKind.Next) throw new ArgumentException("A next-hand request is required.", nameof(request));
            if (operating)
                return new HandStartReceipt(request.HandId, request.CommandId, null, HandStartError.Busy);
            // This local table keeps one human seat identity. Validate before changing the host.
            request.Setup.StartingLedger.GetChips(humanSeat);
            operating = true;
            try
            {
                HandStartReceipt receipt = host.Start(request);
                if (receipt.Accepted && host.CurrentHandId.Value != boundHandId)
                {
                    Guid current = host.CurrentHandId.Value;
                    IPokerSeatPort next = host.BindSeat(current, humanSeat);
                    Human = next; boundHandId = current;
                }
                return receipt;
            }
            finally { operating = false; }
        }

        /// <summary>
        /// At most one bot action per call. False is a guarded no-op when busy or when no bot can act.
        /// Timing belongs to the outer runtime, not the poker rules. The guard is not thread synchronization.
        /// </summary>
        public bool AdvanceOpponent()
        {
            if (operating) return false;
            operating = true;
            try
            {
                PokerPlayerView current = Human.Read();
                SeatId? actor = current.CurrentSeat;
                if (!actor.HasValue || actor == humanSeat) return false;
                IPokerSeatPort port = host.BindSeat(current.HandId, actor.Value);
                HandCommand command = opponent.Choose(port.Read());
                HandReceipt receipt = port.Submit(command);
                if (!receipt.Accepted) throw new InvalidOperationException("The test opponent produced an invalid action: " + receipt.Error);
                return true;
            }
            finally { operating = false; }
        }
    }
}
