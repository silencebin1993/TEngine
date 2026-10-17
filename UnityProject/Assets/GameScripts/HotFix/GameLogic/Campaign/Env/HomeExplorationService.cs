using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG7-ENV-05（FG07 FGR-ENV-050 迷雾、051 家园遗迹点、060 区域危害；验收 FGT-ENV-007）：家园探索。
    ///
    /// - **迷雾**是已探索圆的并集（<see cref="GridState.Explored"/>，连续空间，不是地格）：信号塔 / 中继塔接上网络时把覆盖圆记为已探索
    ///   （<see cref="Signal.SignalCoverageService"/>，覆盖半径变大时按新半径重记），家园机器沿走过的路按半径采样（<see cref="MachineExploration"/>）。
    ///   迷雾只拦建造与情报，不拦机器走路；世界无限，迷雾外的区块在被揭开 / 被查询时才由流式加载器生成（FGR-GEN-050，FG0-ARCH-05 / FG3-GEN-01 已交付）。
    /// - **遗迹点**的位置由世界生成器按（种子, 分布格）决定（<see cref="WorldFeatures"/>，B25）；本服务按离核心的距离把它们分进距离环（fg.TbRelicRing），
    ///   每次追加已探索圆时（<see cref="OnAreaRevealed"/>，唯一入口 <see cref="HomeGridService.RevealArea"/>）找出新被揭开的遗迹点，记进存档并发通知；
    ///   玩家派机器回收（复用“拆解”工单：<see cref="HomeValleyWorkOrders.TryCreateRelicRecovery"/>），完工时按距离环发技术数据（经济账本）和一条背景资料。
    /// - **区域危害**：fg.TbHazard 把每个阵营领地映射到一种危害（外圈危害带同一种）；地图标记 / 图例 / 悬停说明效果与应对办法，第一次揭开时发通知。
    ///   效果数值由各阵营 Story 接机制（表的 storyId 列）；在那之前悬停明说“只作标记”。
    ///
    /// 全部状态在 <see cref="GridState.Exploration"/>（唯一写入口本类）。开销：追加一个已探索圆 O(圆覆盖的分布格 + 领地数)，只在真正揭开新区域时发生；
    /// 悬停 / 右键查遗迹点 O(1～4 个分布格)（点位查询按分布格缓存）。与是否被观察无关（揭开发生在世界模拟步里）。
    /// </summary>
    public static class HomeExplorationService
    {
        public const string RelicPrefix = "relic:";
        public const string TxPrefix = "relic-recover:";
        /// <summary>FG8-GEN-02：读取旧终端的技术数据事务号前缀（统计面板“读取旧终端”行，TechDataFlow.Terminal）。</summary>
        public const string TerminalTxPrefix = "poi-read:";
        public const string NotifyRelicFound = "relic_found";
        public const string NotifyRelicRecovered = "relic_recovered";
        public const string NotifyHazardFound = "hazard_found";
        /// <summary>FG8-GEN-02：资源缓存 / 旧终端（生成器点位，cache:分布格X:分布格Y / term:…）。与遗迹点同一条“发现 → 右键派机器 → 完工发奖励”的流程。</summary>
        public const string CachePrefix = WorldFeatures.CachePrefix;
        public const string TerminalPrefix = WorldFeatures.TerminalPrefix;
        public const string NotifyPoiFound = "poi_found";

        /// <summary>状态变化计数（地图 / 世界标记据此重画）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>自检读：发现 / 通知 / 回收 / 危害通知次数，最近一次揭开处理耗时。</summary>
        public static int FoundCount { get; private set; }
        public static int RelicNotices { get; private set; }
        public static int HazardNotices { get; private set; }
        public static int RecoveredCount { get; private set; }
        /// <summary>FG8-GEN-02：发现资源缓存 / 旧终端的通知次数、打开 / 读取完工次数。</summary>
        public static int PoiNotices { get; private set; }
        public static int PoiDoneCount { get; private set; }
        public static double LastRevealMs { get; private set; }
        public static double MaxRevealMs { get; private set; }

        private static readonly List<RelicRing> RingRows = new List<RelicRing>(4);
        private static readonly List<Hazard> HazardRows = new List<Hazard>(4);
        private static bool _loaded;
        private static readonly List<WorldFeature> Scratch = new List<WorldFeature>(32);

        // ── 表 ────────────────────────────────────────────────────────────────

        public static IReadOnlyList<RelicRing> Rings
        {
            get
            {
                EnsureLoaded();
                return RingRows;
            }
        }

        public static IReadOnlyList<Hazard> Hazards
        {
            get
            {
                EnsureLoaded();
                return HazardRows;
            }
        }

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
        }

        public static void ResetForTests()
        {
            _loaded = false;
            FoundCount = RelicNotices = HazardNotices = RecoveredCount = PoiNotices = PoiDoneCount = 0;
            LastRevealMs = MaxRevealMs = 0;
            Revision++;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            RingRows.Clear();
            HazardRows.Clear();
            try
            {
                TbRelicRing rings = ConfigSystem.Instance?.Tables?.TbRelicRing;
                if (rings != null)
                {
                    RingRows.AddRange(rings.DataList);
                }
                TbHazard hazards = ConfigSystem.Instance?.Tables?.TbHazard;
                if (hazards != null)
                {
                    HazardRows.AddRange(hazards.DataList);
                }
            }
            catch (Exception e)
            {
                Log.Error("[HomeExplorationService] 读取 fg.TbRelicRing / fg.TbHazard 失败：" + e.Message);
            }
            RingRows.Sort((a, b) => a.MinDistance.CompareTo(b.MinDistance));
            HazardRows.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            if (RingRows.Count == 0)
            {
                Log.Warning("[HomeExplorationService] fg.TbRelicRing 缺失：遗迹点不能回收。");
            }
        }

        /// <summary>离核心 <paramref name="distance"/> 格的遗迹点所在的距离环（最后一个最近距离不超过它的环）。</summary>
        public static RelicRing RingFor(int distance)
        {
            EnsureLoaded();
            RelicRing best = null;
            foreach (RelicRing r in RingRows)
            {
                if (distance >= r.MinDistance)
                {
                    best = r;
                }
            }
            return best ?? (RingRows.Count > 0 ? RingRows[0] : null);
        }

        private static float Tuning(string id, float fallback)
        {
            return GridContent.TryGetTuning(id, out float v) ? v : fallback;
        }

        // ── 状态 ──────────────────────────────────────────────────────────────

        public static HomeExplorationState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Grid?.Exploration == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Grid.Exploration;
        }

        public static bool IsRelicId(string id) => id != null && id.StartsWith(RelicPrefix, StringComparison.Ordinal);

        /// <summary>FG8-GEN-02：资源缓存或旧终端的点位 ID。</summary>
        public static bool IsCacheOrTerminalId(string id) =>
            id != null && (id.StartsWith(CachePrefix, StringComparison.Ordinal) || id.StartsWith(TerminalPrefix, StringComparison.Ordinal));

        /// <summary>可以派机器去“回收”的点位（遗迹点、资源缓存、旧终端）：工单复用拆解种类、目标 = 点位 ID，按前缀分流到本类。</summary>
        public static bool IsPoiId(string id) => IsRelicId(id) || IsCacheOrTerminalId(id);

        public static bool IsFound(CampaignState s, string relicId) =>
            Array.IndexOf((IsCacheOrTerminalId(relicId) ? StateOf(s)?.PoisFound : StateOf(s)?.RelicsFound) ?? Array.Empty<string>(), relicId) >= 0;

        public static bool IsRecovered(CampaignState s, string relicId) =>
            Array.IndexOf((IsCacheOrTerminalId(relicId) ? StateOf(s)?.PoisDone : StateOf(s)?.RelicsRecovered) ?? Array.Empty<string>(), relicId) >= 0;

        public static bool HazardSeen(CampaignState s, string hazardId) => Array.IndexOf(StateOf(s)?.HazardsSeen ?? Array.Empty<string>(), hazardId) >= 0;

        /// <summary>
        /// 第一次揭开新区域之前调用（幂等）：已经在已探索圆里的遗迹点、已经被揭开的危害记为“已发现”，不发通知——
        /// 新战役的开局探索圆、FG7-ENV-05 之前的旧档（已经探索过的地方）都不补发通知。
        /// </summary>
        public static void EnsureBaseline(CampaignState s)
        {
            HomeExplorationState st = StateOf(s);
            if (st == null || st.Baselined)
            {
                return;
            }
            // 生成器世界的上下文还没就绪（新战役初始化格网时世界状态可能还没写好）：先不补登，等第一次揭开新区域时再补（原型地形的旧世界没有点位，直接记为已补登）。
            if (WorldGenService.ContextFor(s) == null && (s.World == null || s.World.GeneratorVersion >= 1))
            {
                return;
            }
            st.Baselined = true;
            foreach (ExploredAreaRecord a in s.Grid.Explored ?? Array.Empty<ExploredAreaRecord>())
            {
                if (a != null && a.Radius > 0)
                {
                    Scan(s, st, a.CenterX, a.CenterY, a.Radius, notify: false);
                }
            }
            Revision++;
        }

        // ── 揭开新区域（FGR-ENV-050 / 051 / 060）────────────────────────────────

        /// <summary>
        /// <see cref="HomeGridService.RevealArea"/> 追加了一个已探索圆（圆心格 + 半径）之后调用：新被揭开的遗迹点记进存档并发通知（一次揭开很多时，
        /// 逐个发前 relic.notify_max 条，其余合成一条“又发现 N 个”；同类通知本来也会聚合），第一次揭开的危害发通知。
        /// </summary>
        public static void OnAreaRevealed(CampaignState s, int cx, int cy, int radius)
        {
            HomeExplorationState st = StateOf(s);
            if (st == null || radius <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Scan(s, st, cx, cy, radius, notify: true);
            LastRevealMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastRevealMs > MaxRevealMs)
            {
                MaxRevealMs = LastRevealMs;
            }
        }

        private static void Scan(CampaignState s, HomeExplorationState st, int cx, int cy, int radius, bool notify)
        {
            int limit = Math.Max(1, (int)Math.Round(Tuning("relic.notify_max", 6f)));
            int posted = 0;
            int more = 0;
            int postedPoi = 0;
            int morePoi = 0;
            if (WorldGenService.ContextFor(s) != null)
            {
                Scratch.Clear();
                WorldGenQuery.FeaturesIn(s, cx - radius, cy - radius, cx + radius, cy + radius, Scratch);
                long r2 = (long)radius * radius;
                foreach (WorldFeature f in Scratch)
                {
                    if (f.Kind == WorldFeatureKind.Cache || f.Kind == WorldFeatureKind.Terminal)
                    {
                        // FG8-GEN-02：资源缓存 / 旧终端——揭开时记为已发现，通知（同类聚合）写名字、位置、怎么下令。
                        long px = f.X - cx;
                        long py = f.Y - cy;
                        if (px * px + py * py > (long)radius * radius || Array.IndexOf(st.PoisFound, f.Id) >= 0)
                        {
                            continue;
                        }
                        st.PoisFound = Append(st.PoisFound, f.Id);
                        Revision++;
                        if (notify)
                        {
                            FoundCount++;
                            if (postedPoi < limit)
                            {
                                postedPoi++;
                                PoiNotices++;
                                NotificationCenter.Post(NotifyPoiFound,
                                    GameText.Format(f.Kind == WorldFeatureKind.Cache ? "cache.notify.found" : "terminal.notify.found",
                                        GameText.Get(f.Kind == WorldFeatureKind.Cache ? "world.poi.cache" : "world.poi.terminal"), f.X, f.Y, f.Distance),
                                    new Vector3(f.X, 0f, f.Y));
                            }
                            else
                            {
                                morePoi++;
                            }
                            GuidanceHooks.Raise(GuidanceHooks.WorldFirstPoiFound);
                        }
                        continue;
                    }
                    if (f.Kind != WorldFeatureKind.Relic)
                    {
                        continue;
                    }
                    long dx = f.X - cx;
                    long dy = f.Y - cy;
                    if (dx * dx + dy * dy > r2 || Array.IndexOf(st.RelicsFound, f.Id) >= 0)
                    {
                        continue;
                    }
                    st.RelicsFound = Append(st.RelicsFound, f.Id);
                    // 静默补登的（开局探索圆、旧档已探索区域）记为第 0 步“本来就知道”：补登结果只取决于已探索圆，与补登在哪一刻触发
                    // （第一次揭开新区域 / 打开地图算可回收数）无关——存档不因玩家是否打开过地图而不同（FGR-BASE-021）。
                    st.RelicsFoundTicks = Append(st.RelicsFoundTicks, notify ? GameClock.Ticks : 0L);
                    Revision++;
                    if (!notify)
                    {
                        continue;
                    }
                    FoundCount++;
                    if (posted < limit)
                    {
                        posted++;
                        RelicNotices++;
                        RelicRing ring = RingFor(f.Distance);
                        NotificationCenter.Post(NotifyRelicFound,
                            GameText.Format("relic.notify.found", RingName(ring), f.X, f.Y, f.Distance), new Vector3(f.X, 0f, f.Y));
                    }
                    else
                    {
                        more++;
                    }
                    GuidanceHooks.Raise(GuidanceHooks.EnvFirstRelicFound);
                }
                Scratch.Clear();
            }
            if (more > 0)
            {
                RelicNotices++;
                NotificationCenter.Post(NotifyRelicFound, GameText.Format("relic.notify.found_more", more), new Vector3(cx, 0f, cy));
            }
            if (morePoi > 0)
            {
                // 资源缓存 / 旧终端与遗迹点各自计数、各自合成（“又发现 N 个遗迹点”里不混进缓存与终端）。
                PoiNotices++;
                NotificationCenter.Post(NotifyPoiFound, GameText.Format("poi.notify.found_more", morePoi), new Vector3(cx, 0f, cy));
            }
            ScanHazards(s, st, cx, cy, radius, notify);
            // FG8-GEN-02（FGR-GEN-040）：揭开的范围伸进外延区时，那里的零散前哨按需登记成据点（“从第 0 步起一直在休眠”，与揭开先后无关）。
            WorldGenService.RegisterRevealedOuter(s, cx, cy, radius);
        }

        private static void ScanHazards(CampaignState s, HomeExplorationState st, int cx, int cy, int radius, bool notify)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
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
                Hazard h = HazardForTerritory(t.Id);
                if (h == null || Array.IndexOf(st.HazardsSeen, h.Id) >= 0)
                {
                    continue;
                }
                long dx = t.CenterX - cx;
                long dy = t.CenterY - cy;
                long reach = (long)radius + t.OuterRadius;
                if (dx * dx + dy * dy > reach * reach)
                {
                    continue;
                }
                st.HazardsSeen = Append(st.HazardsSeen, h.Id);
                Revision++;
                if (!notify)
                {
                    continue;
                }
                HazardNotices++;
                // 通知位置：已探索圆上离领地中心最近的点（玩家点通知时镜头飞到危害区边上）。
                double d = Math.Sqrt(dx * dx + dy * dy);
                double k = d > 1e-6 ? Math.Min(1.0, radius / d) : 0.0;
                var at = new Vector3((float)(cx + dx * k), 0f, (float)(cy + dy * k));
                NotificationCenter.Post(NotifyHazardFound, GameText.Format("hazard.notify.found", HazardTitleZone(h, t), GameText.Get(h.EffectKey), GameText.Get(h.CounterKey)), at);
                GuidanceHooks.Raise(GuidanceHooks.EnvFirstHazardSeen);
            }
        }

        // ── 遗迹点查询 ────────────────────────────────────────────────────────

        public struct RelicInfo
        {
            public string Id;
            public int X;
            public int Y;
            public int Distance;
            public RelicRing Ring;
            public bool Explored;
            public bool Recovered;
            public WorkOrderRecord Order;
            /// <summary>FG8-GEN-02：点位种类（遗迹点 / 资源缓存 / 旧终端）与所在领地的模板（领地之外为 null）。</summary>
            public WorldFeatureKind Kind;
            public TerritoryTemplate Template;
            /// <summary>机器到场后要干多少游戏秒、完工发多少技术数据 / 废料。</summary>
            public float Seconds;
            public int TechData;
            public int Scrap;
        }

        /// <summary>按点位 ID（relic:分布格X:分布格Y）取遗迹点：重算这个分布格（纯函数 + 缓存），不存在返回 false。</summary>
        public static bool TryGetRelic(CampaignState s, string relicId, out RelicInfo info)
        {
            info = default;
            if (s == null || !IsPoiId(relicId))
            {
                return false;
            }
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            if (ctx == null)
            {
                return false;
            }
            // 重算这个分布格（纯函数 + 缓存）；ID 与点位种类都要对得上。
            WorldFeature relic = WorldFeatures.FindById(ctx, relicId);
            if (relic == null || (relic.Kind != WorldFeatureKind.Relic && relic.Kind != WorldFeatureKind.Cache && relic.Kind != WorldFeatureKind.Terminal))
            {
                return false;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            TerritoryTemplate tpl = relic.TerritoryId != null ? ctx.Plan?.TemplateFor(relic.TerritoryId) : null;
            info = new RelicInfo
            {
                Id = relic.Id,
                X = relic.X,
                Y = relic.Y,
                Distance = relic.Distance,
                Ring = relic.Kind == WorldFeatureKind.Relic ? RingFor(relic.Distance) : null,
                Explored = map.IsExploredNoLoad(new GridCell(relic.X, relic.Y)),
                Recovered = IsRecovered(s, relic.Id),
                Order = ActiveOrder(s, relic.Id),
                Kind = relic.Kind,
                Template = tpl,
            };
            Rewards(ref info);
            return true;
        }

        /// <summary>FG8-GEN-02：点位的工期与奖励——遗迹点按距离环；缓存按所在领地的模板（外延区：基数 + 每千格加成，越远越多；领地之间：scatter.cache_scrap）；终端按模板。</summary>
        private static void Rewards(ref RelicInfo info)
        {
            switch (info.Kind)
            {
                case WorldFeatureKind.Cache:
                    if (info.Template != null)
                    {
                        info.Scrap = Math.Max(1, info.Template.CacheScrap);
                        info.Seconds = Math.Max(0.1f, info.Template.CacheSeconds);
                    }
                    else
                    {
                        float baseScrap = Tuning("outer.cache_scrap_base", 80f);
                        float per = Tuning("outer.cache_scrap_per_1000", 40f);
                        info.Scrap = Math.Max(1, Mathf.RoundToInt(baseScrap + per * info.Distance / 1000f));
                        info.Seconds = Math.Max(0.1f, Tuning("outer.cache_seconds", 14f));
                    }
                    break;
                case WorldFeatureKind.Terminal:
                    info.TechData = Math.Max(1, info.Template?.TerminalTech ?? 1);
                    info.Seconds = Math.Max(0.1f, info.Template?.TerminalSeconds ?? 16f);
                    break;
                default:
                    info.TechData = Math.Max(0, info.Ring?.TechData ?? 0);
                    info.Seconds = Math.Max(0.1f, info.Ring?.RecoverSeconds ?? 0.1f);
                    break;
            }
        }

        /// <summary>点位的显示名：遗迹点 = 距离环名；资源缓存 / 旧终端 = 种类名。</summary>
        public static string PoiName(in RelicInfo info) =>
            info.Kind == WorldFeatureKind.Cache ? GameText.Get("world.poi.cache")
            : info.Kind == WorldFeatureKind.Terminal ? GameText.Get("world.poi.terminal")
            : RingName(info.Ring);

        public static Vector2 PositionOf(CampaignState s, string relicId) =>
            TryGetRelic(s, relicId, out RelicInfo info) ? new Vector2(info.X, info.Y) : HomeValleyLayout.Core.Position;

        /// <summary>离 <paramref name="p"/> 最近、在 <paramref name="radius"/> 格内、已经探索过的遗迹点（右键 / 悬停用；迷雾里的不返回，不泄露情报）。
        /// <paramref name="skipRecovered"/>：跳过已回收的（右键派工用——已回收的灰碑不是工作目标，旁边还有没回收的就取那一处；悬停不跳过，照样显示“已回收 + 背景资料”）。</summary>
        public static string RelicAt(CampaignState s, Vector2 p, float radius = -1f, bool skipRecovered = false)
        {
            if (s == null || WorldGenService.ContextFor(s) == null)
            {
                return null;
            }
            if (radius <= 0f)
            {
                radius = Tuning("relic.pick_radius", 2.5f);
            }
            int r = Mathf.CeilToInt(radius);
            Scratch.Clear();
            WorldGenQuery.FeaturesIn(s, Mathf.FloorToInt(p.x) - r, Mathf.FloorToInt(p.y) - r, Mathf.CeilToInt(p.x) + r, Mathf.CeilToInt(p.y) + r, Scratch);
            HomeGridMap map = HomeGridService.MapFor(s);
            string best = null;
            float bestD = radius * radius;
            foreach (WorldFeature f in Scratch)
            {
                if (f.Kind != WorldFeatureKind.Relic && f.Kind != WorldFeatureKind.Cache && f.Kind != WorldFeatureKind.Terminal)
                {
                    continue;
                }
                float dx = f.X - p.x;
                float dy = f.Y - p.y;
                float d2 = dx * dx + dy * dy;
                if (d2 <= bestD && map.IsExploredNoLoad(new GridCell(f.X, f.Y)) && !(skipRecovered && IsRecovered(s, f.Id)))
                {
                    bestD = d2;
                    best = f.Id;
                }
            }
            Scratch.Clear();
            return best;
        }

        /// <summary>这处遗迹进行中的回收单（拆解工单、目标 = 遗迹点 ID）；没有返回 null。</summary>
        public static WorkOrderRecord ActiveOrder(CampaignState s, string relicId)
        {
            WorkOrderRecord[] orders = s?.WorkOrders;
            if (orders == null)
            {
                return null;
            }
            foreach (WorkOrderRecord o in orders)
            {
                if (o != null && o.Kind == WorkOrderKind.Salvage && o.TargetId == relicId
                    && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed)
                {
                    return o;
                }
            }
            return null;
        }

        public static string RingName(RelicRing ring) => ring != null ? GameText.Get(ring.NameKey) : GameText.Get("world.poi.relic");

        /// <summary>背景资料：按遗迹点 ID 的稳定哈希挑距离环里的一条（同一遗迹点永远同一条，与读档 / 平台无关）。</summary>
        public static string LoreFor(RelicInfo info)
        {
            string source = info.Kind == WorldFeatureKind.Terminal ? info.Template?.TerminalLoreKeys : info.Ring?.LoreKeys;
            string[] keys = source?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (keys.Length == 0)
            {
                return string.Empty;
            }
            uint h = 2166136261u;
            foreach (char c in info.Id ?? string.Empty)
            {
                h = (h ^ c) * 16777619u;
            }
            return GameText.Get(keys[(int)(h % (uint)keys.Length)].Trim());
        }

        /// <summary>地图图标的标签：环名 + 状态（已回收 / 回收中）。</summary>
        public static string MapLabel(CampaignState s, WorldFeature f, out bool recovered)
        {
            recovered = IsRecovered(s, f.Id);
            if (f.Kind == WorldFeatureKind.Cache || f.Kind == WorldFeatureKind.Terminal)
            {
                string poi = GameText.Get(f.Kind == WorldFeatureKind.Cache ? "world.poi.cache" : "world.poi.terminal");
                if (recovered)
                {
                    return GameText.Format(f.Kind == WorldFeatureKind.Cache ? "ui.map.cache.opened" : "ui.map.terminal.read", poi);
                }
                return ActiveOrder(s, f.Id) != null ? GameText.Format("ui.map.relic.ordered", poi) : poi;
            }
            string name = RingName(RingFor(f.Distance));
            if (recovered)
            {
                return GameText.Format("ui.map.relic.recovered", name);
            }
            return ActiveOrder(s, f.Id) != null ? GameText.Format("ui.map.relic.ordered", name) : name;
        }

        /// <summary>悬停 / 地图面板：标题“环名（x, y）”，正文写距离、能不能回收（工期、技术数据）/ 谁在回收 / 已回收与背景资料、怎么下令。</summary>
        public static bool TryDescribeRelic(CampaignState s, string relicId, out string title, out string body)
        {
            title = body = null;
            if (!TryGetRelic(s, relicId, out RelicInfo info) || !info.Explored)
            {
                return false;
            }
            title = GameText.Format("relic.hover.title", PoiName(info), info.X, info.Y);
            var sb = new StringBuilder(160);
            sb.Append(GameText.Format("relic.hover.distance", info.Distance));
            sb.Append('\n');
            if (info.Kind == WorldFeatureKind.Cache || info.Kind == WorldFeatureKind.Terminal)
            {
                bool cache = info.Kind == WorldFeatureKind.Cache;
                if (info.Recovered)
                {
                    sb.Append(cache ? GameText.Format("cache.hover.opened", info.Scrap) : GameText.Format("terminal.hover.read", info.TechData, LoreFor(info)));
                }
                else if (info.Order != null)
                {
                    string who = info.Order.AssignedMachineLogicId > 0
                        ? GameText.Format("relic.hover.ordered_machine", MachineNaming.Short(info.Order.AssignedMachineLogicId))
                        : GameText.Get("relic.hover.ordered_pool");
                    sb.Append(GameText.Format("relic.hover.ordered", who));
                }
                else
                {
                    sb.Append(cache ? GameText.Format("cache.hover.available", Num(info.Seconds), info.Scrap) : GameText.Format("terminal.hover.available", Num(info.Seconds), info.TechData));
                    sb.Append('\n').Append(GameText.Get("cache.hover.hint"));
                }
                body = sb.ToString();
                return true;
            }
            if (info.Recovered)
            {
                sb.Append(GameText.Format("relic.hover.recovered", info.Ring?.TechData ?? 0, LoreFor(info)));
            }
            else if (info.Order != null)
            {
                string who = info.Order.AssignedMachineLogicId > 0
                    ? GameText.Format("relic.hover.ordered_machine", MachineNaming.Short(info.Order.AssignedMachineLogicId))
                    : GameText.Get("relic.hover.ordered_pool");
                sb.Append(GameText.Format("relic.hover.ordered", who));
            }
            else
            {
                sb.Append(GameText.Format("relic.hover.available", Num(info.Ring?.RecoverSeconds ?? 0f), info.Ring?.TechData ?? 0));
                sb.Append('\n').Append(GameText.Get("relic.hover.hint"));
            }
            body = sb.ToString();
            return true;
        }

        // ── 回收（FGR-ENV-051“探索后可以派机器回收”）─────────────────────────────

        /// <summary>能不能下回收单：能返回 null，否则返回给玩家看的原因（迷雾 / 已回收 / 已有人在回收 / 没有遗迹点）。</summary>
        public static string CheckRecoverable(CampaignState s, string relicId, out RelicInfo info)
        {
            if (!TryGetRelic(s, relicId, out info))
            {
                return GameText.Get("relic.reason.missing");
            }
            if (!info.Explored)
            {
                return GameText.Get("relic.reason.fog");
            }
            if (info.Recovered)
            {
                return GameText.Get("relic.reason.recovered");
            }
            if (info.Kind == WorldFeatureKind.Relic && info.Ring == null)
            {
                return GameText.Get("relic.reason.missing");
            }
            return null;
        }

        /// <summary>
        /// 玩家下令回收：<paramref name="machineLogicId"/> &gt; 0 = 指定这台机器（右键），0 = 进待分配池由空闲的搬运机领（战略地图“派空闲机器回收”）。
        /// 成功时确认音 + 文字；拒绝时返回带原因的失败（原因文字 = <see cref="HomeValleyWorkOrders.DescribeCommandFailure"/>，拒绝音由调用方发：右键走下令模板，地图面板自己发）。
        /// </summary>
        public static HomeValleyWorkOrders.WorkOrderOpResult OrderRecovery(CampaignState s, string relicId, int machineLogicId)
        {
            string reason = CheckRecoverable(s, relicId, out RelicInfo info);
            if (reason == null && info.Order != null && (machineLogicId == 0 || info.Order.State != WorkOrderState.Ready))
            {
                reason = GameText.Get("relic.reason.ordered");
            }
            if (reason != null)
            {
                return HomeValleyWorkOrders.WorkOrderOpResult.Fail(HomeValleyWorkOrders.GridFailurePrefix + reason);
            }
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateRelicRecovery(s, relicId, machineLogicId, Math.Max(0.1f, info.Seconds));
            if (r.Success)
            {
                Revision++;
                string name = PoiName(info);
                string machineKey = info.Kind == WorldFeatureKind.Cache ? "cache.order.machine" : info.Kind == WorldFeatureKind.Terminal ? "terminal.order.machine" : "relic.order.machine";
                FeedbackCues.Raise(FeedbackCueId.CommandAck, machineLogicId > 0
                    ? GameText.Format(machineKey, MachineNaming.Short(machineLogicId), name)
                    : GameText.Format("relic.order.pool", name));
            }
            return r;
        }

        /// <summary>FG7-ENV-05（B02 批量）：已发现、已探索、没回收、也没人在回收的遗迹点（按发现先后）。O(发现过的遗迹点数)，只在地图刷新（按版本号 + 节流缓存）/ 点按钮时算。
        /// 审查修复（P2）：先补登（幂等）——旧档读回后还没揭开过新区域时，已探索区域里的遗迹点也算进来，与地图图标 / 右键单个回收的说法一致。</summary>
        public static List<string> RecoverableRelics(CampaignState s)
        {
            var list = new List<string>();
            HomeExplorationState st = StateOf(s);
            if (st == null)
            {
                return list;
            }
            EnsureBaseline(s);
            foreach (string id in st.RelicsFound)
            {
                if (IsRelicId(id) && CheckRecoverable(s, id, out RelicInfo info) == null && info.Order == null)
                {
                    list.Add(id);
                }
            }
            return list;
        }

        /// <summary>FG7-ENV-05（B02 批量）：给全部可回收的遗迹点各下一张待分配的回收单（空闲的搬运机依次领走）。返回下了几张。</summary>
        public static int OrderAllRecoveries(CampaignState s)
        {
            int n = 0;
            foreach (string id in RecoverableRelics(s))
            {
                string reason = CheckRecoverable(s, id, out RelicInfo info);
                if (reason == null && HomeValleyWorkOrders.TryCreateRelicRecovery(s, id, 0, Math.Max(0.1f, info.Seconds)).Success)
                {
                    n++;
                }
            }
            if (n > 0)
            {
                Revision++;
                FeedbackCues.Raise(FeedbackCueId.CommandAck, GameText.Format("relic.order.pool_all", n));
            }
            return n;
        }

        /// <summary>
        /// 回收单完工（<see cref="HomeValleyWorkOrders"/> 的拆解完工分支调用）：记为已回收，按距离环发技术数据（经济账本生产事务，事务号 relic-recover:ID，
        /// 天然幂等：同一处遗迹只入账一次），通知写技术数据与背景资料。返回发了多少技术数据（重复完工 / 遗迹不存在返回 0）。
        /// </summary>
        public static int CompleteRecovery(CampaignState s, WorkOrderRecord order)
        {
            HomeExplorationState st = StateOf(s);
            if (st == null || order == null || !TryGetRelic(s, order.TargetId, out RelicInfo info) || info.Recovered)
            {
                return 0;
            }
            if (info.Kind == WorldFeatureKind.Cache || info.Kind == WorldFeatureKind.Terminal)
            {
                return CompletePoi(s, st, info);
            }
            st.RelicsRecovered = Append(st.RelicsRecovered, info.Id);
            st.RelicsRecoveredTicks = Append(st.RelicsRecoveredTicks, GameClock.Ticks);
            int amount = Math.Max(0, info.Ring?.TechData ?? 0);
            string tx = TxPrefix + info.Id;
            if (amount > 0 && CampaignEconomyLedger.Find(s, tx) == null)
            {
                CampaignEconomyLedger.ProposeProduce(s, tx, info.Id, CampaignEconomyLedger.ResourceTechData, amount);
                CampaignEconomyLedger.Reserve(s, tx);
                CampaignEconomyLedger.MarkRunning(s, tx);
                if (CampaignEconomyLedger.Commit(s, tx).Success)
                {
                    st.TechData += amount;
                    HomeInventory.Touch();
                }
            }
            RecoveredCount++;
            Revision++;
            NotificationCenter.Post(NotifyRelicRecovered,
                GameText.Format("relic.notify.recovered", RingName(info.Ring), info.X, info.Y, amount, LoreFor(info)), new Vector3(info.X, 0f, info.Y));
            FeedbackCues.Raise(FeedbackCueId.AnalysisComplete);
            GuidanceHooks.Raise(GuidanceHooks.EnvFirstRelicRecovered);
            return amount;
        }

        /// <summary>
        /// FG8-GEN-02：资源缓存 / 旧终端完工。缓存：废料放在缓存原地成为地面物（事务号 = 点位 ID，天然幂等），开系统搬运单进待分配池
        /// （与风暴残骸、拆除返还同一套：仓库满时等待，腾出空间自动继续）；终端：技术数据入账（事务号 poi-read:ID）+ 一条记录。返回发了多少（废料或技术数据）。
        /// </summary>
        private static int CompletePoi(CampaignState s, HomeExplorationState st, RelicInfo info)
        {
            st.PoisDone = Append(st.PoisDone, info.Id);
            PoiDoneCount++;
            Revision++;
            string name = PoiName(info);
            if (info.Kind == WorldFeatureKind.Cache)
            {
                int amount = Math.Max(1, info.Scrap);
                GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(info.X, info.Y), CampaignEconomyLedger.ResourceScrap, amount, info.Id);
                if (g != null)
                {
                    HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
                }
                st.CacheScrap += amount;
                NotificationCenter.Post(NotifyRelicRecovered, GameText.Format("cache.notify.opened", name, info.X, info.Y, amount), new Vector3(info.X, 0f, info.Y));
                FeedbackCues.Raise(FeedbackCueId.Pickup);
                return amount;
            }
            int tech = Math.Max(0, info.TechData);
            string tx = TerminalTxPrefix + info.Id;
            if (tech > 0 && CampaignEconomyLedger.Find(s, tx) == null)
            {
                CampaignEconomyLedger.ProposeProduce(s, tx, info.Id, CampaignEconomyLedger.ResourceTechData, tech);
                CampaignEconomyLedger.Reserve(s, tx);
                CampaignEconomyLedger.MarkRunning(s, tx);
                if (CampaignEconomyLedger.Commit(s, tx).Success)
                {
                    st.TerminalTechData += tech;
                    HomeInventory.Touch();
                }
            }
            NotificationCenter.Post(NotifyRelicRecovered, GameText.Format("terminal.notify.read", name, info.X, info.Y, tech, LoreFor(info)), new Vector3(info.X, 0f, info.Y));
            FeedbackCues.Raise(FeedbackCueId.AnalysisComplete);
            return tech;
        }

        /// <summary>FG8-GEN-02：工单显示名（“回收遗迹 / 打开缓存 / 读取终端”）。</summary>
        public static string WorkLabel(string targetId) =>
            targetId != null && targetId.StartsWith(CachePrefix, StringComparison.Ordinal) ? GameText.Get("cache.work.label")
            : targetId != null && targetId.StartsWith(TerminalPrefix, StringComparison.Ordinal) ? GameText.Get("terminal.work.label")
            : GameText.Get("relic.work.label");

        // ── 区域危害（FGR-ENV-060）──────────────────────────────────────────────

        public static Hazard HazardForTerritory(string territoryId)
        {
            EnsureLoaded();
            foreach (Hazard h in HazardRows)
            {
                if (h.TerritoryId == territoryId)
                {
                    return h;
                }
            }
            return null;
        }

        public static Hazard HazardForBelt(string beltKind)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(beltKind) || beltKind == "none")
            {
                return null;
            }
            foreach (Hazard h in HazardRows)
            {
                if (h.BeltKind == beltKind)
                {
                    return h;
                }
            }
            return null;
        }

        /// <summary>阵营领地在地图上显不显示：本幕及以前的阵营领地，或者已经揭开过它的危害（玩家亲自探索到了）。白潮滩头预留区不在这里（FGR-GEN-041）。</summary>
        public static bool TerritoryVisible(CampaignState s, PlannedTerritory t)
        {
            if (t == null || !t.IsFaction)
            {
                return false;
            }
            int act = Math.Max(1, s?.Progress?.Act ?? 1);
            if (t.Act <= act)
            {
                return true;
            }
            Hazard h = HazardForTerritory(t.Id);
            return h != null && HazardSeen(s, h.Id);
        }

        /// <summary>格子所在的危害（先看外圈危害带，再看领地范围）；<paramref name="visibleOnly"/> 时只算地图上显示的领地。没有返回 null。</summary>
        public static Hazard HazardAt(CampaignState s, int x, int y, bool visibleOnly, out PlannedTerritory territory, out bool belt)
        {
            territory = null;
            belt = false;
            WorldPlan plan = WorldGenService.PlanFor(s);
            if (plan == null)
            {
                return null;
            }
            PlannedTerritory bt = plan.HazardAt(x, y);
            if (bt != null && bt.IsFaction && (!visibleOnly || TerritoryVisible(s, bt)))
            {
                Hazard hb = HazardForBelt(bt.HazardKind);
                if (hb != null)
                {
                    territory = bt;
                    belt = true;
                    return hb;
                }
            }
            PlannedTerritory t = plan.TerritoryAt(x, y);
            if (t != null && t.IsFaction && (!visibleOnly || TerritoryVisible(s, t)))
            {
                // FG8-GEN-02（FGR-GEN-023；DEBT-FG7ENV05-02）：有领地模板的世界（v3 起），领地里只有模板分布的危害区才算危害；
                // 没有模板的旧世界（v1 / v2）仍按整块领地标记（与此前一致）。
                if (plan.HasTemplates && plan.TemplateFor(t.Id) != null)
                {
                    PlannedHazardZone z = plan.HazardZoneAt(x, y);
                    if (z == null || z.TerritoryId != t.Id)
                    {
                        return null;
                    }
                }
                Hazard h = HazardForTerritory(t.Id);
                if (h != null)
                {
                    territory = t;
                    return h;
                }
            }
            return null;
        }

        public static string HazardTitleZone(Hazard h, PlannedTerritory t) =>
            GameText.Format("hazard.hover.title_zone", GameText.Get(h.NameKey), t != null ? GameText.Get(t.NameKey) : string.Empty);

        /// <summary>悬停说明（地图上指着危害区）：标题 + 效果 + 应对办法 + “只作标记”的说明（效果由阵营 Story 接机制之前）。</summary>
        public static bool TryDescribeHazard(CampaignState s, int x, int y, out string title, out string body)
        {
            title = body = null;
            Hazard h = HazardAt(s, x, y, visibleOnly: true, out PlannedTerritory t, out bool belt);
            if (h == null)
            {
                return false;
            }
            title = belt
                ? GameText.Format("hazard.hover.title_belt", GameText.Get(t.HazardKey), GameText.Get(t.NameKey))
                : HazardTitleZone(h, t);
            body = GameText.Format("hazard.hover.effect", GameText.Get(h.EffectKey)) + "\n"
                   + GameText.Format("hazard.hover.counter", GameText.Get(h.CounterKey)) + "\n"
                   + GameText.Get("hazard.hover.pending");
            return true;
        }

        public static Color HazardColor(Hazard h)
        {
            return h != null && ColorUtility.TryParseHtmlString(h.Color, out Color c) ? c : new Color(0.92f, 0.45f, 0.35f, 1f);
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

        private static string Num(float v) => v.ToString(v == Mathf.Round(v) ? "0" : "0.#", CultureInfo.InvariantCulture);

        private static string[] Append(string[] a, string v)
        {
            a ??= Array.Empty<string>();
            var n = new string[a.Length + 1];
            Array.Copy(a, n, a.Length);
            n[a.Length] = v;
            return n;
        }

        private static long[] Append(long[] a, long v)
        {
            a ??= Array.Empty<long>();
            var n = new long[a.Length + 1];
            Array.Copy(a, n, a.Length);
            n[a.Length] = v;
            return n;
        }
    }
}
