using System.IO;
using ChimpKeeper.Shared;
using Nexile.ChimpWorld.Matchmaking;

namespace JkqMatchmaker
{
    /// <summary>
    /// Parses the IPlayerConnectionData blobs embedded in InitialData / PlayerDataUpdate using the
    /// game's own ChimpKeeper.Shared types, so the read offsets exactly match what the client wrote.
    ///   - "Initial" field  => PlayerConnectionInitialData (version string + protocol + ping filter)
    ///   - "Data" field      => PlayerConnectionData (ushort location + float progression)
    /// We only need to consume the bytes correctly; the matchmaker does not use the values for
    /// brokering (invite-only routing is purely by SteamID).
    /// </summary>
    internal sealed class ServerSideDataReader : IPlayerConnectionDataReader
    {
        public IPlayerConnectionData ReadInitial(BinaryReader reader)
        {
            // PlayerConnectionInitialData implements IPlayerConnectionData with an explicit Read.
            var data = new PlayerConnectionInitialData();
            ((IPlayerConnectionData)data).Read(reader);
            return data;
        }

        public IPlayerConnectionData Read(BinaryReader reader)
        {
            var data = new PlayerConnectionData();
            ((IPlayerConnectionData)data).Read(reader);
            return data;
        }
    }
}
