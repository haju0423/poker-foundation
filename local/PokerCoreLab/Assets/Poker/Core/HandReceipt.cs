using System;

namespace Poker.Foundation
{
    public enum HandError
    {
        None, UnauthorizedSeat, WrongHand, CommandConflict, VersionMismatch,
        NotStarted, AlreadyStarted, Complete, SettlementRuleRequired,
        WrongPhase, WrongTurn, IllegalBet, CardNotOwned, Busy
    }

    /// <summary>Card-free acknowledgement. A retry carries the original version, never a current snapshot.</summary>
    public sealed class HandReceipt
    {
        internal HandReceipt(Guid handId, Guid commandId, SeatId? seat, long? appliedVersion, HandError error)
        { HandId = handId; CommandId = commandId; Seat = seat; AppliedVersion = appliedVersion; Error = error; }
        public Guid HandId { get; }
        public Guid CommandId { get; }
        /// <summary>Null for the authority-only Start operation.</summary>
        public SeatId? Seat { get; }
        public long? AppliedVersion { get; }
        public HandError Error { get; }
        public bool Accepted => Error == HandError.None;
    }
}
