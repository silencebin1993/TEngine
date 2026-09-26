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

        private Material _material;
        private Mesh _quad;
        private GraphicsBuffer _unitBuf;
        private GraphicsBuffer _projBuf;
        private MaterialPropertyBlock _unitProps;
        private MaterialPropertyBlock _projProps;
        private NativeList<CombatInstance> _units;
        private NativeList<CombatInstance> _projectiles;
        private int _preparedRevision = int.MinValue;
        private bool _disposed;

        public int LastUnitInstances { get; private set; }
        public int LastProjectileInstances { get; private set; }
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
            GpuAvailable = true;
        }

        /// <summary>每帧（只在观察这个地点时）：内核状态变了就重填缓冲并上传，然后提交至多两次绘制。</summary>
        public void Draw(CombatKernel kernel, Camera camera, float alpha, double2 origin, float height)
        {
            LastDrawCalls = 0;
            if (_disposed || kernel == null || kernel.IsDisposed)
            {
                LastUnitInstances = 0;
                LastProjectileInstances = 0;
                return;
            }
            bool changed = _preparedRevision != kernel.Revision;
            if (changed)
            {
                kernel.PrepareRender(_units, _projectiles, origin);
                _preparedRevision = kernel.Revision;
            }
            LastUnitInstances = _units.Length;
            LastProjectileInstances = _projectiles.Length;
            if (!GpuAvailable)
            {
                return;
            }
            if (changed)
            {
                Upload(ref _unitBuf, _units);
                Upload(ref _projBuf, _projectiles);
                Uploads++;
            }
            var bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e3f, 1e7f));
            float a = Mathf.Clamp01(alpha);
            if (_units.Length > 0)
            {
                Submit(_unitBuf, _unitProps, 0f, a, height, camera, bounds, _units.Length);
            }
            if (_projectiles.Length > 0)
            {
                Submit(_projBuf, _projProps, 1f, a, height + 0.4f, camera, bounds, _projectiles.Length);
            }
        }

        private void Submit(GraphicsBuffer buf, MaterialPropertyBlock props, float kind, float alpha, float height, Camera camera, Bounds bounds, int count)
        {
            props.SetBuffer(InstancesId, buf);
            props.SetFloat(KindId, kind);
            props.SetFloat(AlphaId, alpha);
            props.SetFloat(HeightId, height);
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
            _unitBuf = null;
            _projBuf = null;
            if (_units.IsCreated)
            {
                _units.Dispose();
            }
            if (_projectiles.IsCreated)
            {
                _projectiles.Dispose();
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
