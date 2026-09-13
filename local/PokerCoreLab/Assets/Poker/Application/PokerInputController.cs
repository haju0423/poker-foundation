using System;
using System.Collections.Generic;
using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>One hand's player input state. Never owns a deck, ledger, or other participant's view.</summary>
    public sealed class PokerInputController
    {
        private readonly IPokerSeatPort port;
        private readonly HashSet<Card> selected = new HashSet<Card>();
        private HandCommand pending;
        private bool dispatching;

        public PokerInputController(IPokerSeatPort port)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            View = port.Read() ?? throw new InvalidOperationException("A started participant view is required.");
        }
        public PokerPlayerView View { get; private set; }
        public bool IsPending => pending != null;
        public int SelectedCount => selected.Count;
        public HandReceipt LastReceipt { get; private set; }
        public bool IsSelected(Card card) => selected.Contains(card);

        /// <summary>Only newer snapshots of this seat AND this hand may replace the current display.</summary>
        public bool Receive(PokerPlayerView next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            return !dispatching && AcceptSnapshot(next);
        }
        private bool AcceptSnapshot(PokerPlayerView next)
        {
            if (next.HandId != View.HandId || next.ViewerSeat != View.ViewerSeat || next.Version <= View.Version) return false;
            View = next;
            selected.Clear();
            return true;
        }
        public bool Refresh() => !dispatching && Receive(port.Read());

        public bool Toggle(Card card)
        {
            if (IsPending || !View.CanExchange || !Owns(card)) return false;
            if (selected.Remove(card)) return true;
            if (selected.Count >= View.MaxExchangeCount) return false;
            selected.Add(card);
            return true;
        }

        public bool Bet(BettingAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (IsPending || !View.IsOwnTurn || View.Betting == null) return false;
            return Send(HandCommand.Bet(View.HandId, Guid.NewGuid(), View.ViewerSeat, View.Version, action));
        }
        public bool Exchange()
        {
            if (IsPending || !View.CanExchange) return false;
            var cards = new Card[selected.Count]; selected.CopyTo(cards);
            return Send(HandCommand.Exchange(View.HandId, Guid.NewGuid(), View.ViewerSeat, View.Version, cards));
        }
        /// <summary>Uncertain transport/read failure preserves the exact command ID and payload for retry.</summary>
        public bool RetryPending() => pending != null && Dispatch();

        private bool Send(HandCommand command) { pending = command; return Dispatch(); }
        private bool Dispatch()
        {
            if (dispatching) return false;
            HandCommand request = pending;
            dispatching = true;
            try
            {
                // Keep retries outside this in-flight call, even if a port invokes a synchronous callback.
                // An uncertain failure still retains the exact request for a later explicit retry.
                HandReceipt receipt = port.Submit(request);
                if (!MatchesRequest(receipt, request))
                    throw new InvalidOperationException("The receipt does not match the pending request.");
                LastReceipt = receipt;
                PokerPlayerView current = port.Read() ?? throw new InvalidOperationException("A current participant view is required.");
                AcceptSnapshot(current);
                if (!receipt.Accepted || (receipt.AppliedVersion.HasValue && View.Version >= receipt.AppliedVersion.Value))
                    pending = null;
                return receipt.Accepted;
            }
            finally { dispatching = false; }
        }
        private static bool MatchesRequest(HandReceipt receipt, HandCommand request)
        {
            if (receipt == null || receipt.HandId != request.HandId || receipt.CommandId != request.CommandId
                || receipt.Seat != request.Seat) return false;
            if (!receipt.Accepted) return !receipt.AppliedVersion.HasValue && !receipt.Transition.HasValue;
            if (request.ExpectedVersion == long.MaxValue || receipt.AppliedVersion != request.ExpectedVersion + 1
                || !receipt.Transition.HasValue) return false;
            HandTransition transition = receipt.Transition.Value;
            if (transition.HandId != request.HandId || transition.CommandId != request.CommandId
                || transition.Seat != request.Seat || transition.AppliedVersion != receipt.AppliedVersion.Value
                || transition.Kind != request.Kind) return false;
            // Correlate with the immutable request, not the current view: a valid retry can belong to an older phase.
            if (request.Kind == HandCommandKind.Exchange)
                return transition.ExchangeCount == request.SelectedCards.Count
                    && !transition.BettingAction.HasValue && !transition.TargetTotal.HasValue;
            long? target = request.Action.Kind == BettingActionKind.BetTo || request.Action.Kind == BettingActionKind.RaiseTo
                ? request.Action.Target : (long?)null;
            return transition.BettingAction == request.Action.Kind && transition.TargetTotal == target;
        }
        private bool Owns(Card card)
        {
            foreach (Card own in View.OwnCards) if (own == card) return true;
            return false;
        }
    }
}
