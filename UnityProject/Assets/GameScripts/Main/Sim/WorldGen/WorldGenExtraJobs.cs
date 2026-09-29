using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.Sim.WorldGen
{
    /// <summary>起始区保证的一项查询（FG3-GEN-01，FGR-GEN-031）：核心半径 <see cref="Radius"/> 内，地形 <see cref="Code"/> 的一片连通区
    /// （4 邻接，只数圆内的格）不少于 <see cref="MinCells"/> 格，且里面有 <see cref="MinSquare"/>×<see cref="MinSquare"/> 的整块。</summary>
    public struct WorldStartQuery
    {
        public int Code;
        public int Radius;
        public int MinCells;
        public int MinSquare;
    }

    /// <summary>一项查询的结果。</summary>
    public struct WorldStartResult
    {
        /// <summary>1 = 满足。</summary>
        public int Satisfied;
        /// <summary>满足条件的连通区里最大的一片的格数（不满足时 = 含整块的最大连通区格数，可能为 0）。</summary>
        public int BestCells;
        /// <summary>这片连通区里第一个整块的左下角（格网坐标）；没有整块时为核心。</summary>
        public int SquareX;
        public int SquareY;
        /// <summary>圆内这种地形的总格数。</summary>
        public int TotalCells;
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-031、090）：起始区校验——在核心周围半径 MaxRadius 的圆里按生成器逐格采样（与区块生成同一个纯函数），
    /// 统计第 1 级（没有悬崖、污染上限）与每一项资源保证。全部在 Burst 里做：逐格 O(N) 的工作只在 AOT（CLAUDE.md 架构 4）。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobAnalyzeStart : IJob
    {
        public WorldGenParams Params;
        [ReadOnly] public NativeArray<WorldGenRect> Rects;
        public int RectCount;
        [ReadOnly] public NativeArray<WorldGenZone> Zones;
        public int ZoneCount;
        public int MaxRadius;
        public int FlatRadius;
        [ReadOnly] public NativeArray<WorldStartQuery> Queries;
        public int QueryCount;
        public NativeArray<WorldStartResult> Results;
        /// <summary>[0] = 第 1 级半径内的悬崖格数；[1] = 第 1 级半径内的最高污染；[2] = 采样格数。</summary>
        public NativeArray<int> Stats;

        public void Execute()
        {
            int r = math.max(1, MaxRadius);
            int n = 2 * r + 1;
            int cx = Params.CoreX;
            int cy = Params.CoreY;
            var terrain = new NativeArray<byte>(n * n, Allocator.Temp);
            var labels = new NativeArray<int>(n * n, Allocator.Temp);
            var queue = new NativeArray<int>(n * n, Allocator.Temp);
            var sizes = new NativeList<int>(64, Allocator.Temp);
            long r2 = (long)r * r;
            long f2 = (long)FlatRadius * FlatRadius;
            int cliffs = 0;
            int maxPol = 0;
            int sampled = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    long dx = i - r;
                    long dy = j - r;
                    long d2 = dx * dx + dy * dy;
                    int k = j * n + i;
                    if (d2 > r2)
                    {
                        terrain[k] = 255;
                        continue;
                    }
                    WorldGenMath.Sample(in Params, cx + i - r, cy + j - r, Rects, RectCount, Zones, ZoneCount, out byte t, out byte p);
                    terrain[k] = t;
                    sampled++;
                    if (FlatRadius > 0 && d2 <= f2)
                    {
                        if (t == Params.CodeCliff)
                        {
                            cliffs++;
                        }
                        maxPol = math.max(maxPol, p);
                    }
                }
            }
            Stats[0] = cliffs;
            Stats[1] = maxPol;
            Stats[2] = sampled;

            for (int q = 0; q < QueryCount; q++)
            {
                WorldStartQuery query = Queries[q];
                int qr = math.min(query.Radius, r);
                long qr2 = (long)qr * qr;
                byte code = (byte)query.Code;
                sizes.Clear();
                int total = 0;
                for (int k = 0; k < n * n; k++)
                {
                    labels[k] = -1;
                }
                // 连通区标号（4 邻接，只在半径 qr 的圆里）。扫描顺序固定 → 标号与结果确定。
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int k = j * n + i;
                        if (terrain[k] != code || labels[k] >= 0 || !InDisc(i, j, r, qr2))
                        {
                            continue;
                        }
                        int id = sizes.Length;
                        int head = 0;
                        int tail = 0;
                        queue[tail++] = k;
                        labels[k] = id;
                        while (head < tail)
                        {
                            int cur = queue[head++];
                            int ci = cur % n;
                            int cj = cur / n;
                            for (int dir = 0; dir < 4; dir++)
                            {
                                int ni = ci + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                                int nj = cj + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                                if (ni < 0 || nj < 0 || ni >= n || nj >= n)
                                {
                                    continue;
                                }
                                int nk = nj * n + ni;
                                if (labels[nk] >= 0 || terrain[nk] != code || !InDisc(ni, nj, r, qr2))
                                {
                                    continue;
                                }
                                labels[nk] = id;
                                queue[tail++] = nk;
                            }
                        }
                        sizes.Add(tail);
                        total += tail;
                    }
                }
                // 整块：左下角扫描，整块全部是这种地形且都在圆内；取所在连通区最大的一个（并列取扫描顺序第一个）。
                int sq = math.max(1, query.MinSquare);
                int bestSize = 0;
                int bestSat = 0;
                int bx = cx;
                int by = cy;
                for (int j = 0; j + sq <= n; j++)
                {
                    for (int i = 0; i + sq <= n; i++)
                    {
                        int k0 = j * n + i;
                        if (labels[k0] < 0)
                        {
                            continue;
                        }
                        bool all = true;
                        for (int b = 0; b < sq && all; b++)
                        {
                            for (int a = 0; a < sq; a++)
                            {
                                int kk = (j + b) * n + i + a;
                                if (labels[kk] < 0 || !InDisc(i + a, j + b, r, qr2))
                                {
                                    all = false;
                                    break;
                                }
                            }
                        }
                        if (!all)
                        {
                            continue;
                        }
                        int size = sizes[labels[k0]];
                        int sat = size >= query.MinCells ? 1 : 0;
                        if (sat > bestSat || (sat == bestSat && size > bestSize))
                        {
                            bestSat = sat;
                            bestSize = size;
                            bx = cx + i - r;
                            by = cy + j - r;
                        }
                    }
                }
                Results[q] = new WorldStartResult
                {
                    Satisfied = bestSat,
                    BestCells = bestSize,
                    SquareX = bx,
                    SquareY = by,
                    TotalCells = total,
                };
            }
            terrain.Dispose();
            labels.Dispose();
            queue.Dispose();
            sizes.Dispose();
        }

        private static bool InDisc(int i, int j, int r, long qr2)
        {
            long dx = i - r;
            long dy = j - r;
            return dx * dx + dy * dy <= qr2;
        }
    }

    /// <summary>地貌起伏网格的参数（FG3-GEN-01，FG-GAP-021）。只是表现：可走的地面永远在 0 高度。</summary>
    public struct ReliefParams
    {
        public int ChunkSize;
        public int BaseX;
        public int BaseY;
        public uint Seed;
        public byte CodeCliff;
        public byte CodeWater;
        public float CliffHeight;
        public float CliffNoise;
        public float WaterDepth;
        /// <summary>起伏噪声格距（16.16，单位：半格——顶点坐标用两倍整数，保证区块两侧同一顶点算出同一高度）。</summary>
        public int NoiseScaleQ;
    }

    /// <summary>
    /// FG3-GEN-01（FG-GAP-021“真实的 3D 程序化地形”）：一个区块的地貌起伏网格——每格 4 个三角形（四角 + 格心）。
    /// 高度规则（与平台无关的整数噪声 + 浮点缩放；只是表现，不进存档、不影响玩法）：
    /// - 格角：四个相邻格里有任何可走的格 → 0；四个都是悬崖 → 隆起（基础高度 + 噪声）；四个都是水 → 下陷；
    /// - 格心：悬崖 → 隆起，水 → 下陷，其余 → 0。
    /// 所以**可走的格四角与格心都在 0 高度**：单位、建筑、拾取射线仍按 0 高度平面工作，与起伏网格严丝合缝。
    /// 输入是含一圈邻格的 (S+2)² 地形窗口（区块边上的格角要看隔壁区块的格），所以相邻区块共用的格角高度相同，没有裂缝。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobBuildRelief : IJob
    {
        public ReliefParams Params;
        /// <summary>(S+2)×(S+2)，行优先；[1..S] 是本区块，外圈是邻格。</summary>
        [ReadOnly] public NativeArray<byte> Window;
        public NativeArray<float3> Vertices;
        public NativeArray<float3> Normals;
        [WriteOnly] public NativeArray<float2> Uvs;
        [WriteOnly] public NativeArray<int> Indices;
        /// <summary>[0] 最低、[1] 最高（米）。</summary>
        public NativeArray<float> Range;

        public void Execute()
        {
            ReliefParams q = Params;
            int s = q.ChunkSize;
            int w = s + 2;
            int corners = (s + 1) * (s + 1);
            float lo = 0f;
            float hi = 0f;
            float inv = 1f / s;
            for (int j = 0; j <= s; j++)
            {
                for (int i = 0; i <= s; i++)
                {
                    // 格角 (i, j) 的四个相邻格在窗口里是 (i, j)、(i+1, j)、(i, j+1)、(i+1, j+1)（窗口有 1 格外圈）。
                    byte a = Window[j * w + i];
                    byte b = Window[j * w + i + 1];
                    byte c = Window[(j + 1) * w + i];
                    byte d = Window[(j + 1) * w + i + 1];
                    float h = 0f;
                    if (a == q.CodeCliff && b == q.CodeCliff && c == q.CodeCliff && d == q.CodeCliff)
                    {
                        h = CliffHeight(in q, 2 * (q.BaseX + i) - 1, 2 * (q.BaseY + j) - 1);
                    }
                    else if (a == q.CodeWater && b == q.CodeWater && c == q.CodeWater && d == q.CodeWater)
                    {
                        h = -q.WaterDepth;
                    }
                    int v = j * (s + 1) + i;
                    Vertices[v] = new float3(i, h, j);
                    Uvs[v] = new float2(i * inv, j * inv);
                    Normals[v] = float3.zero;
                    lo = math.min(lo, h);
                    hi = math.max(hi, h);
                }
            }
            for (int j = 0; j < s; j++)
            {
                for (int i = 0; i < s; i++)
                {
                    byte t = Window[(j + 1) * w + i + 1];
                    float h = 0f;
                    if (t == q.CodeCliff)
                    {
                        h = CliffHeight(in q, 2 * (q.BaseX + i), 2 * (q.BaseY + j)) + q.CliffHeight * 0.25f;
                    }
                    else if (t == q.CodeWater)
                    {
                        h = -q.WaterDepth;
                    }
                    int v = corners + j * s + i;
                    Vertices[v] = new float3(i + 0.5f, h, j + 0.5f);
                    Uvs[v] = new float2((i + 0.5f) * inv, (j + 0.5f) * inv);
                    Normals[v] = float3.zero;
                    lo = math.min(lo, h);
                    hi = math.max(hi, h);
                }
            }
            int n = 0;
            for (int j = 0; j < s; j++)
            {
                for (int i = 0; i < s; i++)
                {
                    int c00 = j * (s + 1) + i;
                    int c10 = c00 + 1;
                    int c01 = c00 + s + 1;
                    int c11 = c01 + 1;
                    int m = corners + j * s + i;
                    n = Tri(n, c00, c01, m);
                    n = Tri(n, c01, c11, m);
                    n = Tri(n, c11, c10, m);
                    n = Tri(n, c10, c00, m);
                }
            }
            int total = corners + s * s;
            for (int v = 0; v < total; v++)
            {
                float3 nv = Normals[v];
                Normals[v] = math.lengthsq(nv) > 1e-12f ? math.normalize(nv) : new float3(0f, 1f, 0f);
            }
            Range[0] = lo;
            Range[1] = hi;
        }

        private int Tri(int n, int a, int b, int c)
        {
            Indices[n] = a;
            Indices[n + 1] = b;
            Indices[n + 2] = c;
            // 面法线累加到顶点（顺时针 = 朝上）。
            float3 pa = Vertices[a];
            float3 pb = Vertices[b];
            float3 pc = Vertices[c];
            float3 fn = math.cross(pb - pa, pc - pa);
            Normals[a] += fn;
            Normals[b] += fn;
            Normals[c] += fn;
            return n + 3;
        }

        /// <summary>悬崖隆起高度：整数噪声（两倍坐标 = 半格精度）→ 浮点缩放。同一顶点在两侧区块算出同一值。</summary>
        public static float CliffHeight(in ReliefParams q, int x2, int y2)
        {
            int nz = WorldGenMath.Noise(q.Seed, x2, y2, 11u, math.max(65536, q.NoiseScaleQ));
            return q.CliffHeight + q.CliffNoise * (nz / 65536f);
        }
    }

    /// <summary>战略地图 / 小地图底图的参数（FG3-GEN-01，FGR-GEN-080、081）。</summary>
    public struct MapPaintParams
    {
        public int Width;
        public int Height;
        /// <summary>像素 (0,0) 左下角对应的格网坐标（16.16 定点，允许小数格）。</summary>
        public long OriginXQ;
        public long OriginYQ;
        /// <summary>每像素多少格（16.16）。</summary>
        public int CellsPerPixelQ;
        public int ExploredCount;
        public int BlockLevel;
    }

    /// <summary>
    /// FG3-GEN-01：战略地图 / 小地图底图——每个像素按生成器采样一格（与区块生成同一个纯函数，与访问顺序无关），按地形表颜色上色、
    /// 污染染紫；未探索的像素是迷雾色（不泄露地形）。Burst 工作线程上画，主线程只上传。
    /// 说明：底图画的是“生成出来的样子”；玩家改过的格子（拆废墟、净化）在近景里看，地图上的建筑 / 据点用图标表示。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobPaintMap : IJob
    {
        public MapPaintParams Map;
        public WorldGenParams Gen;
        [ReadOnly] public NativeArray<WorldGenRect> Rects;
        public int RectCount;
        [ReadOnly] public NativeArray<WorldGenZone> Zones;
        public int ZoneCount;
        /// <summary>已探索的圆（x = 中心 X，y = 中心 Y，z = 半径）。</summary>
        [ReadOnly] public NativeArray<int3> Explored;
        [ReadOnly] public NativeArray<Color32> Palette;
        [WriteOnly] public NativeArray<Color32> Pixels;

        public void Execute()
        {
            MapPaintParams m = Map;
            var fog = new Color32(24, 26, 30, 255);
            var fogEdge = new Color32(34, 37, 43, 255);
            for (int py = 0; py < m.Height; py++)
            {
                for (int px = 0; px < m.Width; px++)
                {
                    long xq = m.OriginXQ + (long)m.CellsPerPixelQ * px + m.CellsPerPixelQ / 2;
                    long yq = m.OriginYQ + (long)m.CellsPerPixelQ * py + m.CellsPerPixelQ / 2;
                    int x = (int)WorldGenMath.FloorDiv(xq + 32768, 65536);
                    int y = (int)WorldGenMath.FloorDiv(yq + 32768, 65536);
                    int idx = py * m.Width + px;
                    if (!IsExplored(x, y))
                    {
                        Pixels[idx] = ((px / 4 + py / 4) & 1) == 0 ? fog : fogEdge; // 迷雾：暗色 + 棋盘纹（颜色之外有图案）。
                        continue;
                    }
                    WorldGenMath.Sample(in Gen, x, y, Rects, RectCount, Zones, ZoneCount, out byte t, out byte p);
                    Color32 c = Palette[t];
                    if (p > 0)
                    {
                        int k = p >= m.BlockLevel ? 120 : 60;
                        c = new Color32((byte)(c.r + ((150 - c.r) * k >> 8)), (byte)(c.g + ((40 - c.g) * k >> 8)), (byte)(c.b + ((150 - c.b) * k >> 8)), 255);
                    }
                    Pixels[idx] = c;
                }
            }
        }

        private bool IsExplored(int x, int y)
        {
            for (int i = 0; i < Map.ExploredCount; i++)
            {
                int3 e = Explored[i];
                long dx = x - e.x;
                long dy = y - e.y;
                if (dx * dx + dy * dy <= (long)e.z * e.z)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>一个已调度的地貌起伏网格任务（工作线程）。完成后 <see cref="Upload"/> 到网格，再 <see cref="Release"/>（成对释放）。</summary>
    public sealed class WorldReliefJob
    {
        public int ChunkX { get; private set; }
        public int ChunkY { get; private set; }
        public int Stamp { get; private set; }
        public bool Released { get; private set; }
        public float MinHeight { get; private set; }
        public float MaxHeight { get; private set; }

        private JobHandle _handle;
        private NativeArray<byte> _window;
        private NativeArray<float3> _vertices;
        private NativeArray<float3> _normals;
        private NativeArray<float2> _uvs;
        private NativeArray<int> _indices;
        private NativeArray<float> _range;

        internal void Start(in ReliefParams q, int cx, int cy, int stamp, byte[] window)
        {
            ChunkX = cx;
            ChunkY = cy;
            Stamp = stamp;
            Released = false;
            int s = q.ChunkSize;
            int vcount = (s + 1) * (s + 1) + s * s;
            _window = new NativeArray<byte>(window, Allocator.Persistent);
            _vertices = new NativeArray<float3>(vcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _normals = new NativeArray<float3>(vcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _uvs = new NativeArray<float2>(vcount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _indices = new NativeArray<int>(s * s * 12, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _range = new NativeArray<float>(2, Allocator.Persistent);
            _handle = new JobBuildRelief
            {
                Params = q,
                Window = _window,
                Vertices = _vertices,
                Normals = _normals,
                Uvs = _uvs,
                Indices = _indices,
                Range = _range,
            }.Schedule();
        }

        public bool IsCompleted => Released || _handle.IsCompleted;

        public void Complete()
        {
            if (!Released)
            {
                _handle.Complete();
            }
        }

        /// <summary>把结果写进网格（主线程）。</summary>
        public void Upload(Mesh mesh)
        {
            if (Released)
            {
                throw new InvalidOperationException("WorldReliefJob 已释放");
            }
            _handle.Complete();
            mesh.Clear();
            mesh.indexFormat = _vertices.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices.Reinterpret<Vector3>());
            mesh.SetNormals(_normals.Reinterpret<Vector3>());
            mesh.SetUVs(0, _uvs.Reinterpret<Vector2>());
            mesh.SetIndices(_indices, MeshTopology.Triangles, 0, false);
            MinHeight = _range[0];
            MaxHeight = _range[1];
            int s = (int)math.round(math.sqrt(_indices.Length / 12f));
            mesh.bounds = new Bounds(new Vector3(s * 0.5f, (MinHeight + MaxHeight) * 0.5f, s * 0.5f),
                new Vector3(s, math.max(0.1f, MaxHeight - MinHeight), s));
        }

        /// <summary>读一个顶点的高度（自检用；主线程，完成后）。</summary>
        public float HeightAt(int vertex)
        {
            _handle.Complete();
            return _vertices[vertex].y;
        }

        public int VertexCount => _vertices.IsCreated ? _vertices.Length : 0;

        public void Release()
        {
            if (Released)
            {
                return;
            }
            _handle.Complete();
            if (_window.IsCreated) _window.Dispose();
            if (_vertices.IsCreated) _vertices.Dispose();
            if (_normals.IsCreated) _normals.Dispose();
            if (_uvs.IsCreated) _uvs.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_range.IsCreated) _range.Dispose();
            Released = true;
            WorldGenKernel.Untrack(this);
        }
    }

    /// <summary>一个已调度的地图底图任务（工作线程）。完成后 <see cref="Upload"/> 到贴图，再 <see cref="Release"/>。</summary>
    public sealed class WorldMapPaintJob
    {
        public int Stamp { get; private set; }
        public bool Released { get; private set; }
        public MapPaintParams Map { get; private set; }
        public double ScheduledAtMs { get; set; }

        private JobHandle _handle;
        private NativeArray<WorldGenRect> _rects;
        private NativeArray<WorldGenZone> _zones;
        private NativeArray<int3> _explored;
        private NativeArray<Color32> _palette;
        private NativeArray<Color32> _pixels;

        internal void Start(in MapPaintParams m, in WorldGenParams gen, WorldGenRect[] rects, WorldGenZone[] zones, int3[] explored,
            Color32[] palette, int stamp)
        {
            Stamp = stamp;
            Map = m;
            Released = false;
            _rects = WorldGenKernel.ToNative(rects, Allocator.Persistent);
            _zones = WorldGenKernel.ToNative(zones, Allocator.Persistent);
            _explored = WorldGenKernel.ToNative(explored, Allocator.Persistent);
            _palette = new NativeArray<Color32>(palette, Allocator.Persistent);
            _pixels = new NativeArray<Color32>(m.Width * m.Height, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var mm = m;
            mm.ExploredCount = explored?.Length ?? 0;
            Map = mm;
            _handle = new JobPaintMap
            {
                Map = mm,
                Gen = gen,
                Rects = _rects,
                RectCount = rects?.Length ?? 0,
                Zones = _zones,
                ZoneCount = zones?.Length ?? 0,
                Explored = _explored,
                Palette = _palette,
                Pixels = _pixels,
            }.Schedule();
        }

        public bool IsCompleted => Released || _handle.IsCompleted;

        public void Complete()
        {
            if (!Released)
            {
                _handle.Complete();
            }
        }

        public void Upload(Texture2D texture)
        {
            if (Released)
            {
                throw new InvalidOperationException("WorldMapPaintJob 已释放");
            }
            _handle.Complete();
            texture.SetPixelData(_pixels, 0);
            texture.Apply(false, false);
        }

        public void Release()
        {
            if (Released)
            {
                return;
            }
            _handle.Complete();
            if (_rects.IsCreated) _rects.Dispose();
            if (_zones.IsCreated) _zones.Dispose();
            if (_explored.IsCreated) _explored.Dispose();
            if (_palette.IsCreated) _palette.Dispose();
            if (_pixels.IsCreated) _pixels.Dispose();
            Released = true;
            WorldGenKernel.Untrack(this);
        }
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-031、090，修复轮）：起始区保证点落位——先按调用方给的试探点（子种子派生）依次检查，都不合法时逐格列出全部合法候选、
    /// 取第 PickHash % 候选数 个（行优先：dy 外层、dx 内层）。合法 = 圆心离核心不超过 MaxOffset、圆盘外扩 1 格不碰起始区平地矩形、
    /// 与已放的保证点至少隔 2 格。兜底要枚举 (2·MaxOffset+1)² 个候选（油井约 6.3 万个），逐格 O(N) 的工作放在 AOT Burst（CLAUDE.md 架构 4），
    /// 热更层只拿结果。全整数运算，结果与原先托管实现逐位相同（v2 规划层指纹基准守护）。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobPlaceStamp : IJob
    {
        public int CoreX;
        public int CoreY;
        public int StampRadius;
        public int MaxOffset;
        public uint PickHash;
        [ReadOnly] public NativeArray<WorldGenRect> Rects;
        public int RectCount;
        [ReadOnly] public NativeArray<WorldGenZone> Placed;
        public int PlacedCount;
        [ReadOnly] public NativeArray<int> TryX;
        [ReadOnly] public NativeArray<int> TryY;
        public int TryCount;
        /// <summary>[0] = 结果（k ≥ 0：第 k 个试探点；-1：候选兜底；-2：没有合法位置）；[1]/[2] = 圆心；[3] = 兜底时的候选数。</summary>
        public NativeArray<int> Result;

        public void Execute()
        {
            Result[0] = -2;
            Result[1] = CoreX;
            Result[2] = CoreY;
            Result[3] = 0;
            for (int k = 0; k < TryCount; k++)
            {
                if (Valid(TryX[k], TryY[k]))
                {
                    Result[0] = k;
                    Result[1] = TryX[k];
                    Result[2] = TryY[k];
                    return;
                }
            }
            int hi = MaxOffset;
            int count = 0;
            for (int dy = -hi; dy <= hi; dy++)
            {
                for (int dx = -hi; dx <= hi; dx++)
                {
                    if (Valid(CoreX + dx, CoreY + dy))
                    {
                        count++;
                    }
                }
            }
            Result[3] = count;
            if (count == 0)
            {
                return;
            }
            int pick = (int)(PickHash % (uint)count);
            int seen = 0;
            for (int dy = -hi; dy <= hi; dy++)
            {
                for (int dx = -hi; dx <= hi; dx++)
                {
                    if (!Valid(CoreX + dx, CoreY + dy))
                    {
                        continue;
                    }
                    if (seen == pick)
                    {
                        Result[0] = -1;
                        Result[1] = CoreX + dx;
                        Result[2] = CoreY + dy;
                        return;
                    }
                    seen++;
                }
            }
        }

        private bool Valid(int cx, int cy)
        {
            int r = StampRadius;
            long ox = cx - CoreX;
            long oy = cy - CoreY;
            if (ox * ox + oy * oy > (long)MaxOffset * MaxOffset)
            {
                return false;
            }
            for (int i = 0; i < PlacedCount; i++)
            {
                WorldGenZone z = Placed[i];
                long dx = cx - z.CenterX;
                long dy = cy - z.CenterY;
                long need = r + z.Radius + 2;
                if (dx * dx + dy * dy < need * need)
                {
                    return false;
                }
            }
            int minX = cx - r - 1;
            int maxX = cx + r + 1;
            int minY = cy - r - 1;
            int maxY = cy + r + 1;
            long rr = (long)(r + 1) * (r + 1);
            for (int i = 0; i < RectCount; i++)
            {
                WorldGenRect rect = Rects[i];
                if (maxX < rect.MinX || minX > rect.MaxX || maxY < rect.MinY || minY > rect.MaxY)
                {
                    continue;
                }
                // 包围盒相交：逐格精确判定（圆盘外扩 1 格）。
                for (int yy = minY; yy <= maxY; yy++)
                {
                    for (int xx = minX; xx <= maxX; xx++)
                    {
                        long dx = xx - cx;
                        long dy = yy - cy;
                        if (dx * dx + dy * dy <= rr && xx >= rect.MinX && xx <= rect.MaxX && yy >= rect.MinY && yy <= rect.MaxY)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
