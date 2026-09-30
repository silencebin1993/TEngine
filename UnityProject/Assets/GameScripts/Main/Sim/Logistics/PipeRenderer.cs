using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.Sim.Logistics
{
    /// <summary>
    /// FG3-LOG-05（FGR-LOG-040“管线按所载流体上色，并有图标”）管线渲染：每格一个程序化实例（StructuredBuffer + SV_InstanceID），一次绘制调用画完全部管线件。
    /// - 管线 = 中心块 + 朝相连邻格伸出的管臂（等级越高越粗）；泵 = 圆盘；储罐 = 方罐 + 液位；阀门 = 沿流向的蝶形（关着时画横杠，两侧流体不同时叠红色斜纹）。
    /// - 颜色 = 流体（表 fg.TbFluid 的颜色，热更层经 <see cref="SetPalette"/> 传入）；没有流体 = 灰色。图标 = 按流体的形状（水滴 / 圆点 / 十字 / 三角 / 菱形），
    ///   画在泵、储罐、阀门和每隔几格的管线上，颜色之外还有形状（B15）。
    /// - 实例缓冲只在内核版本变化后重填（<see cref="PipeKernel.PrepareRender"/>，AOT）并上传；热更层每帧只调一次 <see cref="Draw"/>（与格数无关）。
    /// 无图形设备（-nographics / batchmode）时照常准备实例数据（自检据此核对），只跳过 GPU 调用。GPU 资源成对释放：<see cref="Dispose"/>。
    /// </summary>
    public sealed class PipeRenderer : IDisposable
    {
        public const string ShaderName = "BinGames/PipeInstanced";
        public const int PaletteSize = 32;

        [Serializable]
        public struct Settings
        {
            public float CellSize;
            public float Height;
            public int IconEvery;
        }

        private static readonly int InstancesId = Shader.PropertyToID("_Instances");
        private static readonly int PaletteId = Shader.PropertyToID("_Palette");
        private static readonly int GlyphsId = Shader.PropertyToID("_Glyphs");
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int HeightId = Shader.PropertyToID("_Height");

        private readonly Vector4[] _palette = new Vector4[PaletteSize];
        private readonly float[] _glyphs = new float[PaletteSize];
        private Material _material;
        private Mesh _quad;
        private GraphicsBuffer _buf;
        private MaterialPropertyBlock _props;
        private int _uploadedRevision = -1;
        private bool _disposed;

        public bool GpuAvailable { get; private set; }
        public string GpuUnavailableReason { get; private set; }
        public int LastInstances { get; private set; }
        public int LastDrawCalls { get; private set; }
        public int Uploads { get; private set; }
        /// <summary>最近一次准备的实例（自检核对颜色 / 图标编码用，只读）。</summary>
        public PipeInstance[] LastPrepared { get; private set; }

        public PipeRenderer()
        {
            for (int i = 0; i < PaletteSize; i++)
            {
                _palette[i] = new Vector4(0.45f, 0.45f, 0.48f, 1f);
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                GpuUnavailableReason = "无图形设备（-nographics / batchmode）";
                return;
            }
            if (SystemInfo.graphicsShaderLevel < 45)
            {
                GpuUnavailableReason = $"着色器等级 {SystemInfo.graphicsShaderLevel} < 4.5";
                return;
            }
            Shader shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                GpuUnavailableReason = $"找不到或不支持着色器 {ShaderName}";
                return;
            }
            _material = new Material(shader) { name = "PipeInstanced (runtime)", hideFlags = HideFlags.HideAndDontSave };
            _quad = BuildQuad();
            _props = new MaterialPropertyBlock();
            GpuAvailable = true;
        }

        /// <summary>流体编号 → 颜色与图标形状（0 水滴、1 圆点、2 十字、3 三角、4 菱形）。编号 0（没有流体）固定灰色。</summary>
        public void SetPalette(int fluidId, Color color, int glyph)
        {
            if (fluidId <= 0 || fluidId >= PaletteSize)
            {
                return;
            }
            _palette[fluidId] = new Vector4(color.r, color.g, color.b, 1f);
            _glyphs[fluidId] = glyph;
        }

        public Color PaletteColor(int fluidId) =>
            fluidId >= 0 && fluidId < PaletteSize ? new Color(_palette[fluidId].x, _palette[fluidId].y, _palette[fluidId].z) : Color.gray;

        public void Draw(PipeKernel kernel, Camera camera, in Settings settings)
        {
            LastDrawCalls = 0;
            LastInstances = 0;
            if (_disposed || kernel == null)
            {
                return;
            }
            PipeInstance[] data = kernel.PrepareRender(settings.IconEvery, out int count, out _);
            LastPrepared = data;
            LastInstances = count;
            if (!GpuAvailable || count == 0)
            {
                return;
            }
            if (_uploadedRevision != kernel.Revision || _buf == null)
            {
                if (_buf == null || _buf.count < count)
                {
                    _buf?.Release();
                    _buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(64, count * 3 / 2), 32);
                }
                _buf.SetData(data, 0, 0, count);
                _uploadedRevision = kernel.Revision;
                Uploads++;
            }
            _props.SetBuffer(InstancesId, _buf);
            _props.SetVectorArray(PaletteId, _palette);
            _props.SetFloatArray(GlyphsId, _glyphs);
            _props.SetFloat(CellSizeId, settings.CellSize);
            _props.SetFloat(HeightId, settings.Height);
            var rp = new RenderParams(_material)
            {
                worldBounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e3f, 1e7f)),
                matProps = _props,
                camera = camera,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMeshPrimitives(rp, _quad, 0, count);
            LastDrawCalls = 1;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "PipeQuad", hideFlags = HideFlags.HideAndDontSave };
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
            _buf?.Release();
            _buf = null;
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
