using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.ArtBinding;
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

        /// <summary>Bolt 沿 Direction 的独立线性飞行距离，不进 FxRecipeCatalog（只有 Bolt/Spore 需要直线飞行，
        /// Melee/Arc/Wave 语义不同）。不改 <see cref="ComposeMotionMath.Offset"/>（003 D5 锁死）。
        /// story-004：纯俯视下核实 6f 太短——0.3s 全程都贴在玩家附近，读不出"射出去"，调大到 9f。
        /// story-006：与 <see cref="MetabolicSliceBridge.ApplyEvent"/> 的落点结算共用同一飞行距离
        /// （<see cref="MetabolicSliceBridge.ImpactFlightDistance"/>，禁止另起系数分叉，比照 D3/D6 先例）。</summary>
        private const float BoltFlightDistance = MetabolicSliceBridge.ImpactFlightDistance;

        /// <summary>story-004：Bolt 出生点前移量。u=0 时若仍在 Origin（=玩家所在坐标），标记会与玩家胶囊完全
        /// 重叠，人眼读作"角色本体闪了一下"而不是"射出一发子弹"（004 截图实测复现）。加上这个固定前移量，
        /// 标记从第 1 帧起就已经离开玩家轮廓，不叠加进 <see cref="ComposeMotionMath.Offset"/>（003 D5 锁死）。</summary>
        private const float BoltMuzzleOffset = 1.2f;

        /// <summary>story-007：近战挥击前移量，与 <see cref="MetabolicSliceBridge.MeleeFrontOffset"/> 共用
        /// 同一个数（禁止另起系数，比照 D3/D6/BoltMuzzleOffset 先例）——白模贴着真实命中圆心生长，不产生
        /// 视觉/判定错位。</summary>
        private const float MeleeMuzzleOffset = MetabolicSliceBridge.MeleeFrontOffset;

        /// <summary>story-007：近战挥击方向性拉伸系数——局部 +X（朝向轴，随 <see cref="DirectionAngleDeg"/>
        /// 旋转）比侧向更长，读出"劈砍"而非圆点原地放大。</summary>
        private const float MeleeForwardStretch = 1.4f;
        private const float MeleeSideStretch = 0.75f;

        /// <summary>Beam 抻出耗时——0 到 PersistentLife（0.5s）内前 0.15s 从 0 长度快速抻满，
        /// 让光束读出"从身上打出去"而不是贴身凭空出现的静态色块（Bolt 已有 MuzzleOffset 解决同类问题，
        /// Beam 用「起点固定在身上 + 长度动画」的等价手法，因为它是有向长条不是圆形，没有居中重叠问题，
        /// 缺的是"发生"这个动作本身）。</summary>
        private const float BeamIgniteDuration = 0.15f;

        /// <summary>story-006：枪口 VFX 生命周期——短促一次性闪现，仿 <see cref="BeamIgniteDuration"/> 量级。</summary>
        private const float MuzzleLife = 0.15f;

        /// <summary>combat-primitive-presentation P1：带 "Charged" Tag 的施法枪口停留时长倍数。</summary>
        private const float ChargedMuzzleLifeMult = 2.5f;

        /// <summary>story-006：VFX 池按 <c>shape.{Shape}.{role}</c> 覆盖的四个角色，与 Editor 侧
        /// FeatureArtSlotSync.AddShapeSlots 生成的槽 id 一一对应。</summary>
        private static readonly string[] VfxRoles = { "projectile", "muzzle", "hit", "explode" };

        // ── reaction-depth-and-combat-feel story-002：具名反应命中时的额外播报，与元素色弹道本身
        // 区分开——白金色大环 + 飘字，让玩家一眼看出"这发触发了反应"而不是误以为只是普通弹道换了色。──
        private const float ReactionRingMult = 1.6f;
        private const float ReactionLifeMult = 1.8f;
        private static readonly Color ReactionRingColor = new Color(1f, 0.95f, 0.65f, 1f);

        // ── reaction-depth-and-combat-feel story-003：结构器官被动触发标记——青色小环，与元素色
        // 弹道/金色反应环区分开，让荆棘壳反伤/护盾/击杀回血/周期波动这些此前"纯数字"的效果也有
        // 一眼可见的信号。5 种 Kind 目前统一配色（真实美术阶段可按 Kind 分色，见 story-003 Deferred）。
        private const float StructuralHookLife = 0.35f;
        private static readonly Color StructuralHookColor = new Color(0.4f, 0.9f, 0.85f, 1f);

        // ── combat-primitive-presentation P1：Pierce/Bounce/Split/Return/Linger 命中后延续，此前
        // TickPendingImpact/TickPendingLinger 只结算数值从不广播，玩家完全看不出这些基元在发生。
        // 与 StructuralHookColor 同一先例——不追溯原始 Shape，先给每个 Kind 一个固定几何体+专属配色，
        // 让玩家至少能分辨"这是哪种延续效果"，真实美术阶段再细化。──
        private const float ChainMarkerLife = 0.22f;
        private static readonly Color PierceColor = new Color(0.85f, 0.9f, 1f, 1f);      // 淡银蓝，"穿透光"
        private static readonly Color BounceColor = new Color(1f, 0.75f, 0.15f, 1f);     // 橙黄，"弹开"
        private static readonly Color SplitColor = new Color(0.95f, 0.35f, 0.85f, 1f);   // 品红，"分裂"
        private static readonly Color ReturnColor = new Color(0.2f, 0.8f, 0.75f, 1f);    // 深青，"飞镖回旋"
        private static readonly Color LingerColor = new Color(0.55f, 0.85f, 0.25f, 1f);  // 病态绿，"残留毒场"

        /// <summary>Field 抛掷飞行耗时，复用 Bolt 同款 <see cref="FlightLife"/>（0.3s）作为"甩出去"的时间窗，
        /// 落地后再用 <see cref="FieldSettleDuration"/>（0.2s）从小长到满径——二者相加正好等于
        /// <see cref="PersistentLife"/>（0.5s），落点用与 Bolt/Spore 同一个 <see cref="MetabolicSliceBridge.ImpactFlightDistance"/>，
        /// 呼应 CATALOG「把酶雾扔到落点」的设计描述（此前 Field 无任何位移，原地贴身出现）。</summary>
        private const float FieldSettleDuration = 0.2f;

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
        /// <summary>story-008 R7①：Spin/Orbit 残影轨迹，复用同一个 Marker GameObject（不新建独立对象池，
        /// 遵守 R9），只在 spin/orbit 非零的标记上启用 emitting。</summary>
        private TrailRenderer[] _trail;
        private ShapeKind[] _kind;
        private float2[] _origin;
        private float2[] _direction;
        /// <summary>正确性修复：Homing&gt;0 时的目标朝向（信号 HomingDirection 原样存储，逐帧与 _direction
        /// 插值），让 Bolt/Spore 的白模飞行路径真的随时间转向命中判定点，不再对 Homing 视而不见。</summary>
        private float2[] _homingDir;
        /// <summary>同上，追踪强度 0-1；0 时 ApplyTransform 不插值，等价于原有纯直线飞行（零回归）。</summary>
        private float[] _homingStrength;
        private float[] _phase;
        private float[] _spin;
        private float[] _orbit;
        private float[] _radius;
        private float[] _elapsed;
        private float[] _timeLeft;
        private float[] _life;
        private Color[] _color;
        private int _cursor;

        /// <summary>story-006：已激活的 VFX Prefab 池实例（未绑定/未命中时对应槽为 null，走程序化白模）。</summary>
        private GameObject[] _prefabGo;
        private FeatureArtVfxPool _vfxPool;

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

        /// <summary>story-008：最近一次命中的非元素 Tag（未命中 ElementPriorityOrder，但 Tags 非空时取其一），
        /// 供断言"非元素 Tag 也参与染色"，无 Tag 或命中元素词表时为 ""。</summary>
        public string LastResolvedNonElementTag { get; private set; } = "";

        /// <summary>story-008：最近一次实际使用的弹体主色（元素色/中性色/Shape 底色三选一后的结果），
        /// 供断言"非元素 Tag 命中非白色"，比反查配色表更直接。</summary>
        public Color LastCastColor { get; private set; } = Color.white;

        /// <summary>story-008 R7①校验探针：当前寿命内、Spin/Orbit 非零因而正在画残影轨迹的标记数量。</summary>
        public int ActiveTrailCount { get; private set; }

        /// <summary>story-006 验收探针：最近一次 Bolt/default 分支算出的世界坐标，供断言飞行位移随 Tick 逐帧偏离 Origin（非仅原地闪一下）。</summary>
        public Vector3 LastBoltPosition { get; private set; }

        /// <summary>story-007 验收探针：最近一次 Melee 分支算出的世界坐标，供与 <see cref="LastBoltPosition"/> 对照——
        /// Melee 应「原地生长」不随 Tick 明显位移，Bolt 应沿 Direction 明显飞出。</summary>
        public Vector3 LastMeleePosition { get; private set; }

        /// <summary>story-006 验收探针：最近一次从 VFX 池取到并实际使用的 Prefab 实例名；未命中池
        /// （槽未绑定/绑定为空）时不更新，初值 ""——可用来断言"绑定后取到 Prefab / 解绑后回落白模"。</summary>
        public string LastVfxPrefabName { get; private set; } = "";

        /// <summary>story-002（reaction-depth-and-combat-feel）：最近一次命中触发的具名反应 id
        /// （如 "Steam"），未触发时为 ""。供 execute_code 断言反应播报是否真的收到了信号。</summary>
        public string LastReactionName { get; private set; } = "";

        /// <summary>同上，已转换成中文播报文案（<see cref="ReactionFeedbackCatalog.GetLabel"/>）。</summary>
        public string LastReactionLabel { get; private set; } = "";

        private ReactionLabelGUI _reactionLabels;

        private void EnsureReactionLabels()
        {
            if (_reactionLabels != null)
            {
                return;
            }
            var go = new GameObject("ComposeProjectileFeedback_ReactionLabels");
            _reactionLabels = go.AddComponent<ReactionLabelGUI>();
        }

        private void SpawnReactionLabel(Vector3 worldPos, string label)
        {
            EnsureReactionLabels();
            _reactionLabels.Spawn(worldPos, label);
        }

        /// <summary>story-002：具名反应命中时的飘字播报。IMGUI 世界坐标投影，手法照抄
        /// <see cref="GameLogic.Battle.Feedback.WhiteboxCombatFeedback"/> 里伤害飘字用的 DamageNumberGUI，
        /// 只是数字换成反应文案 + 更大字号/更醒目配色，与普通伤害数字区分开，池很小（反应本来就该是
        /// 稀疏的高光时刻，不需要 24 格那么大的池）。</summary>
        private sealed class ReactionLabelGUI : MonoBehaviour
        {
            private const int PoolSize = 8;
            private const float RiseDistance = 1.3f;
            private const float Life = 0.9f;

            private struct Entry
            {
                public Vector3 WorldPos;
                public string Label;
                public float TimeLeft;
            }

            private readonly Entry[] _entries = new Entry[PoolSize];
            private int _cursor;
            private GUIStyle _style;

            public void Spawn(Vector3 worldPos, string label)
            {
                int idx = _cursor;
                _cursor = (_cursor + 1) % PoolSize;
                _entries[idx] = new Entry { WorldPos = worldPos, Label = label, TimeLeft = Life };
            }

            private void Update()
            {
                float dt = Time.deltaTime;
                for (int i = 0; i < PoolSize; i++)
                {
                    if (_entries[i].TimeLeft > 0f)
                    {
                        _entries[i].TimeLeft -= dt;
                    }
                }
            }

            private void OnGUI()
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    return;
                }

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    };
                }

                for (int i = 0; i < PoolSize; i++)
                {
                    if (_entries[i].TimeLeft <= 0f || string.IsNullOrEmpty(_entries[i].Label))
                    {
                        continue;
                    }

                    float t = 1f - (_entries[i].TimeLeft / Life);
                    Vector3 pos = _entries[i].WorldPos + Vector3.up * (RiseDistance * t);
                    Vector3 sp = cam.WorldToScreenPoint(pos);
                    if (sp.z <= 0f)
                    {
                        continue;
                    }

                    float alpha = 1f - t * t;
                    _style.normal.textColor = new Color(1f, 0.92f, 0.5f, alpha);
                    var rect = new Rect(sp.x - 90f, Screen.height - sp.y - 20f, 180f, 36f);
                    GUI.Label(rect, _entries[i].Label, _style);
                }
            }
        }

        /// <summary>story-006：加载 7 Shape × 4 role 共 28 个 VFX 槽的 Prefab 池。由
        /// <see cref="GameLogic.Battle.Feedback.ComposeProjectilePresenter"/> 在阶段进入后调用一次。</summary>
        public async UniTask LoadVfxBindingsAsync()
        {
            _vfxPool ??= new FeatureArtVfxPool();

            var ids = new List<string>(28);
            foreach (ShapeKind kind in Enum.GetValues(typeof(ShapeKind)))
            {
                foreach (string role in VfxRoles)
                {
                    ids.Add($"shape.{kind}.{role}");
                }
            }

            await _vfxPool.LoadAsync(ids);
        }

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

            // story-007 D1：元素 Tag 优先；story-008 R7③：无元素 Tag 但仍带其它 Tag（Physical/Catalyst 等）
            // 时不再直接退化成 Shape 底色，改用中性配色，避免所有非元素 Tag 弹道读起来跟无 Tag 弹道一样。
            string elementTag = ResolveElementTag(signal.Tags);
            string nonElementTag = elementTag.Length == 0 ? ResolveAnyTag(signal.Tags) : "";
            Color castColor;
            if (elementTag.Length > 0)
            {
                castColor = ElementColorFor(elementTag);
            }
            else if (nonElementTag.Length > 0)
            {
                castColor = FxRecipeCatalog.GetNeutralTagColor(nonElementTag);
            }
            else
            {
                castColor = ColorFor(kind);
            }
            LastResolvedElementTag = elementTag;
            LastResolvedNonElementTag = nonElementTag;
            LastCastColor = castColor;
            LastComputedRadius = radius;
            LastShapeKind = kind.ToString();

            // story-006：枪口 VFX——只有 shape.{kind}.muzzle 绑定时才生成，未绑定=零新增视觉，与今天行为一致。
            // 每次施法只生成一次（不随 segments 循环），复用 Direction 作为朝向。
            if (_vfxPool != null && _vfxPool.IsBound($"shape.{kind}.muzzle"))
            {
                float2 muzzleDir = math.normalizesafe(signal.Direction, new float2(0f, 1f));
                // combat-primitive-presentation P1：COMBAT-PRESENTATION.md §5"Charged/Focused 蓄力"行——
                // 蓄力基因贴的真实 Tag 是 "Charged"（GeneCatalog gene_capacitor/gene_magnet），枪口停留
                // 时长拉长，读出"这发是蓄力过的"。矩阵级呼吸挤压预警属于 §3.2①，跟随 Q弹形变一起接入。
                float muzzleLife = signal.Tags != null && signal.Tags.Contains("Charged")
                    ? MuzzleLife * ChargedMuzzleLifeMult : MuzzleLife;
                SpawnMarker(kind, signal.Origin, muzzleDir, 0f, 0f, 0f, radius, muzzleLife, castColor, "muzzle");
            }

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
                    // story-006：Count 多发方向扇形展开，与 MetabolicSliceBridge.ApplyEvent 落点结算用同一公式
                    // （R5）。segments==1 时 FanDirection 原样返回 signal.Direction，不影响单发既有观感。
                    // story-007：Melee 改用前方扇形（MeleeFanDirection，±ArcHalfAngleDeg），其余 Shape 仍走
                    // 全向 FanDirection——判定层 ApplyEvent 对二者做了同样区分，这里必须对齐，否则白模方向
                    // 与真实命中圆心错位。
                    float2 dir = kind == ShapeKind.Melee
                        ? MetabolicSliceBridge.MeleeFanDirection(signal.Direction, h, segments)
                        : MetabolicSliceBridge.FanDirection(signal.Direction, h, segments);
                    // 正确性修复：signal.Homing/HomingDirection 是"整次施法"的单一代表值（同 Direction 的既有
                    // 简化，不按发分别再查一次最近敌人），Count>1 时每个扇形分身共享同一个目标朝向插值——
                    // 比此前"完全不弯"更接近真实命中点，单发（segments==1，homing 场景绝大多数情形）完全精确。
                    SpawnMarker(kind, signal.Origin, dir, phase, signal.Spin, signal.Orbit, radius, life, castColor,
                        homing: signal.Homing, homingDirection: signal.HomingDirection);
                }
            }

            // story-007 D5/D6/D7/D8：Explode 是独立叠加标记，与 Shape 正交，禁止并入上面的分支判断。
            if (signal.ExplodeOnHit)
            {
                float explodeRadius = radius * MetabolicSliceBridge.ExplodeRadiusMult;
                Color explodeColor = elementTag.Length > 0 ? castColor : FxRecipeCatalog.DefaultExplodeColor;
                LastExplodeRadius = explodeRadius;
                // story-006：落地爆炸环坐标从玩家位置改成落点，与 MetabolicSliceBridge.ApplyEvent 的延迟命中
                // 落点用同一飞行距离（R5）。Wave 的 ApplyTransform 在 Spin=Orbit=0 时 offset 恒为零向量，
                // 传入落点作为 origin 即可让环"就地在落点炸开"，不需要改 Wave 自身的位移公式（避免影响
                // Spin/Orbit 主形态那条 Wave 分支，见 ComposeShapePresentation）。Melee-tail 的判定仍原地
                // 瞬时结算（近战方向留给 007），环坐标同步保持 signal.Origin，避免视觉与判定落点错位。
                float2 explodeOrigin = signal.Origin;
                if (kind != ShapeKind.Melee)
                {
                    float2 explodeDir = math.normalizesafe(signal.Direction, new float2(0f, 1f));
                    explodeOrigin = signal.Origin + explodeDir * MetabolicSliceBridge.ImpactFlightDistance;
                }
                SpawnMarker(ShapeKind.Wave, explodeOrigin, signal.Direction, 0f, 0f, 0f, explodeRadius, FlightLife, explodeColor);
            }
            else
            {
                LastExplodeRadius = 0f;
            }

            // story-006：命中 VFX——与 ExplodeOnHit 相互独立，只要 shape.{kind}.hit 绑定就生成，
            // 落点公式与上面的 explodeOrigin 一致（Melee 用 Origin，其余沿 Direction 飞 ImpactFlightDistance）。
            if (_vfxPool != null && _vfxPool.IsBound($"shape.{kind}.hit"))
            {
                float2 hitOrigin = signal.Origin;
                if (kind != ShapeKind.Melee)
                {
                    float2 hitDir = math.normalizesafe(signal.Direction, new float2(0f, 1f));
                    hitOrigin = signal.Origin + hitDir * MetabolicSliceBridge.ImpactFlightDistance;
                }
                SpawnMarker(kind, hitOrigin, signal.Direction, 0f, 0f, 0f, radius * 0.3f, FlightLife, castColor, "hit");
            }

            // reaction-depth-and-combat-feel story-002：具名反应（rx_*/env_* 等，MetabolicSliceBridge.
            // ApplyEvent 已从 evt.Payload["Reaction"] 转发）命中时额外播报，与上面的普通命中/爆炸标记
            // 正交叠加（不是二选一分支）——落点公式复用"命中 VFX"同一套（Melee 用 Origin，其余沿
            // Direction 飞 ImpactFlightDistance），保持与真实命中圆心一致。
            if (!string.IsNullOrEmpty(signal.ReactionName))
            {
                string label = ReactionFeedbackCatalog.GetLabel(signal.ReactionName);
                LastReactionName = signal.ReactionName;
                LastReactionLabel = label;

                float2 reactionPos = signal.Origin;
                if (kind != ShapeKind.Melee)
                {
                    float2 reactionDir = math.normalizesafe(signal.Direction, new float2(0f, 1f));
                    reactionPos = signal.Origin + reactionDir * MetabolicSliceBridge.ImpactFlightDistance;
                }

                SpawnMarker(ShapeKind.Wave, reactionPos, signal.Direction, 0f, 0f, 0f,
                    radius * ReactionRingMult, FlightLife * ReactionLifeMult, ReactionRingColor);
                SpawnReactionLabel(new Vector3(reactionPos.x, MarkerY + 0.8f, reactionPos.y), label);
            }
            else
            {
                LastReactionName = "";
                LastReactionLabel = "";
            }
        }

        /// <summary>story-003（reaction-depth-and-combat-feel）：结构器官被动触发的最小可见反馈——
        /// 一个短促的青色小环，不区分 Kind（5 种钩子目前共用同一视觉，先解决"完全看不见"，
        /// 按 Kind 分色留给真实美术阶段）。半径取信号值与最小可视半径的较大者，避免 Kill/LowHealth
        /// 这类无天然区域概念的钩子传入 0 半径时环小到看不见。</summary>
        public void OnStructuralHookFired(StructuralHookFiredSignal signal)
        {
            float radius = MathF.Max(1.2f, signal.Radius);
            SpawnMarker(ShapeKind.Wave, signal.Position, new float2(0f, 1f), 0f, 0f, 0f,
                radius, StructuralHookLife, StructuralHookColor);
        }

        /// <summary>combat-primitive-presentation P1：Pierce（穿透续飞）/Bounce（弹开）/Split（分裂）/
        /// Return（飞回）/Linger（残留毒场）此前完全没有对应视觉。Kind 固定几何体+配色，供玩家分辨
        /// "命中之后又发生了什么"，不追溯原始 Shape（同 <see cref="OnStructuralHookFired"/> 简化先例）。</summary>
        public void OnComposeChain(ComposeChainSignal signal)
        {
            float radius = MathF.Max(0.6f, signal.Radius);
            switch (signal.Kind)
            {
                case "Pierce":
                    // 复用 Bolt 的直线飞行几何，让它读出"穿过去继续飞"，forceTrail 补一条淡出拖尾。
                    SpawnMarker(ShapeKind.Bolt, signal.Position, signal.Direction, 0f, 0f, 0f,
                        radius * 0.6f, ChainMarkerLife, PierceColor, forceTrail: true);
                    break;
                case "Bounce":
                    SpawnMarker(ShapeKind.Wave, signal.Position, signal.Direction, 0f, 0f, 0f,
                        radius * 0.8f, ChainMarkerLife, BounceColor);
                    break;
                case "Split":
                    SpawnMarker(ShapeKind.Bolt, signal.Position, signal.Direction, 0f, 0f, 0f,
                        radius, ChainMarkerLife, SplitColor);
                    break;
                case "Return":
                    SpawnMarker(ShapeKind.Bolt, signal.Position, signal.Direction, 0f, 0f, 0f,
                        radius * 0.7f, ChainMarkerLife, ReturnColor, forceTrail: true);
                    break;
                case "Linger":
                    // Field 语言的贴地环，寿命=真实 Linger 秒数（不是固定短促时长），到期正好和结算窗口对齐。
                    float lingerLife = signal.Duration > 0f ? signal.Duration : ChainMarkerLife;
                    SpawnMarker(ShapeKind.Field, signal.Position, signal.Direction, 0f, 0f, 0f,
                        radius, lingerLife, LingerColor);
                    break;
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

        /// <summary>story-008：ElementPriorityOrder 未收录时的兜底——取 Tags 里任意一个非空字符串，
        /// 只用于挑一个稳定 key 去查 <see cref="FxRecipeCatalog.GetNeutralTagColor"/>，不代表优先级语义。</summary>
        private static string ResolveAnyTag(HashSet<string> tags)
        {
            if (tags == null)
            {
                return "";
            }
            foreach (string tag in tags)
            {
                if (!string.IsNullOrEmpty(tag))
                {
                    return tag;
                }
            }
            return "";
        }

        public void Tick(float dt)
        {
            if (_timeLeft == null)
            {
                return;
            }

            int active = 0;
            int trailActive = 0;
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
                    _trail[i].emitting = false;
                    if (_prefabGo[i] != null)
                    {
                        _vfxPool.Release(_prefabGo[i]);
                        _prefabGo[i] = null;
                    }
                    continue;
                }

                active++;
                ApplyTransform(i);

                float u = 1f - (_timeLeft[i] / _life[i]);
                Color c = _color[i];
                c.a *= (1f - u) * (1f - u);
                _mr[i].sharedMaterial.color = c;

                if (_trail[i].emitting)
                {
                    trailActive++;
                }
            }

            ActiveMarkerCount = active;
            ActiveTrailCount = trailActive;
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
            _trail = new TrailRenderer[PoolSize];
            _kind = new ShapeKind[PoolSize];
            _origin = new float2[PoolSize];
            _direction = new float2[PoolSize];
            _homingDir = new float2[PoolSize];
            _homingStrength = new float[PoolSize];
            _phase = new float[PoolSize];
            _spin = new float[PoolSize];
            _orbit = new float[PoolSize];
            _radius = new float[PoolSize];
            _elapsed = new float[PoolSize];
            _timeLeft = new float[PoolSize];
            _life = new float[PoolSize];
            _color = new Color[PoolSize];
            _prefabGo = new GameObject[PoolSize];

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

                // story-008 R7①：残影轨迹组件，随 Marker 一起创建/复用，默认关闭（SpawnMarker 按
                // spin/orbit 是否非零决定是否 emitting，不额外分配 GameObject/池）。
                var trail = go.AddComponent<TrailRenderer>();
                trail.sharedMaterial = mr.sharedMaterial;
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                trail.receiveShadows = false;
                trail.widthMultiplier = 1f;
                trail.startWidth = 0.3f;
                trail.endWidth = 0.05f;
                trail.numCapVertices = 2;
                trail.minVertexDistance = 0.05f;
                trail.emitting = false;
                trail.time = FlightLife;

                _tf[i] = go.transform;
                _mf[i] = mf;
                _mr[i] = mr;
                _trail[i] = trail;
                _timeLeft[i] = 0f;
                _life[i] = 0.2f;
                _color[i] = Color.white;
            }
        }

        private void SpawnMarker(ShapeKind kind, float2 origin, float2 direction, float phase, float spin, float orbit,
            float radius, float life, Color color, string role = "projectile", float homing = 0f, float2 homingDirection = default,
            bool forceTrail = false)
        {
            EnsurePool();

            int idx = _cursor;
            _cursor = (_cursor + 1) % PoolSize;

            _kind[idx] = kind;
            _origin[idx] = origin;
            _direction[idx] = math.normalizesafe(direction, new float2(0f, 1f));
            // 正确性修复：homing<=0（绝大多数弹道）时 _homingDir 直接等于 _direction，ApplyTransform 的
            // 插值在该情形下退化为原朝向，零回归；homing>0 时才真的存一个不同的目标朝向供逐帧插值。
            _homingStrength[idx] = homing;
            _homingDir[idx] = homing > 0f ? math.normalizesafe(homingDirection, _direction[idx]) : _direction[idx];
            _phase[idx] = phase;
            _spin[idx] = spin;
            _orbit[idx] = orbit;
            _radius[idx] = radius;
            _elapsed[idx] = 0f;
            _timeLeft[idx] = life;
            _life[idx] = life;
            _color[idx] = color;

            // story-006：槽位被复用时，先归还上一占用者的 VFX Prefab 实例（若有），再按当前 role 重新取用——
            // 池 Prefab 的激活态跨调用持续，不像 MeshRenderer 每次都被覆盖写，必须显式 Release 防止悬空激活实例。
            if (_prefabGo[idx] != null)
            {
                _vfxPool.Release(_prefabGo[idx]);
            }
            string slotId = $"shape.{kind}.{role}";
            GameObject prefabGo = _vfxPool?.TryAcquire(slotId);
            _prefabGo[idx] = prefabGo;
            LastVfxPrefabName = prefabGo != null ? prefabGo.name : LastVfxPrefabName;

            _mf[idx].sharedMesh = MeshFor(kind);
            _mr[idx].enabled = prefabGo == null;
            _mr[idx].sharedMaterial.color = _color[idx];

            // story-008 R7①：spin/orbit 非零才画残影，避免 Bolt/Melee 等直线运动多出一条无意义的拖尾。
            // 每次复用槽位都先 Clear，防止上一次别的 Shape 留下的轨迹点残留到本次标记上。
            // story-006：有 Prefab 覆盖时不叠加程序化残影，避免视觉冲突。
            TrailRenderer trail = _trail[idx];
            trail.Clear();
            // combat-primitive-presentation P1：Pierce 续飞标记额外要求拖尾（forceTrail），
            // 与 Spin/Orbit 触发拖尾的既有条件是"或"关系，互不影响。
            bool wantsTrail = (spin != 0f || orbit != 0f || forceTrail) && prefabGo == null;
            if (wantsTrail)
            {
                trail.time = life;
                Color trailColor = color;
                trailColor.a *= 0.35f;
                Color trailEndColor = trailColor;
                trailEndColor.a = 0f;
                trail.startColor = trailColor;
                trail.endColor = trailEndColor;
            }
            trail.emitting = wantsTrail;

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
                    // 接上角色：起点固定钉在施法坐标（=玩家胶囊）上，前 BeamIgniteDuration 秒长度从 0
                    // 抻到满——读出"从身上打出一道光束"，而不是凭空出现的静态色块（原地贴身瞬间满长）。
                    float grownLength = length * Mathf.Clamp01(_elapsed[idx] / BeamIgniteDuration);
                    _tf[idx].localPosition = new Vector3(origin.x, MarkerY, origin.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(grownLength, 1f, width);
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
                    // D3 强同步：白模半径直接等于 radius，无额外系数（落地后满径不变）。
                    // 接上角色：落地前 FlightLife 秒沿 Direction 从施法点（玩家胶囊）线性飞到落点
                    // （与 Bolt/Spore 同一个 ImpactFlightDistance），呼应 CATALOG「把酶雾扔到落点」的设计——
                    // 此前 Field 无任何位移，原地贴身瞬间满径出现，读不出"甩出去"。落地后再用
                    // FieldSettleDuration 秒从小长到满径，两段时长相加=PersistentLife，不会在满径前提前回收。
                    float2 throwDir = math.normalizesafe(_direction[idx], new float2(0f, 1f));
                    float2 landing = origin + throwDir * MetabolicSliceBridge.ImpactFlightDistance;
                    float flightT = FlightLife > 0f ? Mathf.Clamp01(_elapsed[idx] / FlightLife) : 1f;
                    float2 pos = math.lerp(origin, landing, flightT);
                    float settleT = FieldSettleDuration > 0f
                        ? Mathf.Clamp01((_elapsed[idx] - FlightLife) / FieldSettleDuration)
                        : 1f;
                    float diameterScale = flightT >= 1f ? Mathf.Lerp(0.3f, 1f, settleT) : 0.3f;
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(radius * 2f * diameterScale, 1f, radius * 2f * diameterScale);
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
                    // story-006：Spore 专用于 ExplodeOnHit 主形态（ComposeShapePresentation.Resolve），
                    // 叠加沿 Direction 的独立线性飞行，与 Bolt 同款手法，让"扔出去"读得出来，与
                    // MetabolicSliceBridge 的延迟落点结算同步到达（同一 ImpactFlightDistance/生命周期）。
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 linear = _direction[idx] * (MetabolicSliceBridge.ImpactFlightDistance * u);
                    float2 pos = origin + offset + linear;
                    float diameter = radius * recipe.DiameterCoef;
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.identity;
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    break;
                }
                case ShapeKind.Melee:
                {
                    // story-007 R6：近战「朝向旋转 + 前移 + 方向性拉伸」——不再原地放大/identity 旋转。
                    // 前移量与 MetabolicSliceBridge 的判定圆心共用同一个数（MeleeMuzzleOffset），
                    // 不随 u 增长（非线性飞行，仍是"原地生长"，只是生长的原点前移了），
                    // Spin/Orbit 偏移沿用 003 D5（=0 时天然零向量）。
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    float2 dir = _direction[idx];
                    float2 muzzle = dir * MeleeMuzzleOffset;
                    float2 pos = origin + offset + muzzle;
                    float grown = radius * recipe.DiameterCoef * (0.6f + 0.4f * Mathf.Clamp01(u));
                    float angDeg = DirectionAngleDeg(dir);
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(grown * MeleeForwardStretch, 1f, grown * MeleeSideStretch);
                    LastMeleePosition = _tf[idx].localPosition;
                    break;
                }
                case ShapeKind.Bolt:
                default:
                {
                    float2 offset = ComposeMotionMath.Offset(_phase[idx], _spin[idx], _orbit[idx], _elapsed[idx]);
                    // 正确性修复：_homingStrength[idx]<=0（绝大多数 Bolt）时 curDir 恒等于 _direction[idx]，
                    // 下面这段与改动前逐字节一致，零回归。>0 时随飞行进度 u（0→1）从初始瞄准方向朝
                    // _homingDir[idx] 插值弯过去，插值终值按 homingStrength 加权——与 MetabolicSliceBridge.
                    // ApplyChassisDamage 用同一个 evt.Homing 对命中判定点做 lerp 是同一权重语义，不是另起系数。
                    float2 curDir = _homingStrength[idx] > 0f
                        ? math.normalizesafe(math.lerp(_direction[idx], _homingDir[idx], _homingStrength[idx] * u), _direction[idx])
                        : _direction[idx];
                    // story-006：叠加沿 Direction 的独立线性飞行位移，让 Bolt 读得出「射出去」——
                    // Spin=Orbit=0 时 offset 天然是零向量，与 offset 正交不冲突（Decision 4）。
                    // story-004：加 BoltMuzzleOffset 常量前移，避免 u=0 时贴在玩家原点上。
                    float2 linear = curDir * (BoltMuzzleOffset + BoltFlightDistance * u);
                    float2 pos = origin + offset + linear;
                    float diameter = radius * recipe.DiameterCoef;
                    // story-010 J1：Bolt 换成有指向的锥形网格后必须跟着 Direction 转——
                    // 原来是 Circle（旋转对称）才可以 identity，留着 identity 等于锥尖恒指世界 +X。
                    float angDeg = DirectionAngleDeg(curDir);
                    _tf[idx].localPosition = new Vector3(pos.x, MarkerY, pos.y);
                    _tf[idx].localRotation = Quaternion.Euler(0f, -angDeg, 0f);
                    _tf[idx].localScale = new Vector3(diameter, 1f, diameter);
                    LastBoltPosition = _tf[idx].localPosition;
                    break;
                }
            }

            // story-006：有 Prefab 覆盖时，只同步位置/朝向——缩放由资源自身决定，运行时不缩放。
            if (_prefabGo[idx] != null)
            {
                _prefabGo[idx].transform.position = _tf[idx].localPosition;
                _prefabGo[idx].transform.rotation = _tf[idx].localRotation;
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

        /// <summary>story-008：<see cref="UnityEngine.Object.Destroy"/> 在编辑器非 Play 态下会报
        /// "Destroy may not be called from edit mode" 错误——本类此前只在 Play 期间随场景卸载触发 Dispose，
        /// 从未暴露过；<see cref="MechanismReadoutSmokeReport"/> 新增了 Edit 模式下的直接 Dispose 调用（验收
        /// 优先代码断言，不进 Play），按 <see cref="Application.isPlaying"/> 分流即可，语义不变。</summary>
        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(obj);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        public void Dispose()
        {
            if (_poolRoot != null)
            {
                for (int i = 0; i < _mr.Length; i++)
                {
                    if (_mr[i] != null)
                    {
                        SafeDestroy(_mr[i].sharedMaterial);
                    }
                }

                SafeDestroy(_poolRoot);
                _poolRoot = null;
                _tf = null;
                _mf = null;
                _mr = null;
                _trail = null;
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
                _prefabGo = null;
            }

            _vfxPool?.Dispose();
            _vfxPool = null;

            if (_reactionLabels != null)
            {
                SafeDestroy(_reactionLabels.gameObject);
                _reactionLabels = null;
            }

            if (_matTemplate != null)
            {
                SafeDestroy(_matTemplate);
                _matTemplate = null;
            }

            if (_circleMesh != null)
            {
                SafeDestroy(_circleMesh);
                _circleMesh = null;
            }
            if (_streakMesh != null)
            {
                SafeDestroy(_streakMesh);
                _streakMesh = null;
            }
            if (_wedgeMesh != null)
            {
                SafeDestroy(_wedgeMesh);
                _wedgeMesh = null;
            }
            if (_ringMesh != null)
            {
                SafeDestroy(_ringMesh);
                _ringMesh = null;
            }
            if (_coneMesh != null)
            {
                SafeDestroy(_coneMesh);
                _coneMesh = null;
            }
            if (_crossMesh != null)
            {
                SafeDestroy(_crossMesh);
                _crossMesh = null;
            }
            if (_bandMesh != null)
            {
                SafeDestroy(_bandMesh);
                _bandMesh = null;
            }
        }
    }
}
