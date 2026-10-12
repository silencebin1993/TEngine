using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameLogic.View
{
    /// <summary>
    /// FG7-ENV-03（FGR-ENV-030“建造视图下顶棚半透明，玩家可以看到并操作下面的东西；可以用快捷键隐藏顶棚”）：顶棚画面——占位表现（B22，DEBT-FG7ENV03-01）。
    /// - 每块顶棚一张 4×4 的平板（离地 <see cref="Height"/>），全部合成一个网格、一次绘制；顶棚建成 / 拆除 / 规划变化时才重建（O(块数)），平时每帧 O(1)。
    /// - 普通视角近乎不透明（顶棚盖住下面）；建造模式里半透明（<see cref="BuildAlpha"/>），下面的东西看得见；施工中的顶棚是更淡的虚影色。
    /// - 隐藏（Ctrl+H，存档里的开关）时不画。没有碰撞体：鼠标点地面的格子照常穿过顶棚（建造、选中、右键命令都不受影响）。
    /// - 建造模式拖顶棚时另画一层预览格：新铺 = 绿、已有（跳过）= 灰、不能铺 = 红（颜色之外不能铺的块画得更矮更小，B15）。
    /// - 建造参考线只在建造模式出现（由建造模式自己画）；顶棚本身不画地格线（用户方向：非建造状态没有地格表现）。
    /// </summary>
    public static class RoofView
    {
        public const float Height = 3.4f;
        public const float NormalAlpha = 0.92f;
        public const float BuildAlpha = 0.32f;

        private static GameObject _go;
        private static Mesh _mesh;
        private static MeshRenderer _renderer;
        private static GameObject _previewGo;
        private static Mesh _previewMesh;
        private static MeshRenderer _previewRenderer;
        private static int _builtRevision = -1;
        private static CampaignState _builtFor;
        private static object _previewRef;
        private static int _previewCount = -1;
        private static readonly List<RoofPlanBlock> Blocks = new List<RoofPlanBlock>(64);
        private static readonly List<Vector3> Verts = new List<Vector3>(256);
        private static readonly List<Color> Colors = new List<Color>(256);
        private static readonly List<int> Tris = new List<int>(384);

        /// <summary>顶棚此刻画在屏幕上（家园被观察、有顶棚、没隐藏）。自检读。</summary>
        public static bool Visible { get; private set; }
        /// <summary>当前透明度（建造模式 = <see cref="BuildAlpha"/>，否则 <see cref="NormalAlpha"/>）。自检读。</summary>
        public static float Alpha { get; private set; } = NormalAlpha;
        /// <summary>网格里的顶棚块数（已建成 + 施工中）。</summary>
        public static int Quads { get; private set; }
        public static int RebuildCount { get; private set; }
        public static int PreviewQuads { get; private set; }

        public static void Tick(bool inWorld)
        {
            CampaignState s = CampaignSession.Current;
            HomeValleyController home = WorldSimulation.Home;
            bool observed = inWorld && s != null && home != null && home.IsLoaded && WorldView.IsObserved(HomeValleyLayout.RegionId);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            bool build = mode != null && mode.IsOpen;
            Refresh(s, observed, build);
            RefreshPreview(observed && build ? mode.RoofPreview : null);
        }

        /// <summary>按存档里的顶棚与当前视角刷新（自检直接调）。</summary>
        public static void Refresh(CampaignState s, bool observed, bool buildMode)
        {
            bool hidden = RoofService.Hidden(s);
            if (!observed || s == null)
            {
                SetVisible(false);
                return;
            }
            if (!ReferenceEquals(s, _builtFor) || _builtRevision != RoofService.Revision)
            {
                Rebuild(s);
            }
            Alpha = buildMode ? BuildAlpha : NormalAlpha;
            if (_renderer != null)
            {
                _renderer.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(1f, 1f, 1f, Alpha));
            }
            SetVisible(!hidden && Quads > 0);
        }

        private static void SetVisible(bool on)
        {
            Visible = on;
            if (_go != null && _go.activeSelf != on)
            {
                _go.SetActive(on);
            }
        }

        private static void Ensure()
        {
            if (_go != null)
            {
                return;
            }
            _go = new GameObject("Roofs");
            if (Application.isPlaying)
            {
                Object.DontDestroyOnLoad(_go);
            }
            _mesh = new Mesh { name = "RoofMesh", indexFormat = IndexFormat.UInt32 };
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        private static void Rebuild(CampaignState s)
        {
            Ensure();
            _builtFor = s;
            _builtRevision = RoofService.Revision;
            RebuildCount++;
            RoofService.CollectBlocks(s, Blocks);
            Verts.Clear();
            Colors.Clear();
            Tris.Clear();
            int n = RoofService.BlockCells;
            foreach (RoofPlanBlock b in Blocks)
            {
                Color c = b.State == 0 ? new Color(0.42f, 0.47f, 0.52f, 1f) : new Color(0.62f, 0.78f, 0.86f, 0.55f);
                AddQuad(b.Bx * n - 0.5f + 0.04f, b.By * n - 0.5f + 0.04f, n - 0.08f, Height, c);
            }
            _mesh.Clear();
            _mesh.SetVertices(Verts);
            _mesh.SetColors(Colors);
            _mesh.SetTriangles(Tris, 0);
            _mesh.RecalculateBounds();
            Quads = Blocks.Count;
        }

        private static void AddQuad(float x, float z, float size, float y, Color c)
        {
            int i = Verts.Count;
            Verts.Add(new Vector3(x, y, z));
            Verts.Add(new Vector3(x, y, z + size));
            Verts.Add(new Vector3(x + size, y, z + size));
            Verts.Add(new Vector3(x + size, y, z));
            for (int k = 0; k < 4; k++)
            {
                Colors.Add(c);
            }
            Tris.Add(i);
            Tris.Add(i + 1);
            Tris.Add(i + 2);
            Tris.Add(i);
            Tris.Add(i + 2);
            Tris.Add(i + 3);
        }

        private static void RefreshPreview(RoofPlan plan)
        {
            if (plan == null || plan.Blocks.Count == 0)
            {
                PreviewQuads = 0;
                if (_previewGo != null && _previewGo.activeSelf)
                {
                    _previewGo.SetActive(false);
                }
                _previewRef = null;
                return;
            }
            if (_previewGo == null)
            {
                _previewGo = new GameObject("RoofPreview");
                if (Application.isPlaying)
                {
                    Object.DontDestroyOnLoad(_previewGo);
                }
                _previewMesh = new Mesh { name = "RoofPreviewMesh", indexFormat = IndexFormat.UInt32 };
                _previewGo.AddComponent<MeshFilter>().sharedMesh = _previewMesh;
                _previewRenderer = _previewGo.AddComponent<MeshRenderer>();
                _previewRenderer.shadowCastingMode = ShadowCastingMode.Off;
                _previewRenderer.receiveShadows = false;
                _previewRenderer.sharedMaterial = ViewMaterials.Get("Sprites/Default", new Color(1f, 1f, 1f, 0.45f));
            }
            int key = plan.Blocks.Count * 31 + plan.Min.X * 7 + plan.Min.Y * 13 + plan.Max.X * 17 + plan.Max.Y * 19 + plan.New;
            if (!ReferenceEquals(plan, _previewRef) || key != _previewCount)
            {
                Verts.Clear();
                Colors.Clear();
                Tris.Clear();
                int n = RoofService.BlockCells;
                foreach (RoofPlanBlock b in plan.Blocks)
                {
                    Color c = b.State == 0 ? new Color(0.3f, 0.95f, 0.4f, 1f) : b.State == 1 ? new Color(0.6f, 0.6f, 0.65f, 1f) : new Color(0.95f, 0.25f, 0.2f, 1f);
                    float inset = b.State == 2 ? 0.8f : 0.1f;
                    AddQuad(b.Bx * n - 0.5f + inset, b.By * n - 0.5f + inset, n - inset * 2f, b.State == 2 ? Height - 0.6f : Height + 0.05f, c);
                }
                _previewMesh.Clear();
                _previewMesh.SetVertices(Verts);
                _previewMesh.SetColors(Colors);
                _previewMesh.SetTriangles(Tris, 0);
                _previewMesh.RecalculateBounds();
                _previewRef = plan;
                _previewCount = key;
            }
            PreviewQuads = plan.Blocks.Count;
            if (!_previewGo.activeSelf)
            {
                _previewGo.SetActive(true);
            }
        }

        public static void ResetForTests()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
            if (_previewGo != null)
            {
                Object.DestroyImmediate(_previewGo);
            }
            _go = null;
            _previewGo = null;
            _mesh = null;
            _previewMesh = null;
            _builtFor = null;
            _builtRevision = -1;
            _previewRef = null;
            Visible = false;
            Quads = PreviewQuads = RebuildCount = 0;
        }
    }
}
