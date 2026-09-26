using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace BinGames.Sim.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-050 / FGR-ARC-013）：生成一个区块的地形层与污染层。Burst 编译，在工作线程运行；
    /// 主线程只负责接入结果。整数运算，Burst 与托管 <see cref="Execute"/> 逐字节一致。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobGenerateChunk : IJob
    {
        public WorldGenParams Params;
        public int ChunkX;
        public int ChunkY;
        [ReadOnly] public NativeArray<WorldGenRect> Rects;
        public int RectCount;
        [ReadOnly] public NativeArray<WorldGenZone> Zones;
        public int ZoneCount;
        [WriteOnly] public NativeArray<byte> Terrain;
        [WriteOnly] public NativeArray<byte> Pollution;

        public void Execute()
        {
            int size = Params.ChunkSize;
            int baseX = ChunkX * size;
            int baseY = ChunkY * size;
            for (int ly = 0; ly < size; ly++)
            {
                for (int lx = 0; lx < size; lx++)
                {
                    WorldGenMath.Sample(in Params, baseX + lx, baseY + ly, Rects, RectCount, Zones, ZoneCount, out byte t, out byte p);
                    int i = ly * size + lx;
                    Terrain[i] = t;
                    Pollution[i] = p;
                }
            }
        }
    }

    /// <summary>建造模式地形叠加层一个区块贴图的输入（格网坐标，闭区间）。</summary>
    public struct TilePaintParams
    {
        public int ChunkSize;
        public int PixelsPerCell;
        public int BaseX;
        public int BaseY;
        public int BlockLevel;
        public bool HasReserve;
        public int ReserveMinX;
        public int ReserveMinY;
        public int ReserveMaxX;
        public int ReserveMaxY;
        public int CoreMinX;
        public int CoreMinY;
        public int CoreMaxX;
        public int CoreMaxY;
    }

    /// <summary>
    /// FG0-ARCH-05：把一个区块的地形 / 污染 / 迷雾画成叠加层贴图（与 FG0-ARCH-04 的整张叠加层同一套图案：表颜色 + 图案、
    /// 污染紫色斜线、核心通道黄框、格线、迷雾变暗）。逐像素的循环放在 Burst 工作线程，主线程只上传贴图。
    /// 图案码：0 无、1 斜线、2 点、3 波纹、4 叉、5 方格（与 fg.TbGridTerrain.pattern 对应）。
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct JobPaintTile : IJob
    {
        public TilePaintParams Params;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> Pollution;
        [ReadOnly] public NativeArray<byte> Explored;
        [ReadOnly] public NativeArray<Color32> Palette;
        [ReadOnly] public NativeArray<byte> Patterns;
        [WriteOnly] public NativeArray<Color32> Pixels;

        public void Execute()
        {
            TilePaintParams q = Params;
            int n = q.PixelsPerCell;
            int size = q.ChunkSize * n;
            for (int cy = 0; cy < q.ChunkSize; cy++)
            {
                for (int cx = 0; cx < q.ChunkSize; cx++)
                {
                    int i = cy * q.ChunkSize + cx;
                    byte t = Terrain[i];
                    Color32 baseColor = Palette[t];
                    byte pattern = Patterns[t];
                    bool explored = Explored[i] != 0;
                    int pollution = Pollution[i];
                    int gx = q.BaseX + cx;
                    int gy = q.BaseY + cy;
                    bool reserve = q.HasReserve
                                   && gx >= q.ReserveMinX && gx <= q.ReserveMaxX && gy >= q.ReserveMinY && gy <= q.ReserveMaxY
                                   && !(gx >= q.CoreMinX && gx <= q.CoreMaxX && gy >= q.CoreMinY && gy <= q.CoreMaxY);
                    for (int py = 0; py < n; py++)
                    {
                        for (int px = 0; px < n; px++)
                        {
                            Color32 col = baseColor;
                            if (PatternPixel(pattern, px, py, n))
                            {
                                col = Shade(col, 140); // ≈0.55
                            }
                            if (pollution > 0 && (px + n - 1 - py) % (pollution >= q.BlockLevel ? 2 : 3) == 0)
                            {
                                col = new Color32(150, 40, 150, 255);
                            }
                            if (reserve && (px == 0 || py == 0 || px == n - 1 || py == n - 1))
                            {
                                col = new Color32(230, 200, 40, 255);
                            }
                            else if (px == 0 || py == 0)
                            {
                                col = Shade(col, 204); // ≈0.8 格线
                            }
                            if (!explored)
                            {
                                col = Shade(col, 64); // ≈0.25 迷雾
                            }
                            Pixels[(cy * n + py) * size + cx * n + px] = col;
                        }
                    }
                }
            }
        }

        private static bool PatternPixel(byte pattern, int x, int y, int n)
        {
            switch (pattern)
            {
                case 1: return (x + y) % 3 == 0;
                case 2: return (x == n / 2 || x == n / 2 - 1) && (y == n / 2 || y == n / 2 - 1);
                case 3: return y == n / 2 + ((x / 2) % 2 == 0 ? 0 : 1);
                case 4: return x == y || x == n - 1 - y;
                case 5: return x % 3 == 1 || y % 3 == 1;
                default: return false;
            }
        }

        private static Color32 Shade(Color32 c, int k256) =>
            new Color32((byte)(c.r * k256 >> 8), (byte)(c.g * k256 >> 8), (byte)(c.b * k256 >> 8), c.a);
    }
}
