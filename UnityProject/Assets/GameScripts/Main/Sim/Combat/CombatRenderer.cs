using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG0-ARCH-03 战斗渲染原型：没有 GameObject 表现的单位（突袭者、炮塔原型）与全部弹体用程序化实例绘制
    /// （StructuredBuffer + SV_InstanceID，每帧至多两次绘制调用）。缓冲只在内核状态变化后由 Burst 重填并上传；
    /// 位置按统一时钟的步内比例在上一步与本步之间插值。热更层每帧只调一次 <see cref="Draw"/>，开销与单位 / 弹体数无关。
    /// 无图形设备（-nographics）时照常准备实例数据（自检据此核对），只跳过 GPU 调用。原生 / GPU 资源成对释放：<see cref="Dispose"/>。
    /// 美术是占位（B22）：单位 = 按阵营着色的圆片 + 血量环，弹体 = 发光短线。
    /// </summary>
    public sealed class CombatRenderer : IDisposable
    {
        public const string ShaderName = "BinGames/CombatInstanced";

        private static readonly int InstancesId = Shader.PropertyToID("_Instances");
        private static readonly int KindId = Shader.PropertyToID("_Kind");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private static readonly int HeightId = Shader.PropertyToID("_Height");
        private static readonly int GameTimeId = Shader.PropertyToID("_GameTime");
        private static readonly int HoloId = Shader.PropertyToID("_Holo");

        /// <summary>FG5-RND-03（FGR-RND-031“投影在视觉上有明显的全息效果，不会被误认为真实机器”）：全息画法——单位与弹体按全息配色、
        /// 带随游戏时间滚动的扫描线镂空（形状为主、颜色为辅，B15）。靶场的地点打开它；美术占位（B22，正式外观随美术批次替换）。</summary>
        public bool Hologram { get; set; }

        /// <summary>区域 / 无人机着色器动画的时钟 = 内核（游戏）时间，按一小时取模防浮点精度损失：战略暂停时画面静止、倍速时同步加快（B09）。</summary>
        private float _gameTime;

        /// <summary>着色器动画时钟（秒）：内核时间按一小时取模。暂停不推进内核 → 不变；倍速多推进几步 → 同步加快。</summary>
        public static float AnimationClock(CombatKernel kernel) => kernel == null || kernel.IsDisposed ? 0f : (float)(kernel.Time % 3600.0);

        private Material _material;
        private Mesh _quad;
        private GraphicsBuffer _unitBuf;
        private GraphicsBuffer _projBuf;
        private GraphicsBuffer _fxBuf;
        private MaterialPropertyBlock _fxProps;
        private NativeList<CombatInstance> _effects;
        /// <summary>FG2-FW-03（FGR-FW-031）：头顶状态标签图标（第四次绘制调用，只在有带标签的单位时画）。</summary>
        private NativeList<CombatInstance> _icons;
        private NativeArray<float2> _statusVisuals;
        private bool _hasStatusVisuals;
        private GraphicsBuffer _iconBuf;
        private MaterialPropertyBlock _iconProps;
        private MaterialPropertyBlock _unitProps;
        private MaterialPropertyBlock _projProps;
        private NativeList<CombatInstance> _units;
        private NativeList<CombatInstance> _projectiles;
        private int _preparedRevision = int.MinValue;
        private bool _disposed;

        public int LastUnitInstances { get; private set; }
        public int LastProjectileInstances { get; private set; }
        /// <summary>FG2-FW-02：读法生成的区域与无人机（第三次绘制调用，只在有的时候画；画在单位下面一层）。</summary>
        public int LastEffectInstances { get; private set; }
        public NativeArray<CombatInstance> EffectInstances => _effects.AsArray();
        /// <summary>FG2-FW-03：头顶状态标签图标实例数与缓冲（自检核对：每个带标签的单位都有图标、形状颜色来自表）。</summary>
        public int LastIconInstances { get; private set; }
        public NativeArray<CombatInstance> IconInstances => _icons.AsArray();
        /// <summary>每个单位头顶最多画几个标签图标（其余在悬停读数里列出）。</summary>
        public const int MaxIconsPerUnit = 4;
        /// <summary>图标边长（米）。</summary>
        public const float IconSize = 0.55f;
        public int LastDrawCalls { get; private set; }
        public int Uploads { get; private set; }
        public bool GpuAvailable { get; private set; }
        public string GpuUnavailableReason { get; private set; }
        public NativeArray<CombatInstance> UnitInstances => _units.AsArray();
        public NativeArray<CombatInstance> ProjectileInstances => _projectiles.AsArray();

        public CombatRenderer()
        {
            _units = new NativeList<CombatInstance>(64, Allocator.Persistent);
            _projectiles = new NativeList<CombatInstance>(256, Allocator.Persistent);
            _effects = new NativeList<CombatInstance>(32, Allocator.Persistent);
            _icons = new NativeList<CombatInstance>(32, Allocator.Persistent);
            _statusVisuals = new NativeArray<float2>(32, Allocator.Persistent);
            for (int b = 0; b < 32; b++)
            {
                _statusVisuals[b] = new float2(-1f, 0f);
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                GpuUnavailableReason = "无图形设备（-nographics / batchmode）";
                return;
            }
            if (SystemInfo.graphicsShaderLevel < 45)
            {
                GpuUnavailableReason = $"着色器等级 {SystemInfo.graphicsShaderLevel} < 4.5（不支持 StructuredBuffer 程序化实例）";
                return;
            }
            Shader shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                GpuUnavailableReason = $"找不到或不支持着色器 {ShaderName}";
                return;
            }
            _material = new Material(shader) { name = "CombatInstanced (runtime)", hideFlags = HideFlags.HideAndDontSave };
            _quad = BuildQuad();
            _unitProps = new MaterialPropertyBlock();
            _projProps = new MaterialPropertyBlock();
            _fxProps = new MaterialPropertyBlock();
            _iconProps = new MaterialPropertyBlock();
            GpuAvailable = true;
        }

        /// <summary>FG2-FW-03（FGR-FW-031）：每个状态位的图标（x = 形状序号，&lt;0 = 不画；y = 打包颜色 0xRRGGBB）。热更层按 fg.TbStatusTag 的形状 / 颜色写入一次。</summary>
        public void SetStatusVisuals(float2[] visuals)
        {
            if (_disposed)
            {
                return;
            }
            _hasStatusVisuals = false;
            for (int b = 0; b < 32; b++)
            {
                float2 v = visuals != null && b < visuals.Length ? visuals[b] : new float2(-1f, 0f);
                _statusVisuals[b] = v;
                _hasStatusVisuals |= v.x >= 0f;
            }
            _preparedRevision = int.MinValue;
        }

        public float2 StatusVisualOf(int bit) => bit >= 0 && bit < 32 && !_disposed ? _statusVisuals[bit] : new float2(-1f, 0f);

        /// <summary>每帧（只在观察这个地点时）：内核状态变了就重填缓冲并上传，然后提交至多四次绘制。</summary>
        public void Draw(CombatKernel kernel, Camera camera, float alpha, double2 origin, float height)
        {
            LastDrawCalls = 0;
            if (_disposed || kernel == null || kernel.IsDisposed)
            {
                LastUnitInstances = 0;
                LastProjectileInstances = 0;
                return;
            }
            _gameTime = AnimationClock(kernel);
            bool changed = _preparedRevision != kernel.Revision;
            if (changed)
            {
                kernel.PrepareRender(_units, _projectiles, origin);
                kernel.PrepareEffects(_effects, origin, _statusVisuals);
                if (_hasStatusVisuals)
                {
                    kernel.PrepareStatusIcons(_icons, _statusVisuals, origin, MaxIconsPerUnit, IconSize);
                }
                else
                {
                    _icons.Clear();
                }
                _preparedRevision = kernel.Revision;
            }
            LastUnitInstances = _units.Length;
            LastProjectileInstances = _projectiles.Length;
            LastEffectInstances = _effects.Length;
            LastIconInstances = _icons.Length;
            if (!GpuAvailable)
            {
                return;
            }
            if (changed)
            {
                Upload(ref _unitBuf, _units);
                Upload(ref _projBuf, _projectiles);
                if (_effects.Length > 0)
                {
                    Upload(ref _fxBuf, _effects);
                }
                if (_icons.Length > 0)
                {
                    Upload(ref _iconBuf, _icons);
                }
                Uploads++;
            }
            var bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e3f, 1e7f));
            float a = Mathf.Clamp01(alpha);
            if (_effects.Length > 0)
            {
                Submit(_fxBuf, _fxProps, 0f, a, height - 0.05f, camera, bounds, _effects.Length);
            }
            if (_units.Length > 0)
            {
                Submit(_unitBuf, _unitProps, 0f, a, height, camera, bounds, _units.Length);
            }
            if (_projectiles.Length > 0)
            {
                Submit(_projBuf, _projProps, 1f, a, height + 0.4f, camera, bounds, _projectiles.Length);
            }
            if (_icons.Length > 0)
            {
                Submit(_iconBuf, _iconProps, 2f, a, height + 1.2f, camera, bounds, _icons.Length);
            }
        }

        private void Submit(GraphicsBuffer buf, MaterialPropertyBlock props, float kind, float alpha, float height, Camera camera, Bounds bounds, int count)
        {
            props.SetBuffer(InstancesId, buf);
            props.SetFloat(KindId, kind);
            props.SetFloat(AlphaId, alpha);
            props.SetFloat(HeightId, height);
            props.SetFloat(GameTimeId, _gameTime);
            props.SetFloat(HoloId, Hologram ? 1f : 0f);
            var rp = new RenderParams(_material)
            {
                worldBounds = bounds,
                matProps = props,
                camera = camera,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMeshPrimitives(rp, _quad, 0, count);
            LastDrawCalls++;
        }

        private static void Upload(ref GraphicsBuffer buf, NativeList<CombatInstance> data)
        {
            int count = math.max(1, data.Length);
            if (buf == null || buf.count < count)
            {
                buf?.Release();
                buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, math.max(16, count * 3 / 2), 32);
            }
            if (data.Length > 0)
            {
                buf.SetData(data.AsArray(), 0, 0, data.Length);
            }
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "CombatQuad", hideFlags = HideFlags.HideAndDontSave };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return m;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _unitBuf?.Release();
            _projBuf?.Release();
            _fxBuf?.Release();
            _iconBuf?.Release();
            _iconBuf = null;
            _unitBuf = null;
            _projBuf = null;
            _fxBuf = null;
            if (_units.IsCreated)
            {
                _units.Dispose();
            }
            if (_projectiles.IsCreated)
            {
                _projectiles.Dispose();
            }
            if (_effects.IsCreated)
            {
                _effects.Dispose();
            }
            if (_icons.IsCreated)
            {
                _icons.Dispose();
            }
            if (_statusVisuals.IsCreated)
            {
                _statusVisuals.Dispose();
            }
            if (_material != null)
            {
                UnityEngine.Object.DestroyImmediate(_material);
            }
            if (_quad != null)
            {
                UnityEngine.Object.DestroyImmediate(_quad);
            }
        }
    }
}
