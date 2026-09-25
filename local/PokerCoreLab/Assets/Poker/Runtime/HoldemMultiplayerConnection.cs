using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Runtime
{
    using Poker.Transport;

    /// <summary>Owns sockets and private reconnect identity, not game rules. Pump from one main-thread owner.</summary>
    public sealed class HoldemMultiplayerConnection : IDisposable
    {
        private HoldemTcpServer server;
        private HoldemTcpClient client;
        private HoldemClientIdentity identity;
        private PracticeRandom random;
        private Task<HoldemTcpClient> connecting;
        private IPAddress address;
        private int port;
        private bool disposed, failed;
        private HoldemDealerTurnCoordinator dealerCoordinator;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        public HoldemRemoteTablePort Remote { get; private set; }
        public string ErrorText { get; private set; } = "";
        public bool IsHosting => server != null;
        /// <summary>Only the hosting process receives this capability. It is never a player wire endpoint.</summary>
        public IHoldemDealerTurnPort DealerTurns => !disposed && !failed ? server : null;
        /// <summary>Local host authority; guests cannot close a window or supply its verdict.</summary>
        public IHoldemAccusationResolutionPort Accusations => !disposed && !failed ? server : null;
        /// <summary>
        /// Optional host-only asynchronous mailbox. Access and Poll on the game thread; worker code
        /// may only post completion. Owned by this connection, so leaving or stopping closes it.
        /// </summary>
        public HoldemDealerTurnCoordinator DealerCoordinator
        {
            get
            {
                if (DealerTurns == null) return null;
                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                    throw new InvalidOperationException("Obtain the dealer coordinator on the connection's owning game thread.");
                return dealerCoordinator ?? (dealerCoordinator = new HoldemDealerTurnCoordinator(server));
            }
        }
        public bool HasSession => identity != null;
        public bool IsConnecting => connecting != null || client != null && !client.IsAdmitted && !client.IsClosed;
        public string EndpointText => address == null ? "" : address + ":" + port;
        public int Port => port;
        internal HoldemTransportCloseReason ClientCloseReason => client?.CloseReason ?? HoldemTransportCloseReason.None;
        internal IReadOnlyList<HoldemPeerCloseRecord> ReadCloseRecords()
            => server?.ReadCloseRecords() ?? Array.Empty<HoldemPeerCloseRecord>();
        internal HoldemPeerCloseRecord LatestHostClose => server?.LatestHostClose;
        public bool CanReconnect => !failed && HasSession && !IsConnecting && (client == null || client.IsClosed && client.AdmissionError == null);
        // No transport was adopted or polled, so no admission request could have reserved a seat.
        // An admission response lost after adoption must keep the original reconnect identity.
        public bool CanEditFailedJoin => !disposed && !failed && HasSession && !IsHosting && connecting == null
            && client == null && Remote == null && !string.IsNullOrEmpty(ErrorText);

        /// <summary>Host-process integration seam. These raw batches confer no card manipulation or AI-result authority.</summary>
        public IReadOnlyList<HoldemUtteranceBatch> ReadPendingUtterances()
            => server != null ? server.ReadPendingUtterances() : throw new InvalidOperationException("Only the host process owns input batches.");

        /// <summary>Call after retaining raw input, not after an AI verdict. Never mapped to a client message.</summary>
        public bool AcknowledgeUtteranceBatch(Guid windowId)
            => server != null ? server.AcknowledgeUtteranceBatch(windowId) : throw new InvalidOperationException("Only the host process owns input batches.");

        /// <summary>An injected deck source remains caller-owned; normal rooms use owned OS randomness.</summary>
        public bool Host(string name, string ip, int requestedPort, bool trustedLan, HoldemConfig config,
            IRandomSource deckRandom = null, HoldemUtterancePolicy utterancePolicy = null, int seatCapacity = HoldemRoom.Capacity,
            HoldemAccusationEvidenceScope? accusationEvidenceScope = null)
        {
            if (disposed || HasSession) return false;
            try
            {
                if (config == null) throw new ArgumentNullException(nameof(config));
                HoldemRoomRules.ValidateSeatCapacity(seatCapacity);
                if (config.StartingStack > long.MaxValue / seatCapacity)
                { ErrorText = "참가자들의 시작 칩 합계가 너무 커요. 더 작은 금액으로 방을 만들어 주세요."; return false; }
                if (!TryReadAddress(ip, trustedLan, out var bind)) return false;
                if (trustedLan && IPAddress.IsLoopback(bind))
                { ErrorText = "다른 컴퓨터와 연결하려면 방장의 사설 IP 주소를 선택해 주세요."; return false; }
                if (requestedPort < 0 || requestedPort > 65535) throw new ArgumentException();
                var candidate = new HoldemClientIdentity(name?.Trim());
                if (deckRandom == null) deckRandom = random = new PracticeRandom();
                server = new HoldemTcpServer(candidate, config, new SeatId(1), deckRandom,
                    new IPEndPoint(bind, requestedPort), trustedLan, utterancePolicy: utterancePolicy, seatCapacity: seatCapacity,
                    accusationEvidenceScope: accusationEvidenceScope);
                identity = candidate; address = server.Endpoint.Address; port = server.Endpoint.Port;
                Connect(); return true;
            }
            catch (Exception e) when (e is ArgumentException || e is SocketException || e is InvalidOperationException
                || e is NetworkInformationException || e is NotSupportedException)
            {
                CloseSession(); ErrorText = e is SocketException
                    ? "방을 만들지 못했어요. 주소나 포트가 다른 곳에서 사용 중인지 확인해 주세요."
                    : "이름과 이 컴퓨터의 IP 주소, 포트 번호를 확인해 주세요.";
                return false;
            }
        }

        public bool Join(string name, string ip, int requestedPort, bool trustedLan)
        {
            if (disposed || HasSession) return false;
            try
            {
                if (!TryReadAddress(ip, trustedLan, out var destination)) return false;
                if (requestedPort < 1 || requestedPort > 65535) throw new ArgumentException();
                identity = new HoldemClientIdentity(name?.Trim()); address = destination; port = requestedPort;
                Connect(); return true;
            }
            catch (ArgumentException)
            { CloseSession(); ErrorText = "이름과 방장의 IP 주소, 포트 번호를 확인해 주세요."; return false; }
        }

        public void Poll()
        {
            if (disposed || failed) return;
            server?.Pump();
            if (connecting != null && connecting.IsCompleted)
            {
                var completed = connecting; connecting = null;
                try
                {
                    var replacement = completed.GetAwaiter().GetResult();
                    client?.Dispose(); client = replacement;
                    if (Remote == null) Remote = new HoldemRemoteTablePort(client, Reconnect);
                    else Remote.ReplaceConnection(client);
                    ErrorText = "";
                }
                catch (Exception e) when (e is SocketException || e is System.IO.IOException || e is ObjectDisposedException)
                { ErrorText = "방에 연결하지 못했어요. 방장이 실행 중인지, 주소와 포트가 맞는지 확인해 주세요."; }
            }
            Remote?.Poll();
        }

        public void Reconnect()
        {
            if (disposed || !CanReconnect) return;
            Connect();
        }
        private void Connect()
        {
            ErrorText = "";
            connecting = HoldemTcpClient.ConnectAsync(address, port, identity);
        }
        public void StopForError()
        {
            failed = true;
            dealerCoordinator?.Dispose(); dealerCoordinator = null;
            DiscardPendingConnection();
            Remote?.Dispose(); client?.Dispose(); server?.Dispose();
            ErrorText = "진행을 멈췄어요. 나가기를 눌러 처음 화면으로 돌아가 주세요.";
        }

        /// <summary>Called only after an explicit leave confirmation, or while disposing the application.</summary>
        public void CloseSession()
        {
            dealerCoordinator?.Dispose(); dealerCoordinator = null;
            DiscardPendingConnection();
            Remote?.Dispose(); Remote = null; client?.Dispose(); client = null;
            server?.Dispose(); server = null; random?.Dispose(); random = null;
            identity = null; address = null; port = 0; ErrorText = ""; failed = false;
        }
        private void DiscardPendingConnection()
        {
            if (connecting != null)
            {
                // A cancelled UI must not leak a socket that finishes connecting later.
                connecting.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else { var observed = task.Exception; }
                }, TaskScheduler.Default);
                connecting = null;
            }
        }
        private bool TryReadAddress(string value, bool trustedLan, out IPAddress ip)
        {
            if (!IPAddress.TryParse(value, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            { ErrorText = "IP 주소를 숫자 네 묶음으로 확인해 주세요. 예: 192.168.1.20"; return false; }
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] b = ip.GetAddressBytes();
            bool privateAddress = b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
            if (!privateAddress)
            { ErrorText = "현재는 같은 컴퓨터 또는 같은 네트워크의 사설 IP로 연결할 수 있어요."; return false; }
            if (!trustedLan)
            { ErrorText = "방장과 신뢰하는 같은 네트워크라면 '신뢰하는 같은 네트워크에서 연결'을 선택해 주세요."; return false; }
            return true;
        }
        public void Dispose() { if (disposed) return; disposed = true; CloseSession(); }
    }
}
