namespace JKQLocalMM.Lan
{
    internal enum LanRole { Host, Joiner }

    /// <summary>
    /// Runtime config for the LAN transport, populated from the BepInEx plugin config.
    /// </summary>
    internal sealed class LanConfig
    {
        public LanRole Role = LanRole.Host;
        public string HostIp = "127.0.0.1";
        public ushort Port = 9050;
    }
}
