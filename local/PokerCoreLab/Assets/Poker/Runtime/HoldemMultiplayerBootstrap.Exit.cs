using System;

namespace Poker.Runtime
{
    public sealed partial class HoldemMultiplayerBootstrap
    {
        private bool quittingApplication, quitAuthorized;
        private Action quitApplication = () => UnityEngine.Application.Quit();

        private void AttachApplicationExitGuard()
        {
            quittingApplication = false; quitAuthorized = false;
#if !UNITY_EDITOR
            // Automated players must still terminate without interactive confirmation.
            if (!UnityEngine.Application.isBatchMode)
                UnityEngine.Application.wantsToQuit += OnWantsToQuit;
#endif
        }

        private void DetachApplicationExitGuard()
        {
#if !UNITY_EDITOR
            UnityEngine.Application.wantsToQuit -= OnWantsToQuit;
#endif
        }

        // Normal desktop exit only: force-quit/crash recovery is not provided by this hook.
        private bool OnWantsToQuit()
        {
            if (connection?.HasSession != true) return true;
            // Another application handler can veto the final Quit. An approval for the
            // closed room must not authorize a later room the player enters afterwards.
            quitAuthorized = false;
            quittingApplication = true;
            rematchOpen = false;
            if (!confirming) { confirming = true; leaveError = ""; }
            // Repeated OS requests must not replace an in-flight lobby-leave command.
            Render();
            return false;
        }

        private void CancelLeaveConfirmation()
        {
            if (!confirming || leavingLobby || failed && !quittingApplication) return;
            quittingApplication = false;
            confirming = false; leaveError = "";
            Render();
        }

        private void FinishApplicationExitIfRequested()
        {
            if (!quittingApplication || quitAuthorized) return;
            // ReturnHome has already closed the session. Set this before invoking Quit,
            // since Unity can raise wantsToQuit again for the newly authorized request.
            quittingApplication = false; quitAuthorized = true;
            quitApplication();
        }

        private string ApplicationExitCopy(string roomCopy, bool canAbandonLeave, bool matchEnded)
        {
            if (canAbandonLeave)
                return "퇴장이 처리됐는지 확인하지 못했어요.\n다시 연결하면 같은 요청을 확인할 수 있어요.\n지금 종료하면 연결 정보가 지워져 같은 자리로 돌아올 수 없고, 방에 자리가 남아 있을 수 있어요.";
            if (leavingLobby)
                return "대기실 자리를 비우고 있어요.\n"
                    + (connection.IsConnecting ? "다시 연결하고 있어요." : connection.Remote?.StatusText ?? "응답을 확인하고 있어요.")
                    + "\n퇴장 처리가 확인되면 게임을 종료해요.";
            if (!string.IsNullOrEmpty(leaveError)) return leaveError + "\n게임을 종료하려면 다시 확인해 주세요.";
            if (connection.IsHosting)
                return "게임을 종료할까요?\n방이 닫히고 다른 참가자의 연결도 종료돼요."
                    + (connection.Remote?.HasGame == true && !matchEnded ? "\n진행 중인 게임은 복구할 수 없어요." : "");
            if (CanReturnLobbySeat) return "게임을 종료할까요?\n대기실 자리를 비운 뒤 종료해요.";
            if (matchEnded) return "게임을 종료할까요?\n이 방의 결과를 다시 볼 수 없어요.";
            return "게임을 종료할까요?\n이 자리의 연결 정보가 지워져 같은 판으로 돌아올 수 없어요.";
        }
    }
}
