using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Host-process capability. Never exposed as a player command endpoint.</summary>
    public interface IHoldemAccusationResolutionPort
    {
        HoldemRoomReceipt CloseAccusations(HoldemRoomAccusationClose input);
        HoldemRoomAccusationResolution ResolveAccusations();
    }

    /// <summary>No speaker identity or verdict is accepted from a client.</summary>
    public sealed class HoldemRoomAccusation : HoldemWindowCommand
    {
        public HoldemRoomAccusation(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street, SeatId? target)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street)
        {
            if (target.HasValue && !target.Value.IsValid) throw new ArgumentException("Invalid target.", nameof(target));
            Target = target;
        }
        public SeatId? Target { get; }
    }

    /// <summary>Host-process close request. No caller-supplied verdict.</summary>
    public sealed class HoldemRoomAccusationClose : HoldemWindowCommand
    {
        public HoldemRoomAccusationClose(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            long expectedVersion, HoldemStreet street)
            : base(sessionId, handId, windowId, commandId, expectedVersion, street) { }
    }

    public sealed class HoldemRoomAccusationResolution
    {
        internal HoldemRoomAccusationResolution(HoldemRoomError error, int recorded = 0, int pending = 0)
        { Error = error; RecordedCount = recorded; PendingCount = pending; }
        public HoldemRoomError Error { get; }
        public int RecordedCount { get; }
        public int PendingCount { get; }
    }

    public sealed partial class HoldemRoom
    {
        private readonly HoldemAccusationResolver accusationResolver;

        public HoldemRoomReceipt SubmitAccusation(Guid connection, HoldemRoomAccusation input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                long version = session.Version;
                var command = new HoldemAccusationChoiceCommand(input.SessionId, input.HandId, input.WindowId,
                    input.CommandId, input.ExpectedVersion, input.Street, member.Seat, input.Target);
                var receipt = session.SubmitAccusationChoice(member.Seat, command);
                if (receipt.Accepted && session.Version > version) revision++;
                return Wrap(receipt);
            }
        }

        public HoldemRoomReceipt CloseAccusations(Guid hostConnection, HoldemRoomAccusationClose input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return HostOperation(hostConnection, () => session.ProcessAccusationHostCommand(
                HoldemAccusationHostCommand.Close(input.SessionId, input.HandId, input.WindowId,
                    input.CommandId, input.ExpectedVersion, input.Street)));
        }

        /// <summary>
        /// Host-process only. Uses committed deal records, never client verdicts or AI intentions.
        /// Missing evidence remains pending. This cannot fold a player, transfer chips or resume betting.
        /// </summary>
        public HoldemRoomAccusationResolution ResolveAccusations(Guid hostConnection)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out _);
                if (error != HoldemRoomError.None) return new HoldemRoomAccusationResolution(error);
                if (accusationResolver == null) return new HoldemRoomAccusationResolution(HoldemRoomError.AccusationsDisabled);
                int recorded = 0;
                foreach (var claim in session.GetPendingAccusations())
                    if (accusationResolver.Resolve(claim.ClaimId) == HoldemEvidenceResolution.Recorded) recorded++;
                if (recorded > 0) revision++;
                return new HoldemRoomAccusationResolution(HoldemRoomError.None, recorded, session.GetPendingAccusations().Count);
            }
        }
    }
}
