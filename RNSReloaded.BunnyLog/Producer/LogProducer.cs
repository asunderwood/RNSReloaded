using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RNSReloaded.BunnyLog.Producer;

/// <summary>
/// Hooks GameMaker scripts and emits BunnyLog events. The script targets and field extractions
/// were originally established by DamageTracker; BunnyLog re-implements them as its own copy so
/// the two mods can evolve independently. All detours run on the game's main thread — keep work
/// inside them short; consumers should hand events off to background pipelines for real work.
/// </summary>
internal unsafe class LogProducer : ILogProducer {
    private readonly IRNSReloaded rns;
    private readonly ILoggerV1 logger;

    private readonly List<Action<BunnyLogEvent>> consumers = new();

    private IHook<ScriptDelegate> damageHook = null!;
    private IHook<ScriptDelegate> newFightHook = null!;
    private IHook<ScriptDelegate> addEnemyHook = null!;
    private IHook<ScriptDelegate> hallwayMoveHook = null!;
    private IHook<ScriptDelegate> chooseHallsHook = null!;
    private IHook<ScriptDelegate> triggerCallHook = null!;
    private IHook<ScriptDelegate> gameOverHook = null!;
    private IHook<ScriptDelegate> finishedFightHook = null!;

    public LogProducer(IRNSReloaded rns, IReloadedHooks hooks, ILoggerV1 logger) {
        this.rns = rns;
        this.logger = logger;

        this.damageHook = this.HookScript(hooks, "scr_pattern_deal_damage_enemy_subtract", this.EnemyDamageDetour);
        this.newFightHook = this.HookScript(hooks, "scrdt_encounter", this.NewFightDetour);
        this.addEnemyHook = this.HookScript(hooks, "scrdt_enemy", this.AddEnemyDetour);
        this.hallwayMoveHook = this.HookScript(hooks, "scr_hallwayprogress_move_next", this.HallwayMoveDetour);
        this.chooseHallsHook = this.HookScript(hooks, "scr_hallwayprogress_choose_halls", this.ChooseHallsDetour);
        this.triggerCallHook = this.HookScript(hooks, "scr_trigger_call", this.TriggerCallDetour);
        this.gameOverHook = this.HookScript(hooks, "scr_gamecontrol_do_gameover", this.GameOverDetour);
        this.finishedFightHook = this.HookScript(hooks, "scr_battlecontroller_end_round", this.FinishedFightDetour);
    }

    public void Subscribe(Action<BunnyLogEvent> consumer) {
        this.consumers.Add(consumer);
    }

    private IHook<ScriptDelegate> HookScript(IReloadedHooks hooks, string name, ScriptDelegate detour) {
        var script = this.rns.GetScriptData(this.rns.ScriptFindId(name) - 100000);
        var hook = hooks.CreateHook<ScriptDelegate>(detour, script->Functions->Function);
        hook.Activate();
        hook.Enable();
        return hook;
    }

    private void Emit(BunnyLogEvent ev) {
        // Single dispatch loop; consumers run synchronously on the game thread, so they MUST be
        // cheap (push to a queue, etc.) — see NetworkConsumer for the canonical pattern.
        for (var i = 0; i < this.consumers.Count; i++) {
            this.consumers[i].Invoke(ev);
        }
    }

    private long GameTime() =>
        this.rns.utils.RValueToLong(this.rns.FindValue(this.rns.GetGlobalInstance(), "gametime"));

