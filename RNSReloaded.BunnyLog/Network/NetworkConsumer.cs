using RNSReloaded.BunnyLog.Producer;

namespace RNSReloaded.BunnyLog.Network;

/// <summary>
/// Hands events from the (game-thread) producer to the (background-thread) network pump via the
/// pump's bounded channel. Stays minimal so the game thread does as little work as possible.
/// </summary>
internal sealed class NetworkConsumer {
    private readonly NetworkPump pump;

    public NetworkConsumer(ILogProducer producer, NetworkPump pump) {
        this.pump = pump;
        producer.Subscribe(this.OnEvent);
    }

    private void OnEvent(BunnyLogEvent ev) {
        // BoundedChannel in DropOldest mode: TryWrite always succeeds and silently evicts the
        // oldest pending event if the buffer is full. Game thread never blocks.
        this.pump.Writer.TryWrite(ev);
    }
}
