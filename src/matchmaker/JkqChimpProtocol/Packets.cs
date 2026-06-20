using System;
using System.IO;

namespace JkqChimp
{
    /// <summary>
    /// Packet IDs are assigned by the order of Register&lt;T&gt;() calls in
    /// MatchmakingPeer's constructor. This enum MUST preserve that exact order/value.
    /// See docs/PROTOCOL.md.
    /// </summary>
    public enum PacketId : byte
    {
        InitialData = 0,
        InvasionHostRequest = 1,
        InvasionHostResponse = 2,
        InvasionParticipantRequest = 3,
        InvasionParticipantResponse = 4,
        DisconnectRequestServer = 5,
        InvasionJoinRequest = 6,
        InvasionJoinResponse = 7,
        DisconnectRequestHost = 8,
        ParticipantFirstPairRequest = 9,
        ParticipantFirstPairResponse = 10,
        ParticipantSecondPairRequest = 11,
        ParticipantSecondPairResponse = 12,
        DisconnectedFromParticipant = 13,
        DisconnectedFromHost = 14,
        StopAsHost = 15,
        SettingsUpdate = 16,
        PlayerDataUpdate = 17,
        InviteJoin = 18,
        PartyState = 19,
        JoinExisting = 20,
        LeaveLobby = 21,
        UpdatePartyStateNotification = 22,
        PartyPassword = 23,
    }

    /// <summary>
    /// Mirrors Nexile.ChimpWorld.Matchmaking.ConnectionType. Used for documentation/validity only;
    /// the matchmaker enforces nothing here but tags outgoing packets correctly.
    /// </summary>
    public enum ConnectionType
    {
        Server,
        Host,
        Participant,
        Client,
        Dual,
    }

    /// <summary>
    /// PingFilterPreset from ChimpKeeper.Shared.
    /// </summary>
    public enum PingFilterPreset : byte
    {
        Lenient = 0,
        Normal = 1,
        Strict = 2,
    }

    /// <summary>
    /// Common contract for all matchmaking packets. Read/Write operate on the PAYLOAD only;
    /// the leading packet-id byte and any trailing transaction id are handled by Frame.
    /// </summary>
    public interface IPacket
    {
        PacketId Id { get; }
        ConnectionType ValidFrom { get; }
        bool SendReliable { get; }
        void Read(BinaryReader reader, int protocolVersion);
        void Write(BinaryWriter writer, int protocolVersion);
    }
}
