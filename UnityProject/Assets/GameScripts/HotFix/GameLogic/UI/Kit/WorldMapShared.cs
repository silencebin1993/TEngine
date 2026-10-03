using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>战略地图 / 小地图的图层（筛选按钮一一对应；FGR-GEN-080“图标化、按类型筛选”，FGR-GEN-081“同样的标记和筛选”）。</summary>
    public enum WorldMapLayer
    {
        Territory = 0,
        Outposts = 1,
        Relics = 2,
        Resources = 3,
        Own = 4,
        Groups = 5,
        Markers = 6,
        Signal = 7,
    }

    /// <summary>地图筛选（战略地图与小地图共用，会话内有效）。</summary>
    public static class WorldMapFilters
    {
        public static readonly WorldMapLayer[] All =
        {
            WorldMapLayer.Territory, WorldMapLayer.Outposts, WorldMapLayer.Relics, WorldMapLayer.Resources,
            WorldMapLayer.Own, WorldMapLayer.Groups, WorldMapLayer.Markers, WorldMapLayer.Signal,
        };

        private static readonly bool[] On = { true, true, true, true, true, true, true, true };

        public static int Revision { get; private set; } = 1;

        public static bool IsOn(WorldMapLayer layer) => On[(int)layer];

        public static void Set(WorldMapLayer layer, bool on)
        {
            if (On[(int)layer] != on)
            {
                On[(int)layer] = on;
                Revision++;
            }
        }

        public static void Toggle(WorldMapLayer layer) => Set(layer, !IsOn(layer));

        public static void ResetAll()
        {
            for (int i = 0; i < On.Length; i++)
            {
                On[i] = true;
            }
            Revision++;
        }

        public static string TextKey(WorldMapLayer layer)
        {
            switch (layer)
            {
                case WorldMapLayer.Territory: return "ui.map.filter.territory";
                case WorldMapLayer.Outposts: return "ui.map.filter.outposts";
                case WorldMapLayer.Relics: return "ui.map.filter.relics";
                case WorldMapLayer.Resources: return "ui.map.filter.resources";
                case WorldMapLayer.Own: return "ui.map.filter.own";
                case WorldMapLayer.Groups: return "ui.map.filter.groups";
                case WorldMapLayer.Markers: return "ui.map.filter.markers";
                default: return "ui.map.filter.signal";
            }
        }
    }

    public enum WorldMapItemKind
    {
        Home,
        Outpost,
        Relic,
        Resource,
        Group,
        Marker,
        /// <summary>领地名（圆心处的小圈 + 名字；范围圈由矢量层画）。</summary>
        Territory,
        /// <summary>己方建筑群 / 前哨站（归还核心所在的那一群由 <see cref="Home"/> 代表）。</summary>
        OwnCluster,
        /// <summary>FG5-RND-05：突袭预报（箭尾在来袭方向上，标签写阵营与规模；箭身由矢量层画，指向归还核心）。</summary>
        RaidForecast,
    }

    /// <summary>地图上的一个图标。</summary>
    public struct WorldMapItem
    {
        public WorldMapItemKind Kind;
        public WorldMapLayer Layer;
        public string Id;
        public double X;
        public double Y;
        public string Label;
    }

    /// <summary>地图上的圆（领地范围 + 危害带、信号覆盖）。</summary>
    public struct WorldMapCircle
    {
        public WorldMapLayer Layer;
        public double X;
        public double Y;
        public float Radius;
        public float OuterRadius;
        public string Label;
        public bool Connected;
    }

    /// <summary>地图上的线（突袭路线：队伍 → 目标）。</summary>
    public struct WorldMapLine
    {
        public double X0;
        public double Y0;
        public double X1;
        public double Y1;
        /// <summary>FG5-RND-05：突袭预报箭头（红色粗线，终点画箭头），不是队伍路线。</summary>
        public bool Forecast;
    }

    /// <summary>地图视图：中心（格）+ 横向半宽（格）+ 画布像素尺寸。格 ↔ 画布像素（画布 y 向下，格 +Y = 北 = 向上）。</summary>
    public struct WorldMapView
    {
        public double CenterX;
        public double CenterY;
        public double HalfWidth;
        public float CanvasWidth;
        public float CanvasHeight;

        public double HalfHeight => CanvasWidth > 1f ? HalfWidth * CanvasHeight / CanvasWidth : HalfWidth;
        public double MinX => CenterX - HalfWidth;
        public double MaxX => CenterX + HalfWidth;
        public double MinY => CenterY - HalfHeight;
        public double MaxY => CenterY + HalfHeight;
        public double CellsPerCanvasPixel => CanvasWidth > 1f ? 2.0 * HalfWidth / CanvasWidth : 1.0;

        public Vector2 ToCanvas(double x, double y)
        {
            float px = (float)((x - MinX) / (2.0 * HalfWidth) * CanvasWidth);
            float py = (float)(CanvasHeight - (y - MinY) / (2.0 * HalfHeight) * CanvasHeight);
            return new Vector2(px, py);
        }

        public Vector2 ToCell(Vector2 canvas)
        {
            double x = MinX + canvas.x / Math.Max(1f, CanvasWidth) * 2.0 * HalfWidth;
            double y = MinY + (CanvasHeight - canvas.y) / Math.Max(1f, CanvasHeight) * 2.0 * HalfHeight;
            return new Vector2((float)x, (float)y);
        }

        public bool Contains(double x, double y, double margin = 0) => x >= MinX - margin && x <= MaxX + margin && y >= MinY - margin && y <= MaxY + margin;
    }

    /// <summary>
    /// FG3-GEN-01：一次地图内容收集（图标 + 圆 + 线），战略地图与小地图共用。只读正式数据（规划层、据点、行进队伍、信号覆盖、
    /// 玩家标记、生成器点位、己方建筑群），迷雾外的东西不显示（据点 / 遗迹 / 资源 / 行进中的突袭只在已探索区域），白潮滩头预留区揭示前不显示（FGR-GEN-041）。
    /// 开销 O(视野里的分布格 + 据点数 + 队伍数 + 标记数 + 覆盖源数 + 建筑群数)，由面板按真实时间节流调用，不在每帧。
    /// </summary>
    public sealed class WorldMapModel
    {
        public readonly List<WorldMapItem> Items = new List<WorldMapItem>(64);
        public readonly List<WorldMapCircle> Circles = new List<WorldMapCircle>(16);
        public readonly List<WorldMapLine> Lines = new List<WorldMapLine>(8);
        private readonly List<WorldFeature> _features = new List<WorldFeature>(32);
        private readonly List<IntelRecord> _forecasts = new List<IntelRecord>(4);

        public void Collect(CampaignState state, in WorldMapView view, int maxIcons)
        {
            Items.Clear();
            Circles.Clear();
            Lines.Clear();
            if (state == null)
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            double margin = view.HalfWidth * 0.1;
            GridCell core = HomeGridService.CorePivot(state);

            if (WorldMapFilters.IsOn(WorldMapLayer.Territory))
            {
                WorldPlan plan = WorldGenService.PlanFor(state);
                int act = Math.Max(1, state.Progress?.Act ?? 1);
                if (plan != null)
                {
                    foreach (PlannedTerritory t in plan.Territories)
                    {
                        // 阵营领地按幕显示（任务与情报引导玩家按幕推进，FGR-GEN-022）；白潮滩头预留区揭示前没有任何提示（FGR-GEN-041）。
                        if (!t.IsFaction || t.Act > act)
                        {
                            continue;
                        }
                        string label = GameText.Format("ui.map.territory_label", GameText.Get(t.NameKey));
                        Circles.Add(new WorldMapCircle
                        {
                            Layer = WorldMapLayer.Territory, X = t.CenterX, Y = t.CenterY, Radius = t.Radius, OuterRadius = t.OuterRadius, Label = label,
                        });
                        if (view.Contains(t.CenterX, t.CenterY, margin))
                        {
                            Add(WorldMapItemKind.Territory, WorldMapLayer.Territory, "territory:" + t.Id, t.CenterX, t.CenterY, label);
                        }
                    }
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Signal))
            {
                int n = SignalCoverageService.SourceCount;
                for (int i = 0; i < n && i < 128; i++)
                {
                    if (SignalCoverageService.TryGetSource(i, out Vector2 c, out float r, out _, out bool connected))
                    {
                        Circles.Add(new WorldMapCircle { Layer = WorldMapLayer.Signal, X = c.x, Y = c.y, Radius = r, OuterRadius = r, Connected = connected });
                    }
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Own))
            {
                Add(WorldMapItemKind.Home, WorldMapLayer.Own, "home", core.X, core.Y, GameText.Get("ui.map.home"));
                // FGR-GEN-080“己方建筑群与前哨”：按聚合好的建筑群出图标（O(群数)；聚合只在建筑增删时重做，见 WorldMapOwnClusters）。
                foreach (WorldMapOwnClusters.Cluster c in WorldMapOwnClusters.For(state))
                {
                    if (c.ContainsCore || !view.Contains(c.X, c.Y, margin))
                    {
                        continue;
                    }
                    string label = GameText.Format(c.IsOutpost ? "ui.map.own_outpost" : "ui.map.own_cluster", c.Count);
                    Add(WorldMapItemKind.OwnCluster, WorldMapLayer.Own, c.Id, c.X, c.Y, label);
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Groups))
            {
                foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
                {
                    // 与据点 / 遗迹同一条迷雾规则：队伍当前所在格已探索才显示（图标与路线都不泄露迷雾里的行进）。
                    // 行进途中的“发现 / 拦截”状态由 FG6 的突袭 Story 接管后改读正式的发现状态（FG06“行进途中可以被发现、被拦截”）。
                    if (g == null || !map.IsExploredNoLoad(new GridCell((int)Math.Round(g.PosX), (int)Math.Round(g.PosY))))
                    {
                        continue;
                    }
                    Lines.Add(new WorldMapLine { X0 = g.PosX, Y0 = g.PosY, X1 = g.TargetX, Y1 = g.TargetY });
                    if (view.Contains(g.PosX, g.PosY, margin))
                    {
                        Add(WorldMapItemKind.Group, WorldMapLayer.Groups, g.GroupId, g.PosX, g.PosY, GameText.Get("ui.map.raid"));
                    }
                }
                // FG5-RND-05（FGR-RND-051“突袭预报会在地图上标出来袭方向”）：有效的突袭预报画一支从来袭方向指向归还核心的箭头——
                // 部队还在迷雾里也画（这正是情报的价值），只画方向、不画部队位置。O(有效预报数)。
                Campaign.Economy.IntelService.CollectArrows(state, _forecasts);
                foreach (IntelRecord r in _forecasts)
                {
                    Vector2 tail = Campaign.Economy.IntelService.ArrowTail(state, r);
                    Vector2 head = Campaign.Economy.IntelService.ArrowHead(state, r);
                    Lines.Add(new WorldMapLine { X0 = tail.x, Y0 = tail.y, X1 = head.x, Y1 = head.y, Forecast = true });
                    if (view.Contains(tail.x, tail.y, margin))
                    {
                        Add(WorldMapItemKind.RaidForecast, WorldMapLayer.Groups, "forecast:" + r.Serial, tail.x, tail.y,
                            GameText.Format("ui.map.raid_forecast", WorldTransitSystem.OriginName(r.Faction), r.Units));
                    }
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Outposts))
            {
                foreach (OutpostRecord o in WorldOutpostSystem.Outposts(state))
                {
                    if (o == null || !view.Contains(o.CellX, o.CellY, margin) || !map.IsExploredNoLoad(new GridCell(o.CellX, o.CellY)))
                    {
                        continue;
                    }
                    string label = o.Kind == "scout_nest" ? GameText.Format("world.poi.scout_nest", Math.Max(1, o.Tier)) : GameText.Get("ui.map.filter.outposts");
                    Add(WorldMapItemKind.Outpost, WorldMapLayer.Outposts, o.OutpostId, o.CellX, o.CellY, label);
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Relics) && WorldGenService.ContextFor(state) != null)
            {
                _features.Clear();
                // 视野太大时只查已探索范围的外接矩形（分布格数有上限），远处未探索的本来也不显示。
                (int minX, int minY, int maxX, int maxY) q = ClampToExplored(map, view, margin);
                if (q.maxX >= q.minX && q.maxY >= q.minY)
                {
                    WorldGenQuery.FeaturesIn(state, q.minX, q.minY, q.maxX, q.maxY, _features);
                }
                foreach (WorldFeature f in _features)
                {
                    if (f.Kind == WorldFeatureKind.Relic && map.IsExploredNoLoad(new GridCell(f.X, f.Y)))
                    {
                        Add(WorldMapItemKind.Relic, WorldMapLayer.Relics, f.Id, f.X, f.Y, GameText.Get("world.poi.relic"));
                    }
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Resources))
            {
                StartGuaranteeReport report = WorldGenService.PlanFor(state)?.StartReport;
                if (report != null)
                {
                    foreach (StartGuaranteeItem i in report.Items)
                    {
                        int sx = i.SiteX + i.MinSquare / 2;
                        int sy = i.SiteY + i.MinSquare / 2;
                        if (i.Satisfied && view.Contains(sx, sy, margin) && map.IsExploredNoLoad(new GridCell(sx, sy)))
                        {
                            Add(WorldMapItemKind.Resource, WorldMapLayer.Resources, "res:" + i.Item, sx, sy,
                                GameText.Format("ui.map.guarantee", GameText.Get(i.NameKey)));
                        }
                    }
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Markers))
            {
                foreach (MapMarkerRecord m in WorldMapMarkers.All(state))
                {
                    if (m == null || m.SurfaceId != WorldGenContent.EarthSurfaceId || !view.Contains(m.CellX, m.CellY, margin))
                    {
                        continue;
                    }
                    string name = GameText.Format("ui.map.marker_default", m.Serial);
                    Add(WorldMapItemKind.Marker, WorldMapLayer.Markers, m.MarkerId, m.CellX, m.CellY, string.IsNullOrEmpty(m.Note) ? name : name + "：" + m.Note);
                }
            }
            // 图标过多时按离视野中心的距离取近的（maxIcons，map.max_icons）。
            if (Items.Count > maxIcons)
            {
                double cx = view.CenterX;
                double cy = view.CenterY;
                Items.Sort((a, b) => ((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy)).CompareTo((b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy)));
                Items.RemoveRange(maxIcons, Items.Count - maxIcons);
            }
        }

        private void Add(WorldMapItemKind kind, WorldMapLayer layer, string id, double x, double y, string label)
        {
            Items.Add(new WorldMapItem { Kind = kind, Layer = layer, Id = id, X = x, Y = y, Label = label });
        }

        private static (int, int, int, int) ClampToExplored(HomeGridMap map, in WorldMapView view, double margin)
        {
            long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
            foreach (ExploredAreaRecord e in map.ExploredAreas ?? Array.Empty<ExploredAreaRecord>())
            {
                if (e == null || e.Radius < 0)
                {
                    continue;
                }
                minX = Math.Min(minX, e.CenterX - e.Radius);
                minY = Math.Min(minY, e.CenterY - e.Radius);
                maxX = Math.Max(maxX, e.CenterX + e.Radius);
                maxY = Math.Max(maxY, e.CenterY + e.Radius);
            }
            if (minX == long.MaxValue)
            {
                return (0, 0, -1, -1);
            }
            int a = (int)Math.Max(minX, (long)Math.Floor(view.MinX - margin));
            int b = (int)Math.Max(minY, (long)Math.Floor(view.MinY - margin));
            int c = (int)Math.Min(maxX, (long)Math.Ceiling(view.MaxX + margin));
            int d = (int)Math.Min(maxY, (long)Math.Ceiling(view.MaxY + margin));
            return (a, b, c, d);
        }
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-080“己方建筑群与前哨”）：把星球表面上的己方建筑聚成“建筑群”，给战略地图 / 小地图出图标。
    ///
    /// - 聚合：按 map.own_cluster_cells 格一个聚合格统计建筑（枢轴格），相邻（八邻接）的非空聚合格连成一群；图标在群里建筑的平均位置，标签带座数。
    /// - 归还核心所在的那一群由核心图标代表（不重复出图标）；离核心超过 map.own_outpost_distance 格的群标成“前哨站”
    ///   （FG08 FGR-EXP-017 的初值 300 格；前哨站作为正式实体——改名、仓库、驻守岗位——归 FG8 的前哨站 Story）。
    /// - 只统计星球表面（家园格网区域）的建筑；规划中的虚影也算（玩家已经下了命令，地图上要看得到它在哪），已摧毁的不算。
    /// 开销：聚合 O(建筑数)，只在建筑列表换了（放置 / 拆除都会换新数组）、表版本或核心变了时重做；平时每次查询 O(1) 返回缓存。
    /// </summary>
    public static class WorldMapOwnClusters
    {
        public struct Cluster
        {
            public string Id;
            public double X;
            public double Y;
            public int Count;
            public bool ContainsCore;
            public bool IsOutpost;
        }

        private static readonly List<Cluster> Clusters = new List<Cluster>(8);
        private static readonly Dictionary<long, int> BucketCount = new Dictionary<long, int>();
        private static readonly Dictionary<long, long> BucketSumX = new Dictionary<long, long>();
        private static readonly Dictionary<long, long> BucketSumY = new Dictionary<long, long>();
        private static readonly HashSet<long> Visited = new HashSet<long>();
        private static readonly Stack<long> Frontier = new Stack<long>();
        private static readonly List<long> Keys = new List<long>();
        private static CampaignState _state;
        private static BuildingRecord[] _records;
        private static int _gridRevision = -1;
        private static int _coreX;
        private static int _coreY;

        /// <summary>重算次数（自检用：证明只在建筑列表变化时重算）。</summary>
        public static int RebuildCount { get; private set; }

        public static IReadOnlyList<Cluster> For(CampaignState state)
        {
            if (state == null)
            {
                Clusters.Clear();
                _state = null;
                _records = null;
                return Clusters;
            }
            GridCell core = HomeGridService.CorePivot(state);
            if (ReferenceEquals(state, _state) && ReferenceEquals(state.BuildingRecords, _records) && _gridRevision == GridContent.Revision
                && _coreX == core.X && _coreY == core.Y)
            {
                return Clusters;
            }
            _state = state;
            _records = state.BuildingRecords;
            _gridRevision = GridContent.Revision;
            _coreX = core.X;
            _coreY = core.Y;
            Rebuild(state, core);
            return Clusters;
        }

        private static long Key(long bx, long by) => (bx << 32) ^ (uint)by;

        private static void Rebuild(CampaignState state, GridCell core)
        {
            RebuildCount++;
            Clusters.Clear();
            BucketCount.Clear();
            BucketSumX.Clear();
            BucketSumY.Clear();
            Visited.Clear();
            Keys.Clear();
            int size = Math.Max(4, GridContent.TuningInt("map.own_cluster_cells"));
            double outpostDistance = GridContent.Tuning("map.own_outpost_distance");
            foreach (BuildingRecord b in state.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != Campaign.Regions.HomeValleyLayout.RegionId || b.ConstructionState == BuildingConstructionState.Destroyed)
                {
                    continue;
                }
                long k = Key(FloorDiv(b.GridX, size), FloorDiv(b.GridY, size));
                if (!BucketCount.TryGetValue(k, out int n))
                {
                    Keys.Add(k);
                }
                BucketCount[k] = n + 1;
                BucketSumX[k] = (BucketSumX.TryGetValue(k, out long sx) ? sx : 0L) + b.GridX;
                BucketSumY[k] = (BucketSumY.TryGetValue(k, out long sy) ? sy : 0L) + b.GridY;
            }
            long coreKey = Key(FloorDiv(core.X, size), FloorDiv(core.Y, size));
            // 按建筑列表里第一次出现的顺序遍历聚合格（确定、与字典内部顺序无关）；八邻接连通 = 一群。
            foreach (long start in Keys)
            {
                if (!Visited.Add(start))
                {
                    continue;
                }
                int count = 0;
                long sumX = 0, sumY = 0;
                long minKey = start;
                bool hasCore = false;
                Frontier.Clear();
                Frontier.Push(start);
                while (Frontier.Count > 0)
                {
                    long k = Frontier.Pop();
                    count += BucketCount[k];
                    sumX += BucketSumX[k];
                    sumY += BucketSumY[k];
                    minKey = Math.Min(minKey, k);
                    hasCore |= k == coreKey;
                    long bx = k >> 32;
                    long by = (int)(uint)k;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            long nk = Key(bx + dx, by + dy);
                            if ((dx != 0 || dy != 0) && BucketCount.ContainsKey(nk) && Visited.Add(nk))
                            {
                                Frontier.Push(nk);
                            }
                        }
                    }
                }
                double cx = (double)sumX / count;
                double cy = (double)sumY / count;
                double dist = Math.Sqrt((cx - core.X) * (cx - core.X) + (cy - core.Y) * (cy - core.Y));
                Clusters.Add(new Cluster
                {
                    Id = "own:" + minKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    X = cx,
                    Y = cy,
                    Count = count,
                    ContainsCore = hasCore,
                    IsOutpost = !hasCore && dist > outpostDistance,
                });
            }
        }

        private static long FloorDiv(int v, int d) => v >= 0 ? v / d : -((-(long)v + d - 1) / d);
    }

    /// <summary>
    /// FG3-GEN-01：地图底图（生成器逐像素采样，Burst 工作线程画、主线程只上传）。视图变了才重画，按真实时间节流（map.repaint_min_seconds），
    /// 一次只有一个任务在飞；主线程从不等待工作线程。贴图成对创建 / 销毁。
    /// </summary>
    public sealed class WorldMapTexture : IDisposable
    {
        private Texture2D _texture;
        private WorldMapPaintJob _job;
        private readonly List<WorldMapPaintJob> _retiring = new List<WorldMapPaintJob>();
        private long _paintedStamp = long.MinValue;
        private long _pendingStamp = long.MinValue;
        private float _lastSchedule = -999f;
        private readonly Color32[] _palette = new Color32[256];
        private int _paletteRevision = -1;
        private int _baseSize;
        // FG4-ECO-02（DEBT-FG3GEN01-08）：玩家改过的格子（拆废墟变空地……）的覆盖表，地形改写版本变了才重新收集。
        private readonly List<int4> _overrideScratch = new List<int4>(64);
        private int4[] _overrides = Array.Empty<int4>();
        private int _overrideRevision = int.MinValue;
        private HomeGridMap _overrideMap;

        /// <summary>自检：最近一次重画用了多少个覆盖格。</summary>
        public int LastOverrideCount { get; private set; }

        public Texture2D Texture => _texture;
        public int PaintCount { get; private set; }
        public double LastJobMs { get; private set; }
        public bool Painted => _paintedStamp != long.MinValue;
        /// <summary>当前贴图对应的视图（画完才更新）。</summary>
        public WorldMapView PaintedView { get; private set; }

        public WorldMapTexture(int baseSize)
        {
            _baseSize = Math.Max(32, baseSize);
        }

        /// <summary>每帧（主线程）：收已完成的任务；视图变了且过了节流间隔时调度新任务。<paramref name="force"/> 忽略节流（打开时第一张）。</summary>
        public void Tick(CampaignState state, in WorldMapView view, bool force)
        {
            for (int i = _retiring.Count - 1; i >= 0; i--)
            {
                if (_retiring[i].IsCompleted)
                {
                    _retiring[i].Release();
                    _retiring.RemoveAt(i);
                }
            }
            if (_job != null && _job.IsCompleted)
            {
                MapPaintParams m = _job.Map;
                EnsureTexture(m.Width, m.Height);
                _job.Upload(_texture);
                LastJobMs = (Time.realtimeSinceStartupAsDouble * 1000.0) - _job.ScheduledAtMs;
                _job.Release();
                _job = null;
                _paintedStamp = _pendingStamp;
                PaintedView = _pendingView;
                PaintCount++;
            }
            WorldGenContext ctx = WorldGenService.ContextFor(state);
            if (ctx == null || view.CanvasWidth < 4f || view.CanvasHeight < 4f)
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            long stamp = Stamp(view, map.ExploredRevision, ctx.Seed) ^ ((long)map.TerrainEditRevision * 1000117L);
            if (_job != null || stamp == _paintedStamp || (!force && Time.realtimeSinceStartup - _lastSchedule < GridContent.Tuning("map.repaint_min_seconds")))
            {
                return;
            }
            RefreshPalette();
            int w = _baseSize;
            int h = Mathf.Clamp(Mathf.RoundToInt(_baseSize * view.CanvasHeight / view.CanvasWidth), 16, _baseSize * 2);
            double cpt = 2.0 * view.HalfWidth / w; // 每个贴图像素多少格
            var m2 = new MapPaintParams
            {
                Width = w,
                Height = h,
                OriginXQ = (long)Math.Round(view.MinX * 65536.0),
                OriginYQ = (long)Math.Round((view.CenterY - cpt * h * 0.5) * 65536.0),
                CellsPerPixelQ = (int)Math.Max(1, Math.Round(cpt * 65536.0)),
                BlockLevel = GridContent.TuningInt("grid.pollution_block_level"),
            };
            ExploredAreaRecord[] ex = map.ExploredAreas ?? Array.Empty<ExploredAreaRecord>();
            var explored = new int3[ex.Length];
            int n = 0;
            foreach (ExploredAreaRecord e in ex)
            {
                if (e != null && e.Radius >= 0)
                {
                    explored[n++] = new int3(e.CenterX, e.CenterY, e.Radius);
                }
            }
            if (n != explored.Length)
            {
                Array.Resize(ref explored, n);
            }
            if (!ReferenceEquals(map, _overrideMap) || map.TerrainEditRevision != _overrideRevision)
            {
                map.CollectCellOverrides(_overrideScratch);
                _overrides = _overrideScratch.ToArray();
                _overrideRevision = map.TerrainEditRevision;
                _overrideMap = map;
            }
            LastOverrideCount = _overrides.Length;
            _job = WorldGenKernel.ScheduleMapPaint(in m2, in ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones, explored, _palette, 0, _overrides);
            _job.ScheduledAtMs = Time.realtimeSinceStartupAsDouble * 1000.0;
            WorldGenKernel.Kick();
            _pendingStamp = stamp;
            _pendingView = view;
            _lastSchedule = Time.realtimeSinceStartup;
        }

        private WorldMapView _pendingView;

        /// <summary>自检：等在飞任务画完并上传。</summary>
        public void CompleteNow(CampaignState state, in WorldMapView view)
        {
            Tick(state, view, force: true);
            _job?.Complete();
            Tick(state, view, force: true);
            _job?.Complete();
            Tick(state, view, force: true);
        }

        private static long Stamp(in WorldMapView v, int exploredRevision, int seed)
        {
            unchecked
            {
                long h = (long)Math.Round(v.CenterX * 4) * 1000003L;
                h = (h ^ (long)Math.Round(v.CenterY * 4)) * 1000033L;
                h = (h ^ (long)Math.Round(v.HalfWidth * 16)) * 1000037L;
                h = (h ^ (long)Math.Round(v.CanvasWidth)) * 1000039L;
                h = (h ^ (long)Math.Round(v.CanvasHeight)) * 1000081L;
                h = (h ^ exploredRevision) * 1000099L;
                return h ^ seed;
            }
        }

        private void EnsureTexture(int w, int h)
        {
            if (_texture != null && _texture.width == w && _texture.height == h)
            {
                return;
            }
            if (_texture != null)
            {
                View.UnityObjects.Release(_texture);
            }
            _texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "WorldMapTexture" };
        }

        private void RefreshPalette()
        {
            if (_paletteRevision == GridContent.Revision)
            {
                return;
            }
            for (int i = 0; i < 256; i++)
            {
                _palette[i] = new Color32(90, 90, 90, 255);
            }
            foreach (GridTerrain t in GridContent.Terrains)
            {
                if (t.Code >= 0 && t.Code <= 255 && ColorUtility.TryParseHtmlString(t.Color, out Color c))
                {
                    _palette[t.Code] = c;
                }
            }
            _paletteRevision = GridContent.Revision;
        }

        public void Dispose()
        {
            _job?.Release();
            _job = null;
            foreach (WorldMapPaintJob j in _retiring)
            {
                j.Release();
            }
            _retiring.Clear();
            if (_texture != null)
            {
                View.UnityObjects.Release(_texture);
                _texture = null;
            }
            _paintedStamp = long.MinValue;
        }
    }

    /// <summary>
    /// FG3-GEN-01：地图的矢量层（领地圈 + 危害带、信号覆盖圈、突袭路线、镜头视野框），用 UI Toolkit 的 Painter2D 画在底图上面。
    /// 只在模型或视图变了时重画（MarkDirtyRepaint）。
    /// </summary>
    public static class WorldMapVectorLayer
    {
        public static void Draw(MeshGenerationContext ctx, WorldMapModel model, in WorldMapView view, Vector2[] cameraQuad)
        {
            Painter2D p = ctx.painter2D;
            double cpp = view.CellsPerCanvasPixel;
            foreach (WorldMapCircle c in model.Circles)
            {
                Vector2 center = view.ToCanvas(c.X, c.Y);
                float r = (float)(c.Radius / cpp);
                if (r < 1f)
                {
                    continue;
                }
                if (c.Layer == WorldMapLayer.Territory)
                {
                    if (c.OuterRadius > c.Radius)
                    {
                        p.lineWidth = Mathf.Max(2f, (float)((c.OuterRadius - c.Radius) / cpp));
                        p.strokeColor = new Color(0.85f, 0.55f, 0.2f, 0.35f);
                        Circle(p, center, (float)((c.Radius + c.OuterRadius) * 0.5 / cpp));
                    }
                    p.lineWidth = 2f;
                    p.strokeColor = new Color(0.92f, 0.45f, 0.35f, 0.9f);
                    Circle(p, center, r);
                }
                else
                {
                    p.lineWidth = 1f;
                    p.strokeColor = c.Connected ? new Color(0.35f, 0.86f, 0.92f, 0.75f) : new Color(0.95f, 0.35f, 0.35f, 0.8f);
                    Circle(p, center, r);
                }
            }
            foreach (WorldMapLine l in model.Lines)
            {
                Vector2 a = view.ToCanvas(l.X0, l.Y0);
                Vector2 b = view.ToCanvas(l.X1, l.Y1);
                p.lineWidth = l.Forecast ? 3f : 2f;
                p.strokeColor = l.Forecast ? new Color(0.95f, 0.25f, 0.2f, 0.95f) : new Color(0.95f, 0.55f, 0.25f, 0.9f);
                p.BeginPath();
                p.MoveTo(a);
                p.LineTo(b);
                p.Stroke();
                if (l.Forecast && (b - a).sqrMagnitude > 4f)
                {
                    // 箭头：终点两侧各一笔（形状 + 颜色，色盲也能分辨方向，B15）。
                    Vector2 d = (b - a).normalized;
                    Vector2 n = new Vector2(-d.y, d.x);
                    p.BeginPath();
                    p.MoveTo(b - d * 10f + n * 6f);
                    p.LineTo(b);
                    p.LineTo(b - d * 10f - n * 6f);
                    p.Stroke();
                }
            }
            if (cameraQuad != null && cameraQuad.Length == 4)
            {
                p.lineWidth = 1.5f;
                p.strokeColor = new Color(1f, 1f, 1f, 0.85f);
                p.BeginPath();
                p.MoveTo(view.ToCanvas(cameraQuad[0].x, cameraQuad[0].y));
                for (int i = 1; i < 4; i++)
                {
                    p.LineTo(view.ToCanvas(cameraQuad[i].x, cameraQuad[i].y));
                }
                p.ClosePath();
                p.Stroke();
            }
        }

        private static void Circle(Painter2D p, Vector2 c, float r)
        {
            p.BeginPath();
            p.Arc(c, r, 0f, 360f);
            p.Stroke();
        }

        /// <summary>镜头视野在地面上的四角（格网 XZ）：视口四角的射线与 0 高度平面相交；取不到时返回 false。</summary>
        public static bool CameraGroundQuad(Camera cam, Vector2[] into)
        {
            if (cam == null || into == null || into.Length < 4)
            {
                return false;
            }
            var plane = new Plane(Vector3.up, Vector3.zero);
            Vector2[] vp = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(vp[i].x, vp[i].y, 0f));
                if (!plane.Raycast(ray, out float enter))
                {
                    return false;
                }
                Vector3 hit = ray.GetPoint(enter);
                into[i] = new Vector2(hit.x, hit.z);
            }
            return true;
        }
    }

    /// <summary>地图图标池（战略地图与小地图共用写法）：按模型重排图标，只增不删地复用 VisualElement。</summary>
    public sealed class WorldMapIconPool
    {
        private readonly VisualElement _parent;
        private readonly bool _labels;
        private readonly List<VisualElement> _icons = new List<VisualElement>();
        private readonly List<Label> _texts = new List<Label>();

        public int VisibleCount { get; private set; }

        public WorldMapIconPool(VisualElement parent, bool labels)
        {
            _parent = parent;
            _labels = labels;
        }

        public VisualElement IconAt(int i) => i >= 0 && i < VisibleCount ? _icons[i] : null;

        public void Layout(WorldMapModel model, in WorldMapView view, string selectedMarkerId)
        {
            int n = 0;
            foreach (WorldMapItem item in model.Items)
            {
                Vector2 at = view.ToCanvas(item.X, item.Y);
                if (at.x < -8f || at.y < -8f || at.x > view.CanvasWidth + 8f || at.y > view.CanvasHeight + 8f)
                {
                    continue;
                }
                if (n >= _icons.Count)
                {
                    var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                    icon.AddToClassList("wg-icon");
                    _parent.Add(icon);
                    _icons.Add(icon);
                    var text = new Label { pickingMode = PickingMode.Ignore };
                    text.AddToClassList("wg-icon-label");
                    _parent.Add(text);
                    _texts.Add(text);
                }
                VisualElement e = _icons[n];
                e.name = "MapIcon_" + item.Id;
                e.userData = item.Id;
                e.EnableInClassList("wg-icon-home", item.Kind == WorldMapItemKind.Home);
                e.EnableInClassList("wg-icon-own", item.Kind == WorldMapItemKind.OwnCluster);
                e.EnableInClassList("wg-icon-outpost", item.Kind == WorldMapItemKind.Outpost);
                e.EnableInClassList("wg-icon-relic", item.Kind == WorldMapItemKind.Relic);
                e.EnableInClassList("wg-icon-resource", item.Kind == WorldMapItemKind.Resource);
                e.EnableInClassList("wg-icon-group", item.Kind == WorldMapItemKind.Group);
                e.EnableInClassList("wg-icon-marker", item.Kind == WorldMapItemKind.Marker);
                e.EnableInClassList("wg-icon-marker-selected", item.Kind == WorldMapItemKind.Marker && item.Id == selectedMarkerId);
                e.EnableInClassList("wg-icon-territory", item.Kind == WorldMapItemKind.Territory);
                e.EnableInClassList("wg-icon-forecast", item.Kind == WorldMapItemKind.RaidForecast);
                e.EnableInClassList("uk-hidden", false);
                e.style.left = at.x;
                e.style.top = at.y;
                Label t = _texts[n];
                bool showLabel = _labels && (item.Kind == WorldMapItemKind.Home || item.Kind == WorldMapItemKind.OwnCluster || item.Kind == WorldMapItemKind.Marker || item.Kind == WorldMapItemKind.Outpost
                                            || item.Kind == WorldMapItemKind.Resource || item.Kind == WorldMapItemKind.Relic || item.Kind == WorldMapItemKind.Territory
                                            || item.Kind == WorldMapItemKind.RaidForecast);
                t.EnableInClassList("uk-hidden", !showLabel);
                if (showLabel)
                {
                    t.text = item.Label;
                    t.style.left = at.x + 9f;
                    t.style.top = at.y - 8f;
                }
                n++;
            }
            for (int i = n; i < _icons.Count; i++)
            {
                _icons[i].EnableInClassList("uk-hidden", true);
                _texts[i].EnableInClassList("uk-hidden", true);
            }
            VisibleCount = n;
        }
    }
}
