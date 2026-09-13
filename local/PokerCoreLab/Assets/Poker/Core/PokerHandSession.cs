using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>Single-authority owner of one whole hand, including automatic settlement and accepted-command retries.</summary>
    /// <remarks>
    /// One instance per hand; calls must be serialized. Not a global HandId registry, durable store, or authenticator.
    /// Setup and State are authority-only. No callback executes after commit. Keep player views separate.
    /// </remarks>
    public sealed class PokerHandSession
    {
        private readonly HandSetup setup;
        private readonly IRandomSource random;
        private CommittedHand committed;
        private bool processing;

        public PokerHandSession(Guid handId, HandSetup setup, IRandomSource random)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            HandId = handId;
        }

        public Guid HandId { get; }
        public long Version => committed?.Version ?? 0;
        /// <summary>Null before Start succeeds; otherwise immutable and authority-only.</summary>
        public PokerHandState State => committed?.State;

        /// <summary>Trusted lifecycle call, never a player-controlled command. Same accepted ID never reshuffles.</summary>
        public HandReceipt Start(StartHandCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            Guid commandId = command.CommandId;
            if (command.HandId != HandId) return Reject(command.HandId, commandId, null, HandError.WrongHand);
            if (processing) return Reject(HandId, commandId, null, HandError.Busy);
            if (committed != null)
            {
                if (committed.StartReceipt.CommandId == commandId) return committed.StartReceipt;
                return Reject(HandId, commandId, null, committed.Accepted.ContainsKey(commandId)
                    ? HandError.CommandConflict : HandError.AlreadyStarted);
            }
            processing = true;
            try
            {
                PokerHandState state = PokerHandState.Begin(setup, random);
                var receipt = new HandReceipt(HandId, commandId, null, 1, HandError.None);
                var next = new CommittedHand(state, 1, receipt, new Dictionary<Guid, AcceptedCommand>());
                committed = next;
                return receipt;
            }
            finally { processing = false; }
        }

        /// <summary>The adapter resolves authorizedSeat from access control, not from request data, even on retry.</summary>
        public HandReceipt Submit(SeatId authorizedSeat, HandCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat) return Reject(command, HandError.UnauthorizedSeat);
            if (command.HandId != HandId) return Reject(command, HandError.WrongHand);
            if (processing) return Reject(command, HandError.Busy);
            CommittedHand before = committed;
            if (before == null) return Reject(command, HandError.NotStarted);
            if (before.StartReceipt.CommandId == command.CommandId) return Reject(command, HandError.CommandConflict);
            if (before.Accepted.TryGetValue(command.CommandId, out AcceptedCommand previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt : Reject(command, HandError.CommandConflict);
            if (command.ExpectedVersion != before.Version) return Reject(command, HandError.VersionMismatch);
            if (before.State.IsComplete) return Reject(command, HandError.Complete);
            if (before.State.Phase == HandPhase.AwaitingSettlementRule) return Reject(command, HandError.SettlementRuleRequired);
            bool exchange = command.Kind == HandCommandKind.Exchange;
            if (exchange != (before.State.Phase == HandPhase.Exchange)) return Reject(command, HandError.WrongPhase);
            if (before.State.CurrentSeat != command.Seat) return Reject(command, HandError.WrongTurn);
            if (!exchange && !before.State.CurrentBetting.GetLegalActions().Allows(command.Action))
                return Reject(command, HandError.IllegalBet);
            if (exchange)
            {
                SeatHand hand = before.State.GetHand(command.Seat);
                foreach (Card card in command.SelectedCards)
                {
                    bool owned = false;
                    for (int i = 0; i < hand.Count; i++) if (hand[i] == card) { owned = true; break; }
                    if (!owned) return Reject(command, HandError.CardNotOwned);
                }
            }

            processing = true;
            try
            {
                PokerHandState state = exchange ? before.State.ApplyExchange(command.Seat, command.SelectedCards)
                    : before.State.ApplyBet(command.Seat, command.Action);
                long version = checked(before.Version + 1);
                var receipt = new HandReceipt(HandId, command.CommandId, command.Seat, version, HandError.None);
                var accepted = new Dictionary<Guid, AcceptedCommand>(before.Accepted);
                accepted.Add(command.CommandId, new AcceptedCommand(command, receipt));
                var next = new CommittedHand(state, version, before.StartReceipt, accepted);
                committed = next;
                return receipt;
            }
            finally { processing = false; }
        }

        private static HandReceipt Reject(HandCommand command, HandError error)
            => Reject(command.HandId, command.CommandId, command.Seat, error);
        private static HandReceipt Reject(Guid handId, Guid commandId, SeatId? seat, HandError error)
            => new HandReceipt(handId, commandId, seat, null, error);

        private sealed class AcceptedCommand
        {
            public AcceptedCommand(HandCommand command, HandReceipt receipt) { Command = command; Receipt = receipt; }
            public HandCommand Command { get; }
            public HandReceipt Receipt { get; }
        }

        // Nothing reachable here is mutated after publication. History is retained for this one hand's lifetime.
        private sealed class CommittedHand
        {
            public CommittedHand(PokerHandState state, long version, HandReceipt start, Dictionary<Guid, AcceptedCommand> accepted)
            { State = state; Version = version; StartReceipt = start; Accepted = accepted; }
            public PokerHandState State { get; }
            public long Version { get; }
            public HandReceipt StartReceipt { get; }
            public Dictionary<Guid, AcceptedCommand> Accepted { get; }
        }
    }
}
