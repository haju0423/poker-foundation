using System;
using Poker.Foundation;

namespace Poker.Application
{
    public static class HoldemPublicUtterancePacketReader
    {
        /// <summary>Validate before committing any related own-text or table display state.</summary>
        public static HoldemPublicUtterances Read(HoldemRoomPacket room, HoldemPublicUtterances previous = null)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            bool enabled = room.hasRules && room.rules != null && room.rules.publishesUtterances;
            if (room.hasPublicUtterances != (enabled && room.hasGame)) throw Invalid();
            if (!room.hasPublicUtterances)
            {
                if (previous != null) throw Invalid();
                return null;
            }
            var own = HoldemUtterancePacketReader.Read(room);
            var source = room.publicUtterances;
            if (own == null || !room.rules.receivesUtterances || source == null || source.entries == null
                || room.game.seats == null || room.game.seats.Length < 3 || room.game.seats.Length > 4
                || source.entries.Length > room.game.seats.Length * 96) throw Invalid();
            var history = source.hasHistoryOrder ? HoldemHistoryPacketReader.Read(room) : null;
            if (source.hasHistoryOrder && history == null) throw Invalid();
            if (previous != null && previous.HasHistoryOrder != source.hasHistoryOrder) throw Invalid();
            var roster = new bool[5];
            foreach (var seat in room.game.seats)
            {
                if (seat == null || seat.seat < 1 || seat.seat > 4) throw Invalid();
                roster[seat.seat] = seat.dealtIn;
            }
            var entries = new HoldemPublicUtterance[source.entries.Length];
            var counts = new int[5, 3];
            int previousStreet = -1, ownIndex = 0;
            long previousPosition = 2;
            for (int i = 0; i < entries.Length; i++)
            {
                var item = source.entries[i];
                if (item == null || item.seat < 1 || item.seat > 4 || !roster[item.seat]
                    || item.street < 0 || item.street > 2 || item.street < previousStreet || item.street > room.game.street
                    || string.IsNullOrWhiteSpace(item.text) || item.text.Length > own.MaximumTextLength
                    || !HoldemUtteranceInbox.ValidText(item.text) || ++counts[item.seat, item.street] > 32) throw Invalid();
                previousStreet = item.street;
                if (source.hasHistoryOrder)
                {
                    long position = item.historyPosition;
                    if (position < previousPosition || position > history.OmittedCount + history.Count) throw Invalid();
                    long offset = position - history.OmittedCount;
                    if (offset == history.Count && (item.street != room.game.street || room.game.currentSeat == 0)) throw Invalid();
                    if (offset > 0 && (int)history.GetEntry((int)offset - 1).Street > item.street) throw Invalid();
                    // Closing a betting round and advancing its street are one host operation.
                    if (offset >= 0 && offset < history.Count)
                    {
                        var next = history.GetEntry((int)offset);
                        if ((int)next.Street != item.street || next.Kind == HoldemHistoryKind.UncalledReturn) throw Invalid();
                    }
                    previousPosition = position;
                }
                else if (item.historyPosition != 0) throw Invalid();
                entries[i] = new HoldemPublicUtterance(new SeatId(item.seat), (HoldemStreet)item.street, item.text,
                    source.hasHistoryOrder ? item.historyPosition : (long?)null);
                if (item.seat == own.ViewerSeat.Value)
                {
                    if (ownIndex >= own.Count) throw Invalid();
                    var accepted = own.GetEntry(ownIndex++);
                    if (accepted.Street != entries[i].Street || accepted.Text != item.text) throw Invalid();
                }
            }
            if (ownIndex != own.Count) throw Invalid();
            if (previous != null)
            {
                if (previous.SessionId != own.SessionId) throw Invalid();
                if (previous.HandId == own.HandId)
                {
                    if (entries.Length < previous.Count) throw Invalid();
                    for (int i = 0; i < previous.Count; i++)
                    {
                        var before = previous.GetEntry(i); var after = entries[i];
                        if (before.Speaker != after.Speaker || before.Street != after.Street || before.Text != after.Text
                            || before.HistoryPosition != after.HistoryPosition)
                            throw Invalid();
                    }
                }
            }
            return new HoldemPublicUtterances(own.SessionId, own.HandId, entries, source.hasHistoryOrder);
        }

        private static ArgumentException Invalid() => new ArgumentException("Invalid public utterance projection.");
    }
}
