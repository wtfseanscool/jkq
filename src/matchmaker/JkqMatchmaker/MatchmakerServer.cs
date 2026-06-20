using System;
using System.Collections.Generic;
using Nexile.ChimpWorld.Matchmaking;
using Nexile.ChimpWorld.Matchmaking.Packets;
using Nexile.SteamyChimp;

namespace JkqMatchmaker
{
    /// <summary>
    /// Server-side ChimpWorld matchmaking peer. Subclasses the game's own abstract
    /// MatchmakingPeer (from ChimpWorldMatchmaking.dll) and drives a SteamPeer created via
    /// SteamPeer.CreateServer(...) so it shares the exact transport, wire format, and SteamID
    /// identity handling the game client uses.
    ///
    /// Role: this peer is the "Server" (connection id 0 from each client's perspective). Each
    /// inbound connection's Id is the client's emulated SteamID64 (assigned by SteamPeer via
    /// the connection's SteamNetworkingIdentity). We broker invite-only sessions by relaying the
    /// invasion handshake so the two peers establish a direct Steam P2P connection.
    ///
    /// IMPORTANT: The packet *types* (InitialData, InviteJoin, InvasionParticipantRequest, ...)
    /// are the game's own classes from ChimpWorldMatchmaking.dll — we are not re-serializing,
    /// we let the game's MatchmakingPeer machinery encode/decode. This is why we reuse the DLL.
    /// </summary>
    internal sealed class MatchmakerServer : MatchmakingPeer
    {
        private sealed class Client
        {
            public ulong Id;                 // emulated SteamID64
            public IConnection Connection;
            public bool HasInitialData;
            public PlayerConnectionSettings Settings;
            public DateTime ConnectedAt;
        }

        private readonly Dictionary<ulong, Client> _clients = new Dictionary<ulong, Client>();
        private readonly Action<string> _log;

        public MatchmakerServer(ISteamPeer peer, Action<string> log) : base(peer)
        {
            _log = log ?? (_ => { });

            // Subscribe to the packets a client sends to the server.
            // InitialData announces a client's presence/settings.
            Subscribe<InitialData>(OnInitialData, MakeInitialDataPrototype());
            // SettingsUpdate / PlayerDataUpdate: keep our view fresh (no brokering effect).
            // (Registered by base ctor; we subscribe to act on them.)
            Subscribe<SettingsUpdate>(OnSettingsUpdate);
            // InviteJoin: the actual "I want to join host H" request.
            Subscribe<InviteJoin>(OnInviteJoin);
            // The respondable invasion replies come back as responses to our requests; we wire
            // those with SubscribeResponse so the transaction machinery matches them up.
            SubscribeResponse<InvasionParticipantResponse, InvasionParticipantRequest>(OnParticipantResponse);
            SubscribeResponse<InvasionJoinResponse, InvasionJoinRequest>(OnJoinResponse);
            SubscribeResponse<InvasionHostResponse, InvasionHostRequest>(OnHostResponse);
        }

        // InitialData needs an IPlayerConnectionDataReader to parse its embedded player data.
        private static InitialData MakeInitialDataPrototype()
            => new InitialData(new ServerSideDataReader());

        protected override void OnConnectionAdded(IConnection connection)
        {
            if (connection.IsServer)
            {
                // Shouldn't happen for the server peer (we have no upstream server).
                _log($"[warn] inbound connection flagged IsServer (id={connection.Id}); ignoring");
                return;
            }

            var c = new Client
            {
                Id = connection.Id,
                Connection = connection,
                ConnectedAt = DateTime.UtcNow,
            };
            _clients[connection.Id] = c;
            _log($"[conn+] client {connection.Id} connected ({_clients.Count} total)");
        }

        protected override void OnConnectionRemoved(IConnection connection)
        {
            if (_clients.Remove(connection.Id))
                _log($"[conn-] client {connection.Id} disconnected ({_clients.Count} total)");
        }

        // The server is never a "host connection" in the peer-role sense.
        protected override bool IsHostConnection(IConnection connection) => false;

        protected override int GetProtocolVersionForConnection(ulong id) => 2;

        // ---- inbound handlers ----

        private void OnInitialData(IConnection conn, InitialData packet)
        {
            if (_clients.TryGetValue(conn.Id, out var c))
            {
                c.HasInitialData = true;
                c.Settings = packet.Settings;
                _log($"[init ] client {conn.Id} settings={packet.Settings} loc.len={(packet.Location?.Length ?? 0)}");
            }
        }

