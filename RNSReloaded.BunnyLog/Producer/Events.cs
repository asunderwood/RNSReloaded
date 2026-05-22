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

/// <summary>
/// Probe attached to NewFightEvent during the variant-investigation round. Each field is a
/// candidate source for the bp variant identifier (Rem0 vs Rem1 vs Rem2 etc.). Empty strings
/// mean the lookup didn't resolve.
/// </summary>
public sealed record NewFightProbe(
    int SelfId, int DataId, int ActionScript,
    string SelfBpName, string SelfScript, string SelfPattern,
    string SelfEncKey, string SelfBp, string SelfPatternScript,
    int Argc, string Argv0, string Argv1, string Argv2
);

public sealed record NewFightEvent(NewFightProbe Probe, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "NewFight";
    public override void WriteDataFields(Utf8JsonWriter w) {
        var p = this.Probe;
        w.WriteNumber("probeSelfId", p.SelfId);
        w.WriteNumber("probeDataId", p.DataId);
        w.WriteNumber("probeActionScript", p.ActionScript);
        w.WriteString("probeSelfBpName", p.SelfBpName);
        w.WriteString("probeSelfScript", p.SelfScript);
        w.WriteString("probeSelfPattern", p.SelfPattern);
        w.WriteString("probeSelfEncKey", p.SelfEncKey);
        w.WriteString("probeSelfBp", p.SelfBp);
        w.WriteString("probeSelfPatternScript", p.SelfPatternScript);
        w.WriteNumber("probeArgc", p.Argc);
        w.WriteString("probeArgv0", p.Argv0);
        w.WriteString("probeArgv1", p.Argv1);
        w.WriteString("probeArgv2", p.Argv2);
    }
}

/// <summary>
/// Probe attached to HallwayMoveEvent during the stage-identifier investigation. Goal: find
/// which field tells us the stage (geode/keep/lakeside/lighthouse/nest/outskirts/pinnacle/
/// streets/toybox/arsenal) and whether more notch-record fields beyond [0] (the type) are useful.
/// </summary>
public sealed record HallwayMoveProbe(
    int SelfId,
    string HallKeyAtPos, string GlobalCurrentHall, string GlobalCurrentStage, string GlobalStageId,
    string Notch_1, string Notch_2, string Notch_3
);

public sealed record HallwayMoveEvent(int NotchPos, NotchType NotchType, HallwayMoveProbe Probe, long GameTime)
    : BunnyLogEvent(GameTime) {
    public override string EventName => "HallwayMove";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("notchPos", this.NotchPos);
        w.WriteString("type", this.NotchType.ToString());
        var p = this.Probe;
        w.WriteNumber("probeSelfId", p.SelfId);
        w.WriteString("probeHallKeyAtPos", p.HallKeyAtPos);
        w.WriteString("probeGlobalCurrentHall", p.GlobalCurrentHall);
        w.WriteString("probeGlobalCurrentStage", p.GlobalCurrentStage);
        w.WriteString("probeGlobalStageId", p.GlobalStageId);
        w.WriteString("probeNotch_1", p.Notch_1);
        w.WriteString("probeNotch_2", p.Notch_2);
        w.WriteString("probeNotch_3", p.Notch_3);
    }
}

/// <summary>
/// Probe attached to ChooseHallsEvent — the hall-key array is set during scr_hallwayprogress_choose_halls
/// so this is a natural place to capture the three chosen halls for the upcoming stage.
/// </summary>
public sealed record ChooseHallsProbe(
    int SelfId,
    string HallKey0, string HallKey1, string HallKey2,
    string GlobalCurrentHall, string GlobalCurrentStage
);

public sealed record ChooseHallsEvent(ChooseHallsProbe Probe, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "ChooseHalls";
    public override void WriteDataFields(Utf8JsonWriter w) {
        var p = this.Probe;
        w.WriteNumber("probeSelfId", p.SelfId);
        w.WriteString("probeHallKey0", p.HallKey0);
        w.WriteString("probeHallKey1", p.HallKey1);
        w.WriteString("probeHallKey2", p.HallKey2);
        w.WriteString("probeGlobalCurrentHall", p.GlobalCurrentHall);
        w.WriteString("probeGlobalCurrentStage", p.GlobalCurrentStage);
    }
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

/// <summary>
/// Raw survey of scr_player_invuln invocations. Invuln in this game is a common state caused by
/// many things (revive iframes, item effects, ability iframes); the goal here is to observe
/// values and decide later which patterns map to which game events.
/// </summary>
public sealed record PlayerInvulnEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "PlayerInvuln";
    public int PlayerId { get; init; }
    public int SelfId { get; init; }
    public int Argc { get; init; }
    public string Argv0 { get; init; } = "";
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public string SelfHp { get; init; } = "";
    public string SelfMaxHp { get; init; } = "";
    public string SelfInvuln { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv0", this.Argv0);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteString("selfHp", this.SelfHp);
        w.WriteString("selfMaxHp", this.SelfMaxHp);
        w.WriteString("selfInvuln", this.SelfInvuln);
    }
}

/// <summary>Raw survey of scr_pattern_deal_damage_ally — damage targeting a player.</summary>
public sealed record PlayerHitEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "PlayerHit";
    public int PlayerId { get; init; }
    public int SelfId { get; init; }
    public int Argc { get; init; }
    public string Argv0 { get; init; } = "";
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public string SelfHp { get; init; } = "";
    public string SelfMaxHp { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv0", this.Argv0);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteString("selfHp", this.SelfHp);
        w.WriteString("selfMaxHp", this.SelfMaxHp);
    }
}

/// <summary>Raw survey of scr_rankbar_give_rewards — observe when this fires and with what.</summary>
public sealed record RewardEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "Reward";
    public int SelfId { get; init; }
    public int Argc { get; init; }
    public string Argv0 { get; init; } = "";
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public string Argv4 { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv0", this.Argv0);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteString("argv4", this.Argv4);
    }
}