    private RValue* EnemyDamageDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var hbId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "hbId"));
        var damage = this.rns.utils.RValueToLong(argv[2]);
        var playerId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "playerId"));
        var enemyId = this.rns.utils.RValueToLong(argv[1]);
        var painShare = this.rns.utils.RValueToDouble(
            this.rns.FindValue(this.rns.GetGlobalInstance(), "playerPainshareRatio")->Get(1)->Get(0)
        );
        var playerName = this.LookupPlayerName((int) playerId);
        var charId = this.LookupPlayerCharId((int) playerId);
        var ability = this.LookupAbility(self);
        var gameTime = this.GameTime();

        if (hbId != -1) {
            this.Emit(new DamageEvent(
                PlayerId: (int) playerId,
                PlayerName: playerName,
                CharId: charId,
                EnemyId: (int) enemyId,
                HbId: (int) hbId,
                DataId: ability.DataId,
                AbilityKey: ability.Key,
                AbilityName: ability.Name,
                Damage: (int) damage,
                PainShare: painShare,
                GameTime: gameTime
            ));
        } else {
            var debuffId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "statusId"));
            this.Emit(new DebuffDamageEvent(
                PlayerId: (int) playerId,
                PlayerName: playerName,
                CharId: charId,
                EnemyId: (int) enemyId,
                DebuffId: (int) debuffId,
                DataId: ability.DataId,
                AbilityKey: ability.Key,
                AbilityName: ability.Name,
                Damage: (int) damage,
                PainShare: painShare,
                GameTime: gameTime
            ));
        }

        return this.damageHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    // GameMaker stores all four players' display names and chosen character IDs in two parallel
    // 2D globals (DamageTracker's ImGuiConsumer uses the same indexing). Looking up on every hit
    // costs only a few pointer hops; not worth caching until profiling says otherwise.
    private string LookupPlayerName(int playerId) {
        try {
            return this.rns
                .FindValue(this.rns.GetGlobalInstance(), "playerName")
                ->Get(0)->Get(playerId)->ToString() ?? string.Empty;
        } catch {
            return string.Empty;
        }
    }

    private int LookupPlayerCharId(int playerId) {
        try {
            return (int) this.rns.utils.RValueToLong(
                this.rns.FindValue(this.rns.GetGlobalInstance(), "playerCharId")
                    ->Get(0)->Get(playerId)
            );
        } catch {
            return -1;
        }
    }

    // The ability/move instance carries a `dataId` that indexes into the global `itemData`
    // table. The probe in commit f174a76 confirmed the per-entry layout:
    //   itemData[dataId][0][0] -> internal key (e.g., "it_swift_boots", "mv_defender_2")
    //   itemData[dataId][0][1] -> small integer (tier? rarity?) — skipped for now
    //   itemData[dataId][0][2] -> display / pretty name (e.g., "Swift Boots")
    //   itemData[dataId][0][3] -> description text — skipped for now
    //   itemData[dataId][1][0] -> duplicate of the internal key
    // Reading [0][0] and [0][2] surfaces both the stable identifier and the friendly name.
    // Each lookup is wrapped independently because at least one ability in testing had an
    // empty pretty-name slot; we don't want that to also blank out the key.
    private (int DataId, string Key, string Name) LookupAbility(CInstance* self) {
        int dataId = 0;
        string key = string.Empty;
        string name = string.Empty;
        try {
            var dataIdValue = this.rns.FindValue(self, "dataId");
            if (dataIdValue == null) return (0, key, name);
            dataId = (int) this.rns.utils.RValueToLong(dataIdValue);

            var sub0 = this.rns
                .FindValue(this.rns.GetGlobalInstance(), "itemData")
                ->Get(dataId)->Get(0);

            try { key  = sub0->Get(0)->ToString() ?? string.Empty; } catch { }
            try { name = sub0->Get(2)->ToString() ?? string.Empty; } catch { }
        } catch { }
        return (dataId, key, name);
    }

    private RValue* NewFightDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        this.Emit(new NewFightEvent(this.GameTime()));
        return this.newFightHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* AddEnemyDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var enemyRealId = this.rns.utils.RValueToLong(argv[0]);
        // index 0 is the English key (e.g., "enc_bird_student0"); always ASCII.
        var enemyName = this.rns
            .FindValue(this.rns.GetGlobalInstance(), "enemyData")
            ->Get((int) enemyRealId)
            ->Get(0)
            ->ToString();
        var enemyListId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "playerId"));

        this.Emit(new NewEnemyEvent(enemyName ?? string.Empty, (int) enemyListId, this.GameTime()));
        return this.addEnemyHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* HallwayMoveDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var currentPos = this.rns.utils.RValueToLong(this.rns.FindValue(self, "currentPos")) + 1;
        var notchType = (NotchType) this.rns.utils.RValueToLong(
            this.rns.FindValue(self, "notches")->Get((int) currentPos)->Get(0)
        );

        this.Emit(new HallwayMoveEvent((int) currentPos, notchType, this.GameTime()));
        return this.hallwayMoveHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* ChooseHallsDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        this.Emit(new ChooseHallsEvent(this.GameTime()));
        return this.chooseHallsHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* GameOverDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        this.Emit(new EndFightEvent(Victory: false, GameTime: this.GameTime()));
        return this.gameOverHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* FinishedFightDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        this.Emit(new EndFightEvent(Victory: true, GameTime: this.GameTime()));
        return this.finishedFightHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private const long HBS_CREATED = 33;
    private const long HBS_DESTROYED = 36;

    private RValue* TriggerCallDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var triggerType = argc > 0 ? this.rns.utils.RValueToLong(argv[0]) : 0;

        if (triggerType == HBS_CREATED) {
            // teamId == 0 means a player-applied buff (we want it). teamId == 1 means an enemy
            // applied something; for now we mirror DamageTracker and skip those.
            var teamId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "teamId"));
            if (teamId == 0) {
                var statusId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "statusId"));
                var name = this.rns
                    .FindValue(this.rns.GetGlobalInstance(), "hbsInfo")
                    ->Get((int) statusId)
                    ->Get(0)
                    ->ToString();

                this.Emit(new AddBuffEvent(
                    UniqueId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "hbsUniqueId")),
                    BuffId: (int) statusId,
                    BuffName: name ?? string.Empty,
                    SourceId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "playerId")),
                    TargetId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "aflPlayerId")),
                    TargetsEnemy: this.rns.utils.RValueToLong(this.rns.FindValue(self, "aflTeamId")) == 1,
                    Duration: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "initLength")),
                    Strength: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "strength")),
                    SourceHbId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "originHbId")),
                    GameTime: this.GameTime()
                ));
            }
        } else if (triggerType == HBS_DESTROYED) {
            var teamId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "teamId"));
            if (teamId == 0) {
                this.Emit(new RemoveBuffEvent(
                    UniqueId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "hbsUniqueId")),
                    GameTime: this.GameTime()
                ));
            }
        }

        return this.triggerCallHook.OriginalFunction(self, other, returnValue, argc, argv);
    }
}
