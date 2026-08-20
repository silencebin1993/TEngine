using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Combat;
using GameLogic.MetabolicSlice.ContentCatalog;
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
            Melee,
        }

        // story-009：全局段（G2c）改由 FxRecipeCatalog.Global 承载，这里仍以 const 别名引用——
        // 二者都是编译期常量，取值处（EnsurePool/ApplyTransform/SpawnMarker）零改动，只换来源。
        private const int PoolSize = FxRecipeCatalog.Global.PoolSize;
        private const float MarkerY = FxRecipeCatalog.Global.MarkerY;
        private const float ArcHalfAngleDeg = FxRecipeCatalog.Global.ArcHalfAngleDeg;
        private const int ArcSegments = FxRecipeCatalog.Global.ArcSegments;
        private const int CircleSegments = FxRecipeCatalog.Global.CircleSegments;
        private const float RingInnerRatio = FxRecipeCatalog.Global.RingInnerRatio;
        private const float BandInnerRatio = FxRecipeCatalog.Global.BandInnerRatio;
        private const int BandSegments = FxRecipeCatalog.Global.BandSegments;

        // 生命周期常量（Decision D7）：Bolt/Arc/Wave/Spore 对齐 003 已锁死的真实命中延迟
        // ComposeMotionMath.MotionFlightDuration=0.3f，保证白模消失时刻与真实结算命中同步——
        // story-009 G2a：这是同步不变量，禁止进配方表，仍直接引用 ComposeMotionMath 派生。
        // Beam/Field 是"常驻类"，PersistentLife 属纯表现量，改由 FxRecipeCatalog.Global 承载。
        private const float FlightLife = ComposeMotionMath.MotionFlightDuration;
        private const float PersistentLife = FxRecipeCatalog.Global.PersistentLife;

        /// <summary>story-006：Bolt 沿 Direction 的独立线性飞行距离，不进 FxRecipeCatalog（只有 Bolt 需要直线飞行，
        /// Melee/Arc/Wave/Spore 语义不同）。不改 <see cref="ComposeMotionMath.Offset"/>（003 D5 锁死）。</summary>
        private const float BoltFlightDistance = 6f;

        // story-009：Bolt/Spore 直径系数与 Beam 长/宽系数改为逐配方（G2b），从 FxRecipeCatalog
        // 按当前 ShapeKind 查表读取，不再是同一套固定 const（详见 ApplyTransform）。

        private static readonly HashSet<string> _warnedUnknownShapes = new HashSet<string>();

        /// <summary>story-009 G5 完备性检查：ShapeKind 是 key 空间，FxRecipeCatalog 是内容——
        /// 新增枚举项忘了配表时在此处报错，而不是静默落到某个 default 分支。</summary>
        static WhiteboxComposeProjectileFeedback()
        {
            foreach (ShapeKind kind in Enum.GetValues(typeof(ShapeKind)))
            {
                if (!FxRecipeCatalog.TryGetShapeRecipe(kind.ToString(), out _))
                {
                    TEngine.Log.Error($"[FxRecipeCatalog] 缺配方: {kind}（ShapeKind 与表必须一一对应）");
                }
            }

            // story-010 Required 1：补上反向（表 → 枚举）。009 只查了「枚举 → 表」，表里多出的行
            // 会一路走到 ParseShape 才暴露；这里提前一次性报出来，运行期再由 TryParse 兜底回落 Bolt。
            foreach (string key in FxRecipeCatalog.ShapeKeys)
            {
                if (!Enum.TryParse(typeof(ShapeKind), key, out _))
                {
                    TEngine.Log.Error($"[FxRecipeCatalog] 表里有但 ShapeKind 没有: {key}（ComposeEngine 永远吐不出它）");
                }
            }
        }

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
        private Mesh _coneMesh;
        private Mesh _crossMesh;
        private Mesh _bandMesh;
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

        /// <summary>story-006 验收探针：最近一次 Bolt/default 分支算出的世界坐标，供断言飞行位移随 Tick 逐帧偏离 Origin（非仅原地闪一下）。</summary>
        public Vector3 LastBoltPosition { get; private set; }

        /// <summary>story-007 验收探针：最近一次 Melee 分支算出的世界坐标，供与 <see cref="LastBoltPosition"/> 对照——
        /// Melee 应「原地生长」不随 Tick 明显位移，Bolt 应沿 Direction 明显飞出。</summary>
        public Vector3 LastMeleePosition { get; private set; }

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

            FxRecipeCatalog.TryGetShapeRecipe(kind.ToString(), out var recipe);
            float shapeLife = recipe != null && recipe.Life == FxLifeKind.Persistent ? PersistentLife : FlightLife;

            if (kind == ShapeKind.Beam)
            {
                // Beam 例外（Decision D4）：Count 对单段常驻弹道无意义，固定渲染 1 段，不随 segments 循环。
                LastSegmentCount = 1;
                SpawnMarker(kind, signal.Origin, signal.Direction, 0f, 0f, 0f, radius, shapeLife, castColor);
            }
            else
            {
                LastSegmentCount = segments;
                float life = shapeLife;
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
                Color explodeColor = elementTag.Length > 0 ? castColor : FxRecipeCatalog.DefaultExplodeColor;
                LastExplodeRadius = explodeRadius;
                SpawnMarker(ShapeKind.Wave, signal.Origin, signal.Direction, 0f, 0f, 0f, explodeRadius, FlightLife, explodeColor);
            }
            else
            {
                LastExplodeRadius = 0f;
            }
        }

        // ── 元素 Tag 染色（story-007 D1~D4，对照冻结总案 §3.4 元素词表全 10 项；
        // story-009 起优先级数组/颜色/爆炸兜底色改由 FxRecipeCatalog 承载，逻辑本身不变）──

        private static string ResolveElementTag(HashSet<string> tags)
        {
            if (tags == null)
            {
                return "";
            }

            string[] order = FxRecipeCatalog.ElementPriorityOrder;
            for (int i = 0; i < order.Length; i++)
            {
                if (tags.Contains(order[i]))
                {
                    return order[i];
                }
            }

            return "";
        }

        private static Color ElementColorFor(string tag) => FxRecipeCatalog.GetElementColor(tag);

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

        // ── Shape 路由（Decision D6，引用 story-001 §2 映射表，禁止按 org_* id 分支；
        // story-009 G3：表里查不到的 Shape 按字符串去重只警一次，回落 Bolt）──

        private static ShapeKind ParseShape(string shape)
        {
            if (FxRecipeCatalog.TryGetShapeRecipe(shape, out _))
            {
                // story-010 Required 1: 把 Enum.Parse 换成 TryParse，防止「表里有但枚举没有」抛异常
                if (System.Enum.TryParse(typeof(ShapeKind), shape, out object parsed))
                {
                    return (ShapeKind)parsed;
                }
                // 表里有但枚举没有：同样走告警回落
                if (_warnedUnknownShapes.Add(shape ?? "<null>"))
                {
                    TEngine.Log.Warning($"[FxRecipe] Shape 在表中但枚举未定义: {shape}，回落 Bolt");
                }
                return ShapeKind.Bolt;
            }

            if (shape != "Bolt" && _warnedUnknownShapes.Add(shape ?? "<null>"))
            {
                TEngine.Log.Warning($"[FxRecipe] 未知 Shape: {shape}，回落 Bolt");
            }
            return ShapeKind.Bolt;
        }

        private static Color ColorFor(ShapeKind kind) =>
            FxRecipeCatalog.TryGetShapeRecipe(kind.ToString(), out var recipe) ? recipe.Color : Color.white;

        private Mesh MeshFor(ShapeKind kind)
        {
            if (!FxRecipeCatalog.TryGetShapeRecipe(kind.ToString(), out var recipe))
            {
                return _circleMesh;
            }

            switch (recipe.Mesh)
            {
                case FxMeshKind.Streak: return _streakMesh;
                case FxMeshKind.Wedge: return _wedgeMesh;
                case FxMeshKind.Ring: return _ringMesh;
                case FxMeshKind.Cone: return _coneMesh;
                case FxMeshKind.Cross: return _crossMesh;
                case FxMeshKind.Band: return _bandMesh;
                case FxMeshKind.Circle:
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
            _coneMesh = BuildCone(CircleSegments);
            _crossMesh = BuildCross();
            _bandMesh = BuildBand(BandInnerRatio, BandSegments);

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
            FxRecipeCatalog.TryGetShapeRecipe(kind.ToString(), out var recipe);

            switch (kind)
            {
                case ShapeKind.Beam:
                {
                    float2 dir = _direction[idx];
                    float length = radius * recipe.LengthCoef;
                    float width = radius * recipe.WidthCoef;
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
                    float diameter = radius * recipe.DiameterCoef;
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    break;
                }
                case ShapeKind.Melee:
                {
                    // story-007 D1：近战「原地生长」——不复用 BoltFlightDistance 线性位移，
                    // 只走 003 D5 的 Spin/Orbit 偏移（=0 时天然零向量），靠缩放给出冲击感。
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 pos = origin + offset;
                    float grown = radius * recipe.DiameterCoef * (0.6f + 0.4f * Mathf.Clamp01(u));
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(grown, 1f, grown);
                    LastMeleePosition = _tf[idx].localPosition;
                    break;
                }
                case ShapeKind.Bolt:
                default:
                {
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    // story-006：叠加沿 Direction 的独立线性飞行位移，让 Bolt 读得出「射出去」——
                    // Spin=Orbit=0 时 offset 天然是零向量，与 offset 正交不冲突（Decision 4）。
                    float2 linear = _direction[idx] * BoltFlightDistance * u;
                    float2 pos = origin + offset + linear;
                    float diameter = radius * recipe.DiameterCoef;
                    // story-010 J1：Bolt 换成有指向的锥形网格后必须跟着 Direction 转——
                    // 原来是 Circle（旋转对称）才可以 identity，留着 identity 等于锥尖恒指世界 +X。
                    float angDeg = DirectionAngleDeg(_direction[idx]);
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    LastBoltPosition = _tf[idx].localPosition;
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
        // story-010 J3：网格构建函数改为 internal static，供 WhiteboxComposeAimIndicator 复用

        internal static Mesh BuildCircleStatic(int segments) => BuildCircle(segments);
        internal static Mesh BuildStreakStatic() => BuildStreak();
        internal static Mesh BuildWedgeStatic(float halfAngleDeg, int segments) => BuildWedge(halfAngleDeg, segments);
        internal static Mesh BuildRingStatic(float innerRatio, int segments) => BuildRing(innerRatio, segments);
        internal static Mesh BuildConeStatic(int segments) => BuildCone(segments);
        internal static Mesh BuildCrossStatic() => BuildCross();
        internal static Mesh BuildBandStatic(float innerRatio, int segments) => BuildBand(innerRatio, segments);

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

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileCircle");
        }

        private static Mesh BuildStreak()
        {
            var verts = new List<Vector3>
            {
                new Vector3(0f, 0f, -0.5f),
                new Vector3(0f, 0f, 0.5f),
                new Vector3(1f, 0f, 0.5f),
                new Vector3(1f, 0f, -0.5f),
            };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileStreak");
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

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileWedge");
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

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileRing");
        }

        /// <summary>story-010 J1：锥形指向网格（Bolt 专用）。与本文件其余图元同约定——落在局部 XZ 平面
        /// （y=0、半径 0.5、局部 +X 为朝向轴）：尖端在 +X，尾部收成半圆，从上方看即一枚指向弹头。
        /// **刻意不做立起来的三维锥**：Marker 只有 X/Z 被 <see cref="ApplyTransform"/> 缩放（Y 恒 1），
        /// 立锥会有半个锥体沉到地面以下、且高度不随 diameter 变化。</summary>
        private static Mesh BuildCone(int segments)
        {
            int arc = Mathf.Max(2, segments / 2);
            // 0 = 中心（扇心），1 = 尖端，其后是尾部半圆弧（+Z 侧绕到 -Z 侧）。
            var verts = new List<Vector3> { Vector3.zero, new Vector3(0.5f, 0f, 0f) };
            for (int i = 0; i <= arc; i++)
            {
                float t = Mathf.PI * 0.5f + Mathf.PI * i / arc;
                verts.Add(new Vector3(Mathf.Cos(t) * 0.5f, 0f, Mathf.Sin(t) * 0.5f));
            }

            var tris = new List<int>();
            tris.Add(0); tris.Add(1); tris.Add(2);                       // 尖端 → 弧首
            for (int i = 2; i < verts.Count - 1; i++)                    // 尾弧逐段
            {
                tris.Add(0); tris.Add(i); tris.Add(i + 1);
            }
            tris.Add(0); tris.Add(verts.Count - 1); tris.Add(1);         // 弧末 → 尖端

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileCone");
        }

        /// <summary>story-010 J1：十字网格（Spore 专用，两条正交矩形条，XZ 平面，各边半径 0.5）。</summary>
        private static Mesh BuildCross()
        {
            float w = 0.15f; // 条宽
            var verts = new List<Vector3>
            {
                // 横条（沿 X）
                new Vector3(-0.5f, 0f, -w), new Vector3(0.5f, 0f, -w),
                new Vector3(0.5f, 0f, w), new Vector3(-0.5f, 0f, w),
                // 竖条（沿 Z）
                new Vector3(-w, 0f, -0.5f), new Vector3(w, 0f, -0.5f),
                new Vector3(w, 0f, 0.5f), new Vector3(-w, 0f, 0.5f),
            };
            var tris = new List<int>
            {
                // 横条
                0, 1, 2,  0, 2, 3,
                // 竖条
                4, 5, 6,  4, 6, 7,
            };

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileCross");
        }

        /// <summary>story-010 J1：环带网格（Field 专用）。结构同 Ring，但**参数必须与 Ring 不同**——
        /// 走 <see cref="FxRecipeCatalog.Global"/>.BandInnerRatio / BandSegments（更厚、更密）。
        /// 若沿用 Ring 的 innerRatio+segments，两者顶点逐位相同，Field 与 Wave 就成了同一个东西。</summary>
        private static Mesh BuildBand(float innerRatio, int segments)
        {
            var verts = new List<Vector3>();
            for (int i = 0; i <= segments; i++)
            {
                float t = 2f * Mathf.PI * i / segments;
                float c = Mathf.Cos(t), s = Mathf.Sin(t);
                verts.Add(new Vector3(c * innerRatio * 0.5f, 0f, s * innerRatio * 0.5f));
                verts.Add(new Vector3(c * 0.5f, 0f, s * 0.5f));
            }

            var tris = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                int i0 = i * 2, i1 = i0 + 1, o0 = i0 + 2, o1 = o0 + 1;
                tris.Add(i0); tris.Add(o0); tris.Add(i1);
                tris.Add(o0); tris.Add(o1); tris.Add(i1);
            }

            return ExtrudeFlat(verts, tris, FxRecipeCatalog.Global.MarkerHeight, "ComposeProjectileBand");
        }

        /// <summary>story-005（scene-3d-content）：把一份纯 XZ 平面（y=0）扇形/条带几何挤出成有厚度的实体——
        /// 底面（原三角形反绕，法线朝下）+ 顶面（原三角形正绕，y=height，法线朝上）+ 侧壁（沿边界有向边，
        /// 即反向边未出现过的边，连接底/顶对应点）。对扇形/条带/环带/双四边形/单四边形统一适用，
        /// 不需要按形状特判——环带的内外两圈边界会被同一套边界边检测自然识别为两组侧壁。</summary>
        private static Mesh ExtrudeFlat(List<Vector3> baseVerts, List<int> baseTris, float height, string name)
        {
            int n = baseVerts.Count;
            var verts = new List<Vector3>(n * 2);
            verts.AddRange(baseVerts);
            for (int i = 0; i < n; i++)
            {
                Vector3 v = baseVerts[i];
                verts.Add(new Vector3(v.x, height, v.z));
            }

            var tris = new List<int>(baseTris.Count * 2 + 32);

            for (int i = 0; i < baseTris.Count; i += 3)
            {
                int a = baseTris[i];
                int b = baseTris[i + 1];
                int c = baseTris[i + 2];
                // 底面反绕（法线朝下）
                tris.Add(a); tris.Add(c); tris.Add(b);
            }

            for (int i = 0; i < baseTris.Count; i += 3)
            {
                int a = baseTris[i] + n;
                int b = baseTris[i + 1] + n;
                int c = baseTris[i + 2] + n;
                // 顶面正绕（法线朝上）
                tris.Add(a); tris.Add(b); tris.Add(c);
            }

            var edgeCount = new Dictionary<(int, int), int>();
            for (int i = 0; i < baseTris.Count; i += 3)
            {
                int a = baseTris[i];
                int b = baseTris[i + 1];
                int c = baseTris[i + 2];
                AddEdge(edgeCount, a, b);
                AddEdge(edgeCount, b, c);
                AddEdge(edgeCount, c, a);
            }

            foreach (var edge in edgeCount.Keys)
            {
                int a = edge.Item1;
                int b = edge.Item2;
                if (edgeCount.ContainsKey((b, a)))
                {
                    continue; // 内部共享边，不是边界
                }

                tris.Add(a); tris.Add(b); tris.Add(b + n);
                tris.Add(a); tris.Add(b + n); tris.Add(a + n);
            }

            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static void AddEdge(Dictionary<(int, int), int> edgeCount, int a, int b)
        {
            var key = (a, b);
            edgeCount[key] = edgeCount.TryGetValue(key, out int count) ? count + 1 : 1;
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
            if (_coneMesh != null)
            {
                UnityEngine.Object.Destroy(_coneMesh);
                _coneMesh = null;
            }
            if (_crossMesh != null)
            {
                UnityEngine.Object.Destroy(_crossMesh);
                _crossMesh = null;
            }
            if (_bandMesh != null)
            {
                UnityEngine.Object.Destroy(_bandMesh);
                _bandMesh = null;
            }
        }
    }
}
