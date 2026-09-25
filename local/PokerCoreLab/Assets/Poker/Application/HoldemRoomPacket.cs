using System;
using Poker.Foundation;

namespace Poker.Application
{
    // Explicit wire fields: never serialize HoldemRoom, HoldemSession or an admission into a state broadcast.
    // Protocol 1 uses seats 1..4; 0 means no seat. Optional objects require their has* flag.
    // Card IDs use Card.Id (0..51). Public best-five cards may be evaluated for display only;
    // clients must use the host's result/payouts and never calculate authoritative settlement.
    [Serializable]
    public sealed class HoldemRoomPacket
    {
        public int protocol;
        public string sessionId;
        public long revision;
        public int viewerSeat;
        public bool paused, hasGame, hasLastAction;
        public bool supportsLobbyLeave;
        public bool hasRematch, rematchSupported;
        public long matchNumber;
        public bool hasRules;
        public HoldemRoomRulesPacket rules;
        public bool hasHistory;
        public HoldemHistoryPacket history;
        public bool hasOwnUtterances;
        public HoldemOwnUtterancePacket ownUtterances;
        public bool hasPublicUtterances;
        public HoldemPublicUtterancePacket publicUtterances;
        public bool hasOwnCardChange;
        public HoldemOwnCardChangePacket ownCardChange;
        public HoldemRoomMemberPacket[] members;
        public HoldemGamePacket game;
        public HoldemActionPacket lastAction;
    }

    // This connection's revealed success only. No source deck position, original
    // card, raw interpretation, other speaker or host record belongs on the wire.
    [Serializable]
    public sealed class HoldemOwnCardChangePacket
    {
        public string handId, dealWindowId;
        public int street, boardIndex, card;
    }

    [Serializable]
    public sealed class HoldemHistoryPacket
    {
        public long omittedCount;
        public HoldemHistoryEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemHistoryEntryPacket
    {
        public int kind, street, seat, action;
        public long amount, streetTotal;
        public bool hasAction, allIn;
    }

    [Serializable]
    public sealed class HoldemRoomRulesPacket
    {
        // Omitted/zero in older protocol-1 four-player rooms. New senders always specify 3 or 4.
        public int seatCapacity;
        public long startingStack, smallBlind, bigBlind;
        public bool waitsForHostDeal, pausesAfterReveal, receivesUtterances, publishesUtterances;
    }

    [Serializable]
    public sealed class HoldemPublicUtterancePacket
    {
        // Missing in older senders: keep the legacy separate history display in that case.
        public bool hasHistoryOrder;
        public HoldemPublicUtteranceEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemPublicUtteranceEntryPacket
    {
        public int seat, street;
        public string text;
        public long historyPosition;
    }

    // Source text confirmation only. Speaker/session/hand come from the containing bound room packet.
    // Never add host batch ordering, interpretation, future cards, or success to this projection.
    [Serializable]
    public sealed class HoldemOwnUtterancePacket
    {
        public string windowId;
        public bool canSubmit;
        public bool backlogged;
        public int maximumTextLength, remaining;
        public HoldemOwnUtteranceEntryPacket[] entries;
    }

    [Serializable]
    public sealed class HoldemOwnUtteranceEntryPacket
    {
        public string commandId, windowId, text;
        public int street;
    }

    [Serializable]
    public sealed class HoldemRoomMemberPacket
    {
        public int seat;
        public string name;
        public bool connected, ready, host;
        public bool rematchReady;
        public long rematchRevision;
    }

    [Serializable]
    public sealed class HoldemGamePacket
    {
        public string handId;
        public long version, handNumber, pot;
        public int street, settlementState, button, smallBlind, bigBlind, currentSeat, sessionWinner;
        public bool canContinue, isOver, revealPending, dealPending, hasLegal, hasResult;
        public string dealWindowId;
        public int dealStreet;
        public int[] board;
        public HoldemSeatPacket[] seats;
        public HoldemLegalPacket legal;
        public HoldemResultPacket result;
        // Optional protocol-1 display extension; an older sender omits this and uses lastAction only.
        public bool hasStreetActions;
        public HoldemActionPacket[] streetActions;
    }

    [Serializable]
    public sealed class HoldemSeatPacket
    {
        public int seat, tableIndex, status;
        public bool viewer, dealtIn, button, smallBlind, bigBlind, currentActor;
        public long stack, committed, streetContribution, awarded;
        public int[] visibleCards, revealedBestCards;
    }

    [Serializable]
    public sealed class HoldemLegalPacket
    {
        public bool fold, check, call, bet, raise;
        public long callAmount, minimumTarget, maximumTarget;
    }

    [Serializable]
    public sealed class HoldemResultPacket
    {
        public int kind, winnerSeat, foldedSeat;
        public long pot;
        public HoldemPotPacket[] pots;
    }

