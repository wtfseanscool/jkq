using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Nexile.SteamyChimp;

namespace JKQLocalMM.Lan
{
    internal enum PeerMode { Connect, Listen }

    /// <summary>
    /// Custom ISteamPeer transport over TCP. Two modes:
    ///   - Connect (the game's client): dials the broker at host:port. The broker link is the "server"
    ///     connection (id 0) for the matchmaking handshake. Peer (ConnectP2P) traffic is relayed THROUGH
    ///     the broker, tagged with the destination SteamID.
    ///   - Listen (the host's broker): binds a port, accepts client links. Exposes OnServerMessage and a
    ///     relay hook so LanBroker can run the matchmaking handshake and forward peer traffic.
    ///
    /// Frame format on the wire (inside TcpLink's length-prefixed payload):
    ///   [byte channel][channel-specific body]
    ///     CH_HELLO  : [ulong selfSteamId]
    ///     CH_SERVER : [ChimpWorld message bytes]                      (client <-> broker matchmaking)
    ///     CH_RELAY  : [ulong otherId][ChimpWorld message bytes]       (peer traffic; otherId = dest on send, src on recv)
    /// </summary>
    internal sealed class LanPeer : ISteamPeer
    {
        private const byte CH_HELLO  = 0x02;
        private const byte CH_SERVER = 0x03;
        private const byte CH_RELAY  = 0x04;

        private readonly ulong _selfId;
        private readonly LanConfig _cfg;
        private readonly Action<string> _log;
        private readonly PeerMode _mode;

        // Connect-mode: the single link to the broker.
        private TcpLink _brokerLink;

        // Listen-mode: accept loop + client links keyed by remote SteamID.
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private readonly List<TcpLink> _pending = new List<TcpLink>();
        private readonly ConcurrentDictionary<ulong, TcpLink> _clientLinks = new ConcurrentDictionary<ulong, TcpLink>();

        // Connections surfaced to the game (id 0 = broker/server; others = virtual peers).
        private readonly ConcurrentDictionary<ulong, LanConnection> _connections = new ConcurrentDictionary<ulong, LanConnection>();
        private LanConnection _serverConn;
        private bool _readyFired;

        // Peer connections whose OnConnect must fire on a LATER Update pump, not synchronously inside
        // ConnectP2P/ExpectConnectionFrom. The game's OnInvasionJoinAction.Perform calls ConnectP2P(id)
        // and only AFTER that does player.SetMeta(meta); its Player.OnConnect throws if Meta is still
        // null. Firing OnConnect immediately races ahead of SetMeta. Deferring one pump fixes it and
        // matches real P2P semantics (connection completes asynchronously).
        private readonly ConcurrentQueue<ulong> _pendingConnect = new ConcurrentQueue<ulong>();

        // Listen-mode hooks (set by LanBroker).
        public event Action<ulong, TcpLink> OnClientIdentified;        // a client announced its id
        public event Action<ulong> OnClientGone;
        /// <summary>Relay a peer frame: (srcId, destId, payload). Default routes to the dest client link.</summary>
        public Func<ulong, ulong, byte[], bool> RelayHandler;

        public LanPeer(ulong selfId, LanConfig cfg, Action<string> log, PeerMode mode)
        {
            _selfId = selfId;
            _cfg = cfg;
            _log = log ?? (_ => { });
            _mode = mode;
        }

        // ---------------- ISteamPeer props/events ----------------
        public DateTime Now => DateTime.UtcNow;
        public ulong Id => _selfId;
        public event ISteamPeer.OnConnectionDelegate OnConnectionAdded;
        public event ISteamPeer.OnConnectionDelegate OnConnectionRemoved;
        public event ISteamPeer.OnMessageDelegate OnMessage;
        public event Action<bool> OnReady;

        // ---------------- lifecycle ----------------
        public void Start()
        {
            _running = true;
            _log($"LanPeer.Start mode={_mode} selfId={_selfId}");
            if (_mode == PeerMode.Listen)
            {
                // Broker opens its socket here.
                BeginListen(_cfg.Port);
            }
            if (!_readyFired)
            {
                _readyFired = true;
                OnReady?.Invoke(true);
            }
        }

        public void Close()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _brokerLink?.Dispose();
            foreach (var l in _clientLinks.Values) l.Dispose();
            lock (_pending) { foreach (var l in _pending) l.Dispose(); _pending.Clear(); }
            _clientLinks.Clear();
            _connections.Clear();
        }

        // ---------------- socket setup ----------------

