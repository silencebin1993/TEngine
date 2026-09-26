using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-050；FG17 第 4 节“镜头移动过快、区块还没生成好时，显示‘生成中’的占位地貌，绝不阻塞主线程”）：
    /// 建造模式的地形叠加层，按区块分块、跟随镜头（取代 FG0-ARCH-04 打开时画一次的 73×73 整张图，DEBT-FG0ARCH04-10 的“不随镜头移动”）。
    ///
    /// - 镜头周围 world.view_radius_chunks 半径内每个区块一块贴图（最多 (2R+1)² 块，远低于 FG17 第 7 节“同时加载表现的区块 ≤ 400”）。
    /// - 区块已生成：贴图由 Burst 工作线程按格网数据画（<see cref="JobPaintTile"/>：表颜色 + 图案、污染斜线、核心通道黄框、格线、迷雾变暗），
    ///   主线程只上传；区块内容 / 迷雾 / 核心位置变化时重画这一块。
    /// - 区块还没生成：显示“生成中”占位贴图（灰底 + 斜条纹，颜色之外有图案），并计入 <see cref="PendingCount"/>（建造栏显示“正在生成地形”）。
    ///   这里**从不**同步生成区块——生成由 <see cref="WorldChunkStreamer"/> 在工作线程完成。
    /// 每帧开销 O(贴图块数)，与建筑数、世界大小无关；贴图、材质、占位图成对创建 / 销毁。
    /// </summary>
    public sealed class WorldTerrainOverlay : IDisposable
    {
        public const int PixelsPerCell = 6;
        private const int MaxPaintSchedulesPerFrame = 8;

        private sealed class Tile
        {
            public int ChunkX;
            public int ChunkY;
            public GameObject Go;
            public MeshRenderer Renderer;
            public Texture2D Texture;
            public int Stamp = int.MinValue;
            public bool Placeholder = true;
            public WorldPaintJob Job;
            /// <summary>当前经 MaterialPropertyBlock 设给渲染器的贴图。</summary>
            public Texture Shown;
        }

        private readonly Transform _parent;
        private readonly Material _material;
        private readonly Texture2D _placeholder;
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
        private readonly Stack<Tile> _pool = new Stack<Tile>();
        private readonly List<long> _remove = new List<long>();
        /// <summary>移出窗口时还没画完的贴图任务：挂在这里，完成后在后续帧释放原生内存——主线程不为回收而等待工作线程。</summary>
        private readonly List<WorldPaintJob> _retiring = new List<WorldPaintJob>();

        /// <summary>等待释放的贴图任务数（自检：窗口快速移动后最终归零）。</summary>
        public int RetiringJobCount => _retiring.Count;
        private readonly Color32[] _palette = new Color32[256];
        private readonly byte[] _patterns = new byte[256];
        private int _paletteRevision = -1;
        private int _windowCx = int.MinValue;
        private int _windowCy = int.MinValue;
        private int _windowRadius = -1;
        private int _chunkSize;
        private bool _disposed;

        /// <summary>镜头周围还没生成好（显示占位）的区块数。</summary>
        public int PendingCount { get; private set; }
        public int TileCount => _tiles.Count;
        /// <summary>正在显示占位图的贴图块数。</summary>
        public int PlaceholderCount
        {
            get
            {
                int n = 0;
                foreach (Tile t in _tiles.Values)
                {
                    if (t.Placeholder)
                    {
                        n++;
                    }
                }
                return n;
            }
        }
        /// <summary>画面上任何变化（窗口移动、贴图上传、占位切换）时 +1。</summary>
        public int Revision { get; private set; }
        public int WindowChunkX => _windowCx;
        public int WindowChunkY => _windowCy;
        public int WindowRadius => _windowRadius;
        public Texture2D PlaceholderTexture => _placeholder;

        private readonly float _height;

        /// <param name="alpha">贴图不透明度（建造模式 0.55；FG0-ARCH-01 普通视角的地貌表现层更淡）。</param>
        /// <param name="height">贴图离地高度（普通视角放在建筑与机器下面）。</param>
        public WorldTerrainOverlay(Transform parent, float alpha = 0.55f, float height = 0.03f)
        {
            _parent = parent;
            _height = height;
            _material = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 1f, 1f, alpha) };
            _placeholder = BuildPlaceholder();
        }

        private static Texture2D BuildPlaceholder()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, name = "WorldGeneratingPlaceholder" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    bool stripe = (x + y) % 8 < 2;
                    px[y * n + x] = stripe ? new Color32(150, 150, 160, 255) : new Color32(62, 62, 70, 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>每帧（主线程）：窗口跟随 <paramref name="focus"/>；已生成的区块排队画贴图，没生成的显示占位。
        /// <paramref name="completeNow"/>=true（只给自检 / 需要立刻得到画面的场合）：同步生成窗口内缺的区块并等贴图画完。</summary>
        public void Update(CampaignState state, GridCell focus, bool completeNow = false)
        {
            if (_disposed || state == null)
            {
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(state);
            _chunkSize = map.ChunkSize;
            ReleaseRetired(completeNow);
            RefreshPalette();
            int radius = Math.Max(0, GridContent.TuningInt("world.view_radius_chunks"));
            ChunkAddress a = GridMath.Address(focus, _chunkSize);
            if (a.ChunkX != _windowCx || a.ChunkY != _windowCy || radius != _windowRadius)
            {
                MoveWindow(a.ChunkX, a.ChunkY, radius, map);
            }

            int reserveHash = HomeGridService.TryGetCoreBounds(state, out GridCell coreMin, out GridCell coreMax)
                ? unchecked(coreMin.GetHashCode() * 31 + coreMax.GetHashCode())
                : 0;
            int ring = GridContent.TuningInt("grid.core_reserve_ring");
            int blockLevel = GridContent.TuningInt("grid.pollution_block_level");
            int pending = 0;
            int scheduled = 0;
            foreach (Tile t in _tiles.Values)
            {
                if (completeNow && !map.IsChunkLoaded(t.ChunkX, t.ChunkY) && map.TerrainSource.ChunkExists(t.ChunkX, t.ChunkY, _chunkSize))
                {
                    map.ChunkAt(new GridCell(t.ChunkX * _chunkSize, t.ChunkY * _chunkSize), out _);
                }
                HomeGridMap.Chunk chunk = map.TryGetLoaded(t.ChunkX, t.ChunkY);
                if (chunk == null)
                {
                    pending++;
                    ShowPlaceholder(t);
                    continue;
                }
                int stamp = unchecked(((chunk.ContentRevision * 397) ^ map.ExploredRevision) * 397 ^ reserveHash ^ (_paletteRevision << 20));
                if (t.Job != null)
                {
                    if (completeNow)
                    {
                        t.Job.Complete();
                    }
                    if (t.Job.IsCompleted)
                    {
                        FinishPaint(t);
                    }
                }
                if (t.Job == null && t.Stamp != stamp && (completeNow || scheduled < MaxPaintSchedulesPerFrame))
                {
                    var q = new TilePaintParams
                    {
                        ChunkSize = _chunkSize,
                        PixelsPerCell = PixelsPerCell,
                        BaseX = t.ChunkX * _chunkSize,
                        BaseY = t.ChunkY * _chunkSize,
                        BlockLevel = blockLevel,
                        HasReserve = reserveHash != 0,
                        ReserveMinX = coreMin.X - ring,
                        ReserveMinY = coreMin.Y - ring,
                        ReserveMaxX = coreMax.X + ring,
                        ReserveMaxY = coreMax.Y + ring,
                        CoreMinX = coreMin.X,
                        CoreMinY = coreMin.Y,
                        CoreMaxX = coreMax.X,
                        CoreMaxY = coreMax.Y,
                    };
                    t.Job = WorldGenKernel.SchedulePaint(in q, t.ChunkX, t.ChunkY, stamp, chunk.Terrain, chunk.Pollution, chunk.Explored, _palette, _patterns);
                    scheduled++;
                    if (completeNow)
                    {
                        t.Job.Complete();
                        FinishPaint(t);
                    }
                }
            }
            if (scheduled > 0 && !completeNow)
            {
                WorldGenKernel.Kick();
            }
            if (pending != PendingCount)
            {
                PendingCount = pending;
                Revision++;
            }
        }

        private void FinishPaint(Tile t)
        {
            EnsureTexture(t);
            t.Job.Upload(t.Texture);
            t.Stamp = t.Job.Stamp;
            t.Job.Release();
            t.Job = null;
            t.Placeholder = false;
            SetTexture(t, t.Texture);
            Revision++;
        }

        private void ShowPlaceholder(Tile t)
        {
            if (!t.Placeholder || t.Stamp != int.MinValue)
            {
                t.Placeholder = true;
                t.Stamp = int.MinValue;
                SetTexture(t, _placeholder);
                Revision++;
            }
        }

        private void MoveWindow(int cx, int cy, int radius, HomeGridMap map)
        {
            _windowCx = cx;
            _windowCy = cy;
            _windowRadius = radius;
            _remove.Clear();
            foreach (KeyValuePair<long, Tile> kv in _tiles)
            {
                Tile t = kv.Value;
                if (Math.Max(Math.Abs(t.ChunkX - cx), Math.Abs(t.ChunkY - cy)) > radius)
                {
                    _remove.Add(kv.Key);
                }
            }
            foreach (long k in _remove)
            {
                Recycle(_tiles[k]);
                _tiles.Remove(k);
            }
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    long k = HomeGridMap.Key(x, y);
                    if (_tiles.ContainsKey(k) || !map.TerrainSource.ChunkExists(x, y, map.ChunkSize))
                    {
                        continue;
                    }
                    _tiles[k] = Rent(x, y);
                }
            }
            Revision++;
        }

        private Tile Rent(int cx, int cy)
        {
            Tile t = _pool.Count > 0 ? _pool.Pop() : new Tile();
            t.ChunkX = cx;
            t.ChunkY = cy;
            t.Stamp = int.MinValue;
            t.Placeholder = true;
            if (t.Go == null)
            {
                t.Go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Collider c = t.Go.GetComponent<Collider>();
                if (c != null)
                {
                    SafeDestroy(c); // 不挡选中射线。
                }
                t.Go.transform.SetParent(_parent, false);
                t.Go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                t.Renderer = t.Go.GetComponent<MeshRenderer>();
                t.Renderer.sharedMaterial = _material;
            }
            t.Go.name = $"TerrainChunk_{cx}_{cy}";
            t.Go.SetActive(true);
            float half = (_chunkSize - 1) * 0.5f;
            t.Go.transform.position = new Vector3(cx * _chunkSize + half, _height, cy * _chunkSize + half);
            t.Go.transform.localScale = new Vector3(_chunkSize, _chunkSize, 1f);
            SetTexture(t, _placeholder);
            return t;
        }

        private void Recycle(Tile t)
        {
            if (t.Job != null)
            {
                if (t.Job.IsCompleted)
                {
                    t.Job.Release();
                }
                else
                {
                    _retiring.Add(t.Job); // 还在工作线程上画：不 Complete，完成后由 ReleaseRetired 释放。
                }
                t.Job = null;
            }
            if (t.Go != null)
            {
                t.Go.SetActive(false);
            }
            _pool.Push(t);
        }

        private void EnsureTexture(Tile t)
        {
            int size = _chunkSize * PixelsPerCell;
            if (t.Texture != null && t.Texture.width == size)
            {
                return;
            }
            if (t.Texture != null)
            {
                SafeDestroy(t.Texture);
            }
            t.Texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "TerrainChunkTile" };
        }

        private void SetTexture(Tile t, Texture tex)
        {
            if (t.Renderer == null)
            {
                return;
            }
            _mpb.Clear();
            _mpb.SetTexture("_MainTex", tex);
            t.Renderer.SetPropertyBlock(_mpb);
            t.Shown = tex;
        }

        private void RefreshPalette()
        {
            if (_paletteRevision == GridContent.Revision)
            {
                return;
            }
            for (int i = 0; i < 256; i++)
            {
                _palette[i] = new Color32(90, 90, 90, 255);
                _patterns[i] = 0;
            }
            foreach (GridTerrain t in GridContent.Terrains)
            {
                if (t.Code < 0 || t.Code > 255)
                {
                    continue;
                }
                if (ColorUtility.TryParseHtmlString(t.Color, out Color c))
                {
                    _palette[t.Code] = c;
                }
                _patterns[t.Code] = PatternCode(t.Pattern);
            }
            _paletteRevision = GridContent.Revision;
            foreach (Tile tile in _tiles.Values)
            {
                tile.Stamp = int.MinValue + 1; // 调色板变了：全部重画。
            }
        }

        private static byte PatternCode(string pattern)
        {
            switch (pattern)
            {
                case "hatch": return 1;
                case "dots": return 2;
                case "waves": return 3;
                case "cross": return 4;
                case "grid": return 5;
                default: return 0;
            }
        }

        /// <summary>叠加层上某格某像素的颜色（自检读画面用）；这一格的区块还是占位时返回 false。</summary>
        public bool TryGetPixel(GridCell cell, int px, int py, out Color32 color)
        {
            color = default;
            if (_chunkSize <= 0)
            {
                return false;
            }
            ChunkAddress a = GridMath.Address(cell, _chunkSize);
            if (!_tiles.TryGetValue(HomeGridMap.Key(a.ChunkX, a.ChunkY), out Tile t) || t.Placeholder || t.Texture == null)
            {
                return false;
            }
            color = t.Texture.GetPixel(a.LocalX * PixelsPerCell + px, a.LocalY * PixelsPerCell + py);
            return true;
        }

        /// <summary>某区块的贴图块是否在显示占位图（没有这一块返回 false）。</summary>
        public bool IsPlaceholder(int cx, int cy) => _tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile t) && t.Placeholder;

        public bool HasTile(int cx, int cy) => _tiles.ContainsKey(HomeGridMap.Key(cx, cy));

        /// <summary>某区块贴图块当前用的贴图（占位时是占位图；自检核对画面真的换了）。</summary>
        public Texture ShownTexture(int cx, int cy)
        {
            return _tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile t) ? t.Shown : null;
        }

        /// <summary>释放已经画完的退役贴图任务；<paramref name="all"/>=true 时（关停 / 自检）等全部完成后释放。</summary>
        private void ReleaseRetired(bool all)
        {
            for (int i = _retiring.Count - 1; i >= 0; i--)
            {
                WorldPaintJob job = _retiring[i];
                if (all || job.IsCompleted)
                {
                    job.Release();
                    _retiring.RemoveAt(i);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReleaseRetired(all: true);
            foreach (Tile t in _tiles.Values)
            {
                DestroyTile(t);
            }
            _tiles.Clear();
            while (_pool.Count > 0)
            {
                DestroyTile(_pool.Pop());
            }
            if (_material != null)
            {
                SafeDestroy(_material);
            }
            if (_placeholder != null)
            {
                SafeDestroy(_placeholder);
            }
        }

        /// <summary>运行时用 Destroy；编辑模式（自检）用 DestroyImmediate，避免“edit mode 不能 Destroy”的报错。</summary>
        private static void SafeDestroy(Object o)
        {
            if (o == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(o);
            }
            else
            {
                Object.DestroyImmediate(o);
            }
        }

        private static void DestroyTile(Tile t)
        {
            t.Job?.Release();
            t.Job = null;
            if (t.Texture != null)
            {
                SafeDestroy(t.Texture);
                t.Texture = null;
            }
            if (t.Go != null)
            {
                SafeDestroy(t.Go);
                t.Go = null;
            }
        }
    }
}
