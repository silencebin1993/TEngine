using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace BinGames.Sim
{
    /// <summary>一种视觉表现的渲染资源。</summary>
    public struct SimVisual
    {
        public Mesh Mesh;
        public Material Material;
        public float ScaleMul;
        public Color BaseColor;
    }

    /// <summary>
    /// GPU 实例化渲染。
    ///
    /// 因为本工程是 Built-in RP，官方 Entities Graphics 不可用（见框架文档 §2.1），
    /// 所以自己按 VisualId 分批走 Graphics.RenderMeshInstanced，每批上限 1023。
    /// 10k 单位约 10-15 个 draw call，Built-in RP 完全够用。
    /// </summary>
    public sealed class SimRenderer
    {
        private const int BatchMax = 1023;

        private SimVisual[] _visuals;
        private Matrix4x4[][] _matrices;
        private Vector4[][] _colors;
        private int[] _counts;
        private MaterialPropertyBlock _props;
        private Matrix4x4[] _projMatrices;

        private float _yPlane;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public void Initialize(SimVisual[] visuals, int capacity, float yPlane = 0f)
        {
            _visuals = visuals ?? new SimVisual[0];
            _yPlane = yPlane;
            _props = new MaterialPropertyBlock();

            int n = _visuals.Length;
            _matrices = new Matrix4x4[n][];
            _colors = new Vector4[n][];
            _counts = new int[n];
            for (int i = 0; i < n; i++)
            {
                _matrices[i] = new Matrix4x4[capacity];
                _colors[i] = new Vector4[capacity];
            }
            _projMatrices = new Matrix4x4[BatchMax];
        }

        public void Draw(in SimSnapshot snap)
        {
            if (_visuals == null || _visuals.Length == 0)
            {
                return;
            }

            for (int i = 0; i < _counts.Length; i++)
            {
                _counts[i] = 0;
            }

            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0)
                {
                    continue;
                }

                int v = snap.VisualId[i];
                if (v < 0 || v >= _visuals.Length)
                {
                    v = 0;
                }

                int c = _counts[v];
                if (c >= _matrices[v].Length)
                {
                    continue;
                }

                float2 p = snap.Position[i];
                float s = snap.Radius[i] * 2f * _visuals[v].ScaleMul;
                _matrices[v][c] = Matrix4x4.TRS(
                    new Vector3(p.x, _yPlane, p.y),
                    Quaternion.identity,
                    new Vector3(s, s, s));

                _colors[v][c] = Tint(_visuals[v].BaseColor, snap.Status[i]);
                _counts[v] = c + 1;
            }

            for (int v = 0; v < _visuals.Length; v++)
            {
                int total = _counts[v];
                if (total == 0 || _visuals[v].Mesh == null || _visuals[v].Material == null)
                {
                    continue;
                }

                var rp = new RenderParams(_visuals[v].Material)
                {
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                    shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                    receiveShadows = false,
                };

                for (int off = 0; off < total; off += BatchMax)
                {
                    int n = Mathf.Min(BatchMax, total - off);
                    System.Array.Copy(_matrices[v], off, _projMatrices, 0, n);
                    Graphics.RenderMeshInstanced(rp, _visuals[v].Mesh, 0, _projMatrices, n);
                }
            }
        }

        /// <summary>投射物渲染。数量少，用一个固定视觉。</summary>
        public void DrawProjectiles(NativeArray<ProjectileState> projectiles, in SimVisual visual)
        {
            if (visual.Mesh == null || visual.Material == null || !projectiles.IsCreated)
            {
                return;
            }

            int n = 0;
            var rp = new RenderParams(visual.Material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = false,
            };

            for (int i = 0; i < projectiles.Length; i++)
            {
                ProjectileState s = projectiles[i];
                if (s.Alive == 0)
                {
                    continue;
                }

                float ang = math.atan2(s.Velocity.y, s.Velocity.x) * Mathf.Rad2Deg;
                float sc = s.Radius * 2f * visual.ScaleMul;
                _projMatrices[n++] = Matrix4x4.TRS(
                    new Vector3(s.Position.x, _yPlane, s.Position.y),
                    Quaternion.Euler(0f, -ang, 0f),
                    new Vector3(sc, sc, sc));

                if (n == BatchMax)
                {
                    Graphics.RenderMeshInstanced(rp, visual.Mesh, 0, _projMatrices, n);
                    n = 0;
                }
            }

            if (n > 0)
            {
                Graphics.RenderMeshInstanced(rp, visual.Mesh, 0, _projMatrices, n);
            }
        }

        /// <summary>
        /// 状态染色。这是"万敌规模下可读性"风险项的对策（Spec §16）：
        /// 首领/精英/导电/破体/腐蚀各有明确色彩偏移，玩家能一眼分层。
        /// </summary>
        private static Vector4 Tint(Color baseColor, uint status)
        {
            Color c = baseColor;
            if ((status & (uint)SimStatus.Boss) != 0u)
            {
                c = Color.Lerp(c, new Color(1f, 0.35f, 0.1f), 0.55f);
            }
            else if ((status & (uint)SimStatus.Elite) != 0u)
            {
                c = Color.Lerp(c, new Color(1f, 0.8f, 0.2f), 0.45f);
            }
            if ((status & (uint)SimStatus.Conductive) != 0u)
            {
                c = Color.Lerp(c, new Color(0.4f, 0.8f, 1f), 0.4f);
            }
            if ((status & (uint)SimStatus.Breached) != 0u)
            {
                c = Color.Lerp(c, new Color(1f, 0.4f, 0.4f), 0.3f);
            }
            if ((status & (uint)SimStatus.Corroded) != 0u)
            {
                c = Color.Lerp(c, new Color(0.55f, 0.85f, 0.3f), 0.3f);
            }
            if ((status & (uint)SimStatus.Marked) != 0u)
            {
                c = Color.Lerp(c, Color.magenta, 0.25f);
            }
            return new Vector4(c.r, c.g, c.b, c.a);
        }

        public void Dispose()
        {
            _visuals = null;
            _matrices = null;
            _colors = null;
            _counts = null;
            _props = null;
            _projMatrices = null;
        }
    }
}
