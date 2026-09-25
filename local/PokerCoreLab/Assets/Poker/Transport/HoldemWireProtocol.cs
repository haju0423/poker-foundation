using System;
using System.Security.Cryptography;
using Poker.Application;

namespace Poker.Transport
{
    // No seat, connection ID, or host claim is accepted from a player.
    [Serializable]
    public sealed class HoldemWireRequest
    {
        public int protocol;
        public string type, id, sessionId, handId, windowId;
        public string name, admissionKey, text;
        public long version, target;
        public int action, street, oddChipRule;
        public bool ready;
        // Compatibility declaration, not an authority or identity claim.
        public bool supportsThreePlayerRooms;
        public bool supportsPublicUtterances;
        public bool supportsRematch;
        public bool supportsAccusations;
        // Accusation target only. Zero means pass; sender identity comes from the admitted connection.
        public int accusationTarget;
        public string completedHandId;
        public long rematchRevision;
    }

    [Serializable]
    public sealed class HoldemWireResponse
    {
        public int protocol;
        public string type, id, sessionId, error;
        public int seat;
        public bool accepted, hasReceipt;
        public HoldemWireReceipt receipt;
        public HoldemRoomPacket state;
        public bool hasUtteranceReceipt;
        public string utteranceError;
    }

    [Serializable]
    public sealed class HoldemWireReceipt
    {
        public string commandId, handId, error;
        public long version;
        public bool hasVersion, accepted;
    }

    /// <summary>Private, in-memory seat identity. Keep it through reconnects; never put it in public state or logs.</summary>
    public sealed class HoldemClientIdentity
    {
        public string Name { get; }
        internal string AdmissionKey { get; }
        public string SessionId { get; internal set; }
        public int Seat { get; internal set; }

        public HoldemClientIdentity(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 24) throw new ArgumentException("Invalid name.", nameof(name));
            if (!HoldemPlayerText.IsValidSingleLine(name)) throw new ArgumentException("Invalid name.", nameof(name));
            Name = name;
            byte[] secret = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            AdmissionKey = Convert.ToBase64String(secret);
            Array.Clear(secret, 0, secret.Length);
        }

        public HoldemWireRequest AdmissionRequest() => new HoldemWireRequest
        {
            protocol = HoldemRoomPacketMapper.ProtocolVersion, type = "admit", id = Guid.NewGuid().ToString("N"),
            name = Name, admissionKey = AdmissionKey, sessionId = SessionId ?? "", supportsThreePlayerRooms = true,
            supportsPublicUtterances = true, supportsRematch = true, supportsAccusations = true
        };
    }
}
