using System;
using System.Collections.Generic;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Explicit host-process fixture. No model, player endpoint or settlement policy.</summary>
    internal sealed class HoldemMultiplayerDealerDebug : IDisposable, IHoldemDealerSelectionPolicy
    {
        private readonly HoldemMultiplayerConnection connection;
        private readonly HoldemTableScreen screen;
        private readonly Func<bool> active;
        private readonly HoldemDealerTurnCoordinator coordinator;
        private readonly VisualElement box, actions;
        private readonly Label status;
        private DropdownField speaker;
        private HoldemDealerWork current;
        private HoldemRoomAccusationClose closeCommand;
        private Guid selectedUtterance, renderedWindow;
        private HoldemAccusationPhase? renderedPhase;
        private TimeSpan clock;
        private bool disposed, submitted, change;
        private string completion;

        internal static HoldemUtterancePolicy CreateUtterancePolicy(HoldemMultiplayerSettings settings)
            => new HoldemUtterancePolicy(settings.utteranceMaximumLength, settings.utteranceMaximumPerStreet,
                HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw,
                HoldemUtteranceBatchRetention.UntilHostAcknowledges);

        internal HoldemMultiplayerDealerDebug(HoldemMultiplayerConnection connection, HoldemTableScreen screen, Func<bool> active)
        {
            this.connection = connection; this.screen = screen; this.active = active;
            if (!(connection.DealerTurns is IHoldemDealerDealApplicationPort applications) || connection.Accusations == null)
                throw new InvalidOperationException("Only the host process can own the test dealer.");
            // Do not obtain Connection.DealerCoordinator as well: this is the only completion mailbox.
            coordinator = new HoldemDealerTurnCoordinator(applications, this, TimeSpan.FromSeconds(5), () => clock);
            box = new VisualElement { name = "omc-multiplayer-dealer-debug" };
            box.AddToClassList("omc-dealer-debug"); box.AddToClassList("omc-multiplayer-dealer-debug");
            status = new Label { name = "omc-multiplayer-debug-status", enableRichText = false }; box.Add(status);
            actions = new VisualElement { name = "omc-multiplayer-debug-actions" };
            actions.AddToClassList("omc-dealer-debug-actions"); box.Add(actions);
            screen.Root.AddToClassList("dealer-debug-table"); screen.Root.Insert(1, box);
        }

        private bool CanOperate => !disposed && active() && !screen.IsProgressPaused
            && connection.IsHosting && connection.Remote?.HasGame == true && connection.Remote.CanSend;

        public void Tick()
        {
            if (disposed) return;
            actions.SetEnabled(CanOperate && !submitted);
            if (!CanOperate) return;
            var receipt = coordinator.Poll(out var work);
            if (receipt != null && receipt.Accepted)
            {
                // Keep the exact source until the authoritative card application has completed.
                if (current?.SourceUtterances != null)
                    connection.AcknowledgeUtteranceBatch(current.SourceUtterances.WindowId);
                current = null; submitted = false; actions.Clear(); screen.Render();
            }
            else if (receipt != null && receipt.Error != HoldemRoomError.Paused && receipt.Error != HoldemRoomError.Disconnected)
                throw new InvalidOperationException("Test dealer application rejected: " + receipt.Error);
            if (work != null) BuildDeal(work);

            var view = connection.Remote.Read();
            // A fold can finish the hand before there is a dealer turn to consume its closed input.
            // Discard only batches belonging to this authoritatively finished hand, never future input.
            if (view.Result != null)
                foreach (var batch in connection.ReadPendingUtterances())
                    if (batch.HandId == view.HandId) connection.AcknowledgeUtteranceBatch(batch.WindowId);
            if (view.Accusations != null && current == null)
            {
                var state = view.Accusations;
                if (renderedWindow != state.WindowId || renderedPhase != state.Phase)
                    BuildAccusations(view);
                var close = actions.Q<Button>("omc-debug-close-accusations");
                close?.SetEnabled(state.ResponseCount == state.EligibleCount);
                status.text = state.Phase == HoldemAccusationPhase.Collecting
                    ? "방장 검사 · 고발 응답 " + state.ResponseCount + "/" + state.EligibleCount + "명 · 전원 입력 후 마감"
                    : state.Phase == HoldemAccusationPhase.AwaitingVerdicts ? "마감 완료 · 실제 카드 변경 기록으로 판정하세요."
                    : state.Phase == HoldemAccusationPhase.ClosedWithoutClaims ? "모두 고발 안 함 · 아래 계속 버튼으로 진행하세요."
                    : "판정 완료 · 고발 정산은 미적용. 다른 경우는 방을 새로 만들어 확인하세요.";
            }
            else if (!view.IsDealPending && current == null)
            {
                current = null; submitted = false; renderedWindow = Guid.Empty; renderedPhase = null; closeCommand = null;
                actions.Clear();
                status.text = "방장 검사 · 베팅 전에 멘트를 보내세요. 고정 카드 / 수동 결과 / 실제 AI 아님";
            }
            else status.text = submitted ? completion ?? "카드 처리 중" : "방장 검사 · 변경 원인이 될 멘트와 결과 선택";
            actions.SetEnabled(CanOperate && !submitted);
        }

        private void BuildDeal(HoldemDealerWork work)
        {
            current = work; submitted = false; completion = null;
            renderedWindow = Guid.Empty; renderedPhase = null; closeCommand = null;
            actions.Clear();
            var labels = new List<string>();
            if (work.SourceUtterances != null)
                for (int i = 0; i < work.SourceUtterances.Count; i++)
                {
                    var entry = work.SourceUtterances.GetEntry(i);
                    labels.Add(entry.Speaker.Value + "번 멘트 #" + entry.Ordinal);
                }
            bool hasSpeech = labels.Count > 0;
            speaker = new DropdownField(labels, hasSpeech ? 0 : -1) { name = "omc-debug-source" };
            speaker.tooltip = "실제 AI 대신 방장이 변경 원인이 될 멘트를 직접 선택하는 검사입니다.";
            speaker.SetEnabled(hasSpeech); actions.Add(speaker);
            Add("변경 성공", "omc-debug-change", () => Complete(work, true, false), hasSpeech);
            Add(hasSpeech ? "요청 실패" : "조작 없이 공개", "omc-debug-unchanged", () => Complete(work, false, false));
            Add("시간 초과", "omc-debug-timeout", () => Complete(work, false, true));
        }

        private void Complete(HoldemDealerWork work, bool shouldChange, bool timeout)
        {
            if (!CanOperate || submitted || !ReferenceEquals(current, work)) return;
            var read = connection.DealerTurns.ReadPendingTurn();
            if (read.Error != HoldemRoomError.None || read.Turn == null
                || read.Turn.TargetDeal.WindowId != work.TargetDeal.WindowId || read.Turn.ExpectedVersion != work.ExpectedVersion) return;
            var source = work.SourceUtterances;
            if (shouldChange && (source == null || speaker.index < 0 || speaker.index >= source.Count)) return;
            change = shouldChange;
            selectedUtterance = shouldChange ? source.GetEntry(speaker.index).CommandId : Guid.Empty;
            if (timeout)
            {
                clock += TimeSpan.FromSeconds(5); submitted = true;
                completion = "시간 초과 처리 중 · 변경 없이 공개";
            }
            else if (source == null || source.Count == 0) submitted = coordinator.TryPostUnchanged(work);
            else
            {
                Card card = Card.FromId(44 + (int)work.TargetDeal.Street);
                var entries = new HoldemDealerInterpretation[source.Count];
                for (int i = 0; i < entries.Length; i++)
                    entries[i] = new HoldemDealerInterpretation(source.GetEntry(i).CommandId,
                        HoldemDealerIntent.CardPreference, card.Rank, card.Suit, .1, .9);
                var deal = work.TargetDeal;
                submitted = coordinator.TryPostInterpretations(work, new HoldemDealerInterpretationBatch(
                    deal.SessionId, deal.HandId, deal.WindowId, work.ExpectedVersion, deal.Street, source.WindowId, entries));
            }
            actions.SetEnabled(false);
        }

        public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
        {
            if (!change) return HoldemDealerSelection.Unchanged();
            for (int i = 0; i < interpretations.Count; i++)
            {
                var entry = interpretations.GetEntry(i);
                if (entry.UtteranceId != selectedUtterance) continue;
                int slot = work.TargetDeal.Street == HoldemStreet.Flop ? 0 : work.TargetDeal.Street == HoldemStreet.Turn ? 3 : 4;
                return HoldemDealerSelection.Change(entry.UtteranceId, slot, new Card(entry.Rank.Value, entry.Suit.Value),
                    HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            }
            throw new InvalidOperationException("Selected test source is missing.");
        }

        private void BuildAccusations(HoldemTableDisplay view)
        {
            var state = view.Accusations;
            renderedWindow = state.WindowId; renderedPhase = state.Phase; submitted = false;
            actions.Clear();
            if (state.Phase == HoldemAccusationPhase.Collecting)
                Add("고발 마감", "omc-debug-close-accusations", () => Close(view));
            else if (state.Phase == HoldemAccusationPhase.AwaitingVerdicts)
                Add("기록으로 판정", "omc-debug-resolve-accusations", () => Resolve(view));
        }

        private bool SameAccusations(HoldemTableDisplay basis, HoldemAccusationPhase phase)
        {
            if (!CanOperate) return false;
            var view = connection.Remote.Read();
            return view.SessionId == basis.SessionId && view.HandId == basis.HandId
                && view.Accusations?.WindowId == basis.Accusations.WindowId && view.Accusations.Phase == phase;
        }

        private void Close(HoldemTableDisplay basis)
        {
            if (!SameAccusations(basis, HoldemAccusationPhase.Collecting)) return;
            var view = connection.Remote.Read();
            if (view.Accusations.ResponseCount != view.Accusations.EligibleCount) return;
            // Retain the exact command through pause/disconnect. A definitive rejection permits a fresh attempt.
            if (closeCommand == null) closeCommand = new HoldemRoomAccusationClose(view.SessionId, view.HandId,
                view.Accusations.WindowId, Guid.NewGuid(), view.SessionVersion, view.Street);
            var receipt = connection.Accusations.CloseAccusations(closeCommand);
            if (!receipt.Accepted && receipt.Error != HoldemRoomError.Paused && receipt.Error != HoldemRoomError.Disconnected)
                closeCommand = null;
        }

        private void Resolve(HoldemTableDisplay basis)
        {
            if (!SameAccusations(basis, HoldemAccusationPhase.AwaitingVerdicts)) return;
            var result = connection.Accusations.ResolveAccusations();
            if (result.Error != HoldemRoomError.None && result.Error != HoldemRoomError.Paused && result.Error != HoldemRoomError.Disconnected)
                throw new InvalidOperationException("Test accusation resolution rejected: " + result.Error);
        }

        private void Add(string label, string name, Action action, bool enabled = true)
        { var button = new Button(action) { text = label, name = name }; button.SetEnabled(enabled); actions.Add(button); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; current = null; coordinator.Dispose(); box.RemoveFromHierarchy();
            screen.Root.RemoveFromClassList("dealer-debug-table");
        }
    }
}
