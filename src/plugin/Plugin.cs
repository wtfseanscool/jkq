using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Nexile.JKQuest;

namespace JKQLocalMM
{
    /// <summary>
    /// Redirects JUMP KING QUEST's ChimpWorld matchmaking client away from Nexile's
    /// regional matchmaking servers and toward a local/self-hosted matchmaker, enabling
    /// invite-only co-op between players running the same fix (e.g. under online-fix).
    ///
    /// Mechanism: the game resolves its matchmaking endpoint through
    /// BuildConfig.GetServer(key, mayPing, out found), which returns a MatchmakingServerConfig
    /// carrying Ip/Port/Key. We postfix that method and substitute a config built from a
    /// "key;region;label;ip;port" string we control. Every downstream consumer
    /// (ClientConnectionConfig -> MatchmakingClient -> SteamPeer.ConnectIP) then dials our host.
    ///
    /// The same-server gate in ChimpworldConfig.GetConnectionTarget compares the join key's
    /// server field against JKQ.ServerConfig.Key, so both players MUST use an identical
    /// ServerKey value for invites to resolve. Default "internal" matches the stock fallback.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.jkqcoop.localmm";
        public const string PluginName = "JKQ Local Matchmaker Redirect";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        // Config-bound values, read once at load.
        internal static ConfigEntry<string> CfgServerKey;
        internal static ConfigEntry<string> CfgIp;
        internal static ConfigEntry<int> CfgPort;
        internal static ConfigEntry<string> CfgLabel;
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgRole;
        internal static ConfigEntry<string> CfgHostSteamId;
        internal static ConfigEntry<bool> CfgAutoJoin;
        internal static ConfigEntry<string> CfgJoinKey;

        private Lan.HostBroker _hostBroker;

