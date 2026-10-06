using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>突袭历史的一项：有结算的（<see cref="Result"/>）或只有导演历史、没有在家园展开攻城的（<see cref="History"/>）。</summary>
    public readonly struct RaidHistoryEntry
    {
        public readonly RaidResultRecord Result;
        public readonly RaidHistoryRecord History;
        public readonly long SortTick;

        public RaidHistoryEntry(RaidResultRecord result, RaidHistoryRecord history, long sortTick)
        {
            Result = result;
            History = history;
            SortTick = sortTick;
        }

        /// <summary>选择键（面板记住选中的是哪一项）：R序号 / H计划ID。</summary>
        public string Key => Result != null ? "R" + Result.Serial.ToString(CultureInfo.InvariantCulture) : "H" + (History?.PlanId ?? string.Empty);
    }

    /// <summary>
    /// FG6-DEF-08：突袭结算的文字组装（突袭历史面板、离家报告“突袭”段）。只在面板打开 / 数据变化 / 语言变化时调用，O(时间线 + 损失 + 贡献 + 战利品)。
    /// 文字全部走文本键；数值格式与统计面板同一套。行复用离家报告的行（<see cref="AwayLine"/>：可点的定位 / 打开面板）。
    /// </summary>
    public static partial class RaidResultService
    {
        private static int Hz => Math.Max(1, GameClock.StepHz);

        private static double Sec(long tick) => Math.Max(0, tick) / (double)Hz;

        public static string FactionText(string faction) => WorldTransitSystem.OriginName(faction);

        public static string LevelText(int level) => GameText.Has("raid.level." + level.ToString(CultureInfo.InvariantCulture))
            ? GameText.Get("raid.level." + level.ToString(CultureInfo.InvariantCulture)) : level.ToString(CultureInfo.InvariantCulture);

        /// <summary>突袭历史：有结算的与只有导演历史的合在一起，最新的在前。</summary>
        public static List<RaidHistoryEntry> HistoryEntries(CampaignState s)
        {
            var list = new List<RaidHistoryEntry>();
            var withResult = new HashSet<string>(StringComparer.Ordinal);
            foreach (RaidResultRecord r in All(s))
            {
                if (r == null)
                {
                    continue;
                }
                list.Add(new RaidHistoryEntry(r, null, r.UnfoldTick));
                if (!string.IsNullOrEmpty(r.PlanId))
                {
                    withResult.Add(r.PlanId);
                }
            }
            foreach (RaidHistoryRecord h in RaidDirectorService.History(s))
            {
                if (h == null || (!string.IsNullOrEmpty(h.PlanId) && withResult.Contains(h.PlanId)))
                {
                    continue;
                }
                list.Add(new RaidHistoryEntry(null, h, h.EndTick));
            }
            list.Sort((a, b) =>
            {
                bool ao = a.Result != null && a.Result.EndTick < 0, bo = b.Result != null && b.Result.EndTick < 0;
                if (ao != bo)
                {
                    return ao ? -1 : 1; // 进行中的排最前
                }
                int c = b.SortTick.CompareTo(a.SortTick);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        /// <summary>选择框里的一项：“第 3 波 · 铸造 · 全部击毁（第 4 天 13:20）”。</summary>
        public static string ChoiceText(in RaidHistoryEntry e)
        {
            if (e.Result != null)
            {
                RaidResultRecord r = e.Result;
                double t = Sec(r.EndTick >= 0 ? r.EndTick : r.UnfoldTick);
                return r.EndTick < 0
                    ? GameText.Format("raid.result.panel.choice_open", WaveLabel(r), FactionText(r.Faction))
                    : GameText.Format("raid.result.panel.choice", WaveLabel(r), FactionText(r.Faction) + GameText.Get("rules.sep_dot") + OutcomeText(r),
                        GameClock.DayOf(t), GameClock.FormatHhMm(t));
            }
            RaidHistoryRecord h = e.History;
            double ht = Sec(h?.EndTick ?? 0);
            return GameText.Format("raid.result.panel.choice", h != null && h.Wave > 0 ? GameText.Format("raid.result.wave", h.Wave) : GameText.Get("raid.result.wave_unplanned"),
                FactionText(h?.Faction) + GameText.Get("rules.sep_dot") + EndReasonText(h?.EndReason), GameClock.DayOf(ht), GameClock.FormatHhMm(ht));
        }

        public static string EndReasonText(string reason)
        {
            string key = "raid.end." + (string.IsNullOrEmpty(reason) ? RaidDirectorService.EndGone : reason);
            return GameText.Has(key) ? GameText.Get(key) : reason ?? string.Empty;
        }

        /// <summary>一份结算的概要（面板顶部）。</summary>
        public static string SummaryText(in RaidHistoryEntry e)
        {
            if (e.Result != null)
            {
                RaidResultRecord r = e.Result;
                long start = r.ArrivedTick >= 0 ? r.ArrivedTick : r.UnfoldTick;
                long end = r.EndTick >= 0 ? r.EndTick : GameClock.Ticks;
                double s = Sec(start);
                return GameText.Format("raid.result.summary", WaveLabel(r), FactionText(r.Faction), LevelText(r.Level), GameClock.DayOf(s), GameClock.FormatHhMm(s),
                    RaidDirectorService.Duration(end - start), OutcomeText(r));
            }
            RaidHistoryRecord h = e.History;
            if (h == null)
            {
                return string.Empty;
            }
            double hs = Sec(h.EndTick);
            return GameText.Format("raid.result.summary_history", h.Wave > 0 ? GameText.Format("raid.result.wave", h.Wave) : GameText.Get("raid.result.wave_unplanned"),
                FactionText(h.Faction), LevelText(h.Level), GameClock.DayOf(hs), GameClock.FormatHhMm(hs), EndReasonText(h.EndReason));
        }

        // ── 面板：一份结算的全部行 ─────────────────────────────────────────────

        public static void BuildDetail(CampaignState s, in RaidHistoryEntry e, List<AwayLine> into)
        {
            into.Clear();
            if (e.Result == null)
            {
                if (e.History != null)
                {
                    Plain(into, GameText.Format("raid.result.no_siege", EndReasonText(e.History.EndReason)), "history");
                }
                return;
            }
            RaidResultRecord r = e.Result;
            AppendTimeline(s, r, into, "timeline");
            Title(into, "raid.result.sec.damage", "damage");
            long dealt = r.EndTick >= 0 ? r.Dealt : LiveDealt(r);
            long taken = r.EndTick >= 0 ? r.Taken : LiveTaken(r);
            Plain(into, GameText.Format("raid.result.dmg", ProductionStats.UiNumber(dealt), ProductionStats.UiNumber(taken)), "damage");
            Plain(into, GameText.Format("raid.result.kills", r.Killed, r.EliteKilled, r.Exited, r.Unfolded), "damage");
            DefenseState ds = s?.Raids?.Defense;
            bool live = r.EndTick < 0 && ds != null;
            Plain(into, GameText.Format("raid.result.defense",
                live ? Math.Max(0, ds.TotalOverloads - r.BaseOverloads) : r.Overloads,
                live ? Math.Max(0, ds.TotalLays - r.BaseLays) : r.Lays,
                ProductionStats.UiNumber((float)(live ? Math.Max(0, ds.TotalRepaired - r.BaseRepaired) : r.Repaired)),
                live ? Math.Max(0, ds.TotalKitsUsed - r.BaseKits) : r.Kits), "damage");
            AppendReactions(s, r, into);
            AppendLosses(r, into, int.MaxValue);
            AppendContrib(s, r, into, ContribTop, "contrib");
            AppendLoot(r, into);
            Title(into, "raid.result.sec.wreck", "wreck");
            Plain(into, WreckStatusText(s), "wreck");
        }

        private static long LiveDealt(RaidResultRecord r)
        {
            Combat.CombatSite site = HomeSite;
            return site != null && !site.IsDisposed ? Math.Max(0, site.KernelCounters.DamageToHostile - r.BaseDealt) : 0;
        }

        private static long LiveTaken(RaidResultRecord r)
        {
            Combat.CombatSite site = HomeSite;
            return site != null && !site.IsDisposed ? Math.Max(0, site.KernelCounters.DamageToPlayer - r.BaseTaken) : 0;
        }

        public static string WreckStatusText(CampaignState s)
        {
            RaidResultState st = StateOf(s);
            bool bench = HomeValleyAnalysis.FindBench(s) != null;
            string buffer = bench
                ? HomeValleyAnalysis.WreckBuffered(s).ToString(CultureInfo.InvariantCulture)
                : GameText.Get("raid.result.wreck.no_bench");
            return GameText.Format("raid.result.wreck.status", WrecksOnField(s), HomeInventory.Stock(s, AnalysisCatalog.WreckId), buffer,
                bench ? AnalysisCatalog.WreckBufferCap.ToString(CultureInfo.InvariantCulture) : "-", RouteText(st?.WreckRouting ?? RouteBench));
        }

        /// <summary>时间线（结算里记的 + 进行中时远征中对这一波刚做的选择）。</summary>
        public static void AppendTimeline(CampaignState s, RaidResultRecord r, List<AwayLine> into, string section)
        {
            Title(into, "raid.result.sec.timeline", section);
            var rows = new List<RaidTimelineRecord>(r.Timeline);
            if (r.EndTick < 0)
            {
                rows.AddRange(MergeDecisions(s, r, persist: false));
                rows.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            }
            foreach (RaidTimelineRecord t in rows)
            {
                if (t == null)
                {
                    continue;
                }
                double sec = Sec(t.Tick);
                var line = new AwayLine
                {
                    Text = GameText.Format("raid.result.tl.row", GameClock.DayOf(sec), GameClock.FormatHhMm(sec), TimelineText(t)),
                    Section = section,
                    Cls = t.Kind == "core_lost" || t.Kind == "first_loss" ? "ar-row-warn" : null,
                };
                if (t.HasPos)
                {
                    line.Action = AwayLineAction.Locate;
                    line.HasPos = true;
                    line.RegionId = HomeValleyLayout.RegionId;
                    line.Pos = new Vector3(t.X, 0f, t.Y);
                }
                into.Add(line);
            }
            if (r.TimelineDropped > 0)
            {
                Plain(into, GameText.Format("raid.result.tl.more", r.TimelineDropped), section);
            }
        }

        public static string TimelineText(RaidTimelineRecord t)
        {
            switch (t.Kind)
            {
                case "warn": return GameText.Get("raid.result.tl.warn");
                case "arrive": return GameText.Format("raid.result.tl.arrive", t.Arg);
                case "unfold": return GameText.Format("raid.result.tl.unfold", t.Arg);
                case "first_loss": return GameText.Format("raid.result.tl.first_loss", t.Arg);
                case "retreat_losses": return GameText.Get("raid.result.tl.retreat_losses");
                case "retreat_time": return GameText.Get("raid.result.tl.retreat_time");
                case "end_destroyed": return GameText.Get("raid.result.tl.end_destroyed");
                case "end_withdrawn": return GameText.Format("raid.result.tl.end_withdrawn", t.Arg);
                case "core_lost": return GameText.Get("raid.result.tl.core_lost");
                case "choice_jump": return GameText.Get("raid.result.tl.choice_jump");
                case "choice_stay": return GameText.Get("raid.result.tl.choice_stay");
                default: return t.Kind ?? string.Empty;
            }
        }

        private static void AppendReactions(CampaignState s, RaidResultRecord r, List<AwayLine> into)
        {
            Title(into, "raid.result.sec.reaction", "reaction");
            ReactionShareRecord[] shares = r.Reactions;
            double total = r.ReactionTotal;
            if (r.EndTick < 0)
            {
                // 进行中：读还开着的突袭场次（不写记录）。
                foreach (ReactionSessionRecord x in Combat.ReactionAttribution.Sessions(s))
                {
                    if (x != null && x.Kind == Combat.ReactionAttribution.KindRaid && x.EndTick < 0)
                    {
                        shares = x.Reactions ?? Array.Empty<ReactionShareRecord>();
                        total = x.TotalDamage;
                    }
                }
            }
            var sorted = new List<ReactionShareRecord>();
            double reactionDamage = 0;
            foreach (ReactionShareRecord x in shares ?? Array.Empty<ReactionShareRecord>())
            {
                if (x != null && (x.Count > 0 || x.Damage > 0))
                {
                    sorted.Add(x);
                    reactionDamage += Math.Max(0, x.Damage);
                }
            }
            if (sorted.Count == 0)
            {
                Dim(into, "raid.result.reaction_none", "reaction");
                return;
            }
            sorted.Sort((a, b) => a.Damage != b.Damage ? b.Damage.CompareTo(a.Damage) : a.Count != b.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.ReactionId, b.ReactionId));
            var sb = new StringBuilder();
            foreach (ReactionShareRecord x in sorted)
            {
                if (sb.Length > 0)
                {
                    sb.Append(GameText.Get("intel.list_sep"));
                }
                int pct = total > 0 ? Mathf.RoundToInt((float)(100.0 * Math.Min(1.0, x.Damage / total))) : 0;
                sb.Append(GameText.Format("raid.result.reaction_item", Combat.ReactionAttribution.DisplayName(s, x.ReactionId), pct, x.Count));
            }
            Plain(into, sb.ToString(), "reaction");
            int all = total > 0 ? Mathf.RoundToInt((float)(100.0 * Math.Min(1.0, reactionDamage / total))) : 0;
            Plain(into, GameText.Format("raid.result.reaction_total", all), "reaction");
        }

        private static void AppendLosses(RaidResultRecord r, List<AwayLine> into, int maxRows)
        {
            Title(into, "raid.result.sec.losses", "losses");
            if (LossTotal(r) == 0)
            {
                Dim(into, "raid.result.loss.none", "losses");
                return;
            }
            into.Add(new AwayLine
            {
                Text = GameText.Format("raid.result.loss.summary", r.LostBuildings, r.LostTurrets, r.LostDefenses, r.LostMachines, r.LostDrones),
                Section = "losses", Cls = "ar-row-warn",
            });
            int shown = 0;
            foreach (RaidLossRecord l in r.Losses)
            {
                if (l == null)
                {
                    continue;
                }
                if (shown >= maxRows)
                {
                    break;
                }
                shown++;
                double sec = Sec(l.Tick);
                var line = new AwayLine
                {
                    Text = GameText.Format("raid.result.loss.row", GameClock.DayOf(sec), GameClock.FormatHhMm(sec), KindText(l.Kind), l.Name),
                    Section = "losses", Cls = "ar-row-warn",
                    Action = AwayLineAction.Locate, HasPos = true, RegionId = HomeValleyLayout.RegionId, Pos = new Vector3(l.X, 0f, l.Y),
                };
                if (l.Kind == KindMachine && int.TryParse(l.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logic))
                {
                    line.Action = AwayLineAction.Machine;
                    line.LogicId = logic;
                }
                into.Add(line);
            }
            int more = r.LossesDropped + Math.Max(0, CountLosses(r) - shown);
            if (more > 0)
            {
                Plain(into, GameText.Format("raid.result.loss.more", more), "losses");
            }
        }

        private static int CountLosses(RaidResultRecord r)
        {
            int n = 0;
            foreach (RaidLossRecord l in r.Losses)
            {
                if (l != null)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>贡献排行前 <paramref name="top"/> 名（点炮塔 / 防御建筑 = 定位并打开它的面板；点机器 = 名册里这台机器）。</summary>
        public static void AppendContrib(CampaignState s, RaidResultRecord r, List<AwayLine> into, int top, string section)
        {
            Title(into, "raid.result.sec.contrib", section);
            List<RaidContribRecord> ranked = Ranked(r);
            if (ranked.Count == 0 && r.OtherKills <= 0)
            {
                Dim(into, "raid.result.contrib.none", section);
            }
            for (int i = 0; i < ranked.Count && i < top; i++)
            {
                RaidContribRecord c = ranked[i];
                var line = new AwayLine
                {
                    Text = GameText.Format("raid.result.contrib.row", i + 1, c.Name, KindText(c.Kind), c.Kills, c.Elites),
                    Section = section,
                };
                if (c.Kind == KindMachine && int.TryParse(c.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logic))
                {
                    line.Action = AwayLineAction.Machine;
                    line.LogicId = logic;
                    if (MachineRegistry.TryGetRecord(logic, out MachineRecord m) && m.IsAlive && MachineRegistry.TryGetLivePosition(logic, out Vector2 at))
                    {
                        line.HasPos = true;
                        line.RegionId = m.RegionId ?? HomeValleyLayout.RegionId;
                        line.Pos = new Vector3(at.x, 0f, at.y);
                    }
                }
                else
                {
                    BuildingRecord b = HomeGridService.FindBuilding(s, c.Id);
                    line.Action = b != null ? AwayLineAction.Locate : AwayLineAction.None;
                    if (b != null)
                    {
                        line.HasPos = true;
                        line.RegionId = HomeValleyLayout.RegionId;
                        line.Pos = new Vector3(b.Position.x, 0f, b.Position.y);
                    }
                }
                into.Add(line);
            }
            if (r.OtherKills > 0)
            {
                Plain(into, GameText.Format("raid.result.contrib.other", r.OtherKills), section);
            }
        }

        private static void AppendLoot(RaidResultRecord r, List<AwayLine> into)
        {
            Title(into, "raid.result.sec.loot", "loot");
            if (r.Loot.Length == 0)
            {
                Dim(into, "raid.result.loot.none", "loot");
                return;
            }
            Plain(into, LootText(r), "loot");
        }

        public static string LootText(RaidResultRecord r)
        {
            var sb = new StringBuilder();
            foreach (ItemAmountRecord a in r?.Loot ?? Array.Empty<ItemAmountRecord>())
            {
                if (a == null || a.Amount <= 0)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(GameText.Get("intel.list_sep"));
                }
                string name = ItemCatalog.NameOf(a.ItemId);
                sb.Append(GameText.Format("raid.result.loot.row", string.IsNullOrEmpty(name) ? a.ItemId : name, a.Amount));
            }
            return sb.Length > 0 ? sb.ToString() : GameText.Get("raid.result.loot.none");
        }

        // ── 离家报告“突袭”段（FGR-DEF-052）─────────────────────────────────────

        /// <summary>
        /// 一份离家报告期间的突袭（与报告时间段重叠的结算）：每一次一行标题（点开看完整结算）+ 过程时间线（含远征中对这一波做的选择）+ 损失 + 贡献最大的前几名 + 战利品。
        /// 返回写了几次突袭。
        /// </summary>
        public static int AppendAway(CampaignState s, long from, long to, List<AwayLine> into)
        {
            List<RaidResultRecord> list = InWindow(s, from, to);
            foreach (RaidResultRecord r in list)
            {
                int first = into.Count;
                into.Add(new AwayLine
                {
                    Text = GameText.Format("raid.result.title_row", WaveLabel(r), FactionText(r.Faction), LevelText(r.Level), OutcomeText(r)),
                    Section = "raid", Action = AwayLineAction.RaidResult, Arg = r.Serial.ToString(CultureInfo.InvariantCulture),
                    Cls = r.Outcome == OutcomeCoreLost || LossTotal(r) > 0 ? "ar-row-warn" : null,
                });
                var rows = new List<AwayLine>();
                AppendTimeline(s, r, rows, "raid");
                rows.RemoveAt(0); // 标题行在上面已经写了“哪一次突袭”
                into.AddRange(rows);
                into.Add(new AwayLine { Text = GameText.Format("raid.result.kills", r.Killed, r.EliteKilled, r.Exited, r.Unfolded), Section = "raid" });
                if (LossTotal(r) > 0)
                {
                    into.Add(new AwayLine
                    {
                        Text = GameText.Format("raid.result.loss.summary", r.LostBuildings, r.LostTurrets, r.LostDefenses, r.LostMachines, r.LostDrones),
                        Section = "raid", Cls = "ar-row-warn", Action = AwayLineAction.RaidResult, Arg = r.Serial.ToString(CultureInfo.InvariantCulture),
                    });
                }
                if (Ranked(r).Count > 0 || r.OtherKills > 0)
                {
                    // 谁（哪座炮塔、哪台机器）贡献最大：前 raid.result.contrib_top 名，点击定位 / 打开名册；
                    // 全部击毁都来自反应 / 场地 / 来源不明时也写“其它击毁 N 台”那一行（复修 P2：原来整段消失，离家报告答不了“谁贡献最大”）。
                    var tmp = new List<AwayLine>();
                    AppendContrib(s, r, tmp, ContribTop, "raid");
                    foreach (AwayLine l in tmp)
                    {
                        if (l.Cls != "ar-row-title")
                        {
                            into.Add(l);
                        }
                    }
                }
                if (r.Loot.Length > 0)
                {
                    into.Add(new AwayLine { Text = GameText.Format("raid.result.loot.drops", LootText(r)), Section = "raid" });
                }
                // 离家报告里的每一条都可以点击（FG04 第 4 节）：没有自己去处的行（没有位置的过程、击毁统计、战利品……）点开 = 突袭历史面板里这一份结算。
                for (int i = first; i < into.Count; i++)
                {
                    AwayLine l = into[i];
                    if (l.Action == AwayLineAction.None && l.Cls != "ar-row-title" && l.Cls != "ar-row-dim")
                    {
                        l.Action = AwayLineAction.RaidResult;
                        l.Arg = r.Serial.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            return list.Count;
        }

        /// <summary>贡献最大的那一个的一句话（离家报告概要 / 自检）；没有返回空串。</summary>
        public static string BestText(RaidResultRecord r)
        {
            List<RaidContribRecord> ranked = Ranked(r);
            return ranked.Count == 0 ? string.Empty : GameText.Format("away.raid.best", ranked[0].Name, ranked[0].Kills);
        }

        private static void Title(List<AwayLine> into, string key, string section) =>
            into.Add(new AwayLine { Text = GameText.Get(key), Cls = "ar-row-title", Section = section });

        private static void Dim(List<AwayLine> into, string key, string section) =>
            into.Add(new AwayLine { Text = GameText.Get(key), Cls = "ar-row-dim", Section = section });

        private static void Plain(List<AwayLine> into, string text, string section) =>
            into.Add(new AwayLine { Text = text ?? string.Empty, Section = section });
    }
}
