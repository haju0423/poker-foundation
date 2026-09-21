using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public interface IHoldemHistoryPort
    {
        HoldemHandHistory ReadHistory();
    }

    public enum HoldemHistoryKind { SmallBlind, BigBlind, Action, UncalledReturn }

    /// <summary>Public chip movements only. No cards, accusation choices or dealer evidence.</summary>
    public sealed class HoldemHistoryEntry
    {
        internal HoldemHistoryEntry(HoldemHistoryKind kind, HoldemStreet street, SeatId seat,
            BettingActionKind? action, long amount, long streetTotal, bool allIn)
        { Kind = kind; Street = street; Seat = seat; Action = action; Amount = amount; StreetTotal = streetTotal; IsAllIn = allIn; }
        public HoldemHistoryKind Kind { get; }
        public HoldemStreet Street { get; }
        public SeatId Seat { get; }
        public BettingActionKind? Action { get; }
        public long Amount { get; }
        public long StreetTotal { get; }
        public bool IsAllIn { get; }
    }

    /// <summary>Immutable copy of the current hand's history, safe to retain while play continues.</summary>
    public sealed class HoldemHandHistory
    {
        private readonly HoldemHistoryEntry[] entries;
        internal HoldemHandHistory(Guid sessionId, Guid handId, long handNumber, List<HoldemHistoryEntry> entries)
        { SessionId = sessionId; HandId = handId; HandNumber = handNumber; this.entries = entries.ToArray(); }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public long HandNumber { get; }
        public int Count => entries.Length;
        public HoldemHistoryEntry GetEntry(int index) => entries[index];
    }
}
