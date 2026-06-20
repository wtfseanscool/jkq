using System;
using System.Collections.Generic;
using Nexile.ChimpWorld.Matchmaking;
using Nexile.ChimpWorld.Matchmaking.Packets;
using Nexile.SteamyChimp;

namespace JkqBroker
{
    /// <summary>
    /// Embedded ChimpWorld matchmaking broker for LAN co-op. Subclasses the game's own
    /// MatchmakingPeer (driven by an injected ISteamPeer — our LanPeer's server channel) and brokers
    /// the invite handshake between the two connected clients so they establish a direct peer link.
    ///
    /// Assembly is named ChimpWorldMatchmakingServer to satisfy the game's InternalsVisibleTo and use
    /// the internal packet types.
    ///
    /// Handshake (verified byte-for-byte from ChimpWorldMatchmakingClient's handlers):
    ///   On InviteJoin(host=H) from joiner J:
    ///     STEP 1  send InvasionHostRequest(J, meta, invite, pw) -> H
    ///               H stores meta[J] (REQUIRED — without this, the later InvasionJoinRequest throws
    ///               "no stored meta with id J"), then replies InvasionHostResponse(confirmed).
    ///     STEP 2  on InvasionHostResponse(confirmed) from H:
    ///               send InvasionJoinRequest(J, invite) -> H
    ///               H does ConnectP2P(J) and replies InvasionJoinResponse(failed).
    ///     STEP 3  on InvasionJoinResponse(!failed) from H:
    ///               send InvasionParticipantRequest(H, meta, invite) -> J
    ///               J does ExpectConnectionFrom(H), marks H as host, replies InvasionParticipantResponse.
    ///     STEP 4  on InvasionParticipantResponse(confirmed) from J: pairing complete.
    ///   The two clients then have a direct peer connection (relayed via our LanPeer).
    ///
    /// PlayerMeta is an empty struct in this build (no serialized fields), so a fresh new PlayerMeta()
    /// is byte-identical to a captured one — the host only needs the meta KEY to exist.
    ///
    /// VERBOSE LOGGING: every connection lifecycle event, every received packet, and each handshake
    /// step is logged with a [broker] prefix so a single test run reveals exactly where pairing breaks.
    /// </summary>
    public sealed class LanBroker : MatchmakingPeer
    {
        private sealed class Client
        {
            public ulong Id;
            public IConnection Connection;
            public bool HasInitialData;
            public DateTime ConnectedAt;
            public int PlayerDataUpdates;
            public int PendingInvitePassword;   // password from the InviteJoin, reused across steps
        }

        private readonly Dictionary<ulong, Client> _clients = new Dictionary<ulong, Client>();
        private readonly HashSet<ulong> _inFlight = new HashSet<ulong>();   // joiners with a handshake in progress
        private readonly Action<string> _log;
        private int _inviteCount;

        // Plain INVASION semantics, not party-invite. With invite=false the host accepts on its
        // CanBeInvaded setting and the joiner accepts on its CanInvade setting — no party password
        // coordination. invite=true requires the joiner's password to equal the host's RANDOM
        // partyPassword (ShouldAcceptPlayer), which a pw=0 join can never satisfy → host rejects.
        // See docs/PROTOCOL-TRACE.md.
        private const bool Invite = false;

        public LanBroker(ISteamPeer peer, Action<string> log) : base(peer)
        {
            _log = log ?? (_ => { });
            _log("[broker] ctor: subscribing to packets (InitialData, InviteJoin, PlayerDataUpdate, " +
                 "SettingsUpdate, InvasionHostResponse, InvasionJoinResponse, InvasionParticipantResponse)");
            Subscribe<InitialData>(OnInitialData, new InitialData(new LanDataReader()));
            Subscribe<InviteJoin>(OnInviteJoin);
            // The client periodically sends these to the "server"; we don't need them, but we MUST
            // subscribe or MatchmakingPeer.OnMessage throws "no subscriber at that id".
            Subscribe<PlayerDataUpdate>(OnPlayerDataUpdate, new PlayerDataUpdate(new LanDataReader()));
            Subscribe<SettingsUpdate>(OnSettingsUpdate);
            SubscribeResponse<InvasionHostResponse, InvasionHostRequest>(OnHostResponse);
            SubscribeResponse<InvasionJoinResponse, InvasionJoinRequest>(OnJoinResponse);
            SubscribeResponse<InvasionParticipantResponse, InvasionParticipantRequest>(OnParticipantResponse);
            _log("[broker] ctor complete");
        }

