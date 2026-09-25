using System;
using Poker.Application;
using Poker.Transport;

namespace Poker.Runtime
{
    public interface IHoldemRemoteUtteranceSource
    {
        IHoldemAsyncUtterancePort Utterances { get; }
    }

    /// <summary>Network intake. A pending send is not a rejection; keep its exact command until confirmed.</summary>
    public interface IHoldemAsyncUtterancePort : IHoldemUtterancePlayerPort
    {
        bool CanInteract { get; }
        /// <returns>True only when a correlated receipt is known; false means confirmation is pending.</returns>
        bool SendOrRetry(HoldemUtteranceCommand command, out HoldemUtteranceReceipt receipt);
        /// <summary>Reads a completed receipt for this exact command, without sending anything.</summary>
        bool TryReadReceipt(HoldemUtteranceCommand command, out HoldemUtteranceReceipt receipt);
    }

    /// <summary>One independent speech intent. The owning remote port is the only reader of socket responses.</summary>
    internal sealed class HoldemRemoteUtterancePort : IHoldemAsyncUtterancePort
    {
        private readonly Func<bool> ready;
        private readonly Func<HoldemWireRequest, string> send;
        private readonly Action changed;
        private readonly Func<long> observedRevision;
        private HoldemUtteranceView view;
        private HoldemUtteranceCommand pending, completed;
        private HoldemUtteranceReceipt lastReceipt;
        private HoldemWireRequest request;
        private long pauseRevision = -1;
        public bool Available => view != null;
        public bool CanInteract => Available && ready() && (pauseRevision < 0 || observedRevision() > pauseRevision);
        public bool HasPending => pending != null;

        internal HoldemRemoteUtterancePort(Func<bool> ready, Func<HoldemWireRequest, string> send,
            Action changed, Func<long> observedRevision)
        { this.ready = ready; this.send = send; this.changed = changed; this.observedRevision = observedRevision; }

        public HoldemUtteranceView ReadUtterances() => view ?? throw new InvalidOperationException("Speech intake is disabled.");

        // Synchronous callers retain their existing uncertain-delivery semantics.
        // The composer uses SendOrRetry, where false explicitly means an outstanding confirmation.
        public HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command)
        {
            if (SendOrRetry(command, out var receipt)) return receipt;
            throw new InvalidOperationException("Speech confirmation is pending.");
        }

