namespace RNSReloaded.BunnyLog.Network.Transport;

/// <summary>
/// One-way transport for already-encoded frames. The pump constructs a fresh transport per
/// connection attempt — keeping connection lifecycle inside the transport rather than the pump
/// makes "swap TCP for named pipes" a localized change.
/// </summary>
public interface ITransport : IAsyncDisposable {
    Task ConnectAsync(CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
}
