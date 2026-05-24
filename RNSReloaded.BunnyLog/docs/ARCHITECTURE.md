# RNSReloaded.BunnyLog architecture

This document is the cold-reader entry point for the mod. It explains how the pieces fit, where the seams are, and how to extend each one. If you're touching the code for the first time, read this top-to-bottom before opening any `.cs` file.

The relay side (the companion process this mod ships events to) lives in a **separate repository** at `/home/alexasu/repos/BunnyLog-Relay`; its architecture is described in that repo's `docs/ARCHITECTURE.md`.

## What this mod does

A Reloaded II mod loaded into `rabbitsteel.exe` (the Rabbit and Steel game) that hooks ~8 GameMaker scripts (more on the debug branch), packages each hook firing as a typed event, and ships those events over TCP to the BunnyLog-Relay companion process. It never blocks the game thread on I/O.

## Pipeline

```
GameMaker script call               background task (started in Mod.Ready)
  --> hook detour                         |
       (game thread)                      v
       LookupAbility / Probe* / etc       ChannelReader.ReadAllAsync
       Emit(typed BunnyLogEvent)          | for each event:
       --> ILogProducer subscribers       |   IEventCodec.EncodeEvent
            |                             |   ITransport.SendAsync
            v                             |
       NetworkConsumer.OnEvent            on Send failure:
            \                             |   close transport
             \--> Channel<BunnyLogEvent>  |   wait exponential backoff
                  (bounded, DropOldest)   |   reconnect; flush queued events
                  ^                       |
                  |                       |
       game thread enqueue (never blocks; capacity 10k by default)
```

Two important architectural points:

1. **Game thread MUST never block.** Every detour finishes quickly: read a few RValues, allocate the event record, call `Channel.Writer.TryWrite`, return. The bounded `DropOldest` channel is the firewall — if the relay is gone or slow, we drop oldest events rather than freezing the game.
2. **The producer is decoupled from the network.** `ILogProducer` accepts subscriptions of `Action<BunnyLogEvent>`. The network is just one subscriber (`NetworkConsumer`). A future "write to disk via the mod itself" or "in-game ImGui debug" consumer is the same pattern.

## File map (`RNSReloaded.BunnyLog/`)

| File | Owns |
|---|---|
| `Mod.cs` | `IMod` entry point. Resolves `IRNSReloaded` + `IReloadedHooks` controllers, loads config, subscribes `Ready` to `OnReady`, then in `Ready`: constructs `LogProducer`, picks codec, builds the `transportFactory` closure, constructs `NetworkPump`, wires `NetworkConsumer`. |
| `ModConfig.json` | Reloaded II mod metadata + dependency declarations. Declares dependency on `RNSReloaded` and `reloaded.sharedlib.hooks`. |
| `Config/Config.cs` | Config schema: `Enabled`, `Host`, `Port`, `BufferCapacity`, `Codec`, `LogDropsToReloadedLogger`. |
| `Config/Configurable.cs` | Hot-reload base class (copied from DamageTracker pattern). Watches the config file with `FileSystemWatcher`; swaps a new instance in on change. |
| `Config/Configurator.cs` | Implements `IConfiguratorV3` — Reloaded II's UI integration for our `Config` class. |
| `Producer/Events.cs` | The whole event type hierarchy: abstract `BunnyLogEvent(long GameTime)` base + one `sealed record` per event type (DamageEvent, DebuffDamageEvent, NewEnemyEvent, NewFightEvent, HallwayMoveEvent, ChooseHallsEvent, AddBuffEvent, RemoveBuffEvent, EndFightEvent). Each record owns its own `WriteDataFields(Utf8JsonWriter)` — explicit, version-controlled wire projection (no reflection). |
| `Producer/ILogProducer.cs` | Subscription interface: `void Subscribe(Action<BunnyLogEvent>)`. Single dispatch point — all event types flow through one subscription. |
| `Producer/LogProducer.cs` | The hook hub. Creates `IHook<ScriptDelegate>` for each GameMaker script we care about, holds the detour methods, holds the consumers list, exposes shared read helpers (`LookupPlayerName`, `LookupPlayerCharId`, `LookupAbility`, etc.). |
| `Network/NetworkConsumer.cs` | The bridge from game thread to background task. Subscribes to the producer, calls `pump.Writer.TryWrite(ev)`. That's it — no synchronization, no allocation beyond the event record itself. |
| `Network/NetworkPump.cs` | The background `Task` that owns the bounded `Channel<BunnyLogEvent>`, the codec, the transport factory, the reconnect loop. Sends a Hello frame on every successful (re)connect, then drains the channel until error or cancellation. |
| `Network/Codec/IEventCodec.cs` | Codec abstraction: `Name`, `SchemaVersion`, `EncodeHello`, `EncodeEvent`. Lets us swap NDJSON for MessagePack or framed binary later without touching transport or pump. |
| `Network/Codec/NdjsonCodec.cs` | NDJSON wire codec — one JSON object per frame, `\n`-terminated. Uses `Utf8JsonWriter` directly so we don't depend on `System.Text.Json`'s polymorphic serialization. |
| `Network/Transport/ITransport.cs` | Transport abstraction: `ConnectAsync`, `SendAsync`, `IAsyncDisposable`. Per-connection lifecycle, so a fresh transport is constructed each reconnect attempt. |
| `Network/Transport/TcpTransport.cs` | The one transport today — wraps `TcpClient` + `NetworkStream` with `NoDelay = true`. |

