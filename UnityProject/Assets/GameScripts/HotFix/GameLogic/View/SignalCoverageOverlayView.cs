using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG1-SIG-07（FG01 FGR-SIG-050“战略地图有覆盖网络叠加层；与核心断开的中继不提供覆盖，地图上高亮断开的位置”）：覆盖网络叠加层（纯表现，不驱动任何游戏状态）。
    ///
    /// - 画镜头所在地点的全部覆盖源：与归还核心连通的 = 青色圈；断开的 = 红色粗圈 + 圆心上方一枚“断开的信号”形状标记（不只靠颜色，B15）。
    /// - 开关：“叠加层切换”键（默认 O，可重绑）或 HUD 的“覆盖网络”按钮；建造模式里选中信号塔 / 信号中继塔时自动显示（放置前就看得到能不能接上）。
    /// - 开销：每帧 O(1)（比较覆盖网络版本号、地点与开关）；网络变了才重画（O(覆盖源数 × 圆的段数)，每 0.5 游戏秒最多一次）。
    /// 美术是占位（B22）：圈是世界空间线条，断开标记复用安全模式的“断开的信号”图标。
    /// </summary>
    public static class SignalCoverageOverlayView
    {
        private const int Segments = 72;
        private const float Height = 0.12f;

        private static readonly List<LineRenderer> Rings = new List<LineRenderer>(16);
        private static readonly List<WorldBadge> CutBadges = new List<WorldBadge>(4);
        private static readonly Vector3[] Points = new Vector3[Segments];
        private static GameObject _root;
        private static int _drawnVersion = -1;
        private static string _drawnSite;
        private static bool _shown;

        /// <summary>玩家打开了叠加层（HUD 按钮 / O 键）。本局会话内有效，回主菜单复位。</summary>
        public static bool Enabled { get; private set; }

        /// <summary>此刻画着叠加层（手动打开，或建造模式里正在放信号塔 / 中继塔）。</summary>
        public static bool Visible => _shown;

        /// <summary>画出的圈数、其中断开的圈数、最近一次重画时的网络版本（自检 / 冒烟读取）。</summary>
        public static int DrawnRings { get; private set; }
        public static int DrawnCut { get; private set; }
        public static int RedrawCount { get; private set; }
        public static int ToggleCount { get; private set; }

        public static void Toggle() => SetEnabled(!Enabled);

        public static void SetEnabled(bool on)
        {
            if (Enabled == on)
            {
                return;
            }
            Enabled = on;
            ToggleCount++;
            _drawnVersion = -1;
            if (on)
            {
                GuidanceHooks.Raise(GuidanceHooks.SignalCoverageFirstOverlay);
            }
        }

        /// <summary>建造模式里正在放信号塔 / 信号中继塔：自动显示叠加层（不改玩家的开关）。</summary>
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
                return t == HomeValleyLayout.BuildingTypeSignalRelay || t == HomeValleyLayout.BuildingTypeSignalTower;
            }
        }

        /// <summary>每帧（世界模拟推进之后）。</summary>
        public static void FrameTick()
        {
            string site = WorldView.ObservedSiteId;
            bool want = (Enabled || AutoShown) && !string.IsNullOrEmpty(site) && SignalCoverageService.IsBoundedSite(site);
            if (!want)
            {
                if (_shown)
                {
                    HideAll();
                }
                return;
            }
            int version = SignalCoverageService.Version;
            // 网络版本只在重建时变（重建由采样 / 步首触发）；这里读一次源数，保证镜头所在地点的覆盖源是最新的。
            int count = SignalCoverageService.SiteSourceCount(site);
            if (_shown && version == _drawnVersion && site == _drawnSite)
            {
                return;
            }
            Redraw(site, count);
            _drawnVersion = SignalCoverageService.Version;
            _drawnSite = site;
            _shown = true;
        }

        private static void Redraw(string site, int count)
        {
            RedrawCount++;
            EnsureRoot();
            int cut = 0;
            int cutBadges = 0;
            for (int i = 0; i < count; i++)
            {
                if (!SignalCoverageService.TryGetSiteSource(site, i, out SignalCoverageSourceInfo s))
                {
                    continue;
                }
                LineRenderer ring = RingAt(i);
                ring.sharedMaterial = ViewMaterials.Get("Sprites/Default", s.Connected
                    ? new Color(0.25f, 0.85f, 0.95f, 0.75f)
                    : new Color(0.95f, 0.25f, 0.2f, 0.95f));
                ring.widthMultiplier = s.Connected ? 0.6f : 1.4f;
                for (int k = 0; k < Segments; k++)
                {
                    float a = k * Mathf.PI * 2f / Segments;
                    Points[k] = new Vector3(s.Center.x + Mathf.Cos(a) * s.Radius, Height, s.Center.y + Mathf.Sin(a) * s.Radius);
                }
                ring.positionCount = Segments;
                ring.SetPositions(Points);
                ring.enabled = true;
                if (!s.Connected)
                {
                    cut++;
                    WorldBadge badge = BadgeAt(cutBadges++);
                    badge.transform.position = new Vector3(s.Center.x, 2.6f, s.Center.y);
                    badge.SetIcon(SignalLinkService.SafeModeIconId);
                    badge.SetVisible(true);
                }
            }
            for (int i = count; i < Rings.Count; i++)
            {
                if (Rings[i] != null)
                {
                    Rings[i].enabled = false;
                }
            }
            for (int i = cutBadges; i < CutBadges.Count; i++)
            {
                CutBadges[i]?.SetVisible(false);
            }
            DrawnRings = count;
            DrawnCut = cut;
        }

        /// <summary>自检：第 i 个圈此刻是否显示、是否是“断开”样式（红色粗圈）。</summary>
        public static bool RingShown(int index, out bool cutStyle)
        {
            cutStyle = false;
            if (!_shown || index < 0 || index >= Rings.Count || Rings[index] == null || !Rings[index].enabled)
            {
                return false;
            }
            cutStyle = Rings[index].widthMultiplier > 1f;
            return true;
        }

        /// <summary>自检：显示中的“断开”标记数。</summary>
        public static int VisibleCutBadges
        {
            get
            {
                int n = 0;
                foreach (WorldBadge b in CutBadges)
                {
                    if (b != null && b.WantsVisible && b.IconId == SignalLinkService.SafeModeIconId)
                    {
                        n++;
                    }
                }
                return _shown ? n : 0;
            }
        }

        private static void EnsureRoot()
        {
            if (_root == null)
            {
                _root = new GameObject("SignalCoverageOverlay");
            }
        }

        private static LineRenderer RingAt(int i)
        {
            while (Rings.Count <= i)
            {
                Rings.Add(null);
            }
            if (Rings[i] == null)
            {
                var go = new GameObject("CoverageRing" + i);
                go.transform.SetParent(_root.transform, false);
                LineRenderer r = go.AddComponent<LineRenderer>();
                r.useWorldSpace = true;
                r.loop = true;
                r.numCapVertices = 0;
                r.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 平铺在地面上（与编队路线、覆盖边缘预警同一做法）。
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                Rings[i] = r;
            }
            return Rings[i];
        }

        private static WorldBadge BadgeAt(int i)
        {
            while (CutBadges.Count <= i)
            {
                CutBadges.Add(null);
            }
            if (CutBadges[i] == null)
            {
                CutBadges[i] = WorldBadge.Create(_root.transform, "CoverageCut" + i, Vector3.zero, 2.2f);
            }
            return CutBadges[i];
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
            foreach (WorldBadge b in CutBadges)
            {
                b?.SetVisible(false);
            }
            _shown = false;
            _drawnVersion = -1;
            DrawnRings = 0;
            DrawnCut = 0;
        }

        /// <summary>卸载整个世界（回主菜单 / 读档）/ 自检之间：销毁叠加层对象，开关复位。</summary>
        public static void Clear()
        {
            if (_root != null)
            {
                UnityObjects.Release(_root);
            }
            _root = null;
            Rings.Clear();
            CutBadges.Clear();
            _shown = false;
            _drawnVersion = -1;
            _drawnSite = null;
            Enabled = false;
            DrawnRings = 0;
            DrawnCut = 0;
        }

        public static void ResetForTests()
        {
            Clear();
            RedrawCount = 0;
            ToggleCount = 0;
        }
    }
}
