using System;
using System.Globalization;
using System.IO;

namespace JkqMatchmaker
{
    /// <summary>
    /// Tiny key=value config. Created with defaults on first run.
    /// </summary>
    internal sealed class MatchmakerConfig
    {
        public ushort Port = 9050;
        public uint AppId = 2317640u;

        public static MatchmakerConfig LoadOrCreate(string path)
        {
            var cfg = new MatchmakerConfig();
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "# JKQ local matchmaker config\n" +
                        "# Port the matchmaker listens on. Must match the BepInEx plugin's Matchmaker/Port.\n" +
                        "Port=9050\n" +
                        "# Steam AppId for JUMP KING QUEST. Leave as-is unless you know otherwise.\n" +
                        "AppId=2317640\n");
                    return cfg;
                }

                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "port":
                            if (ushort.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) cfg.Port = p;
                            break;
                        case "appid":
                            if (uint.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) cfg.AppId = a;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[warn] config load failed, using defaults: " + e.Message);
            }
            return cfg;
        }
    }
}
