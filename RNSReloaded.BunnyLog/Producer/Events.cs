using System.Text.Json;
using RNSReloaded.Interfaces;

namespace RNSReloaded.BunnyLog.Producer;

/// <summary>
/// Base class for every BunnyLog event. Each subclass owns its own field projection (via
/// <see cref="WriteDataFields"/>) so the wire envelope stays explicit and version-controlled
/// rather than reflection-driven.
/// </summary>
public abstract record BunnyLogEvent(long GameTime) {
    public abstract string EventName { get; }
    public abstract void WriteDataFields(Utf8JsonWriter writer);
}

/// <summary>
/// Temporary container for the shotgun-probe round on multi-player / item name resolution.
/// Each field corresponds to a candidate value the mod scrapes alongside the known good answer
/// so we can identify the right source by observing values over many events. Removed once we
/// know which source consistently has what we want.
/// </summary>
public sealed record AbilityProbe(
    int SelfId, int ActionScript,
    string Idx0_1, string Idx0_3, string Idx0_4,
    string Idx1_1, string Idx2_0, string Idx2_1,
    string AltHbData, string AltTestItem
);

public sealed record DamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int HbId, int DataId, string AbilityKey, string AbilityName,
    AbilityProbe Probe,
    int Damage, double PainShare, long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "Damage";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteString("playerName", this.PlayerName);
        w.WriteNumber("charId", this.CharId);
        w.WriteNumber("enemyId", this.EnemyId);
        w.WriteNumber("hbId", this.HbId);
        w.WriteNumber("dataId", this.DataId);
        w.WriteString("abilityKey", this.AbilityKey);
        w.WriteString("abilityName", this.AbilityName);
        WriteProbeFields(w, this.Probe);
        w.WriteNumber("damage", this.Damage);
        w.WriteNumber("painShare", this.PainShare);
    }

    internal static void WriteProbeFields(Utf8JsonWriter w, AbilityProbe p) {
        w.WriteNumber("probeSelfId", p.SelfId);
        w.WriteNumber("probeActionScript", p.ActionScript);
        w.WriteString("probeIdx0_1", p.Idx0_1);
        w.WriteString("probeIdx0_3", p.Idx0_3);
        w.WriteString("probeIdx0_4", p.Idx0_4);
        w.WriteString("probeIdx1_1", p.Idx1_1);
        w.WriteString("probeIdx2_0", p.Idx2_0);
        w.WriteString("probeIdx2_1", p.Idx2_1);
        w.WriteString("probeAltHbData", p.AltHbData);
        w.WriteString("probeAltTestItem", p.AltTestItem);
    }
}

public sealed record DebuffDamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int DebuffId, int DataId, string AbilityKey, string AbilityName,
    AbilityProbe Probe,
    int Damage, double PainShare, long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "DebuffDamage";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteString("playerName", this.PlayerName);
        w.WriteNumber("charId", this.CharId);
        w.WriteNumber("enemyId", this.EnemyId);
        w.WriteNumber("debuffId", this.DebuffId);
        w.WriteNumber("dataId", this.DataId);
        w.WriteString("abilityKey", this.AbilityKey);
        w.WriteString("abilityName", this.AbilityName);
        DamageEvent.WriteProbeFields(w, this.Probe);
        w.WriteNumber("damage", this.Damage);
        w.WriteNumber("painShare", this.PainShare);
    }
}

public sealed record NewEnemyEvent(string EnemyKey, int EnemyId, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "NewEnemy";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteString("enemyKey", this.EnemyKey);
        w.WriteNumber("enemyId", this.EnemyId);
    }
}

public sealed record NewFightEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "NewFight";
    public override void WriteDataFields(Utf8JsonWriter w) { /* no payload fields yet */ }
}

public sealed record HallwayMoveEvent(int NotchPos, NotchType NotchType, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "HallwayMove";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("notchPos", this.NotchPos);
        w.WriteString("type", this.NotchType.ToString());
    }
}

public sealed record ChooseHallsEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "ChooseHalls";
    public override void WriteDataFields(Utf8JsonWriter w) { /* no payload fields yet */ }
}

public sealed record AddBuffEvent(
    int UniqueId, int BuffId, string BuffName,
    int SourceId, int TargetId, bool TargetsEnemy,
    int Duration, int Strength, int SourceHbId,
    long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "AddBuff";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("uniqueId", this.UniqueId);
        w.WriteNumber("buffId", this.BuffId);
        w.WriteString("buffName", this.BuffName);
        w.WriteNumber("sourceId", this.SourceId);
        w.WriteNumber("targetId", this.TargetId);
        w.WriteBoolean("targetsEnemy", this.TargetsEnemy);
        w.WriteNumber("duration", this.Duration);
        w.WriteNumber("strength", this.Strength);
        w.WriteNumber("sourceHbId", this.SourceHbId);
    }
}

public sealed record RemoveBuffEvent(int UniqueId, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "RemoveBuff";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("uniqueId", this.UniqueId);
    }
}

public sealed record EndFightEvent(bool Victory, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "EndFight";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteBoolean("victory", this.Victory);
    }
}
