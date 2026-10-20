using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER7-CORE-01 STORY-EXECUTION-CARDS.md：主核心 Boss 战——两供能节点+主核心状态机的唯一
    /// 写入口。仍是同一 <c>foundry_outpost</c> 会话内部的第三个空间子区（ER6-REGION-01 已确立"核心
    /// 分区门禁在外围场景内部解决，不是出发去新地图"），不新开 Controller，挂在
    /// <see cref="FoundryOutpostController"/> 既有 Update 循环里。
    ///
    /// ── 两供能节点/主核心是 RegionEnemyRecord，不是新数据类型 ──
    /// EnemyTypeId 为 <see cref="FoundryOutpostLayout.BossNodeTypeId"/>/<see cref="FoundryOutpostLayout.BossCoreTypeId"/>
    /// 的三条 <see cref="RegionEnemyRecord"/>，复用它已有的 Health/MaxHealth/IsAlive/
    /// CycleCooldownRemaining 字段，与既有 <see cref="FoundryOutpostRegion.TryAttackEnemy"/>/
    /// <see cref="CannonCombat.TryFire"/> 伤害结算路径完全共用（含铸造重炮/熔穿过载/标记等既有反应），
    /// 不新造第二套伤害管线——"熔穿过载在本战必须能实际触发一次"因此结构上直接成立，不需要专门为
    /// Boss 战重新接一遍反应判定。<see cref="FoundryOutpostRegion.TryDamageEnemy"/>/
    /// <see cref="FracturedCityRegion.TryDamageEnemy"/>（铸造重炮命中固定走后者，见该类类注释）都在
    /// 顶部拦截这两个 EnemyTypeId，转发到本类 <see cref="ApplyDamage"/>，不落入"死了掉废料"的通用
    /// 阵亡分支——护盾/阶段语义与掉落完全不同。
    ///
    /// ── 状态机（RegionRecord.CoreState 唯一写入口）──
    /// <see cref="CoreBossState"/>：Demo 的 Locked→Shielded→Phase1→Transition→Phase2 原样保留；FG9-FAC-02 新增
    /// Phase2→<see cref="CoreBossState.AntiAir"/>（阶段三·对空形态，耐久降到 boss.foundry.phase3_hp_fraction 时进入）→
    /// <see cref="CoreBossState.Berserk"/>（15 秒后恢复并狂暴）→Destroyed；对空形态的 15 秒爆发窗口里打到 0 也可以直接 →Destroyed。
    /// <see cref="TryTransition"/> 拒绝任何不在合法表里的跳转并打 <see cref="Log.Error"/> 级开发诊断
    /// （"不在玩家HUD泄漏枚举名"——玩家可见文案走 <see cref="DisplayPhaseText"/>）。
    ///
    /// ── FG9-FAC-02 新增 ──
    /// - 对空形态：主核心不再对地开火，按敌人表的齐射参数只打无人机与飞行中的弹体（内核 AirMode），核心暴露、受到伤害 × boss.foundry.exposed_damage_mult。
    /// - 狂暴：对地开火间隔 × boss.foundry.berserk_cooldown_mult，派出编成表 boss_wave 行（精英攻城重甲）一次；侧后加成照旧。
    /// - 场地边界（FGR-FAC-006）：首领战进行中，玩家在本地点的机器全部离开核心区超过 boss.foundry.arena_leave_seconds 秒 → 主核心复位（固定结构，原地不动）、
    ///   当前阶段的耐久恢复（护盾阶段：存活的供能节点回满；其余阶段：回到本阶段开始时的耐久），通知 + 字幕 + HUD 倒计时。
    /// - 击败奖励（只发一次）：敌人表 components 列的核心固件（过载、电容蓄力）、一个三选一数据包（来源 boss）；铸造领地平定与熔炉心走领地控制 / 主线（leaderRule foundry_core）。
    /// - 失败（撤离 / 全灭放弃）：<see cref="ResetToPreBossState"/> 记“上次止步于第几阶段”（通知 + HUD），首领战前自动存档供完整重试。
    /// 全部计时用 <see cref="CampaignState.PlaySeconds"/>（与 Demo 过渡计时同一时钟：暂停不走、倍速按游戏时间）。</summary>
    public static class FoundryOutpostCoreBoss
    {
        private static readonly HashSet<(CoreBossState, CoreBossState)> Legal = new HashSet<(CoreBossState, CoreBossState)>
        {
            (CoreBossState.Locked, CoreBossState.Shielded),
            (CoreBossState.Shielded, CoreBossState.Phase1),
            (CoreBossState.Phase1, CoreBossState.Transition),
            (CoreBossState.Transition, CoreBossState.Phase2),
            (CoreBossState.Phase2, CoreBossState.AntiAir),
            (CoreBossState.AntiAir, CoreBossState.Berserk),
            (CoreBossState.AntiAir, CoreBossState.Destroyed),
            (CoreBossState.Berserk, CoreBossState.Destroyed),
        };

        /// <summary>FG9-FAC-02：本次运行里的计数（自检读）。</summary>
        public static int RewardGrants { get; private set; }
        public static int ArenaResets { get; private set; }

        public static void ResetCountersForTests()
        {
            RewardGrants = 0;
            ArenaResets = 0;
        }

        public static float Phase3HealthFraction => Mathf.Clamp(Tuning("boss.foundry.phase3_hp_fraction", 0.3f), 0.05f, 0.55f);
        public static float AntiAirSeconds => Tuning("boss.foundry.antiair_seconds", 15f);
        public static float ExposedDamageMult => Mathf.Max(1f, Tuning("boss.foundry.exposed_damage_mult", 1.5f));
        public static float BerserkCooldownMult => Mathf.Clamp(Tuning("boss.foundry.berserk_cooldown_mult", 0.5f), 0.1f, 1f);
        public static float ArenaLeaveSeconds => Tuning("boss.foundry.arena_leave_seconds", 20f);

        public static CoreBossState GetState(RegionRecord region)
        {
            if (region == null || string.IsNullOrEmpty(region.CoreState))
            {
                return CoreBossState.Locked;
            }
            return Enum.TryParse(region.CoreState, out CoreBossState s) ? s : CoreBossState.Locked;
        }

        public static bool IsInitialized(RegionRecord region) => GetState(region) != CoreBossState.Locked;

        /// <summary>首领战进行中（已初始化、还没摧毁）。</summary>
        public static bool IsFighting(RegionRecord region)
        {
            CoreBossState s = GetState(region);
            return s != CoreBossState.Locked && s != CoreBossState.Destroyed;
        }

        /// <summary>唯一状态写入口——非法跳转（不在 <see cref="Legal"/> 表内）一律拒绝，只打开发
        /// 诊断日志，不抛异常、不改状态、调用方按 false 处理（防御性——正常游戏流程中不应触发）。</summary>
        public static bool TryTransition(CampaignState state, RegionRecord region, CoreBossState to)
        {
            CoreBossState current = GetState(region);
            if (!Legal.Contains((current, to)))
            {
                Log.Error($"[FoundryOutpostCoreBoss] 非法 Boss 状态转换：{current} → {to}，已拒绝（开发诊断，" +
                    "正常游戏流程不应出现，说明调用方逻辑有误）。");
                return false;
            }
            region.CoreState = to.ToString();
            Log.Info($"[FoundryOutpostCoreBoss] Boss 状态 {current} → {to}。");
            // FG9-FAC-02（FGR-FAC-006 场地边界）：记下本阶段开始时主核心的耐久（复位时恢复到这个值）。
            RegionEnemyRecord core = state != null ? FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.MainCoreId) : null;
            region.CorePhaseStartHealth = core != null ? core.Health : 0f;
            region.CoreArenaOutsideSeconds = 0f;
            // ER8-CONTENT-01 AC-AUD-001 Boss 阶段：唯一状态写入口，每次合法转换出声一次；
            // 文案走 DisplayPhaseText（不把枚举名给玩家看）。
            Vector2 at = FoundryOutpostLayout.MainCore.Position;
            switch (to)
            {
                case CoreBossState.Destroyed:
                    region.CoreLastReachedState = null; // 修复轮：通关后不再显示“上次止步于”（FG09 第 9 节只服务于重试）
                    Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BossDestroyed, at);
                    break;
                case CoreBossState.AntiAir:
                    // FG9-FAC-02：剧情时刻——所有炮口转向天空（伏笔的高潮）。
                    Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.FoundryBossAntiAir, at);
                    NotificationCenter.Post("foundry_boss", GameText.Format("boss.foundry.notify.antiair", Mathf.RoundToInt(AntiAirSeconds).ToString()),
                        new Vector3(at.x, 0f, at.y));
                    GuidanceHooks.Raise("foundry.first_antiair");
                    break;
                case CoreBossState.Berserk:
                    Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.FoundryBossBerserk, at);
                    NotificationCenter.Post("foundry_boss", GameText.Get("boss.foundry.notify.berserk"), new Vector3(at.x, 0f, at.y));
                    GuidanceHooks.Raise("foundry.first_berserk");
                    break;
                default:
                    Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BossPhase, at, DisplayPhaseText(region));
                    break;
            }
            return true;
        }

        /// <summary>玩家可见文案——"不在玩家HUD泄漏枚举名"，节点/主核心血条面板读这个而不是
        /// <see cref="RegionRecord.CoreState"/> 原始字符串。FG9-FAC-02 起走文本键（中文与 Demo 原文一致）。</summary>
        public static string DisplayPhaseText(RegionRecord region) => PhaseName(GetState(region));

        public static string PhaseName(CoreBossState s) => GameText.Get(s switch
        {
            CoreBossState.Locked => "boss.foundry.phase.locked",
            CoreBossState.Shielded => "boss.foundry.phase.shielded",
            CoreBossState.Phase1 => "boss.foundry.phase.phase1",
            CoreBossState.Transition => "boss.foundry.phase.transition",
            CoreBossState.Phase2 => "boss.foundry.phase.phase2",
            CoreBossState.AntiAir => "boss.foundry.phase.antiair",
            CoreBossState.Berserk => "boss.foundry.phase.berserk",
            CoreBossState.Destroyed => "boss.foundry.phase.destroyed",
            _ => "boss.foundry.phase.locked",
        });

        /// <summary>首次跨过核心分区封锁线时调用一次——幂等（已初始化直接返回）。生成两节点+主核心
        /// 三条 <see cref="RegionEnemyRecord"/>，状态推进到 <see cref="CoreBossState.Shielded"/>。
        /// 唯一调用点 <see cref="FoundryOutpostController"/> 的越线检测（见该类"进入核心分区"分支）。</summary>
        public static void EnsureInitialized(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null || IsInitialized(region))
            {
                return;
            }

            // 防御：理论上不会有残留（同一存档不会反复触发这条初始化路径），读档异常场景兜底不重复。
            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Where(e => e.EnemyInstanceId != FoundryOutpostLayout.CoreNode1Id
                    && e.EnemyInstanceId != FoundryOutpostLayout.CoreNode2Id
                    && e.EnemyInstanceId != FoundryOutpostLayout.MainCoreId)
                .ToArray();

            // FG9-DATA-01：首领部件来自编成表 boss 行（两供能节点 + 主核心），耐久 = 敌人表 × 难度倍率。
            RegionEnemySites.SeedEncounters(state, FoundryOutpostRegion.RegionId, EnemyDefs.ConditionBoss);

            region.CoreTransitionEndAtPlaySeconds = 0f;
            region.CoreTransitionRepairBotSummoned = false;
            region.CoreDataDropped = false;
            region.CoreLockoutWarnAtPlaySeconds = 0f;
            region.CoreLockoutActive = false;
            ClearPhase3Fields(region);

            TryTransition(state, region, CoreBossState.Shielded);

            // ER7-CREDITS-01：MachineExperienceFlags.BossParticipation 此前"依赖尚未实现的 ER7 Boss
            // 内容，当前无真实触发源"（ER4-MCH-01 DEBT-ER4MCH01-01 原文）——ER7-CORE-01 落地后这个
            // 触发源第一次真实存在：越过核心分区封锁线的那一刻，在场全部机器记一次"参与过 Boss 战"。
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == FoundryOutpostRegion.RegionId)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.BossParticipation);
                }
            }

            Log.Info("[FoundryOutpostCoreBoss] Boss 战已初始化：两供能节点+主核心就位。");
        }

        private static void ClearPhase3Fields(RegionRecord region)
        {
            region.CoreAntiAirEndAtPlaySeconds = 0f;
            region.CorePhaseStartHealth = 0f;
            region.CoreArenaOutsideSeconds = 0f;
            region.CoreBerserkWaveSummoned = false;
        }

        /// <summary>唯一伤害/阶段转换判定入口——<paramref name="target"/> 的存活/EnemyTypeId 已由
        /// 调用方（<see cref="FoundryOutpostRegion.TryDamageEnemy"/>/<see cref="FracturedCityRegion.TryDamageEnemy"/>）
        /// 校验过，这里只管"当前状态允不允许这条伤害生效"+"生效后要不要转换阶段"。伤害倍率（Phase2
        /// 侧后+20%）已由更上层调用方（<see cref="FoundryOutpostRegion.TryAttackEnemy"/>）用真实
        /// attackerPosition 算好烘进 <paramref name="damage"/>，本方法不重复计算方向；对空形态的“核心暴露”倍率在这里乘（与方向无关）。</summary>
        public static (bool success, string reason) ApplyDamage(CampaignState state, RegionEnemyRecord target, float damage)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !IsInitialized(region))
            {
                return (false, "boss-not-initialized");
            }
            CoreBossState s = GetState(region);

            if (target.EnemyTypeId == FoundryOutpostLayout.BossNodeTypeId)
            {
                if (s != CoreBossState.Shielded)
                {
                    return (false, "node-invulnerable-in-state:" + s);
                }
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
                if (target.Health <= 0f && target.IsAlive)
                {
                    target.IsAlive = false;
                    int shieldsRemaining = 2 - CountDestroyedNodes(state);
                    Log.Info($"[FoundryOutpostCoreBoss] 供能节点 {target.EnemyInstanceId} 已摧毁：护盾 {shieldsRemaining}/2。");
                    if (shieldsRemaining <= 0)
                    {
                        TryTransition(state, region, CoreBossState.Phase1);
                    }
                }
                return (true, null);
            }

            if (target.EnemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
            {
                if (s != CoreBossState.Phase1 && s != CoreBossState.Phase2 && s != CoreBossState.AntiAir && s != CoreBossState.Berserk)
                {
                    return (false, "core-invulnerable-in-state:" + s);
                }
                float dealt = Mathf.Max(0f, damage) * (s == CoreBossState.AntiAir ? ExposedDamageMult : 1f);
                if (s == CoreBossState.Phase2)
                {
                    // FG9-FAC-02：阶段二的伤害不会把主核心打到阶段三门槛以下——到门槛就进入对空形态（多出的伤害作废，阶段三一定会出现）。
                    float floor = target.MaxHealth * Phase3HealthFraction;
                    // 修复轮：阶段二开局就低于门槛（阶段一一击打穿）时不把耐久“补”回门槛——取 min(门槛, 当前) 作下限。
                    target.Health = Mathf.Max(Mathf.Min(floor, target.Health), target.Health - dealt);
                    if (target.Health <= floor)
                    {
                        TriggerAntiAir(state, region);
                    }
                    return (true, null);
                }
                target.Health = Mathf.Max(0f, target.Health - dealt);
                if (s == CoreBossState.Phase1 &&
                    target.Health <= target.MaxHealth * FoundryOutpostLayout.Phase1TransitionHealthFraction)
                {
                    TriggerTransition(state, region);
                }
                else if ((s == CoreBossState.AntiAir || s == CoreBossState.Berserk) && target.Health <= 0f && target.IsAlive)
                {
                    target.IsAlive = false;
                    Economy.TestRangeService.NoteEnemyDefeated(state, target.EnemyTypeId); // FG5-RND-03：击败主核心解锁护盾靶（类型即敌人表 ID）。
                    TryTransition(state, region, CoreBossState.Destroyed);
                    DropCoreDataOnce(state, region);
                    GrantRewardsOnce(state, region, target);
                }
                return (true, null);
            }

            return (false, "not-a-boss-target");
        }

        private static int CountDestroyedNodes(CampaignState state)
        {
            int count = 0;
            RegionEnemyRecord n1 = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode1Id);
            RegionEnemyRecord n2 = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode2Id);
            if (n1 != null && !n1.IsAlive) count++;
            if (n2 != null && !n2.IsAlive) count++;
            return count;
        }

        /// <summary>Phase1→Transition：5秒计时+一次性召唤维修机（DEMO-CONTENT-LOCK.md"只召一台维修机"
        /// 字面要求，<see cref="RegionRecord.CoreTransitionRepairBotSummoned"/> 幂等标记）。</summary>
        private static void TriggerTransition(CampaignState state, RegionRecord region)
        {
            if (!TryTransition(state, region, CoreBossState.Transition))
            {
                return;
            }
            region.CoreTransitionEndAtPlaySeconds = state.PlaySeconds + FoundryOutpostLayout.TransitionDurationSeconds;
            if (!region.CoreTransitionRepairBotSummoned)
            {
                SummonTransitionRepairBot(state);
                region.CoreTransitionRepairBotSummoned = true;
            }
        }

        /// <summary>FG9-FAC-02：Phase2→对空形态：计时 15 秒（爆发窗口）。</summary>
        private static void TriggerAntiAir(CampaignState state, RegionRecord region)
        {
            if (!TryTransition(state, region, CoreBossState.AntiAir))
            {
                return;
            }
            region.CoreAntiAirEndAtPlaySeconds = state.PlaySeconds + AntiAirSeconds;
            Log.Info($"[FoundryOutpostCoreBoss] 阶段三·对空形态：所有炮口转向天空，核心暴露 {AntiAirSeconds:F0} 秒（受伤 ×{ExposedDamageMult:0.##}）。");
        }

        /// <summary>编成表 boss_summon 行（铸造维修机）——召唤出的维修机与外围驻守的那台是同一行敌人表、同一个读法，
        /// 不新写第二套维修机行为。</summary>
        private static void SummonTransitionRepairBot(CampaignState state)
        {
            // 防重入：正常流程受 CoreTransitionRepairBotSummoned 保护；SeedEncounters 本身对已存在的实例幂等（按编成表 boss_summon 全部行，不看写死的实例 ID）。
            RegionEnemySites.SeedEncounters(state, FoundryOutpostRegion.RegionId, EnemyDefs.ConditionBossSummon);
            Log.Info("[FoundryOutpostCoreBoss] Transition：已召唤一台维修机支援（一次性，不会再召第二台）。");
        }

        /// <summary>"主核心毁灭只掉一次核心数据盒"——与既有关键物同一生命周期
        /// （<see cref="RegionQuestItemState"/>），复用 <see cref="FoundryOutpostRegion.SpawnQuestItemOnGround"/>
        /// 不新造第二套关键物落地逻辑。</summary>
        private static void DropCoreDataOnce(CampaignState state, RegionRecord region)
        {
            if (region.CoreDataDropped)
            {
                return;
            }
            region.CoreDataDropped = true;
            FoundryOutpostRegion.SpawnQuestItemOnGround(state, FoundryOutpostLayout.CoreDataContentId, FoundryOutpostLayout.MainCore.Position);
            Log.Info("[FoundryOutpostCoreBoss] 主核心已摧毁：核心数据盒已掉落（唯一一次，Destroyed后不再新增任何敌方生产）。");
        }

        /// <summary>
        /// FG9-FAC-02（FGR-FAC-006“奖励：固定的核心固件和关键材料，外加一个三选一数据包”；FG09 第 5 章）：只发一次。
        /// 敌人表 components 列 = 固定核心固件（过载、电容蓄力，进固件存放，满了进待领取）；一个三选一数据包（来源 boss）；
        /// 关键材料“熔炉心”随领地平定 → 主线“熄灭熔炉”的任务奖励进核心保管库（leaderRule foundry_core，FG8-QST-01）。
        /// </summary>
        private static void GrantRewardsOnce(CampaignState state, RegionRecord region, RegionEnemyRecord core)
        {
            if (region.CoreRewardsGranted)
            {
                return;
            }
            region.CoreRewardsGranted = true;
            RewardGrants++;
            Vector2 at = FoundryOutpostLayout.MainCore.Position;
            if (core != null && EnemyDefs.TryGet(core.EnemyTypeId, out GameConfig.fg.MechEnemy row) && !string.IsNullOrEmpty(row.Components) && row.Components != EnemyDefs.None)
            {
                string[] fw = row.Components.Split(',');
                for (int i = 0; i < fw.Length; i++)
                {
                    string id = fw[i].Trim();
                    if (Signal.FirmwareKinds.IsFirmware(id))
                    {
                        Primitive.PrimitiveInventory.GrantBossFirmware(state, "boss:" + FoundryOutpostRegion.RegionId + ":" + i, id);
                    }
                }
            }
            int holder = Economy.LootPickService.NearestMachine(FoundryOutpostRegion.RegionId, at, 200f, 0);
            Economy.LootPickService.Grant(state, Economy.LootPickService.SourceBoss, EnemyDefs.FactionFoundry, holder, FoundryOutpostRegion.RegionId, at);
            NotificationCenter.Post("foundry_boss", GameText.Get("boss.foundry.notify.defeated"), new Vector3(at.x, 0f, at.y));
        }

        /// <summary>Phase2"侧后+20%"倍率——只在目标是主核心且当前恰为 Phase2（FG9-FAC-02：或狂暴）时才可能大于 1，其余
        /// 一律 1（不影响节点/其它阶段伤害）。唯一调用点 <see cref="FoundryOutpostRegion.TryAttackEnemy"/>，
        /// 在真正结算伤害之前用真实 attackerPosition 算好，再传给 <see cref="CannonCombat.TryFire"/>/
        /// 直接乘进普通武器伤害——两条路径复用同一份倍率计算，不允许出现"直控算一套、AI算另一套"。</summary>
        public static float ComputeDamageMultiplier(CampaignState state, RegionEnemyRecord target, Vector2 attackerPosition)
        {
            if (target == null || target.EnemyTypeId != FoundryOutpostLayout.BossCoreTypeId)
            {
                return 1f;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (!HasBackHitBonus(GetState(region)))
            {
                return 1f;
            }
            return IsBackHit(target.Position, attackerPosition) ? 1f + FoundryOutpostLayout.Phase2BackHitBonusPct : 1f;
        }

        /// <summary>侧后加成生效的阶段：阶段二（Demo）与狂暴（散热口仍暴露）。</summary>
        public static bool HasBackHitBonus(CoreBossState s) => s == CoreBossState.Phase2 || s == CoreBossState.Berserk;

        public static bool IsBackHit(Vector2 corePosition, Vector2 attackerPosition)
        {
            Vector2 toAttacker = attackerPosition - corePosition;
            if (toAttacker.sqrMagnitude < 1e-6f)
            {
                return false; // 贴脸算正面，不给侧后奖励留歧义（护甲机同类判定的反向语义：弱点不是默认态）。
            }
            float cosAngle = Vector2.Dot(FoundryOutpostLayout.MainCoreFacing.normalized, toAttacker.normalized);
            float cosHalf = Mathf.Cos(FoundryOutpostLayout.MainCoreFrontalHalfAngleDeg * Mathf.Deg2Rad);
            return cosAngle < cosHalf; // 不在正面锥角内＝侧后（散热口暴露方向）。
        }

        /// <summary>ER7-FAIL-01 STORY-EXECUTION-CARDS.md 第2条："Boss Phase2撤离/全灭/退出下次从Boss前
        /// 自动档恢复主核心与节点状态，不允许跨出击磨血或重复阶段奖励；已安全结算的外围回收和蓝图仍
        /// 保留"——唯一调用点 <see cref="ExpeditionReturnService.TryConfirmEvacuation"/>/
        /// <see cref="ExpeditionReturnService.TryConfirmAbandon"/>，在"撤离事务已提交"之后立即调用。
        /// 只重置 Boss 专属字段（三条 RegionEnemyRecord 的 HP/存活+ <see cref="RegionRecord.CoreState"/>
        /// 等 FSM 字段），不碰蓝图/机队/仓储/关键物——"从 Boss 前自动档恢复"字面上等价于把这些字段
        /// 恢复成 <see cref="SaveReason.BossEngageEnter"/> 那次存档时的样子（该次存档正是在
        /// <see cref="EnsureInitialized"/> 之前打的，此时 Boss 尚是 Locked），不需要真的做一次磁盘
        /// 读档来达到同样效果——比对全量回滚（<see cref="ExpeditionDepartureService"/> 那种"回出发前档"）
        /// 更精确：不会把本次已经安全结算的外围战利品/蓝图/机队变更也一并撤销。
        ///
        /// Destroyed 后不重置（"不允许重复阶段奖励"的反面——已经拿到的胜利不会被撤离动作抹掉，核心
        /// 数据盒不管有没有装车都原样留在地面/货舱，供下次再来捡，同既有关键物"未拾取则留在场上"
        /// 生命周期，不特殊处理）。FG9-FAC-02：记“上次止步于第几阶段”（通知 + HUD），狂暴时派出的精英一并撤走。</summary>
        public static void ResetToPreBossState(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null || !IsInitialized(region) || GetState(region) == CoreBossState.Destroyed)
            {
                return;
            }

            CoreBossState before = GetState(region);
            // 一次性召唤物（编成表 boss_summon / boss_wave 行）撤走。
            var summoned = new HashSet<string>(EnemyDefs.Encounters(FoundryOutpostRegion.RegionId, EnemyDefs.ConditionBossSummon).Select(enc => enc.Id));
            foreach (GameConfig.fg.EnemyEncounter enc in EnemyDefs.Encounters(FoundryOutpostRegion.RegionId, EnemyDefs.ConditionBossWave))
            {
                summoned.Add(enc.Id);
            }
            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Where(e => e != null && !summoned.Contains(e.EnemyInstanceId))
                .ToArray();
            ResetBossRecord(state, FoundryOutpostLayout.CoreNode1Id);
            ResetBossRecord(state, FoundryOutpostLayout.CoreNode2Id);
            ResetBossRecord(state, FoundryOutpostLayout.MainCoreId);

            region.CoreState = null; // Locked——下次越线重新 EnsureInitialized，天然拿到满血全新一轮。
            region.CoreTransitionEndAtPlaySeconds = 0f;
            region.CoreTransitionRepairBotSummoned = false;
            region.CoreLockoutWarnAtPlaySeconds = 0f;
            region.CoreLockoutActive = false;
            ClearPhase3Fields(region);
            region.CoreLastReachedState = before.ToString();
            // CoreDataDropped 不重置于此——本方法只在"未 Destroyed"时执行（上面已早退），Destroyed
            // 之后才可能为真，本分支内它必然仍是 false，写不写都一样，保留默认值不做无意义赋值。

            NotificationCenter.Post("foundry_boss", GameText.Format("boss.foundry.notify.retry", PhaseName(before)));
            Log.Info($"[FoundryOutpostCoreBoss] 未完成的 Boss 尝试已重置（原状态 {before}）：两节点+主核心" +
                "恢复满血，下次进入核心分区重新开始，不跨出击磨血。");
        }

        private static void ResetBossRecord(CampaignState state, string instanceId)
        {
            RegionEnemyRecord record = FoundryOutpostRegion.FindEnemy(state, instanceId);
            if (record == null || !EnemyDefs.TryGet(record.EnemyTypeId, out GameConfig.fg.MechEnemy row))
            {
                return;
            }
            float maxHealth = EnemyDefs.SeedHealth(state, row); // FG9-DATA-01：敌人表 × 难度倍率（与播种同一口径）。
            record.Health = maxHealth;
            record.MaxHealth = maxHealth;
            record.IsAlive = true;
            record.CycleCooldownRemaining = 0f;
        }

        /// <summary>每帧驱动——Transition 计时→自动进 Phase2；Phase2 区域封锁预警→生效；对空形态计时→狂暴（派出精英一次）。
        /// <see cref="CoreBossState.Destroyed"/> 后整体 no-op（"停止新增敌方生产"，
        /// 不会再召维修机/不会再触发任何转换）。唯一调用点 <see cref="FoundryOutpostController.SimStep"/>。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !IsInitialized(region) || dt <= 0f)
            {
                return;
            }
            CoreBossState s = GetState(region);
            if (s == CoreBossState.Destroyed)
            {
                return;
            }

            if (s == CoreBossState.Transition)
            {
                if (state.PlaySeconds >= region.CoreTransitionEndAtPlaySeconds)
                {
                    if (TryTransition(state, region, CoreBossState.Phase2))
                    {
                        region.CoreLockoutWarnAtPlaySeconds = state.PlaySeconds + FoundryOutpostLayout.CoreLockoutWarnSeconds;
                        Log.Info("[FoundryOutpostCoreBoss] Phase2 区域封锁预警：2秒后核心分区入口锁死。");
                        // DEBT-ER7CORE01-01：此前只有 HUD 文字，没看 HUD 的玩家收不到预警——补上可听警报。
                        Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BossLockoutWarning, FoundryOutpostLayout.CoreGate.Position);
                    }
                }
                return; // Transition 期间不攻击（DEMO-CONTENT-LOCK.md 字面要求）。
            }

            if (s == CoreBossState.AntiAir && state.PlaySeconds >= region.CoreAntiAirEndAtPlaySeconds)
            {
                // FG9-FAC-02：爆发窗口结束——恢复并狂暴；精英攻城重甲（编成表 boss_wave）从核心区入口一侧压上来（每次挑战一次）。
                if (TryTransition(state, region, CoreBossState.Berserk) && !region.CoreBerserkWaveSummoned)
                {
                    region.CoreBerserkWaveSummoned = true;
                    RegionEnemySites.SeedEncounters(state, FoundryOutpostRegion.RegionId, EnemyDefs.ConditionBossWave);
                }
            }

            if (GetState(region) != CoreBossState.Transition && !region.CoreLockoutActive && region.CoreLockoutWarnAtPlaySeconds > 0f
                && state.PlaySeconds >= region.CoreLockoutWarnAtPlaySeconds)
            {
                region.CoreLockoutActive = true;
                Log.Info("[FoundryOutpostCoreBoss] 区域封锁已生效：核心分区不能再退回外围。");
                Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.BossLockoutActive, FoundryOutpostLayout.CoreGate.Position);
            }

            // FG0-ARCH-03：主核心的开火（阶段一 / 二 / 狂暴，射程内最近的可见机器，冷却；对空形态只打空中目标）在战斗内核里执行——
            // 本方法只推进阶段状态机；阶段变化由 CombatDemoContent.SyncBossFlags 同步成内核里“可开火 / 可伤 / 侧后加成 / 对空 / 狂暴武器”。
        }

        /// <summary>
        /// FG9-FAC-02（FGR-FAC-006 场地边界）：每个模拟步由控制器传入“本地点还有活着的机器”“有机器在核心区里”。首领战进行中、机器全部离开核心区
        /// 累计超过 <see cref="ArenaLeaveSeconds"/> 秒 → 复位并恢复当前阶段耐久（一次；回到核心区重新计时）。返回本步是否复位。O(1)。
        /// </summary>
        public static bool TickArena(CampaignState state, float dt, bool anyMachineAlive, bool anyMachineInArena)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !IsFighting(region) || dt <= 0f)
            {
                return false;
            }
            if (!anyMachineAlive || anyMachineInArena)
            {
                region.CoreArenaOutsideSeconds = 0f; // 没有机器（全灭由全灭流程处理）或有机器在场：不计时
                return false;
            }
            region.CoreArenaOutsideSeconds += dt;
            if (region.CoreArenaOutsideSeconds < ArenaLeaveSeconds)
            {
                return false;
            }
            region.CoreArenaOutsideSeconds = 0f;
            CoreBossState s = GetState(region);
            if (s == CoreBossState.Shielded)
            {
                RestoreNode(state, FoundryOutpostLayout.CoreNode1Id);
                RestoreNode(state, FoundryOutpostLayout.CoreNode2Id);
            }
            else
            {
                RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.MainCoreId);
                if (core != null && core.IsAlive && region.CorePhaseStartHealth > core.Health)
                {
                    core.Health = Mathf.Min(core.MaxHealth, region.CorePhaseStartHealth);
                }
            }
            region.CoreArenaResets++;
            ArenaResets++;
            Feedback.FeedbackCues.RaiseLocated(Feedback.FeedbackCueId.FoundryBossArenaReset, FoundryOutpostLayout.MainCore.Position);
            NotificationCenter.Post("foundry_boss", GameText.Format("boss.foundry.notify.arena_reset", Mathf.RoundToInt(ArenaLeaveSeconds).ToString(), PhaseName(s)),
                new Vector3(FoundryOutpostLayout.MainCore.Position.x, 0f, FoundryOutpostLayout.MainCore.Position.y));
            Log.Info($"[FoundryOutpostCoreBoss] 场地边界：机器全部离开核心区超过 {ArenaLeaveSeconds:F0} 秒，主核心复位并恢复{s}阶段耐久。");
            return true;
        }

        /// <summary>核心区（首领场地）：越过核心区大门的那条线（与进场判定同一条）。</summary>
        public static bool IsInArena(Vector2 position) => position.y > FoundryOutpostLayout.CoreGateBlockLineY;

        private static void RestoreNode(CampaignState state, string id)
        {
            RegionEnemyRecord n = FoundryOutpostRegion.FindEnemy(state, id);
            if (n != null && n.IsAlive)
            {
                n.Health = n.MaxHealth;
            }
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
