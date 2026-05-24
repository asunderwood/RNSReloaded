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
/// Probe shipped alongside Damage/DebuffDamage in round 4. Carries (a) trigger-condition data
/// from itemData[dataId][2][0..1] that we haven't fully characterized yet, and (b) deeper
/// itemData index probes ([0][5..7], [1][2..3], [2][2..3], [3][0..1]) in case per-move sprite
/// information lives further into the record. See sprite-correlation note in the round-4 plan.
/// </summary>
public sealed record AbilityProbe(
    string Idx2_0, string Idx2_1,
    string Idx0_5, string Idx0_6, string Idx0_7,
    string Idx1_2, string Idx1_3,
    string Idx2_2, string Idx2_3,
    string Idx3_0, string Idx3_1
);

public sealed record DamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int HbId, int DataId,
    string AbilityKey, string AbilityName, string SpriteRef,
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
        w.WriteString("spriteRef", this.SpriteRef);
        WriteProbeFields(w, this.Probe);
        w.WriteNumber("damage", this.Damage);
        w.WriteNumber("painShare", this.PainShare);
    }

    internal static void WriteProbeFields(Utf8JsonWriter w, AbilityProbe p) {
        w.WriteString("probeIdx2_0", p.Idx2_0);
        w.WriteString("probeIdx2_1", p.Idx2_1);
        w.WriteString("probeIdx0_5", p.Idx0_5);
        w.WriteString("probeIdx0_6", p.Idx0_6);
        w.WriteString("probeIdx0_7", p.Idx0_7);
        w.WriteString("probeIdx1_2", p.Idx1_2);
        w.WriteString("probeIdx1_3", p.Idx1_3);
        w.WriteString("probeIdx2_2", p.Idx2_2);
        w.WriteString("probeIdx2_3", p.Idx2_3);
        w.WriteString("probeIdx3_0", p.Idx3_0);
        w.WriteString("probeIdx3_1", p.Idx3_1);
    }
}

public sealed record DebuffDamageEvent(
    int PlayerId, string PlayerName, int CharId,
    int EnemyId, int DebuffId, string DebuffName,
    string HbsInfo1, string HbsInfo2, string HbsInfo3,
    int Damage, double PainShare, long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "DebuffDamage";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteString("playerName", this.PlayerName);
        w.WriteNumber("charId", this.CharId);
        w.WriteNumber("enemyId", this.EnemyId);
        w.WriteNumber("debuffId", this.DebuffId);
        w.WriteString("debuffName", this.DebuffName);
        // Round-4.5: hbsInfo[statusId][0] is the internal key (e.g. "hbs_poison_0"). Probing
        // siblings to find which holds the display / pretty name. Promote next round.
        w.WriteString("probeHbsInfo1", this.HbsInfo1);
        w.WriteString("probeHbsInfo2", this.HbsInfo2);
        w.WriteString("probeHbsInfo3", this.HbsInfo3);
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

public sealed record NewFightEvent(string EncounterKey, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "NewFight";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteString("encounterKey", this.EncounterKey);
    }
}

/// <summary>
/// Layer-B probe attached to HallwayMoveEvent. Goal: find the field that tells us the current
/// stage's location (geode/aurum/depths/sanct/darkhall/...) directly, instead of inferring it
/// from the first encounter's key prefix. self.hallkey[currentPos] cycles per-position so it's
/// NOT the answer (round 3 finding). Plus we keep notch[3] retained from round 3 since its
/// 0/16/32/64 distribution still isn't characterized.
/// </summary>
public sealed record HallwayMoveStageProbe(
    string Notch_3,
    string SelfCurrentStage, string SelfCurrentHall, string SelfCurrentLocation,
    string SelfStage, string SelfHall, string SelfLocation, string SelfZone,
    string SelfStageHall, string SelfStageLoc, string SelfStageNum,
    string SelfStageIndex, string SelfStageKey, string SelfCurrentHallKey,
    string GlobCurrentStage, string GlobCurrentLocation, string GlobCurrentHall,
    string GlobRunStage, string GlobCurrentZone, string GlobStageLoc, string GlobCurrentLoc
);

