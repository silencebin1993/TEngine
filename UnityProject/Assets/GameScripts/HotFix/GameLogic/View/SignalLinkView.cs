using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG1-SIG-04 的世界表现（纯表现，不驱动任何游戏状态）：
    /// 1. 安全模式头顶图标（FGR-SIG-041）：处于安全模式的机器头顶一枚形状标记（<see cref="SignalLinkService.SafeModeIconId"/>，
    ///    盾形 + 断开的信号，不只靠颜色）。只在进入安全模式时才为那台机器建标记；按 <see cref="SignalLinkService.SafeModeRevision"/> 变化刷新
    ///    （O(已挂表现的机器数)，只在安全模式变化那一帧），平时每帧 O(1)。
    /// 2. 地图上的覆盖边缘预警（卡片“必须同时交付”）：被接入的机器接近覆盖边缘或已走出覆盖（宽限中）时，在地面画出罩着它的那个覆盖圈
    ///    （接近边缘黄色、宽限中红色）；圆心 / 半径不变时不重算顶点。覆盖网络的完整叠加层与断开处高亮在 FG1-SIG-07。
    /// 由 <c>WorldSimulation.Frame</c> 每帧调用 <see cref="FrameTick"/>；机器表现挂上 / 摘下时由 <see cref="HomeValleyMachineMarker"/> 通知。
    /// </summary>
    public static class SignalLinkView
    {
        private const int RingSegments = 96;
        private const float RingHeight = 0.08f;
        private const float BadgeHeight = 1.9f; // 与敌人头顶标记同一高度（机身剪影都在 2.3 以下）。
        private const float BadgeSize = 0.95f;

        private static readonly Dictionary<int, MachineView> Views = new Dictionary<int, MachineView>(32);
        private static readonly Dictionary<int, WorldBadge> Badges = new Dictionary<int, WorldBadge>(8);
        // FG1-HUD-01（FGR-SIG-081“带接入口的机器在地图上有专门标记”）：战略视角下机器头侧的菱形标记（接入视角里隐藏，不挡驾驶视野）。
        // 只在装配登记版本（带不带接入口）/ 镜头模式 / 表现挂上时刷新：O(已挂表现的机器数) 且只在变化那一帧，平时每帧 O(1)。
        private static readonly Dictionary<int, WorldBadge> PortBadges = new Dictionary<int, WorldBadge>(16);
        private const float PortBadgeHeight = 1.45f;
        private const float PortBadgeSide = 0.95f;
        private const float PortBadgeSize = 0.8f;
        private static int _seenPortRevision = -1;
        private static bool _seenStrategy;

        /// <summary>这台机器头侧是否挂着“应当显示”的接入口标记（自检读）。</summary>
        public static bool PortBadgeWanted(int logicId) =>
            PortBadges.TryGetValue(logicId, out WorldBadge b) && b != null && b.WantsVisible && b.IconId == GameLogic.UI.Common.ContentIcons.StateUplinkPort;

        /// <summary>自检读点：接入口标记整体刷新的次数。</summary>
        public static int PortRefreshCount { get; private set; }

        // FG1-HUD-01（FGR-SIG-081“靠近边缘时变色并加图标”）：被接入的机器接近覆盖边缘 / 宽限中时，头顶加倒三角标记（与地面上的黄 / 红预警圈同时出现）。
        private static WorldBadge _edgeBadge;
        private static int _edgeBadgeLogicId;

        /// <summary>边缘预警的头顶标记此刻是否“应当显示”、挂在哪台机器上（自检读）。</summary>
        public static bool EdgeBadgeWanted => _edgeBadge != null && _edgeBadge.WantsVisible;
        public static int EdgeBadgeLogicId => EdgeBadgeWanted ? _edgeBadgeLogicId : 0;
        private static readonly List<int> Scratch = new List<int>(32);
        private static int _seenSafeRevision = -1;

        private static LineRenderer _ring;
        private static Vector2 _ringCenter;
        private static float _ringRadius = -1f;
        private static bool _ringDanger;

        /// <summary>地图预警圈此刻是否显示（自检 / 冒烟读取）。</summary>
        public static bool RingVisible => _ring != null && _ring.enabled;
        public static Vector2 RingCenter => _ringCenter;
        public static float RingRadius => _ringRadius;
        /// <summary>true = 红色（已走出覆盖、宽限中）；false = 黄色（接近边缘）。</summary>
        public static bool RingDanger => _ringDanger;
        public static int RingPointCount => _ring != null ? _ring.positionCount : 0;

        /// <summary>这台机器的安全模式头顶标记（没有建过为 null）。</summary>
        public static WorldBadge BadgeFor(int logicId) => Badges.TryGetValue(logicId, out WorldBadge b) && b != null ? b : null;

        /// <summary>这台机器头顶是否挂着“应当显示”的安全模式图标（贴图在 Play 里异步加载；编辑模式自检读这个）。</summary>
        public static bool BadgeWanted(int logicId)
        {
            WorldBadge b = BadgeFor(logicId);
            return b != null && b.WantsVisible && b.IconId == SignalLinkService.SafeModeIconId;
        }

        // ── 机器表现挂上 / 摘下 ──

        public static void OnViewAttached(HomeValleyMachineMarker marker)
        {
            if (marker?.View == null)
            {
                return;
            }
            Views[marker.LogicId] = marker.View;
            if (Badges.TryGetValue(marker.LogicId, out WorldBadge old) && (old == null || old.transform.parent != marker.View.transform))
            {
                Badges.Remove(marker.LogicId); // 表现对象换了（重新观察这个地点）：旧标记随旧对象销毁。
            }
            if (PortBadges.TryGetValue(marker.LogicId, out WorldBadge oldPort) && (oldPort == null || oldPort.transform.parent != marker.View.transform))
            {
                PortBadges.Remove(marker.LogicId);
            }
            Refresh(marker.LogicId, CampaignSession.Current);
            _seenStrategy = IsStrategyView;
            RefreshPort(marker.LogicId, CampaignSession.Current);
        }

        public static void OnViewDetached(int logicId)
        {
            Views.Remove(logicId);
            Badges.Remove(logicId);
            PortBadges.Remove(logicId);
            if (_edgeBadgeLogicId == logicId)
            {
                _edgeBadge = null; // 标记挂在表现对象下面，随它一起销毁。
                _edgeBadgeLogicId = 0;
            }
        }

        // ── 每帧 ──

        public static void FrameTick()
        {
            if (_seenSafeRevision != SignalLinkService.SafeModeRevision)
            {
                _seenSafeRevision = SignalLinkService.SafeModeRevision;
                RefreshAll(CampaignSession.Current);
            }
            bool strategy = IsStrategyView;
            if (_seenPortRevision != UplinkHudModel.PortRevision || strategy != _seenStrategy)
            {
                _seenPortRevision = UplinkHudModel.PortRevision;
                _seenStrategy = strategy;
                RefreshAllPorts(CampaignSession.Current);
            }
            UpdateRing();
            UpdateEdgeBadge();
        }

        private static void UpdateEdgeBadge()
        {
            bool want = SignalLinkService.EdgeWarningActive && SignalLinkService.WatchedSample.Bounded;
            int id = want ? SignalLinkService.WatchedLogicId : 0;
            if (_edgeBadge != null && (_edgeBadgeLogicId != id && id != 0))
            {
                UnityObjects.Release(_edgeBadge.gameObject); // 换了一台机器：旧标记收掉，下面按新机器重建。
                _edgeBadge = null;
            }
            if (id != 0 && _edgeBadge == null && Views.TryGetValue(id, out MachineView view) && view != null)
            {
                Transform t = view.transform;
                _edgeBadge = WorldBadge.Create(t, "LinkEdgeBadge", t.position + Vector3.up * (BadgeHeight + 0.9f), BadgeSize);
                _edgeBadge.SetIcon(GameLogic.UI.Common.ContentIcons.StateLinkEdge);
                _edgeBadgeLogicId = id;
            }
            if (_edgeBadge != null)
            {
                _edgeBadge.SetVisible(id != 0 && id == _edgeBadgeLogicId);
            }
        }

        /// <summary>战略视角（含切到战略的过渡）= 地图；接入视角隐藏接入口标记。</summary>
        private static bool IsStrategyView
        {
            get
            {
                CameraDirector d = GameLogic.Campaign.WorldSim.WorldView.Director;
                return d == null || !d.IsBound || !d.HeadingDirect;
            }
        }

        private static void RefreshAllPorts(CampaignState s)
        {
            PortRefreshCount++;
            Scratch.Clear();
            Scratch.AddRange(Views.Keys);
            foreach (int id in Scratch)
            {
                RefreshPort(id, s);
            }
        }

        private static void RefreshPort(int logicId, CampaignState s)
        {
            if (!Views.TryGetValue(logicId, out MachineView view) || view == null)
            {
                return;
            }
            bool want = _seenStrategy && s != null && UplinkHudModel.HasUplinkPort(s, logicId);
            PortBadges.TryGetValue(logicId, out WorldBadge badge);
            if (badge == null && want)
            {
                Transform t = view.transform;
                badge = WorldBadge.Create(t, "UplinkPortBadge", t.position + Vector3.up * PortBadgeHeight + Vector3.right * PortBadgeSide, PortBadgeSize);
                badge.SetIcon(GameLogic.UI.Common.ContentIcons.StateUplinkPort);
                PortBadges[logicId] = badge;
            }
            badge?.SetVisible(want);
        }

        private static void RefreshAll(CampaignState s)
        {
            Scratch.Clear();
            Scratch.AddRange(Views.Keys);
            foreach (int id in Scratch)
            {
                Refresh(id, s);
            }
        }

        private static void Refresh(int logicId, CampaignState s)
        {
            if (!Views.TryGetValue(logicId, out MachineView view) || view == null)
            {
                Views.Remove(logicId);
                Badges.Remove(logicId);
                return;
            }
            bool want = SignalLinkService.IsInSafeMode(s, logicId);
            Badges.TryGetValue(logicId, out WorldBadge badge);
            if (badge == null && want)
            {
                Transform t = view.transform;
                badge = WorldBadge.Create(t, "SafeModeBadge", t.position + Vector3.up * BadgeHeight, BadgeSize);
                badge.SetIcon(SignalLinkService.SafeModeIconId);
                Badges[logicId] = badge;
            }
            badge?.SetVisible(want);
        }

        private static void UpdateRing()
        {
            bool show = SignalLinkService.EdgeWarningActive && SignalLinkService.WatchedSample.Bounded;
            if (!show)
            {
                if (_ring != null)
                {
                    _ring.enabled = false;
                }
                return;
            }
            SignalCoverageSample sample = SignalLinkService.WatchedSample;
            bool danger = SignalLinkService.GraceReason == SignalLinkBreakReason.OutOfCoverage;
            if (_ring == null)
            {
                var go = new GameObject("SignalCoverageEdgeWarning");
                _ring = go.AddComponent<LineRenderer>();
                _ring.useWorldSpace = true;
                _ring.loop = true;
                _ring.widthMultiplier = 0.4f;
                _ring.numCapVertices = 0;
                _ring.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 平铺在地面上，俯视镜头下粗细稳定（与编队路线同一做法）。
                _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _ring.receiveShadows = false;
                _ringRadius = -1f;
                _ringDanger = !danger;
            }
            if (danger != _ringDanger || !_ring.enabled)
            {
                _ringDanger = danger;
                Color c = danger ? new Color(0.9f, 0.25f, 0.15f, 0.9f) : new Color(0.95f, 0.8f, 0.2f, 0.85f);
                _ring.sharedMaterial = ViewMaterials.Get("Sprites/Default", c);
            }
            if (!Mathf.Approximately(sample.SourceRadius, _ringRadius) || (sample.SourceCenter - _ringCenter).sqrMagnitude > 1e-4f)
            {
                _ringCenter = sample.SourceCenter;
                _ringRadius = sample.SourceRadius;
                _ring.positionCount = RingSegments;
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / RingSegments;
                    _ring.SetPosition(i, new Vector3(_ringCenter.x + Mathf.Cos(a) * _ringRadius, RingHeight, _ringCenter.y + Mathf.Sin(a) * _ringRadius));
                }
            }
            _ring.enabled = true;
        }

        /// <summary>卸载整个世界（回主菜单 / 读档）/ 自检之间：清掉登记与地图预警圈（标记随机器表现对象一起销毁）。</summary>
        public static void Clear()
        {
            Views.Clear();
            Badges.Clear();
            PortBadges.Clear();
            _seenSafeRevision = -1;
            _seenPortRevision = -1;
            _edgeBadge = null;
            _edgeBadgeLogicId = 0;
            if (_ring != null)
            {
                UnityObjects.Release(_ring.gameObject);
            }
            _ring = null;
            _ringRadius = -1f;
            _ringCenter = default;
        }
    }
}
