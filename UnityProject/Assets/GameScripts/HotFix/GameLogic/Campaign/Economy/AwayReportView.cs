using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>离家报告一行被点击时做什么（FG04 第 4 节“离家报告里的每一条都可以点击定位”）。</summary>
    public enum AwayLineAction
    {
        /// <summary>标题 / 空状态行（不是条目）。</summary>
        None,
        /// <summary>统计面板生产页并选中这种物品（Arg = 物品 ID）。</summary>
        StatsItem,
        /// <summary>统计面板的某一页（Arg = StatsTab 名字）。</summary>
        StatsTab,
        /// <summary>镜头飞到建筑并打开它的面板（Arg = 建筑 ID）。</summary>
        Building,
        /// <summary>镜头飞到位置。</summary>
        Locate,
        /// <summary>镜头飞到机器（有位置时）并打开名册里这台机器的详情（LogicId）。</summary>
        Machine,
        /// <summary>镜头飞到规则作用的实体（有位置时）并打开常驻规则面板。</summary>
        Rules,
        PowerPanel,
        Roster,
        /// <summary>没有位置的通知类条目：打开通知中心（历史里有这一条）。</summary>
        Notifications,
        /// <summary>FG6-DEF-08：打开突袭历史面板并选中这一份结算（Arg = 结算序号）。</summary>
        RaidResult,
    }

    /// <summary>离家报告的一行。</summary>
    public sealed class AwayLine
    {
        public string Text = string.Empty;
        /// <summary>样式类：ar-row-title（分段标题）/ ar-row-dim（空状态）/ ar-row-warn（停电、阵亡、重伤）/ null（普通条目）。</summary>
        public string Cls;
        public AwayLineAction Action;
        public string Arg = string.Empty;
        public bool HasPos;
        public string RegionId = string.Empty;
        public Vector3 Pos;
        public int LogicId;
        /// <summary>所在分段（自检按它断言“这一段有这一条”）。</summary>
        public string Section = string.Empty;

        public bool IsEntry => Action != AwayLineAction.None;
    }

    /// <summary>
    /// FG4-ECO-09（FGR-ECO-060 内容；FG13 FGU-30）：把一份离家报告组装成界面的行。只在面板打开、换报告或语言变化时调用（O(报告条目 + 物品种类)；
    /// 进行中的报告另外 O(生产建筑) 现算瓶颈）。文字全部走文本键；数值用统计面板的同一套格式。
    /// 分段顺序：生产 → 瓶颈 → 电力 → 物流 → 天气与环境 → 机器变化 → 突袭 → 事件 → 研究与解析 → 建筑 → 常驻规则 → 远征战斗。
    /// 分段里没有内容时写一行空状态（说明“没有发生”，而不是缺这一段）；整份报告什么都没发生时只写一行“一切照旧”（卡片负向“空报告”）。
    /// </summary>
    public static class AwayReportView
    {
        public static string Summary(CampaignState state, AwayReportRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            string site = AwayReportService.SiteName(r.RegionId);
            double start = Sec(r.StartTick);
            if (r.EndTick < 0)
            {
                return GameText.Format("away.summary.open", site, GameClock.DayOf(start), GameClock.FormatHhMm(start), AwayReportService.Duration(GameClock.Ticks - r.StartTick));
            }
            double end = Sec(r.EndTick);
            string outcome = r.Outcome == AwayReportService.OutcomeEvacuated ? GameText.Get("away.outcome.evacuated")
                : r.Outcome == AwayReportService.OutcomeWiped ? GameText.Get("away.outcome.wiped") : GameText.Get("away.outcome.other");
            return GameText.Format("away.summary", site, GameClock.DayOf(start), GameClock.FormatHhMm(start), GameClock.DayOf(end), GameClock.FormatHhMm(end),
                AwayReportService.Duration(r.EndTick - r.StartTick), outcome);
        }

        /// <summary>组装全部行。返回 true = 报告里什么都没发生（只写了一行“一切照旧”）。</summary>
        public static bool Build(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            into.Clear();
            if (r == null)
            {
                return true;
            }
            AwayReportRecord deltas = r;
            if (r.EndTick < 0)
            {
                // 进行中的报告：差额现算到临时记录里（不改存档）。
                deltas = new AwayReportRecord
                {
                    BaseFuelMl = r.BaseFuelMl, BaseFuelOuts = r.BaseFuelOuts, BasePumpedMl = r.BasePumpedMl, BaseDeliveredMl = r.BaseDeliveredMl,
                    BaseFlushedMl = r.BaseFlushedMl, BaseRemovedMl = r.BaseRemovedMl, BaseCleared = r.BaseCleared, BaseDiscarded = r.BaseDiscarded, BaseSplit = r.BaseSplit,
                    BaseBeltIn = r.BaseBeltIn, BaseBeltOut = r.BaseBeltOut,
                };
                AwayReportService.FillDeltas(state, deltas);
            }
            List<AwayStarveRecord> starve = AwayReportService.CollectStarve(state, r, clear: false);
            ReactionSessionRecord session = AwayReportService.FindReactionSession(state, r);
            if (IsEmpty(r, deltas, starve, session))
            {
                into.Add(new AwayLine { Text = GameText.Get("away.panel.empty_report"), Cls = "ar-row-dim", Section = "empty" });
                return true;
            }
            BuildProduction(state, r, into);
            BuildBottlenecks(state, r, starve, into);
            BuildPower(state, r, deltas, into);
            BuildLogistics(deltas, into);
            BuildEntries(state, r, "weather", "away.section.weather", "away.weather.none", into);
            BuildMachines(state, r, into);
            BuildRaids(state, r, into);
            BuildEntries(state, r, "event", "away.section.event", "away.event.none", into);
            BuildEntries(state, r, "research", "away.section.research", "away.research.none", into);
            BuildEntries(state, r, "buildings", "away.section.buildings", null, into);
            BuildRules(state, r, into);
            if (session != null)
            {
                Title(into, "away.section.combat", "combat");
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.combat.row", ReactionAttribution.Title(session) + GameText.Get("rules.sep_dot") + ReactionAttribution.Summary(state, session)),
                    Action = AwayLineAction.StatsTab, Arg = "Combat", Section = "combat",
                });
            }
            if (r.EntriesDropped > 0)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.entry.more", r.EntriesDropped), Action = AwayLineAction.Notifications, Section = "more" });
            }
            return false;
        }

        private static bool IsEmpty(AwayReportRecord r, AwayReportRecord d, List<AwayStarveRecord> starve, ReactionSessionRecord session) =>
            r.Produced.Length == 0 && r.Consumed.Length == 0 && starve.Count == 0 && r.Outages.Length == 0 && r.OutagesDropped == 0 && r.ShortSeconds == 0
            && r.Machines.Length == 0 && r.MachinesDropped == 0 && r.Entries.Length == 0 && r.EntriesDropped == 0 && r.RulesTotal == 0
            && d.FuelMl == 0 && d.FuelOuts == 0 && d.PumpedMl == 0 && d.DeliveredMl == 0 && d.FlushedMl == 0 && d.RemovedMl == 0 && d.Cleared == 0 && d.Discarded == 0 && d.Split == 0 && d.BeltIn == 0 && d.BeltOut == 0
            && AwayReportService.MembersLost(r) == 0 && session == null
            && Defense.RaidResultService.InWindow(CampaignSession.Current, r.StartTick, r.EndTick).Count == 0;

        // ── 生产 ─────────────────────────────────────────────────────────────

        private static void BuildProduction(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            Title(into, "away.section.production", "production");
            var ids = new List<string>();
            foreach (ItemAmountRecord a in r.Produced)
            {
                if (a != null && !ids.Contains(a.ItemId))
                {
                    ids.Add(a.ItemId);
                }
            }
            foreach (ItemAmountRecord a in r.Consumed)
            {
                if (a != null && !ids.Contains(a.ItemId))
                {
                    ids.Add(a.ItemId);
                }
            }
            if (ids.Count == 0)
            {
                Dim(into, "away.prod.none", "production");
            }
            ids.Sort((a, b) =>
            {
                long na = Math.Abs(Get(r.Produced, a) - Get(r.Consumed, a));
                long nb = Math.Abs(Get(r.Produced, b) - Get(r.Consumed, b));
                if (na != nb)
                {
                    return nb.CompareTo(na);
                }
                long pa = Get(r.Produced, a), pb = Get(r.Produced, b);
                return pa != pb ? pb.CompareTo(pa) : string.CompareOrdinal(a, b);
            });
            int max = AwayReportService.ItemsMax;
            for (int i = 0; i < ids.Count && i < max; i++)
            {
                string id = ids[i];
                ItemDef def = ItemCatalog.Find(id);
                long p = Get(r.Produced, id), c = Get(r.Consumed, id);
                long net = p - c;
                string netText = (net > 0 ? "+" : net < 0 ? "-" : string.Empty) + ProductionStats.Amount(def, Math.Abs(net));
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.prod.row", ItemCatalog.NameOf(id), ProductionStats.Amount(def, p), ProductionStats.Amount(def, c), netText),
                    Action = AwayLineAction.StatsItem, Arg = id, Section = "production", Cls = net < 0 ? "ar-row-warn" : null,
                });
            }
            if (ids.Count > max)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.prod.more", ids.Count - max), Action = AwayLineAction.StatsTab, Arg = "Production", Section = "production" });
            }
            AddEntries(state, r, "production", into);
        }

        // ── 瓶颈 ─────────────────────────────────────────────────────────────

        private static void BuildBottlenecks(CampaignState state, AwayReportRecord r, List<AwayStarveRecord> starve, List<AwayLine> into)
        {
            int top = AwayReportService.BottleneckTop;
            Title(into, "away.section.bottleneck", "bottleneck", top);
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            var worst = new Dictionary<string, AwayStarveRecord>(StringComparer.Ordinal);
            foreach (AwayStarveRecord s in starve)
            {
                if (s == null || string.IsNullOrEmpty(s.ItemId))
                {
                    continue;
                }
                totals.TryGetValue(s.ItemId, out long t);
                totals[s.ItemId] = t + s.Ticks;
                if (!worst.TryGetValue(s.ItemId, out AwayStarveRecord w) || s.Ticks > w.Ticks)
                {
                    worst[s.ItemId] = s; // 列表已按步数从大到小排好：同一物品第一次出现的就是缺得最久的建筑。
                }
            }
            var items = new List<string>(totals.Keys);
            items.Sort((a, b) => totals[a] != totals[b] ? totals[b].CompareTo(totals[a]) : string.CompareOrdinal(a, b));
            if (items.Count == 0)
            {
                Dim(into, "away.bn.none", "bottleneck");
            }
            for (int i = 0; i < items.Count && i < top; i++)
            {
                AwayStarveRecord w = worst[items[i]];
                BuildingRecord b = HomeGridService.FindBuilding(state, w.BuildingId);
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.bn.row", ItemCatalog.NameOf(items[i]), AwayReportService.Duration(totals[items[i]]),
                        b != null ? BuildingOps.NameOf(b) : w.BuildingId, AwayReportService.Duration(w.Ticks)),
                    Action = AwayLineAction.Building, Arg = w.BuildingId, HasPos = true, RegionId = HomeValleyLayout.RegionId,
                    Pos = new Vector3(w.X, 0f, w.Y), Section = "bottleneck", Cls = "ar-row-warn",
                });
            }
            AddEntries(state, r, "bottleneck", into);
        }

        // ── 电力 ─────────────────────────────────────────────────────────────

        private static void BuildPower(CampaignState state, AwayReportRecord r, AwayReportRecord d, List<AwayLine> into)
        {
            Title(into, "away.section.power", "power");
            if (r.PowerSeconds <= 0)
            {
                Dim(into, "away.power.no_grid", "power");
            }
            else
            {
                double n = r.PowerSeconds;
                var parts = new List<string>();
                for (int c = 0; c < r.ClassSum.Length && c < HomeValleyPowerGrid.SourceClassCount; c++)
                {
                    if (r.ClassSum[c] / n > 0.001)
                    {
                        parts.Add(GameText.Format("away.power.class", HomeValleyPowerGrid.SourceClassName(c), N(r.ClassSum[c] / n)));
                    }
                }
                string mix = parts.Count > 0 ? string.Join(GameText.Get("stats.list_sep"), parts) : GameText.Get("away.power.none_class");
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.power.avg", N(r.SupplySum / n), mix, N(r.DemandSum / n), N(r.DeliveredSum / n), AwayReportService.Duration(r.ShortSeconds * Hz)),
                    Action = AwayLineAction.PowerPanel, Section = "power", Cls = r.ShortSeconds > 0 ? "ar-row-warn" : null,
                });
                if (r.ChargedSum > 0.5 || r.DischargedSum > 0.5)
                {
                    into.Add(new AwayLine { Text = GameText.Format("away.power.storage", N(r.ChargedSum / 60.0), N(r.DischargedSum / 60.0)), Action = AwayLineAction.PowerPanel, Section = "power" });
                }
            }
            if (d.FuelMl > 0 || d.FuelOuts > 0)
            {
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.power.fuel", N(d.FuelMl / 1000.0), d.FuelOuts.ToString(CultureInfo.InvariantCulture)),
                    Action = AwayLineAction.StatsTab, Arg = "Flow", Section = "power", Cls = d.FuelOuts > 0 ? "ar-row-warn" : null,
                });
            }
            for (int i = 0; i < r.Outages.Length; i++)
            {
                AwayOutageRecord o = r.Outages[i];
                if (o == null)
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, o.BuildingId);
                string where = b != null ? BuildingOps.NameOf(b) : o.BuildingId;
                double s = Sec(o.StartTick);
                bool open = o.EndTick < 0 || (i == r.Outages.Length - 1 && r.OutageOngoingAtEnd);
                string text = open
                    ? GameText.Format("away.power.outage_open", GameClock.DayOf(s), GameClock.FormatHhMm(s), o.Peak, where)
                    : GameText.Format("away.power.outage", GameClock.DayOf(s), GameClock.FormatHhMm(s), AwayReportService.Duration(o.EndTick - o.StartTick), o.Peak, where);
                into.Add(new AwayLine
                {
                    Text = text, Action = AwayLineAction.Building, Arg = o.BuildingId, HasPos = true, RegionId = HomeValleyLayout.RegionId,
                    Pos = new Vector3(o.X, 0f, o.Y), Section = "power", Cls = "ar-row-warn",
                });
            }
            if (r.OutagesDropped > 0)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.power.outage_more", r.OutagesDropped), Action = AwayLineAction.PowerPanel, Section = "power" });
            }
            if (r.Outages.Length == 0 && r.OutagesDropped == 0 && r.PowerSeconds > 0)
            {
                Dim(into, "away.power.no_outage", "power");
            }
            AddEntries(state, r, "power", into);
        }

        // ── 物流 ─────────────────────────────────────────────────────────────

        private static void BuildLogistics(AwayReportRecord d, List<AwayLine> into)
        {
            bool flow = d.BeltIn > 0 || d.BeltOut > 0;
            bool belts = d.Cleared > 0 || d.Discarded > 0 || d.Split > 0;
            bool pipes = d.PumpedMl > 0 || d.DeliveredMl > 0 || d.FlushedMl > 0 || d.RemovedMl > 0;
            if (!belts && !pipes && !flow)
            {
                return;
            }
            Title(into, "away.section.logistics", "logistics");
            if (flow)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.logi.flow", d.BeltIn, d.BeltOut), Action = AwayLineAction.StatsTab, Arg = "Flow", Section = "logistics" });
            }
            if (belts)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.logi.belt", d.Cleared, d.Discarded, d.Split), Action = AwayLineAction.StatsTab, Arg = "Flow", Section = "logistics" });
            }
            if (pipes)
            {
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.logi.pipe", N(d.PumpedMl / 1000.0), N(d.DeliveredMl / 1000.0), N(d.FlushedMl / 1000.0), N(d.RemovedMl / 1000.0)),
                    Action = AwayLineAction.StatsTab, Arg = "Flow", Section = "logistics",
                });
            }
        }

        // ── 机器变化 ─────────────────────────────────────────────────────────

        private static void BuildMachines(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            Title(into, "away.section.machines", "machines");
            if (r.Members.Length > 0)
            {
                // 修复轮（审查 P1）：已结算的报告读结算时固化的结局，不随之后的出征改写；进行中的报告现算。
                int lost = AwayReportService.MembersLost(r);
                into.Add(new AwayLine
                {
                    Text = GameText.Format("away.machine.team", r.Members.Length, r.Members.Length - lost, lost),
                    Action = AwayLineAction.Roster, Section = "machines", Cls = lost > 0 ? "ar-row-warn" : null,
                });
            }
            float frac = HomeValleyAlarms.WoundedFraction;
            foreach (AwayMachineRecord a in r.Machines)
            {
                if (a == null)
                {
                    continue;
                }
                string name = MachineNaming.Short(a.LogicId);
                string where = GameText.Get(a.Expedition ? "away.where.expedition" : "away.where.home");
                string text;
                if (a.Died)
                {
                    double s = Sec(a.DiedTick);
                    text = GameText.Format("away.machine.died", name, where, GameClock.DayOf(s), GameClock.FormatHhMm(s));
                }
                else
                {
                    float now, max;
                    if (a.HasEndHealth)
                    {
                        // 已结算：结算那一刻的耐久（之后修好 / 再受伤都不改写历史报告）。
                        now = a.EndHealth;
                        max = a.EndMaxHealth > 0f ? a.EndMaxHealth : a.MaxHealth;
                    }
                    else
                    {
                        MachineRegistry.TryGetRecord(a.LogicId, out MachineRecord m);
                        now = m?.Health ?? a.MinHealth;
                        max = m?.MaxHealth > 0f ? m.MaxHealth : a.MaxHealth;
                    }
                    text = GameText.Format(a.MinHealth <= max * frac ? "away.machine.hurt_bad" : "away.machine.hurt", name, Mathf.RoundToInt(now), Mathf.RoundToInt(max),
                        Mathf.RoundToInt(a.MinHealth), where);
                }
                into.Add(new AwayLine
                {
                    Text = text, Action = AwayLineAction.Machine, LogicId = a.LogicId, HasPos = true, RegionId = a.RegionId, Pos = new Vector3(a.X, 0f, a.Y),
                    Section = "machines", Cls = a.Died || a.MinHealth <= a.MaxHealth * frac ? "ar-row-warn" : null,
                });
            }
            if (r.MachinesDropped > 0)
            {
                into.Add(new AwayLine { Text = GameText.Format("away.machine.more", r.MachinesDropped), Action = AwayLineAction.Roster, Section = "machines" });
            }
            if (r.Machines.Length == 0 && r.MachinesDropped == 0)
            {
                Dim(into, "away.machine.none", "machines");
            }
            AddEntries(state, r, "machines", into);
        }

        // ── 常驻规则（FG-GAP-098）────────────────────────────────────────────

        private static void BuildRules(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            Title(into, "away.section.rules", "rules");
            if (r.RulesTotal == 0)
            {
                Dim(into, "away.rule.none", "rules");
                return;
            }
            foreach (RuleLogRecord e in r.Rules)
            {
                if (e == null)
                {
                    continue;
                }
                into.Add(new AwayLine
                {
                    Text = StandingRuleService.LogText(state, e), Action = AwayLineAction.Rules, HasPos = e.HasPos, RegionId = HomeValleyLayout.RegionId,
                    Pos = new Vector3(e.X, 0f, e.Y), Section = "rules",
                });
            }
            into.Add(new AwayLine { Text = GameText.Format("away.rule.count", r.RulesTotal), Action = AwayLineAction.Rules, Section = "rules" });
        }

        // ── 突袭（FG6-DEF-08 FGR-DEF-052：过程时间线、损失、谁贡献最大；FG4-ECO-09 的突袭通知条目照常列在后面）──────────

        private static void BuildRaids(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            int entries = 0;
            foreach (AwayEntryRecord e in r.Entries)
            {
                if (e != null && e.Section == "raid")
                {
                    entries++;
                }
            }
            Title(into, "away.section.raid", "raid");
            int raids = Defense.RaidResultService.AppendAway(state, r.StartTick, r.EndTick, into);
            if (raids == 0 && entries == 0)
            {
                Dim(into, "away.raid.none", "raid");
                return;
            }
            AddEntries(state, r, "raid", into);
        }

        // ── 通知转来的条目 ────────────────────────────────────────────────────

        private static void BuildEntries(CampaignState state, AwayReportRecord r, string section, string titleKey, string noneKey, List<AwayLine> into)
        {
            int count = 0;
            foreach (AwayEntryRecord e in r.Entries)
            {
                if (e != null && e.Section == section)
                {
                    count++;
                }
            }
            if (count == 0 && noneKey == null)
            {
                return;
            }
            Title(into, titleKey, section);
            if (count == 0)
            {
                Dim(into, noneKey, section);
                return;
            }
            AddEntries(state, r, section, into);
        }

        private static void AddEntries(CampaignState state, AwayReportRecord r, string section, List<AwayLine> into)
        {
            foreach (AwayEntryRecord e in r.Entries)
            {
                if (e == null || e.Section != section)
                {
                    continue;
                }
                into.Add(EntryLine(e));
            }
        }

        public static AwayLine EntryLine(AwayEntryRecord e)
        {
            string detail = string.IsNullOrEmpty(e.Detail) ? string.Empty : GameText.Has(e.Detail) ? GameText.Get(e.Detail) : e.Detail;
            string body = NotificationCatalog.TryGetType(e.TypeId, out NotifyTypeDef def)
                ? string.IsNullOrEmpty(detail) ? GameText.Get(def.NameKey) : GameText.Format(def.SingleKey, detail)
                : detail;
            double s = Sec(e.Tick);
            return new AwayLine
            {
                Text = GameText.Format("away.entry.row", GameClock.DayOf(s), GameClock.FormatHhMm(s), body),
                Action = e.HasLocation ? AwayLineAction.Locate : AwayLineAction.Notifications,
                HasPos = e.HasLocation, RegionId = e.RegionId, Pos = new Vector3(e.X, e.Y, e.Z), Section = e.Section,
                Cls = def != null && def.Tier == NotifyLevel.Urgent ? "ar-row-warn" : null,
            };
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);

        private static double Sec(long tick) => tick / (double)Hz;

        private static string N(double v) => ProductionStats.UiNumber((float)v);

        private static long Get(ItemAmountRecord[] arr, string id)
        {
            foreach (ItemAmountRecord a in arr)
            {
                if (a != null && a.ItemId == id)
                {
                    return a.Amount;
                }
            }
            return 0;
        }

        private static void Title(List<AwayLine> into, string key, string section, int arg = -1) =>
            into.Add(new AwayLine { Text = arg >= 0 ? GameText.Format(key, arg) : GameText.Get(key), Cls = "ar-row-title", Section = section });

        private static void Dim(List<AwayLine> into, string key, string section) =>
            into.Add(new AwayLine { Text = GameText.Get(key), Cls = "ar-row-dim", Section = section });
    }
}
