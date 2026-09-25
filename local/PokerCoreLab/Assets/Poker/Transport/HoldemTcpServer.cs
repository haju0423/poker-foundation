using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;

namespace Poker.Transport
{
    /// <summary>
    /// Development transport for loopback or an explicitly selected trusted LAN interface. No TLS, relay or NAT traversal.
    /// Call Pump and host integration methods on one owning game thread; socket workers only handle framed bytes.
    /// </summary>
    public sealed class HoldemTcpServer : IDisposable, IHoldemDealerDealApplicationPort
    {
        private const int MaximumAdmissionIdentities = 256;
        public const int MaximumCloseRecords = 32;
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly HoldemRoom room;
        private readonly Guid sessionId;
        private readonly string hostKeyHash;
        private readonly bool publishesUtterances;
        private readonly List<Peer> peers = new List<Peer>();
        private readonly Dictionary<string, Admission> admissions = new Dictionary<string, Admission>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<HoldemPeerCloseRecord> closeRecords = new Queue<HoldemPeerCloseRecord>();
        private long closeSequence;
        private HoldemPeerCloseRecord latestHostClose;
        private bool disposed;
        public IPEndPoint Endpoint { get; }
        /// <summary>A copied, bounded host-local diagnostic view. Normal server shutdown is not a peer failure.</summary>
        public IReadOnlyList<HoldemPeerCloseRecord> ReadCloseRecords()
        { lock (gate) return Array.AsReadOnly(closeRecords.ToArray()); }
        /// <summary>Retained separately so unauthenticated traffic cannot evict the latest host observation.</summary>
        public HoldemPeerCloseRecord LatestHostClose
        { get { lock (gate) return latestHostClose; } }
        public int PendingInputCount
        {
            get { lock (gate) { int count = 0; foreach (Peer peer in peers) count += peer.Channel.PendingMessageCount; return count; } }
        }

        private sealed class Peer
        {
            public Guid Connection = Guid.NewGuid();
            public HoldemTcpChannel Channel;
            public bool Admitted;
            public int Seat;
            public bool CloseRecorded;
            public string KeyHash;
            public long SentRevision = -1, LastRateTime;
            public double Tokens = 30;
            public Guid LeftCommandId;
        }
        private sealed class Admission
        {
            public string Name, ResumeToken;
            public Guid Connection;
            public int Seat;
            public Guid LeftCommandId;
        }

        public HoldemTcpServer(HoldemClientIdentity hostIdentity, HoldemConfig config, SeatId initialButton,
            IRandomSource deckRandom, IPEndPoint endpoint = null, bool allowTrustedLan = false,
            HoldemOddChipRule oddChipRule = HoldemOddChipRule.RequireExplicitPriority,
            HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = HoldemRoom.Capacity)
        {
            if (hostIdentity == null) throw new ArgumentNullException(nameof(hostIdentity));
            ValidateUtterancePolicy(utterancePolicy, seatCapacity);
            publishesUtterances = utterancePolicy?.Visibility == HoldemUtteranceVisibility.PublicRaw;
            endpoint = endpoint ?? new IPEndPoint(IPAddress.Loopback, 0);
            ValidateBindAddress(endpoint.Address, allowTrustedLan);
            sessionId = Guid.NewGuid();
            Guid reservedHost = Guid.NewGuid();
            room = HoldemRoom.Create(sessionId, reservedHost, hostIdentity.Name, config, initialButton, deckRandom,
                out var hostAdmission, oddChipRule, utterancePolicy, seatCapacity);
            room.Disconnect(reservedHost);
            hostKeyHash = KeyHash(hostIdentity.AdmissionKey);
            admissions.Add(hostKeyHash, new Admission
            { Name = hostIdentity.Name, Seat = hostAdmission.Seat.Value, ResumeToken = hostAdmission.ResumeToken, Connection = reservedHost });
            listener = new TcpListener(endpoint);
            try { listener.Start(8); Endpoint = (IPEndPoint)listener.LocalEndpoint; }
            catch { listener.Stop(); throw; }
        }