public sealed record HallwayMoveEvent(
    int NotchPos, NotchType NotchType,
    string NextEncounterKey, string NotchSeed,
    HallwayMoveStageProbe StageProbe,
    long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "HallwayMove";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("notchPos", this.NotchPos);
        w.WriteString("type", this.NotchType.ToString());
        w.WriteString("nextEncounterKey", this.NextEncounterKey);
        w.WriteString("notchSeed", this.NotchSeed);
        var p = this.StageProbe;
        w.WriteString("probeNotch_3", p.Notch_3);
        w.WriteString("probeSelfCurrentStage", p.SelfCurrentStage);
        w.WriteString("probeSelfCurrentHall", p.SelfCurrentHall);
        w.WriteString("probeSelfCurrentLocation", p.SelfCurrentLocation);
        w.WriteString("probeSelfStage", p.SelfStage);
        w.WriteString("probeSelfHall", p.SelfHall);
        w.WriteString("probeSelfLocation", p.SelfLocation);
        w.WriteString("probeSelfZone", p.SelfZone);
        w.WriteString("probeSelfStageHall", p.SelfStageHall);
        w.WriteString("probeSelfStageLoc", p.SelfStageLoc);
        w.WriteString("probeSelfStageNum", p.SelfStageNum);
        w.WriteString("probeSelfStageIndex", p.SelfStageIndex);
        w.WriteString("probeSelfStageKey", p.SelfStageKey);
        w.WriteString("probeSelfCurrentHallKey", p.SelfCurrentHallKey);
        w.WriteString("probeGlobCurrentStage", p.GlobCurrentStage);
        w.WriteString("probeGlobCurrentLocation", p.GlobCurrentLocation);
        w.WriteString("probeGlobCurrentHall", p.GlobCurrentHall);
        w.WriteString("probeGlobRunStage", p.GlobRunStage);
        w.WriteString("probeGlobCurrentZone", p.GlobCurrentZone);
        w.WriteString("probeGlobStageLoc", p.GlobStageLoc);
        w.WriteString("probeGlobCurrentLoc", p.GlobCurrentLoc);
    }
}

/// <summary>
/// Layer-B probe attached to ChooseHallsEvent. The detour now calls OriginalFunction FIRST and
/// then probes self/argv/globals, so we see the populated state. Round 3's entry-side probes
/// were uniformly empty because the script populates state inside its body. The goal is to find
/// the 5-element location-order array that's decided at run start.
/// </summary>
public sealed record ChooseHallsProbe(
    // Array-shaped self reads, each shipped as a 5-tuple (Get(0)..Get(4)).
    string[] Stages, string[] Halls, string[] RunHalls,
    string[] StageHalls, string[] StageOrder, string[] LocationOrder,
    string[] Path, string[] Locations, string[] RunStages,
    string[] ChosenHalls, string[] SelectedHalls,
    // Scalar self reads.
    string SelfCurrentStage, string SelfStage, string SelfHall, string SelfLocation, string SelfZone,
    // argv.
    int Argc, string Argv0, string Argv1, string Argv2, string Argv3, string Argv4,
    // Globals.
    string GlobCurrentStage, string GlobCurrentLocation,
    string GlobRunStage, string GlobRunLocation, string GlobSelectedHall
);

