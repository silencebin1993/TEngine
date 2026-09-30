using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG3-LOG-06（FGR-LOG-061“电力覆盖叠加层显示哪些建筑没有接入电网”；FG03 第 4 节“放置时预览电力覆盖”）：电力覆盖叠加层（纯表现，不驱动任何游戏状态）。
    ///
    /// - 每个导电的电力节点（归还核心自带配电、电塔）画一个覆盖圈，同一个电网同一种颜色；相连的节点之间画连线（连线本身就是“哪些连在一起”的形状信息，不只靠颜色，B15）。
    /// - 运转中却没接入电网的用电 / 发电建筑：头顶一枚“断电”形状标记。
    /// - 开关：电网面板的“显示电力覆盖”按钮（面板开着时也显示）；建造模式里选中和电网有关的建筑（电塔、用电、发电、储能）或搬迁这类建筑时自动显示；
    ///   选中电塔时在光标处另画一个放下后的覆盖圈（白色粗圈）。
    /// - 开销：每帧 O(1)（比较内核拓扑版本、预览格与开关）；拓扑变了才重画（O(节点 × 圆的段数 + 建筑)）。美术是占位（B22）。
    /// </summary>
    public static class PowerCoverageOverlayView
    {
        private const int Segments = 64;
        private const float Height = 0.14f;

        private static readonly Color[] Palette =
        {
            new Color(0.95f, 0.85f, 0.25f, 0.8f), new Color(0.3f, 0.85f, 0.95f, 0.8f), new Color(0.6f, 0.9f, 0.35f, 0.8f),
            new Color(0.95f, 0.5f, 0.85f, 0.8f), new Color(0.95f, 0.6f, 0.25f, 0.8f), new Color(0.6f, 0.6f, 0.98f, 0.8f),
        };

        private static readonly List<LineRenderer> Rings = new List<LineRenderer>(16);
        private static readonly List<LineRenderer> Links = new List<LineRenderer>(16);
        private static readonly List<WorldBadge> Badges = new List<WorldBadge>(8);
        private static readonly Vector3[] Points = new Vector3[Segments];
        private static LineRenderer _preview;
        private static GameObject _root;
        private static int _drawnTopology = -1;
        private static PowerKernel _drawnKernel;
        private static bool _shown;
        private static int _previewKey = int.MinValue;

        /// <summary>玩家打开了叠加层（电网面板按钮）。本局会话内有效，回主菜单复位。</summary>
        public static bool Enabled { get; private set; }
        public static bool Visible => _shown;
        public static int DrawnRings { get; private set; }
        public static int DrawnLinks { get; private set; }
        public static int DrawnUnconnected { get; private set; }
        public static bool PreviewShown => _preview != null && _preview.enabled;
        public static float PreviewRadius { get; private set; }
        public static int RedrawCount { get; private set; }

        public static void Toggle() => SetEnabled(!Enabled);

        public static void SetEnabled(bool on)
        {
            if (Enabled == on)
            {
                return;
            }
            Enabled = on;
            _drawnTopology = -1;
        }

        /// <summary>建造模式里正在放 / 搬迁和电网有关的建筑：自动显示（不改玩家的开关）。</summary>
        public static bool AutoShown
        {
            get
            {
                HomeValleyController home = WorldSimulation.Home;
                if (home == null || !home.IsLoaded || !WorldView.IsObserved(home.SiteId) || !home.BuildMode.IsOpen)
                {
                    return false;
                }
                string t = home.BuildMode.SelectedTypeId;
                if (t == null && home.BuildMode.CarryBuildingId != null)
                {
                    t = HomeGridService.FindBuilding(CampaignSession.Current, home.BuildMode.CarryBuildingId)?.BuildingTypeId;
                }
                return t != null && HomeValleyPowerGrid.IsPowerRelevantType(t);
            }
        }

        /// <summary>每帧（世界模拟推进之后）。</summary>
        public static void FrameTick()
        {
            CampaignState state = CampaignSession.Current;
            PowerKernel k = HomeValleyPowerGrid.Kernel;
            bool want = (Enabled || PowerPanelUIToolkit.IsOpen || AutoShown) && k != null && state != null
                        && ReferenceEquals(HomeValleyPowerGrid.BoundState, state) && WorldView.IsObserved(HomeValleyLayout.RegionId);
            if (!want)
            {
                if (_shown)
                {
                    HideAll();
                }
                return;
            }
            if (!_shown || k.TopologyVersion != _drawnTopology || !ReferenceEquals(k, _drawnKernel))
            {
                Redraw(k);
                _drawnTopology = k.TopologyVersion;
                _drawnKernel = k;
                _shown = true;
            }
            TickPreview();
        }

        private static void Redraw(PowerKernel k)
        {
            RedrawCount++;
            EnsureRoot();
            int rings = 0;
            int links = 0;
            int badges = 0;
            for (int i = 0; i < k.Count; i++)
            {
                PowerEntity e = k.Entity(i);
                int s = k.SubnetOf(i);
                Vector2 c = HomeValleyPowerGrid.EntityCenter(i);
                if (e.NodeRadius > 0f && e.Conducts && s >= 0)
                {
                    Color color = Palette[(k.Subnet(s).Serial - 1 + Palette.Length * 16) % Palette.Length];
                    LineRenderer ring = Line(Rings, rings++, "PowerRing");
                    ring.sharedMaterial = ViewMaterials.Get("Sprites/Default", color);
                    ring.widthMultiplier = 0.45f;
                    ring.loop = true;
                    for (int n = 0; n < Segments; n++)
                    {
                        float a = n * Mathf.PI * 2f / Segments;
                        Points[n] = new Vector3(c.x + Mathf.Cos(a) * e.NodeRadius, Height, c.y + Mathf.Sin(a) * e.NodeRadius);
                    }
                    ring.positionCount = Segments;
                    ring.SetPositions(Points);
                    ring.enabled = true;
                    int parent = k.LinkOf(i);
                    if (parent >= 0)
                    {
                        Vector2 pc = HomeValleyPowerGrid.EntityCenter(parent);
                        LineRenderer link = Line(Links, links++, "PowerLink");
                        link.sharedMaterial = ViewMaterials.Get("Sprites/Default", color);
                        link.widthMultiplier = 0.25f;
                        link.loop = false;
                        link.positionCount = 2;
                        link.SetPosition(0, new Vector3(c.x, Height + 0.02f, c.y));
                        link.SetPosition(1, new Vector3(pc.x, Height + 0.02f, pc.y));
                        link.enabled = true;
                    }
                }
                else if (s < 0 && e.NodeRadius <= 0f && (e.DemandOn || e.SupplyOn) && HomeValleyPowerGrid.RecordAt(i) != null)
                {
                    WorldBadge badge = BadgeAt(badges++);
                    badge.transform.position = new Vector3(c.x, 4.2f, c.y);
                    badge.SetIcon(ContentIcons.StateUnpowered);
                    badge.SetVisible(true);
                }
            }
            for (int i = rings; i < Rings.Count; i++)
            {
                if (Rings[i] != null)
                {
                    Rings[i].enabled = false;
                }
            }
            for (int i = links; i < Links.Count; i++)
            {
                if (Links[i] != null)
                {
                    Links[i].enabled = false;
                }
            }
            for (int i = badges; i < Badges.Count; i++)
            {
                Badges[i]?.SetVisible(false);
            }
            DrawnRings = rings;
            DrawnLinks = links;
            DrawnUnconnected = badges;
        }

        /// <summary>建造模式选中电塔时，在光标处画放下后的覆盖圈（换格才重画）。</summary>
        private static void TickPreview()
        {
            HomeValleyController home = WorldSimulation.Home;
            HomeValleyBuildMode mode = home != null && home.IsLoaded ? home.BuildMode : null;
            float radius = mode != null && mode.IsOpen && mode.HasHover && mode.SelectedTypeId != null ? HomeValleyPowerGrid.CoverRadiusOf(mode.SelectedTypeId) : 0f;
            if (radius <= 0f || mode.SelectedTypeId == HomeValleyLayout.BuildingTypeCore)
            {
                if (_preview != null && _preview.enabled)
                {
                    _preview.enabled = false;
                }
                _previewKey = int.MinValue;
                PreviewRadius = 0f;
                return;
            }
            int key = mode.HoverCell.X * 73856093 ^ mode.HoverCell.Y * 19349663 ^ mode.SelectedTypeId.GetHashCode();
            if (key == _previewKey && _preview != null && _preview.enabled)
            {
                return;
            }
            _previewKey = key;
            EnsureRoot();
            if (_preview == null)
            {
                _preview = Line(new List<LineRenderer>(1), 0, "PowerPreviewRing");
            }
            _preview.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(1f, 1f, 1f, 0.9f));
            _preview.widthMultiplier = 0.7f;
            _preview.loop = true;
            float cx = mode.HoverCell.X;
            float cy = mode.HoverCell.Y;
            for (int n = 0; n < Segments; n++)
            {
                float a = n * Mathf.PI * 2f / Segments;
                Points[n] = new Vector3(cx + Mathf.Cos(a) * radius, Height + 0.04f, cy + Mathf.Sin(a) * radius);
            }
            _preview.positionCount = Segments;
            _preview.SetPositions(Points);
            _preview.enabled = true;
            PreviewRadius = radius;
        }

        private static void EnsureRoot()
        {
            if (_root == null)
            {
                _root = new GameObject("PowerCoverageOverlay");
            }
        }

        private static LineRenderer Line(List<LineRenderer> pool, int i, string name)
        {
            while (pool.Count <= i)
            {
                pool.Add(null);
            }
            if (pool[i] == null)
            {
                var go = new GameObject(name + i);
                go.transform.SetParent(_root.transform, false);
                LineRenderer r = go.AddComponent<LineRenderer>();
                r.useWorldSpace = true;
                r.numCapVertices = 0;
                r.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 平铺在地面上（与信号覆盖叠加层同一做法）。
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                pool[i] = r;
            }
            return pool[i];
        }

        private static WorldBadge BadgeAt(int i)
        {
            while (Badges.Count <= i)
            {
                Badges.Add(null);
            }
            if (Badges[i] == null)
            {
                Badges[i] = WorldBadge.Create(_root.transform, "PowerUnconnected" + i, Vector3.zero, 1.8f);
            }
            return Badges[i];
        }

        private static void HideAll()
        {
            foreach (LineRenderer r in Rings)
            {
                if (r != null)
                {
                    r.enabled = false;
                }
            }
            foreach (LineRenderer r in Links)
            {
                if (r != null)
                {
                    r.enabled = false;
                }
            }
            foreach (WorldBadge b in Badges)
            {
                b?.SetVisible(false);
            }
            if (_preview != null)
            {
                _preview.enabled = false;
            }
            _previewKey = int.MinValue;
            _shown = false;
            _drawnTopology = -1;
            DrawnRings = 0;
            DrawnLinks = 0;
            DrawnUnconnected = 0;
            PreviewRadius = 0f;
        }

        /// <summary>卸载整个世界（回主菜单 / 读档）/ 自检之间：销毁叠加层对象，开关复位。</summary>
        public static void Clear()
        {
            if (_root != null)
            {
                UnityObjects.Release(_root);
            }
            _root = null;
            _preview = null;
            Rings.Clear();
            Links.Clear();
            Badges.Clear();
            _shown = false;
            _drawnTopology = -1;
            _drawnKernel = null;
            _previewKey = int.MinValue;
            Enabled = false;
            DrawnRings = 0;
            DrawnLinks = 0;
            DrawnUnconnected = 0;
            PreviewRadius = 0f;
        }

        public static void ResetForTests()
        {
            Clear();
            RedrawCount = 0;
        }
    }
}
