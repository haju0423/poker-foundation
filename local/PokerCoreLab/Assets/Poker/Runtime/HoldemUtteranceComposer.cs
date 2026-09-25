using System;
using Poker.Application;
using Poker.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Optional own-text intake widget. Never displays dealer interpretation or claims delivery to AI.</summary>
    internal sealed class HoldemUtteranceComposer : IDisposable
    {
        private readonly IHoldemUtterancePlayerPort port;
        private readonly Func<bool> canInteract;
        private readonly TextField input;
        private readonly Button send;
        private readonly Label status;
        private HoldemUtteranceView view;
        private HoldemUtteranceCommand pending;
        private Guid draftWindow;
        private const int RetryDelayMilliseconds = 3000;
        private double retryAt;
        private IVisualElementScheduledItem retrySchedule;
        private bool disposed, unavailable;
        private string notice = "";

        public HoldemUtteranceComposer(VisualElement parent, IHoldemUtterancePlayerPort port, Func<bool> canInteract,
            bool publishesUtterances = false)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.canInteract = canInteract ?? throw new ArgumentNullException(nameof(canInteract));
            var box = new VisualElement { name = "omc-utterance" }; box.AddToClassList("omc-utterance"); parent.Add(box);
            var row = new VisualElement(); row.AddToClassList("omc-utterance-row"); box.Add(row);
            row.Add(new Label("멘트") { name = "omc-utterance-label" });
            input = new TextField { name = "omc-utterance-input", multiline = false };
            input.AddToClassList("omc-utterance-input"); row.Add(input);
            send = new Button(Submit) { name = "omc-utterance-send", text = "보내기" }; row.Add(send);
            status = new Label { name = "omc-utterance-status", enableRichText = false };
            status.AddToClassList("omc-utterance-status"); box.Add(status);
            var scope = new Label(publishesUtterances ? "보낸 멘트는 이름과 함께 모두에게 보여요."
                : "접수만 확인해요. AI나 카드에는 반영되지 않아요.") { name = "omc-utterance-scope" };
            scope.AddToClassList("omc-utterance-scope"); box.Add(scope);
            box.tooltip = publishesUtterances
                ? "멘트 원문과 발언자는 모두에게 공개돼요. 이번 판 기록에서 다시 볼 수 있어요. 현재는 AI나 카드에 반영되지 않아요."
                : port is IHoldemAsyncUtterancePort
                ? "원문은 방장 기기의 접수함에 저장돼요. 다른 참가자 화면이나 AI, 카드에는 아직 연결하지 않았어요."
                : "이 개발 장면에서는 본인 접수만 확인하며 다른 참가자 화면이나 AI에 전달하거나 카드에 반영하지 않아요.";
            input.RegisterValueChangedCallback(OnChanged);
        }

        public void Render()
        {
            if (disposed) return;
            try
            {
                // A correlated response can precede a new-hand state in the same poll.
                HoldemUtteranceReceipt appliedReceipt = null;
                if (pending != null && port is IHoldemAsyncUtterancePort asyncPort
                    && asyncPort.TryReadReceipt(pending, out var completed))
                { appliedReceipt = completed; ApplyReceipt(completed); }
                var next = port.ReadUtterances();
                bool differentHand = view != null && (view.SessionId != next.SessionId || view.HandId != next.HandId);
                if (differentHand)
                {
                    notice = appliedReceipt != null
                        ? appliedReceipt.Accepted ? "이전 판의 멘트 접수가 확인됐어요." : "이전 판 멘트: " + ErrorText(appliedReceipt.Error)
                        : pending == null ? "" : "이전 판의 접수 결과는 확인하지 못했어요.";
                    CancelRetryDelay(); pending = null; input.SetValueWithoutNotify(""); draftWindow = Guid.Empty;
                }
                view = next; unavailable = false; input.maxLength = view.MaximumTextLength;
                if (pending != null)
                {
                    for (int i = 0; i < view.Count; i++)
                        if (view.GetEntry(i).CommandId == pending.CommandId && view.GetEntry(i).Text == pending.Text
                            && view.GetEntry(i).Speaker == pending.Speaker && view.GetEntry(i).WindowId == pending.WindowId)
                        { Acknowledge(); break; }
                }
                // A draft belongs to its original street; never silently submit it on a later board.
                if (pending == null && view.WindowId != Guid.Empty && view.WindowId != draftWindow)
                {
                    if (!string.IsNullOrEmpty(input.value) && appliedReceipt == null)
                        notice = "새 단계가 시작돼 이전 미전송 멘트는 지웠어요.";
                    input.SetValueWithoutNotify(""); draftWindow = view.WindowId;
                }
                Refresh();
            }
            catch
            {
                unavailable = true; status.text = "멘트 접수를 확인하지 못했어요.";
                input.SetEnabled(false); send.SetEnabled(false);
            }
        }

        private void OnChanged(ChangeEvent<string> change)
        {
            if (disposed) return;
            notice = ""; Refresh();
        }

        private void Refresh()
        {
            if (view == null || unavailable || disposed) return;
            bool allowed = canInteract() && (!(port is IHoldemAsyncUtterancePort asyncPort) || asyncPort.CanInteract);
            input.isReadOnly = pending != null;
            input.SetEnabled(allowed && (pending != null || view.CanSubmit));
            send.text = pending == null ? "보내기" : WaitingToRetry ? "확인 중…" : "접수 확인";
            send.SetEnabled(allowed && !WaitingToRetry && (pending != null || view.CanSubmit && !string.IsNullOrWhiteSpace(input.value)));
            if (notice.Length > 0) status.text = notice;
            else if (view.IsBacklogged) status.text = "멘트 접수함이 가득 찼어요. 포커는 계속할 수 있어요.";
            else if (!string.IsNullOrEmpty(input.value) && view.WindowId == Guid.Empty)
                status.text = "입력이 마감돼 보내지지 않았어요.";
            else if (view.Count > 0)
            {
                var latest = view.GetEntry(view.Count - 1);
                status.text = KoreanPokerText.StreetName(latest.Street) + "에 보낸 멘트: " + latest.Text;
            }
            else status.text = view.CanSubmit ? "베팅 중에 보낼 수 있어요." : "지금은 멘트 입력 시간이 아니에요.";
            status.tooltip = status.text;
        }

        private void Submit()
        {
            if (disposed || unavailable || !canInteract() || view == null || WaitingToRetry) return;
            if (port is IHoldemAsyncUtterancePort remote && !remote.CanInteract) return;
            if (pending == null)
            {
                if (!view.CanSubmit || string.IsNullOrWhiteSpace(input.value) || draftWindow != view.WindowId) return;
                pending = new HoldemUtteranceCommand(view.SessionId, view.HandId, view.WindowId,
                    Guid.NewGuid(), view.Street, view.ViewerSeat, input.value);
            }
            try
            {
                if (port is IHoldemAsyncUtterancePort asyncPort)
                {
                    // Guard before sending: callbacks or an uncertain enqueue failure must not
                    // turn repeated clicks into a burst. A receipt cancels this wait immediately.
                    BeginRetryDelay();
                    if (!asyncPort.SendOrRetry(pending, out var receipt))
                    { notice = "멘트 접수를 확인하고 있어요."; Refresh(); return; }
                    ApplyReceipt(receipt);
                }
                else ApplyReceipt(port.SubmitUtterance(pending));
                Render();
            }
            catch
            {
                // Unknown delivery result: retain the exact command ID for an explicit safe retry.
                notice = WaitingToRetry ? "접수 결과를 확인하지 못했어요. 잠시 후 다시 확인해 주세요."
                    : "접수 결과를 확인하지 못했어요. 접수 확인을 눌러 주세요.";
                Refresh();
            }
        }

        private void ApplyReceipt(HoldemUtteranceReceipt receipt)
        {
            if (receipt == null || pending == null || receipt.CommandId != pending.CommandId)
                throw new InvalidOperationException("Uncorrelated utterance receipt.");
            if (receipt.Accepted) Acknowledge();
            else { CancelRetryDelay(); notice = ErrorText(receipt.Error); pending = null; }
        }

        private void Acknowledge()
        {
            CancelRetryDelay(); pending = null; input.SetValueWithoutNotify(""); notice = "";
        }

        private bool WaitingToRetry => pending != null && Time.realtimeSinceStartupAsDouble < retryAt;

        private void BeginRetryDelay()
        {
            CancelRetryDelay();
            var command = pending;
            retryAt = Time.realtimeSinceStartupAsDouble + RetryDelayMilliseconds / 1000.0;
            double deadline = retryAt;
            retrySchedule = send.schedule.Execute(() => {
                if (disposed || pending != command || retryAt != deadline || WaitingToRetry) return;
                CancelRetryDelay();
                Refresh(); // Expiry never sends, and still respects modal, pause and connection state.
            }).Every(50).StartingIn(RetryDelayMilliseconds);
        }

        private void CancelRetryDelay()
        {
            retrySchedule?.Pause(); retrySchedule = null; retryAt = 0;
        }

        private static string ErrorText(HoldemUtteranceError error)
        {
            switch (error)
            {
                case HoldemUtteranceError.EmptyText: return "멘트를 입력해 주세요.";
                case HoldemUtteranceError.TextTooLong: return "멘트를 조금 줄여 주세요.";
                case HoldemUtteranceError.InvalidText: return "줄바꿈이나 사용할 수 없는 문자가 있어요.";
                case HoldemUtteranceError.LimitReached: return "이번 단계에서 보낼 수 있는 멘트를 모두 보냈어요.";
                case HoldemUtteranceError.WindowClosed:
                case HoldemUtteranceError.WrongWindow:
                case HoldemUtteranceError.WrongHand: return "입력 시간이 지나 보내지지 않았어요.";
                case HoldemUtteranceError.SeatNotAllowed: return "지금 상태에서는 멘트를 보낼 수 없어요.";
                case HoldemUtteranceError.BacklogFull: return "아직 처리 중인 멘트가 많아요. 잠시 후 다시 보내 주세요.";
                case HoldemUtteranceError.DeliveryUnavailable: return "연결 상태가 바뀌어 접수하지 못했어요. 다시 확인해 주세요.";
                default: return "멘트가 접수되지 않았어요. 현재 상태를 확인해 주세요.";
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; CancelRetryDelay(); input.UnregisterValueChangedCallback(OnChanged);
        }
    }
}
