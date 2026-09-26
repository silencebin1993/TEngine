using System;
using GameLogic.Campaign.Grid;
using UnityEngine;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-051）：无缝大地图上的坐标 =“区块索引 + 区块内偏移”，不用单个浮点数表示远处的位置。
    ///
    /// - 格子级：区块索引（int）+ 区块内格偏移（0～区块边长-1）；格子坐标本身也是 int，±1,000,000 内精确。
    /// - 格内位置：<see cref="SubX"/>/<see cref="SubY"/>（0～1 的浮点，只表示格内的一小段，所以永远有足够精度）。
    /// - 画面：<see cref="ToRenderPosition"/> 相对一个“原点区块”换算成 float——离原点区块近的东西精度都在毫米级；
    ///   镜头原点的浮动（floating origin）由 FG0-ARCH-01 的全局镜头管理器接上（见 ADR-ARC-013）。
    /// - 上限：离原点超过 world.coord_limit 格的位置不可达（<see cref="WithinLimit"/>）；超出时给出“超出世界范围”的原因。
    /// </summary>
    public readonly struct WorldCoord : IEquatable<WorldCoord>
    {
        public readonly int ChunkX;
        public readonly int ChunkY;
        public readonly int LocalX;
        public readonly int LocalY;
        public readonly float SubX;
        public readonly float SubY;

        public WorldCoord(int chunkX, int chunkY, int localX, int localY, float subX = 0f, float subY = 0f)
        {
            ChunkX = chunkX;
            ChunkY = chunkY;
            LocalX = localX;
            LocalY = localY;
            SubX = subX;
            SubY = subY;
        }

        /// <summary>格子 → 区块索引 + 区块内偏移（负坐标向下取整）。</summary>
        public static WorldCoord FromCell(GridCell cell, int chunkSize, float subX = 0f, float subY = 0f)
        {
            ChunkAddress a = GridMath.Address(cell, chunkSize);
            return new WorldCoord(a.ChunkX, a.ChunkY, a.LocalX, a.LocalY, subX, subY);
        }

        /// <summary>双精度世界位置（x, z）→ 坐标。格子取 floor(x + 0.5)（格心在整数处），余下的写进格内偏移。</summary>
        public static WorldCoord FromWorld(double x, double z, int chunkSize)
        {
            double fx = Math.Floor(x + 0.5);
            double fz = Math.Floor(z + 0.5);
            return FromCell(new GridCell((int)fx, (int)fz), chunkSize, (float)(x - fx), (float)(z - fz));
        }

        public GridCell Cell(int chunkSize) => new GridCell(ChunkX * chunkSize + LocalX, ChunkY * chunkSize + LocalY);

        /// <summary>相对原点区块的画面位置（float，精度只取决于离原点区块的距离，与离世界原点多远无关）。</summary>
        public Vector3 ToRenderPosition(int originChunkX, int originChunkY, int chunkSize, float height = 0f)
        {
            long dx = (long)(ChunkX - originChunkX) * chunkSize + LocalX;
            long dy = (long)(ChunkY - originChunkY) * chunkSize + LocalY;
            return new Vector3((float)dx + SubX, height, (float)dy + SubY);
        }

        /// <summary>格子在世界坐标上限内（|x|、|y| 都不超过 <paramref name="limit"/>）。</summary>
        public static bool WithinLimit(GridCell cell, int limit) =>
            cell.X >= -limit && cell.X <= limit && cell.Y >= -limit && cell.Y <= limit;

        public bool Equals(WorldCoord o) => ChunkX == o.ChunkX && ChunkY == o.ChunkY && LocalX == o.LocalX && LocalY == o.LocalY
                                            && SubX.Equals(o.SubX) && SubY.Equals(o.SubY);

        public override bool Equals(object obj) => obj is WorldCoord c && Equals(c);

        public override int GetHashCode() => unchecked(((ChunkX * 73856093) ^ (ChunkY * 19349663)) + LocalX * 83492791 + LocalY);

        public override string ToString() => $"[{ChunkX},{ChunkY}]+({LocalX},{LocalY})";
    }
}
