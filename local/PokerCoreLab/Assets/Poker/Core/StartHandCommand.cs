using System;

namespace Poker.Foundation
{
    /// <summary>Authority-only lifecycle intent, routed by HandId. Setup is already frozen in the owner.</summary>
    public sealed class StartHandCommand
    {
        public StartHandCommand(Guid handId, Guid commandId)
        {
            if (handId == Guid.Empty) throw new ArgumentException("A hand ID is required.", nameof(handId));
            if (commandId == Guid.Empty) throw new ArgumentException("A command ID is required.", nameof(commandId));
            HandId = handId; CommandId = commandId;
        }
        public Guid HandId { get; }
        public Guid CommandId { get; }
    }
}
