using System;
using System.Collections.Generic;
using BinGames.TerrainVisual;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.View;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002“只渲染镜头附近的区块，其余区块不生成表现对象”；DEBT-FG0ARCH05-02 普通视角地貌）：星球表面的表现层。
    ///
    /// - **地貌**：普通视角也显示镜头附近区块的地形（悬崖 / 水源 / 矿脉 / 污染 / 迷雾），复用 FG0-ARCH-05 的按区块贴图
    ///   （<see cref="WorldTerrainOverlay"/>：Burst 工作线程画、没生成好显示“生成中”占位），比建造模式的叠加层更淡、放在建筑下面；
    ///   建造模式打开时让位给建造叠加层（不叠两层）。窗口只覆盖 world.view_radius_chunks，镜头以外的区块没有表现对象。
    /// - **行进中的队伍**：每支队伍一个标记（菱形 + 立柱，形状区分、不只靠颜色），只在镜头窗口附近显示；位置只读
    ///   <see cref="WorldTransitSystem"/> 的正式数据（IC-REQ-010：表现层不另算一套）。
    /// - 流式加载（<see cref="HomeGridService.Streamer"/>）跟随镜头焦点，只在观察星球表面时驱动。
    /// 美术是占位（B22）：地貌是地形图层色块、突袭是标记柱，关注点条的提示里写明。每帧开销 O(贴图块 + 队伍数)。
    /// </summary>
    public static class WorldPlanetView
    {
        private static GameObject _root;
        private static GameObject _terrainRoot;
        private static WorldTerrainOverlay _terrain;
        private static readonly Dictionary<string, GameObject> Markers = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly List<string> GoneScratch = new List<string>(4);
        private static readonly List<Renderer> RendererScratch = new List<Renderer>(2);
        private static readonly HashSet<string> LiveScratch = new HashSet<string>(StringComparer.Ordinal);
        private static Material _raidMaterial;
        private static Material _arrivedMaterial;
        private static Material _outpostMaterial;
        private static Material _patrolMaterial;
        private static Material _relicMaterial;
        private static Material _relicDoneMaterial;
        private static Material _cacheMaterial;
        private static Material _terminalMaterial;
        private static Material _anchorMaterial;
        private static Material _gateMaterial;
        private static Material _protectMaterial;
        private static bool _placeholderLogged;
        private static TerrainProfile _pcgProfile;
        private static int _pcgLoadEpoch;

        /// <summary>FG0-ARCH-06：当前画出来的据点 / 巡逻标记数（自检：迷雾外不画、休眠的也按“此刻”位置画）。</summary>
        public static int VisibleOutpostMarkerCount { get; private set; }

        public static bool Visible => _root != null && _root.activeSelf;
        public static WorldTerrainOverlay Terrain => _terrain;
        public static bool PcgReady => _pcgProfile != null && _terrain != null && _terrain.PcgEnabled;
        public static bool TerrainShown => _terrainRoot != null && _terrainRoot.activeInHierarchy;
        public static int MarkerCount => Markers.Count;
        public static int VisibleMarkerCount
        {
            get
            {
                int n = 0;
                foreach (GameObject go in Markers.Values)
                {
                    if (go != null && go.activeSelf)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public static bool TryGetMarkerPosition(string groupId, out Vector3 position)
        {
            if (Markers.TryGetValue(groupId, out GameObject go) && go != null)
            {
                position = go.transform.position;
                return true;
            }
            position = default;
            return false;
        }

        public static void SetVisible(bool visible)
        {
            if (!visible && _root == null)
            {
                return;
            }
            EnsureRoot();
            if (_root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        /// <summary>观察星球表面时每帧：流式加载跟随焦点、地貌窗口、队伍标记。</summary>
        public static void Tick(CampaignState state, GridCell focus)
        {
            if (state == null)
            {
                return;
            }
            EnsureRoot();
            HomeGridService.Streamer(state).Tick(focus);

            if (_terrain != null)
            {
                // FG3-GEN-01（FG-GAP-021）：地貌是不透明的起伏网格，建造模式也保留（建造叠加层是它上面的半透明地格参考线），
                // 不再让位——否则一开建造模式山脊就被压平。
                if (!_terrainRoot.activeSelf)
                {
                    _terrainRoot.SetActive(true);
                }
                if (HomeGridService.MapFor(state).TerrainSource is WorldGen.WorldTerrainSource src)
                {
                    _terrain.SetReliefSeed(src.Params.SurfaceSeed);
                }
                _terrain.PollutionView = GameLogic.View.OverlayService.Active == GameLogic.View.OverlayKind.Pollution; // FG3-LOG-08：污染叠加层（普通视角同样按等级强调）。
                _terrain.Update(state, focus);
            }
            TickMarkers(state, focus);
            // FG0-ARCH-02：传送带（近景逐物品实例化 / 远景流动贴图），每帧一次 O(1) 调用，逐物品工作在 AOT。
            BeltNetworkService.Render(WorldView.Camera);
            PipeNetworkService.Render(WorldView.Camera);
        }

        private static void TickMarkers(CampaignState state, GridCell focus)
        {
            int chunk = Math.Max(1, GridContent.TuningInt("grid.chunk_size"));
            float radius = (Math.Max(0, GridContent.TuningInt("world.view_radius_chunks")) + 1) * chunk;
            LiveScratch.Clear();
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(state))
            {
                if (g == null)
                {
                    continue;
                }
                LiveScratch.Add(g.GroupId);
                bool near = Math.Abs(g.PosX - focus.X) <= radius && Math.Abs(g.PosY - focus.Y) <= radius;
                Markers.TryGetValue(g.GroupId, out GameObject go);
                if (!near)
                {
                    if (go != null && go.activeSelf)
                    {
                        go.SetActive(false);
                    }
                    continue;
                }
                if (go == null)
                {
                    go = CreateMarker(g.GroupId);
                    Markers[g.GroupId] = go;
                }
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }
                go.transform.position = new Vector3((float)g.PosX, 0f, (float)g.PosY);
                // 菱形与立柱一起变色（到达态整个标记换色）；只对镜头附近的少数标记做，列表复用不分配。
                Material want = g.State == TransitGroupState.Arrived ? _arrivedMaterial : _raidMaterial;
                go.GetComponentsInChildren(RendererScratch);
                foreach (Renderer r in RendererScratch)
                {
                    if (r != null && r.sharedMaterial != want)
                    {
                        r.sharedMaterial = want;
                    }
                }
                RendererScratch.Clear();
            }
            TickOutpostMarkers(state, focus, radius);
            TickRelicMarkers(state, focus, radius);
            TickAnchorMarkers(state, focus, radius);
            GoneScratch.Clear();
            foreach (KeyValuePair<string, GameObject> kv in Markers)
            {
                if (!LiveScratch.Contains(kv.Key))
                {
                    GoneScratch.Add(kv.Key);
                }
            }
            foreach (string id in GoneScratch)
            {
                UnityObjects.Release(Markers[id]);
                Markers.Remove(id);
            }
        }

        /// <summary>
        /// FG0-ARCH-06：敌方据点与巡逻的标记——只画已探索区域里的（迷雾不泄露情报），只画镜头附近的；休眠的据点 / 巡逻用只读补算的“此刻”位置
        /// （<see cref="WorldOutpostSystem.PeekPatrolPosition"/>，不唤醒、不改状态：观察不改变结果）。美术是占位（B22）。
        /// </summary>
        private static void TickOutpostMarkers(CampaignState state, GridCell focus, float radius)
        {
            VisibleOutpostMarkerCount = 0;
            HomeGridMap map = HomeGridService.BoundMap(state);
            if (map == null)
            {
                return;
            }
            // FG8-GEN-02（DEBT-FG0ARCH06-06）：按空间桶只取镜头附近的据点（与据点总数无关）；巡逻只算据点在附近的那些（巡逻走不出折返半径）。
            NearScratch.Clear();
            float patrolReach = radius + 2f * Mathf.Max(24f, PatrolReachCells);
            WorldOutpostSystem.OutpostsNear(state, focus.X, focus.Y, patrolReach, NearScratch);
            foreach (OutpostRecord o in NearScratch)
            {
                if (Math.Abs(o.CellX - focus.X) <= radius && Math.Abs(o.CellY - focus.Y) <= radius)
                {
                    PlaceOutpostMarker(o.OutpostId, o.CellX, o.CellY, focus, radius, map, isPatrol: false);
                }
                // 巡逻按据点索引取（与巡逻总数无关，审查 P2）。
                PatrolScratch.Clear();
                WorldOutpostSystem.PatrolsOf(state, o.OutpostId, PatrolScratch);
                foreach (PatrolRecord p in PatrolScratch)
                {
                    if (p == null)
                    {
                        continue;
                    }
                    Vector2 at = WorldOutpostSystem.PeekPatrolPosition(state, p);
                    PlaceOutpostMarker(p.PatrolId, at.x, at.y, focus, radius, map, isPatrol: true);
                }
            }
            PatrolScratch.Clear();
            NearScratch.Clear();
        }

        private static readonly List<OutpostRecord> NearScratch = new List<OutpostRecord>(32);
        private static readonly List<PatrolRecord> PatrolScratch = new List<PatrolRecord>(8);
        private static float PatrolReachCells => GridContent.TryGetTuning("outpost.patrol_radius_cells", out float r) ? Math.Max(r, 32f) : 32f;

        private static readonly List<WorldGen.WorldFeature> FeatureScratch = new List<WorldGen.WorldFeature>(16);

        /// <summary>FG3-GEN-01：已探索区域里、镜头附近的遗迹点（生成器点位，纯函数查询，不改状态）。占位外形：细高方尖碑（B22）。</summary>
        private static readonly List<string> RelicIds = new List<string>(8);
        private static GridCell _relicFocus = new GridCell(int.MinValue, int.MinValue);
        private static int _relicExploredRevision = -1;
        private static CampaignState _relicState;

        private static void TickRelicMarkers(CampaignState state, GridCell focus, float radius)
        {
            HomeGridMap map = HomeGridService.BoundMap(state);
            if (map == null)
            {
                VisibleRelicMarkerCount = 0;
                return;
            }
            // 只在镜头移动超过 8 格、探索范围变化或换战役时重查（点位查询按分布格缓存；这里连查询本身也不每帧做）。
            if (ReferenceEquals(state, _relicState) && map.ExploredRevision == _relicExploredRevision && Env.HomeExplorationService.Revision == _relicStatusRevision
                && Math.Abs(focus.X - _relicFocus.X) < 8 && Math.Abs(focus.Y - _relicFocus.Y) < 8)
            {
                foreach (string id in RelicIds)
                {
                    LiveScratch.Add(id);
                }
                return;
            }
            _relicState = state;
            _relicFocus = focus;
            _relicExploredRevision = map.ExploredRevision;
            _relicStatusRevision = Env.HomeExplorationService.Revision;
            RelicIds.Clear();
            VisibleRelicMarkerCount = 0;
            RecoveredRelicMarkerCount = 0;
            if (WorldGen.WorldGenService.ContextFor(state) == null)
            {
                return;
            }
            FeatureScratch.Clear();
            int r = (int)radius;
            WorldGen.WorldGenQuery.FeaturesIn(state, focus.X - r, focus.Y - r, focus.X + r, focus.Y + r, FeatureScratch);
            foreach (WorldGen.WorldFeature f in FeatureScratch)
            {
                // FG8-GEN-02：资源缓存（矮宽箱）/ 旧终端（细高柱）与遗迹点（方尖碑）同一套标记：迷雾外不画、完工后变矮变灰（形状 + 颜色，B15）。
                if (f.Kind != WorldGen.WorldFeatureKind.Relic && f.Kind != WorldGen.WorldFeatureKind.Cache && f.Kind != WorldGen.WorldFeatureKind.Terminal)
                {
                    continue;
                }
                LiveScratch.Add(f.Id);
                RelicIds.Add(f.Id);
                Markers.TryGetValue(f.Id, out GameObject go);
                bool explored = map.IsExploredNoLoad(new GridCell(f.X, f.Y));
                if (!explored)
                {
                    if (go != null && go.activeSelf)
                    {
                        go.SetActive(false);
                    }
                    continue;
                }
                if (go == null)
                {
                    go = new GameObject("RelicMarker_" + f.Id);
                    go.transform.SetParent(_root.transform, false);
                    GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityObjects.Release(body.GetComponent<Collider>());
                    body.name = "Obelisk";
                    body.transform.SetParent(go.transform, false);
                    body.transform.localPosition = new Vector3(0f, 2f, 0f);
                    body.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
                    body.transform.localScale = new Vector3(0.9f, 4f, 0.9f);
                    body.GetComponent<Renderer>().sharedMaterial = f.Kind == WorldGen.WorldFeatureKind.Cache ? _cacheMaterial
                        : f.Kind == WorldGen.WorldFeatureKind.Terminal ? _terminalMaterial : _relicMaterial;
                    Markers[f.Id] = go;
                }
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }
                go.transform.position = new Vector3(f.X, 0f, f.Y);
                // FG7-ENV-05：回收过的遗迹点换成矮一截的灰色方尖碑（形状 + 颜色两种区分，B15）。
                bool done = Env.HomeExplorationService.IsRecovered(state, f.Id);
                Transform obelisk = go.transform.childCount > 0 ? go.transform.GetChild(0) : null;
                if (obelisk != null)
                {
                    Vector3 full = f.Kind == WorldGen.WorldFeatureKind.Cache ? new Vector3(2.2f, 1.2f, 1.6f)
                        : f.Kind == WorldGen.WorldFeatureKind.Terminal ? new Vector3(0.6f, 3f, 0.6f) : new Vector3(0.9f, 4f, 0.9f);
                    Vector3 low = new Vector3(full.x, Mathf.Min(full.y, 1.6f) * 0.5f, full.z);
                    obelisk.localScale = done ? low : full;
                    obelisk.localPosition = new Vector3(0f, (done ? low.y : full.y) * 0.5f, 0f);
                    Renderer rr = obelisk.GetComponent<Renderer>();
                    Material want = done ? _relicDoneMaterial : f.Kind == WorldGen.WorldFeatureKind.Cache ? _cacheMaterial
                        : f.Kind == WorldGen.WorldFeatureKind.Terminal ? _terminalMaterial : _relicMaterial;
                    if (rr != null && want != null && rr.sharedMaterial != want)
                    {
                        rr.sharedMaterial = want;
                    }
                }
                if (done)
                {
                    RecoveredRelicMarkerCount++;
                }
                VisibleRelicMarkerCount++;
            }
        }

        /// <summary>FG8-GEN-02：当前画出来的锚点标记数（自检读：迷雾外不画、预留区永不画）。</summary>
        public static int VisibleAnchorMarkerCount { get; private set; }

        /// <summary>
        /// FG8-GEN-02（FGR-GEN-024 / 042）：手工锚点在星球上的占位外形（B22）：模板的每个点位一个矮方块（Demo 地点的关键部分：入口光环、终端、节点、大门、主核心……），
        /// 室内入口是一座深色门框 + 入口保护范围的淡色地圈（说明“这一圈不能建造”）。只画镜头附近、已探索的；预留区永远不画（FGR-GEN-041）。纯表现，不改状态。
        /// </summary>
        private static void TickAnchorMarkers(CampaignState state, GridCell focus, float radius)
        {
            VisibleAnchorMarkerCount = 0;
            HomeGridMap map = HomeGridService.BoundMap(state);
            WorldGen.WorldPlan plan = WorldGen.WorldGenService.PlanFor(state);
            if (map == null || plan == null)
            {
                return;
            }
            foreach (WorldGen.PlannedAnchor a in plan.Anchors)
            {
                if (a.IsReserve)
                {
                    continue;
                }
                Vector2 entry = WorldGen.TerritoryAnchorChecks.EntryOf(state, a);
                float reach = radius + a.FootprintRadius;
                if (Math.Abs(a.OriginX - focus.X) > reach || Math.Abs(a.OriginY - focus.Y) > reach
                    || !map.IsExploredNoLoad(new GridCell(Mathf.RoundToInt(entry.x), Mathf.RoundToInt(entry.y))))
                {
                    continue;
                }
                string id = "anchor:" + a.Id;
                LiveScratch.Add(id);
                // 入口挪过位（可达校验之后）就按新位置重建门框与保护圈：画出来的禁建圈与建造校验 / 点入口用的是同一个点（审查 P2）。
                bool stale = Markers.TryGetValue(id, out GameObject go) && go != null
                             && (!AnchorMarkerEntry.TryGetValue(id, out Vector2 builtAt) || (builtAt - entry).sqrMagnitude > 0.01f);
                if (stale)
                {
                    UnityObjects.Release(go);
                    go = null;
                }
                if (go == null)
                {
                    go = BuildAnchorMarker(a, entry);
                    Markers[id] = go;
                    AnchorMarkerEntry[id] = entry;
                }
                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }
                VisibleAnchorMarkerCount++;
            }
        }

        /// <summary>每个锚点标记建好时用的入口位置（入口挪位后据此重建）。</summary>
        private static readonly Dictionary<string, Vector2> AnchorMarkerEntry = new Dictionary<string, Vector2>(StringComparer.Ordinal);

        private static GameObject BuildAnchorMarker(WorldGen.PlannedAnchor a, Vector2 entry)
        {
            var go = new GameObject("AnchorMarker_" + a.Id);
            go.transform.SetParent(_root.transform, false);
            go.transform.position = Vector3.zero;
            foreach (WorldGen.PlannedAnchorPoint p in a.Points)
            {
                GameObject part = GameObject.CreatePrimitive(p.Kind == "entry" ? PrimitiveType.Cylinder : PrimitiveType.Cube);
                UnityObjects.Release(part.GetComponent<Collider>());
                part.name = "Point_" + p.PointId;
                part.transform.SetParent(go.transform, false);
                Vector2 at = p.Kind == "entry" ? entry : new Vector2(p.X, p.Y);
                float size = Mathf.Max(1f, p.Radius * 1.4f);
                float h = p.Kind == "gate" ? 4f : p.Kind == "core" ? 3f : p.Kind == "entry" ? 0.05f : 1.4f;
                part.transform.position = new Vector3(at.x, h * 0.5f, at.y);
                part.transform.localScale = p.Kind == "entry" ? new Vector3(size * 2f, 0.05f, size * 2f) : new Vector3(size, h, size);
                part.GetComponent<Renderer>().sharedMaterial = a.IsIndoorEntrance && p.Kind == "entry" ? _gateMaterial : _anchorMaterial;
            }
            if (a.IsIndoorEntrance)
            {
                // 门框（两根立柱 + 门楣），朝向背离家园的一侧。
                float rad = a.ForwardDeg * Mathf.Deg2Rad;
                var side = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
                for (int k = -1; k <= 1; k += 2)
                {
                    GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    UnityObjects.Release(post.GetComponent<Collider>());
                    post.name = "GatePost";
                    post.transform.SetParent(go.transform, false);
                    Vector2 pp = entry + side * (2.2f * k);
                    post.transform.position = new Vector3(pp.x, 2f, pp.y);
                    post.transform.localScale = new Vector3(0.8f, 4f, 0.8f);
                    post.GetComponent<Renderer>().sharedMaterial = _gateMaterial;
                }
                if (a.ProtectRadius > 0)
                {
                    GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    UnityObjects.Release(ring.GetComponent<Collider>());
                    ring.name = "ProtectRing";
                    ring.transform.SetParent(go.transform, false);
                    ring.transform.position = new Vector3(entry.x, 0.02f, entry.y);
                    ring.transform.localScale = new Vector3(a.ProtectRadius * 2f, 0.01f, a.ProtectRadius * 2f);
                    ring.GetComponent<Renderer>().sharedMaterial = _protectMaterial;
                }
            }
            return go;
        }

        /// <summary>FG7-ENV-05：当前画出来的“已回收”遗迹点标记数（自检读）。</summary>
        public static int RecoveredRelicMarkerCount { get; private set; }

        private static int _relicStatusRevision = -1;

        /// <summary>FG3-GEN-01：当前画出来的遗迹点标记数（自检：迷雾外不画）。</summary>
        public static int VisibleRelicMarkerCount { get; private set; }

        private static void PlaceOutpostMarker(string id, float x, float y, GridCell focus, float radius, HomeGridMap map, bool isPatrol)
        {
            LiveScratch.Add(id);
            Markers.TryGetValue(id, out GameObject go);
            bool near = Math.Abs(x - focus.X) <= radius && Math.Abs(y - focus.Y) <= radius;
            bool explored = near && map.IsExplored(new GridCell(Mathf.RoundToInt(x), Mathf.RoundToInt(y)));
            if (!explored)
            {
                if (go != null && go.activeSelf)
                {
                    go.SetActive(false);
                }
                return;
            }
            if (go == null)
            {
                go = CreateOutpostMarker(id, isPatrol);
                Markers[id] = go;
                if (!_placeholderLogged)
                {
                    _placeholderLogged = true;
                    TEngine.Log.Info("[WorldPlanetView] " + GameLogic.Localization.GameText.Get("outpost.placeholder"));
                }
            }
            if (!go.activeSelf)
            {
                go.SetActive(true);
            }
            go.transform.position = new Vector3(x, 0f, y);
            VisibleOutpostMarkerCount++;
        }

        private static GameObject CreateOutpostMarker(string id, bool isPatrol)
        {
            var go = new GameObject((isPatrol ? "PatrolMarker_" : "OutpostMarker_") + id);
            go.transform.SetParent(_root.transform, false);
            GameObject body = GameObject.CreatePrimitive(isPatrol ? PrimitiveType.Sphere : PrimitiveType.Cube);
            UnityObjects.Release(body.GetComponent<Collider>());
            body.name = isPatrol ? "Disc" : "Tower";
            body.transform.SetParent(go.transform, false);
            // 形状区分（不只靠颜色，B15）：据点 = 高方柱，巡逻 = 扁球。
            body.transform.localPosition = isPatrol ? new Vector3(0f, 0.6f, 0f) : new Vector3(0f, 2.5f, 0f);
            body.transform.localScale = isPatrol ? new Vector3(2.2f, 0.8f, 2.2f) : new Vector3(2.5f, 5f, 2.5f);
            body.GetComponent<Renderer>().sharedMaterial = isPatrol ? _patrolMaterial : _outpostMaterial;
            return go;
        }

        private static GameObject CreateMarker(string groupId)
        {
            var go = new GameObject("RaidMarker_" + groupId);
            go.transform.SetParent(_root.transform, false);
            GameObject diamond = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityObjects.Release(diamond.GetComponent<Collider>()); // 不挡选中射线。
            diamond.name = "Diamond";
            diamond.transform.SetParent(go.transform, false);
            diamond.transform.localPosition = new Vector3(0f, 3.2f, 0f);
            diamond.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
            diamond.transform.localScale = new Vector3(1.6f, 1.6f, 1.6f);
            diamond.GetComponent<Renderer>().sharedMaterial = _raidMaterial;
            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityObjects.Release(pillar.GetComponent<Collider>());
            pillar.name = "Pillar";
            pillar.transform.SetParent(go.transform, false);
            pillar.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            pillar.transform.localScale = new Vector3(0.35f, 1.2f, 0.35f);
            pillar.GetComponent<Renderer>().sharedMaterial = _raidMaterial;
            return go;
        }

        private static void EnsureRoot()
        {
            if (_root != null)
            {
                return;
            }
            _root = new GameObject("[PlanetView]");
            if (Application.isPlaying)
            {
                Object.DontDestroyOnLoad(_root);
            }
            _terrainRoot = new GameObject("Terrain");
            _terrainRoot.transform.SetParent(_root.transform, false);
            _terrain = new WorldTerrainOverlay(_terrainRoot.transform, alpha: 0.4f, height: -0.05f, terrainView: true);
            if (Application.isPlaying) LoadPcgProfileAsync(++_pcgLoadEpoch).Forget();
            Shader shader = Shader.Find("Standard");
            _raidMaterial = new Material(shader) { color = new Color(0.85f, 0.18f, 0.12f) };
            _arrivedMaterial = new Material(shader) { color = new Color(1f, 0.55f, 0.1f) };
            _outpostMaterial = new Material(shader) { color = new Color(0.45f, 0.12f, 0.35f) };
            _patrolMaterial = new Material(shader) { color = new Color(0.72f, 0.22f, 0.55f) };
            _relicMaterial = new Material(shader) { color = new Color(0.86f, 0.78f, 0.45f) };
            _relicDoneMaterial = new Material(shader) { color = new Color(0.42f, 0.44f, 0.46f) };
            _cacheMaterial = new Material(shader) { color = new Color(0.78f, 0.58f, 0.3f) };
            _terminalMaterial = new Material(shader) { color = new Color(0.5f, 0.8f, 0.85f) };
            _anchorMaterial = new Material(shader) { color = new Color(0.55f, 0.55f, 0.58f) };
            _gateMaterial = new Material(shader) { color = new Color(0.16f, 0.14f, 0.22f) };
            _protectMaterial = new Material(shader) { color = new Color(0.95f, 0.85f, 0.4f, 0.35f) };
        }

        private static async UniTask LoadPcgProfileAsync(int epoch)
        {
            TerrainProfile profile = null;
            try
            {
                profile = await GameModule.Resource.LoadAssetAsync<TerrainProfile>("TerrainCampaignProfile");
                if (epoch != _pcgLoadEpoch || _terrain == null) return;
                if (profile == null) throw new InvalidOperationException("PCG 地貌配置加载失败。");
                _terrain.SetPcgProfile(profile);
                _pcgProfile = profile;
                profile = null; // 所有权转交到 Shutdown，素材使用期间保持资源引用。
            }
            catch (Exception e)
            {
                if (epoch == _pcgLoadEpoch) TEngine.Log.Error("正式 PCG 地貌加载失败：" + e);
            }
            finally
            {
                // 离开战役后的晚到结果也必须释放，不能串到下一局。
                if (profile != null) GameModule.Resource.UnloadAsset(profile);
            }
        }

        /// <summary>离开世界：销毁全部表现对象与材质、释放贴图任务（成对释放）。</summary>
        public static void Shutdown()
        {
            _pcgLoadEpoch++;
            _terrain?.Dispose();
            _terrain = null;
            if (_pcgProfile != null)
            {
                GameModule.Resource.UnloadAsset(_pcgProfile);
                _pcgProfile = null;
            }
            _terrainRoot = null;
            Markers.Clear();
            if (_root != null)
            {
                UnityObjects.Release(_root);
                _root = null;
            }
            if (_raidMaterial != null)
            {
                UnityObjects.Release(_raidMaterial);
                _raidMaterial = null;
            }
            if (_arrivedMaterial != null)
            {
                UnityObjects.Release(_arrivedMaterial);
                _arrivedMaterial = null;
            }
            if (_outpostMaterial != null)
            {
                UnityObjects.Release(_outpostMaterial);
                _outpostMaterial = null;
            }
            if (_patrolMaterial != null)
            {
                UnityObjects.Release(_patrolMaterial);
                _patrolMaterial = null;
            }
            if (_relicMaterial != null)
            {
                UnityObjects.Release(_relicMaterial);
                _relicMaterial = null;
            }
            if (_relicDoneMaterial != null)
            {
                UnityObjects.Release(_relicDoneMaterial);
                _relicDoneMaterial = null;
            }
            // FG8-GEN-02：缓存 / 终端 / 锚点 / 室内入口的材质成对释放。
            foreach (Material m in new[] { _cacheMaterial, _terminalMaterial, _anchorMaterial, _gateMaterial, _protectMaterial })
            {
                if (m != null)
                {
                    UnityObjects.Release(m);
                }
            }
            _cacheMaterial = _terminalMaterial = _anchorMaterial = _gateMaterial = _protectMaterial = null;
            VisibleAnchorMarkerCount = 0;
            VisibleRelicMarkerCount = 0;
            RecoveredRelicMarkerCount = 0;
            _relicStatusRevision = -1;
            RelicIds.Clear();
            _relicState = null;
            VisibleOutpostMarkerCount = 0;
        }
    }
}
