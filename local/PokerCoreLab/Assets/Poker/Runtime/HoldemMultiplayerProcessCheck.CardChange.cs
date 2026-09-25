using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poker.Application;
using Poker.Foundation;
using Poker.Presentation;
using UnityEngine;

namespace Poker.Runtime
{
    internal sealed partial class HoldemMultiplayerProcessCheck
    {
        // Deterministic test fixture only: reachable through batchmode + the explicit checker flag.
        // These expected owners/cards are never a game rule, model interpretation or client command.
        private bool cardChangeCheck;
        private HoldemDealerTurnCoordinator cardChangeCoordinator;
        private Task<bool> cardChangeCallback;
        private int appliedCardChanges, ownCardReveals;
        private double cardRevealAdvanceAt;
        private readonly HashSet<string> observedCardReveals = new HashSet<string>();
        private readonly HashSet<Guid> observedOwnEvents = new HashSet<Guid>();
        private readonly Dictionary<string, Guid> observedDealWindows = new Dictionary<string, Guid>();

        private sealed class OrderedCardChangeDeck : IRandomSource
        { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }

        private sealed class ScriptedCardSelection : IHoldemDealerSelectionPolicy
        {
            public HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations)
            {
                HoldemDealerInterpretation chosen = null;
                for (int i = 0; i < interpretations.Count; i++)
                {
                    var entry = interpretations.GetEntry(i);
                    if (entry.Intent != HoldemDealerIntent.CardPreference) continue;
                    if (chosen != null) throw new CheckFailure("ScriptedMultiplePreferences");
                    chosen = entry;
                }
                if (chosen == null || !chosen.Rank.HasValue || !chosen.Suit.HasValue)
                    throw new CheckFailure("ScriptedPreferenceMissing");
                int slot = work.TargetDeal.Street == HoldemStreet.Flop ? 0 : work.TargetDeal.Street == HoldemStreet.Turn ? 3 : 4;
                return HoldemDealerSelection.Change(chosen.UtteranceId, slot,
                    new Card(chosen.Rank.Value, chosen.Suit.Value), HoldemCardSourceScope.UndealtOutsideCurrentHandRunout);
            }
        }

        private int ScriptedOwner(long handNumber, HoldemStreet street)
            => (int)(((handNumber - 1) * 3 + (int)street - 1) % seatCapacity) + 1;
        private static Card ScriptedCard(HoldemStreet street) => Card.FromId(44 + (int)street);

        private bool CompleteScriptedCardChange(IHoldemDealerTurnPort dealer, HoldemTableDisplay view)
        {
            if (cardChangeCoordinator == null)
            {
                if (!(dealer is IHoldemDealerDealApplicationPort application)) throw new CheckFailure("CardChangeCapability");
                cardChangeCoordinator = new HoldemDealerTurnCoordinator(application, new ScriptedCardSelection());
            }
            if (cardChangeCallback != null)
            {
                if (!cardChangeCallback.IsCompleted) return false;
                if (!cardChangeCallback.GetAwaiter().GetResult()) throw new CheckFailure("ScriptedInterpretationPost");
                cardChangeCallback = null;
            }
            var receipt = cardChangeCoordinator.Poll(out var work);
            if (work != null)
            {
                var source = work.SourceUtterances; var d = work.TargetDeal;
                int selectedSeat = ScriptedOwner(view.HandNumber, d.Street);
                Card selectedCard = ScriptedCard(d.Street);
                // Host-only test response. The worker has no port, private cards or Unity access.
                cardChangeCallback = Task.Run(() =>
                {
                    var responses = new HoldemDealerInterpretation[source.Count];
                    for (int i = 0; i < source.Count; i++)
                    {
                        var entry = source.GetEntry(i); bool selected = entry.Speaker.Value == selectedSeat;
                        responses[i] = new HoldemDealerInterpretation(entry.CommandId,
                            selected ? HoldemDealerIntent.CardPreference : HoldemDealerIntent.NoRequest,
                            selected ? selectedCard.Rank : (Rank?)null, selected ? selectedCard.Suit : (Suit?)null, .1, .9);
                    }
                    var batch = new HoldemDealerInterpretationBatch(d.SessionId, d.HandId, d.WindowId,
                        work.ExpectedVersion, d.Street, source.WindowId, responses);
                    return cardChangeCoordinator.TryPostInterpretations(work, batch)
                        && cardChangeCoordinator.TryPostInterpretations(work, batch);
                });
            }
            if (receipt == null) return false;
            if (!receipt.Accepted) throw new CheckFailure("ScriptedCardApplication");
            appliedCardChanges++;
            return true;
        }

        private void CheckCardChangeDisplay(HoldemTableDisplay view)
        {
            if (view.IsDealPending)
            {
                var d = view.PendingDeal;
                string pendingKey = view.HandId.ToString("N") + ":" + (int)d.Street;
                if (observedDealWindows.TryGetValue(pendingKey, out var previous) && previous != d.WindowId)
                    throw new CheckFailure("ChangedPendingWindow");
                observedDealWindows[pendingKey] = d.WindowId;
            }
            if (!view.IsRevealPending)
            {
                if (view.OwnCardChange != null) throw new CheckFailure("FeedbackOutsideReveal");
                return;
            }
            string key = view.HandId.ToString("N") + ":" + (int)view.Street;
            bool ownsChange = view.ViewerSeat.Value == ScriptedOwner(view.HandNumber, view.Street);
            int slot = view.Street == HoldemStreet.Flop ? 0 : view.Street == HoldemStreet.Turn ? 3 : 4;
            var expected = ScriptedCard(view.Street);
            if (view.GetBoardCard(slot) != expected) throw new CheckFailure("ChangedCommunityCard");
            if ((view.OwnCardChange != null) != ownsChange) throw new CheckFailure("OwnerFeedbackPrivacy");
            if (!observedDealWindows.TryGetValue(key, out var expectedEvent)) throw new CheckFailure("MissedDealWindow");
            if (ownsChange && (view.OwnCardChange.BoardIndex != slot || view.OwnCardChange.Card != expected
                || view.OwnCardChange.EventId != expectedEvent)) throw new CheckFailure("OwnerFeedbackCard");
            if (!observedCardReveals.Add(key)) return;
            if (ownsChange)
            {
                if (!observedOwnEvents.Add(view.OwnCardChange.EventId)) throw new CheckFailure("ReusedCardChangeEvent");
                ownCardReveals++;
            }
            // All clients must observe all nine gates; a missed packet fails the final coverage check.
            // This delay belongs to the checker only, not the room's gameplay/accusation timing.
            if (hosting) cardRevealAdvanceAt = Time.realtimeSinceStartupAsDouble + 1;
            Debug.Log("OMC_PEER_CARD_REVEAL_OK seat=" + view.ViewerSeat.Value + " hand=" + view.HandNumber
                + " street=" + view.Street + " version=" + view.SessionVersion);
        }

        private void CheckCardChangeCompletion(HoldemTableDisplay view)
        {
            int expectedOwn = (9 + seatCapacity - view.ViewerSeat.Value) / seatCapacity;
            if (observedDealWindows.Count != 9 || observedCardReveals.Count != 9 || ownCardReveals != expectedOwn
                || hosting && appliedCardChanges != 9)
                throw new CheckFailure("CardChangeCoverage");
        }
    }
}
