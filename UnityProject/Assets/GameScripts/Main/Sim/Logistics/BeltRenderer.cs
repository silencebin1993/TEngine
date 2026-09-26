using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG0-ARCH-02 传送带渲染原型（FGR-ARC-004 渲染；FGR-LOG-090）：
    /// - 近景：每格一个实例（带面 + 滚动箭头），每件物品一个实例（GPU 实例化，程序化实例 + StructuredBuffer，一次绘制调用画完全部物品）。
    /// - 远景（镜头正交半高 ≥ 阈值，带回差）：不再逐物品绘制，每格一个实例，用“流动贴图”（沿带方向按真实带速滚动的条纹，
    ///   亮度 = 这一格的占用率）表达物流；堵塞格在两种模式下都用斜纹 + 红色标出（FGR-LOG-025“图案加颜色”，不只靠颜色）。
    /// - 渲染缓冲只在内核状态变化后重填（Burst，<see cref="BeltKernel.PrepareRender"/>）并上传；远景只重填 / 上传格实例；每帧至多两次绘制调用，
    ///   热更层每帧只调一次 <see cref="Draw"/>（开销与格数 / 物品数无关）。
    /// - 物品位置用上一步位移做插值（alpha = 距上一内核步的游戏时间比例），20 Hz 内核在 60 帧画面上也连续。
    /// 无图形设备（-nographics / batchmode）时照常准备实例数据（自检据此核对），只跳过 GPU 调用。
    /// 原生 / GPU 资源成对释放：<see cref="Dispose"/>。
    /// </summary>
    public sealed class BeltRenderer : IDisposable
    {
        public const string ShaderName = "BinGames/BeltInstanced";

        [Serializable]
        public struct Settings
        {
            /// <summary>格网一格的世界边长（格网格中心在整数坐标）。</summary>
            public float CellSize;
            /// <summary>正交半高 ≥ 这个值切到远景流动贴图。</summary>
            public float FlowOrthoEnter;
            /// <summary>远景下正交半高 ≤ 这个值切回近景（回差，避免在阈值附近来回闪）。</summary>
            public float FlowOrthoExit;
            public float Height;
            /// <summary>物品边长（格）。</summary>
            public float ItemSize;
        }

        private static readonly int InstancesId = Shader.PropertyToID("_Instances");
        private static readonly int KindId = Shader.PropertyToID("_Kind");
        private static readonly int FarId = Shader.PropertyToID("_Far");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int ItemSizeId = Shader.PropertyToID("_ItemSize");
        private static readonly int HeightId = Shader.PropertyToID("_Height");

        private Material _material;
        private Mesh _quad;
        private GraphicsBuffer _cellBuf;
        private GraphicsBuffer _itemBuf;
        private MaterialPropertyBlock _cellProps;
        private MaterialPropertyBlock _itemProps;
        private int _uploadedRevision = -1;
        private int _uploadedItemRevision = -1;
        private bool _disposed;

        public bool FarMode { get; private set; }
        public int ModeSwitches { get; private set; }
        /// <summary>上一帧画的实例数（无图形设备时 = 本该画的数量）。远景下物品实例为 0。</summary>
        public int LastCellInstances { get; private set; }
        public int LastItemInstances { get; private set; }
        /// <summary>上一帧真正提交的绘制调用数（0～2；无图形设备 / 找不到着色器时为 0）。</summary>
        public int LastDrawCalls { get; private set; }
        /// <summary>格实例缓冲上传次数。</summary>
        public int Uploads { get; private set; }
        /// <summary>物品实例缓冲上传次数（远景下不增加）。</summary>
        public int ItemUploads { get; private set; }
        public bool GpuAvailable { get; private set; }
        /// <summary>GPU 不可用的原因（null = 可用）。</summary>
        public string GpuUnavailableReason { get; private set; }

        public BeltRenderer()
        {
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
            _material = new Material(shader) { name = "BeltInstanced (runtime)", hideFlags = HideFlags.HideAndDontSave };
            _quad = BuildQuad();
            _cellProps = new MaterialPropertyBlock();
            _itemProps = new MaterialPropertyBlock();
            GpuAvailable = true;
        }

        /// <summary>
        /// 每帧（只在观察这个表面时调用）：按镜头缩放选近景 / 远景，内核状态变了就重填并上传缓冲，然后提交至多两次绘制。
        /// <paramref name="alpha"/>：距上一内核步经过的游戏时间 / 内核步长（0～1），物品按上一步位移插值。
        /// </summary>
        public void Draw(BeltKernel kernel, Camera camera, float orthoSize, float alpha, in Settings settings)
        {
            LastDrawCalls = 0;
            LastCellInstances = 0;
            LastItemInstances = 0;
            if (_disposed || kernel == null || kernel.IsDisposed)
            {
                return;
            }
            bool far = FarMode ? orthoSize > settings.FlowOrthoExit : orthoSize >= settings.FlowOrthoEnter;
            if (far != FarMode)
            {
                FarMode = far;
                ModeSwitches++;
            }
            // 远景不画物品：只重填 / 上传格实例；物品实例留到切回近景时再补一次（大网络每步省下约 1 MB 的物品缓冲上传）。
            kernel.PrepareRender(settings.CellSize, out NativeArray<BeltInstance> cells, out int cellCount,
                out NativeArray<BeltInstance> items, out int itemCount, includeItems: !far);
            LastCellInstances = cellCount;
            LastItemInstances = far ? 0 : itemCount;
            if (!GpuAvailable || cellCount == 0)
            {
                return;
            }
            if (_uploadedRevision != kernel.Revision)
            {
                Upload(ref _cellBuf, cells, cellCount);
                _uploadedRevision = kernel.Revision;
                Uploads++;
            }
            if (!far && _uploadedItemRevision != kernel.Revision)
            {
                Upload(ref _itemBuf, items, Math.Max(1, itemCount));
                _uploadedItemRevision = kernel.Revision;
                ItemUploads++;
            }
            var bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e3f, 1e7f));
            float a = Mathf.Clamp01(alpha);

            _cellProps.SetBuffer(InstancesId, _cellBuf);
            _cellProps.SetFloat(KindId, 0f);
            _cellProps.SetFloat(FarId, far ? 1f : 0f);
            _cellProps.SetFloat(AlphaId, a);
            _cellProps.SetFloat(CellSizeId, settings.CellSize);
            _cellProps.SetFloat(ItemSizeId, settings.ItemSize);
            _cellProps.SetFloat(HeightId, settings.Height);
            var rpCells = new RenderParams(_material)
            {
                worldBounds = bounds,
                matProps = _cellProps,
                camera = camera,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMeshPrimitives(rpCells, _quad, 0, cellCount);
            LastDrawCalls++;

            if (!far && itemCount > 0)
            {
                _itemProps.SetBuffer(InstancesId, _itemBuf);
                _itemProps.SetFloat(KindId, 1f);
                _itemProps.SetFloat(FarId, 0f);
                _itemProps.SetFloat(AlphaId, a);
                _itemProps.SetFloat(CellSizeId, settings.CellSize);
                _itemProps.SetFloat(ItemSizeId, settings.ItemSize);
                _itemProps.SetFloat(HeightId, settings.Height);
                var rpItems = new RenderParams(_material)
                {
                    worldBounds = bounds,
                    matProps = _itemProps,
                    camera = camera,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                };
                Graphics.RenderMeshPrimitives(rpItems, _quad, 0, itemCount);
                LastDrawCalls++;
            }
        }

        private static void Upload(ref GraphicsBuffer buf, NativeArray<BeltInstance> data, int count)
        {
            int stride = 32;
            if (buf == null || buf.count < count)
            {
                buf?.Release();
                buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(16, count * 3 / 2), stride);
            }
            int n = Math.Min(count, data.Length);
            if (n > 0)
            {
                buf.SetData(data, 0, 0, n);
            }
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "BeltQuad", hideFlags = HideFlags.HideAndDontSave };
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
            _cellBuf?.Release();
            _itemBuf?.Release();
            _cellBuf = null;
            _itemBuf = null;
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
