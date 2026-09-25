using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Serialized host intake, independent of betting revisions. Retains at most the current hand's three
    /// bounded windows. Call Synchronize after poker transitions; retain returned frozen batches if needed.
    /// This is not a network authenticator, broadcaster, AI interpreter or card-manipulation request queue.
    /// </summary>
    public sealed class HoldemUtteranceInbox
    {
        private readonly Func<HoldemSnapshot> readState;
        private readonly HoldemUtterancePolicy policy;
        private readonly Guid sessionId;
        private Guid handId;
        private readonly List<Window> windows = new List<Window>();
        private readonly Dictionary<Guid, Accepted> accepted = new Dictionary<Guid, Accepted>();
        private Window current;
        private long ordinal;

        public HoldemUtteranceInbox(Func<HoldemSnapshot> readState, HoldemUtterancePolicy policy)
        {
            this.readState = readState ?? throw new ArgumentNullException(nameof(readState));
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            var state = readState() ?? throw new InvalidOperationException("An active table snapshot is required.");
            sessionId = state.SessionId;
            Synchronize();
        }

        // The caller is the trusted host. Bind only the authenticated connection's assigned seat.
        public IHoldemUtterancePlayerPort Bind(SeatId authorizedSeat)
        {
            var state = Synchronize();
            if (!IsRosterSeat(state, authorizedSeat)) throw new ArgumentException("Unknown table seat.", nameof(authorizedSeat));
            return new SeatPort(this, authorizedSeat);
        }

        public HoldemSnapshot Synchronize()
        {
            var state = readState() ?? throw new InvalidOperationException("The table snapshot is unavailable.");
            if (state.SessionId != sessionId) throw new InvalidOperationException("An inbox cannot move between sessions.");
            if (state.HandId != handId)
            {
                CloseCurrent(); windows.Clear(); accepted.Clear(); ordinal = 0;
                handId = state.HandId; current = null;
            }
            bool open = IsBettingSpeechPhase(state);
            if (current != null && (current.Street != state.Street || !open)) CloseCurrent();
            if (open && current == null)
            {
                // A closed window may never reopen even if a caller supplies a regressed snapshot.
                bool knownStreet = false;
                foreach (var window in windows) if (window.Street == state.Street) knownStreet = true;
                if (!knownStreet)
                {
                    current = new Window(state.Street); windows.Add(current);
                }
            }
            return state;
        }

        public IReadOnlyList<HoldemUtteranceBatch> ReadClosedBatches()
        {
            Synchronize();
            var result = new List<HoldemUtteranceBatch>();
            foreach (var window in windows) if (window.Batch != null) result.Add(window.Batch);
            return result.AsReadOnly();
        }

        // Independent of dealer delivery/acknowledgement. Retain the current hand's accepted raw text.
        public HoldemPublicUtterances ReadPublicUtterances()
        {
            if (policy.Visibility != HoldemUtteranceVisibility.PublicRaw) return null;
            var state = Synchronize();
            var entries = new List<HoldemPublicUtterance>();
            foreach (var window in windows)
                foreach (var entry in window.Entries)
                    entries.Add(new HoldemPublicUtterance(entry.Speaker, entry.Street, entry.Text));
            return new HoldemPublicUtterances(state.SessionId, state.HandId, entries.ToArray());
        }

        private void CloseCurrent()
        {
            if (current == null) return;
            current.Batch = new HoldemUtteranceBatch(sessionId, handId, current.Id, current.Street, current.Entries.ToArray());
            current = null;
        }

        private HoldemUtteranceView Read(SeatId viewer)
        {
            var state = Synchronize();
            if (!IsRosterSeat(state, viewer)) throw new InvalidOperationException("The bound seat is no longer in the roster.");
            var own = new List<HoldemUtteranceEntry>();
            foreach (var window in windows)
                foreach (var entry in window.Entries) if (entry.Speaker == viewer) own.Add(entry);
            int remaining = current == null ? 0 : policy.MaximumPerSeatPerStreet - CountFor(current, viewer);
            bool canSubmit = current != null && remaining > 0 && policy.Allows(state.GetSeat(viewer));
            return new HoldemUtteranceView(state, viewer, current?.Id ?? Guid.Empty, canSubmit,
                policy.MaximumTextLength, remaining, own.ToArray());
        }

        private HoldemUtteranceReceipt Submit(SeatId authorizedSeat, HoldemUtteranceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            var state = Synchronize();
            if (command.SessionId != sessionId) return Reject(HoldemUtteranceError.WrongSession);
            if (command.HandId != handId) return Reject(HoldemUtteranceError.WrongHand);
            if (command.Speaker != authorizedSeat || !IsRosterSeat(state, authorizedSeat))
                return Reject(HoldemUtteranceError.UnauthorizedSeat);
            if (accepted.TryGetValue(command.CommandId, out var previous))
                return previous.Command.SamePayload(command) ? previous.Receipt : Reject(HoldemUtteranceError.CommandConflict);
            if (current == null || !IsBettingSpeechPhase(state)) return Reject(HoldemUtteranceError.WindowClosed);
            if (command.WindowId != current.Id || command.Street != current.Street) return Reject(HoldemUtteranceError.WrongWindow);
            if (!policy.Allows(state.GetSeat(authorizedSeat))) return Reject(HoldemUtteranceError.SeatNotAllowed);
            if (string.IsNullOrWhiteSpace(command.Text)) return Reject(HoldemUtteranceError.EmptyText);
            if (command.Text.Length > policy.MaximumTextLength) return Reject(HoldemUtteranceError.TextTooLong);
            if (!ValidText(command.Text)) return Reject(HoldemUtteranceError.InvalidText);
            if (CountFor(current, authorizedSeat) >= policy.MaximumPerSeatPerStreet)
                return Reject(HoldemUtteranceError.LimitReached);
            var receipt = new HoldemUtteranceReceipt(HoldemUtteranceError.None, command.CommandId, ++ordinal);
            current.Entries.Add(new HoldemUtteranceEntry(command, ordinal));
            accepted.Add(command.CommandId, new Accepted(command, receipt));
            return receipt;

            HoldemUtteranceReceipt Reject(HoldemUtteranceError error) => new HoldemUtteranceReceipt(error, command.CommandId);
        }

        internal static bool ValidText(string text) => HoldemPlayerText.IsValidSingleLine(text);

        private static bool IsRosterSeat(HoldemSnapshot state, SeatId seat)
        {
            for (int i = 0; i < state.SeatCount; i++) if (state.GetSeatAt(i).Seat == seat) return true;
            return false;
        }
        private static bool IsBettingSpeechPhase(HoldemSnapshot state) => state.Result == null
            && !state.IsOver && !state.IsSettlementPending && !state.IsDealPending && !state.IsRevealPending
            && state.Accusations == null && state.CurrentSeat.HasValue
            && (state.Street == HoldemStreet.Preflop || state.Street == HoldemStreet.Flop || state.Street == HoldemStreet.Turn);
        private static int CountFor(Window window, SeatId seat)
        {
            int result = 0;
            foreach (var entry in window.Entries) if (entry.Speaker == seat) result++;
            return result;
        }
        private sealed class Window
        {
            public Window(HoldemStreet street) { Street = street; Id = Guid.NewGuid(); }
            public readonly Guid Id;
            public readonly HoldemStreet Street;
            public readonly List<HoldemUtteranceEntry> Entries = new List<HoldemUtteranceEntry>();
            public HoldemUtteranceBatch Batch;
        }
        private sealed class Accepted
        {
            public Accepted(HoldemUtteranceCommand command, HoldemUtteranceReceipt receipt)
            { Command = command; Receipt = receipt; }
            public readonly HoldemUtteranceCommand Command;
            public readonly HoldemUtteranceReceipt Receipt;
        }
        private sealed class SeatPort : IHoldemUtterancePlayerPort
        {
            private readonly HoldemUtteranceInbox inbox;
            private readonly SeatId seat;
            public SeatPort(HoldemUtteranceInbox inbox, SeatId seat) { this.inbox = inbox; this.seat = seat; }
            public HoldemUtteranceView ReadUtterances() => inbox.Read(seat);
            public HoldemUtteranceReceipt SubmitUtterance(HoldemUtteranceCommand command) => inbox.Submit(seat, command);
        }
    }
}
