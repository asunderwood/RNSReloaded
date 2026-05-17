using System.Buffers;
using RNSReloaded.BunnyLog.Producer;

namespace RNSReloaded.BunnyLog.Network.Codec;

/// <summary>
/// Wire codec abstraction. The transport layer never touches typed events directly; it only
/// receives ready-to-send byte buffers from a codec. This is the seam that lets us swap NDJSON
/// for MessagePack (or any binary format) without touching the rest of the pipeline.
/// </summary>
public interface IEventCodec {
    /// <summary>Identifier sent in the Hello frame so the relay knows how to decode.</summary>
    string Name { get; }

    /// <summary>Schema version this codec speaks; bumped on breaking wire changes.</summary>
    int SchemaVersion { get; }

    void EncodeHello(IBufferWriter<byte> writer, string modName, string modVersion);
    void EncodeEvent(IBufferWriter<byte> writer, BunnyLogEvent ev);
}
