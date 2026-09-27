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

        /// <summary>接到一个战役（新建 / 读档）时清零会话态。</summary>
        public static void ResetSession()
        {
            _lastSteppedTick = -1;
            ById.Clear();
            _indexed = null;
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
            if (withPatrol)
            {
                SpawnPatrol(state, o);
            }
            return o;
        }

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

        private static PatrolRecord SpawnPatrol(CampaignState state, OutpostRecord o)
        {
            RaidState raids = state.Raids;
            int serial = raids.NextPatrolSerial++;
            uint seed = unchecked((uint)(state.World?.WorldSeed ?? state.RandomSeed));
            uint h = WorldGenMath.Hash(seed, serial, o.CellX ^ o.CellY, 0x50415452u); // "PATR"
            double ang = (h & 0xFFFF) / 65536.0 * 2.0 * Math.PI;
            double r = Math.Max(2.0, Tuning("outpost.patrol_radius_cells", 24f));
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
            // 3. 活跃据点推进这一步。
            if (outposts != null)
            {
                foreach (OutpostRecord o in outposts)
                {
                    if (o == null || (o.Dormant && !o.AlwaysSimulate))
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
                            if (o.Garrison < o.GarrisonCap)
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
                    if (p.RouteState == RouteNeed)
                    {
                        RequestPatrolRoute(state, p, o);
                    }
                    if (o == null || (o.Dormant && !o.AlwaysSimulate))
                    {
                        continue;
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
                long add = Math.Min(k, room);
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
            CollectOwnPositions(state, OwnScratch);
            double wake = DormantDistance;
            double sleep = wake + WakeHysteresis;
            foreach (OutpostRecord o in outposts)
            {
                if (o == null || o.AlwaysSimulate)
                {
                    continue;
                }
                double d = MinDistance(o.CellX, o.CellY, OwnScratch, sleep);
                if (!o.Dormant && d > sleep)
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
        public static bool DeliverRoute(CampaignState state, in NavResult r, NavKernel kernel, long tick)
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
                    int2 pt = kernel.ResultPoint(r.PointStart + k);
                    xs[k + 1] = pt.x;
                    ys[k + 1] = pt.y;
                }
                p.RouteX = xs;
                p.RouteY = ys;
                p.LoopMilli = 2 * ForwardMilli(p);
                p.ProgressMilli = 0;
                p.RouteState = p.LoopMilli > 0 ? RouteReady : RouteFailed;
                p.RouteReadyTick = tick;
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
        public static int InvalidateRoutes(CampaignState state, NavKernel kernel)
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
                if (!kernel.RouteClear(new int2(p.RouteX[0], p.RouteY[0]), pts, 0, NavConst.ClassHostile))
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
                garrison = (int)Math.Min(o.GarrisonCap, o.Garrison + k);
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
                if (o == null || (o.Dormant && !o.AlwaysSimulate))
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
