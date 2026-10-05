using System;
using System.Collections.Generic;
using BinGames.Sim.Nav;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002“行进中的突袭与巡逻同时运行”）/ FG0-ARCH-06（FGR-ARC-015 沿地形行进）：星球表面上行进中的队伍（聚合体）。
    ///
    /// - 纯数据：队伍 = 人数 + 双精度格坐标 + 目标 + 速度 + 路线，存在 <see cref="RaidState.InTransit"/>，由 <see cref="WorldSimulation"/>
    ///   按统一时钟的固定步推进。**没有表现对象也照常推进**，镜头在不在旁边结果都一样（FGR-BASE-021）；表现层只读这里的位置。
    /// - 沿地形行进（FG0-ARCH-06）：出发后向寻路服务要一条路线（敌方类别，允许部分路线），按固定延迟拿到后沿路点走；
    ///   路线中途被新建筑截断时从当前位置重新规划；通往核心的路被完全堵住时走到最近处停下，“突袭到达”通知里写明原因（攻城在 FG6-DEF-05）。
    ///   寻路服务没有绑定（没有家园的自检场景）时退回直线。
    /// - 确定性：队伍 ID、寻路序号按存档里的计数递增（不用 GUID）；路线在固定的采纳步交到。
    /// - 出发点按种子生成的规划层取（领地中心朝家园方向的边缘），或从敌方据点出发（<see cref="DispatchRaidFromOutpost"/>，会唤醒据点）。
    /// 每步开销 O(队伍数)（队伍是聚合体；逐单位模拟在战斗内核里）。
    /// </summary>
    public static class WorldTransitSystem
    {
        public const int RouteNeed = 0;
        public const int RouteAwaiting = 1;
        public const int RouteFollowing = 2;
        public const int RouteFailed = 3;

        /// <summary>派遣次数、到达次数（自检 / 统计用）。</summary>
        public static int DispatchCount { get; private set; }
        public static int ArrivalCount { get; private set; }

        public static IReadOnlyList<TransitGroupRecord> Groups(CampaignState state) =>
            (IReadOnlyList<TransitGroupRecord>)state?.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>();

        /// <summary>派出一支突袭：从 <paramref name="territoryId"/> 领地朝家园核心一侧的边缘出发，目标是归还核心枢轴格。
        /// 领地不存在时返回 null 并给出原因（不静默）。</summary>
        public static TransitGroupRecord DispatchRaidFromTerritory(CampaignState state, string territoryId, int unitCount, out string failure)
        {
            failure = null;
            if (state == null)
            {
                failure = "no-campaign";
                return null;
            }
            WorldPlan plan = WorldGenService.PlanFor(state);
            PlannedTerritory t = plan?.Find(territoryId);
            if (t == null)
            {
                failure = "unknown-territory:" + territoryId;
                return null;
            }
            GridCell core = HomeGridService.CorePivot(state);
            double dx = core.X - t.CenterX;
            double dy = core.Y - t.CenterY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ox = t.CenterX;
            double oy = t.CenterY;
            if (len > 1e-6)
            {
                double edge = Math.Min(t.OuterRadius, len * 0.5);
                ox += dx / len * edge;
                oy += dy / len * edge;
            }
            return Dispatch(state, TransitGroupKind.Raid, territoryId, unitCount, ox, oy, core.X, core.Y);
        }

        /// <summary>
        /// FG0-ARCH-06：从敌方据点派出一支突袭（“突袭导演调用”——FG6-DEF-04 的正式入口会调这里）：先按确定性规则唤醒据点（补算休眠期间的增援），
        /// 再从驻军里抽人（至少留 1 台守家，驻军不够时如实失败），从据点所在格出发，目标是归还核心。
        /// </summary>
        public static TransitGroupRecord DispatchRaidFromOutpost(CampaignState state, string outpostId, int unitCount, out string failure)
        {
            failure = null;
            OutpostRecord o = WorldOutpostSystem.Find(state, outpostId);
            if (o == null)
            {
                failure = "unknown-outpost:" + outpostId;
                return null;
            }
            WorldOutpostSystem.WakeNow(state, outpostId, WorldOutpostSystem.WakeRaid);
            int take = Math.Min(Math.Max(1, unitCount), o.Garrison - 1);
            if (take <= 0)
            {
                failure = "garrison-too-small:" + o.Garrison;
                return null;
            }
            o.Garrison -= take;
            GridCell core = HomeGridService.CorePivot(state);
            return Dispatch(state, TransitGroupKind.Raid, o.TerritoryId, take, o.CellX, o.CellY, core.X, core.Y);
        }

        /// <summary>突袭部队的行进速度（格 / 游戏秒，transit.raid_speed_cells_per_second）——导演算预警时间与队伍推进用同一个值。</summary>
        public static float RaidSpeed => Math.Max(0.01f, GameClock.TuningOr("transit.raid_speed_cells_per_second", 1.5f));

        /// <summary>到达半径（格，transit.arrival_radius_cells）。</summary>
        public static double ArrivalRadius => Math.Max(0.0, GameClock.TuningOr("transit.arrival_radius_cells", 12f));

        /// <summary>
        /// FG6-DEF-04（FGR-DEF-023）：按突袭计划派出部队——出发据点先按确定性规则唤醒（补算休眠期间的增援）；据点是集结点：驻军里最多抽出（驻军 − 1）台随队，
        /// 其余由阵营后方补足（编成按预算，不受一座侦察巢的驻军限制）；抽走的驻军在撤回时还给据点。队伍带上计划的路线（排定时已经沿地形算好）：
        /// 路线这期间被新建筑截断时从出发地重新要路线（预计抵达随之变化，预警条读队伍的实时预计）。
        /// </summary>
        public static TransitGroupRecord DispatchPlanned(CampaignState state, RaidPlanRecord plan)
        {
            if (state == null || plan == null)
            {
                return null;
            }
            int take = 0;
            string outpostId = string.Empty;
            if (plan.OriginKind == Defense.RaidDirectorService.OriginOutpost)
            {
                OutpostRecord o = WorldOutpostSystem.Find(state, plan.OriginId);
                if (o != null && !o.Destroyed)
                {
                    WorldOutpostSystem.WakeNow(state, o.OutpostId, WorldOutpostSystem.WakeRaid);
                    take = Math.Min(plan.UnitTotal, Math.Max(0, o.Garrison - 1));
                    o.Garrison -= take;
                    outpostId = o.OutpostId;
                }
            }
            TransitGroupRecord g = Dispatch(state, TransitGroupKind.Raid, plan.Faction, plan.UnitTotal, plan.OriginX, plan.OriginY, plan.TargetX, plan.TargetY);
            g.PlanId = plan.PlanId;
            g.Wave = plan.Wave;
            g.Faction = plan.Faction ?? string.Empty;
            g.OutpostId = outpostId;
            g.GarrisonTaken = take;
            g.TargetKind = plan.TargetKind ?? string.Empty;
            g.UnitIds = (string[])(plan.UnitIds ?? Array.Empty<string>()).Clone();
            g.UnitCounts = (int[])(plan.UnitCounts ?? Array.Empty<int>()).Clone();
            g.EliteCounts = (int[])(plan.EliteCounts ?? Array.Empty<int>()).Clone();
            int n = plan.RouteX?.Length ?? 0;
            if (plan.RouteState == Defense.RaidDirectorService.RouteReady && n > 0 && n == (plan.RouteY?.Length ?? 0))
            {
                bool clear = true;
                if (NavService.IsBound && ReferenceEquals(NavService.BoundState, state))
                {
                    RouteScratch.Clear();
                    for (int k = 0; k < n; k++)
                    {
                        RouteScratch.Add(new int2(plan.RouteX[k], plan.RouteY[k]));
                    }
                    GridCell c = NavService.CellOf(g.PosX, g.PosY);
                    clear = NavService.RouteClear(new int2(c.X, c.Y), RouteScratch, 0, NavConst.ClassHostile);
                }
                if (clear)
                {
                    g.RouteX = (int[])plan.RouteX.Clone();
                    g.RouteY = (int[])plan.RouteY.Clone();
                    g.RouteIndex = 0;
                    g.RouteState = RouteFollowing;
                    g.NavReason = plan.NavReason;
                }
            }
            return g;
        }

        /// <summary>派出一支队伍（格坐标，双精度）。速度取 transit.raid_speed_cells_per_second。</summary>
        public static TransitGroupRecord Dispatch(CampaignState state, TransitGroupKind kind, string originId, int unitCount,
            double fromX, double fromY, double toX, double toY)
        {
            CampaignFgStateDomains.EnsureAll(state);
            RaidState raids = state.Raids;
            var g = new TransitGroupRecord
            {
                GroupId = "transit-" + raids.NextGroupSerial,
                NavKey = raids.NextGroupSerial,
                Kind = kind,
                State = TransitGroupState.Marching,
                OriginId = originId ?? string.Empty,
                UnitCount = Math.Max(1, unitCount),
                PosX = fromX,
                PosY = fromY,
                TargetX = toX,
                TargetY = toY,
                Speed = RaidSpeed,
                DispatchedAtTick = GameClock.Ticks,
                ArrivedAtTick = -1,
                RouteState = RouteNeed,
                // FG6-DEF-04：记下出发地（撤退时沿原路回到这里）。
                HasOrigin = true,
                OriginX = fromX,
                OriginY = fromY,
            };
            raids.NextGroupSerial++;
            var list = new List<TransitGroupRecord>(raids.InTransit ?? Array.Empty<TransitGroupRecord>()) { g };
            raids.InTransit = list.ToArray();
            DispatchCount++;
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstRaidInTransit);
            return g;
        }

        private static int KeyOf(TransitGroupRecord g)
        {
            if (g.NavKey > 0)
            {
                return g.NavKey;
            }
            // FG0-ARCH-06 之前的存档：键 = GroupId 里的序号。
            if (g.GroupId != null && g.GroupId.StartsWith("transit-", StringComparison.Ordinal)
                && int.TryParse(g.GroupId.Substring("transit-".Length), out int n))
            {
                g.NavKey = n;
            }
            return g.NavKey;
        }

        /// <summary>一个固定模拟步：行进中的队伍沿路线走 speed × dt 格；进入到达半径即到达（发一次紧急通知，定位到队伍位置）。</summary>
        public static void Step(CampaignState state, float dt)
        {
            TransitGroupRecord[] groups = state?.Raids?.InTransit;
            if (groups == null || groups.Length == 0 || dt <= 0f)
            {
                return;
            }
            double arrival = Math.Max(0.0, GameClock.TuningOr("transit.arrival_radius_cells", 12f));
            bool nav = NavService.IsBound && ReferenceEquals(NavService.BoundState, state);
            int arrivalsBefore = ArrivalCount;
            int withdrawalsBefore = WithdrawalCount;
            try
            {
                StepGroups(state, groups, arrival, nav, dt);
            }
            finally
            {
                if (Finished.Count > 0)
                {
                    RemoveFinished(state);
                }
                if (ArrivalCount != arrivalsBefore || WithdrawalCount != withdrawalsBefore)
                {
                    // FG4-ECO-06：突袭部队到达家园 = 突袭开始、开始撤退 = 突袭结束——战时预案在下一个模拟步立刻检查（不等整分钟）。
                    Economy.StandingRuleService.NotifyRaidArrived(state);
                }
            }
        }

        /// <summary>FG6-DEF-04：开始撤退 / 撤回出发地离场的次数（统计 / 自检）。</summary>
        public static int WithdrawalCount { get; private set; }
        public static int RetreatsCompleted { get; private set; }

        private static readonly List<TransitGroupRecord> Finished = new List<TransitGroupRecord>(4);

        /// <summary>到达后的时间上限（步；FGR-DEF-032 初值 1 个游戏小时）。</summary>
        public static long TimeLimitTicks => GameClock.TicksFor(Defense.RaidCatalog.TimeLimitHours * GameClock.DaySeconds / 24.0);

        private static void StepGroups(CampaignState state, TransitGroupRecord[] groups, double arrival, bool nav, float dt)
        {
            long limit = TimeLimitTicks;
            for (int i = 0; i < groups.Length; i++)
            {
                TransitGroupRecord g = groups[i];
                if (g == null || g.Intercepted)
                {
                    continue; // FG6-DEF-05（DEBT-FG6DEF04-03）：被拦截、就地展开交战的队伍不推进（收拢后接着走）
                }
                if (g.State == TransitGroupState.Arrived)
                {
                    // FG6-DEF-04（FGR-DEF-032 时间上限）：攻城行为（FG6-DEF-05）接管之前，到达的聚合体在外围停留到时间上限后沿原路撤回出发地。
                    if (!g.Engaged && limit > 0 && g.ArrivedAtTick >= 0 && GameClock.Ticks - g.ArrivedAtTick >= limit)
                    {
                        BeginRetreat(state, g);
                    }
                    continue;
                }
                if (g.State == TransitGroupState.Retreating)
                {
                    StepRetreat(g, dt);
                    continue;
                }
                if (g.State != TransitGroupState.Marching)
                {
                    continue;
                }
                double dx = g.TargetX - g.PosX;
                double dy = g.TargetY - g.PosY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (!nav)
                {
                    StepStraight(g, dx, dy, dist, arrival, dt);
                    continue;
                }
                if (dist <= arrival)
                {
                    Arrive(g, blocked: false);
                    continue;
                }
                switch (g.RouteState)
                {
                    case RouteNeed:
                        RequestRoute(state, g);
                        continue;
                    case RouteAwaiting:
                        continue;
                    case RouteFailed:
                        // 起点被围死 / 超出范围……：走不了，按“到达（受阻）”结算并说明原因（不原地发呆）。
                        g.Blocked = true;
                        Arrive(g, blocked: true);
                        continue;
                }
                if (FollowRoute(g, g.Speed * (double)dt, arrival))
                {
                    double ex = g.TargetX - g.PosX;
                    double ey = g.TargetY - g.PosY;
                    bool reached = Math.Sqrt(ex * ex + ey * ey) <= arrival + 1e-6;
                    g.Blocked = !reached;
                    Arrive(g, blocked: !reached);
                }
            }
        }

        private static void StepStraight(TransitGroupRecord g, double dx, double dy, double dist, double arrival, float dt)
        {
            double remaining = dist - arrival;
            double step = g.Speed * (double)dt;
            if (remaining <= step)
            {
                if (dist > 1e-9 && remaining > 0)
                {
                    g.PosX += dx / dist * remaining;
                    g.PosY += dy / dist * remaining;
                }
                Arrive(g, blocked: false);
                return;
            }
            g.PosX += dx / dist * step;
            g.PosY += dy / dist * step;
        }

        // ─────────────────────────────── 撤退（FG6-DEF-04 / FGR-DEF-032 时间上限）───────────────────────────────

        /// <summary>
        /// 开始撤退：沿来时的路线原路返回出发地（已走过的路点倒序 + 出发格），途中照常是星球上的聚合体（可以被追击，追击属于 FG6-DEF-05 / FG8）。
        /// FG6-DEF-04 之前的存档没有出发地记录：直接离场（如实通知）。发一条“突袭部队撤退”通知；战时预案下一步检查（突袭结束）。
        /// </summary>
        public static void BeginRetreat(CampaignState state, TransitGroupRecord g) => BeginRetreat(state, g, null);

        /// <summary>FG6-DEF-05：攻城部队撤出家园后并回行进队伍，沿原路回出发地——通知改写成 <paramref name="notifyText"/>（“N 台敌人撤出家园”），类型用攻城进展 raid_siege——撤退令已经发过一条 raid_withdrawn，同类型会合并成“2 支突袭部队撤退”误导玩家。</summary>
        public static void BeginRetreat(CampaignState state, TransitGroupRecord g, string notifyText)
        {
            if (g == null || g.State == TransitGroupState.Retreating)
            {
                return;
            }
            double stayed = g.ArrivedAtTick >= 0 ? Math.Max(0, GameClock.Ticks - g.ArrivedAtTick) / (double)GameClock.StepHz : 0;
            g.State = TransitGroupState.Retreating;
            g.RetreatTick = GameClock.Ticks;
            WithdrawalCount++;
            GuidanceHooks.Raise(GuidanceHooks.RaidFirstWithdrawn);
            string where = OriginName(g.OriginId);
            NotificationCenter.Post(notifyText != null ? "raid_siege" : "raid_withdrawn", notifyText ?? GameText.Format("raid.withdrawn.notify", g.UnitCount.ToString(),
                GameClock.FormatGameDuration(stayed), string.IsNullOrEmpty(where) ? GameText.Get("raid.withdrawn.home") : where),
                new Vector3((float)g.PosX, 0f, (float)g.PosY));
            if (!g.HasOrigin)
            {
                Finished.Add(g);
                return;
            }
            // 原路 = 当前路线已走过的路点倒序 + 行军途中重新要路线之前走过的路点（含重新规划那一刻所在的格）倒序 + 出发格（复审 P2：路线中途重算过也不走直线穿地形）。
            int walked = Math.Min(Math.Max(0, g.RouteIndex), g.RouteX?.Length ?? 0);
            int trail = Math.Min(g.TrailX?.Length ?? 0, g.TrailY?.Length ?? 0);
            var xs = new int[walked + trail + 1];
            var ys = new int[walked + trail + 1];
            for (int k = 0; k < walked; k++)
            {
                xs[k] = g.RouteX[walked - 1 - k];
                ys[k] = g.RouteY[walked - 1 - k];
            }
            for (int k = 0; k < trail; k++)
            {
                xs[walked + k] = g.TrailX[trail - 1 - k];
                ys[walked + k] = g.TrailY[trail - 1 - k];
            }
            GridCell o = NavService.CellOf(g.OriginX, g.OriginY);
            xs[walked + trail] = o.X;
            ys[walked + trail] = o.Y;
            g.TrailX = Array.Empty<int>();
            g.TrailY = Array.Empty<int>();
            g.RouteX = xs;
            g.RouteY = ys;
            g.RouteIndex = 0;
            g.RouteState = RouteFollowing;
            g.TargetX = g.OriginX;
            g.TargetY = g.OriginY;
        }

        /// <summary>FG6-DEF-05：队伍被全歼——从星球上移除（抽走的驻军不还给据点：都死了）。返回是否移除了。</summary>
        public static bool RemoveDestroyed(CampaignState state, TransitGroupRecord g)
        {
            RaidState raids = state?.Raids;
            if (raids?.InTransit == null || g == null)
            {
                return false;
            }
            var list = new List<TransitGroupRecord>(raids.InTransit.Length);
            bool removed = false;
            foreach (TransitGroupRecord x in raids.InTransit)
            {
                if (ReferenceEquals(x, g))
                {
                    removed = true;
                    continue;
                }
                list.Add(x);
            }
            if (removed)
            {
                raids.InTransit = list.ToArray();
                DestroyedCount++;
            }
            return removed;
        }

        /// <summary>FG6-DEF-05：被全歼的队伍数（统计 / 自检）。</summary>
        public static int DestroyedCount { get; private set; }

        private static void StepRetreat(TransitGroupRecord g, float dt)
        {
            if (FollowRoute(g, g.Speed * (double)dt, 0.5))
            {
                Finished.Add(g);
            }
        }

        /// <summary>撤回出发地的队伍离场：从据点抽走的驻军还给据点（据点还在、没被摧毁时；不超过上限）。</summary>
        private static void RemoveFinished(CampaignState state)
        {
            RaidState raids = state.Raids;
            var list = new List<TransitGroupRecord>(raids.InTransit.Length);
            foreach (TransitGroupRecord g in raids.InTransit)
            {
                if (g != null && Finished.Contains(g))
                {
                    if (!string.IsNullOrEmpty(g.OutpostId) && g.GarrisonTaken > 0)
                    {
                        OutpostRecord o = WorldOutpostSystem.Find(state, g.OutpostId);
                        if (o != null && !o.Destroyed)
                        {
                            o.Garrison = Math.Min(o.GarrisonCap, o.Garrison + g.GarrisonTaken);
                        }
                    }
                    RetreatsCompleted++;
                    continue;
                }
                list.Add(g);
            }
            raids.InTransit = list.ToArray();
            Finished.Clear();
        }

        // ─────────────────────────────── 路线几何（导演算预警时间与队伍推进同一套）───────────────────────────────

        /// <summary>
        /// 从 (<paramref name="px"/>, <paramref name="py"/>) 沿路点 [<paramref name="from"/>, n) 走到第一次进入以 (<paramref name="tx"/>, <paramref name="ty"/>) 为圆心、
        /// <paramref name="arrival"/> 为半径的圆要走的长度，以及那一点；路线走完都进不去时 = 路线全长、点 = 路线末端（再按直线补到圆上，见 <see cref="PredictArrival"/>）。
        /// 与 <see cref="DistanceUntilArrival(TransitGroupRecord,double)"/> 同一套几何（逐段解 |p + t·d − c|² = r²）。
        /// </summary>
        public static double PathUntilArrival(double px, double py, int[] rx, int[] ry, int from, double tx, double ty, double arrival, out double ax, out double ay, out bool entered)
        {
            double sum = 0;
            double r2 = arrival * arrival;
            entered = false;
            ax = px;
            ay = py;
            if ((px - tx) * (px - tx) + (py - ty) * (py - ty) <= r2)
            {
                entered = true;
                return 0;
            }
            int n = Math.Min(rx?.Length ?? 0, ry?.Length ?? 0);
            for (int k = Math.Max(0, from); k < n; k++)
            {
                double qx = rx[k];
                double qy = ry[k];
                double dx = qx - px;
                double dy = qy - py;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-12)
                {
                    double fx = px - tx;
                    double fy = py - ty;
                    double a = dx * dx + dy * dy;
                    double b = 2 * (fx * dx + fy * dy);
                    double c = fx * fx + fy * fy - r2;
                    double disc = b * b - 4 * a * c;
                    if (disc >= 0)
                    {
                        double t = (-b - Math.Sqrt(disc)) / (2 * a);
                        if (t >= 0 && t <= 1)
                        {
                            ax = px + t * dx;
                            ay = py + t * dy;
                            entered = true;
                            return sum + t * len;
                        }
                    }
                }
                sum += len;
                px = qx;
                py = qy;
            }
            ax = px;
            ay = py;
            return sum;
        }

        /// <summary>沿路线走一步（一步里可以越过几个路点；途中进入到达半径就停在那里）。返回 true = 到达路线终点或进入到达半径。</summary>
        private static bool FollowRoute(TransitGroupRecord g, double stepLen, double arrival)
        {
            int n = g.RouteX?.Length ?? 0;
            double remaining = stepLen;
            for (int guard = 0; guard < 16 && remaining > 1e-12; guard++)
            {
                if (g.RouteIndex >= n)
                {
                    return true;
                }
                double tx = g.RouteX[g.RouteIndex];
                double ty = g.RouteY[g.RouteIndex];
                double ddx = tx - g.PosX;
                double ddy = ty - g.PosY;
                double d = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (d <= remaining)
                {
                    g.PosX = tx;
                    g.PosY = ty;
                    remaining -= d;
                    g.RouteIndex++;
                }
                else
                {
                    g.PosX += ddx / d * remaining;
                    g.PosY += ddy / d * remaining;
                    remaining = 0;
                }
                double ax = g.TargetX - g.PosX;
                double ay = g.TargetY - g.PosY;
                if (Math.Sqrt(ax * ax + ay * ay) <= arrival)
                {
                    return true;
                }
            }
            return g.RouteIndex >= n;
        }

        private static void RequestRoute(CampaignState state, TransitGroupRecord g)
        {
            RaidState raids = state.Raids;
            if (g.HasOrigin)
            {
                AppendTrail(g);
            }
            g.NavSerial = raids.NextNavSerial++;
            g.RouteState = RouteAwaiting;
            g.RouteIndex = 0;
            g.RouteX = Array.Empty<int>();
            g.RouteY = Array.Empty<int>();
            NavService.Request(NavService.OwnerTransit, KeyOf(g), g.NavSerial, NavConst.ClassHostile,
                NavService.CellOf(g.PosX, g.PosY), NavService.CellOf(g.TargetX, g.TargetY), allowPartial: true);
        }

        /// <summary>
        /// 要新路线之前（行军途中路线被截断 / 失效）：把当前路线已走过的路点和此刻所在的格接到 <see cref="TransitGroupRecord.TrailX"/> 后面，撤退时沿它原路返回。
        /// 还没离开出发格就重新要（刚派出、没有排定路线）不记。相邻重复的格只记一次。
        /// </summary>
        private static void AppendTrail(TransitGroupRecord g)
        {
            int walked = Math.Min(Math.Max(0, g.RouteIndex), Math.Min(g.RouteX?.Length ?? 0, g.RouteY?.Length ?? 0));
            GridCell here = NavService.CellOf(g.PosX, g.PosY);
            GridCell origin = NavService.CellOf(g.OriginX, g.OriginY);
            int had = Math.Min(g.TrailX?.Length ?? 0, g.TrailY?.Length ?? 0);
            if (walked == 0 && had == 0 && here.X == origin.X && here.Y == origin.Y)
            {
                return;
            }
            var xs = new List<int>(had + walked + 1);
            var ys = new List<int>(had + walked + 1);
            for (int k = 0; k < had; k++)
            {
                xs.Add(g.TrailX[k]);
                ys.Add(g.TrailY[k]);
            }
            for (int k = 0; k <= walked; k++)
            {
                int x = k < walked ? g.RouteX[k] : here.X;
                int y = k < walked ? g.RouteY[k] : here.Y;
                if (xs.Count > 0 && xs[xs.Count - 1] == x && ys[ys.Count - 1] == y)
                {
                    continue;
                }
                xs.Add(x);
                ys.Add(y);
            }
            g.TrailX = xs.ToArray();
            g.TrailY = ys.ToArray();
        }

        private static void Arrive(TransitGroupRecord g, bool blocked)
        {
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks + 1;
            ArrivalCount++;
            // 目标是前哨站的突袭如实写“抵达前哨站附近”，不说“家园外围”（复审 P1；家园战时预案与核心告警也只认目标为家园的，见 StandingRuleService / HomeValleyAlarms）。
            string detail = GameText.Format(g.TargetKind == Defense.RaidDirectorService.TargetOutpost ? "world.transit.raid.arrived_outpost_detail" : "world.transit.raid.arrived_detail",
                g.UnitCount.ToString(), OriginName(g.OriginId));
            if (blocked)
            {
                detail += " — " + GameText.Format("nav.transit.blocked",
                    GameText.Get(NavService.FailKey(g.NavReason != 0 ? (NavFailReason)g.NavReason : NavFailReason.Unreachable)));
            }
            NotificationCenter.Post("raid_arrival", detail, new Vector3((float)g.PosX, 0f, (float)g.PosY));
        }

        /// <summary>寻路服务交回一条结果：队伍还在等这条（序号一致）才采纳。</summary>
        public static bool DeliverRoute(CampaignState state, in NavResult r)
        {
            TransitGroupRecord g = FindByKey(state, r.Request.OwnerKey);
            if (g == null || g.State != TransitGroupState.Marching || g.RouteState != RouteAwaiting || g.NavSerial != r.Request.Serial)
            {
                return false;
            }
            if (r.Status == NavStatus.Ok || r.Status == NavStatus.Partial)
            {
                var xs = new int[r.PointCount];
                var ys = new int[r.PointCount];
                for (int k = 0; k < r.PointCount; k++)
                {
                    int2 p = NavService.ResultPoint(r.PointStart + k);
                    xs[k] = p.x;
                    ys[k] = p.y;
                }
                g.RouteX = xs;
                g.RouteY = ys;
                g.RouteIndex = 0;
                g.RouteState = RouteFollowing;
                g.NavReason = r.Status == NavStatus.Partial ? (int)r.Reason : 0;
            }
            else
            {
                g.RouteState = RouteFailed;
                g.NavReason = (int)r.Reason;
            }
            return true;
        }

        private static readonly List<int2> RouteScratch = new List<int2>(64);

        /// <summary>地形变化后：剩余路线被挡的队伍从当前位置重新要路线。返回失效条数。</summary>
        public static int InvalidateRoutes(CampaignState state)
        {
            int n = 0;
            foreach (TransitGroupRecord g in Groups(state))
            {
                if (g == null || g.State != TransitGroupState.Marching || g.RouteState != RouteFollowing)
                {
                    continue;
                }
                RouteScratch.Clear();
                for (int k = g.RouteIndex; k < (g.RouteX?.Length ?? 0); k++)
                {
                    RouteScratch.Add(new int2(g.RouteX[k], g.RouteY[k]));
                }
                GridCell c = NavService.CellOf(g.PosX, g.PosY);
                if (!NavService.RouteClear(new int2(c.X, c.Y), RouteScratch, 0, NavConst.ClassHostile))
                {
                    g.RouteState = RouteNeed;
                    n++;
                }
            }
            return n;
        }

        /// <summary>寻路快照读不了时：在等路线的队伍重新要（旧序号作废）。</summary>
        public static void ReissueAwaiting(CampaignState state)
        {
            foreach (TransitGroupRecord g in Groups(state))
            {
                if (g != null && g.RouteState == RouteAwaiting)
                {
                    g.RouteState = RouteNeed;
                }
            }
        }

        public static TransitGroupRecord FindByKey(CampaignState state, int key)
        {
            foreach (TransitGroupRecord g in Groups(state))
            {
                if (g != null && KeyOf(g) == key)
                {
                    return g;
                }
            }
            return null;
        }

        /// <summary>沿路线剩余的长度（格）；还没拿到路线时按直线。</summary>
        public static double RemainingDistance(TransitGroupRecord g)
        {
            if (g == null)
            {
                return 0;
            }
            if (g.RouteState != RouteFollowing || (g.RouteX?.Length ?? 0) == 0)
            {
                double dx = g.TargetX - g.PosX;
                double dy = g.TargetY - g.PosY;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            double sum = 0;
            double px = g.PosX;
            double py = g.PosY;
            for (int k = g.RouteIndex; k < g.RouteX.Length; k++)
            {
                double ddx = g.RouteX[k] - px;
                double ddy = g.RouteY[k] - py;
                sum += Math.Sqrt(ddx * ddx + ddy * ddy);
                px = g.RouteX[k];
                py = g.RouteY[k];
            }
            return sum;
        }

        /// <summary>
        /// 预计还要多少游戏秒到达（已到达为 0）。FG0-ARCH-06：拿到路线后沿路线算到“第一次进入到达半径”的那一点（与逐步推进同一套几何，
        /// 绕路时比直线长）；还在等路线时按直线估算。
        /// </summary>
        public static double EtaSeconds(TransitGroupRecord g)
        {
            if (g == null || g.State != TransitGroupState.Marching)
            {
                return 0;
            }
            double arrival = GameClock.TuningOr("transit.arrival_radius_cells", 12f);
            double along = g.RouteState == RouteFollowing ? DistanceUntilArrival(g, arrival) : RemainingDistance(g) - arrival;
            return Math.Max(0, along) / Math.Max(0.01f, g.Speed);
        }

        /// <summary>沿路线走到第一次进入到达半径（以目标为圆心）要走的长度；路线走完都进不去（部分路线）时 = 路线剩余长度。</summary>
        public static double DistanceUntilArrival(TransitGroupRecord g, double arrival)
        {
            double px = g.PosX;
            double py = g.PosY;
            double sum = 0;
            double r2 = arrival * arrival;
            if ((px - g.TargetX) * (px - g.TargetX) + (py - g.TargetY) * (py - g.TargetY) <= r2)
            {
                return 0;
            }
            for (int k = g.RouteIndex; k < (g.RouteX?.Length ?? 0); k++)
            {
                double qx = g.RouteX[k];
                double qy = g.RouteY[k];
                double dx = qx - px;
                double dy = qy - py;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-12)
                {
                    // |p + t·d − c|² = r²，取段内最小的 t。
                    double fx = px - g.TargetX;
                    double fy = py - g.TargetY;
                    double a = dx * dx + dy * dy;
                    double b = 2 * (fx * dx + fy * dy);
                    double c = fx * fx + fy * fy - r2;
                    double disc = b * b - 4 * a * c;
                    if (disc >= 0)
                    {
                        double t = (-b - Math.Sqrt(disc)) / (2 * a);
                        if (t >= 0 && t <= 1)
                        {
                            return sum + t * len;
                        }
                    }
                }
                sum += len;
                px = qx;
                py = qy;
            }
            return sum;
        }

        /// <summary>
        /// FG5-RND-05（FGT-RND-008 突袭预报的方向）：队伍预计在哪一点进入到达半径——拿到路线后沿路线找第一次进入半径的那一点（与 <see cref="DistanceUntilArrival"/>、
        /// 逐步推进同一套几何）；还在等路线 / 路线走完都进不去时按“当前位置 → 目标”的直线取半径上的那一点。纯查询，O(剩余路点数)。
        /// </summary>
        public static void PredictArrival(TransitGroupRecord g, out double x, out double y)
        {
            x = g?.TargetX ?? 0;
            y = g?.TargetY ?? 0;
            if (g == null)
            {
                return;
            }
            double arrival = Math.Max(0.0, GameClock.TuningOr("transit.arrival_radius_cells", 12f));
            double r2 = arrival * arrival;
            double px = g.PosX;
            double py = g.PosY;
            if ((px - g.TargetX) * (px - g.TargetX) + (py - g.TargetY) * (py - g.TargetY) <= r2)
            {
                x = px;
                y = py;
                return;
            }
            if (g.RouteState == RouteFollowing)
            {
                for (int k = g.RouteIndex; k < (g.RouteX?.Length ?? 0); k++)
                {
                    double qx = g.RouteX[k];
                    double qy = g.RouteY[k];
                    double dx = qx - px;
                    double dy = qy - py;
                    double a = dx * dx + dy * dy;
                    if (a > 1e-12)
                    {
                        double fx = px - g.TargetX;
                        double fy = py - g.TargetY;
                        double b = 2 * (fx * dx + fy * dy);
                        double c = fx * fx + fy * fy - r2;
                        double disc = b * b - 4 * a * c;
                        if (disc >= 0)
                        {
                            double t = (-b - Math.Sqrt(disc)) / (2 * a);
                            if (t >= 0 && t <= 1)
                            {
                                x = px + t * dx;
                                y = py + t * dy;
                                return;
                            }
                        }
                    }
                    px = qx;
                    py = qy;
                }
            }
            // 直线：从（路线末端或当前位置）朝目标，取到达半径上的那一点。
            double ex = px - g.TargetX;
            double ey = py - g.TargetY;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len > 1e-9)
            {
                x = g.TargetX + ex / len * Math.Min(arrival, len);
                y = g.TargetY + ey / len * Math.Min(arrival, len);
            }
        }

        public static Vector2 Position(TransitGroupRecord g) => g == null ? Vector2.zero : new Vector2((float)g.PosX, (float)g.PosY);

        public static TransitGroupRecord Find(CampaignState state, string groupId)
        {
            foreach (TransitGroupRecord g in Groups(state))
            {
                if (g != null && g.GroupId == groupId)
                {
                    return g;
                }
            }
            return null;
        }

        /// <summary>出发地的显示名（领地名文本键；找不到时原样显示 ID）。</summary>
        public static string OriginName(string originId)
        {
            if (string.IsNullOrEmpty(originId))
            {
                return string.Empty;
            }
            string key = "world.territory." + originId + ".name";
            return GameText.Has(key) ? GameText.Get(key) : originId;
        }

        public static void ResetCountersForTests()
        {
            DispatchCount = 0;
            ArrivalCount = 0;
            WithdrawalCount = 0;
            RetreatsCompleted = 0;
            DestroyedCount = 0;
            Finished.Clear();
        }
    }
}
