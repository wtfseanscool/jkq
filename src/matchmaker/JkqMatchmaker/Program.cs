using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Nexile.SteamyChimp;

namespace JkqMatchmaker
{
    internal static class Program
    {
        // JUMP KING QUEST app id (used for the steam_appid.txt the emulator/game-server init needs).
        private const uint DefaultAppId = 2317640u;
        private const ushort DefaultPort = 9050;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetLastError();

        /// <summary>
        /// The online-fix emulator is normally injected because UnityPlayer.dll imports winmm.dll,
        /// which triggers the winmm proxy -> OnlineFix64.dll. A plain console exe imports neither,
        /// so we must force-load the emulator ourselves BEFORE any Steamworks P/Invoke. OnlineFix64's
        /// DllMain installs the API hooks on attach (incl. resolving SteamInternal_GameServer_Init).
        /// </summary>
        private static void PreloadEmulator()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;

            // IMPORTANT: do NOT load winmm.dll here. The online-fix winmm.dll is a *Mono game loader*
            // (it looks up mono_jit_init_version and invokes OnlineFix.Main:Init). In a plain .NET
            // (CLR / mscoree) process it fails its environment check with "Self-protection failed,
            // error code 4". The matchmaker is not a Mono process, so that path can never work.
            //
            // The emulator core we actually need is OnlineFix64.dll (a steamclient-style emulator
            // exporting CreateInterface). steam_api64.dll (the shim) loads/binds it when Steam is
            // initialized. We set the AppId env vars the shim/emulator read, then let Steamworks.NET's
            // GameServer.Init drive the rest.
            Environment.SetEnvironmentVariable("SteamAppId", DefaultAppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", DefaultAppId.ToString());
            Environment.SetEnvironmentVariable("SteamOverlayGameId", DefaultAppId.ToString());

            // Pre-load the emulator core and the API shim from our own folder so the loader resolves
            // them locally (not from any system path). Order: emulator core first, then the shim.
            foreach (var dll in new[] { "OnlineFix64.dll", "steam_api64.dll" })
            {
                string path = Path.Combine(dir, dll);
                if (!File.Exists(path))
                {
                    Log($"[warn] emulator component missing: {dll}");
                    continue;
                }
                IntPtr h = LoadLibraryW(path);
                if (h == IntPtr.Zero)
                    Log($"[warn] LoadLibrary({dll}) failed, err={GetLastError()}");
                else
                    Log($"[load ] {dll} -> 0x{h.ToInt64():X}");
            }
        }

        private static int Main(string[] args)
        {
            Console.Title = "JKQ Local Matchmaker";
            Log("JUMP KING QUEST — invite-only local matchmaker");
            Log("-----------------------------------------------");

            var cfg = MatchmakerConfig.LoadOrCreate(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "matchmaker.cfg"));

            uint appId = cfg.AppId == 0 ? DefaultAppId : cfg.AppId;
            ushort port = cfg.Port == 0 ? DefaultPort : cfg.Port;

            // The emulator / Steam game-server init requires a steam_appid.txt in the working dir.
            EnsureSteamAppIdFile(appId);

            // Force-load the online-fix emulator before any Steam call.
            PreloadEmulator();

            Log($"AppId={appId}  ListenPort={port}");
            Log($"Working dir: {Directory.GetCurrentDirectory()}");
            Log("Initializing Steam game server (via online-fix emulated steam_api64)...");

            ISteamPeer peer;
            try
            {
                // gamePort/queryPort: use the configured port and port+1 for the query socket.
                // directory/productId/fullName are cosmetic server metadata.
                peer = SteamPeer.CreateServer(port, (ushort)(port + 1), "jkq", (int)appId, "JUMP KING QUEST Local MM");
            }
            catch (Exception e)
            {
                Log("[FATAL] CreateServer failed: " + e.Message);
                Log("Make sure this exe sits next to the online-fix DLLs (OnlineFix64.dll, steam_api64.dll,");
                Log("winmm.dll, SteamOverlay64.dll) and a steam_appid.txt, and that nothing else is bound to the port.");
                return 2;
            }

            MatchmakerServer server = null;
            try
            {
                server = new MatchmakerServer(peer, Log);
                server.Start();

                // Open the IP listen socket so clients' ConnectByIPAddress(ip:port) can reach us.
                peer.CreateIPSocket(port);
                Log($"Listening on UDP {port}. Waiting for clients...");
                Log("(Clients must be redirected here by the JKQLocalMM BepInEx plugin using the SAME ServerKey.)");

                bool running = true;
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

                int tick = 0;
                int lastCount = -1;
                while (running)
                {
                    server.Update();    // pumps peer.Update() -> RunCallbacks + message receive
                    if (server.ClientCount != lastCount)
                    {
                        lastCount = server.ClientCount;
                        Log($"[stat ] connected clients: {lastCount}");
                    }
                    Thread.Sleep(16);   // ~60 Hz, matches the game's pacing closely enough
                    tick++;
                }

                Log("Shutting down...");
            }
            catch (Exception e)
            {
                Log("[FATAL] runtime error: " + e);
                return 3;
            }
            finally
            {
                try { server?.Stop(); } catch { /* best effort */ }
            }

            return 0;
        }

        private static void EnsureSteamAppIdFile(uint appId)
        {
            try
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
                File.WriteAllText(path, appId.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Log("[warn] could not write steam_appid.txt: " + e.Message);
            }
        }

        private static void Log(string msg)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
        }
    }
}
