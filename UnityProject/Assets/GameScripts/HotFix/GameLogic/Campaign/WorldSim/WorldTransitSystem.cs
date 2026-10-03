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
                Speed = Math.Max(0.01f, GameClock.TuningOr("transit.raid_speed_cells_per_second", 1.5f)),
                DispatchedAtTick = GameClock.Ticks,
                ArrivedAtTick = -1,
                RouteState = RouteNeed,
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
            try
            {
                StepGroups(state, groups, arrival, nav, dt);
            }
            finally
            {
                if (ArrivalCount != arrivalsBefore)
                {
                    // FG4-ECO-06：突袭部队到达家园 = 突袭开始——战时预案在下一个模拟步立刻检查（不等整分钟）。
                    Economy.StandingRuleService.NotifyRaidArrived(state);
                }
            }
        }

        private static void StepGroups(CampaignState state, TransitGroupRecord[] groups, double arrival, bool nav, float dt)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                TransitGroupRecord g = groups[i];
                if (g == null || g.State != TransitGroupState.Marching)
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
            g.NavSerial = raids.NextNavSerial++;
            g.RouteState = RouteAwaiting;
            g.RouteIndex = 0;
            g.RouteX = Array.Empty<int>();
            g.RouteY = Array.Empty<int>();
            NavService.Request(NavService.OwnerTransit, KeyOf(g), g.NavSerial, NavConst.ClassHostile,
                NavService.CellOf(g.PosX, g.PosY), NavService.CellOf(g.TargetX, g.TargetY), allowPartial: true);
        }

        private static void Arrive(TransitGroupRecord g, bool blocked)
        {
            g.State = TransitGroupState.Arrived;
            g.ArrivedAtTick = GameClock.Ticks + 1;
            ArrivalCount++;
            string detail = GameText.Format("world.transit.raid.arrived_detail", g.UnitCount.ToString(), OriginName(g.OriginId));
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
        }
    }
}
