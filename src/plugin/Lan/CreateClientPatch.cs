using System;
using System.Reflection;
using HarmonyLib;
using Nexile.SteamyChimp;
using Steamworks;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// Swaps the game's Steam-backed transport for our LanPeer. SteamPeer.CreateClient() is the single
    /// factory the game calls to build its ISteamPeer (confirmed by the probe). A prefix that sets
    /// __result and returns false replaces the transport for the whole networking layer.
    ///
    /// We keep REAL Steam initialized (for ownership + identity), so SteamUser.GetSteamID() gives us a
    /// genuine, distinct SteamID per player to use as our LanPeer identity.
    /// </summary>
    [HarmonyPatch(typeof(SteamPeer), nameof(SteamPeer.CreateClient))]
    internal static class CreateClientPatch
    {
        internal static LanConfig Config;
        internal static Action<string> Log = _ => { };
        internal static LanPeer ActivePeer;

        private static bool Prefix(ref ISteamPeer __result)
        {
            try
            {
                ulong selfId = ResolveSelfId();
                var peer = new LanPeer(selfId, Config ?? new LanConfig(), Log, PeerMode.Connect);
                ActivePeer = peer;
                __result = peer;
                Log($"[lan] SteamPeer.CreateClient intercepted -> LanPeer (selfId={selfId}, role={Config?.Role})");
                return false; // skip original; use our peer
            }
            catch (Exception e)
            {
                Log("[lan] CreateClient prefix failed, falling back to vanilla: " + e);
                return true; // let the original run if we somehow fail
            }
        }

        private static ulong ResolveSelfId()
        {
            // Real Steam is up (we did not remove it), so this returns the player's real SteamID64.
            try
            {
                if (SteamAPI.IsSteamRunning())
                {
                    var id = SteamUser.GetSteamID().m_SteamID;
                    if (id != 0) return id;
                }
            }
            catch { }
            // Fallback: synthesize a stable-ish id from role so host/joiner differ even without Steam.
            return Config != null && Config.Role == LanRole.Host ? 1UL : 2UL;
        }
    }
}
