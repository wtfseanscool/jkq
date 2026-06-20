using System;
using System.Reflection;
using System.Runtime.Serialization;
using Nexile.SteamyChimp;

namespace JKQLocalMM.Lan
{
    /// <summary>
    /// PingLocation has only internal constructors (taking the internal ISteamAPI). On LAN there is no
    /// Steam ping data and we don't need real values — the matchmaking client only reads
    /// PingLocation.LocationString and sends it in InitialData. So we build an uninitialized instance
    /// and set its private 'locationString' backing field directly; the LocationString getter returns
    /// it as-is when non-null.
    /// </summary>
    internal static class PingLocationFactory
    {
        private static readonly FieldInfo LocationStringField =
            typeof(PingLocation).GetField("locationString", BindingFlags.Instance | BindingFlags.NonPublic);

        public static PingLocation Create(string value)
        {
            try
            {
                var obj = (PingLocation)FormatterServices.GetUninitializedObject(typeof(PingLocation));
                LocationStringField?.SetValue(obj, value ?? "lan");
                return obj;
            }
            catch
            {
                return null;
            }
        }
    }
}
