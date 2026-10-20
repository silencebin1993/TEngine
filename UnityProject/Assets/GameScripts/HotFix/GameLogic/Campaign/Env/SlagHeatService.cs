using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG9-FAC-02（FG09 第 5 章“危害：熔渣热区。区内散热 -30%”，第 13 节初值；承接 DEBT-FG7ENV05-01 的铸造一份）：铸造领地里的熔渣热区。
    ///
    /// - 热区 = 规划层里属于“危害 = 熔渣热区”的领地的危害区（FG8-GEN-02 按种子放置，B25 不依赖固定坐标）。
    /// - 区内武器散热 × hazard.slag_heat.dissipation_mult：家园 / 星球战斗内核逐单位结算（<see cref="CombatSite.SetSlagZones"/>，派生量不进快照，内核打开 / 读档时写一次）——
    ///   敌我同一规则（FGR-FAC-003）：铸造自己的单位在热区里同样散热变慢。
    /// - 信号所在的机器第一次进入某片热区时提示一次（同一片只一次，进存档）；悬停说明从“只作标记”改为“生效中”。
    /// 开销：区数个位数；内核逐单位 O(区数)，只在有热量的单位散热时；提示检查每游戏秒一次 O(区数)。
    /// </summary>
    public static class SlagHeatService
    {
        public const string HazardId = "slag_heat";

        public static float DissipationMult => Mathf.Clamp(Tuning("hazard.slag_heat.dissipation_mult", 0.7f), 0.05f, 1f);

        public static int Notices { get; private set; }

        private static WorldPlan _plan;
        private static readonly List<PlannedHazardZone> ZoneList = new List<PlannedHazardZone>(8);
        private static readonly List<float3> Circles = new List<float3>(8);

        public static void ResetForTests()
        {
            _plan = null;
            Notices = 0;
        }

        public static string ZoneId(PlannedHazardZone z) => z == null ? string.Empty : z.TerritoryId + "#" + z.Index.ToString();

        /// <summary>全部熔渣热区（x, y, 半径；格）。</summary>
        public static IReadOnlyList<float3> ActiveCircles(CampaignState s)
        {
            Ensure(s);
            return Circles;
        }

        public static IReadOnlyList<PlannedHazardZone> Zones(CampaignState s)
        {
            Ensure(s);
            return ZoneList;
        }

        /// <summary>某个星球位置的散热倍率：在热区里 = <see cref="DissipationMult"/>，否则 1。O(区数)。</summary>
        public static float DissipationFactorAt(CampaignState s, Vector2 pos) => ZoneAt(s, pos) != null ? DissipationMult : 1f;

        public static string ZoneAt(CampaignState s, Vector2 pos)
        {
            Ensure(s);
            for (int i = 0; i < Circles.Count; i++)
            {
                float3 c = Circles[i];
                float dx = pos.x - c.x;
                float dy = pos.y - c.y;
                if (dx * dx + dy * dy <= c.z * c.z)
                {
                    return ZoneId(ZoneList[i]);
                }
            }
            return null;
        }

        private static void Ensure(CampaignState s)
        {
            WorldPlan plan = s != null ? WorldGenService.PlanFor(s) : null;
            if (ReferenceEquals(plan, _plan))
            {
                return;
            }
            _plan = plan;
            ZoneList.Clear();
            Circles.Clear();
            if (plan == null)
            {
                return;
            }
            foreach (PlannedHazardZone z in plan.HazardZones)
            {
                Hazard h = HomeExplorationService.HazardForTerritory(z.TerritoryId);
                if (h == null || h.Id != HazardId)
                {
                    continue;
                }
                ZoneList.Add(z);
                Circles.Add(new float3(z.CenterX, z.CenterY, z.Radius));
            }
        }

        /// <summary>家园（星球表面）战斗内核打开 / 读档后：写入熔渣热区与子母雷上限（派生量）。O(区数)。</summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            IReadOnlyList<float3> circles = ActiveCircles(s);
            site.SetSlagZones(circles, DissipationMult);
            site.SetMineCap(Mathf.RoundToInt(Tuning("foundry.mine.max_per_site", 12f)));
            return circles.Count;
        }

        /// <summary>信号所在的机器第一次进入某片熔渣热区时提示一次（每游戏秒一次，只看步序号）。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (s == null || tick % Math.Max(1, GameClock.StepHz) != 0)
            {
                return;
            }
            int id = Signal.SignalUplinkService.CurrentMachine(s);
            if (id == 0 || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || m.RegionId != HomeValleyLayout.RegionId)
            {
                return;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
            string zone = ZoneAt(s, pos);
            if (zone == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(s);
            if (Array.IndexOf(s.Factions.SlagZonesNoticed, zone) >= 0)
            {
                return;
            }
            var list = new List<string>(s.Factions.SlagZonesNoticed) { zone };
            list.Sort(StringComparer.Ordinal);
            s.Factions.SlagZonesNoticed = list.ToArray();
            Notices++;
            NotificationCenter.Post("slag_heat", GameText.Get("hazard.slag_heat.notify.first"), new Vector3(pos.x, 0f, pos.y));
            GuidanceHooks.Raise("foundry.first_slag");
        }

        /// <summary>悬停说明里“效果”这一行（熔渣热区已生效）。</summary>
        public static string HoverLine() =>
            GameText.Format("hazard.slag_heat.active", Mathf.RoundToInt((1f - DissipationMult) * 100f).ToString());

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
