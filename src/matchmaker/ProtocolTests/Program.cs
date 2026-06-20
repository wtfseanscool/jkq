using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace JkqChimp.Tests
{
    /// <summary>
    /// Cross-validates our reconstructed wire format (JkqChimpProtocol) against the REAL game
    /// assemblies (ChimpKeeperShared) by serializing equivalent values with both and comparing bytes.
    /// Exits non-zero on any mismatch.
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static int Main()
        {
            Console.WriteLine("=== ChimpWorld wire-format cross-validation ===");

            TestPlayerConnectionData();
            TestPlayerConnectionInitialData();
            TestReaderRoundTrip();

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("ALL CHECKS PASSED");
                return 0;
            }
            Console.WriteLine($"{_failures} CHECK(S) FAILED");
            return 1;
        }

        // --- helpers to invoke the real game types via their IPlayerConnectionData interface ---

        private static byte[] GameSerialize(object gameObj)
        {
            // ChimpKeeper.Shared types implement Nexile.ChimpWorld.Matchmaking.IPlayerConnectionData explicitly.
            var iface = gameObj.GetType().GetInterfaces()
                .FirstOrDefault(i => i.Name == "IPlayerConnectionData");
            if (iface == null) throw new Exception("Game type doesn't implement IPlayerConnectionData: " + gameObj.GetType());

            var writeMethod = iface.GetMethod("Write");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Utf8NoBom, true))
            {
                // Explicit interface impl: invoke through the interface map.
                var map = gameObj.GetType().GetInterfaceMap(iface);
                var idx = Array.FindIndex(map.InterfaceMethods, m => m.Name == "Write");
                map.TargetMethods[idx].Invoke(gameObj, new object[] { w });
                w.Flush();
                return ms.ToArray();
            }
        }

        private static object MakeGame(string typeName, params object[] ctorArgs)
        {
            var asm = Assembly.Load("ChimpKeeperShared");
            var type = asm.GetTypes().First(t => t.Name == typeName);
            return Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, ctorArgs, null);
        }

        private static byte[] MySerialize(IPlayerConnectionData mine)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Utf8NoBom, true))
            {
                mine.Write(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void Compare(string label, byte[] expected, byte[] actual)
        {
            string e = BitConverter.ToString(expected);
            string a = BitConverter.ToString(actual);
            if (e == a)
            {
                Console.WriteLine($"  PASS  {label}  [{a}]");
            }
            else
            {
                _failures++;
                Console.WriteLine($"  FAIL  {label}");
                Console.WriteLine($"        game(expected): {e}");
                Console.WriteLine($"        ours(actual)  : {a}");
            }
        }

        private static void TestPlayerConnectionData()
        {
            Console.WriteLine("\n[PlayerConnectionData: ushort Location, float ProgressionValue]");
            // Game ctor: PlayerConnectionData(ushort location, float value)
            (ushort, float)[] cases = { ((ushort)0, 0f), ((ushort)1, 0.5f), ((ushort)65535, 1f), ((ushort)1234, 0.123456f) };
            foreach (var (loc, val) in cases)
            {
                var game = MakeGame("PlayerConnectionData", loc, val);
                var mine = new PlayerConnectionData(loc, val);
                Compare($"PlayerConnectionData({loc},{val})", GameSerialize(game), MySerialize(mine));
            }
        }

        private static void TestPlayerConnectionInitialData()
        {
            Console.WriteLine("\n[PlayerConnectionInitialData: string Version+\"___\", byte proto, byte pingFilter]");
            // Game ctor: PlayerConnectionInitialData(string version, PingFilterPreset pingFilter)
            var asm = Assembly.Load("ChimpKeeperShared");
            var pfpType = asm.GetTypes().First(t => t.Name == "PingFilterPreset");
            string[] versions = { "1.0.0", "0.9.13b", "" };
            foreach (var v in versions)
            {
                foreach (var pf in new[] { 0, 1, 2 })
                {
                    var pfVal = Enum.ToObject(pfpType, pf);
                    var game = MakeGame("PlayerConnectionInitialData", v, pfVal);
                    var mine = new PlayerConnectionInitialData(v, (PingFilterPreset)pf);
                    Compare($"InitialData(\"{v}\",pf={pf})", GameSerialize(game), MySerialize(mine));
                }
            }
        }

        private static void TestReaderRoundTrip()
        {
            Console.WriteLine("\n[Round-trip: our Write -> our Read yields same bytes]");
            var d = new PlayerConnectionData(4321, 0.789f);
            var bytes = MySerialize(d);
            var d2 = new PlayerConnectionData();
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms, Utf8NoBom))
                d2.Read(r);
            var bytes2 = MySerialize(d2);
            Compare("PlayerConnectionData round-trip", bytes, bytes2);

            var init = new PlayerConnectionInitialData("2.1.0", PingFilterPreset.Normal);
            var ib = MySerialize(init);
            var init2 = new PlayerConnectionInitialData();
            using (var ms = new MemoryStream(ib))
            using (var r = new BinaryReader(ms, Utf8NoBom))
                init2.Read(r);
            Compare("InitialData round-trip", ib, MySerialize(init2));
        }
    }
}
