using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.WorldGen
{
    /// <summary>表面种类（FGR-GEN-010）。</summary>
    public enum WorldSurfaceKind : byte
    {
        /// <summary>连续、无限的主地图（星球）。</summary>
        Planet = 0,
        /// <summary>从地表入口进入的有限小地图（室内场地）。</summary>
        Interior = 1,
    }

    /// <summary>
    /// FG0-ARCH-05：一次区块生成的全部输入（blittable，Burst 可用）。全部是整数与 16.16 定点数——
    /// 同一输入在 Burst 工作线程、Burst 主线程（Run）、托管代码（Execute）三条路径上逐字节一致，与平台无关。
    /// 由热更层 WorldGenService 从（种子, 生成器版本, 世界设置, 表面）组装；字段含义见 fg.TbWorldGenVersion。
    /// </summary>
    public struct WorldGenParams
    {
        public int Version;
        public WorldSurfaceKind Kind;
        public uint SurfaceSeed;
        public int ChunkSize;
        public int CoreX;
        public int CoreY;
        /// <summary>室内表面的宽高（格）；星球为 0。</summary>
        public int InteriorWidth;
        public int InteriorHeight;

        // 噪声格距（16.16 定点，单位：格）
        public int ScaleCliffQ;
        public int ScaleOreQ;
        public int ScaleRareQ;
        public int ScaleRuinQ;
        public int ScaleOilQ;
        public int ScalePollutionQ;

        // 阈值（16.16 定点，0～65536）
        public int CliffT;
        public int WaterT;
        public int OreT;
        public int RareBiasQ;
        public int OilBiasQ;
        public int PollutionT;
        public int InteriorRuinT;

        // 距离项（格）
        public int PollutionDistStart;
        public int PollutionDistPerLevel;
        /// <summary>世界设置“污染强度”倍率（16.16）。</summary>
        public int PollutionIntensityQ;
        public int ResourcePerStep;
        public int ResourceStepQ;
        public int ResourceMaxSteps;

        public int TerritoryBonus;
        public int HazardPollution;
        public int PillarSpacing;

        // 地形字节值（fg.TbGridTerrain.code）
        public byte CodeBuildable;
        public byte CodeCliff;
        public byte CodeWater;
        public byte CodeOil;
        public byte CodeOreMetal;
        public byte CodeOreRare;
        public byte CodeRuin;
    }

    /// <summary>规划层给出的一块领地（圆 + 外圈危害带），格网坐标。</summary>
    public struct WorldGenZone
    {
        public int CenterX;
        public int CenterY;
        public int Radius;
        public int HazardWidth;
    }

    /// <summary>强制为可建空地的矩形（起始区保护），闭区间。</summary>
    public struct WorldGenRect
    {
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;

        public WorldGenRect(int minX, int minY, int maxX, int maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }
    }

    /// <summary>
    /// FG0-ARCH-05：世界生成的纯函数（整数哈希 + 16.16 定点值噪声）。只依赖参数与格子坐标，与调用顺序、线程、
    /// 访问历史无关（FGR-GEN-002）；不读任何玩法随机流（FGR-GEN-003）。Burst 与托管代码共用同一份实现。
    /// </summary>
    public static class WorldGenMath
    {
        public const int One = 65536;

        public static uint Hash(uint seed, int x, int y, uint salt)
        {
            unchecked
            {
                uint h = seed * 0x9E3779B1u ^ salt * 0x85EBCA77u;
                h ^= (uint)x * 0xC2B2AE3Du;
                h = ((h << 13) | (h >> 19)) * 0x27D4EB2Fu;
                h ^= (uint)y * 0x165667B1u;
                h = ((h << 17) | (h >> 15)) * 0x9E3779B1u;
                h ^= h >> 15;
                h *= 0x85EBCA77u;
                h ^= h >> 13;
                h *= 0xC2B2AE3Du;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>向下取整的整数除法（负数也向下取整）。</summary>
        public static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0)))
            {
                q--;
            }
            return q;
        }

        /// <summary>精确的整数平方根（floor(sqrt(n))）。先用 IEEE 双精度开方（正确舍入、各平台一致）估计，再整数修正。</summary>
        public static int Isqrt(long n)
        {
            if (n <= 0)
            {
                return 0;
            }
            long x = (long)math.sqrt((double)n);
            while (x * x > n)
            {
                x--;
            }
            while ((x + 1) * (x + 1) <= n)
            {
                x++;
            }
            return (int)x;
        }

        private static int Smooth(int t)
        {
            long t2 = ((long)t * t) >> 16;
            return (int)((t2 * (3L * One - 2L * t)) >> 16);
        }

        private static int Lattice(uint seed, int x, int y, uint salt) => (int)(Hash(seed, x, y, salt) & 0xFFFFu);

        /// <summary>值噪声，返回 16.16 定点 [0, 1)。<paramref name="scaleQ"/> = 格距 × 65536。</summary>
        public static int Noise(uint seed, int x, int y, uint salt, int scaleQ)
        {
            long qx = FloorDiv((long)x << 32, scaleQ);
            long qy = FloorDiv((long)y << 32, scaleQ);
            int x0 = (int)(qx >> 16);
            int y0 = (int)(qy >> 16);
            int tx = Smooth((int)(qx & 0xFFFF));
            int ty = Smooth((int)(qy & 0xFFFF));
            int a = Lattice(seed, x0, y0, salt);
            int b = Lattice(seed, x0 + 1, y0, salt);
            int c = Lattice(seed, x0, y0 + 1, salt);
            int d = Lattice(seed, x0 + 1, y0 + 1, salt);
            int ab = a + (int)(((long)(b - a) * tx) >> 16);
            int cd = c + (int)(((long)(d - c) * tx) >> 16);
            return ab + (int)(((long)(cd - ab) * ty) >> 16);
        }

        /// <summary>一个格子的地形与污染。星球：起始区保护 → 地形特征 → 污染（噪声 + 距离 + 领地 / 危害带）；室内：墙、入口、立柱、废墟。</summary>
        public static void Sample(in WorldGenParams p, int x, int y,
            NativeArray<WorldGenRect> rects, int rectCount, NativeArray<WorldGenZone> zones, int zoneCount,
            out byte terrain, out byte pollution)
        {
            if (p.Kind == WorldSurfaceKind.Interior)
            {
                SampleInterior(in p, x, y, out terrain);
                pollution = 0;
                return;
            }

            terrain = p.CodeBuildable;
            pollution = 0;
            for (int i = 0; i < rectCount; i++)
            {
                WorldGenRect r = rects[i];
                if (x >= r.MinX && x <= r.MaxX && y >= r.MinY && y <= r.MaxY)
                {
                    return; // 起始区保护：可建空地、无污染。
                }
            }

            uint seed = p.SurfaceSeed;
            long dx = x - p.CoreX;
            long dy = y - p.CoreY;
            int dist = Isqrt(dx * dx + dy * dy);

            // 离核心越远资源越丰富（FGR-GEN-032）：矿阈值按距离分档下降。
            int steps = p.ResourcePerStep > 0 ? math.min(p.ResourceMaxSteps, dist / p.ResourcePerStep) : 0;
            int oreT = math.max(One / 2, p.OreT - steps * p.ResourceStepQ);
            int rareT = oreT + (int)(((long)(One - oreT) * p.RareBiasQ) >> 16);
            int oilT = oreT + (int)(((long)(One - oreT) * p.OilBiasQ) >> 16);

            if (Noise(seed, x, y, 1u, p.ScaleCliffQ) > p.CliffT)
            {
                terrain = p.CodeCliff;
            }
            else if (Noise(seed, x, y, 2u, p.ScaleCliffQ) > p.WaterT)
            {
                terrain = p.CodeWater;
            }
            else if (Noise(seed, x, y, 3u, p.ScaleOreQ) > oreT)
            {
                terrain = p.CodeOreMetal;
            }
            else if (Noise(seed, x, y, 4u, p.ScaleRareQ) > rareT)
            {
                terrain = p.CodeOreRare;
            }
            else if (Noise(seed, x, y, 5u, p.ScaleRuinQ) > oreT)
            {
                terrain = p.CodeRuin;
            }
            else if (Noise(seed, x, y, 6u, p.ScaleOilQ) > oilT)
            {
                terrain = p.CodeOil;
            }

            // 污染：局部浓淡（噪声）+ 离核心越远越高 + 阵营领地内部加重 + 外圈危害带（FGR-GEN-030、021）。
            int level = 0;
            int n = Noise(seed, x, y, 7u, p.ScalePollutionQ);
            if (n > p.PollutionT)
            {
                level = 1 + (int)((long)(n - p.PollutionT) * 3 / math.max(1, One - p.PollutionT));
                level = math.clamp(level, 1, 3);
            }
            if (dist > p.PollutionDistStart && p.PollutionDistPerLevel > 0)
            {
                long over = ((long)(dist - p.PollutionDistStart) * p.PollutionIntensityQ) >> 16;
                level += (int)math.min(3L, over / p.PollutionDistPerLevel);
            }
            for (int i = 0; i < zoneCount; i++)
            {
                WorldGenZone z = zones[i];
                long zx = x - z.CenterX;
                long zy = y - z.CenterY;
                long d2 = zx * zx + zy * zy;
                long r = z.Radius;
                if (d2 <= r * r)
                {
                    level += p.TerritoryBonus;
                }
                else if (z.HazardWidth > 0)
                {
                    long outer = r + z.HazardWidth;
                    if (d2 <= outer * outer)
                    {
                        level = math.max(level, p.HazardPollution);
                    }
                }
            }
            pollution = (byte)math.clamp(level, 0, 3);
        }

        private static void SampleInterior(in WorldGenParams p, int x, int y, out byte terrain)
        {
            int w = p.InteriorWidth;
            int h = p.InteriorHeight;
            if (x < 0 || y < 0 || x >= w || y >= h)
            {
                terrain = p.CodeCliff; // 室外是虚空：不可建、不可达。
                return;
            }
            int half = w / 2;
            bool entrance = y == 0 && x >= half - 1 && x <= half + 1;
            if (!entrance && (x == 0 || y == 0 || x == w - 1 || y == h - 1))
            {
                terrain = p.CodeCliff; // 墙
                return;
            }
            int s = math.max(4, p.PillarSpacing);
            int px = x % s;
            int py = y % s;
            int c = s / 2;
            bool nearEntrance = y < s && x >= half - s && x <= half + s;
            if (!nearEntrance && (px == c || px == c - 1) && (py == c || py == c - 1) && x > 1 && y > 1 && x < w - 2 && y < h - 2)
            {
                terrain = p.CodeCliff; // 立柱
                return;
            }
            terrain = Noise(p.SurfaceSeed, x, y, 5u, p.ScaleRuinQ) > p.InteriorRuinT ? p.CodeRuin : p.CodeBuildable;
        }
    }
}
