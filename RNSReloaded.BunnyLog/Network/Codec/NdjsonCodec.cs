using System.Buffers;
using System.Text.Json;
using RNSReloaded.BunnyLog.Producer;

namespace RNSReloaded.BunnyLog.Network.Codec;

/// <summary>
/// Newline-delimited JSON wire codec. One JSON object per frame, terminated by a single '\n'.
/// Frame envelope:
///   {"t":"hello",...}
///   {"t":"event","name":"&lt;type&gt;","gameTime":&lt;ms&gt;,"data":{...}}
/// </summary>
public sealed class NdjsonCodec : IEventCodec {
    private static readonly JsonWriterOptions WriterOpts = new() {
        Indented = false,
        SkipValidation = false,
    };

    public string Name => "ndjson";
    public int SchemaVersion => 1;

    public void EncodeHello(IBufferWriter<byte> writer, string modName, string modVersion) {
        using var jw = new Utf8JsonWriter(writer, WriterOpts);
        jw.WriteStartObject();
        jw.WriteString("t", "hello");
        jw.WriteNumber("schema", this.SchemaVersion);
        jw.WriteString("mod", modName);
        jw.WriteString("modVersion", modVersion);
        jw.WriteString("codec", this.Name);
        jw.WriteNumber("sentAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        jw.WriteEndObject();
        jw.Flush();
        WriteNewline(writer);
    }

    public void EncodeEvent(IBufferWriter<byte> writer, BunnyLogEvent ev) {
        using var jw = new Utf8JsonWriter(writer, WriterOpts);
        jw.WriteStartObject();
        jw.WriteString("t", "event");
        jw.WriteString("name", ev.EventName);
        jw.WriteNumber("gameTime", ev.GameTime);
        jw.WriteStartObject("data");
        ev.WriteDataFields(jw);
        jw.WriteEndObject();
        jw.WriteEndObject();
        jw.Flush();
        WriteNewline(writer);
    }

    private static void WriteNewline(IBufferWriter<byte> writer) {
        var span = writer.GetSpan(1);
        span[0] = (byte) '\n';
        writer.Advance(1);
    }
}