        protected override void OnConnectionAdded(IConnection connection)
        {
            if (connection.IsServer)
            {
                _log($"[broker] OnConnectionAdded: SERVER connection (id={connection.Id}) — ignored");
                return;
            }
            _clients[connection.Id] = new Client
            {
                Id = connection.Id,
                Connection = connection,
                ConnectedAt = DateTime.UtcNow,
            };
            _log($"[broker] +CLIENT {connection.Id}  (total clients now: {_clients.Count})");
            DumpClients();
        }

        protected override void OnConnectionRemoved(IConnection connection)
        {
            _inFlight.Remove(connection.Id);
            if (_clients.Remove(connection.Id))
            {
                _log($"[broker] -CLIENT {connection.Id}  (total clients now: {_clients.Count})");
                DumpClients();
            }
            else
            {
                _log($"[broker] OnConnectionRemoved: {connection.Id} was not a tracked client");
            }
        }

        protected override bool IsHostConnection(IConnection connection) => false;
        protected override int GetProtocolVersionForConnection(ulong id) => 2;

        private void OnInitialData(IConnection conn, InitialData packet)
        {
            if (conn.IsServer)
            {
                _log($"[broker] <- InitialData from SERVER conn (id={conn.Id}) — ignored");
                return;
            }
            if (!_clients.TryGetValue(conn.Id, out var c))
            {
                // Self-heal: a client we haven't tracked yet (connection-added race). Track it now so
                // the subsequent InviteJoin can pair instead of failing with "host NOT connected".
                _log($"[broker] <- InitialData from untracked {conn.Id}; tracking on demand");
                c = new Client { Id = conn.Id, Connection = conn, ConnectedAt = DateTime.UtcNow };
                _clients[conn.Id] = c;
            }
            c.HasInitialData = true;
            _log($"[broker] <- InitialData from {conn.Id}  (settings: canInvade={packet.Settings.CanInvade} " +
                 $"canBeInvaded={packet.Settings.CanBeInvaded}, loc.len={(packet.Location?.Length ?? 0)})");
            DumpClients();
        }

        private void OnPlayerDataUpdate(IConnection conn, PlayerDataUpdate packet)
        {
            if (_clients.TryGetValue(conn.Id, out var c))
            {
                c.PlayerDataUpdates++;
                // Log only the first few to avoid spam; these arrive periodically.
                if (c.PlayerDataUpdates <= 2)
                    _log($"[broker] <- PlayerDataUpdate from {conn.Id} (#{c.PlayerDataUpdates})");
            }
        }

        private void OnSettingsUpdate(IConnection conn, SettingsUpdate packet)
        {
            _log($"[broker] <- SettingsUpdate from {conn.Id} (canInvade={packet.Settings.CanInvade} " +
                 $"canBeInvaded={packet.Settings.CanBeInvaded})");
        }

        private void OnInviteJoin(IConnection conn, InviteJoin packet)
        {
            ulong joiner = conn.Id;
            ulong host = packet.Id;
            _inviteCount++;
            _log($"[broker] ===== INVITE #{_inviteCount}: joiner={joiner} wants host={host} (pw={packet.Password}) =====");
            DumpClients();

            if (joiner == host)
            {
                _log($"[broker] !! joiner==host ({host}); ignoring self-join");
                return;
            }

            if (!_clients.TryGetValue(host, out var hostClient))
            {
                _log($"[broker] !! host {host} NOT connected; cannot broker. Known clients: {ClientIds()}");
                return;
            }
            if (!_clients.TryGetValue(joiner, out var joinerClient))
            {
                _log($"[broker] !! joiner {joiner} NOT tracked; aborting");
                return;
            }

            // Ignore duplicate InviteJoins while a handshake for this pair is already in flight.
            // (The joiner can send several — auto-join + a manual key press.) Restarting mid-handshake
            // corrupts the host's player/meta state and makes the second attempt fail.
            if (_inFlight.Contains(joiner))
            {
                _log($"[broker] joiner {joiner} already has a handshake in flight; ignoring duplicate INVITE");
                return;
            }
            _inFlight.Add(joiner);

            // STEP 1: tell the HOST about the joiner so it stores meta[joiner]. The host replies
            // InvasionHostResponse; only then is it safe to send InvasionJoinRequest.
            hostClient.PendingInvitePassword = packet.Password;
            _log($"[broker] STEP 1: -> InvasionHostRequest(id={joiner}) to HOST {host} (pw={packet.Password})");
            try
            {
                Send(hostClient.Connection,
                     new InvasionHostRequest(joiner, new PlayerMeta(), invite: Invite, password: packet.Password),
                     30000, joinerClient.Connection);
                _log($"[broker] STEP 1 sent OK (30s transaction opened)");
            }
            catch (Exception e)
            {
                _log($"[broker] !! STEP 1 send FAILED: {e}");
            }
        }

