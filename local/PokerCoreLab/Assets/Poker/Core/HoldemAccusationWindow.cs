using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public enum HoldemAccusationPhase
    {
        Collecting = 1,
        ClosedWithoutClaims = 2,
        AwaitingVerdicts = 3,
        AwaitingConsequences = 4
    }

    /// <summary>Viewer-relative choice and own claim result. No other seat's choice or dealer record is exposed.</summary>
    public sealed class HoldemAccusationView
    {
        private readonly SeatId[] targets;
        internal HoldemAccusationView(Guid windowId, HoldemAccusationPhase phase, int eligibleCount,
            int responseCount, bool canRespond, bool hasResponded, SeatId? ownTarget, SeatId[] targets,
            bool? ownVerdict = null)
        {
            WindowId = windowId; Phase = phase; EligibleCount = eligibleCount; ResponseCount = responseCount;
            CanRespond = canRespond; HasResponded = hasResponded; OwnTarget = ownTarget;
            this.targets = targets; OwnVerdict = ownVerdict;
        }
        public Guid WindowId { get; }
        public HoldemAccusationPhase Phase { get; }
        public int EligibleCount { get; }
        public int ResponseCount { get; }
        public bool CanRespond { get; }
        public bool HasResponded { get; }
        public SeatId? OwnTarget { get; }
        /// <summary>Result of this viewer's frozen claim only. Null means no recorded result, never a failed claim.</summary>
        public bool? OwnVerdict { get; }
        public int TargetCount => targets.Length;
        public SeatId GetTargetAt(int index)
        {
            if (index < 0 || index >= targets.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return targets[index];
        }
    }

    /// <summary>Host-only pending claim, keyed to a frozen choice and a specific reveal.</summary>
    public sealed class HoldemAccusationClaim
    {
        internal HoldemAccusationClaim(Guid windowId, HoldemAccusationChoiceCommand choice)
        {
            WindowId = windowId; SessionId = choice.SessionId; HandId = choice.HandId;
            Street = choice.Street; ClaimId = choice.CommandId; Accuser = choice.Seat; Target = choice.Target.Value;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid WindowId { get; }
        public HoldemStreet Street { get; }
        public Guid ClaimId { get; }
        public SeatId Accuser { get; }
        public SeatId Target { get; }
    }

    /// <summary>Host-only recorded decision for later consequence handling. Never part of a player snapshot.</summary>
    public sealed class HoldemAccusationDecision
    {
        internal HoldemAccusationDecision(HoldemAccusationClaim claim, bool wasManipulated)
        { Claim = claim; WasManipulated = wasManipulated; }
        public HoldemAccusationClaim Claim { get; }
        public bool WasManipulated { get; }
    }

    /// <summary>Immutable intake only. No fold, fine, elimination or pot settlement is performed here.</summary>
    internal sealed class HoldemAccusationWindow
    {
        private readonly SeatId[] eligible;
        private readonly HoldemAccusationChoiceCommand[] choices;
        private readonly bool?[] verdicts;
        public Guid Id { get; }
        public Guid HandId { get; }
        public HoldemStreet Street { get; }
        public HoldemAccusationPhase Phase { get; }

        public HoldemAccusationWindow(HoldemHand hand)
        {
            if (!hand.IsRevealPending) throw new ArgumentException("A reveal must be pending.", nameof(hand));
            Id = Guid.NewGuid(); HandId = hand.HandId; Street = hand.Street;
            var seats = new List<SeatId>();
            for (int i = 0; i < hand.DealtInSeatCount; i++)
            {
                SeatId seat = hand.GetDealtInSeatAt(i);
                if (!hand.IsFolded(seat)) seats.Add(seat);
            }
            eligible = seats.ToArray(); choices = new HoldemAccusationChoiceCommand[eligible.Length];
            verdicts = new bool?[eligible.Length]; Phase = HoldemAccusationPhase.Collecting;
        }
        private HoldemAccusationWindow(HoldemAccusationWindow source, HoldemAccusationChoiceCommand[] choices,
            bool?[] verdicts, HoldemAccusationPhase phase)
        {
            Id = source.Id; HandId = source.HandId; Street = source.Street; eligible = source.eligible;
            this.choices = choices; this.verdicts = verdicts; Phase = phase;
        }
        public bool IsEligible(SeatId seat) => IndexOf(seat) >= 0;
        public HoldemAccusationWindow Choose(HoldemAccusationChoiceCommand command)
        {
            var next = (HoldemAccusationChoiceCommand[])choices.Clone();
            next[IndexOf(command.Seat)] = command;
            return new HoldemAccusationWindow(this, next, verdicts, Phase);
        }
        public HoldemCommandError CanClose()
        {
            if (Phase != HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowClosed;
            for (int i = 0; i < choices.Length; i++)
                if (choices[i] == null) return HoldemCommandError.AccusationResponsesPending;
            return HoldemCommandError.None;
        }
        public HoldemAccusationWindow Close()
        {
            bool hasClaims = false;
            foreach (var choice in choices) if (choice.Target.HasValue) hasClaims = true;
            return new HoldemAccusationWindow(this, choices, verdicts, hasClaims
                ? HoldemAccusationPhase.AwaitingVerdicts : HoldemAccusationPhase.ClosedWithoutClaims);
        }
        public HoldemCommandError CanRecord(Guid claimId)
        {
            if (Phase == HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowNotClosed;
            int index = ClaimIndex(claimId);
            if (index < 0) return HoldemCommandError.UnknownAccusation;
            return verdicts[index].HasValue ? HoldemCommandError.AccusationVerdictRecorded : HoldemCommandError.None;
        }
        public HoldemAccusationWindow Record(Guid claimId, bool wasManipulated)
        {
            var next = (bool?[])verdicts.Clone(); next[ClaimIndex(claimId)] = wasManipulated;
            bool pending = false;
            for (int i = 0; i < choices.Length; i++)
                if (choices[i].Target.HasValue && !next[i].HasValue) pending = true;
            return new HoldemAccusationWindow(this, choices, next, pending
                ? HoldemAccusationPhase.AwaitingVerdicts : HoldemAccusationPhase.AwaitingConsequences);
        }
        public IReadOnlyList<HoldemAccusationClaim> PendingClaims()
        {
            var claims = new List<HoldemAccusationClaim>();
            if (Phase == HoldemAccusationPhase.AwaitingVerdicts)
                for (int i = 0; i < choices.Length; i++)
                    if (choices[i].Target.HasValue && !verdicts[i].HasValue)
                        claims.Add(new HoldemAccusationClaim(Id, choices[i]));
            return claims.AsReadOnly();
        }
        public IReadOnlyList<HoldemAccusationDecision> RecordedDecisions()
        {
            var decisions = new List<HoldemAccusationDecision>();
            for (int i = 0; i < choices.Length; i++)
                if (verdicts[i].HasValue)
                    decisions.Add(new HoldemAccusationDecision(new HoldemAccusationClaim(Id, choices[i]), verdicts[i].Value));
            return decisions.AsReadOnly();
        }
        public HoldemAccusationView ForViewer(SeatId viewer)
        {
            int own = IndexOf(viewer), responded = 0;
            foreach (var choice in choices) if (choice != null) responded++;
            var targets = new List<SeatId>();
            if (own >= 0) foreach (var seat in eligible) if (seat != viewer) targets.Add(seat);
            return new HoldemAccusationView(Id, Phase, eligible.Length, responded,
                own >= 0 && Phase == HoldemAccusationPhase.Collecting, own >= 0 && choices[own] != null,
                own >= 0 ? choices[own]?.Target : null, targets.ToArray(), own >= 0 ? verdicts[own] : null);
        }
        private int IndexOf(SeatId seat) => Array.IndexOf(eligible, seat);
        private int ClaimIndex(Guid claimId)
        {
            for (int i = 0; i < choices.Length; i++)
                if (choices[i] != null && choices[i].Target.HasValue && choices[i].CommandId == claimId) return i;
            return -1;
        }
    }
}
