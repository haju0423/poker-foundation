using System;
using Poker.Foundation;

namespace Poker.Application
{
    public sealed partial class HoldemRoom
    {
        /// <summary>Read the current deal and its preceding raw input atomically, without consuming either.</summary>
        public HoldemDealerTurnRead ReadPendingDealerTurn(Guid hostConnection)
        {
            lock (gate)
            {
                var error = Guard(hostConnection, true, out var host);
                if (error != HoldemRoomError.None) return new HoldemDealerTurnRead(error);
                if (!session.CurrentHandId.HasValue) return new HoldemDealerTurnRead(HoldemRoomError.None);
                var snapshot = session.GetSnapshot(host.Seat);
                var deal = snapshot.PendingDeal;
                if (!snapshot.IsDealPending || deal == null) return new HoldemDealerTurnRead(HoldemRoomError.None);
                HoldemUtteranceBatch matching = null;
                // The current inbox retains frozen windows even after the separate delivery queue is acknowledged.
                // Never guess from the oldest/newest queued batch: it may belong to a previous hand or street.
                if (RetainsDealerInput && utterances != null)
                    foreach (var batch in utterances.ReadClosedBatches())
                        if (batch.SessionId == deal.SessionId && batch.HandId == deal.HandId
                            && (int)batch.Street + 1 == (int)deal.Street) matching = batch;
                return new HoldemDealerTurnRead(HoldemRoomError.None,
                    new HoldemDealerTurn(deal, snapshot.SessionVersion, RetainsDealerInput, matching));
            }
        }
    }
}
