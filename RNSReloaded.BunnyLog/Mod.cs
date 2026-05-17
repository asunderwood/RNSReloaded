using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.BunnyLog.Network;
using RNSReloaded.BunnyLog.Network.Codec;
using RNSReloaded.BunnyLog.Network.Transport;
using RNSReloaded.BunnyLog.Producer;
using RNSReloaded.Interfaces;

namespace RNSReloaded.BunnyLog;

public unsafe class Mod : IMod {
    public const string ModId = "RNSReloaded.BunnyLog";
    public const string ModVersion = "0.1.0";

    private WeakReference<IRNSReloaded>? rnsRef;
    private WeakReference<IReloadedHooks>? hooksRef;
    private ILoggerV1 logger = null!;
    private Config.Config config = null!;

    private LogProducer? producer;
    private NetworkPump? pump;
    private NetworkConsumer? consumer;

    public void StartEx(IModLoaderV1 loader, IModConfigV1 modConfig) {
        this.rnsRef = loader.GetController<IRNSReloaded>();
        this.hooksRef = loader.GetController<IReloadedHooks>();
        this.logger = loader.GetLogger();

        var configurator = new Config.Configurator(((IModLoader) loader).GetModConfigDirectory(modConfig.ModId));
        this.config = configurator.GetConfiguration<Config.Config>(0);

        if (this.rnsRef.TryGetTarget(out var rns)) {
            rns.OnReady += this.Ready;
        }

        this.logger.PrintMessage(
            $"BunnyLog: loaded; target {this.config.Host}:{this.config.Port} (enabled={this.config.Enabled})",
            this.logger.ColorGreen
        );
    }

    public void Ready() {
        if (!this.config.Enabled) {
            this.logger.PrintMessage("BunnyLog: disabled by config; not hooking", this.logger.ColorYellow);
            return;
        }
        if (
            this.rnsRef is not null && this.rnsRef.TryGetTarget(out var rns)
            && this.hooksRef is not null && this.hooksRef.TryGetTarget(out var hooks)
        ) {
            this.producer = new LogProducer(rns, hooks, this.logger);

            IEventCodec codec = this.config.Codec.Trim().ToLowerInvariant() switch {
                "ndjson" => new NdjsonCodec(),
                var other => throw new InvalidOperationException(
                    $"BunnyLog: unknown codec '{other}' in config; valid: ndjson"
                ),
            };

            var host = this.config.Host;
            var port = this.config.Port;
            this.pump = new NetworkPump(
                this.config,
                codec,
                transportFactory: () => new TcpTransport(host, port),
                this.logger
            );
            this.pump.Start();

            this.consumer = new NetworkConsumer(this.producer, this.pump);

            this.logger.PrintMessage(
                $"BunnyLog: ready; pump will connect to {host}:{port}",
                this.logger.ColorGreen
            );
        }
    }

    public void Suspend() { }
    public void Resume() { }
    public bool CanSuspend() => false;

    public void Unload() {
        if (this.pump is not null) {
            // Fire-and-forget dispose; we're on the unload path.
            _ = this.pump.DisposeAsync();
        }
    }
    public bool CanUnload() => false;

    public Action Disposing => () => { };
}
