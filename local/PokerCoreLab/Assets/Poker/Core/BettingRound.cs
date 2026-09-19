using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    /// <summary>One immutable No-Limit street. It owns a chip candidate, not a hand/session command history.</summary>
    public sealed class BettingRound
    {
        private struct Player
        {
            public SeatId Seat;
            public long Paid;
            public long LastFaced;
            public bool HasActed;
            public bool Folded;
        }

        private readonly Player[] players;
        private int currentIndex = -1;

        private BettingRound(ChipLedger ledger, Player[] ownedPlayers, long minimumBet,
            long currentBet, long lastFullRaise)
        {
            Ledger = ledger;
            players = ownedPlayers;
            MinimumBet = minimumBet;
            CurrentBet = currentBet;
            LastFullRaise = lastFullRaise;
        }

        public ChipLedger Ledger { get; private set; }
        public int SeatCount => players.Length;
        public int ActiveSeatCount
        {
            get { int count = 0; foreach (Player p in players) if (!p.Folded) count++; return count; }
        }
        public bool IsComplete => currentIndex < 0;
        public SeatId? CurrentSeat => IsComplete ? (SeatId?)null : players[currentIndex].Seat;
        /// <summary>Nominal opening bring-in while pending; actual highest street payment after completion.</summary>
        public long CurrentBet { get; private set; }
        public long MinimumBet { get; }
        public long LastFullRaise { get; private set; }
        public SeatId? RefundedSeat { get; private set; }
        public long RefundedAmount { get; private set; }

        /// <summary>
        /// Posts blinds on a candidate. Order starts at first action, with SB then BB last (also heads-up).
        /// Caller resolves button, eligibility and hand entry. No antes, reset or duplicate-StartHand protection.
        /// </summary>
        public static BettingRound BeginOpening(ChipLedger ledger, IReadOnlyList<SeatId> actionOrder,
            SeatId smallBlindSeat, SeatId bigBlindSeat, long smallBlind, long bigBlind)
        {
            if (bigBlind <= 0) throw new ArgumentOutOfRangeException(nameof(bigBlind));
            if (smallBlind <= 0 || smallBlind > bigBlind) throw new ArgumentOutOfRangeException(nameof(smallBlind));
            Player[] players = CopyPlayers(ledger, actionOrder);
            if (players.Length < 2 || players.Length != ledger.SeatCount || ledger.TotalCommitted != 0)
                throw new ArgumentException("An opening round requires all funded starting seats and no prior contributions.");
            int sb = players.Length - 2, bb = players.Length - 1;
            if (players[sb].Seat != smallBlindSeat || players[bb].Seat != bigBlindSeat)
                throw new ArgumentException("The final two positions must be the small and big blind seats.");
            foreach (Player p in players)
                if (ledger.GetChips(p.Seat).Stack == 0) throw new ArgumentException("Starting seats must have chips.");
            players[sb].Paid = Math.Min(smallBlind, ledger.GetChips(smallBlindSeat).Stack);
            players[bb].Paid = Math.Min(bigBlind, ledger.GetChips(bigBlindSeat).Stack);
            ChipLedger candidate = ledger.Contribute(smallBlindSeat, players[sb].Paid)
                .Contribute(bigBlindSeat, players[bb].Paid);
            var round = new BettingRound(candidate, players, bigBlind, bigBlind, bigBlind);
            round.Advance(0);
            return round;
        }

        /// <summary>
        /// Starts a zero-payment street using the caller's nonfolded order, including all-in seats.
        /// Preserves prior contributions; the caller controls when to advance to the next round.
        /// </summary>
        public static BettingRound BeginUnopened(ChipLedger ledger, IReadOnlyList<SeatId> activeOrder,
            long minimumBet)
        {
            if (minimumBet <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBet));
            var round = new BettingRound(ledger, CopyPlayers(ledger, activeOrder), minimumBet, 0, minimumBet);
            round.Advance(0);
            return round;
        }

        public SeatId GetSeatAt(int index)
        {
            if (index < 0 || index >= players.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return players[index].Seat;
        }
        public long GetStreetContribution(SeatId seat) => players[FindSeat(seat)].Paid;
        public bool IsFolded(SeatId seat) => players[FindSeat(seat)].Folded;
        public bool IsAllIn(SeatId seat)
        {
            int index = FindSeat(seat);
            return !players[index].Folded && Ledger.GetChips(seat).Stack == 0;
        }

        public LegalBettingActions GetLegalActions()
        {
            if (IsComplete) return new LegalBettingActions(false, false, 0, false, null, null);
            Player actor = players[currentIndex];
            long stack = Ledger.GetChips(actor.Seat).Stack;
            long needed = Math.Max(0, CallTarget(currentIndex) - actor.Paid);
            long ownMaximum = checked(actor.Paid + stack);
            long? minimum = null, maximum = null;
            if (HasSolventOpponent(currentIndex) && ownMaximum > CurrentBet &&
                (!actor.HasActed || CurrentBet - actor.LastFaced >= LastFullRaise))
            {
                // If a full raise would overflow, only a finite whole-stack short all-in can fit.
                minimum = LastFullRaise > long.MaxValue - CurrentBet
                    ? ownMaximum : Math.Min(checked(CurrentBet + LastFullRaise), ownMaximum);
                maximum = ownMaximum;
            }
            return new LegalBettingActions(true, needed == 0, Math.Min(needed, stack),
                CurrentBet == 0, minimum, maximum);
        }

        /// <summary>
        /// Validates using the same options exposed to callers, then returns a new whole street/chip candidate.
        /// Invalid gameplay is InvalidOperationException; malformed/null/unknown inputs remain argument errors.
        /// The caller must authenticate and deduplicate before accepting this candidate as current state.
        /// </summary>
        public BettingRound Apply(SeatId seat, BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            int index = FindSeat(seat);
            if (IsComplete || index != currentIndex) throw new InvalidOperationException("This seat cannot act now.");
            LegalBettingActions legal = GetLegalActions();
            if (!legal.Allows(action)) throw new InvalidOperationException("The action is not legal for this street state.");

            var next = new BettingRound(Ledger, (Player[])players.Clone(), MinimumBet, CurrentBet, LastFullRaise);
            Player actor = next.players[index];
            if (action.Kind == BettingActionKind.Fold) actor.Folded = true;
            else if (action.Kind == BettingActionKind.Call)
            {
                next.Ledger = Ledger.Contribute(seat, legal.CallAmount);
                actor.Paid = checked(actor.Paid + legal.CallAmount);
            }
            else if (action.Kind == BettingActionKind.BetTo || action.Kind == BettingActionKind.RaiseTo)
            {
                next.Ledger = Ledger.Contribute(seat, checked(action.Target - actor.Paid));
                actor.Paid = action.Target;
                long increase = checked(action.Target - CurrentBet);
                if (increase >= LastFullRaise) next.LastFullRaise = increase;
                next.CurrentBet = action.Target;
            }
            actor.HasActed = true;
            actor.LastFaced = next.CurrentBet;
            next.players[index] = actor;
            next.Advance((index + 1) % players.Length);
            return next;
        }

        private void Advance(int firstIndex)
        {
            int solventCount = 0, sole = -1;
            for (int i = 0; i < players.Length; i++)
                if (!players[i].Folded && Ledger.GetChips(players[i].Seat).Stack > 0) { solventCount++; sole = i; }
            if (ActiveSeatCount <= 1 || solventCount == 0) Finish();
            else if (solventCount == 1)
            {
                if (players[sole].Paid < CallTarget(sole)) currentIndex = sole;
                else Finish();
            }
            else
            {
                currentIndex = -1;
                for (int offset = 0; offset < players.Length; offset++)
                {
                    int i = (firstIndex + offset) % players.Length;
                    Player p = players[i];
                    if (!p.Folded && Ledger.GetChips(p.Seat).Stack > 0 && (!p.HasActed || p.Paid < CurrentBet))
                    { currentIndex = i; break; }
                }
                if (currentIndex < 0) Finish();
            }
            ValidateState();
        }

        private long CallTarget(int actorIndex)
        {
            if (HasSolventOpponent(actorIndex)) return CurrentBet;
            long actual = 0;
            for (int i = 0; i < players.Length; i++)
                if (i != actorIndex && !players[i].Folded) actual = Math.Max(actual, players[i].Paid);
            return actual;
        }

        private bool HasSolventOpponent(int actorIndex)
        {
            for (int i = 0; i < players.Length; i++)
                if (i != actorIndex && !players[i].Folded && Ledger.GetChips(players[i].Seat).Stack > 0) return true;
            return false;
        }

        private void Finish()
        {
            long highest = 0, second = 0;
            int owner = -1;
            for (int i = 0; i < players.Length; i++)
            {
                long paid = players[i].Paid; // Folded money still matches a wager; never omit it here.
                if (paid > highest) { second = highest; highest = paid; owner = i; }
                else if (paid > second) second = paid;
            }
            if (highest > second)
            {
                if (players[owner].Folded) throw new InvalidOperationException("A folded seat cannot own unmatched excess.");
                long refund = checked(highest - second);
                Ledger = Ledger.RefundContribution(players[owner].Seat, refund);
                players[owner].Paid = second;
                RefundedSeat = players[owner].Seat;
                RefundedAmount = refund;
            }
            CurrentBet = Math.Min(highest, second);
            currentIndex = -1;
        }

        private void ValidateState()
        {
            if (MinimumBet <= 0 || LastFullRaise < MinimumBet || CurrentBet < 0 || ActiveSeatCount == 0)
                throw new InvalidOperationException("Invalid betting state.");
            foreach (Player p in players)
                if (p.Paid < 0 || p.Paid > Ledger.GetChips(p.Seat).Committed || p.Paid > CurrentBet)
                    throw new InvalidOperationException("Street payments must agree with the chip ledger and target.");
        }

        private int FindSeat(SeatId seat)
        {
            if (!seat.IsValid) throw new ArgumentException("A valid seat is required.", nameof(seat));
            for (int i = 0; i < players.Length; i++) if (players[i].Seat == seat) return i;
            throw new KeyNotFoundException("The seat is not part of this betting round.");
        }

        private static Player[] CopyPlayers(ChipLedger ledger, IReadOnlyList<SeatId> order)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (order == null) throw new ArgumentNullException(nameof(order));
            int count = order.Count;
            if (count < 1 || count > ledger.SeatCount) throw new ArgumentException("Invalid number of round seats.", nameof(order));
            var result = new Player[count];
            var seen = new HashSet<SeatId>();
            for (int i = 0; i < count; i++)
            {
                SeatId seat = order[i];
                if (!seat.IsValid || !seen.Add(seat)) throw new ArgumentException("Seats must be valid and unique.", nameof(order));
                ledger.GetChips(seat);
                result[i] = new Player { Seat = seat };
            }
            return result;
        }

    }
}
