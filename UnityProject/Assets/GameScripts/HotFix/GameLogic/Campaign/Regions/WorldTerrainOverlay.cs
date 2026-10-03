using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using BinGames.TerrainVisual;
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
    /// FG3-GEN-01（FG-GAP-021）：普通视角（terrainView）的贴图块不再是平面方片，而是 Burst 工作线程生成的**地貌起伏网格**
    /// （<see cref="JobBuildRelief"/>：悬崖隆起成山脊、水面下陷；可走的地面恒在 0 高度，单位 / 建筑 / 拾取仍按 0 高度平面），
    /// 不透明、受光照（Standard），贴图照旧按格网数据画（每格 1 像素 + 双线性过滤，没有地格线）。建造模式的叠加层仍是半透明平面方片（地格参考线）。
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
            // FG3-GEN-01：地貌起伏网格（只在普通视角）。
            public MeshFilter Filter;
            public Mesh Mesh;
            public int ReliefStamp = int.MinValue;
            public WorldReliefJob ReliefJob;
            public bool ReliefBuilt;
            public float ReliefMin;
            public float ReliefMax;
            public CampaignTerrainChunk Pcg;
            public int PcgContent = int.MinValue, PcgExplored = int.MinValue, PcgRelief = int.MinValue;
        }

        private readonly Transform _parent;
        private Material _material;
        private TerrainProfile _pcgProfile;
        private int _appearanceRevision;
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

        /// <summary>FG3-LOG-01：建造叠加层画不画格线（建造模式每帧按设置写入；变化时各区块按新值重画）。普通视角恒不画。</summary>
        public bool GridLines { get; set; } = true;

        /// <summary>FG3-LOG-08（FGR-LOG-080 污染叠加层）：按污染等级强调着色（每帧由 <see cref="GameLogic.View.OverlayService"/> 写入；变化时各区块按新值重画）。</summary>
        public bool PollutionView { get; set; }

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
        /// <summary>普通视角地貌（非建造）：每格 1 像素 + 双线性过滤，只有平滑的地貌色，没有格线 / 图案。</summary>
        private readonly bool _terrainView;
        private readonly int _ppc;
        /// <summary>FG3-GEN-01：普通视角用起伏网格（不透明、受光照）。</summary>
        private readonly bool _relief;
        private Mesh _flatMesh;
        private byte[] _reliefWindow;
        private int _reliefWindowSize;
        private const int MaxReliefSchedulesPerFrame = 6;

        /// <summary>已经换成起伏网格的贴图块数（自检）。</summary>
        public int ReliefTileCount
        {
            get
            {
                int n = 0;
                foreach (Tile t in _tiles.Values)
                {
                    n += t.ReliefBuilt ? 1 : 0;
                }
                return n;
            }
        }

        /// <summary>某区块起伏网格的高度范围（米）；还没建好返回 false。</summary>
        public bool TryGetReliefRange(int cx, int cy, out float min, out float max)
        {
            min = max = 0f;
            if (!_tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile t) || !t.ReliefBuilt)
            {
                return false;
            }
            min = t.ReliefMin;
            max = t.ReliefMax;
            return true;
        }

        /// <summary>某区块的起伏网格（自检读顶点；还没建好返回 null）。</summary>
        public Mesh ReliefMesh(int cx, int cy) => _tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile t) && t.ReliefBuilt ? t.Mesh : null;

        /// <summary>某区块贴图块的世界位置（起伏网格的原点 = 区块左下角格的左下角）。</summary>
        public bool TryGetTileTransform(int cx, int cy, out Vector3 position)
        {
            position = default;
            if (!_tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile t) || t.Go == null)
            {
                return false;
            }
            position = t.Go.transform.position;
            return true;
        }

        public bool IsRelief => _relief;
        public bool PcgEnabled => _pcgProfile != null;
        public Material GroundMaterial => _material;

        public bool TryGetPcgCounts(int cx, int cy, out int water, out int rocks)
        {
            water = rocks = 0;
            if (!_tiles.TryGetValue(HomeGridMap.Key(cx, cy), out Tile tile) || tile.Pcg == null) return false;
            water = tile.Pcg.WaterCellCount;
            rocks = tile.Pcg.RockCount;
            return true;
        }

        /// <summary>FG3-GEN-01：复用试验场材质与素材；地形、迷雾和区块生命周期仍由正式世界驱动。</summary>
        public void SetPcgProfile(TerrainProfile profile)
        {
            if (_disposed || !_relief || profile == null) return;
            if (!profile.GroundMaterial || !profile.GroundMaterial.HasProperty("_CampaignMode"))
                throw new ArgumentException("PCG 地貌配置缺少正式世界材质。");
            _pcgProfile = profile;
            _appearanceRevision++;
            Material old = _material;
            _material = new Material(profile.GroundMaterial) { name = "CampaignPcgGround" };
            _material.SetFloat("_CampaignMode", 1);
            _material.SetFloat("_UseOrbis", 0);
            foreach (Tile t in _tiles.Values)
            {
                t.Renderer.sharedMaterial = _material;
                t.Pcg?.Dispose();
                t.Pcg = null;
                t.PcgContent = int.MinValue;
            }
            foreach (Tile t in _pool) t.Renderer.sharedMaterial = _material;
            SafeDestroy(old);
            _paletteRevision = -1;
        }

        /// <summary>本叠加层每格几个像素（建造模式 <see cref="PixelsPerCell"/>，普通视角 1）。</summary>
        public int CellPixels => _ppc;

        /// <param name="alpha">贴图不透明度（建造模式 0.55；FG0-ARCH-01 普通视角的地貌表现层更淡）。</param>
        /// <param name="height">贴图离地高度（普通视角放在建筑与机器下面）。</param>
        /// <param name="terrainView">true = 普通视角地貌（无地格参考线）；false = 建造模式叠加层（格线、图案、核心通道框）。</param>
        public WorldTerrainOverlay(Transform parent, float alpha = 0.55f, float height = 0.03f, bool terrainView = false)
        {
            _parent = parent;
            _height = height;
            _terrainView = terrainView;
            _relief = terrainView;
            _ppc = terrainView ? 1 : PixelsPerCell;
            if (_relief)
            {
                // 不透明、受光照：起伏靠明暗读出来（颜色之外的形状信息，B15）。
                Shader standard = Shader.Find("Standard");
                _material = new Material(standard != null ? standard : Shader.Find("Sprites/Default")) { color = Color.white, name = "TerrainRelief" };
                if (_material.HasProperty("_Glossiness"))
                {
                    _material.SetFloat("_Glossiness", 0.08f);
                }
            }
            else
            {
                _material = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 1f, 1f, alpha) };
            }
            _placeholder = BuildPlaceholder();
        }

        /// <summary>占位用的平面网格（区块边长 × 区块边长，原点在区块左下角格的左下角，与起伏网格同一坐标系）。</summary>
        private Mesh FlatMesh(int size)
        {
            if (_flatMesh != null && Math.Abs(_flatMesh.bounds.size.x - size) < 0.01f)
            {
                return _flatMesh;
            }
            if (_flatMesh != null)
            {
                SafeDestroy(_flatMesh);
            }
            _flatMesh = new Mesh { name = "TerrainFlat" };
            _flatMesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, size), new Vector3(size, 0, size), new Vector3(size, 0, 0) };
            _flatMesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            _flatMesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            _flatMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _flatMesh.RecalculateBounds();
            return _flatMesh;
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
                // 迷雾用区块自己的遮罩版本（FG3-LOG-01）：追加一个探索圆只重画被圆碰到的区块，不整窗重画。
                int stamp = unchecked(((chunk.ContentRevision * 397) ^ chunk.ExploredMaskRevision) * 397 ^ reserveHash ^ (_paletteRevision << 20)
                    ^ (_appearanceRevision * 16127) ^ (GridLines ? 0x5A5A : 0) ^ (PollutionView ? 0x3C3C0000 : 0));
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
                        PixelsPerCell = _ppc,
                        TerrainView = _terrainView,
                        GridLines = GridLines,
                        PollutionView = PollutionView,
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
            if (_relief)
            {
                scheduled += UpdateRelief(map, completeNow);
                UpdatePcg(map, completeNow);
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

        /// <summary>正式起伏网格完成后刷新 PCG 素材，普通更新每帧最多重建两个区块。</summary>
        private void UpdatePcg(HomeGridMap map, bool completeNow)
        {
            if (_pcgProfile == null) return;
            int built = 0;
            foreach (Tile t in _tiles.Values)
            {
                HomeGridMap.Chunk chunk = map.TryGetLoaded(t.ChunkX, t.ChunkY);
                if (chunk == null || t.Placeholder || !t.ReliefBuilt || t.ReliefJob != null) continue;
                if (t.Pcg != null && t.PcgContent == chunk.ContentRevision && t.PcgExplored == chunk.ExploredMaskRevision
                    && t.PcgRelief == t.ReliefStamp) continue;
                if (!completeNow && built >= 2) break;
                if (t.Pcg == null) t.Pcg = new CampaignTerrainChunk(t.Go.transform);
                t.Pcg.Rebuild(_pcgProfile, t.Mesh, _reliefSeed, t.ChunkX * _chunkSize, t.ChunkY * _chunkSize, _chunkSize,
                    chunk.Terrain, chunk.Explored, chunk.Pollution, GridContent.TerrainCode("cliff"), GridContent.TerrainCode("water"));
                t.PcgContent = chunk.ContentRevision;
                t.PcgExplored = chunk.ExploredMaskRevision;
                t.PcgRelief = t.ReliefStamp;
                built++;
            }
        }

        /// <summary>
        /// FG3-GEN-01：起伏网格——区块内容或邻区块（边上的格角要看隔壁）变化时在工作线程重建；完成的上传到网格。
        /// 窗口 = 本区块 + 一圈邻格，邻区块没加载时那一圈按可走地面算（格角在 0 高度），邻区块加载后重建一次补齐，不会留下裂缝。
        /// 主线程每帧 O(贴图块数)；重建只在变化时，O(区块格数) 的拼窗口 + 上传。
        /// </summary>
        private int UpdateRelief(HomeGridMap map, bool completeNow)
        {
            int size = _chunkSize;
            int w = size + 2;
            if (_reliefWindowSize != w || _reliefWindow == null)
            {
                _reliefWindowSize = w;
                _reliefWindow = new byte[w * w];
            }
            byte ground = GridContent.TerrainCode("buildable");
            int scheduled = 0;
            foreach (Tile t in _tiles.Values)
            {
                if (t.ReliefJob != null)
                {
                    if (completeNow)
                    {
                        t.ReliefJob.Complete();
                    }
                    if (t.ReliefJob.IsCompleted)
                    {
                        FinishRelief(t);
                    }
                    else
                    {
                        continue;
                    }
                }
                HomeGridMap.Chunk chunk = map.TryGetLoaded(t.ChunkX, t.ChunkY);
                if (chunk == null)
                {
                    if (t.ReliefBuilt)
                    {
                        t.ReliefBuilt = false;
                        t.ReliefStamp = int.MinValue;
                        t.Filter.sharedMesh = FlatMesh(size);
                    }
                    continue;
                }
                int stamp = chunk.ContentRevision * 31;
                int neighborMask = 0;
                int bit = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        HomeGridMap.Chunk n = map.TryGetLoaded(t.ChunkX + dx, t.ChunkY + dy);
                        if (n != null)
                        {
                            neighborMask |= 1 << bit;
                            stamp = unchecked(stamp * 397 + n.ContentRevision);
                        }
                        bit++;
                    }
                }
                stamp = unchecked(stamp * 397 + neighborMask);
                if (t.ReliefStamp == stamp || (!completeNow && scheduled >= MaxReliefSchedulesPerFrame))
                {
                    continue;
                }
                for (int j = -1; j <= size; j++)
                {
                    for (int i = -1; i <= size; i++)
                    {
                        byte code = ground;
                        if (i >= 0 && j >= 0 && i < size && j < size)
                        {
                            code = chunk.Terrain[j * size + i];
                        }
                        else
                        {
                            int nx = t.ChunkX + (i < 0 ? -1 : i >= size ? 1 : 0);
                            int ny = t.ChunkY + (j < 0 ? -1 : j >= size ? 1 : 0);
                            HomeGridMap.Chunk n = map.TryGetLoaded(nx, ny);
                            if (n != null)
                            {
                                int li = i < 0 ? size - 1 : i >= size ? 0 : i;
                                int lj = j < 0 ? size - 1 : j >= size ? 0 : j;
                                code = n.Terrain[lj * size + li];
                            }
                        }
                        _reliefWindow[(j + 1) * w + i + 1] = code;
                    }
                }
                ReliefParams q = ReliefParamsFor(ReliefSeed(), size, t.ChunkX * size, t.ChunkY * size);
                t.ReliefJob = WorldGenKernel.ScheduleRelief(in q, t.ChunkX, t.ChunkY, stamp, _reliefWindow);
                scheduled++;
                if (completeNow)
                {
                    t.ReliefJob.Complete();
                    FinishRelief(t);
                }
            }
            return scheduled;
        }

        /// <summary>起伏网格参数（地形码、高度调参 world.relief.*）。WorldGenQuery.ReliefHeight 用同一份参数，保证查询与画面一致。</summary>
        public static ReliefParams ReliefParamsFor(uint seed, int size, int baseX, int baseY) => new ReliefParams
        {
            ChunkSize = size,
            BaseX = baseX,
            BaseY = baseY,
            Seed = seed,
            CodeCliff = GridContent.TerrainCode("cliff"),
            CodeWater = GridContent.TerrainCode("water"),
            CliffHeight = GridContent.Tuning("world.relief.cliff_height"),
            CliffNoise = GridContent.Tuning("world.relief.cliff_noise"),
            WaterDepth = GridContent.Tuning("world.relief.water_depth"),
            NoiseScaleQ = WorldTerrainSource.Q(GridContent.Tuning("world.relief.noise_scale") * 2f),
        };

        /// <summary>起伏噪声的种子：取地形来源的表面种子（同一世界同一起伏；只是表现）。</summary>
        public uint ReliefSeed() => _reliefSeed;

        private uint _reliefSeed;

        /// <summary>设起伏噪声种子（WorldPlanetView 按战役设置；换战役时重建全部网格）。</summary>
        public void SetReliefSeed(uint seed)
        {
            if (_reliefSeed == seed)
            {
                return;
            }
            _reliefSeed = seed;
            foreach (Tile t in _tiles.Values)
            {
                t.ReliefStamp = int.MinValue;
            }
        }

        private void FinishRelief(Tile t)
        {
            WorldReliefJob job = t.ReliefJob;
            t.ReliefJob = null;
            if (t.Mesh == null)
            {
                t.Mesh = new Mesh { name = "TerrainRelief" };
                t.Mesh.MarkDynamic();
            }
            job.Upload(t.Mesh);
            t.ReliefMin = job.MinHeight;
            t.ReliefMax = job.MaxHeight;
            t.ReliefStamp = job.Stamp;
            job.Release();
            t.ReliefBuilt = true;
            if (t.Filter != null)
            {
                t.Filter.sharedMesh = t.Mesh;
            }
            Revision++;
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
            t.Pcg?.Dispose();
            t.Pcg = null;
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
                if (_relief)
                {
                    t.Go = new GameObject();
                    t.Go.transform.SetParent(_parent, false);
                    t.Filter = t.Go.AddComponent<MeshFilter>();
                    t.Renderer = t.Go.AddComponent<MeshRenderer>();
                    t.Renderer.sharedMaterial = _material;
                    t.Renderer.receiveShadows = true;
                    t.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                else
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
            }
            t.Go.name = $"TerrainChunk_{cx}_{cy}";
            t.Go.SetActive(true);
            if (_relief)
            {
                // 起伏网格的原点 = 区块左下角格的左下角（格心在整数坐标，格角在 ±0.5）。
                t.Go.transform.position = new Vector3(cx * _chunkSize - 0.5f, _height, cy * _chunkSize - 0.5f);
                t.Go.transform.localScale = Vector3.one;
                t.Filter.sharedMesh = FlatMesh(_chunkSize);
                t.ReliefBuilt = false;
                t.ReliefStamp = int.MinValue;
            }
            else
            {
                float half = (_chunkSize - 1) * 0.5f;
                t.Go.transform.position = new Vector3(cx * _chunkSize + half, _height, cy * _chunkSize + half);
                t.Go.transform.localScale = new Vector3(_chunkSize, _chunkSize, 1f);
            }
            SetTexture(t, _placeholder);
            return t;
        }

        private void Recycle(Tile t)
        {
            t.Pcg?.Dispose();
            t.Pcg = null;
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
            if (t.ReliefJob != null)
            {
                if (t.ReliefJob.IsCompleted)
                {
                    t.ReliefJob.Release();
                }
                else
                {
                    _retiringRelief.Add(t.ReliefJob);
                }
                t.ReliefJob = null;
            }
            t.ReliefBuilt = false;
            t.ReliefStamp = int.MinValue;
            if (t.Go != null)
            {
                t.Go.SetActive(false);
            }
            _pool.Push(t);
        }

        private void EnsureTexture(Tile t)
        {
            int size = _chunkSize * _ppc;
            if (t.Texture != null && t.Texture.width == size)
            {
                return;
            }
            if (t.Texture != null)
            {
                SafeDestroy(t.Texture);
            }
            t.Texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = _terrainView ? FilterMode.Bilinear : FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "TerrainChunkTile",
            };
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
            if (_pcgProfile != null)
            {
                _palette[GridContent.TerrainCode("buildable")] = _pcgProfile.Soil;
                _palette[GridContent.TerrainCode("cliff")] = _pcgProfile.Rock;
                _palette[GridContent.TerrainCode("water")] = _pcgProfile.Water;
                _palette[GridContent.TerrainCode("ruin")] = Color.Lerp(_pcgProfile.Soil, _pcgProfile.Sand, .5f);
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
            color = t.Texture.GetPixel(a.LocalX * _ppc + Math.Min(px, _ppc - 1), a.LocalY * _ppc + Math.Min(py, _ppc - 1));
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

        private readonly List<WorldReliefJob> _retiringRelief = new List<WorldReliefJob>();

        /// <summary>释放已经画完的退役贴图任务；<paramref name="all"/>=true 时（关停 / 自检）等全部完成后释放。</summary>
        private void ReleaseRetired(bool all)
        {
            for (int i = _retiringRelief.Count - 1; i >= 0; i--)
            {
                WorldReliefJob job = _retiringRelief[i];
                if (all || job.IsCompleted)
                {
                    job.Release();
                    _retiringRelief.RemoveAt(i);
                }
            }
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
            if (_flatMesh != null)
            {
                SafeDestroy(_flatMesh);
                _flatMesh = null;
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
            t.Pcg?.Dispose();
            t.Pcg = null;
            t.Job?.Release();
            t.Job = null;
            t.ReliefJob?.Release();
            t.ReliefJob = null;
            if (t.Mesh != null)
            {
                SafeDestroy(t.Mesh);
                t.Mesh = null;
            }
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
