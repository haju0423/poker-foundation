using System;
using System.Collections.Generic;
using Poker.Application;

namespace Poker.Presentation
{
    // Structural and visibility checks before drawing. This does not re-run game rules or calculate a winner.
    internal static class HoldemPacketDisplayValidation
    {
        internal static void Validate(HoldemRoomPacket p)
        {
            Require(p != null && p.protocol == HoldemRoomPacketMapper.ProtocolVersion && p.hasGame);
            Require(Id(p.sessionId) && p.viewerSeat >= 1 && p.viewerSeat <= 4 && p.revision >= 0);
            var g = p.game;
            Require(g != null && Id(g.handId) && g.version >= 0 && g.handNumber >= 1 && g.pot >= 0);
            Require(g.street >= 0 && g.street <= 4 && g.settlementState >= 0 && g.settlementState <= 2);
            Require(g.board != null && (g.board.Length == 0 || g.board.Length >= 3 && g.board.Length <= 5));
            Require(g.seats != null && g.seats.Length >= 2 && g.seats.Length <= 4 && p.members != null && p.members.Length == g.seats.Length);
            int capacity = HoldemRoom.Capacity;
            if (p.hasRules)
            {
                Require(p.rules != null);
                capacity = p.rules.seatCapacity == 0 ? HoldemRoom.Capacity : p.rules.seatCapacity;
                HoldemRoomRules.ValidateSeatCapacity(capacity);
            }
            Require(g.seats.Length == capacity);
            var cards = new HashSet<int>(); foreach (int card in g.board) Require(Card(card) && cards.Add(card));
            if (p.hasOwnCardChange)
            {
                Require(p.hasRules && p.rules.waitsForHostDeal && p.rules.pausesAfterReveal
                    && p.rules.receivesUtterances && p.hasOwnUtterances && p.ownUtterances != null);
                var own = p.ownCardChange;
                Require(own != null && Id(own.dealWindowId) && own.handId == g.handId
                    && own.street == g.street && g.revealPending && !g.dealPending && !g.hasResult);
                int first = g.street == 1 ? 0 : g.street == 2 ? 3 : 4;
                int last = g.street == 1 ? 2 : first;
                Require(g.street >= 1 && g.street <= 3 && own.boardIndex >= first && own.boardIndex <= last
                    && own.boardIndex < g.board.Length && own.card == g.board[own.boardIndex]);
            }
            var seatIds = new HashSet<int>(); var indices = new HashSet<int>(); bool foundViewer = false;
            foreach (var s in g.seats)
            {
                Require(s != null && s.seat >= 1 && s.seat <= capacity && seatIds.Add(s.seat));
                Require(s.tableIndex >= 0 && s.tableIndex < g.seats.Length && indices.Add(s.tableIndex));
                Require(s.viewer == (s.seat == p.viewerSeat)); foundViewer |= s.viewer;
                Require(s.status >= 0 && s.status <= 3 && s.stack >= 0 && s.committed >= 0 && s.streetContribution >= 0 && s.awarded >= 0);
                Require(s.visibleCards != null && (s.visibleCards.Length == 0 || s.visibleCards.Length == 2));
                Require(s.revealedBestCards != null && (s.revealedBestCards.Length == 0 || s.revealedBestCards.Length == 5));
                bool publicBest = s.revealedBestCards.Length == 5;
                Require(s.visibleCards.Length == (s.dealtIn && (s.viewer || publicBest) ? 2 : 0));
                Require(!publicBest || s.dealtIn && s.status != 2 && (g.settlementState == 1 || g.hasResult && g.result != null && g.result.kind == 2));
                var available = new HashSet<int>(g.board);
                foreach (int card in s.visibleCards) { Require(Card(card) && cards.Add(card)); available.Add(card); }
                var best = new HashSet<int>(); foreach (int card in s.revealedBestCards) Require(Card(card) && best.Add(card) && available.Contains(card));
            }
            Require(foundViewer && (g.currentSeat == 0 || seatIds.Contains(g.currentSeat)));
            ValidateAccusations(p);
            Require(seatIds.Contains(g.button) && seatIds.Contains(g.smallBlind) && seatIds.Contains(g.bigBlind)
                && (g.sessionWinner == 0 || seatIds.Contains(g.sessionWinner)));
            if (p.hasRules) ValidateChipTotal(p, capacity);
            var names = new HashSet<int>();
            foreach (var member in p.members)
            {
                Require(member != null && seatIds.Contains(member.seat) && names.Add(member.seat)
                    && !string.IsNullOrWhiteSpace(member.name) && member.name.Length <= 24);
                Require(HoldemPlayerText.IsValidSingleLine(member.name));
            }
            if (g.hasLegal)
            {
                var l = g.legal;
                Require(l != null && g.currentSeat == p.viewerSeat && !g.hasResult && !g.dealPending && !g.revealPending);
                Require(l.callAmount >= 0 && l.call == (l.callAmount > 0) && !(l.check && l.call) && !(l.bet && l.raise));
                Require(!(l.bet || l.raise) || l.minimumTarget > 0 && l.maximumTarget >= l.minimumTarget);
            }
            if (g.dealPending) Require(Id(g.dealWindowId) && g.dealStreet >= 1 && g.dealStreet <= 3);
            if (g.hasResult)
            {
                var r = g.result;
                Require(r != null && (r.kind == 1 || r.kind == 2) && r.pot >= 0 && (r.winnerSeat == 0 || seatIds.Contains(r.winnerSeat))
                    && (r.foldedSeat == 0 || seatIds.Contains(r.foldedSeat)));
                Require(r.pots != null && r.pots.Length >= 1 && r.pots.Length <= capacity);
                foreach (var pot in r.pots)
                {
                    Require(pot != null && pot.payouts != null && pot.payouts.Length >= 1 && pot.payouts.Length <= capacity);
                    Require(pot.eligibleSeats != null && pot.eligibleSeats.Length >= 1 && pot.eligibleSeats.Length <= capacity);
                    var eligible = new HashSet<int>();
                    foreach (int seat in pot.eligibleSeats) Require(seatIds.Contains(seat) && eligible.Add(seat));
                    var paid = new HashSet<int>();
                    foreach (var payout in pot.payouts) Require(payout != null && eligible.Contains(payout.seat)
                        && paid.Add(payout.seat) && payout.amount >= 0);
                }
            }
            if (p.hasLastAction)
                ValidateAction(p.lastAction, seatIds);
            if (g.hasStreetActions)
            {
                Require(g.streetActions != null && g.streetActions.Length <= g.seats.Length);
                var actingSeats = new HashSet<int>();
                foreach (var action in g.streetActions)
                {
                    ValidateAction(action, seatIds);
                    Require(action.street == g.street && actingSeats.Add(action.seat));
                }
                if (g.streetActions.Length > 0 || p.hasLastAction && p.lastAction.street == g.street)
                {
                    Require(p.hasLastAction && p.lastAction.street == g.street);
                    HoldemActionPacket latest = null;
                    foreach (var action in g.streetActions) if (action.seat == p.lastAction.seat) latest = action;
                    Require(latest != null && latest.kind == p.lastAction.kind && latest.paid == p.lastAction.paid
                        && latest.target == p.lastAction.target);
                }
            }
        }
        private static void ValidateAccusations(HoldemRoomPacket p)
        {
            var g = p.game;
            bool enabled = p.hasRules && p.rules.accusationsEnabled;
            Require(g.hasAccusations == (enabled && g.revealPending));
            if (!g.hasAccusations) return;
            Require(p.rules.waitsForHostDeal && p.rules.pausesAfterReveal && p.rules.receivesUtterances && p.rules.publishesUtterances
                && !g.dealPending && !g.hasLegal && !g.hasResult && !g.canContinue && !g.isOver
                && g.currentSeat == 0 && g.street >= 1 && g.street <= 3);
            var a = g.accusations;
            Require(a != null && Id(a.windowId) && a.phase >= 1 && a.phase <= 4 && a.targets != null);
            var eligible = new HashSet<int>();
            foreach (var s in g.seats) if (s.dealtIn && s.status != 2) eligible.Add(s.seat);
            bool viewerEligible = eligible.Contains(p.viewerSeat);
            Require(a.eligibleCount == eligible.Count && a.eligibleCount >= 2
                && a.responseCount >= 0 && a.responseCount <= a.eligibleCount
                && a.canRespond == (viewerEligible && a.phase == 1)
                && (!a.hasResponded || viewerEligible && a.responseCount > 0)
                && a.targets.Length == (viewerEligible ? eligible.Count - 1 : 0));
            var targets = new HashSet<int>();
            foreach (int target in a.targets) Require(target != p.viewerSeat && eligible.Contains(target) && targets.Add(target));
            Require(a.hasOwnTarget ? a.hasResponded && targets.Contains(a.ownTarget) : a.ownTarget == 0);
            Require(a.hasOwnVerdict ? a.hasOwnTarget && a.phase >= 3 : !a.ownVerdict);
            if (a.phase != 1) Require(a.responseCount == a.eligibleCount && a.hasResponded == viewerEligible);
            if (a.phase == 2) Require(!a.hasOwnTarget && !a.hasOwnVerdict);
            if (a.phase == 4 && a.hasOwnTarget) Require(a.hasOwnVerdict);
        }

        private static void ValidateChipTotal(HoldemRoomPacket p, int capacity)
        {
            Require(p.rules.startingStack > 0 && p.rules.startingStack <= long.MaxValue / capacity);
            try
            {
                long total = p.game.hasResult ? 0 : p.game.pot;
                foreach (var seat in p.game.seats) total = checked(total + seat.stack);
                Require(total == checked(p.rules.startingStack * capacity));
            }
            catch (OverflowException) { throw new ArgumentException("Invalid table chip total."); }
        }
        private static void ValidateAction(HoldemActionPacket action, HashSet<int> seats)
            => Require(action != null && seats.Contains(action.seat) && action.kind >= 0 && action.kind <= 4
                && action.street >= 0 && action.street <= 3 && action.paid >= 0 && action.target >= 0);
        private static bool Id(string s) => s != null && s.Length == 32 && Guid.TryParseExact(s, "N", out var id) && id != Guid.Empty;
        private static bool Card(int id) => id >= 0 && id < 52;
        private static void Require(bool valid) { if (!valid) throw new ArgumentException("Invalid table display packet."); }
    }
}
