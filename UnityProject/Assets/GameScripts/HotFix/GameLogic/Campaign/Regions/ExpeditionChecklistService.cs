using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>FG8-EXP-01（FGR-EXP-001 派遣检查单）：派遣前的检查单——硬约束（不满足不能派遣）与提醒（可以无视，每条写清后果）。
    ///
    /// ── 分类（FG8-EXP-01 Opus 返工，按 FG08 原文）──
    /// 硬约束只有三条：至少一台机器、人数在信号带宽允许范围内（都取自 <see cref="ExpeditionDepartureService.ValidateRoster"/>，与出发事务同一份结果）、
    /// 目的地已探索或在迷雾边缘（区域级近似：区域还没开放时写真实的解锁条件，见 <see cref="LockedConditionsText"/>）。
    /// 另外“机器此刻不在可派位置”（已阵亡 / 不在家园 / 在工厂 / 不可中断的抢修）照样拦并写明——那不是派遣规则，是这台机器去不了。
    /// 提醒：目的地在迷雾边缘、路线走出覆盖、货舱低于建议（含没有货舱）、没有主武器、跨过静默夜、家园劳动力下降、敌方反制、机器伤势（重伤 / 偏重）、路线经过危害带。
    ///
    /// ── 路线（FG8-EXP-02 交付 DEBT-FG8EXP01-03）──
    /// <see cref="RouteProvider"/> 默认 = 从归还核心到目的地锚点的真实层级寻路路线（<see cref="ExpeditionSiteAnchors.Route"/>，星球格），
    /// “路线走出覆盖”按家园所在星球表面、与核心连通的覆盖网沿路线采样——中继铺到哪里、提醒就跟着变（真实几何，不注入覆盖）。
    /// 锚点还在校验中时没有路线，这条提醒暂不出现（出发会被“目的地路线计算中”拦下）。
    /// “路线经过危害带”仍待危害数据（DEBT-FG8EXP01-04，承接 FG8-GEN-02），<see cref="HazardProbe"/> 默认为空。
    /// </summary>
    public static class ExpeditionChecklistService
    {
        /// <summary>检查单的一行：<see cref="Code"/> 为原因码 / 提醒种类，<see cref="Text"/> 为玩家看到的文字（全部走文本键）。</summary>
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

        /// <summary>危害带探测结果：危害名与后果（后果为空时用 <c>exp.check.rem.hazard_default</c>，保证“每条提醒写清后果”）。</summary>
        public readonly struct HazardHit
        {
            public readonly string Name;
            public readonly string Consequence;

            public HazardHit(string name, string consequence)
            {
                Name = name;
                Consequence = consequence;
            }
        }

        /// <summary>真实行军路线（星球格，起点 = 归还核心，终点 = 目的地锚点）。默认 = <see cref="ExpeditionSiteAnchors.Route"/>（FG8-EXP-02）；自检可以换。</summary>
        public static Func<CampaignState, ExpeditionDepartureService.ExpeditionTarget, IReadOnlyList<Vector2>> RouteProvider = DefaultRoute;

        /// <summary>默认路线：从归还核心到目的地锚点的层级寻路路线（锚点未定时 null）。</summary>
        public static IReadOnlyList<Vector2> DefaultRoute(CampaignState state, ExpeditionDepartureService.ExpeditionTarget target) =>
            ExpeditionSiteAnchors.Route(state, target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost ? FoundryOutpostLayout.RegionId : FracturedCityLayout.RegionId);

        /// <summary>给定路线返回经过的危害（没有 = null）。默认 = <see cref="DefaultHazardProbe"/>（FG8-GEN-02 接入领地外圈危害带与领地内危害区，DEBT-FG8EXP01-04）；自检可以换。</summary>
        public static Func<CampaignState, IReadOnlyList<Vector2>, HazardHit?> HazardProbe = DefaultHazardProbe;

        /// <summary>
        /// FG8-GEN-02（DEBT-FG8EXP01-04）：沿真实行军路线每 4 格采样一次，第一处落在外圈危害带 / 领地内危害区里的点给出“危害名（所在领地）”与后果——
        /// 后果写 fg.TbHazard 的效果说明；效果机制还没随阵营内容开放时如实附上“当前只作标记”（DEBT-FG7ENV05-01）。只在检查单生成时调用，O(路线长度 / 4 × 领地数)。
        /// </summary>
        public static HazardHit? DefaultHazardProbe(CampaignState state, IReadOnlyList<Vector2> route)
        {
            if (state == null || route == null || route.Count < 2 || WorldGen.WorldGenService.PlanFor(state) == null)
            {
                return null;
            }
            for (int i = 1; i < route.Count; i++)
            {
                Vector2 a = route[i - 1];
                Vector2 b = route[i];
                float len = Vector2.Distance(a, b);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 4f));
                for (int k = 0; k <= n; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)k / n);
                    GameConfig.fg.Hazard h = Env.HomeExplorationService.HazardAt(state, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), visibleOnly: false, out WorldGen.PlannedTerritory t, out bool belt);
                    if (h == null || t == null)
                    {
                        continue;
                    }
                    string name = belt
                        ? GameText.Format("hazard.hover.title_belt", GameText.Get(t.HazardKey), GameText.Get(t.NameKey))
                        : Env.HomeExplorationService.HazardTitleZone(h, t);
                    string consequence = GameText.Get(h.EffectKey) + "；" + GameText.Get("hazard.hover.pending");
                    return new HazardHit(name, consequence);
                }
            }
            return null;
        }

        /// <summary>生成检查单。<paramref name="snap"/> 必须是同一时刻 <see cref="ExpeditionDepartureService.BuildPrepSnapshot"/> 的结果。
        /// 面板已经算好的校验与劳动力预告可以传进来（<paramref name="validation"/> / <paramref name="laborText"/> + <paramref name="laborDrops"/>），不重复扫描机器。</summary>
        public static Checklist Build(CampaignState state, IReadOnlyList<int> selected, ExpeditionDepartureService.PrepSnapshot snap,
            ExpeditionDepartureService.RosterValidation? validation = null, string laborText = null, bool laborDrops = false)
        {
            var hard = new List<Item>();
            var reminders = new List<Item>();
            selected ??= Array.Empty<int>();

            if (state == null)
            {
                hard.Add(new Item("no-active-campaign", DescribeHard("no-active-campaign")));
                return new Checklist(hard.ToArray(), reminders.ToArray());
            }

            // ── 硬约束 3：目的地（区域级近似）。区域都没开放时写真实的解锁条件；远征队在外时写明等它回来。
            // FG8-EXP-02：目的地锚点还在寻路校验 / 走不到时同样不能派（出发事务同一判据）。──
            if (!snap.RegionReachable)
            {
                if (snap.BlockedReason == "region-locked")
                {
                    hard.Add(new Item("destination-locked", LockedConditionsText(state)));
                }
                else
                {
                    string code = snap.BlockedReason ?? "region-unreachable";
                    hard.Add(new Item(code, DescribeHard(code)));
                }
                return new Checklist(hard.ToArray(), reminders.ToArray());
            }

            // ── 硬约束 1 / 2：至少一台、带宽（+ 机器此刻不在可派位置），与出发事务同一份校验 ──
            ExpeditionDepartureService.RosterValidation v = validation ?? ExpeditionDepartureService.ValidateRoster(state, selected);
            foreach (string reason in v.BlockingReasons)
            {
                hard.Add(new Item(reason, DescribeHard(reason)));
            }

            ExpeditionDepartureService.ExpeditionTarget target = snap.Target;
            string site = target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost
                ? FoundryOutpostLayout.RegionId
                : FracturedCityLayout.RegionId;

            // ── 硬约束（FG8-EXP-02）：目的地锚点——远征队要沿地形走过去的终点——还在寻路校验中 / 从家园走不到（与出发事务同一判据）──
            ExpeditionSiteAnchors.EnsureResolved(state);
            if (!ExpeditionSiteAnchors.IsResolved(state, site))
            {
                string code = ExpeditionSiteAnchors.IsFailed(state, site) ? "destination-unreachable" : "destination-pending";
                hard.Add(new Item(code, DescribeHard(code)));
            }

            // ── 提醒：目的地在迷雾边缘（已开放、尚未进入过）──
            if (ExpeditionDepartureService.ClassifyDestination(state, target) == ExpeditionDepartureService.DestinationFog.FogEdge)
            {
                reminders.Add(new Item("destination-fog-edge", GameText.Get("exp.check.rem.fog_edge")));
            }

            // ── 提醒：路线走出覆盖 / 经过危害带（真实路线在星球表面上：按家园所在表面、与核心连通的覆盖网采样，FGR-SIG-053）──
            IReadOnlyList<Vector2> route = RouteProvider?.Invoke(state, target);
            if (route != null && route.Count >= 2)
            {
                if (SignalCoverageService.IsBoundedSite(HomeValleyLayout.RegionId))
                {
                    float outside = SignalCoverageService.RouteOutsideLength(HomeValleyLayout.RegionId, route, out _);
                    if (outside > 0f)
                    {
                        reminders.Add(new Item("coverage", GameText.Format("exp.check.rem.coverage", Mathf.CeilToInt(outside))));
                    }
                }
                // FG8-GEN-02（DEBT-FG8EXP02-02）：按锚点判定地点内部覆盖时（signal.coverage.site_root_mode = 2），入口不在与核心连通的覆盖里 → 到了地点也收不到命令。
                if (SignalCoverageService.SiteRootMode >= 2 && !SignalCoverageService.SiteRootConnected(state, site))
                {
                    reminders.Add(new Item("site-root", GameText.Format("signal.site.anchor_uncovered", GameText.Get(ExpeditionTextKeys.SiteNameKey(site)))));
                }
                HazardHit? hazard = HazardProbe?.Invoke(state, route);
                if (hazard.HasValue && !string.IsNullOrEmpty(hazard.Value.Name))
                {
                    string consequence = string.IsNullOrEmpty(hazard.Value.Consequence)
                        ? GameText.Get("exp.check.rem.hazard_default")
                        : hazard.Value.Consequence;
                    reminders.Add(new Item("hazard", GameText.Format("exp.check.rem.hazard", hazard.Value.Name, consequence)));
                }
            }

            // ── 队伍构成类提醒（名单为空时不提，硬约束“至少一台”已写明）──
            if (selected.Count > 0)
            {
                RegionRecord region = target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost
                    ? FoundryOutpostRegion.Find(state)
                    : FracturedCityRegion.Find(state);
                int suggested = ExpeditionDepartureService.MinRecommendedCargoSlots(target, region);
                if (v.TotalCargoSlots <= 0)
                {
                    reminders.Add(new Item("cargo", GameText.Format("exp.check.rem.cargo_none", suggested)));
                }
                else if (v.TotalCargoSlots < suggested)
                {
                    reminders.Add(new Item("cargo", GameText.Format("exp.check.rem.cargo", v.TotalCargoSlots, suggested)));
                }

                if (!v.HasWeapon)
                {
                    reminders.Add(new Item("no_weapon", GameText.Get("exp.check.rem.no_weapon")));
                }

                bool drops = laborDrops;
                string labor = laborText ?? MachineRoster.LaborForecastText(state, selected as IReadOnlyCollection<int> ?? new List<int>(selected), out drops);
                if (drops && !string.IsNullOrEmpty(labor))
                {
                    reminders.Add(new Item("labor", GameText.Format("exp.check.rem.labor", labor)));
                }

                // 机器伤势：重伤（低于重伤线）逐台点名；偏重（重伤线与警告线之间）合并一条。
                int warnCount = 0;
                float warnPct = Tuning("exp.check.warn_health_pct", 0.6f);
                float severePct = ExpeditionDepartureService.SeverePct;
                foreach (int logicId in selected)
                {
                    if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null || !rec.IsAlive || rec.MaxHealth <= 0f)
                    {
                        continue;
                    }
                    if (ExpeditionDepartureService.IsSeverelyDamaged(rec))
                    {
                        // 向下取整：生命 24.6% 显示 24%，不会出现“生命 25%，低于 25%”。
                        int pct = Mathf.FloorToInt(rec.Health / rec.MaxHealth * 100f);
                        reminders.Add(new Item("severe:" + logicId.ToString(CultureInfo.InvariantCulture),
                            GameText.Format("exp.check.rem.severe", MachineNaming.Short(rec), pct, Mathf.RoundToInt(severePct * 100f))));
                    }
                    else if (rec.Health < rec.MaxHealth * warnPct)
                    {
                        warnCount++;
                    }
                }
                if (warnCount > 0)
                {
                    reminders.Add(new Item("damaged", GameText.Format("exp.check.rem.damaged", warnCount, Mathf.RoundToInt(warnPct * 100f))));
                }
            }

            // ── 提醒：预计会跨过静默夜（FG9-ACT1-01 承接 DEBT-FG7ENV02-03：按到目的地锚点的真实路线 × 这支队伍的行军速度估往返 + 地点停留）──
            string silent = SilentNightService.ExpeditionCrossText(state, SilentNightService.ExpeditionEstimateSeconds(state,
                snap.Target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost ? FoundryOutpostLayout.RegionId : FracturedCityLayout.RegionId, selected));
            if (!string.IsNullOrEmpty(silent))
            {
                reminders.Add(new Item("silent_night", silent));
            }

            // ── 提醒：情报显示的敌方反制（与出发时锁定的是同一份计算）──
            if (snap.AdaptationId != null && snap.AdaptationId != AdaptationCatalog.None)
            {
                reminders.Add(new Item("enemy_counter", GameText.Format("exp.check.rem.enemy", snap.AdaptationDisplayName,
                    snap.AdaptationHazardText, snap.AdaptationCounterHintText)));
            }

            return new Checklist(hard.ToArray(), reminders.ToArray());
        }

        /// <summary>区域都没开放时的硬约束文字：真实的解锁条件，逐项标已完成 / 未完成（条件判定与 <see cref="HomeValleySignal.RecomputeUnlock"/> /
        /// <see cref="FoundryOutpostRegion.RecomputeUnlock"/> 共用同一组函数）。破碎都市没开放时只写它；破碎都市开放后铸造前哨仍锁着时写铸造前哨。</summary>
        public static string LockedConditionsText(CampaignState state)
        {
            string done = GameText.Get("exp.check.mark.done");
            string todo = GameText.Get("exp.check.mark.todo");
            string text = GameText.Get("exp.check.hard.locked");
            RegionRecord ruins = FracturedCityRegion.Find(state);
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            bool ruinsLocked = ruins == null || ruins.State == RegionState.Locked;
            if (ruinsLocked)
            {
                text += "\n" + GameText.Format("exp.check.hard.locked.ruins",
                    HomeValleySignal.UnlockTowerOperational(state) ? done : todo,
                    HomeValleySignal.UnlockHasErc003() ? done : todo);
            }
            else if (foundry == null || foundry.State == RegionState.Locked)
            {
                text += "\n" + GameText.Format("exp.check.hard.locked.foundry",
                    FoundryOutpostRegion.UnlockCrossFactionSaved(state) ? done : todo,
                    FoundryOutpostRegion.UnlockErc003Retrofitted() ? done : todo);
            }
            return text;
        }

        /// <summary>硬约束 / 出发被拒原因码 → 玩家文字（全部走文本键；检查单与出发被拒的提示、字幕同一份，不露原因码）。</summary>
        public static string DescribeHard(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }
            if (reason.StartsWith("roster-too-small", StringComparison.Ordinal))
            {
                return GameText.Format("exp.check.hard.too_small", ExpeditionDepartureService.MinDepartureSize);
            }
            if (reason.StartsWith("bandwidth-exceeded", StringComparison.Ordinal))
            {
                return GameText.Format("exp.check.hard.bandwidth", Field(reason, "need="), Field(reason, "cap="));
            }
            if (reason == "region-locked" || reason == "destination-locked")
            {
                return LockedConditionsText(CampaignSession.Current);
            }
            if (reason == "expedition-underway")
            {
                return GameText.Get("expedition.reason.underway"); // FG0-ARCH-01 既有文本：远征在外时家园照常运行，但不能再派第二支
            }
            if (reason == "no-active-campaign")
            {
                return GameText.Get("exp.check.hard.no_campaign");
            }
            // FG8-EXP-02：行军的终点与家园状态。
            if (reason == "destination-pending")
            {
                return GameText.Get("exp.check.hard.destination_pending");
            }
            if (reason == "destination-unreachable")
            {
                return GameText.Get("exp.check.hard.destination_unreachable");
            }
            if (reason == "home-not-loaded")
            {
                return GameText.Get("exp.check.hard.home_not_loaded");
            }
            // 出发事务失败：存档结果 / 异常原文已由 TryDepart 写进日志，玩家只看玩家语言（FG8-EXP-01 返工第 1 轮修复）。
            if (reason.StartsWith("autosave-failed", StringComparison.Ordinal))
            {
                return GameText.Get("exp.check.hard.autosave_failed");
            }
            if (reason.StartsWith("transaction-failed-rolled-back", StringComparison.Ordinal))
            {
                return GameText.Get("exp.check.hard.transaction_failed");
            }
            if (reason.StartsWith("transaction-failed-rollback-also-failed", StringComparison.Ordinal))
            {
                return GameText.Get("exp.check.hard.rollback_failed");
            }
            string machineKey = reason.StartsWith("machine-dead:", StringComparison.Ordinal) ? "exp.check.hard.machine.dead"
                : reason.StartsWith("machine-already-deployed:", StringComparison.Ordinal) ? "exp.check.hard.machine.deployed"
                : reason.StartsWith("machine-in-factory:", StringComparison.Ordinal) ? "exp.check.hard.machine.factory"
                : reason.StartsWith("machine-busy-uninterruptible:", StringComparison.Ordinal) ? "exp.check.hard.machine.busy"
                : reason.StartsWith("machine-unknown:", StringComparison.Ordinal) ? "exp.check.hard.machine.unknown"
                : reason.StartsWith("duplicate-selection:", StringComparison.Ordinal) ? "exp.check.hard.machine.duplicate"
                : null;
            if (machineKey != null)
            {
                string[] parts = reason.Split(':');
                string who = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int logicId)
                    ? MachineNaming.Short(logicId)
                    : string.Empty;
                return GameText.Format("exp.check.hard.machine", who, GameText.Get(machineKey));
            }
            return GameText.Format("exp.check.hard.other", reason);
        }

        /// <summary>多条硬约束 / 被拒原因拼成一行（分隔符走文本键，英文界面不出现全角分号）。</summary>
        public static string JoinReasons(IEnumerable<string> texts) => string.Join(GameText.Get("exp.check.sep"), texts);

        /// <summary>从 “bandwidth-exceeded:need=12:cap=8” 里取一个字段（没有 = 空串）。</summary>
        private static string Field(string reason, string prefix)
        {
            foreach (string part in reason.Split(':'))
            {
                if (part.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return part.Substring(prefix.Length);
                }
            }
            return string.Empty;
        }

        private static float Tuning(string id, float fallback) =>
            GameLogic.Campaign.Grid.GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