        public void Pump()
        {
            lock (gate)
            {
                if (disposed) return;
                // Process transport closure before any queued player input, including old-socket input after reconnect.
                ObserveClosedPeers();
                for (int accepted = 0; accepted < 8 && listener.Pending(); accepted++)
                {
                    TcpClient socket = listener.AcceptTcpClient();
                    if (peers.Count >= 8) { socket.Close(); continue; }
                    peers.Add(new Peer { Channel = new HoldemTcpChannel(socket), LastRateTime = clock.ElapsedMilliseconds });
                }
                foreach (Peer peer in peers)
                {
                    for (int count = 0; count < 8 && !peer.Channel.IsClosed && peer.Channel.TryRead(out var json); count++)
                    {
                        if (!ConsumeRateToken(peer))
                        {
                            UnityEngine.Debug.LogWarning("Poker connection closed: input rate limit exceeded.");
                            ClosePeer(peer, HoldemPeerCloseTrigger.RateLimit); break;
                        }
                        Handle(peer, json);
                    }
                    if (peer.Channel.IsClosed) ClosePeer(peer);
                }
                // Never reuse one viewer's packet for another socket, even when the public revision is identical.
                foreach (Peer peer in peers)
                {
                    if (!peer.Admitted || peer.Channel.IsClosed) continue;
                    var view = room.Read(peer.Connection);
                    if (view.Revision == peer.SentRevision) continue;
                    SendState(peer, view);
                }
            }
        }

        private void Handle(Peer peer, string json)
        {
            HoldemWireRequest request;
            try { request = JsonUtility.FromJson<HoldemWireRequest>(json); }
            catch (ArgumentException) { ClosePeer(peer, HoldemPeerCloseTrigger.InvalidEnvelope); return; }
            if (request == null || request.protocol != HoldemRoomPacketMapper.ProtocolVersion || !TryId(request.id, out var id))
            { ClosePeer(peer, HoldemPeerCloseTrigger.InvalidEnvelope); return; }
            try
            {
                if (request.type == "admit") { Admit(peer, request); return; }
                if (peer.LeftCommandId != Guid.Empty)
                {
                    if (request.type == "leave" && id == peer.LeftCommandId && request.sessionId == sessionId.ToString("N"))
                        Reply(peer, request.id, HoldemRoomError.None, null);
                    else Error(peer, request.id, "LobbyLeft");
                    return;
                }
                if (!peer.Admitted) { Error(peer, request.id, "NotAdmitted"); return; }
                if (!TryId(request.sessionId, out var requestedSession) || requestedSession != sessionId)
                { Error(peer, request.id, "SessionGone"); return; }
                if (request.type == "ping") { Send(peer, Response("pong", request.id)); return; }
                if (request.type == "sync") { SendState(peer, room.Read(peer.Connection), request.id); return; }
                if (request.type == "leave")
                {
                    var error = room.LeaveLobby(peer.Connection);
                    if (error == HoldemRoomError.None)
                    {
                        var retired = admissions[peer.KeyHash];
                        retired.ResumeToken = null; retired.Name = null; retired.Seat = 0; retired.Connection = Guid.Empty;
                        retired.LeftCommandId = id;
                        peer.LeftCommandId = id; peer.Admitted = false;
                    }
                    Reply(peer, request.id, error, null); return;
                }
                if (request.type == "ready")
                {
                    var error = room.SetReady(peer.Connection, request.ready);
                    Reply(peer, request.id, error, null); return;
                }
                if (request.type == "utterance")
                {
                    if (!TryId(request.handId, out var speechHand) || !TryId(request.windowId, out var speechWindow))
                    { Error(peer, request.id, "MalformedRequest"); return; }
                    var speechResult = room.SubmitUtterance(peer.Connection, new HoldemRoomUtterance(sessionId, speechHand,
                        speechWindow, id, (HoldemStreet)request.street, request.text));
                    var response = Response("utterance-receipt", request.id);
                    response.accepted = speechResult.Accepted; response.error = speechResult.Error.ToString();
                    response.hasUtteranceReceipt = speechResult.Utterance != null;
                    response.utteranceError = speechResult.Utterance?.Error.ToString();
                    Send(peer, response); return;
                }
                if (request.version < 0) { Error(peer, request.id, "MalformedRequest"); return; }
                if (!TryId(request.handId, out var hand)) { Error(peer, request.id, "MalformedRequest"); return; }
                if (request.type == "rematch-ready")
                {
                    var error = room.SetRematchReady(peer.Connection, hand, request.version, request.rematchRevision, request.ready);
                    Reply(peer, request.id, error, null); return;
                }
                HoldemRoomReceipt result;
                switch (request.type)
                {
                    case "act":
                        result = room.Submit(peer.Connection, new HoldemRoomAction(sessionId, hand, id, request.version, Action(request)));
                        break;
                    case "start":
                        result = room.StartHand(peer.Connection, new HoldemStartCommand(sessionId, hand, id, request.version));
                        break;
                    case "rematch":
                        if (!TryId(request.completedHandId, out var completed)) { Error(peer, request.id, "MalformedRequest"); return; }
                        result = room.RestartMatch(peer.Connection, new HoldemStartCommand(sessionId, hand, id, request.version), completed);
                        break;
                    case "deal":
                        if (!TryId(request.windowId, out var window)) { Error(peer, request.id, "MalformedRequest"); return; }
                        result = room.DealUnchanged(peer.Connection, new HoldemDealCommand(sessionId, hand, window, id,
                            request.version, (HoldemStreet)request.street));
                        break;
                    case "reveal":
                        result = room.ResumeAfterReveal(peer.Connection, new HoldemRevealCommand(sessionId, hand, id,
                            request.version, (HoldemStreet)request.street));
                        break;
                    case "settle":
                        // The core settlement command does not carry a hand ID; reject a cross-hand wire request here.
                        if (room.Read(peer.Connection).Game?.HandId != hand) { Error(peer, request.id, "WrongHand"); return; }
                        result = room.ResolveSettlement(peer.Connection, id, request.version, (HoldemOddChipRule)request.oddChipRule);
                        break;
                    default: Error(peer, request.id, "UnknownMessage"); return;
                }
                Reply(peer, request.id, result.Error, result.Poker);
            }
            catch (ArgumentException) { Error(peer, request.id, "MalformedRequest"); }
        }

