using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG8-EXP-03（FG08 FGR-EXP-021、022、030；FG17 FGR-GEN-060；验收 FGT-EXP-005）：领地控制、增援与收复。<b>唯一写入口</b>（<see cref="RaidState.Territory"/>，
    /// 以及据点 / 巡逻记录上的迎战字段）。
    ///
    /// - <b>控制度</b>：每个阵营领地 0～100 = 仍然活跃的领地主力据点（<see cref="WorldOutpostSystem.KindTerritory"/>，FG8-GEN-02 按模板登记）等级之和 ÷ 全部领地主力据点等级之和（向下取整）。
    ///   只在据点集合变化（<see cref="WorldOutpostSystem.Revision"/>）时重算，平时 O(1)。
    /// - <b>敌控</b>：首领被击败之前，阵营每 territory.reinforce.interval_days 个游戏日（初值 3）增援一次——每个被摧毁的领地据点按（世界种子, 领地, 第几次, 据点）的确定性概率重建
    ///   （据点附近有己方实体时不重建，不在玩家眼前凭空出现）；据点自己的驻军增长沿用 FG0-ARCH-06 的增援点。
    /// - <b>平定中</b>：首领被击败（fg.TbTerritoryControl.leaderRule，与突袭导演 / 情报同一判定口径；还没上线的首领由首领 Story 调 <see cref="MarkLeaderDefeated"/>）：
    ///   不再增援（据点驻军不再增长、不再重建），控制度随剩余据点被摧毁而下降。
    /// - <b>已收复</b>：平定中、控制度降到 territory.control.reclaim_below（初值 20）以下：这片领地的敌方巡逻撤离，在这里建设不再被骚扰；危害区与污染照旧，
    ///   净化塔净化后计入归还面积（<see cref="Env.PurificationService.ReturnedArea"/> 与位置无关地统计）；精英残部每 territory.elite.interval_days 个游戏日（初值 7）刷新一批（上一批没被击溃时不叠加），首领不再刷新。
    ///   收复时发通知与一条核心备忘（GameEvent <see cref="Env.PurificationService.CoreMemoEvent"/>，FG11 叙事接口）。
    /// - <b>迎战</b>（承接 DEBT-FG3GEN01-05“巡逻交战、摧毁掉落”）：己方机器走到醒着的据点 / 巡逻 territory.engage.radius_cells 以内时，驻军 / 巡逻就地展开成家园战斗内核里的敌人
    ///   （与突袭拦截同一套生成与“就地交战”模式），全灭 = 据点被摧毁（废料掉在原地、开搬运单）/ 巡逻被歼灭；附近没有己方机器满 territory.engage.regroup_seconds 后幸存者收拢。
    /// - <b>骚扰</b>（卡片负向“在敌控领地里建设：允许，但会持续被攻击”）：敌控 / 平定中的领地里有己方建筑群时，每 territory.harass.interval_hours 个游戏小时从离它最近的领地据点派一支骚扰部队
    ///   （到达后与“前哨站目标”的突袭同一套展开与攻城）；已收复的领地不再派。
    ///
    /// 时间一律是统一时钟的整数步、检查周期按步序号（暂停不走，0.5x～3x 只是一帧多走几步）；规则只读存档里的据点与己方实体位置，与镜头 / 观察无关（FGR-BASE-021）。
    /// 开销：每 territory.control.sync_seconds 一次 O(领地数)（控制度重算只在据点变化时 O(据点)）；迎战检查每 siege.sync_seconds 一次 O(醒着的据点 + 巡逻)，
    /// 内核查询是 AOT 里的一次调用；逐单位的移动、交战在家园战斗内核（Main/Sim）。
    /// </summary>
    public static class TerritoryControlService
    {
        public const int StatusEnemy = 0;
        public const int StatusPacifying = 1;
        public const int StatusReclaimed = 2;

        /// <summary>
        /// 迎战队伍键区间 [<see cref="KeyBase"/>, <see cref="KeyBase"/> + <see cref="KeyCount"/>)（复审 P1：原来的 2000 万超出击毁事件 float 的精确范围，种类解不出、靶场不解锁）。
        /// 击毁事件经 float 传队伍键与外部键的相反数 −(300 万 + 队伍键 × 64 + 种类)，小于 2^24 才精确 → 队伍键 ≤ 215268（<see cref="CombatSite"/> 攻城单位契约）；
        /// 突袭队伍键 = 寻路序号（NextNavSerial，正常一局远小于 20 万）。分配时跳过正在用的键（迎战中的据点 / 巡逻、在途 / 攻城的队伍），区间用完后循环复用。
        /// </summary>
        public const int KeyBase = 200000;
        public const int KeyCount = 15000;

        /// <summary>这个键落在迎战区间里。</summary>
        public static bool IsEngageKey(int key) => key >= KeyBase && key < KeyBase + KeyCount;

        public const string NotifyStatus = "territory_status";
        public const string NotifyAlert = "territory_alert";
        public const string LootPrefix = "outpost-loot:";
        public const string MemoPrefix = "territory_reclaimed.";
        /// <summary>FG9-NAR-01：这片领地的首领被击败（参数：领地 ID）。叙事框架据此投放剧情节点（FGR-NAR-010“击败寂听主脑 / 铸造主核心”）。</summary>
        public const string LeaderDefeatedEvent = "World.LeaderDefeated";
        public const string RuleNone = "none";
        /// <summary>领地状态变化时发（参数 = 领地 ID）。地图 / 面板据此刷新。</summary>
        public const string ChangedEvent = "TerritoryControl.Changed";

        // ── 自检读点 ──
        public static int RecomputeCount { get; private set; }
        public static int EngageCount { get; private set; }
        public static int RegroupCount { get; private set; }
        public static int RoutedCount { get; private set; }
        public static int PatrolRoutedCount { get; private set; }
        public static int RebuiltCount { get; private set; }
        public static int EliteCount { get; private set; }
        public static int HarassCount { get; private set; }
        /// <summary>收复时撤回的在途骚扰部队数（自检读点）。</summary>
        public static int HarassRecalled { get; private set; }
        public static int StatusChanges { get; private set; }
        public static double LastSyncMs { get; private set; }
        public static double MaxSyncMs { get; private set; }
        public static double LastCombatMs { get; private set; }
        public static double MaxCombatMs { get; private set; }
        public static string LastProblem { get; private set; } = string.Empty;
        /// <summary>每次状态 / 控制度变化 +1（地图标签缓存用）。</summary>
        public static int Revision { get; private set; } = 1;

        private static int _seenOutpostRevision = -1;
        private static CampaignState _seenState;
        private static readonly List<OutpostRecord> OutpostScratch = new List<OutpostRecord>(32);
        private static readonly List<PatrolRecord> PatrolScratch = new List<PatrolRecord>(16);
        private static readonly List<Vector2> OwnScratch = new List<Vector2>(128);
        private static readonly List<(RaidUnitDef Def, bool Elite)> UnitScratch = new List<(RaidUnitDef, bool)>(32);

        // ─────────────────────────────── 调参（fg.TbHomeTuning，fgdata_territory_control.py）───────────────────────────────

        private static float T(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static long SyncTicks => Math.Max(1, GameClock.TicksFor(Math.Max(0.05, T("territory.control.sync_seconds", 1f))));
        public static int ReclaimBelow => Mathf.Clamp(Mathf.RoundToInt(T("territory.control.reclaim_below", 20f)), 1, 100);
        public static double ReinforceDays => Math.Max(0.01, T("territory.reinforce.interval_days", 3f));
        public static long ReinforceTicks => Math.Max(1, GameClock.TicksFor(ReinforceDays * GameClock.DaySeconds));
        public static double RebuildChance => Mathf.Clamp01(T("territory.reinforce.rebuild_chance", 0.5f));
        public static int RebuildMax => Math.Max(0, Mathf.RoundToInt(T("territory.reinforce.rebuild_max", 1f)));
        public static float RebuildGarrisonRatio => Mathf.Clamp01(T("territory.reinforce.rebuild_garrison_ratio", 0.5f));
        public static float RebuildBlockRadius => Math.Max(0f, T("territory.reinforce.block_radius_cells", 80f));
        public static double EliteDays => Math.Max(0.01, T("territory.elite.interval_days", 7f));
        public static long EliteTicks => Math.Max(1, GameClock.TicksFor(EliteDays * GameClock.DaySeconds));
        public static long HarassTicks => Math.Max(1, GameClock.TicksFor(Math.Max(0.01, T("territory.harass.interval_hours", 6f)) * GameClock.HourSeconds));
        public static int HarassUnits => Math.Max(1, Mathf.RoundToInt(T("territory.harass.units", 3f)));
        public static float EngageRadius => Math.Max(2f, T("territory.engage.radius_cells", 18f));
        /// <summary>家园区侦察巢是否也迎战（territory.engage.scout_nest_mode：初值 1 = 不迎战，2 = 迎战；M1～M7 已验收的家园区节奏不变，是否让侦察巢主动交战由 FG9-BAL-01 第一幕平衡定，DEBT-FG8EXP03-07）。</summary>
        public static bool ScoutNestsEngage => ScoutNestsEngageOverrideForTests ?? T("territory.engage.scout_nest_mode", 1f) >= 1.5f;

        /// <summary>自检用：临时打开 / 关掉侦察巢迎战（null = 按表）。正式代码没有赋值点。</summary>
        public static bool? ScoutNestsEngageOverrideForTests;
        public static double RegroupSeconds => Math.Max(1.0, T("territory.engage.regroup_seconds", 20f));
        public static double EngageMaxSeconds => Math.Max(5.0, T("territory.engage.max_seconds", 180f));
        public static int ScrapPerTier => Math.Max(0, Mathf.RoundToInt(T("territory.loot.scrap_per_tier", 20f)));

        // ─────────────────────────────── 表 ───────────────────────────────

        public static GameConfig.fg.TerritoryControl Row(string territoryId)
        {
            if (string.IsNullOrEmpty(territoryId))
            {
                return null;
            }
            try
            {
                return ConfigSystem.Instance.Tables?.TbTerritoryControl?.GetOrDefault(territoryId);
            }
            catch (Exception e)
            {
                LastProblem = "fg.TbTerritoryControl 读不到：" + e.Message;
                return null;
            }
        }

        // ─────────────────────────────── 状态 ───────────────────────────────

        public static TerritoryControlState StateOf(CampaignState s) => s?.Raids?.Territory;

        public static void EnsureState(CampaignState s)
        {
            if (s?.Raids == null)
            {
                return;
            }
            TerritoryControlState st = s.Raids.Territory ??= new TerritoryControlState();
            st.Records ??= Array.Empty<TerritoryControlRecord>();
            if (st.NextEngageSerial < 1)
            {
                st.NextEngageSerial = 1;
            }
            if (st.NextRemnantSerial < 1)
            {
                st.NextRemnantSerial = 1;
            }
            foreach (TerritoryControlRecord r in st.Records)
            {
                if (r == null)
                {
                    continue;
                }
                r.TerritoryId ??= string.Empty;
                r.RemnantId ??= string.Empty;
                r.HarassGroupId ??= string.Empty;
                r.Status = Mathf.Clamp(r.Status, StatusEnemy, StatusReclaimed);
                r.Control = Mathf.Clamp(r.Control, 0, 100);
            }
        }

        /// <summary>新会话（新建 / 读档 / 回主菜单）：会话统计与缓存清零（存档不动）。</summary>
        public static void ResetSession()
        {
            _seenOutpostRevision = -1;
            _seenState = null;
            RecomputeCount = 0;
            EngageCount = 0;
            RegroupCount = 0;
            RoutedCount = 0;
            PatrolRoutedCount = 0;
            RebuiltCount = 0;
            EliteCount = 0;
            HarassCount = 0;
            HarassRecalled = 0;
            StatusChanges = 0;
            LastSyncMs = 0;
            MaxSyncMs = 0;
            LastCombatMs = 0;
            MaxCombatMs = 0;
            LastProblem = string.Empty;
            Revision++;
        }

        public static TerritoryControlRecord Find(CampaignState s, string territoryId)
        {
            TerritoryControlRecord[] all = StateOf(s)?.Records;
            if (all == null || string.IsNullOrEmpty(territoryId))
            {
                return null;
            }
            foreach (TerritoryControlRecord r in all)
            {
                if (r != null && r.TerritoryId == territoryId)
                {
                    return r;
                }
            }
            return null;
        }

        public static int StatusOf(CampaignState s, string territoryId) => Find(s, territoryId)?.Status ?? StatusEnemy;

        public static bool IsReclaimed(CampaignState s, string territoryId) => StatusOf(s, territoryId) == StatusReclaimed;

        public static string StatusKey(int status) => status switch
        {
            StatusPacifying => "territory.status.pacifying",
            StatusReclaimed => "territory.status.reclaimed",
            _ => "territory.status.enemy",
        };

        /// <summary>
        /// 控制度（0～100，向下取整）：活跃的领地主力据点等级之和 ÷ 全部领地主力据点等级之和。<paramref name="total"/> = 0（旧世界没有领地据点）时返回 100。O(据点)。
        /// </summary>
        public static int ComputeControl(CampaignState s, string territoryId, out int alive, out int total)
        {
            alive = 0;
            total = 0;
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o == null || o.Kind != WorldOutpostSystem.KindTerritory || o.TerritoryId != territoryId)
                {
                    continue;
                }
                int w = Math.Max(1, o.Tier);
                total += w;
                if (!o.Destroyed)
                {
                    alive += w;
                }
            }
            return total <= 0 ? 100 : (int)(alive * 100L / total);
        }

        /// <summary>战略地图上的领地标签：“名字（控制度 N% · 敌控 / 平定中 / 已收复）”；旧世界（没有领地据点）写“待侦察”。</summary>
        public static string MapLabel(CampaignState s, PlannedTerritory t)
        {
            if (t == null)
            {
                return string.Empty;
            }
            TerritoryControlRecord r = Find(s, t.Id);
            ComputeControlCached(s, t.Id, out int total);
            if (r == null || total <= 0)
            {
                return GameText.Format("ui.map.territory_label", GameText.Get(t.NameKey));
            }
            return GameText.Format("ui.map.territory_control", GameText.Get(t.NameKey), r.Control, GameText.Get(StatusKey(r.Status)));
        }

        private static readonly Dictionary<string, int> TotalCache = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int _totalCacheRevision = -1;
        private static CampaignState _totalCacheState;

        private static void ComputeControlCached(CampaignState s, string territoryId, out int total)
        {
            if (!ReferenceEquals(s, _totalCacheState) || _totalCacheRevision != WorldOutpostSystem.Revision)
            {
                TotalCache.Clear();
                _totalCacheState = s;
                _totalCacheRevision = WorldOutpostSystem.Revision;
            }
            if (!TotalCache.TryGetValue(territoryId, out total))
            {
                ComputeControl(s, territoryId, out _, out total);
                TotalCache[territoryId] = total;
            }
        }

        /// <summary>
        /// 统计面板“物流与能源”页的领地段（B19）：每个有领地据点的阵营领地一行“名字：状态（控制度 N%）”，再加一行累计（摧毁据点 / 歼灭巡逻 / 击溃精英残部 / 阵营重建 / 受骚扰）。
        /// O(领地数)（控制度是记录里的缓存），只在面板刷新时调用。
        /// </summary>
        public static void BuildStatsLines(CampaignState s, List<(string Text, string Cls)> into)
        {
            TerritoryControlState st = StateOf(s);
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (st == null || plan == null || into == null || st.Records == null || st.Records.Length == 0)
            {
                return;
            }
            int act = Math.Max(1, s.Progress?.Act ?? 1);
            bool title = false;
            foreach (TerritoryControlRecord r in st.Records)
            {
                PlannedTerritory t = r != null ? plan.Find(r.TerritoryId) : null;
                if (t == null || (t.Act > act && r.Status == StatusEnemy))
                {
                    continue;
                }
                ComputeControlCached(s, r.TerritoryId, out int total);
                if (total <= 0)
                {
                    continue;
                }
                if (!title)
                {
                    into.Add((GameText.Get("stats.territory.title"), "st-row-title"));
                    title = true;
                }
                into.Add((GameText.Format("stats.territory.row", GameText.Get(t.NameKey), GameText.Get(StatusKey(r.Status)), r.Control), r.Status == StatusReclaimed ? null : "st-row-dim"));
            }
            if (title)
            {
                into.Add((GameText.Format("stats.territory.totals", st.OutpostsDestroyed, st.PatrolsRouted, st.RemnantsRouted, st.OutpostsRebuilt, st.HarassSent), null));
            }
        }

        // ─────────────────────────────── 每个模拟步（世界模拟在据点与突袭导演之后调用）───────────────────────────────

        /// <summary>
        /// 每 territory.control.sync_seconds 一次（按步序号）：补齐记录、控制度（据点变化时重算）、首领判定 → 平定、控制度低于阈值 → 收复、
        /// 敌控时的阵营增援（重建）、已收复时的精英残部、敌控 / 平定中时的骚扰。暂停不走、与观察无关。
        /// </summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (s?.Raids == null || tick % SyncTicks != 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return;
            }
            EnsureState(s);
            EnsureRecords(s, plan, tick);
            if (!ReferenceEquals(s, _seenState) || _seenOutpostRevision != WorldOutpostSystem.Revision)
            {
                RecomputeAll(s);
            }
            foreach (TerritoryControlRecord r in StateOf(s).Records)
            {
                if (r == null)
                {
                    continue;
                }
                GameConfig.fg.TerritoryControl row = Row(r.TerritoryId);
                if (!r.LeaderDefeated && row != null && row.LeaderRule != RuleNone && RaidDirectorService.StoryRuleMet(s, row.LeaderRule))
                {
                    Pacify(s, r, tick);
                }
                if (r.Status == StatusPacifying && r.Control < ReclaimBelow)
                {
                    Reclaim(s, plan, r, tick);
                }
                if (r.Status == StatusEnemy && tick >= r.NextReinforceTick)
                {
                    ReinforceWave(s, plan, r, tick);
                }
                if (r.Status == StatusReclaimed && tick >= r.NextEliteTick)
                {
                    EliteWave(s, plan, r, row, tick);
                }
                if (r.Status != StatusReclaimed && tick >= r.NextHarassTick)
                {
                    HarassCheck(s, plan, r, row, tick);
                }
            }
            LastSyncMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxSyncMs = Math.Max(MaxSyncMs, LastSyncMs);
        }

        private static void EnsureRecords(CampaignState s, WorldPlan plan, long tick)
        {
            TerritoryControlState st = StateOf(s);
            List<TerritoryControlRecord> list = null;
            foreach (PlannedTerritory t in plan.Territories)
            {
                if (!t.IsFaction || Find(s, t.Id) != null)
                {
                    continue;
                }
                list ??= new List<TerritoryControlRecord>(st.Records);
                // 计时都落在固定网格上（k × 间隔），与记录何时补出来无关（旧档读进来第一次推进时补）。
                list.Add(new TerritoryControlRecord
                {
                    TerritoryId = t.Id,
                    Status = StatusEnemy,
                    Control = ComputeControl(s, t.Id, out _, out _),
                    NextReinforceTick = NextGrid(tick, ReinforceTicks),
                    NextHarassTick = NextGrid(tick, HarassTicks),
                });
            }
            if (list != null)
            {
                st.Records = list.ToArray();
                Revision++;
            }
        }

        private static long NextGrid(long tick, long interval) => (tick / Math.Max(1, interval) + 1) * Math.Max(1, interval);

        private static void RecomputeAll(CampaignState s)
        {
            _seenState = s;
            _seenOutpostRevision = WorldOutpostSystem.Revision;
            RecomputeCount++;
            foreach (TerritoryControlRecord r in StateOf(s).Records)
            {
                if (r == null)
                {
                    continue;
                }
                int c = ComputeControl(s, r.TerritoryId, out _, out _);
                if (c != r.Control)
                {
                    r.Control = c;
                    Revision++;
                }
            }
        }

        /// <summary>
        /// 首领被击败（首领 Story 的正式入口；有剧情规则的领地由 <see cref="WorldStep"/> 自动判定）：领地进入平定，不再增援。幂等。
        /// 返回是否这一次才变成平定。
        /// </summary>
        public static bool MarkLeaderDefeated(CampaignState s, string territoryId)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (s?.Raids == null || plan == null)
            {
                return false;
            }
            EnsureState(s);
            EnsureRecords(s, plan, GameClock.Ticks);
            TerritoryControlRecord r = Find(s, territoryId);
            if (r == null || r.LeaderDefeated)
            {
                return false;
            }
            Pacify(s, r, GameClock.Ticks);
            RecomputeAll(s);
            if (r.Status == StatusPacifying && r.Control < ReclaimBelow)
            {
                Reclaim(s, plan, r, GameClock.Ticks);
            }
            return true;
        }

        private static void Pacify(CampaignState s, TerritoryControlRecord r, long tick)
        {
            r.LeaderDefeated = true;
            r.LeaderDefeatedTick = tick;
            if (r.Status == StatusEnemy)
            {
                r.Status = StatusPacifying;
            }
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o != null && o.TerritoryId == r.TerritoryId && o.Kind == WorldOutpostSystem.KindTerritory)
                {
                    WorldOutpostSystem.StopReinforcement(s, o);
                }
            }
            StatusChanges++;
            Revision++;
            GameConfig.fg.TerritoryControl row = Row(r.TerritoryId);
            string name = TerritoryName(s, r.TerritoryId);
            NotificationCenter.Post(NotifyStatus, GameText.Format("territory.notify.pacified", name, row != null ? GameText.Get(row.LeaderNameKey) : string.Empty, ReclaimBelow),
                CenterOf(s, r.TerritoryId));
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstTerritoryPacified);
            GameEvent.Send(LeaderDefeatedEvent, r.TerritoryId);
            GameEvent.Send(ChangedEvent, r.TerritoryId);
            Log.Info($"[TerritoryControlService] {r.TerritoryId} 首领被击败，进入平定（控制度 {r.Control}%），第 {tick} 步");
        }

        private static void Reclaim(CampaignState s, WorldPlan plan, TerritoryControlRecord r, long tick)
        {
            r.Status = StatusReclaimed;
            r.ReclaimedTick = tick;
            r.NextEliteTick = tick + EliteTicks;
            // 敌方巡逻撤离（FGR-EXP-022）：这片领地据点的巡逻全部移除（正在迎战的先从内核里撤走）。
            CombatSite site = HomeSite;
            string id = r.TerritoryId;
            foreach (PatrolRecord p in WorldOutpostSystem.Patrols(s))
            {
                if (p != null && p.Engaged && BelongsTo(s, p, id))
                {
                    site?.DespawnSiegeGroup(p.EngageKey);
                }
            }
            WorldOutpostSystem.RemovePatrols(s, p => BelongsTo(s, p, id));
            // 复审 P2：收复那一刻还在路上的骚扰部队撤回（沿原路回出发据点，驻军还给据点）；已经到达展开攻城的按攻城规则打完 / 撤退。
            if (!string.IsNullOrEmpty(r.HarassGroupId))
            {
                foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
                {
                    if (g != null && g.GroupId == r.HarassGroupId && g.State == TransitGroupState.Marching && !g.Engaged)
                    {
                        WorldTransitSystem.BeginRetreat(s, g);
                        HarassRecalled++;
                    }
                }
            }
            StatusChanges++;
            Revision++;
            GameConfig.fg.TerritoryControl row = Row(id);
            string name = TerritoryName(s, id);
            Vector3 at = CenterOf(s, id);
            NotificationCenter.Post(NotifyStatus, GameText.Format("territory.notify.reclaimed", name), at);
            if (row != null)
            {
                NotificationCenter.Post("core_memo", GameText.Format("territory.notify.memo", GameText.Get(row.MemoTitleKey), GameText.Get(row.MemoBodyKey)), at);
            }
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstTerritoryReclaimed);
            GameEvent.Send(Env.PurificationService.CoreMemoEvent, MemoPrefix + id);
            GameEvent.Send(ChangedEvent, id);
            Log.Info($"[TerritoryControlService] {id} 已收复（控制度 {r.Control}%），第 {tick} 步");
        }

        private static bool BelongsTo(CampaignState s, PatrolRecord p, string territoryId)
        {
            OutpostRecord o = WorldOutpostSystem.Find(s, p.OutpostId);
            return o != null && o.TerritoryId == territoryId && o.Kind == WorldOutpostSystem.KindTerritory;
        }

        // ── 阵营增援 ──

        private static void ReinforceWave(CampaignState s, WorldPlan plan, TerritoryControlRecord r, long tick)
        {
            long interval = ReinforceTicks;
            while (r.NextReinforceTick <= tick)
            {
                r.NextReinforceTick += interval;
            }
            r.Reinforcements++;
            int max = RebuildMax;
            if (max <= 0)
            {
                return;
            }
            OutpostScratch.Clear();
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o != null && o.Destroyed && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == r.TerritoryId)
                {
                    OutpostScratch.Add(o);
                }
            }
            if (OutpostScratch.Count == 0)
            {
                return;
            }
            OutpostScratch.Sort((a, b) => string.CompareOrdinal(a.OutpostId, b.OutpostId));
            WorldOutpostSystem.CollectOwnPositions(s, OwnScratch);
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int tIndex = TerritoryIndex(plan, r.TerritoryId);
            double chance = RebuildChance;
            float block = RebuildBlockRadius;
            int rebuilt = 0;
            foreach (OutpostRecord o in OutpostScratch)
            {
                if (rebuilt >= max)
                {
                    break;
                }
                uint h = WorldGenMath.Hash(seed, tIndex * 1000 + r.Reinforcements, StableHash(o.OutpostId), 0x5245494Eu); // "REIN"
                if ((h & 0xFFFFFF) / (double)0x1000000 >= chance || NearOwn(o.CellX, o.CellY, block))
                {
                    continue;
                }
                int garrison = Math.Max(1, Mathf.RoundToInt(o.GarrisonCap * RebuildGarrisonRatio));
                if (WorldOutpostSystem.Rebuild(s, o.OutpostId, garrison))
                {
                    rebuilt++;
                }
            }
            OutpostScratch.Clear();
            if (rebuilt == 0)
            {
                return;
            }
            RebuiltCount += rebuilt;
            StateOf(s).OutpostsRebuilt += rebuilt;
            RecomputeAll(s);
            NotificationCenter.Post(NotifyStatus, GameText.Format("territory.notify.rebuilt", TerritoryName(s, r.TerritoryId), rebuilt, r.Control,
                ReinforceDays.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)), CenterOf(s, r.TerritoryId));
            GameEvent.Send(ChangedEvent, r.TerritoryId);
        }

        private static bool NearOwn(int x, int y, float radius)
        {
            float r2 = radius * radius;
            foreach (Vector2 p in OwnScratch)
            {
                float dx = p.x - x;
                float dy = p.y - y;
                if (dx * dx + dy * dy <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 精英残部 ──

        private static void EliteWave(CampaignState s, WorldPlan plan, TerritoryControlRecord r, GameConfig.fg.TerritoryControl row, long tick)
        {
            long interval = EliteTicks;
            while (r.NextEliteTick <= tick)
            {
                r.NextEliteTick += interval;
            }
            OutpostRecord alive = string.IsNullOrEmpty(r.RemnantId) ? null : WorldOutpostSystem.Find(s, r.RemnantId);
            if (alive != null && !alive.Destroyed)
            {
                return; // 上一批还在：不叠加
            }
            GridCell at = RemnantCell(s, plan, r, out int tier);
            TerritoryControlState st = StateOf(s);
            string id = "remnant:" + r.TerritoryId + ":" + st.NextRemnantSerial;
            int count = Math.Max(1, row?.EliteCount ?? 4);
            OutpostRecord o = WorldOutpostSystem.SpawnRemnant(s, id, r.TerritoryId, at, tier, count);
            if (o == null)
            {
                return;
            }
            st.NextRemnantSerial++;
            r.RemnantId = id;
            r.EliteWaves++;
            EliteCount++;
            Revision++;
            NotificationCenter.Post(NotifyStatus, GameText.Format("territory.notify.elite", TerritoryName(s, r.TerritoryId), count,
                EliteDays.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)), new Vector3(at.X, 0f, at.Y));
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstEliteRemnant);
        }

        /// <summary>精英残部的位置：这片领地某个领地据点原来的格子（按（世界种子, 领地, 第几批）确定地挑；那里生成时就是可走的平地）；一个都没有时取领地中心附近第一个敌方可走格。</summary>
        private static GridCell RemnantCell(CampaignState s, WorldPlan plan, TerritoryControlRecord r, out int tier)
        {
            OutpostScratch.Clear();
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o != null && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == r.TerritoryId)
                {
                    OutpostScratch.Add(o);
                }
            }
            tier = 1;
            PlannedTerritory t = plan.Find(r.TerritoryId);
            if (OutpostScratch.Count == 0)
            {
                OutpostScratch.Clear();
                GridCell c = t != null ? new GridCell(t.CenterX, t.CenterY) : HomeGridService.CorePivot(s);
                return NavService.IsBound ? SiegeService.NearestPassable(c, 12) : c;
            }
            OutpostScratch.Sort((a, b) => string.CompareOrdinal(a.OutpostId, b.OutpostId));
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            uint h = WorldGenMath.Hash(seed, TerritoryIndex(plan, r.TerritoryId), r.EliteWaves, 0x454C4954u); // "ELIT"
            OutpostRecord pick = OutpostScratch[(int)(h % (uint)OutpostScratch.Count)];
            foreach (OutpostRecord o in OutpostScratch)
            {
                tier = Math.Max(tier, o.Tier);
            }
            OutpostScratch.Clear();
            return new GridCell(pick.CellX, pick.CellY);
        }

        // ── 骚扰 ──

        private static void HarassCheck(CampaignState s, WorldPlan plan, TerritoryControlRecord r, GameConfig.fg.TerritoryControl row, long tick)
        {
            long interval = HarassTicks;
            while (r.NextHarassTick <= tick)
            {
                r.NextHarassTick += interval;
            }
            if (!string.IsNullOrEmpty(r.HarassGroupId))
            {
                foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
                {
                    if (g != null && g.GroupId == r.HarassGroupId)
                    {
                        return; // 上一支还在途中 / 攻城：不再派
                    }
                }
                r.HarassGroupId = string.Empty;
            }
            PlannedTerritory t = plan.Find(r.TerritoryId);
            if (t == null)
            {
                return;
            }
            // 这片领地里的己方建筑群（座数最多的那一群；同数按 ID）。
            bool found = false;
            WorldMapOwnClusters.Cluster target = default;
            double r2 = (double)t.Radius * t.Radius;
            foreach (WorldMapOwnClusters.Cluster c in WorldMapOwnClusters.For(s))
            {
                if (c.ContainsCore || c.Count <= 0)
                {
                    continue;
                }
                double dx = c.X - t.CenterX;
                double dy = c.Y - t.CenterY;
                if (dx * dx + dy * dy > r2)
                {
                    continue;
                }
                if (!found || c.Count > target.Count || (c.Count == target.Count && string.CompareOrdinal(c.Id, target.Id) < 0))
                {
                    target = c;
                    found = true;
                }
            }
            if (!found)
            {
                return;
            }
            OutpostRecord origin = null;
            double best = double.MaxValue;
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o == null || o.Destroyed || o.Engaged || o.Kind != WorldOutpostSystem.KindTerritory || o.TerritoryId != r.TerritoryId)
                {
                    continue;
                }
                WorldOutpostSystem.Peek(s, o, out int garrison, out _);
                if (garrison < 2)
                {
                    continue;
                }
                double dx = o.CellX - target.X;
                double dy = o.CellY - target.Y;
                double d = dx * dx + dy * dy;
                if (d < best || (d == best && origin != null && string.CompareOrdinal(o.OutpostId, origin.OutpostId) < 0))
                {
                    best = d;
                    origin = o;
                }
            }
            if (origin == null)
            {
                return; // 领地里没有能派兵的据点（全被摧毁 / 驻军不够）：这一次不骚扰
            }
            TransitGroupRecord g2 = WorldTransitSystem.DispatchHarass(s, origin, r.TerritoryId, HarassUnits, target.X, target.Y, out string failure);
            if (g2 == null)
            {
                LastProblem = "骚扰派兵失败：" + failure;
                return;
            }
            FillComposition(g2, row?.GarrisonUnits, g2.UnitCount, r.TerritoryId);
            r.HarassGroupId = g2.GroupId;
            HarassCount++;
            StateOf(s).HarassSent++;
            NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.harass", TerritoryName(s, r.TerritoryId), g2.UnitCount),
                new Vector3((float)target.X, 0f, (float)target.Y));
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstTerritoryHarassed);
        }

        private static void FillComposition(TransitGroupRecord g, string units, int count, string faction)
        {
            BuildUnits(units, count, faction, elite: false);
            var ids = new List<string>(4);
            var counts = new List<int>(4);
            foreach ((RaidUnitDef def, bool _) in UnitScratch)
            {
                if (def == null)
                {
                    continue;
                }
                int i = ids.IndexOf(def.Id);
                if (i < 0)
                {
                    ids.Add(def.Id);
                    counts.Add(1);
                }
                else
                {
                    counts[i]++;
                }
            }
            UnitScratch.Clear();
            if (ids.Count == 0)
            {
                return;
            }
            g.UnitIds = ids.ToArray();
            g.UnitCounts = counts.ToArray();
            g.EliteCounts = new int[ids.Count];
        }

        // ─────────────────────────────── 迎战（家园载入时，在攻城之后）───────────────────────────────

        private static CombatSite HomeSite => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        /// <summary>
        /// 每 siege.sync_seconds 一次（按步序号）：醒着的据点 / 巡逻附近有己方机器 → 就地展开迎战；迎战中的全灭 → 摧毁 / 歼灭（掉落、通知、控制度）；
        /// 附近没有己方机器满 territory.engage.regroup_seconds、或僵持满 territory.engage.max_seconds → 幸存者收拢。
        /// </summary>
        public static void CombatStep(CampaignState s, long ticksBefore, int worldHz)
        {
            CombatSite site = HomeSite;
            if (s?.Raids == null || site == null || site.IsDisposed || worldHz <= 0)
            {
                return;
            }
            long every = Math.Max(1, (long)Math.Round(SiegeCatalog.SyncSeconds * worldHz));
            if (ticksBefore % every != 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureState(s);
            float radius = EngageRadius;
            long now = GameClock.Ticks;
            long regroup = GameClock.TicksFor(RegroupSeconds);
            long maxEngage = GameClock.TicksFor(EngageMaxSeconds);
            OutpostScratch.Clear();
            OutpostScratch.AddRange(WorldOutpostSystem.Awake(s)); // 复制一份：摧毁会让醒着列表重建
            bool nests = ScoutNestsEngage;
            foreach (OutpostRecord o in OutpostScratch)
            {
                if (o == null || o.Destroyed || (!nests && !o.Engaged && o.Kind == WorldOutpostSystem.KindScoutNest))
                {
                    continue;
                }
                var at = new Vector2(o.CellX, o.CellY);
                if (o.Engaged)
                {
                    int alive = site.CountSiegeGroup(o.EngageKey);
                    if (alive == 0)
                    {
                        Routed(s, site, o);
                        continue;
                    }
                    bool stalemate = o.EngageTick >= 0 && now - o.EngageTick >= maxEngage;
                    if (!stalemate && site.FindNearestMachine(at, radius * 1.5f) > 0)
                    {
                        o.LastContactTick = now;
                    }
                    else if (stalemate || (o.LastContactTick >= 0 && now - o.LastContactTick >= regroup))
                    {
                        site.DespawnSiegeGroup(o.EngageKey);
                        o.Garrison = Math.Max(1, alive);
                        o.Engaged = false;
                        o.UnfoldedCount = 0;
                        o.LastContactTick = -1;
                        o.EngageTick = stalemate ? now : -1;
                        RegroupCount++;
                    }
                    continue;
                }
                if (o.Garrison <= 0 || (o.EngageTick >= 0 && now - o.EngageTick < maxEngage) || site.FindNearestMachine(at, radius) <= 0)
                {
                    continue;
                }
                GameConfig.fg.TerritoryControl row = Row(FactionOf(s, o));
                bool elite = o.Kind == WorldOutpostSystem.KindRemnant;
                BuildUnits(elite ? row?.EliteUnits : row?.GarrisonUnits, o.Garrison, FactionOf(s, o), elite);
                int key = o.EngageKey = AcquireKey(s, o.EngageKey);
                int placed = Unfold(s, site, key, new GridCell(o.CellX, o.CellY));
                if (placed <= 0)
                {
                    continue;
                }
                o.Engaged = true;
                o.UnfoldedCount = placed;
                o.LastContactTick = now;
                o.EngageTick = now;
                EngageCount++;
                NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.engaged", OwnerName(s, o), placed), new Vector3(o.CellX, 0f, o.CellY));
            }
            OutpostScratch.Clear();

            PatrolScratch.Clear();
            PatrolScratch.AddRange(WorldOutpostSystem.Patrols(s));
            foreach (PatrolRecord p in PatrolScratch)
            {
                if (p == null)
                {
                    continue;
                }
                OutpostRecord o = WorldOutpostSystem.Find(s, p.OutpostId);
                if (!p.Engaged && o != null && o.Dormant && !o.AlwaysSimulate)
                {
                    continue; // 休眠的巡逻离己方实体都很远
                }
                var at = new Vector2((float)p.PosX, (float)p.PosY);
                if (p.Engaged)
                {
                    int alive = site.CountSiegeGroup(p.EngageKey);
                    if (alive == 0)
                    {
                        PatrolRouted(s, p, o);
                        continue;
                    }
                    bool stalemate = p.EngageTick >= 0 && now - p.EngageTick >= maxEngage;
                    if (!stalemate && site.FindNearestMachine(at, radius * 1.5f) > 0)
                    {
                        p.LastContactTick = now;
                    }
                    else if (stalemate || (p.LastContactTick >= 0 && now - p.LastContactTick >= regroup))
                    {
                        site.DespawnSiegeGroup(p.EngageKey);
                        p.UnitCount = Math.Max(1, alive);
                        p.Engaged = false;
                        p.UnfoldedCount = 0;
                        p.LastContactTick = -1;
                        p.EngageTick = stalemate ? now : -1;
                        RegroupCount++;
                    }
                    continue;
                }
                if (p.UnitCount <= 0 || (p.EngageTick >= 0 && now - p.EngageTick < maxEngage) || site.FindNearestMachine(at, radius) <= 0)
                {
                    continue;
                }
                string faction = o != null ? FactionOf(s, o) : string.Empty;
                BuildUnits(Row(faction)?.GarrisonUnits, p.UnitCount, faction, elite: false);
                int key = p.EngageKey = AcquireKey(s, p.EngageKey);
                int placed = Unfold(s, site, key, NavService.CellOf(p.PosX, p.PosY));
                if (placed <= 0)
                {
                    continue;
                }
                p.Engaged = true;
                p.UnfoldedCount = placed;
                p.LastContactTick = now;
                p.EngageTick = now;
                EngageCount++;
                NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.patrol_engaged", o != null ? OwnerName(s, o) : GameText.Get("ui.territory.wild"), placed),
                    new Vector3((float)p.PosX, 0f, (float)p.PosY));
            }
            PatrolScratch.Clear();
            LastCombatMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxCombatMs = Math.Max(MaxCombatMs, LastCombatMs);
        }

        /// <summary>这个队伍键是不是正在迎战的据点 / 巡逻（攻城服务清理“没有主人的队伍”时问它）。O(据点 + 巡逻)，只在内核里有队伍时调用。</summary>
        public static bool OwnsKey(CampaignState s, int key)
        {
            if (!IsEngageKey(key) || s?.Raids == null)
            {
                return false;
            }
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o != null && o.Engaged && o.EngageKey == key)
                {
                    return true;
                }
            }
            foreach (PatrolRecord p in WorldOutpostSystem.Patrols(s))
            {
                if (p != null && p.Engaged && p.EngageKey == key)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 展开迎战时取队伍键：上一次用过、仍在区间里且没被别人占用的沿用（确定），否则按序号在区间里取下一个没被占用的。
        /// 只在展开那一刻调用（O(据点 + 巡逻 + 队伍)，与逐帧无关）。
        /// </summary>
        private static int AcquireKey(CampaignState s, int previous)
        {
            if (IsEngageKey(previous) && !KeyInUse(s, previous))
            {
                return previous;
            }
            TerritoryControlState st = StateOf(s);
            for (int i = 0; i < KeyCount; i++)
            {
                int serial = Math.Max(1, st.NextEngageSerial);
                st.NextEngageSerial = serial >= KeyCount ? 1 : serial + 1;
                int key = KeyBase + (serial - 1) % KeyCount;
                if (!KeyInUse(s, key))
                {
                    return key;
                }
            }
            LastProblem = "迎战队伍键区间已满（" + KeyCount + " 个同时在用）";
            return KeyBase;
        }

        private static bool KeyInUse(CampaignState s, int key)
        {
            if (OwnsKey(s, key))
            {
                return true;
            }
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (g != null && SiegeService.KeyOf(g) == key)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>按编成（逗号分隔的 fg.TbRaidUnit.id，循环取）凑 <paramref name="count"/> 台；编成为 none / 表里没有时取该阵营编成表第一种单位，再没有取第一行。</summary>
        private static void BuildUnits(string units, int count, string faction, bool elite)
        {
            UnitScratch.Clear();
            var defs = new List<RaidUnitDef>(4);
            if (!string.IsNullOrEmpty(units) && units != RuleNone)
            {
                foreach (string id in units.Split(','))
                {
                    if (RaidCatalog.TryGetUnit(id.Trim(), out RaidUnitDef d))
                    {
                        defs.Add(d);
                    }
                }
            }
            if (defs.Count == 0)
            {
                RaidUnitDef fallback = null;
                foreach (RaidUnitDef u in RaidCatalog.Units)
                {
                    if (fallback == null || (!string.IsNullOrEmpty(faction) && u.Faction == faction && fallback.Faction != faction))
                    {
                        fallback = u;
                    }
                }
                if (fallback != null)
                {
                    defs.Add(fallback);
                }
            }
            for (int i = 0; i < Math.Max(0, count); i++)
            {
                UnitScratch.Add((defs.Count > 0 ? defs[i % defs.Count] : null, elite));
            }
        }

        /// <summary>在 <paramref name="at"/> 附近按切比雪夫环把 <see cref="UnitScratch"/> 逐台生成（敌方可走格，固定顺序），设成“就地交战”。返回生成的台数。</summary>
        private static int Unfold(CampaignState s, CombatSite site, int key, GridCell at)
        {
            GridCell c = SiegeService.NearestPassable(at, 8);
            var home = new Vector2(c.X, c.Y);
            int step = Math.Max(1, Mathf.RoundToInt(SiegeCatalog.SpawnSpacing));
            int placed = 0;
            int next = 0;
            float hp = DifficultyService.EnemyHealthMul(s);
            float dmg = DifficultyService.EnemyDamageMul(s);
            for (int ring = 0; ring <= 32 && next < UnitScratch.Count; ring++)
            {
                for (int dy = -ring; dy <= ring && next < UnitScratch.Count; dy++)
                {
                    for (int dx = -ring; dx <= ring && next < UnitScratch.Count; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring || !NavService.PassableNow(c.X + dx * step, c.Y + dy * step, BinGames.Sim.Nav.NavConst.ClassHostile))
                        {
                            continue;
                        }
                        (RaidUnitDef def, bool elite) = UnitScratch[next++];
                        int unit = SiegeService.SpawnOne(site, key, new Vector2(c.X + dx * step, c.Y + dy * step), home, def, elite, hp, dmg);
                        if (unit > 0)
                        {
                            site.SetSiegeUnitMode(unit, CombatSiegeMode.Skirmish);
                            placed++;
                        }
                        else
                        {
                            LastProblem = "迎战展开生成失败（战斗内核已满？）：" + (def?.EnemyTypeId ?? "(无编成)");
                        }
                    }
                }
            }
            UnitScratch.Clear();
            return placed;
        }

        /// <summary>迎战的驻军全灭：据点被摧毁（突袭导演记方向）、废料掉在原地并开搬运单、通知控制度变化。</summary>
        private static void Routed(CampaignState s, CombatSite site, OutpostRecord o)
        {
            string territory = o.TerritoryId;
            bool counts = o.Kind == WorldOutpostSystem.KindTerritory;
            int before = counts ? ComputeControl(s, territory, out _, out _) : 0;
            site.DespawnSiegeGroup(o.EngageKey);
            if (!WorldOutpostSystem.MarkDestroyed(s, o.OutpostId))
            {
                return;
            }
            RoutedCount++;
            TerritoryControlState st = StateOf(s);
            bool remnant = o.Kind == WorldOutpostSystem.KindRemnant;
            int scrap = remnant ? Math.Max(1, Row(territory)?.EliteScrap ?? 1) : Math.Max(0, Math.Max(1, o.Tier) * ScrapPerTier);
            var at = new Vector2(o.CellX, o.CellY);
            if (scrap > 0)
            {
                GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, CampaignEconomyLedger.ResourceScrap, scrap,
                    LootPrefix + o.OutpostId + ":" + o.DestroyCount);
                if (g != null)
                {
                    HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
                }
            }
            if (!remnant)
            {
                // FG8-LOOT-01（FGR-LOOT-010“据点：材料、少量组件；摧毁时可能掉数据核心”）：按掉落表 outpost 行另掷一份（与上面的等级废料并存）。
                Economy.FieldLootService.OnOutpostDestroyed(s, FactionOf(s, o), at);
            }
            var where = new Vector3(o.CellX, 0f, o.CellY);
            if (remnant)
            {
                st.RemnantsRouted++;
                NotificationCenter.Post(NotifyStatus, GameText.Format("territory.notify.elite_routed", TerritoryName(s, territory), scrap), where);
            }
            else if (counts)
            {
                st.OutpostsDestroyed++;
                RecomputeAll(s);
                int after = Find(s, territory)?.Control ?? ComputeControl(s, territory, out _, out _);
                NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.destroyed", TerritoryName(s, territory), before, after, scrap), where);
                GameEvent.Send(ChangedEvent, territory);
            }
            else
            {
                st.OutpostsDestroyed++;
                NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.destroyed_other", OutpostName(o), scrap), where);
            }
            GuidanceHooks.Raise(GuidanceHooks.WorldFirstOutpostDestroyed);
        }

        private static void PatrolRouted(CampaignState s, PatrolRecord p, OutpostRecord o)
        {
            string id = p.PatrolId;
            WorldOutpostSystem.RemovePatrols(s, x => x.PatrolId == id);
            PatrolRoutedCount++;
            StateOf(s).PatrolsRouted++;
            NotificationCenter.Post(NotifyAlert, GameText.Format("territory.notify.patrol_routed", o != null ? OwnerName(s, o) : GameText.Get("ui.territory.wild")),
                new Vector3((float)p.PosX, 0f, (float)p.PosY));
        }

        // ─────────────────────────────── 小工具 ───────────────────────────────

        /// <summary>据点属于哪个阵营（领地据点 = 领地 ID；零散前哨 / 侦察巢按突袭导演的归属规则）。</summary>
        private static string FactionOf(CampaignState s, OutpostRecord o) =>
            o.Kind == WorldOutpostSystem.KindTerritory || o.Kind == WorldOutpostSystem.KindRemnant ? o.TerritoryId : RaidDirectorService.FactionOfOutpost(s, o) ?? o.TerritoryId;

        private static string OwnerName(CampaignState s, OutpostRecord o) =>
            o.Kind == WorldOutpostSystem.KindTerritory || o.Kind == WorldOutpostSystem.KindRemnant ? TerritoryName(s, o.TerritoryId) : OutpostName(o);

        /// <summary>据点名（地图标签同一套文本）：“静默据点（3 级）”“铸造零散前哨（5 级）”“侦察巢（2 级）”“精英残部”。</summary>
        public static string OutpostName(OutpostRecord o)
        {
            string fk = TerritoryPlanner.FactionKey(o.TerritoryId ?? string.Empty);
            string faction = !string.IsNullOrEmpty(o.TerritoryId) && GameText.Has(fk) ? GameText.Get(fk) : string.Empty;
            return o.Kind == WorldOutpostSystem.KindScoutNest ? GameText.Format("world.poi.scout_nest", Math.Max(1, o.Tier))
                : o.Kind == WorldOutpostSystem.KindTerritory ? GameText.Format("world.poi.outpost", faction, Math.Max(1, o.Tier))
                : o.Kind == WorldOutpostSystem.KindScatter || o.Kind == WorldOutpostSystem.KindOuter ? GameText.Format("world.poi.scatter_outpost", faction, Math.Max(1, o.Tier))
                : o.Kind == WorldOutpostSystem.KindRemnant ? GameText.Get("world.poi.remnant")
                : GameText.Get("outpost.name");
        }

        public static string TerritoryName(CampaignState s, string territoryId)
        {
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(territoryId);
            return t != null ? GameText.Get(t.NameKey) : GameText.Get("ui.territory.wild");
        }

        private static Vector3 CenterOf(CampaignState s, string territoryId)
        {
            PlannedTerritory t = WorldGenService.PlanFor(s)?.Find(territoryId);
            return t != null ? new Vector3(t.CenterX, 0f, t.CenterY) : Vector3.zero;
        }

        private static int TerritoryIndex(WorldPlan plan, string territoryId)
        {
            for (int i = 0; i < plan.Territories.Count; i++)
            {
                if (plan.Territories[i].Id == territoryId)
                {
                    return i;
                }
            }
            return 0;
        }

        /// <summary>字符串的稳定哈希（FNV-1a，与运行时 / 平台无关；不用 string.GetHashCode）。</summary>
        public static int StableHash(string text)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in text ?? string.Empty)
                {
                    h = (h ^ ch) * 16777619u;
                }
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }
}
