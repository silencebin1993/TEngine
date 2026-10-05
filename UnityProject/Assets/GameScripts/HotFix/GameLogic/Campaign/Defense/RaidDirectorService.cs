using System;
using System.Collections.Generic;
using System.Diagnostics;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using TEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-04 突袭导演与预警（FG06 FGR-DEF-020～024；FG16 第 3、5 节；FG17 FGR-GEN-032、034；FG14 FGR-ARC-015；FGT-DEF-003、004）。
    ///
    /// 一次突袭 = 一个计划（<see cref="RaidPlanRecord"/>），状态机：触发 → 筹备（预热途经区块、等寻路）→ 已排定（路线就绪，算好预警 / 出发 / 抵达）→
    /// 已预警（发预警；路程不够最短预警时部队在出发地集结）→ 已出发（行进队伍沿排定的路线走，<see cref="WorldSim.WorldTransitSystem"/>）→ 结束（撤退离场 / 被消灭）。
    /// - 触发（FGR-DEF-020）：暴露越过 30 / 60 / 90（<see cref="OnExposureChanged"/>，由暴露账本在每一笔实际变化时调用）、超过 90 后周期性 4 级、剧情节点
    ///   （击败首领后的报复：fg.TbRaidStory，按规则每游戏秒检查；或调用 <see cref="RequestStoryRaid"/>）、静默夜 / 巨构阶段 / 净化塔（接口，来源系统在后续里程碑）、低强度骚扰（频率上限）。
    /// - 最短间隔：两次普通突袭的抵达至少隔 raid.min_interval_days（难度频率倍率除它、出发方向被摧毁的据点放大它）；剧情 / 静默夜 / 巨构不受限，与时间重叠的普通突袭合并为一波。
    /// - 预算与编成、阵营、反制、目标、出发地、预警见 RaidDirectorService.Plan.cs。
    /// 确定性：只看统一时钟的步序号；随机抽取 = WorldGenMath.Hash(世界种子, 计划序号, 第几次, 盐)；寻路走固定延迟采纳——观察 / 不观察、暂停与 0.5x～3x、存读档逐字段一致。
    /// 开销：每步 O(计划数)（个位数）；每游戏秒一次 O(剧情规则数)；编成 O(单位数上限) 只在排定 / 升级时算一次。热更层，没有逐单位逻辑（逐单位在战斗内核，FG6-DEF-05 展开）。
    /// </summary>
    public static partial class RaidDirectorService
    {
        public const int StatePlanning = 0;
        public const int StateScheduled = 1;
        public const int StateWarned = 2;
        public const int StateDeparted = 3;
        public const int StateEnded = 4;
        public const int StateMerged = 5;
        public const int StateCancelled = 6;

        public const int RouteNeed = 0;
        public const int RouteAwaiting = 1;
        public const int RouteReady = 2;
        public const int RouteFailed = 3;

        public const string OriginOutpost = "outpost";
        public const string OriginFog = "fog";
        public const string TargetHome = "home";
        public const string TargetOutpost = "outpost";

        public const string EndWithdrawn = "withdrawn";
        public const string EndGone = "gone";
        public const string EndMerged = "merged";
        public const string EndCancelled = "cancelled";

        private const uint SaltCompose = 0x52414944u; // "RAID"
        private const uint SaltTarget = 0x54415247u;  // "TARG"

        /// <summary>界面刷新用：计划状态变了就 +1（预警条按它判断要不要重建）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>本会话执行的导演步数与耗时（性能证据）。</summary>
        public static long Steps { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        public static double LastComposeMs { get; private set; }
        public static double MaxComposeMs { get; private set; }

        public static RaidDirectorState StateOf(CampaignState s) => s?.Raids?.Director;

        public static IReadOnlyList<RaidPlanRecord> Plans(CampaignState s) =>
            (IReadOnlyList<RaidPlanRecord>)StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>();

        public static IReadOnlyList<RaidHistoryRecord> History(CampaignState s) =>
            (IReadOnlyList<RaidHistoryRecord>)StateOf(s)?.History ?? Array.Empty<RaidHistoryRecord>();

        public static RaidPlanRecord FindPlan(CampaignState s, string planId)
        {
            if (string.IsNullOrEmpty(planId))
            {
                return null;
            }
            foreach (RaidPlanRecord p in StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                if (p != null && p.PlanId == planId)
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>导演域补成空域；坏值钳回（数组长度不一致按最短截齐、方位数组补成 8 个）。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Raids == null)
            {
                return;
            }
            RaidDirectorState d = s.Raids.Director ??= new RaidDirectorState();
            d.Plans ??= Array.Empty<RaidPlanRecord>();
            d.History ??= Array.Empty<RaidHistoryRecord>();
            d.Pending ??= Array.Empty<RaidTriggerRecord>();
            d.StoriesFired ??= Array.Empty<string>();
            if (d.DestroyedByOctant == null || d.DestroyedByOctant.Length != 8)
            {
                var a = new int[8];
                for (int i = 0; i < Math.Min(8, d.DestroyedByOctant?.Length ?? 0); i++)
                {
                    a[i] = Math.Max(0, d.DestroyedByOctant[i]);
                }
                d.DestroyedByOctant = a;
            }
            if (d.NextPlanSerial < 1)
            {
                d.NextPlanSerial = 1;
            }
            if (d.NextWaveSerial < 1)
            {
                d.NextWaveSerial = 1;
            }
            foreach (RaidPlanRecord p in d.Plans)
            {
                if (p == null)
                {
                    continue;
                }
                p.PlanId ??= string.Empty;
                p.Trigger ??= string.Empty;
                p.Triggers ??= Array.Empty<string>();
                p.StoryId ??= string.Empty;
                p.Faction ??= string.Empty;
                p.Counter ??= string.Empty;
                p.CounterCategory ??= string.Empty;
                p.TargetKind ??= string.Empty;
                p.TargetId ??= string.Empty;
                p.OriginKind ??= string.Empty;
                p.OriginId ??= string.Empty;
                p.UnitIds ??= Array.Empty<string>();
                p.UnitCounts ??= Array.Empty<int>();
                p.EliteCounts ??= Array.Empty<int>();
                int n = Math.Min(p.UnitIds.Length, Math.Min(p.UnitCounts.Length, p.EliteCounts.Length));
                if (n != p.UnitIds.Length || n != p.UnitCounts.Length || n != p.EliteCounts.Length)
                {
                    Array.Resize(ref p.UnitIds, n);
                    Array.Resize(ref p.UnitCounts, n);
                    Array.Resize(ref p.EliteCounts, n);
                }
                p.RouteX ??= Array.Empty<int>();
                p.RouteY ??= Array.Empty<int>();
                p.PendingX ??= Array.Empty<int>();
                p.PendingY ??= Array.Empty<int>();
                if (p.PendingX.Length != p.PendingY.Length)
                {
                    p.PendingRoute = false;
                    p.PendingX = Array.Empty<int>();
                    p.PendingY = Array.Empty<int>();
                }
                if (p.RouteX.Length != p.RouteY.Length)
                {
                    p.RouteX = Array.Empty<int>();
                    p.RouteY = Array.Empty<int>();
                    if (p.State == StatePlanning)
                    {
                        p.RouteState = RouteNeed;
                    }
                }
                p.GroupId ??= string.Empty;
                p.MergedInto ??= string.Empty;
                p.EndReason ??= string.Empty;
                if (p.IntervalMul < 1f)
                {
                    p.IntervalMul = 1f;
                }
            }
            foreach (RaidHistoryRecord h in d.History)
            {
                if (h != null)
                {
                    h.PlanId ??= string.Empty;
                    h.Trigger ??= string.Empty;
                    h.Faction ??= string.Empty;
                    h.TargetKind ??= string.Empty;
                    h.OriginKind ??= string.Empty;
                    h.EndReason ??= string.Empty;
                }
            }
            foreach (RaidTriggerRecord t in d.Pending)
            {
                if (t != null)
                {
                    t.Kind ??= string.Empty;
                    t.Faction ??= string.Empty;
                    t.StoryId ??= string.Empty;
                }
            }
        }

        /// <summary>接到一个战役（新建 / 读档）时清零会话统计（存档里的导演状态不动）。</summary>
        public static void ResetSessionState()
        {
            Steps = 0;
            LastStepMs = 0;
            MaxStepMs = 0;
            LastComposeMs = 0;
            MaxComposeMs = 0;
            Revision++;
        }

        // ─────────────────────────────── 时间换算 ───────────────────────────────

        public static long DayTicks(double days) => GameClock.TicksFor(days * GameClock.DaySeconds);
        public static long HourTicks(double hours) => GameClock.TicksFor(hours * GameClock.DaySeconds / 24.0);

        /// <summary>开局宽限的终点（步）：之前不会有暴露 / 骚扰触发的突袭抵达。</summary>
        public static long GraceEndTick => DayTicks(RaidCatalog.FirstRaidMinDays);

        /// <summary>最短预警（步，已乘难度预警倍率）。</summary>
        public static long MinWarningTicks(CampaignState s) => HourTicks(RaidCatalog.MinWarningHours * RaidCatalog.DifficultyWarning(s?.DifficultyId));

        private static uint SeedOf(CampaignState s) => unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));

        private static float Threshold(int i) => i == 0
            ? CampaignExposureLedger.ThresholdScoutTip
            : i == 1 ? CampaignExposureLedger.ThresholdAdaptationIntel : CampaignExposureLedger.ThresholdCoreReinforcement;

        // ─────────────────────────────── 触发入口 ───────────────────────────────

        /// <summary>
        /// 第一次接上导演（新档开局 / 旧档第一次读进来）：按 <paramref name="exposure"/> 补好阈值状态——已经越过的阈值记为已触发、不追溯；
        /// 已经满足的剧情规则（例如旧档里早就击败了铸造主核心）记为已触发，不在读档后突然来一波报复。骚扰从宽限期结束后开始计时。
        /// </summary>
        private static void EnsureInitialized(CampaignState s, RaidDirectorState d, float exposure)
        {
            if (d.Initialized)
            {
                return;
            }
            d.Initialized = true;
            d.ThresholdFired = 0;
            for (int i = 0; i < 3; i++)
            {
                if (exposure >= Threshold(i))
                {
                    d.ThresholdFired |= 1 << i;
                }
            }
            long now = GameClock.Ticks;
            d.Level4NextTick = exposure > Threshold(2) ? now + DayTicks(RaidCatalog.Level4PeriodDays / RaidCatalog.DifficultyFrequency(s.DifficultyId)) : -1;
            d.HarassNextTick = Math.Max(now, GraceEndTick) + DayTicks(RaidCatalog.HarassMinIntervalDays / RaidCatalog.DifficultyFrequency(s.DifficultyId));
            var fired = new List<string>(d.StoriesFired);
            foreach (GameConfig.fg.RaidStory st in RaidCatalog.Stories)
            {
                if (!fired.Contains(st.Id) && StoryRuleMet(s, st.Rule))
                {
                    fired.Add(st.Id);
                }
            }
            d.StoriesFired = fired.ToArray();
        }

        /// <summary>
        /// 暴露的每一笔实际变化（<see cref="CampaignExposureLedger"/> 唯一调用方）：从下向上越过 30 / 60 / 90 时排一个 1 / 2 / 3 级触发
        /// （一笔越过几个阈值只排最高的那一级）；降到阈值 − raid.threshold_rearm 以下清位，之后再越过会再次触发；超过 90 开始 4 级周期计时。
        /// 只排队、不当场排定（下一个模拟步按顺序处理），所以暴露在模拟步之外变化（界面操作）也是确定的。
        /// </summary>
        public static void OnExposureChanged(CampaignState s, float before, float after)
        {
            if (s?.Raids == null)
            {
                return;
            }
            EnsureState(s);
            RaidDirectorState d = s.Raids.Director;
            EnsureInitialized(s, d, before);
            int level = 0;
            float rearm = RaidCatalog.ThresholdRearm;
            for (int i = 0; i < 3; i++)
            {
                float thr = Threshold(i);
                int bit = 1 << i;
                if (after >= thr && (d.ThresholdFired & bit) == 0)
                {
                    d.ThresholdFired |= bit;
                    level = i + 1;
                }
                else if (after < thr - rearm && (d.ThresholdFired & bit) != 0)
                {
                    d.ThresholdFired &= ~bit;
                }
            }
            if (after > Threshold(2))
            {
                if (d.Level4NextTick < 0)
                {
                    d.Level4NextTick = GameClock.Ticks + DayTicks(RaidCatalog.Level4PeriodDays / RaidCatalog.DifficultyFrequency(s.DifficultyId));
                }
            }
            else
            {
                d.Level4NextTick = -1;
            }
            if (level > 0)
            {
                Enqueue(d, RaidCatalog.TriggerExposure, level, null, null);
            }
        }

        /// <summary>剧情节点引发的突袭（FGR-DEF-020“剧情节点”；击败首领的报复已由 fg.TbRaidStory 规则自动触发）：不受最短间隔限制，与重叠的普通突袭合并为一波。</summary>
        public static bool RequestStoryRaid(CampaignState s, string faction, int level, string storyId = null)
        {
            if (s?.Raids == null)
            {
                return false;
            }
            EnsureState(s);
            Enqueue(s.Raids.Director, RaidCatalog.TriggerStory, Math.Max(1, Math.Min(4, level)), faction, storyId);
            return true;
        }

        /// <summary>静默夜开始（FG07；静默夜系统 FG7-ENV-02 调用）：每次静默夜必定有一次突袭，不受最短间隔限制。</summary>
        public static bool OnSilentNightStarted(CampaignState s) => EnqueueKind(s, RaidCatalog.TriggerSilentNight, null);

        /// <summary>第三幕巨构一个阶段完工（FG11 / FG13-END-01 调用）：触发一次突袭，不受最短间隔限制。</summary>
        public static bool OnMegastructureStageCompleted(CampaignState s) => EnqueueKind(s, RaidCatalog.TriggerMegastructure, null);

        /// <summary>化工阵营针对净化塔（FG09；FG10-RAID-01 在净化塔建成 / 化工阵营登场时调用）：由化工阵营发动。</summary>
        public static bool OnPurificationTowerTargeted(CampaignState s) => EnqueueKind(s, RaidCatalog.TriggerPurification, RaidCatalog.FactionChemical);

        private static bool EnqueueKind(CampaignState s, string kind, string faction)
        {
            if (s?.Raids == null)
            {
                return false;
            }
            EnsureState(s);
            Enqueue(s.Raids.Director, kind, -1, faction, null);
            return true;
        }

        private static void Enqueue(RaidDirectorState d, string kind, int level, string faction, string storyId)
        {
            var list = new List<RaidTriggerRecord>(d.Pending.Length + 1);
            list.AddRange(d.Pending);
            list.Add(new RaidTriggerRecord
            {
                Kind = kind ?? string.Empty,
                Level = level,
                Faction = faction ?? string.Empty,
                StoryId = storyId ?? string.Empty,
                Tick = GameClock.Ticks,
            });
            d.Pending = list.ToArray();
            d.TriggersReceived++;
        }

        /// <summary>FG6-DEF-04（FGR-GEN-034）：据点被摧毁（<see cref="WorldSim.WorldOutpostSystem.MarkDestroyed"/> 调用）——记下它相对家园的方位，
        /// 从那个方向出发的突袭最短间隔 ×(1 + raid.destroyed_outpost_interval_bonus × 个数)。还没出发、从这个据点出发的计划在出发时改道（见 Depart）。</summary>
        public static void OnOutpostDestroyed(CampaignState s, OutpostRecord o)
        {
            if (s?.Raids == null || o == null)
            {
                return;
            }
            EnsureState(s);
            Grid.GridCell core = Grid.HomeGridService.CorePivot(s);
            int oct = Economy.IntelService.Octant(o.CellX - core.X, o.CellY - core.Y);
            s.Raids.Director.DestroyedByOctant[oct]++;
            Revision++;
        }

        /// <summary>剧情规则：首领被击败（与情报 / 远征同一判定口径）。</summary>
        public static bool StoryRuleMet(CampaignState s, string rule)
        {
            if (rule == RaidCatalog.StoryRuleFoundryCore)
            {
                RegionRecord r = FoundryOutpostRegion.Find(s);
                return r != null && FoundryOutpostCoreBoss.GetState(r) == CoreBossState.Destroyed;
            }
            return false;
        }

        // ─────────────────────────────── 每个模拟步 ───────────────────────────────

        /// <summary>
        /// 世界模拟的一个固定步（WorldSimulation 在行进队伍与据点之后调用；tick = 当前步）：每游戏秒查一次剧情规则、4 级周期与骚扰；
        /// 处理排队的触发；推进每个计划的状态机。只看步序号，与观察、帧率、倍速无关。
        /// </summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            RaidDirectorState d = StateOf(s);
            if (d == null)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            EnsureInitialized(s, d, s.SignalExposure);
            if (tick % Math.Max(1, GameClock.StepHz) == 0)
            {
                PeriodicChecks(s, d, tick);
            }
            if (d.Pending.Length > 0)
            {
                RaidTriggerRecord[] list = d.Pending;
                d.Pending = Array.Empty<RaidTriggerRecord>();
                foreach (RaidTriggerRecord t in list)
                {
                    if (t != null)
                    {
                        HandleTrigger(s, d, t, tick);
                    }
                }
            }
            if (d.Plans.Length > 0)
            {
                AdvancePlans(s, d, tick);
            }
            Steps++;
            LastStepMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
        }

        private static void PeriodicChecks(CampaignState s, RaidDirectorState d, long tick)
        {
            // 剧情：击败首领后的报复（每个只触发一次）。
            foreach (GameConfig.fg.RaidStory st in RaidCatalog.Stories)
            {
                if (Array.IndexOf(d.StoriesFired, st.Id) < 0 && StoryRuleMet(s, st.Rule))
                {
                    var fired = new List<string>(d.StoriesFired) { st.Id };
                    d.StoriesFired = fired.ToArray();
                    Enqueue(d, RaidCatalog.TriggerStory, Math.Max(1, st.Level), st.Faction, st.Id);
                }
            }
            float freq = RaidCatalog.DifficultyFrequency(s.DifficultyId);
            // 暴露超过 90：周期性 4 级。
            if (d.Level4NextTick >= 0 && tick >= d.Level4NextTick)
            {
                if (s.SignalExposure > Threshold(2))
                {
                    Enqueue(d, RaidCatalog.TriggerExposure, 4, null, null);
                    d.Level4NextTick = tick + DayTicks(RaidCatalog.Level4PeriodDays / freq);
                }
                else
                {
                    d.Level4NextTick = -1;
                }
            }
            // 低强度骚扰：第一次突袭之后、两次骚扰至少隔 raid.harass_min_interval_days、最近 raid.harass_quiet_days 没有突袭、也没有还没出发的普通突袭。
            if (d.RaidCount >= 1 && d.HarassNextTick >= 0 && tick >= d.HarassNextTick)
            {
                bool quiet = d.LastAnyArrivalTick < 0 || tick - d.LastAnyArrivalTick >= DayTicks(RaidCatalog.HarassQuietDays);
                if (quiet && PendingNormalPlan(d) == null)
                {
                    Enqueue(d, RaidCatalog.TriggerHarass, 0, null, null);
                    d.HarassNextTick = tick + DayTicks(RaidCatalog.HarassMinIntervalDays / freq);
                }
                else
                {
                    // 条件不满足：过一阵再看（不攒次数，频率上限照旧）。
                    d.HarassNextTick = tick + DayTicks(0.25);
                }
            }
        }

        /// <summary>
        /// 还没出发的普通（受最短间隔约束的）计划：优先还没发预警的那个（同一时刻至多一个，新的普通触发并进它）；没有时返回已预警、正在集结 / 等出发的那个。
        /// 都没有 = null（骚扰据此判断“没有还没出发的普通突袭”）。
        /// </summary>
        public static RaidPlanRecord PendingNormalPlan(RaidDirectorState d)
        {
            RaidPlanRecord warned = null;
            foreach (RaidPlanRecord p in d?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                if (p != null && !p.Exempt && p.State <= StateWarned)
                {
                    if (p.State < StateWarned)
                    {
                        return p;
                    }
                    warned ??= p;
                }
            }
            return warned;
        }

        private static void HandleTrigger(CampaignState s, RaidDirectorState d, RaidTriggerRecord t, long tick)
        {
            if (!RaidCatalog.TryGetTrigger(t.Kind, out RaidTriggerDef def))
            {
                d.TriggersSkipped++;
                Log.Warning($"[RaidDirector] 不认识的触发 {t.Kind}，跳过。");
                return;
            }
            if (RaidCatalog.StoryOnly(s.DifficultyId) && t.Kind != RaidCatalog.TriggerStory)
            {
                // FG16 第 3 节：建造者难度只有剧情突袭。
                d.TriggersSkipped++;
                return;
            }
            int level = ResolveLevel(s, def, t);
            string faction = !string.IsNullOrEmpty(t.Faction) ? t.Faction : MostStimulatedFaction(s);
            if (string.IsNullOrEmpty(faction) || !RaidCatalog.HasUnits(faction))
            {
                d.TriggersSkipped++;
                Log.Warning($"[RaidDirector] 触发 {t.Kind}：没有能发动突袭的阵营（{faction ?? "无"}），跳过。");
                return;
            }
            if (!def.Exempt)
            {
                // 并进还没出发的普通计划。它已经发了预警（集结中）就不能再升级：更高等级的触发另起一波（按最短间隔排在它后面），
                // 不被悄悄吞掉——阈值位已经置上、以后不会再补（复审 P2）；同级或更低的触发照旧并进去。
                RaidPlanRecord pending = PendingNormalPlan(d);
                if (pending != null && (pending.State < StateWarned || level <= pending.Level))
                {
                    MergeTrigger(s, pending, def.Id, level);
                    d.TriggersMerged++;
                    return;
                }
            }
            CreatePlan(s, d, def, t, level, faction, tick);
        }

        /// <summary>等级：暴露阈值 / 调用方给出；tier = 当前暴露档位（越过的阈值数，至少 1、至多 3）；骚扰 = 0。</summary>
        private static int ResolveLevel(CampaignState s, RaidTriggerDef def, RaidTriggerRecord t)
        {
            switch (def.LevelRule)
            {
                case "harass":
                    return 0;
                case "tier":
                {
                    int n = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        if (s.SignalExposure >= Threshold(i))
                        {
                            n++;
                        }
                    }
                    return Math.Max(1, Math.Min(3, n));
                }
                default:
                    return Math.Max(1, Math.Min(4, t.Level < 1 ? 2 : t.Level));
            }
        }

        private static void Touch() => Revision++;
    }
}
