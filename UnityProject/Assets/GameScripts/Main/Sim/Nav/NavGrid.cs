using System;
using BinGames.Sim.WorldGen;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BinGames.Sim.Nav
{
    /// <summary>通行格网的标量（长度 1 的原生数组，作业与托管调用共用）。</summary>
    public struct NavGridScalars
    {
        public WorldGenParams Gen;
        public int HasGen;
        public int RectCount;
        public int ZoneCount;
        public int CoordLimit;
        public long Generated;
    }

    /// <summary>
    /// FG0-ARCH-06：一张表面的通行格网（按区块存，每格一个字节，编码见 <see cref="NavConst"/>）。
    ///
    /// 两份实例：<b>镜像</b>（主线程：战斗内核的碰撞、路线失效检查、放置预览的可达性）与<b>工作副本</b>（只在寻路作业里读写）。
    /// 纯地形区块（没有被修改、没有建筑）不需要热更层推送：缺哪个区块就按（种子, 生成器版本, 世界设置）用世界生成的同一个纯函数
    /// （<see cref="WorldGenMath.Sample"/>）就地生成，结果与格网里的区块逐字节一致（FGR-GEN-002）。
    /// 被修改过或有建筑的区块由热更层推送（<see cref="NavGridOps.ComputeChunkCells"/>），此后以推送的为准。
    /// </summary>
    public struct NavGrid : IDisposable
    {
        public int ChunkSize;
        public int Cells;
        public NativeHashMap<long, int> Slots;
        public NativeList<long> SlotKeys;
        public NativeList<byte> Cell;
        /// <summary>地形码 → 通行字节（256 项）。</summary>
        public NativeArray<byte> TerrainCell;
        public NativeArray<WorldGenRect> Rects;
        public NativeArray<WorldGenZone> Zones;
        public NativeArray<NavGridScalars> S;

        public bool IsCreated => Slots.IsCreated;

        public static NavGrid Create(int chunkSize, byte[] terrainCell, bool hasGen, in WorldGenParams gen, WorldGenRect[] rects, WorldGenZone[] zones,
            int coordLimit, int capacity)
        {
            int n = chunkSize * chunkSize;
            var g = new NavGrid
            {
                ChunkSize = chunkSize,
                Cells = n,
                Slots = new NativeHashMap<long, int>(math.max(16, capacity), Allocator.Persistent),
                SlotKeys = new NativeList<long>(math.max(16, capacity), Allocator.Persistent),
                Cell = new NativeList<byte>(math.max(16, capacity) * n, Allocator.Persistent),
                TerrainCell = new NativeArray<byte>(256, Allocator.Persistent),
                Rects = new NativeArray<WorldGenRect>(math.max(1, rects?.Length ?? 0), Allocator.Persistent),
                Zones = new NativeArray<WorldGenZone>(math.max(1, zones?.Length ?? 0), Allocator.Persistent),
                S = new NativeArray<NavGridScalars>(1, Allocator.Persistent),
            };
            for (int i = 0; i < 256; i++)
            {
                g.TerrainCell[i] = terrainCell != null && i < terrainCell.Length ? terrainCell[i] : NavConst.Solid;
            }
            if (rects != null)
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    g.Rects[i] = rects[i];
                }
            }
            if (zones != null)
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    g.Zones[i] = zones[i];
                }
            }
            g.S[0] = new NavGridScalars
            {
                Gen = gen,
                HasGen = hasGen ? 1 : 0,
                RectCount = rects?.Length ?? 0,
                ZoneCount = zones?.Length ?? 0,
                CoordLimit = coordLimit,
            };
            return g;
        }

        public void Dispose()
        {
            if (Slots.IsCreated)
            {
                Slots.Dispose();
            }
            if (SlotKeys.IsCreated)
            {
                SlotKeys.Dispose();
            }
            if (Cell.IsCreated)
            {
                Cell.Dispose();
            }
            if (TerrainCell.IsCreated)
            {
                TerrainCell.Dispose();
            }
            if (Rects.IsCreated)
            {
                Rects.Dispose();
            }
            if (Zones.IsCreated)
            {
                Zones.Dispose();
            }
            if (S.IsCreated)
            {
                S.Dispose();
            }
        }

        public int ChunkCount => SlotKeys.Length;
    }

    /// <summary>通行格网的全部读写（Burst 与托管共用；与 HomeGridMap.Key 同一区块键）。</summary>
    public static class NavGridOps
    {
        public static long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public static void Unkey(long key, out int cx, out int cy)
        {
            cx = (int)(key >> 32);
            cy = (int)(uint)key;
        }

        public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        public static bool IsBlockedClass(byte cell, int cls) => (cell & (1 << cls)) != 0;

        public static int CostOf(byte cell) => math.max(1, cell >> 4);

        public static bool InWorld(ref NavGrid g, int x, int y)
        {
            int lim = g.S[0].CoordLimit;
            return lim <= 0 || (x >= -lim && x <= lim && y >= -lim && y <= lim);
        }

        /// <summary>区块的槽位；没有时按种子生成（没有生成参数时返回 -1 = 地形未知）。</summary>
        public static int EnsureSlot(ref NavGrid g, int cx, int cy)
        {
            long key = Key(cx, cy);
            if (g.Slots.TryGetValue(key, out int slot))
            {
                return slot;
            }
            NavGridScalars s = g.S[0];
            if (s.HasGen == 0)
            {
                return -1;
            }
            slot = g.SlotKeys.Length;
            g.SlotKeys.Add(key);
            int baseIndex = slot * g.Cells;
            g.Cell.ResizeUninitialized(baseIndex + g.Cells);
            int size = g.ChunkSize;
            int bx = cx * size;
            int by = cy * size;
            for (int ly = 0; ly < size; ly++)
            {
                for (int lx = 0; lx < size; lx++)
                {
                    int x = bx + lx;
                    int y = by + ly;
                    byte c;
                    if (s.CoordLimit > 0 && (x < -s.CoordLimit || x > s.CoordLimit || y < -s.CoordLimit || y > s.CoordLimit))
                    {
                        c = NavConst.Solid;
                    }
                    else
                    {
                        WorldGenMath.Sample(in s.Gen, x, y, g.Rects, s.RectCount, g.Zones, s.ZoneCount, out byte terrain, out _);
                        c = g.TerrainCell[terrain];
                    }
                    g.Cell[baseIndex + ly * size + lx] = c;
                }
            }
            g.Slots.Add(key, slot);
            s.Generated++;
            g.S[0] = s;
            return slot;
        }

        public static bool IsKnown(ref NavGrid g, int cx, int cy) => g.Slots.ContainsKey(Key(cx, cy)) || g.S[0].HasGen != 0;

        /// <summary>格子的通行字节（必要时生成区块）。超出世界 / 地形未知 = 完全不能走。</summary>
        public static byte CellAt(ref NavGrid g, int x, int y)
        {
            if (!InWorld(ref g, x, y))
            {
                return NavConst.Solid;
            }
            int size = g.ChunkSize;
            int cx = FloorDiv(x, size);
            int cy = FloorDiv(y, size);
            int slot = EnsureSlot(ref g, cx, cy);
            if (slot < 0)
            {
                return NavConst.Solid;
            }
            return g.Cell[slot * g.Cells + (y - cy * size) * size + (x - cx * size)];
        }

        public static bool Passable(ref NavGrid g, int x, int y, int cls) => !IsBlockedClass(CellAt(ref g, x, y), cls);

        public static bool Passable(ref NavGrid g, int2 c, int cls) => Passable(ref g, c.x, c.y, cls);

        /// <summary>写入 / 覆盖一个区块（主线程推送或镜像 → 工作副本）。返回内容是否与原来不同（原来没有也算不同）。</summary>
        public static bool SetChunk(ref NavGrid g, long key, NativeArray<byte> cells, int offset)
        {
            if (!g.Slots.TryGetValue(key, out int slot))
            {
                slot = g.SlotKeys.Length;
                g.SlotKeys.Add(key);
                g.Cell.ResizeUninitialized((slot + 1) * g.Cells);
                g.Slots.Add(key, slot);
                for (int i = 0; i < g.Cells; i++)
                {
                    g.Cell[slot * g.Cells + i] = cells[offset + i];
                }
                return true;
            }
            bool changed = false;
            int b = slot * g.Cells;
            for (int i = 0; i < g.Cells; i++)
            {
                byte v = cells[offset + i];
                if (g.Cell[b + i] != v)
                {
                    g.Cell[b + i] = v;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>从另一份格网复制一个区块（镜像 → 工作副本）。源里没有时什么也不做。</summary>
        public static bool CopyChunk(ref NavGrid from, ref NavGrid to, long key)
        {
            if (!from.Slots.TryGetValue(key, out int src))
            {
                return false;
            }
            return SetChunk(ref to, key, from.Cell.AsArray(), src * from.Cells);
        }

        /// <summary>
        /// 由格网区块的地形层 + 占用层算出通行字节（Burst）：地形码查表；有建筑占地的格子按占用者的挡路位追加（建筑挡住全部类别；
        /// FG06 的闸门只挡敌方，由 <paramref name="occupantBlock"/> 的位区分）。超出世界坐标上限的格子完全不能走。
        /// </summary>
        public static void ComputeChunkCells(NativeArray<byte> terrainCell, NativeArray<byte> terrain, NativeArray<int> occupancy, NativeArray<byte> occupantBlock,
            int occupantCount, int chunkSize, int cx, int cy, int coordLimit, NativeArray<byte> outCells)
        {
            int n = chunkSize * chunkSize;
            int bx = cx * chunkSize;
            int by = cy * chunkSize;
            for (int i = 0; i < n; i++)
            {
                int x = bx + i % chunkSize;
                int y = by + i / chunkSize;
                if (coordLimit > 0 && (x < -coordLimit || x > coordLimit || y < -coordLimit || y > coordLimit))
                {
                    outCells[i] = NavConst.Solid;
                    continue;
                }
                byte c = terrainCell[terrain[i]];
                int occ = occupancy[i];
                if (occ > 0)
                {
                    byte bits = occ <= occupantCount ? occupantBlock[occ - 1] : NavConst.BlockAll;
                    c = (byte)(c | (bits & NavConst.BlockAll));
                }
                outCells[i] = c;
            }
        }
    }

    /// <summary>主线程推送一个区块时的计算（Burst，Run）。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct NavComputeChunkJob : IJob
    {
        [ReadOnly] public NativeArray<byte> TerrainCell;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<int> Occupancy;
        [ReadOnly] public NativeArray<byte> OccupantBlock;
        public int OccupantCount;
        public int ChunkSize;
        public int ChunkX;
        public int ChunkY;
        public int CoordLimit;
        public NativeArray<byte> Out;

        public void Execute()
        {
            NavGridOps.ComputeChunkCells(TerrainCell, Terrain, Occupancy, OccupantBlock, OccupantCount, ChunkSize, ChunkX, ChunkY, CoordLimit, Out);
        }
    }
}
