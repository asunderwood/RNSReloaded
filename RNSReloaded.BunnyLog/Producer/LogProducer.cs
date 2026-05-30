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
    private IHook<ScriptDelegate> stageChangeHook = null!;
    private IHook<ScriptDelegate> triggerCallHook = null!;
    private IHook<ScriptDelegate> gameOverHook = null!;
    private IHook<ScriptDelegate> finishedFightHook = null!;
    private IHook<ScriptDelegate> itemPickupHook = null!;
    private IHook<ScriptDelegate> itemCreateHook = null!;
    // Debug-branch survey hooks. Their events ship raw argv + a few self field reads without
    // interpretation — meaning gets applied later by reviewing the log-mirror file.
    private IHook<ScriptDelegate> playerInvulnHook = null!;
    private IHook<ScriptDelegate> playerHitHook = null!;
    private IHook<ScriptDelegate> rewardHook = null!;
    // Layer C: tolerant try-hooks. Each entry is a (name, hook) pair; the hook is null if the
    // script didn't exist in the loaded game version. We keep references alive for the lifetime
    // of the mod so the hooks stay active.
    private readonly List<(string Name, IHook<ScriptDelegate>? Hook)> probeHooks = new();
    // Strong refs to the per-script detour delegates so they aren't GC'd. C# closures referenced
    // only by native code can otherwise be collected.
    private readonly List<ScriptDelegate> probeDelegates = new();
    // Item populate / store-setup hooks — names from the user's full script dump. Each fires a
    // typed ItemPopulateEvent post-call. Tolerant: a missing name is simply not added. Kept in a
    // list so references stay alive for the mod's lifetime.
    private readonly List<IHook<ScriptDelegate>> populateHooks = new();

    // (name, source) candidates for the item populate / store-setup hooks. source follows the
    // pickup convention: 1 = loot/chest, 2 = store/shop. The plain populate_store/_loot are
    // seed-driven generators that delegate the actual item-writing — the _network_* variants and
    // refresh_storebin are the suspected real writers. "storebin" is the structure name lead.
    private static readonly (string Name, int Source)[] PopulateCandidates = {
        // Seed-only generators (kept for timing / seed correlation; expose nothing via self/ret).
        ("scr_itemsys_populate_loot", 1),
        ("scr_itemsys_populate_store", 2),
        ("scr_itemsys_refresh_storebin", 2),
        ("scr_itemsys_populate_loot_end", 1),   // fires after loot fill — table may be reachable
        // ITEM FACTORY — scr_itemsys_create_item is now a first-class typed ItemCreate hook
        // (argv0=dataId, argv1=playerId, argv2=category, ret=instanceId). Kept here for cross-check
        // and slot-binding discovery:
        ("scr_itemsys_create_item_by_key", 0),  // confirms dataId<->key (argv0 is the literal key)
        ("scr_itemsys_place_item_in_row", 0),   // argv0=instanceId, argv1=row/section — slot binding
        // Loot-specific pickup ("helditem" = the held item), distinct from scr_itemsys_pickup.
        ("scr_itemsys_helditem_pickup_loot", 1),
        // NOTE: scrc_h_* / scr_lobbyhost_* populate scripts exist but are host/networking paths —
        // confirmed to NOT fire in single-player (they hooked cleanly, never executed). Dropped.
    };

    // Single-string-array allocation reused for empty 5-tuple defaults to avoid allocations in
    // the ChooseHalls detour. ChooseHalls only fires once per run so this is a micro-optimization
    // but keeps the detour clean.
    private static readonly string[] EmptyFive = { "", "", "", "", "" };

    // Per-(non-buff) triggerType sample counts. scr_trigger_call is a hot dispatcher; emitting a
    // TriggerProbeEvent on every non-buff invocation flooded the log. We cap at N samples per
    // distinct trigger type so we still get the type inventory + a handful of argv snapshots.
    private const int MaxTriggerProbeSamplesPerType = 5;
    private readonly Dictionary<long, int> triggerProbeSampleCount = new();

    // Per-script-name sample cap on ScriptProbeEvent. Belt-and-braces protection in case one of
    // the surviving try-hook candidates (e.g. scr_itemsys_pickup) turns out to be a per-frame
    // check rather than a discrete event. 200 is enough to see ~30 area transitions worth of
    // activity per script before silencing — refine if a particular script needs more samples.
    private const int MaxScriptProbeSamplesPerName = 200;
    private readonly Dictionary<string, int> scriptProbeSampleCount = new();

    // Per-script cap on ItemPopulate emissions. The factory scripts (create_item*) fire in bursts
    // (per item created) rather than per-frame, so the flood risk is bounded, but a generous cap
    // still guards against an unexpected draw-loop caller. 120 comfortably covers a focused run's
    // run-start loadout plus a chest (5) and shop (6) without truncating the items we care about.
    private const int MaxItemPopulateSamplesPerScript = 120;
    private readonly Dictionary<string, int> itemPopulateSampleCount = new();

    public LogProducer(IRNSReloaded rns, IReloadedHooks hooks, ILoggerV1 logger) {
        this.rns = rns;
        this.logger = logger;

        this.damageHook = this.HookScript(hooks, "scr_pattern_deal_damage_enemy_subtract", this.EnemyDamageDetour);
        this.newFightHook = this.HookScript(hooks, "scrdt_encounter", this.NewFightDetour);
        this.addEnemyHook = this.HookScript(hooks, "scrdt_enemy", this.AddEnemyDetour);
        this.hallwayMoveHook = this.HookScript(hooks, "scr_hallwayprogress_move_next", this.HallwayMoveDetour);
        this.chooseHallsHook = this.HookScript(hooks, "scr_hallwayprogress_choose_halls", this.ChooseHallsDetour);
        this.stageChangeHook = this.HookScript(hooks, "scr_stage_change", this.StageChangeDetour);
        this.triggerCallHook = this.HookScript(hooks, "scr_trigger_call", this.TriggerCallDetour);
        this.gameOverHook = this.HookScript(hooks, "scr_gamecontrol_do_gameover", this.GameOverDetour);
        this.finishedFightHook = this.HookScript(hooks, "scr_battlecontroller_end_round", this.FinishedFightDetour);
        this.playerInvulnHook = this.HookScript(hooks, "scr_player_invuln", this.PlayerInvulnDetour);
        this.playerHitHook = this.HookScript(hooks, "scr_pattern_deal_damage_ally", this.PlayerHitDetour);
        this.rewardHook = this.HookScript(hooks, "scr_rankbar_give_rewards", this.RewardDetour);
        this.itemPickupHook = this.HookScript(hooks, "scr_itemsys_pickup", this.ItemPickupDetour);
        this.itemCreateHook = this.HookScript(hooks, "scr_itemsys_create_item", this.ItemCreateDetour);

        // Chest/shop SETUP hooks (user-supplied names). Tolerant — log which ones exist.
        var hookedPopulate = new List<string>();
        foreach (var (name, src) in PopulateCandidates) {
            var hook = this.TryHookItemPopulate(hooks, name, src);
            if (hook is not null) {
                this.populateHooks.Add(hook);
                hookedPopulate.Add(name);
            }
        }
        this.logger.PrintMessage(
            $"BunnyLog: hooked {hookedPopulate.Count} of {PopulateCandidates.Length} populate/store-setup scripts" +
            (hookedPopulate.Count > 0 ? ": " + string.Join(", ", hookedPopulate) : ""),
            this.logger.ColorYellow
        );

        this.AttachProbeHooks(hooks);
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
        var gameTime = this.GameTime();

        if (hbId != -1) {
            // Normal hit damage: look up the move/item from itemData, ship key + pretty name + sprite category.
            var ability = this.LookupAbility(self);
            var probe = this.ProbeAbilityDeep(self, ability.DataId);
            this.Emit(new DamageEvent(
                PlayerId: (int) playerId,
                PlayerName: playerName,
                CharId: charId,
                EnemyId: (int) enemyId,
                HbId: (int) hbId,
                DataId: ability.DataId,
                AbilityKey: ability.Key,
                AbilityName: ability.Name,
                SpriteRef: ability.SpriteRef,
                Probe: probe,
                Damage: (int) damage,
                PainShare: painShare,
                GameTime: gameTime
            ));
        } else {
            // Debuff / status tick. self.statusId indexes into hbsInfo (NOT itemData). Layout
            // confirmed by the [1..3] probe: [0]=variant key, [1]=base key, [2]=pretty name,
            // [3]=description. Ship [0] as debuffKey and [2] as debuffName.
            var debuffId = this.rns.utils.RValueToLong(this.rns.FindValue(self, "statusId"));
            var (debuffKey, debuffName) = this.LookupHbsInfo((int) debuffId);
            this.Emit(new DebuffDamageEvent(
                PlayerId: (int) playerId,
                PlayerName: playerName,
                CharId: charId,
                EnemyId: (int) enemyId,
                DebuffId: (int) debuffId,
                DebuffKey: debuffKey,
                DebuffName: debuffName,
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

    /// <summary>
    /// Reads hbsInfo for a given statusId. Layout confirmed by the [1..3] probe pass:
    ///   [0] = variant-specific internal key (e.g. "hbs_poison_0"), returned as Key.
    ///   [1] = base internal key (e.g. "hbs_poison"), not surfaced.
    ///   [2] = pretty display name (e.g. "Poison"), returned as Name.
    ///   [3] = description text, not surfaced.
    /// Both reads are independently try/catch'd so a missing slot doesn't blank the other.
    /// </summary>
    private (string Key, string Name) LookupHbsInfo(int statusId) {
        string key = string.Empty, name = string.Empty;
        try {
            var entry = this.rns
                .FindValue(this.rns.GetGlobalInstance(), "hbsInfo")
                ->Get(statusId);
            try { key  = entry->Get(0)->ToString() ?? string.Empty; } catch { }
            try { name = entry->Get(2)->ToString() ?? string.Empty; } catch { }
        } catch { }
        return (key, name);
    }

    // itemData record layout confirmed by previous probe rounds:
    //   itemData[dataId][0][0] -> internal key (e.g., "it_swift_boots", "mv_defender_2")
    //   itemData[dataId][0][1] -> small integer (tier/rarity)
    //   itemData[dataId][0][2] -> display / pretty name (e.g., "Swift Boots")
    //   itemData[dataId][0][3] -> description text
    //   itemData[dataId][0][4] -> sprite category reference (e.g., "ref sprite spr_potion_normal")
    //                              — same value for every move of a class, so class/category only.
    //   itemData[dataId][1][0] -> duplicate of the internal key
    // Round 4 also probes [0][5..7], [1][2..3], [2][2..3], [3][0..1] looking for per-move data.
    private (int DataId, string Key, string Name, string SpriteRef) LookupAbility(CInstance* self) {
        int dataId = 0;
        string key = string.Empty;
        string name = string.Empty;
        string spriteRef = string.Empty;
        try {
            var dataIdValue = this.rns.FindValue(self, "dataId");
            if (dataIdValue == null) return (0, key, name, spriteRef);
            dataId = (int) this.rns.utils.RValueToLong(dataIdValue);

            var sub0 = this.rns
                .FindValue(this.rns.GetGlobalInstance(), "itemData")
                ->Get(dataId)->Get(0);

            try { key       = sub0->Get(0)->ToString() ?? string.Empty; } catch { }
            try { name      = sub0->Get(2)->ToString() ?? string.Empty; } catch { }
            try { spriteRef = sub0->Get(4)->ToString() ?? string.Empty; } catch { }
        } catch { }
        return (dataId, key, name, spriteRef);
    }

    // Layer-A probe for trigger-condition data [2][0..1] (retained because partial values aren't
    // characterized) plus Layer-A deeper-index probes for sprite-correlation (no clear hit on
    // per-move data yet; one more wide sweep). Each lookup is independently try-catch'd so a bad
    // index for one slot doesn't blank the others.
    private AbilityProbe ProbeAbilityDeep(CInstance* self, int dataId) {
        string idx2_0 = string.Empty, idx2_1 = string.Empty;
        string idx0_5 = string.Empty, idx0_6 = string.Empty, idx0_7 = string.Empty;
        string idx1_2 = string.Empty, idx1_3 = string.Empty;
        string idx2_2 = string.Empty, idx2_3 = string.Empty;
        string idx3_0 = string.Empty, idx3_1 = string.Empty;

        if (dataId > 0) {
            try {
                var item = this.rns
                    .FindValue(this.rns.GetGlobalInstance(), "itemData")
                    ->Get(dataId);
                var sub0 = item->Get(0);
                try { idx2_0 = item->Get(2)->Get(0)->ToString() ?? string.Empty; } catch { }
                try { idx2_1 = item->Get(2)->Get(1)->ToString() ?? string.Empty; } catch { }
                try { idx0_5 = sub0->Get(5)->ToString() ?? string.Empty; } catch { }
                try { idx0_6 = sub0->Get(6)->ToString() ?? string.Empty; } catch { }
                try { idx0_7 = sub0->Get(7)->ToString() ?? string.Empty; } catch { }
                try { idx1_2 = item->Get(1)->Get(2)->ToString() ?? string.Empty; } catch { }
                try { idx1_3 = item->Get(1)->Get(3)->ToString() ?? string.Empty; } catch { }
                try { idx2_2 = item->Get(2)->Get(2)->ToString() ?? string.Empty; } catch { }
                try { idx2_3 = item->Get(2)->Get(3)->ToString() ?? string.Empty; } catch { }
                try { idx3_0 = item->Get(3)->Get(0)->ToString() ?? string.Empty; } catch { }
                try { idx3_1 = item->Get(3)->Get(1)->ToString() ?? string.Empty; } catch { }
            } catch { }
        }

        return new AbilityProbe(
            Idx2_0: idx2_0, Idx2_1: idx2_1,
            Idx0_5: idx0_5, Idx0_6: idx0_6, Idx0_7: idx0_7,
            Idx1_2: idx1_2, Idx1_3: idx1_3,
            Idx2_2: idx2_2, Idx2_3: idx2_3,
            Idx3_0: idx3_0, Idx3_1: idx3_1
        );
    }

    private RValue* NewFightDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        // Round 3 confirmed: argv[0] carries the full enc_<location>_<entity><variant> string.
        // All self-side probes (bpName, script, pattern, etc.) were uniformly empty.
        var encounterKey = ReadArgvString(argc, argv, 0);
        this.Emit(new NewFightEvent(encounterKey, this.GameTime()));
        return this.newFightHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* StageChangeDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        // argv[0] = the new stage's location ID (int); argv[1] = transition animation ms.
        // At run start argv[1] is "undefined" which RValueToLong throws on — caught, ms=0.
        int locationId = 0;
        int transitionMs = 0;
        try { locationId = (int) this.rns.utils.RValueToLong(argv[0]); } catch { }
        if (argc > 1) {
            try { transitionMs = (int) this.rns.utils.RValueToLong(argv[1]); } catch { }
        }
        this.Emit(new StageChangeEvent(locationId, transitionMs, this.GameTime()));
        return this.stageChangeHook.OriginalFunction(self, other, returnValue, argc, argv);
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

        // Promoted typed fields from round-3 probes.
        string nextEncounterKey = string.Empty;
        string notchSeed = string.Empty;
        try { nextEncounterKey = this.rns.FindValue(self, "notches")->Get((int) currentPos)->Get(1)->ToString() ?? ""; } catch { }
        try { notchSeed        = this.rns.FindValue(self, "notches")->Get((int) currentPos)->Get(2)->ToString() ?? ""; } catch { }

        var stageProbe = this.ProbeHallwayMoveStage(self, (int) currentPos);
        this.Emit(new HallwayMoveEvent(
            NotchPos: (int) currentPos,
            NotchType: notchType,
            NextEncounterKey: nextEncounterKey,
            NotchSeed: notchSeed,
            StageProbe: stageProbe,
            GameTime: this.GameTime()
        ));
        return this.hallwayMoveHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private HallwayMoveStageProbe ProbeHallwayMoveStage(CInstance* self, int currentPos) {
        string notch3 = string.Empty, notch4 = string.Empty, notch5 = string.Empty;
        string notch6 = string.Empty, notch7 = string.Empty;
        try { notch3 = this.rns.FindValue(self, "notches")->Get(currentPos)->Get(3)->ToString() ?? ""; } catch { }
        try { notch4 = this.rns.FindValue(self, "notches")->Get(currentPos)->Get(4)->ToString() ?? ""; } catch { }
        try { notch5 = this.rns.FindValue(self, "notches")->Get(currentPos)->Get(5)->ToString() ?? ""; } catch { }
        try { notch6 = this.rns.FindValue(self, "notches")->Get(currentPos)->Get(6)->ToString() ?? ""; } catch { }
        try { notch7 = this.rns.FindValue(self, "notches")->Get(currentPos)->Get(7)->ToString() ?? ""; } catch { }

        return new HallwayMoveStageProbe(
            Notch_3: notch3, Notch_4: notch4, Notch_5: notch5, Notch_6: notch6, Notch_7: notch7,
            SelfCurrentStage:    ReadSelfString(this.rns, self, "currentStage"),
            SelfCurrentHall:     ReadSelfString(this.rns, self, "currentHall"),
            SelfCurrentLocation: ReadSelfString(this.rns, self, "currentLocation"),
            SelfStage:           ReadSelfString(this.rns, self, "stage"),
            SelfHall:            ReadSelfString(this.rns, self, "hall"),
            SelfLocation:        ReadSelfString(this.rns, self, "location"),
            SelfZone:            ReadSelfString(this.rns, self, "zone"),
            SelfStageHall:       ReadSelfString(this.rns, self, "stageHall"),
            SelfStageLoc:        ReadSelfString(this.rns, self, "stageLoc"),
            SelfStageNum:        ReadSelfString(this.rns, self, "stageNum"),
            SelfStageIndex:      ReadSelfString(this.rns, self, "stageIndex"),
            SelfStageKey:        ReadSelfString(this.rns, self, "stageKey"),
            SelfCurrentHallKey:  ReadSelfString(this.rns, self, "currentHallKey"),
            GlobCurrentStage:    ReadGlobalString(this.rns, "currentStage"),
            GlobCurrentLocation: ReadGlobalString(this.rns, "currentLocation"),
            GlobCurrentHall:     ReadGlobalString(this.rns, "currentHall"),
            GlobRunStage:        ReadGlobalString(this.rns, "runStage"),
            GlobCurrentZone:     ReadGlobalString(this.rns, "currentZone"),
            GlobStageLoc:        ReadGlobalString(this.rns, "stageLoc"),
            GlobCurrentLoc:      ReadGlobalString(this.rns, "currentLoc")
        );
    }

    private RValue* ChooseHallsDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        // Order swap from round 3: call the original FIRST so the script populates self state,
        // then probe. Round 3's entry-side probes were uniformly empty.
        var ret = this.chooseHallsHook.OriginalFunction(self, other, returnValue, argc, argv);
        this.Emit(new ChooseHallsEvent(this.ProbeChooseHalls(self, argc, argv), this.GameTime()));
        return ret;
    }

    private ChooseHallsProbe ProbeChooseHalls(CInstance* self, int argc, RValue** argv) {
        return new ChooseHallsProbe(
            Stages:         ReadSelfArray5(this.rns, self, "stages"),
            Halls:          ReadSelfArray5(this.rns, self, "halls"),
            RunHalls:       ReadSelfArray5(this.rns, self, "runHalls"),
            StageHalls:     ReadSelfArray5(this.rns, self, "stageHalls"),
            StageOrder:     ReadSelfArray5(this.rns, self, "stageOrder"),
            LocationOrder:  ReadSelfArray5(this.rns, self, "locationOrder"),
            Path:           ReadSelfArray5(this.rns, self, "path"),
            Locations:      ReadSelfArray5(this.rns, self, "locations"),
            RunStages:      ReadSelfArray5(this.rns, self, "runStages"),
            ChosenHalls:    ReadSelfArray5(this.rns, self, "chosenHalls"),
            SelectedHalls:  ReadSelfArray5(this.rns, self, "selectedHalls"),
            SelfCurrentStage: ReadSelfString(this.rns, self, "currentStage"),
            SelfStage:        ReadSelfString(this.rns, self, "stage"),
            SelfHall:         ReadSelfString(this.rns, self, "hall"),
            SelfLocation:     ReadSelfString(this.rns, self, "location"),
            SelfZone:         ReadSelfString(this.rns, self, "zone"),
            Argc:  argc,
            Argv0: ReadArgvString(argc, argv, 0),
            Argv1: ReadArgvString(argc, argv, 1),
            Argv2: ReadArgvString(argc, argv, 2),
            Argv3: ReadArgvString(argc, argv, 3),
            Argv4: ReadArgvString(argc, argv, 4),
            GlobCurrentStage:    ReadGlobalString(this.rns, "currentStage"),
            GlobCurrentLocation: ReadGlobalString(this.rns, "currentLocation"),
            GlobRunStage:        ReadGlobalString(this.rns, "runStage"),
            GlobRunLocation:     ReadGlobalString(this.rns, "runLocation"),
            GlobSelectedHall:    ReadGlobalString(this.rns, "selectedHall")
        );
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
    private const long HBS_SURVEY_34 = 34;     // semantic TBD - same payload as create/destroy
    private const long HBS_SURVEY_35 = 35;     // semantic TBD
    private const long HBS_DESTROYED = 36;

    private RValue* TriggerCallDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var triggerType = argc > 0 ? this.rns.utils.RValueToLong(argv[0]) : 0;

        if (triggerType == HBS_CREATED) {
            var ctx = this.GatherBuffContext(self);
            this.Emit(new AddBuffEvent(
                UniqueId: ctx.UniqueId,
                BuffId: ctx.StatusId,
                BuffKey: ctx.Key,
                BuffName: ctx.Name,
                SourceId: ctx.SourceId,
                TargetId: ctx.TargetId,
                TargetsEnemy: ctx.TargetsEnemy,
                SourceTeamId: ctx.SourceTeamId,
                Duration: ctx.Duration,
                Strength: ctx.Strength,
                SourceHbId: ctx.SourceHbId,
                GameTime: this.GameTime()
            ));
        } else if (triggerType == HBS_DESTROYED) {
            this.Emit(new RemoveBuffEvent(
                UniqueId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "hbsUniqueId")),
                GameTime: this.GameTime()
            ));
        } else if (triggerType == HBS_SURVEY_34 || triggerType == HBS_SURVEY_35) {
            // 34/35 carry the same buff-shaped self context as 33/36; observe with the full payload
            // so we can pattern-match meaning across runs (possibly refresh / tick / area-applied).
            var ctx = this.GatherBuffContext(self);
            this.Emit(new BuffSurveyEvent(
                TriggerType: (int) triggerType,
                UniqueId: ctx.UniqueId,
                BuffId: ctx.StatusId,
                BuffKey: ctx.Key,
                BuffName: ctx.Name,
                SourceId: ctx.SourceId,
                TargetId: ctx.TargetId,
                TargetsEnemy: ctx.TargetsEnemy,
                SourceTeamId: ctx.SourceTeamId,
                Duration: ctx.Duration,
                Strength: ctx.Strength,
                SourceHbId: ctx.SourceHbId,
                GameTime: this.GameTime()
            ));
        } else {
            // Other trigger types fire constantly during gameplay (movement, state-setters, etc.).
            // Sample per-type so we still see the inventory without flooding the log.
            if (!this.triggerProbeSampleCount.TryGetValue(triggerType, out var count)) count = 0;
            if (count < MaxTriggerProbeSamplesPerType) {
                this.triggerProbeSampleCount[triggerType] = count + 1;
                this.Emit(new TriggerProbeEvent((int) triggerType, this.GameTime()) {
                    Argc = argc,
                    Argv1 = ReadArgvString(argc, argv, 1),
                    Argv2 = ReadArgvString(argc, argv, 2),
                    Argv3 = ReadArgvString(argc, argv, 3),
                    SelfId = ReadSelfInt(this.rns, self, "id"),
                    SelfPlayerId = ReadSelfInt(this.rns, self, "playerId"),
                    SelfStatusId = ReadSelfString(this.rns, self, "statusId"),
                    SelfTeamId = ReadSelfInt(this.rns, self, "teamId"),
                    SelfHbsUniqueId = ReadSelfString(this.rns, self, "hbsUniqueId"),
                });
            }
        }

        return this.triggerCallHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    /// <summary>
    /// Common buff/status context shared by trigger types 33-36. We bundle the FindValue reads
    /// in one place so the per-type dispatch in TriggerCallDetour stays readable. SourceTeamId
    /// captures who applied the status (0 = player team, 1 = enemy team) so consumers can
    /// distinguish buffs from debuffs without re-reading self.
    /// </summary>
    private (int UniqueId, int StatusId, string Key, string Name,
             int SourceId, int TargetId, bool TargetsEnemy, int SourceTeamId,
             int Duration, int Strength, int SourceHbId)
        GatherBuffContext(CInstance* self) {
        var statusId = (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "statusId"));
        var (key, name) = this.LookupHbsInfo(statusId);
        return (
            UniqueId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "hbsUniqueId")),
            StatusId: statusId,
            Key: key,
            Name: name,
            SourceId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "playerId")),
            TargetId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "aflPlayerId")),
            TargetsEnemy: this.rns.utils.RValueToLong(this.rns.FindValue(self, "aflTeamId")) == 1,
            SourceTeamId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "teamId")),
            Duration: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "initLength")),
            Strength: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "strength")),
            SourceHbId: (int) this.rns.utils.RValueToLong(this.rns.FindValue(self, "originHbId"))
        );
    }

    // === Survey hooks ============================================================================
    // Round 4 promotions: PlayerInvuln argv0 → DurationMs; Reward argv0/1/2 → PlayerId/Tier/Score.
    // PlayerHit expanded with many more probe fields since round-3 PlayerHit data was constant.

    private RValue* PlayerInvulnDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var durationMs = 0;
        try { durationMs = (int) this.rns.utils.RValueToLong(argv[0]); } catch { }

        this.Emit(new PlayerInvulnEvent(durationMs, this.GameTime()) {
            PlayerId = ReadSelfInt(this.rns, self, "playerId"),
            SelfId = ReadSelfInt(this.rns, self, "id"),
            Argc = argc,
            Argv1 = ReadArgvString(argc, argv, 1),
            Argv2 = ReadArgvString(argc, argv, 2),
            Argv3 = ReadArgvString(argc, argv, 3),
        });
        return this.playerInvulnHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* PlayerHitDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        this.Emit(new PlayerHitEvent(this.GameTime()) {
            PlayerId = ReadSelfInt(this.rns, self, "playerId"),
            SelfId = ReadSelfInt(this.rns, self, "id"),
            Argc = argc,
            Argv0 = ReadArgvString(argc, argv, 0),
            Argv1 = ReadArgvString(argc, argv, 1),
            Argv2 = ReadArgvString(argc, argv, 2),
            Argv3 = ReadArgvString(argc, argv, 3),
            Argv4 = ReadArgvString(argc, argv, 4),
            Argv5 = ReadArgvString(argc, argv, 5),
            Argv6 = ReadArgvString(argc, argv, 6),
            SelfDataId         = ReadSelfString(this.rns, self, "dataId"),
            SelfStatusId       = ReadSelfString(this.rns, self, "statusId"),
            SelfTargetPlayerId = ReadSelfString(this.rns, self, "targetPlayerId"),
            SelfAttackerId     = ReadSelfString(this.rns, self, "attackerId"),
            SelfBp             = ReadSelfString(this.rns, self, "bp"),
            SelfScript         = ReadSelfString(this.rns, self, "script"),
            SelfBpName         = ReadSelfString(this.rns, self, "bpName"),
            SelfActionScript   = ReadSelfString(this.rns, self, "actionScript"),
            SelfDmg            = ReadSelfString(this.rns, self, "dmg"),
            SelfDamage         = ReadSelfString(this.rns, self, "damage"),
            SelfTeamId         = ReadSelfString(this.rns, self, "teamId"),
            SelfAflPlayerId    = ReadSelfString(this.rns, self, "aflPlayerId"),
            SelfAflTeamId      = ReadSelfString(this.rns, self, "aflTeamId"),
            SelfPainshare      = ReadSelfString(this.rns, self, "painshare"),
        });
        return this.playerHitHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* RewardDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var playerId = 0;
        var tier = 0;
        var score = 0;
        try { playerId = (int) this.rns.utils.RValueToLong(argv[0]); } catch { }
        try { tier     = (int) this.rns.utils.RValueToLong(argv[1]); } catch { }
        try { score    = (int) this.rns.utils.RValueToLong(argv[2]); } catch { }

        this.Emit(new RewardEvent(playerId, tier, score, this.GameTime()) {
            SelfId = ReadSelfInt(this.rns, self, "id"),
            Argc = argc,
        });
        return this.rewardHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    // Width of each candidate offered-items array probe. Chests contain 5 items per the user's
    // observation; shop has at least 6 (slot 5 was used). 8 covers both with headroom.
    private const int ItemPickupProbeArrayLen = 8;

    private RValue* ItemPickupDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        // Args characterized empirically: argv[0]=playerId, argv[1]=source (1=chest, 2=shop),
        // argv[2]=slot index of the chosen entry within the offered-items list.
        var playerId = 0;
        var source = 0;
        var slot = 0;
        try { playerId = (int) this.rns.utils.RValueToLong(argv[0]); } catch { }
        try { source   = (int) this.rns.utils.RValueToLong(argv[1]); } catch { }
        try { slot     = (int) this.rns.utils.RValueToLong(argv[2]); } catch { }

        var (selfHits, otherHits, globHits) = this.ScanItemFields(self, other);
        this.Emit(new ItemPickupEvent(playerId, source, slot, selfHits, otherHits, globHits, this.GameTime()));
        return this.itemPickupHook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* ItemCreateDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        // Call original first so returnValue holds the new item's instance id.
        var result = this.itemCreateHook.OriginalFunction(self, other, returnValue, argc, argv);

        var dataId = 0;
        var playerId = 0;
        var category = 0;
        try { dataId   = (int) this.rns.utils.RValueToLong(argv[0]); } catch { }
        try { playerId = (int) this.rns.utils.RValueToLong(argv[1]); } catch { }
        try { category = (int) this.rns.utils.RValueToLong(argv[2]); } catch { }
        var instanceId = 0;
        try { instanceId = (int) this.rns.utils.RValueToLong(returnValue); } catch { }

        var (key, name) = this.LookupItemByDataId(dataId);
        this.Emit(new ItemCreateEvent(dataId, playerId, category, instanceId, key, name, this.GameTime()));
        return result;
    }

    // Resolves a dataId to its (key, pretty name) via the global itemData table — same access
    // path as LookupAbility, but keyed by an explicit dataId rather than self.dataId. Each slot
    // read is independently guarded so a missing pretty name doesn't blank the key.
    private (string Key, string Name) LookupItemByDataId(int dataId) {
        string key = string.Empty, name = string.Empty;
        if (dataId <= 0) return (key, name);
        try {
            var sub0 = this.rns
                .FindValue(this.rns.GetGlobalInstance(), "itemData")
                ->Get(dataId)->Get(0);
            try { key  = sub0->Get(0)->ToString() ?? string.Empty; } catch { }
            try { name = sub0->Get(2)->ToString() ?? string.Empty; } catch { }
        } catch { }
        return (key, name);
    }

    // Candidate field names scanned on instances (self / other) when hunting for the offered /
    // owned item structure. "storebin" / "lootbin" are the strongest leads (from the script-dump
    // name scr_itemsys_refresh_storebin); the rest are accumulated guesses. Append freely — each
    // miss is a cheap failed FindValue.
    private static readonly string[] ItemInstanceFields = {
        "items", "options", "choices", "loot", "inventory", "itemList", "contents", "offered",
        "dataIds", "itemPool", "pool", "lootTable",
        "storebin", "lootbin", "potionbin", "upgradebin", "store", "bin",
        "potions", "upgrades", "storeItems", "lootItems", "itemCount", "choiceCount", "size",
    };

    // Candidate global names scanned in parallel (the structure may be a global, not on the
    // instance). Includes the same storebin family.
    private static readonly string[] ItemGlobalFields = {
        "chestItems", "shopItems", "currentLoot", "currentShop", "lootOptions", "shopInventory",
        "currentItems", "lootDataIds", "shopDataIds",
        "storebin", "lootbin", "potionbin", "upgradebin", "store", "loot",
        "storeItems", "currentStore", "currentChest", "potions", "upgrades",
    };

    // Probes self / other / globals for the item structure. Returns three lists of self-describing
    // hit strings ("field=[v0, v1, ...]" for arrays, "field=value" for scalars). Empty/missing
    // fields are omitted, so a populated structure stands out immediately in the log.
    private (List<string> Self, List<string> Other, List<string> Glob) ScanItemFields(CInstance* self, CInstance* other) {
        return (
            this.ScanInstanceFields(self, ItemInstanceFields),
            this.ScanInstanceFields(other, ItemInstanceFields),
            this.ScanInstanceFields(this.rns.GetGlobalInstance(), ItemGlobalFields)
        );
    }

    // Tolerant hook for the chest/shop populate scripts. The detour calls the original FIRST so
    // the populate function fills its structures, THEN probes for the now-populated offered list.
    private IHook<ScriptDelegate>? TryHookItemPopulate(IReloadedHooks hooks, string name, int source) {
        var id = this.rns.ScriptFindId(name);
        if (id == -1) return null;
        try {
            var script = this.rns.GetScriptData(id - 100000);
            IHook<ScriptDelegate>? hookRef = null;
            ScriptDelegate detour = (self, other, ret, argc, argv) => {
                var result = hookRef!.OriginalFunction(self, other, ret, argc, argv);
                this.ItemPopulateEmit(name, source, self, other, ret, argc, argv);
                return result;
            };
            hookRef = hooks.CreateHook<ScriptDelegate>(detour, script->Functions->Function);
            hookRef.Activate();
            hookRef.Enable();
            this.probeDelegates.Add(detour);
            return hookRef;
        } catch {
            return null;
        }
    }

    private void ItemPopulateEmit(
        string scriptName, int source, CInstance* self, CInstance* other, RValue* ret, int argc, RValue** argv
    ) {
        // Cap per script so a per-frame setpos/refresh can't flood the log (and we skip the
        // expensive field scan once capped).
        if (!this.itemPopulateSampleCount.TryGetValue(scriptName, out var count)) count = 0;
        if (count >= MaxItemPopulateSamplesPerScript) return;
        this.itemPopulateSampleCount[scriptName] = count + 1;

        // The return value may itself hold the populated list (array form) or a single item
        // identity (scalar form) — capture both.
        var retArray = new string[ItemPickupProbeArrayLen];
        Array.Fill(retArray, string.Empty);
        var retScalar = string.Empty;
        if (ret != null) {
            for (var i = 0; i < ItemPickupProbeArrayLen; i++) {
                try {
                    var v = ret->Get(i);
                    if (v != null) retArray[i] = v->ToString() ?? string.Empty;
                } catch { }
            }
            try { retScalar = ret->ToString() ?? string.Empty; } catch { }
        }

        // Scan each argument as an array — a _single(itemRecord) call might hand us the whole
        // item struct as one arg, whose elements (key, name, ...) we'd only see via Get(i).
        var argHits = new List<string>();
        for (var a = 0; a < argc && a < 8; a++) {
            var vals = new List<string>();
            var any = false;
            for (var i = 0; i < ItemPickupProbeArrayLen; i++) {
                var s = string.Empty;
                try {
                    var v = argv[a]->Get(i);
                    if (v != null) s = v->ToString() ?? string.Empty;
                } catch { }
                if (s.Length > 0) any = true;
                vals.Add(s);
            }
            if (any) argHits.Add($"argv{a}=[{string.Join(", ", vals)}]");
        }

        var (selfHits, otherHits, globHits) = this.ScanItemFields(self, other);
        this.Emit(new ItemPopulateEvent(
            ScriptName: scriptName,
            Source: source,
            Argc: argc,
            // Widened argv: a _single(slot, dataId)-style call may pass the placed item's identity
            // directly, so capture the full scalar span (plus ArgHits for array-shaped args).
            Argv0: ReadArgvString(argc, argv, 0),
            Argv1: ReadArgvString(argc, argv, 1),
            Argv2: ReadArgvString(argc, argv, 2),
            Argv3: ReadArgvString(argc, argv, 3),
            Argv4: ReadArgvString(argc, argv, 4),
            Argv5: ReadArgvString(argc, argv, 5),
            Argv6: ReadArgvString(argc, argv, 6),
            ArgHits: argHits,
            RetScalar: retScalar,
            RetArray: retArray,
            SelfHits: selfHits,
            OtherHits: otherHits,
            GlobHits: globHits,
            GameTime: this.GameTime()
        ));
    }

    // Scans an instance's candidate fields and returns self-describing hit strings. A field that
    // reads as a non-empty array yields "field=[v0, v1, ...]"; otherwise a non-trivial scalar
    // yields "field=value". Missing / empty / zero fields are omitted. One pass over a wide
    // candidate list — the whole point is to let us throw names at the wall cheaply and read the
    // winner straight out of the log.
    private List<string> ScanInstanceFields(CInstance* inst, string[] candidates) {
        var hits = new List<string>();
        if (inst == null) return hits;
        foreach (var field in candidates) {
            RValue* fv;
            try { fv = this.rns.FindValue(inst, field); } catch { continue; }
            if (fv == null) continue;

            // Array form first.
            var vals = new List<string>();
            var anyArray = false;
            for (var i = 0; i < ItemPickupProbeArrayLen; i++) {
                var s = string.Empty;
                try {
                    var v = fv->Get(i);
                    if (v != null) s = v->ToString() ?? string.Empty;
                } catch { }
                if (s.Length > 0) anyArray = true;
                vals.Add(s);
            }
            if (anyArray) {
                hits.Add($"{field}=[{string.Join(", ", vals)}]");
                continue;
            }

            // Scalar fallback. Filter trivial values so the hit list stays signal-only.
            string scalar = string.Empty;
            try { scalar = fv->ToString() ?? string.Empty; } catch { }
            if (scalar.Length > 0 && scalar != "0" && scalar != "0.00" && scalar != "[array]") {
                hits.Add($"{field}={scalar}");
            }
        }
        return hits;
    }

    // === Layer C: tolerant try-hooks ==============================================================
    // Each candidate is attempted once at startup; missing scripts (ScriptFindId == -1) are
    // silently skipped. Survivors all emit ScriptProbeEvent with the script name as the
    // discriminator. Add new candidate names by appending to ProbeCandidates below.

    // Candidate list refined after the previous pass returned 0/46 hooks. Strategy:
    //   1. Names confirmed-referenced as actual ScriptFindId targets elsewhere in the game / other
    //      mods (highest confidence — these definitely exist).
    //   2. Sibling patterns of confirmed scripts (hallwayprogress_*, stage_*, itemsys_*) — high
    //      confidence on the namespace, guessing the verb.
    //   3. Common verbs paired with itemsys (the only itemsys script we know is _erase_potions).
    // We're NOT re-trying the old chest_*/loot_*/shop_* names — those produced zero hits and we
    // have no evidence they're separate scripts (likely live on object events instead). The
    // TriggerProbeEvent passthrough on scr_trigger_call is the better instrument for those.
    private static readonly string[] ProbeCandidates = {
        // CONFIRMED-EXIST in the game (referenced by other mods' actual CreateHook calls or by
        // RNSReloaded itself). Hooking these will succeed; the question is what data each carries.
        // Empirically verified noisy on startup (>97% of log was scr_hbsflag_check; 3% was
        // scr_player_update_control):
        //   - scr_hbsflag_check: called constantly while ANY buff exists (per-frame per-buff).
        //   - scr_player_update_control: per-frame per-player input poll.
        // Both are dropped — too noisy to instrument as catch-alls without per-condition gating.
        // scr_stage_change is now a first-class hook (typed StageChangeEvent), removed from here.
        "scr_stage_play_music",
        "scr_hallwayprogress_start_hallway",
        "scr_init_adventure_map",
        "scr_itemsys_erase_potions",
        "scr_players_move_next_position",
        "scr_chat_add_mesage_system",
        "scr_playercolor_set",
        // Sibling guesses — scr_hallwayprogress_* (we have three confirmed siblings).
        "scr_hallwayprogress_end_hallway",
        "scr_hallwayprogress_confirm",
        "scr_hallwayprogress_select_hall",
        "scr_hallwayprogress_pick_hall",
        "scr_hallwayprogress_present_halls",
        "scr_hallwayprogress_finish",
        // Stage-level guesses (scr_stage_change + scr_stage_play_music are confirmed).
        "scr_stage_init", "scr_stage_start", "scr_stage_finish", "scr_stage_end", "scr_stage_complete",
        // itemsys-* guesses (scr_itemsys_erase_potions + scr_itemsys_pickup confirm the namespace).
        // scr_itemsys_pickup is now a first-class hook (typed ItemPickupEvent).
        // First pass (none of these hit): add, give, grant, remove, clear, init, buy, get,
        // has_item, get_count — keeping a few in case the game version changes.
        "scr_itemsys_add", "scr_itemsys_buy",
        // New verbs targeting "the offered items get set up here" hypothesis. If any of these
        // fire before scr_itemsys_pickup, the offered list is probably accessible from inside.
        "scr_itemsys_offer", "scr_itemsys_present", "scr_itemsys_show",
        "scr_itemsys_choose", "scr_itemsys_setup", "scr_itemsys_apply",
        "scr_itemsys_select", "scr_itemsys_roll", "scr_itemsys_generate",
        "scr_itemsys_populate", "scr_itemsys_open", "scr_itemsys_close",
        "scr_itemsys_create_loot", "scr_itemsys_create_shop", "scr_itemsys_roll_loot",
        // dt-namespace (scrdt_encounter, scrdt_enemy are confirmed).
        "scrdt_item", "scrdt_loot", "scrdt_chest", "scrdt_shop", "scrdt_treasure",
        "scrdt_reward", "scrdt_hall", "scrdt_stage",
    };

    private void AttachProbeHooks(IReloadedHooks hooks) {
        var hooked = new List<string>();
        foreach (var name in ProbeCandidates) {
            var hook = this.TryHookScript(hooks, name);
            this.probeHooks.Add((name, hook));
            if (hook is not null) hooked.Add(name);
        }
        this.logger.PrintMessage(
            $"BunnyLog: hooked {hooked.Count} of {ProbeCandidates.Length} candidate exploration scripts" +
            (hooked.Count > 0 ? ": " + string.Join(", ", hooked) : ""),
            this.logger.ColorYellow
        );
    }

    private IHook<ScriptDelegate>? TryHookScript(IReloadedHooks hooks, string name) {
        var id = this.rns.ScriptFindId(name);
        if (id == -1) return null;
        try {
            var script = this.rns.GetScriptData(id - 100000);
            // Per-script detour closure: captures `name` so the emitted event carries the script
            // identifier. The IHook reference is captured-by-ref into the closure via the local
            // `hookRef` slot so the detour can call OriginalFunction.
            IHook<ScriptDelegate>? hookRef = null;
            ScriptDelegate detour = (self, other, ret, argc, argv) => {
                this.ScriptProbeEmit(name, self, argc, argv);
                return hookRef!.OriginalFunction(self, other, ret, argc, argv);
            };
            hookRef = hooks.CreateHook<ScriptDelegate>(detour, script->Functions->Function);
            hookRef.Activate();
            hookRef.Enable();
            // Keep the delegate alive against GC — the underlying native callback is unmanaged.
            this.probeDelegates.Add(detour);
            return hookRef;
        } catch {
            return null;
        }
    }

    private void ScriptProbeEmit(string scriptName, CInstance* self, int argc, RValue** argv) {
        if (!this.scriptProbeSampleCount.TryGetValue(scriptName, out var count)) count = 0;
        if (count >= MaxScriptProbeSamplesPerName) return;
        this.scriptProbeSampleCount[scriptName] = count + 1;
        this.Emit(new ScriptProbeEvent(scriptName, this.GameTime()) {
            Argc = argc,
            Argv0 = ReadArgvString(argc, argv, 0),
            Argv1 = ReadArgvString(argc, argv, 1),
            Argv2 = ReadArgvString(argc, argv, 2),
            Argv3 = ReadArgvString(argc, argv, 3),
            Argv4 = ReadArgvString(argc, argv, 4),
            Argv5 = ReadArgvString(argc, argv, 5),
            Argv6 = ReadArgvString(argc, argv, 6),
            SelfId       = ReadSelfInt(this.rns, self, "id"),
            SelfPlayerId = ReadSelfInt(this.rns, self, "playerId"),
            SelfDataId   = ReadSelfInt(this.rns, self, "dataId"),
            SelfHb       = ReadSelfInt(this.rns, self, "hbId"),
        });
    }

    // === Small read helpers shared by the survey probes ==========================================
    // All swallow exceptions and return empty / 0 on any failure path so the survey doesn't
    // crash the detour. The cost is per-probe overhead; acceptable while these are temporary.

    private static int ReadSelfInt(IRNSReloaded rns, CInstance* self, string field) {
        try {
            var v = rns.FindValue(self, field);
            if (v == null) return 0;
            return (int) rns.utils.RValueToLong(v);
        } catch {
            return 0;
        }
    }

    private static string ReadSelfString(IRNSReloaded rns, CInstance* self, string field) {
        try {
            var v = rns.FindValue(self, field);
            if (v == null) return string.Empty;
            return v->ToString() ?? string.Empty;
        } catch {
            return string.Empty;
        }
    }

    private static string ReadGlobalString(IRNSReloaded rns, string name) {
        try {
            var v = rns.FindValue(rns.GetGlobalInstance(), name);
            if (v == null) return string.Empty;
            return v->ToString() ?? string.Empty;
        } catch {
            return string.Empty;
        }
    }

    private static string ReadArgvString(int argc, RValue** argv, int idx) {
        if (idx >= argc) return string.Empty;
        try {
            return argv[idx]->ToString() ?? string.Empty;
        } catch {
            return string.Empty;
        }
    }

    // Reads a 5-element string array from self.<field>[0..4]. Returns the shared EmptyFive
    // sentinel when the read fails entirely; otherwise allocates a fresh 5-array with whatever
    // we could read (per-slot try/catch).
    private static string[] ReadSelfArray5(IRNSReloaded rns, CInstance* self, string field) {
        try {
            var arr = rns.FindValue(self, field);
            if (arr == null) return EmptyFive;
            var result = new string[5];
            var anyOk = false;
            for (var i = 0; i < 5; i++) {
                try {
                    var v = arr->Get(i);
                    if (v != null) {
                        result[i] = v->ToString() ?? string.Empty;
                        anyOk = true;
                    } else {
                        result[i] = string.Empty;
                    }
                } catch {
                    result[i] = string.Empty;
                }
            }
            return anyOk ? result : EmptyFive;
        } catch {
            return EmptyFive;
        }
    }
}
