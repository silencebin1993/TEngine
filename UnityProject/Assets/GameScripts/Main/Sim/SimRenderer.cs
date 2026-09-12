using System.Collections.Generic;
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
    /// Built-in RP 下按 VisualId 分批 <see cref="Graphics.RenderMeshInstanced"/>（每批 ≤1023）。
    /// 材质须支持 GPU Instancing。默认 Look：SimBioGlass；LOD/压测：SimInstancedUnlit。
    /// 定案：DesignDocs/Material_LookDev_BioGlass.md
    ///
    /// 每实例额外写入：
    /// - <c>_Motion</c>：游动方向 + 速度强度（来自 Snapshot.Velocity）
    /// - <c>_Impact</c>：受击压缩方向 + 强度（来自本帧 Hits，带衰减）
    /// 成本：每单位几个 float 算术 + 每批多两次 SetVectorArray；无额外 Draw Call。
    /// </summary>
    public sealed class SimRenderer
    {
        private const int BatchMax = 1023;
        private const float ImpactDecay = 0.88f;
        private const float ImpactDamageScale = 0.28f;
        private const float SpeedRef = 4.5f;
        /// <summary>投射物沿飞行方向拉长的倍率——纯圆点在快速移动时几乎不可见（story-010 V1）。</summary>
        private const float ProjectileStretch = 2.2f;

        /// <summary>combat-primitive-presentation story-004（COMBAT-PRESENTATION §4）：近战整体前冲的
        /// 最大位移距离（世界单位）。纯表现位移，不改 <see cref="SimSnapshot.PlayerPosition"/>/碰撞判定。</summary>
        private const float PlayerLungeDistance = 0.6f;
        private const float ControlledScalePulse = 0.08f;

        private SimVisual[] _visuals;
        private Matrix4x4[][] _matrices;
        private Vector4[][] _colors;
        private Vector4[][] _motions;
        private Vector4[][] _impacts;
        private int[] _counts;
        private MaterialPropertyBlock _props;
        private Matrix4x4[] _batchMatrices;
        private Vector4[] _batchColors;
        private Vector4[] _batchMotions;
        private Vector4[] _batchImpacts;

        /// <summary>按内核单位索引缓存的受击冲量（跨帧衰减）。</summary>
        private float2[] _impactDir;
        private float[] _impactAmt;
        private readonly Dictionary<int, int> _logicToIndex = new Dictionary<int, int>(256);

        /// <summary>combat-primitive-presentation story-004：近战整体前冲的方向+当前强度（0-1，由
        /// HotFix 层 <see cref="SetPlayerLunge"/> 每帧写入，随近战判定的时间窗自然归零，本类不自己
        /// 计时——避免这里长出一份独立于施法逻辑的计时状态）。</summary>
        private float2 _playerLungeDir;
        private float _playerLungeAmount;
        private SimEntityId _lastControlledUnitId;

        /// <summary>最近一次 Draw 解析到的受控槽位，仅供表现诊断与自动回归。</summary>
        public int LastControlledUnitIndex { get; private set; } = SimConst.InvalidIndex;
        public SimEntityId LastControlledUnitId => _lastControlledUnitId;
        public bool HasControlledLunge => _playerLungeAmount > 0f;

        private float _yPlane;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MotionId = Shader.PropertyToID("_Motion");
        private static readonly int ImpactId = Shader.PropertyToID("_Impact");

        public void Initialize(SimVisual[] visuals, int capacity, float yPlane = 0f)
        {
            _visuals = visuals ?? new SimVisual[0];
            _yPlane = yPlane;
            _props = new MaterialPropertyBlock();
            _batchMatrices = new Matrix4x4[BatchMax];
            _batchColors = new Vector4[BatchMax];
            _batchMotions = new Vector4[BatchMax];
            _batchImpacts = new Vector4[BatchMax];
            _impactDir = new float2[capacity];
            _impactAmt = new float[capacity];

            int n = _visuals.Length;
            _matrices = new Matrix4x4[n][];
            _colors = new Vector4[n][];
            _motions = new Vector4[n][];
            _impacts = new Vector4[n][];
            _counts = new int[n];
            for (int i = 0; i < n; i++)
            {
                _matrices[i] = new Matrix4x4[capacity];
                _colors[i] = new Vector4[capacity];
                _motions[i] = new Vector4[capacity];
                _impacts[i] = new Vector4[capacity];
            }
        }

        public void Draw(in SimSnapshot snap)
        {
            int controlledIndex = snap.TryResolveControlledUnit(out int resolvedControlledIndex)
                ? resolvedControlledIndex
                : SimConst.InvalidIndex;
            if (_lastControlledUnitId != snap.ControlledUnitId)
            {
                ClearControlledPresentation();
                _lastControlledUnitId = snap.ControlledUnitId;
            }
            LastControlledUnitIndex = controlledIndex;

            if (_visuals == null || _visuals.Length == 0)
            {
                return;
            }

            EnsureImpactCapacity(snap.Count);
            DecayAndApplyImpacts(in snap);

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
                if (i == controlledIndex && _playerLungeAmount > 0f)
                {
                    // combat-primitive-presentation story-004：纯渲染位移，不写回 Position，不影响碰撞/寻路。
                    p += _playerLungeDir * (PlayerLungeDistance * _playerLungeAmount);
                }
                bool isControlled = i == controlledIndex;
                float controlPulse = isControlled
                    ? 0.5f + 0.5f * Mathf.Sin(Time.time * 5f)
                    : 0f;
                float s = snap.Radius[i] * 2f * _visuals[v].ScaleMul;
                if (isControlled)
                {
                    s *= 1f + ControlledScalePulse * controlPulse;
                }
                _matrices[v][c] = Matrix4x4.TRS(
                    new Vector3(p.x, _yPlane, p.y),
                    Quaternion.identity,
                    new Vector3(s, s, s));

                Color tint = Tint(_visuals[v].BaseColor, snap.Status[i], Time.time);
                if (isControlled)
                {
                    tint = Color.Lerp(tint, new Color(0.35f, 1f, 1f, 1f), 0.35f + controlPulse * 0.2f);
                }
                _colors[v][c] = tint;
                _motions[v][c] = PackMotion(snap.Velocity[i]);
                _impacts[v][c] = PackImpact(i);
                _counts[v] = c + 1;
            }

            for (int v = 0; v < _visuals.Length; v++)
            {
                int total = _counts[v];
                if (total == 0 || _visuals[v].Mesh == null || _visuals[v].Material == null)
                {
                    continue;
                }

                for (int off = 0; off < total; off += BatchMax)
                {
                    int n = Mathf.Min(BatchMax, total - off);
                    System.Array.Copy(_matrices[v], off, _batchMatrices, 0, n);
                    System.Array.Copy(_colors[v], off, _batchColors, 0, n);
                    System.Array.Copy(_motions[v], off, _batchMotions, 0, n);
                    System.Array.Copy(_impacts[v], off, _batchImpacts, 0, n);

                    _props.Clear();
                    _props.SetVectorArray(ColorId, _batchColors);
                    _props.SetVectorArray(MotionId, _batchMotions);
                    _props.SetVectorArray(ImpactId, _batchImpacts);

                    var rp = new RenderParams(_visuals[v].Material)
                    {
                        worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                        shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                        receiveShadows = false,
                        matProps = _props,
                    };

                    Graphics.RenderMeshInstanced(rp, _visuals[v].Mesh, 0, _batchMatrices, n);
                }
            }
        }

        /// <summary>combat-primitive-presentation story-004（COMBAT-PRESENTATION §4）：近战攻击触发时，
        /// HotFix 层每帧调用本方法写入当前前冲方向+强度（0=无位移，1=位移满 <see cref="PlayerLungeDistance"/>）。
        /// 只在下一次 <see cref="Draw"/> 时对快照中 <see cref="SimSnapshot.ControlledUnitId"/> 对应实例
        /// 的渲染矩阵生效，不进模拟状态、不碰其它单位，成本恒为 O(1)。</summary>
        public void SetControlledLunge(float2 direction, float amount)
        {
            _playerLungeDir = direction;
            _playerLungeAmount = math.saturate(amount);
        }

        /// <summary>兼容旧入口；语义已经改为当前受控实体。</summary>
        public void SetPlayerLunge(float2 direction, float amount) => SetControlledLunge(direction, amount);

        /// <summary>切换或失去控制目标时立即清理玩家专属前冲缓存。</summary>
        public void ClearControlledPresentation()
        {
            _playerLungeDir = float2.zero;
            _playerLungeAmount = 0f;
        }

        /// <summary>投射物渲染。数量少，用一个固定视觉。</summary>
        public void DrawProjectiles(NativeArray<ProjectileState> projectiles, in SimVisual visual)
        {
            if (visual.Mesh == null || visual.Material == null || !projectiles.IsCreated)
            {
                return;
            }

            int n = 0;
            for (int i = 0; i < projectiles.Length; i++)
            {
                ProjectileState s = projectiles[i];
                if (s.Alive == 0)
                {
                    continue;
                }

                float ang = math.atan2(s.Velocity.y, s.Velocity.x) * Mathf.Rad2Deg;
                float sc = s.Radius * 2f * visual.ScaleMul;
                // 局部 +X 经此旋转后对齐飞行方向，拉长 X 让弹体读作"射出去的东西"而不是一个点。
                _batchMatrices[n] = Matrix4x4.TRS(
                    new Vector3(s.Position.x, _yPlane, s.Position.y),
                    Quaternion.Euler(0f, -ang, 0f),
                    new Vector3(sc * ProjectileStretch, sc, sc));
                // combat-primitive-overhaul：逐弹体色。热更层把元素/反应配色打包进 Tint 带下来，
                // 真弹体因此保留了此前只有白模才有的染色，不会因为"弹道改走内核"就退回全场一律亮黄。
                _batchColors[n] = s.Tint != 0u
                    ? new Vector4(
                        ((s.Tint >> 24) & 0xFFu) / 255f,
                        ((s.Tint >> 16) & 0xFFu) / 255f,
                        ((s.Tint >> 8) & 0xFFu) / 255f,
                        (s.Tint & 0xFFu) / 255f)
                    : new Vector4(
                        visual.BaseColor.r, visual.BaseColor.g, visual.BaseColor.b, visual.BaseColor.a);
                _batchMotions[n] = PackMotion(s.Velocity);
                _batchImpacts[n] = Vector4.zero;
                n++;

                if (n == BatchMax)
                {
                    FlushProjectileBatch(visual, n);
                    n = 0;
                }
            }

            if (n > 0)
            {
                FlushProjectileBatch(visual, n);
            }
        }

        /// <summary>
        /// 持续区域（毒坑 / 酸洼 / 贴身毒环）。
        ///
        /// enemy-mechanics-parity：区域下沉进内核之后，敌人也会放毒了——
        /// 但**没有渲染通道的伤害区就是"看不见的伤害"**，正是这条线一路在修的那类问题
        /// （"打得到的看不见"）。这里按内核的真实半径逐帧画，区域涨多大画多大，
        /// 与 <see cref="DrawProjectiles"/> 同一套实例化批次，不另起渲染路径。
        ///
        /// 贴地：Y 压到 <c>_yPlane</c> 略下方，避免与单位 z-fighting；
        /// 高度压扁成一片，读作"地上的一摊"而不是一个球。
        /// </summary>
        public void DrawZones(NativeArray<ZoneState> zones, in SimVisual visual)
        {
            if (visual.Mesh == null || visual.Material == null || !zones.IsCreated)
            {
                return;
            }

            int n = 0;
            for (int i = 0; i < zones.Length; i++)
            {
                ZoneState z = zones[i];
                if (z.Alive == 0)
                {
                    continue;
                }

                float d = z.Radius * 2f * visual.ScaleMul;
                _batchMatrices[n] = Matrix4x4.TRS(
                    new Vector3(z.Position.x, _yPlane - ZoneYOffset, z.Position.y),
                    Quaternion.identity,
                    new Vector3(d, d * ZoneFlatten, d));
                _batchColors[n] = z.Tint != 0u
                    ? new Vector4(
                        ((z.Tint >> 24) & 0xFFu) / 255f,
                        ((z.Tint >> 16) & 0xFFu) / 255f,
                        ((z.Tint >> 8) & 0xFFu) / 255f,
                        (z.Tint & 0xFFu) / 255f)
                    : new Vector4(
                        visual.BaseColor.r, visual.BaseColor.g, visual.BaseColor.b, visual.BaseColor.a);
                _batchMotions[n] = Vector4.zero;
                _batchImpacts[n] = Vector4.zero;
                n++;

                if (n == BatchMax)
                {
                    FlushProjectileBatch(visual, n);
                    n = 0;
                }
            }

            if (n > 0)
            {
                FlushProjectileBatch(visual, n);
            }
        }

        /// <summary>区域压到地面下方一点，避免与单位/弹体 z-fighting。</summary>
        private const float ZoneYOffset = 0.12f;
        /// <summary>区域高度压扁系数——读作"地上的一摊"，不是一个球。</summary>
        private const float ZoneFlatten = 0.08f;

        private void FlushProjectileBatch(in SimVisual visual, int n)
        {
            _props.Clear();
            _props.SetVectorArray(ColorId, _batchColors);
            _props.SetVectorArray(MotionId, _batchMotions);
            _props.SetVectorArray(ImpactId, _batchImpacts);
            var rp = new RenderParams(visual.Material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000f),
                shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = false,
                matProps = _props,
            };
            Graphics.RenderMeshInstanced(rp, visual.Mesh, 0, _batchMatrices, n);
        }

        private void EnsureImpactCapacity(int count)
        {
            if (_impactAmt != null && _impactAmt.Length >= count)
            {
                return;
            }

            int cap = math.max(count, 64);
            _impactDir = new float2[cap];
            _impactAmt = new float[cap];
        }

        private void DecayAndApplyImpacts(in SimSnapshot snap)
        {
            int cap = math.min(snap.Count, _impactAmt.Length);
            for (int i = 0; i < cap; i++)
            {
                _impactAmt[i] *= ImpactDecay;
                if (_impactAmt[i] < 0.01f)
                {
                    _impactAmt[i] = 0f;
                    _impactDir[i] = float2.zero;
                }
            }

            if (snap.HitCount <= 0 || !snap.Hits.IsCreated)
            {
                return;
            }

            _logicToIndex.Clear();
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0)
                {
                    continue;
                }

                _logicToIndex[snap.LogicId[i]] = i;
            }

            for (int h = 0; h < snap.HitCount; h++)
            {
                HitEvent hit = snap.Hits[h];
                if (!_logicToIndex.TryGetValue(hit.TargetLogicId, out int idx) || idx >= _impactAmt.Length)
                {
                    continue;
                }

                float2 toCenter = snap.Position[idx] - hit.Position;
                float2 n = math.normalizesafe(toCenter, float2.zero);
                if (math.lengthsq(n) < 1e-6f)
                {
                    // 命中点几乎在中心：用速度反方向当压缩轴
                    n = math.normalizesafe(-snap.Velocity[idx], new float2(1f, 0f));
                }
                else
                {
                    // 外侧压向中心 → 压缩方向取 -toCenter（从接触点指向外法线）
                    n = -n;
                }

                float add = math.saturate(hit.Damage * ImpactDamageScale);
                // 叠加同向冲量
                float2 blended = _impactDir[idx] * _impactAmt[idx] + n * add;
                float amt = math.min(1f, math.length(blended));
                _impactDir[idx] = math.normalizesafe(blended, n);
                _impactAmt[idx] = amt;
            }
        }

        private static Vector4 PackMotion(float2 velocity)
        {
            float speed = math.length(velocity);
            if (speed < 1e-4f)
            {
                return Vector4.zero;
            }

            float2 dir = velocity / speed;
            float strength = math.saturate(speed / SpeedRef);
            return new Vector4(dir.x, dir.y, strength, 0f);
        }

        private Vector4 PackImpact(int unitIndex)
        {
            if (_impactAmt == null || unitIndex < 0 || unitIndex >= _impactAmt.Length)
            {
                return Vector4.zero;
            }

            float amt = _impactAmt[unitIndex];
            if (amt < 0.01f)
            {
                return Vector4.zero;
            }

            float2 d = _impactDir[unitIndex];
            return new Vector4(d.x, d.y, amt, 0f);
        }

        /// <summary>
        /// 状态染色。万敌规模下可读性对策（Spec §16）。
        /// </summary>
        private static Vector4 Tint(Color baseColor, uint status, float time)
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
            if ((status & (uint)SimStatus.Telegraphing) != 0u)
            {
                // 蓄力脉冲：在原色与警示橙红间来回插值，让"即将冲刺"和"普通移动"一眼可辨
                float pulse = Mathf.PingPong(time * 4f, 1f);
                c = Color.Lerp(c, new Color(1f, 0.45f, 0.1f), 0.35f + 0.45f * pulse);
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
            _motions = null;
            _impacts = null;
            _counts = null;
            _props = null;
            _batchMatrices = null;
            _batchColors = null;
            _batchMotions = null;
            _batchImpacts = null;
            _impactDir = null;
            _impactAmt = null;
            _logicToIndex.Clear();
            ClearControlledPresentation();
            _lastControlledUnitId = SimEntityId.None;
            LastControlledUnitIndex = SimConst.InvalidIndex;
        }
    }
}