    [Serializable]
    public sealed class HoldemPotPacket
    {
        public long lowerBound, contributionCap, amount;
        public int[] eligibleSeats;
        public HoldemPayoutPacket[] payouts;
    }

    [Serializable]
    public sealed class HoldemPayoutPacket
    {
        public int seat;
        public long amount;
        public bool includesOddChip;
    }

    [Serializable]
    public sealed class HoldemActionPacket
    {
        public int seat, kind, street;
        public long paid, target;
    }

    public static class HoldemRoomPacketMapper
    {
        public const int ProtocolVersion = 1;

        /// <summary>Call separately for each bound connection's room view. Returns a detached payload, not authority.</summary>
        public static HoldemRoomPacket Create(HoldemRoomView source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var packet = new HoldemRoomPacket
            {
                protocol = ProtocolVersion, sessionId = source.SessionId.ToString("N"), revision = source.Revision,
                viewerSeat = source.ViewerSeat.Value, paused = source.Paused, hasGame = source.Game != null,
                supportsLobbyLeave = true, hasOwnUtterances = source.OwnUtterances != null,
                hasRematch = true, rematchSupported = source.RematchSupported, matchNumber = source.MatchNumber,
                hasPublicUtterances = source.PublicUtterances != null,
                hasOwnCardChange = source.OwnCardChange != null,
                hasRules = source.Rules != null,
                hasHistory = source.History != null,
                members = new HoldemRoomMemberPacket[source.MemberCount], hasLastAction = source.LastAction != null
            };
            if (source.OwnCardChange != null)
            {
                var own = source.OwnCardChange;
                packet.ownCardChange = new HoldemOwnCardChangePacket { handId = own.HandId.ToString("N"),
                    dealWindowId = own.DealWindowId.ToString("N"), street = (int)own.Street,
                    boardIndex = own.BoardIndex, card = own.Card.Id };
            }
            if (source.History != null)
            {
                var history = source.History;
                packet.history = new HoldemHistoryPacket { omittedCount = history.OmittedCount,
                    entries = new HoldemHistoryEntryPacket[history.Count] };
                for (int i = 0; i < history.Count; i++)
                {
                    var entry = history.GetEntry(i);
                    packet.history.entries[i] = new HoldemHistoryEntryPacket { kind = (int)entry.Kind, street = (int)entry.Street,
                        seat = entry.Seat.Value, hasAction = entry.Action.HasValue, action = (int)(entry.Action ?? 0),
                        amount = entry.Amount, streetTotal = entry.StreetTotal, allIn = entry.IsAllIn };
                }
            }
            if (source.Rules != null)
            {
                var rules = source.Rules;
                packet.rules = new HoldemRoomRulesPacket { seatCapacity = rules.SeatCapacity, startingStack = rules.StartingStack,
                    smallBlind = rules.SmallBlind, bigBlind = rules.BigBlind, waitsForHostDeal = rules.WaitsForHostDeal,
                    pausesAfterReveal = rules.PausesAfterReveal, receivesUtterances = rules.ReceivesUtterances,
                    publishesUtterances = rules.PublishesUtterances };
            }
            for (int i = 0; i < packet.members.Length; i++)
            {
                var m = source.GetMember(i);
                packet.members[i] = new HoldemRoomMemberPacket
                { seat = m.Seat.Value, name = m.Name, connected = m.Connected, ready = m.Ready, host = m.IsHost,
                    rematchReady = m.RematchReady, rematchRevision = m.RematchRevision };
            }
            if (source.Game != null)
            {
                packet.game = Game(source.Game);
                packet.game.hasStreetActions = true;
                packet.game.streetActions = new HoldemActionPacket[source.StreetActionCount];
                for (int i = 0; i < source.StreetActionCount; i++) packet.game.streetActions[i] = Action(source.GetStreetAction(i));
            }
            if (source.LastAction != null) packet.lastAction = Action(source.LastAction);
            if (source.OwnUtterances != null)
            {
                var input = source.OwnUtterances;
                packet.ownUtterances = new HoldemOwnUtterancePacket
                {
                    windowId = input.WindowId.ToString("N"), canSubmit = input.CanSubmit,
                    backlogged = input.IsBacklogged,
                    maximumTextLength = input.MaximumTextLength, remaining = input.Remaining,
                    entries = new HoldemOwnUtteranceEntryPacket[input.Count]
                };
                for (int i = 0; i < input.Count; i++)
                {
                    var entry = input.GetEntry(i);
                    packet.ownUtterances.entries[i] = new HoldemOwnUtteranceEntryPacket
                    { commandId = entry.CommandId.ToString("N"), windowId = entry.WindowId.ToString("N"),
                        text = entry.Text, street = (int)entry.Street };
                }
            }
            if (source.PublicUtterances != null)
            {
                var remarks = source.PublicUtterances;
                packet.publicUtterances = new HoldemPublicUtterancePacket
                { hasHistoryOrder = remarks.HasHistoryOrder, entries = new HoldemPublicUtteranceEntryPacket[remarks.Count] };
                for (int i = 0; i < remarks.Count; i++)
                {
                    var entry = remarks.GetEntry(i);
                    packet.publicUtterances.entries[i] = new HoldemPublicUtteranceEntryPacket
                    { seat = entry.Speaker.Value, street = (int)entry.Street, text = entry.Text,
                        historyPosition = entry.HistoryPosition ?? 0 };
                }
            }
            return packet;
        }

