using System;
using System.Collections.Generic;

namespace GameLogic.Campaign.Grid
{
    /// <summary>格网的六层（FGR-ARC-001 / FGR-LOG-001）。</summary>
    public enum GridLayer : byte
    {
        Terrain = 0,
        Pollution = 1,
        Occupancy = 2,
        Belt = 3,
        Pipe = 4,
        Fog = 5,
    }

    /// <summary>
    /// FG0-ARCH-04（FGR-ARC-001）：一张表面（家园）的**无限格网**，按区块（初值 32×32，fg.TbHomeTuning grid.chunk_size）
    /// 惰性创建，每个区块六层：地形、污染、占用、传送带、管线、迷雾。
    ///
    /// - 地形 / 污染：区块第一次被访问时由 <see cref="IGridTerrainSource"/> 按（种子, 格子）生成，与访问顺序无关。
    /// - 占用：建筑记录的派生缓存（格子 → 建筑序号）。唯一真相是 <see cref="BuildingRecord"/> 的枢轴格与朝向；
    ///   <see cref="HomeGridService"/> 在建筑记录变化时整体重建，旋转时增量改写。
    /// - 传送带 / 管线：层与读写接口已就绪，写入者是 FG0-ARCH-02（传送带内核）与 FG3-LOG-05（管线）；
    ///   放置校验已经把它们当作“被占用”。
    /// - 迷雾：已探索 = 落在任一已探索圆里（GridState.Explored），区块创建时按圆计算，圆变化时整体失效重算。
    /// 每次查询 O(1)（字典取区块 + 数组下标），与建筑数、区块数无关。
    /// </summary>
    public sealed class HomeGridMap
    {
        public sealed class Chunk
        {
            public readonly int ChunkX;
            public readonly int ChunkY;
            public readonly byte[] Terrain;
            public readonly byte[] Pollution;
            /// <summary>0 = 空；n = 占用者序号 + 1（见 <see cref="HomeGridMap.OccupantId"/>）。</summary>
            public readonly int[] Occupancy;
            /// <summary>0 = 无传送带；其余取值由 FG0-ARCH-02 定义（传送带段 ID）。</summary>
            public readonly ushort[] Belt;
            /// <summary>0 = 无管线；其余取值由 FG3-LOG-05 定义（管线段 ID）。</summary>
            public readonly ushort[] Pipe;
            /// <summary>1 = 已探索。</summary>
            public readonly byte[] Explored;
            public int ExploredRevision = -1;

            public Chunk(int cx, int cy, int size)
            {
                ChunkX = cx;
                ChunkY = cy;
                int n = size * size;
                Terrain = new byte[n];
                Pollution = new byte[n];
                Occupancy = new int[n];
                Belt = new ushort[n];
                Pipe = new ushort[n];
                Explored = new byte[n];
            }
        }

        private readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        private readonly List<string> _occupantIds = new List<string>();
        private readonly Dictionary<string, int> _occupantIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private ExploredAreaRecord[] _explored = Array.Empty<ExploredAreaRecord>();
        private int _exploredRevision;

        public int ChunkSize { get; }
        public IGridTerrainSource TerrainSource { get; }
        public int LoadedChunkCount => _chunks.Count;

