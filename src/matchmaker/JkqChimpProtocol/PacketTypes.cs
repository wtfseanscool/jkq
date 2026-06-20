using System.IO;

namespace JkqChimp.Packets
{
    // ---- id 0 ----
    public sealed class InitialData : IPacket
    {
        public PlayerConnectionSettings Settings;
        public string Location;
        public PlayerConnectionInitialData Initial = new PlayerConnectionInitialData();
        public PlayerConnectionData Data = new PlayerConnectionData();

        public PacketId Id => PacketId.InitialData;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;

        public void Read(BinaryReader r, int proto)
        {
            // Meta (0 bytes)
            Settings = PlayerConnectionSettings.Read(r);
            Location = r.ReadString();
            Initial = new PlayerConnectionInitialData();
            Initial.Read(r);
            Data = new PlayerConnectionData();
            Data.Read(r);
        }

        public void Write(BinaryWriter w, int proto)
        {
            // Meta (0 bytes)
            Settings.Write(w);
            w.Write(Location ?? "");
            Initial.Write(w);
            Data.Write(w);
        }
    }

    // ---- id 1 ----
    public sealed class InvasionHostRequest : IPacket
    {
        public ulong TargetId;
        public bool Invite;
        public int Password;

        public PacketId Id => PacketId.InvasionHostRequest;
        public ConnectionType ValidFrom => ConnectionType.Server;
        public bool SendReliable => true;

        public void Read(BinaryReader r, int proto)
        {
            TargetId = r.ReadUInt64();
            // Meta (0 bytes)
            Invite = r.ReadBoolean();
            Password = (Invite && proto >= 2) ? r.ReadInt32() : 0;
        }

        public void Write(BinaryWriter w, int proto)
        {
            w.Write(TargetId);
            // Meta (0 bytes)
            w.Write(Invite);
            if (Invite && proto >= 2) w.Write(Password);
        }
    }

    // ---- id 2 ----
    public sealed class InvasionHostResponse : IPacket
    {
        public bool Confirmed;
        public PacketId Id => PacketId.InvasionHostResponse;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => Confirmed = r.ReadBoolean();
        public void Write(BinaryWriter w, int proto) => w.Write(Confirmed);
    }

    // ---- id 3 ----
    public sealed class InvasionParticipantRequest : IPacket
    {
        public ulong TargetId;
        public bool Invite;
        public PacketId Id => PacketId.InvasionParticipantRequest;
        public ConnectionType ValidFrom => ConnectionType.Server;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto)
        {
            TargetId = r.ReadUInt64();
            // Meta (0 bytes)
            Invite = r.ReadBoolean();
        }
        public void Write(BinaryWriter w, int proto)
        {
            w.Write(TargetId);
            // Meta (0 bytes)
            w.Write(Invite);
        }
    }

    // ---- id 4 ----
    public sealed class InvasionParticipantResponse : IPacket
    {
        public bool Confirmed;
        public PacketId Id => PacketId.InvasionParticipantResponse;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => Confirmed = r.ReadBoolean();
        public void Write(BinaryWriter w, int proto) => w.Write(Confirmed);
    }

    // ---- id 5 ----
    public sealed class DisconnectRequestServer : IPacket
    {
        public ulong TargetId;
        public PacketId Id => PacketId.DisconnectRequestServer;
        public ConnectionType ValidFrom => ConnectionType.Server;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => TargetId = r.ReadUInt64();
        public void Write(BinaryWriter w, int proto) => w.Write(TargetId);
    }

    // ---- id 6 ----
    public sealed class InvasionJoinRequest : IPacket
    {
        public ulong TargetId;
        public bool Invite;
        public PacketId Id => PacketId.InvasionJoinRequest;
        public ConnectionType ValidFrom => ConnectionType.Server;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto)
        {
            TargetId = r.ReadUInt64();
            Invite = r.ReadBoolean();
        }
        public void Write(BinaryWriter w, int proto)
        {
            w.Write(TargetId);
            w.Write(Invite);
        }
    }

    // ---- id 7 ----
    public sealed class InvasionJoinResponse : IPacket
    {
        public bool Failed;
        public PacketId Id => PacketId.InvasionJoinResponse;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => Failed = r.ReadBoolean();
        public void Write(BinaryWriter w, int proto) => w.Write(Failed);
    }

    // ---- id 16 ----
    public sealed class SettingsUpdate : IPacket
    {
        public PlayerConnectionSettings Settings;
        public PacketId Id => PacketId.SettingsUpdate;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => Settings = PlayerConnectionSettings.Read(r);
        public void Write(BinaryWriter w, int proto) => Settings.Write(w);
    }

    // ---- id 17 ----
    public sealed class PlayerDataUpdate : IPacket
    {
        public PlayerConnectionData Data = new PlayerConnectionData();
        public PacketId Id => PacketId.PlayerDataUpdate;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) { Data = new PlayerConnectionData(); Data.Read(r); }
        public void Write(BinaryWriter w, int proto) => Data.Write(w);
    }

    // ---- id 18 ----
    public sealed class InviteJoin : IPacket
    {
        public ulong TargetId;
        public int Password;
        public PacketId Id => PacketId.InviteJoin;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto)
        {
            TargetId = r.ReadUInt64();
            Password = (proto >= 2) ? r.ReadInt32() : 0;
        }
        public void Write(BinaryWriter w, int proto)
        {
            w.Write(TargetId);
            if (proto >= 2) w.Write(Password);
        }
    }

    // ---- id 20 ----
    public sealed class JoinExisting : IPacket
    {
        public int Password;
        public PacketId Id => PacketId.JoinExisting;
        public ConnectionType ValidFrom => ConnectionType.Client;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) => Password = r.ReadInt32();
        public void Write(BinaryWriter w, int proto) => w.Write(Password);
    }

    // ---- id 21 ----
    public sealed class LeaveLobby : IPacket
    {
        public PacketId Id => PacketId.LeaveLobby;
        public ConnectionType ValidFrom => ConnectionType.Server;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) { }
        public void Write(BinaryWriter w, int proto) { }
    }

    // ---- id 19 ----
    public sealed class PartyState : IPacket
    {
        public ulong TargetId;
        public bool State;
        public PacketId Id => PacketId.PartyState;
        public ConnectionType ValidFrom => ConnectionType.Host;
        public bool SendReliable => true;
        public void Read(BinaryReader r, int proto) { TargetId = r.ReadUInt64(); State = r.ReadBoolean(); }
        public void Write(BinaryWriter w, int proto) { w.Write(TargetId); w.Write(State); }
    }
}
