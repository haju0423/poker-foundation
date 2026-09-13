using System;

namespace Poker.Foundation
{
    public enum ExchangeError
    {
        None,
        UnauthorizedSeat,
        WrongHand,
        CommandConflict,
        VersionMismatch,
        Complete,
        WrongTurn,
        CardNotOwned
    }

    /// <summary>Immutable acknowledgement, without hands, deck, or an actionable state snapshot.</summary>
    public sealed class ExchangeReceipt
    {
        internal ExchangeReceipt(ExchangeCommand command, long? appliedVersion, ExchangeError error)
        {
            HandId = command.HandId;
            CommandId = command.CommandId;
            Seat = command.Seat;
            AppliedVersion = appliedVersion;
            Error = error;
        }

        public Guid HandId { get; }
        public Guid CommandId { get; }
        public SeatId Seat { get; }
        /// <summary>Original applied version on success/retry; null on rejection. Never current state.</summary>
        public long? AppliedVersion { get; }
        public ExchangeError Error { get; }
        public bool Accepted => Error == ExchangeError.None;
    }
}
