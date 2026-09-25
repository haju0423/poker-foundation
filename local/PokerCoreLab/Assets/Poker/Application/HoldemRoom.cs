using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Poker.Foundation;

namespace Poker.Application
{
    /// <summary>
    /// Server-owned three/four-player room. Connection IDs come from a trusted transport, never the request payload.
    /// This serializes authority operations; transport authentication, encryption and rate limits remain external.
    /// </summary>
    public sealed partial class HoldemRoom
    {
        public const int Capacity = 4; // Maximum and backwards-compatible default.
        public int SeatCapacity { get; }
        private readonly object gate = new object();
        private readonly List<Member> members = new List<Member>();
        private readonly Dictionary<Guid, Member> connections = new Dictionary<Guid, Member>();
        private readonly HoldemSession session;
        private readonly HoldemRoomRules rules;
        private long revision;
        private HoldemActionNotice lastAction;
        private readonly HoldemActionNotice[] seatActions;

        private sealed class Member
        {
            public SeatId Seat;
            public string Name;
            public Guid Connection;
            public bool Connected, Ready;
            public bool SupportsRematch = true, RematchReady;
            public long RematchRevision, LastRematchBasisRevision = -1;
            public bool LastRematchReady;
            public byte[] Secret;
        }

        public HoldemRoom(Guid sessionId, Guid hostConnection, string hostName, HoldemConfig config,
            SeatId initialButton, IRandomSource deckRandom,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority,
            HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = Capacity)
        {
            RequireConnection(hostConnection); RequireName(hostName);
            if (config == null) throw new ArgumentNullException(nameof(config));
            HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
            SeatCapacity = seatCapacity;
            seatActions = new HoldemActionNotice[seatCapacity];
            if (config.AccusationMode != HoldemAccusationMode.Disabled)
                throw new ArgumentException("This room currently supports base poker without accusation input.", nameof(config));
            var seats = new SeatId[seatCapacity];
            for (int i = 0; i < seats.Length; i++) seats[i] = new SeatId(i + 1);
            rules = new HoldemRoomRules(config, utterancePolicy != null, seatCapacity,
                utterancePolicy?.Visibility == HoldemUtteranceVisibility.PublicRaw);
            session = new HoldemSession(sessionId, config, seats, initialButton, deckRandom, oddChipRule);
            this.utterancePolicy = utterancePolicy;
            AddMember(hostConnection, hostName);
        }

        /// <summary>Create the room and return the host's private admission together for the transport handshake.</summary>
        public static HoldemRoom Create(Guid sessionId, Guid hostConnection, string hostName, HoldemConfig config,
            SeatId initialButton, IRandomSource deckRandom, out HoldemRoomAdmission hostAdmission,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority,
            HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = Capacity)
        {
            var room = new HoldemRoom(sessionId, hostConnection, hostName, config, initialButton, deckRandom, oddChipRule, utterancePolicy, seatCapacity);
            hostAdmission = Admit(room.members[0]).Admission;
            return room;
        }

        public HoldemRoomJoinResult Join(Guid connection, string name, bool supportsRematch = true)
        {
            RequireConnection(connection); RequireName(name);
            lock (gate)
            {
                if (connections.TryGetValue(connection, out var existing))
                    return existing.Connected ? Admit(existing) : JoinError(HoldemRoomError.Disconnected);
                if (session.CurrentHandId.HasValue) return JoinError(HoldemRoomError.AlreadyStarted);
                if (members.Count >= SeatCapacity) return JoinError(HoldemRoomError.Full);
                var member = AddMember(connection, name);
                member.SupportsRematch = supportsRematch;
                return Admit(member);
            }
        }

