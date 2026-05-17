namespace RNSReloaded.BunnyLog.Producer;

/// <summary>
/// Source-of-truth for scraped game events. Unlike DamageTracker's variant which dispatches
/// per-type, BunnyLog uses a single subscription point keyed on the polymorphic
/// <see cref="BunnyLogEvent"/> base so consumers (network, future file mirror, future ImGui debug
/// overlay) don't have to subscribe nine times each.
/// </summary>
public interface ILogProducer {
    void Subscribe(Action<BunnyLogEvent> consumer);
}