        public bool SendOrRetry(HoldemUtteranceCommand command, out HoldemUtteranceReceipt receipt)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (TryReadReceipt(command, out receipt)) return true;
            if (pending != null && !Same(pending, command))
                throw new InvalidOperationException("Keep the outstanding speech command until its result is known.");
            if (!CanInteract) throw new InvalidOperationException("Speech transport is unavailable.");
            if (pending == null)
            {
                if (command.SessionId != view.SessionId || command.HandId != view.HandId || command.Speaker != view.ViewerSeat)
                    throw new ArgumentException("Speech belongs to another bound table.");
                pending = command; completed = null; lastReceipt = null;
                request = new HoldemWireRequest { type = "utterance", id = command.CommandId.ToString("N"),
                    sessionId = command.SessionId.ToString("N"), handId = command.HandId.ToString("N"),
                    windowId = command.WindowId.ToString("N"), street = (int)command.Street, text = command.Text };
            }
            pauseRevision = -1; // Explicit confirmation; automatic reconnect replay cannot clear this fence.
            send(request); // Even an uncertain enqueue keeps the exact intent; never replace its ID automatically.
            changed();
            receipt = null; return false;
        }

        public bool TryReadReceipt(HoldemUtteranceCommand command, out HoldemUtteranceReceipt receipt)
        {
            receipt = Same(completed, command) ? lastReceipt : null;
            return receipt != null;
        }

        internal bool Handle(HoldemWireResponse response)
        {
            if (pending == null || response.id != request.id
                || response.type != "utterance-receipt" && response.type != "rejected") return false;
            if (response.sessionId != request.sessionId) throw new ArgumentException("Uncorrelated speech response.");
            if (response.type == "utterance-receipt" && response.error == nameof(HoldemRoomError.Paused))
            {
                if (response.accepted || response.hasUtteranceReceipt || response.hasReceipt
                    || !string.IsNullOrEmpty(response.utteranceError))
                    throw new ArgumentException("Invalid paused speech confirmation.");
                // The room checks pause before inbox deduplication. This is not a verdict on
                // the original text, which an own-history update may still confirm.
                DeferForPause(observedRevision());
                return true;
            }
            if (response.type == "utterance-receipt" && response.hasUtteranceReceipt)
            {
                if (!Enum.TryParse(response.utteranceError, out HoldemUtteranceError error)
                    || !Enum.IsDefined(typeof(HoldemUtteranceError), error)
                    || response.accepted != (error == HoldemUtteranceError.None)
                    || response.error != (error == HoldemUtteranceError.None ? "None" : "UtteranceRejected"))
                    throw new ArgumentException("Invalid speech confirmation.");
                Complete(error);
            }
            else
            {
                if (response.accepted) throw new ArgumentException("Missing speech confirmation.");
                Complete(HoldemUtteranceError.DeliveryUnavailable);
            }
            return true;
        }

        internal void Observe(HoldemUtteranceView next)
        {
            // Accepted own history is append-only within a hand, even when revisions are skipped or a socket is replaced.
            if (view != null)
            {
                if (next == null || next.SessionId != view.SessionId || next.MaximumTextLength != view.MaximumTextLength)
                    throw new ArgumentException("Speech capability regressed.");
                if (next.HandId == view.HandId)
                {
                    if (next.Count < view.Count) throw new ArgumentException("Confirmed speech was removed.");
                    for (int i = 0; i < view.Count; i++)
                    {
                        var before = view.GetEntry(i); var after = next.GetEntry(i);
                        if (before.CommandId != after.CommandId || before.WindowId != after.WindowId
                            || before.Street != after.Street || before.Speaker != after.Speaker || before.Text != after.Text)
                            throw new ArgumentException("Confirmed speech was altered.");
                    }
                }
            }
            if (pending == null) { view = next; return; }
            if (next == null || next.SessionId != pending.SessionId || next.HandId != pending.HandId)
            {
                // The old hand's receipt may be lost. This is unknown, not a fabricated rejection or new submission.
                pending = null; request = null; completed = null; lastReceipt = null;
                pauseRevision = -1;
                view = next;
                return;
            }
            for (int i = 0; i < next.Count; i++)
            {
                var entry = next.GetEntry(i);
                if (entry.CommandId != pending.CommandId) continue;
                if (entry.Text != pending.Text || entry.WindowId != pending.WindowId || entry.Street != pending.Street
                    || entry.Speaker != pending.Speaker) throw new ArgumentException("Conflicting own-text confirmation.");
                Complete(HoldemUtteranceError.None); view = next; return;
            }
            if (next.WindowId != pending.WindowId || next.Street != pending.Street)
                Complete(HoldemUtteranceError.WindowClosed);
            view = next;
        }

        internal void RetryPending()
        {
            if (pending != null && pauseRevision < 0 && CanInteract) send(request);
        }

        internal void DeferForPause(long revision)
        { if (pending != null) pauseRevision = Math.Max(pauseRevision, revision); }

        private void Complete(HoldemUtteranceError error)
        {
            completed = pending;
            lastReceipt = HoldemUtteranceReceipt.Confirmation(pending.CommandId, error);
            pending = null; request = null; pauseRevision = -1;
        }

        private static bool Same(HoldemUtteranceCommand a, HoldemUtteranceCommand b) => a != null && b != null
            && a.SessionId == b.SessionId && a.HandId == b.HandId && a.WindowId == b.WindowId && a.CommandId == b.CommandId
            && a.Speaker == b.Speaker && a.Street == b.Street && a.Text == b.Text;
    }
}
