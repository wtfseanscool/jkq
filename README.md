JUMP KING QUEST — LAN Co-op
=========================================

This adds direct LAN/VPN co-op to JUMP KING QUEST without the official matchmaking
servers. One player HOSTS (runs an embedded broker); the other JOINS by IP. Peer
traffic is relayed through the host. Each player uses their own real Steam account
(the game still runs through Steam for ownership and identity).

REQUIREMENTS
------------
- Both players own/run JUMP KING QUEST through Steam normally.
- Both PCs can reach each other over the network:
    * same LAN, OR
    * a VPN like ZeroTier / Tailscale / Hamachi (recommended for over-internet — no
      port forwarding needed; use the VPN IP).
- The HOST allows inbound TCP on the chosen port (default 9050) through the firewall.

INSTALL
-------
Extract this folder anywhere, open PowerShell in it, then:

  HOST machine:
      .\install.ps1 -Role Host

  JOINER machine (see PLAY instructions below for how to locate SteamID64):
      .\install.ps1 -Role Joiner -HostIp <host IP> -HostSteamId <host SteamID64>

  (Optional) custom port / key on BOTH machines:
      .\install.ps1 -Role Host   -Port 9050 -ServerKey mygame
      .\install.ps1 -Role Joiner -HostIp 25.1.2.3 -HostSteamId <host SteamID64> -Port 9050 -ServerKey mygame

The installer auto-detects the game via Steam. If it can't, add:
      -GameDir "C:\path\to\steamapps\common\JUMP KING QUEST"

PLAY
----
1. HOST launches the game first, then enters an ONLINE state (system settings > online type = ONLINE HOST, then create a party in-game).
   The HOST's BepInEx log will show:  CreateClient intercepted -> LanPeer (selfId=...)
   That selfId is the HOST's SteamID64 — give it to the joiner once.
   Run .\showlog.ps1 or navigate to <game>\BepInEx\LogOutput.log to view logs
   
3. JOINER launches the game and ALSO enters an online state (system settings > online type = ONLINE CLIENT, then go in-game). With HostSteamId set, the joiner auto-joins within a few seconds.
   You can also press the JoinKey (default F6) to trigger a join manually.

   First time only, set the host's SteamID on the JOINER:
       .\install.ps1 -Role Joiner -HostIp <host IP> -HostSteamId <host SteamID64>

IMPORTANT
---------
- Both players MUST use the SAME ServerKey, or the join is rejected
  ("Can't connect to player on another server").
- Both players must be in an ONLINE-enabled state for the matchmaker to connect;
  the LAN join is suppressed (and logged as "waiting: matchmaker status=...") until
  then — this is expected, not an error.
- Exactly ONE player is Host.
- Start the Host before the Joiner.

UNINSTALL (restore vanilla)
---------------------------
  .\install.ps1 -Uninstall

TROUBLESHOOTING
---------------
- Logs are written to:  <game>\BepInEx\LogOutput.log
- On the JOINER, the join progress is logged with a [join] prefix:
    "Update() running"                  -> the driver is live
    "waiting: matchmaker status=..."     -> not online yet; enable invasions in-game
    "attempt N: Networking.Join(...)"    -> a join was actually dispatched
    "<<< OnJoinNotification: type=..."   -> the game's verdict:
         JoinRequestInitiated   = accepted, brokering now (good)
         JoinRequestWrongServer = ServerKey mismatch between the two players
         JoinRequestInvalid     = bad key / host SteamID
- On the HOST, successful brokering logs:
    "===== INVITE #1 ..."  then  STEP 1..4  then  "PAIRING COMPLETE".
- "connection refused" on the joiner: the host isn't running, the IP is wrong, or a
  firewall is blocking the port.
- Nothing happens: confirm both used the same ServerKey and the host launched first.

This package contains BepInEx and two small plugin DLLs. It does NOT contain any
game files — those load from your own installed copy at runtime.