## Threading model

- **Game thread**: every detour and every event production happens here. Bounded by GameMaker's tick rate.
- **Pump background `Task`**: started in `Mod.Ready` via `Task.Run`. Owns the `Channel<BunnyLogEvent>.Reader`, the codec, and the reconnect lifecycle. Sends one frame per dequeued event. Wakes the game thread never — only consumes.
- **Reloaded II FileSystemWatcher** (config hot-reload): runs on the OS threadpool. Doesn't touch our event path.

The `Channel<BunnyLogEvent>` is configured `BoundedChannelOptions { FullMode = DropOldest, SingleReader = true, SingleWriter = false }`. SingleWriter is false because the producer's `Emit` could in theory dispatch from any thread (currently only the game thread, but the contract doesn't require it).

## What's hooked today

| Script | Detour | Event(s) emitted |
|---|---|---|
| `scr_pattern_deal_damage_enemy_subtract` | `EnemyDamageDetour` | `DamageEvent` if `hbId != -1`, else `DebuffDamageEvent` |
| `scrdt_encounter` | `NewFightDetour` | `NewFightEvent` |
| `scrdt_enemy` | `AddEnemyDetour` | `NewEnemyEvent` |
| `scr_hallwayprogress_move_next` | `HallwayMoveDetour` | `HallwayMoveEvent` |
| `scr_hallwayprogress_choose_halls` | `ChooseHallsDetour` | `ChooseHallsEvent` |
| `scr_trigger_call` | `TriggerCallDetour` | `AddBuffEvent` / `RemoveBuffEvent` (filtered by `teamId == 0`) |
| `scr_gamecontrol_do_gameover` | `GameOverDetour` | `EndFightEvent { Victory = false }` |
| `scr_battlecontroller_end_round` | `FinishedFightDetour` | `EndFightEvent { Victory = true }` |