        private void Admit(Peer peer, HoldemWireRequest request)
        {
            if (room.SeatCapacity == 3 && !request.supportsThreePlayerRooms)
            { Error(peer, request.id, "ClientUpgradeRequired"); return; }
            if (publishesUtterances && !request.supportsPublicUtterances)
            { Error(peer, request.id, "ClientUpgradeRequired"); return; }
            if (!string.IsNullOrEmpty(request.sessionId) && request.sessionId != sessionId.ToString("N"))
            { Error(peer, request.id, "SessionGone"); return; }
            string key = KeyHash(request.admissionKey);
            if (key == null) { Error(peer, request.id, "InvalidIdentity"); return; }
            if (peer.Admitted && peer.KeyHash != key) { Error(peer, request.id, "IdentityConflict"); return; }
            if (admissions.TryGetValue(key, out var cached))
            {
                if (cached.LeftCommandId != Guid.Empty) { Error(peer, request.id, "LobbyLeft"); return; }
                if (cached.Name != request.name) { Error(peer, request.id, "IdentityConflict"); return; }
                var resumed = room.Reconnect(peer.Connection, cached.ResumeToken, request.supportsRematch);
                if (!resumed.Accepted) { Reply(peer, request.id, resumed.Error, null); return; }
                cached.Connection = peer.Connection;
            }
            else
            {
                // A known session with a missing capability must never silently create a replacement seat.
                if (!string.IsNullOrEmpty(request.sessionId)) { Error(peer, request.id, "InvalidIdentity"); return; }
                if (admissions.Count >= MaximumAdmissionIdentities) { Error(peer, request.id, "AdmissionLimit"); return; }
                var joined = room.Join(peer.Connection, request.name, request.supportsRematch);
                if (!joined.Accepted) { Reply(peer, request.id, joined.Error, null); return; }
                cached = new Admission { Name = request.name, Seat = joined.Admission.Seat.Value,
                    ResumeToken = joined.Admission.ResumeToken, Connection = peer.Connection };
                // Store before enqueueing a response: loss of the first admission response remains recoverable.
                admissions.Add(key, cached);
            }
            peer.Admitted = true; peer.KeyHash = key; peer.Seat = cached.Seat;
            var response = Response("admitted", request.id);
            response.accepted = true; response.seat = cached.Seat;
            if (!Send(peer, response)) return;
            SendState(peer, room.Read(peer.Connection));
        }

