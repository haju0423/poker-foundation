using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemLocalTable
    {
        // Same host contract as TCP, never part of the human/NPC seat-bound input port.
        public HoldemDealerTurnRead ReadPendingTurn()
        {
            var snapshot = Human.Read(); var deal = snapshot.PendingDeal;
            if (!snapshot.IsDealPending || deal == null) return new HoldemDealerTurnRead(HoldemRoomError.None);
            HoldemUtteranceBatch matching = null;
            if (retainsDealerInput && utterances != null)
                foreach (var batch in utterances.ReadClosedBatches())
                    if (batch.SessionId == deal.SessionId && batch.HandId == deal.HandId
                        && (int)batch.Street + 1 == (int)deal.Street) matching = batch;
            return new HoldemDealerTurnRead(HoldemRoomError.None,
                new HoldemDealerTurn(deal, snapshot.SessionVersion, retainsDealerInput, matching));
        }

        HoldemRoomReceipt IHoldemDealerTurnPort.DealUnchanged(HoldemDealCommand command)
            => Wrap(DealUnchanged(command));
        HoldemRoomReceipt IHoldemDealerDealApplicationPort.ApplyDealerDeal(HoldemDealCommand command)
            => Wrap(ApplyDealerDeal(command));
        private static HoldemRoomReceipt Wrap(HoldemReceipt receipt)
            => new HoldemRoomReceipt(receipt.Accepted ? HoldemRoomError.None : HoldemRoomError.CoreRejected, receipt);
    }
}
