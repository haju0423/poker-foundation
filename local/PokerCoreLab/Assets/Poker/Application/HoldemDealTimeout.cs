using System;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Optional host watchdog. Poll on the same serialized loop as session commands.
    /// The caller chooses a wait duration and supplies a monotonic clock; no timer or AI call runs here.
    /// Waiting begins when Poll first observes a new deal window, not at construction.
    /// </summary>
    public sealed class HoldemDealTimeout
    {
        private readonly Func<HoldemSnapshot> read;
        private readonly Func<HoldemDealCommand, HoldemReceipt> dealUnchanged;
        private readonly Func<TimeSpan> clock;
        private readonly TimeSpan maximumWait;
        private Guid windowId;
        private TimeSpan startedAt;
        private TimeSpan? lastTime;

        public HoldemDealTimeout(Func<HoldemSnapshot> read,
            Func<HoldemDealCommand, HoldemReceipt> dealUnchanged,
            TimeSpan maximumWait, Func<TimeSpan> monotonicClock)
        {
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.dealUnchanged = dealUnchanged ?? throw new ArgumentNullException(nameof(dealUnchanged));
            clock = monotonicClock ?? throw new ArgumentNullException(nameof(monotonicClock));
            if (maximumWait <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumWait));
            this.maximumWait = maximumWait;
        }

        /// <returns>Null when no fallback was attempted; otherwise the normal host command receipt.</returns>
        public HoldemReceipt Poll()
        {
            TimeSpan now = clock();
            if (now < TimeSpan.Zero || (lastTime.HasValue && now < lastTime.Value))
                throw new InvalidOperationException("The deal watchdog requires a nonnegative monotonic clock.");
            lastTime = now;
            var view = read();
            var pending = view.PendingDeal;
            if (!view.IsDealPending || pending == null) { windowId = Guid.Empty; return null; }
            if (pending.WindowId != windowId)
            {
                windowId = pending.WindowId; startedAt = now;
                return null;
            }
            if (now - startedAt < maximumWait) return null;
            // Do not turn a timeout into an accusation verdict. The deck simply proceeds unchanged.
            return dealUnchanged(new HoldemDealCommand(pending.SessionId, pending.HandId, pending.WindowId,
                Guid.NewGuid(), view.SessionVersion, pending.Street));
        }
    }
}
