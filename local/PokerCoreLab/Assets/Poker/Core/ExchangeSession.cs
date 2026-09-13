using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Single-authority, hand-scoped owner of one exchange stage, not a full poker hand.</summary>
    /// <remarks>
    /// The caller must keep one instance per hand, resolve eligibility, authenticate seats, and
    /// serialize all calls. This is not thread-safe, durable, or a global HandId registry.
    /// State is authority-only; never send it wholesale to a client or player-controlled AI.
    /// </remarks>
    public sealed class ExchangeSession
    {
        private CommittedExchange committed;

        public ExchangeSession(Guid handId, InitialDeal deal, IReadOnlyList<SeatId> eligibleSeatsInOrder)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            HandId = handId;
            committed = new CommittedExchange(ExchangeRound.Begin(deal, eligibleSeatsInOrder), 0,
                new Dictionary<Guid, AcceptedExchange>());
        }

        public Guid HandId { get; }
        public ExchangeRound State => committed.State;
        public long Version => committed.Version;

        /// <summary>
        /// The trusted adapter resolves authorizedSeat from current session access, never from
        /// untrusted request data. Re-check it even on retries. No observer callbacks execute here.
        /// </summary>
        public ExchangeReceipt Submit(SeatId authorizedSeat, ExchangeCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            CommittedExchange before = committed;
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, ExchangeError.UnauthorizedSeat);
            if (command.HandId != HandId)
                return Reject(command, ExchangeError.WrongHand);
            if (before.Accepted.TryGetValue(command.CommandId, out AcceptedExchange previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, ExchangeError.CommandConflict);
            if (command.ExpectedVersion != before.Version)
                return Reject(command, ExchangeError.VersionMismatch);
            if (before.State.IsComplete)
                return Reject(command, ExchangeError.Complete);
            if (before.State.CurrentSeat != command.Seat)
                return Reject(command, ExchangeError.WrongTurn);

            SeatHand hand = before.State.GetHand(command.Seat);
            foreach (Card card in command.SelectedCards)
            {
                bool owned = false;
                for (int i = 0; i < hand.Count; i++) if (hand[i] == card) { owned = true; break; }
                if (!owned) return Reject(command, ExchangeError.CardNotOwned);
            }

            // Prepare every allocation and invariant check before the sole authority-state write.
            // Internal failures propagate; they are not disguised as ordinary rejected input.
            ExchangeRound nextState = before.State.Apply(command.Seat, command.SelectedCards);
            long nextVersion = checked(before.Version + 1);
            var receipt = new ExchangeReceipt(command, nextVersion, ExchangeError.None);
            var accepted = new Dictionary<Guid, AcceptedExchange>(before.Accepted);
            accepted.Add(command.CommandId, new AcceptedExchange(command, receipt));
            var next = new CommittedExchange(nextState, nextVersion, accepted);
            committed = next;
            return receipt;
        }

        private static ExchangeReceipt Reject(ExchangeCommand command, ExchangeError error)
            => new ExchangeReceipt(command, null, error);

        private sealed class AcceptedExchange
        {
            public AcceptedExchange(ExchangeCommand command, ExchangeReceipt receipt)
            { Command = command; Receipt = receipt; }
            public ExchangeCommand Command { get; }
            public ExchangeReceipt Receipt { get; }
        }

        // The dictionary never changes after publication. Only accepted commands are retained;
        // at most one per eligible seat (at most five in the current no-reuse profile).
        private sealed class CommittedExchange
        {
            public CommittedExchange(ExchangeRound state, long version, Dictionary<Guid, AcceptedExchange> accepted)
            { State = state; Version = version; Accepted = accepted; }
            public ExchangeRound State { get; }
            public long Version { get; }
            public Dictionary<Guid, AcceptedExchange> Accepted { get; }
        }
    }
}
