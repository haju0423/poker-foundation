using System;

namespace Poker.Application
{
    public enum HandStartError
    {
        None, Busy, AlreadyHasCurrent, NoCurrent, WrongPredecessorHand, VersionMismatch,
        NotComplete, SettlementRuleRequired, CommandConflict, HandIdAlreadyUsed
    }

    /// <summary>Card-free acknowledgement of a trusted start request, not a current-hand snapshot.</summary>
    public sealed class HandStartReceipt
    {
        internal HandStartReceipt(Guid handId, Guid commandId, long? appliedVersion, HandStartError error)
        { HandId = handId; CommandId = commandId; AppliedVersion = appliedVersion; Error = error; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        public long? AppliedVersion { get; }
        public HandStartError Error { get; }
        public bool Accepted => Error == HandStartError.None;
    }
}