        private void Awake()
        {
            Log = Logger;

            CfgEnabled = Config.Bind(
                "General", "Enabled", true,
                "Master switch. When false the plugin loads but does not redirect matchmaking (vanilla behavior).");

            CfgServerKey = Config.Bind(
                "Matchmaker", "ServerKey", "internal",
                "Logical server identifier. BOTH players must use the SAME value or invites are rejected " +
                "with 'Can't connect to player on another server'. 'internal' matches the game's default fallback.");

            CfgIp = Config.Bind(
                "Matchmaker", "Ip", "127.0.0.1",
                "IP/hostname of the machine running jkq-matchmaker. The host uses their own reachable address; " +
                "the joiner uses the host's address. Use 127.0.0.1 for same-PC tests, a LAN IP on a local network, " +
                "or a VPN IP (ZeroTier/Tailscale) for over-internet play without port forwarding.");

            CfgPort = Config.Bind(
                "Matchmaker", "Port", 9050,
                "UDP port the matchmaker listens on. Must match the matchmaker's configured port.");

            CfgLabel = Config.Bind(
                "Matchmaker", "Label", "Local",
                "Cosmetic region label shown in-game. No functional effect.");

            CfgRole = Config.Bind(
                "Lan", "Role", "Host",
                "LAN role: 'Host' opens a listen socket and runs the embedded broker; 'Joiner' connects " +
                "to the host's Ip:Port. Exactly one player is Host. (Used by the LAN transport.)");

            CfgHostSteamId = Config.Bind(
                "Lan", "HostSteamId", "0",
                "JOINER ONLY: the HOST player's SteamID64 (the long number, e.g. 76561198079457803). " +
                "Required to join without the Steam invite UI. The host can read theirs from the game's " +
                "BepInEx log line 'CreateClient intercepted -> LanPeer (selfId=...)'. 0 disables auto-join.");

            CfgAutoJoin = Config.Bind(
                "Lan", "AutoJoin", true,
                "JOINER ONLY: automatically attempt to join the host every few seconds once in a session " +
                "(requires HostSteamId). If false, press the JoinKey in-game instead.");

            CfgJoinKey = Config.Bind(
                "Lan", "JoinKey", "F6",
                "JOINER ONLY: key to manually trigger a join to the host (used if AutoJoin is off or fails).");

            if (!CfgEnabled.Value)
            {
                Log.LogInfo("Disabled via config; matchmaking redirect NOT applied.");
                return;
            }

            // Build the LAN transport config from the bound settings.
            var lanCfg = new Lan.LanConfig
            {
                Role = string.Equals(CfgRole.Value, "Joiner", StringComparison.OrdinalIgnoreCase)
                    ? Lan.LanRole.Joiner : Lan.LanRole.Host,
                HostIp = CfgIp.Value,
                Port = (ushort)CfgPort.Value,
            };
            Lan.CreateClientPatch.Config = lanCfg;
            Lan.CreateClientPatch.Log = m => Log.LogInfo(m);

            // Host: start the embedded broker (listener + relay) BEFORE the game's client connects.
            if (lanCfg.Role == Lan.LanRole.Host)
            {
                try
                {
                    _hostBroker = new Lan.HostBroker(lanCfg, m => Log.LogInfo(m));
                    _hostBroker.Start();
                }
                catch (Exception e)
                {
                    Log.LogError("Failed to start host broker: " + e);
                }
            }
            else
            {
                // Joiner: enable the join driver (a Harmony postfix on NetworkManager.Update drives
                // the join — see JoinDriver for why we don't use a MonoBehaviour here).
                try
                {
                    ulong hostId = 0;
                    ulong.TryParse(CfgHostSteamId.Value, out hostId);
                    Lan.JoinDriver.ServerKey = CfgServerKey.Value;
                    Lan.JoinDriver.HostSteamId = hostId;
                    Lan.JoinDriver.AutoJoin = CfgAutoJoin.Value;
                    Lan.JoinDriver.Log = m => Log.LogInfo(m);
                    if (Enum.TryParse(CfgJoinKey.Value, true, out UnityEngine.KeyCode kc)) Lan.JoinDriver.JoinKey = kc;
                    Lan.JoinDriver.Enabled = true;
                    Log.LogInfo($"Joiner: join driver enabled (host={hostId}, autoJoin={CfgAutoJoin.Value}, key={Lan.JoinDriver.JoinKey})");
                }
                catch (Exception e)
                {
                    Log.LogError("Failed to set up join driver: " + e);
                }
            }

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(BuildConfigGetServerPatch));
                harmony.PatchAll(typeof(Lan.CreateClientPatch));
                // The join driver is a Harmony patch on NetworkManager.Update; apply it on the Joiner.
                if (lanCfg.Role == Lan.LanRole.Joiner)
                    harmony.PatchAll(typeof(Lan.JoinDriver));
                Log.LogInfo($"Patched. ServerKey={CfgServerKey.Value} Role={lanCfg.Role} Host={lanCfg.HostIp}:{lanCfg.Port}");
            }
            catch (Exception e)
            {
                Log.LogError("Failed to apply Harmony patch: " + e);
            }
        }
    }

    /// <summary>
    /// Postfix on BuildConfig.GetServer(string key, bool mayPing, out bool found).
    /// Replaces whatever server config the game resolved with our local one.
    /// </summary>
    [HarmonyPatch(typeof(BuildConfig), nameof(BuildConfig.GetServer))]
    internal static class BuildConfigGetServerPatch
    {
        // MatchmakingServerConfig(string) parses "key;region;label;ip;port".
        private static readonly ConstructorInfo MmscCtor =
            typeof(MatchmakingServerConfig).GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);

        // ref __result lets us overwrite the returned MatchmakingServerConfig.
        // 'found' is an out param; force it true so the game treats our server as resolved.
        private static void Postfix(string key, bool mayPing, ref bool found, ref MatchmakingServerConfig __result)
        {
            try
            {
                string serverKey = Plugin.CfgServerKey.Value;
                string label = string.IsNullOrEmpty(Plugin.CfgLabel.Value) ? "Local" : Plugin.CfgLabel.Value;
                string ip = Plugin.CfgIp.Value;
                int port = Plugin.CfgPort.Value;

                // Field order: key;region;label;ip;port  (region non-empty so RegionPinger keeps it in the list)
                string spec = $"{serverKey};{serverKey};{label};{ip};{port}";

                if (MmscCtor == null)
                {
                    Plugin.Log.LogError("MatchmakingServerConfig(string) ctor not found; cannot redirect.");
                    return;
                }

                __result = (MatchmakingServerConfig)MmscCtor.Invoke(new object[] { spec });
                found = true;
                Plugin.Log.LogInfo($"GetServer(\"{key}\") -> redirected to {spec}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Redirect postfix failed: " + e);
            }
        }
    }
}
