# JKQ ChimpWorld matchmaking — complete protocol trace

Reconstructed from decompiled `MatchmakingClient`, `MatchmakingPeer`, and the packet types.
Goal: understand the FULL host+joiner flow so the LAN broker mimics the real server exactly.

## Actors
- **Joiner** = the player invading / joining. Settings: `CanInvade=true`.
- **Host**   = the player being invaded / hosting. Settings: `CanBeInvaded=true`.
- **Broker** = our `LanBroker` standing in for Nexile's matchmaking server.
- Each game client is a `MatchmakingClient` (subclass of `MatchmakingPeer`) talking to the broker
  over our LanPeer "server" channel; peer (P2P) traffic is relayed by the broker.

## Connection bring-up (both clients) — CONFIRMED WORKING in logs
1. Client `peer.ConnectIP(brokerIp, port)` → LanPeer dials broker; `OnConnectionAdded(serverConn)`.
2. `OnConnectToMatchmaker`: `peer.CreateP2PSocket(0)` (no-op for us), then `TrySendInitialData`.
3. `InitialData(meta, settings, location, initial, data)` → broker (pktId 0). Broker tracks the client.
4. Periodic `PlayerDataUpdate` (pktId 17) and `SettingsUpdate` (pktId 16) to the broker. We ignore content.
   `Status` becomes `Connected` once `sentPlayerData` is set (right after InitialData).

## Join trigger (joiner) — CONFIRMED WORKING
5. Our JoinDriver calls `NetworkManager.Join("internal {hostId}", 0)` →
   `MatchmakingClient.Join(key, 0)` → `GetConnectionTarget` validates ServerKey == "internal" and
   parses hostId → `Send(matchmaker, new InviteJoin(hostId, password=0))` (pktId 18).
   Joiner fires `OnJoinNotification(JoinRequestInitiated)`.

## Server-brokered invasion handshake — THIS IS WHERE WE MUST MATCH THE SERVER EXACTLY

The server receives `InviteJoin(host=H, pw)` from joiner J. It must drive BOTH clients:

### Host side (J is being introduced to H)
- Server → H: `InvasionHostRequest(J, metaJ, invite, pw)`  [pktId 1]
  H runs `OnInvasionHostRequest`:
    `accept = ShouldAcceptPlayer(J, settings.CanBeInvaded, mustBeLeader=true, invite, pw)`
    if accept: `meta[J] = metaJ`; notify JoinInitiatedHost
    Respond `InvasionHostResponse(accept)`  [pktId 2]
- Server → H: `InvasionJoinRequest(J, invite)`  [pktId 6]
  H runs `OnInvasionJoinAction.Perform`:
    requires `meta[J]` to exist (else throws "no stored meta")  ← why HostRequest must come first
    `peer.ConnectP2P(J, 0)` → creates player[J], begins P2P connect to J
    waits for the P2P connection to come up, then Respond `InvasionJoinResponse(failed)`  [pktId 7]
    on success: H.IsHost = true; player[J].IsPartyMember = invite

### Joiner side (H is introduced to J as the host)
- Server → J: `InvasionParticipantRequest(H, metaH, invite)`  [pktId 3]
  J runs `OnInvasionParticipantAction.Perform`:
    `accept = ShouldAcceptPlayer(H, settings.CanInvade, mustBeLeader=false, invite, 0)`
    if accept: `peer.ExpectConnectionFrom(H)`; player[H].MarkAsHost(invite); player[H].SetMeta(metaH)
    Respond `InvasionParticipantResponse(accept)`  [pktId 4]

### The actual P2P connection
- After H does `ConnectP2P(J)` and J does `ExpectConnectionFrom(H)`, the two peers establish a direct
  connection. In our system "P2P" is RELAYED through the broker (CH_RELAY frames). The `OnConnect`
  callback on each side's player connection is what completes the handshake:
    - H's `OnInvasionJoinAction.WaitForConnection` waits for player[J].Connection.OnConnect.
    - J's player[H] connection OnConnect fires `OnConnectToHost` (sets parent.host = H).
  => BOTH sides must see their peer connection's `OnConnect` fire. In LanPeer, ConnectP2P/
     ExpectConnectionFrom call `conn.RaiseConnect()` immediately. GOOD — but see OPEN QUESTIONS.

## CRITICAL: invite flag + password gating (ShouldAcceptPlayer)

```
ShouldAcceptPlayer(id, allowedBySetting, mustBeLeader, invite, password):
  if !ShouldAcceptPlayer(id): return false      // blacklist / dup
  if invite:
     flag = ActivePartyKey != null
            || (mustBeLeader && password == partyPassword && potentialPartyKey != null)
     if flag != mustBeLeader: return false
     if flag && password == partyPassword:
         if ActivePartyKey == null: CreateParty()
         return true
     if !flag: return true
     return peer.IsFriend(id)
  return allowedBySetting
```

