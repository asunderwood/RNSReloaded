using System.Net.Sockets;

namespace RNSReloaded.BunnyLog.Network.Transport;

public sealed class TcpTransport : ITransport {
    private readonly string host;
    private readonly int port;
    private TcpClient? client;
    private NetworkStream? stream;

    public TcpTransport(string host, int port) {
        this.host = host;
        this.port = port;
    }

    public async Task ConnectAsync(CancellationToken ct) {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(this.host, this.port, ct);
        this.client = c;
        this.stream = c.GetStream();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) {
        if (this.stream is null) {
            throw new InvalidOperationException("TcpTransport.SendAsync called before ConnectAsync");
        }
        await this.stream.WriteAsync(bytes, ct);
    }

    public async ValueTask DisposeAsync() {
        if (this.stream is not null) {
            await this.stream.DisposeAsync();
            this.stream = null;
        }
        this.client?.Dispose();
        this.client = null;
    }
}
