using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
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
        /// <summary>FG8-LOOT-03（FG-GAP-104）：地上的战利品——数据包、黑匣子、阵亡 / 丢弃掉落的货、关键材料。</summary>
        Loot = 8,
    }

    /// <summary>地图筛选（战略地图与小地图共用，会话内有效）。</summary>
    public static class WorldMapFilters
    {
        public static readonly WorldMapLayer[] All =
        {
            WorldMapLayer.Territory, WorldMapLayer.Outposts, WorldMapLayer.Relics, WorldMapLayer.Resources,
            WorldMapLayer.Own, WorldMapLayer.Groups, WorldMapLayer.Markers, WorldMapLayer.Signal, WorldMapLayer.Loot,
        };

        private static readonly bool[] On = { true, true, true, true, true, true, true, true, true };

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
                case WorldMapLayer.Loot: return "ui.map.filter.loot";
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
        /// <summary>FG6-DEF-04：突袭预警（没有情报时也画：箭尾在来袭方向上，标签写方向与倒计时；FGR-DEF-024 地图上的来袭方向箭头）。</summary>
        RaidWarning,
        /// <summary>FG8-EXP-02（FGR-EXP-010 / 012）：星球上行军中的远征队（队伍颜色；标签写名字 · 状态 · 预计到达时间；路线由矢量层画）。</summary>
        Expedition,
        /// <summary>FG8-EXP-02：远征目的地锚点（远征队在外时标出要走到哪里）。</summary>
        ExpeditionGoal,
        /// <summary>FG8-GEN-02（FGR-GEN-024 / 042）：手工锚点——远征地点、首领场地、室内入口（领地图层；标签写名字与种类）。</summary>
        Anchor,
        /// <summary>FG8-GEN-02（FGR-GEN-023）：资源缓存（遗迹图层；已打开的换样式）。</summary>
        Cache,
        /// <summary>FG8-GEN-02（FGR-GEN-023）：旧终端（遗迹图层；已读取的换样式）。</summary>
        Terminal,
        /// <summary>FG8-GEN-02（DEBT-FG0ARCH06-08）：敌方巡逻（据点图层；标签写人数，路线还在规划时写“正在规划路线…”）。</summary>
        Patrol,
        /// <summary>FG8-DROP-01：在飞的投送舱（己方图层；标签写编号 · 载荷 · 剩余时间；航线由矢量层画）。</summary>
        DropPod,
        /// <summary>FG8-DROP-01：投送舱去程的落点 / 回收点。</summary>
        DropTarget,
        /// <summary>FG8-LOOT-03（FG-GAP-104）：地上的战利品（标签写是什么、在哪个敌方领地内）。</summary>
        Loot,
        /// <summary>FG8-LOOT-03：地上的黑匣子（与普通战利品不同样式）。</summary>
        LootBlackBox,
        /// <summary>FG8-QST-01（FGR-QST-003；DEBT-FG3GEN01-06 任务点）：进行中 / 可接取任务的地点（标记图层；标签写任务名）。</summary>
        Quest,
        /// <summary>FG8-QST-01：追踪中的任务（更醒目的样式，标签前加 ◆）。</summary>
        QuestTracked,
        /// <summary>FG9-ACT1-01（DEBT-FG9FAC01-02）：静默信号点（据点图层；迷雾里的已侦测信号，真据点与诱饵中继同一样式，甄别后写真假）。</summary>
        SignalContact,
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
        /// <summary>FG7-ENV-05：遗迹点已回收（图标换成空心灰色）。</summary>
        public bool Done;
        /// <summary>FG8-EXP-02：图标底色（远征队 = 队伍颜色）；a = 0 = 按样式类。</summary>
        public Color Tint;
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
        /// <summary>FG8-DROP-01（FGR-EXP-014）：敌方防空覆盖（红色区域，投送舱不能着陆）。</summary>
        public bool AntiAir;
        /// <summary>FG7-ENV-05（FGR-ENV-060）：领地的危害（fg.TbHazard.id）与图例颜色；外圈危害带画成带刻度的圈（形状 + 颜色，B15）。</summary>
        public string HazardId;
        public Color Tint;
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
        /// <summary>FG8-EXP-02：线条颜色（远征队路线 = 队伍颜色）；a = 0 = 默认橙色（突袭路线）。</summary>
        public Color Tint;
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
                        // FG7-ENV-05：更后幕的领地，玩家亲自探索到它（揭开过它的危害）后也显示。
                        if (!t.IsFaction || (t.Act > act && !HomeExplorationService.TerritoryVisible(state, t)))
                        {
                            continue;
                        }
                        Hazard hz = HomeExplorationService.HazardForTerritory(t.Id);
                        // FG8-EXP-03（FGR-EXP-022“战略地图上显示每个领地的范围、控制度和状态”）：有领地据点的写“控制度 N% · 敌控 / 平定中 / 已收复”；旧世界（没有领地据点）仍写“待侦察”。
                        string label = TerritoryControlService.MapLabel(state, t);
                        if (hz != null)
                        {
                            label = GameText.Format("ui.map.territory_hazard", label, GameText.Get(hz.NameKey));
                        }
                        Circles.Add(new WorldMapCircle
                        {
                            Layer = WorldMapLayer.Territory, X = t.CenterX, Y = t.CenterY, Radius = t.Radius, OuterRadius = t.OuterRadius, Label = label,
                            HazardId = hz?.Id, Tint = HomeExplorationService.HazardColor(hz),
                        });
                        if (view.Contains(t.CenterX, t.CenterY, margin))
                        {
                            Add(WorldMapItemKind.Territory, WorldMapLayer.Territory, "territory:" + t.Id, t.CenterX, t.CenterY, label);
                        }
                    }
                    // FG8-GEN-02（FGR-GEN-023；DEBT-FG7ENV05-02）：领地里按模板分布的危害区（同一图例颜色的小圈；只画已显示领地里、已探索的）。
                    foreach (PlannedHazardZone z in plan.HazardZones)
                    {
                        PlannedTerritory zt = plan.Find(z.TerritoryId);
                        if (zt == null || (zt.Act > act && !HomeExplorationService.TerritoryVisible(state, zt)) || !map.IsExploredNoLoad(new GridCell(z.CenterX, z.CenterY)))
                        {
                            continue;
                        }
                        Hazard hz = HomeExplorationService.HazardForTerritory(zt.Id);
                        Circles.Add(new WorldMapCircle
                        {
                            Layer = WorldMapLayer.Territory, X = z.CenterX, Y = z.CenterY, Radius = z.Radius, OuterRadius = z.Radius,
                            Label = hz != null ? GameText.Format("ui.map.hazard_zone", GameText.Get(hz.NameKey), GameText.Get(zt.NameKey)) : string.Empty,
                            HazardId = hz?.Id, Tint = HomeExplorationService.HazardColor(hz),
                        });
                    }
                    // FG8-GEN-02（FGR-GEN-024 / 042）：手工锚点（按表的 mapAct 幕次显示；预留区永远不显示，FGR-GEN-041）。
                    foreach (PlannedAnchor a in plan.Anchors)
                    {
                        if (a.IsReserve || a.Row.MapAct > act || !view.Contains(a.EntryX, a.EntryY, margin))
                        {
                            continue;
                        }
                        Vector2 at = Campaign.WorldGen.TerritoryAnchorChecks.EntryOf(state, a);
                        bool moved = Campaign.WorldGen.TerritoryAnchorChecks.IsMoved(state, a.Id);
                        string name = GameText.Format("anchor.hover.title", GameText.Get(a.Row.NameKey), GameText.Get(a.KindKey));
                        Add(WorldMapItemKind.Anchor, WorldMapLayer.Territory, "anchor:" + a.Id, at.x, at.y, moved ? GameText.Format("ui.map.anchor.moved", name) : name);
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
                IReadOnlyList<WorldMapOwnClusters.Cluster> owns = WorldMapOwnClusters.For(state);
                for (int ci = 0; ci < owns.Count; ci++)
                {
                    WorldMapOwnClusters.Cluster c = owns[ci];
                    if (c.ContainsCore || !view.Contains(c.X, c.Y, margin))
                    {
                        continue;
                    }
                    // FG8-OUT-01：已经归为前哨站的建筑群写前哨站的名字（玩家可以改名）。
                    OwnOutpostRecord op = c.IsOutpost ? OwnOutpostService.OwnerOfCluster(state, ci) : null;
                    string label = op != null ? GameText.Format("ui.map.own_outpost_named", OwnOutpostService.NameOf(op), c.Count)
                        : GameText.Format(c.IsOutpost ? "ui.map.own_outpost" : "ui.map.own_cluster", c.Count);
                    Add(WorldMapItemKind.OwnCluster, WorldMapLayer.Own, c.Id, c.X, c.Y, label);
                }
                // FG8-EXP-02（FGR-EXP-010 / 012，FG08 第 7 节“战略地图：己方远征队、路线、预计到达时间”）：己方队伍，不受迷雾限制。
                CollectExpedition(state, view, margin);
                // FG8-DROP-01：在飞的投送舱（航线 + 图标 + 去程落点）。
                CollectDrops(state, view, margin);
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Groups))
            {
                foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
                {
                    // 与据点 / 遗迹同一条迷雾规则：队伍当前所在格已探索才显示（图标与路线都不泄露迷雾里的行进）。
                    // 行进途中的“发现 / 拦截”状态由 FG6 的突袭 Story 接管后改读正式的发现状态（FG06“行进途中可以被发现、被拦截”）。
                    // FG6-DEF-04（FGR-DEF-023 行进途中可以被发现）：发现 = 走进已探索区域，或监听站正跟着它（有有效预报）。
                    if (g == null || !Campaign.Defense.RaidDirectorService.IsDiscovered(state, g, GameLogic.Core.GameClock.Ticks))
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
                // FG6-DEF-04（FGR-DEF-024 预警界面：地图上的来袭方向箭头）：已发预警、还没有有效预报的突袭也画一支方向箭头（只画方向，不泄露迷雾里的位置）。
                CollectWarnings(state, core, view, margin);
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Outposts) || StrategicMapUIToolkit.PickActive)
            {
                // FG8-DROP-01（FGR-EXP-014“地图上用红色区域标出”）：铸造 / 白潮据点的防空覆盖（已探明的：据点格已探索，或瞄准 / 被拒 / 被逼回时探明；视野里的，与据点总数无关）。
                // 投送选点模式下即使关了据点图层也画（选点时必须看得见哪里会被拒）。
                Campaign.Regions.DropPodService.AntiAirCircles(state, (float)view.CenterX, (float)view.CenterY, (float)(Math.Max(view.HalfWidth, view.HalfHeight) + margin), _antiAir);
                foreach ((Vector2 c, float r, string _) in _antiAir)
                {
                    Circles.Add(new WorldMapCircle { Layer = WorldMapLayer.Outposts, X = c.x, Y = c.y, Radius = r, OuterRadius = r, AntiAir = true });
                }
                _antiAir.Clear();
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Outposts))
            {
                // FG8-GEN-02（DEBT-FG0ARCH06-06 / 08）：按空间桶只取视野里的据点（与据点总数无关）；标签写阵营、种类、等级与驻军（休眠的按只读补算现算，不改状态）。
                _outposts.Clear();
                WorldOutpostSystem.OutpostsNear(state, (float)view.CenterX, (float)view.CenterY, (float)(Math.Max(view.HalfWidth, view.HalfHeight) + margin), _outposts);
                foreach (OutpostRecord o in _outposts)
                {
                    if (o == null || o.Destroyed || !view.Contains(o.CellX, o.CellY, margin) || !map.IsExploredNoLoad(new GridCell(o.CellX, o.CellY)))
                    {
                        continue;
                    }
                    Add(WorldMapItemKind.Outpost, WorldMapLayer.Outposts, o.OutpostId, o.CellX, o.CellY, OutpostLabel(state, o));
                    // 这个据点的巡逻（巡逻在据点附近折返；只画站在已探索格上的）。
                    _patrols.Clear();
                    WorldOutpostSystem.PatrolsOf(state, o.OutpostId, _patrols);
                    foreach (PatrolRecord p in _patrols)
                    {
                        int px = (int)Math.Round(p.PosX);
                        int py = (int)Math.Round(p.PosY);
                        if (!view.Contains(px, py, margin) || !map.IsExploredNoLoad(new GridCell(px, py)))
                        {
                            continue;
                        }
                        Add(WorldMapItemKind.Patrol, WorldMapLayer.Outposts, p.PatrolId, p.PosX, p.PosY, PatrolLabel(p));
                    }
                }
                _outposts.Clear();
                _patrols.Clear();
                // FG9-ACT1-01（DEBT-FG9FAC01-02）：静默信号点——迷雾里的已侦测信号（真据点 + 诱饵中继，未甄别时一模一样）；揭开迷雾的诱饵写“已识破”。O(已侦测的信号点)。
                SignalContactService.CollectForMap(state, map, view.MinX - margin, view.MinY - margin, view.MaxX + margin, view.MaxY + margin, _contacts);
                foreach ((string cid, Vector2 c, string label, bool decoy) in _contacts)
                {
                    Items.Add(new WorldMapItem { Kind = WorldMapItemKind.SignalContact, Layer = WorldMapLayer.Outposts, Id = "contact:" + cid, X = c.x, Y = c.y, Label = label, Done = decoy });
                }
                _contacts.Clear();
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
                    bool poi = f.Kind == WorldFeatureKind.Cache || f.Kind == WorldFeatureKind.Terminal;
                    if ((f.Kind == WorldFeatureKind.Relic || poi) && map.IsExploredNoLoad(new GridCell(f.X, f.Y)))
                    {
                        // FG7-ENV-05：标签写距离环与状态（回收中 / 已回收），已回收的图标换样式。FG8-GEN-02：资源缓存 / 旧终端同一图层，各自的图标与状态。
                        string label = HomeExplorationService.MapLabel(state, f, out bool done);
                        WorldMapItemKind kind = f.Kind == WorldFeatureKind.Cache ? WorldMapItemKind.Cache : f.Kind == WorldFeatureKind.Terminal ? WorldMapItemKind.Terminal : WorldMapItemKind.Relic;
                        Add(kind, WorldMapLayer.Relics, f.Id, f.X, f.Y, label);
                        if (done)
                        {
                            WorldMapItem last = Items[Items.Count - 1];
                            last.Done = true;
                            Items[Items.Count - 1] = last;
                        }
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
                // FG7-ENV-05（DEBT-FG7ENV04-05）：归还 20,000 格揭示的稀土富矿在地图 / 小地图上有专门的图标与标签（产出倍数）。
                RichDepositRecord[] rich = PurificationService.StateOf(state)?.RichDeposits;
                if (rich != null)
                {
                    int mult = Math.Max(1, GridContent.TuningInt("purify.rich_ore.yield_mult"));
                    foreach (RichDepositRecord d in rich)
                    {
                        if (d != null && view.Contains(d.X, d.Y, margin) && map.IsExploredNoLoad(new GridCell(d.X, d.Y)))
                        {
                            Add(WorldMapItemKind.Resource, WorldMapLayer.Resources, "rich:" + d.X + ":" + d.Y, d.X, d.Y, GameText.Format("ui.map.rich_ore", mult));
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
            if (WorldMapFilters.IsOn(WorldMapLayer.Markers))
            {
                // FG8-QST-01（FGR-QST-003“追踪中的任务在战略地图上有标记”；FG08 第 7 节“未完成任务”）：进行中 / 可接取的任务地点，追踪中的换样式。
                // 任务地点是按种子解析的（不受迷雾限制：任务本身就是告诉玩家去哪里）。O(任务数)。
                foreach (ObjectiveRecord r in state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                {
                    if (!CampaignObjectiveTracker.IsQuestRecord(r) || !CampaignObjectiveTracker.IsOpen(r) || CampaignObjectiveTracker.IsHidden(r))
                    {
                        continue;
                    }
                    Vector2? at = CampaignObjectiveTracker.QuestLocation(state, r);
                    if (!at.HasValue || !view.Contains(at.Value.x, at.Value.y, margin))
                    {
                        continue;
                    }
                    bool tracked = r.TrackOrder > 0 && r.State == ObjectiveState.Active;
                    Add(tracked ? WorldMapItemKind.QuestTracked : WorldMapItemKind.Quest, WorldMapLayer.Markers, "quest:" + r.ObjectiveId, at.Value.x, at.Value.y,
                        GameText.Format(tracked ? "ui.map.quest_tracked" : "ui.map.quest", CampaignObjectiveTracker.QuestTitle(r)));
                }
            }
            if (WorldMapFilters.IsOn(WorldMapLayer.Loot))
            {
                // FG8-LOOT-03（FG-GAP-104）：地上的战利品——自己的机器掉的 / 丢的，不受迷雾限制。O(数据包 + 关键物 + 地面物)。
                Campaign.Economy.CargoService.CollectGroundMarkers(state, _loot);
                foreach (Campaign.Economy.CargoService.GroundMarker g in _loot)
                {
                    if (view.Contains(g.X, g.Y, margin))
                    {
                        Add(g.Kind == "blackbox" ? WorldMapItemKind.LootBlackBox : WorldMapItemKind.Loot, WorldMapLayer.Loot, g.Id, g.X, g.Y, g.Label);
                    }
                }
                _loot.Clear();
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

        private readonly List<OutpostRecord> _outposts = new List<OutpostRecord>(32);
        private readonly List<Campaign.Economy.CargoService.GroundMarker> _loot = new List<Campaign.Economy.CargoService.GroundMarker>(16);
        private readonly List<PatrolRecord> _patrols = new List<PatrolRecord>(4);

        /// <summary>FG8-GEN-02（DEBT-FG0ARCH06-08）：据点标签——“静默据点（3 级）（驻军 6）”/“铸造零散前哨（5 级）”/“侦察巢（2 级）”。驻军按只读补算（Peek，观察不改变结果）。</summary>
        /// <summary>巡逻的地图标签：“敌方巡逻（3 台）”；路线还在规划（刚醒来的据点）时追加“正在规划路线…”。</summary>
        public static string PatrolLabel(PatrolRecord p)
        {
            string label = GameText.Format("outpost.patrol.marker", GameText.Get("outpost.patrol.name"), Math.Max(1, p.UnitCount));
            return p.RouteState == WorldOutpostSystem.RouteAwaiting ? label + " · " + GameText.Get("nav.route.awaiting") : label;
        }

        public static string OutpostLabel(CampaignState state, OutpostRecord o)
        {
            string name = TerritoryControlService.OutpostName(o); // FG8-EXP-03：与领地交战通知同一套名字（含精英残部）
            WorldOutpostSystem.Peek(state, o, out int garrison, out _);
            string label = GameText.Format("outpost.marker", name, garrison);
            // FG9-ACT1-01 修复轮（第 1 轮审查 P2）：远征地点里的领地驻军（kind = site）在星球上不迎战展开——标签写明“在远征地点内”，玩家知道要远征过去打。
            return o != null && o.Kind == WorldOutpostSystem.KindSite ? label + GameText.Get("outpost.marker.in_site") : label;
        }

        private void Add(WorldMapItemKind kind, WorldMapLayer layer, string id, double x, double y, string label, Color tint = default)
        {
            Items.Add(new WorldMapItem { Kind = kind, Layer = layer, Id = id, X = x, Y = y, Label = label, Tint = tint });
        }

        private readonly List<Vector2> _expRoute = new List<Vector2>(64);
        private readonly List<(string Id, Vector2 Cell, string Label, bool Decoy)> _contacts = new List<(string, Vector2, string, bool)>(8);
        private readonly List<(Vector2 Center, float Radius, string OutpostId)> _antiAir = new List<(Vector2, float, string)>(8);
        private readonly List<(int Serial, Vector2 Pos, Vector2 To, bool Returning, string Label)> _drops = new List<(int, Vector2, Vector2, bool, string)>(8);

        /// <summary>FG8-DROP-01：在飞的投送舱——剩余航线（直线）、舱图标（编号 · 载荷 · 剩余时间）、去程的落点。O(舱数)。</summary>
        private void CollectDrops(CampaignState state, in WorldMapView view, double margin)
        {
            Campaign.Regions.DropPodService.FlyingForView(state, _drops);
            var tint = new Color(0.98f, 0.82f, 0.3f, 0.95f);
            foreach ((int serial, Vector2 pos, Vector2 to, bool returning, string label) in _drops)
            {
                Lines.Add(new WorldMapLine { X0 = pos.x, Y0 = pos.y, X1 = to.x, Y1 = to.y, Tint = tint });
                if (view.Contains(pos.x, pos.y, margin))
                {
                    Add(WorldMapItemKind.DropPod, WorldMapLayer.Own, "drop-pod:" + serial, pos.x, pos.y, label);
                }
                if (!returning && view.Contains(to.x, to.y, margin))
                {
                    Add(WorldMapItemKind.DropTarget, WorldMapLayer.Own, "drop-target:" + serial, to.x, to.y, GameText.Get("drop.map.target"));
                }
            }
            _drops.Clear();
        }

        /// <summary>
        /// FG8-EXP-02：远征队在外——星球上行军时画剩余路线（队伍颜色的折线，每段一条线）、队伍图标（名字 · 状态 · 预计到达时间）和目的地锚点；
        /// 在远征地点里时只画目的地锚点（标签写地点名）。O(路线点数)。
        /// </summary>
        private void CollectExpedition(CampaignState state, in WorldMapView view, double margin)
        {
            GameLogic.Campaign.ExpeditionMarchState m = ExpeditionMarchService.Current(state);
            int phase = m?.Phase ?? ExpeditionMarchService.PhaseNone;
            if (phase != ExpeditionMarchService.PhaseOutbound && phase != ExpeditionMarchService.PhaseReturning && phase != ExpeditionMarchService.PhaseAtSite)
            {
                return;
            }
            Color team = ExpeditionTeamService.ColorOf(ExpeditionTeamService.ColorIndexOf(state));
            if (ExpeditionSiteAnchors.TryGet(state, m.SiteId, out Vector2 goal) && view.Contains(goal.x, goal.y, margin))
            {
                Add(WorldMapItemKind.ExpeditionGoal, WorldMapLayer.Own, "expedition-goal:" + m.SiteId, goal.x, goal.y,
                    GameText.Format("exp.map.destination", GameText.Get(ExpeditionTextKeys.SiteNameKey(m.SiteId))));
            }
            if (phase == ExpeditionMarchService.PhaseAtSite || !ExpeditionMarchService.TryTeamCenter(state, out Vector2 at, out _))
            {
                return;
            }
            if (ExpeditionMarchService.CopyRoute(state, _expRoute) >= 2)
            {
                for (int k = 0; k + 1 < _expRoute.Count; k++)
                {
                    Lines.Add(new WorldMapLine { X0 = _expRoute[k].x, Y0 = _expRoute[k].y, X1 = _expRoute[k + 1].x, Y1 = _expRoute[k + 1].y, Tint = team });
                }
            }
            if (!view.Contains(at.x, at.y, margin))
            {
                return;
            }
            ExpeditionMarchService.TeamStatus st = ExpeditionMarchService.StatusOf(state);
            double eta = ExpeditionMarchService.EtaSeconds(state);
            string etaText = st == ExpeditionMarchService.TeamStatus.Standby ? GameText.Get("exp.map.eta_hold")
                : eta >= 0 ? GameText.Format("exp.map.eta", GameLogic.Core.GameClock.FormatGameDuration(eta)) : GameText.Get("exp.map.eta_planning");
            Add(WorldMapItemKind.Expedition, WorldMapLayer.Own, "expedition:" + m.SiteId, at.x, at.y,
                GameText.Format("exp.map.label", ExpeditionTeamService.DisplayName(state), GameText.Get(ExpeditionMarchService.StatusTextKey(st)), etaText), team);
        }

        /// <summary>
        /// FG6-DEF-04：已发预警（集结 / 在路上）、还没有有效预报的每一波突袭画一支方向箭头：箭尾在“目标 → 预计抵达点”方向上离目标 intel.map_arrow_cells 格处，箭头指向目标
        /// （家园 = 归还核心；前哨站 = 那个建筑群）。方向与标签文字用同一个向量（<see cref="Campaign.Defense.RaidDirectorService.WaveApproachVector"/>，与预警条、预报同一口径，复审 P2）。
        /// 有有效预报的那一波由预报箭头代表（不重复画）；只有情报的计划中突袭也由预报箭头代表。O(计划数)。
        /// </summary>
        private void CollectWarnings(CampaignState state, GridCell core, in WorldMapView view, double margin)
        {
            long now = GameLogic.Core.GameClock.Ticks;
            float len = Campaign.Economy.IntelCatalog.MapArrowCells;
            foreach (Campaign.Defense.RaidWaveView w in Campaign.Defense.RaidDirectorService.IncomingWaves(state, now))
            {
                if (w.IntelKnown || w.PlannedOnly || w.Arrived)
                {
                    continue;
                }
                double cx = w.Lead.TargetX;
                double cy = w.Lead.TargetY;
                Campaign.Defense.RaidDirectorService.WaveApproachVector(w, out double dx, out double dy);
                double d = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
                double tx = cx + dx / d * len;
                double ty = cy + dy / d * len;
                double hx = cx + dx / d * len * 0.15;
                double hy = cy + dy / d * len * 0.15;
                Lines.Add(new WorldMapLine { X0 = tx, Y0 = ty, X1 = hx, Y1 = hy, Forecast = true });
                // 地图打开时视野围着家园、可能比箭头短：箭尾在视野外而目标在视野里时，标签放到箭头与视野边缘的交点往里一点，
                // 打开地图总能看到“从哪个方向、多久后到”。
                double lx = tx;
                double ly = ty;
                if (!view.Contains(lx, ly) && view.Contains(cx, cy))
                {
                    double t = 1.0;
                    if (lx > view.MaxX) t = Math.Min(t, (view.MaxX - cx) / (lx - cx));
                    if (lx < view.MinX) t = Math.Min(t, (view.MinX - cx) / (lx - cx));
                    if (ly > view.MaxY) t = Math.Min(t, (view.MaxY - cy) / (ly - cy));
                    if (ly < view.MinY) t = Math.Min(t, (view.MinY - cy) / (ly - cy));
                    t = Math.Max(0.0, t) * 0.85;
                    lx = cx + (tx - cx) * t;
                    ly = cy + (ty - cy) * t;
                }
                if (view.Contains(lx, ly, margin))
                {
                    Add(WorldMapItemKind.RaidWarning, WorldMapLayer.Groups, "warning:" + w.Lead.PlanId, lx, ly,
                        GameText.Format("ui.map.raid_warning", Campaign.Defense.RaidDirectorService.WaveDirectionText(w),
                            Campaign.Defense.RaidDirectorService.Duration(w.ArrivalTick - now)));
                }
            }
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
                    // FG7-ENV-05（FGR-ENV-060）：领地圈按危害的图例颜色画；外圈危害带 = 半透明宽带 + 外缘刻度（形状区分，不只靠颜色，B15）。
                    Color tint = c.HazardId != null ? c.Tint : new Color(0.92f, 0.45f, 0.35f, 1f);
                    if (c.OuterRadius > c.Radius)
                    {
                        p.lineWidth = Mathf.Max(2f, (float)((c.OuterRadius - c.Radius) / cpp));
                        p.strokeColor = new Color(tint.r, tint.g, tint.b, 0.3f);
                        Circle(p, center, (float)((c.Radius + c.OuterRadius) * 0.5 / cpp));
                        Ticks(p, center, (float)(c.OuterRadius / cpp), new Color(tint.r, tint.g, tint.b, 0.85f));
                    }
                    p.lineWidth = 2f;
                    p.strokeColor = new Color(tint.r, tint.g, tint.b, 0.9f);
                    Circle(p, center, r);
                }
                else if (c.AntiAir)
                {
                    // FG8-DROP-01：防空覆盖 = 半透明红色实心圆 + 红色描边 + 外缘刻度（形状区分，不只靠颜色，B15）。
                    p.fillColor = new Color(0.95f, 0.18f, 0.15f, 0.18f);
                    p.BeginPath();
                    p.Arc(center, r, 0f, 360f);
                    p.Fill();
                    p.lineWidth = 2f;
                    p.strokeColor = new Color(0.95f, 0.22f, 0.18f, 0.9f);
                    Circle(p, center, r);
                    Ticks(p, center, r, new Color(0.95f, 0.22f, 0.18f, 0.9f));
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
                p.strokeColor = l.Forecast ? new Color(0.95f, 0.25f, 0.2f, 0.95f) : l.Tint.a > 0f ? l.Tint : new Color(0.95f, 0.55f, 0.25f, 0.9f);
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

        /// <summary>FG7-ENV-05：外圈危害带外缘的刻度（24 道朝外的短线），半径太小时不画。</summary>
        private static void Ticks(Painter2D p, Vector2 c, float r, Color color)
        {
            if (r < 12f)
            {
                return;
            }
            p.lineWidth = 1.5f;
            p.strokeColor = color;
            float len = Mathf.Clamp(r * 0.06f, 3f, 8f);
            for (int i = 0; i < 24; i++)
            {
                float a = i * (Mathf.PI * 2f / 24f);
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                p.BeginPath();
                p.MoveTo(c + d * r);
                p.LineTo(c + d * (r + len));
                p.Stroke();
            }
        }

        /// <summary>镜头视野在地面上的四角（格网 XZ）：视口四角的射线与 0 高度平面相交；取不到时返回 false。</summary>
        public static bool CameraGroundQuad(Camera cam, Vector2[] into, float viewSize = 0f)
        {
            if (cam == null || into == null || into.Length < 4)
            {
                return false;
            }
            var plane = new Plane(Vector3.up, Vector3.zero);
            float scale = viewSize > 0f ? viewSize / Mathf.Max(0.01f, cam.orthographicSize) : 1f;
            Ray center = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector2[] vp = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(vp[i].x, vp[i].y, 0f));
                if (cam.orthographic)
                {
                    ray.origin = center.origin + (ray.origin - center.origin) * scale;
                }
                else if (viewSize > 0f)
                {
                    // 透视缩放改变 tan(FOV/2)。直接构造目标视口射线，避免反投影当前射线再放大其浮点误差。
                    float halfHeight = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * scale;
                    Vector3 direction = new Vector3((vp[i].x * 2f - 1f) * halfHeight * cam.aspect,
                        (vp[i].y * 2f - 1f) * halfHeight, 1f);
                    ray = new Ray(cam.transform.position, cam.transform.TransformDirection(direction));
                }
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
                e.EnableInClassList("wg-icon-patrol", item.Kind == WorldMapItemKind.Patrol);
                e.EnableInClassList("wg-icon-contact", item.Kind == WorldMapItemKind.SignalContact);
                e.EnableInClassList("wg-icon-contact-decoy", item.Kind == WorldMapItemKind.SignalContact && item.Done);
                e.EnableInClassList("wg-icon-relic", item.Kind == WorldMapItemKind.Relic);
                e.EnableInClassList("wg-icon-relic-done", (item.Kind == WorldMapItemKind.Relic || item.Kind == WorldMapItemKind.Cache || item.Kind == WorldMapItemKind.Terminal) && item.Done);
                e.EnableInClassList("wg-icon-cache", item.Kind == WorldMapItemKind.Cache);
                e.EnableInClassList("wg-icon-terminal", item.Kind == WorldMapItemKind.Terminal);
                e.EnableInClassList("wg-icon-anchor", item.Kind == WorldMapItemKind.Anchor);
                e.EnableInClassList("wg-icon-resource", item.Kind == WorldMapItemKind.Resource);
                e.EnableInClassList("wg-icon-group", item.Kind == WorldMapItemKind.Group);
                e.EnableInClassList("wg-icon-marker", item.Kind == WorldMapItemKind.Marker);
                e.EnableInClassList("wg-icon-marker-selected", item.Kind == WorldMapItemKind.Marker && item.Id == selectedMarkerId);
                e.EnableInClassList("wg-icon-territory", item.Kind == WorldMapItemKind.Territory);
                e.EnableInClassList("wg-icon-forecast", item.Kind == WorldMapItemKind.RaidForecast || item.Kind == WorldMapItemKind.RaidWarning);
                e.EnableInClassList("wg-icon-expedition", item.Kind == WorldMapItemKind.Expedition);
                e.EnableInClassList("wg-icon-expedition-goal", item.Kind == WorldMapItemKind.ExpeditionGoal);
                e.EnableInClassList("wg-icon-drop-pod", item.Kind == WorldMapItemKind.DropPod);
                e.EnableInClassList("wg-icon-drop-target", item.Kind == WorldMapItemKind.DropTarget);
                e.EnableInClassList("wg-icon-loot", item.Kind == WorldMapItemKind.Loot); // FG8-LOOT-03
                e.EnableInClassList("wg-icon-loot-blackbox", item.Kind == WorldMapItemKind.LootBlackBox);
                e.EnableInClassList("wg-icon-quest", item.Kind == WorldMapItemKind.Quest); // FG8-QST-01
                e.EnableInClassList("wg-icon-quest-tracked", item.Kind == WorldMapItemKind.QuestTracked);
                // FG8-EXP-02：远征队图标的底色 = 队伍颜色（数据驱动的运行时颜色，UI Toolkit 红线 2 允许）；其余图标清掉，按样式类。
                e.style.backgroundColor = item.Tint.a > 0f ? new StyleColor(item.Tint) : new StyleColor(StyleKeyword.Null);
                e.EnableInClassList("uk-hidden", false);
                e.style.left = at.x;
                e.style.top = at.y;
                Label t = _texts[n];
                bool showLabel = _labels && (item.Kind == WorldMapItemKind.Home || item.Kind == WorldMapItemKind.OwnCluster || item.Kind == WorldMapItemKind.Marker || item.Kind == WorldMapItemKind.Outpost
                                            || item.Kind == WorldMapItemKind.Resource || item.Kind == WorldMapItemKind.Relic || item.Kind == WorldMapItemKind.Territory
                                            || item.Kind == WorldMapItemKind.RaidForecast || item.Kind == WorldMapItemKind.RaidWarning
                                            || item.Kind == WorldMapItemKind.Expedition || item.Kind == WorldMapItemKind.ExpeditionGoal || item.Kind == WorldMapItemKind.DropPod
                                            || item.Kind == WorldMapItemKind.Anchor || item.Kind == WorldMapItemKind.Cache || item.Kind == WorldMapItemKind.Terminal
                                            || item.Kind == WorldMapItemKind.Loot || item.Kind == WorldMapItemKind.LootBlackBox
                                            || item.Kind == WorldMapItemKind.Quest || item.Kind == WorldMapItemKind.QuestTracked);
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
