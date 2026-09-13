using System;

namespace Poker.Application
{
    public enum PracticeProgressFailure { None, OpponentAction, Display }
    public enum PracticeProgressOutcome { None, Advanced, Refreshed, Stopped, Failed, Busy }

    /// <summary>
    /// Serial local-practice orchestration, with explicit recovery and no timer or game rules.
    /// The local action callback must fail before commit or return its known outcome. This is NOT
    /// a retry protocol for an uncertain remote command. Display retries never invoke that callback.
    /// </summary>
    public sealed class PracticeProgressRunner
    {
        private readonly Func<bool> advanceOpponent;
        private readonly Action refreshDisplay;
        private bool operating;

        public PracticeProgressRunner(Func<bool> advanceOpponent, Action refreshDisplay)
        {
            this.advanceOpponent = advanceOpponent ?? throw new ArgumentNullException(nameof(advanceOpponent));
            this.refreshDisplay = refreshDisplay ?? throw new ArgumentNullException(nameof(refreshDisplay));
        }

        public PracticeProgressFailure Failure { get; private set; }
        /// <summary>Diagnostic type name only; no exception, message, stack, or payload is retained.</summary>
        public string FailureType { get; private set; } = "";

        public PracticeProgressOutcome Tick()
        {
            if (operating) return PracticeProgressOutcome.Busy;
            if (Failure != PracticeProgressFailure.None) return PracticeProgressOutcome.Stopped;
            return Execute(false);
        }

        public PracticeProgressOutcome Retry()
        {
            if (operating) return PracticeProgressOutcome.Busy;
            if (Failure == PracticeProgressFailure.None) return PracticeProgressOutcome.None;
            return Execute(true);
        }

        private PracticeProgressOutcome Execute(bool retry)
        {
            operating = true;
            try
            {
                bool advanced = false;
                if (Failure != PracticeProgressFailure.Display)
                {
                    try { advanced = advanceOpponent(); }
                    catch (Exception error) { return Stop(PracticeProgressFailure.OpponentAction, error); }
                }
                if (advanced || retry)
                {
                    try { refreshDisplay(); }
                    catch (Exception error) { return Stop(PracticeProgressFailure.Display, error); }
                }
                Failure = PracticeProgressFailure.None; FailureType = "";
                return advanced ? PracticeProgressOutcome.Advanced : retry ? PracticeProgressOutcome.Refreshed : PracticeProgressOutcome.None;
            }
            finally { operating = false; }
        }

        private PracticeProgressOutcome Stop(PracticeProgressFailure failure, Exception error)
        { Failure = failure; FailureType = error.GetType().Name; return PracticeProgressOutcome.Failed; }
    }
}
