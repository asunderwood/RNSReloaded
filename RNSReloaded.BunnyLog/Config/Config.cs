using System.ComponentModel;

namespace RNSReloaded.BunnyLog.Config;

public class Config : Configurable<Config> {
    [DisplayName("Enabled")]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [DisplayName("Relay host")]
    [DefaultValue("127.0.0.1")]
    public string Host { get; set; } = "127.0.0.1";

    [DisplayName("Relay port")]
    [DefaultValue(47999)]
    public int Port { get; set; } = 47999;

    [DisplayName("Buffer capacity (events)")]
    [Description("Bounded queue between the game thread and the network pump. Drop-oldest when full.")]
    [DefaultValue(10000)]
    public int BufferCapacity { get; set; } = 10000;

    [DisplayName("Wire codec")]
    [Description("ndjson (default). Future: msgpack.")]
    [DefaultValue("ndjson")]
    public string Codec { get; set; } = "ndjson";

    [DisplayName("Log overflow drops")]
    [DefaultValue(true)]
    public bool LogDropsToReloadedLogger { get; set; } = true;
}
