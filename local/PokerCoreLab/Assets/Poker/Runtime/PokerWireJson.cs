using System;
using System.Text;
using Poker.Foundation;
using Poker.Presentation;
using Poker.Transport;
using UnityEngine;

namespace Poker.Runtime
{
    /// <summary>Local structured JSON adapter, not an authenticated internet transport or strict JSON parser.</summary>
    public static class PokerWireJson
    {
        public const int MaximumUtf8Bytes = 32768;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Serialize(HandCommand command) => Encode(PokerWireMapper.ToWire(command));
        public static string Serialize(HandReceipt receipt) => Encode(PokerWireMapper.ToWire(receipt));
        public static string Serialize(PokerPlayerView view) => Encode(PokerWireMapper.ToWire(view));

        public static HandCommand ReadCommand(string json) => PokerWireMapper.ToCommand(Decode<PokerWireCommand>(json));
        public static PokerWireReceipt ReadReceipt(string json)
        { var value = Decode<PokerWireReceipt>(json); PokerWireMapper.Validate(value); return value; }
        public static PokerWireSnapshot ReadSnapshot(string json)
        { var value = Decode<PokerWireSnapshot>(json); PokerWireMapper.Validate(value); return value; }

        private static string Encode<T>(T value)
        { string json = JsonUtility.ToJson(value); CheckSize(json); return json; }

        private static T Decode<T>(string json) where T : class
        {
            CheckSize(json);
            // Only object-root envelopes are supported. JsonUtility ignores unknown fields.
            string trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                throw new PokerWireException("json_root");
            try
            {
                T value = JsonUtility.FromJson<T>(json);
                if (value == null) throw new PokerWireException("json_object");
                return value;
            }
            catch (ArgumentException) { throw new PokerWireException("json_syntax"); }
        }

        private static void CheckSize(string json)
        {
            if (json == null || json.Length == 0 || json.Length > MaximumUtf8Bytes)
                throw new PokerWireException("json_size");
            try
            {
                if (Utf8.GetByteCount(json) > MaximumUtf8Bytes) throw new PokerWireException("json_size");
            }
            catch (EncoderFallbackException) { throw new PokerWireException("json_encoding"); }
        }
    }
}
