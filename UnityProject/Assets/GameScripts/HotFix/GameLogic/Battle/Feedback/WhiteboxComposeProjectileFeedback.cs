using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模组合弹道反馈（story-004 默认实现）。
    ///
    /// 只读 <see cref="ComposeCastSignal"/> 的字段选几何模板（story-001 §2 映射表），
    /// **禁止按 org_*/OrganelleId/GeneId 分支**——玩家换装配后，白模必须立刻随 Shape/Scale/
    /// Count/Spin/Orbit 变化，不认器官身份。
    ///
    /// 半径/段数与 <see cref="MetabolicSliceBridge.ApplyEvent"/> 用同一公式（Decision D3/D4），
    /// 禁止另起系数分叉；运动复用 story-003 <see cref="ComposeMotionMath.Offset"/>（Decision D5），
    /// Spin=Orbit=0 时天然退化为原地标记，与 ApplyEvent 的瞬时 AoE 语义一致。
    ///
    /// 全部运行期程序化创建（不依赖 prefab/UI 资源）+ 固定对象池，局末随 <see cref="Dispose"/> 清理，
    /// 手法照抄 <see cref="WhiteboxAbilityCastFeedback"/>。
    /// </summary>
    public sealed class WhiteboxComposeProjectileFeedback : IComposeProjectileFeedback, IDisposable
    {
        private enum ShapeKind
        {
            Bolt,
            Beam,
            Arc,
            Field,
            Wave,
            Spore,
        }

        private const int PoolSize = 32;
        private const float MarkerY = 0.19f;

        // 生命周期常量（Decision D7）：Bolt/Arc/Wave/Spore 对齐 003 已锁死的真实命中延迟
        // ComposeMotionMath.MotionFlightDuration=0.3f，保证白模消失时刻与真实结算命中同步；
        // Beam/Field 是"常驻类"，按 story-001 §2 映射表描述略长，给人看清弹道方向/范围的时间。
        private const float FlightLife = ComposeMotionMath.MotionFlightDuration;
        private const float PersistentLife = 0.5f;

        // 尺寸系数（Decision D3：必须是 radius 的线性函数，不能是与 Scale 无关的固定值）
        private const float BoltDiameterCoef = 0.35f;
        private const float SporeDiameterCoef = 0.3f;
        private const float BeamLengthCoef = 1.5f;
        private const float BeamWidthCoef = 0.25f;
        private const float ArcHalfAngleDeg = 40f;
        private const int ArcSegments = 10;
        private const int CircleSegments = 20;
        private const float RingInnerRatio = 0.65f;

        private GameObject _poolRoot;
        private Transform[] _tf;
        private MeshFilter[] _mf;
        private MeshRenderer[] _mr;
        private ShapeKind[] _kind;
        private float2[] _origin;
        private float2[] _direction;
        private float[] _phase;
        private float[] _spin;
        private float[] _orbit;
        private float[] _radius;
        private float[] _elapsed;
        private float[] _timeLeft;
        private float[] _life;
        private Color[] _color;
        private int _cursor;

        private Mesh _circleMesh;
        private Mesh _streakMesh;
        private Mesh _wedgeMesh;
        private Mesh _ringMesh;
        private Material _matTemplate;

        /// <summary>当前处于寿命内的白模标记数（story-004 验收探针，同 <see cref="MetabolicSliceBridge.PendingMotionCount"/> 惯例）。</summary>
        public int ActiveMarkerCount { get; private set; }

        /// <summary>最近一次 <see cref="OnComposeCast"/> 算出的半径（Decision D3 校验探针）。</summary>
        public float LastComputedRadius { get; private set; }

        /// <summary>最近一次实际生成的段数（Beam 恒为 1，其余等于 segments）（Decision D4 校验探针）。</summary>
        public int LastSegmentCount { get; private set; }

        /// <summary>最近一次命中的 Shape 几何分支（Decision D10 校验探针，用于断言不同 Shape 走了不同路径）。</summary>
        public string LastShapeKind { get; private set; } = "";

        /// <summary>最近一次生成的爆炸环半径（story-007 D9 校验探针，无 Explode 时为 0）。</summary>
        public float LastExplodeRadius { get; private set; }

        /// <summary>最近一次命中的主元素 Tag（story-007 D9 校验探针，未命中任何元素词表时为 ""）。</summary>
        public string LastResolvedElementTag { get; private set; } = "";

        public void OnComposeCast(ComposeCastSignal signal)
        {
            if (!signal.HasProjectile)
            {
                // D2：Heal/Shield/Displace/无 Damage 的 Spin/Orbit 摘要不生成弹体几何，那是其它系统的表现范畴。
                return;
            }

            // 与 MetabolicSliceBridge.ApplyEvent 同一行公式（Decision D3/D4），禁止另起系数。
            float radius = MetabolicSliceBridge.DamageAreaRadius * MathF.Max(0.1f, signal.Scale);
            int segments = Math.Max(1, (int)MathF.Round(signal.Count));
            ShapeKind kind = ParseShape(signal.Shape);

            // story-007 D1：元素 Tag 优先，无则退回 Shape 底色；无元素 Tag 命中时 castColor 与旧行为逐位一致。
            string elementTag = ResolveElementTag(signal.Tags);
            Color castColor = elementTag.Length > 0 ? ElementColorFor(elementTag) : ColorFor(kind);
            LastResolvedElementTag = elementTag;
            LastComputedRadius = radius;
            LastShapeKind = kind.ToString();

            if (kind == ShapeKind.Beam)
            {
                // Beam 例外（Decision D4）：Count 对单段常驻弹道无意义，固定渲染 1 段，不随 segments 循环。
                LastSegmentCount = 1;
                SpawnMarker(kind, signal.Origin, signal.Direction, 0f, 0f, 0f, radius, PersistentLife, castColor);
            }
            else
            {
                LastSegmentCount = segments;
                float life = kind == ShapeKind.Field ? PersistentLife : FlightLife;
                for (int h = 0; h < segments; h++)
                {
                    // 与 PendingMotionHit 生成时同一分片公式（Decision D5）。
                    float phase = 2f * MathF.PI * h / segments;
                    SpawnMarker(kind, signal.Origin, signal.Direction, phase, signal.Spin, signal.Orbit, radius, life, castColor);
                }
            }

            // story-007 D5/D6/D7/D8：Explode 是独立叠加标记，与 Shape 正交，禁止并入上面的分支判断。
            if (signal.ExplodeOnHit)
            {
                float explodeRadius = radius * MetabolicSliceBridge.ExplodeRadiusMult;
                Color explodeColor = elementTag.Length > 0 ? castColor : DefaultExplodeColor;
                LastExplodeRadius = explodeRadius;
                SpawnMarker(ShapeKind.Wave, signal.Origin, signal.Direction, 0f, 0f, 0f, explodeRadius, FlightLife, explodeColor);
            }
            else
            {
                LastExplodeRadius = 0f;
            }
        }

        // ── 元素 Tag 染色（story-007 D1~D4，对照冻结总案 §3.4 元素词表全 10 项）──

        // 数组顺序即优先级（Decision D3：战斗强调型 Fire/Shock/Acid/Ice 优先于环境覆盖型 Steam/Wet/Water/Oil，
        // Light/Dark 零 producer 排最后），first-match-wins，不做多色混合。
        private static readonly string[] ElementPriorityOrder =
        {
            "Fire", "Shock", "Acid", "Ice", "Steam", "Wet", "Water", "Oil", "Light", "Dark",
        };

        private static readonly Color FireColor = new Color(1f, 0.35f, 0.1f, 0.9f);
        private static readonly Color ShockColor = new Color(0.75f, 0.35f, 1f, 0.9f);
        private static readonly Color AcidColor = new Color(0.55f, 0.85f, 0.15f, 0.9f);
        private static readonly Color IceColor = new Color(0.6f, 0.9f, 1f, 0.9f);
        private static readonly Color SteamColor = new Color(0.85f, 0.85f, 0.9f, 0.8f);
        private static readonly Color WetColor = new Color(0.3f, 0.55f, 1f, 0.85f);
        private static readonly Color WaterColor = new Color(0.15f, 0.4f, 0.85f, 0.85f);
        private static readonly Color OilColor = new Color(0.35f, 0.25f, 0.15f, 0.9f);
        private static readonly Color LightColor = new Color(1f, 0.95f, 0.6f, 0.9f);
        private static readonly Color DarkColor = new Color(0.25f, 0.1f, 0.35f, 0.9f);

        // D8：爆炸环兜底默认色（无元素 Tag 命中时使用，不借用任何单一 Shape 的 ColorFor）。
        private static readonly Color DefaultExplodeColor = new Color(1f, 0.55f, 0.15f, 0.6f);

        private static string ResolveElementTag(HashSet<string> tags)
        {
            if (tags == null)
            {
                return "";
            }

            for (int i = 0; i < ElementPriorityOrder.Length; i++)
            {
                if (tags.Contains(ElementPriorityOrder[i]))
                {
                    return ElementPriorityOrder[i];
                }
            }

            return "";
        }

        private static Color ElementColorFor(string tag)
        {
            switch (tag)
            {
                case "Fire": return FireColor;
                case "Shock": return ShockColor;
                case "Acid": return AcidColor;
                case "Ice": return IceColor;
                case "Steam": return SteamColor;
                case "Wet": return WetColor;
                case "Water": return WaterColor;
                case "Oil": return OilColor;
                case "Light": return LightColor;
                case "Dark": return DarkColor;
                default: return Color.white;
            }
        }

        public void Tick(float dt)
        {
            if (_timeLeft == null)
            {
                return;
            }

            int active = 0;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_timeLeft[i] <= 0f)
                {
                    continue;
                }

                _elapsed[i] += dt;
                _timeLeft[i] -= dt;
                if (_timeLeft[i] <= 0f)
                {
                    // 到点即回收，不循环重放（Decision D5）。
                    _timeLeft[i] = 0f;
                    _mr[i].enabled = false;
                    continue;
                }

                active++;
                ApplyTransform(i);

                float u = 1f - (_timeLeft[i] / _life[i]);
                Color c = _color[i];
                c.a *= (1f - u) * (1f - u);
                _mr[i].sharedMaterial.color = c;
            }

            ActiveMarkerCount = active;
        }

        // ── Shape 路由（Decision D6，引用 story-001 §2 映射表，禁止按 org_* id 分支）──

        private static ShapeKind ParseShape(string shape)
        {
            switch (shape)
            {
                case "Beam": return ShapeKind.Beam;
                case "Arc": return ShapeKind.Arc;
                case "Field": return ShapeKind.Field;
                case "Wave": return ShapeKind.Wave;
                case "Spore": return ShapeKind.Spore;
                case "Bolt":
                default: return ShapeKind.Bolt;
            }
        }

        private static readonly Color BoltColor = new Color(1f, 0.95f, 0.6f, 0.95f);
        private static readonly Color BeamColor = new Color(0.6f, 0.95f, 1f, 0.85f);
        private static readonly Color ArcColor = new Color(1f, 0.5f, 0.15f, 0.55f);
        private static readonly Color FieldColor = new Color(0.25f, 0.9f, 0.65f, 0.4f);
        private static readonly Color WaveColor = new Color(0.5f, 0.85f, 1f, 0.8f);
        private static readonly Color SporeColor = new Color(0.78f, 0.5f, 1f, 0.9f);

        private static Color ColorFor(ShapeKind kind)
        {
            switch (kind)
            {
                case ShapeKind.Beam: return BeamColor;
                case ShapeKind.Arc: return ArcColor;
                case ShapeKind.Field: return FieldColor;
                case ShapeKind.Wave: return WaveColor;
                case ShapeKind.Spore: return SporeColor;
                case ShapeKind.Bolt:
                default: return BoltColor;
            }
        }

        private Mesh MeshFor(ShapeKind kind)
        {
            switch (kind)
            {
                case ShapeKind.Beam: return _streakMesh;
                case ShapeKind.Arc: return _wedgeMesh;
                case ShapeKind.Wave: return _ringMesh;
                case ShapeKind.Field:
                case ShapeKind.Spore:
                case ShapeKind.Bolt:
                default: return _circleMesh;
            }
        }

        // ── 对象池（禁止逐次 Instantiate/Destroy，手法照抄 WhiteboxAbilityCastFeedback）──

        private void EnsurePool()
        {
            if (_poolRoot != null)
            {
                return;
            }

            _circleMesh = BuildCircle(CircleSegments);
            _streakMesh = BuildStreak();
            _wedgeMesh = BuildWedge(ArcHalfAngleDeg, ArcSegments);
            _ringMesh = BuildRing(RingInnerRatio, CircleSegments);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            _matTemplate = new Material(shader) { color = Color.white };

            _poolRoot = new GameObject("ComposeProjectileFeedback_MarkerPool");
            _tf = new Transform[PoolSize];
            _mf = new MeshFilter[PoolSize];
            _mr = new MeshRenderer[PoolSize];
            _kind = new ShapeKind[PoolSize];
            _origin = new float2[PoolSize];
            _direction = new float2[PoolSize];
            _phase = new float[PoolSize];
            _spin = new float[PoolSize];
            _orbit = new float[PoolSize];
            _radius = new float[PoolSize];
            _elapsed = new float[PoolSize];
            _timeLeft = new float[PoolSize];
            _life = new float[PoolSize];
            _color = new Color[PoolSize];

            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Marker_{i}");
                go.transform.SetParent(_poolRoot.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(_matTemplate);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;

                _tf[i] = go.transform;
                _mf[i] = mf;
                _mr[i] = mr;
                _timeLeft[i] = 0f;
                _life[i] = 0.2f;
                _color[i] = Color.white;
            }
        }

        private void SpawnMarker(ShapeKind kind, float2 origin, float2 direction, float phase, float spin, float orbit,
            float radius, float life, Color color)
        {
            EnsurePool();

            int idx = _cursor;
            _cursor = (_cursor + 1) % PoolSize;

            _kind[idx] = kind;
            _origin[idx] = origin;
            _direction[idx] = math.normalizesafe(direction, new float2(0f, 1f));
            _phase[idx] = phase;
            _spin[idx] = spin;
            _orbit[idx] = orbit;
            _radius[idx] = radius;
            _elapsed[idx] = 0f;
            _timeLeft[idx] = life;
            _life[idx] = life;
            _color[idx] = color;

            _mf[idx].sharedMesh = MeshFor(kind);
            _mr[idx].enabled = true;
            _mr[idx].sharedMaterial.color = _color[idx];

            ApplyTransform(idx);
        }

        /// <summary>按 Shape 重算 pos/rot/scale——Bolt/Spore/Wave 沿 D5 偏移飞行/扩张，Beam/Arc/Field 静态。</summary>
        private void ApplyTransform(int idx)
        {
            ShapeKind kind = _kind[idx];
            float2 origin = _origin[idx];
            float radius = _radius[idx];
            float u = _life[idx] > 0f ? 1f - (_timeLeft[idx] / _life[idx]) : 0f;

            switch (kind)
            {
                case ShapeKind.Beam:
                {
                    float2 dir = _direction[idx];
                    float length = radius * BeamLengthCoef;
                    float width = radius * BeamWidthCoef;
                    float angDeg = DirectionAngleDeg(dir);
                    _tf[idx].localPosition = new Vector3(origin.x, MarkerY, origin.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(length, 1f, width);
                    break;
                }
                case ShapeKind.Arc:
                {
                    float2 dir = _direction[idx];
                    float angDeg = DirectionAngleDeg(dir);
                    _tf[idx].localPosition = new Vector3(origin.x, MarkerY, origin.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(radius, 1f, radius);
                    break;
                }
                case ShapeKind.Field:
                {
                    // D3 强同步：白模半径直接等于 radius，无额外系数。
                    _tf[idx].localPosition = new Vector3(origin.x, MarkerY, origin.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(radius * 2f, 1f, radius * 2f);
                    break;
                }
                case ShapeKind.Wave:
                {
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 pos = origin + offset;
                    float grown = radius * Mathf.Clamp01(u);
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(grown * 2f, 1f, grown * 2f);
                    break;
                }
                case ShapeKind.Spore:
                {
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 pos = origin + offset;
                    float diameter = radius * SporeDiameterCoef;
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    break;
                }
                case ShapeKind.Bolt:
                default:
                {
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 pos = origin + offset;
                    float diameter = radius * BoltDiameterCoef;
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    break;
                }
            }
        }

        private static float DirectionAngleDeg(float2 dir)
        {
            float2 n = math.normalizesafe(dir, new float2(0f, 1f));
            return math.atan2(n.y, n.x) * Mathf.Rad2Deg;
        }

        // ── 几何：局部 +X 为"朝向"轴，与 SpawnMarker 的旋转约定配套 ──

        private static Mesh BuildCircle(int segments)
        {
            var verts = new List<Vector3> { Vector3.zero };
            for (int i = 0; i <= segments; i++)
            {
                float t = 2f * Mathf.PI * i / segments;
                verts.Add(new Vector3(Mathf.Cos(t) * 0.5f, 0f, Mathf.Sin(t) * 0.5f));
            }

            var tris = new List<int>();
            for (int i = 1; i < verts.Count - 1; i++)
            {
                tris.Add(0);
                tris.Add(i);
                tris.Add(i + 1);
            }

            var m = new Mesh { name = "ComposeProjectileCircle" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildStreak()
        {
            var m = new Mesh { name = "ComposeProjectileStreak" };
            m.SetVertices(new List<Vector3>
            {
                new Vector3(0f, 0f, -0.5f),
                new Vector3(0f, 0f, 0.5f),
                new Vector3(1f, 0f, 0.5f),
                new Vector3(1f, 0f, -0.5f),
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildWedge(float halfAngleDeg, int segments)
        {
            var verts = new List<Vector3> { Vector3.zero };
            float half = halfAngleDeg * Mathf.Deg2Rad;
            for (int i = 0; i <= segments; i++)
            {
                float t = -half + (2f * half) * i / segments;
                verts.Add(new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t)));
            }

            var tris = new List<int>();
            for (int i = 1; i < verts.Count - 1; i++)
            {
                tris.Add(0);
                tris.Add(i);
                tris.Add(i + 1);
            }

            var m = new Mesh { name = "ComposeProjectileWedge" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildRing(float innerRatio, int segments)
        {
            const float outer = 0.5f;
            float inner = outer * innerRatio;

            var verts = new List<Vector3>();
            for (int i = 0; i <= segments; i++)
            {
                float t = 2f * Mathf.PI * i / segments;
                float cos = Mathf.Cos(t);
                float sin = Mathf.Sin(t);
                verts.Add(new Vector3(cos * outer, 0f, sin * outer));
                verts.Add(new Vector3(cos * inner, 0f, sin * inner));
            }

            var tris = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                int o0 = i * 2;
                int i0 = i * 2 + 1;
                int o1 = o0 + 2;
                int i1 = i0 + 2;
                tris.Add(o0); tris.Add(o1); tris.Add(i0);
                tris.Add(o1); tris.Add(i1); tris.Add(i0);
            }

            var m = new Mesh { name = "ComposeProjectileRing" };
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
                for (int i = 0; i < _mr.Length; i++)
                {
                    if (_mr[i] != null)
                    {
                        UnityEngine.Object.Destroy(_mr[i].sharedMaterial);
                    }
                }

                UnityEngine.Object.Destroy(_poolRoot);
                _poolRoot = null;
                _tf = null;
                _mf = null;
                _mr = null;
                _kind = null;
                _origin = null;
                _direction = null;
                _phase = null;
                _spin = null;
                _orbit = null;
                _radius = null;
                _elapsed = null;
                _timeLeft = null;
                _life = null;
                _color = null;
            }

            if (_matTemplate != null)
            {
                UnityEngine.Object.Destroy(_matTemplate);
                _matTemplate = null;
            }

            if (_circleMesh != null)
            {
                UnityEngine.Object.Destroy(_circleMesh);
                _circleMesh = null;
            }
            if (_streakMesh != null)
            {
                UnityEngine.Object.Destroy(_streakMesh);
                _streakMesh = null;
            }
            if (_wedgeMesh != null)
            {
                UnityEngine.Object.Destroy(_wedgeMesh);
                _wedgeMesh = null;
            }
            if (_ringMesh != null)
            {
                UnityEngine.Object.Destroy(_ringMesh);
                _ringMesh = null;
            }
        }
    }
}
