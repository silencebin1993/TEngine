using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>格子坐标（1 格 = 1 米；格子 (X, Y) 的中心在世界 (X, 0, Y)，+Y = 北 = 世界 +Z）。</summary>
    [Serializable]
    public readonly struct GridCell : IEquatable<GridCell>
    {
        public readonly int X;
        public readonly int Y;

        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridCell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridCell c && Equals(c);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public override string ToString() => $"({X},{Y})";
        public static bool operator ==(GridCell a, GridCell b) => a.Equals(b);
        public static bool operator !=(GridCell a, GridCell b) => !a.Equals(b);

        /// <summary>世界 XZ 平面上的点 → 所在格子（四舍五入到最近的格心）。</summary>
        public static GridCell FromWorld(Vector2 xz) => new GridCell(Mathf.RoundToInt(xz.x), Mathf.RoundToInt(xz.y));

        public Vector2 ToWorld() => new Vector2(X, Y);
    }

    /// <summary>朝向（端口朝外的方向）。按顺时针排列，旋转 90° = +1。</summary>
    public enum GridDir : byte
    {
        N = 0,
        E = 1,
        S = 2,
        W = 3,
    }

    /// <summary>区块坐标 + 区块内偏移（FGR-ARC-013“坐标用区块索引 + 区块内偏移”）。</summary>
    public readonly struct ChunkAddress
    {
        public readonly int ChunkX;
        public readonly int ChunkY;
        public readonly int LocalX;
        public readonly int LocalY;

        public ChunkAddress(int chunkX, int chunkY, int localX, int localY)
        {
            ChunkX = chunkX;
            ChunkY = chunkY;
            LocalX = localX;
            LocalY = localY;
        }
    }

    /// <summary>
    /// FG0-ARCH-04：格网几何的唯一实现——旋转、占地、端口朝向、区块寻址。放置校验、开局布局、可视化、存读档都经这里，
    /// 与 tools/cell_tables/check_luban.py 的 _footprint 算法逐行一致（生成前的表检查与运行时同一结论）。
    /// </summary>
    public static class GridMath
    {
        /// <summary>规范化到 0 / 90 / 180 / 270（非 90 的倍数按最近的 90 取整）。</summary>
        public static int NormalizeRotation(float degrees)
        {
            int steps = Mathf.RoundToInt(degrees / 90f);
            steps %= 4;
            if (steps < 0)
            {
                steps += 4;
            }
            return steps * 90;
        }

        public static int RotationSteps(int rotation) => NormalizeRotation(rotation) / 90;

        /// <summary>局部偏移顺时针旋转：90° 时 (x, y) → (y, -x)。</summary>
        public static Vector2Int RotateOffset(int dx, int dy, int rotation)
        {
            int steps = RotationSteps(rotation);
            for (int i = 0; i < steps; i++)
            {
                int t = dx;
                dx = dy;
                dy = -t;
            }
            return new Vector2Int(dx, dy);
        }

        public static GridDir RotateDir(GridDir dir, int rotation) => (GridDir)(((int)dir + RotationSteps(rotation)) % 4);

        public static bool TryParseDir(string text, out GridDir dir)
        {
            switch (text)
            {
                case "N": dir = GridDir.N; return true;
                case "E": dir = GridDir.E; return true;
                case "S": dir = GridDir.S; return true;
                case "W": dir = GridDir.W; return true;
                default: dir = GridDir.N; return false;
            }
        }

        public static Vector2Int DirVector(GridDir dir)
        {
            switch (dir)
            {
                case GridDir.N: return new Vector2Int(0, 1);
                case GridDir.E: return new Vector2Int(1, 0);
                case GridDir.S: return new Vector2Int(0, -1);
                default: return new Vector2Int(-1, 0);
            }
        }

        public static string DirTextKey(GridDir dir)
        {
            switch (dir)
            {
                case GridDir.N: return "grid.dir.n";
                case GridDir.E: return "grid.dir.e";
                case GridDir.S: return "grid.dir.s";
                default: return "grid.dir.w";
            }
        }

        /// <summary>建筑“正面”朝向：旋转 0 时朝北，随旋转顺时针转。</summary>
        public static GridDir FacingOf(int rotation) => RotateDir(GridDir.N, rotation);

        /// <summary>枢轴格在占地里的局部位置：奇数尺寸时是正中格。</summary>
        public static Vector2Int Pivot(int w, int h) => new Vector2Int((w - 1) / 2, (h - 1) / 2);

        /// <summary>占地覆盖的全部格子（写进 <paramref name="into"/>，先清空）。O(w×h)。</summary>
        public static void FootprintCells(GridCell pivotCell, int w, int h, int rotation, List<GridCell> into)
        {
            into.Clear();
            Vector2Int p = Pivot(w, h);
            for (int lx = 0; lx < w; lx++)
            {
                for (int ly = 0; ly < h; ly++)
                {
                    Vector2Int d = RotateOffset(lx - p.x, ly - p.y, rotation);
                    into.Add(new GridCell(pivotCell.X + d.x, pivotCell.Y + d.y));
                }
            }
        }

        /// <summary>占地的包围盒（含端点）。</summary>
        public static void FootprintBounds(GridCell pivotCell, int w, int h, int rotation, out GridCell min, out GridCell max)
        {
            Vector2Int p = Pivot(w, h);
            Vector2Int a = RotateOffset(-p.x, -p.y, rotation);
            Vector2Int b = RotateOffset(w - 1 - p.x, h - 1 - p.y, rotation);
            min = new GridCell(pivotCell.X + Math.Min(a.x, b.x), pivotCell.Y + Math.Min(a.y, b.y));
            max = new GridCell(pivotCell.X + Math.Max(a.x, b.x), pivotCell.Y + Math.Max(a.y, b.y));
        }

        /// <summary>占地的几何中心（世界 XZ）。奇数尺寸时等于枢轴格中心；偶数尺寸时落在格线上。</summary>
        public static Vector2 FootprintCenter(GridCell pivotCell, int w, int h, int rotation)
        {
            FootprintBounds(pivotCell, w, h, rotation, out GridCell min, out GridCell max);
            return new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);
        }

        /// <summary>旋转后占地在世界里的尺寸（X 向、Z 向格数）。</summary>
        public static Vector2Int RotatedSize(int w, int h, int rotation) =>
            RotationSteps(rotation) % 2 == 0 ? new Vector2Int(w, h) : new Vector2Int(h, w);

        /// <summary>端口的世界格与朝外方向（局部格与方向随建筑旋转）。</summary>
        public static GridCell PortCell(GridCell pivotCell, int localX, int localY, int rotation)
        {
            Vector2Int d = RotateOffset(localX, localY, rotation);
            return new GridCell(pivotCell.X + d.x, pivotCell.Y + d.y);
        }

        public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        public static ChunkAddress Address(GridCell cell, int chunkSize)
        {
            int cx = FloorDiv(cell.X, chunkSize);
            int cy = FloorDiv(cell.Y, chunkSize);
            return new ChunkAddress(cx, cy, cell.X - cx * chunkSize, cell.Y - cy * chunkSize);
        }
    }
}
