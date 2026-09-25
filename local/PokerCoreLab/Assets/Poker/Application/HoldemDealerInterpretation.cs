using System;
using System.Collections.Generic;
using Poker.Foundation;

namespace Poker.Application
{
    // Internal adapter vocabulary, not the model's agreed wire format or a success decision.
    public enum HoldemDealerIntent { NoRequest, CardPreference }

    public sealed class HoldemDealerInterpretation
    {
        public HoldemDealerInterpretation(Guid utteranceId, HoldemDealerIntent intent, Rank? rank, Suit? suit,
            double explicitness, double confidence)
        {
            if (utteranceId == Guid.Empty) throw new ArgumentException("An utterance ID is required.");
            if (!Enum.IsDefined(typeof(HoldemDealerIntent), intent)) throw new ArgumentOutOfRangeException(nameof(intent));
            if (rank.HasValue && (rank < Foundation.Rank.Two || rank > Foundation.Rank.Ace))
                throw new ArgumentOutOfRangeException(nameof(rank));
            if (suit.HasValue && (suit < Foundation.Suit.Clubs || suit > Foundation.Suit.Spades))
                throw new ArgumentOutOfRangeException(nameof(suit));
            if ((intent == HoldemDealerIntent.NoRequest) != (!rank.HasValue && !suit.HasValue))
                throw new ArgumentException("A card preference needs a rank or suit; no-request has neither.");
            CheckUnit(explicitness, nameof(explicitness)); CheckUnit(confidence, nameof(confidence));
            UtteranceId = utteranceId; Intent = intent; Rank = rank; Suit = suit;
            Explicitness = explicitness; Confidence = confidence;
        }
        public Guid UtteranceId { get; }
        public HoldemDealerIntent Intent { get; }
        public Rank? Rank { get; }
        public Suit? Suit { get; }
        /// <summary>Adapter-normalized 0..1. Neither this value nor Confidence is an application probability.</summary>
        public double Explicitness { get; }
        public double Confidence { get; }
        private static void CheckUnit(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(name);
        }
        internal bool Matches(HoldemDealerInterpretation other) => other != null && UtteranceId == other.UtteranceId
            && Intent == other.Intent && Rank == other.Rank && Suit == other.Suit
            && Explicitness == other.Explicitness && Confidence == other.Confidence;
    }

    /// <summary>Complete, immutable host-only interpretation of exactly one closed input batch.</summary>
    public sealed class HoldemDealerInterpretationBatch
    {
        private readonly HoldemDealerInterpretation[] entries;
        public HoldemDealerInterpretationBatch(Guid sessionId, Guid handId, Guid dealWindowId, long expectedVersion,
            HoldemStreet street, Guid utteranceWindowId, IReadOnlyList<HoldemDealerInterpretation> entries)
        {
            if (sessionId == Guid.Empty || handId == Guid.Empty || dealWindowId == Guid.Empty || utteranceWindowId == Guid.Empty)
                throw new ArgumentException("Full response correlation is required.");
            if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            if (street < HoldemStreet.Flop || street > HoldemStreet.River) throw new ArgumentOutOfRangeException(nameof(street));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            this.entries = new HoldemDealerInterpretation[entries.Count];
            var seen = new HashSet<Guid>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || !seen.Add(entry.UtteranceId)) throw new ArgumentException("Null or duplicate interpretation.");
                this.entries[i] = entry;
            }
            SessionId = sessionId; HandId = handId; DealWindowId = dealWindowId; ExpectedVersion = expectedVersion;
            Street = street; UtteranceWindowId = utteranceWindowId;
        }
        public Guid SessionId { get; }
        public Guid HandId { get; }
        public Guid DealWindowId { get; }
        public long ExpectedVersion { get; }
        public HoldemStreet Street { get; }
        public Guid UtteranceWindowId { get; }
        public int Count => entries.Length;
        public HoldemDealerInterpretation GetEntry(int index) => entries[index];
        internal HoldemDealerInterpretation Find(Guid id)
        { foreach (var entry in entries) if (entry.UtteranceId == id) return entry; return null; }
        internal bool MatchesWork(HoldemDealerWork work)
        {
            if (work == null || work.InputKind != HoldemDealerInputKind.ClosedBettingWindow || work.SourceUtterances == null)
                return false;
            var deal = work.TargetDeal; var source = work.SourceUtterances;
            if (SessionId != deal.SessionId || HandId != deal.HandId || DealWindowId != deal.WindowId
                || ExpectedVersion != work.ExpectedVersion || Street != deal.Street
                || UtteranceWindowId != source.WindowId || entries.Length != source.Count) return false;
            for (int i = 0; i < source.Count; i++) if (Find(source.GetEntry(i).CommandId) == null) return false;
            return true;
        }
        internal bool Matches(HoldemDealerInterpretationBatch other)
        {
            if (other == null || SessionId != other.SessionId || HandId != other.HandId || DealWindowId != other.DealWindowId
                || ExpectedVersion != other.ExpectedVersion || Street != other.Street
                || UtteranceWindowId != other.UtteranceWindowId || Count != other.Count) return false;
            foreach (var entry in entries) if (!entry.Matches(other.Find(entry.UtteranceId))) return false;
            return true;
        }
    }

    public enum HoldemDealerSelectionKind { Unchanged, Change }

    /// <summary>Explicit terminal policy decision. An absent decision is an error, never implicit no-cheating evidence.</summary>
    public sealed class HoldemDealerSelection
    {
        private HoldemDealerSelection(HoldemDealerSelectionKind kind, Guid utteranceId, int boardIndex,
            Card card, HoldemCardSourceScope sourceScope)
        { Kind = kind; UtteranceId = utteranceId; BoardIndex = boardIndex; Card = card; SourceScope = sourceScope; }
        public static HoldemDealerSelection Unchanged() => new HoldemDealerSelection(HoldemDealerSelectionKind.Unchanged,
            Guid.Empty, 0, default, default);
        public static HoldemDealerSelection Change(Guid utteranceId, int boardIndex, Card card, HoldemCardSourceScope sourceScope)
        {
            if (utteranceId == Guid.Empty || boardIndex < 0 || boardIndex > 4 || !card.IsValid
                || sourceScope != HoldemCardSourceScope.UndealtOutsideCurrentHandRunout)
                throw new ArgumentException("An explicit source, slot, card and supported source scope are required.");
            return new HoldemDealerSelection(HoldemDealerSelectionKind.Change, utteranceId, boardIndex, card, sourceScope);
        }
        public HoldemDealerSelectionKind Kind { get; }
        public Guid UtteranceId { get; }
        public int BoardIndex { get; }
        public Card Card { get; }
        public HoldemCardSourceScope SourceScope { get; }
    }

    /// <summary>Host-owner thread only. Inject a policy explicitly; this project supplies no default tie/probability rule.</summary>
    public interface IHoldemDealerSelectionPolicy
    {
        HoldemDealerSelection Select(HoldemDealerWork work, HoldemDealerInterpretationBatch interpretations);
    }

    /// <summary>The same host authority must provide both the pending turn and actual application.</summary>
    public interface IHoldemDealerDealApplicationPort : IHoldemDealerTurnPort
    {
        HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand command);
    }
}
