using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>生成器分布的点位种类（FG3-GEN-01；FG8-GEN-02 起多了据点、资源缓存、旧终端、支线地点）。</summary>
    public enum WorldFeatureKind : byte
    {
        /// <summary>遗迹点（FGR-GEN-033）：全世界分布；v3 起领地里按模板的倍率更密。手工人类遗产按幕分配到遗迹点属于 FG8-EXP-04。</summary>
        Relic = 1,
        /// <summary>零散的敌方侦察巢（FGR-GEN-030、032、034）：家园区（无据点半径之外、家园区半径之内），离核心越远越强。</summary>
        ScoutNest = 2,
        /// <summary>FG8-GEN-02（FGR-GEN-023、034、040）：敌方据点——阵营领地里的主力据点（越往中心越密越强，部分派巡逻）、
        /// 领地之间与外延区的零散前哨（离核心越远越强）。</summary>
        Outpost = 3,
        /// <summary>FG8-GEN-02（FGR-GEN-023、040）：资源缓存——领地里与外延区（越远奖励越高）。派机器打开，废料放在原地由搬运机搬回。</summary>
        Cache = 4,
        /// <summary>FG8-GEN-02（FGR-GEN-023）：旧终端——领地里。派机器读取，得到技术数据与一条记录。</summary>
        Terminal = 5,
        /// <summary>FG8-GEN-02（FGR-GEN-023）：支线任务地点——领地里（地点供 FG8-QST-01 的支线使用；本身没有交互、不在地图上显示）。</summary>
        SideSite = 6,
    }

    /// <summary>一个生成器点位（纯函数结果：只取决于种子、版本、设置与分布格坐标，与访问顺序无关）。</summary>
    public sealed class WorldFeature
    {
        public string Id;
        public WorldFeatureKind Kind;
        public int X;
        public int Y;
        /// <summary>侦察巢 / 据点等级（1 起）；遗迹点、缓存、终端为 0。</summary>
        public int Tier;
        /// <summary>分布格坐标。</summary>
        public int CellX;
        public int CellY;
        /// <summary>原定位置落在悬崖 / 水面上，就近挪了位（FG17 第 5 节“锚点放置与悬崖、水域冲突 → 按规则挪位”）。</summary>
        public bool Shifted;
        public int Distance;
        /// <summary>FG8-GEN-02：点位所在的阵营领地（不在领地里为 null）。</summary>
        public string TerritoryId;
        /// <summary>FG8-GEN-02：据点属于哪个阵营（领地里 = 领地；零散前哨 = 方向最近的阵营领地）。</summary>
        public string FactionId;
        /// <summary>FG8-GEN-02：外延区（离核心 outerZoneDistance 格以外）的点位。</summary>
        public bool Outer;
        /// <summary>FG8-GEN-02：这个据点派一支巡逻。</summary>
        public bool Patrol;
    }

    /// <summary>
    /// FG3-GEN-01（FGR-GEN-030、032、033、034）+ FG8-GEN-02（FGR-GEN-023、040）：生成器点位的分布。
    ///
    /// 世界按 poiCellSize（64）格划成分布格；每个分布格只由（种子, 分布格坐标）决定有没有某种点位、放在格里哪儿——
    /// 与玩家先去了哪里无关（FGR-GEN-002）。落点在悬崖 / 水面上时，在 poiShiftRadius 格内按固定的螺旋顺序找第一个可走的格挪过去，
    /// 找不到就不生成这个点位。
    /// - 遗迹点：每格按 relicChance（v3 起阵营领地里再乘模板的 relicFactor）；避开起始区平地（离核心 relicCoreExclusion 格内不放）。
    /// - 侦察巢：只在家园区（离核心 [无据点半径, 家园区半径]）；每格按 nestChance × 世界设置“据点密度”；等级随距离。
    /// - v3 起（领地模板集合存在时）：
    ///   · 据点：家园区以外。阵营领地里按模板从边缘到中心线性插值的概率与等级（越往中心越密越强），按据点 ID 哈希每 patrolEvery 个派一支巡逻；
    ///     领地之间（外延区以内、不在任何领地里、不在白潮预留区里）按 scatterOutpostChance；外延区按 outerOutpostChance；零散前哨等级 = 1 + (距离 − 家园区半径) / scatterTierDistance。
    ///     阵营 = 所在领地，零散前哨取方向最近的阵营领地。
    ///   · 资源缓存：领地里按模板 cacheChance，外延区按 outerCacheChance；旧终端、支线地点只在领地里。
    ///   · 据点、缓存、终端、支线地点都避开手工锚点的平整范围 + 过渡带 + 模板净空（不压在远征入口 / 室内入口上）。
    /// 版本行没有保证集合（v1）时不分布任何点位；没有模板集合（v1 / v2）时只有遗迹点与侦察巢（与 FG3-GEN-01 逐位相同）。
    /// </summary>
    public static class WorldFeatures
    {
        private const uint RelicSalt = 0x52454C43u; // "RELC"
        private const uint NestSalt = 0x4E455354u;  // "NEST"
        private const uint PostSalt = 0x504F5354u;  // "POST"
        private const uint CacheSalt = 0x43414348u; // "CACH"
        private const uint TermSalt = 0x5445524Du;  // "TERM"
        private const uint SideSalt = 0x53494445u;  // "SIDE"

        /// <summary>每个分布格的点位槽（固定顺序：遗迹点、侦察巢、据点、缓存、终端、支线地点）。</summary>
        private const int Slots = 6;

        public const string OutpostPrefix = "post:";
        public const string CachePrefix = "cache:";
        public const string TerminalPrefix = "term:";
        public const string SidePrefix = "side:";

        public static bool Enabled(WorldGenVersion v) => v != null && WorldGenContent.HasSet(v.GuaranteeSet) && v.PoiCellSize > 0;

        /// <summary>v3 起：有领地模板（据点、缓存、终端、支线地点）。</summary>
        public static bool TerritoryEnabled(WorldGenContext ctx) => ctx != null && Enabled(ctx.Row) && WorldGenContent.HasTerritoryContent(ctx.Row) && ctx.Plan != null && ctx.Plan.HasTemplates;

        /// <summary>矩形 [minX..maxX]×[minY..maxY] 里的点位（按分布格行优先、格内按槽顺序：遗迹点、侦察巢、据点、缓存、终端、支线地点），追加到 <paramref name="into"/>。
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
            Prefetch(ctx, mx0, my0, mx1, my1);
            for (int my = my0; my <= my1; my++)
            {
                for (int mx = mx0; mx <= mx1; mx++)
                {
                    WorldFeature[] cell = CellAll(ctx, mx, my);
                    for (int k = 0; k < Slots; k++)
                    {
                        WorldFeature f = cell[k];
                        if (f != null && f.X >= minX && f.X <= maxX && f.Y >= minY && f.Y <= maxY)
                        {
                            into.Add(f);
                        }
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

        /// <summary>FG8-GEN-02：外延区以内（离核心 &lt; outerZoneDistance）的全部据点——阵营领地的主力据点与领地之间的零散前哨（新战役一次登记成据点）。
        /// 一次 O(圆覆盖的分布格数)（标准设置约 6,000 格），只在新战役 / 旧档第一次进家园时做。</summary>
        public static List<WorldFeature> InnerOutposts(WorldGenContext ctx)
        {
            var list = new List<WorldFeature>();
            if (!TerritoryEnabled(ctx))
            {
                return list;
            }
            int r = Math.Max(0, ctx.Row.OuterZoneDistance);
            var all = new List<WorldFeature>();
            Query(ctx, ctx.Core.X - r, ctx.Core.Y - r, ctx.Core.X + r, ctx.Core.Y + r, all);
            foreach (WorldFeature f in all)
            {
                if (f.Kind == WorldFeatureKind.Outpost && !f.Outer)
                {
                    list.Add(f);
                }
            }
            return list;
        }

        /// <summary>按点位 ID 取点位（重算它的分布格，纯函数 + 缓存）。ID 格式：relic:mx:my / nest:mx:my / post:mx:my / cache:mx:my / term:mx:my / side:mx:my。</summary>
        public static WorldFeature FindById(WorldGenContext ctx, string id)
        {
            if (ctx == null || string.IsNullOrEmpty(id) || !Enabled(ctx.Row))
            {
                return null;
            }
            int sep = id.IndexOf(':');
            if (sep <= 0)
            {
                return null;
            }
            string[] parts = id.Substring(sep + 1).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int mx)
                || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int my))
            {
                return null;
            }
            foreach (WorldFeature f in CellAll(ctx, mx, my))
            {
                if (f != null && f.Id == id)
                {
                    return f;
                }
            }
            return null;
        }

        /// <summary>一个分布格的遗迹点与侦察巢（没有时为 null）。纯函数 + 缓存。</summary>
        public static void CellFeatures(WorldGenContext ctx, int mx, int my, out WorldFeature relic, out WorldFeature nest)
        {
            WorldFeature[] all = CellAll(ctx, mx, my);
            relic = all[0];
            nest = all[1];
        }

        /// <summary>一个分布格的全部点位（槽：0 遗迹点、1 侦察巢、2 据点、3 缓存、4 终端、5 支线地点；没有的槽为 null）。纯函数 + 缓存。</summary>
        public static WorldFeature[] CellAll(WorldGenContext ctx, int mx, int my)
        {
            long key = ((long)mx << 32) | (uint)my;
            if (ctx.FeatureCache.TryGetValue(key, out WorldFeature[] cached))
            {
                return cached;
            }
            WorldGenVersion v = ctx.Row;
            var slots = new WorldFeature[Slots];

            if (RelicCandidate(ctx, mx, my, out int rx, out int ry))
            {
                int x = rx;
                int y = ry;
                if (TryWalkable(ctx, ref x, ref y, out bool shifted))
                {
                    slots[0] = new WorldFeature
                    {
                        Id = $"relic:{mx}:{my}", Kind = WorldFeatureKind.Relic, X = x, Y = y, CellX = mx, CellY = my, Shifted = shifted, Distance = Distance(ctx, x, y),
                        TerritoryId = FactionTerritoryAt(ctx, x, y)?.Id,
                    };
                }
            }

            int noOutpost = NoOutpostRadius(ctx);
            if (NestCandidate(ctx, mx, my, out int nx, out int ny))
            {
                int x = nx;
                int y = ny;
                if (TryWalkable(ctx, ref x, ref y, out bool shifted))
                {
                    int d = Distance(ctx, x, y);
                    if (d > noOutpost && d <= v.HomeZoneRadius)
                    {
                        int tier = Math.Min(Math.Max(1, v.NestMaxTier), 1 + Math.Max(0, d - noOutpost) / Math.Max(1, v.NestTierDistance));
                        slots[1] = new WorldFeature
                        {
                            Id = $"nest:{mx}:{my}", Kind = WorldFeatureKind.ScoutNest, X = x, Y = y, Tier = tier, CellX = mx, CellY = my, Shifted = shifted, Distance = d,
                        };
                    }
                }
            }

            if (TerritoryEnabled(ctx))
            {
                slots[2] = TerritoryFeature(ctx, mx, my, WorldFeatureKind.Outpost);
                slots[3] = TerritoryFeature(ctx, mx, my, WorldFeatureKind.Cache);
                slots[4] = TerritoryFeature(ctx, mx, my, WorldFeatureKind.Terminal);
                slots[5] = TerritoryFeature(ctx, mx, my, WorldFeatureKind.SideSite);
            }
            ctx.FeatureCache[key] = slots;
            return slots;
        }

        private static int NoOutpostRadius(WorldGenContext ctx) =>
            ctx.Plan?.StartReport?.NoOutpostRadius ?? WorldTerrainSource.ScaledRadius(ctx.Row.StartNoOutpostRadius, ctx.Settings);

        /// <summary>阵营领地（白潮滩头预留区不算）。</summary>
        private static PlannedTerritory FactionTerritoryAt(WorldGenContext ctx, int x, int y)
        {
            PlannedTerritory t = ctx.Plan?.TerritoryAt(x, y);
            return t != null && t.IsFaction ? t : null;
        }

        private static int Margin(WorldGenVersion v, out int span)
        {
            int c = v.PoiCellSize;
            int margin = Math.Max(0, Math.Min(v.PoiEdgeMargin, c / 2 - 1));
            span = Math.Max(1, c - 2 * margin);
            return margin;
        }

        /// <summary>分布格 (mx, my) 的遗迹点原定落点（概率命中、且离核心超过排除半径）；不命中返回 false。纯函数。
        /// v3 起阵营领地里的概率再乘模板 relicFactor（候选位置不变，只改命中阈值）。</summary>
        private static bool RelicCandidate(WorldGenContext ctx, int mx, int my, out int x, out int y)
        {
            WorldGenVersion v = ctx.Row;
            uint seed = unchecked((uint)ctx.Seed);
            int c = v.PoiCellSize;
            int margin = Margin(v, out int span);
            x = y = 0;
            uint hr = WorldGenMath.Hash(seed, mx, my, RelicSalt);
            uint hp = WorldGenMath.Hash(seed, mx, my, RelicSalt ^ 0x9E3779B9u);
            x = mx * c + margin + (int)((hr >> 16) % (uint)span);
            y = my * c + margin + (int)((hp >> 16) % (uint)span);
            int chanceQ = Math.Min(65536, WorldTerrainSource.Q(v.RelicChance));
            if (TerritoryEnabled(ctx))
            {
                PlannedTerritory t = FactionTerritoryAt(ctx, x, y);
                TerritoryTemplate tpl = t != null ? ctx.Plan.TemplateFor(t.Id) : null;
                if (tpl != null)
                {
                    chanceQ = (int)Math.Min(65536L, ((long)chanceQ * WorldTerrainSource.Q(tpl.RelicFactor)) >> 16);
                }
            }
            if ((hr & 0xFFFFu) >= (uint)chanceQ)
            {
                return false;
            }
            return Distance(ctx, x, y) > v.RelicCoreExclusion;
        }

        /// <summary>分布格 (mx, my) 的侦察巢原定落点（概率 × 据点密度命中、且在家园区的环里）；不命中返回 false。纯函数。</summary>
        private static bool NestCandidate(WorldGenContext ctx, int mx, int my, out int x, out int y)
        {
            WorldGenVersion v = ctx.Row;
            uint seed = unchecked((uint)ctx.Seed);
            int c = v.PoiCellSize;
            int margin = Margin(v, out int span);
            x = y = 0;
            uint hn = WorldGenMath.Hash(seed, mx, my, NestSalt);
            uint chanceQ = (uint)Math.Min(65536, (int)(((long)WorldTerrainSource.Q(v.NestChance) * WorldTerrainSource.Q(ctx.Settings?.OutpostDensity ?? 1f)) >> 16));
            if ((hn & 0xFFFFu) >= chanceQ)
            {
                return false;
            }
            uint hp = WorldGenMath.Hash(seed, mx, my, NestSalt ^ 0x9E3779B9u);
            x = mx * c + margin + (int)((hn >> 16) % (uint)span);
            y = my * c + margin + (int)((hp >> 16) % (uint)span);
            int d = Distance(ctx, x, y);
            return d > NoOutpostRadius(ctx) && d <= v.HomeZoneRadius;
        }

        private static uint SaltOf(WorldFeatureKind kind) =>
            kind == WorldFeatureKind.Outpost ? PostSalt : kind == WorldFeatureKind.Cache ? CacheSalt : kind == WorldFeatureKind.Terminal ? TermSalt : SideSalt;

        private static string PrefixOf(WorldFeatureKind kind) =>
            kind == WorldFeatureKind.Outpost ? OutpostPrefix : kind == WorldFeatureKind.Cache ? CachePrefix : kind == WorldFeatureKind.Terminal ? TerminalPrefix : SidePrefix;

        /// <summary>v3：分布格 (mx, my) 某一种领地点位的原定落点与命中概率（16.16）；概率为 0 = 这里不会有。纯函数。</summary>
        private static bool TerritoryCandidate(WorldGenContext ctx, int mx, int my, WorldFeatureKind kind, out int x, out int y, out uint hash, out PlannedTerritory territory, out int d)
        {
            WorldGenVersion v = ctx.Row;
            uint seed = unchecked((uint)ctx.Seed);
            int c = v.PoiCellSize;
            int margin = Margin(v, out int span);
            uint salt = SaltOf(kind);
            hash = WorldGenMath.Hash(seed, mx, my, salt);
            uint hp = WorldGenMath.Hash(seed, mx, my, salt ^ 0x9E3779B9u);
            x = mx * c + margin + (int)((hash >> 16) % (uint)span);
            y = my * c + margin + (int)((hp >> 16) % (uint)span);
            d = Distance(ctx, x, y);
            territory = null;
            if (d <= v.HomeZoneRadius)
            {
                return false;
            }
            PlannedTerritory any = ctx.Plan.TerritoryAt(x, y);
            if (any != null && !any.IsFaction)
            {
                return false; // 白潮滩头预留区：揭示前只是普通地形（FGR-GEN-041），不放任何点位。
            }
            territory = any;
            int chanceQ = ChanceQ(ctx, kind, territory, x, y, d);
            if (chanceQ <= 0 || (hash & 0xFFFFu) >= (uint)Math.Min(65536, chanceQ))
            {
                return false;
            }
            TerritoryTemplate tpl = territory != null ? ctx.Plan.TemplateFor(territory.Id) : null;
            int clearance = tpl?.AnchorClearance ?? 60;
            return !TerritoryPlanner.NearAnyAnchor(ctx.Plan, x, y, clearance);
        }

        /// <summary>命中概率（16.16）。据点再乘世界设置“据点密度”。</summary>
        private static int ChanceQ(WorldGenContext ctx, WorldFeatureKind kind, PlannedTerritory territory, int x, int y, int d)
        {
            WorldGenVersion v = ctx.Row;
            TerritoryTemplate tpl = territory != null ? ctx.Plan.TemplateFor(territory.Id) : null;
            bool outer = d >= v.OuterZoneDistance;
            int q;
            switch (kind)
            {
                case WorldFeatureKind.Outpost:
                    if (tpl != null)
                    {
                        int centerQ = CenterFactorQ(territory, x, y);
                        int edge = WorldTerrainSource.Q(tpl.OutpostEdgeChance);
                        int center = WorldTerrainSource.Q(tpl.OutpostCenterChance);
                        q = edge + (int)(((long)(center - edge) * centerQ) >> 16);
                    }
                    else if (territory != null)
                    {
                        q = 0; // 有领地却没有模板：不放（表配置问题，校验另报）
                    }
                    else
                    {
                        q = WorldTerrainSource.Q(outer ? v.OuterOutpostChance : v.ScatterOutpostChance);
                    }
                    return (int)(((long)q * WorldTerrainSource.Q(ctx.Settings?.OutpostDensity ?? 1f)) >> 16);
                case WorldFeatureKind.Cache:
                    return tpl != null ? WorldTerrainSource.Q(tpl.CacheChance) : territory == null && outer ? WorldTerrainSource.Q(v.OuterCacheChance) : 0;
                case WorldFeatureKind.Terminal:
                    return tpl != null ? WorldTerrainSource.Q(tpl.TerminalChance) : 0;
                default:
                    return tpl != null ? WorldTerrainSource.Q(tpl.SideChance) : 0;
            }
        }

        /// <summary>越往领地中心越接近 1（16.16）：1 − 离中心距离 / 半径。</summary>
        private static int CenterFactorQ(PlannedTerritory t, int x, int y)
        {
            long dx = x - t.CenterX;
            long dy = y - t.CenterY;
            int dc = WorldGenMath.Isqrt(dx * dx + dy * dy);
            if (t.Radius <= 0 || dc >= t.Radius)
            {
                return 0;
            }
            return (int)(((long)(t.Radius - dc) << 16) / t.Radius);
        }

        private static WorldFeature TerritoryFeature(WorldGenContext ctx, int mx, int my, WorldFeatureKind kind)
        {
            if (!TerritoryCandidate(ctx, mx, my, kind, out int cx, out int cy, out uint hash, out PlannedTerritory territory, out _))
            {
                return null;
            }
            int x = cx;
            int y = cy;
            if (!TryWalkable(ctx, ref x, ref y, out bool shifted))
            {
                return null;
            }
            WorldGenVersion v = ctx.Row;
            int d = Distance(ctx, x, y);
            TerritoryTemplate tpl = territory != null ? ctx.Plan.TemplateFor(territory.Id) : null;
            var f = new WorldFeature
            {
                Id = PrefixOf(kind) + mx + ":" + my,
                Kind = kind,
                X = x,
                Y = y,
                CellX = mx,
                CellY = my,
                Shifted = shifted,
                Distance = d,
                TerritoryId = territory?.Id,
                Outer = d >= v.OuterZoneDistance,
            };
            if (kind == WorldFeatureKind.Outpost)
            {
                if (tpl != null)
                {
                    int centerQ = CenterFactorQ(territory, x, y);
                    f.Tier = Math.Max(1, tpl.OutpostTierEdge + (int)(((long)(tpl.OutpostTierCenter - tpl.OutpostTierEdge) * centerQ + 32768) >> 16));
                    f.FactionId = territory.Id;
                    f.Patrol = tpl.PatrolEvery > 0 && (hash >> 7) % (uint)tpl.PatrolEvery == 0;
                }
                else
                {
                    f.Tier = Math.Min(Math.Max(1, v.OutpostMaxTier), 1 + Math.Max(0, d - v.HomeZoneRadius) / Math.Max(1, v.ScatterTierDistance));
                    f.FactionId = NearestFactionByAngle(ctx, x, y);
                }
            }
            return f;
        }

        /// <summary>零散前哨的阵营：从核心看，方向夹角最小的阵营领地（并列取表顺序在前的）。纯函数。</summary>
        public static string NearestFactionByAngle(WorldGenContext ctx, int x, int y)
        {
            if (ctx?.Plan == null)
            {
                return null;
            }
            int a = WorldPlan.RadialAngle(x - ctx.Core.X, y - ctx.Core.Y);
            string best = null;
            int bestDiff = int.MaxValue;
            foreach (PlannedTerritory t in ctx.Plan.Territories)
            {
                if (!t.IsFaction)
                {
                    continue;
                }
                int diff = WorldPlan.AngleBetween(a, t.AngleDeg);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = t.Id;
                }
            }
            return best;
        }

        /// <summary>
        /// FG7-ENV-05 性能（结果不变）：矩形里还没算过的分布格，先把所有候选落点一次性批量采样（一个 Burst 任务），
        /// 落点本身可走的点位就不必再逐个采样；不可走的仍按原螺旋规则逐个挪位。只预取落点地形，点位的生成规则与 <see cref="CellAll"/> 完全相同。
        /// </summary>
        private static void Prefetch(WorldGenContext ctx, int mx0, int my0, int mx1, int my1)
        {
            if (!ReferenceEquals(_centerCtx, ctx))
            {
                CenterKnown.Clear();
                _centerCtx = ctx;
            }
            PrefetchXs.Clear();
            PrefetchYs.Clear();
            bool territory = TerritoryEnabled(ctx);
            for (int my = my0; my <= my1; my++)
            {
                for (int mx = mx0; mx <= mx1; mx++)
                {
                    long key = ((long)mx << 32) | (uint)my;
                    if (ctx.FeatureCache.ContainsKey(key))
                    {
                        continue;
                    }
                    if (RelicCandidate(ctx, mx, my, out int rx, out int ry))
                    {
                        AddPrefetch(rx, ry);
                    }
                    if (NestCandidate(ctx, mx, my, out int nx, out int ny))
                    {
                        AddPrefetch(nx, ny);
                    }
                    if (territory)
                    {
                        for (WorldFeatureKind k = WorldFeatureKind.Outpost; k <= WorldFeatureKind.SideSite; k++)
                        {
                            if (TerritoryCandidate(ctx, mx, my, k, out int tx, out int ty, out _, out _, out _))
                            {
                                AddPrefetch(tx, ty);
                            }
                        }
                    }
                }
            }
            int n = PrefetchXs.Count;
            if (n < 2)
            {
                return;
            }
            int[] xs = PrefetchXs.ToArray();
            int[] ys = PrefetchYs.ToArray();
            var terrain = new byte[n];
            WorldGenKernel.SampleCells(in ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones, xs, ys, n, terrain);
            for (int i = 0; i < n; i++)
            {
                CenterKnown[PointKey(xs[i], ys[i])] = terrain[i];
            }
        }

        private static void AddPrefetch(int x, int y)
        {
            if (CenterKnown.ContainsKey(PointKey(x, y)))
            {
                return;
            }
            PrefetchXs.Add(x);
            PrefetchYs.Add(y);
        }

        private static long PointKey(int x, int y) => ((long)x << 32) | (uint)y;

        private static readonly List<int> PrefetchXs = new List<int>(64);
        private static readonly List<int> PrefetchYs = new List<int>(64);
        /// <summary>预取过的落点地形（按上下文；只在 TryWalkable 里读一次就删掉，不长期占内存）。</summary>
        private static readonly Dictionary<long, byte> CenterKnown = new Dictionary<long, byte>();
        private static WorldGenContext _centerCtx;

        private static readonly int[] CenterXs = new int[1];
        private static readonly int[] CenterYs = new int[1];
        private static readonly byte[] CenterTerrain = new byte[1];

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
            byte cliffCode = ctx.Source.Params.CodeCliff;
            byte waterCode = ctx.Source.Params.CodeWater;
            // FG7-ENV-05 性能（结果不变）：绝大多数落点本身就可走——先看落点这一格（矩形查询时已批量预取，否则单独采样一格），可走就直接返回；
            // 不可走才采样整个螺旋。此前每个点位都把螺旋里的 (2r+1)² 格全部采样一遍（主线程逐格噪声），一次揭开 300 格要几十毫秒。
            long pk = PointKey(x, y);
            byte center;
            if (ReferenceEquals(_centerCtx, ctx) && CenterKnown.TryGetValue(pk, out byte known))
            {
                center = known;
                CenterKnown.Remove(pk);
            }
            else
            {
                CenterXs[0] = x;
                CenterYs[0] = y;
                WorldGenKernel.SampleCells(in ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones, CenterXs, CenterYs, 1, CenterTerrain);
                center = CenterTerrain[0];
            }
            if (center != cliffCode && center != waterCode)
            {
                return true;
            }
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