        public HoldemRoomError SetReady(Guid connection, bool ready)
        {
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) return error;
                if (session.CurrentHandId.HasValue) return HoldemRoomError.AlreadyStarted;
                if (member.Ready != ready) { member.Ready = ready; revision++; }
                return HoldemRoomError.None;
            }
        }

        /// <summary>Explicit pre-game guest departure. A lost connection still reserves its original seat.</summary>
        public HoldemRoomError LeaveLobby(Guid connection)
        {
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) return error;
                if (member == members[0]) return HoldemRoomError.HostCannotLeave;
                if (session.CurrentHandId.HasValue) return HoldemRoomError.AlreadyStarted;
                connections.Remove(connection); members.Remove(member);
                Array.Clear(member.Secret, 0, member.Secret.Length); revision++;
                return HoldemRoomError.None;
            }
        }

        public HoldemRoomView Read(Guid connection)
        {
            lock (gate)
            {
                var error = FindConnected(connection, out var member);
                if (error != HoldemRoomError.None) throw new InvalidOperationException("The connection has no active room binding.");
                var visible = new HoldemRoomMemberView[members.Count];
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    visible[i] = new HoldemRoomMemberView(m.Seat, m.Name, m.Connected, m.Ready, i == 0,
                        m.RematchReady, m.RematchRevision);
                }
                var game = session.CurrentHandId.HasValue ? session.GetSnapshot(member.Seat) : null;
                var currentActions = new List<HoldemActionNotice>(Capacity);
                if (game != null)
                    foreach (var action in seatActions)
                        if (action != null && action.Street == game.Street) currentActions.Add(action);
                return new HoldemRoomView(session.SessionId, revision, member.Seat, IsPaused(), visible,
                    game, lastAction, currentActions.ToArray(), ReadUtterances(connection), rules, ReadHistory(game),
                    ReadPublicRemarks(game), session.MatchNumber, SupportsRematch(),
                    session.ReadCurrentRevealCardChange(member.Seat));
            }
        }

        public HoldemRoomReceipt Submit(Guid connection, HoldemRoomAction input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            lock (gate)
            {
                var error = Guard(connection, false, out var member);
                if (error != HoldemRoomError.None) return Reject(error);
                HoldemSnapshot before = session.CurrentHandId.HasValue ? session.GetSnapshot(member.Seat) : null;
                var command = HoldemCommand.Act(input.SessionId, input.HandId, input.CommandId,
                    member.Seat, input.ExpectedVersion, input.Action);
                var receipt = session.Submit(member.Seat, command);
                if (receipt.Accepted && before != null && receipt.Version > before.SessionVersion)
                {
                    long paid = input.Action.Kind == BettingActionKind.Call ? before.LegalActions.CallAmount
                        : input.Action.Kind == BettingActionKind.BetTo || input.Action.Kind == BettingActionKind.RaiseTo
                            ? input.Action.Target - before.OwnStreetContribution : 0;
                    lastAction = new HoldemActionNotice(member.Seat, input.Action, paid, before.Street);
                    seatActions[member.Seat.Value - 1] = lastAction;
                    AppendHistory(new HoldemHistoryEntry(HoldemHistoryKind.Action, before.Street, member.Seat,
                        input.Action.Kind, paid, before.OwnStreetContribution + paid, paid > 0 && paid == before.OwnStack));
                    RecordHistoryReturns(before, session.GetSnapshot(member.Seat), member.Seat, paid);
                    SynchronizeUtterances();
                    revision++;
                }
                return Wrap(receipt);
            }
        }

        // Host controls are separate from player betting. No automatic next hand or payout policy is inferred here.
        public HoldemRoomReceipt StartHand(Guid connection, HoldemStartCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                var error = Guard(connection, true, out _);
                if (error != HoldemRoomError.None) return Reject(error);
                if (members.Count != SeatCapacity) return Reject(HoldemRoomError.WaitingForPlayers);
                if (!session.CurrentHandId.HasValue)
                    foreach (var member in members) if (!member.Ready) return Reject(HoldemRoomError.PlayersNotReady);
                // Preserve the old hand's closed input before the inbox observes a new HandId.
                SynchronizeUtterances();
                long version = session.Version;
                var previous = session.CurrentHandId.HasValue ? session.GetSnapshot(members[0].Seat) : null;
                var receipt = session.StartNextHand(command);
                if (receipt.Accepted && session.Version > version)
                { lastAction = null; Array.Clear(seatActions, 0, seatActions.Length);
                    StartHistory(previous); SynchronizeUtterances(); revision++; }
                return Wrap(receipt);
            }
        }

        public HoldemRoomReceipt DealUnchanged(Guid connection, HoldemDealCommand command)
            => HostOperation(connection, () => session.DealUnchanged(command));

        public HoldemRoomReceipt ResumeAfterReveal(Guid connection, HoldemRevealCommand command)
            => HostOperation(connection, () => session.ResumeAfterReveal(command));

        public HoldemRoomReceipt ResolveSettlement(Guid connection, Guid commandId, long expectedVersion, HoldemOddChipRule rule)
            => HostOperation(connection, () => session.ResolvePendingSettlement(commandId, expectedVersion, rule));

        /// <summary>Transport disconnect notification only. Does not fold, replace, cash out or advance any seat.</summary>
        public HoldemRoomError Disconnect(Guid connection)
        {
            lock (gate)
            {
                if (!connections.TryGetValue(connection, out var member)) return HoldemRoomError.UnknownConnection;
                if (member.Connected)
                { member.Connected = false; ClearRematchConsent(member); revision++; }
                return HoldemRoomError.None;
            }
        }

        public HoldemRoomJoinResult Reconnect(Guid newConnection, string resumeToken, bool supportsRematch = true)
        {
            RequireConnection(newConnection);
            lock (gate)
            {
                if (resumeToken == null || resumeToken.Length != 44) return JoinError(HoldemRoomError.InvalidResumeToken);
                byte[] secret;
                try { secret = Convert.FromBase64String(resumeToken ?? ""); }
                catch (FormatException) { return JoinError(HoldemRoomError.InvalidResumeToken); }
                if (secret.Length != 32) return JoinError(HoldemRoomError.InvalidResumeToken);
                Member match = null;
                foreach (var member in members) if (SameSecret(member.Secret, secret)) match = member;
                Array.Clear(secret, 0, secret.Length);
                if (match == null) return JoinError(HoldemRoomError.InvalidResumeToken);
                if (connections.TryGetValue(newConnection, out var existing))
                    return existing == match && existing.Connected ? Admit(existing) : JoinError(HoldemRoomError.ConnectionInUse);
                if (match.Connected) return JoinError(HoldemRoomError.MemberStillConnected);
                connections.Remove(match.Connection);
                match.Connection = newConnection; match.Connected = true;
                match.SupportsRematch = supportsRematch;
                connections.Add(newConnection, match); revision++;
                return Admit(match);
            }
        }

        private HoldemRoomReceipt HostOperation(Guid connection, Func<HoldemReceipt> operation)
        {
            lock (gate)
            {
                var error = Guard(connection, true, out _);
                if (error != HoldemRoomError.None) return Reject(error);
                long version = session.Version;
                var before = session.CurrentHandId.HasValue ? session.GetSnapshot(members[0].Seat) : null;
                var receipt = operation();
                if (receipt.Accepted && session.Version > version)
                { if (before != null) RecordHistoryReturns(before, session.GetSnapshot(members[0].Seat), null, 0);
                    SynchronizeUtterances(); revision++; }
                return Wrap(receipt);
            }
        }

        private HoldemRoomError Guard(Guid connection, bool hostOnly, out Member member)
        {
            var error = FindConnected(connection, out member);
            if (error != HoldemRoomError.None) return error;
            if (hostOnly && member != members[0]) return HoldemRoomError.HostOnly;
            return IsPaused() ? HoldemRoomError.Paused : HoldemRoomError.None;
        }

        private HoldemRoomError FindConnected(Guid connection, out Member member)
        {
            if (!connections.TryGetValue(connection, out member)) return HoldemRoomError.UnknownConnection;
            return member.Connected ? HoldemRoomError.None : HoldemRoomError.Disconnected;
        }

        private bool IsPaused()
        {
            // A settled final result never depends on another connection returning.
            // IsOver uses the completed hand and settled ledger, not an all-in's zero stack.
            if (session.IsOver) return false;
            foreach (var member in members)
                if (!member.Connected && (member == members[0] || !session.IsEliminatedAfterSettlement(member.Seat))) return true;
            return false;
        }

        private Member AddMember(Guid connection, string name)
        {
            var secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            int availableSeat = 1;
            while (members.Exists(m => m.Seat.Value == availableSeat)) availableSeat++;
            var member = new Member { Seat = new SeatId(availableSeat), Name = name, Connection = connection,
                Connected = true, Secret = secret };
            members.Add(member); connections.Add(connection, member); revision++;
            return member;
        }

        private static bool SameSecret(byte[] a, byte[] b)
        {
            int difference = 0;
            for (int i = 0; i < 32; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
        private static HoldemRoomJoinResult Admit(Member member)
            => new HoldemRoomJoinResult(HoldemRoomError.None, new HoldemRoomAdmission(member.Seat, Convert.ToBase64String(member.Secret)));
        private static HoldemRoomJoinResult JoinError(HoldemRoomError error) => new HoldemRoomJoinResult(error);
        private static HoldemRoomReceipt Reject(HoldemRoomError error) => new HoldemRoomReceipt(error);
        private static HoldemRoomReceipt Wrap(HoldemReceipt receipt)
            => new HoldemRoomReceipt(receipt.Accepted ? HoldemRoomError.None : HoldemRoomError.CoreRejected, receipt);
        private static void RequireConnection(Guid connection)
        { if (connection == Guid.Empty) throw new ArgumentException("A transport connection ID is required.", nameof(connection)); }
        private static void RequireName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 24)
                throw new ArgumentException("A display name of 1 to 24 characters is required.", nameof(name));
            if (!HoldemPlayerText.IsValidSingleLine(name))
                throw new ArgumentException("Display names must be valid single-line text.", nameof(name));
        }
    }
}
