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

public sealed record DamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int HbId, int DataId, string AbilityName,
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
        w.WriteString("abilityName", this.AbilityName);
        w.WriteNumber("damage", this.Damage);
        w.WriteNumber("painShare", this.PainShare);
    }
}

public sealed record DebuffDamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int DebuffId, int DataId, string AbilityName,
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
        w.WriteString("abilityName", this.AbilityName);
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
