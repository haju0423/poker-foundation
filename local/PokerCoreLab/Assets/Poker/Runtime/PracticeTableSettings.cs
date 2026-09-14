using System;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;

namespace Poker.Runtime
{
    /// <summary>Explicit practice fixture, not approved team rules or a multi-hand tournament configuration.</summary>
    [CreateAssetMenu(menuName = "Poker/Practice Table Settings")]
    public sealed class PracticeTableSettings : ScriptableObject
    {
        public int humanSeat = 1;
        public long startingStack = 100;
        public long smallBlind = 1;
        public long bigBlind = 2;
        public int[] dealOrder = { 1, 2 };
        public int[] openingOrder = { 1, 2 };
        public int[] exchangeOrder = { 2, 1 };
        public int[] closingOrder = { 2, 1 };
        [Min(0.1f)] public float opponentDelaySeconds = 0.7f;

        public HandSetup CreateSetup()
        {
            if (dealOrder == null) throw new InvalidOperationException("Practice seats are required.");
            // This first screen deliberately supports exactly two seats, not a hidden multiplayer player limit.
            if (dealOrder.Length != 2)
                throw new InvalidOperationException("This first practice screen requires exactly two seats.");
            var seats = new SeatChips[dealOrder.Length];
            for (int i = 0; i < seats.Length; i++) seats[i] = new SeatChips(new SeatId(dealOrder[i]), startingStack);
            ChipLedger ledger = ChipLedger.Create(seats);
            ledger.GetChips(new SeatId(humanSeat));
            return new HandSetup(ledger, Convert(dealOrder), Convert(openingOrder), Convert(exchangeOrder), Convert(closingOrder),
                smallBlind, bigBlind); // No odd-chip default; the team still owns that decision.
        }
        /// <summary>Carry final settled stacks only. Preserve explicit fixture orders; no rotation or rebuy policy.</summary>
        public HandSetup CreateContinuationSetup(PokerHandResultView result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            HandSetup configured = CreateSetup();
            if (result.Seats.Count != configured.DealOrder.Count)
                throw new InvalidOperationException("The practice participants cannot change between hands.");
            var seats = new SeatChips[result.Seats.Count];
            for (int i = 0; i < seats.Length; i++)
            {
                var previous = result.Seats[i];
                if (previous.Seat != configured.DealOrder[i])
                    throw new InvalidOperationException("The practice participants cannot change between hands.");
                if (previous.FinalStack <= 0)
                    throw new InvalidOperationException("An unfunded participant requires an explicit new practice, not an automatic rebuy.");
                seats[i] = new SeatChips(previous.Seat, previous.FinalStack);
            }
            return new HandSetup(ChipLedger.Create(seats), configured.DealOrder, configured.OpeningOrder,
                configured.ExchangeOrder, configured.ClosingOrder, configured.SmallBlind, configured.BigBlind);
        }
        private static SeatId[] Convert(int[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new SeatId[values.Length];
            for (int i = 0; i < result.Length; i++) result[i] = new SeatId(values[i]);
            return result;
        }
    }
}
