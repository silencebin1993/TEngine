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
    /// FG11-FAC-01（FG09 第 7 章“危害：高温辐射。热量上限 -25%，散热 -20%”；承接 DEBT-FG7ENV05-01 / DEBT-FG8EXP02-03 的超频一份）：超频领地的高温辐射。
    ///
    /// - 辐射区 = 规划层里超频领地的外圈危害带（圆环，hazardKind 对应“高温辐射”这一行）+ 领地模板里危害 = 高温辐射的危害区（FG8-GEN-02 按种子放置，B25 不依赖固定坐标）；
    ///   没有领地模板的旧世界整块领地都算（与 <see cref="AcidMarshService"/> 同一口径）。
    /// - 区内武器热量上限 × hazard.heat_radiation.cap_mult、散热 × hazard.heat_radiation.dissipation_mult：星球战斗内核逐单位结算（<see cref="CombatSite.SetHeatRadiation"/>，派生量不进快照，
    ///   内核打开 / 读档时写一次）——敌我同一规则（FGR-FAC-003）。
    /// - 信号所在的机器第一次进入某片辐射区时提示一次（同一片只一次，进存档）；悬停说明从“只作标记”改为“生效中”。
    /// 开销：区数个位数；内核逐单位 O(区数)，只在积热 / 散热判定时；提示检查每游戏秒一次 O(区数)。
    /// 正式领地（热寂数据城模板、冷却塔遗址恢复热量上限）由 FG11-REG-01 交付，本服务按危害数据驱动，领地一出现即生效（DEBT-FG11FAC01-02 联验）。
    /// </summary>
    public static class HeatRadiationService
    {
        public const string HazardId = "heat_radiation";

        public static float CapMult => Mathf.Clamp(Tuning("hazard.heat_radiation.cap_mult", 0.75f), 0.05f, 1f);
        public static float DissipationMult => Mathf.Clamp(Tuning("hazard.heat_radiation.dissipation_mult", 0.8f), 0.05f, 1f);

        public static int Notices { get; private set; }

        private static WorldPlan _plan;
        private static readonly List<float4> Shapes = new List<float4>(8);
        private static readonly List<string> ShapeIds = new List<string>(8);

        public static void ResetForTests()
        {
            _plan = null;
            Notices = 0;
        }

        /// <summary>全部辐射区形状（x, y, 内半径, 外半径；格）。</summary>
        public static IReadOnlyList<float4> ActiveShapes(CampaignState s)
        {
            Ensure(s);
            return Shapes;
        }

        /// <summary>某个星球位置在哪片辐射区里（外圈带 = 领地 ID#belt，领地里的辐射区 = 领地 ID#序号；不在返回 null）。O(区数)。</summary>
        public static string ZoneAt(CampaignState s, Vector2 pos)
        {
            Ensure(s);
            for (int i = 0; i < Shapes.Count; i++)
            {
                float4 c = Shapes[i];
                float dx = pos.x - c.x;
                float dy = pos.y - c.y;
                float d2 = dx * dx + dy * dy;
                if (d2 <= c.w * c.w && d2 >= c.z * c.z)
                {
                    return ShapeIds[i];
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
            Shapes.Clear();
            ShapeIds.Clear();
            if (plan == null)
            {
                return;
            }
            foreach (PlannedTerritory t in plan.Territories)
            {
                if (t == null || !t.IsFaction)
                {
                    continue;
                }
                Hazard belt = HomeExplorationService.HazardForBelt(t.HazardKind);
                if (belt != null && belt.Id == HazardId && t.HazardWidth > 0)
                {
                    Shapes.Add(new float4(t.CenterX, t.CenterY, t.Radius, t.OuterRadius));
                    ShapeIds.Add(t.Id + "#belt");
                }
                Hazard zone = HomeExplorationService.HazardForTerritory(t.Id);
                if (zone == null || zone.Id != HazardId)
                {
                    continue;
                }
                if (plan.HasTemplates && plan.TemplateFor(t.Id) != null)
                {
                    foreach (PlannedHazardZone z in plan.HazardZones)
                    {
                        if (z != null && z.TerritoryId == t.Id)
                        {
                            Shapes.Add(new float4(z.CenterX, z.CenterY, 0f, z.Radius));
                            ShapeIds.Add(t.Id + "#" + z.Index.ToString());
                        }
                    }
                }
                else
                {
                    Shapes.Add(new float4(t.CenterX, t.CenterY, 0f, t.Radius));
                    ShapeIds.Add(t.Id + "#all");
                }
            }
        }

        /// <summary>家园（星球表面）战斗内核打开 / 读档后：写入辐射区（派生量）。O(区数)。返回写了几片。</summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            IReadOnlyList<float4> shapes = ActiveShapes(s);
            site.SetHeatRadiation(shapes, CapMult, DissipationMult);
            return shapes.Count;
        }

        /// <summary>信号所在的机器第一次进入某片辐射区时提示一次（每游戏秒一次，只看步序号）。</summary>
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
            if (Array.IndexOf(s.Factions.RadiationZonesNoticed, zone) >= 0)
            {
                return;
            }
            var list = new List<string>(s.Factions.RadiationZonesNoticed) { zone };
            list.Sort(StringComparer.Ordinal);
            s.Factions.RadiationZonesNoticed = list.ToArray();
            Notices++;
            NotificationCenter.Post("heat_radiation", GameText.Get("hazard.heat_radiation.notify.first"), new Vector3(pos.x, 0f, pos.y));
            GuidanceHooks.Raise("overclock.first_radiation");
        }

        /// <summary>悬停说明里“效果”这一行（高温辐射已生效）。</summary>
        public static string HoverLine() =>
            GameText.Format("hazard.heat_radiation.active", Mathf.RoundToInt((1f - CapMult) * 100f).ToString(), Mathf.RoundToInt((1f - DissipationMult) * 100f).ToString());

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
