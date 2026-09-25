using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Poker.Application;
using UnityEngine;

namespace Poker.Transport
{
    /// <summary>Packet-only client. Poll on the UI thread; it never owns a HoldemSession or predicts chip changes.</summary>
    public sealed class HoldemTcpClient : IDisposable
    {
        private readonly HoldemTcpChannel channel;
        private readonly Queue<HoldemWireResponse> responses = new Queue<HoldemWireResponse>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HoldemWireRequest admissionRequest;
        private long nextPing, nextAdmissionRetry = -1;
        private int admissionBackoff = 200;
        private bool admitted, admissionStarted;
        public HoldemClientIdentity Identity { get; }
        public bool IsAdmitted => admitted && !channel.IsClosed;
        public int BufferedMessageCount => channel.PendingMessageCount;
        public bool IsClosed => channel.IsClosed;
        public HoldemTransportCloseReason CloseReason => channel.CloseReason;
        public HoldemRoomPacket Latest { get; private set; }
        public string AdmissionError { get; private set; }

        private HoldemTcpClient(TcpClient socket, HoldemClientIdentity identity)
        {
            Identity = identity; channel = new HoldemTcpChannel(socket);
            admissionRequest = identity.AdmissionRequest();
        }

        public static async Task<HoldemTcpClient> ConnectAsync(IPAddress address, int port,
            HoldemClientIdentity identity, int timeoutMilliseconds = 5000)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            var socket = new TcpClient(address.AddressFamily);
            var transport = socket.Client;
            try
            {
                Task connect = socket.ConnectAsync(address, port);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != connect)
                {
                    // Mono's pending EndConnect callback still dereferences TcpClient.Client.
                    // Abort the underlying socket first; clear the wrapper only after observing the callback.
                    transport.Close();
                    try { await connect.ConfigureAwait(false); } catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { }
                    throw new IOException("Connection timed out.");
                }
                await connect.ConfigureAwait(false);
                return new HoldemTcpClient(socket, identity);
            }
            catch { socket.Close(); throw; }
        }

        public void Poll()
        {
            // The main-thread owner adopts a completed connection by polling it. A cancelled async
            // connect can therefore be disposed without ever reserving a room seat in the background.
            if (!admissionStarted && !IsClosed)
            { admissionStarted = true; channel.TrySend(JsonUtility.ToJson(admissionRequest)); }
            while (channel.TryRead(out var json))
            {
                HoldemWireResponse response;
                try { response = JsonUtility.FromJson<HoldemWireResponse>(json); }
                catch (ArgumentException) { Dispose(); return; }
                if (response == null || response.protocol != HoldemRoomPacketMapper.ProtocolVersion)
                { Dispose(); return; }
                if (response.type == "admitted")
                {
                    if (response.seat < 1 || response.seat > 4 || !Guid.TryParseExact(response.sessionId, "N", out var session)
                        || session == Guid.Empty || Identity.SessionId != null && Identity.SessionId != response.sessionId
                        || Identity.Seat != 0 && Identity.Seat != response.seat)
                    { Dispose(); return; }
                    Identity.SessionId = response.sessionId; Identity.Seat = response.seat; admitted = true; nextAdmissionRetry = -1;
                }
                if (!IsAdmitted && response.id == admissionRequest.id && response.error == "MemberStillConnected")
                {
                    nextAdmissionRetry = clock.ElapsedMilliseconds + admissionBackoff;
                    admissionBackoff = Math.Min(2000, admissionBackoff * 2);
                }
                else if (!IsAdmitted && response.id == admissionRequest.id && !string.IsNullOrEmpty(response.error)
                    && response.error != "None")
                {
                    AdmissionError = response.error;
                    channel.Dispose();
                }
                if (response.type == "state")
                {
                    var state = response.state;
                    if (!IsAdmitted || state == null || state.protocol != HoldemRoomPacketMapper.ProtocolVersion
                        || state.sessionId != Identity.SessionId || state.viewerSeat != Identity.Seat)
                    { Dispose(); return; }
                    if (Latest == null || state.revision > Latest.revision) Latest = state;
                    if (string.IsNullOrEmpty(response.id)) continue;
                }
                if (response.type == "pong") continue;
                if (responses.Count >= 64) { Dispose(); return; }
                responses.Enqueue(response);
            }
            if (IsAdmitted && !IsClosed && clock.ElapsedMilliseconds >= nextPing)
            {
                nextPing = clock.ElapsedMilliseconds + 2000;
                channel.TrySend(JsonUtility.ToJson(new HoldemWireRequest { protocol = HoldemRoomPacketMapper.ProtocolVersion,
                    type = "ping", id = Guid.NewGuid().ToString("N"), sessionId = Identity.SessionId }));
            }
            if (!IsAdmitted && !IsClosed && nextAdmissionRetry >= 0 && clock.ElapsedMilliseconds >= nextAdmissionRetry)
            {
                if (clock.ElapsedMilliseconds >= 45000) { Dispose(); return; }
                nextAdmissionRetry = -1;
                channel.TrySend(JsonUtility.ToJson(admissionRequest));
            }
        }

        /// <summary>The returned request ID must be reused after an uncertain result; do not invent a replacement action.</summary>
        public string Send(HoldemWireRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.type == "ping" || request.type == "admit") throw new ArgumentException("Admission and heartbeat are managed internally.", nameof(request));
            if (!IsAdmitted || IsClosed) return null;
            request.protocol = HoldemRoomPacketMapper.ProtocolVersion;
            request.id = request.id ?? Guid.NewGuid().ToString("N");
            request.sessionId = request.sessionId ?? Identity.SessionId;
            return channel.TrySend(JsonUtility.ToJson(request)) ? request.id : null;
        }

        public bool TryReadResponse(out HoldemWireResponse response)
        {
            if (responses.Count == 0) { response = null; return false; }
            response = responses.Dequeue(); return true;
        }

        public void Dispose() { channel.Dispose(); admitted = false; }
    }
}