        public HomeGridMap(int chunkSize, IGridTerrainSource terrainSource)
        {
            if (chunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), "区块边长必须 >= 1（fg.TbHomeTuning grid.chunk_size）");
            }
            ChunkSize = chunkSize;
            TerrainSource = terrainSource ?? throw new ArgumentNullException(nameof(terrainSource));
        }

        private static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        /// <summary>取（必要时生成）格子所在的区块。</summary>
        public Chunk ChunkAt(GridCell cell, out int index)
        {
            ChunkAddress a = GridMath.Address(cell, ChunkSize);
            index = a.LocalY * ChunkSize + a.LocalX;
            long key = Key(a.ChunkX, a.ChunkY);
            if (!_chunks.TryGetValue(key, out Chunk chunk))
            {
                chunk = Generate(a.ChunkX, a.ChunkY);
                _chunks[key] = chunk;
            }
            if (chunk.ExploredRevision != _exploredRevision)
            {
                RefreshExplored(chunk);
            }
            return chunk;
        }

        public bool IsChunkLoaded(int cx, int cy) => _chunks.ContainsKey(Key(cx, cy));

        private Chunk Generate(int cx, int cy)
        {
            var chunk = new Chunk(cx, cy, ChunkSize);
            int baseX = cx * ChunkSize;
            int baseY = cy * ChunkSize;
            for (int ly = 0; ly < ChunkSize; ly++)
            {
                for (int lx = 0; lx < ChunkSize; lx++)
                {
                    TerrainSource.Sample(baseX + lx, baseY + ly, out byte t, out byte p);
                    int i = ly * ChunkSize + lx;
                    chunk.Terrain[i] = t;
                    chunk.Pollution[i] = p;
                }
            }
            return chunk;
        }

        // ── 地形 / 污染 ──────────────────────────────────────────────────────────

        public byte GetTerrain(GridCell cell) => ChunkAt(cell, out int i).Terrain[i];

        public byte GetPollution(GridCell cell) => ChunkAt(cell, out int i).Pollution[i];

        /// <summary>测试与后续地形改造（FG07 净化、FG0-ARCH-05 差异）用：直接改一格地形 / 污染。</summary>
        public void SetTerrain(GridCell cell, byte terrain) => ChunkAt(cell, out int i).Terrain[i] = terrain;

        public void SetPollution(GridCell cell, byte level) => ChunkAt(cell, out int i).Pollution[i] = (byte)Math.Min(level, (byte)3);

        // ── 传送带 / 管线（FG0-ARCH-02 / FG3-LOG-05 写入）─────────────────────────────

        public ushort GetBelt(GridCell cell) => ChunkAt(cell, out int i).Belt[i];

        public void SetBelt(GridCell cell, ushort segment) => ChunkAt(cell, out int i).Belt[i] = segment;

        public ushort GetPipe(GridCell cell) => ChunkAt(cell, out int i).Pipe[i];

        public void SetPipe(GridCell cell, ushort segment) => ChunkAt(cell, out int i).Pipe[i] = segment;

        // ── 迷雾 ─────────────────────────────────────────────────────────────────

        public void SetExplored(ExploredAreaRecord[] areas)
        {
            _explored = areas ?? Array.Empty<ExploredAreaRecord>();
            _exploredRevision++;
        }

        public bool IsExplored(GridCell cell) => ChunkAt(cell, out int i).Explored[i] != 0;

        private void RefreshExplored(Chunk chunk)
        {
            int baseX = chunk.ChunkX * ChunkSize;
            int baseY = chunk.ChunkY * ChunkSize;
            Array.Clear(chunk.Explored, 0, chunk.Explored.Length);
            foreach (ExploredAreaRecord a in _explored)
            {
                if (a == null || a.Radius < 0)
                {
                    continue;
                }
                long r2 = (long)a.Radius * a.Radius;
                int minX = Math.Max(baseX, a.CenterX - a.Radius);
                int maxX = Math.Min(baseX + ChunkSize - 1, a.CenterX + a.Radius);
                int minY = Math.Max(baseY, a.CenterY - a.Radius);
                int maxY = Math.Min(baseY + ChunkSize - 1, a.CenterY + a.Radius);
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        long dx = x - a.CenterX;
                        long dy = y - a.CenterY;
                        if (dx * dx + dy * dy <= r2)
                        {
                            chunk.Explored[(y - baseY) * ChunkSize + (x - baseX)] = 1;
                        }
                    }
                }
            }
            chunk.ExploredRevision = _exploredRevision;
        }

        // ── 占用 ─────────────────────────────────────────────────────────────────

        /// <summary>占用者（建筑 ID）；空格返回 null。</summary>
        public string OccupantAt(GridCell cell)
        {
            int v = ChunkAt(cell, out int i).Occupancy[i];
            return v == 0 ? null : _occupantIds[v - 1];
        }

        public string OccupantId(int occupancyValue) => occupancyValue <= 0 || occupancyValue > _occupantIds.Count ? null : _occupantIds[occupancyValue - 1];

        public int OccupantCount => _occupantIndex.Count;

        /// <summary>清空全部占用（区块本身与地形保留）。</summary>
        public void ClearOccupancy()
        {
            foreach (Chunk c in _chunks.Values)
            {
                Array.Clear(c.Occupancy, 0, c.Occupancy.Length);
            }
            _occupantIds.Clear();
            _occupantIndex.Clear();
        }

        /// <summary>把一组格子标成某建筑占用（后写覆盖先写；重叠由调用方先校验）。</summary>
        public void Occupy(string buildingId, List<GridCell> cells)
        {
            if (!_occupantIndex.TryGetValue(buildingId, out int idx))
            {
                _occupantIds.Add(buildingId);
                idx = _occupantIds.Count;
                _occupantIndex[buildingId] = idx;
            }
            for (int k = 0; k < cells.Count; k++)
            {
                ChunkAt(cells[k], out int i).Occupancy[i] = idx;
            }
        }

        /// <summary>释放一组格子里属于该建筑的占用（其它建筑的格子不动）。</summary>
        public void Release(string buildingId, List<GridCell> cells)
        {
            if (!_occupantIndex.TryGetValue(buildingId, out int idx))
            {
                return;
            }
            for (int k = 0; k < cells.Count; k++)
            {
                Chunk c = ChunkAt(cells[k], out int i);
                if (c.Occupancy[i] == idx)
                {
                    c.Occupancy[i] = 0;
                }
            }
        }

        /// <summary>全图占用格数（自检用，O(已加载区块格数)）。</summary>
        public int CountOccupiedCells()
        {
            int n = 0;
            foreach (Chunk c in _chunks.Values)
            {
                for (int i = 0; i < c.Occupancy.Length; i++)
                {
                    if (c.Occupancy[i] != 0)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>全图占用快照（格子 → 建筑 ID），自检比对“增量改写 == 整体重建”用。</summary>
        public Dictionary<GridCell, string> SnapshotOccupancy()
        {
            var map = new Dictionary<GridCell, string>();
            foreach (Chunk c in _chunks.Values)
            {
                for (int i = 0; i < c.Occupancy.Length; i++)
                {
                    if (c.Occupancy[i] != 0)
                    {
                        var cell = new GridCell(c.ChunkX * ChunkSize + i % ChunkSize, c.ChunkY * ChunkSize + i / ChunkSize);
                        map[cell] = _occupantIds[c.Occupancy[i] - 1];
                    }
                }
            }
            return map;
        }
    }
}