The `bunnylog-debug` branch additionally hooks `scr_player_invuln`, `scr_pattern_deal_damage_ally`, `scr_rankbar_give_rewards` for ongoing survey work — see [Debug branches](#debug-branches).

## Wire protocol

The mod is the **client**; the relay is the server. On every (re)connect the mod sends a Hello frame, then begins flushing events from the channel:

```json
{"t":"hello","schema":1,"mod":"BunnyLog","modVersion":"0.1.0","codec":"ndjson","sentAt":<utc-ms>}
{"t":"event","name":"<EventName>","gameTime":<ms>,"data":{ ...per-event fields... }}
```

`schema` is the wire schema major; bumps only on breaking changes. **Additive changes** (new event names, new optional fields) do NOT bump it. See the relay's `docs/ARCHITECTURE.md` for the relay-side decode semantics.

## Reloaded II conventions worth knowing

- `IMod.StartEx(loader, modConfig)` runs *before* the game is fully initialized. **Don't call `IRNSReloaded` APIs there.** Subscribe to `IRNSReloaded.OnReady` and do the real wiring in your `Ready()` callback.
- Controllers (`loader.GetController<T>()`) return `WeakReference<T>`. Always `TryGetTarget` before use.
- Hooks must be **activated and enabled** to take effect: see `HookScript` helper in `LogProducer.cs`.
- The Reloaded logger (`ILoggerV1`) is the right place for occasional status messages. **Don't** spam it from inside detours.

## Extension recipes

### Add a new GameMaker hook

1. **`Producer/Events.cs`**: add a new `sealed record YourEvent(...) : BunnyLogEvent(GameTime)`. Override `EventName` (returns the wire `name` string, matched on the relay side) and `WriteDataFields(Utf8JsonWriter)`.
2. **`Producer/LogProducer.cs`**:
   - Add a `private IHook<ScriptDelegate> yourHook = null!;` field.
   - In the constructor, `this.yourHook = this.HookScript(hooks, "scr_target_name", this.YourDetour);`.
   - Implement `private RValue* YourDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv)` — must end with `return this.yourHook.OriginalFunction(self, other, returnValue, argc, argv);`. Keep the body short. Use the existing `Read*` helpers for try/catch'd reads.
3. **Relay side**: mirror the new event type per the relay's `docs/ARCHITECTURE.md` "Add a new event type" recipe.

### Scrape a new field from `self` or a global

1. Find the right place to read it (a specific detour, or a constructor-time global read).
2. Wrap the read in `try { ... } catch { }` independently — never let one failed read blank out the others.
3. Use the existing helpers when the field is on `self`: `ReadSelfInt(rns, self, "fieldName")`, `ReadSelfString(rns, self, "fieldName")`, `ReadGlobalString(rns, "globalName")`. If reading argv: `ReadArgvString(argc, argv, idx)`.
4. Add the field to the relevant `record` in `Events.cs`, and to `WriteDataFields`.

### Add a new codec (e.g., MessagePack)

1. **`Network/Codec/MsgPackCodec.cs`**: implement `IEventCodec`. The two methods take an `IBufferWriter<byte>` and write the encoded frame. Decide framing (length-prefix is conventional for binary).
2. **`Mod.cs`**: in `Ready()`, extend the `codec` switch:
   ```csharp
   IEventCodec codec = this.config.Codec.Trim().ToLowerInvariant() switch {
       "ndjson" => new NdjsonCodec(),
       "msgpack" => new MsgPackCodec(),
       var other => throw new InvalidOperationException(...),
   };
   ```
3. The relay must support the new codec too — see the relay-side recipe.

### Add a new transport (named pipes, Unix socket, etc.)

1. **`Network/Transport/FooTransport.cs`**: implement `ITransport`. The `transportFactory` closure (a `Func<ITransport>`) constructs a fresh instance per reconnect attempt.
2. **`Mod.cs`**: branch the factory on config. Currently the factory is `() => new TcpTransport(host, port)`; add config fields and a branch.

### Add a new consumer (besides NetworkConsumer)

Implement a class that subscribes to `ILogProducer` and does whatever you want with the events:

```csharp
public sealed class MyConsumer {
    public MyConsumer(ILogProducer producer) {
        producer.Subscribe(this.OnEvent);
    }
    private void OnEvent(BunnyLogEvent ev) {
        // …  Keep this CHEAP; runs on game thread.
    }
}
```

Wire it in `Mod.Ready` alongside the existing `NetworkConsumer`. The producer dispatches to all subscribers in order; one bad subscriber can slow down the game thread, so anything non-trivial should hand off to a background task immediately (NetworkConsumer's channel is the canonical pattern).

## Debug branches

The mod's `bunnylog-debug` branch is a sibling to the production `bunnylog` branch reserved for shotgun-probe rounds. It additionally hooks `scr_player_invuln`, `scr_pattern_deal_damage_ally`, `scr_rankbar_give_rewards` and ships probe-bearing variants of existing events (extra "what value is at this field?" candidates) for investigation.

**Never merge `bunnylog-debug` into `bunnylog`.** Once a probe yields a useful answer, promote the winning field as a clean addition on `bunnylog` separately.

## Things deliberately NOT in this codebase yet

- **In-game UI**: BunnyLog has no ImGui consumer. The relay is the UI. DamageTracker's in-game ImGui windows are a separate, parallel mod with no relation to BunnyLog.
- **Local file logging**: the *relay's* `--log-mirror` is the persistent record. The mod could grow a `FileLogConsumer` if it ever became useful as a standalone "no relay running" mode.
- **Two-way protocol**: today the mod sends and the relay receives. No commands flow back. If we ever need them (e.g., "pause the run," "reset state"), define a separate wire frame type and route via a separate channel.
- **Codec / transport runtime selection** beyond `ndjson` / TCP: stubs exist; not exercised.
