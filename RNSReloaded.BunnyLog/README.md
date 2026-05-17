# BunnyLog

Live telemetry mod for Rabbit and Steel. Scrapes battle, buff, and hallway events from GameMaker script hooks and ships them over TCP to a [BunnyLog-Relay](../../BunnyLog-Relay) companion process for terminal-UI viewing (today) and remote/web display (later).

The wire format is newline-delimited JSON. See `Network/Codec/NdjsonCodec.cs` for the envelope and the relay README for the schema reference.

## Configuration

Reloaded II loads `Config.json` for this mod. Defaults:

| Field | Default | Notes |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `Host` | `127.0.0.1` | Relay host. |
| `Port` | `47999` | Relay port. |
| `BufferCapacity` | `10000` | Bounded in-memory queue between the game thread and the network pump. Drop-oldest when full. |
| `Codec` | `ndjson` | Wire codec. Future: `msgpack`. |
| `LogDropsToReloadedLogger` | `true` | Surface buffer overflow in the Reloaded log. |
