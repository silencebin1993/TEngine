using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG9-ACT1-01（FG16 第 2 节 0:00～8:00；设计案 6.5“第一幕在第一次远征之后开放基础传送带”；FG13 FGR-UX-040）：第一幕节奏台账。唯一写入口
    /// （<see cref="CampaignProgressState.Pacing"/>）。
    ///
    /// - 节奏点（fg.TbActPacing）按 condition 判定到达：存档事实（建筑、底盘、远征地点、首领、幕、静默夜、研究、传送带）每 pace.check_seconds 检查一次；
    ///   “这个机制在本局发生过”的事件（接入、建炮塔、突袭预警……）由 <see cref="GuidanceHooks.Touched"/> 在发生的那一步记下。只进不退，记第一次到达的那一步。
    /// - 到达时：发节奏点的引导钩子（引导内容统一在 FG15-UX-04）、解锁写明的图鉴条目、开放写明的建造菜单条目（unlockRule = pace:&lt;节奏点&gt;，
    ///   <see cref="IsGateOpen"/>），开放了新内容的发一条“新系统”通知；GameEvent <see cref="ReachedEvent"/>（参数：节奏点 ID）。
    /// - 跳过（卡片负向“玩家跳过某个系统，不影响主线推进”）：mainline = 0 的节奏点没到也不挡任何东西——主线节奏点的条件里不引用它们（FgAct1SelfCheck 断言），
    ///   剧情节点、幕推进、远征解锁都不读本台账。
    /// - 旧档（节奏台账上线前的存档）：第一次载入时按事实补记已经满足的节奏点（Migrated = true，不发通知、不发钩子，只补图鉴），
    ///   研发 / 节奏上线前放下的传送带照常可用（belts_open 的条件含 belt_any）。
    /// 只看存档与步序号（与观察、帧率、倍速无关；暂停时不走）；每次检查 O(节奏点)，事件 O(1)。
    /// </summary>
    public static class Act1PacingService
    {
        public const string RulePrefix = "pace:";
        public const string ReachedEvent = "Act1.PaceReached";
        public const string NotifyOpen = "pace_open";

        public const string CondNewGame = "new_game";
        public const string CondBuilding = "building";
        public const string CondChassis = "chassis";
        public const string CondSiteEntered = "site_entered";
        public const string CondSiteBack = "site_back";
        public const string CondLeader = "leader";
        public const string CondAct = "act";
        public const string CondSilentNights = "silent_nights";
        public const string CondResearch = "research";
        public const string CondBeltAny = "belt_any";
        public const string CondHook = "hook";

        /// <summary>里程碑旅程 M1～M8 写于节奏台账之前：由旅程机器人在开局打开（与研发树的 LegacyGatesOpenForTests 同一口径），真实游戏恒为 false。</summary>
        public static bool LegacyGatesOpenForJourneys;

        public static int Revision { get; private set; } = 1;
        public static int ReachedCount { get; private set; }
        public static string LastReached { get; private set; }

        private static bool _listening;
        private static readonly List<ActPacing> Rows = new List<ActPacing>(16);
        private static object _rowsSource;

        // ─────────────────────────────── 表 ───────────────────────────────

        public static IReadOnlyList<ActPacing> AllRows()
        {
            TbActPacing table = null;
            try
            {
                table = ConfigSystem.Instance.Tables?.TbActPacing;
            }
            catch (Exception)
            {
                table = null;
            }
            if (ReferenceEquals(table, _rowsSource) && (table == null || Rows.Count > 0))
            {
                return Rows;
            }
            _rowsSource = table;
            Rows.Clear();
            if (table != null)
            {
                Rows.AddRange(table.DataList);
                Rows.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            }
            return Rows;
        }

        public static ActPacing Row(string id)
        {
            foreach (ActPacing r in AllRows())
            {
                if (r.Id == id)
                {
                    return r;
                }
            }
            return null;
        }

        public static bool IsPaceRule(string rule) => rule != null && rule.StartsWith(RulePrefix, StringComparison.Ordinal) && rule.Length > RulePrefix.Length;

        public static string PointOf(string rule) => IsPaceRule(rule) ? rule.Substring(RulePrefix.Length) : null;

        // ─────────────────────────────── 查询 ───────────────────────────────

        public static ActPaceRecord Find(CampaignState s, string id)
        {
            ActPaceRecord[] list = s?.Progress?.Pacing;
            if (list == null)
            {
                return null;
            }
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null && list[i].Id == id)
                {
                    return list[i];
                }
            }
            return null;
        }

        public static bool IsReached(CampaignState s, string id) => Find(s, id) != null;

        /// <summary>建造菜单的 pace:&lt;节奏点&gt; 门槛：节奏点到了才开放（全量自检的旧段 / M1～M8 旅程按开放前的规则视为已满足）。</summary>
        public static bool IsGateOpen(CampaignState s, string pointId) =>
            Economy.ResearchGate.LegacyGatesOpenForTests || LegacyGatesOpenForJourneys || IsReached(s, pointId);

        /// <summary>“传送带 T1：还没到（第一幕节奏）”/“已开放”。</summary>
        public static string Describe(CampaignState s, string rule)
        {
            string id = PointOf(rule);
            ActPacing row = id != null ? Row(id) : null;
            if (row == null)
            {
                return string.Empty;
            }
            return GameText.Format(IsGateOpen(s, id) ? "pace.gate.done" : "pace.gate.locked", GameText.Get(row.TitleKey));
        }

        /// <summary>到达时刻（游戏分钟）；没到返回 -1。</summary>
        public static double MinuteOf(CampaignState s, string id)
        {
            ActPaceRecord r = Find(s, id);
            return r == null ? -1 : r.Tick / (double)Math.Max(1, GameClock.StepHz) / 60.0;
        }

        // ─────────────────────────────── 推进 ───────────────────────────────

        public static void EnsureListening()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            GuidanceHooks.Touched += OnHookTouched;
        }

        public static void ResetSessionState()
        {
            ReachedCount = 0;
            LastReached = null;
        }

        /// <summary>
        /// 进入战役（新建 / 读档 / 回滚）：订阅钩子；新游戏（统一时钟还没走过一步）把 new_game 的节奏点记到；节奏台账上线前的旧档按事实补记（不通知）。
        /// 读档 / 回滚：节奏台账就在存档里，什么也不改（前后逐字段一致）。
        /// </summary>
        public static void OnCampaignEntered(CampaignState s)
        {
            EnsureListening();
            if (s?.Progress == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(s);
            long ticks = s.Clock?.Ticks ?? 0;
            if (ticks == 0)
            {
                Evaluate(s, 0, quiet: false);
                return;
            }
            if (s.Progress.Pacing.Length == 0)
            {
                Evaluate(s, ticks, quiet: true); // 旧档迁移：补记，不通知
            }
        }

        /// <summary>每 pace.check_seconds 检查一次（只看步序号）。</summary>
        public static int WorldStep(CampaignState s, long tick)
        {
            if (s?.Progress == null)
            {
                return 0;
            }
            long every = Math.Max(1L, (long)Math.Round(Tuning("pace.check_seconds", 1f) * GameClock.StepHz));
            if (tick % every != 0)
            {
                return 0;
            }
            // 修复轮（第 1 轮审查 P2）：进入第 2 幕、第一幕结束点已记下后不再检查第一幕的节奏点（没走到的可跳过系统就停在“未到达”，统计照常显示），
            // 第一幕之后不再每秒扫建筑 / 机器。
            if ((s.Progress.Act >= 2) && IsReached(s, "act1_end"))
            {
                return 0;
            }
            return Evaluate(s, tick, quiet: false);
        }

        /// <summary>按存档事实把满足条件的节奏点记到（钩子条件只认本局记下的事实，见 <see cref="OnHookTouched"/>）。返回本次新记几个。</summary>
        public static int Evaluate(CampaignState s, long tick, bool quiet)
        {
            int n = 0;
            foreach (ActPacing row in AllRows())
            {
                if (IsReached(s, row.Id) || !Satisfied(s, row.Condition, null, migrating: quiet))
                {
                    continue;
                }
                Reach(s, row, tick, quiet);
                n++;
            }
            return n;
        }

        private static void OnHookTouched(string hookId)
        {
            CampaignState s = CampaignSession.Current;
            if (s?.Progress == null || string.IsNullOrEmpty(hookId))
            {
                return;
            }
            // 修复轮（第 1 轮审查 P2）：钩子路径只认条件里写了 hook:<这个钩子> 的节奏点（按钩子建好的索引，O(1) 查表），
            // 不再在每次钩子（掉落、开面板……）时全量判定建筑 / 机器事实——那些事实只在 WorldStep 的周期检查里记，时刻只看存档与步序号。
            List<ActPacing> rows = RowsForHook(hookId);
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (!IsReached(s, rows[i].Id))
                {
                    Reach(s, rows[i], GameClock.Ticks, quiet: false);
                }
            }
        }

        private static readonly Dictionary<string, List<ActPacing>> HookIndex = new Dictionary<string, List<ActPacing>>(StringComparer.Ordinal);
        private static object _hookIndexSource;
        private static bool _hookIndexBuilt;

        /// <summary>条件里写了 hook:&lt;ID&gt; 的节奏点（表重载时重建一次）。</summary>
        private static List<ActPacing> RowsForHook(string hookId)
        {
            IReadOnlyList<ActPacing> all = AllRows();
            if (!_hookIndexBuilt || !ReferenceEquals(_hookIndexSource, _rowsSource))
            {
                HookIndex.Clear();
                _hookIndexSource = _rowsSource;
                _hookIndexBuilt = true;
                foreach (ActPacing row in all)
                {
                    if (string.IsNullOrEmpty(row.Condition))
                    {
                        continue;
                    }
                    foreach (string raw in row.Condition.Split('|'))
                    {
                        string c = raw.Trim();
                        if (!c.StartsWith(CondHook + ":", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string id = c.Substring(CondHook.Length + 1);
                        if (!HookIndex.TryGetValue(id, out List<ActPacing> list))
                        {
                            HookIndex[id] = list = new List<ActPacing>(2);
                        }
                        list.Add(row);
                    }
                }
            }
            return HookIndex.TryGetValue(hookId, out List<ActPacing> hit) ? hit : null;
        }

        /// <summary>condition 里“|”分隔的任一事实成立。<paramref name="touchedHook"/> 非空时 hook:&lt;ID&gt; 只认这一个刚发生的钩子；
        /// <paramref name="migrating"/> = 旧档迁移补记（belt_any 只在这时认：节奏台账上线前已经铺了传送带的旧档，正常游戏里传送带要等 belts_open 才能铺）。</summary>
        public static bool Satisfied(CampaignState s, string condition, string touchedHook, bool migrating = false)
        {
            if (string.IsNullOrEmpty(condition))
            {
                return false;
            }
            foreach (string raw in condition.Split('|'))
            {
                string c = raw.Trim();
                int colon = c.IndexOf(':');
                string head = colon < 0 ? c : c.Substring(0, colon);
                string arg = colon < 0 ? string.Empty : c.Substring(colon + 1);
                if (Fact(s, head, arg, touchedHook, migrating))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Fact(CampaignState s, string head, string arg, string touchedHook, bool migrating)
        {
            switch (head)
            {
                case CondNewGame:
                    return true;
                case CondHook:
                    return touchedHook != null && touchedHook == arg;
                case CondBuilding:
                    if (s.BuildingRecords == null)
                    {
                        return false;
                    }
                    foreach (BuildingRecord b in s.BuildingRecords)
                    {
                        if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == arg && b.ConstructionState == BuildingConstructionState.Operational)
                        {
                            return true;
                        }
                    }
                    return false;
                case CondChassis:
                    if (ReferenceEquals(CampaignSession.Current, s))
                    {
                        foreach (MachineRecord m in MachineRegistry.AllRecords)
                        {
                            if (m != null && m.ChassisId == arg)
                            {
                                return true;
                            }
                        }
                    }
                    foreach (MachineRecord m in s.MachineRecords ?? Array.Empty<MachineRecord>())
                    {
                        if (m != null && m.ChassisId == arg)
                        {
                            return true;
                        }
                    }
                    return false;
                case CondSiteEntered:
                    return EnteredSite(s, arg);
                case CondSiteBack:
                {
                    if (!EnteredSite(s, arg))
                    {
                        return false;
                    }
                    ExpeditionMarchState march = ExpeditionMarchService.Current(s);
                    bool teamOut = march?.Members != null && march.Members.Length > 0
                        && march.Phase != ExpeditionMarchService.PhaseNone && march.Phase != ExpeditionMarchService.PhaseReturned;
                    return !teamOut && !WorldSimulation.IsSiteLoaded(arg);
                }
                case CondLeader:
                    return TerritoryControlService.Find(s, arg)?.LeaderDefeated ?? false;
                case CondAct:
                    return int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int act) && (s.Progress?.Act ?? 1) >= act;
                case CondSilentNights:
                    return int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nights) && (s.Weather?.SilentNight?.Count ?? 0) >= nights;
                case CondResearch:
                    return Economy.ResearchGate.TreeAvailable && Economy.ResearchGate.IsCompleted(s, arg);
                case CondBeltAny:
                    // 修复轮（第 1 轮审查 P1）：只在旧档迁移时认——正常游戏里整族传送带都挂 pace:belts_open，铺不出来；旅程旧段（门槛视为已开）也不靠它提前到达。
                    return migrating && BeltCells(s) > 0;
                default:
                    return false;
            }
        }

        private static bool EnteredSite(CampaignState s, string regionId)
        {
            if (s.RegionRecords == null)
            {
                return false;
            }
            foreach (RegionRecord r in s.RegionRecords)
            {
                if (r != null && r.RegionId == regionId)
                {
                    return r.ExpeditionCount > 0 && r.State >= RegionState.Active;
                }
            }
            return false;
        }

        private static int BeltCells(CampaignState s)
        {
            if (ReferenceEquals(CampaignSession.Current, s) && Logistics.BeltNetworkService.Kernel != null)
            {
                return Logistics.BeltNetworkService.Kernel.CellCount;
            }
            return s.Belts?.CellCount ?? 0;
        }

        private static void Reach(CampaignState s, ActPacing row, long tick, bool quiet)
        {
            CampaignFgStateDomains.EnsureAll(s);
            var list = new List<ActPaceRecord>(s.Progress.Pacing) { new ActPaceRecord { Id = row.Id, Tick = Math.Max(0, tick), Migrated = quiet } };
            // 存档里按表的先后排（与到达先后无关的稳定顺序）。
            list.Sort((a, b) => (Row(a.Id)?.SortOrder ?? int.MaxValue).CompareTo(Row(b.Id)?.SortOrder ?? int.MaxValue));
            s.Progress.Pacing = list.ToArray();
            Revision++;
            ReachedCount++;
            LastReached = row.Id;
            if (!string.IsNullOrEmpty(row.CodexIds) && row.CodexIds != "none")
            {
                foreach (string cid in row.CodexIds.Split(','))
                {
                    Progression.MechanicCodex.Unlock(cid.Trim());
                }
            }
            if (quiet)
            {
                return;
            }
            GuidanceHooks.Raise(row.HookId);
            if (!string.IsNullOrEmpty(row.Opens) && row.Opens != "none")
            {
                NotificationCenter.Post(NotifyOpen, GameText.Format("pace.notify.open", GameText.Get(row.TitleKey), GameText.Get(row.DescKey)));
            }
            GameEvent.Send(ReachedEvent, row.Id);
            if (!string.IsNullOrEmpty(row.Opens) && row.Opens != "none")
            {
                CampaignObjectiveTracker.Recompute(s); // 随之开放的指引任务（传送带指引，出现时机 pace）当场出现
            }
            Log.Info($"[Act1PacingService] 节奏点 {row.Id} 到达：第 {tick} 步（{FormatMinute(tick / (double)Math.Max(1, GameClock.StepHz) / 60.0)}，目标 {FormatMinute(row.Minute)}）");
        }

        /// <summary>测试 / 冒烟：把某个节奏点当作已到达（走与正式到达同一条路径：钩子、图鉴、开放、通知）。</summary>
        public static bool MarkReachedForTests(CampaignState s, string id)
        {
            ActPacing row = Row(id);
            if (s?.Progress == null || row == null || IsReached(s, id))
            {
                return false;
            }
            Reach(s, row, GameClock.Ticks, quiet: false);
            return true;
        }

        public static string FormatMinute(double minute)
        {
            int total = (int)Math.Round(Math.Max(0, minute));
            return GameText.Format("pace.stats.time", total / 60, total % 60);
        }

        /// <summary>统计面板“第一幕节奏”段：每个节奏点一行“名字：实际（目标）”，偏离超过 pace.deviation_warn 标黄；可跳过的没到写“可跳过”。O(节奏点)。</summary>
        public static void BuildStatsLines(CampaignState s, List<(string Text, string Cls)> into)
        {
            if (s?.Progress == null || into == null || AllRows().Count == 0)
            {
                return;
            }
            into.Add((GameText.Get("pace.stats.title"), "st-row-title"));
            float warn = Tuning("pace.deviation_warn", 0.25f);
            foreach (ActPacing row in AllRows())
            {
                string name = GameText.Get(row.TitleKey);
                ActPaceRecord r = Find(s, row.Id);
                if (r == null)
                {
                    into.Add((row.Mainline == 1 ? GameText.Format("pace.stats.row_pending", name, FormatMinute(row.Minute)) : GameText.Format("pace.stats.row_skipped", name), "st-row-dim"));
                    continue;
                }
                double minute = MinuteOf(s, row.Id);
                double dev = row.Minute > 0 ? Math.Abs(minute - row.Minute) / row.Minute : 0;
                if (!r.Migrated && dev > warn)
                {
                    into.Add((GameText.Format("pace.stats.row_done_warn", name, FormatMinute(minute), FormatMinute(row.Minute), Math.Round(dev * 100).ToString(CultureInfo.InvariantCulture)), "st-row-warn"));
                }
                else
                {
                    into.Add((GameText.Format("pace.stats.row_done", name, FormatMinute(minute), FormatMinute(row.Minute)), null));
                }
            }
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
