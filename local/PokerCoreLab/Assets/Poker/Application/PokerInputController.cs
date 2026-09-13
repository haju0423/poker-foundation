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
            if (next.HandId != View.HandId || next.ViewerSeat != View.ViewerSeat || next.Version <= View.Version) return false;
            View = next;
            selected.Clear();
            return true;
        }
        public bool Refresh() => Receive(port.Read());

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
            // No catch-and-forget: an uncertain failure must not create a second new-ID action.
            HandReceipt receipt = port.Submit(pending);
            if (receipt == null || receipt.HandId != View.HandId || receipt.CommandId != pending.CommandId)
                throw new InvalidOperationException("The receipt does not match the pending request.");
            LastReceipt = receipt;
            Receive(port.Read());
            if (!receipt.Accepted || (receipt.AppliedVersion.HasValue && View.Version >= receipt.AppliedVersion.Value))
                pending = null;
            return receipt.Accepted;
        }
        private bool Owns(Card card)
        {
            foreach (Card own in View.OwnCards) if (own == card) return true;
            return false;
        }
    }
}
