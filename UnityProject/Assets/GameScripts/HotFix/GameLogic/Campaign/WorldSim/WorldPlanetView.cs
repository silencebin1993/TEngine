using System;
using System.Collections.Generic;
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

        public static bool Visible => _root != null && _root.activeSelf;
        public static WorldTerrainOverlay Terrain => _terrain;
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

            HomeValleyController home = WorldSimulation.Home;
            bool buildOverlay = home != null && home.IsLoaded && home.BuildMode.IsOpen;
            if (_terrain != null)
            {
                if (_terrainRoot.activeSelf == buildOverlay)
                {
                    _terrainRoot.SetActive(!buildOverlay);
                }
                if (!buildOverlay)
                {
                    _terrain.Update(state, focus);
                }
            }
            TickMarkers(state, focus);
            // FG0-ARCH-02：传送带（近景逐物品实例化 / 远景流动贴图），每帧一次 O(1) 调用，逐物品工作在 AOT。
            BeltNetworkService.Render(WorldView.Camera);
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
            _terrain = new WorldTerrainOverlay(_terrainRoot.transform, alpha: 0.4f, height: -0.05f);
            Shader shader = Shader.Find("Standard");
            _raidMaterial = new Material(shader) { color = new Color(0.85f, 0.18f, 0.12f) };
            _arrivedMaterial = new Material(shader) { color = new Color(1f, 0.55f, 0.1f) };
        }

        /// <summary>离开世界：销毁全部表现对象与材质、释放贴图任务（成对释放）。</summary>
        public static void Shutdown()
        {
            _terrain?.Dispose();
            _terrain = null;
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
        }
    }
}
