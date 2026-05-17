using System.Buffers;
using System.Threading.Channels;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.BunnyLog.Config;
using RNSReloaded.BunnyLog.Network.Codec;
using RNSReloaded.BunnyLog.Network.Transport;
using RNSReloaded.BunnyLog.Producer;

namespace RNSReloaded.BunnyLog.Network;

/// <summary>
/// Background pump that drains the producer-side channel, encodes events through the active
/// codec, and ships them down the transport. Owns the reconnect loop: if connect or send fails
/// at any point, the transport is disposed, we wait a capped exponential backoff, and try again.
///
/// Events emitted while disconnected accumulate in the bounded channel (drop-oldest); they are
/// flushed in arrival order when the next connection opens.
/// </summary>
internal sealed class NetworkPump : IAsyncDisposable {
    private readonly Config.Config config;
    private readonly IEventCodec codec;
    private readonly Func<ITransport> transportFactory;
    private readonly ILoggerV1 logger;
    private readonly CancellationTokenSource cts = new();
    private readonly Channel<BunnyLogEvent> channel;
    private Task? loopTask;

    public ChannelWriter<BunnyLogEvent> Writer => this.channel.Writer;

    public NetworkPump(
        Config.Config config,
        IEventCodec codec,
        Func<ITransport> transportFactory,
        ILoggerV1 logger
    ) {
        this.config = config;
        this.codec = codec;
        this.transportFactory = transportFactory;
        this.logger = logger;

        var opts = new BoundedChannelOptions(Math.Max(1, config.BufferCapacity)) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        this.channel = System.Threading.Channels.Channel.CreateBounded<BunnyLogEvent>(opts);
    }

    public void Start() {
        this.loopTask = Task.Run(() => this.RunLoopAsync(this.cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct) {
        const int BackoffStartMs = 200;
        const int BackoffMaxMs = 5_000;
        int backoffMs = BackoffStartMs;
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 1024);

        while (!ct.IsCancellationRequested) {
            ITransport? transport = null;
            try {
                transport = this.transportFactory();
                await transport.ConnectAsync(ct);
                this.logger.PrintMessage(
                    $"BunnyLog: pump connected to {this.config.Host}:{this.config.Port}",
                    this.logger.ColorGreen
                );
                backoffMs = BackoffStartMs;

                // Hello frame is sent first on every (re)connect so the relay can validate schema.
                buffer.ResetWrittenCount();
                this.codec.EncodeHello(buffer, modName: "BunnyLog", modVersion: Mod.ModVersion);
                await transport.SendAsync(buffer.WrittenMemory, ct);

                await foreach (var ev in this.channel.Reader.ReadAllAsync(ct)) {
                    buffer.ResetWrittenCount();
                    this.codec.EncodeEvent(buffer, ev);
                    await transport.SendAsync(buffer.WrittenMemory, ct);
                }
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                this.logger.PrintMessage(
                    $"BunnyLog: pump disconnected ({ex.GetType().Name}: {ex.Message}); retrying in {backoffMs}ms",
                    this.logger.ColorYellow
                );
                try {
                    await Task.Delay(backoffMs, ct);
                } catch (OperationCanceledException) {
                    break;
                }
                backoffMs = Math.Min(backoffMs * 2, BackoffMaxMs);
            } finally {
                if (transport is not null) {
                    try { await transport.DisposeAsync(); } catch { /* ignore */ }
                }
            }
        }
    }

    public async ValueTask DisposeAsync() {
        this.cts.Cancel();
        this.channel.Writer.TryComplete();
        if (this.loopTask is not null) {
            try { await this.loopTask; } catch { /* expected on cancel */ }
        }
        this.cts.Dispose();
    }
}
