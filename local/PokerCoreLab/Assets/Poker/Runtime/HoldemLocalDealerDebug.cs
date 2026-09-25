using System;
using System.Collections.Generic;
using Poker.Application;
using Poker.Foundation;
using UnityEngine.UIElements;

namespace Poker.Runtime
{
    /// <summary>Explicit local fixture, owned by the host bootstrap; never a player or model API.</summary>
    internal sealed class HoldemLocalDealerDebug : IDisposable, IHoldemDealerSelectionPolicy
    {
        internal sealed class OrderedDeck : IRandomSource
        { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        internal sealed class PassiveOpponent : IHoldemOpponentPolicy
        {
            public BettingAction Choose(HoldemSnapshot view, IRandomSource random)
                => view.LegalActions.CanCheck ? BettingAction.Check() : BettingAction.Call();
        }

        private readonly HoldemLocalTable table;
        private readonly HoldemTableScreen screen;
        private readonly HoldemDealerTurnCoordinator coordinator;
        private readonly VisualElement box, actions;
        private readonly Label status;
        private HoldemDealerWork current;
        private DropdownField speaker;
        private Guid selectedUtterance, speechWindow;
        private TimeSpan clock;
        private bool disposed, submitted, change;
        private string completion, resolutionIssue;

        internal HoldemLocalDealerDebug(HoldemLocalTable table, HoldemTableScreen screen)
        {
            this.table = table; this.screen = screen;
            coordinator = new HoldemDealerTurnCoordinator(table, this, TimeSpan.FromSeconds(5), () => clock);
            box = new VisualElement { name = "omc-dealer-debug" }; box.AddToClassList("omc-dealer-debug");
            screen.Root.AddToClassList("dealer-debug-table");
            screen.Root.Insert(1, box);
            status = new Label { name = "omc-dealer-debug-status", enableRichText = false }; box.Add(status);
            actions = new VisualElement { name = "omc-dealer-debug-actions" }; actions.AddToClassList("omc-dealer-debug-actions");
            box.Add(actions);
            screen.Root.Q<Label>(className: "omc-subtitle").text = "개발용 · 고정 카드 / 체크·콜 NPC / 실제 AI 아님";
            Tick();
        }

        public void Tick()
        {
            if (disposed) return;
            actions.SetEnabled(!screen.IsProgressPaused && !submitted && resolutionIssue == null);
            if (screen.IsProgressPaused) return;
            var receipt = coordinator.Poll(out var work);
            if (receipt != null)
            {
                if (!receipt.Accepted) throw new InvalidOperationException("Test dealer application rejected: " + receipt.Error);
                current = null; actions.Clear(); screen.Render();
            }
            if (work != null)
            {
                current = work; submitted = false; completion = null; actions.Clear();
                bool hasSpeech = work.SourceUtterances != null && work.SourceUtterances.Count > 0;
                var labels = new List<string>();
                if (hasSpeech)
                    for (int i = 0; i < work.SourceUtterances.Count; i++)
                    {
                        var entry = work.SourceUtterances.GetEntry(i);
                        labels.Add(entry.Speaker.Value == 1 ? "내 멘트" : "NPC " + (entry.Speaker.Value - 1) + " 멘트");
                    }
                speaker = new DropdownField(labels, hasSpeech ? 0 : -1) { name = "omc-debug-source" };
                speaker.SetEnabled(hasSpeech); actions.Add(speaker);
                Add("변경 성공", "omc-dealer-debug-change", () => Complete(work, true, false), hasSpeech);
                Add(hasSpeech ? "요청 실패" : "조작 없이 공개", "omc-dealer-debug-unchanged", () => Complete(work, false, false),
                    tooltip: "정상 해석을 받았지만 조작 후보로 선택되지 않은 경우예요. AI 오류를 성공 응답으로 바꾸는 기능이 아니에요.");
                Add("시간 초과 재현", "omc-dealer-debug-timeout", () => Complete(work, false, true),
                    tooltip: "응답 없이 테스트 시계만 5초 진행시켜 시간 초과 처리를 확인해요.");
            }
            var view = table.Human.Read();
            var speech = table.HumanUtterances.ReadUtterances();
            bool needsVerdict = view.Accusations?.Phase == HoldemAccusationPhase.AwaitingVerdicts;
            if (needsVerdict && actions.Q<Button>("omc-debug-resolve-accusations") == null)
            {
                actions.Clear(); submitted = false;
                Guid window = view.Accusations.WindowId;
                Add("기록으로 판정", "omc-debug-resolve-accusations", () => Resolve(window));
            }
            if (current == null && speech.WindowId != Guid.Empty)
            {
                if (speechWindow != speech.WindowId || actions.Q<Button>("omc-debug-npc-speech") == null)
                {
                    speechWindow = speech.WindowId; actions.Clear(); submitted = false;
                    Guid window = speech.WindowId;
                    Add("NPC 멘트 넣기", "omc-debug-npc-speech", () => AddNpcSpeech(window));
                }
                actions.Q<Button>("omc-debug-npc-speech").SetEnabled(CanAddNpcSpeech(speech.WindowId));
            }
            actions.style.display = current != null || speech.WindowId != Guid.Empty || needsVerdict ? DisplayStyle.Flex : DisplayStyle.None;
            actions.SetEnabled(!submitted && resolutionIssue == null);
            status.text = resolutionIssue ?? (view.IsDealPending
                ? "원인이 될 멘트와 테스트 결과 선택"
                : needsVerdict ? "고발 접수 완료 · 실제 카드 변경 기록으로 판정하세요."
                : view.Accusations?.Phase == HoldemAccusationPhase.AwaitingConsequences
                    ? "판정 완료 · 벌금·팟 지급은 미적용. 다른 경우는 ‘새 게임’으로 확인하세요."
                : view.IsRevealPending ? (completion ?? "공용 카드 공개") + " 고발하거나 ‘고발 안 함’을 고르세요."
                : "혼자 고발 검사 · NPC 멘트를 넣고 베팅하세요. 실제 AI·고발 정산은 없어요.");
        }