        private static HoldemActionPacket Action(HoldemActionNotice a) => new HoldemActionPacket
        { seat = a.Seat.Value, kind = (int)a.Kind, street = (int)a.Street, paid = a.Paid, target = a.Target };

        private static HoldemGamePacket Game(HoldemSnapshot source)
        {
            var packet = new HoldemGamePacket
            {
                handId = source.HandId.ToString("N"), version = source.SessionVersion, handNumber = source.HandNumber,
                pot = source.PotAmount, street = (int)source.Street, settlementState = (int)source.SettlementState,
                button = source.ButtonSeat.Value, smallBlind = source.SmallBlindSeat.Value, bigBlind = source.BigBlindSeat.Value,
                currentSeat = source.CurrentSeat?.Value ?? 0, sessionWinner = source.SessionWinnerSeat?.Value ?? 0,
                canContinue = source.CanContinue, isOver = source.IsOver, revealPending = source.IsRevealPending,
                dealPending = source.IsDealPending, dealWindowId = source.PendingDeal?.WindowId.ToString("N") ?? "",
                dealStreet = source.PendingDeal == null ? 0 : (int)source.PendingDeal.Street,
                hasLegal = source.LegalActions != null, hasResult = source.Result != null,
                board = Cards(source.BoardCount, source.GetBoardCard), seats = new HoldemSeatPacket[source.SeatCount]
            };
            for (int i = 0; i < packet.seats.Length; i++)
            {
                var s = source.GetSeatAt(i);
                bool publicBest = s.RevealedHandValue.HasValue && s.RevealedBestCardCount == HandEvaluator.HandSize;
                packet.seats[i] = new HoldemSeatPacket
                {
                    seat = s.Seat.Value, tableIndex = s.TableIndex, status = (int)s.Status, viewer = s.IsViewer,
                    dealtIn = s.WasDealtIn, button = s.IsButton, smallBlind = s.IsSmallBlind, bigBlind = s.IsBigBlind,
                    currentActor = s.IsCurrentActor, stack = s.Stack, committed = s.Committed,
                    streetContribution = s.StreetContribution, awarded = s.Awarded,
                    visibleCards = Cards(s.VisibleHoleCardCount, s.GetVisibleHoleCard),
                    revealedBestCards = Cards(publicBest ? HandEvaluator.HandSize : 0, s.GetRevealedBestCard)
                };
            }
            if (source.LegalActions != null)
            {
                var l = source.LegalActions;
                packet.legal = new HoldemLegalPacket
                { fold = l.CanFold, check = l.CanCheck, call = l.CanCall, bet = l.CanBet, raise = l.CanRaise,
                    callAmount = l.CallAmount, minimumTarget = l.MinimumAggressiveTarget ?? 0,
                    maximumTarget = l.MaximumAggressiveTarget ?? 0 };
            }
            if (source.Result != null) packet.result = Result(source.Result);
            return packet;
        }

        private static HoldemResultPacket Result(HoldemPublicResult source)
        {
            var packet = new HoldemResultPacket { kind = (int)source.Kind, winnerSeat = source.WinnerSeat?.Value ?? 0,
                foldedSeat = source.FoldedSeat?.Value ?? 0, pot = source.PotAmount, pots = new HoldemPotPacket[source.PotCount] };
            for (int i = 0; i < packet.pots.Length; i++)
            {
                var p = source.GetPot(i);
                var mapped = new HoldemPotPacket { lowerBound = p.LowerBound, contributionCap = p.ContributionCap,
                    amount = p.Amount, eligibleSeats = new int[p.EligibleSeatCount], payouts = new HoldemPayoutPacket[p.PayoutCount] };
                for (int j = 0; j < mapped.eligibleSeats.Length; j++) mapped.eligibleSeats[j] = p.GetEligibleSeat(j).Value;
                for (int j = 0; j < mapped.payouts.Length; j++)
                {
                    var payout = p.GetPayout(j);
                    mapped.payouts[j] = new HoldemPayoutPacket
                    { seat = payout.Seat.Value, amount = payout.Amount, includesOddChip = payout.HasOddChip };
                }
                packet.pots[i] = mapped;
            }
            return packet;
        }

        private static int[] Cards(int count, Func<int, Card> read)
        {
            var cards = new int[count];
            for (int i = 0; i < count; i++) cards[i] = read(i).Id;
            return cards;
        }
    }
}
