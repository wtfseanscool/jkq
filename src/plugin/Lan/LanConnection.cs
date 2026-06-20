using System;
using System.IO;
using Nexile.SteamyChimp;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// IConnection implementation for the LAN transport. Wraps a logical link to a remote party
    /// (or the synthetic local "server"/matchmaker connection, which has Id == 0 and IsServer == true).
    /// </summary>
    internal sealed class LanConnection : IConnection
    {
        private ulong _id;

        public ulong Id => _id;
        public bool IsServer => _id == 0UL;

        public event Action OnConnect;
        public event Action<int> OnDisconnect;

        public LanConnection(ulong id)
        {
            _id = id;
        }

        // SteamPeer re-keys the matchmaker connection from id 0 to its real SteamID after the
        // transport identity is known; we mirror that capability for parity.
        public void SetId(ulong id) => _id = id;

        internal void RaiseConnect() => OnConnect?.Invoke();
        internal void RaiseDisconnect(int reason) => OnDisconnect?.Invoke(reason);

        public override string ToString() => $"LanConn({_id})";
    }
}
