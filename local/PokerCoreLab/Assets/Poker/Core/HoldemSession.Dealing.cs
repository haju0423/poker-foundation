using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public sealed partial class HoldemSession
    {
        private sealed class AcceptedDeal
        {
            public AcceptedDeal(HoldemDealCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemDealCommand Command { get; }
            public HoldemReceipt Receipt { get; }
        }

        private readonly Dictionary<Guid, AcceptedDeal> acceptedDeals = new Dictionary<Guid, AcceptedDeal>();
        private HoldemDealWindow pendingDeal;

        public HoldemDealWindow PendingDeal => pendingDeal;

        /// <summary>
        /// Serialized host path only. A missing/failed interpretation may take this neutral path;
        /// no manipulation evidence or accusation verdict is fabricated here.
        /// </summary>
        public HoldemReceipt DealUnchanged(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand);
            if (processing) return Reject(command, HoldemCommandError.Busy);
            if (acceptedDeals.TryGetValue(command.CommandId, out AcceptedDeal previous))
                return previous.Command.HasSamePayload(command) ? previous.Receipt
                    : Reject(command, HoldemCommandError.CommandConflict);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch);
            if (pendingDeal == null || !currentHand.IsDealPending)
                return Reject(command, HoldemCommandError.DealNotPending);
            if (command.WindowId != pendingDeal.WindowId || command.Street != pendingDeal.Street)
                return Reject(command, HoldemCommandError.WrongDealWindow);

            processing = true;
            try
            {
                long nextVersion = checked(Version + 1);
                HoldemHand candidate = currentHand.DealUnchanged(command.Street);
                ChipLedger candidateLedger = candidate.IsComplete ? MergeSettled(candidate) : settledLedger;
                HoldemDealWindow nextDeal = DealWindowFor(candidate);
                HoldemAccusationWindow nextAccusations = AccusationsFor(candidate);
                var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId,
                    null, nextVersion, HoldemCommandError.None);
                currentHand = candidate; pendingDeal = nextDeal; accusations = nextAccusations;
                settledLedger = candidateLedger; Version = nextVersion;
                acceptedDeals.Add(command.CommandId, new AcceptedDeal(command, receipt));
                usedCommandIds.Add(command.CommandId);
                return receipt;
            }
            finally { processing = false; }
        }

        private HoldemDealWindow DealWindowFor(HoldemHand candidate)
        {
            if (!candidate.IsDealPending) return null;
            return pendingDeal != null && pendingDeal.HandId == candidate.HandId && pendingDeal.Street == candidate.PendingDealStreet
                ? pendingDeal : new HoldemDealWindow(SessionId, candidate);
        }

        private HoldemReceipt Reject(HoldemDealCommand command, HoldemCommandError error)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, null, null, error);
    }
}