public sealed record ChooseHallsEvent(ChooseHallsProbe Probe, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "ChooseHalls";
    public override void WriteDataFields(Utf8JsonWriter w) {
        var p = this.Probe;
        WriteStringArray(w, "probeStages", p.Stages);
        WriteStringArray(w, "probeHalls", p.Halls);
        WriteStringArray(w, "probeRunHalls", p.RunHalls);
        WriteStringArray(w, "probeStageHalls", p.StageHalls);
        WriteStringArray(w, "probeStageOrder", p.StageOrder);
        WriteStringArray(w, "probeLocationOrder", p.LocationOrder);
        WriteStringArray(w, "probePath", p.Path);
        WriteStringArray(w, "probeLocations", p.Locations);
        WriteStringArray(w, "probeRunStages", p.RunStages);
        WriteStringArray(w, "probeChosenHalls", p.ChosenHalls);
        WriteStringArray(w, "probeSelectedHalls", p.SelectedHalls);
        w.WriteString("probeSelfCurrentStage", p.SelfCurrentStage);
        w.WriteString("probeSelfStage", p.SelfStage);
        w.WriteString("probeSelfHall", p.SelfHall);
        w.WriteString("probeSelfLocation", p.SelfLocation);
        w.WriteString("probeSelfZone", p.SelfZone);
        w.WriteNumber("probeArgc", p.Argc);
        w.WriteString("probeArgv0", p.Argv0);
        w.WriteString("probeArgv1", p.Argv1);
        w.WriteString("probeArgv2", p.Argv2);
        w.WriteString("probeArgv3", p.Argv3);
        w.WriteString("probeArgv4", p.Argv4);
        w.WriteString("probeGlobCurrentStage", p.GlobCurrentStage);
        w.WriteString("probeGlobCurrentLocation", p.GlobCurrentLocation);
        w.WriteString("probeGlobRunStage", p.GlobRunStage);
        w.WriteString("probeGlobRunLocation", p.GlobRunLocation);
        w.WriteString("probeGlobSelectedHall", p.GlobSelectedHall);
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, string[] arr) {
        w.WriteStartArray(name);
        for (var i = 0; i < arr.Length; i++) {
            w.WriteStringValue(arr[i]);
        }
        w.WriteEndArray();
    }
}

public sealed record AddBuffEvent(
    int UniqueId, int BuffId, string BuffName,
    string HbsInfo1, string HbsInfo2, string HbsInfo3,
    int SourceId, int TargetId, bool TargetsEnemy,
    int Duration, int Strength, int SourceHbId,
    long GameTime
) : BunnyLogEvent(GameTime) {
    public override string EventName => "AddBuff";
    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("uniqueId", this.UniqueId);
        w.WriteNumber("buffId", this.BuffId);
        w.WriteString("buffName", this.BuffName);
        // Round-4.5: same probe as DebuffDamage. hbsInfo[0] is internal key; finding pretty name.
        w.WriteString("probeHbsInfo1", this.HbsInfo1);
        w.WriteString("probeHbsInfo2", this.HbsInfo2);
        w.WriteString("probeHbsInfo3", this.HbsInfo3);
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
/// Survey of scr_player_invuln invocations. argv0 promoted to DurationMs after round 3 confirmed
/// it's a duration in milliseconds; other argv values stay as probes while we characterize them
/// across multiplayer runs.
/// </summary>
public sealed record PlayerInvulnEvent(int DurationMs, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "PlayerInvuln";
    public int PlayerId { get; init; }
    public int SelfId { get; init; }
    public int Argc { get; init; }
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("durationMs", this.DurationMs);
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
    }
}

/// <summary>
/// Round-3 PlayerHit data was uninterpretable (argv constant, self.playerId showed impossible
/// value 5, self.hp/maxHp empty). Round 4 expands the probe surface: argv up to 6, plus many
/// self field candidates so we can figure out whether this script fires on actual hits or
/// pre-hit checks.
/// </summary>
public sealed record PlayerHitEvent(long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "PlayerHit";
    public int PlayerId { get; init; }
    public int SelfId { get; init; }
    public int Argc { get; init; }
    public string Argv0 { get; init; } = "";
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public string Argv4 { get; init; } = "";
    public string Argv5 { get; init; } = "";
    public string Argv6 { get; init; } = "";
    public string SelfDataId { get; init; } = "";
    public string SelfStatusId { get; init; } = "";
    public string SelfTargetPlayerId { get; init; } = "";
    public string SelfAttackerId { get; init; } = "";
    public string SelfBp { get; init; } = "";
    public string SelfScript { get; init; } = "";
    public string SelfBpName { get; init; } = "";
    public string SelfActionScript { get; init; } = "";
    public string SelfDmg { get; init; } = "";
    public string SelfDamage { get; init; } = "";
    public string SelfTeamId { get; init; } = "";
    public string SelfAflPlayerId { get; init; } = "";
    public string SelfAflTeamId { get; init; } = "";
    public string SelfPainshare { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv0", this.Argv0);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteString("argv4", this.Argv4);
        w.WriteString("argv5", this.Argv5);
        w.WriteString("argv6", this.Argv6);
        w.WriteString("selfDataId", this.SelfDataId);
        w.WriteString("selfStatusId", this.SelfStatusId);
        w.WriteString("selfTargetPlayerId", this.SelfTargetPlayerId);
        w.WriteString("selfAttackerId", this.SelfAttackerId);
        w.WriteString("selfBp", this.SelfBp);
        w.WriteString("selfScript", this.SelfScript);
        w.WriteString("selfBpName", this.SelfBpName);
        w.WriteString("selfActionScript", this.SelfActionScript);
        w.WriteString("selfDmg", this.SelfDmg);
        w.WriteString("selfDamage", this.SelfDamage);
        w.WriteString("selfTeamId", this.SelfTeamId);
        w.WriteString("selfAflPlayerId", this.SelfAflPlayerId);
        w.WriteString("selfAflTeamId", this.SelfAflTeamId);
        w.WriteString("selfPainshare", this.SelfPainshare);
    }
}

