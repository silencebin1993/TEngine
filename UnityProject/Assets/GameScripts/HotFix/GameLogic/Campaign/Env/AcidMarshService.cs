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
    /// FG10-FAC-01（FG09 第 6 章“危害：酸沼。站在酸液里的单位持续受到腐蚀伤害，初值每秒 2% 耐久；冷却液可以冲淡酸液”；
    /// 收口 DEBT-FG7ENV05-01 的澄净一份与 DEBT-FG8EXP02-03“酸沼外圈危害带的伤害效果”）：
    ///
    /// - 酸沼 = 规划层里危害为“酸沼”的两种区域（按种子放置，B25 不依赖固定坐标）：领地外圈的酸沼带（圆环：领地半径～外圈半径），
    ///   与领地里模板分布的酸沼区（实心圆；没有模板的旧世界按整块领地）。与 <see cref="HomeExplorationService.HazardAt"/> 同一份判定。
    /// - 伤害在家园 / 星球战斗内核逐单位结算（<see cref="CombatSite.SetAcidMarsh"/>，派生量不进快照，内核打开 / 读档时写一次）：每游戏秒一次，
    ///   站在酸沼里的地面单位扣 最大耐久 × hazard.acid_marsh.corrode_pct × (1 − 酸蚀抗性) ×（湿润时 hazard.acid_marsh.wet_mult）；区域重叠只扣一次；
    ///   敌我同一规则（FGR-FAC-003），澄净单位带 acid_proof（抗性 1）。空中单位、炮塔与建筑不受酸沼影响。与是否被观察无关（FGR-BASE-021）。
    /// - 信号所在的机器第一次进入某片酸沼时提示一次（同一片只一次，进存档）；悬停说明从“只作标记”改为“生效中”。
    /// 开销：区数个位数；内核每游戏秒 O(地面单位 × 区数)；提示检查每游戏秒一次 O(区数)。
    /// </summary>
    public static class AcidMarshService
    {
        public const string HazardId = "acid_marsh";

        public static float CorrodePct => Mathf.Clamp(Tuning("hazard.acid_marsh.corrode_pct", 0.02f), 0f, 1f);

        public static float WetMult => Mathf.Clamp01(Tuning("hazard.acid_marsh.wet_mult", 0.5f));

        public static int Notices { get; private set; }

        private static WorldPlan _plan;
        private static readonly List<float4> Shapes = new List<float4>(8);
        private static readonly List<string> ShapeIds = new List<string>(8);

        public static void ResetForTests()
        {
            _plan = null;
            Shapes.Clear();
            ShapeIds.Clear();
            Notices = 0;
        }

        /// <summary>全部酸沼形状（x, y, 内半径, 外半径；格）。</summary>
        public static IReadOnlyList<float4> ActiveShapes(CampaignState s)
        {
            Ensure(s);
            return Shapes;
        }

        /// <summary>某个星球位置在哪片酸沼里（外圈带 = 领地 ID#belt，领地里的酸沼 = 领地 ID#序号；不在返回 null）。O(区数)。</summary>
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
                // 外圈危害带（圆环）：领地的 hazardKind 对应“酸沼”这一行。
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
                    // 没有领地模板的旧世界（v1 / v2）：整块领地都是酸沼（与 HazardAt 同一口径）。
                    Shapes.Add(new float4(t.CenterX, t.CenterY, 0f, t.Radius));
                    ShapeIds.Add(t.Id + "#all");
                }
            }
        }

        /// <summary>家园（星球表面）战斗内核打开 / 读档后：写入酸沼（派生量）。O(区数)。返回写了几片。</summary>
        public static int RestoreAfterLoad(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed)
            {
                return 0;
            }
            IReadOnlyList<float4> shapes = ActiveShapes(s);
            site.SetAcidMarsh(shapes, CorrodePct, WetMult);
            return shapes.Count;
        }

        /// <summary>信号所在的机器第一次进入某片酸沼时提示一次（每游戏秒一次，只看步序号）。</summary>
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
            if (Array.IndexOf(s.Factions.AcidZonesNoticed, zone) >= 0)
            {
                return;
            }
            var list = new List<string>(s.Factions.AcidZonesNoticed) { zone };
            list.Sort(StringComparer.Ordinal);
            s.Factions.AcidZonesNoticed = list.ToArray();
            Notices++;
            NotificationCenter.Post("acid_marsh", GameText.Get("hazard.acid_marsh.notify.first"), new Vector3(pos.x, 0f, pos.y));
            GuidanceHooks.Raise("clarity.first_acid");
        }

        /// <summary>悬停说明里“效果”这一行（酸沼已生效）。</summary>
        public static string HoverLine() =>
            GameText.Format("hazard.acid_marsh.active", Pct(CorrodePct), Pct(CorrodePct * WetMult));

        private static string Pct(float v) => (v * 100f).ToString(Mathf.Approximately(v * 100f, Mathf.Round(v * 100f)) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
