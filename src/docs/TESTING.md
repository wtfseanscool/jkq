# Test runbook — JUMP KING QUEST co-op fix

Milestones build on each other. Stop at the first failure and capture the output noted in each step.

## Prerequisites (once)
- Build everything:
  - `dotnet build plugin/JKQLocalMM.csproj -c Release`
  - `dotnet build matchmaker/JkqMatchmaker/JkqMatchmaker.csproj -c Release`
- Assemble the matchmaker folder: `tools/deploy-matchmaker.ps1`
- Install the client fix into the game: `tools/deploy-game.ps1 -Ip 127.0.0.1 -Port 9050 -ServerKey internal`
  - (Revert anytime with `tools/deploy-game.ps1 -Uninstall`.)

## Milestone 1 — matchmaker boots + a client reaches it (127.0.0.1)
1. Run `dist/matchmaker/ChimpWorldMatchmakingServer.exe`.
   - EXPECT: `[load ] winmm.dll`, `[load ] OnlineFix64.dll`, `[load ] steam_api64.dll`, then
     `Listening on UDP 9050. Waiting for clients...`
   - IF you see "dlllist.txt is missing" -> re-run deploy-matchmaker.ps1.
   - IF "CreateServer failed" -> emulator loaded but GameServer.Init was rejected; capture the message.
2. Launch JKQ once (through Steam or JKQ.exe directly). In the game, enable online/co-op so it
   connects to matchmaking.
   - EXPECT in `BepInEx/LogOutput.log`: a line from "JKQ Local Matchmaker Redirect" showing
     `GetServer(...) -> redirected to internal;internal;Local;127.0.0.1;9050`.
   - EXPECT in the matchmaker console: `[conn+] client <steamid> connected` then `[init ] client <steamid> ...`.
   - This is the WIN condition for milestone 1.

## Milestone 2 — invite/join handshake (still 127.0.0.1, two instances)
- Two emulated SteamIDs are REQUIRED. In online-fix's account settings/ini, give each instance a
  DISTINCT SteamID, or run the second copy from a separate game folder with its own online-fix account.
1. Both instances launched, both show `[conn+]` in the matchmaker.
2. Host opens the invite (Steam overlay invite / friends), joiner accepts.
   - EXPECT matchmaker console: `[join ] joiner J -> host H`, `[part<] host H ACCEPTED participant J`,
     `[join<] joiner J OK connecting to host H`.
   - EXPECT both game instances to transition into a shared session (peers do ConnectP2P directly).

## Milestone 3 — LAN (two PCs)
- Re-run `deploy-game.ps1` on each PC. Host: `-Ip <hostLANip>`. Joiner: `-Ip <hostLANip>`.
- Run the matchmaker on the host PC. Open UDP 9050/9051 on the host firewall.
- Repeat milestone 2 across the two machines.

## Milestone 4 — internet via online-fix
- Same as LAN but `-Ip <reachable WAN ip>` and port-forward 9050/9051, OR put both PCs on a VPN
  (ZeroTier/Tailscale) and use the VPN IP (behaves like LAN, no port-forwarding).
- This is the one path that depends on online-fix relaying ConnectP2P between two independent
  installs; if peers connect to the matchmaker but never establish P2P, fall back to VPN.

## What to capture on failure
- `BepInEx/LogOutput.log` (game side)
- The matchmaker console output
- Which milestone + step number