        private void OnSettingsUpdate(IConnection conn, SettingsUpdate packet)
        {
            if (_clients.TryGetValue(conn.Id, out var c))
            {
                c.Settings = packet.Settings;
                _log($"[setn ] client {conn.Id} settings={packet.Settings}");
            }
        }

        /// <summary>
        /// Joiner J (conn) wants to join host H (packet.Id). Broker the invasion handshake:
        ///   1) ask H to expect a connection from J  (InvasionParticipantRequest -> H)
        ///   2) on H's confirm, tell J to connect to H (InvasionJoinRequest -> J)
        /// Each step is a respondable transaction; the responses route back through SubscribeResponse.
        /// </summary>
        private void OnInviteJoin(IConnection conn, InviteJoin packet)
        {
            ulong joiner = conn.Id;
            ulong host = packet.Id;
            _log($"[join ] joiner {joiner} -> host {host} (pw={packet.Password})");

            if (!_clients.TryGetValue(host, out var hostClient))
            {
                _log($"[join!] host {host} not connected; cannot broker. Sending join failure to {joiner}.");
                // Tell joiner it failed (best-effort; no transaction context here).
                TrySend(conn, new InvasionJoinResponse(true));
                return;
            }

            // Step 1: ask the HOST to expect a connection from the joiner.
            // PlayerMeta is empty on the wire; pass a fresh instance.
            var participantReq = new InvasionParticipantRequest(joiner, new PlayerMeta(), invite: true);
            // Stash the pairing so the response handler knows who to advance.
            _pending[(host, joiner)] = new Pending { Host = host, Joiner = joiner, Invite = true };
            SendRespondable(hostClient.Connection, participantReq, joiner, host);
        }

        // pairing state keyed by (host, joiner)
        private struct Pending { public ulong Host; public ulong Joiner; public bool Invite; }
        private readonly Dictionary<(ulong, ulong), Pending> _pending = new Dictionary<(ulong, ulong), Pending>();

        private bool OnParticipantResponse(IConnection conn, InvasionParticipantResponse resp, InvasionParticipantRequest original)
        {
            // conn is the HOST replying. original.Id is the joiner we asked about.
            ulong host = conn.Id;
            ulong joiner = original.Id;
            _log($"[part<] host {host} {(resp.Confirmed ? "ACCEPTED" : "declined")} participant {joiner}");

            if (!resp.Confirmed)
            {
                FailJoin(joiner);
                _pending.Remove((host, joiner));
                return true;
            }

            // Step 2: tell the JOINER to connect to the host.
            if (_clients.TryGetValue(joiner, out var joinerClient))
            {
                var joinReq = new InvasionJoinRequest(host, invite: true);
                SendRespondable(joinerClient.Connection, joinReq, host, joiner);
            }
            return true;
        }

        private bool OnJoinResponse(IConnection conn, InvasionJoinResponse resp, InvasionJoinRequest original)
        {
            // conn is the JOINER replying. original.Id is the host.
            ulong joiner = conn.Id;
            ulong host = original.Id;
            _log($"[join<] joiner {joiner} {(resp.Failed ? "FAILED" : "OK")} connecting to host {host}");
            _pending.Remove((host, joiner));
            // On OK, both peers are now establishing direct P2P; matchmaker's job is done for this pair.
            return true;
        }

        private bool OnHostResponse(IConnection conn, InvasionHostResponse resp, InvasionHostRequest original)
        {
            _log($"[host<] client {conn.Id} host-response confirmed={resp.Confirmed}");
            return true;
        }

        private void FailJoin(ulong joiner)
        {
            if (_clients.TryGetValue(joiner, out var c))
                TrySend(c.Connection, new InvasionJoinResponse(true));
        }

        // ---- send helpers (thin wrappers over MatchmakingPeer protected API) ----

        private void TrySend(IConnection conn, IPacket packet)
        {
            try { Send(conn, packet); }
            catch (Exception e) { _log($"[err  ] send {packet.GetType().Name} -> {conn.Id}: {e.Message}"); }
        }

        // Send a respondable (transactional) request with a timeout so the response routes back.
        private void SendRespondable(IConnection conn, IPacket packet, ulong partnerId, ulong selfContextId)
        {
            try
            {
                IConnection partner = null;
                _clients.TryGetValue(partnerId, out var pc);
                if (pc != null) partner = pc.Connection;
                // 30s transaction timeout; partner lets the transaction cancel if either side drops.
                Send(conn, packet, 30000, partner);
            }
            catch (Exception e)
            {
                _log($"[err  ] sendRespondable {packet.GetType().Name} -> {conn.Id}: {e.Message}");
            }
        }

        public int ClientCount => _clients.Count;
    }
}
