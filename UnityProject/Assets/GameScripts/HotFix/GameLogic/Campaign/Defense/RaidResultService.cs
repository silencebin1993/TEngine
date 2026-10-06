using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-08（FG06 FGR-DEF-050～053；第 4 节“突袭历史”；FGT-DEF-009）：突袭结算、残骸与核心被摧毁的唯一业务入口（热更层）。
    ///
    /// - <b>结算</b>（FGR-DEF-050）：突袭队伍在家园（或前哨站）展开攻城时开一份（<see cref="Begin"/>，记下家园战斗内核的伤害累计读数），
    ///   攻城服务收拢 / 全歼时结束（<see cref="End"/>：全部被击毁 = destroyed，有残部撤离 = withdrawn）。期间按事件记账：
    ///   每次击毁（内核玩法事件 <see cref="CombatEventKind.SiegeKilled"/>，带击杀者）、每处损失（建筑 / 炮塔 / 防御建筑 / 机器 / 维修无人机各自的摧毁入口调 <see cref="NoteLoss"/>）、
    ///   撤退令（时间线）。结束时补上伤害差额、复制反应归因场次的占比、并入远征中对这一波做的选择，发“突袭结算”通知。
    /// - <b>残骸</b>（FGR-DEF-051）：每次击毁在原地留下敌方残骸地面物（近处并堆），每堆一张返还物同款的搬运单（机器搬回仓库，仓库满时等待）；
    ///   仓库里的残骸按玩家设的“残骸去向”由机器送进解析台残骸缓存或回收站输入（<see cref="WorldStep"/> 每 raid.result.route_seconds 检查一次）；少量掉落未解析模块 / 加密固件（确定性抽取）。
    /// - <b>失败</b>（FGR-DEF-053）：攻城对账时归还核心的内核耐久到下限 = 被摧毁（<see cref="CheckCore"/>）：核心记为摧毁（家园冻结、失败页出现），进行中的结算按 core_lost 结束，
    ///   此后 <see cref="CampaignAutoSaveService"/> 拒绝写盘（保住最近的安全档）。突袭预警发出时请求一次自动存档（<see cref="OnRaidWarned"/>，帧末执行），给失败页一个读档点。
    /// 推进只看步序号与事件，与观察无关（FGR-BASE-021）；热更层每次击毁 O(本次结算的贡献者 + 残骸堆 + 地面物查找)，每 raid.result.route_seconds O(进行中的送货单)；逐单位逻辑在内核。
    /// </summary>
    public static partial class RaidResultService
    {
        public const string OutcomeOpen = "open";
        public const string OutcomeDestroyed = "destroyed";
        public const string OutcomeWithdrawn = "withdrawn";
        public const string OutcomeCoreLost = "core_lost";

        public const string KindBuilding = "building";
        public const string KindTurret = "turret";
        public const string KindDefense = "defense";
        public const string KindMachine = "machine";
        public const string KindDrone = "drone";

        public const int RouteBench = 0;
        public const int RouteRecycler = 1;
        public const int RouteStore = 2;
        public const int RouteOff = 3;
        public const int RouteCount = 4;

        /// <summary>残骸送货单的发起方（工单面板 / 建筑面板写“突袭残骸：送往……”，FGR-BASE-020 可追溯）。</summary>
        public const string DeliverIssuer = "raid_wreck";

        // ── 调参 ─────────────────────────────────────────────────────────────
        public static int WreckPerUnit => Math.Max(1, (int)Math.Round(Tuning("raid.result.wreck_per_unit", 1f)));
        public static int WreckPerElite => Math.Max(WreckPerUnit, (int)Math.Round(Tuning("raid.result.wreck_per_elite", 2f)));
        public static float WreckMergeCells => Math.Max(0f, Tuning("raid.result.wreck_merge_cells", 3f));
        public static int WreckPileMax => Math.Max(WreckPerElite, (int)Math.Round(Tuning("raid.result.wreck_pile_max", 5f)));
        public static int PilesMax => Math.Max(1, (int)Math.Round(Tuning("raid.result.piles_max", 48f)));
        public static float DropModuleChance => Mathf.Clamp01(Tuning("raid.result.drop_module_chance", 0.03f));
        public static float DropFirmwareChance => Mathf.Clamp01(Tuning("raid.result.drop_firmware_chance", 0.015f));
        public static float DropEliteMult => Math.Max(1f, Tuning("raid.result.drop_elite_mult", 2f));
        public static int TimelineMax => Math.Max(4, (int)Math.Round(Tuning("raid.result.timeline_max", 24f)));
        public static int LossesMax => Math.Max(1, (int)Math.Round(Tuning("raid.result.losses_max", 32f)));
        public static int ContribMax => Math.Max(1, (int)Math.Round(Tuning("raid.result.contrib_max", 24f)));
        public static int ContribTop => Math.Max(1, (int)Math.Round(Tuning("raid.result.contrib_top", 3f)));
        public static float RouteSeconds => Math.Max(0.5f, Tuning("raid.result.route_seconds", 10f));
        public static int RouteBatch => Math.Max(1, (int)Math.Round(Tuning("raid.result.route_batch", 5f)));
        public static bool AutosaveOnWarning => Tuning("raid.result.autosave_on_warning", 1f) >= 0.5f;
        public static float PanelRefreshSeconds => Math.Max(0.1f, Tuning("raid.result.panel_refresh_seconds", 1f));
        public static float PerfMs => Math.Max(0.001f, Tuning("raid.result.perf_ms", 0.05f));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        // ── 会话统计（自检 / 性能证据）────────────────────────────────────────
        /// <summary>任何结算数据变了 +1（界面据此重建）。</summary>
        public static int Revision { get; private set; } = 1;
        public static long KillEvents { get; private set; }
        public static double KillMs { get; private set; }
        public static double MaxKillMs { get; private set; }
        public static int Begins { get; private set; }
        public static int Ends { get; private set; }
        public static int DeliverOrders { get; private set; }
        public static int AutosaveCount { get; private set; }
        public static int AutosaveRefused { get; private set; }
        public static string LastProblem { get; private set; } = string.Empty;

        /// <summary>
        /// 自检专用：只测撤退时机 / 寻路的长时间攻城段（FgRaidDirectorSelfCheck 的时间上限撤退）关掉“核心打到下限 = 被摧毁”的判定，让攻城跑满时间上限；
        /// 核心被摧毁本身由 FgRaidResultSelfCheck F2 与 FgHomeRaidAlertSelfCheck N4 覆盖。正式代码从不设置。
        /// </summary>
        public static bool CoreLossDisabledForTests;

        /// <summary>等帧末执行的预警自动存档（波次；0 = 没有）。</summary>
        public static int PendingAutosaveWave { get; private set; }

        private static readonly HashSet<string> HookedOnce = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetSessionState()
        {
            KillEvents = 0;
            KillMs = 0;
            MaxKillMs = 0;
            Begins = 0;
            Ends = 0;
            DeliverOrders = 0;
            AutosaveCount = 0;
            AutosaveRefused = 0;
            AutosaveDeferrals = 0;
            PendingAutosaveWave = 0;
            LastProblem = string.Empty;
            HookedOnce.Clear();
            Revision++;
        }

        private static void Hook(string id)
        {
            if (HookedOnce.Add(id))
            {
                GuidanceHooks.Raise(id);
            }
        }

        public static void Touch() => Revision++;

        // ── 存档域 ───────────────────────────────────────────────────────────

        public static RaidResultState StateOf(CampaignState s) => s?.Raids?.Results;

        public static RaidResultState EnsureState(CampaignState s)
        {
            if (s?.Raids == null)
            {
                return null;
            }
            RaidResultState st = s.Raids.Results ??= new RaidResultState();
            st.Results ??= Array.Empty<RaidResultRecord>();
            st.AutosavedWaves ??= Array.Empty<int>();
            if (st.NextSerial < 1)
            {
                st.NextSerial = 1;
            }
            if (st.NextWreckSerial < 1)
            {
                st.NextWreckSerial = 1;
            }
            if (st.WreckRouting < 0 || st.WreckRouting >= RouteCount)
            {
                st.WreckRouting = RouteBench;
            }
            foreach (RaidResultRecord r in st.Results)
            {
                if (r == null)
                {
                    continue;
                }
                r.PlanId ??= string.Empty;
                r.GroupId ??= string.Empty;
                r.Faction ??= string.Empty;
                r.Trigger ??= string.Empty;
                r.TargetKind ??= string.Empty;
                r.Outcome ??= OutcomeOpen;
                r.RetreatReason ??= string.Empty;
                r.ReactionSessionId ??= string.Empty;
                r.Reactions ??= Array.Empty<ReactionShareRecord>();
                r.Timeline ??= Array.Empty<RaidTimelineRecord>();
                r.Losses ??= Array.Empty<RaidLossRecord>();
                r.Contrib ??= Array.Empty<RaidContribRecord>();
                r.Loot ??= Array.Empty<ItemAmountRecord>();
                r.Piles ??= Array.Empty<RaidWreckPileRecord>();
                r.KilledKinds ??= Array.Empty<ItemAmountRecord>();
            }
            return st;
        }

        public static bool IsCoreLost(CampaignState s) => StateOf(s)?.CoreLost ?? false;

        // ── 查询 ─────────────────────────────────────────────────────────────

        public static IReadOnlyList<RaidResultRecord> All(CampaignState s) =>
            (IReadOnlyList<RaidResultRecord>)StateOf(s)?.Results ?? Array.Empty<RaidResultRecord>();

        public static RaidResultRecord Find(CampaignState s, int serial)
        {
            foreach (RaidResultRecord r in All(s))
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        public static RaidResultRecord FindByPlan(CampaignState s, string planId)
        {
            if (string.IsNullOrEmpty(planId))
            {
                return null;
            }
            RaidResultRecord best = null;
            foreach (RaidResultRecord r in All(s))
            {
                if (r != null && r.PlanId == planId)
                {
                    best = r; // 同一计划先后展开过多次（不该发生）时取最新一份
                }
            }
            return best;
        }

        /// <summary>这支队伍进行中的结算（没有 = null）。</summary>
        public static RaidResultRecord OpenFor(CampaignState s, string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                return null;
            }
            foreach (RaidResultRecord r in All(s))
            {
                if (r != null && r.EndTick < 0 && r.GroupId == groupId)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>最早展开、还在进行中的结算（损失记到它身上；没有 = null）。</summary>
        public static RaidResultRecord PrimaryOpen(CampaignState s)
        {
            foreach (RaidResultRecord r in All(s))
            {
                if (r != null && r.EndTick < 0)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>最新一份已结算的（通知点开 / 面板默认）。</summary>
        public static RaidResultRecord LatestEnded(CampaignState s)
        {
            IReadOnlyList<RaidResultRecord> all = All(s);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (all[i] != null && all[i].EndTick >= 0)
                {
                    return all[i];
                }
            }
            return null;
        }

        /// <summary>与时间段 [<paramref name="from"/>, <paramref name="to"/>]（to &lt; 0 = 到现在）有重叠的结算（离家报告读），按展开先后。</summary>
        public static List<RaidResultRecord> InWindow(CampaignState s, long from, long to)
        {
            var list = new List<RaidResultRecord>();
            foreach (RaidResultRecord r in All(s))
            {
                if (r == null || r.UnfoldTick < 0)
                {
                    continue;
                }
                bool startsBeforeEnd = to < 0 || r.UnfoldTick <= to;
                bool endsAfterStart = r.EndTick < 0 || r.EndTick >= from;
                if (startsBeforeEnd && endsAfterStart)
                {
                    list.Add(r);
                }
            }
            return list;
        }

        public static int LossTotal(RaidResultRecord r) =>
            r == null ? 0 : r.LostBuildings + r.LostTurrets + r.LostDefenses + r.LostMachines + r.LostDrones;

        // ── 开 / 关 ──────────────────────────────────────────────────────────

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>攻城服务展开一支突袭队伍后调用：开一份结算（同一队伍已有进行中的直接返回）。</summary>
        public static RaidResultRecord Begin(CampaignState s, TransitGroupRecord g, CombatSite site)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || g == null || st.CoreLost)
            {
                return null;
            }
            RaidResultRecord open = OpenFor(s, g.GroupId);
            if (open != null)
            {
                return open;
            }
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, g.PlanId);
            long now = GameClock.Ticks;
            CombatCounters c = site != null && !site.IsDisposed ? site.KernelCounters : default;
            var r = new RaidResultRecord
            {
                Serial = st.NextSerial++,
                PlanId = g.PlanId ?? string.Empty,
                GroupId = g.GroupId ?? string.Empty,
                Wave = p?.Wave ?? 0,
                Faction = g.Faction ?? string.Empty,
                Level = p?.Level ?? 0,
                Trigger = p?.Trigger ?? string.Empty,
                TargetKind = g.TargetKind ?? string.Empty,
                WarnTick = p?.WarnTick ?? -1,
                ArrivedTick = g.ArrivedAtTick,
                UnfoldTick = now,
                Unfolded = g.UnfoldedCount,
                BaseDealt = c.DamageToHostile,
                BaseTaken = c.DamageToPlayer,
                ReactionSessionId = ReactionAttribution.Current(s, HomeValleyLayout.RegionId, create: false)?.SessionId ?? string.Empty,
                BaseOverloads = s.Raids?.Defense?.TotalOverloads ?? 0,
                BaseLays = s.Raids?.Defense?.TotalLays ?? 0,
                BaseRepaired = s.Raids?.Defense?.TotalRepaired ?? 0,
                BaseKits = s.Raids?.Defense?.TotalKitsUsed ?? 0,
            };
            if (r.WarnTick >= 0)
            {
                AddTimeline(r, r.WarnTick, "warn", string.Empty, false, 0f, 0f);
            }
            if (r.ArrivedTick >= 0)
            {
                AddTimeline(r, r.ArrivedTick, "arrive", g.UnitCount.ToString(CultureInfo.InvariantCulture), true, g.GatherX, g.GatherY);
            }
            AddTimeline(r, now, "unfold", g.UnfoldedCount.ToString(CultureInfo.InvariantCulture), true, g.GatherX, g.GatherY);
            var list = new List<RaidResultRecord>(st.Results) { r };
            st.Results = list.ToArray();
            Begins++;
            Revision++;
            return r;
        }

        /// <summary>攻城服务给这支队伍下了撤退令：记一条过程。</summary>
        public static void OnRetreatOrdered(CampaignState s, TransitGroupRecord g, string reason)
        {
            RaidResultRecord r = g != null ? OpenFor(s, g.GroupId) : null;
            if (r == null)
            {
                return;
            }
            r.RetreatReason = reason ?? string.Empty;
            AddTimeline(r, GameClock.Ticks, reason == SiegeService.ReasonLosses ? "retreat_losses" : "retreat_time", string.Empty, true, g.GatherX, g.GatherY);
            Revision++;
        }

        /// <summary>
        /// 攻城服务收拢 / 全歼一支队伍时调用（FGR-DEF-050“结束条件：敌人全灭或撤退”）：补上伤害差额、复制反应占比、并入远征中的选择，进历史，发“突袭结算”通知。
        /// 没有进行中的结算（本 Story 之前的旧档读进来时已经在攻城）时现开一份再结束——伤害读数从开的那一刻算（不追溯）。
        /// </summary>
        public static RaidResultRecord End(CampaignState s, TransitGroupRecord g)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || g == null || st.CoreLost)
            {
                return null;
            }
            CombatSite site = HomeSite;
            RaidResultRecord r = OpenFor(s, g.GroupId) ?? Begin(s, g, site);
            if (r == null)
            {
                return null;
            }
            r.Exited = g.ExitedCount;
            r.Unfolded = Math.Max(r.Unfolded, g.UnfoldedCount);
            bool withdrawn = g.ExitedCount > 0;
            AddTimeline(r, GameClock.Ticks, withdrawn ? "end_withdrawn" : "end_destroyed", g.ExitedCount.ToString(CultureInfo.InvariantCulture), true, g.GatherX, g.GatherY);
            Close(s, r, withdrawn ? OutcomeWithdrawn : OutcomeDestroyed, site, notify: true);
            return r;
        }

        private static void Close(CampaignState s, RaidResultRecord r, string outcome, CombatSite site, bool notify)
        {
            RaidResultState st = StateOf(s);
            r.EndTick = GameClock.Ticks;
            r.Outcome = outcome;
            if (site != null && !site.IsDisposed)
            {
                CombatCounters c = site.KernelCounters;
                r.Dealt = Math.Max(0, c.DamageToHostile - r.BaseDealt);
                r.Taken = Math.Max(0, c.DamageToPlayer - r.BaseTaken);
            }
            DefenseState ds = s.Raids?.Defense;
            if (ds != null)
            {
                r.Overloads = Math.Max(0, ds.TotalOverloads - r.BaseOverloads);
                r.Lays = Math.Max(0, ds.TotalLays - r.BaseLays);
                r.Repaired = Math.Max(0, ds.TotalRepaired - r.BaseRepaired);
                r.Kits = Math.Max(0, ds.TotalKitsUsed - r.BaseKits);
            }
            CopyReactions(s, r);
            MergeDecisions(s, r, persist: true);
            Prune(st);
            Ends++;
            Revision++;
            Hook(GuidanceHooks.RaidFirstResult);
            if (notify)
            {
                NotificationCenter.Post("raid_result", GameText.Format("raid.result.notify", WaveLabel(r), OutcomeText(r), r.Killed, LossTotal(r)));
            }
            TEngine.Log.Info($"[RaidResultService] 突袭结算 #{r.Serial}（{r.GroupId}，第 {r.Wave} 波）：{r.Outcome}，击毁 {r.Killed}（精英 {r.EliteKilled}），撤走 {r.Exited}，损失 {LossTotal(r)}，第 {GameClock.Ticks} 步");
        }

        /// <summary>结束的记录最多保留 raid.history_max 条（进行中的不删）。</summary>
        private static void Prune(RaidResultState st)
        {
            int max = RaidCatalog.HistoryMax;
            int ended = 0;
            foreach (RaidResultRecord r in st.Results)
            {
                if (r != null && r.EndTick >= 0)
                {
                    ended++;
                }
            }
            if (ended <= max)
            {
                return;
            }
            int drop = ended - max;
            var keep = new List<RaidResultRecord>(st.Results.Length);
            foreach (RaidResultRecord r in st.Results)
            {
                if (r != null && r.EndTick >= 0 && drop > 0)
                {
                    drop--;
                    continue;
                }
                if (r != null)
                {
                    keep.Add(r);
                }
            }
            st.Results = keep.ToArray();
        }

        private static void CopyReactions(CampaignState s, RaidResultRecord r)
        {
            ReactionSessionRecord session = null;
            foreach (ReactionSessionRecord x in ReactionAttribution.Sessions(s))
            {
                if (x != null && x.Kind == ReactionAttribution.KindRaid
                    && ((!string.IsNullOrEmpty(r.ReactionSessionId) && x.SessionId == r.ReactionSessionId) || (string.IsNullOrEmpty(r.ReactionSessionId) && x.EndTick < 0)))
                {
                    session = x;
                }
            }
            if (session == null)
            {
                return;
            }
            r.ReactionSessionId = session.SessionId ?? string.Empty;
            r.ReactionTotal = session.TotalDamage;
            var copy = new ReactionShareRecord[session.Reactions?.Length ?? 0];
            for (int i = 0; i < copy.Length; i++)
            {
                ReactionShareRecord src = session.Reactions[i];
                copy[i] = new ReactionShareRecord { ReactionId = src?.ReactionId ?? string.Empty, Count = src?.Count ?? 0, Damage = src?.Damage ?? 0 };
            }
            r.Reactions = copy;
        }

        /// <summary>远征中对这一波做的选择（突袭导演域 AwayDecisions）并进时间线；<paramref name="persist"/> = 写进记录（结算时），否则只返回给界面。</summary>
        public static List<RaidTimelineRecord> MergeDecisions(CampaignState s, RaidResultRecord r, bool persist)
        {
            var extra = new List<RaidTimelineRecord>();
            if (r == null || r.Wave <= 0)
            {
                return extra;
            }
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            foreach (RaidAwayDecisionRecord dec in d?.AwayDecisions ?? Array.Empty<RaidAwayDecisionRecord>())
            {
                if (dec == null || dec.Wave != r.Wave)
                {
                    continue;
                }
                string kind = dec.Choice == HomeRaidAlertService.ChoiceJumpHome ? "choice_jump" : "choice_stay";
                bool known = false;
                foreach (RaidTimelineRecord t in r.Timeline)
                {
                    if (t != null && t.Kind == kind && t.Tick == dec.Tick)
                    {
                        known = true;
                        break;
                    }
                }
                if (known)
                {
                    continue;
                }
                var rec = new RaidTimelineRecord { Tick = dec.Tick, Kind = kind, Arg = dec.SiteId ?? string.Empty };
                if (persist)
                {
                    InsertTimeline(r, rec);
                }
                else
                {
                    extra.Add(rec);
                }
            }
            return extra;
        }

        private static void AddTimeline(RaidResultRecord r, long tick, string kind, string arg, bool hasPos, float x, float y) =>
            InsertTimeline(r, new RaidTimelineRecord { Tick = tick, Kind = kind, Arg = arg ?? string.Empty, HasPos = hasPos, X = x, Y = y });

        /// <summary>按步序号插入（同一步按插入顺序）；超过上限时计数不丢结尾（结尾 = 结束那一条，最重要）。</summary>
        private static void InsertTimeline(RaidResultRecord r, RaidTimelineRecord rec)
        {
            r.Timeline ??= Array.Empty<RaidTimelineRecord>();
            if (r.Timeline.Length >= TimelineMax)
            {
                bool isEnd = rec.Kind.StartsWith("end_", StringComparison.Ordinal) || rec.Kind == "core_lost";
                if (!isEnd)
                {
                    r.TimelineDropped++;
                    return;
                }
                // 结尾一定要记：挤掉倒数第二条之前最近的一条非开头记录。
                var trimmed = new List<RaidTimelineRecord>(r.Timeline);
                trimmed.RemoveAt(trimmed.Count - 1);
                r.Timeline = trimmed.ToArray();
                r.TimelineDropped++;
            }
            var list = new List<RaidTimelineRecord>(r.Timeline.Length + 1);
            list.AddRange(r.Timeline);
            int at = list.Count;
            while (at > 0 && list[at - 1] != null && list[at - 1].Tick > rec.Tick)
            {
                at--;
            }
            list.Insert(at, rec);
            r.Timeline = list.ToArray();
        }

        // ── 击毁（内核事件）───────────────────────────────────────────────────

        private static readonly List<int> GroupKeyScratch = new List<int>(4);

        /// <summary>
        /// 内核事件 <see cref="CombatEventKind.SiegeKilled"/>（家园战斗内核，CombatSite 绑定）：记“击败过的敌人种类”（靶场解锁，FG-GAP-101，攻城与拦截都算）；
        /// 攻城中的队伍另记结算——击毁数、按种类、贡献、残骸、掉落。O(队伍数 + 贡献者 + 残骸堆)。
        /// </summary>
        public static void OnKilled(CombatSite site, CombatEvent e)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CampaignState s = CampaignSession.Current;
            if (s == null || site == null || !ReferenceEquals(site, HomeSite))
            {
                return;
            }
            RaidResultState st = EnsureState(s);
            if (st == null || st.CoreLost)
            {
                return;
            }
            int group = (int)Math.Round(e.Value);
            int kind = CombatSite.SiegeKindOf((int)Math.Round(e.Value2), group);
            RaidUnitDef def = RaidCatalog.UnitOfKind(kind);
            if (def != null)
            {
                TestRangeService.NoteEnemyDefeated(s, def.EnemyTypeId);
            }
            TransitGroupRecord g = null;
            foreach (TransitGroupRecord x in WorldTransitSystem.Groups(s))
            {
                if (x != null && SiegeService.KeyOf(x) == group)
                {
                    g = x;
                    break;
                }
            }
            if (g == null || !SiegeService.IsSieging(g))
            {
                Stat(t0);
                return; // 行进途中的拦截战：不进家园结算、不留残骸（ADR-DEF-008 §残骸）
            }
            RaidResultRecord r = OpenFor(s, g.GroupId) ?? Begin(s, g, site);
            if (r == null)
            {
                Stat(t0);
                return;
            }
            bool elite = e.Code2 == 1;
            r.Killed++;
            if (elite)
            {
                r.EliteKilled++;
            }
            AddAmount(ref r.KilledKinds, def?.Id ?? "unknown", 1);
            Credit(s, site, r, e.Other, elite);
            var at = new Vector2((float)e.Pos.x, (float)e.Pos.y);
            AddWreck(s, st, r, at, elite ? WreckPerElite : WreckPerUnit);
            RollDrops(s, st, r, at, elite);
            Revision++;
            Stat(t0);
        }

        private static void Stat(long t0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            KillEvents++;
            KillMs += ms;
            MaxKillMs = Math.Max(MaxKillMs, ms);
        }

        /// <summary>击杀者 → 贡献（炮塔 / 机器 / 防御建筑；认不出来的记“其它”）。</summary>
        private static void Credit(CampaignState s, CombatSite site, RaidResultRecord r, int killerUnit, bool elite)
        {
            string kind = null, id = null;
            if (killerUnit > 0)
            {
                int turret = site.TurretSerialOf(killerUnit);
                if (turret > 0)
                {
                    TurretRecord tr = TurretService.FindBySerial(s, turret);
                    if (tr != null)
                    {
                        kind = KindTurret;
                        id = tr.BuildingId;
                    }
                }
                else if (site.TryGetMachineOfUnit(killerUnit, out int logic))
                {
                    kind = KindMachine;
                    id = logic.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    int defense = site.DefenseSerialOf(killerUnit);
                    DefenseRecord dr = defense > 0 ? DefenseService.FindBySerial(s, defense) : null;
                    if (dr != null)
                    {
                        kind = KindDefense;
                        id = dr.BuildingId;
                    }
                }
            }
            if (kind == null)
            {
                r.OtherKills++;
                return;
            }
            RaidContribRecord c = null;
            foreach (RaidContribRecord x in r.Contrib)
            {
                if (x != null && x.Kind == kind && x.Id == id)
                {
                    c = x;
                    break;
                }
            }
            if (c == null)
            {
                if (r.Contrib.Length >= ContribMax)
                {
                    r.OtherKills++;
                    return;
                }
                c = new RaidContribRecord { Kind = kind, Id = id, Name = NameOf(s, kind, id) };
                var list = new List<RaidContribRecord>(r.Contrib) { c };
                r.Contrib = list.ToArray();
            }
            c.Kills++;
            if (elite)
            {
                c.Elites++;
            }
        }

        /// <summary>贡献排行（击毁多的在前；同击毁按精英、再按种类与 ID，显示顺序确定）。</summary>
        public static List<RaidContribRecord> Ranked(RaidResultRecord r)
        {
            var list = new List<RaidContribRecord>();
            if (r?.Contrib == null)
            {
                return list;
            }
            foreach (RaidContribRecord c in r.Contrib)
            {
                if (c != null && c.Kills > 0)
                {
                    list.Add(c);
                }
            }
            list.Sort((a, b) => a.Kills != b.Kills ? b.Kills.CompareTo(a.Kills)
                : a.Elites != b.Elites ? b.Elites.CompareTo(a.Elites)
                : string.CompareOrdinal(a.Kind, b.Kind) != 0 ? string.CompareOrdinal(a.Kind, b.Kind) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        // ── 残骸与掉落（FGR-DEF-051）──────────────────────────────────────────

        private static void AddWreck(CampaignState s, RaidResultState st, RaidResultRecord r, Vector2 at, int amount)
        {
            if (amount <= 0)
            {
                return;
            }
            string res = ItemCatalog.ResourceTypeOf(AnalysisCatalog.WreckId);
            if (string.IsNullOrEmpty(res))
            {
                LastProblem = "物品表里没有敌方残骸（enemy_wreck），残骸没有落地";
                return;
            }
            float m2 = WreckMergeCells * WreckMergeCells;
            int pileMax = WreckPileMax;
            GroundItemRecord pile = null;
            foreach (RaidWreckPileRecord p in r.Piles)
            {
                if (p == null || (p.X - at.x) * (p.X - at.x) + (p.Y - at.y) * (p.Y - at.y) > m2)
                {
                    continue;
                }
                GroundItemRecord gi = HomeValleyCargo.FindGroundItem(s, p.GroundItemId);
                if (gi != null && gi.ResourceType == res && gi.Amount + amount <= pileMax)
                {
                    pile = gi;
                    break;
                }
            }
            if (pile != null)
            {
                pile.Amount += amount;
            }
            else
            {
                string sid = "raidwreck:" + (st.NextWreckSerial++).ToString(CultureInfo.InvariantCulture);
                pile = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, res, amount, sid);
                if (r.Piles.Length < PilesMax)
                {
                    var list = new List<RaidWreckPileRecord>(r.Piles) { new RaidWreckPileRecord { GroundItemId = pile.GroundItemId, X = at.x, Y = at.y } };
                    r.Piles = list.ToArray();
                }
                Hook(GuidanceHooks.RaidFirstWreck);
            }
            if (st.WreckRouting != RouteOff)
            {
                HomeValleyWorkOrders.TryCreateHaulPool(s, pile.GroundItemId); // 幂等：同一堆只有一张
            }
            st.TotalWrecks += amount;
            AddAmount(ref r.Loot, AnalysisCatalog.WreckId, amount);
        }

        /// <summary>确定性掉落：按（世界种子, 结算序号, 第几次击毁）派生，存读档 / 观察与否一致。掉落走解析台的唯一发放入口（先进仓库，放不下的落在原地由机器搬回）。</summary>
        private static void RollDrops(CampaignState s, RaidResultState st, RaidResultRecord r, Vector2 at, bool elite)
        {
            float mult = elite ? DropEliteMult : 1f;
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            double a = WorldGenMath.Hash(seed, r.Serial, r.Killed, 0x52524D31u) / 4294967296.0;
            double b = WorldGenMath.Hash(seed, r.Serial, r.Killed, 0x52524632u) / 4294967296.0;
            if (a < DropModuleChance * mult)
            {
                Drop(s, st, r, AnalysisCatalog.UnparsedModuleId, at);
            }
            if (b < DropFirmwareChance * mult)
            {
                Drop(s, st, r, AnalysisCatalog.EncryptedFirmwareId, at);
            }
        }

        private static void Drop(CampaignState s, RaidResultState st, RaidResultRecord r, string itemId, Vector2 at)
        {
            // 身份（是哪个模块 / 哪种固件）由掉落表决定，掉落表在 FG8-LOOT-01（DEBT-FG5RND02-01）；这里按“身份不明”发放，解析时按重复解析给技术数据。
            if (HomeValleyAnalysis.Acquire(s, itemId, string.Empty, "raid", 1, at) > 0)
            {
                st.TotalDrops++;
                AddAmount(ref r.Loot, itemId, 1);
            }
        }

        // ── 损失 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 各摧毁入口调用（建筑结构单位阵亡、炮塔座被毁、防御建筑被毁、机器阵亡、维修无人机被击落）：有进行中的结算时记到最早展开的那一份。
        /// 明细至多 raid.result.losses_max 条（合计照常累加）；第一处损失另记一条过程。
        /// </summary>
        public static void NoteLoss(CampaignState s, string kind, string id, Vector2 at)
        {
            RaidResultState st = StateOf(s);
            if (st == null || st.CoreLost)
            {
                return;
            }
            RaidResultRecord r = PrimaryOpen(s);
            if (r == null)
            {
                return;
            }
            switch (kind)
            {
                case KindBuilding: r.LostBuildings++; break;
                case KindTurret: r.LostTurrets++; break;
                case KindDefense: r.LostDefenses++; break;
                case KindMachine: r.LostMachines++; break;
                case KindDrone: r.LostDrones++; break;
            }
            string name = NameOf(s, kind, id);
            if (LossTotal(r) == 1)
            {
                AddTimeline(r, GameClock.Ticks, "first_loss", KindText(kind) + GameText.Get("rules.sep_dot") + name, true, at.x, at.y);
            }
            if (r.Losses.Length >= LossesMax)
            {
                r.LossesDropped++;
            }
            else
            {
                var list = new List<RaidLossRecord>(r.Losses)
                {
                    new RaidLossRecord { Tick = GameClock.Ticks, Kind = kind ?? string.Empty, Id = id ?? string.Empty, Name = name, X = at.x, Y = at.y },
                };
                r.Losses = list.ToArray();
            }
            Revision++;
        }

        /// <summary>机器阵亡（<see cref="MachineRegistry.MarkDeadByLogicId"/> 唯一翻转点）：家园里的机器在攻城期间阵亡记一处损失。</summary>
        public static void OnMachineDied(CampaignState s, MachineRecord m, Vector2 at)
        {
            if (m == null || m.RegionId != HomeValleyLayout.RegionId)
            {
                return;
            }
            NoteLoss(s, KindMachine, m.LogicId.ToString(CultureInfo.InvariantCulture), at);
        }

        // ── 归还核心被摧毁（FGR-DEF-053）──────────────────────────────────────

        /// <summary>
        /// 攻城对账（<see cref="SiegeService.Sync"/>，每 siege.sync_seconds）：归还核心在内核里的耐久到下限（内核让它停在下限、不阵亡）= 被摧毁。
        /// 只看步序号与内核状态，观察与否一致。返回这一次是否判定被摧毁。
        /// </summary>
        public static bool CheckCore(CampaignState s, CombatSite site)
        {
            if (s == null || HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                return false;
            }
            BuildingRecord core = CoreAtFloorIn(s, site);
            return core != null && OnCoreDestroyed(s, core);
        }

        /// <summary>归还核心此刻的内核耐久已到下限（攻城中；还没被对账判定也算）。存档入口据此拒绝写一份必败档（<see cref="CampaignAutoSaveService"/>）。</summary>
        public static bool CoreAtFloor(CampaignState s) => CoreAtFloorIn(s, HomeSite) != null;

        private static BuildingRecord CoreAtFloorIn(CampaignState s, CombatSite site)
        {
            if (s == null || site == null || site.IsDisposed || CoreLossDisabledForTests)
            {
                return null;
            }
            SiegeState siege = SiegeService.StateOf(s);
            if (siege == null || !siege.TheaterActive)
            {
                return null;
            }
            foreach (SiegeStructureRecord rec in siege.Structures)
            {
                if (rec == null)
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(s, rec.BuildingId);
                if (b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore)
                {
                    continue;
                }
                return site.TryGetSiegeStructHealth(rec.Serial, out float hp, out _, out bool alive) && alive && hp <= CombatSiegeConst.HealthFloor + 1e-5f ? b : null;
            }
            return null;
        }

        /// <summary>核心被摧毁：记为摧毁（家园冻结、失败页出现），进行中的结算按 core_lost 结束，结束反应归因的突袭场次。此后不再写存档。</summary>
        public static bool OnCoreDestroyed(CampaignState s, BuildingRecord core)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || !HomeValleySoftlockGuard.DestroyCore(s))
            {
                return false;
            }
            st.CoreLost = true;
            st.CoreLostTick = GameClock.Ticks;
            CombatSite site = HomeSite;
            var open = new List<RaidResultRecord>();
            foreach (RaidResultRecord r in st.Results)
            {
                if (r != null && r.EndTick < 0)
                {
                    open.Add(r);
                }
            }
            st.CoreLostResult = open.Count > 0 ? open[0].Serial : 0;
            foreach (RaidResultRecord r in open)
            {
                AddTimeline(r, GameClock.Ticks, "core_lost", string.Empty, core != null, core?.Position.x ?? 0f, core?.Position.y ?? 0f);
                Close(s, r, OutcomeCoreLost, site, notify: false);
            }
            ReactionAttribution.EndRaid(s);
            PendingAutosaveWave = 0; // 失败之后不再存
            Hook(GuidanceHooks.RaidFirstCoreLost);
            Revision++;
            TEngine.Log.Info($"[RaidResultService] 归还核心被突袭摧毁（第 {GameClock.Ticks} 步），战役失败；之后不再写存档");
            return true;
        }

        /// <summary>失败页读：摧毁核心的那一次突袭（没有 = null）。</summary>
        public static RaidResultRecord CoreLostBy(CampaignState s)
        {
            RaidResultState st = StateOf(s);
            return st != null && st.CoreLost ? Find(s, st.CoreLostResult) : null;
        }

        // ── 预警自动存档 ───────────────────────────────────────────────────────

        /// <summary>突袭导演发出预警（<see cref="RaidDirectorService"/> Warn）：目标是家园、这一波还没存过 → 请求帧末自动存档一次（存档不能在模拟步中间写）。</summary>
        public static void OnRaidWarned(CampaignState s, RaidPlanRecord p)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || p == null || !AutosaveOnWarning || st.CoreLost || HomeValleySoftlockGuard.IsCoreDestroyed(s)
                || p.TargetKind != RaidDirectorService.TargetHome || Array.IndexOf(st.AutosavedWaves, p.Wave) >= 0)
            {
                return;
            }
            PendingAutosaveWave = Math.Max(1, p.Wave);
        }

        /// <summary>帧末（<c>GameRoot</c> 每帧在世界推进之后调用）：执行等待中的预警自动存档（攻城 / 靶场测试进行中时推迟，见 <see cref="AutosaveDeferReason"/>）。</summary>
        public static void FrameUpdate()
        {
            if (PendingAutosaveWave == 0 || !CampaignSession.HasActiveCampaign)
            {
                return;
            }
            FlushAutosave(CampaignSession.Current);
        }

        /// <summary>预警自动存档因攻城 / 靶场测试进行中被推迟的帧数（本进程；自检读）。</summary>
        public static int AutosaveDeferrals { get; private set; }

        /// <summary>
        /// FG6-DEF-08 复修（倍速矩阵 / FG5-RND-03 回归）：预警自动存档由模拟内部事件触发、在帧末执行，存在哪一步随倍速 / 帧率而变——所以只在“存档对模拟没有可见副作用”的时候写：
        /// 家园没有攻城（攻城中的对账 / 维修推送最密集，也是玩家最不该被打断的时候）、靶场没有进行中的测试（存档会以“已存档”结束测试，FG5-RND-03 只允许玩家自己存档时这样做）。
        /// 返回推迟原因（null = 现在可以写）。推迟期间请求保留，条件解除后的第一个帧末写。
        /// </summary>
        public static string AutosaveDeferReason(CampaignState s)
        {
            if (SiegeService.StateOf(s)?.TheaterActive ?? false)
            {
                return "siege";
            }
            if (TestRangeService.ActiveSessions > 0)
            {
                return "test_range";
            }
            return null;
        }

        /// <summary>执行等待中的预警自动存档（自检直接调）。核心已被摧毁时拒绝（不覆盖安全档）；攻城 / 靶场测试进行中时推迟（请求保留）。返回是否写盘成功。</summary>
        public static bool FlushAutosave(CampaignState s)
        {
            int wave = PendingAutosaveWave;
            RaidResultState st = EnsureState(s);
            if (wave == 0 || st == null)
            {
                PendingAutosaveWave = 0;
                return false;
            }
            if (st.CoreLost || HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                PendingAutosaveWave = 0;
                AutosaveRefused++;
                return false;
            }
            if (AutosaveDeferReason(s) != null)
            {
                AutosaveDeferrals++;
                return false;
            }
            PendingAutosaveWave = 0;
            var waves = new List<int>(st.AutosavedWaves) { wave };
            if (waves.Count > 8)
            {
                waves.RemoveRange(0, waves.Count - 8);
            }
            st.AutosavedWaves = waves.ToArray();
            // 通用的“自动存档”轻提示不发（toast: false），只发这一条更具体的“突袭预警：已自动存档”，不重复提示。
            SaveResult res = CampaignAutoSaveService.SaveAuto(SaveReason.RaidWarning, toast: false);
            if (!res.Success)
            {
                LastProblem = "突袭预警自动存档失败：" + res.Message;
                return false;
            }
            AutosaveCount++;
            Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.SaveComplete, GameText.Get("raid.result.autosaved"));
            return true;
        }

        // ── 残骸去向（每 raid.result.route_seconds）────────────────────────────

        public static bool SetWreckRouting(CampaignState s, int mode)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || mode < 0 || mode >= RouteCount)
            {
                return false;
            }
            int before = st.WreckRouting;
            st.WreckRouting = mode;
            st.NextRouteTick = -1; // 下一步就按新的去向检查
            if (before == RouteOff && mode != RouteOff)
            {
                // 从“不自动搬运”改回来：给还在战场上的残骸补上搬运单（只扫记录里的残骸堆，O(结算数 × 堆数)，只在改设置时）。
                foreach (RaidResultRecord r in st.Results)
                {
                    foreach (RaidWreckPileRecord p in r?.Piles ?? Array.Empty<RaidWreckPileRecord>())
                    {
                        if (p != null && HomeValleyCargo.FindGroundItem(s, p.GroundItemId) != null)
                        {
                            HomeValleyWorkOrders.TryCreateHaulPool(s, p.GroundItemId);
                        }
                    }
                }
            }
            Revision++;
            return true;
        }

        public static string RouteText(int mode) => GameText.Get("raid.result.panel.wreck." + (mode switch
        {
            RouteRecycler => "recycler",
            RouteStore => "store",
            RouteOff => "off",
            _ => "bench",
        }));

        private static readonly List<(string Target, int Room)> RouteScratch = new List<(string, int)>(8);

        /// <summary>
        /// 世界模拟每步（家园载入时，攻城服务之后）：每 raid.result.route_seconds 游戏秒按“残骸去向”把仓库里的残骸开成送货单（机器送进解析台残骸缓存 / 回收站输入）。
        /// 送货单开单即从仓库预留（与阈值补给同一做法），已经在路上的不重复开。只看步序号，与观察无关；O(工单数 + 回收站数)，只在检查的那一步。
        /// </summary>
        public static void WorldStep(CampaignState s, long ticksBefore, int worldHz)
        {
            RaidResultState st = EnsureState(s);
            if (st == null || worldHz <= 0 || st.CoreLost)
            {
                return;
            }
            if (st.NextRouteTick < 0)
            {
                st.NextRouteTick = ticksBefore;
            }
            if (ticksBefore < st.NextRouteTick)
            {
                return;
            }
            st.NextRouteTick = ticksBefore + Math.Max(1, (long)Math.Round(RouteSeconds * worldHz));
            ReconcileOpen(s, st);
            RouteWrecks(s, st);
        }

        /// <summary>
        /// 兜底（每 raid.result.route_seconds）：进行中的结算对应的队伍已经不在攻城（被别的流程移除 / 旧档），按现有数据结束（击毁数够全部展开的 = 全部击毁，否则残部撤离），
        /// 不留一份永远“进行中”的结算（损失会一直记到它身上）。正式路径（攻城服务的收拢 / 全歼）在同一步里先结束，不会走到这里。
        /// </summary>
        private static void ReconcileOpen(CampaignState s, RaidResultState st)
        {
            List<RaidResultRecord> stale = null;
            foreach (RaidResultRecord r in st.Results)
            {
                if (r == null || r.EndTick >= 0)
                {
                    continue;
                }
                TransitGroupRecord g = WorldTransitSystem.Find(s, r.GroupId);
                if (g == null || !SiegeService.IsSieging(g))
                {
                    (stale ??= new List<RaidResultRecord>()).Add(r);
                }
            }
            if (stale == null)
            {
                return;
            }
            CombatSite site = HomeSite;
            foreach (RaidResultRecord r in stale)
            {
                bool destroyed = r.Killed >= r.Unfolded && r.Unfolded > 0;
                AddTimeline(r, GameClock.Ticks, destroyed ? "end_destroyed" : "end_withdrawn",
                    Math.Max(0, r.Unfolded - r.Killed).ToString(CultureInfo.InvariantCulture), false, 0f, 0f);
                if (!destroyed)
                {
                    r.Exited = Math.Max(r.Exited, r.Unfolded - r.Killed);
                }
                Close(s, r, destroyed ? OutcomeDestroyed : OutcomeWithdrawn, site, notify: true);
            }
        }

        /// <summary>按去向开送货单（自检可以直接调）。返回开出的单数。</summary>
        public static int RouteWrecks(CampaignState s, RaidResultState st)
        {
            if (st.WreckRouting == RouteStore || st.WreckRouting == RouteOff)
            {
                return 0;
            }
            int stock = HomeInventory.Stock(s, AnalysisCatalog.WreckId);
            if (stock <= 0)
            {
                return 0;
            }
            RouteScratch.Clear();
            var pending = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (WorkOrderRecord o in s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
            {
                if (o != null && o.Kind == WorkOrderKind.Deliver && o.IssuerId == DeliverIssuer && HomeValleyWorkOrders.IsActive(o) && o.ReservedItemAmount > 0)
                {
                    pending.TryGetValue(o.TargetId ?? string.Empty, out int n);
                    pending[o.TargetId ?? string.Empty] = n + o.ReservedItemAmount;
                }
            }
            BuildingRecord bench = HomeValleyAnalysis.FindBench(s);
            int benchRoom = bench != null ? HomeValleyAnalysis.WreckRoom(s) : 0;
            if (bench != null)
            {
                pending.TryGetValue(bench.BuildingId, out int onWay);
                benchRoom -= onWay;
            }
            var recyclers = new List<(string, int)>();
            ProductionService.TryGet(s, null, out _); // 确保索引是这个存档的
            ItemCatalog.TryGet(AnalysisCatalog.WreckId, out ItemDef wreckDef);
            foreach (ProductionService.Producer p in ProductionService.All)
            {
                if (p?.Def == null || p.Def.Mode != ProducerMode.Recycler || !ProductionService.IsActive(p.Building))
                {
                    continue;
                }
                // 复修 P2：回收站的输入是各种固体共用一个容量（传送带进料也按 InCapacity − Total 算），按输入总数算剩余空间，不按同种物品数。
                int room = ProductionService.InCapacity(p, wreckDef) - ProductionService.Total(p.Rec.In);
                pending.TryGetValue(p.Building.BuildingId, out int onWay);
                room -= onWay;
                if (room > 0)
                {
                    recyclers.Add((p.Building.BuildingId, room));
                }
            }
            if (st.WreckRouting == RouteBench)
            {
                if (benchRoom > 0)
                {
                    RouteScratch.Add((bench.BuildingId, benchRoom));
                }
                RouteScratch.AddRange(recyclers);
            }
            else
            {
                RouteScratch.AddRange(recyclers);
                if (benchRoom > 0)
                {
                    RouteScratch.Add((bench.BuildingId, benchRoom));
                }
            }
            int made = 0;
            int batch = RouteBatch;
            foreach ((string target, int room) in RouteScratch)
            {
                int left = room;
                while (left > 0 && stock > 0)
                {
                    int n = Math.Min(batch, Math.Min(left, stock));
                    HomeValleyWorkOrders.WorkOrderOpResult res = HomeValleyWorkOrders.TryCreateDeliverPool(s, "raidwreck:" + target, target, AnalysisCatalog.WreckId, n,
                        StandingRuleService.UnloadSeconds, 0);
                    if (!res.Success)
                    {
                        LastProblem = "残骸送货单开不出来：" + res.FailureReason;
                        return made;
                    }
                    WorkOrderRecord o = HomeValleyWorkOrders.Find(s, res.WorkOrderId);
                    if (o != null)
                    {
                        o.IssuerId = DeliverIssuer;
                    }
                    if (bench != null && target == bench.BuildingId)
                    {
                        st.WrecksToBench += n;
                    }
                    else
                    {
                        st.WrecksToRecycler += n;
                    }
                    stock -= n;
                    left -= n;
                    made++;
                    DeliverOrders++;
                }
                if (stock <= 0)
                {
                    break;
                }
            }
            if (made > 0)
            {
                Revision++;
            }
            return made;
        }

        /// <summary>战场上还没搬走的残骸份数（只数记录里的残骸堆；面板 / 自检读）。</summary>
        public static int WrecksOnField(CampaignState s)
        {
            int n = 0;
            string res = ItemCatalog.ResourceTypeOf(AnalysisCatalog.WreckId);
            foreach (GroundItemRecord g in s?.GroundItems ?? Array.Empty<GroundItemRecord>())
            {
                if (g != null && g.ResourceType == res && g.RegionId == HomeValleyLayout.RegionId)
                {
                    n += g.Amount;
                }
            }
            return n;
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        private static void AddAmount(ref ItemAmountRecord[] arr, string id, long amount)
        {
            arr ??= Array.Empty<ItemAmountRecord>();
            foreach (ItemAmountRecord a in arr)
            {
                if (a != null && a.ItemId == id)
                {
                    a.Amount += amount;
                    return;
                }
            }
            var next = new ItemAmountRecord[arr.Length + 1];
            Array.Copy(arr, next, arr.Length);
            next[arr.Length] = new ItemAmountRecord { ItemId = id, Amount = amount };
            // 按 ID 排序存（存档确定性，与先后无关）。
            Array.Sort(next, (x, y) => string.CompareOrdinal(x.ItemId, y.ItemId));
            arr = next;
        }

        public static string KindText(string kind) => GameText.Get("raid.result.kind." + (kind switch
        {
            KindBuilding => "building",
            KindTurret => "turret",
            KindDefense => "defense",
            KindMachine => "machine",
            KindDrone => "drone",
            _ => "other",
        }));

        /// <summary>损失 / 贡献的显示名（记录那一刻取一次存下，之后改名 / 被拆也看得懂）。</summary>
        public static string NameOf(CampaignState s, string kind, string id)
        {
            if (kind == KindMachine && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int logic))
            {
                string m = Feedback.FeedbackCues.MachineLabel(logic);
                return string.IsNullOrEmpty(m) ? GameText.Get("raid.result.name_unknown") : m;
            }
            string b = Feedback.FeedbackCues.BuildingLabel(id);
            return string.IsNullOrEmpty(b) ? GameText.Get("raid.result.name_unknown") : b;
        }

        public static string WaveLabel(RaidResultRecord r) =>
            r != null && r.Wave > 0 ? GameText.Format("raid.result.wave", r.Wave) : GameText.Get("raid.result.wave_unplanned");

        public static string OutcomeText(RaidResultRecord r)
        {
            switch (r?.Outcome)
            {
                case OutcomeDestroyed:
                    return GameText.Get("raid.result.outcome.destroyed");
                case OutcomeWithdrawn:
                    return GameText.Format("raid.result.outcome.withdrawn", GameText.Get(r.RetreatReason == SiegeService.ReasonLosses ? "raid.result.reason.losses"
                        : r.RetreatReason == SiegeService.ReasonTime ? "raid.result.reason.time" : "raid.result.reason.other"));
                case OutcomeCoreLost:
                    return GameText.Get("raid.result.outcome.core_lost");
                default:
                    return GameText.Get("raid.result.outcome.open");
            }
        }

        /// <summary>自检 / 确定性对照：结算域的规范化快照（不含显示名之外的运行时缓存）。</summary>
        public static string Snapshot(CampaignState s)
        {
            RaidResultState st = StateOf(s);
            if (st == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(512);
            sb.Append(st.NextSerial).Append('|').Append(st.WreckRouting).Append('|').Append(st.NextRouteTick).Append('|').Append(st.NextWreckSerial)
              .Append('|').Append(st.CoreLost ? 'L' : '-').Append(st.CoreLostTick).Append('|').Append(st.TotalWrecks).Append('|').Append(st.TotalDrops)
              .Append('|').Append(st.WrecksToBench).Append('|').Append(st.WrecksToRecycler);
            foreach (RaidResultRecord r in st.Results)
            {
                if (r == null)
                {
                    continue;
                }
                sb.Append("|R").Append(r.Serial).Append(':').Append(r.GroupId).Append(':').Append(r.Wave).Append(':').Append(r.Outcome).Append(':').Append(r.UnfoldTick)
                  .Append('-').Append(r.EndTick).Append(':').Append(r.Killed).Append('/').Append(r.EliteKilled).Append('/').Append(r.Exited).Append('/').Append(r.Unfolded)
                  .Append(':').Append(r.Dealt).Append('/').Append(r.Taken).Append(':').Append(r.OtherKills).Append(':').Append(LossTotal(r));
                foreach (RaidContribRecord c in r.Contrib)
                {
                    sb.Append(",C").Append(c.Kind).Append(c.Id).Append('=').Append(c.Kills).Append('/').Append(c.Elites);
                }
                foreach (ItemAmountRecord a in r.Loot)
                {
                    sb.Append(",L").Append(a.ItemId).Append('=').Append(a.Amount);
                }
                foreach (RaidTimelineRecord t in r.Timeline)
                {
                    sb.Append(",T").Append(t.Kind).Append('@').Append(t.Tick);
                }
                foreach (RaidLossRecord l in r.Losses)
                {
                    sb.Append(",X").Append(l.Kind).Append(l.Id).Append('@').Append(l.Tick);
                }
            }
            return sb.ToString();
        }
    }
}
