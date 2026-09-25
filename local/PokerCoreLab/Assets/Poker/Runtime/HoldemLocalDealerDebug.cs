using System;
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
        private TimeSpan clock;
        private bool disposed, submitted, change;
        private string completion;

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
            actions.SetEnabled(!screen.IsProgressPaused && !submitted);
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
                Add("변경 성공", "omc-dealer-debug-change", () => Complete(work, true, false), hasSpeech);
                Add(hasSpeech ? "요청 실패" : "조작 없이 공개", "omc-dealer-debug-unchanged", () => Complete(work, false, false),
                    tooltip: "정상 해석을 받았지만 조작 후보로 선택되지 않은 경우예요. AI 오류를 성공 응답으로 바꾸는 기능이 아니에요.");
                Add("시간 초과 재현", "omc-dealer-debug-timeout", () => Complete(work, false, true),
                    tooltip: "응답 없이 테스트 시계만 5초 진행시켜 시간 초과 처리를 확인해요.");
            }
            var view = table.Human.Read();
            actions.style.display = current != null ? DisplayStyle.Flex : DisplayStyle.None;
            actions.SetEnabled(!submitted);
            status.text = view.IsDealPending
                ? "테스트 결과 선택 · 멘트가 있어야 변경 가능"
                : view.IsRevealPending ? completion ?? "테스트 결과를 확인한 뒤 계속하세요."
                : "카드 변경 테스트 · 베팅을 마치기 전에 멘트를 보내세요. 고발 정산은 이 검사에서 실행하지 않아요.";
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
            if (shouldChange && (source == null || source.Count == 0)) return;
            change = shouldChange;
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
                completion = shouldChange ? "변경 성공 · 내 멘트로 바뀐 카드에 표시가 붙었어요."
                    : "요청 실패 · 해석 결과는 받았지만 카드 변경 없이 공개했어요.";
            }
            actions.SetEnabled(false);
        }

        public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
        {
            if (!change) return HoldemDealerSelection.Unchanged();
            var entry = interpretations.GetEntry(0);
            int slot = work.TargetDeal.Street == HoldemStreet.Flop ? 0 : work.TargetDeal.Street == HoldemStreet.Turn ? 3 : 4;
            return HoldemDealerSelection.Change(entry.UtteranceId, slot, new Card(entry.Rank.Value, entry.Suit.Value),
                HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; current = null; coordinator.Dispose(); box.RemoveFromHierarchy();
            screen.Root.RemoveFromClassList("dealer-debug-table");
        }
    }
}
