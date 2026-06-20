# ChimpWorld Matchmaking Protocol (reconstructed)

Reconstructed from decompiled `SteamyChimp.dll`, `ChimpWorldMatchmaking.dll`,
`ChimpWorldMatchmakingClient.dll`, `ChimpKeeperShared.dll`. This is the wire format the
JUMP KING QUEST client speaks to its matchmaker. Our standalone matchmaker must speak it back.

## Transport

- Steam GameNetworkingSockets, message-oriented (`SendMessageToConnection` / `ReceiveMessagesOnConnection`).
- Client reaches the matchmaker via `ConnectByIPAddress("<ip>:<port>")` (NOT P2P). The matchmaker
  is connection id `0` on the client (`SteamPeer`: `isServer = id == 0`), i.e. the "Server".
- Each logical message is one network message. Reliable send flag = `8`
  (`k_nSteamNetworkingSend_Reliable`); unreliable = `0`. All matchmaker packets are `SendReliable=true`.

## Message framing (`SteamyChimp.Messenger` + `MatchmakingPeer.Send`)

```
[byte  packetId]            // index assigned by Register<T>() order (see table)
[      packet payload ]     // packet.Write(connectionId, writer)
[ulong transactionId]       // appended by Send/Respond when a transaction is used; see below
```

- `MatchmakingPeer.Send(conn, packet)` writes `packetId` then `packet.Write(...)` with NO trailing id.
- `Send(conn, packet, timeout)` and `Respond(conn, packet, transactionId)` append a `ulong transactionId`
  after the payload.
- On receive, `OnMessage` reads `packetId` (1 byte) and dispatches; the subscriber's packet `Read`
  consumes the payload, and respondable/response subscribers then read the trailing `ulong`.

### .NET BinaryWriter/BinaryReader semantics (must match exactly)
- Little-endian.
- `Write(string)`: 7-bit-encoded length prefix (LEB128) then UTF-8 bytes.
- `Write(bool)`: 1 byte (0/1). `Write(ulong)`: 8 bytes. `Write(int)`: 4 bytes. `Write(ushort)`: 2 bytes.
  `Write(float)`: 4 bytes IEEE-754.

## Packet ID table (Register<T>() order in MatchmakingPeer ctor)

| ID | Packet | Valid From | Payload |
|----|--------|-----------|---------|
| 0  | InitialData | Client | Meta(0) + Settings(2 bool) + Location(string) + Initial(IPlayerConnectionData) + Data(IPlayerConnectionData) |
| 1  | InvasionHostRequest | Server | ulong Id, Meta(0), bool Invite, [if Invite && proto>=2: int Password] |
| 2  | InvasionHostResponse | Client | bool Confirmed |
| 3  | InvasionParticipantRequest | Server | ulong Id, Meta(0), bool Invite |
| 4  | InvasionParticipantResponse | Client | bool Confirmed |
| 5  | DisconnectRequestServer | Server | ulong Id |
| 6  | InvasionJoinRequest | Server | ulong Id, bool Invite |
| 7  | InvasionJoinResponse | Client | bool Failed |
| 8  | DisconnectRequestHost | Host | ulong Id |
| 9  | ParticipantFirstPairRequest | Host | ulong Id, Meta(0) |
| 10 | ParticipantFirstPairResponse | Client | (empty) |
| 11 | ParticipantSecondPairRequest | Host | ulong Id, Meta(0) |
| 12 | ParticipantSecondPairResponse | Client | bool Failed |
| 13 | DisconnectedFromParticipant | Host | ulong Id |
| 14 | DisconnectedFromHost | Participant | ulong Id |
| 15 | StopAsHost | Server | (empty) |
| 16 | SettingsUpdate | Client | Settings(2 bool) |
| 17 | PlayerDataUpdate | Client | Data(IPlayerConnectionData) |
| 18 | InviteJoin | Client | ulong Id, [if proto>=2: int Password] |
| 19 | PartyState | Host | ulong Id, bool State |
| 20 | JoinExisting | Client | int Password |
| 21 | LeaveLobby | Server | (empty) |
| 22 | UpdatePartyStateNotification | Host | bool State |
| 23 | PartyPassword | Host | int Password |

`Valid From` is the role the *receiver* enforces via `CheckValidity`. From the matchmaker's
perspective the matchmaker IS the "Server", so it SENDS Server-tagged packets (1,3,5,6,15,21) and
RECEIVES Client-tagged packets (0,2,4,7,16,17,18,20). Host/Participant-tagged packets are peer<->peer
(relayed if at all). For invite-only brokering the matchmaker mainly needs: receive 0/2/4/7/18, send 1/3/6.

## IPlayerConnectionData (ChimpKeeperShared)

Two concrete readers, selected by `IPlayerConnectionDataReader`:

- `PlayerConnectionInitialData` (the "Initial" field of InitialData):
  - Write: `string Version+"___"`, `byte ProtocolVersion(=2)`, `byte PingFilter`
  - Read: `string Version`; if endsWith("___") read `byte ProtocolVersion` else 0; if proto>0 read `byte PingFilter` else Strict
- `PlayerConnectionData` (the "Data" field of InitialData and PlayerDataUpdate payload):
  - Write: `ushort Location`, `float ProgressionValue`
  - Read: same

## PlayerMeta

`PlayerMeta.Write` is EMPTY and `new PlayerMeta(reader)` reads NOTHING — 0 bytes on the wire.

## PlayerConnectionSettings

- Write: `bool CanInvade`, `bool CanBeInvaded` (2 bytes)
- Read: same

## Connection key (Steam invite path)

- `GenerateConnectionKey()` = `"{ServerKey} {SteamId}"`
- `ParseConnectionKey` splits on space, requires EXACTLY 2 parts; rejects otherwise.
- `GetConnectionTarget` rejects if `keyServer != JKQ.ServerConfig.Key` -> both peers need same ServerKey.

## Invite-only brokering flow (minimum viable)

1. Each client connects (ConnectByIPAddress) -> matchmaker sees a new connection.
2. Each client sends `InitialData` (id 0) carrying its SteamId-derived data; matchmaker records
   `connection -> steamId`. (SteamId is learned from the transport identity, not InitialData payload.)
3. Periodically clients send `PlayerDataUpdate` (id 17) and `SettingsUpdate` (id 16): matchmaker stores/ignores.
4. Joiner (invitee) sends `InviteJoin` (id 18) with the host's SteamId (from parsed invite key) + password.
5. Matchmaker brokers: sends `InvasionParticipantRequest`/`InvasionHostRequest` (ids 3/1) to the two
   parties, relays the `Response`s (ids 4/2), then `InvasionJoinResponse` (id 7) to the joiner.
6. On success both clients call `ConnectP2P(partnerSteamId)` directly -> Steam P2P via online-fix.
   Matchmaker is idle thereafter.

NOTE: The exact invasion/join handshake ordering on the SERVER side lives in the unshipped
`ChimpWorldMatchmakingServer.dll`. The CLIENT side (ChimpWorldMatchmakingClient) is fully known and
constrains what the server must send/accept. We derive the server behavior from the client's
subscriptions and responses (see MatchmakingClient: OnInvasionHostRequest, OnInvasionParticipantRequest,
OnInvasionJoinRequest, OnInvasionJoinAction -> ConnectP2P).
