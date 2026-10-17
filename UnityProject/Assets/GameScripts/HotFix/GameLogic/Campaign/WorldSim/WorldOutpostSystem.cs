using System;
using System.Collections.Generic;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-06（FG14 FGR-ARC-016 休眠与唤醒；FG17 FGR-GEN-052 第 3 条；FGT-GEN-010）：星球上的敌方据点与巡逻。
    ///
    /// <b>活跃时</b>每个模拟步推进：增援（增援点落在固定网格上：出生步 + k × 间隔，满员时这一次作废）、巡逻沿寻路路线在据点与折返点之间来回走
    /// （进度是定点整数：每步 + 速度）。<b>休眠时</b>什么都不算。
    /// 离所有己方实体（家园建筑、家园机器）都超过 outpost.dormant_distance_cells（初值 300 格）就休眠；回到这个距离内、突袭导演调用、事件触发时唤醒。
    /// 唤醒按确定性规则补算休眠期间应有的变化：跨过了几个增援点就加几个（不超过上限），巡逻进度 + 速度 × 休眠步数（模运算）——
    /// 与“一直在模拟”逐字段一致（整数运算，没有浮点累加误差）。
    ///
    /// 确定性：休眠判定只在步序号是 sim.activity_refresh_seconds × 步频 的整数倍时做（与会话、镜头、帧率无关）；按距离唤醒的据点排队，
    /// 每步至多 outpost.wakes_per_step 个（分帧进行），队列进存档。巡逻路线的请求与交付与据点是否休眠无关（两边同一步拿到同一条路线）。
    /// 表现层看休眠据点时用 <see cref="Peek"/> 现算（不改状态：观察不改变结果）。
    /// 每步开销 O(据点数 + 巡逻数)（聚合体）；休眠判定每游戏秒一次 O(据点数 × 己方实体数)，有提前退出。
    /// 据点的正式生成（领地模板、密度）属于 FG3-GEN-01 / FG8-GEN-02；增援规则与控制度属于 FG8-EXP-03；巡逻与己方交战属于 FG8。
    /// 现在正式流程还不会生成据点：自检与冒烟用测试捷径 <see cref="SpawnOutpost"/>（DEBT-FG0ARCH06-01）。
    /// </summary>
    public static class WorldOutpostSystem
    {
        public const string WakeProximity = "outpost.wake.proximity";
        public const string WakeRaid = "outpost.wake.raid";
        public const string WakeEvent = "outpost.wake.event";

        public const int RouteNeed = 0;
        public const int RouteAwaiting = 1;
        public const int RouteReady = 2;
        public const int RouteFailed = 3;

        private static long _lastSteppedTick = -1;
        private static readonly Dictionary<string, OutpostRecord> ById = new Dictionary<string, OutpostRecord>(StringComparer.Ordinal);
        private static OutpostRecord[] _indexed;
        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();
        private static readonly List<Vector2> OwnScratch = new List<Vector2>(128);

        public static int WakesProcessed { get; private set; }
        public static int SleepsApplied { get; private set; }
        public static double LastWakeMs { get; private set; }
        public static double MaxWakeMs { get; private set; }
        public static int Evaluations { get; private set; }

        // FG8-GEN-02（DEBT-FG0ARCH06-06）：醒着的据点列表与空间桶索引——每步只推进醒着的据点（休眠的不再被遍历），
        // 地图 / 星球标记与休眠判定按桶粗筛。都是纯缓存：结果与逐个遍历完全相同（排序不影响：每个据点的推进互不依赖）。
        private static OutpostRecord[] _awakeSource;
        private static int _awakeStamp = -1;
        private static int _stateStamp;
        private static readonly List<OutpostRecord> AwakeList = new List<OutpostRecord>(32);
        private static OutpostRecord[] _bucketSource;
        private static readonly Dictionary<long, List<OutpostRecord>> Buckets = new Dictionary<long, List<OutpostRecord>>();
        private static readonly HashSet<long> OwnBuckets = new HashSet<long>();
        private static readonly Dictionary<long, List<Vector2>> OwnByBucket = new Dictionary<long, List<Vector2>>();
        private static readonly List<List<Vector2>> OwnListPool = new List<List<Vector2>>(16);

        /// <summary>自检读点：休眠判定里真正做了逐个距离比较的据点数（粗筛后）、最近一次每步推进的醒着据点数。</summary>
        public static int LastExactChecks { get; private set; }
        public static int LastSteppedOutposts { get; private set; }

        public static int BucketCells => Math.Max(16, (int)Math.Round(Tuning("outpost.bucket_cells", 128f)));

        /// <summary>接到一个战役（新建 / 读档）时清零会话态。</summary>
        public static void ResetSession()
        {
            _lastSteppedTick = -1;
            ById.Clear();
            _indexed = null;
            _awakeSource = null;
            _awakeStamp = -1;
            _stateStamp++;
            Revision++;
            _bucketSource = null;
            Buckets.Clear();
            WakesProcessed = 0;
            SleepsApplied = 0;
            LastWakeMs = 0;
            MaxWakeMs = 0;
            Evaluations = 0;
        }

        public static IReadOnlyList<OutpostRecord> Outposts(CampaignState state) =>
            (IReadOnlyList<OutpostRecord>)state?.Raids?.Outposts ?? Array.Empty<OutpostRecord>();

        public static IReadOnlyList<PatrolRecord> Patrols(CampaignState state) =>
            (IReadOnlyList<PatrolRecord>)state?.Raids?.Patrols ?? Array.Empty<PatrolRecord>();

        private static PatrolRecord[] _patrolIndexSource;
        private static readonly Dictionary<string, List<PatrolRecord>> PatrolIndex = new Dictionary<string, List<PatrolRecord>>(StringComparer.Ordinal);

        /// <summary>
        /// FG8-GEN-02（DEBT-FG0ARCH06-08）：某个据点的巡逻（地图按视野里的据点找它们的巡逻，与巡逻总数无关）。索引跟着巡逻数组重建（数组只在增减巡逻时整体替换）。
        /// </summary>
        public static void PatrolsOf(CampaignState state, string outpostId, List<PatrolRecord> into)
        {
            PatrolRecord[] all = state?.Raids?.Patrols;
            if (!ReferenceEquals(all, _patrolIndexSource))
            {
                _patrolIndexSource = all;
                PatrolIndex.Clear();
                for (int i = 0; all != null && i < all.Length; i++)
                {
                    PatrolRecord p = all[i];
                    if (p == null || string.IsNullOrEmpty(p.OutpostId))
                    {
                        continue;
                    }
                    if (!PatrolIndex.TryGetValue(p.OutpostId, out List<PatrolRecord> list))
                    {
                        PatrolIndex[p.OutpostId] = list = new List<PatrolRecord>(1);
                    }
                    list.Add(p);
                }
            }
            if (outpostId != null && PatrolIndex.TryGetValue(outpostId, out List<PatrolRecord> found))
            {
                into.AddRange(found);
            }
        }

        public static OutpostRecord Find(CampaignState state, string outpostId)
        {
            OutpostRecord[] all = state?.Raids?.Outposts;
            if (all == null || string.IsNullOrEmpty(outpostId))
            {
                return null;
            }
            if (!ReferenceEquals(_indexed, all))
            {
                ById.Clear();
                foreach (OutpostRecord o in all)
                {
                    if (o != null && !string.IsNullOrEmpty(o.OutpostId))
                    {
                        ById[o.OutpostId] = o;
                    }
                }
                _indexed = all;
            }
            return ById.TryGetValue(outpostId, out OutpostRecord r) ? r : null;
        }

        // ─────────────────────────────── 调参 ───────────────────────────────

        private static float Tuning(string id, float fallback) => NavService.Tuning(id, fallback);

        public static double DormantDistance => Math.Max(1.0, Tuning("outpost.dormant_distance_cells", 300f));
        public static double WakeHysteresis => Math.Max(0.0, Tuning("outpost.wake_hysteresis_cells", 32f));
        public static int WakesPerStep => Math.Max(1, (int)Math.Round(Tuning("outpost.wakes_per_step", 4f)));

        /// <summary>休眠判定的周期（步）：sim.activity_refresh_seconds × 步频。</summary>
        public static long EvaluateEveryTicks => Math.Max(1, (long)Math.Round(GameClock.TuningOr("sim.activity_refresh_seconds", 1f) * GameClock.StepHz));

        /// <summary>“现在”要补算到哪一步（不含）：本步的据点推进已经跑过 = 当前步 + 1，否则 = 当前步。</summary>
        private static long CatchUpTarget() => _lastSteppedTick >= GameClock.Ticks ? GameClock.Ticks + 1 : GameClock.Ticks;

        // ─────────────────────────────── 生成（测试捷径；正式生成属于 FG3-GEN-01 / FG8-GEN-02）───────────────────────────────

        /// <summary>
        /// 在 <paramref name="cell"/> 放一个敌方据点（可带一支巡逻）。<paramref name="reinforceIntervalTicks"/> &lt;= 0 时用 outpost.reinforce_interval_days。
        /// 巡逻折返点按（世界种子, 据点序号）派生方向（B25：不依赖固定坐标）。
        /// </summary>
        public static OutpostRecord SpawnOutpost(CampaignState state, string territoryId, GridCell cell, bool withPatrol, long reinforceIntervalTicks = 0)
        {
            CampaignFgStateDomains.EnsureAll(state);
            RaidState raids = state.Raids;
            long now = CatchUpTarget();
            long interval = reinforceIntervalTicks > 0
                ? reinforceIntervalTicks
                : Math.Max(1L, (long)Math.Round(Tuning("outpost.reinforce_interval_days", 3f) * GameClock.DaySeconds * GameClock.StepHz));
            int cap = Math.Max(1, (int)Math.Round(Tuning("outpost.garrison_cap", 12f)));
            var o = new OutpostRecord
            {
                OutpostId = "outpost-" + raids.NextOutpostSerial,
                TerritoryId = territoryId ?? string.Empty,
                CellX = cell.X,
                CellY = cell.Y,
                Garrison = Math.Min(cap, Math.Max(1, (int)Math.Round(Tuning("outpost.garrison_start", 6f)))),
                GarrisonCap = cap,
                ReinforceIntervalTicks = interval,
                NextReinforceTick = now + interval,
                SimTick = now,
                Dormant = false,
                LastWakeReason = string.Empty,
            };
            raids.NextOutpostSerial++;
            var list = new List<OutpostRecord>(raids.Outposts ?? Array.Empty<OutpostRecord>()) { o };
            raids.Outposts = list.ToArray();
            Revision++;
            if (withPatrol)
            {
                SpawnPatrol(state, o);
            }
            return o;
        }

        /// <summary>
        /// FG3-GEN-01（FGR-GEN-030、032、034）：把生成器分布的一个家园区侦察巢登记成据点。据点 ID = 点位 ID（按分布格，确定）；
        /// 驻军与上限按等级放大（outpost.nest_garrison_per_tier），离核心越远越强。侦察巢不派巡逻：巡逻与己方交战、摧毁掉落属于 FG8 / FG6（DEBT-FG3GEN01-05）。
        /// </summary>
        public static OutpostRecord SpawnScoutNest(CampaignState state, WorldGen.WorldFeature nest)
        {
            OutpostRecord o = SpawnOutpost(state, "home_zone", new GridCell(nest.X, nest.Y), withPatrol: false);
            int tier = Math.Max(1, nest.Tier);
            int per = Math.Max(1, (int)Math.Round(Tuning("outpost.nest_garrison_per_tier", 2f)));
            o.OutpostId = nest.Id;
            o.Kind = "scout_nest";
            o.Tier = tier;
            o.GarrisonCap = Math.Max(1, per * tier + 2);
            o.Garrison = Math.Min(o.GarrisonCap, per * tier);
            return o;
        }

        /// <summary>
        /// FG8-GEN-02（FGR-GEN-023、034、040；DEBT-FG0ARCH06-01）：把生成器分布的一个据点（阵营领地的主力据点 / 领地之间与外延区的零散前哨）登记成据点。
        /// 据点 ID = 点位 ID（按分布格，确定，幂等：已登记的返回原记录）。驻军 = 等级 × 每级驻军，上限再 +2；带巡逻的派一支（折返点离据点 <paramref name="patrolRadius"/> 格）。
        /// <b>“从第 0 步起一直在休眠”</b>：出生步 = 0、增援点落在 0 + k × 间隔、初始即休眠——无论在开局一次登记，还是外延区在被揭开时才按需登记，
        /// 被唤醒时都按确定性规则从第 0 步补算，结果与“开局就登记、一直在模拟”逐字段一致（FGR-GEN-002 与访问顺序无关）。
        /// </summary>
        public static OutpostRecord SpawnGenerated(CampaignState state, WorldGen.WorldFeature f, int garrisonPerTier, int patrolRadius)
        {
            if (state == null || f == null || f.Kind != WorldGen.WorldFeatureKind.Outpost)
            {
                return null;
            }
            OutpostRecord existing = Find(state, f.Id);
            if (existing != null)
            {
                return existing;
            }
            CampaignFgStateDomains.EnsureAll(state);
            RaidState raids = state.Raids;
            OutpostRecord o = BuildGenerated(f, garrisonPerTier);
            // 不占用 NextOutpostSerial（测试捷径 SpawnOutpost 的“outpost-N”编号与此前一致）。
            var list = new List<OutpostRecord>(raids.Outposts ?? Array.Empty<OutpostRecord>()) { o };
            raids.Outposts = list.ToArray();
            _stateStamp++;
            Revision++;
            if (f.Patrol)
            {
                // FG8-EXP-03：重建时按它重新派巡逻——与 SpawnPatrol 同一默认值（模板半径为 0 时取 outpost.patrol_radius_cells），重建后的巡逻与初次生成的一样（复审 P2）。
                o.PatrolRadius = EffectivePatrolRadius(patrolRadius);
                SpawnPatrol(state, o, o.PatrolRadius);
            }
            return o;
        }

        /// <summary>同 <see cref="SpawnGenerated"/>，一次登记一批（外延区揭开一大片时只换一次数组；不派巡逻——零散前哨不巡逻）。已登记的跳过。返回登记了几个。</summary>
        public static int SpawnGeneratedMany(CampaignState state, List<WorldGen.WorldFeature> features, int garrisonPerTier)
        {
            if (state == null || features == null || features.Count == 0)
            {
                return 0;
            }
            CampaignFgStateDomains.EnsureAll(state);
            RaidState raids = state.Raids;
            List<OutpostRecord> list = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (WorldGen.WorldFeature f in features)
            {
                if (f == null || f.Kind != WorldGen.WorldFeatureKind.Outpost || Find(state, f.Id) != null || !seen.Add(f.Id))
                {
                    continue;
                }
                list ??= new List<OutpostRecord>(raids.Outposts ?? Array.Empty<OutpostRecord>());
                list.Add(BuildGenerated(f, garrisonPerTier));
            }
            if (list == null)
            {
                return 0;
            }
            int added = list.Count - (raids.Outposts?.Length ?? 0);
            raids.Outposts = list.ToArray();
            _stateStamp++;
            Revision++;
            return added;
        }

        private static OutpostRecord BuildGenerated(WorldGen.WorldFeature f, int garrisonPerTier)
        {
            long interval = Math.Max(1L, (long)Math.Round(Tuning("outpost.reinforce_interval_days", 3f) * GameClock.DaySeconds * GameClock.StepHz));
            int tier = Math.Max(1, f.Tier);
            int per = Math.Max(1, garrisonPerTier);
            return new OutpostRecord
            {
                OutpostId = f.Id,
                TerritoryId = f.FactionId ?? string.Empty,
                Kind = f.TerritoryId != null ? KindTerritory : f.Outer ? KindOuter : KindScatter,
                Tier = tier,
                CellX = f.X,
                CellY = f.Y,
                GarrisonCap = per * tier + 2,
                Garrison = per * tier,
                ReinforceIntervalTicks = interval,
                NextReinforceTick = interval,
                SimTick = 0,
                Dormant = true,
                DormantSinceTick = 0,
                LastWakeReason = string.Empty,
                PatrolRadius = -1, // 带巡逻的由 SpawnGenerated 改写成实际半径
            };
        }

        /// <summary>据点种类（OutpostRecord.Kind）：家园区侦察巢 / 阵营领地的主力据点 / 领地之间的零散前哨 / 外延区前哨。领地控制度（FG8-EXP-03）只数 territory。</summary>
        public const string KindScoutNest = "scout_nest";
        public const string KindTerritory = "territory";
        public const string KindScatter = "scatter";
        public const string KindOuter = "outer";
        /// <summary>FG8-EXP-03：已收复领地里定期刷新的精英残部（不增援、不巡逻、不算控制度、不作突袭出发地）。</summary>
        public const string KindRemnant = "remnant";

        /// <summary>FG8-EXP-03：据点集合的版本（摧毁 / 重建 / 新登记 / 移除巡逻时 +1；领地控制度据此重算，平时 O(1)）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>
        /// 按种子生成的据点（领地主力 / 零散前哨 / 外延区，FG8-GEN-02）：“从第 0 步起就在、一直休眠”。它们的巡逻路线按需请求——据点醒着才要路线，
        /// 路线视为第 0 步就绪（进度按已模拟的步数补上），所以与何时第一次醒来、是否被观察都无关（FGR-BASE-021）；开局不必为几十支远处巡逻一起算路线、
        /// 占住寻路工作线程（FG17 性能：200 个单位同时请求路线时主线程不等工作线程）。
        /// </summary>
        public static bool IsGenerated(OutpostRecord o) => o != null && (o.Kind == KindTerritory || o.Kind == KindScatter || o.Kind == KindOuter);

        /// <summary>在某个领地里（规划层按种子给出的领地中心）放一个据点（测试捷径；B25：位置来自种子，不是固定坐标）。</summary>
        public static OutpostRecord SpawnOutpostInTerritory(CampaignState state, string territoryId, bool withPatrol, long reinforceIntervalTicks = 0)
        {
            PlannedTerritory t = WorldGenService.PlanFor(state)?.Find(territoryId);
            if (t == null)
            {
                return null;
            }
            return SpawnOutpost(state, territoryId, new GridCell(t.CenterX, t.CenterY), withPatrol, reinforceIntervalTicks);
        }

        /// <summary>巡逻折返半径（格）：给了正数用它，否则 outpost.patrol_radius_cells；至少 2 格。</summary>
        private static int EffectivePatrolRadius(int radiusCells) =>
            Math.Max(2, radiusCells > 0 ? radiusCells : (int)Math.Round(Tuning("outpost.patrol_radius_cells", 24f)));

        /// <summary>
        /// 旧档（FG8-EXP-03 之前登记的据点 PatrolRadius = 0）补域：有巡逻的按巡逻折返点离据点的距离补回半径，没有的记 −1（不派）。
        /// 只处理 PatrolRadius == 0 的记录，补过一次就不再进来（每条记录一次 O(巡逻)）。
        /// </summary>
        public static void BackfillPatrolRadius(CampaignState state)
        {
            OutpostRecord[] all = state?.Raids?.Outposts;
            if (all == null)
            {
                return;
            }
            foreach (OutpostRecord o in all)
            {
                if (o == null || o.PatrolRadius != 0)
                {
                    continue;
                }
                o.PatrolRadius = -1;
                foreach (PatrolRecord p in state.Raids.Patrols ?? Array.Empty<PatrolRecord>())
                {
                    if (p != null && p.OutpostId == o.OutpostId)
                    {
                        double dx = p.TurnX - o.CellX;
                        double dy = p.TurnY - o.CellY;
                        o.PatrolRadius = Math.Max(2, (int)Math.Round(Math.Sqrt(dx * dx + dy * dy)));
                        break;
                    }
                }
            }
        }

        private static PatrolRecord SpawnPatrol(CampaignState state, OutpostRecord o, int radiusCells = 0)
        {
            RaidState raids = state.Raids;
            int serial = raids.NextPatrolSerial++;
            uint seed = unchecked((uint)(state.World?.WorldSeed ?? state.RandomSeed));
            uint h = WorldGenMath.Hash(seed, serial, o.CellX ^ o.CellY, 0x50415452u); // "PATR"
            double ang = (h & 0xFFFF) / 65536.0 * 2.0 * Math.PI;
            double r = EffectivePatrolRadius(radiusCells);
            var p = new PatrolRecord
            {
                PatrolId = "patrol-" + serial,
                Serial = serial,
                OutpostId = o.OutpostId,
                UnitCount = Math.Max(1, (int)Math.Round(Tuning("outpost.patrol_units", 3f))),
                TurnX = o.CellX + (int)Math.Round(Math.Cos(ang) * r),
                TurnY = o.CellY + (int)Math.Round(Math.Sin(ang) * r),
                SpeedMilliPerTick = Math.Max(1, (int)Math.Round(Tuning("outpost.patrol_speed_cells_per_second", 1.5f) * 1000.0 / GameClock.StepHz)),
                RouteState = RouteNeed,
                PosX = o.CellX,
                PosY = o.CellY,
            };
            var list = new List<PatrolRecord>(raids.Patrols ?? Array.Empty<PatrolRecord>()) { p };
            raids.Patrols = list.ToArray();
            return p;
        }

        // ─────────────────────────────── 每步 ───────────────────────────────

        /// <summary>一个固定模拟步（WorldSimulation 在队伍之后调用；tick = 当前步）。</summary>
        public static void Step(CampaignState state, long tick)
        {
            RaidState raids = state?.Raids;
            if (raids == null)
            {
                _lastSteppedTick = tick;
                return;
            }
            OutpostRecord[] outposts = raids.Outposts;
            PatrolRecord[] patrols = raids.Patrols;
            if ((outposts == null || outposts.Length == 0) && (patrols == null || patrols.Length == 0))
            {
                _lastSteppedTick = tick;
                return;
            }
            // 1. 休眠判定（固定周期，步序号决定）。
            if (tick % EvaluateEveryTicks == 0)
            {
                EvaluateDormancy(state, tick);
            }
            // 2. 按距离唤醒的据点：每步至多 N 个（分帧进行）。
            ProcessWakeQueue(state, tick, WakesPerStep);
            // 3. 活跃据点推进这一步（FG8-GEN-02：只遍历醒着的据点列表，休眠的不再逐个跳过——DEBT-FG0ARCH06-06）。
            if (outposts != null)
            {
                List<OutpostRecord> awake = AwakeOutposts(outposts);
                LastSteppedOutposts = awake.Count;
                for (int ai = 0; ai < awake.Count; ai++)
                {
                    OutpostRecord o = awake[ai];
                    if (o == null || o.Destroyed || (o.Dormant && !o.AlwaysSimulate))
                    {
                        continue;
                    }
                    if (o.SimTick < tick)
                    {
                        CatchUp(state, o, tick); // 保险：不应发生（活跃据点每步都推进）。
                    }
                    if (o.SimTick == tick)
                    {
                        if (tick >= o.NextReinforceTick)
                        {
                            // FG8-EXP-03：平定后不再增援；驻军展开迎战期间也不增长（增援点照常前进，与补算逐位一致）。
                            if (o.Garrison < o.GarrisonCap && !o.ReinforceStopped && !o.Engaged)
                            {
                                o.Garrison++;
                                o.ReinforcementsApplied++;
                            }
                            o.NextReinforceTick += Math.Max(1, o.ReinforceIntervalTicks);
                        }
                        o.SimTick = tick + 1;
                    }
                }
            }
            // 4. 巡逻：路线请求与据点是否休眠无关；活跃据点的巡逻前进一步。
            if (patrols != null)
            {
                foreach (PatrolRecord p in patrols)
                {
                    if (p == null)
                    {
                        continue;
                    }
                    OutpostRecord o = Find(state, p.OutpostId);
                    if (o != null && o.Destroyed)
                    {
                        continue; // FG6-DEF-04：据点被摧毁，巡逻停在原地（巡逻与己方交战、清剿在 FG8）。
                    }
                    if (p.RouteState == RouteNeed && !(IsGenerated(o) && o.Dormant && !o.AlwaysSimulate))
                    {
                        RequestPatrolRoute(state, p, o);
                    }
                    if (o == null || (o.Dormant && !o.AlwaysSimulate))
                    {
                        continue;
                    }
                    if (p.Engaged)
                    {
                        continue; // FG8-EXP-03：巡逻展开迎战期间停在原地（单位在家园战斗内核里交战）。
                    }
                    if (p.RouteState == RouteReady && p.RouteReadyTick <= tick && p.LoopMilli > 0)
                    {
                        p.ProgressMilli = (p.ProgressMilli + p.SpeedMilliPerTick) % p.LoopMilli;
                    }
                    UpdatePatrolPosition(p, o);
                }
            }
            _lastSteppedTick = tick;
        }

        /// <summary>
        /// 补算据点在 [SimTick, target) 这些步里应有的变化：增援点 NextReinforceTick + k × 间隔 落在区间里的个数 = 加几个（不超过上限）；
        /// 巡逻进度 + 速度 × 路线就绪以后的步数（模一个来回）。与逐步推进逐位相同（整数运算）。
        /// </summary>
        private static void CatchUp(CampaignState state, OutpostRecord o, long target)
        {
            long last = target - 1;
            long interval = Math.Max(1, o.ReinforceIntervalTicks);
            if (o.NextReinforceTick <= last)
            {
                long k = (last - o.NextReinforceTick) / interval + 1;
                long room = Math.Max(0, o.GarrisonCap - o.Garrison);
                long add = o.ReinforceStopped ? 0 : Math.Min(k, room); // FG8-EXP-03：平定后增援点照常前进、不加人
                o.Garrison += (int)add;
                o.ReinforcementsApplied += add;
                o.NextReinforceTick += k * interval;
            }
            foreach (PatrolRecord p in Patrols(state))
            {
                if (p == null || p.OutpostId != o.OutpostId)
                {
                    continue;
                }
                if (p.RouteState == RouteReady && p.LoopMilli > 0)
                {
                    long from = Math.Max(o.SimTick, p.RouteReadyTick);
                    long count = target - from;
                    if (count > 0)
                    {
                        p.ProgressMilli = (long)(((decimal)p.ProgressMilli + (decimal)p.SpeedMilliPerTick * count) % p.LoopMilli);
                    }
                }
                UpdatePatrolPosition(p, o);
            }
            o.SimTick = Math.Max(o.SimTick, target);
        }

        // ─────────────────────────────── 休眠与唤醒 ───────────────────────────────

        /// <summary>
        /// 休眠判定：离所有己方实体（家园建筑、家园机器）的最近距离 &gt; 休眠距离 + 滞回 → 休眠（立即，从这一步起不再推进）；
        /// 休眠中的据点回到休眠距离以内 → 排队唤醒（原因：己方单位靠近）。
        /// </summary>
        public static void EvaluateDormancy(CampaignState state, long tick)
        {
            OutpostRecord[] outposts = state?.Raids?.Outposts;
            if (outposts == null || outposts.Length == 0)
            {
                return;
            }
            Evaluations++;
            _stateStamp++; // 外部改过 AlwaysSimulate 的（自检）在下一步按新状态重建醒着列表
            CollectOwnPositions(state, OwnScratch);
            double wake = DormantDistance;
            double sleep = wake + WakeHysteresis;
            // FG8-GEN-02（DEBT-FG0ARCH06-06）：己方实体按桶归类（O(己方实体)），每个据点只和离它 sleep 以内的桶比距离——
            // 远处整片外延区的据点一次桶距离比较就判定“远”，不再逐个对全部己方实体开方。结果与逐个比较完全相同（桶外的实体一定更远）。
            BucketOwn(OwnScratch);
            int exact = 0;
            foreach (OutpostRecord o in outposts)
            {
                if (o == null || o.AlwaysSimulate || o.Destroyed)
                {
                    continue;
                }
                double d = MinDistanceBucketed(o.CellX, o.CellY, sleep, ref exact);
                if (!o.Dormant && d > sleep && !o.Engaged) // FG8-EXP-03：驻军展开迎战期间不休眠（收拢后照常判定）
                {
                    o.Dormant = true;
                    o.DormantSinceTick = o.SimTick;
                    SleepsApplied++;
                    DequeueWake(state, o.OutpostId);
                }
                else if (o.Dormant && d <= wake)
                {
                    EnqueueWake(state, o.OutpostId, WakeProximity);
                }
            }
            LastExactChecks = exact;
        }

        private static void BucketOwn(List<Vector2> own)
        {
            foreach (List<Vector2> l in OwnByBucket.Values)
            {
                l.Clear();
                OwnListPool.Add(l);
            }
            OwnByBucket.Clear();
            OwnBuckets.Clear();
            int b = BucketCells;
            for (int i = 0; i < own.Count; i++)
            {
                long key = BucketKey((int)Math.Floor(own[i].x / b), (int)Math.Floor(own[i].y / b));
                if (!OwnByBucket.TryGetValue(key, out List<Vector2> list))
                {
                    if (OwnListPool.Count > 0)
                    {
                        list = OwnListPool[OwnListPool.Count - 1];
                        OwnListPool.RemoveAt(OwnListPool.Count - 1);
                    }
                    else
                    {
                        list = new List<Vector2>(8);
                    }
                    OwnByBucket[key] = list;
                    OwnBuckets.Add(key);
                }
                list.Add(own[i]);
            }
        }

        /// <summary>离最近己方实体的距离；只有离据点 <paramref name="cutoff"/> 以内的桶参与逐个比较，超出返回 double.MaxValue（与逐个比较的判定一致）。</summary>
        private static double MinDistanceBucketed(int x, int y, double cutoff, ref int exact)
        {
            int b = BucketCells;
            double best = double.MaxValue;
            foreach (KeyValuePair<long, List<Vector2>> kv in OwnByBucket)
            {
                int bx = (int)(kv.Key >> 32);
                int by = (int)(kv.Key & 0xFFFFFFFFL);
                double minX = (double)bx * b;
                double minY = (double)by * b;
                double ddx = x < minX ? minX - x : x > minX + b ? x - (minX + b) : 0.0;
                double ddy = y < minY ? minY - y : y > minY + b ? y - (minY + b) : 0.0;
                if (ddx * ddx + ddy * ddy > cutoff * cutoff)
                {
                    continue;
                }
                exact++;
                double d = MinDistance(x, y, kv.Value, cutoff);
                if (d < best)
                {
                    best = d;
                }
            }
            return best;
        }

        private static long BucketKey(int bx, int by) => ((long)bx << 32) | (uint)by;

        /// <summary>醒着（或常驻模拟）、没被摧毁的据点（数组换了或状态变了才重建；顺序 = 数组顺序）。</summary>
        private static List<OutpostRecord> AwakeOutposts(OutpostRecord[] all)
        {
            if (!ReferenceEquals(all, _awakeSource) || _awakeStamp != _stateStamp)
            {
                AwakeList.Clear();
                foreach (OutpostRecord o in all)
                {
                    if (o != null && !o.Destroyed && (!o.Dormant || o.AlwaysSimulate))
                    {
                        AwakeList.Add(o);
                    }
                }
                _awakeSource = all;
                _awakeStamp = _stateStamp;
            }
            return AwakeList;
        }

        /// <summary>
        /// FG8-GEN-02（DEBT-FG0ARCH06-06“表现层每帧对全部巡逻先算位置再剔除”）：离 (<paramref name="x"/>, <paramref name="y"/>) 切比雪夫距离 <paramref name="radius"/> 以内的据点
        /// （按空间桶查，O(覆盖的桶 + 结果数)，与据点总数无关；据点位置不变，桶只在据点数组换了时重建）。追加到 <paramref name="into"/>。
        /// </summary>
        public static void OutpostsNear(CampaignState state, float x, float y, float radius, List<OutpostRecord> into)
        {
            OutpostRecord[] all = state?.Raids?.Outposts;
            if (all == null || all.Length == 0)
            {
                return;
            }
            int b = BucketCells;
            if (!ReferenceEquals(all, _bucketSource))
            {
                foreach (List<OutpostRecord> l in Buckets.Values)
                {
                    l.Clear();
                }
                Buckets.Clear();
                foreach (OutpostRecord o in all)
                {
                    if (o == null)
                    {
                        continue;
                    }
                    long key = BucketKey((int)Math.Floor((double)o.CellX / b), (int)Math.Floor((double)o.CellY / b));
                    if (!Buckets.TryGetValue(key, out List<OutpostRecord> list))
                    {
                        list = new List<OutpostRecord>(4);
                        Buckets[key] = list;
                    }
                    list.Add(o);
                }
                _bucketSource = all;
            }
            int bx0 = (int)Math.Floor((x - radius) / b);
            int by0 = (int)Math.Floor((y - radius) / b);
            int bx1 = (int)Math.Floor((x + radius) / b);
            int by1 = (int)Math.Floor((y + radius) / b);
            for (int by = by0; by <= by1; by++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    if (!Buckets.TryGetValue(BucketKey(bx, by), out List<OutpostRecord> list))
                    {
                        continue;
                    }
                    foreach (OutpostRecord o in list)
                    {
                        if (Math.Abs(o.CellX - x) <= radius && Math.Abs(o.CellY - y) <= radius)
                        {
                            into.Add(o);
                        }
                    }
                }
            }
        }

        /// <summary>己方实体位置：家园建筑的占地中心、家园里存活机器的实时位置（战斗内核）。</summary>
        public static void CollectOwnPositions(CampaignState state, List<Vector2> into)
        {
            into.Clear();
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId)
                {
                    into.Add(new Vector2(b.GridX, b.GridY));
                }
            }
            foreach (Vector2 p in WorldActivity.HomeMachinePositions(state))
            {
                into.Add(p);
            }
        }

        private static double MinDistance(int x, int y, List<Vector2> own, double stopBelow)
        {
            double best = double.MaxValue;
            for (int i = 0; i < own.Count; i++)
            {
                double dx = own[i].x - x;
                double dy = own[i].y - y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best)
                {
                    best = d;
                    if (best < stopBelow * 0.5)
                    {
                        break;
                    }
                }
            }
            return best;
        }

        private static void EnqueueWake(CampaignState state, string outpostId, string reason)
        {
            RaidState raids = state.Raids;
            foreach (string id in raids.PendingWakeIds)
            {
                if (id == outpostId)
                {
                    return;
                }
            }
            var ids = new List<string>(raids.PendingWakeIds) { outpostId };
            var reasons = new List<string>(raids.PendingWakeReasons) { reason };
            raids.PendingWakeIds = ids.ToArray();
            raids.PendingWakeReasons = reasons.ToArray();
        }

        private static void DequeueWake(CampaignState state, string outpostId)
        {
            RaidState raids = state.Raids;
            int at = Array.IndexOf(raids.PendingWakeIds, outpostId);
            if (at < 0)
            {
                return;
            }
            var ids = new List<string>(raids.PendingWakeIds);
            var reasons = new List<string>(raids.PendingWakeReasons);
            ids.RemoveAt(at);
            if (at < reasons.Count)
            {
                reasons.RemoveAt(at);
            }
            raids.PendingWakeIds = ids.ToArray();
            raids.PendingWakeReasons = reasons.ToArray();
        }

        private static void ProcessWakeQueue(CampaignState state, long tick, int max)
        {
            RaidState raids = state.Raids;
            int n = Math.Min(max, raids.PendingWakeIds.Length);
            if (n == 0)
            {
                return;
            }
            for (int i = 0; i < n; i++)
            {
                string id = raids.PendingWakeIds[i];
                string reason = i < raids.PendingWakeReasons.Length ? raids.PendingWakeReasons[i] : WakeProximity;
                OutpostRecord o = Find(state, id);
                if (o != null && o.Dormant)
                {
                    Wake(state, o, tick, reason);
                }
            }
            var ids = new List<string>(raids.PendingWakeIds);
            var reasons = new List<string>(raids.PendingWakeReasons);
            ids.RemoveRange(0, n);
            reasons.RemoveRange(0, Math.Min(n, reasons.Count));
            raids.PendingWakeIds = ids.ToArray();
            raids.PendingWakeReasons = reasons.ToArray();
        }

        private static void Wake(CampaignState state, OutpostRecord o, long target, string reason)
        {
            Watch.Restart();
            CatchUp(state, o, target);
            o.Dormant = false;
            _stateStamp++;
            o.WakeCount++;
            o.LastWakeTick = target;
            o.LastWakeReason = reason ?? string.Empty;
            Watch.Stop();
            WakesProcessed++;
            LastWakeMs = Watch.Elapsed.TotalMilliseconds;
            if (LastWakeMs > MaxWakeMs)
            {
                MaxWakeMs = LastWakeMs;
            }
        }

        /// <summary>
        /// FG6-DEF-04（FGR-GEN-034“摧毁据点会降低这个方向的突袭频率”）：据点被摧毁——不再休眠 / 增援 / 巡逻 / 作为突袭出发地，
        /// 并告诉突袭导演记下这个方向（从那个方向出发的突袭最短间隔变长）。幂等。交战摧毁、掉落与控制度由 FG8-EXP-03 调用本入口。
        /// </summary>
        public static bool MarkDestroyed(CampaignState state, string outpostId)
        {
            OutpostRecord o = Find(state, outpostId);
            if (o == null || o.Destroyed)
            {
                return false;
            }
            if (o.Dormant)
            {
                Wake(state, o, CatchUpTarget(), WakeEvent);
            }
            DequeueWake(state, outpostId);
            o.Destroyed = true;
            _stateStamp++;
            Revision++;
            o.DestroyedTick = GameClock.Ticks;
            o.Garrison = 0;
            o.DestroyCount++;
            o.Engaged = false;
            // 复审 P1：精英残部不是突袭出发地（Plan 也排除它），打掉它不降频；其余据点记下方位，重建时减回（计数 = 当前处于摧毁状态的出发地据点数）。
            o.RaidOctant = o.Kind == KindRemnant ? -1 : Defense.RaidDirectorService.OnOutpostDestroyed(state, o);
            return true;
        }

        /// <summary>立即唤醒（突袭导演调用、事件触发）：补算到“现在”，从下一次推进起完整模拟。已经醒着的什么也不做。</summary>
        public static bool WakeNow(CampaignState state, string outpostId, string reason)
        {
            OutpostRecord o = Find(state, outpostId);
            if (o == null || !o.Dormant)
            {
                return false;
            }
            DequeueWake(state, outpostId);
            Wake(state, o, CatchUpTarget(), reason);
            return true;
        }

        // ─────────────────────────────── 巡逻路线 ───────────────────────────────

        private static void RequestPatrolRoute(CampaignState state, PatrolRecord p, OutpostRecord o)
        {
            if (o == null || !NavService.IsBound)
            {
                return;
            }
            RaidState raids = state.Raids;
            p.NavSerial = raids.NextNavSerial++;
            p.RouteState = RouteAwaiting;
            // 起终点固定（据点格 → 折返点），与巡逻当前在哪无关：休眠与否拿到的是同一条路线。
            NavService.Request(NavService.OwnerPatrol, p.Serial, p.NavSerial, NavConst.ClassHostile,
                new GridCell(o.CellX, o.CellY), new GridCell(p.TurnX, p.TurnY), allowPartial: true);
        }

        /// <summary>寻路服务交回巡逻路线：去程 = 据点格 + 路点；来回长度按千分之一格取整；进度从这一步起为 0。</summary>
        public static bool DeliverRoute(CampaignState state, in NavResult r, long tick)
        {
            PatrolRecord p = null;
            foreach (PatrolRecord q in Patrols(state))
            {
                if (q != null && q.Serial == r.Request.OwnerKey)
                {
                    p = q;
                    break;
                }
            }
            if (p == null || p.RouteState != RouteAwaiting || p.NavSerial != r.Request.Serial)
            {
                return false;
            }
            OutpostRecord o = Find(state, p.OutpostId);
            if (r.Status == NavStatus.Ok || r.Status == NavStatus.Partial)
            {
                var xs = new int[r.PointCount + 1];
                var ys = new int[r.PointCount + 1];
                xs[0] = r.Request.Start.x;
                ys[0] = r.Request.Start.y;
                for (int k = 0; k < r.PointCount; k++)
                {
                    int2 pt = NavService.ResultPoint(r.PointStart + k);
                    xs[k + 1] = pt.x;
                    ys[k + 1] = pt.y;
                }
                p.RouteX = xs;
                p.RouteY = ys;
                p.LoopMilli = 2 * ForwardMilli(p);
                p.ProgressMilli = 0;
                p.RouteState = p.LoopMilli > 0 ? RouteReady : RouteFailed;
                p.RouteReadyTick = tick;
                if (IsGenerated(o) && p.LoopMilli > 0)
                {
                    // 按需路线（见 IsGenerated）：视为第 0 步就绪；进度 = 速度 × 据点已模拟到的步数（醒着 = 到这一步为止，休眠 = 到休眠那一刻，之后由补算接上）。
                    p.RouteReadyTick = 0;
                    p.ProgressMilli = (long)(((decimal)p.SpeedMilliPerTick * Math.Max(0L, o.SimTick)) % p.LoopMilli);
                }
                p.NavReason = r.Status == NavStatus.Partial ? (int)r.Reason : 0;
            }
            else
            {
                p.RouteState = RouteFailed;
                p.NavReason = (int)r.Reason;
                p.RouteReadyTick = -1;
                p.ProgressMilli = 0;
            }
            if (o != null)
            {
                UpdatePatrolPosition(p, o);
            }
            return true;
        }

        private static long SegmentMilli(int x0, int y0, int x1, int y1)
        {
            double dx = x1 - x0;
            double dy = y1 - y0;
            return (long)Math.Round(Math.Sqrt(dx * dx + dy * dy) * 1000.0);
        }

        private static long ForwardMilli(PatrolRecord p)
        {
            long sum = 0;
            for (int k = 1; k < (p.RouteX?.Length ?? 0); k++)
            {
                sum += SegmentMilli(p.RouteX[k - 1], p.RouteY[k - 1], p.RouteX[k], p.RouteY[k]);
            }
            return sum;
        }

        /// <summary>地形变化后：路线被新障碍截断的巡逻重新要路线（进度归零，回到据点重新出发——与是否休眠无关，两边一致）。</summary>
        public static int InvalidateRoutes(CampaignState state)
        {
            int n = 0;
            var pts = new List<int2>(32);
            foreach (PatrolRecord p in Patrols(state))
            {
                if (p == null || p.RouteState != RouteReady || (p.RouteX?.Length ?? 0) < 2)
                {
                    continue;
                }
                pts.Clear();
                for (int k = 1; k < p.RouteX.Length; k++)
                {
                    pts.Add(new int2(p.RouteX[k], p.RouteY[k]));
                }
                if (!NavService.RouteClear(new int2(p.RouteX[0], p.RouteY[0]), pts, 0, NavConst.ClassHostile))
                {
                    p.RouteState = RouteNeed;
                    p.ProgressMilli = 0;
                    p.RouteReadyTick = -1;
                    OutpostRecord o = Find(state, p.OutpostId);
                    if (o != null)
                    {
                        UpdatePatrolPosition(p, o);
                    }
                    n++;
                }
            }
            return n;
        }

        public static void ReissueAwaiting(CampaignState state)
        {
            foreach (PatrolRecord p in Patrols(state))
            {
                if (p != null && p.RouteState == RouteAwaiting)
                {
                    p.RouteState = RouteNeed;
                }
            }
        }

        // ─────────────────────────────── 位置与只读查看 ───────────────────────────────

        private static void UpdatePatrolPosition(PatrolRecord p, OutpostRecord o)
        {
            PositionAt(p, o, p.ProgressMilli, out p.PosX, out p.PosY);
        }

        /// <summary>巡逻在给定进度下的位置：去程按路线前进，回程原路返回；路线没就绪时在据点上。</summary>
        public static void PositionAt(PatrolRecord p, OutpostRecord o, long progressMilli, out double x, out double y)
        {
            int n = p.RouteX?.Length ?? 0;
            if (p.RouteState != RouteReady || n < 2 || p.LoopMilli <= 0)
            {
                x = o != null ? o.CellX : p.PosX;
                y = o != null ? o.CellY : p.PosY;
                return;
            }
            long forward = p.LoopMilli / 2;
            long s = progressMilli % p.LoopMilli;
            if (s > forward)
            {
                s = p.LoopMilli - s;
            }
            for (int k = 1; k < n; k++)
            {
                long seg = SegmentMilli(p.RouteX[k - 1], p.RouteY[k - 1], p.RouteX[k], p.RouteY[k]);
                if (s <= seg || k == n - 1)
                {
                    double t = seg > 0 ? Math.Min(1.0, (double)s / seg) : 0.0;
                    x = p.RouteX[k - 1] + (p.RouteX[k] - p.RouteX[k - 1]) * t;
                    y = p.RouteY[k - 1] + (p.RouteY[k] - p.RouteY[k - 1]) * t;
                    return;
                }
                s -= seg;
            }
            x = p.RouteX[n - 1];
            y = p.RouteY[n - 1];
        }

        /// <summary>
        /// 只读查看（表现层、情报）：休眠据点按补算规则现算“此刻”的驻军与下次增援，不改任何状态（观察不改变结果）。醒着的据点就是记录本身。
        /// </summary>
        public static void Peek(CampaignState state, OutpostRecord o, out int garrison, out long nextReinforceTick)
        {
            garrison = o.Garrison;
            nextReinforceTick = o.NextReinforceTick;
            if (!o.Dormant || o.AlwaysSimulate)
            {
                return;
            }
            long last = CatchUpTarget() - 1;
            long interval = Math.Max(1, o.ReinforceIntervalTicks);
            if (o.NextReinforceTick <= last)
            {
                long k = (last - o.NextReinforceTick) / interval + 1;
                garrison = o.ReinforceStopped ? o.Garrison : (int)Math.Min(o.GarrisonCap, o.Garrison + k);
                nextReinforceTick = o.NextReinforceTick + k * interval;
            }
        }

        /// <summary>巡逻“此刻”的位置（休眠时现算，不改状态）。</summary>
        public static Vector2 PeekPatrolPosition(CampaignState state, PatrolRecord p)
        {
            OutpostRecord o = Find(state, p.OutpostId);
            if (o == null || !o.Dormant || o.AlwaysSimulate || p.RouteState != RouteReady || p.LoopMilli <= 0)
            {
                return new Vector2((float)p.PosX, (float)p.PosY);
            }
            long target = CatchUpTarget();
            long from = Math.Max(o.SimTick, p.RouteReadyTick);
            long count = Math.Max(0, target - from);
            long progress = (long)(((decimal)p.ProgressMilli + (decimal)p.SpeedMilliPerTick * count) % p.LoopMilli);
            PositionAt(p, o, progress, out double x, out double y);
            return new Vector2((float)x, (float)y);
        }

        // ─────────────────────────────── FG8-EXP-03：平定、重建、巡逻撤离、精英残部 ───────────────────────────────

        /// <summary>醒着的据点（每步推进的那些；迎战检查只看它们——休眠的离己方实体都很远，不可能有机器走近）。</summary>
        public static IReadOnlyList<OutpostRecord> Awake(CampaignState state)
        {
            OutpostRecord[] all = state?.Raids?.Outposts;
            return all == null || all.Length == 0 ? (IReadOnlyList<OutpostRecord>)Array.Empty<OutpostRecord>() : AwakeOutposts(all);
        }

        /// <summary>
        /// 首领被击败（领地平定）：这个据点不再增援。休眠中的先按确定性规则补算到“现在”（之前的增援照算），再停——之后无论何时醒来，
        /// 补算的结果都与“一直在模拟”逐位一致（FGR-BASE-021）。幂等。
        /// </summary>
        public static bool StopReinforcement(CampaignState state, OutpostRecord o)
        {
            if (state == null || o == null || o.ReinforceStopped)
            {
                return false;
            }
            if (o.Dormant && !o.AlwaysSimulate && !o.Destroyed)
            {
                CatchUp(state, o, CatchUpTarget());
            }
            o.ReinforceStopped = true;
            return true;
        }

        /// <summary>
        /// 阵营增援重建一个被摧毁的据点（FGR-EXP-022“被摧毁的据点有可能重建”）：驻军 = <paramref name="garrison"/>，增援点接回原来的网格（出生步 + k × 间隔），
        /// 从“现在”起按休眠处理（离己方实体近的下一次休眠判定就会醒来）；生成时带巡逻、巡逻已被歼灭的重新派一支。返回是否重建了。
        /// </summary>
        public static bool Rebuild(CampaignState state, string outpostId, int garrison)
        {
            OutpostRecord o = Find(state, outpostId);
            if (o == null || !o.Destroyed)
            {
                return false;
            }
            long now = CatchUpTarget();
            long interval = Math.Max(1, o.ReinforceIntervalTicks);
            o.Destroyed = false;
            o.DestroyedTick = -1;
            o.Engaged = false;
            o.UnfoldedCount = 0;
            o.Garrison = Math.Max(1, Math.Min(o.GarrisonCap, garrison));
            o.NextReinforceTick = (now / interval + 1) * interval;
            o.SimTick = now;
            o.Dormant = !o.AlwaysSimulate;
            o.DormantSinceTick = now;
            o.RebuildCount++;
            if (o.RaidOctant >= 0)
            {
                Defense.RaidDirectorService.OnOutpostRebuilt(state, o.RaidOctant);
                o.RaidOctant = -1;
            }
            _stateStamp++;
            Revision++;
            if (o.PatrolRadius > 0)
            {
                bool hasPatrol = false;
                foreach (PatrolRecord p in Patrols(state))
                {
                    if (p != null && p.OutpostId == o.OutpostId)
                    {
                        hasPatrol = true;
                        break;
                    }
                }
                if (!hasPatrol)
                {
                    SpawnPatrol(state, o, o.PatrolRadius);
                }
            }
            return true;
        }

        /// <summary>移除符合条件的巡逻（领地收复时敌方巡逻撤离、巡逻被歼灭）。返回移除了几支。</summary>
        public static int RemovePatrols(CampaignState state, Func<PatrolRecord, bool> match)
        {
            PatrolRecord[] all = state?.Raids?.Patrols;
            if (all == null || all.Length == 0 || match == null)
            {
                return 0;
            }
            var keep = new List<PatrolRecord>(all.Length);
            foreach (PatrolRecord p in all)
            {
                if (p != null && !match(p))
                {
                    keep.Add(p);
                }
            }
            int removed = all.Length - keep.Count;
            if (removed > 0)
            {
                state.Raids.Patrols = keep.ToArray();
                Revision++;
            }
            return removed;
        }

        /// <summary>
        /// 已收复领地里的一批精英残部（FGR-EXP-022）：作为一个据点登记在 <paramref name="cell"/>（不增援、不巡逻、不算控制度），醒着出生，
        /// 离己方实体远的下一次休眠判定就睡下。ID 由调用方按（领地, 序号）给出，确定。已存在的返回原记录。
        /// </summary>
        public static OutpostRecord SpawnRemnant(CampaignState state, string remnantId, string territoryId, GridCell cell, int tier, int count)
        {
            if (state == null || string.IsNullOrEmpty(remnantId))
            {
                return null;
            }
            OutpostRecord existing = Find(state, remnantId);
            if (existing != null)
            {
                return existing;
            }
            CampaignFgStateDomains.EnsureAll(state);
            long now = CatchUpTarget();
            long interval = Math.Max(1L, (long)Math.Round(Tuning("outpost.reinforce_interval_days", 3f) * GameClock.DaySeconds * GameClock.StepHz));
            var o = new OutpostRecord
            {
                OutpostId = remnantId,
                TerritoryId = territoryId ?? string.Empty,
                Kind = KindRemnant,
                Tier = Math.Max(1, tier),
                CellX = cell.X,
                CellY = cell.Y,
                Garrison = Math.Max(1, count),
                GarrisonCap = Math.Max(1, count),
                ReinforceIntervalTicks = interval,
                NextReinforceTick = (now / interval + 1) * interval,
                SimTick = now,
                Dormant = false,
                LastWakeReason = string.Empty,
                ReinforceStopped = true,
                PatrolRadius = -1,
            };
            var list = new List<OutpostRecord>(state.Raids.Outposts ?? Array.Empty<OutpostRecord>()) { o };
            state.Raids.Outposts = list.ToArray();
            _stateStamp++;
            Revision++;
            return o;
        }

        /// <summary>醒着的巡逻所在区块（“行进中的巡逻”所在区块完整模拟，FGR-GEN-052 第 1 条）。</summary>
        public static void AddActivePatrolChunks(CampaignState state, int chunkSize, HashSet<long> into)
        {
            foreach (PatrolRecord p in Patrols(state))
            {
                if (p == null)
                {
                    continue;
                }
                OutpostRecord o = Find(state, p.OutpostId);
                if (o == null || o.Destroyed || (o.Dormant && !o.AlwaysSimulate))
                {
                    continue;
                }
                GridCell c = NavService.CellOf(p.PosX, p.PosY);
                ChunkAddress a = GridMath.Address(c, chunkSize);
                into.Add(HomeGridMap.Key(a.ChunkX, a.ChunkY));
            }
        }
    }
}
