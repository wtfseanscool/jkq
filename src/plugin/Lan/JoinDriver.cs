using System;
using HarmonyLib;
using UnityEngine;
using Nexile.JKQuest;
using Nexile.JKQuest.Networking;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// Drives the join on the JOINER side WITHOUT the Steam invite/overlay UI (which we bypass on LAN).
    /// The game's join entry point is NetworkManager.Join(key, password) where key = "{ServerKey} {hostSteamId}".
    ///
    /// IMPORTANT — why this is a Harmony patch and not a MonoBehaviour:
    /// A previous version added a MonoBehaviour to the BepInEx plugin GameObject and relied on its
    /// Unity Start()/Update() messages. In JKQ those messages never fired (the plugin object is not
    /// pumped by the game's player loop in a way our added component sees), so the join was never
    /// attempted. Instead we postfix NetworkManager.Update(bool) — the game calls this every frame on
    /// its own loop, with `this` being the live NetworkManager — guaranteeing execution and a non-null
    /// session. No dependency on our own MonoBehaviour lifecycle.
    ///
    /// JKQ uses the NEW Input System, so legacy UnityEngine.Input.GetKeyDown THROWS. The keybind uses
    /// the new Input System via reflection (best-effort); auto-join is the reliable, input-free path.
    /// </summary>
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Update))]
    internal static class JoinDriver
    {
        internal static string ServerKey = "internal";
        internal static ulong HostSteamId = 0;
        internal static KeyCode JoinKey = KeyCode.F6;
        internal static bool AutoJoin = true;
        internal static Action<string> Log = _ => { };
        internal static bool Enabled = false;   // only true on the Joiner

        private static float _nextAuto;
        private static int _autoAttempts;
        private const int MaxAutoAttempts = 240;    // ~12 min at 3s spacing
        private static bool _joinedOnce;
        private static bool _aliveLogged;
        private static bool _notifyHooked;
        private static string _lastNotReady = "";

        // Reflection handles for the new Input System keybind (resolved lazily, optional).
        private static bool _inputResolved;
        private static Func<bool> _keyPressed;

        /// <summary>Postfix on NetworkManager.Update — runs every frame on the game's own loop.</summary>
        private static void Postfix(NetworkManager __instance)
        {
            if (!Enabled) return;

            if (!_aliveLogged)
            {
                _aliveLogged = true;
                Log("[join] driver live (NetworkManager.Update postfix is firing)");
            }

            TryHookNotifications(__instance);

            // --- AUTO-JOIN; needs no input API so it always runs. ---
            if (AutoJoin && HostSteamId != 0 && !_joinedOnce
                && _autoAttempts < MaxAutoAttempts && Time.unscaledTime >= _nextAuto)
            {
                _nextAuto = Time.unscaledTime + 3f;
                TryJoin(__instance, auto: true);
            }

            // --- Manual keybind via the NEW Input System (best-effort, never throws). ---
            try
            {
                if (!_inputResolved) ResolveNewInput();
                if (_keyPressed != null && _keyPressed())
                {
                    if (_joinedOnce)
                    {
                        Log($"[join] {JoinKey} pressed but a join is already in progress; ignoring (avoids duplicate InviteJoin)");
                    }
                    else
                    {
                        Log($"[join] {JoinKey} pressed (new Input System) -> manual join");
                        TryJoin(__instance, auto: false);
                    }
                }
            }
            catch { /* keybind is optional; auto-join is the reliable path */ }
        }

        private static void TryHookNotifications(NetworkManager net)
        {
            if (_notifyHooked) return;
            try
            {
                if (net == null) return;
                net.OnJoinNotification += (id, type, party) =>
                {
                    Log($"[join] <<< OnJoinNotification: id={id} type={type} party={party}");
                    // Stop auto-retrying once the join has actually been initiated/accepted. Keep
                    // retrying on hard rejections so a config fix can still take effect.
                    string t = type.ToString();
                    if (t.IndexOf("Initiated", StringComparison.OrdinalIgnoreCase) >= 0
                        || t.IndexOf("Connected", StringComparison.OrdinalIgnoreCase) >= 0
                        || t.IndexOf("Shortcut", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!_joinedOnce) Log($"[join] join initiated (type={type}); auto-retry disabled");
                        _joinedOnce = true;
                    }
                };
                _notifyHooked = true;
                Log("[join] hooked OnJoinNotification (will report the game's join verdict)");
            }
            catch (Exception e) { Log("[join] could not hook OnJoinNotification: " + e.Message); }
        }

        private static void TryJoin(NetworkManager net, bool auto)
        {
            if (HostSteamId == 0)
            {
                Throttle("HostSteamId not set; set [Lan] HostSteamId in the config");
                return;
            }
            try
            {
                if (net == null)
                {
                    Throttle("NetworkManager null; waiting");
                    return;
                }
                if (net.IsSinglePlayer)
                {
                    Throttle("session is single-player (matchmaker not connected); enable invasions/online");
                    return;
                }
                var status = net.Status; // MatchmakerStatus
                if (status.ToString() != "Connected")
                {
                    Throttle($"matchmaker status={status} (need Connected); waiting");
                    return;
                }

                _autoAttempts++;
                string key = $"{ServerKey} {HostSteamId}";
                Log($"[join] attempt {_autoAttempts} (auto={auto}): Networking.Join(\"{key}\", 0)  " +
                    $"[status={status} selfId={net.NetworkingId}]");
                net.Join(key, 0);
                Log("[join] Networking.Join returned — watch for OnJoinNotification + broker INVITE/STEP lines");
            }
            catch (Exception e)
            {
                Log("[join] Join threw: " + e);
            }
        }

        private static void Throttle(string msg)
        {
            if (_lastNotReady == msg) return;
            _lastNotReady = msg;
            Log("[join] waiting: " + msg);
        }

        /// <summary>
        /// Resolve Keyboard.current[Key.F6].wasPressedThisFrame via reflection so we don't hard-link
        /// Unity.InputSystem at compile time. If anything is missing, _keyPressed stays null.
        /// </summary>
        private static void ResolveNewInput()
        {
            _inputResolved = true;
            try
            {
                var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                var keyEnumType  = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
                if (keyboardType == null || keyEnumType == null) { Log("[join] new Input System not found; keybind disabled (use auto-join)"); return; }

                var currentProp = keyboardType.GetProperty("current");
                object keyVal;
                try { keyVal = Enum.Parse(keyEnumType, JoinKey.ToString()); }
                catch { Log($"[join] key {JoinKey} not mappable to new Input System; keybind disabled"); return; }

                _keyPressed = () =>
                {
                    var kb = currentProp.GetValue(null);
                    if (kb == null) return false;
                    var indexer = keyboardType.GetProperty("Item", new[] { keyEnumType });
                    var keyControl = indexer?.GetValue(kb, new[] { keyVal });
                    if (keyControl == null) return false;
                    var wasPressed = keyControl.GetType().GetProperty("wasPressedThisFrame");
                    return wasPressed != null && (bool)wasPressed.GetValue(keyControl);
                };
                Log("[join] keybind ready via new Input System");
            }
            catch (Exception e)
            {
                Log("[join] could not resolve new Input System keybind: " + e.Message + " (auto-join still works)");
            }
        }
    }
}
