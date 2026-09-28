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
            Refresh(marker.LogicId, CampaignSession.Current);
        }

        public static void OnViewDetached(int logicId)
        {
            Views.Remove(logicId);
            Badges.Remove(logicId);
        }

        // ── 每帧 ──

        public static void FrameTick()
        {
            if (_seenSafeRevision != SignalLinkService.SafeModeRevision)
            {
                _seenSafeRevision = SignalLinkService.SafeModeRevision;
                RefreshAll(CampaignSession.Current);
            }
            UpdateRing();
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
            _seenSafeRevision = -1;
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
