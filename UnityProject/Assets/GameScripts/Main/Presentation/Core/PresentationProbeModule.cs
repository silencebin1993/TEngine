// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>
    /// 宿主自检探针（功能 ID <see cref="PresentationFeatureRegistry.ProbeId"/>）：启用时在 [PresentationRoot] 下建 64 个方块，每帧移动，停用时全部销毁。
    /// 只在自检 / Lab 里登记进宿主，正式游戏不登记。
    /// PL-INF-07 重建协议探针：<see cref="TransientCount"/>（上次重建以来的帧数，Rebuild 时丢弃）、地点标记（Derived：挂在 0 号方块下，
    /// 每帧按 frame.ObservedSiteId 重算，Rebuild 时销毁、下一次 Tick 重建；不在世界里时不建）、<see cref="RebuildCount"/>（按原因计数）。
    /// 方块动画走真实时间（只由 frame.RealDt 驱动，不读 Time / Random，同一串合成帧两次结果逐字节相同）。
    /// </summary>
    [PresentationTime(TimeDomain.Real)]
    public sealed class PresentationProbeModule : IPresentationModule
    {
        public const int CubeCount = 64;

        private readonly Transform[] _cubes = new Transform[CubeCount];
        private float _t;
        private readonly int[] _rebuilds = new int[4];
        private Transform _siteMarker;
        private string _siteMarkerId;

        public const string SiteMarkerPrefix = "pl_probe_site_";

        /// <summary>Transient：上次 Rebuild（或 OnEnable）以来 Tick 的帧数。</summary>
        public int TransientCount { get; private set; }

        /// <summary>Derived：当前地点标记对应的地点（没有标记为 null）。</summary>
        public string SiteMarkerId => _siteMarker != null ? _siteMarkerId : null;

        public int RebuildCount(RebuildReason reason) => _rebuilds[(int)reason];

        public int RebuildTotal => _rebuilds[0] + _rebuilds[1] + _rebuilds[2] + _rebuilds[3];

        public string Id => PresentationFeatureRegistry.ProbeId;

        public void OnEnable()
        {
            Material m = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.6f, 0.6f, 0.6f));
            for (int i = 0; i < CubeCount; i++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.name = "pl_probe_" + i;
                go.GetComponent<MeshRenderer>().sharedMaterial = m;
                go.transform.SetParent(PresentationHost.Root, false);
                _cubes[i] = go.transform;
            }
        }

        public void OnDisable()
        {
            DropSiteMarker();
            TransientCount = 0;
            for (int i = 0; i < CubeCount; i++)
            {
                if (_cubes[i] != null)
                {
                    Object.DestroyImmediate(_cubes[i].gameObject);
                }
                _cubes[i] = null;
            }
        }

        public void Tick(in PresentationFrame frame)
        {
            TransientCount++;
            if (!frame.InWorld || frame.ObservedSiteId == null)
            {
                DropSiteMarker();
            }
            else if (_siteMarker == null || !string.Equals(_siteMarkerId, frame.ObservedSiteId))
            {
                DropSiteMarker();
                var go = new GameObject(SiteMarkerPrefix + frame.ObservedSiteId); // 只在地点变化时分配
                go.transform.SetParent(_cubes[0], false);
                _siteMarker = go.transform;
                _siteMarkerId = frame.ObservedSiteId;
            }
            _t += frame.RealDt;
            for (int i = 0; i < CubeCount; i++)
            {
                _cubes[i].localPosition = PresentationSpace.ToRender(new Unity.Mathematics.double3(i % 8, Mathf.Sin(_t + i), i / 8));
            }
        }

        public void Rebuild(RebuildReason reason)
        {
            _rebuilds[(int)reason]++;
            TransientCount = 0;
            DropSiteMarker();
        }

        private void DropSiteMarker()
        {
            if (_siteMarker != null)
            {
                Object.DestroyImmediate(_siteMarker.gameObject);
            }
            _siteMarker = null;
            _siteMarkerId = null;
        }
    }
}
