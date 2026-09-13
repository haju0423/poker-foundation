using System;
using System.Collections.Generic;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Trusted local lifecycle owner. Starts only caller-configured hands; supplies no table policy.</summary>
    /// <remarks>
    /// Serialize all host and bound-port calls on one authority. The start guard is not thread synchronization.
    /// Keep the injected random source alive for the host lifetime. This host does not own/dispose that source.
    /// Accepted start history lasts for this instance, not across processes. No authentication or durable storage.
    /// </remarks>
    public sealed class PokerHandHost
    {
        private readonly IRandomSource random;
        private readonly Dictionary<Guid, AcceptedStart> accepted = new Dictionary<Guid, AcceptedStart>();
        private readonly HashSet<Guid> usedHandIds = new HashSet<Guid>();
        private PokerHandSession current;
        private bool starting;

        public PokerHandHost(IRandomSource random)
        { this.random = random ?? throw new ArgumentNullException(nameof(random)); }

        public Guid? CurrentHandId => current?.HandId;
        public long CurrentVersion => current?.Version ?? 0;
        public HandPhase? CurrentPhase => current?.State.Phase;

        /// <summary>Authority only. A successful historical retry returns its original receipt without rewinding.</summary>
        public HandStartReceipt Start(HandStartRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (starting) return Reject(request, HandStartError.Busy);
            PokerHandSession before = current;
            if (accepted.TryGetValue(request.CommandId, out AcceptedStart prior))
                return prior.Request.HasSamePayload(request) ? prior.Receipt : Reject(request, HandStartError.CommandConflict);
            if (usedHandIds.Contains(request.HandId))
                return Reject(request, HandStartError.HandIdAlreadyUsed);
            if (request.Kind == HandStartKind.First)
            {
                if (before != null) return Reject(request, HandStartError.AlreadyHasCurrent);
            }
            else
            {
                if (before == null) return Reject(request, HandStartError.NoCurrent);
                if (request.PredecessorHandId != before.HandId)
                    return Reject(request, HandStartError.WrongPredecessorHand);
                if (request.PredecessorVersion != before.Version)
                    return Reject(request, HandStartError.VersionMismatch);
                if (before.State.Phase == HandPhase.AwaitingSettlementRule)
                    return Reject(request, HandStartError.SettlementRuleRequired);
                if (!before.State.IsComplete) return Reject(request, HandStartError.NotComplete);
            }

            starting = true;
            try
            {
                var session = new PokerHandSession(request.HandId, request.Setup, random);
                HandReceipt started = session.Start(new StartHandCommand(request.HandId, request.CommandId));
                if (!started.Accepted) throw new InvalidOperationException("A fresh candidate hand could not start.");
                var receipt = new HandStartReceipt(request.HandId, request.CommandId, started.AppliedVersion, HandStartError.None);
                var entry = new AcceptedStart(request, receipt);
                // These indexes belong only to this serialized authority. Do not copy all history per new hand.
                // No caller callbacks occur between recording the entries and publishing the candidate session.
                bool acceptedAdded = false, handIdAdded = false;
                try
                {
                    accepted.Add(request.CommandId, entry); acceptedAdded = true;
                    handIdAdded = usedHandIds.Add(request.HandId);
                    if (!handIdAdded) throw new InvalidOperationException("A validated hand ID was already recorded.");
                    current = session;
                    return receipt;
                }
                catch
                {
                    if (handIdAdded) usedHandIds.Remove(request.HandId);
                    if (acceptedAdded) accepted.Remove(request.CommandId);
                    throw;
                }
            }
            finally { starting = false; }
        }

        /// <summary>
        /// The caller resolves authenticated membership, not a request's seat field. The expected hand is mandatory.
        /// A returned port remains on that exact hand even after the host starts another one.
        /// </summary>
        public IPokerSeatPort BindSeat(Guid expectedHandId, SeatId authorizedSeat)
        {
            if (expectedHandId == Guid.Empty) throw new ArgumentException("An expected hand ID is required.", nameof(expectedHandId));
            if (!authorizedSeat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(authorizedSeat));
            PokerHandSession session = current;
            if (session == null) throw new InvalidOperationException("No hand has started.");
            if (session.HandId != expectedHandId) throw new InvalidOperationException("The expected hand is not current.");
            session.State.Ledger.GetChips(authorizedSeat);
            return new BoundSeatPort(session, authorizedSeat);
        }

        private static HandStartReceipt Reject(HandStartRequest request, HandStartError error)
            => new HandStartReceipt(request.HandId, request.CommandId, null, error);

        private sealed class BoundSeatPort : IPokerSeatPort
        {
            private readonly PokerHandSession session;
            private readonly SeatId seat;
            public BoundSeatPort(PokerHandSession session, SeatId seat) { this.session = session; this.seat = seat; }
            public PokerPlayerView Read() => PokerPlayerViewProjector.Create(session, seat);
            public HandReceipt Submit(HandCommand command) => session.Submit(seat, command);
        }

        private sealed class AcceptedStart
        {
            public AcceptedStart(HandStartRequest request, HandStartReceipt receipt) { Request = request; Receipt = receipt; }
            public HandStartRequest Request { get; }
            public HandStartReceipt Receipt { get; }
        }

    }
}
