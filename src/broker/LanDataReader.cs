using System.IO;
using ChimpKeeper.Shared;
using Nexile.ChimpWorld.Matchmaking;

namespace JkqBroker
{
    /// <summary>
    /// Parses the IPlayerConnectionData blobs in InitialData using the game's own ChimpKeeper.Shared
    /// types so byte offsets match. Values are unused by the broker (routing is by SteamID), but the
    /// bytes must be consumed correctly.
    /// </summary>
    public sealed class LanDataReader : IPlayerConnectionDataReader
    {
        public IPlayerConnectionData ReadInitial(BinaryReader reader)
        {
            var d = new PlayerConnectionInitialData();
            ((IPlayerConnectionData)d).Read(reader);
            return d;
        }

        public IPlayerConnectionData Read(BinaryReader reader)
        {
            var d = new PlayerConnectionData();
            ((IPlayerConnectionData)d).Read(reader);
            return d;
        }
    }
}
