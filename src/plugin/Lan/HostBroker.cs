using System;
using System.Threading;
using JkqBroker;
using Nexile.SteamyChimp;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// Host-only: owns a listen-mode LanPeer + the LanBroker, and pumps it on a background thread so
    /// the broker is accepting connections (and relaying peer traffic) independently of the game's
    /// own networking loop. Started at plugin load when Role=Host.
    ///
    /// The broker's LanPeer binds the configured port; the host's GAME client connects to it over
    /// loopback (127.0.0.1), exactly like the joiner connects over the network.
    /// </summary>
    internal sealed class HostBroker
    {
        private readonly LanConfig _cfg;
        private readonly Action<string> _log;
        private LanPeer _peer;
        private LanBroker _broker;
        private Thread _pump;
        private volatile bool _running;

        public HostBroker(LanConfig cfg, Action<string> log)
        {
            _cfg = cfg;
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            // Broker uses a synthetic high id so it never collides with a real SteamID.
            ulong brokerId = 0UL; // the broker is "the server"; its own Id is unused by clients
            _log("[host] HostBroker.Start: creating listen-mode LanPeer + LanBroker");
            _peer = new LanPeer(brokerId, _cfg, _log, PeerMode.Listen);
            _broker = new LanBroker(_peer, _log);

            // Relay is handled inside LanPeer by default (RelayTo dest link). The broker subclass drives
            // the matchmaking handshake via the game's MatchmakingPeer machinery.
            _broker.Start();   // MatchmakingPeer.Start -> peer.Start -> BeginListen(port)

            _running = true;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "LanBrokerPump" };
            _pump.Start();
            _log($"[host] broker started on port {_cfg.Port}; pump thread running");
        }

        private void PumpLoop()
        {
            while (_running)
            {
                try { _broker.Update(); }
                catch (Exception e) { _log("[host][err] broker update: " + e.Message); }
                Thread.Sleep(16);
            }
        }

        public void Stop()
        {
            _running = false;
            try { _broker?.Stop(); } catch { }
        }
    }
}
