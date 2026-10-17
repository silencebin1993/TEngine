using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Signal;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>FG8-EXP-01（FGR-EXP-001 派遣检查单）：派遣前的检查单——硬约束（不满足不能派遣）与提醒（可以无视，每条写清后果）。
    ///
    /// ── 单一事实来源 ──
    /// 硬约束只来自 <see cref="ExpeditionDepartureService.ValidateRoster"/>（面板的阻塞原因与出发事务同一份结果，不允许面板另算一套）；
    /// 提醒各自调已有的接口：信号覆盖 <see cref="SignalCoverageService.RouteOutsideLength"/>、静默夜 <see cref="SilentNightService.ExpeditionCrossText"/>、
    /// 劳动力 <see cref="MachineRoster.LaborForecastText"/>、敌方反制 <see cref="ExpeditionDepartureService.PrepSnapshot"/> 的 Adaptation 字段。
    ///
    /// ── 占位（登记 DEBT，见证据文件） ──
    /// 路线目前是“覆盖源到目的地站点原点”的直线占位（真实行军路线属于 FG8-EXP-02）；“路线经过危害带”的数据尚未由目的地区域提供，
    /// 只保留文本键与 <see cref="Item"/> 类型，<see cref="HazardProbe"/> 为空时不发提醒。
    /// </summary>
    public static class ExpeditionChecklistService
    {
        /// <summary>检查单的一行：硬约束的 <see cref="Code"/> 为原因码（旧原因码沿用 ValidateRoster 的写法，由面板映射文字）；提醒的 <see cref="Code"/> 为提醒种类。</summary>
        public readonly struct Item
        {
            public readonly string Code;
            public readonly string Text;

            public Item(string code, string text)
            {
                Code = code;
                Text = text;
            }
        }

        public readonly struct Checklist
        {
            /// <summary>有任一硬约束即不能派遣。</summary>
            public readonly bool Blocked;
            public readonly Item[] Hard;
            public readonly Item[] Reminders;

            public Checklist(Item[] hard, Item[] reminders)
            {
                Hard = hard ?? Array.Empty<Item>();
                Reminders = reminders ?? Array.Empty<Item>();
                Blocked = Hard.Length > 0;
            }
        }

        /// <summary>占位钩子：给定路线（站点局部坐标）返回经过的危害带名称（没有 = null）。默认空实现，等目的地区域的危害数据接入（DEBT）。</summary>
        public static Func<CampaignState, IReadOnlyList<Vector2>, string> HazardProbe;

        /// <summary>生成检查单。<paramref name="snap"/> 必须是同一时刻 <see cref="ExpeditionDepartureService.BuildPrepSnapshot"/> 的结果（面板已算好，不重复算）。</summary>
        public static Checklist Build(CampaignState state, IReadOnlyList<int> selected, ExpeditionDepartureService.PrepSnapshot snap)
        {
            var hard = new List<Item>();
            var reminders = new List<Item>();
            selected ??= Array.Empty<int>();

            if (state == null)
            {
                hard.Add(new Item("no-active-campaign", string.Empty));
                return new Checklist(hard.ToArray(), reminders.ToArray());
            }

            // 目的地在迷雾深处（区域仍 Locked，还没有可以派遣的路线）。
            if (!snap.RegionReachable)
            {
                if (snap.BlockedReason == "region-locked")
                {
                    hard.Add(new Item("destination-in-fog", Localization.GameText.Get("exp.check.hard.fog")));
                }
                else
                {
                    hard.Add(new Item(snap.BlockedReason ?? "region-unreachable", snap.BlockedReason ?? string.Empty));
                }
                return new Checklist(hard.ToArray(), reminders.ToArray());
            }

            // ── 硬约束：人数 / 带宽 / 武器 / 货舱 / 机器状态（与出发事务同一份校验）──
            ExpeditionDepartureService.RosterValidation validation = ExpeditionDepartureService.ValidateRoster(state, selected);
            foreach (string reason in validation.BlockingReasons)
            {
                hard.Add(new Item(reason, DescribeHard(reason)));
            }

            ExpeditionDepartureService.ExpeditionTarget target = snap.Target;
            string site = target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost
                ? FoundryOutpostLayout.RegionId
                : FracturedCityLayout.RegionId;

            // FG8-EXP-01 修复轮（FGR-EXP-001 硬约束“目的地已探索或在迷雾边缘”）：迷雾边缘放行，但提醒“尚未进入过”，敌情与路线以情报为准（不拦出发）。
            if (ExpeditionDepartureService.ClassifyDestination(state, target) == ExpeditionDepartureService.DestinationFog.FogEdge)
            {
                reminders.Add(new Item("destination-fog-edge", Localization.GameText.Get("exp.check.rem.fog_edge")));
            }

            // ── 提醒 1：路线有一段会走出信号覆盖（FGR-SIG-053：走出去只执行最后一条命令）──
            if (SignalCoverageService.IsBoundedSite(site))
            {
                var route = new List<Vector2> { SignalCoverageService.ExpeditionUplinkPosition(site), Vector2.zero };
                float outside = SignalCoverageService.RouteOutsideLength(site, route, out _);
                if (outside > 0f)
                {
                    reminders.Add(new Item("coverage",
                        Localization.GameText.Format("exp.check.rem.coverage", Mathf.RoundToInt(outside))));
                }
            }

            // ── 提醒 2：货舱低于建议值（战利品装不下，多出的留在现场）──
            RegionRecord region = target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost
                ? FoundryOutpostRegion.Find(state)
                : FracturedCityRegion.Find(state);
            int suggested = ExpeditionDepartureService.MinRecommendedCargoSlots(target, region);
            if (validation.TotalCargoSlots > 0 && validation.TotalCargoSlots < suggested)
            {
                reminders.Add(new Item("cargo",
                    Localization.GameText.Format("exp.check.rem.cargo", validation.TotalCargoSlots, suggested)));
            }

            // ── 提醒 3：预计会跨过静默夜（沿用 SilentNightService 的远征估时）──
            string silent = SilentNightService.ExpeditionCrossText(state);
            if (!string.IsNullOrEmpty(silent))
            {
                reminders.Add(new Item("silent_night", silent));
            }

            // ── 提醒 4：派遣后家园劳动力下降（沿用 MachineRoster 的劳动力预告）──
            string labor = MachineRoster.LaborForecastText(state, selected);
            if (!string.IsNullOrEmpty(labor))
            {
                reminders.Add(new Item("labor", labor));
            }

            // ── 提醒 5：情报显示的敌方反制（与出发时锁定的是同一份计算）──
            if (snap.AdaptationId != null && snap.AdaptationId != AdaptationCatalog.None)
            {
                reminders.Add(new Item("enemy_counter",
                    Localization.GameText.Format("exp.check.rem.enemy", snap.AdaptationDisplayName,
                        snap.AdaptationHazardText, snap.AdaptationCounterHintText)));
            }

            // ── 提醒 6：伤势偏重（低于警告线、但还没低到派遣下限的机器）──
            int warnCount = 0;
            float warnPct = Tuning("exp.check.warn_health_pct", 0.6f);
            foreach (int logicId in selected)
            {
                if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null || !rec.IsAlive)
                {
                    continue;
                }
                if (rec.MaxHealth > 0f && rec.Health < rec.MaxHealth * warnPct && !ExpeditionDepartureService.IsTooDamaged(rec))
                {
                    warnCount++;
                }
            }
            if (warnCount > 0)
            {
                reminders.Add(new Item("damaged",
                    Localization.GameText.Format("exp.check.rem.damaged", warnCount, Mathf.RoundToInt(warnPct * 100f))));
            }

            // ── 提醒 7：路线经过危害带（占位钩子，目的地区域数据接入前不发）──
            if (HazardProbe != null)
            {
                var route = new List<Vector2> { SignalCoverageService.ExpeditionUplinkPosition(site), Vector2.zero };
                string hazard = HazardProbe(state, route);
                if (!string.IsNullOrEmpty(hazard))
                {
                    reminders.Add(new Item("hazard",
                        Localization.GameText.Format("exp.check.rem.hazard", hazard, string.Empty)));
                }
            }

            return new Checklist(hard.ToArray(), reminders.ToArray());
        }

        /// <summary>硬约束文字：新增的原因码由文本键给出；其余（人数 / 带宽 / 武器 / 货舱 / 已派出 / 在工厂 / 已死亡 …）沿用面板既有映射，这里只返回原因码本身。</summary>
        private static string DescribeHard(string reason)
        {
            if (reason.StartsWith("machine-too-damaged:", StringComparison.Ordinal))
            {
                string idText = reason.Substring("machine-too-damaged:".Length);
                if (int.TryParse(idText, out int logicId) && MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null)
                {
                    float pct = rec.MaxHealth > 0f ? rec.Health / rec.MaxHealth * 100f : 0f;
                    float minPct = Tuning("exp.check.min_health_pct", 0.25f) * 100f;
                    return Localization.GameText.Format("exp.check.hard.damaged", MachineNaming.Short(rec),
                        Mathf.RoundToInt(pct), Mathf.RoundToInt(minPct));
                }
            }
            return reason;
        }

        private static float Tuning(string id, float fallback) =>
            GameLogic.Campaign.Grid.GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
