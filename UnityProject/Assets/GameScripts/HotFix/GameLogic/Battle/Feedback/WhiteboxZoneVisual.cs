using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模区域可视化（story-007 默认实现）。
    ///
    /// 常驻同步的圆盘池：每帧把 <see cref="AreaZoneSystem.Zones"/> 的 [0, Count) 段
    /// 写到对应 index 的圆盘（位置/缩放/颜色/可见），多出的旧 index 隐藏。
    /// 不追踪 Zone↔GameObject 稳定身份——同一 Zone 在同一帧内对应同一 index 已足够
    /// 满足"随生成/过期出现消失"的视觉要求。
    ///
    /// 池化/材质/坐标手法照抄 <see cref="WhiteboxCombatFeedback"/> 已验证可行的路径。
    /// </summary>
    public sealed class WhiteboxZoneVisual : IZoneVisualFeedback, System.IDisposable
    {
        private const int PoolSize = 64;
        private const float DiscY = -0.02f;
        private const int CircleSegments = 24;

        private GameObject _poolRoot;
        private Transform[] _discTf;
        private MeshRenderer[] _discRenderer;
        private Mesh _circleMesh;
        private Material _matTemplate;
        private int _lastActiveCount;

        public void Sync(IReadOnlyList<AreaZoneSystem.Zone> zones)
        {
            EnsurePool();

            int count = zones.Count;
            for (int i = 0; i < count && i < PoolSize; i++)
            {
                AreaZoneSystem.Zone z = zones[i];
                Transform tf = _discTf[i];
                tf.localPosition = new Vector3(z.Center.x, DiscY, z.Center.y);
                tf.localScale = Vector3.one * (z.Radius * 2f);
                _discRenderer[i].enabled = true;
                _discRenderer[i].sharedMaterial.color = ColorFor(z.Kind);
            }

            int hideFrom = Mathf.Min(count, PoolSize);
            int hideTo = Mathf.Min(_lastActiveCount, PoolSize);
            for (int i = hideFrom; i < hideTo; i++)
            {
                _discRenderer[i].enabled = false;
            }

            _lastActiveCount = count;
        }

        private static Color ColorFor(AreaZoneSystem.ZoneKind kind)
        {
            switch (kind)
            {
                case AreaZoneSystem.ZoneKind.Mycelium:
                    return new Color(0.35f, 0.9f, 0.4f, 0.35f);
                case AreaZoneSystem.ZoneKind.Caustic:
                    return new Color(0.75f, 0.25f, 0.85f, 0.4f);
                case AreaZoneSystem.ZoneKind.Conductive:
                    return new Color(0.25f, 0.75f, 1.0f, 0.4f);
                case AreaZoneSystem.ZoneKind.Roots:
                    return new Color(0.55f, 0.4f, 0.15f, 0.4f);
                default:
                    return new Color(0.6f, 0.6f, 0.6f, 0.3f);
            }
        }

        private void EnsurePool()
        {
            if (_poolRoot != null)
            {
                return;
            }

            _circleMesh = BuildCircle(CircleSegments);
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            _matTemplate = new Material(shader) { color = Color.white };

            _poolRoot = new GameObject("ZoneVisual_DiscPool");
            _discTf = new Transform[PoolSize];
            _discRenderer = new MeshRenderer[PoolSize];

            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Disc_{i}");
                go.transform.SetParent(_poolRoot.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _circleMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(_matTemplate);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;

                _discTf[i] = go.transform;
                _discRenderer[i] = mr;
            }
        }

        /// <summary>俯视三角扇圆盘，半径 0.5 单位圆；实际半径靠 localScale 缩放。</summary>
        private static Mesh BuildCircle(int segments)
        {
            var verts = new List<Vector3>(segments + 1) { Vector3.zero };
            var tris = new List<int>(segments * 3);

            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
            }

            for (int i = 1; i <= segments; i++)
            {
                tris.Add(0);
                tris.Add(i);
                tris.Add(i + 1);
            }

            var m = new Mesh { name = "ZoneVisualDisc" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public void Dispose()
        {
            if (_poolRoot != null)
            {
                for (int i = 0; i < _discRenderer.Length; i++)
                {
                    if (_discRenderer[i] != null)
                    {
                        Object.Destroy(_discRenderer[i].sharedMaterial);
                    }
                }

                Object.Destroy(_poolRoot);
                _poolRoot = null;
                _discTf = null;
                _discRenderer = null;
            }

            if (_matTemplate != null)
            {
                Object.Destroy(_matTemplate);
                _matTemplate = null;
            }

            if (_circleMesh != null)
            {
                Object.Destroy(_circleMesh);
                _circleMesh = null;
            }

            _lastActiveCount = 0;
        }
    }
}
