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

        /// <summary>FG3-GEN-01（FGR-GEN-031 第 1 级）：核心欧氏半径内没有悬崖、污染不超过 <see cref="FlatPollutionCap"/>。0 = 不启用（v1）。</summary>
        public int FlatRadius;
        public int FlatPollutionCap;

        // 地形字节值（fg.TbGridTerrain.code）
        public byte CodeBuildable;
        public byte CodeCliff;
        public byte CodeWater;
        public byte CodeOil;
        public byte CodeOreMetal;
        public byte CodeOreRare;
        public byte CodeRuin;
    }

    /// <summary>
    /// 规划层给出的一个形状（格网坐标）。<see cref="Kind"/> = 0 是 FG0-ARCH-05 的领地（圆 + 外圈危害带）；
    /// FG3-GEN-01（生成器 v2）起还有河流段、矿带、起始区保证点（见 <see cref="WorldGenZoneKind"/>）。v1 世界只有领地，采样结果不变。
    /// </summary>
    public struct WorldGenZone
    {
        public int CenterX;
        public int CenterY;
        public int Radius;
        public int HazardWidth;

        /// <summary>形状种类（<see cref="WorldGenZoneKind"/>）；默认 0 = 领地。</summary>
        public int Kind;
        /// <summary>线段形状（河流段、矿带）的终点；起点是 (CenterX, CenterY)。</summary>
        public int X1;
        public int Y1;
        /// <summary>矿带 / 保证点的地形码。</summary>
        public int Code;
        /// <summary>矿带：阈值下降量（16.16）；河流：浅滩周期（格）。</summary>
        public int ValueQ;
        /// <summary>河流：本段起点之前的累计河长（格，浅滩沿整条河连续）；浅滩宽度写在 <see cref="HazardWidth"/>。</summary>
        public int Extra;
        /// <summary>包围盒（非领地形状的快速排除，闭区间）。</summary>
        public int BoxMinX;
        public int BoxMinY;
        public int BoxMaxX;
        public int BoxMaxY;
    }

    /// <summary>规划层形状种类（FG3-GEN-01）。</summary>
    public static class WorldGenZoneKind
    {
        /// <summary>阵营领地 / 白潮滩头预留区（圆 + 外圈危害带）：只影响污染。</summary>
        public const int Territory = 0;
        /// <summary>河流的一段（胶囊体，半宽 = Radius）：水面，每隔 ValueQ 格有 HazardWidth 宽的浅滩（可建可走）。</summary>
        public const int River = 1;
        /// <summary>矿带（胶囊体，半宽 = Radius）：对应矿种的噪声阈值下降 ValueQ。</summary>
        public const int Belt = 2;
        /// <summary>起始区保证点（圆盘）：强制为 Code 地形（FGR-GEN-031 局部重生成）。</summary>
        public const int Stamp = 3;
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

            // FG3-GEN-01：规划层的矿带（金属 / 稀土各自的阈值下降）、河流、保证点。v1 世界没有这些形状，以下全部是空操作。
            int metalT = oreT;
            int rareBeltT = rareT;
            bool river = false;
            bool ford = false;
            int stampCode = -1;
            for (int i = 0; i < zoneCount; i++)
            {
                WorldGenZone z = zones[i];
                if (z.Kind == WorldGenZoneKind.Territory || x < z.BoxMinX || x > z.BoxMaxX || y < z.BoxMinY || y > z.BoxMaxY)
                {
                    continue;
                }
                if (z.Kind == WorldGenZoneKind.Stamp)
                {
                    long sx = x - z.CenterX;
                    long sy = y - z.CenterY;
                    if (sx * sx + sy * sy <= (long)z.Radius * z.Radius)
                    {
                        stampCode = z.Code;
                    }
                    continue;
                }
                if (!InCapsule(x, y, in z, out long along))
                {
                    continue;
                }
                if (z.Kind == WorldGenZoneKind.Belt)
                {
                    if (z.Code == p.CodeOreRare)
                    {
                        rareBeltT = math.max(One / 2, rareBeltT - z.ValueQ);
                    }
                    else
                    {
                        metalT = math.max(One / 2, metalT - z.ValueQ);
                    }
                }
                else if (z.Kind == WorldGenZoneKind.River)
                {
                    river = true;
                    int period = math.max(2, z.ValueQ);
                    long s = along + z.Extra;
                    if (s >= 0 && s % period < z.HazardWidth)
                    {
                        ford = true;
                    }
                }
            }
            bool flat = p.FlatRadius > 0 && dist <= p.FlatRadius;

            if (!flat && Noise(seed, x, y, 1u, p.ScaleCliffQ) > p.CliffT)
            {
                terrain = p.CodeCliff;
            }
            else if (Noise(seed, x, y, 2u, p.ScaleCliffQ) > p.WaterT)
            {
                terrain = p.CodeWater;
            }
            else if (Noise(seed, x, y, 3u, p.ScaleOreQ) > metalT)
            {
                terrain = p.CodeOreMetal;
            }
            else if (Noise(seed, x, y, 4u, p.ScaleRareQ) > rareBeltT)
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
            if (river)
            {
                // 河道是水面；浅滩是可走可建的河床（保证领地之间可达，FGT-GEN-004）。浅滩也清掉河道里的悬崖。
                terrain = ford ? p.CodeBuildable : p.CodeWater;
            }
            if (stampCode >= 0)
            {
                terrain = (byte)stampCode; // 起始区保证点（FGR-GEN-031 局部重生成）：只覆盖这一项的圆盘。
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
                if (z.Kind != WorldGenZoneKind.Territory)
                {
                    continue;
                }
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
            if (flat)
            {
                level = math.min(level, p.FlatPollutionCap); // FGR-GEN-031 第 1 级：起始区核心附近污染不超过上限。
            }
            pollution = (byte)math.clamp(level, 0, 3);
        }

        /// <summary>
        /// 点到线段（(CenterX, CenterY) → (X1, Y1)）的距离是否 ≤ 半宽 Radius（胶囊体），全整数运算。
        /// <paramref name="along"/> = 点在线段上的投影长度（格，向下取整，可能为负 / 超过段长），河流浅滩用。
        /// </summary>
        public static bool InCapsule(int x, int y, in WorldGenZone z, out long along)
        {
            long ax = z.CenterX;
            long ay = z.CenterY;
            long vx = z.X1 - ax;
            long vy = z.Y1 - ay;
            long px = x - ax;
            long py = y - ay;
            long len2 = vx * vx + vy * vy;
            long r2 = (long)z.Radius * z.Radius;
            long dot = px * vx + py * vy;
            long len = len2 > 0 ? Isqrt(len2) : 0;
            along = len > 0 ? FloorDiv(dot, len) : 0;
            if (len2 == 0 || dot <= 0)
            {
                return px * px + py * py <= r2;
            }
            if (dot >= len2)
            {
                long ex = x - z.X1;
                long ey = y - z.Y1;
                return ex * ex + ey * ey <= r2;
            }
            long cross = px * vy - py * vx;
            // 垂距² = cross² / len2 ≤ r² ⇔ cross² ≤ r² × len2（坐标在 ±1,000,000 以内、段长 ≤ 几千格时不溢出：cross ≤ 2e6 × 5e3 = 1e10，平方 1e20 会溢出 long，
            // 所以先比较 |cross| 与 r × len 的上界，再精确比较）。
            long bound = z.Radius * (len + 1);
            if (cross > bound || cross < -bound)
            {
                return false;
            }
            return cross * cross <= r2 * len2;
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