        private static BettingAction Action(HoldemWireRequest request)
        {
            if (request.action <= 2 && request.target != 0) throw new ArgumentException("Passive actions have no target.");
            switch (request.action)
            {
                case 0: return BettingAction.Fold();
                case 1: return BettingAction.Check();
                case 2: return BettingAction.Call();
                case 3: return BettingAction.BetTo(request.target);
                case 4: return BettingAction.RaiseTo(request.target);
                default: throw new ArgumentException("Unknown action.");
            }
        }

        private void Reply(Peer peer, string id, HoldemRoomError error, HoldemReceipt receipt)
        {
            var response = Response("receipt", id);
            response.accepted = error == HoldemRoomError.None; response.error = error.ToString();
            response.hasReceipt = receipt != null;
            if (receipt != null) response.receipt = new HoldemWireReceipt
            { commandId = receipt.CommandId.ToString("N"), handId = receipt.HandId.ToString("N"),
                accepted = receipt.Accepted, error = receipt.Error.ToString(), hasVersion = receipt.Version.HasValue, version = receipt.Version ?? 0 };
            Send(peer, response);
        }
        private void Error(Peer peer, string id, string error)
        { var response = Response("rejected", id); response.error = error; Send(peer, response); }
        private HoldemWireResponse Response(string type, string id) => new HoldemWireResponse
        { protocol = HoldemRoomPacketMapper.ProtocolVersion, type = type, id = id, sessionId = sessionId.ToString("N") };
        private bool Send(Peer peer, HoldemWireResponse response)
        {
            if (peer.Channel.TrySend(JsonUtility.ToJson(response))) return true;
            ClosePeer(peer, HoldemPeerCloseTrigger.SendFailed); return false;
        }
        private void SendState(Peer peer, HoldemRoomView view, string requestId = "")
        {
            var response = Response("state", requestId); response.state = HoldemRoomPacketMapper.Create(view);
            if (Send(peer, response)) peer.SentRevision = view.Revision;
        }
        private void ClosePeer(Peer peer, HoldemPeerCloseTrigger trigger = HoldemPeerCloseTrigger.ChannelObserved)
        {
            // A rejected input, the same Pump's cleanup and the next Pump can all reach here.
            // Preserve the first observation before Dispose changes an open channel to LocalClosed.
            if (!peer.CloseRecorded)
            {
                peer.CloseRecorded = true;
                var record = new HoldemPeerCloseRecord(++closeSequence, clock.ElapsedMilliseconds,
                    peer.Admitted ? peer.Seat : 0, peer.Admitted, peer.Admitted && peer.KeyHash == hostKeyHash,
                    trigger, peer.Channel.CloseReason);
                if (closeRecords.Count == MaximumCloseRecords) closeRecords.Dequeue();
                closeRecords.Enqueue(record);
                if (record.WasHost) latestHostClose = record;
            }
            // Reflect a known close in authority before processing another player's queued command.
            if (peer.Admitted) { room.Disconnect(peer.Connection); peer.Admitted = false; }
            peer.Channel.Dispose();
        }
        private void ObserveClosedPeers()
        {
            // Shared by Pump and in-process commands: Unity component order must not bypass a known close.
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                if (!peers[i].Channel.IsClosed) continue;
                ClosePeer(peers[i]); peers.RemoveAt(i);
            }
        }
        private bool ConsumeRateToken(Peer peer)
        {
            long now = clock.ElapsedMilliseconds;
            peer.Tokens = Math.Min(30, peer.Tokens + (now - peer.LastRateTime) / 100.0); peer.LastRateTime = now;
            if (peer.Tokens < 1) return false;
            peer.Tokens--; return true;
        }
        private static bool TryId(string value, out Guid id)
        {
            id = Guid.Empty;
            return value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out id) && id != Guid.Empty;
        }

        /// <summary>In-process host consumer only. No client request exposes this feed.</summary>
        public HoldemDealerTurnRead ReadPendingTurn()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return room.ReadPendingDealerTurn(admissions[hostKeyHash].Connection);
            }
        }

        /// <summary>Host-process completion; delayed or duplicate commands use the normal authority checks.</summary>
        public HoldemRoomReceipt DealUnchanged(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return room.DealUnchanged(admissions[hostKeyHash].Connection, command);
            }
        }

        /// <summary>Host-process only. Player packets cannot choose replacement cards or attribution.</summary>
        public HoldemRoomReceipt ApplyDealerDeal(HoldemDealCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                ObserveClosedPeers();
                return room.ApplyDealerDeal(admissions[hostKeyHash].Connection, command);
            }
        }

        /// <summary>In-process host consumer only. No client request exposes this feed.</summary>
        public IReadOnlyList<HoldemUtteranceBatch> ReadPendingUtterances()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                return room.ReadClosedUtteranceBatches(admissions[hostKeyHash].Connection);
            }
        }

        public bool AcknowledgeUtteranceBatch(Guid windowId)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(HoldemTcpServer));
                return room.AcknowledgeUtteranceBatch(admissions[hostKeyHash].Connection, windowId);
            }
        }

        private static void ValidateUtterancePolicy(HoldemUtterancePolicy policy, int seatCapacity)
        {
            if (policy == null) return;
            HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
            // A public room sends all remarks PLUS this viewer's separate confirmation history.
            // Bound three streets under worst-case JSON escaping (six bytes/code unit).
            // Reserve at least half the frame for room/cards/results and response metadata.
            long copies = 1 + (policy.Visibility == HoldemUtteranceVisibility.PublicRaw ? seatCapacity : 0);
            // Include the public history anchor (field name, punctuation, up to 19 decimal digits).
            long historyBytes = checked(512 + copies * 3L * policy.MaximumPerSeatPerStreet * (560 + 6L * policy.MaximumTextLength));
            if (historyBytes > HoldemFrameCodec.MaximumBytes / 2)
                throw new ArgumentException("Speech history exceeds this transport's bounded frame capacity.", nameof(policy));
        }
        private static string KeyHash(string key)
        {
            if (key == null || key.Length != 44) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(key); }
            catch (FormatException) { return null; }
            if (bytes.Length != 32 || Convert.ToBase64String(bytes) != key) return null;
            using (var hash = SHA256.Create())
            {
                string result = Convert.ToBase64String(hash.ComputeHash(bytes));
                Array.Clear(bytes, 0, bytes.Length); return result;
            }
        }

        private static void ValidateBindAddress(IPAddress address, bool allowTrustedLan)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("This transport currently uses IPv4.");
            if (IPAddress.IsLoopback(address)) return;
            byte[] b = address.GetAddressBytes();
            bool privateAddress = b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
            if (!allowTrustedLan || !privateAddress) throw new ArgumentException("Choose a specific trusted LAN interface explicitly.");
            if (HoldemLanAddresses.IsAssignedLocally(address, NetworkInterface.GetAllNetworkInterfaces)) return;
            throw new ArgumentException("The chosen address is not a local network interface.");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; listener.Stop();
                foreach (Peer peer in peers) peer.Channel.Dispose();
                peers.Clear(); admissions.Clear();
            }
        }
    }
}
