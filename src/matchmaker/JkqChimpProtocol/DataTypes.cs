using System.IO;

namespace JkqChimp
{
    /// <summary>
    /// PlayerMeta is empty on the wire (PlayerMeta.Write writes nothing; ctor reads nothing).
    /// </summary>
    public sealed class PlayerMeta
    {
        public static readonly PlayerMeta Empty = new PlayerMeta();
        public void Write(BinaryWriter writer) { /* 0 bytes */ }
        public static PlayerMeta Read(BinaryReader reader) => Empty;
        public override string ToString() => "PlayerMeta";
    }

    /// <summary>
    /// PlayerConnectionSettings: two booleans.
    /// </summary>
    public struct PlayerConnectionSettings
    {
        public bool CanInvade;
        public bool CanBeInvaded;

        public PlayerConnectionSettings(bool canInvade, bool canBeInvaded)
        {
            CanInvade = canInvade;
            CanBeInvaded = canBeInvaded;
        }

        public static PlayerConnectionSettings Read(BinaryReader r)
            => new PlayerConnectionSettings(r.ReadBoolean(), r.ReadBoolean());

        public void Write(BinaryWriter w)
        {
            w.Write(CanInvade);
            w.Write(CanBeInvaded);
        }

        public override string ToString() => CanInvade + "-" + CanBeInvaded;
    }

    /// <summary>
    /// Marker for the two IPlayerConnectionData shapes. The reader needs to know which to use;
    /// in InitialData the first is "Initial" (PlayerConnectionInitialData) and the second is
    /// "Data" (PlayerConnectionData). PlayerDataUpdate carries a "Data" (PlayerConnectionData).
    /// </summary>
    public interface IPlayerConnectionData
    {
        void Read(BinaryReader reader);
        void Write(BinaryWriter writer);
    }

    /// <summary>
    /// ChimpKeeper.Shared.PlayerConnectionData: ushort Location, float ProgressionValue.
    /// </summary>
    public sealed class PlayerConnectionData : IPlayerConnectionData
    {
        public ushort Location;
        public float ProgressionValue;

        public PlayerConnectionData() { }
        public PlayerConnectionData(ushort location, float value)
        {
            Location = location;
            ProgressionValue = value;
        }

        public void Read(BinaryReader reader)
        {
            Location = reader.ReadUInt16();
            ProgressionValue = reader.ReadSingle();
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Location);
            writer.Write(ProgressionValue);
        }

        public override string ToString() => $"{Location}|{ProgressionValue}";
    }

    /// <summary>
    /// ChimpKeeper.Shared.PlayerConnectionInitialData.
    /// Write: string (Version + "___"), byte ProtocolVersion, byte PingFilter.
    /// Read: string Version; if it ends with "___" read byte ProtocolVersion else 0;
    ///       if ProtocolVersion > 0 read byte PingFilter else Strict.
    /// </summary>
    public sealed class PlayerConnectionInitialData : IPlayerConnectionData
    {
        public string Version;
        public byte ProtocolVersion;
        public PingFilterPreset PingFilter;

        public PlayerConnectionInitialData() { }
        public PlayerConnectionInitialData(string version, PingFilterPreset pingFilter)
        {
            Version = version;
            ProtocolVersion = 2;
            PingFilter = pingFilter;
        }

        public void Read(BinaryReader reader)
        {
            Version = reader.ReadString();
            // The game appends a "___" sentinel on write to signal "protocol byte follows".
            // It checks EndsWith("___") to decide whether to read ProtocolVersion. We strip the
            // sentinel after the check so Version holds the logical value and re-serialization is stable.
            bool hasSentinel = Version.EndsWith("___");
            if (hasSentinel)
            {
                ProtocolVersion = reader.ReadByte();
                Version = Version.Substring(0, Version.Length - 3);
            }
            else
            {
                ProtocolVersion = 0;
            }

            if (ProtocolVersion > 0)
                PingFilter = (PingFilterPreset)reader.ReadByte();
            else
                PingFilter = PingFilterPreset.Strict;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Version + "___");
            writer.Write(ProtocolVersion);
            writer.Write((byte)PingFilter);
        }

        public override string ToString() => Version ?? "";
    }
}
