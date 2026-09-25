using System;
using System.Collections.Generic;
using Poker.Application;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Detached public room membership. Contains neither cards nor admission capabilities.</summary>
    public sealed class HoldemLobbyDisplay
    {
        private readonly HoldemLobbyMember[] members;
        public Guid SessionId { get; }
        public long Revision { get; }
        public int ViewerSeat { get; }
        public bool Paused { get; }
        public bool HasGame { get; }
        public bool SupportsLobbyLeave { get; }
        public HoldemRoomRules Rules { get; }
        public int SeatCapacity => Rules?.SeatCapacity ?? HoldemRoom.Capacity;
        public int MemberCount => members.Length;
        public bool IsHost { get; }
        public bool IsReady { get; }
        public bool AllReady { get; }
        public bool HasRematch { get; }
        public bool RematchSupported { get; }
        public long MatchNumber { get; }
        public bool IsRematchReady { get; }
        public long RematchRevision { get; }
        public bool AllRematchReady { get; }
        public HoldemLobbyMember GetMember(int index) => members[index];

        public HoldemLobbyDisplay(HoldemRoomPacket packet)
        {
            if (packet == null || packet.protocol != HoldemRoomPacketMapper.ProtocolVersion
                || !Guid.TryParseExact(packet.sessionId, "N", out var session) || session == Guid.Empty
                || packet.revision < 0 || packet.viewerSeat < 1 || packet.viewerSeat > 4
                || packet.members == null || packet.members.Length < 1 || packet.members.Length > 4)
                throw new ArgumentException("Invalid lobby packet.");
            SessionId = session; Revision = packet.revision; ViewerSeat = packet.viewerSeat;
            Paused = packet.paused; HasGame = packet.hasGame; SupportsLobbyLeave = packet.supportsLobbyLeave;
            HasRematch = packet.hasRematch; RematchSupported = packet.rematchSupported; MatchNumber = packet.matchNumber;
            if (HasRematch ? MatchNumber < 1 || !HasGame && MatchNumber != 1
                : RematchSupported || MatchNumber != 0)
                throw new ArgumentException("Invalid rematch state.");
            if (packet.hasRules)
            {
                var r = packet.rules ?? throw new ArgumentException("Missing room rules.");
                Rules = new HoldemRoomRules(new HoldemConfig(r.startingStack, r.smallBlind, r.bigBlind,
                    r.pausesAfterReveal ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic,
                    dealPolicy: r.waitsForHostDeal ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic),
                    r.receivesUtterances, r.seatCapacity == 0 ? HoldemRoom.Capacity : r.seatCapacity, r.publishesUtterances);
                if (packet.hasGame && r.receivesUtterances != packet.hasOwnUtterances)
                    throw new ArgumentException("Room intake capability is inconsistent.");
                if (packet.hasPublicUtterances != (packet.hasGame && r.publishesUtterances))
                    throw new ArgumentException("Room speech visibility is inconsistent.");
            }
            else if (packet.hasPublicUtterances) throw new ArgumentException("Public speech requires room rules.");
            if (packet.viewerSeat > SeatCapacity || packet.members.Length > SeatCapacity
                || Rules != null && packet.hasGame && packet.members.Length != SeatCapacity)
                throw new ArgumentException("Room membership exceeds or disagrees with its capacity.");
            var seats = new HashSet<int>(); int hosts = 0; bool foundViewer = false, ready = packet.members.Length == SeatCapacity;
            bool terminal = packet.hasGame && packet.game?.isOver == true;
            bool rematchReady = HasRematch && RematchSupported && terminal && packet.members.Length == SeatCapacity;
            members = new HoldemLobbyMember[packet.members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                var m = packet.members[i];
                if (m == null || m.seat < 1 || m.seat > SeatCapacity || !seats.Add(m.seat)
                    || string.IsNullOrWhiteSpace(m.name) || m.name.Length > 24)
                    throw new ArgumentException("Invalid room member.");
                if (!HoldemPlayerText.IsValidSingleLine(m.name)) throw new ArgumentException("Invalid room member.");
                if (m.rematchRevision < 0 || !HasRematch && (m.rematchReady || m.rematchRevision != 0)
                    || m.rematchReady && (!terminal || !m.connected))
                    throw new ArgumentException("Invalid rematch consent.");
                if (m.host) hosts++;
                ready &= m.connected && m.ready;
                rematchReady &= m.connected && m.rematchReady;
                if (m.seat == ViewerSeat)
                { foundViewer = true; IsHost = m.host; IsReady = m.ready; IsRematchReady = m.rematchReady; RematchRevision = m.rematchRevision; }
                members[i] = new HoldemLobbyMember(m.seat, m.name, m.connected, m.ready, m.host, m.rematchReady, m.rematchRevision);
            }
            if (!foundViewer || hosts != 1) throw new ArgumentException("Invalid room membership.");
            Array.Sort(members, (a, b) => a.Seat.CompareTo(b.Seat)); AllReady = ready && !Paused;
            AllRematchReady = rematchReady && !Paused;
        }
    }

    public sealed class HoldemLobbyMember
    {
        internal HoldemLobbyMember(int seat, string name, bool connected, bool ready, bool host, bool rematchReady = false, long rematchRevision = 0)
        { Seat = seat; Name = name; Connected = connected; Ready = ready; IsHost = host;
            RematchReady = rematchReady; RematchRevision = rematchRevision; }
        public int Seat { get; }
        public string Name { get; }
        public bool Connected { get; }
        public bool Ready { get; }
        public bool IsHost { get; }
        public bool RematchReady { get; }
        public long RematchRevision { get; }
    }
}
