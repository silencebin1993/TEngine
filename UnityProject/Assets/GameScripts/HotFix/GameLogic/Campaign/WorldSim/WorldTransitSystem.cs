using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002“行进中的突袭与巡逻同时运行”）：星球表面上行进中的队伍（聚合体）。
    ///
    /// - 纯数据：队伍 = 人数 + 双精度格坐标 + 目标 + 速度，存在 <see cref="RaidState.InTransit"/>，由 <see cref="WorldSimulation"/>
    ///   按统一时钟的固定步推进。**没有表现对象也照常推进**，镜头在不在旁边结果都一样（FGR-BASE-021）；表现层
    ///   （<see cref="WorldView"/> 的标记柱）只读这里的位置。
    /// - 确定性：队伍 ID 按存档里的序号递增（不用 GUID）；位置只由（出发点, 目标, 速度, 已走的步数）决定。
    /// - 出发点按种子生成的规划层取（领地中心朝家园方向的边缘，FG00 B25：不依赖固定坐标）。
    /// - 本 Story 只交付“行进”这一段：谁来发起突袭（突袭导演、预警）属于 FG6-DEF-04；沿地形寻路属于 FG0-ARCH-06 / FG6-DEF-05
    ///   （现在沿直线）；到达后的攻城与结算属于 FG6-DEF-05～08（现在到达即停在家园外围并发“突袭到达”紧急通知）。
    /// 每步开销 O(队伍数)（队伍是聚合体，数量级是个位数；逐单位模拟在 FG0-ARCH-03 的战斗内核里）。
    /// </summary>
    public static class WorldTransitSystem
    {
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

        /// <summary>派出一支队伍（格坐标，双精度）。速度取 transit.raid_speed_cells_per_second。</summary>
        public static TransitGroupRecord Dispatch(CampaignState state, TransitGroupKind kind, string originId, int unitCount,
            double fromX, double fromY, double toX, double toY)
        {
            CampaignFgStateDomains.EnsureAll(state);
            RaidState raids = state.Raids;
            var g = new TransitGroupRecord
            {
                GroupId = "transit-" + raids.NextGroupSerial,
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
            };
            raids.NextGroupSerial++;
            var list = new List<TransitGroupRecord>(raids.InTransit ?? Array.Empty<TransitGroupRecord>()) { g };
            raids.InTransit = list.ToArray();
            DispatchCount++;
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstRaidInTransit);
            return g;
        }

        /// <summary>一个固定模拟步：行进中的队伍朝目标走 speed × dt 格；进入到达半径即到达（发一次紧急通知，定位到队伍位置）。</summary>
        public static void Step(CampaignState state, float dt)
        {
            TransitGroupRecord[] groups = state?.Raids?.InTransit;
            if (groups == null || groups.Length == 0 || dt <= 0f)
            {
                return;
            }
            double arrival = Math.Max(0.0, GameClock.TuningOr("transit.arrival_radius_cells", 12f));
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
                double remaining = dist - arrival;
                double step = g.Speed * (double)dt;
                if (remaining <= step)
                {
                    if (dist > 1e-9 && remaining > 0)
                    {
                        g.PosX += dx / dist * remaining;
                        g.PosY += dy / dist * remaining;
                    }
                    g.State = TransitGroupState.Arrived;
                    g.ArrivedAtTick = GameClock.Ticks + 1;
                    ArrivalCount++;
                    NotificationCenter.Post("raid_arrival",
                        GameText.Format("world.transit.raid.arrived_detail", g.UnitCount.ToString(), OriginName(g.OriginId)),
                        new Vector3((float)g.PosX, 0f, (float)g.PosY));
                    continue;
                }
                g.PosX += dx / dist * step;
                g.PosY += dy / dist * step;
            }
        }

        /// <summary>预计还要多少游戏秒到达（已到达为 0）。</summary>
        public static double EtaSeconds(TransitGroupRecord g)
        {
            if (g == null || g.State != TransitGroupState.Marching)
            {
                return 0;
            }
            double dx = g.TargetX - g.PosX;
            double dy = g.TargetY - g.PosY;
            double remaining = Math.Sqrt(dx * dx + dy * dy) - GameClock.TuningOr("transit.arrival_radius_cells", 12f);
            return Math.Max(0, remaining) / Math.Max(0.01f, g.Speed);
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
