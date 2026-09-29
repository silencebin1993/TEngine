using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>生成器分布的点位种类（FG3-GEN-01）。</summary>
    public enum WorldFeatureKind : byte
    {
        /// <summary>遗迹点（FGR-GEN-033）：全世界分布；手工人类遗产按幕分配到遗迹点属于 FG8（DEBT-FG3GEN01-04）。</summary>
        Relic = 1,
        /// <summary>零散的敌方侦察巢（FGR-GEN-030、032、034）：家园区（无据点半径之外、家园区半径之内），离核心越远越强。</summary>
        ScoutNest = 2,
    }

    /// <summary>一个生成器点位（纯函数结果：只取决于种子、版本、设置与分布格坐标，与访问顺序无关）。</summary>
    public sealed class WorldFeature
    {
        public string Id;
        public WorldFeatureKind Kind;
        public int X;
        public int Y;
        /// <summary>侦察巢等级（1 起，离核心越远越高）；遗迹点为 0。</summary>
        public int Tier;
        /// <summary>分布格坐标。</summary>
        public int CellX;
        public int CellY;
        /// <summary>原定位置落在悬崖 / 水面上，就近挪了位（FG17 第 5 节“锚点放置与悬崖、水域冲突 → 按规则挪位”）。</summary>
        public bool Shifted;
        public int Distance;
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-030、032、033、034）：遗迹点与零散侦察巢的分布。
    ///
    /// 世界按 poiCellSize（64）格划成分布格；每个分布格只由（种子, 分布格坐标）决定有没有遗迹点、有没有侦察巢、放在格里哪儿——
    /// 与玩家先去了哪里无关（FGR-GEN-002）。落点在悬崖 / 水面上时，在 poiShiftRadius 格内按固定的螺旋顺序找第一个可走的格挪过去，
    /// 找不到就不生成这个点位（写日志）。
    /// - 遗迹点：每格按 relicChance；避开起始区平地（离核心 32 格内不放）。
    /// - 侦察巢：只在家园区（离核心 [无据点半径, 家园区半径]，FGR-GEN-031 第 3 级“64 格内没有敌方建筑”）；每格按 nestChance × 世界设置“据点密度”；
    ///   等级 = 1 + (距离 − 无据点半径) / nestTierDistance（封顶 nestMaxTier），离核心越远越强（FGR-GEN-032）。
    ///   阵营领地里的主力据点、领地之间与外延区的零散前哨属于 FG8-GEN-02（DEBT-FG0ARCH06-01 剩余部分）。
    /// 版本行没有保证集合（v1）时不分布任何点位（旧世界不变）。
    /// </summary>
    public static class WorldFeatures
    {
        private const uint RelicSalt = 0x52454C43u; // "RELC"
        private const uint NestSalt = 0x4E455354u;  // "NEST"

        public static bool Enabled(WorldGenVersion v) => v != null && WorldGenContent.HasSet(v.GuaranteeSet) && v.PoiCellSize > 0;

        /// <summary>矩形 [minX..maxX]×[minY..maxY] 里的点位（按分布格行优先、格内遗迹点在前），追加到 <paramref name="into"/>。
        /// 开销 O(矩形覆盖的分布格数)，结果按上下文缓存。</summary>
        public static void Query(WorldGenContext ctx, int minX, int minY, int maxX, int maxY, List<WorldFeature> into)
        {
            if (ctx == null || !Enabled(ctx.Row))
            {
                return;
            }
            int c = ctx.Row.PoiCellSize;
            int mx0 = (int)WorldGenMath.FloorDiv(minX, c);
            int my0 = (int)WorldGenMath.FloorDiv(minY, c);
            int mx1 = (int)WorldGenMath.FloorDiv(maxX, c);
            int my1 = (int)WorldGenMath.FloorDiv(maxY, c);
            for (int my = my0; my <= my1; my++)
            {
                for (int mx = mx0; mx <= mx1; mx++)
                {
                    CellFeatures(ctx, mx, my, out WorldFeature relic, out WorldFeature nest);
                    if (relic != null && relic.X >= minX && relic.X <= maxX && relic.Y >= minY && relic.Y <= maxY)
                    {
                        into.Add(relic);
                    }
                    if (nest != null && nest.X >= minX && nest.X <= maxX && nest.Y >= minY && nest.Y <= maxY)
                    {
                        into.Add(nest);
                    }
                }
            }
        }

        /// <summary>家园区里的全部侦察巢（新战役把它们登记成据点）。</summary>
        public static List<WorldFeature> HomeZoneNests(WorldGenContext ctx)
        {
            var list = new List<WorldFeature>();
            if (ctx == null || !Enabled(ctx.Row))
            {
                return list;
            }
            int r = ctx.Row.HomeZoneRadius;
            var all = new List<WorldFeature>();
            Query(ctx, ctx.Core.X - r, ctx.Core.Y - r, ctx.Core.X + r, ctx.Core.Y + r, all);
            foreach (WorldFeature f in all)
            {
                if (f.Kind == WorldFeatureKind.ScoutNest)
                {
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>一个分布格的遗迹点与侦察巢（没有时为 null）。纯函数 + 缓存。</summary>
        public static void CellFeatures(WorldGenContext ctx, int mx, int my, out WorldFeature relic, out WorldFeature nest)
        {
            long key = ((long)mx << 32) | (uint)my;
            if (ctx.FeatureCache.TryGetValue(key, out (WorldFeature relic, WorldFeature nest) cached))
            {
                relic = cached.relic;
                nest = cached.nest;
                return;
            }
            WorldGenVersion v = ctx.Row;
            uint seed = unchecked((uint)ctx.Seed);
            int c = v.PoiCellSize;
            int margin = Math.Max(0, Math.Min(v.PoiEdgeMargin, c / 2 - 1));
            int span = Math.Max(1, c - 2 * margin);
            relic = null;
            nest = null;

            uint hr = WorldGenMath.Hash(seed, mx, my, RelicSalt);
            if ((hr & 0xFFFFu) < (uint)Math.Min(65536, WorldTerrainSource.Q(v.RelicChance)))
            {
                uint hp = WorldGenMath.Hash(seed, mx, my, RelicSalt ^ 0x9E3779B9u);
                int x = mx * c + margin + (int)((hr >> 16) % (uint)span);
                int y = my * c + margin + (int)((hp >> 16) % (uint)span);
                if (Distance(ctx, x, y) > v.RelicCoreExclusion && TryWalkable(ctx, ref x, ref y, out bool shifted))
                {
                    relic = new WorldFeature
                    {
                        Id = $"relic:{mx}:{my}", Kind = WorldFeatureKind.Relic, X = x, Y = y, CellX = mx, CellY = my, Shifted = shifted, Distance = Distance(ctx, x, y),
                    };
                }
            }

            int noOutpost = ctx.Plan?.StartReport?.NoOutpostRadius ?? WorldTerrainSource.ScaledRadius(v.StartNoOutpostRadius, ctx.Settings);
            uint hn = WorldGenMath.Hash(seed, mx, my, NestSalt);
            uint chanceQ = (uint)Math.Min(65536, (int)(((long)WorldTerrainSource.Q(v.NestChance) * WorldTerrainSource.Q(ctx.Settings?.OutpostDensity ?? 1f)) >> 16));
            if ((hn & 0xFFFFu) < chanceQ)
            {
                uint hp = WorldGenMath.Hash(seed, mx, my, NestSalt ^ 0x9E3779B9u);
                int x = mx * c + margin + (int)((hn >> 16) % (uint)span);
                int y = my * c + margin + (int)((hp >> 16) % (uint)span);
                int d = Distance(ctx, x, y);
                if (d > noOutpost && d <= v.HomeZoneRadius && TryWalkable(ctx, ref x, ref y, out bool shifted))
                {
                    d = Distance(ctx, x, y);
                    if (d > noOutpost && d <= v.HomeZoneRadius)
                    {
                        int tier = Math.Min(Math.Max(1, v.NestMaxTier), 1 + Math.Max(0, d - noOutpost) / Math.Max(1, v.NestTierDistance));
                        nest = new WorldFeature
                        {
                            Id = $"nest:{mx}:{my}", Kind = WorldFeatureKind.ScoutNest, X = x, Y = y, Tier = tier, CellX = mx, CellY = my, Shifted = shifted, Distance = d,
                        };
                    }
                }
            }
            ctx.FeatureCache[key] = (relic, nest);
        }

        private static int Distance(WorldGenContext ctx, int x, int y)
        {
            long dx = x - ctx.Core.X;
            long dy = y - ctx.Core.Y;
            return WorldGenMath.Isqrt(dx * dx + dy * dy);
        }

        /// <summary>落点可走（不是悬崖 / 水面）就不动；否则按螺旋顺序（环半径从 1 到 poiShiftRadius，环上按固定顺序）找第一个可走的格。</summary>
        private static bool TryWalkable(WorldGenContext ctx, ref int x, ref int y, out bool shifted)
        {
            shifted = false;
            int r = Math.Max(0, ctx.Row.PoiShiftRadius);
            int n = (2 * r + 1) * (2 * r + 1);
            var xs = new int[n];
            var ys = new int[n];
            int k = 0;
            xs[k] = x;
            ys[k] = y;
            k++;
            for (int ring = 1; ring <= r; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    xs[k] = x + dx; ys[k] = y - ring; k++;
                    xs[k] = x + dx; ys[k] = y + ring; k++;
                }
                for (int dy = -ring + 1; dy <= ring - 1; dy++)
                {
                    xs[k] = x - ring; ys[k] = y + dy; k++;
                    xs[k] = x + ring; ys[k] = y + dy; k++;
                }
            }
            var terrain = new byte[k];
            WorldGenKernel.SampleCells(in ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones, xs, ys, k, terrain);
            byte cliff = ctx.Source.Params.CodeCliff;
            byte water = ctx.Source.Params.CodeWater;
            for (int i = 0; i < k; i++)
            {
                if (terrain[i] != cliff && terrain[i] != water)
                {
                    shifted = i > 0;
                    x = xs[i];
                    y = ys[i];
                    return true;
                }
            }
            return false;
        }
    }
}
