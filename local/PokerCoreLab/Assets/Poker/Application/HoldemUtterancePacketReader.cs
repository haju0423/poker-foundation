using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>Immutable own-text projection from the same already-validated room/game packet.</summary>
    public static class HoldemUtterancePacketReader
    {
        public static HoldemUtteranceView Read(HoldemRoomPacket room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            if (!room.hasOwnUtterances) return null;
            var input = room.ownUtterances;
            if (room.protocol != HoldemRoomPacketMapper.ProtocolVersion || !room.hasGame || room.game == null || input == null || input.entries == null
                || input.entries.Length > 96 || room.viewerSeat < 1 || room.viewerSeat > 4
                || room.game.street < 0 || room.game.street > (int)HoldemStreet.Complete
                || input.maximumTextLength < 1 || input.maximumTextLength > 4096 || input.remaining < 0 || input.remaining > 32)
                throw Invalid();
            Guid session = Id(room.sessionId), hand = Id(room.game.handId);
            Guid window = Id(input.windowId, true);
            if (window == Guid.Empty && (input.canSubmit || input.remaining != 0)) throw Invalid();
            if (window != Guid.Empty && (room.game.street > (int)HoldemStreet.Turn || room.game.hasResult
                || room.game.isOver || room.game.dealPending || room.game.revealPending
                || room.game.settlementState == (int)HoldemSettlementState.AwaitingOddChipPriority
                || room.game.currentSeat == 0)) throw Invalid();
            if (input.canSubmit && (room.paused || input.backlogged || input.remaining == 0)) throw Invalid();
            HoldemSeatPacket viewer = null;
            if (room.game.seats != null)
                foreach (var seat in room.game.seats) if (seat != null && seat.seat == room.viewerSeat) viewer = seat;
            if (viewer == null || !viewer.viewer || input.canSubmit && (!viewer.dealtIn || viewer.status == (int)HoldemSeatStatus.Busted))
                throw Invalid();
            var commands = new HashSet<Guid>();
            var windows = new Dictionary<int, Guid>();
            var windowStreets = new Dictionary<Guid, int>();
            var streetCounts = new int[3];
            var entries = new HoldemUtteranceEntry[input.entries.Length];
            int previousStreet = -1;
            for (int i = 0; i < entries.Length; i++)
            {
                var item = input.entries[i];
                if (item == null || item.street < previousStreet || item.street < 0 || item.street > 2
                    || item.street > room.game.street || string.IsNullOrWhiteSpace(item.text)
                    || item.text.Length > input.maximumTextLength || !HoldemUtteranceInbox.ValidText(item.text)) throw Invalid();
                Guid command = Id(item.commandId), entryWindow = Id(item.windowId);
                if (!commands.Add(command) || ++streetCounts[item.street] > 32
                    || windows.TryGetValue(item.street, out var known) && known != entryWindow
                    || windowStreets.TryGetValue(entryWindow, out var knownStreet) && knownStreet != item.street
                    || item.street == room.game.street && window != Guid.Empty && window != entryWindow) throw Invalid();
                windows[item.street] = entryWindow; windowStreets[entryWindow] = item.street; previousStreet = item.street;
                var source = new HoldemUtteranceCommand(session, hand, entryWindow, command,
                    (HoldemStreet)item.street, new SeatId(room.viewerSeat), item.text);
                entries[i] = new HoldemUtteranceEntry(source, 0);
            }
            if (room.game.street <= (int)HoldemStreet.Turn && streetCounts[room.game.street] + input.remaining > 32)
                throw Invalid();
            return new HoldemUtteranceView(session, hand, (HoldemStreet)room.game.street, new SeatId(room.viewerSeat),
                window, input.canSubmit, input.maximumTextLength, input.remaining, entries, input.backlogged);
        }

        private static Guid Id(string text, bool allowEmpty = false)
        {
            if (text == null || text.Length != 32 || !Guid.TryParseExact(text, "N", out var id)
                || !allowEmpty && id == Guid.Empty) throw Invalid();
            return id;
        }
        private static ArgumentException Invalid() => new ArgumentException("Invalid own-text projection.");
    }
}