        private bool OnHostResponse(IConnection conn, InvasionHostResponse resp, InvasionHostRequest original)
        {
            // conn = host replying; original.Id = joiner.
            ulong host = conn.Id;
            ulong joiner = original.Id;
            _log($"[broker] STEP 2: <- InvasionHostResponse from HOST {host} (confirmed={resp.Confirmed}) for joiner={joiner}");
            if (!resp.Confirmed)
            {
                _log($"[broker] !! host {host} REJECTED the host request (canBeInvaded off / not leader / pw mismatch); aborting");
                _inFlight.Remove(joiner);
                return true;
            }
            if (!_clients.TryGetValue(host, out var hostClient))
            {
                _log($"[broker] !! host {host} no longer tracked at STEP 2");
                _inFlight.Remove(joiner);
                return true;
            }
            _clients.TryGetValue(joiner, out var joinerClient);

            // Host now has meta[joiner]; ask it to actually join (it will ConnectP2P(joiner)).
            _log($"[broker] STEP 2b: -> InvasionJoinRequest(id={joiner}) to HOST {host}");
            try
            {
                Send(hostClient.Connection, new InvasionJoinRequest(joiner, invite: Invite), 30000,
                     joinerClient?.Connection);
                _log($"[broker] STEP 2b sent OK");
            }
            catch (Exception e)
            {
                _log($"[broker] !! STEP 2b send FAILED: {e}");
            }
            return true;
        }

        private bool OnJoinResponse(IConnection conn, InvasionJoinResponse resp, InvasionJoinRequest original)
        {
            // conn = host replying; original.Id = joiner.
            ulong host = conn.Id;
            ulong joiner = original.Id;
            _log($"[broker] STEP 3: <- InvasionJoinResponse from HOST {host} (failed={resp.Failed}) for joiner={joiner}");
            if (resp.Failed)
            {
                _log($"[broker] !! host {host} reported join FAILED; aborting pairing");
                _inFlight.Remove(joiner);
                return true;
            }

            // Step 3b: tell the JOINER to expect/treat the host as host (joiner ExpectConnectionFrom(host)).
            if (_clients.TryGetValue(joiner, out var jc))
            {
                _log($"[broker] STEP 3b: -> InvasionParticipantRequest(id={host}) to JOINER {joiner}");
                try
                {
                    Send(jc.Connection, new InvasionParticipantRequest(host, new PlayerMeta(), invite: Invite), 30000,
                         _clients.TryGetValue(host, out var hc) ? hc.Connection : null);
                    _log($"[broker] STEP 3b sent OK");
                }
                catch (Exception e)
                {
                    _log($"[broker] !! STEP 3 send FAILED: {e}");
                }
            }
            else
            {
                _log($"[broker] !! joiner {joiner} no longer tracked at STEP 3b");
                _inFlight.Remove(joiner);
            }
            return true;
        }

        private bool OnParticipantResponse(IConnection conn, InvasionParticipantResponse resp, InvasionParticipantRequest original)
        {
            // conn = joiner replying; original.Id = host.
            _log($"[broker] STEP 4: <- InvasionParticipantResponse from JOINER {conn.Id} confirmed={resp.Confirmed}");
            _inFlight.Remove(conn.Id);   // handshake terminal — clear in-flight either way
            if (resp.Confirmed)
                _log($"[broker] ***** PAIRING COMPLETE: {original.Id} <-> {conn.Id}. Peers should now relay game traffic. *****");
            else
                _log($"[broker] !! joiner {conn.Id} did NOT confirm participant; pairing incomplete");
            return true;
        }

        private void DumpClients()
        {
            var sb = new System.Text.StringBuilder("[broker] state: ");
            sb.Append(_clients.Count).Append(" client(s): ");
            foreach (var c in _clients.Values)
                sb.Append($"{c.Id}(init={c.HasInitialData}) ");
            _log(sb.ToString());
        }

        private string ClientIds()
        {
            return string.Join(",", new List<ulong>(_clients.Keys).ConvertAll(x => x.ToString()).ToArray());
        }

        public int ClientCount => _clients.Count;
    }
}
