using System.Collections.Generic;
using GameLogic.Ability;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模技能施放反馈（story-010 默认实现）。
    ///
    /// 按 <see cref="EffectSpec.Kind"/>/<see cref="EffectSpec.Shape"/> 选表现模板
    /// （Dash / Projectile / SelfBuff / TargetZap / AreaCircle / AreaCone / AreaLine），
    /// **不按 AbilityId 硬编码**——新增技能只要在配表里配对了 Kind+Shape 就自动出正确白模。
    ///
    /// 所有标记都是瞬时/短寿命指示（≤0.35s），不追踪玩家后续位置（Presenter 无 Sim 引用，
    /// 也不需要——"看出放了什么"只需要施放那一刻的方向/位置示意）。
    /// 与 story-007 AreaZoneSystem 持久圆盘区分：本类颜色高对比、寿命更短，资源不复用。
    ///
    /// 全部运行期程序化创建（不依赖 prefab/UI 资源）+ 对象池，局末随 <see cref="Dispose"/> 清理。
    /// </summary>
    public sealed class WhiteboxAbilityCastFeedback : IAbilityCastFeedback, System.IDisposable
    {
        private enum TemplateKind
        {
            Dash,
            Projectile,
            SelfBuff,
            TargetZap,
            AreaCircle,
            AreaCone,
            AreaLine,
        }

        private const int PoolSize = 24;
        private const float MarkerY = 0.17f;
        private const float ConeHalfAngleDeg = 32f;
        private const int ConeSegments = 8;

        /// <summary>story-005（scene-3d-content）：本类白模挤出高度，独立于 Compose 反馈
        /// （同 FxRecipeCatalog.Global.MarkerHeight 取值，但两份反馈既有约定不共享网格/常量）。</summary>
        private const float MarkerHeight = 0.22f;

        private GameObject _poolRoot;
        private Transform[] _tf;
        private MeshFilter[] _mf;
        private MeshRenderer[] _mr;
        private float[] _timeLeft;
        private float[] _life;
        private Color[] _color;
        private int _cursor;

        private Mesh _discMesh;
        private Mesh _streakMesh;
        private Mesh _wedgeMesh;
        private Material _matTemplate;

        /// <summary>本次施放内已生成过的 (Kind,Shape) 组合，避免同一技能多条效果重复叠加同一模板。</summary>
        private readonly List<(EffectKind Kind, EffectShape Shape)> _dedupBuffer = new List<(EffectKind, EffectShape)>(4);

        /// <summary>探针（story-008）：最近一次 <see cref="OnAbilityCast"/> 生成的标记数量。</summary>
        public int LastAbilityCastMarkerCount { get; private set; }

        /// <summary>探针（story-008）：最近一次 <see cref="SpawnForEffect"/> 路由到的模板种类。</summary>
        public string LastAbilityCastTemplateKind { get; private set; }

        public void OnAbilityCast(AbilityCastSignal signal)
        {
            AbilitySpec spec = DataRegistry.Instance.GetAbility(signal.AbilityId);
            if (spec?.Effects == null || spec.Effects.Count == 0)
            {
                return;
            }

            _dedupBuffer.Clear();
            LastAbilityCastMarkerCount = 0;
            for (int i = 0; i < spec.Effects.Count; i++)
            {
                EffectSpec fx = spec.Effects[i];
                if (fx == null || fx.Kind == EffectKind.None)
                {
                    continue;
                }

                var key = (fx.Kind, fx.Shape);
                if (_dedupBuffer.Contains(key))
                {
                    continue;
                }
                _dedupBuffer.Add(key);

                SpawnForEffect(fx, spec, signal.Origin, signal.Direction);
            }
        }

        public void Tick(float dt)
        {
            TickMarkers(dt);
        }

        // ── 模板选择（M1：Kind 优先于 Shape，禁止按 AbilityId 硬编码）──

        private static TemplateKind SelectTemplate(EffectSpec fx)
        {
            if (fx.Kind == EffectKind.Dash)
            {
                return TemplateKind.Dash;
            }
            if (fx.Kind == EffectKind.Projectile)
            {
                return TemplateKind.Projectile;
            }
            if (fx.Kind == EffectKind.Spawn && fx.Shape == EffectShape.Self)
            {
                // 原地生成物（孢子召唤/脱壳留壳）语义上是"放置"，不是自身光环
                return TemplateKind.AreaCircle;
            }

            switch (fx.Shape)
            {
                case EffectShape.Self: return TemplateKind.SelfBuff;
                case EffectShape.Target: return TemplateKind.TargetZap;
                case EffectShape.Circle: return TemplateKind.AreaCircle;
                case EffectShape.Cone: return TemplateKind.AreaCone;
                case EffectShape.Line: return TemplateKind.AreaLine;
                case EffectShape.Point: return TemplateKind.AreaCircle;
                default: return TemplateKind.AreaCircle;
            }
        }

        private void SpawnForEffect(EffectSpec fx, AbilitySpec spec, float2 origin, float2 dir)
        {
            TemplateKind kind = SelectTemplate(fx);
            LastAbilityCastTemplateKind = kind.ToString();
            switch (kind)
            {
                case TemplateKind.Dash:
                    SpawnDash(origin, dir, fx);
                    break;
                case TemplateKind.Projectile:
                    SpawnProjectileMuzzle(origin);
                    break;
                case TemplateKind.SelfBuff:
                    SpawnSelfBuff(origin);
                    break;
                case TemplateKind.TargetZap:
                    SpawnTargetZap(origin, dir, spec);
                    break;
                case TemplateKind.AreaCircle:
                    SpawnAreaCircle(origin, fx);
                    break;
                case TemplateKind.AreaCone:
                    SpawnAreaCone(origin, dir, fx);
                    break;
                case TemplateKind.AreaLine:
                    SpawnAreaLine(origin, dir, fx);
                    break;
            }
        }

        // ── 具体模板：颜色/寿命定义（Z1：瞬时指示高对比 + 短寿命，与常驻 Zone 圆盘区分）──

        private static readonly Color DashColor = new Color(0.85f, 0.95f, 1f, 0.9f);
        private static readonly Color ProjectileMuzzleColor = new Color(1f, 0.98f, 0.75f, 1f);
        private static readonly Color SelfBuffColor = new Color(1f, 0.9f, 0.5f, 0.85f);
        private static readonly Color TargetZapLineColor = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color TargetZapHitColor = new Color(0.55f, 0.95f, 1f, 1f);
        private static readonly Color AreaCircleColor = new Color(1f, 0.3f, 0.85f, 0.55f);
        private static readonly Color AreaConeColor = new Color(1f, 0.55f, 0.2f, 0.55f);
        private static readonly Color AreaLineColor = new Color(0.55f, 1f, 0.4f, 0.6f);

        private void SpawnDash(float2 origin, float2 dir, EffectSpec fx)
        {
            float dist = fx.Value > 0f ? fx.Value : 4f;
            SpawnStreak(origin, dir, dist, 0.7f, DashColor, 0.3f);
            float2 dirN = math.normalizesafe(dir, new float2(1f, 0f));
            SpawnDisc(origin + dirN * dist, 0.6f, DashColor, 0.3f);
        }

        private void SpawnProjectileMuzzle(float2 origin)
        {
            SpawnDisc(origin, 0.9f, ProjectileMuzzleColor, 0.15f);
        }

        private void SpawnSelfBuff(float2 origin)
        {
            SpawnDisc(origin, 2.2f, SelfBuffColor, 0.35f);
        }

        private void SpawnTargetZap(float2 origin, float2 dir, AbilitySpec spec)
        {
            float len = Mathf.Clamp(spec.CastRange > 0f ? spec.CastRange : 6f, 2f, 8f);
            SpawnStreak(origin, dir, len, 0.35f, TargetZapLineColor, 0.2f);
            float2 dirN = math.normalizesafe(dir, new float2(1f, 0f));
            SpawnDisc(origin + dirN * len, 1.1f, TargetZapHitColor, 0.22f);
        }

        private void SpawnAreaCircle(float2 origin, EffectSpec fx)
        {
            float r = fx.Radius > 0f ? fx.Radius : 2.5f;
            SpawnDisc(origin, r, AreaCircleColor, 0.28f);
        }

        private void SpawnAreaCone(float2 origin, float2 dir, EffectSpec fx)
        {
            float len = fx.Radius > 0f ? fx.Radius : 4f;
            SpawnWedge(origin, dir, len, AreaConeColor, 0.28f);
        }

        private void SpawnAreaLine(float2 origin, float2 dir, EffectSpec fx)
        {
            float len = fx.Radius > 0f ? fx.Radius : 4f;
            SpawnStreak(origin, dir, len, 0.6f, AreaLineColor, 0.28f);
        }

        // ── 对象池（V2：GameObject + 池化，禁止逐次 Instantiate/Destroy）──

        private void EnsurePool()
        {
            if (_poolRoot != null)
            {
                return;
            }

            _discMesh = BuildDisc();
            _streakMesh = BuildStreak();
            _wedgeMesh = BuildWedge(ConeHalfAngleDeg, ConeSegments);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            _matTemplate = new Material(shader) { color = Color.white };

            _poolRoot = new GameObject("AbilityCastFeedback_MarkerPool");
            _tf = new Transform[PoolSize];
            _mf = new MeshFilter[PoolSize];
            _mr = new MeshRenderer[PoolSize];
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

        private void SpawnDisc(float2 pos, float radius, Color color, float life)
        {
            SpawnMarker(_discMesh, pos, 0f, new Vector3(radius * 2f, 1f, radius * 2f), color, life);
        }

        private void SpawnStreak(float2 origin, float2 dir, float length, float width, Color color, float life)
        {
            float angDeg = DirectionAngleDeg(dir);
            SpawnMarker(_streakMesh, origin, angDeg, new Vector3(length, 1f, width), color, life);
        }

        private void SpawnWedge(float2 origin, float2 dir, float length, Color color, float life)
        {
            float angDeg = DirectionAngleDeg(dir);
            SpawnMarker(_wedgeMesh, origin, angDeg, new Vector3(length, 1f, length), color, life);
        }

        private static float DirectionAngleDeg(float2 dir)
        {
            float2 n = math.normalizesafe(dir, new float2(1f, 0f));
            return math.atan2(n.y, n.x) * Mathf.Rad2Deg;
        }

        private void SpawnMarker(Mesh mesh, float2 pos, float angleDeg, Vector3 scale, Color color, float life)
        {
            EnsurePool();

            int idx = _cursor;
            _cursor = (_cursor + 1) % PoolSize;

            _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
            // 与 SimRenderer.DrawProjectiles 同一约定：mesh 局部 +X 经此旋转后对齐世界方向。
            _tf[idx].localRotation = Quaternion.Euler(0f, -angleDeg, 0f);
            _tf[idx].localScale = scale;
            _mf[idx].sharedMesh = mesh;
            _mr[idx].enabled = true;
            _mr[idx].sharedMaterial.color = color;
            _timeLeft[idx] = life;
            _life[idx] = life;
            _color[idx] = color;
            LastAbilityCastMarkerCount++;
        }

        private void TickMarkers(float dt)
        {
            if (_timeLeft == null)
            {
                return;
            }

            for (int i = 0; i < PoolSize; i++)
            {
                if (_timeLeft[i] <= 0f)
                {
                    continue;
                }

                _timeLeft[i] -= dt;
                if (_timeLeft[i] <= 0f)
                {
                    _timeLeft[i] = 0f;
                    _mr[i].enabled = false;
                    continue;
                }

                float u = 1f - (_timeLeft[i] / _life[i]);
                Color c = _color[i];
                c.a *= (1f - u) * (1f - u);
                _mr[i].sharedMaterial.color = c;
            }
        }

        // ── 几何：局部 +X 为"朝向"轴，与 SpawnMarker 的旋转约定配套 ──

        private static Mesh BuildDisc()
        {
            var verts = new List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, -0.5f),
            };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3 };
            return ExtrudeFlat(verts, tris, MarkerHeight, "AbilityCastDisc");
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
            return ExtrudeFlat(verts, tris, MarkerHeight, "AbilityCastStreak");
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

            return ExtrudeFlat(verts, tris, MarkerHeight, "AbilityCastWedge");
        }

        /// <summary>story-005（scene-3d-content）：把一份纯 XZ 平面（y=0）几何挤出成有厚度的实体——
        /// 底面（原三角形反绕，法线朝下）+ 顶面（原三角形正绕，y=height，法线朝上）+ 侧壁（沿边界有向边，
        /// 即反向边未出现过的边，连接底/顶对应点）。与 WhiteboxComposeProjectileFeedback 同款算法，
        /// 独立一份不共享（既有约定）。</summary>
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
                tris.Add(a); tris.Add(c); tris.Add(b);
            }

            for (int i = 0; i < baseTris.Count; i += 3)
            {
                int a = baseTris[i] + n;
                int b = baseTris[i + 1] + n;
                int c = baseTris[i + 2] + n;
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
                    continue;
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
                        Object.Destroy(_mr[i].sharedMaterial);
                    }
                }

                Object.Destroy(_poolRoot);
                _poolRoot = null;
                _tf = null;
                _mf = null;
                _mr = null;
                _timeLeft = null;
                _life = null;
                _color = null;
            }

            if (_matTemplate != null)
            {
                Object.Destroy(_matTemplate);
                _matTemplate = null;
            }

            if (_discMesh != null)
            {
                Object.Destroy(_discMesh);
                _discMesh = null;
            }
            if (_streakMesh != null)
            {
                Object.Destroy(_streakMesh);
                _streakMesh = null;
            }
            if (_wedgeMesh != null)
            {
                Object.Destroy(_wedgeMesh);
                _wedgeMesh = null;
            }
        }
    }
}