### Host call: ShouldAcceptPlayer(J, CanBeInvaded, mustBeLeader=TRUE, invite, pw)
- invite=TRUE:
   - flag = ActivePartyKey!=null || (true && pw==partyPassword && potentialPartyKey!=null)
   - need flag == mustBeLeader(true). The host's potentialPartyKey is set at NetworkManager ctor via
     `SetPartyLeaderKey(GenerateConnectionKey())` → potentialPartyKey != null, and partyPassword is a
     RANDOM int (GeneratePartyPassword). So flag requires `pw == partyPassword`.
   - Joiner sent pw=0. Host.partyPassword is random (almost never 0). => flag=false => returns false
     => HOST REJECTS. **This is the latent bug if we use invite=true with pw=0.**
   - (If pw matched: returns true.)
- invite=FALSE:
   - returns allowedBySetting = CanBeInvaded. Host had CanBeInvaded=true => ACCEPT, no password.

### Joiner call: ShouldAcceptPlayer(H, CanInvade, mustBeLeader=FALSE, invite, 0)
- invite=TRUE:
   - flag = ActivePartyKey!=null || (false && ...) = ActivePartyKey != null. Joiner has no active party
     => flag=false. need flag==mustBeLeader(false) => OK. Then `if !flag: return true` => ACCEPT.
- invite=FALSE:
   - returns allowedBySetting = CanInvade = true => ACCEPT.

### CONCLUSION on invite/password
The real "invite" path is for **party invites** where the host shared a party password out-of-band.
Our LAN flow is a plain **invasion** (joiner invades a host who has CanBeInvaded=true), with pw=0.
=> The broker MUST drive the handshake with **invite=FALSE** for BOTH InvasionHostRequest and
   InvasionParticipantRequest. With invite=false:
     - Host accepts iff CanBeInvaded (true). 
     - Joiner accepts iff CanInvade (true). 
   No password coordination needed. Using invite=true (our current code) makes the HOST reject at
   InvasionHostResponse because pw(0) != host.partyPassword(random).

NOTE: invite=false means player[J].IsPartyMember stays false (it's an invasion, not a party member).
That's correct for invasion co-op. Party membership is a separate feature we don't need for "see each
other in game".

## OPEN QUESTIONS — RESOLVED

A. Transaction matching — CONFIRMED. `Send(conn, packet, timeout, partner)` (MatchmakingPeer) writes a
   random transactionId and stores a Transaction; the response is matched by `SubscribeResponse<TResp,
   TOrig>`. Our broker uses exactly this. GOOD.

B. Host's InvasionJoinResponse waits for player[J].Connection.OnConnect (WaitForConnection). CRITICAL
   TIMING: OnInvasionJoinAction.Perform does, in order:
       meta.Remove(id); peer.ConnectP2P(id,0); player = players[id]; player.SetMeta(value); WaitForConnection(...)
   Player.OnConnect THROWS if Meta == null. Therefore OnConnect must NOT fire synchronously inside
   ConnectP2P (Meta isn't set until the line after). LanPeer must DEFER RaiseConnect() to the next
   Update() pump. (Originally fired synchronously → "Received an OnConnect callback, but we still
   haven't received any PlayerMeta" → InvasionJoinResponse(failed) → pairing aborts.) FIXED via
   _pendingConnect queue drained at the top of LanPeer.Update(). Same applies to ExpectConnectionFrom
   on the joiner side (player[H].SetMeta(metaH) runs after ExpectConnectionFrom).

C. Post-pairing gameplay traffic — CONFIRMED via EntityManager:
     - Host (ConnectionState.Host): `matchmaking.Send(TryGetConnection(clientId), StateData/YieldDataUpdate)`.
     - Both sides exchange InputData/StateData/TimeSync over their PEER connection.
   MatchmakingPeer.Send → peer.Send(conn, ...). For a non-server connection, LanPeer emits CH_RELAY
   tagged with conn.Id (destination SteamID); broker.RelayTo forwards to that client's link re-stamped
   with the source id; receiver dispatches to its peer connection for the source id. Our LanPeer already
   implements this. GOOD — provided both peers hold a peer connection keyed by the counterpart's SteamID,
   which ConnectP2P(J)/ExpectConnectionFrom(H) establish. CONFIRMED.

D. PlayerDataUpdate/SettingsUpdate to the matchmaker continue all session; broker ignores them (but MUST
   stay subscribed or MatchmakingPeer.OnMessage throws). Already handled.

E. State machine: host reaches ConnectionState.Host (IsHost=true set in OnInvasionJoinAction on success),
   joiner reaches ConnectionState.Client (player[H].MarkAsHost + parent.host=H via OnConnectToHost). These
   are produced by the corrected invite=false handshake. Gameplay sync (OnPlayerConnected → new Client)
   fires off the peer connection's OnConnect. CONFIRMED.

## THE ONE REMAINING CODE FIX
Broker must use **invite=FALSE** for InvasionHostRequest and InvasionParticipantRequest (plain invasion,
no party password). With invite=true + pw=0 the host rejects at InvasionHostResponse. Everything else in
the handshake and relay path is already correct.
