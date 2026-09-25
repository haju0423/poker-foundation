using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Seat-free transport input. Only the room can assign the authenticated speaker.</summary>
    public sealed class HoldemRoomUtterance
    {
        public HoldemRoomUtterance(Guid sessionId, Guid handId, Guid windowId, Guid commandId,
            HoldemStreet street, string text)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || windowId == Guid.Empty || commandId == Guid.Empty)
                throw new ArgumentException("Session, hand, window and command IDs are required.");
            if (street != HoldemStreet.Preflop && street != HoldemStreet.Flop && street != HoldemStreet.Turn)
                throw new ArgumentOutOfRangeException(nameof(street));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            SessionId = sessionId; HandId = handId; WindowId = windowId; CommandId = commandId; Street = street;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public Guid CommandId { get; }
        public HoldemStreet Street { get; }
        public string Text { get; }
    }

    public sealed class HoldemRoomUtteranceReceipt
    {
        internal HoldemRoomUtteranceReceipt(HoldemRoomError error, HoldemUtteranceReceipt utterance = null)
        { Error = error; Utterance = utterance; }
        public HoldemRoomError Error { get; }
        public HoldemUtteranceReceipt Utterance { get; }
        public bool Accepted => Error == HoldemRoomError.None && Utterance != null && Utterance.Accepted;
    }

    public sealed partial class HoldemRoom
    {
        private readonly HoldemUtterancePolicy utterancePolicy;
        private HoldemUtteranceInbox utterances;
        private Guid utteranceHandId;
        private long lastUtteranceOrdinal;
        private readonly List<HoldemPublicUtterance> publicRemarks = new List<HoldemPublicUtterance>();
        // Technical backpressure only. An absent consumer never pauses the poker game.
        public const int MaximumPendingUtteranceBatches = 16;
        private readonly List<HoldemUtteranceBatch> pendingUtteranceBatches = new List<HoldemUtteranceBatch>();
        private readonly HashSet<Guid> capturedUtteranceWindows = new HashSet<Guid>();
        private bool RetainsDealerInput => utterancePolicy != null
            && utterancePolicy.BatchRetention == HoldemUtteranceBatchRetention.UntilHostAcknowledges;

        /// <summary>Optional own-text confirmation. Null before the first hand or when disabled.</summary>
        public HoldemUtteranceView ReadUtterances(Guid connection)
        {
            lock (gate)
            {
                if (FindConnected(connection, out var member) != HoldemRoomError.None)
                    throw new InvalidOperationException("The connection has no active room binding.");
                if (utterances == null) return null;
                var view = utterances.Bind(member.Seat).ReadUtterances();
                if (!IsPaused() && pendingUtteranceBatches.Count < MaximumPendingUtteranceBatches) return view;
                var entries = new HoldemUtteranceEntry[view.Count];
                for (int i = 0; i < entries.Length; i++) entries[i] = view.GetEntry(i);
                return new HoldemUtteranceView(session.GetSnapshot(member.Seat), member.Seat, view.WindowId,
                    false, view.MaximumTextLength, view.Remaining, entries,
                    pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches);
            }
        }

        public HoldemRoomUtteranceReceipt SubmitUtterance(Guid connection, HoldemRoomUtterance input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return new HoldemRoomUtteranceReceipt(error);
                if (utterances == null)
                    return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(utterancePolicy == null ? HoldemUtteranceError.Disabled
                            : HoldemUtteranceError.WindowClosed, input.CommandId));
                var command = new HoldemUtteranceCommand(input.SessionId, input.HandId, input.WindowId,
                    input.CommandId, input.Street, member.Seat, input.Text);
                var port = utterances.Bind(member.Seat);
                if (pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches)
                {
                    bool retry = false;
                    var view = port.ReadUtterances();
                    for (int i = 0; i < view.Count; i++)
                        if (view.GetEntry(i).CommandId == input.CommandId) { retry = true; break; }
                    if (!retry) return new HoldemRoomUtteranceReceipt(HoldemRoomError.UtteranceRejected,
                        new HoldemUtteranceReceipt(HoldemUtteranceError.BacklogFull, input.CommandId));
                }
                var receipt = port.SubmitUtterance(command);
                if (receipt.Accepted && receipt.Ordinal > lastUtteranceOrdinal)
                {
                    if (utterancePolicy.Visibility == HoldemUtteranceVisibility.PublicRaw)
                        publicRemarks.Add(new HoldemPublicUtterance(member.Seat, input.Street, input.Text,
                            omittedHistoryCount + history.Count));
                    lastUtteranceOrdinal = receipt.Ordinal; revision++;
                }
                return new HoldemRoomUtteranceReceipt(receipt.Accepted ? HoldemRoomError.None
                    : HoldemRoomError.UtteranceRejected, receipt);
            }
        }

        /// <summary>
        /// Trusted server-side consumer only; never expose this through player packets.
        /// Nonempty batches survive hand transitions until the consumer acknowledges their WindowId.
        /// Repeated reads return the same batches: consumers must deduplicate by SessionId and WindowId.
        /// </summary>
        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedUtteranceBatches(Guid hostConnection)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can read closed input batches.");
                return Array.AsReadOnly(pendingUtteranceBatches.ToArray());
            }
        }

        /// <summary>Acknowledge retained raw input, not AI interpretation or manipulation success.</summary>
        public bool AcknowledgeUtteranceBatch(Guid hostConnection, Guid windowId)
        {
            lock (gate)
            {
                if (FindConnected(hostConnection, out var member) != HoldemRoomError.None || member != members[0])
                    throw new InvalidOperationException("Only the connected host can acknowledge input batches.");
                int index = pendingUtteranceBatches.FindIndex(batch => batch.WindowId == windowId);
                if (index < 0) return false;
                bool wasFull = pendingUtteranceBatches.Count >= MaximumPendingUtteranceBatches;
                pendingUtteranceBatches.RemoveAt(index);
                // Routine private consumption has no public event; only restored input availability changes the view.
                if (wasFull) revision++;
                return true;
            }
        }

        // Called under the room gate, like betting and initial remark acceptance.
        private HoldemPublicUtterances ReadPublicRemarks(HoldemSnapshot game)
        {
            if (game == null || utterancePolicy == null || utterancePolicy.Visibility != HoldemUtteranceVisibility.PublicRaw)
                return null;
            return new HoldemPublicUtterances(game.SessionId, game.HandId, publicRemarks.ToArray(), true);
        }

        private void SynchronizeUtterances()
        {
            if (utterancePolicy == null || !session.CurrentHandId.HasValue) return;
            if (utteranceHandId != session.CurrentHandId.Value)
            {
                utteranceHandId = session.CurrentHandId.Value; lastUtteranceOrdinal = 0;
                publicRemarks.Clear();
                capturedUtteranceWindows.Clear();
            }
            if (utterances == null)
                utterances = new HoldemUtteranceInbox(() => session.GetSnapshot(members[0].Seat), utterancePolicy);
            else utterances.Synchronize();
            if (!RetainsDealerInput) return;
            foreach (var batch in utterances.ReadClosedBatches())
                if (capturedUtteranceWindows.Add(batch.WindowId) && batch.Count > 0)
                    pendingUtteranceBatches.Add(batch);
        }
    }
}
