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
        /// <summary>FG8-EXP-05：打开货舱面板（货留在货舱里时）。</summary>
        Cargo,
        /// <summary>FG8-EXP-05：打开三选一面板（带回的数据包待分拣）。</summary>
        LootPick,
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
            // FG8-EXP-05（FGR-EXP-050）：远征结算在最前面（带回什么、做了什么、留下了什么）；其后才是家园在离家期间的报告。
            if (r.HasSettlement)
            {
                BuildSettlement(state, r, into);
            }
            if (IsEmpty(r, deltas, starve, session))
            {
                into.Add(new AwayLine { Text = GameText.Get("away.panel.empty_report"), Cls = "ar-row-dim", Section = "empty" });
                return !r.HasSettlement;
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
            BuildOutposts(state, r, into);
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
                if (e != null && e.Section == "raid" && !InOutpost(e))
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
                if (e != null && e.Section == section && !InOutpost(e))
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
                if (e == null || e.Section != section || InOutpost(e))
                {
                    continue;
                }
                into.Add(EntryLine(e));
            }
        }

        /// <summary>FG8-OUT-01：这一条发生在前哨站里（归到“前哨站”分组，不重复出现在家园的分段里）。</summary>
        private static bool InOutpost(AwayEntryRecord e) => e != null && !string.IsNullOrEmpty(e.OutpostId);

        /// <summary>
        /// FG8-OUT-01（卡片“前哨站的统计和离家报告分组”）：离家期间发生在各前哨站的事件（突袭、建筑受损 / 被毁、全毁、电力……）按前哨站分组列出，
        /// 每组开头一行写这个前哨站现在的状况（立着 / 共几座、前哨劳动几台）。前哨站已经不在了时写“已不存在”。按第一次出现的顺序。
        /// </summary>
        private static void BuildOutposts(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            List<string> order = null;
            foreach (AwayEntryRecord e in r.Entries)
            {
                if (InOutpost(e) && (order == null || !order.Contains(e.OutpostId)))
                {
                    (order ??= new List<string>(2)).Add(e.OutpostId);
                }
            }
            if (order == null)
            {
                return;
            }
            Title(into, "away.section.outpost", "outpost");
            foreach (string id in order)
            {
                OwnOutpostRecord o = WorldSim.OwnOutpostService.Find(state, id);
                int n = 0;
                foreach (AwayEntryRecord e in r.Entries)
                {
                    n += e != null && e.OutpostId == id ? 1 : 0;
                }
                string head = o != null
                    ? GameText.Format("away.outpost.head", WorldSim.OwnOutpostService.NameOf(o), n, o.Intact, o.Count, WorldSim.OwnOutpostService.LaborCount(state, id))
                    : GameText.Format("away.outpost.head_gone", id, n);
                into.Add(new AwayLine
                {
                    Text = head, Action = o != null ? AwayLineAction.Locate : AwayLineAction.Notifications, HasPos = o != null,
                    RegionId = Regions.HomeValleyLayout.RegionId, Pos = o != null ? new Vector3(o.X, 0f, o.Y) : Vector3.zero, Section = "outpost",
                    Cls = o != null && o.Intact == 0 && o.Count > 0 ? "ar-row-warn" : null,
                });
                foreach (AwayEntryRecord e in r.Entries)
                {
                    if (e != null && e.OutpostId == id)
                    {
                        AwayLine line = EntryLine(e);
                        line.Section = "outpost";
                        into.Add(line);
                    }
                }
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

        // ── FG8-EXP-05 远征结算 ──────────────────────────────────────────────

        public const string SectionSettle = "settle";

        /// <summary>
        /// 远征结算段（FGR-EXP-050）：结局与耗时 → 带回的物品（去处）→ 数据包 → 三选一记录 → 任务进度 → 熔合线索 → 暴露变化 → 机器经历 → 留在原地的东西（倒计时 / 永不消失）→ 全灭说明。
        /// 每一块没有内容时写一行空状态（说明“没有”，不是缺这一块）。O(结算条目)，只在面板重建时。
        /// </summary>
        public static void BuildSettlement(CampaignState state, AwayReportRecord r, List<AwayLine> into)
        {
            ExpeditionSettlementRecord st = r.Settlement;
            if (st == null)
            {
                return;
            }
            Title(into, "settle.section", SectionSettle);
            string via = GameText.Get("settle.via." + (string.IsNullOrEmpty(st.Via) ? ExpeditionSettlementService.ViaFoot : st.Via));
            long end = r.EndTick >= 0 ? r.EndTick : GameClock.Ticks;
            into.Add(new AwayLine
            {
                Text = GameText.Format("settle.head", via, AwayReportService.SiteName(r.RegionId), AwayReportService.Duration(end - r.StartTick)),
                Action = AwayLineAction.Roster, Section = SectionSettle, Cls = st.Via == ExpeditionSettlementService.ViaWiped ? "ar-row-warn" : null,
            });
            // 带回的物品
            int shown = 0;
            foreach (SettleItemRecord it in st.Items ?? Array.Empty<SettleItemRecord>())
            {
                if (it == null)
                {
                    continue;
                }
                shown++;
                bool cargo = it.Kind == ExpeditionSettlementService.KindCargo;
                into.Add(new AwayLine
                {
                    Text = GameText.Format("settle.item.row", ItemName(state, it),
                        it.Dest == ExpeditionSettlementService.DestAnalysisWait && !string.IsNullOrEmpty(it.Reason)
                            ? GameText.Format("settle.dest.analysis_fail", HomeValleyAnalysis.ReasonText(it.Reason)) // 修复第 2 轮：没能排进解析台时写明原因
                            : GameText.Get("settle.dest." + (string.IsNullOrEmpty(it.Dest) ? "warehouse" : it.Dest))),
                    Action = cargo && it.Dest == ExpeditionSettlementService.DestCargo ? AwayLineAction.Cargo
                        : cargo && ItemCatalog.TryGet(it.ItemId, out _) ? AwayLineAction.StatsItem : AwayLineAction.Notifications,
                    Arg = it.ItemId ?? string.Empty, Section = SectionSettle,
                });
            }
            if (shown == 0)
            {
                Dim(into, "settle.items.none", SectionSettle);
            }
            if (st.CargoKept)
            {
                into.Add(new AwayLine { Text = GameText.Get("settle.cargo_kept"), Action = AwayLineAction.Cargo, Section = SectionSettle, Cls = "ar-row-warn" });
            }
            if (st.PacketsHome > 0)
            {
                into.Add(new AwayLine { Text = GameText.Format("settle.packets", st.PacketsHome), Action = AwayLineAction.LootPick, Section = SectionSettle });
            }
            // 三选一
            if ((st.Picks?.Length ?? 0) == 0)
            {
                Dim(into, "settle.picks.none", SectionSettle);
            }
            foreach (LootPickLogRecord p in st.Picks ?? Array.Empty<LootPickLogRecord>())
            {
                if (p == null)
                {
                    continue;
                }
                LootPickDef def = LootPickCatalog.Find(p.ChosenRowId);
                string name = def != null
                    ? LootPickService.CandidateName(new LootCandidateRecord { Kind = def.Kind, ItemId = def.Item?.Id, TargetId = def.Target, Amount = Math.Max(1, def.Min) })
                    : GameText.Get("settle.pick.unknown");
                into.Add(new AwayLine
                {
                    Text = GameText.Format("settle.pick.row", GameText.Get("settle.pick.src." + (string.IsNullOrEmpty(p.Source) ? "elite" : p.Source)), name,
                        LootPickService.RarityName(p.ChosenTier), GameText.Get(p.Destination == "cargo" ? "settle.pick.to_cargo" : "settle.pick.to_depot"), p.TechData),
                    Action = AwayLineAction.Notifications, Section = SectionSettle,
                });
            }
            // 任务进度
            if ((st.Quests?.Length ?? 0) == 0)
            {
                Dim(into, "settle.quests.none", SectionSettle);
            }
            foreach (SettleQuestRecord q in st.Quests ?? Array.Empty<SettleQuestRecord>())
            {
                if (q == null)
                {
                    continue;
                }
                ObjectiveRecord o = FindObjective(state, q.ObjectiveId);
                string title = o != null ? CampaignObjectiveTracker.QuestTitle(o) : q.ObjectiveId;
                bool failed = q.State == (int)ObjectiveState.Failed;
                into.Add(new AwayLine
                {
                    Text = GameText.Format(failed ? "settle.quest.failed" : "settle.quest.done", title),
                    Action = AwayLineAction.Notifications, Section = SectionSettle, Cls = failed ? "ar-row-warn" : null,
                });
            }
            // 熔合线索
            if ((st.Clues?.Length ?? 0) == 0)
            {
                Dim(into, "settle.clues.none", SectionSettle);
            }
            foreach (int serial in st.Clues ?? Array.Empty<int>())
            {
                FusionClueRecord c = FindClue(state, serial);
                into.Add(new AwayLine
                {
                    Text = GameText.Format("settle.clue.row", c != null ? FusionService.ClueText(state, c) : "#" + serial.ToString(CultureInfo.InvariantCulture)),
                    Action = AwayLineAction.Notifications, Section = SectionSettle,
                });
            }
            // 暴露变化
            if (st.HasExposure)
            {
                float d = st.ExposureEnd - st.ExposureStart;
                string delta = (d > 0.05f ? "+" : string.Empty) + d.ToString("0.#", CultureInfo.InvariantCulture);
                into.Add(new AwayLine
                {
                    Text = GameText.Format("settle.exposure", st.ExposureStart.ToString("0.#", CultureInfo.InvariantCulture), st.ExposureEnd.ToString("0.#", CultureInfo.InvariantCulture), delta),
                    Action = AwayLineAction.Notifications, Section = SectionSettle, Cls = d > 0.05f ? "ar-row-warn" : null,
                });
            }
            // 机器经历
            foreach (SettleMachineRecord m in st.Machines ?? Array.Empty<SettleMachineRecord>())
            {
                if (m == null)
                {
                    continue;
                }
                string who = MachineNaming.Short(m.LogicId);
                string text = m.Alive
                    ? GameText.Format(m.Injured ? "settle.machine.back_injured" : "settle.machine.back", who, m.Expeditions)
                    : GameText.Format("settle.machine.lost", who);
                into.Add(new AwayLine { Text = text, Action = AwayLineAction.Machine, LogicId = m.LogicId, Section = SectionSettle, Cls = m.Alive ? null : "ar-row-warn" });
            }
            // 留在原地的东西
            int left = (st.Left?.Length ?? 0) + st.LeftMore;
            into.Add(new AwayLine { Text = left > 0 ? GameText.Format("settle.left.title", left) : GameText.Get("settle.left.none"), Cls = left > 0 ? "ar-row-title" : "ar-row-dim", Section = SectionSettle });
            foreach (SettleLeftRecord e in st.Left ?? Array.Empty<SettleLeftRecord>())
            {
                if (e == null)
                {
                    continue;
                }
                // 家园所在星球上的点击定位；远征地点里的（地点没载入时镜头去不了）点开通知中心（阵亡 / 掉落通知里有它掉在哪），不假装能定位。
                bool planet = string.IsNullOrEmpty(e.RegionId) || e.RegionId == HomeValleyLayout.RegionId;
                into.Add(new AwayLine
                {
                    Text = ExpeditionSettlementService.LeftText(state, e), Action = planet ? AwayLineAction.Locate : AwayLineAction.Notifications, HasPos = planet, RegionId = e.RegionId,
                    Pos = new Vector3(e.X, 0f, e.Y), Section = SectionSettle, Cls = e.ExpireTick > 0 ? "ar-row-warn" : null,
                });
            }
            if (st.LeftMore > 0)
            {
                into.Add(new AwayLine { Text = GameText.Format("settle.left.more", st.LeftMore), Cls = "ar-row-dim", Section = SectionSettle });
            }
            // 全灭说明（FGR-EXP-031；卡片“全灭时的处理说明”）
            if (st.Via == ExpeditionSettlementService.ViaWiped)
            {
                Title(into, "settle.wipe.title", SectionSettle);
                into.Add(new AwayLine { Text = GameText.Get("settle.wipe.explain_items"), Cls = "ar-row-dim", Section = SectionSettle });
                into.Add(new AwayLine { Text = GameText.Get("settle.wipe.explain_expire"), Cls = "ar-row-dim", Section = SectionSettle });
                into.Add(new AwayLine { Text = GameText.Get("settle.wipe.explain_signal"), Cls = "ar-row-dim", Section = SectionSettle });
            }
        }

        /// <summary>带回的一份东西的名字（货舱的货按种类拼；关键物按回收实例 ID 找）。</summary>
        public static string ItemName(CampaignState state, SettleItemRecord it)
        {
            if (it.Kind == ExpeditionSettlementService.KindCargo)
            {
                return LootPickService.CandidateName(new LootCandidateRecord { Kind = it.CargoKind, ItemId = it.ItemId, TargetId = it.TargetId, Amount = it.Amount });
            }
            foreach (RegionQuestItemRecord q in state?.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>())
            {
                if (q != null && q.SalvageInstanceId == it.TargetId)
                {
                    return ExpeditionSettlementService.QuestName(q);
                }
            }
            return Feedback.FeedbackCues.QuestItemName(it.ItemId);
        }

        private static ObjectiveRecord FindObjective(CampaignState state, string id)
        {
            foreach (ObjectiveRecord o in state?.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (o != null && o.ObjectiveId == id)
                {
                    return o;
                }
            }
            return null;
        }

        private static FusionClueRecord FindClue(CampaignState state, int serial)
        {
            foreach (FusionClueRecord c in state?.Research?.Fusion?.Clues ?? Array.Empty<FusionClueRecord>())
            {
                if (c != null && c.Serial == serial)
                {
                    return c;
                }
            }
            return null;
        }

        private static void Title(List<AwayLine> into, string key, string section, int arg = -1) =>
            into.Add(new AwayLine { Text = arg >= 0 ? GameText.Format(key, arg) : GameText.Get(key), Cls = "ar-row-title", Section = section });

        private static void Dim(List<AwayLine> into, string key, string section) =>
            into.Add(new AwayLine { Text = GameText.Get(key), Cls = "ar-row-dim", Section = section });
    }
}