        private void Resolve(Guid window)
        {
            if (disposed || screen.IsProgressPaused || resolutionIssue != null) return;
            var state = table.Human.Read().Accusations;
            if (state?.WindowId != window || state.Phase != HoldemAccusationPhase.AwaitingVerdicts) return;
            // The same committed card-change evidence used by multiplayer and owner feedback.
            foreach (var claim in table.GetPendingAccusations())
                if (table.ResolveRecordedAccusation(claim.ClaimId, HoldemAccusationEvidenceScope.CurrentRevealOnly)
                    != HoldemEvidenceResolution.Recorded)
                {
                    resolutionIssue = "카드 변경 기록을 확인하지 못해 판정을 멈췄어요. 현재 판은 유지됩니다.";
                    break;
                }
            screen.Render(); Tick();
        }

        private bool CanAddNpcSpeech(Guid window)
        {
            var view = table.Human.Read();
            for (int i = 0; i < view.SeatCount; i++)
            {
                var seat = view.GetSeatAt(i).Seat;
                if (seat == view.ViewerSeat) continue;
                var speech = table.BindNpcUtterances(seat).ReadUtterances();
                if (speech.CanSubmit && speech.WindowId == window) return true;
            }
            return false;
        }

        private void AddNpcSpeech(Guid window)
        {
            if (disposed || screen.IsProgressPaused || current != null) return;
            var view = table.Human.Read();
            for (int i = 0; i < view.SeatCount; i++)
            {
                var seat = view.GetSeatAt(i).Seat;
                if (seat == view.ViewerSeat) continue;
                var port = table.BindNpcUtterances(seat); var speech = port.ReadUtterances();
                if (!speech.CanSubmit || speech.WindowId != window) continue;
                var command = new HoldemUtteranceCommand(speech.SessionId, speech.HandId, window, Guid.NewGuid(),
                    speech.Street, seat, "NPC " + (seat.Value - 1) + " 테스트 멘트 · 좋은 카드 부탁해요.");
                if (!port.SubmitUtterance(command).Accepted) throw new InvalidOperationException("Test NPC speech rejected.");
            }
            screen.Render(); Tick();
        }

        private void Add(string label, string name, Action action, bool enabled = true, string tooltip = null)
        {
            var button = new Button(action) { text = label, name = name, tooltip = tooltip }; button.SetEnabled(enabled); actions.Add(button);
        }

        private void Complete(HoldemDealerWork work, bool shouldChange, bool timeout)
        {
            if (disposed || submitted || screen.IsProgressPaused || !ReferenceEquals(current, work)) return;
            var pending = table.ReadPendingTurn().Turn;
            if (pending == null || pending.TargetDeal.WindowId != work.TargetDeal.WindowId
                || pending.ExpectedVersion != work.ExpectedVersion) return;
            var source = work.SourceUtterances;
            if (shouldChange && (source == null || speaker.index < 0 || speaker.index >= source.Count)) return;
            change = shouldChange;
            selectedUtterance = shouldChange ? source.GetEntry(speaker.index).CommandId : Guid.Empty;
            if (timeout)
            {
                // Advance the test clock, not a second timer or a fabricated successful callback.
                clock += TimeSpan.FromSeconds(5); submitted = true;
                completion = "시간 초과 처리 · 카드 변경 없이 공개했어요.";
            }
            else if (source == null || source.Count == 0)
            {
                submitted = coordinator.TryPostUnchanged(work);
                completion = "멘트 없음 · 카드 변경 없이 공개했어요.";
            }
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
                completion = shouldChange ? "변경 성공 · 성공한 본인에게만 카드가 표시돼요."
                    : "요청 실패 · 해석 결과는 받았지만 카드 변경 없이 공개했어요.";
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

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; current = null; coordinator.Dispose(); box.RemoveFromHierarchy();
            screen.Root.RemoveFromClassList("dealer-debug-table");
        }
    }
}
