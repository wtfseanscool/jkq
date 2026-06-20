using System;
using System.IO;
using System.Text;

namespace JkqChimp
{
    /// <summary>
    /// Encodes/decodes the ChimpWorld message envelope:
    ///   [byte packetId][payload][optional ulong transactionId]
    ///
    /// Matches SteamyChimp.Messenger + MatchmakingPeer.Send/Respond. BinaryWriter/BinaryReader
    /// are used to get identical .NET serialization (little-endian, 7-bit length-prefixed strings).
    /// </summary>
    public static class Frame
    {
        // UTF8 without BOM; BinaryWriter default uses UTF8 no-BOM and 7-bit-encoded length, matching the game.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Serialize a packet (no transaction id).
        /// </summary>
        public static byte[] Encode(IPacket packet, int protocolVersion = 2)
            => Encode(packet, false, 0, protocolVersion);

        /// <summary>
        /// Serialize a packet with an appended transaction id.
        /// </summary>
        public static byte[] EncodeWithTransaction(IPacket packet, ulong transactionId, int protocolVersion = 2)
            => Encode(packet, true, transactionId, protocolVersion);

        private static byte[] Encode(IPacket packet, bool withTransaction, ulong transactionId, int protocolVersion)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Utf8NoBom, leaveOpen: true))
            {
                w.Write((byte)packet.Id);
                packet.Write(w, protocolVersion);
                if (withTransaction) w.Write(transactionId);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Read just the leading packet id without consuming the rest.
        /// </summary>
        public static PacketId PeekId(byte[] data)
        {
            if (data == null || data.Length < 1)
                throw new InvalidDataException("Empty message.");
            return (PacketId)data[0];
        }

        /// <summary>
        /// Decode a message into a packet of type T plus the trailing transaction id, if present.
        /// hasTransaction tells the decoder whether to read a trailing ulong (the client only appends
        /// one for respondable/transactional sends).
        /// </summary>
        public static T Decode<T>(byte[] data, bool hasTransaction, out ulong transactionId, int protocolVersion = 2)
            where T : IPacket, new()
        {
            transactionId = 0;
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms, Utf8NoBom))
            {
                byte id = r.ReadByte();
                var packet = new T();
                if ((byte)packet.Id != id)
                    throw new InvalidDataException($"Expected packet id {(byte)packet.Id} ({packet.Id}) but got {id}.");
                packet.Read(r, protocolVersion);
                if (hasTransaction)
                {
                    if (ms.Length - ms.Position >= 8)
                        transactionId = r.ReadUInt64();
                    else
                        throw new InvalidDataException("Expected a trailing transaction id but the message was too short.");
                }
                return packet;
            }
        }

        /// <summary>
        /// Decode the trailing transaction id from a raw message whose payload we don't need to parse.
        /// Used when relaying: we only need the id at the very end.
        /// </summary>
        public static ulong ReadTrailingTransactionId(byte[] data)
        {
            if (data.Length < 8) throw new InvalidDataException("Message too short to contain a transaction id.");
            // little-endian ulong from the last 8 bytes
            return BitConverter.ToUInt64(data, data.Length - 8);
        }
    }
}
