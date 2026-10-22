using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG8-QST-01 任务系统（FG08 FGR-QST-001～005、020；FGT-EXP-009）——在 Demo 目标追踪后端上扩展（FGR-QST-005）：
    /// - 同一份存档记录：<see cref="CampaignState.ObjectiveRecords"/>（<see cref="ObjectiveRecord.DefId"/> 非空 = fg.TbQuest 定义的任务；空 = OBJ-01～10）；
    /// - 同一个重算入口：<see cref="Recompute"/> 末尾调用 <see cref="RecomputeQuests"/>，模拟步每 clock.objective_recompute_seconds 一次（暂停不走、倍速按游戏时间、与观察无关）；
    /// - 同样的幂等判定：只读既有权威字段（领地控制记录、建筑记录、传送带内核、静默夜、己方前哨站、据点的被摧毁次数、机器位置），重复调用不产生错误结果，
    ///   奖励按（任务, 第几次尝试）的事件账本只发一次。
    /// 三类：主线（不能放弃，失败可以重新开始，关键材料由它给出）、支线（领地的支线地点被发现时出现，接取后才判定，可以放弃）、家园指引（可选，可以在设置里关闭，完成后领取奖励）。
    /// 到达 / 停留 / 护送 / 时限每 quest.check_seconds 检查一次（按步序号）；逐机器的距离判定只在有这类进行中的任务时做，O(这类任务数 × 机器数)。
    /// </summary>
    public static partial class CampaignObjectiveTracker
    {
        // ── 调参 ─────────────────────────────────────────────────────────

        public static int TrackMax => Math.Max(1, Mathf.RoundToInt(GameClock.TuningOr("quest.track_max", 3f)));
        public static double CheckSeconds => Math.Max(0.05, GameClock.TuningOr("quest.check_seconds", 1f));
        public static float ReachRadius => Math.Max(1f, GameClock.TuningOr("quest.reach_radius_cells", 12f));
        public static float HoldRadius => Math.Max(1f, GameClock.TuningOr("quest.hold_radius_cells", 14f));
        public static float EscortHealth => Math.Max(1f, GameClock.TuningOr("quest.escort_health", 160f));
        public static float EscortSpawnOffset => Math.Max(0f, GameClock.TuningOr("quest.escort_spawn_offset", 4f));
        public static float OutpostRadius => Math.Max(1f, GameClock.TuningOr("quest.outpost_radius_cells", 80f));
        public static double QuestPerfMs => Math.Max(0.01, GameClock.TuningOr("quest.perf_ms", 0.5f));

        /// <summary>任务状态任何变化（出现、接取、完成、失败、放弃、重新开始、追踪、阶段）都 +1：界面据此刷新（不每帧重排）。</summary>
        public static int QuestRevision { get; private set; } = 1;

        /// <summary>自检：最近一次 / 最长一次“到达 / 停留 / 护送 / 时限”检查的耗时与次数。</summary>
        public static double QuestCheckLastMs { get; private set; }
        public static double QuestCheckMaxMs { get; private set; }
        public static long QuestChecks { get; private set; }

        public static void ResetQuestStats()
        {
            QuestCheckLastMs = 0;
            QuestCheckMaxMs = 0;
            QuestChecks = 0;
        }

        /// <summary>最近一次操作的结果文字（追踪 / 放弃 / 重新开始 / 接取 / 领取 / 飞过去；界面与自检读）。</summary>
        public static string LastQuestMessage { get; private set; } = string.Empty;

        private static readonly List<WorldFeature> SideScratch = new List<WorldFeature>(32);
        private static readonly List<Vector2> DepotScratch = new List<Vector2>(8);

        // ── 查询 ─────────────────────────────────────────────────────────

        public static bool IsQuestRecord(ObjectiveRecord r) => r != null && !string.IsNullOrEmpty(r.DefId);

        public static ObjectiveRecord FindQuest(CampaignState state, string objectiveId)
        {
            if (state?.ObjectiveRecords == null || string.IsNullOrEmpty(objectiveId))
            {
                return null;
            }
            foreach (ObjectiveRecord r in state.ObjectiveRecords)
            {
                if (r != null && r.ObjectiveId == objectiveId)
                {
                    return r;
                }
            }
            return null;
        }

        public static Quest DefOf(ObjectiveRecord r) => r != null ? QuestCatalog.Get(r.DefId) : null;

        public static bool IsGuide(ObjectiveRecord r) => DefOf(r)?.Category == QuestCatalog.CatGuide;

        /// <summary>家园指引关掉时，这类任务在界面上不出现（记录原样保留，重新打开接着进行）。</summary>
        public static bool IsHidden(ObjectiveRecord r) => r != null && IsGuide(r) && !GameSettings.HomeGuideQuestsEnabled;

        /// <summary>界面上的“进行中”栏：进行中 + 可接取（Demo 目标链的进行中由调用方另外列出）。</summary>
        public static bool IsOpen(ObjectiveRecord r) => r != null && (r.State == ObjectiveState.Active || r.State == ObjectiveState.Available);

        /// <summary>追踪中的任务（按追踪先后），不含隐藏的家园指引。</summary>
        public static void TrackedQuests(CampaignState state, List<ObjectiveRecord> into)
        {
            into.Clear();
            if (state?.ObjectiveRecords == null)
            {
                return;
            }
            foreach (ObjectiveRecord r in state.ObjectiveRecords)
            {
                if (IsQuestRecord(r) && r.TrackOrder > 0 && r.State == ObjectiveState.Active && !IsHidden(r))
                {
                    into.Add(r);
                }
            }
            into.Sort((a, b) => a.TrackOrder.CompareTo(b.TrackOrder));
        }

        public static int TrackedCount(CampaignState state)
        {
            int n = 0;
            foreach (ObjectiveRecord r in state?.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (IsQuestRecord(r) && r.TrackOrder > 0 && r.State == ObjectiveState.Active && !IsHidden(r))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>任务现在在星球上的位置：护送中的货运车 = 车的实时位置；其余 = 任务地点（没有地点 = null）。</summary>
        public static Vector2? QuestLocation(CampaignState state, ObjectiveRecord r)
        {
            if (r == null)
            {
                return null;
            }
            if (!IsQuestRecord(r))
            {
                return CampaignObjectiveCatalog.LocationOf(state, r.ObjectiveId);
            }
            if (r.Stage == 1 && r.EscortLogicId > 0 && r.State == ObjectiveState.Active && MachineRegistry.TryGetRecord(r.EscortLogicId, out MachineRecord m) && m.IsAlive)
            {
                return LootPickService.PositionOfMachine(m);
            }
            return r.HasAnchor ? new Vector2(r.AnchorX, r.AnchorY) : (Vector2?)null;
        }

        public static string QuestTitle(ObjectiveRecord r) =>
            r == null ? string.Empty : IsQuestRecord(r) ? QuestCatalog.Title(DefOf(r)) ?? r.DefId : CampaignObjectiveCatalog.TitleOf(r.ObjectiveId);

        // ── 重算入口（Recompute 末尾）──────────────────────────────────────

        private static void RecomputeQuests(CampaignState state)
        {
            if (state == null || !QuestCatalog.Loaded)
            {
                return;
            }
            state.Quests ??= new QuestState();
            QuestState qs = state.Quests;
            long now = GameClock.Ticks;
            EnsureSideBaseline(state, qs);
            OfferQuests(state, now);
            // 建筑 / 首领 / 静默夜 / 前哨站类：每次重算都判定（只读既有字段；建筑类只在家园指引进行中时数建筑）。
            EvaluateStructural(state, now);
            // 到达 / 停留 / 护送 / 时限类不在这里判定：重算由事件与会话内计时器触发（读档、存档、观察都会改变它的时机），
            // 改由 WorldStep 按步序号判定（quest.check_seconds），保证存档 / 读档 / 观察不改变判定发生在哪一步。
        }

        /// <summary>
        /// 世界步（<see cref="WorldSim.WorldSimulation"/> 每步提交后调用）：每 quest.check_seconds 游戏秒（只看步序号：tick 是间隔的整数倍时，与观察 / 读档 / 存档无关）
        /// 判定到达 / 停留 / 护送 / 时限类任务。没有任务状态时 O(1)。
        /// </summary>
        public static void WorldStep(CampaignState state, long tick)
        {
            QuestState qs = state?.Quests;
            if (qs == null || !QuestCatalog.Loaded)
            {
                return;
            }
            long every = Math.Max(1L, GameClock.TicksFor(CheckSeconds));
            if (tick % every != 0)
            {
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            EvaluateTimed(state, tick);
            QuestCheckLastMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            QuestChecks++;
            if (QuestCheckLastMs > QuestCheckMaxMs)
            {
                QuestCheckMaxMs = QuestCheckLastMs;
            }
        }

        // ── 出现（主线 / 家园指引）─────────────────────────────────────────

        private static void OfferQuests(CampaignState state, long now)
        {
            bool guideOn = GameSettings.HomeGuideQuestsEnabled;
            foreach (Quest q in QuestCatalog.Rows)
            {
                if (q.Category == QuestCatalog.CatSide || FindQuest(state, q.Id) != null)
                {
                    continue;
                }
                if (q.Category == QuestCatalog.CatGuide && !guideOn)
                {
                    continue;
                }
                if (!Unlocked(state, q))
                {
                    continue;
                }
                ObjectiveRecord r = NewQuestRecord(state, q, q.Id, now);
                if (!ResolveAnchor(state, q, r, null))
                {
                    // 地点还解析不出来（生成器世界尚未就绪）：下一次重算再试，不出现半成品。
                    RemoveRecord(state, r);
                    continue;
                }
                AcceptInternal(state, q, r, now, notify: true);
            }
        }

        private static bool Unlocked(CampaignState state, Quest q)
        {
            switch (q.Unlock)
            {
                case QuestCatalog.UnlockStart:
                    return true;
                case QuestCatalog.UnlockPrereq:
                    return FindQuest(state, q.Prereq)?.State == ObjectiveState.Completed;
                case QuestCatalog.UnlockPace:
                    return Act1PacingService.IsGateOpen(state, q.Prereq); // FG9-ACT1-01：传送带指引随传送带 T1 一起出现
                case QuestCatalog.UnlockTerritoryVisible:
                    PlannedTerritory t = WorldGenService.PlanFor(state)?.Find(q.Territory);
                    if (t == null)
                    {
                        return false;
                    }
                    int act = Math.Max(1, state.Progress?.Act ?? 1);
                    return t.Act <= act || Env.HomeExplorationService.TerritoryVisible(state, t);
                default:
                    return false;
            }
        }

        private static ObjectiveRecord NewQuestRecord(CampaignState state, Quest q, string objectiveId, long now)
        {
            var r = new ObjectiveRecord
            {
                ObjectiveId = objectiveId,
                DefId = q.Id,
                State = ObjectiveState.Available,
                StartedAtPlaySeconds = state.PlaySeconds,
                OfferedTick = now,
                Attempt = 1,
            };
            state.ObjectiveRecords = Append(state.ObjectiveRecords, r);
            QuestRevision++;
            return r;
        }

        /// <summary>接取（主线 / 家园指引出现即接取；支线由玩家在任务日志里接取）：开始计时（timerFrom = offer）、选定目标据点、发通知。</summary>
        private static void AcceptInternal(CampaignState state, Quest q, ObjectiveRecord r, long now, bool notify)
        {
            r.State = ObjectiveState.Active;
            r.OfferedTick = now;
            r.StartedAtPlaySeconds = state.PlaySeconds;
            r.Stage = 0;
            r.HoldTicks = 0;
            r.DeadlineWarned = false;
            r.DeadlineTick = QuestCatalog.HasTimeLimit(q) && q.TimerFrom != "stage" ? now + GameClock.TicksFor(q.TimeHours * GameClock.HourSeconds) : -1;
            if (q.Kind == QuestCatalog.KindDestroy)
            {
                SelectTarget(state, q, r);
            }
            QuestRevision++;
            if (notify)
            {
                FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestNew, QuestLocation(state, r),
                    GameText.Format("quest.notify.new", QuestCatalog.CategoryName(q.Category), QuestCatalog.SourceName(q.Source), QuestCatalog.Title(q)));
                GuidanceHooks.Raise(GuidanceHooks.QuestFirstNew);
            }
        }

        // ── 支线地点（HomeExplorationService 揭开新区域时调用）────────────────

        /// <summary>
        /// 一个领地里的支线地点被揭开（<see cref="Env.HomeExplorationService"/> 的揭开扫描）：按（世界种子, 地点 ID）从支线池抽一种任务，记为“可接取”。
        /// 幂等（地点已有任务就不动）；<paramref name="notify"/> = false 时静默补登（开局探索圆、旧档已探索区域）。
        /// </summary>
        public static ObjectiveRecord OnSideSiteRevealed(CampaignState state, WorldFeature f, bool notify)
        {
            if (state == null || f == null || f.Kind != WorldFeatureKind.SideSite || string.IsNullOrEmpty(f.TerritoryId) || !QuestCatalog.Loaded)
            {
                return null;
            }
            if (FindQuest(state, f.Id) is ObjectiveRecord existing)
            {
                return existing;
            }
            Quest q = PickSide(state, f);
            if (q == null)
            {
                return null;
            }
            ObjectiveRecord r = NewQuestRecord(state, q, f.Id, GameClock.Ticks);
            r.SiteId = f.Id;
            ResolveAnchor(state, q, r, f);
            if (notify)
            {
                FeedbackCues.RaiseLocated(FeedbackCueId.QuestNew, new Vector2(f.X, f.Y),
                    GameText.Format("quest.notify.new_side", QuestCatalog.SourceName(q.Source), QuestCatalog.Title(q), f.X.ToString(CultureInfo.InvariantCulture),
                        f.Y.ToString(CultureInfo.InvariantCulture)));
                GuidanceHooks.Raise(GuidanceHooks.QuestFirstSideFound);
            }
            return r;
        }

        /// <summary>按（世界种子, 地点 ID）的稳定哈希在支线池里按权重抽（与发现先后无关，B25）；“摧毁据点”类在这片领地没有活着的据点时不参与抽取。</summary>
        public static Quest PickSide(CampaignState state, WorldFeature f)
        {
            IReadOnlyList<Quest> pool = QuestCatalog.SideRows;
            int total = 0;
            foreach (Quest q in pool)
            {
                if (SideEligible(state, q, f))
                {
                    total += q.Weight;
                }
            }
            if (total <= 0)
            {
                return null;
            }
            int roll = (int)(QuestCatalog.StableHash(state.World?.WorldSeed ?? state.RandomSeed, f.Id) % (uint)total);
            foreach (Quest q in pool)
            {
                if (!SideEligible(state, q, f))
                {
                    continue;
                }
                if (roll < q.Weight)
                {
                    return q;
                }
                roll -= q.Weight;
            }
            return null;
        }

        private static bool SideEligible(CampaignState state, Quest q, WorldFeature f) =>
            q.Kind != QuestCatalog.KindDestroy || FindTarget(state, q.Param, f.TerritoryId, f.X, f.Y) != null;

        /// <summary>旧档 / 开局探索圆里已经揭开的支线地点：第一次检查时静默补登（只做一次）。</summary>
        private static void EnsureSideBaseline(CampaignState state, QuestState qs)
        {
            if (qs.SideBaselined)
            {
                return;
            }
            if (WorldGenService.ContextFor(state) == null)
            {
                if (state.World == null || state.World.GeneratorVersion < 1)
                {
                    qs.SideBaselined = true; // 原型地形的旧世界没有支线地点
                }
                return;
            }
            qs.SideBaselined = true;
            foreach (ExploredAreaRecord a in state.Grid?.Explored ?? Array.Empty<ExploredAreaRecord>())
            {
                if (a == null || a.Radius <= 0)
                {
                    continue;
                }
                SideScratch.Clear();
                WorldGenQuery.FeaturesIn(state, a.CenterX - a.Radius, a.CenterY - a.Radius, a.CenterX + a.Radius, a.CenterY + a.Radius, SideScratch);
                long r2 = (long)a.Radius * a.Radius;
                foreach (WorldFeature f in SideScratch)
                {
                    long dx = f.X - a.CenterX;
                    long dy = f.Y - a.CenterY;
                    if (f.Kind == WorldFeatureKind.SideSite && dx * dx + dy * dy <= r2)
                    {
                        OnSideSiteRevealed(state, f, notify: false);
                    }
                }
            }
            SideScratch.Clear();
        }

        // ── 地点与目标 ───────────────────────────────────────────────────

        /// <summary>自检用：按任务表解析一条任务的地点（不改存档）。</summary>
        public static bool ResolveAnchorForTests(CampaignState state, string questId, out int x, out int y)
        {
            x = y = 0;
            Quest q = QuestCatalog.Get(questId);
            var r = new ObjectiveRecord { ObjectiveId = questId };
            if (q == null || !ResolveAnchor(state, q, r, null) || !r.HasAnchor)
            {
                return false;
            }
            x = r.AnchorX;
            y = r.AnchorY;
            return true;
        }

        /// <summary>按 fg.TbQuest.anchor 解析任务地点（按种子生成的领地 / 支线地点 / 归还核心，不依赖固定坐标，B25）。解析不出返回 false。</summary>
        private static bool ResolveAnchor(CampaignState state, Quest q, ObjectiveRecord r, WorldFeature site)
        {
            switch (q.Anchor)
            {
                case QuestCatalog.AnchorNone:
                    r.HasAnchor = false;
                    return true;
                case QuestCatalog.AnchorCore:
                {
                    GridCell pivot = HomeGridService.CorePivot(state);
                    SetAnchor(r, pivot.X, pivot.Y);
                    return true;
                }
                case QuestCatalog.AnchorSideSite:
                    if (site == null)
                    {
                        return false;
                    }
                    SetAnchor(r, site.X, site.Y);
                    r.SiteId = site.Id;
                    return true;
                case QuestCatalog.AnchorTerritoryCenter:
                {
                    PlannedTerritory t = WorldGenService.PlanFor(state)?.Find(q.Territory);
                    if (t == null)
                    {
                        return false;
                    }
                    SetAnchor(r, t.CenterX, t.CenterY);
                    r.SiteId = t.Id;
                    return true;
                }
                case QuestCatalog.AnchorBeltOuter:
                {
                    // FG10-REG-01（FGR-GEN-022 / FGR-QST-020）：接车点在外圈危害带外缘再往核心方向 pickup_margin 格（最近一格机器能站的），不进危害带。
                    PlannedTerritory t = WorldGenService.PlanFor(state)?.Find(q.Territory);
                    if (t == null)
                    {
                        return false;
                    }
                    GridCell core = HomeGridService.CorePivot(state);
                    Vector2 c = new Vector2(t.CenterX, t.CenterY);
                    Vector2 dir = new Vector2(core.X, core.Y) - c;
                    dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector2.up;
                    float margin = Env.AcidMarshService.PickupMargin;
                    GridCell cell = default;
                    for (int step = 1; step <= 4; step++)
                    {
                        // 最近的可走格还落在危害带里（带外缘正好是水 / 悬崖）→ 再往外推一个 margin 重找，最多 4 次。
                        Vector2 p = c + dir * (t.OuterRadius + margin * step);
                        cell = Defense.SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), Mathf.Max(8, Mathf.RoundToInt(margin)));
                        if (Env.AcidMarshService.ZoneAt(state, new Vector2(cell.X, cell.Y)) == null)
                        {
                            break;
                        }
                    }
                    SetAnchor(r, cell.X, cell.Y);
                    r.SiteId = t.Id;
                    return true;
                }
                case QuestCatalog.AnchorSideNearest:
                case QuestCatalog.AnchorSideCenter:
                {
                    PlannedTerritory t = WorldGenService.PlanFor(state)?.Find(q.Territory);
                    if (t == null || WorldGenService.ContextFor(state) == null)
                    {
                        return false;
                    }
                    GridCell core = HomeGridService.CorePivot(state);
                    int fx = q.Anchor == QuestCatalog.AnchorSideNearest ? core.X : t.CenterX;
                    int fy = q.Anchor == QuestCatalog.AnchorSideNearest ? core.Y : t.CenterY;
                    SideScratch.Clear();
                    WorldGenQuery.FeaturesIn(state, t.CenterX - t.Radius, t.CenterY - t.Radius, t.CenterX + t.Radius, t.CenterY + t.Radius, SideScratch);
                    WorldFeature best = null;
                    long bestD = long.MaxValue;
                    foreach (WorldFeature f in SideScratch)
                    {
                        if (f.Kind != WorldFeatureKind.SideSite || f.TerritoryId != t.Id)
                        {
                            continue;
                        }
                        long dx = f.X - fx;
                        long dy = f.Y - fy;
                        long d = dx * dx + dy * dy;
                        if (d < bestD || (d == bestD && best != null && string.CompareOrdinal(f.Id, best.Id) < 0))
                        {
                            bestD = d;
                            best = f;
                        }
                    }
                    SideScratch.Clear();
                    if (best != null)
                    {
                        SetAnchor(r, best.X, best.Y);
                        r.SiteId = best.Id;
                    }
                    else
                    {
                        // 这片领地没有支线地点（不应发生，FgTerritorySelfCheck E1 保证每个领地都有）：退到领地中心最近的可走格。
                        GridCell c = Defense.SiegeService.NearestPassable(new GridCell(t.CenterX, t.CenterY), 24);
                        SetAnchor(r, c.X, c.Y);
                        r.SiteId = t.Id;
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        private static void SetAnchor(ObjectiveRecord r, int x, int y)
        {
            r.HasAnchor = true;
            r.AnchorX = x;
            r.AnchorY = y;
        }

        /// <summary>摧毁据点类：在任务出现 / 接取时选定目标（nearest = 这片领地里离地点最近的活着的领地据点；strongest = 等级最高的，同级取近的）。</summary>
        private static void SelectTarget(CampaignState state, Quest q, ObjectiveRecord r)
        {
            string territory = TerritoryOfRecord(state, q, r);
            OutpostRecord o = FindTarget(state, q.Param, territory, r.AnchorX, r.AnchorY);
            r.TargetId = o?.OutpostId;
            r.TargetBaseline = o?.DestroyCount ?? 0;
        }

        private static OutpostRecord FindTarget(CampaignState state, string mode, string territoryId, int x, int y)
        {
            OutpostRecord best = null;
            long bestD = long.MaxValue;
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(state))
            {
                if (o == null || o.Destroyed || o.Kind != WorldOutpostSystem.KindTerritory || o.TerritoryId != territoryId)
                {
                    continue;
                }
                long dx = o.CellX - x;
                long dy = o.CellY - y;
                long d = dx * dx + dy * dy;
                bool better = best == null
                              || (mode == "strongest" ? o.Tier > best.Tier || (o.Tier == best.Tier && d < bestD) : d < bestD)
                              || (d == bestD && (mode != "strongest" || o.Tier == best.Tier) && string.CompareOrdinal(o.OutpostId, best.OutpostId) < 0);
                if (better)
                {
                    best = o;
                    bestD = d;
                }
            }
            return best;
        }

        private static string TerritoryOfRecord(CampaignState state, Quest q, ObjectiveRecord r)
        {
            if (q.Territory != "any")
            {
                return q.Territory;
            }
            return r.HasAnchor ? WorldGenQuery.TerritoryAt(state, r.AnchorX, r.AnchorY)?.Id : null;
        }

        // ── 判定 ─────────────────────────────────────────────────────────

        private static void EvaluateStructural(CampaignState state, long now)
        {
            bool guideOn = GameSettings.HomeGuideQuestsEnabled;
            ObjectiveRecord[] all = state.ObjectiveRecords;
            for (int i = 0; i < all.Length; i++)
            {
                ObjectiveRecord r = all[i];
                if (!IsQuestRecord(r) || r.State != ObjectiveState.Active)
                {
                    continue;
                }
                Quest q = DefOf(r);
                if (q == null || (q.Category == QuestCatalog.CatGuide && !guideOn))
                {
                    if (q != null && r.TrackOrder > 0)
                    {
                        // 家园指引关掉：让出追踪栏的位置。
                        r.TrackOrder = 0;
                        QuestRevision++;
                    }
                    continue;
                }
                bool done;
                switch (q.Kind)
                {
                    case QuestCatalog.KindLeader:
                        done = TerritoryControlService.Find(state, q.Param)?.LeaderDefeated == true;
                        break;
                    case QuestCatalog.KindBuildType:
                        done = CountOperational(state, q.Param) >= q.Count;
                        break;
                    case QuestCatalog.KindBuildBelt:
                        done = BeltCells(state) >= q.Count;
                        break;
                    case QuestCatalog.KindSilentNight:
                        done = SilentNightsSurvived(state) >= q.Count;
                        break;
                    case QuestCatalog.KindOwnOutpost when q.Category == QuestCatalog.CatGuide:
                        done = OwnOutpostNear(state, null);
                        break;
                    default:
                        continue;
                }
                if (done)
                {
                    CompleteQuest(state, q, r, now);
                }
            }
        }

        private static readonly List<ObjectiveRecord> _timedScratch = new List<ObjectiveRecord>();
        private static readonly Comparison<ObjectiveRecord> ByObjectiveId = (x, y) => string.CompareOrdinal(x.ObjectiveId, y.ObjectiveId);

        private static void EvaluateTimed(CampaignState state, long now)
        {
            ResetMachineIndex();
            // 按 ObjectiveId 的稳定顺序判定，不按数组顺序：存档时 ObjectiveRecords 会按 ObjectiveId 重排（CampaignState.NormalizeForSave），
            // 数组顺序因此随“存没存过档”而变；同一次检查里两条护送同时出车时，货运车 LogicId 的分配顺序不能跟着变。
            ObjectiveRecord[] all = state.ObjectiveRecords;
            _timedScratch.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                ObjectiveRecord r = all[i];
                if (IsQuestRecord(r) && r.State == ObjectiveState.Active)
                {
                    _timedScratch.Add(r);
                }
            }
            if (_timedScratch.Count > 1)
            {
                _timedScratch.Sort(ByObjectiveId);
            }
            for (int i = 0; i < _timedScratch.Count; i++)
            {
                ObjectiveRecord r = _timedScratch[i];
                if (r.State != ObjectiveState.Active)
                {
                    continue;
                }
                Quest q = DefOf(r);
                if (q == null || (q.Category == QuestCatalog.CatGuide && !GameSettings.HomeGuideQuestsEnabled))
                {
                    continue;
                }
                switch (q.Kind)
                {
                    case QuestCatalog.KindReach:
                        if (MachineNear(r, ReachRadius) > 0)
                        {
                            CompleteQuest(state, q, r, now);
                        }
                        break;
                    case QuestCatalog.KindHold:
                        if (MachineNear(r, HoldRadius) > 0)
                        {
                            if (r.Stage == 0)
                            {
                                r.Stage = 1;
                                StageReached(state, q, r, now);
                            }
                            r.HoldTicks += Math.Max(1, GameClock.TicksFor(CheckSeconds));
                            QuestRevision++;
                            if (r.HoldTicks >= GameClock.TicksFor(q.Count))
                            {
                                CompleteQuest(state, q, r, now);
                            }
                        }
                        break;
                    case QuestCatalog.KindEscort:
                        EvaluateEscort(state, q, r, now);
                        break;
                    case QuestCatalog.KindDestroy:
                    {
                        OutpostRecord o = string.IsNullOrEmpty(r.TargetId) ? null : WorldOutpostSystem.Find(state, r.TargetId);
                        if (o == null)
                        {
                            FailQuest(state, q, r, now, "quest.fail.target_gone");
                        }
                        else if (o.DestroyCount > r.TargetBaseline)
                        {
                            CompleteQuest(state, q, r, now);
                        }
                        break;
                    }
                    case QuestCatalog.KindOwnOutpost when q.Category != QuestCatalog.CatGuide:
                        if (OwnOutpostNear(state, r))
                        {
                            CompleteQuest(state, q, r, now);
                        }
                        break;
                }
                // 时限（FGR-QST-004）：到期前提前警告，到期后标为失败并说明原因（本次检查里刚完成的不算）。
                if (r.State == ObjectiveState.Active && r.DeadlineTick >= 0)
                {
                    long warnAt = r.DeadlineTick - GameClock.TicksFor(q.WarnHours * GameClock.HourSeconds);
                    if (!r.DeadlineWarned && now >= warnAt && now < r.DeadlineTick)
                    {
                        r.DeadlineWarned = true;
                        QuestRevision++;
                        FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestAlert, QuestLocation(state, r),
                            GameText.Format("quest.notify.warn", QuestCatalog.Title(q), QuestCatalog.FormatRemaining((r.DeadlineTick - now) / (double)GameClock.StepHz)));
                        GuidanceHooks.Raise(GuidanceHooks.QuestFirstTimeWarning);
                    }
                    if (now >= r.DeadlineTick)
                    {
                        FailQuest(state, q, r, now, "quest.fail.expired");
                    }
                }
            }
        }

        private static void EvaluateEscort(CampaignState state, Quest q, ObjectiveRecord r, long now)
        {
            if (r.Stage == 0)
            {
                if (MachineNear(r, ReachRadius) <= 0)
                {
                    return;
                }
                int truck = SpawnEscort(state, r);
                ResetMachineIndex(); // 名册多了一台：本次检查里后面的任务重新建桶
                if (truck <= 0)
                {
                    return; // 寻路网格还没就绪：下一次检查再试
                }
                r.EscortLogicId = truck;
                r.EscortHullReported = 100;
                r.Stage = 1;
                if (QuestCatalog.HasTimeLimit(q) && q.TimerFrom == "stage")
                {
                    r.DeadlineTick = now + GameClock.TicksFor(q.TimeHours * GameClock.HourSeconds);
                    r.DeadlineWarned = false;
                }
                QuestRevision++;
                FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestUpdate, QuestLocation(state, r),
                    GameText.Format("quest.notify.escort", QuestCatalog.Title(q)));
                GuidanceHooks.Raise(GuidanceHooks.QuestFirstEscort);
                return;
            }
            if (!MachineRegistry.TryGetRecord(r.EscortLogicId, out MachineRecord m) || !m.IsAlive)
            {
                FailQuest(state, q, r, now, "quest.fail.escort_destroyed");
                return;
            }
            if (LootPickService.AtDepot(state, m))
            {
                CompleteQuest(state, q, r, now);
                return;
            }
            int hull = m.MaxHealth > 0f ? Mathf.Clamp(Mathf.FloorToInt(m.Health / m.MaxHealth * 100f), 0, 100) : 100;
            if (hull <= r.EscortHullReported - 25)
            {
                r.EscortHullReported = hull / 25 * 25;
                QuestRevision++;
                FeedbackCues.RaiseLocated(FeedbackCueId.QuestAlert, LootPickService.PositionOfMachine(m),
                    GameText.Format("quest.notify.escort_hit", QuestCatalog.Title(q), hull.ToString(CultureInfo.InvariantCulture)));
            }
        }

        /// <summary>护送货运车：搬运轮式（真实的己方机器，进名册、可以下命令，被摧毁 = 任务失败），出现在接车点旁边最近一格机器能站的地方。</summary>
        private static int SpawnEscort(CampaignState state, ObjectiveRecord r)
        {
            int ox = r.AnchorX + Mathf.RoundToInt(EscortSpawnOffset);
            int oy = r.AnchorY;
            GridCell? at = null;
            for (int ring = 0; ring <= 16 && !at.HasValue && Nav.NavService.IsBound; ring++)
            {
                for (int dy = -ring; dy <= ring && !at.HasValue; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        {
                            continue;
                        }
                        if (Nav.NavService.PassableNow(ox + dx, oy + dy, BinGames.Sim.Nav.NavConst.ClassPlayer))
                        {
                            at = new GridCell(ox + dx, oy + dy);
                            break;
                        }
                    }
                }
            }
            // 寻路镜像里找不到可站格（还没加载到那里）：落在支线地点本身（生成器保证地点格可走，FGR-GEN-023），不让任务卡在“等车”。
            at ??= new GridCell(r.AnchorX, r.AnchorY);
            BlueprintRecord bp = null;
            foreach (BlueprintRecord b in state.BlueprintRecords ?? Array.Empty<BlueprintRecord>())
            {
                if (b != null && b.BlueprintId == HomeValleyLayout.BlueprintHaulerId)
                {
                    bp = b;
                    break;
                }
            }
            BlueprintVersionRecord version = null;
            foreach (BlueprintVersionRecord v in bp?.Versions ?? Array.Empty<BlueprintVersionRecord>())
            {
                if (v != null && v.Version == bp.ActiveVersion)
                {
                    version = v;
                }
            }
            float hp = EscortHealth;
            MachineOpResult res = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                new Vector2(at.Value.X, at.Value.Y), hp, hp, "Player", version?.Version ?? 1, version?.CompileSignature);
            if (!res.Success)
            {
                return 0;
            }
            Blueprint.MachineLoadoutRegistry.Register(state, res.LogicId, HomeValleyLayout.BlueprintHaulerId, version?.Version ?? 1);
            MachineRegistry.TryMarkExperience(res.LogicId, "quest_escort");
            return res.LogicId;
        }

        // ── 机器位置的空间桶（每次检查最多建一次：O(机器数)；每个到达 / 停留类任务只看地点附近的桶，O(附近机器数)）──
        private const float MachineBucketCells = 32f;
        private static readonly Dictionary<long, List<int>> MachineBuckets = new Dictionary<long, List<int>>(64);
        private static readonly Stack<List<int>> BucketPool = new Stack<List<int>>(64);
        private static readonly List<int> MachineIds = new List<int>(128);
        private static readonly List<Vector2> MachinePositions = new List<Vector2>(128);
        private static bool _machineIndexBuilt;

        private static long BucketKey(int bx, int by) => ((long)bx << 32) | (uint)by;

        private static void ResetMachineIndex()
        {
            foreach (List<int> l in MachineBuckets.Values)
            {
                l.Clear();
                BucketPool.Push(l);
            }
            MachineBuckets.Clear();
            MachineIds.Clear();
            MachinePositions.Clear();
            _machineIndexBuilt = false;
        }

        private static void BuildMachineIndex()
        {
            _machineIndexBuilt = true;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || m.IsInFactory || LootPickService.RegionOfMachine(m) != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                Vector2 at = LootPickService.PositionOfMachine(m);
                int index = MachineIds.Count;
                MachineIds.Add(m.LogicId);
                MachinePositions.Add(at);
                long key = BucketKey(Mathf.FloorToInt(at.x / MachineBucketCells), Mathf.FloorToInt(at.y / MachineBucketCells));
                if (!MachineBuckets.TryGetValue(key, out List<int> list))
                {
                    list = BucketPool.Count > 0 ? BucketPool.Pop() : new List<int>(4);
                    MachineBuckets[key] = list;
                }
                list.Add(index);
            }
        }

        /// <summary>地点 <paramref name="radius"/> 格内最近的存活己方机器（不含护送的货运车；同距离取 LogicId 小的；没有 = 0）。与 <see cref="LootPickService.NearestMachine"/> 同一口径，按空间桶查。</summary>
        private static int MachineNear(ObjectiveRecord r, float radius)
        {
            if (!r.HasAnchor)
            {
                return 0;
            }
            if (!_machineIndexBuilt)
            {
                BuildMachineIndex();
            }
            var at = new Vector2(r.AnchorX, r.AnchorY);
            int reach = Mathf.CeilToInt(radius / MachineBucketCells);
            int bx = Mathf.FloorToInt(at.x / MachineBucketCells);
            int by = Mathf.FloorToInt(at.y / MachineBucketCells);
            int best = 0;
            float bestD = radius * radius;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (!MachineBuckets.TryGetValue(BucketKey(bx + dx, by + dy), out List<int> list))
                    {
                        continue;
                    }
                    foreach (int i in list)
                    {
                        int id = MachineIds[i];
                        if (id == r.EscortLogicId)
                        {
                            continue;
                        }
                        float d = (MachinePositions[i] - at).sqrMagnitude;
                        if (d < bestD - 1e-4f || (Math.Abs(d - bestD) <= 1e-4f && (best == 0 || id < best)))
                        {
                            bestD = d;
                            best = id;
                        }
                    }
                }
            }
            return best;
        }

        private static void StageReached(CampaignState state, Quest q, ObjectiveRecord r, long now)
        {
            if (QuestCatalog.HasTimeLimit(q) && q.TimerFrom == "stage" && r.DeadlineTick < 0)
            {
                r.DeadlineTick = now + GameClock.TicksFor(q.TimeHours * GameClock.HourSeconds);
            }
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestUpdate, QuestLocation(state, r),
                GameText.Format("quest.notify.progress", QuestCatalog.Title(q), ProgressText(state, r)));
        }

        // ── 结果 ─────────────────────────────────────────────────────────

        private static void CompleteQuest(CampaignState state, Quest q, ObjectiveRecord r, long now)
        {
            if (r.State != ObjectiveState.Active)
            {
                return;
            }
            r.State = ObjectiveState.Completed;
            r.CompletedAtPlaySeconds = state.PlaySeconds;
            r.EndedTick = now;
            r.TrackOrder = 0;
            r.EventId = $"quest_complete:{state.CampaignId}:{r.ObjectiveId}:{r.Attempt}";
            CampaignEventLedger.TryGrant(state, r.EventId, "QuestComplete", state.PlaySeconds, r.DefId);
            state.Quests.Completed++;
            QuestRevision++;
            string reward = QuestCatalog.RewardText(q);
            // 家园指引的奖励在任务日志里领取（不打乱正在进行的经济节奏）；主线 / 支线完成即发。
            if (q.Category != QuestCatalog.CatGuide)
            {
                GrantReward(state, q, r);
            }
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestComplete, QuestLocation(state, r),
                string.IsNullOrEmpty(reward) ? GameText.Format("quest.notify.done_plain", QuestCatalog.Title(q)) : GameText.Format("quest.notify.done", QuestCatalog.Title(q), reward));
            GuidanceHooks.Raise(GuidanceHooks.QuestFirstComplete);
        }

        /// <summary>发奖励：物品进家园（关键材料进核心保管库，不受仓库空间限制，不会丢失）；蓝图记进事件账本。按（任务, 第几次尝试）只发一次。</summary>
        private static void GrantReward(CampaignState state, Quest q, ObjectiveRecord r)
        {
            if (r.RewardClaimed)
            {
                return;
            }
            r.RewardClaimed = true;
            if (!CampaignEventLedger.TryGrant(state, RewardEventId(r.ObjectiveId), "QuestReward", state.PlaySeconds, r.DefId))
            {
                return; // 同一任务的奖励只发一次（主线重新开始后再完成不重复发关键材料）
            }
            if (!string.IsNullOrEmpty(q.RewardItem) && q.RewardItem != QuestCatalog.None && q.RewardAmount > 0)
            {
                HomeInventory.Add(state, q.RewardItem, q.RewardAmount, clampToSpace: false);
            }
            if (!string.IsNullOrEmpty(q.RewardBlueprint) && q.RewardBlueprint != QuestCatalog.None)
            {
                if (CampaignEventLedger.TryGrant(state, BlueprintEventId(q.RewardBlueprint), "QuestBlueprint", state.PlaySeconds, q.Id))
                {
                    Env.AcidMarshService.InvalidateCoating(); // FG10-REG-01：防腐涂层蓝图到手，下一个游戏秒写进星球战斗内核

                    KeyMaterial k = QuestCatalog.KeyMaterialOf(q.RewardBlueprint);
                    FeedbackCues.Raise(FeedbackCueId.QuestUpdate, GameText.Format("quest.notify.blueprint", k != null ? GameText.Get(k.NameKey) : q.RewardBlueprint));
                }
            }
        }

        public static string BlueprintEventId(string blueprintId) => "quest_blueprint:" + blueprintId;

        /// <summary>任务奖励的事件账本 ID（同一任务只发一次）。</summary>
        public static string RewardEventId(string objectiveId) => "quest_reward:" + objectiveId;

        /// <summary>
        /// 关键材料 / 蓝图有没有拿到过（清单的“已获得”列；后续 Story 判断“玩家拿到过这件材料”也读这里）。
        /// 按来源判断，不看当前库存：关键材料本来就要被正式造价用掉（超控阵列 T1～T3 的建筑材料，施工时从核心保管库取走），
        /// 用掉之后仍是“已获得”。物品 = 给出它的任务（fg.TbKeyMaterial.questId）的奖励已记进事件账本，或核心保管库里现在就有（其他来源 / 旧档）；
        /// 蓝图 = 事件账本里有。现存几件另看 <see cref="KeyMaterialStock"/>。
        /// </summary>
        public static bool HasKeyMaterial(CampaignState state, KeyMaterial k)
        {
            if (state == null || k == null)
            {
                return false;
            }
            if (k.Kind == "blueprint")
            {
                return CampaignEventLedger.Contains(state, BlueprintEventId(k.Id));
            }
            return (!string.IsNullOrEmpty(k.QuestId) && CampaignEventLedger.Contains(state, RewardEventId(k.QuestId))) || HomeInventory.Stock(state, k.Id) > 0;
        }

        /// <summary>物品类关键材料现存几件（核心保管库；投入建筑后为 0，拆除退回后恢复）；蓝图返回 -1（不占仓库）。</summary>
        public static int KeyMaterialStock(CampaignState state, KeyMaterial k) =>
            state == null || k == null || k.Kind == "blueprint" ? -1 : HomeInventory.Stock(state, k.Id);

        private static void FailQuest(CampaignState state, Quest q, ObjectiveRecord r, long now, string reasonKey)
        {
            if (r.State != ObjectiveState.Active)
            {
                return;
            }
            r.State = ObjectiveState.Failed;
            r.FailReasonKey = reasonKey;
            r.EndedTick = now;
            r.TrackOrder = 0;
            state.Quests.Failed++;
            QuestRevision++;
            string text = QuestCatalog.IsRestartable(q)
                ? GameText.Format("quest.notify.failed_main", QuestCatalog.Title(q), GameText.Get(reasonKey))
                : GameText.Format("quest.notify.failed", QuestCatalog.Title(q), GameText.Get(reasonKey));
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestAlert, r.HasAnchor ? new Vector2(r.AnchorX, r.AnchorY) : (Vector2?)null, text);
            GuidanceHooks.Raise(GuidanceHooks.QuestFirstFailed);
        }

        // ── 玩家操作（任务日志 / 追踪栏；B06 每个拒绝都有原因）──────────────────

        /// <summary>接取一个“可接取”的支线（FGT-EXP-009“接取”）：开始判定与计时；追踪栏有空位时顺手追踪。</summary>
        public static bool TryAccept(CampaignState state, string objectiveId, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            Quest q = DefOf(r);
            if (q == null)
            {
                return Refuse(out message, "quest.reason.unknown");
            }
            if (r.State != ObjectiveState.Available)
            {
                return Refuse(out message, "quest.reason.accept_not_available");
            }
            // 摧毁据点类：目标在接取时才选定；这片领地的据点已经全被拔掉时不接（否则下一次检查就“目标已不存在”失败）。
            // 任务留在“可接取”：阵营重建据点后可以再接。
            if (q.Kind == QuestCatalog.KindDestroy && FindTarget(state, q.Param, TerritoryOfRecord(state, q, r), r.AnchorX, r.AnchorY) == null)
            {
                return Refuse(out message, "quest.reason.accept_no_target");
            }
            AcceptInternal(state, q, r, GameClock.Ticks, notify: false);
            if (TrackedCount(state) < TrackMax)
            {
                SetTracked(state, r, true);
            }
            message = LastQuestMessage = GameText.Format("quest.notify.progress", QuestCatalog.Title(q), GameText.Get("quest.state.active"));
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestUpdate, QuestLocation(state, r), message);
            GuidanceHooks.Raise(GuidanceHooks.QuestFirstNew);
            return true;
        }

        /// <summary>追踪 / 取消追踪（最多 quest.track_max 个；只能追踪进行中的；OBJ 目标链常驻目标条不占位）。</summary>
        public static bool TrySetTracked(CampaignState state, string objectiveId, bool on, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            if (r == null)
            {
                return Refuse(out message, "quest.reason.unknown");
            }
            if (!IsQuestRecord(r))
            {
                return Refuse(out message, "quest.reason.track_chain");
            }
            if (!on)
            {
                SetTracked(state, r, false);
                message = LastQuestMessage = GameText.Format("quest.msg.untracked", QuestTitle(r));
                return true;
            }
            if (r.State != ObjectiveState.Active || IsHidden(r))
            {
                return Refuse(out message, "quest.reason.track_not_active");
            }
            if (r.TrackOrder > 0)
            {
                message = LastQuestMessage = GameText.Format("quest.msg.tracked", QuestTitle(r));
                return true;
            }
            if (TrackedCount(state) >= TrackMax)
            {
                message = LastQuestMessage = GameText.Format("quest.reason.track_full", TrackMax.ToString(CultureInfo.InvariantCulture));
                return false;
            }
            SetTracked(state, r, true);
            message = LastQuestMessage = GameText.Format("quest.msg.tracked", QuestTitle(r));
            GuidanceHooks.Raise(GuidanceHooks.QuestFirstTrack);
            return true;
        }

        private static void SetTracked(CampaignState state, ObjectiveRecord r, bool on)
        {
            if (!on)
            {
                if (r.TrackOrder > 0)
                {
                    r.TrackOrder = 0;
                    QuestRevision++;
                }
                return;
            }
            int max = 0;
            foreach (ObjectiveRecord o in state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (o != null && o.TrackOrder > max)
                {
                    max = o.TrackOrder;
                }
            }
            r.TrackOrder = max + 1;
            QuestRevision++;
        }

        /// <summary>放弃（只有支线 / 家园指引；主线不能放弃，FGR-QST-003）。护送中的货运车留在你这里。</summary>
        public static bool TryAbandon(CampaignState state, string objectiveId, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            Quest q = DefOf(r);
            if (q == null)
            {
                return Refuse(out message, IsQuestRecord(r) || r == null ? "quest.reason.unknown" : "quest.reason.abandon_main");
            }
            if (!QuestCatalog.CanAbandon(q))
            {
                return Refuse(out message, "quest.reason.abandon_main");
            }
            if (!IsOpen(r))
            {
                return Refuse(out message, "quest.reason.abandon_not_active");
            }
            r.State = ObjectiveState.Abandoned;
            r.EndedTick = GameClock.Ticks;
            r.TrackOrder = 0;
            state.Quests ??= new QuestState();
            state.Quests.Abandoned++;
            QuestRevision++;
            message = LastQuestMessage = GameText.Format("quest.msg.abandoned", QuestCatalog.Title(q));
            FeedbackCues.Raise(FeedbackCueId.QuestUpdate, GameText.Format("quest.notify.abandoned", QuestCatalog.Title(q)));
            return true;
        }

        /// <summary>重新开始一个失败的主线任务（FGR-QST-004“不会卡死剧情”）：回到“去地点”，第几次尝试 +1，地点不变；护送的货运车下次到接车点时重新出现。</summary>
        public static bool TryRestart(CampaignState state, string objectiveId, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            Quest q = DefOf(r);
            if (q == null)
            {
                return Refuse(out message, "quest.reason.unknown");
            }
            if (r.State != ObjectiveState.Failed || !QuestCatalog.IsRestartable(q))
            {
                return Refuse(out message, "quest.reason.restart_not_failed");
            }
            r.Attempt = Math.Max(1, r.Attempt) + 1;
            r.FailReasonKey = null;
            r.EndedTick = -1;
            r.EscortLogicId = 0;
            r.EscortHullReported = 100;
            AcceptInternal(state, q, r, GameClock.Ticks, notify: false);
            state.Quests ??= new QuestState();
            state.Quests.Restarted++;
            if (TrackedCount(state) < TrackMax)
            {
                SetTracked(state, r, true);
            }
            message = LastQuestMessage = GameText.Format("quest.msg.restarted", QuestCatalog.Title(q), r.Attempt.ToString(CultureInfo.InvariantCulture));
            FeedbackCues.RaiseLocatedIfKnown(FeedbackCueId.QuestUpdate, QuestLocation(state, r), GameText.Format("quest.notify.restarted", QuestCatalog.Title(q)));
            return true;
        }

        /// <summary>家园指引的奖励：完成后在任务日志里领取（只领一次）。</summary>
        public static bool TryClaim(CampaignState state, string objectiveId, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            Quest q = DefOf(r);
            if (q == null)
            {
                return Refuse(out message, "quest.reason.unknown");
            }
            if (r.RewardClaimed)
            {
                return Refuse(out message, "quest.reason.claim_done");
            }
            if (r.State != ObjectiveState.Completed)
            {
                return Refuse(out message, "quest.reason.claim_not_done");
            }
            GrantReward(state, q, r);
            QuestRevision++;
            message = LastQuestMessage = GameText.Format("quest.notify.done", QuestCatalog.Title(q), QuestCatalog.RewardText(q));
            FeedbackCues.Raise(FeedbackCueId.QuestUpdate, message);
            return true;
        }

        public static bool CanClaim(ObjectiveRecord r) => r != null && r.State == ObjectiveState.Completed && !r.RewardClaimed && IsGuide(r) && QuestCatalog.RewardText(DefOf(r)).Length > 0;

        /// <summary>让镜头飞到任务的位置（与点通知定位同一入口 <see cref="NotificationCenter.LocateHandler"/>）。</summary>
        public static bool TryFlyTo(CampaignState state, string objectiveId, out string message)
        {
            ObjectiveRecord r = FindQuest(state, objectiveId);
            if (r == null)
            {
                return Refuse(out message, "quest.reason.unknown");
            }
            Vector2? at = QuestLocation(state, r);
            if (!at.HasValue)
            {
                return Refuse(out message, "quest.msg.fly_none");
            }
            string fail = null;
            bool ok = NotificationCenter.LocateHandler != null && NotificationCenter.LocateHandler(HomeValleyLayout.RegionId, new Vector3(at.Value.x, 0f, at.Value.y), out fail);
            LastFlyTarget = at.Value;
            message = LastQuestMessage = ok ? GameText.Format("quest.msg.fly_ok", QuestTitle(r)) : GameText.Get(string.IsNullOrEmpty(fail) ? "quest.msg.fly_none" : fail);
            return ok;
        }

        /// <summary>最近一次“飞过去”的目标位置（自检读；没有镜头的环境下 LocateHandler 为空时也记）。</summary>
        public static Vector2 LastFlyTarget { get; private set; }

        private static int _focusCursor;

        /// <summary>快捷键（FocusQuest，默认 Alt+Q）：镜头依次飞到追踪栏里的任务。</summary>
        public static bool FocusNextTracked(CampaignState state, out string message)
        {
            var list = new List<ObjectiveRecord>(4);
            TrackedQuests(state, list);
            if (list.Count == 0)
            {
                message = LastQuestMessage = InputDisplay.ExpandActionTokens(GameText.Get("quest.msg.no_tracked"));
                return false;
            }
            _focusCursor = (_focusCursor % list.Count + list.Count) % list.Count;
            ObjectiveRecord r = list[_focusCursor];
            _focusCursor = (_focusCursor + 1) % list.Count;
            return TryFlyTo(state, r.ObjectiveId, out message);
        }

        public static void SetTrackerCollapsed(CampaignState state, bool collapsed)
        {
            if (state == null)
            {
                return;
            }
            state.Quests ??= new QuestState();
            if (state.Quests.TrackerCollapsed != collapsed)
            {
                state.Quests.TrackerCollapsed = collapsed;
                QuestRevision++;
            }
        }

        private static bool Refuse(out string message, string key)
        {
            message = LastQuestMessage = InputDisplay.ExpandActionTokens(GameText.Get(key));
            return false;
        }

        // ── 进度 / 位置 / 时限文字（面板与追踪栏同一来源）────────────────────

        public static string ProgressText(CampaignState state, ObjectiveRecord r)
        {
            Quest q = DefOf(r);
            if (q == null)
            {
                return string.Empty;
            }
            switch (q.Kind)
            {
                case QuestCatalog.KindLeader:
                    bool down = TerritoryControlService.Find(state, q.Param)?.LeaderDefeated == true;
                    return GameText.Format("quest.prog.leader", TerritoryControlService.TerritoryName(state, q.Param), GameText.Get(down ? "quest.prog.leader_down" : "quest.prog.leader_alive"));
                case QuestCatalog.KindBuildType:
                    return GameText.Format("quest.prog.count", Math.Min(q.Count, CountOperational(state, q.Param)).ToString(CultureInfo.InvariantCulture), q.Count.ToString(CultureInfo.InvariantCulture));
                case QuestCatalog.KindBuildBelt:
                    return GameText.Format("quest.prog.count", Math.Min(q.Count, BeltCells(state)).ToString(CultureInfo.InvariantCulture), q.Count.ToString(CultureInfo.InvariantCulture));
                case QuestCatalog.KindSilentNight:
                    return GameText.Format("quest.prog.silent_night", Math.Min(q.Count, SilentNightsSurvived(state)).ToString(CultureInfo.InvariantCulture), q.Count.ToString(CultureInfo.InvariantCulture));
                case QuestCatalog.KindReach:
                    return GameText.Get("quest.prog.reach");
                case QuestCatalog.KindHold:
                {
                    int secs = (int)Math.Min(q.Count, r.HoldTicks / Math.Max(1, GameClock.StepHz));
                    return GameText.Format("quest.prog.hold", secs.ToString(CultureInfo.InvariantCulture), q.Count.ToString(CultureInfo.InvariantCulture));
                }
                case QuestCatalog.KindEscort:
                    if (r.Stage == 0 || r.EscortLogicId <= 0 || !MachineRegistry.TryGetRecord(r.EscortLogicId, out MachineRecord m) || !m.IsAlive)
                    {
                        return GameText.Get("quest.prog.escort_go");
                    }
                    int hull = m.MaxHealth > 0f ? Mathf.Clamp(Mathf.FloorToInt(m.Health / m.MaxHealth * 100f), 0, 100) : 100;
                    return GameText.Format("quest.prog.escort_drive", MachineNaming.Short(m), hull.ToString(CultureInfo.InvariantCulture),
                        Mathf.RoundToInt(DepotDistance(state, LootPickService.PositionOfMachine(m))).ToString(CultureInfo.InvariantCulture));
                case QuestCatalog.KindDestroy:
                {
                    OutpostRecord o = string.IsNullOrEmpty(r.TargetId) ? null : WorldOutpostSystem.Find(state, r.TargetId);
                    if (o == null || r.State == ObjectiveState.Completed)
                    {
                        return GameText.Get("quest.prog.destroy_done");
                    }
                    return GameText.Format("quest.prog.destroy", TerritoryControlService.OutpostName(o), o.Tier.ToString(CultureInfo.InvariantCulture));
                }
                case QuestCatalog.KindOwnOutpost:
                    return q.Category == QuestCatalog.CatGuide ? QuestCatalog.Goal(q)
                        : GameText.Format("quest.prog.outpost", Mathf.RoundToInt(OutpostRadius).ToString(CultureInfo.InvariantCulture));
                default:
                    return string.Empty;
            }
        }

        public static string WhereText(CampaignState state, ObjectiveRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            if (!IsQuestRecord(r))
            {
                ObjectiveDef def = CampaignObjectiveCatalog.Get(r.ObjectiveId);
                return def != null ? CampaignObjectiveCatalog.RegionDisplayName(def.RegionId) : string.Empty;
            }
            Quest q = DefOf(r);
            if (q == null || !r.HasAnchor)
            {
                return GameText.Get("quest.detail.where_none");
            }
            string x = r.AnchorX.ToString(CultureInfo.InvariantCulture);
            string y = r.AnchorY.ToString(CultureInfo.InvariantCulture);
            switch (q.Anchor)
            {
                case QuestCatalog.AnchorCore:
                    return GameText.Get("quest.where.core");
                case QuestCatalog.AnchorTerritoryCenter:
                    return GameText.Format("quest.where.territory", TerritoryControlService.TerritoryName(state, q.Territory), x, y);
                default:
                {
                    GridCell core = HomeGridService.CorePivot(state);
                    int dist = Mathf.RoundToInt(Mathf.Sqrt((r.AnchorX - core.X) * (float)(r.AnchorX - core.X) + (r.AnchorY - core.Y) * (float)(r.AnchorY - core.Y)));
                    string terr = TerritoryOfRecord(state, q, r);
                    return GameText.Format("quest.where.site", terr != null ? TerritoryControlService.TerritoryName(state, terr) : string.Empty, x, y,
                        dist.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>时限：没有时限 = null；否则剩余游戏秒（负数 = 已经到期）。</summary>
        public static double? RemainingSeconds(ObjectiveRecord r)
        {
            if (r == null || r.DeadlineTick < 0 || r.State != ObjectiveState.Active)
            {
                return null;
            }
            return (r.DeadlineTick - GameClock.Ticks) / (double)Math.Max(1, GameClock.StepHz);
        }

        public static bool IsWarningTime(ObjectiveRecord r)
        {
            Quest q = DefOf(r);
            double? left = RemainingSeconds(r);
            return q != null && left.HasValue && left.Value <= q.WarnHours * GameClock.HourSeconds;
        }

        // ── 权威字段的只读查询 ─────────────────────────────────────────────

        private static int CountOperational(CampaignState state, string typeId)
        {
            int n = 0;
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == typeId && b.ConstructionState == BuildingConstructionState.Operational)
                {
                    n++;
                }
            }
            return n;
        }

        private static int BeltCells(CampaignState state)
        {
            if (ReferenceEquals(CampaignSession.Current, state) && Logistics.BeltNetworkService.Kernel != null)
            {
                return Logistics.BeltNetworkService.Kernel.CellCount;
            }
            return state.Belts?.CellCount ?? 0;
        }

        private static int SilentNightsSurvived(CampaignState state)
        {
            SilentNightState sn = state.Weather?.SilentNight;
            if (sn == null)
            {
                return 0;
            }
            return Math.Max(0, sn.Count - (sn.Active ? 1 : 0));
        }

        private static bool OwnOutpostNear(CampaignState state, ObjectiveRecord r)
        {
            float r2 = OutpostRadius * OutpostRadius;
            foreach (OwnOutpostRecord o in OwnOutpostService.All(state))
            {
                if (o == null || o.Intact <= 0)
                {
                    continue;
                }
                if (r == null || !r.HasAnchor)
                {
                    return true;
                }
                float dx = o.X - r.AnchorX;
                float dy = o.Y - r.AnchorY;
                if (dx * dx + dy * dy <= r2)
                {
                    return true;
                }
            }
            return false;
        }

        private static float DepotDistance(CampaignState state, Vector2 at)
        {
            LootPickService.CollectDepotPositions(state, DepotScratch);
            float best = float.MaxValue;
            foreach (Vector2 d in DepotScratch)
            {
                best = Mathf.Min(best, (d - at).magnitude);
            }
            DepotScratch.Clear();
            return best == float.MaxValue ? 0f : best;
        }

        private static ObjectiveRecord[] Append(ObjectiveRecord[] arr, ObjectiveRecord r)
        {
            arr ??= Array.Empty<ObjectiveRecord>();
            var next = new ObjectiveRecord[arr.Length + 1];
            Array.Copy(arr, next, arr.Length);
            next[arr.Length] = r;
            return next;
        }

        private static void RemoveRecord(CampaignState state, ObjectiveRecord r)
        {
            ObjectiveRecord[] arr = state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>();
            int idx = Array.IndexOf(arr, r);
            if (idx < 0)
            {
                return;
            }
            var next = new ObjectiveRecord[arr.Length - 1];
            Array.Copy(arr, 0, next, 0, idx);
            Array.Copy(arr, idx + 1, next, idx, arr.Length - idx - 1);
            state.ObjectiveRecords = next;
        }

        // ── 读档对账（DEBT-FG0SAVE01-07 任务类：游戏更新删掉某个任务定义）────────

        /// <summary>引用已不存在的任务定义的进行中 / 可接取任务：标为失败（原因“游戏更新移除了这个任务”），让出追踪栏；返回处理条数。</summary>
        public static int ReconcileRemovedQuests(CampaignState state, List<string> removedTitles)
        {
            if (state?.ObjectiveRecords == null || !QuestCatalog.Loaded)
            {
                return 0;
            }
            int n = 0;
            foreach (ObjectiveRecord r in state.ObjectiveRecords)
            {
                if (!IsQuestRecord(r) || QuestCatalog.Get(r.DefId) != null || !IsOpen(r))
                {
                    continue;
                }
                r.State = ObjectiveState.Failed;
                r.FailReasonKey = "quest.fail.content_removed";
                r.TrackOrder = 0;
                r.EndedTick = GameClock.Ticks;
                removedTitles?.Add(r.DefId);
                n++;
                if (state.Quests != null)
                {
                    state.Quests.Failed++; // 统计面板的失败数也算上（B19）
                }
            }
            if (n > 0)
            {
                QuestRevision++;
            }
            return n;
        }

        /// <summary>统计面板（B19）：任务 · 完成 / 失败 / 放弃 / 重新开始 / 进行中。没有任何任务记录时不出这一段。</summary>
        public static void BuildStatsLines(CampaignState state, List<(string Text, string Cls)> into)
        {
            QuestState qs = state?.Quests;
            if (qs == null || into == null)
            {
                return;
            }
            int open = 0;
            bool any = false;
            foreach (ObjectiveRecord r in state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
            {
                if (IsQuestRecord(r) && !IsHidden(r))
                {
                    any = true;
                    if (r.State == ObjectiveState.Active)
                    {
                        open++;
                    }
                }
            }
            if (!any)
            {
                return;
            }
            into.Add((GameText.Get("stats.quest.title"), "st-row-title"));
            into.Add((GameText.Format("stats.quest.row", qs.Completed.ToString(CultureInfo.InvariantCulture), qs.Failed.ToString(CultureInfo.InvariantCulture),
                qs.Abandoned.ToString(CultureInfo.InvariantCulture), qs.Restarted.ToString(CultureInfo.InvariantCulture), open.ToString(CultureInfo.InvariantCulture)), null));
        }

        /// <summary>自检用：在指定支线地点上按指定定义登记一个“可接取”的支线（跳过按哈希抽取，用来逐种覆盖；其余与 <see cref="OnSideSiteRevealed"/> 相同）。</summary>
        public static ObjectiveRecord OfferSideForTests(CampaignState state, WorldFeature f, string defId)
        {
            Quest q = QuestCatalog.Get(defId);
            if (state == null || f == null || q == null || FindQuest(state, f.Id) != null)
            {
                return FindQuest(state, f?.Id);
            }
            ObjectiveRecord r = NewQuestRecord(state, q, f.Id, GameClock.Ticks);
            r.SiteId = f.Id;
            ResolveAnchor(state, q, r, f);
            return r;
        }

        /// <summary>自检用：清掉会话内的光标 / 统计（不碰存档）。</summary>
        public static void ResetQuestSession()
        {
            _focusCursor = 0;
            LastQuestMessage = string.Empty;
            ResetQuestStats();
            QuestRevision++;
        }
    }
}