        // Game (client) opens its P2P listen socket after connecting to matchmaker; for us this is a no-op
        // because peer traffic is relayed through the broker link we already have.
        public void CreateP2PSocket(int virtualPort)
        {
            _log($"CreateP2PSocket({virtualPort}) -> no-op (peer traffic relays via broker)");
            EnsureServerConn();
        }

        // Only the broker (listen mode) uses this; the game never calls it.
        public void CreateIPSocket(ushort port)
        {
            _log($"CreateIPSocket({port})");
            if (_mode == PeerMode.Listen) BeginListen(port);
        }

        private void BeginListen(ushort port)
        {
            if (_listener != null) return;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LanAccept" };
                _acceptThread.Start();
                _log($"[broker] listening on TCP {port}");
            }
            catch (Exception e) { _log($"[err] listen {port}: {e.Message}"); }
        }

        // Game (client) dials the broker.
        public void ConnectIP(string ip, ushort port)
        {
            _log($"ConnectIP({ip}:{port})");
            EnsureServerConn();
            try
            {
                var client = new TcpClient();
                client.Connect(ip, port);
                _brokerLink = new TcpLink(client) { Log = _log };
                _brokerLink.OnClosed += _ => OnBrokerLinkClosed();
                SendHello(_brokerLink);
                _log($"[client] connected to broker {ip}:{port}");
                // The broker connection (server, id 0) is now live for the handshake.
                _serverConn.RaiseConnect();
            }
            catch (Exception e) { _log($"[err] connect {ip}:{port}: {e.Message}"); }
        }

        // Peer connect/expect: create a virtual connection routed through the broker relay.
        public void ConnectP2P(ulong id, int virtualPort)
        {
            _log($"[peer] ConnectP2P({id}) vport={virtualPort} -> virtual peer (relayed via broker)");
            var conn = _connections.GetOrAdd(id, x => new LanConnection(x));
            OnConnectionAdded?.Invoke(conn);
            // Defer OnConnect to the next Update pump (see _pendingConnect). Raising it synchronously
            // here fires Player.OnConnect before the game's Perform() calls SetMeta -> "no PlayerMeta".
            _pendingConnect.Enqueue(id);
            _log($"[peer] ConnectP2P({id}): connection surfaced; OnConnect deferred to next pump");
        }

        public void ExpectConnectionFrom(ulong id)
        {
            _log($"[peer] ExpectConnectionFrom({id}) -> virtual peer (relayed via broker)");
            var conn = _connections.GetOrAdd(id, x => new LanConnection(x));
            OnConnectionAdded?.Invoke(conn);
            _pendingConnect.Enqueue(id);
            _log($"[peer] ExpectConnectionFrom({id}): connection surfaced; OnConnect deferred to next pump");
        }

        public void DisconnectFrom(ulong id, int reason)
        {
            _log($"DisconnectFrom({id}, {reason})");
            if (_connections.TryRemove(id, out var conn))
            {
                conn.RaiseDisconnect(reason);
                OnConnectionRemoved?.Invoke(conn);
            }
        }

        // ---------------- send ----------------
        public void Send(IConnection connection, Action<BinaryWriter> func, bool reliable)
        {
            byte[] msg;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, new UTF8Encoding(false)))
            {
                func(w); w.Flush(); msg = ms.ToArray();
            }

            byte pktId = msg.Length > 0 ? msg[0] : (byte)255;

            if (_mode == PeerMode.Connect)
            {
                if (connection.IsServer)
                {
                    // matchmaking handshake to the broker
                    if (_brokerLink == null) { _log($"[client][warn] Send(server, pktId={pktId}) but brokerLink is null"); return; }
                    _log($"[client] -> SERVER pktId={pktId} len={msg.Length}");
                    SendFramed(_brokerLink, CH_SERVER, msg);
                }
                else
                {
                    // peer traffic: relay through broker, tagged with destination id
                    if (_brokerLink == null) { _log($"[client][warn] Send(relay->{connection.Id}, pktId={pktId}) but brokerLink is null"); return; }
                    _log($"[client] -> RELAY dest={connection.Id} pktId={pktId} len={msg.Length}");
                    SendRelay(_brokerLink, connection.Id, msg);
                }
            }
            else // Listen (broker) sending as the "server" to a specific client
            {
                if (_clientLinks.TryGetValue(connection.Id, out var link))
                {
                    _log($"[broker] -> client {connection.Id} pktId={pktId} len={msg.Length}");
                    SendFramed(link, CH_SERVER, msg);
                }
                else
                {
                    _log($"[broker][warn] Send to {connection.Id} pktId={pktId}: NO client link (known: {string.Join(",", _clientLinks.Keys)})");
                }
            }
        }

        private static void SendFramed(TcpLink link, byte channel, byte[] body)
        {
            if (link == null) return;
            var framed = new byte[1 + body.Length];
            framed[0] = channel;
            Buffer.BlockCopy(body, 0, framed, 1, body.Length);
            link.Send(framed);
        }

        private static void SendRelay(TcpLink link, ulong otherId, byte[] body)
        {
            if (link == null) return;
            var framed = new byte[1 + 8 + body.Length];
            framed[0] = CH_RELAY;
            BitConverter.GetBytes(otherId).CopyTo(framed, 1);
            Buffer.BlockCopy(body, 0, framed, 9, body.Length);
            link.Send(framed);
        }

        /// <summary>Broker uses this to forward a relayed frame to a destination client.</summary>
        public bool RelayTo(ulong destId, ulong srcId, byte[] payload)
        {
            if (_clientLinks.TryGetValue(destId, out var link))
            {
                SendRelay(link, srcId, payload);
                return true;
            }
            return false;
        }

        // ---------------- receive / update ----------------
        public void Update()
        {
            // Fire any deferred peer OnConnect callbacks first. By now the game's Perform() that called
            // ConnectP2P/ExpectConnectionFrom has returned and set the player's Meta, so Player.OnConnect
            // won't throw "no PlayerMeta".
            while (_pendingConnect.TryDequeue(out var pid))
            {
                if (_connections.TryGetValue(pid, out var pconn))
                {
                    _log($"[peer] firing deferred OnConnect for {pid}");
                    pconn.RaiseConnect();
                }
            }

            if (_mode == PeerMode.Connect)
            {
                PumpLink(_brokerLink, isBrokerLink: true);
            }
            else
            {
                // Pump all links (pending + identified). fromId is read LIVE from link.RemoteId.
                PumpPending();
                foreach (var kv in _clientLinks) PumpLink(kv.Value, isBrokerLink: false);
            }
        }

        private void PumpLink(TcpLink link, bool isBrokerLink)
        {
            if (link == null) return;
            while (link.TryDequeue(out var frame))
            {
                if (frame.Length < 1) continue;
                byte ch = frame[0];
                switch (ch)
                {
                    case CH_HELLO:
                        HandleHello(link, frame);
                        break;
                    case CH_SERVER:
                        if (isBrokerLink)
                        {
                            // client receiving from the broker = the "server" (id 0).
                            DispatchServer(0UL, frame, 1);
                        }
                        else
                        {
                            // broker receiving from a client. Must know the client's SteamID first.
                            if (link.RemoteId == 0)
                            {
                                _log("[broker][warn] CH_SERVER before HELLO; re-queueing");
                                // Re-queue by handling HELLO opportunistically: drop is unsafe, so
                                // we requeue this frame to the front by processing after a HELLO scan.
                                link.Requeue(frame);
                                return; // stop draining; next Update will retry after HELLO lands
                            }
                            DispatchServer(link.RemoteId, frame, 1);
                        }
                        break;
                    case CH_RELAY:
                        HandleRelay(link, isBrokerLink, link.RemoteId, frame);
                        break;
                }
            }
        }

        private void DispatchServer(ulong fromId, byte[] frame, int offset)
        {
            var conn = _connections.GetOrAdd(fromId, x => new LanConnection(x));
            using (var ms = new MemoryStream(frame, offset, frame.Length - offset, false))
            using (var r = new BinaryReader(ms, new UTF8Encoding(false)))
            {
                try { OnMessage?.Invoke(conn, r); }
                catch (Exception e) { _log($"[err] OnMessage(server) from {fromId}: {e.Message}"); }
            }
        }

        private void HandleRelay(TcpLink link, bool isBrokerLink, ulong fromLinkId, byte[] frame)
        {
            if (frame.Length < 9) { _log("[warn] CH_RELAY frame too short"); return; }
            ulong otherId = BitConverter.ToUInt64(frame, 1);
            byte pktId = frame.Length > 9 ? frame[9] : (byte)255;

            if (_mode == PeerMode.Listen)
            {
                // Broker: forward this peer frame from fromLinkId to otherId (the destination).
                _log($"[broker] RELAY {fromLinkId} -> {otherId} pktId={pktId} len={frame.Length - 9}");
                bool ok = RelayHandler != null
                    ? RelayHandler(fromLinkId, otherId, Slice(frame, 9))
                    : RelayTo(otherId, fromLinkId, Slice(frame, 9));
                if (!ok) _log($"[broker][warn] RELAY {fromLinkId}->{otherId} FAILED (dest not connected; known: {string.Join(",", _clientLinks.Keys)})");
            }
            else
            {
                // Client: this is peer traffic from 'otherId' (the source the broker stamped).
                _log($"[client] <- RELAY from {otherId} pktId={pktId} len={frame.Length - 9}");
                var conn = _connections.GetOrAdd(otherId, x => new LanConnection(x));
                using (var ms = new MemoryStream(frame, 9, frame.Length - 9, false))
                using (var r = new BinaryReader(ms, new UTF8Encoding(false)))
                {
                    try { OnMessage?.Invoke(conn, r); }
                    catch (Exception e) { _log($"[err] OnMessage(relay) from {otherId} pktId={pktId}: {e.Message}"); }
                }
            }
        }

        private static byte[] Slice(byte[] src, int offset)
        {
            var dst = new byte[src.Length - offset];
            Buffer.BlockCopy(src, offset, dst, 0, dst.Length);
            return dst;
        }

        // ---------------- HELLO / identity ----------------
        private void SendHello(TcpLink link)
        {
            var framed = new byte[1 + 8];
            framed[0] = CH_HELLO;
            BitConverter.GetBytes(_selfId).CopyTo(framed, 1);
            link.Send(framed);
        }

        private void HandleHello(TcpLink link, byte[] frame)
        {
            if (frame.Length < 9) return;
            ulong remoteId = BitConverter.ToUInt64(frame, 1);
            link.RemoteId = remoteId;
            if (_mode == PeerMode.Listen)
            {
                _clientLinks[remoteId] = link;
                lock (_pending) _pending.Remove(link);
                _log($"[broker] client identified: {remoteId} ({link.RemoteEndpoint})");

                // CRITICAL: surface this client as an IConnection so the base MatchmakingPeer
                // tracks it (its OnConnectionAdded handler populates LanBroker._clients). Without
                // this, InitialData arrives from an UNTRACKED connection and pairing never works.
                bool isNew = !_connections.ContainsKey(remoteId);
                var conn = _connections.GetOrAdd(remoteId, x => new LanConnection(x));
                int subs = OnConnectionAdded == null ? 0 : OnConnectionAdded.GetInvocationList().Length;
                _log($"[broker] HELLO {remoteId}: isNew={isNew} connCount={_connections.Count} onAddedSubs={subs}");
                // Always surface the connection so the base MatchmakingPeer tracks it. The previous
                // isNew-guard skipped surfacing whenever DispatchServer's GetOrAdd had already created
                // the LanConnection (CH_SERVER arriving before/around HELLO), leaving _clients empty.
                _log($"[broker] surfacing connection for client {remoteId} (OnConnectionAdded -> base MatchmakingPeer)");
                OnConnectionAdded?.Invoke(conn);
                conn.RaiseConnect();
                OnClientIdentified?.Invoke(remoteId, link);
            }
            else
            {
                _log($"[client] broker identified (id {remoteId})");
            }
        }

        // ---------------- accept loop (broker) ----------------
        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }
                var link = new TcpLink(client) { Log = _log };
                link.OnClosed += l => OnClientLinkClosed(l);
                SendHello(link);
                lock (_pending) _pending.Add(link);
                _log($"[broker] accepted {link.RemoteEndpoint}");
            }
        }

        private void PumpPending()
        {
            List<TcpLink> snap;
            lock (_pending) snap = new List<TcpLink>(_pending);
            foreach (var link in snap) PumpLink(link, isBrokerLink: false);
        }

        private void OnClientLinkClosed(TcpLink link)
        {
            if (link.RemoteId != 0) { _clientLinks.TryRemove(link.RemoteId, out _); OnClientGone?.Invoke(link.RemoteId); }
            lock (_pending) _pending.Remove(link);
        }

        private void OnBrokerLinkClosed()
        {
            _log("[client] broker link closed (this is expected on game quit; mid-session = problem)");
            if (_serverConn != null) _serverConn.RaiseDisconnect(0);
        }

        // ---------------- helpers / stubs ----------------
        private void EnsureServerConn()
        {
            if (_serverConn == null)
            {
                _serverConn = new LanConnection(0UL);
                _connections[0UL] = _serverConn;
                OnConnectionAdded?.Invoke(_serverConn);
            }
        }

        public int GetPing(IConnection connection) => 0;
        public bool IsFriend(ulong id) => true;
        public void SetConnectKey(string key) { }
        public bool TryGetLocation(out PingLocation location) { location = PingLocationFactory.Create("lan"); return location != null; }
        public PingLocation ParseLocation(string str) => PingLocationFactory.Create(str ?? "lan");
    }
}
