using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>洗礼塔的阶段（存档稳定码：只追加，不重排）。</summary>
    public enum BaptismPhase
    {
        Dormant = 0,
        /// <summary>阶段一·灌注：4 座泵站轮流往场地里灌油、酸、水。</summary>
        Fill = 1,
        /// <summary>阶段二·反应：反应塔主动点燃油 / 酸、给水通电；召来一波澄净单位。</summary>
        React = 2,
        /// <summary>阶段三·泄压：过热循环——封闭（减伤、冻不住）→ 预警 → 泄压（核心暴露、增伤、能冻住）。</summary>
        Vent = 3,
        Defeated = 4,
    }

    /// <summary>
    /// FG10-BOSS-01（FG09 第 6 章“首领：洗礼塔”，FGR-FAC-006；FGT-FAC-003）：洗礼塔首领战的状态机——<see cref="TryTransition"/> 是阶段的唯一写入口，
    /// 复位（全员离开 / 全灭）走 <see cref="ResetToPreFight"/>。场地是星球表面上的湖心平台（规划层锚点 baptism_platform，按种子放在澄净领地里），
    /// 首领在星球战斗内核里就地展开（与领地据点同一个内核，FG17“湖心平台（地表）”），不切表面。
    ///
    /// - 部件：反应塔 1 座（敌人表 boss 行：耐久在这里分阶段结算，external_health；固定环形喷淋架 = 枢轴喷灌同一套内核结算）+ 泵站 4 座
    ///   （敌人表澄净结构行，按表里的顺序轮流取；内核耐久，被打掉就停掉它的流体）。都是 <see cref="RegionEnemyRecord"/>（区域 = 家园 / 星球），随快照与键表进存档。
    /// - 开战：澄净领地已在地图上显示、有己方机器进入场地半径 → 先自动存档（BossEngageEnter，FGR-FAC-006“全灭后从首领战前的自动存档重试”）→ 播种部件 → 灌注。
    /// - 灌注（全程）：每 fill_seconds 轮到一座存活泵站，落点先预警 fill_warn_seconds（≥ 0.8），再落下一片中立流体（战斗内核布区：敌我站进去都沾油 / 酸 / 湿润，
    ///   流体与双方的标签按反应规则起反应）。落点按（种子, 第几次挑战, 第几片）派生。
    /// - 反应（耐久 ≤ phase2）：开局召来一波澄净单位；每 react_seconds 挑一片“站着最多己方机器”的流体，预警 react_warn_seconds 后盖上一块火（油 / 酸）或电（水）——
    ///   站在里面的单位（包括它自己的小兵）当场起反应：借力打力是有意设计（卡片负向）。
    /// - 泄压（耐久 ≤ phase3）：循环——封闭（伤害 × sealed_damage_mult，冻不住）→ 预警 → 泄压（伤害 × vent_damage_mult，能冻住：先冻再烧打出热震）。
    /// - 复位：己方机器全部离开场地（含全灭）满 arena_leave_seconds → 部件回满、波次撤走、阶段 = 沉寂，记“上次止步”；通知写明可以从首领战前的自动存档重试。
    /// - 击败：固定奖励（敌人表 components 第一个 = 核心固件“反应增幅”，第二个 = 此前没拿到才给的流体类固件）、一个三选一数据包、澄净领地平定（MarkLeaderDefeated → 主线“反应核”）。
    /// 每步 O(1)；开战 / 场地检查每游戏秒一次 O(机器数)；挑反应目标每 react_seconds 一次 O(机器 × 流体)。与是否被观察无关（FGR-BASE-021）。
    /// </summary>
    public static class BaptismTowerBoss
    {
        public const string AnchorId = "baptism_platform";
        public const string TowerInstanceId = "clarity.boss.tower";
        public const int PumpCount = 4;
        public const string NotifyType = "clarity_boss";
        /// <summary>阶段二召来的单位在战斗内核里的编组键（不是突袭队伍；击毁按野外掉落结算）。落在迎战键区间 [200000, 215000) 之外，
        /// 且小于击毁事件经 float 传外部键的精确上限（约 21.5 万，CombatSite.Siege.cs）——否则种类序号解码错位。攻城服务清理无主队伍时由 <see cref="OwnsKey"/> 认领。</summary>
        public const int WaveKey = 215100;

        private static readonly Dictionary<BaptismPhase, BaptismPhase> LegalNext = new Dictionary<BaptismPhase, BaptismPhase>
        {
            [BaptismPhase.Dormant] = BaptismPhase.Fill,
            [BaptismPhase.Fill] = BaptismPhase.React,
            [BaptismPhase.React] = BaptismPhase.Vent,
            [BaptismPhase.Vent] = BaptismPhase.Defeated,
        };

        private static string SiteId => HomeValleyLayout.RegionId;

        /// <summary>自检读点。</summary>
        public static int Engages { get; private set; }
        public static int Resets { get; private set; }
        public static int FillsPoured { get; private set; }
        public static int ReactionsTriggered { get; private set; }
        public static int VentsOpened { get; private set; }
        public static int RejectedTransitions { get; private set; }
        public static int RewardGrants { get; private set; }
        /// <summary>开战前自动存档（自检可关：自检里存档目录是临时的，开关只为不改写自检的存档计数）。</summary>
        public static bool SaveOnEngage = true;
        /// <summary>开战已排到本步末尾（整步提交之后才存档开战，防重复排队）。</summary>
        private static bool _engagePending;

        public static void ResetForTests()
        {
            Engages = Resets = FillsPoured = ReactionsTriggered = VentsOpened = RejectedTransitions = RewardGrants = 0;
            SaveOnEngage = true;
            _engagePending = false;
        }

        public static float ArenaRadius => Tuning("boss.clarity.arena_radius", 40f);
        public static float ArenaLeaveSeconds => Tuning("boss.clarity.arena_leave_seconds", 20f);
        public static float PumpRing => Tuning("boss.clarity.pump_ring", 18f);
        public static float FillSeconds => Tuning("boss.clarity.fill_seconds", 6f);
        public static float FillWarnSeconds => Mathf.Max(0.8f, Tuning("boss.clarity.fill_warn_seconds", 1f));
        public static float FillRadius => Tuning("boss.clarity.fill_radius", 6f);
        public static float FillLifeSeconds => Tuning("boss.clarity.fill_life_seconds", 40f);
        public static int FillCap => Mathf.Max(1, Mathf.RoundToInt(Tuning("boss.clarity.fill_cap", 8f)));
        public static float ReactSeconds => Tuning("boss.clarity.react_seconds", 8f);
        public static float ReactWarnSeconds => Mathf.Max(0.8f, Tuning("boss.clarity.react_warn_seconds", 1.5f));
        public static float ReactZoneSeconds => Tuning("boss.clarity.react_zone_seconds", 2f);
        public static float ReactDps => Tuning("boss.clarity.react_dps", 8f);
        public static float Phase2HpFraction => Mathf.Clamp(Tuning("boss.clarity.phase2_hp_fraction", 0.7f), 0.1f, 0.95f);
        public static float Phase3HpFraction => Mathf.Clamp(Tuning("boss.clarity.phase3_hp_fraction", 0.35f), 0.05f, Phase2HpFraction - 0.01f);
        public static float VentCycleSeconds => Tuning("boss.clarity.vent_cycle_seconds", 16f);
        public static float VentSeconds => Tuning("boss.clarity.vent_seconds", 6f);
        public static float VentWarnSeconds => Mathf.Max(0.8f, Tuning("boss.clarity.vent_warn_seconds", 1f));
        public static float VentDamageMult => Mathf.Max(1f, Tuning("boss.clarity.vent_damage_mult", 2f));
        public static float SealedDamageMult => Mathf.Clamp(Tuning("boss.clarity.sealed_damage_mult", 0.5f), 0.05f, 1f);
        public static int WaveCount => Mathf.Max(0, Mathf.RoundToInt(Tuning("boss.clarity.wave_count", 4f)));

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static ClarityBossRecord Record(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Factions?.ClarityBoss == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Factions?.ClarityBoss;
        }

        public static BaptismPhase PhaseOf(CampaignState s) => (BaptismPhase)(Record(s)?.Phase ?? 0);

        public static bool IsEngaged(CampaignState s)
        {
            BaptismPhase p = PhaseOf(s);
            return p == BaptismPhase.Fill || p == BaptismPhase.React || p == BaptismPhase.Vent;
        }

        public static bool IsDefeated(CampaignState s) => PhaseOf(s) == BaptismPhase.Defeated;

        public static string PhaseName(BaptismPhase p) => GameText.Get(p switch
        {
            BaptismPhase.Fill => "boss.clarity.phase.fill",
            BaptismPhase.React => "boss.clarity.phase.react",
            BaptismPhase.Vent => "boss.clarity.phase.vent",
            BaptismPhase.Defeated => "boss.clarity.phase.defeated",
            _ => "boss.clarity.phase.dormant",
        });

        /// <summary>湖心平台（场地圆心，星球格坐标）。v1 / v2 世界没有这个锚点返回 false。</summary>
        public static bool TryArena(CampaignState s, out Vector2 center)
        {
            center = Vector2.zero;
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return false;
            }
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a != null && a.Id == AnchorId)
                {
                    center = new Vector2(a.OriginX, a.OriginY);
                    return true;
                }
            }
            return false;
        }

        public static string PumpInstanceId(int i) => "clarity.boss.pump." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static int PumpIndexOf(string instanceId)
        {
            for (int i = 0; i < PumpCount; i++)
            {
                if (instanceId == PumpInstanceId(i))
                {
                    return i;
                }
            }
            return -1;
        }

        public static bool IsBossPart(string instanceId) => instanceId == TowerInstanceId || PumpIndexOf(instanceId) >= 0;

        /// <summary>攻城服务清理“没有主人的队伍”时问：这个队伍键是不是洗礼塔召来、仍在战斗中的那一波。</summary>
        public static bool OwnsKey(CampaignState s, int key)
        {
            ClarityBossRecord r = key == WaveKey ? Record(s) : null;
            return r != null && r.WaveSpawned && IsEngaged(s);
        }

        public static Vector2 PumpPosition(Vector2 center, int i)
        {
            float a = (45f + 90f * i) * Mathf.Deg2Rad;
            return center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * PumpRing;
        }

        /// <summary>反应塔的敌人表行：澄净阵营、职能 boss（按表识别，不按 ID）。</summary>
        public static MechEnemy TowerRow()
        {
            foreach (MechEnemy row in EnemyDefs.Rows)
            {
                if (row != null && row.Faction == EnemyDefs.FactionClarity && row.Role == EnemyDefs.RoleBoss)
                {
                    return row;
                }
            }
            return null;
        }

        /// <summary>泵站的敌人表行：澄净阵营、职能 structure、带状态标签（= 它灌的流体），按表里的顺序。</summary>
        public static List<MechEnemy> PumpRows()
        {
            var list = new List<MechEnemy>(3);
            foreach (MechEnemy row in EnemyDefs.Rows)
            {
                if (row != null && row.Faction == EnemyDefs.FactionClarity && row.Role == EnemyDefs.RoleStructure
                    && !string.IsNullOrEmpty(row.StatusTag) && row.StatusTag != EnemyDefs.None)
                {
                    list.Add(row);
                }
            }
            return list;
        }

        public static RegionEnemyRecord FindPart(CampaignState s, string instanceId)
        {
            if (s?.RegionEnemies == null || instanceId == null)
            {
                return null;
            }
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e != null && e.EnemyInstanceId == instanceId)
                {
                    return e;
                }
            }
            return null;
        }

        public static RegionEnemyRecord Tower(CampaignState s) => FindPart(s, TowerInstanceId);

        public static RegionEnemyRecord Pump(CampaignState s, int i) => FindPart(s, PumpInstanceId(i));

        public static int PumpsAlive(CampaignState s)
        {
            int n = 0;
            for (int i = 0; i < PumpCount; i++)
            {
                if (Pump(s, i)?.IsAlive == true)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>第 i 座泵站灌的流体（状态标签 ID）。</summary>
        public static string FluidOf(CampaignState s, int i)
        {
            RegionEnemyRecord rec = Pump(s, i);
            return rec != null && EnemyDefs.TryGet(rec.EnemyTypeId, out MechEnemy row) ? row.StatusTag : null;
        }

        /// <summary>泄压循环里的当前段：0 封闭 / 1 预警 / 2 泄压（只在阶段三有意义）。</summary>
        public static int VentStateAt(ClarityBossRecord r, long tick)
        {
            long cycle = Math.Max(3, GameClock.TicksFor(VentCycleSeconds));
            long vent = Math.Max(1, GameClock.TicksFor(VentSeconds));
            long warn = Math.Max(1, GameClock.TicksFor(VentWarnSeconds));
            long t = Math.Max(0, tick - r.VentCycleTick) % cycle;
            return t >= cycle - vent ? 2 : t >= cycle - vent - warn ? 1 : 0;
        }

        /// <summary>泄压循环里离下一段还有几游戏秒。</summary>
        public static float VentSecondsLeft(ClarityBossRecord r, long tick)
        {
            long cycle = Math.Max(3, GameClock.TicksFor(VentCycleSeconds));
            long vent = Math.Max(1, GameClock.TicksFor(VentSeconds));
            long warn = Math.Max(1, GameClock.TicksFor(VentWarnSeconds));
            long t = Math.Max(0, tick - r.VentCycleTick) % cycle;
            long edge = t >= cycle - vent ? cycle : t >= cycle - vent - warn ? cycle - vent : cycle - vent - warn;
            return (edge - t) / (float)Math.Max(1, GameClock.StepHz);
        }

        /// <summary>反应塔此刻受到的伤害倍率（阶段三封闭 / 泄压不同）。</summary>
        public static float DamageMult(CampaignState s)
        {
            ClarityBossRecord r = Record(s);
            if (r == null || (BaptismPhase)r.Phase != BaptismPhase.Vent)
            {
                return 1f;
            }
            return VentStateAt(r, GameClock.Ticks) == 2 ? VentDamageMult : SealedDamageMult;
        }

        // ─────────────────────────────── 阶段 ───────────────────────────────

        public static bool TryTransition(CampaignState s, BaptismPhase to)
        {
            ClarityBossRecord r = Record(s);
            BaptismPhase from = (BaptismPhase)(r?.Phase ?? 0);
            if (r == null || !LegalNext.TryGetValue(from, out BaptismPhase next) || next != to)
            {
                RejectedTransitions++;
                Log.Error($"[BaptismTowerBoss] 非法阶段跳转 {from} → {to}");
                return false;
            }
            r.Phase = (int)to;
            r.BestPhase = Math.Max(r.BestPhase, (int)to);
            long now = GameClock.Ticks;
            Vector2 at = PartPosition(s, TowerInstanceId);
            CombatSite site = CombatSites.Get(SiteId);
            switch (to)
            {
                case BaptismPhase.React:
                    r.NextReactTick = now + GameClock.TicksFor(ReactSeconds * 0.5f);
                    r.ReactFill = -1;
                    Announce(s, to, at, "boss.clarity.notify.phase_react");
                    if (site != null && !r.WaveSpawned && TryArena(s, out Vector2 c))
                    {
                        SpawnWave(s, site, r, c);
                    }
                    break;
                case BaptismPhase.Vent:
                    r.VentCycleTick = now;
                    r.VentState = 0;
                    Announce(s, to, at, "boss.clarity.notify.phase_vent");
                    SyncFlags(site, s);
                    break;
                case BaptismPhase.Defeated:
                    r.DefeatedTick = now;
                    r.ReactFill = -1;
                    r.FillPump = -1;
                    site?.RemoveEnemy(TowerInstanceId);
                    // 首领倒下：泵站停摆撤出、召来的单位撤走（之后不会再有“泵站被摧毁”的提示，也不留没人管的敌人）。
                    if (site != null)
                    {
                        site.DespawnSiegeGroup(WaveKey);
                        for (int i = 0; i < PumpCount; i++)
                        {
                            site.RemoveEnemy(PumpInstanceId(i));
                            RegionEnemyRecord pump = Pump(s, i);
                            if (pump != null)
                            {
                                pump.IsAlive = false;
                            }
                        }
                    }
                    r.WaveUnits = Array.Empty<int>();
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossDestroyed, at, GameText.Get("boss.clarity.notify.defeated"));
                    GrantRewardsOnce(s, at);
                    break;
            }
            return true;
        }

        private static void Announce(CampaignState s, BaptismPhase to, Vector2 at, string detailKey)
        {
            string text = GameText.Format("boss.clarity.notify.phase", PhaseName(to));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, text);
            NotificationCenter.Post(NotifyType, text + "\n" + GameText.Get(detailKey), new Vector3(at.x, 0f, at.y));
        }

        // ─────────────────────────────── 开战 / 复位 ───────────────────────────────

        /// <summary>开战：先自动存档（首领战前），播种部件（满血），阶段 → 灌注。已在战斗中 / 已击败返回 false。</summary>
        public static bool Engage(CampaignState s)
        {
            ClarityBossRecord r = Record(s);
            CombatSite site = CombatSites.Get(SiteId);
            if (r == null || (BaptismPhase)r.Phase != BaptismPhase.Dormant || site == null || site.IsDisposed || !TryArena(s, out Vector2 center))
            {
                return false;
            }
            if (TowerRow() == null || PumpRows().Count == 0)
            {
                EnemyDefs.ReportEncounterProblem(SiteId, "baptism_tower");
                return false;
            }
            r.RetrySaved = false;
            if (SaveOnEngage)
            {
                SaveResult save = CampaignAutoSaveService.SaveAuto(SaveReason.BossEngageEnter);
                r.RetrySaved = save.Success;
                if (!save.Success)
                {
                    // 星球上没有“门”可以拦：照常开战，复位时如实说明这一次没有首领战前的存档可回。
                    Log.Warning($"[BaptismTowerBoss] 首领战前自动存档未成功（{save.Outcome}），照常开战。");
                }
            }
            r.Attempt++;
            r.EngagedTick = GameClock.Ticks;
            r.NextFillTick = GameClock.Ticks + GameClock.TicksFor(FillWarnSeconds);
            r.FillPump = -1;
            r.ReactFill = -1;
            r.Fills = 0;
            r.PumpCursor = 0;
            r.Reactions = 0;
            r.OutsideSinceTick = -1;
            r.WaveSpawned = false;
            r.WaveUnits = Array.Empty<int>();
            r.Active = Array.Empty<ClarityBossFill>();
            SeedParts(s, site, center);
            if (!TryTransition(s, BaptismPhase.Fill))
            {
                return false;
            }
            Engages++;
            NotificationCenter.Post(NotifyType, GameText.Get("boss.clarity.notify.engaged"), new Vector3(center.x, 0f, center.y));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, center, GameText.Format("boss.clarity.notify.phase", PhaseName(BaptismPhase.Fill)));
            float r2 = ArenaRadius * ArenaRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == SiteId && (LivePos(m) - center).sqrMagnitude <= r2)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.BossParticipation);
                }
            }
            GuidanceHooks.Raise("clarity.first_boss");
            return true;
        }

        /// <summary>
        /// 全员离开场地 / 全灭、首领还没被击败 → 回到开战前：部件回满、召来的单位撤走、阶段 = 沉寂，记下“上次止步于第几阶段”。
        /// 不碰机队 / 仓储 / 已结算的战利品；完整重试由开战前的自动存档提供（通知写明）。场上已经灌下的流体照常到期干涸。
        /// </summary>
        public static bool ResetToPreFight(CampaignState s)
        {
            ClarityBossRecord r = Record(s);
            BaptismPhase p = (BaptismPhase)(r?.Phase ?? 0);
            if (r == null || p == BaptismPhase.Dormant || p == BaptismPhase.Defeated)
            {
                return false;
            }
            CombatSite site = CombatSites.Get(SiteId);
            r.LastReachedPhase = (int)p;
            if (site != null)
            {
                site.DespawnSiegeGroup(WaveKey);
                site.RemoveEnemy(TowerInstanceId);
                for (int i = 0; i < PumpCount; i++)
                {
                    site.RemoveEnemy(PumpInstanceId(i));
                }
            }
            RestorePart(s, TowerInstanceId);
            for (int i = 0; i < PumpCount; i++)
            {
                RestorePart(s, PumpInstanceId(i));
            }
            r.Phase = (int)BaptismPhase.Dormant;
            r.FillPump = -1;
            r.ReactFill = -1;
            r.OutsideSinceTick = -1;
            r.WaveSpawned = false;
            r.WaveUnits = Array.Empty<int>();
            r.Active = Array.Empty<ClarityBossFill>();
            Resets++;
            string text = GameText.Format(r.RetrySaved ? "boss.clarity.notify.retry" : "boss.clarity.notify.retry_nosave", PhaseName(p));
            if (TryArena(s, out Vector2 c))
            {
                FeedbackCues.RaiseLocated(FeedbackCueId.FoundryBossArenaReset, c, text);
            }
            NotificationCenter.Post(NotifyType, text);
            Log.Info($"[BaptismTowerBoss] 首领复位：止步于 {p}。");
            return true;
        }

        private static void SeedParts(CampaignState s, CombatSite site, Vector2 center)
        {
            MechEnemy tower = TowerRow();
            List<MechEnemy> pumps = PumpRows();
            EnsurePart(s, site, TowerInstanceId, tower, center);
            for (int i = 0; i < PumpCount; i++)
            {
                EnsurePart(s, site, PumpInstanceId(i), pumps[i % pumps.Count], PumpPosition(center, i));
            }
            SyncFlags(site, s);
        }

        private static void EnsurePart(CampaignState s, CombatSite site, string instanceId, MechEnemy row, Vector2 at)
        {
            RegionEnemyRecord rec = FindPart(s, instanceId);
            if (rec == null)
            {
                rec = new RegionEnemyRecord { EnemyInstanceId = instanceId, RegionId = SiteId };
                var list = new List<RegionEnemyRecord>(s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()) { rec };
                s.RegionEnemies = list.ToArray();
            }
            float hp = EnemyDefs.SeedHealth(s, row);
            rec.EnemyTypeId = row.Id;
            rec.Position = at;
            rec.Health = hp;
            rec.MaxHealth = hp;
            rec.IsAlive = true;
            site.RemoveEnemy(instanceId);
            Spawn(s, site, rec);
        }

        private static void Spawn(CampaignState s, CombatSite site, RegionEnemyRecord rec)
        {
            if (EnemySpawnBuilder.TryBuild(site, s, rec, null, out CombatSpawn spawn))
            {
                site.SpawnEnemy(rec, spawn);
            }
        }

        private static void RestorePart(CampaignState s, string instanceId)
        {
            RegionEnemyRecord rec = FindPart(s, instanceId);
            if (rec == null || !EnemyDefs.TryGet(rec.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            float hp = EnemyDefs.SeedHealth(s, row);
            rec.Health = hp;
            rec.MaxHealth = hp;
            rec.IsAlive = true;
        }

        /// <summary>开战中、部件不在内核里（新会话没有快照的家园内核等）→ 按记录补回内核。O(部件)。</summary>
        private static void EnsureSpawned(CampaignState s, CombatSite site)
        {
            bool any = false;
            foreach (string id in AllPartIds())
            {
                RegionEnemyRecord rec = FindPart(s, id);
                if (rec != null && rec.IsAlive && !site.TryGetEnemyUnit(id, out _))
                {
                    Spawn(s, site, rec);
                    any = true;
                }
            }
            if (any)
            {
                // 内核是重新建的（没有快照）：之前灌下的流体区域与召来的单位也不在了——记录跟着清空，主动反应不会点到空地。
                ClarityBossRecord r = Record(s);
                if (r != null)
                {
                    r.Active = Array.Empty<ClarityBossFill>();
                    r.ReactFill = -1;
                    r.FillPump = -1;
                    r.WaveUnits = Array.Empty<int>();
                }
                SyncFlags(site, s);
            }
        }

        private static IEnumerable<string> AllPartIds()
        {
            yield return TowerInstanceId;
            for (int i = 0; i < PumpCount; i++)
            {
                yield return PumpInstanceId(i);
            }
        }

        // ─────────────────────────────── 伤害 ───────────────────────────────

        /// <summary>反应塔（external_health）的伤害请求：按阶段与泄压循环结算倍率。耐久打到下一个阶段门槛就停在门槛上并推进阶段
        /// （溢出的伤害作废——一发巨额伤害不会跳过反应 / 泄压阶段，三个阶段都要实打）。</summary>
        public static (bool ok, string reason) ApplyDamage(CampaignState s, RegionEnemyRecord target, float damage)
        {
            ClarityBossRecord r = Record(s);
            if (r == null || target == null || !target.IsAlive || target.EnemyInstanceId != TowerInstanceId)
            {
                return (false, "not-a-boss-part");
            }
            BaptismPhase p = (BaptismPhase)r.Phase;
            if (p == BaptismPhase.Dormant || p == BaptismPhase.Defeated)
            {
                return (false, "dormant");
            }
            float after = target.Health - Mathf.Max(0f, damage) * DamageMult(s);
            switch (p)
            {
                case BaptismPhase.Fill:
                    if (after <= target.MaxHealth * Phase2HpFraction)
                    {
                        target.Health = target.MaxHealth * Phase2HpFraction;
                        TryTransition(s, BaptismPhase.React);
                        return (true, null);
                    }
                    break;
                case BaptismPhase.React:
                    if (after <= target.MaxHealth * Phase3HpFraction)
                    {
                        target.Health = target.MaxHealth * Phase3HpFraction;
                        TryTransition(s, BaptismPhase.Vent);
                        return (true, null);
                    }
                    break;
                case BaptismPhase.Vent:
                    if (after <= 0f)
                    {
                        target.Health = 0f;
                        target.IsAlive = false;
                        TryTransition(s, BaptismPhase.Defeated);
                        return (true, null);
                    }
                    break;
            }
            target.Health = after;
            return (true, null);
        }

        public static string InvulnerableDetail(CampaignState s) => GameText.Get("boss.clarity.invulnerable");

        /// <summary>泵站被击毁（内核耐久打空）：它的流体停止灌入。</summary>
        public static void OnPartKilled(CampaignState s, RegionEnemyRecord e)
        {
            int i = PumpIndexOf(e?.EnemyInstanceId);
            if (i < 0 || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            string text = GameText.Format("boss.clarity.notify.pump_down", StatusTagCatalog.NameOf(row.StatusTag) ?? row.StatusTag);
            FeedbackCues.RaiseLocated(FeedbackCueId.EnemyDestroyed, e.Position, text);
            NotificationCenter.Post(NotifyType, text, new Vector3(e.Position.x, 0f, e.Position.y));
            ClarityBossRecord r = Record(s);
            if (r != null && r.FillPump == i)
            {
                r.FillPump = -1; // 正在预警的那座被打掉：这一片不落
                r.NextFillTick = GameClock.Ticks + GameClock.TicksFor(FillSeconds);
            }
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>星球世界步（与观察无关）：沉寂时每游戏秒看一次有没有己方机器进场；开战中推进灌注 / 反应 / 泄压，并每游戏秒检查场地。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            ClarityBossRecord r = Record(s);
            if (r == null || (BaptismPhase)r.Phase == BaptismPhase.Defeated)
            {
                return;
            }
            CombatSite site = CombatSites.Get(SiteId);
            if (site == null || site.IsDisposed)
            {
                return;
            }
            bool second = tick % Math.Max(1, GameClock.StepHz) == 0;
            if ((BaptismPhase)r.Phase == BaptismPhase.Dormant)
            {
                // 开战（含首领战前自动存档）排到整步提交之后（ERD-SAV-002：存档不得写在事务半提交中）；攻城剧场开着 / 靶场测试进行中时推迟到下一秒。
                if (second && !_engagePending && ClarityCounterService.Visible(s) && TryArena(s, out Vector2 at) && MachinesInArena(s, at, forEngage: true) > 0
                    && RaidResultService.AutosaveDeferReason(s) == null)
                {
                    _engagePending = true;
                    WorldSim.WorldSimulation.RunAfterStep(() =>
                    {
                        _engagePending = false;
                        if (CampaignSession.Current == s && PhaseOf(s) == BaptismPhase.Dormant && TryArena(s, out Vector2 c2) && MachinesInArena(s, c2, forEngage: true) > 0)
                        {
                            Engage(s);
                        }
                    });
                }
                return;
            }
            if (!TryArena(s, out Vector2 center))
            {
                return;
            }
            if (second)
            {
                EnsureSpawned(s, site);
                if (TickArena(s, r, center, tick))
                {
                    return; // 复位了
                }
            }
            TickFill(s, r, site, center, tick);
            if ((BaptismPhase)r.Phase == BaptismPhase.React || (BaptismPhase)r.Phase == BaptismPhase.Vent)
            {
                TickReact(s, r, site, tick);
            }
            if ((BaptismPhase)r.Phase == BaptismPhase.Vent)
            {
                TickVent(s, r, site, tick);
            }
        }

        /// <summary>场地检查：有己方机器在场 → 计时清零；全部离开（含全灭）满 arena_leave_seconds → 复位。返回是否复位了。</summary>
        private static bool TickArena(CampaignState s, ClarityBossRecord r, Vector2 center, long tick)
        {
            if (MachinesInArena(s, center) > 0)
            {
                r.OutsideSinceTick = -1;
                return false;
            }
            if (r.OutsideSinceTick < 0)
            {
                r.OutsideSinceTick = tick;
                NotificationCenter.Post(NotifyType, GameText.Format("boss.clarity.notify.arena_leave", Mathf.RoundToInt(ArenaLeaveSeconds).ToString()),
                    new Vector3(center.x, 0f, center.y));
                return false;
            }
            if (tick - r.OutsideSinceTick >= GameClock.TicksFor(ArenaLeaveSeconds))
            {
                return ResetToPreFight(s);
            }
            return false;
        }

        public static float ArenaLeaveSecondsLeft(CampaignState s)
        {
            ClarityBossRecord r = Record(s);
            if (r == null || r.OutsideSinceTick < 0)
            {
                return -1f;
            }
            return Mathf.Max(0f, ArenaLeaveSeconds - (GameClock.Ticks - r.OutsideSinceTick) / (float)Math.Max(1, GameClock.StepHz));
        }

        private static void TickFill(CampaignState s, ClarityBossRecord r, CombatSite site, Vector2 center, long tick)
        {
            if (r.FillPump < 0 && tick >= r.NextFillTick)
            {
                Prune(r, tick);
                int pump = NextLivePump(s, r);
                if (pump < 0 || r.Active.Length >= FillCap)
                {
                    r.NextFillTick = tick + GameClock.TicksFor(FillSeconds);
                    return;
                }
                Vector2 spot = FillSpot(s, r, center);
                r.FillPump = pump;
                r.FillX = spot.x;
                r.FillY = spot.y;
                r.FillAtTick = tick + GameClock.TicksFor(FillWarnSeconds);
                // 预警：落点先亮起（没有效果的脉冲圈）+ 声音 + 字幕。
                site.Kernel.SpawnFieldZone(CombatFaction.Neutral, new double2(spot.x, spot.y), FillRadius, FillWarnSeconds, 0f, 0u, 0f, 0f, 0f, 0f, CombatZoneLook.Pulse);
                FeedbackCues.RaiseLocated(FeedbackCueId.ClaritySprayWarn, spot, GameText.Format("boss.clarity.cue.fill_warn", FluidName(FluidOf(s, pump))));
                return;
            }
            if (r.FillPump >= 0 && tick >= r.FillAtTick)
            {
                int pump = r.FillPump;
                r.FillPump = -1;
                r.PumpCursor = pump + 1;
                r.NextFillTick = tick + GameClock.TicksFor(FillSeconds);
                string tag = FluidOf(s, pump);
                if (Pump(s, pump)?.IsAlive != true || string.IsNullOrEmpty(tag))
                {
                    return;
                }
                var at = new Vector2(r.FillX, r.FillY);
                StatusTagCatalog.TryGet(tag, out StatusTag st);
                float tagSeconds = EnemyDefs.TryGet(Pump(s, pump).EnemyTypeId, out MechEnemy pumpRow) ? pumpRow.StatusSeconds : 4f;
                site.Kernel.SpawnFieldZone(CombatFaction.Neutral, new double2(at.x, at.y), FillRadius, FillLifeSeconds, 0f, NamedReactionCatalog.BitOf(tag),
                    Mathf.Max(1f, tagSeconds), st?.Amount ?? 0f, 0f, 0f, CombatZoneLook.Pool);
                r.Fills++;
                var list = new List<ClarityBossFill>(r.Active)
                {
                    new ClarityBossFill { Serial = r.Fills, X = at.x, Y = at.y, R = FillRadius, Tag = tag, UntilTick = tick + GameClock.TicksFor(FillLifeSeconds) },
                };
                r.Active = list.ToArray();
                FillsPoured++;
                FeedbackCues.RaiseLocated(FeedbackCueId.ClaritySpray, at, GameText.Format("boss.clarity.cue.fill", FluidName(tag)));
            }
        }

        private static void TickReact(CampaignState s, ClarityBossRecord r, CombatSite site, long tick)
        {
            if (r.ReactFill < 0 && tick >= r.NextReactTick)
            {
                Prune(r, tick);
                ClarityBossFill target = PickReactTarget(s, r);
                if (target == null)
                {
                    r.NextReactTick = tick + GameClock.TicksFor(2f);
                    return;
                }
                r.ReactFill = target.Serial;
                r.ReactAtTick = tick + GameClock.TicksFor(ReactWarnSeconds);
                var at = new Vector2(target.X, target.Y);
                site.Kernel.SpawnFieldZone(CombatFaction.Neutral, new double2(at.x, at.y), target.R, ReactWarnSeconds, 0f, 0u, 0f, 0f, 0f, 0f, CombatZoneLook.Pulse);
                FeedbackCues.RaiseLocated(FeedbackCueId.ClarityPivotWarn, at, GameText.Format("boss.clarity.cue.react_warn", FluidName(target.Tag)));
                return;
            }
            if (r.ReactFill >= 0 && tick >= r.ReactAtTick)
            {
                ClarityBossFill target = FindFill(r, r.ReactFill);
                r.ReactFill = -1;
                r.NextReactTick = tick + GameClock.TicksFor(ReactSeconds);
                if (target == null || target.UntilTick <= tick)
                {
                    return;
                }
                string trigger = TriggerOf(target.Tag);
                StatusTagCatalog.TryGet(trigger, out StatusTag st);
                var at = new Vector2(target.X, target.Y);
                site.Kernel.SpawnFieldZone(CombatFaction.Neutral, new double2(at.x, at.y), target.R, ReactZoneSeconds, ReactDps, NamedReactionCatalog.BitOf(trigger),
                    Mathf.Max(1f, ReactZoneSeconds), st?.Amount ?? 0f, 0f, 0f, CombatZoneLook.Pulse);
                r.Reactions++;
                ReactionsTriggered++;
                string verb = GameText.Get(trigger == "Shock" ? "boss.clarity.react.shock" : "boss.clarity.react.ignite");
                FeedbackCues.RaiseLocated(FeedbackCueId.ClarityDeflagrate, at, GameText.Format("boss.clarity.cue.react", verb + FluidName(target.Tag)));
            }
        }

        private static void TickVent(CampaignState s, ClarityBossRecord r, CombatSite site, long tick)
        {
            int state = VentStateAt(r, tick);
            if (state == r.VentState)
            {
                return;
            }
            r.VentState = state;
            Vector2 at = PartPosition(s, TowerInstanceId);
            switch (state)
            {
                case 1:
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossLockoutWarning, at, GameText.Get("boss.clarity.cue.vent_warn"));
                    break;
                case 2:
                    VentsOpened++;
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, GameText.Format("boss.clarity.cue.vent", VentDamageMult.ToString("0.##")));
                    break;
                default:
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, GameText.Get("boss.clarity.cue.seal"));
                    break;
            }
            SyncFlags(site, s);
        }

        /// <summary>反应塔的免疫位：一直不怕酸（澄净）；阶段三泄压以外冻不住（核心封闭），泄压时能冻住（先冻再烧打出热震）。</summary>
        public static void SyncFlags(CombatSite site, CampaignState s)
        {
            if (site == null || s == null || !site.TryGetEnemyUnit(TowerInstanceId, out int unit))
            {
                return;
            }
            ClarityBossRecord r = Record(s);
            uint mask = NamedReactionCatalog.BitOf("Acid");
            bool venting = r != null && (BaptismPhase)r.Phase == BaptismPhase.Vent && VentStateAt(r, GameClock.Ticks) == 2;
            if (!venting)
            {
                mask |= NamedReactionCatalog.BitOf("Frozen");
            }
            site.SetImmuneMask(unit, mask);
        }

        // ─────────────────────────────── 召来的单位 ───────────────────────────────

        private static void SpawnWave(CampaignState s, CombatSite site, ClarityBossRecord r, Vector2 center)
        {
            r.WaveSpawned = true;
            var defs = new List<RaidUnitDef>();
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (u != null && u.Faction == EnemyDefs.FactionClarity && u.MinLevel <= 1)
                {
                    defs.Add(u);
                }
            }
            if (defs.Count == 0 || WaveCount <= 0)
            {
                return;
            }
            var ids = new List<int>(WaveCount);
            for (int i = 0; i < WaveCount; i++)
            {
                float a = (i * 360f / WaveCount + 22.5f) * Mathf.Deg2Rad;
                Vector2 at = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (PumpRing * 0.6f);
                int unit = SiegeService.SpawnOne(site, WaveKey, at, center, defs[i % defs.Count], false,
                    DifficultyService.EnemyHealthMul(s), DifficultyService.EnemyDamageMul(s));
                if (unit > 0)
                {
                    site.SetSiegeUnitMode(unit, CombatSiegeMode.Skirmish); // 就地迎战（不走攻城流场去拆家园建筑）
                    ids.Add(unit);
                }
            }
            r.WaveUnits = ids.ToArray();
            NotificationCenter.Post(NotifyType, GameText.Format("boss.clarity.notify.wave", ids.Count.ToString()), new Vector3(center.x, 0f, center.y));
        }

        // ─────────────────────────────── 奖励 ───────────────────────────────

        private static void GrantRewardsOnce(CampaignState s, Vector2 at)
        {
            ClarityBossRecord r = Record(s);
            if (r == null || r.RewardsGranted)
            {
                return;
            }
            r.RewardsGranted = true;
            RewardGrants++;
            MechEnemy row = TowerRow();
            if (row != null && !string.IsNullOrEmpty(row.Components) && row.Components != EnemyDefs.None)
            {
                string[] fw = row.Components.Split(',');
                // components 第一个 = 固定的核心固件（反应增幅）；第二个 = 此前还没拿到才给的流体类固件（酸液）。
                if (fw.Length > 0)
                {
                    Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + EnemyDefs.FactionClarity + ":0", fw[0].Trim());
                }
                if (fw.Length > 1 && !Signal.FirmwareLibrary.IsObtained(s, fw[1].Trim()))
                {
                    Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + EnemyDefs.FactionClarity + ":1", fw[1].Trim());
                    NotificationCenter.Post(NotifyType, GameText.Format("boss.clarity.notify.reward_fluid", Signal.FirmwareKinds.DisplayName(fw[1].Trim()) ?? fw[1].Trim()));
                }
                Economy.TestRangeService.NoteEnemyDefeated(s, row.Id);
            }
            int holder = Economy.LootPickService.NearestMachine(SiteId, at, ArenaRadius * 2f, 0);
            Economy.LootPickService.Grant(s, Economy.LootPickService.SourceBoss, EnemyDefs.FactionClarity, holder, SiteId, at);
            // 澄净领地平定 → 主线“击败澄净的首领”完成 → 关键材料“反应核”进核心保管库（FG8-QST-01）；DEBT-FG8EXP03-01 的澄净一份。
            WorldSim.TerritoryControlService.MarkLeaderDefeated(s, EnemyDefs.FactionClarity);
            NotificationCenter.Post(NotifyType, GameText.Get("boss.clarity.notify.defeated"), new Vector3(at.x, 0f, at.y));
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        public static Vector2 PartPosition(CampaignState s, string instanceId)
        {
            CombatSite site = CombatSites.Get(SiteId);
            if (site != null && instanceId != null && site.TryGetEnemyPosition(instanceId, out Vector2 live))
            {
                return live;
            }
            RegionEnemyRecord rec = FindPart(s, instanceId);
            return rec != null ? rec.Position : Vector2.zero;
        }

        /// <summary>场地里的己方机器数。<paramref name="forEngage"/> = 判断要不要开战：行军中的远征队路过不算（玩家没让它们去打首领，FGR-BASE-020）；开战后谁在场都算。</summary>
        public static int MachinesInArena(CampaignState s, Vector2 center, bool forEngage = false)
        {
            float r2 = ArenaRadius * ArenaRadius;
            HashSet<int> marching = null;
            if (forEngage && ExpeditionMarchService.IsMarching(s))
            {
                marching = new HashSet<int>(ExpeditionMarchService.AlivePlanetMembers(s));
            }
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && !m.IsInFactory && m.RegionId == SiteId && (marching == null || !marching.Contains(m.LogicId))
                    && (LivePos(m) - center).sqrMagnitude <= r2)
                {
                    n++;
                }
            }
            return n;
        }

        private static Vector2 LivePos(MachineRecord m) => MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;

        private static int NextLivePump(CampaignState s, ClarityBossRecord r)
        {
            for (int k = 0; k < PumpCount; k++)
            {
                int i = (r.PumpCursor + k) % PumpCount;
                if (Pump(s, i)?.IsAlive == true)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>落点：泵站内圈到场地六成半径之间，方向与距离按（种子, 第几次挑战, 第几片）派生。</summary>
        private static Vector2 FillSpot(CampaignState s, ClarityBossRecord r, Vector2 center)
        {
            uint seed = (uint)(s.World?.WorldSeed ?? s.RandomSeed);
            uint h = Hash(seed, (uint)r.Attempt, (uint)(r.Fills + 1));
            float a = (h % 3600u) * 0.1f * Mathf.Deg2Rad;
            float min = Mathf.Max(FillRadius, PumpRing * 0.3f);
            float max = Mathf.Max(min + 1f, ArenaRadius * 0.6f);
            float d = min + ((h >> 12) % 1000u) / 1000f * (max - min);
            return center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
        }

        /// <summary>主动反应的目标：站着最多己方机器的那片流体；一样多取最早灌的。</summary>
        private static ClarityBossFill PickReactTarget(CampaignState s, ClarityBossRecord r)
        {
            ClarityBossFill best = null;
            int bestCount = -1;
            foreach (ClarityBossFill f in r.Active)
            {
                if (f == null)
                {
                    continue;
                }
                int n = 0;
                float rr = f.R * f.R;
                foreach (MachineRecord m in MachineRegistry.AllRecords)
                {
                    if (m != null && m.IsAlive && m.RegionId == SiteId && (LivePos(m) - new Vector2(f.X, f.Y)).sqrMagnitude <= rr)
                    {
                        n++;
                    }
                }
                if (n > bestCount)
                {
                    bestCount = n;
                    best = f;
                }
            }
            return best;
        }

        private static ClarityBossFill FindFill(ClarityBossRecord r, int serial)
        {
            foreach (ClarityBossFill f in r.Active)
            {
                if (f != null && f.Serial == serial)
                {
                    return f;
                }
            }
            return null;
        }

        private static void Prune(ClarityBossRecord r, long tick)
        {
            int alive = 0;
            foreach (ClarityBossFill f in r.Active)
            {
                if (f != null && f.UntilTick > tick)
                {
                    alive++;
                }
            }
            if (alive == r.Active.Length)
            {
                return;
            }
            var list = new List<ClarityBossFill>(alive);
            foreach (ClarityBossFill f in r.Active)
            {
                if (f != null && f.UntilTick > tick)
                {
                    list.Add(f);
                }
            }
            r.Active = list.ToArray();
        }

        /// <summary>正在预警的主动反应是“点燃”还是“通电”（HUD 用；没有预警返回空）。</summary>
        public static string PendingReactVerb(CampaignState s)
        {
            ClarityBossRecord r = Record(s);
            ClarityBossFill f = r != null && r.ReactFill >= 0 ? FindFill(r, r.ReactFill) : null;
            return f == null ? string.Empty
                : GameText.Get(TriggerOf(f.Tag) == "Shock" ? "boss.clarity.react.shock" : "boss.clarity.react.ignite") + FluidName(f.Tag);
        }

        /// <summary>引发反应用的标签：水 → 通电（短路），油 / 酸 → 点燃（爆燃 / 腐蚀灼烧）。</summary>
        public static string TriggerOf(string fluidTag) => fluidTag == "Wet" ? "Shock" : "Fire";

        private static string FluidName(string tag) => string.IsNullOrEmpty(tag) ? string.Empty : StatusTagCatalog.NameOf(tag) ?? tag;

        private static uint Hash(uint a, uint b, uint c)
        {
            uint h = 2166136261u;
            h = (h ^ a) * 16777619u;
            h = (h ^ b) * 16777619u;
            h = (h ^ c) * 16777619u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
