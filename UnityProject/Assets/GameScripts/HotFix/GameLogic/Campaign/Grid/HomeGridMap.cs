using System;
using System.Collections.Generic;
using GameLogic.Campaign.WorldGen;
using TEngine;

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

    /// <summary>区块状态（FGR-GEN-060）：未生成 / 已生成未修改 / 已修改。只有已修改的区块进存档。</summary>
    public enum ChunkState : byte
    {
        NotGenerated = 0,
        Generated = 1,
        Modified = 2,
    }

    /// <summary>
    /// FG0-ARCH-04（FGR-ARC-001）/ FG0-ARCH-05（FGR-GEN-050、060）：一张表面的**无限格网**，按区块（初值 32×32，
    /// fg.TbHomeTuning grid.chunk_size）存储，每个区块六层：地形、污染、占用、传送带、管线、迷雾。
    ///
    /// - 地形 / 污染：由 <see cref="IGridTerrainSource"/> 按（种子, 版本, 世界设置, 表面, 格子）生成，与访问顺序无关。两条入口：
    ///   流式加载（<see cref="WorldChunkStreamer"/> 在工作线程生成，主线程 <see cref="TryAdopt"/> 接入）与同步兜底（玩法查询碰到
    ///   还没生成的区块时 <see cref="ChunkAt"/> 当场生成）。两条入口结果逐字节相同（自检）。
    /// - 区块差异（FGR-GEN-060）：<see cref="SetTerrain"/> / <see cref="SetPollution"/> 第一次改某个区块时保存“生成基线”副本并把区块
    ///   标为已修改；存档时 <see cref="CollectDiffs"/> 只输出与基线不同的格子；改回原样的区块自动回到“未修改”。读档后区块被生成
    ///   （无论哪条入口）时，先按种子生成再套上存档里的差异。
    /// - 占用：建筑记录的派生缓存（格子 → 建筑序号），<see cref="HomeGridService"/> 维护，不进存档。
    /// - 传送带 / 管线：层与读写接口已就绪（FG0-ARCH-02 / FG3-LOG-05 写入）。
    /// - 迷雾：已探索 = 落在任一已探索圆里（GridState.Explored）。
    /// - 回收（FGR-GEN-052“纯地形区块不需要模拟”）：未修改、没有建筑 / 传送带 / 管线的区块可以被 <see cref="TryEvict"/> 丢弃，
    ///   需要时按种子重新生成，结果相同。
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
            /// <summary>地形 / 污染内容的版本（每次修改 +1；叠加层据此重画）。</summary>
            public int ContentRevision;
            /// <summary>已修改（与生成基线不同的可能性）；基线在第一次修改时保存。</summary>
            public bool Modified;
            public byte[] BaseTerrain;
            public byte[] BasePollution;

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

            /// <summary>有建筑、传送带或管线（不能回收）。O(格数)。</summary>
            public bool HasStructures()
            {
                for (int i = 0; i < Occupancy.Length; i++)
                {
                    if (Occupancy[i] != 0 || Belt[i] != 0 || Pipe[i] != 0)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        private readonly List<string> _occupantIds = new List<string>();
        private readonly Dictionary<string, int> _occupantIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>存档里的区块差异（编码后）：键 = 区块。未加载的区块也保留在这里，存档时原样写回（FGR-GEN-060）。</summary>
        private readonly Dictionary<long, string> _savedDiffs = new Dictionary<long, string>();
        private readonly List<ChunkCellDiff> _diffScratch = new List<ChunkCellDiff>(64);
        private ExploredAreaRecord[] _explored = Array.Empty<ExploredAreaRecord>();
        private int _exploredRevision;

        public int ChunkSize { get; }
        public IGridTerrainSource TerrainSource { get; }
        /// <summary>这张格网是哪个表面（fg.TbSurface.id），区块差异按它存。</summary>
        public string SurfaceId { get; }
        public int LoadedChunkCount => _chunks.Count;
        /// <summary>任何区块被生成、接入、回收或地形 / 污染被修改时 +1。</summary>
        public int Revision { get; private set; }
        public int ExploredRevision => _exploredRevision;
        public ExploredAreaRecord[] ExploredAreas => _explored;
        /// <summary>同步生成的区块数（玩法查询碰到还没生成的区块；正常游戏里被预生成边距覆盖，应当很少）。</summary>
        public int SyncGeneratedCount { get; private set; }
        /// <summary>经流式加载（工作线程）接入的区块数。</summary>
        public int AdoptedCount { get; private set; }
        public int EvictedCount { get; private set; }
        public IEnumerable<Chunk> LoadedChunks => _chunks.Values;

        public HomeGridMap(int chunkSize, IGridTerrainSource terrainSource, string surfaceId = WorldGenContent.EarthSurfaceId)
        {
            if (chunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), "区块边长必须 >= 1（fg.TbHomeTuning grid.chunk_size）");
            }
            ChunkSize = chunkSize;
            TerrainSource = terrainSource ?? throw new ArgumentNullException(nameof(terrainSource));
            SurfaceId = surfaceId;
        }

        public static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public static void Unkey(long key, out int cx, out int cy)
        {
            cx = (int)(key >> 32);
            cy = (int)(uint)key;
        }

        /// <summary>取（必要时同步生成）格子所在的区块。</summary>
        public Chunk ChunkAt(GridCell cell, out int index)
        {
            ChunkAddress a = GridMath.Address(cell, ChunkSize);
            index = a.LocalY * ChunkSize + a.LocalX;
            long key = Key(a.ChunkX, a.ChunkY);
            if (!_chunks.TryGetValue(key, out Chunk chunk))
            {
                chunk = Generate(a.ChunkX, a.ChunkY);
                _chunks[key] = chunk;
                SyncGeneratedCount++;
                Revision++;
            }
            if (chunk.ExploredRevision != _exploredRevision)
            {
                RefreshExplored(chunk);
            }
            return chunk;
        }

        public bool IsChunkLoaded(int cx, int cy) => _chunks.ContainsKey(Key(cx, cy));

        /// <summary>已加载的区块（不触发生成）；没加载返回 null。</summary>
        public Chunk TryGetLoaded(int cx, int cy)
        {
            if (!_chunks.TryGetValue(Key(cx, cy), out Chunk c))
            {
                return null;
            }
            if (c.ExploredRevision != _exploredRevision)
            {
                RefreshExplored(c);
            }
            return c;
        }

        public ChunkState StateOf(int cx, int cy)
        {
            if (!_chunks.TryGetValue(Key(cx, cy), out Chunk c))
            {
                return ChunkState.NotGenerated;
            }
            return c.Modified ? ChunkState.Modified : ChunkState.Generated;
        }

        private Chunk Generate(int cx, int cy)
        {
            var chunk = new Chunk(cx, cy, ChunkSize);
            TerrainSource.FillChunk(cx, cy, ChunkSize, chunk.Terrain, chunk.Pollution);
            ApplySavedDiff(chunk);
            return chunk;
        }

        /// <summary>接入工作线程生成的区块（主线程）。区块已存在（比如被同步查询先生成了）时丢弃结果并返回 false——两条入口结果相同。</summary>
        public bool TryAdopt(int cx, int cy, byte[] terrain, byte[] pollution)
        {
            long key = Key(cx, cy);
            if (_chunks.ContainsKey(key))
            {
                return false;
            }
            var chunk = new Chunk(cx, cy, ChunkSize);
            Buffer.BlockCopy(terrain, 0, chunk.Terrain, 0, chunk.Terrain.Length);
            Buffer.BlockCopy(pollution, 0, chunk.Pollution, 0, chunk.Pollution.Length);
            ApplySavedDiff(chunk);
            _chunks[key] = chunk;
            AdoptedCount++;
            Revision++;
            return true;
        }

        /// <summary>回收一个纯地形、未修改、没有建筑 / 传送带 / 管线的区块（之后需要时按种子重新生成）。不满足条件返回 false。</summary>
        public bool TryEvict(int cx, int cy)
        {
            long key = Key(cx, cy);
            if (!_chunks.TryGetValue(key, out Chunk c) || c.Modified || _savedDiffs.ContainsKey(key) || c.HasStructures())
            {
                return false;
            }
            _chunks.Remove(key);
            EvictedCount++;
            Revision++;
            return true;
        }

        // ── 区块差异（FGR-GEN-060）────────────────────────────────────────────────

        /// <summary>读档：设置这张表面的已保存差异（只接受本表面的记录；编码已在读档时校验过）。</summary>
        public void SetSavedDiffs(IEnumerable<ChunkDiffRecord> records)
        {
            _savedDiffs.Clear();
            if (records == null)
            {
                return;
            }
            foreach (ChunkDiffRecord r in records)
            {
                if (r != null && r.SurfaceId == SurfaceId)
                {
                    _savedDiffs[Key(r.ChunkX, r.ChunkY)] = r.DiffPayload;
                }
            }
            // 已加载的区块立刻套用（通常 SetSavedDiffs 在建图后、生成任何区块前调用）。
            foreach (Chunk c in _chunks.Values)
            {
                ApplySavedDiff(c);
            }
        }

        private void ApplySavedDiff(Chunk chunk)
        {
            if (chunk.Modified || !_savedDiffs.TryGetValue(Key(chunk.ChunkX, chunk.ChunkY), out string payload))
            {
                return;
            }
            if (!WorldDiffCodec.TryDecode(payload, ChunkSize, _diffScratch, out string error))
            {
                // 读档时已整体校验；走到这里说明是运行中被外部改坏，保留原样、不套用，写 Error 便于发现。
                Log.Error($"[HomeGridMap] 表面 {SurfaceId} 区块 ({chunk.ChunkX},{chunk.ChunkY}) 的差异无法解码：{error}");
                return;
            }
            chunk.BaseTerrain = (byte[])chunk.Terrain.Clone();
            chunk.BasePollution = (byte[])chunk.Pollution.Clone();
            chunk.Modified = true;
            foreach (ChunkCellDiff d in _diffScratch)
            {
                if ((d.Mask & ChunkCellDiff.TerrainBit) != 0)
                {
                    chunk.Terrain[d.Index] = d.Terrain;
                }
                if ((d.Mask & ChunkCellDiff.PollutionBit) != 0)
                {
                    chunk.Pollution[d.Index] = d.Pollution;
                }
            }
            chunk.ContentRevision++;
        }

        private void Touch(Chunk chunk)
        {
            if (!chunk.Modified)
            {
                chunk.BaseTerrain = (byte[])chunk.Terrain.Clone();
                chunk.BasePollution = (byte[])chunk.Pollution.Clone();
                chunk.Modified = true;
            }
            chunk.ContentRevision++;
            Revision++;
        }

        /// <summary>
        /// 存档：这张表面的全部区块差异——已加载且真的与基线不同的区块重新编码（改回原样的区块回到“未修改”、不再保存），
        /// 没加载的区块沿用读档时的差异。已生成但未修改的区块不输出（读档时按种子重新生成）。
        /// </summary>
        public void CollectDiffs(List<ChunkDiffRecord> into)
        {
            foreach (Chunk c in _chunks.Values)
            {
                if (!c.Modified)
                {
                    continue;
                }
                _diffScratch.Clear();
                for (int i = 0; i < c.Terrain.Length; i++)
                {
                    byte mask = 0;
                    if (c.Terrain[i] != c.BaseTerrain[i])
                    {
                        mask |= ChunkCellDiff.TerrainBit;
                    }
                    if (c.Pollution[i] != c.BasePollution[i])
                    {
                        mask |= ChunkCellDiff.PollutionBit;
                    }
                    if (mask != 0)
                    {
                        _diffScratch.Add(new ChunkCellDiff((ushort)i, mask, c.Terrain[i], c.Pollution[i]));
                    }
                }
                long key = Key(c.ChunkX, c.ChunkY);
                if (_diffScratch.Count == 0)
                {
                    c.Modified = false;
                    c.BaseTerrain = null;
                    c.BasePollution = null;
                    _savedDiffs.Remove(key);
                    continue;
                }
                _savedDiffs[key] = WorldDiffCodec.Encode(ChunkSize, _diffScratch);
            }
            foreach (KeyValuePair<long, string> kv in _savedDiffs)
            {
                Unkey(kv.Key, out int cx, out int cy);
                into.Add(new ChunkDiffRecord { SurfaceId = SurfaceId, ChunkX = cx, ChunkY = cy, DiffPayload = kv.Value });
            }
        }

        /// <summary>这张表面有差异的区块数（含未加载的）。</summary>
        public int SavedDiffCount => _savedDiffs.Count;

        // ── 地形 / 污染 ──────────────────────────────────────────────────────────

        public byte GetTerrain(GridCell cell) => ChunkAt(cell, out int i).Terrain[i];

        public byte GetPollution(GridCell cell) => ChunkAt(cell, out int i).Pollution[i];

        /// <summary>地形改造（FG07 净化、拆废墟……）的写入口：改一格地形。区块因此变为“已修改”，进存档差异。</summary>
        public void SetTerrain(GridCell cell, byte terrain)
        {
            Chunk c = ChunkAt(cell, out int i);
            if (c.Terrain[i] == terrain)
            {
                return;
            }
            Touch(c);
            c.Terrain[i] = terrain;
        }

        public void SetPollution(GridCell cell, byte level)
        {
            Chunk c = ChunkAt(cell, out int i);
            byte v = (byte)Math.Min(level, (byte)3);
            if (c.Pollution[i] == v)
            {
                return;
            }
            Touch(c);
            c.Pollution[i] = v;
        }

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