/// <summary>
/// Survey of scr_rankbar_give_rewards. Round-3 confirmed argv0=playerId, argv1=tier (range 7-13
/// observed), argv2=score (range 72-128 observed). Working names — promote / rename once we
/// know which is gold vs EXP vs rank-letter.
/// </summary>
public sealed record RewardEvent(int PlayerId, int Tier, int Score, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "Reward";
    public int SelfId { get; init; }
    public int Argc { get; init; }

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("playerId", this.PlayerId);
        w.WriteNumber("tier", this.Tier);
        w.WriteNumber("score", this.Score);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("argc", this.Argc);
    }
}

/// <summary>
/// Round-4.5 follow-up: `scr_trigger_call` is a known dispatcher (argv[0] is the trigger type;
/// 33=HBS_CREATED, 36=HBS_DESTROYED for buffs). Other trigger types fire too — emit this event
/// for any non-buff type so we can see what the rest of the trigger system handles (possibly
/// chest pickups, shop purchases, scripted events).
/// </summary>
public sealed record TriggerProbeEvent(int TriggerType, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "TriggerProbe";
    public int Argc { get; init; }
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public int SelfId { get; init; }
    public int SelfPlayerId { get; init; }
    public string SelfStatusId { get; init; } = "";
    public int SelfTeamId { get; init; }
    public string SelfHbsUniqueId { get; init; } = "";

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteNumber("triggerType", this.TriggerType);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("selfPlayerId", this.SelfPlayerId);
        w.WriteString("selfStatusId", this.SelfStatusId);
        w.WriteNumber("selfTeamId", this.SelfTeamId);
        w.WriteString("selfHbsUniqueId", this.SelfHbsUniqueId);
    }
}

/// <summary>
/// Layer-C exploration vehicle: one event type emitted by every try-hooked candidate script
/// (loot/shop/item-grant/roll candidates). The ScriptName field identifies which script fired;
/// argv + self fields are shipped raw. Once we identify which candidates fire usefully, we'll
/// promote the winners to typed events on their own.
/// </summary>
public sealed record ScriptProbeEvent(string ScriptName, long GameTime) : BunnyLogEvent(GameTime) {
    public override string EventName => "ScriptProbe";
    public int Argc { get; init; }
    public string Argv0 { get; init; } = "";
    public string Argv1 { get; init; } = "";
    public string Argv2 { get; init; } = "";
    public string Argv3 { get; init; } = "";
    public string Argv4 { get; init; } = "";
    public string Argv5 { get; init; } = "";
    public string Argv6 { get; init; } = "";
    public int SelfId { get; init; }
    public int SelfPlayerId { get; init; }
    public int SelfDataId { get; init; }
    public int SelfHb { get; init; }

    public override void WriteDataFields(Utf8JsonWriter w) {
        w.WriteString("scriptName", this.ScriptName);
        w.WriteNumber("argc", this.Argc);
        w.WriteString("argv0", this.Argv0);
        w.WriteString("argv1", this.Argv1);
        w.WriteString("argv2", this.Argv2);
        w.WriteString("argv3", this.Argv3);
        w.WriteString("argv4", this.Argv4);
        w.WriteString("argv5", this.Argv5);
        w.WriteString("argv6", this.Argv6);
        w.WriteNumber("selfId", this.SelfId);
        w.WriteNumber("selfPlayerId", this.SelfPlayerId);
        w.WriteNumber("selfDataId", this.SelfDataId);
        w.WriteNumber("selfHb", this.SelfHb);
    }
}
