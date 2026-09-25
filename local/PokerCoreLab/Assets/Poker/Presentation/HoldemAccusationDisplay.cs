using System;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Detached viewer-only display; never carries authoritative claims or evidence.</summary>
    public sealed class HoldemAccusationDisplay
    {
        private readonly SeatId[] targets;
        public Guid WindowId { get; }
        public HoldemAccusationPhase Phase { get; }
        public int EligibleCount { get; }
        public int ResponseCount { get; }
        public bool CanRespond { get; }
        public bool HasResponded { get; }
        public SeatId? OwnTarget { get; }
        public bool? OwnVerdict { get; }
        public int TargetCount => targets.Length;
        public SeatId GetTargetAt(int index) => targets[index];

        internal HoldemAccusationDisplay(HoldemAccusationView s)
        {
            WindowId = s.WindowId; Phase = s.Phase; EligibleCount = s.EligibleCount; ResponseCount = s.ResponseCount;
            CanRespond = s.CanRespond; HasResponded = s.HasResponded; OwnTarget = s.OwnTarget; OwnVerdict = s.OwnVerdict;
            targets = new SeatId[s.TargetCount];
            for (int i = 0; i < targets.Length; i++) targets[i] = s.GetTargetAt(i);
        }

        internal HoldemAccusationDisplay(HoldemAccusationPacket s)
        {
            WindowId = Guid.ParseExact(s.windowId, "N"); Phase = (HoldemAccusationPhase)s.phase;
            EligibleCount = s.eligibleCount; ResponseCount = s.responseCount;
            CanRespond = s.canRespond; HasResponded = s.hasResponded;
            OwnTarget = s.hasOwnTarget ? new SeatId(s.ownTarget) : (SeatId?)null;
            OwnVerdict = s.hasOwnVerdict ? s.ownVerdict : (bool?)null;
            targets = new SeatId[s.targets.Length];
            for (int i = 0; i < targets.Length; i++) targets[i] = new SeatId(s.targets[i]);
        }

        public static void ValidateTransition(HoldemTableDisplay previous, HoldemTableDisplay next)
        {
            if (previous == null || next == null || previous.HandId != next.HandId
                || previous.Street != next.Street || !previous.IsRevealPending || !next.IsRevealPending) return;
            var a = previous.Accusations; var b = next.Accusations;
            if ((a == null) != (b == null)) throw new ArgumentException("Accusation window disappeared.");
            if (a == null) return;
            if (a.WindowId != b.WindowId || a.EligibleCount != b.EligibleCount || a.TargetCount != b.TargetCount
                || b.ResponseCount < a.ResponseCount || b.Phase < a.Phase || a.HasResponded && !b.HasResponded
                || a.Phase == HoldemAccusationPhase.ClosedWithoutClaims && b.Phase != a.Phase
                || a.Phase != HoldemAccusationPhase.Collecting && a.OwnTarget != b.OwnTarget
                || a.OwnVerdict.HasValue && a.OwnVerdict != b.OwnVerdict)
                throw new ArgumentException("Accusation state regressed or a frozen choice changed.");
            for (int i = 0; i < a.TargetCount; i++)
                if (a.GetTargetAt(i) != b.GetTargetAt(i)) throw new ArgumentException("Accusation targets changed.");
        }
    }
}
