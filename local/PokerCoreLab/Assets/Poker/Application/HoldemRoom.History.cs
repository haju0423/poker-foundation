using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemRoom
    {
        // A transport/display bound, not a limit on legal bets or the length of a hand.
        public const int MaximumHistoryEntries = 64;
        private readonly List<HoldemHistoryEntry> history = new List<HoldemHistoryEntry>();
        private long omittedHistoryCount;
        private HoldemHandHistory historySnapshot;

        private void StartHistory(HoldemSnapshot previous)
        {
            history.Clear(); omittedHistoryCount = 0; historySnapshot = null;
            var current = session.GetSnapshot(members[0].Seat);
            AddBlind(current.SmallBlindSeat, HoldemHistoryKind.SmallBlind, rules.SmallBlind);
            AddBlind(current.BigBlindSeat, HoldemHistoryKind.BigBlind, rules.BigBlind);
            for (int i = 0; i < current.SeatCount; i++)
            {
                var seat = current.GetSeatAt(i);
                long initial = previous == null ? rules.StartingStack : previous.GetSeat(seat.Seat).Stack;
                long paid = seat.IsSmallBlind ? System.Math.Min(initial, rules.SmallBlind)
                    : seat.IsBigBlind ? System.Math.Min(initial, rules.BigBlind) : 0;
                AddHistoryReturn(HoldemStreet.Preflop, seat.Seat, seat.Stack - initial + paid - seat.Awarded);
            }

            void AddBlind(SeatId seat, HoldemHistoryKind kind, long blind)
            {
                long stack = previous == null ? rules.StartingStack : previous.GetSeat(seat).Stack;
                long paid = System.Math.Min(stack, blind);
                AppendHistory(new HoldemHistoryEntry(kind, HoldemStreet.Preflop, seat, null, paid, paid, paid == stack));
            }
        }

        private void RecordHistoryReturns(HoldemSnapshot before, HoldemSnapshot after, SeatId? actor, long paid)
        {
            for (int i = 0; i < after.SeatCount; i++)
            {
                var seat = after.GetSeatAt(i); var old = before.GetSeat(seat.Seat);
                long returned = seat.Stack - old.Stack + (seat.Seat == actor ? paid : 0) - (seat.Awarded - old.Awarded);
                AddHistoryReturn(before.Street, seat.Seat, returned);
            }
        }

        private void AddHistoryReturn(HoldemStreet street, SeatId seat, long amount)
        {
            if (amount > 0) AppendHistory(new HoldemHistoryEntry(HoldemHistoryKind.UncalledReturn, street, seat, null, amount, 0, false));
        }

        private void AppendHistory(HoldemHistoryEntry entry)
        {
            if (history.Count == MaximumHistoryEntries) { history.RemoveAt(0); omittedHistoryCount++; }
            history.Add(entry); historySnapshot = null;
        }

        private HoldemHandHistory ReadHistory(HoldemSnapshot game)
        {
            if (game == null) return null;
            return historySnapshot ?? (historySnapshot = new HoldemHandHistory(game.SessionId, game.HandId,
                game.HandNumber, history, omittedHistoryCount));
        }
    }
}
