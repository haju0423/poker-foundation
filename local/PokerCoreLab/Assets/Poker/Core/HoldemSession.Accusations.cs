using System;
using System.Collections.Generic;

namespace Poker.Foundation
{
    public sealed partial class HoldemSession
    {
        private sealed class AcceptedAccusation
        {
            public AcceptedAccusation(HoldemWindowCommand command, HoldemReceipt receipt)
            { Command = command; Receipt = receipt; }
            public HoldemWindowCommand Command { get; }
            public HoldemReceipt Receipt { get; }
            public bool Matches(HoldemWindowCommand other) =>
                Command is HoldemAccusationChoiceCommand choice && other is HoldemAccusationChoiceCommand otherChoice
                    ? choice.HasSamePayload(otherChoice)
                    : Command is HoldemAccusationHostCommand host && other is HoldemAccusationHostCommand otherHost
                        && host.HasSamePayload(otherHost);
        }

        private HoldemAccusationWindow accusations;
        private readonly Dictionary<Guid, AcceptedAccusation> acceptedAccusations = new Dictionary<Guid, AcceptedAccusation>();

        /// <summary>Adapter-authenticated seat input. Open-window revisions replace only that seat's private choice.</summary>
        public HoldemReceipt SubmitAccusationChoice(SeatId authorizedSeat, HoldemAccusationChoiceCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!authorizedSeat.IsValid || authorizedSeat != command.Seat)
                return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            HoldemReceipt prior = ValidateIntakeEnvelope(command, command.Seat);
            if (prior != null) return prior;
            if (accusations.Phase != HoldemAccusationPhase.Collecting)
                return Reject(command, HoldemCommandError.AccusationWindowClosed, command.Seat);
            if (!accusations.IsEligible(command.Seat)) return Reject(command, HoldemCommandError.UnauthorizedSeat, command.Seat);
            if (command.Target.HasValue && (command.Target == command.Seat || !accusations.IsEligible(command.Target.Value)))
                return Reject(command, HoldemCommandError.InvalidAccusationTarget, command.Seat);

            processing = true;
            try { return CommitIntake(command, accusations.Choose(command), command.Seat); }
            finally { processing = false; }
        }

        /// <summary>
        /// Trusted host only. Close freezes the collected choices. The adapter must authenticate the host,
        /// serialize commands and validate verdicts against the actual dealer record before calling this method.
        /// This method records supplied verdicts; it does not inspect dealer evidence. Missing evidence must
        /// stay pending. Recording a verdict never applies a fine, fold or payout.
        /// </summary>
        public HoldemReceipt ProcessAccusationHostCommand(HoldemAccusationHostCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            HoldemReceipt prior = ValidateIntakeEnvelope(command, null);
            if (prior != null) return prior;
            HoldemCommandError error = command.Action == HoldemAccusationHostAction.CloseWindow
                ? accusations.CanClose() : accusations.CanRecord(command.ClaimId);
            if (error != HoldemCommandError.None) return Reject(command, error, null);

            processing = true;
            try
            {
                HoldemAccusationWindow candidate = command.Action == HoldemAccusationHostAction.CloseWindow
                    ? accusations.Close() : accusations.Record(command.ClaimId, command.WasManipulated);
                return CommitIntake(command, candidate, null);
            }
            finally { processing = false; }
        }

        /// <summary>Host-only integration queue. Never expose this list through a player connection.</summary>
        public IReadOnlyList<HoldemAccusationClaim> GetPendingAccusations()
            => accusations == null ? Array.Empty<HoldemAccusationClaim>() : accusations.PendingClaims();

        /// <summary>Host-only decisions retained through consequence wait for the current window.</summary>
        public IReadOnlyList<HoldemAccusationDecision> GetAccusationDecisions()
            => accusations == null ? Array.Empty<HoldemAccusationDecision>() : accusations.RecordedDecisions();

        private HoldemReceipt ValidateIntakeEnvelope(HoldemWindowCommand command, SeatId? seat)
        {
            if (command.SessionId != SessionId) return Reject(command, HoldemCommandError.WrongSession, seat);
            if (currentHand == null) return Reject(command, HoldemCommandError.NotStarted, seat);
            if (command.HandId != currentHand.HandId) return Reject(command, HoldemCommandError.WrongHand, seat);
            if (processing) return Reject(command, HoldemCommandError.Busy, seat);
            if (acceptedAccusations.TryGetValue(command.CommandId, out AcceptedAccusation previous))
                return previous.Matches(command) ? previous.Receipt : Reject(command, HoldemCommandError.CommandConflict, seat);
            if (usedCommandIds.Contains(command.CommandId)) return Reject(command, HoldemCommandError.CommandConflict, seat);
            if (command.ExpectedVersion != Version) return Reject(command, HoldemCommandError.VersionMismatch, seat);
            if (config.AccusationMode == HoldemAccusationMode.Disabled)
                return Reject(command, HoldemCommandError.AccusationNotEnabled, seat);
            if (!currentHand.IsRevealPending || accusations == null) return Reject(command, HoldemCommandError.RevealNotPending, seat);
            if (command.WindowId != accusations.Id || command.Street != accusations.Street)
                return Reject(command, HoldemCommandError.WrongRevealWindow, seat);
            return null;
        }

        private HoldemReceipt CommitIntake(HoldemWindowCommand command, HoldemAccusationWindow candidate, SeatId? seat)
        {
            long nextVersion = checked(Version + 1);
            var receipt = new HoldemReceipt(SessionId, command.HandId, command.CommandId, seat, nextVersion, HoldemCommandError.None);
            accusations = candidate;
            Version = nextVersion;
            acceptedAccusations.Add(command.CommandId, new AcceptedAccusation(command, receipt));
            usedCommandIds.Add(command.CommandId);
            return receipt;
        }

        private HoldemAccusationWindow AccusationsFor(HoldemHand candidate)
        {
            if (config.AccusationMode == HoldemAccusationMode.Disabled || !candidate.IsRevealPending) return null;
            return accusations != null && accusations.HandId == candidate.HandId && accusations.Street == candidate.Street
                ? accusations : new HoldemAccusationWindow(candidate);
        }

        private HoldemCommandError AccusationResumeError()
        {
            if (config.AccusationMode == HoldemAccusationMode.Disabled) return HoldemCommandError.None;
            if (accusations == null) return HoldemCommandError.AccusationWindowNotClosed;
            if (accusations.Phase == HoldemAccusationPhase.ClosedWithoutClaims) return HoldemCommandError.None;
            if (accusations.Phase == HoldemAccusationPhase.Collecting) return HoldemCommandError.AccusationWindowNotClosed;
            return accusations.Phase == HoldemAccusationPhase.AwaitingVerdicts
                ? HoldemCommandError.AccusationVerdictsPending : HoldemCommandError.AccusationConsequencesPending;
        }

        private HoldemReceipt Reject(HoldemWindowCommand command, HoldemCommandError error, SeatId? seat)
            => new HoldemReceipt(command.SessionId, command.HandId, command.CommandId, seat, null, error);
    }
}
