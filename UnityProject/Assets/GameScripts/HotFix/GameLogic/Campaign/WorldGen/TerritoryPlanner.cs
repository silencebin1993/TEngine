using System;
using System.Collections.Generic;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>FG8-GEN-02：手工锚点模板里的一个点位落到星球上的位置。</summary>
    public sealed class PlannedAnchorPoint
    {
        public string PointId;
        public string Kind;
        public float LocalX;
        public float LocalY;
        public float Radius;
        public string NameKey;
        public int X;
        public int Y;
    }

    /// <summary>
    /// FG8-GEN-02（FGR-GEN-024）：规划层放下的一个手工锚点——模板原点、朝向（模板 +Y 的世界方向）、入口、平整范围与模板点位。
    /// 纯函数结果：只取决于（种子, 生成器版本, 世界设置, 核心落点）与表；读档时重算，不进存档（可达性校验结果另存，见 TerritoryAnchorChecks）。
    /// </summary>
    public sealed class PlannedAnchor
    {
        public string Id;
        public int Index;
        public AnchorSite Row;
        public PlannedTerritory Territory;
        public string Kind;
        public int OriginX;
        public int OriginY;
        /// <summary>模板 +Y 轴的世界方向（整数度，0 = +X，90 = +Y）。所有锚点的 +Y 都背向归还核心：入口朝着家园。</summary>
        public int ForwardDeg;
        public int EntryX;
        public int EntryY;
        public int FootprintRadius;
        public int BlendWidth;
        public string SurfaceId;
        public int EntryRadius;
        public int ProtectRadius;
        /// <summary>用了第几次确定性试探才放下（0 = 第一次就合法；FGR-GEN-090 的局部重生成计数）。</summary>
        public int Attempts;
        /// <summary>所有试探都不合法、按兜底扫描放置（仍确定，写日志）。</summary>
        public bool Fallback;
        public readonly List<PlannedAnchorPoint> Points = new List<PlannedAnchorPoint>();

        public bool IsIndoorEntrance => Kind == TerritoryPlanner.KindIndoor;
        public bool IsDemoSite => Kind == TerritoryPlanner.KindDemo;
        public bool IsReserve => Kind == TerritoryPlanner.KindReserve;

        /// <summary>锚点种类的文本键（world.anchor.kind.{种类}，表里逐个登记）。</summary>
        public string KindKey => "world.anchor.kind." + Kind;
        /// <summary>平整范围 + 过渡带的外缘半径（据点、缓存、危害区都避开这一圈再加模板净空）。</summary>
        public int OuterRadius => FootprintRadius + BlendWidth;

        public PlannedAnchorPoint Point(string pointId)
        {
            foreach (PlannedAnchorPoint p in Points)
            {
                if (p.PointId == pointId)
                {
                    return p;
                }
            }
            return null;
        }
    }

    /// <summary>FG8-GEN-02（FGR-GEN-023 “危害区分布”）：领地里的一处危害区（圆，危害种类 = fg.TbHazard 里这个领地的那一行）。</summary>
    public sealed class PlannedHazardZone
    {
        public string TerritoryId;
        public int Index;
        public int CenterX;
        public int CenterY;
        public int Radius;
    }

    /// <summary>
    /// FG8-GEN-02（FG17 FGR-GEN-021～024、090）：生成器 v3 起，规划层在领地之上再放三样东西，全部是（种子, 版本, 设置, 核心）的纯函数：
    /// <list type="number">
    /// <item><b>手工锚点</b>（fg.TbAnchorSite，按表顺序）：在所属领地里离中心 radialMin～radialMax 倍半径处；toward_core = 在领地中心朝归还核心的一侧
    ///   （± jitterDeg），seeded = 方向由子种子决定。约束：原点与入口都在领地里、平整范围之间至少隔 minGap 格。某次试探不合法时只对这一个锚点
    ///   用派生子种子（种子, 锚点序号, 次数）确定性地重试；全部失败按逐度扫描兜底，再失败写进 <see cref="WorldPlan.Failures"/>（FGR-GEN-090）。
    ///   锚点的平整范围（Flatten 形状）让悬崖、水面、河道都让开，过渡带里逐渐回到正常地形——“与悬崖、水域冲突 → 平整地形”（FG17 第 5 节）。
    ///   从家园出发是否可达，开局时用层级寻路校验（运行时，结果进存档，<see cref="TerritoryAnchorChecks"/>）。</item>
    /// <item><b>地貌风格</b>（fg.TbTerritoryTemplate 的两种风格）：Style 形状，越往领地中心对应地形越多。</item>
    /// <item><b>领地内危害区</b>：每个领地 hazardZones 个圆，避开锚点的平整范围与过渡带（FGR-GEN-023；DEBT-FG7ENV05-02）。</item>
    /// </list>
    /// 开销：每个锚点至多 planMaxAttempts 次整数试探 + 每个领地几个危害区，读档时重算约几十微秒。全部整数运算（角度查 16.16 余弦表），与平台无关。
    /// </summary>
    public static class TerritoryPlanner
    {
        /// <summary>阵营名的文本键（world.faction.{领地 ID}，表里逐个登记；零散前哨按方向归属的阵营也用它）。</summary>
        public static string FactionKey(string territoryId) => "world.faction." + territoryId;

        public const string KindDemo = "demo_site";
        public const string KindBoss = "boss_site";
        public const string KindIndoor = "indoor_entrance";
        public const string KindReserve = "reserve";

        private const uint AnchorSalt = 0x414E4348u; // "ANCH"
        private const uint HazardSalt = 0x485A4F4Eu; // "HZON"

        /// <summary>v3 起：放锚点、加风格与平整形状、放危害区。<paramref name="zones"/> 是给内核的形状列表（追加到河流 / 矿带之后）。</summary>
        public static void Plan(WorldPlan plan, uint seed, WorldGenVersion v, List<WorldGenZone> zones)
        {
            if (!WorldGenContent.HasTerritoryContent(v))
            {
                return;
            }
            PlaceAnchors(plan, seed, v);
            foreach (TerritoryTemplate t in WorldGenContent.TemplatesFor(v))
            {
                plan.Templates[t.TerritoryId] = t;
            }
            for (int ti = 0; ti < plan.Territories.Count; ti++)
            {
                PlannedTerritory territory = plan.Territories[ti];
                if (!plan.Templates.TryGetValue(territory.Id, out TerritoryTemplate tpl))
                {
                    continue;
                }
                AddStyle(zones, territory, tpl.StyleTerrain1, tpl.StyleBonus1);
                AddStyle(zones, territory, tpl.StyleTerrain2, tpl.StyleBonus2);
                PlaceHazardZones(plan, seed, ti, territory, tpl);
            }
            byte flat = GridContent.TerrainCode("buildable");
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a.FootprintRadius <= 0)
                {
                    continue;
                }
                int outer = a.FootprintRadius + Math.Max(0, a.BlendWidth);
                zones.Add(new WorldGenZone
                {
                    Kind = WorldGenZoneKind.Flatten,
                    CenterX = a.OriginX,
                    CenterY = a.OriginY,
                    X1 = a.OriginX,
                    Y1 = a.OriginY,
                    Radius = a.FootprintRadius,
                    HazardWidth = Math.Max(0, a.BlendWidth),
                    Code = flat,
                    BoxMinX = a.OriginX - outer,
                    BoxMinY = a.OriginY - outer,
                    BoxMaxX = a.OriginX + outer,
                    BoxMaxY = a.OriginY + outer,
                });
            }
        }

        private static void AddStyle(List<WorldGenZone> zones, PlannedTerritory t, string terrain, float bonus)
        {
            if (!WorldGenContent.HasSet(terrain) || bonus <= 0f)
            {
                return;
            }
            zones.Add(new WorldGenZone
            {
                Kind = WorldGenZoneKind.Style,
                CenterX = t.CenterX,
                CenterY = t.CenterY,
                X1 = t.CenterX,
                Y1 = t.CenterY,
                Radius = t.Radius,
                Code = GridContent.TerrainCode(terrain),
                ValueQ = WorldTerrainSource.Q(bonus),
                BoxMinX = t.CenterX - t.Radius,
                BoxMinY = t.CenterY - t.Radius,
                BoxMaxX = t.CenterX + t.Radius,
                BoxMaxY = t.CenterY + t.Radius,
            });
        }

        // ── 手工锚点 ───────────────────────────────────────────────────────────

        private static void PlaceAnchors(WorldPlan plan, uint seed, WorldGenVersion v)
        {
            List<AnchorSite> rows = WorldGenContent.AnchorsFor(v);
            int maxAttempts = Math.Max(1, v.PlanMaxAttempts);
            for (int i = 0; i < rows.Count; i++)
            {
                AnchorSite row = rows[i];
                PlannedTerritory t = plan.Find(row.TerritoryId);
                if (t == null)
                {
                    plan.Failures.Add($"锚点 {row.Id}：领地 {row.TerritoryId} 不在规划层里（表配置错误）");
                    continue;
                }
                PlannedAnchor placed = null;
                string lastWhy = null;
                for (int k = 0; k < maxAttempts && placed == null; k++)
                {
                    uint h = WorldGenMath.Hash(seed, i, k, AnchorSalt);
                    uint h2 = WorldGenMath.Hash(seed, i, k, AnchorSalt ^ 0x9E3779B9u);
                    int angle = BaseAngle(plan, t, row, h);
                    int lo = (int)Math.Round(row.RadialMin * 1000f);
                    int hi = Math.Max(lo, (int)Math.Round(row.RadialMax * 1000f));
                    int permille = lo + (int)((h2 >> 5) % (uint)(hi - lo + 1));
                    PlannedAnchor cand = Make(plan, row, t, i, angle, (int)((long)t.Radius * permille / 1000), k);
                    string why = Reject(plan, cand);
                    if (why == null)
                    {
                        placed = cand;
                        if (k > 0)
                        {
                            plan.Log.Add($"锚点 {row.Id}：第 {k + 1} 次试探才合法（上一次原因：{lastWhy}；按子种子（种子, 锚点 {i}, 次数）只重算这一项）");
                        }
                    }
                    lastWhy = why;
                }
                if (placed == null)
                {
                    // 兜底：从种子给出的方向起逐度扫描、距离从近到远；仍确定，写日志。
                    uint h0 = WorldGenMath.Hash(seed, i, maxAttempts, AnchorSalt);
                    int start = BaseAngle(plan, t, row, h0);
                    int dmin = (int)((long)t.Radius * (int)Math.Round(row.RadialMin * 1000f) / 1000);
                    int dmax = Math.Max(dmin, (int)((long)t.Radius * (int)Math.Round(row.RadialMax * 1000f) / 1000));
                    for (int step = 0; step < 360 && placed == null; step++)
                    {
                        for (int d = dmin; d <= dmax && placed == null; d += Math.Max(1, t.Radius / 16))
                        {
                            PlannedAnchor cand = Make(plan, row, t, i, (start + step) % 360, d, maxAttempts);
                            if (Reject(plan, cand) == null)
                            {
                                placed = cand;
                                placed.Fallback = true;
                            }
                        }
                    }
                    if (placed != null)
                    {
                        plan.Log.Add($"锚点 {row.Id}：{maxAttempts} 次试探都不合法（{lastWhy}），按逐度扫描兜底放在 ({placed.OriginX},{placed.OriginY})");
                    }
                    else
                    {
                        placed = Make(plan, row, t, i, start, dmin, maxAttempts);
                        placed.Fallback = true;
                        plan.Failures.Add($"锚点 {row.Id}：试探与逐度扫描都找不到满足约束的位置（{lastWhy}），已放在 ({placed.OriginX},{placed.OriginY})（约束冲突，需要调表）");
                    }
                }
                plan.Anchors.Add(placed);
            }
        }

        /// <summary>候选方向：toward_core = 领地中心朝核心的方向 ± 抖动；seeded = 子种子给的方向。</summary>
        private static int BaseAngle(WorldPlan plan, PlannedTerritory t, AnchorSite row, uint h)
        {
            if (row.AngleMode == "toward_core")
            {
                int toCore = WorldPlan.RadialAngle(plan.CoreX - t.CenterX, plan.CoreY - t.CenterY);
                int j = Math.Max(0, row.JitterDeg);
                return toCore + (int)(h % (uint)(2 * j + 1)) - j;
            }
            return (int)(h % 360u);
        }

        private static PlannedAnchor Make(WorldPlan plan, AnchorSite row, PlannedTerritory t, int index, int angle, int dist, int attempts)
        {
            int ox = t.CenterX + (int)(((long)dist * WorldPlan.Cos(angle) + 32768) >> 16);
            int oy = t.CenterY + (int)(((long)dist * WorldPlan.Cos(angle - 90) + 32768) >> 16);
            // 模板 +Y 背向归还核心（入口朝着家园）；原点就在核心上时取领地方向。
            int forward = ox == plan.CoreX && oy == plan.CoreY ? t.AngleDeg : WorldPlan.RadialAngle(ox - plan.CoreX, oy - plan.CoreY);
            var a = new PlannedAnchor
            {
                Id = row.Id,
                Index = index,
                Row = row,
                Territory = t,
                Kind = row.Kind,
                OriginX = ox,
                OriginY = oy,
                ForwardDeg = forward,
                EntryX = ox,
                EntryY = oy,
                FootprintRadius = Math.Max(0, row.FootprintRadius),
                BlendWidth = Math.Max(0, row.BlendWidth),
                SurfaceId = row.SurfaceId,
                EntryRadius = Math.Max(0, row.EntryRadius),
                ProtectRadius = Math.Max(0, row.ProtectRadius),
                Attempts = attempts,
            };
            foreach (AnchorTemplatePoint p in WorldGenContent.TemplatePoints(row.TemplateId))
            {
                ToWorld(a, p.LocalX, p.LocalY, out int px, out int py);
                var pp = new PlannedAnchorPoint
                {
                    PointId = p.PointId,
                    Kind = p.Kind,
                    LocalX = p.LocalX,
                    LocalY = p.LocalY,
                    Radius = p.Radius,
                    NameKey = p.NameKey,
                    X = px,
                    Y = py,
                };
                a.Points.Add(pp);
                if (p.Kind == "entry")
                {
                    a.EntryX = px;
                    a.EntryY = py;
                }
            }
            return a;
        }

        /// <summary>模板局部坐标（+Y = 朝外方向，+X = 朝外方向的右手侧）→ 世界格（16.16 定点，四舍五入）。</summary>
        public static void ToWorld(PlannedAnchor a, float localX, float localY, out int x, out int y)
        {
            long fx = WorldPlan.Cos(a.ForwardDeg);
            long fy = WorldPlan.Cos(a.ForwardDeg - 90);
            // 右手侧 = 朝外方向顺时针转 90°：(sin, -cos)。
            long rx = fy;
            long ry = -fx;
            long lx = WorldTerrainSource.Q(localX);
            long ly = WorldTerrainSource.Q(localY);
            x = a.OriginX + (int)((lx * rx + ly * fx + (1L << 31)) >> 32);
            y = a.OriginY + (int)((lx * ry + ly * fy + (1L << 31)) >> 32);
        }

        /// <summary>不合法原因；合法返回 null。</summary>
        private static string Reject(WorldPlan plan, PlannedAnchor c)
        {
            PlannedTerritory t = c.Territory;
            if (!Inside(t, c.OriginX, c.OriginY, 0))
            {
                return "原点不在领地里";
            }
            if (!Inside(t, c.EntryX, c.EntryY, 0))
            {
                return "入口不在领地里";
            }
            // FG8-GEN-02 修复轮：入口离领地边界至少一个平整半径——走向入口的队伍要先进领地（顶部领地名称提示、行军“进入领地”提醒）再到入口；
            // 入口贴着边界时，远征队在到达半径（expedition.march.arrive_radius）外就进了地点，一路都没进过领地（冒烟 StepMarchArrived 实测）。
            if (!Inside(t, c.EntryX, c.EntryY, c.FootprintRadius))
            {
                return "入口离领地边界不足一个平整半径";
            }
            long cdx = c.OriginX - plan.CoreX;
            long cdy = c.OriginY - plan.CoreY;
            if (WorldGenMath.Isqrt(cdx * cdx + cdy * cdy) - c.OuterRadius <= plan.HomeZoneRadius)
            {
                return "进入家园区";
            }
            foreach (PlannedAnchor o in plan.Anchors)
            {
                long dx = c.OriginX - o.OriginX;
                long dy = c.OriginY - o.OriginY;
                long need = (long)c.FootprintRadius + o.FootprintRadius + Math.Max(c.Row.MinGap, o.Row.MinGap);
                if (dx * dx + dy * dy < need * need)
                {
                    return $"与锚点 {o.Id} 的平整范围太近";
                }
            }
            return null;
        }

        private static bool Inside(PlannedTerritory t, int x, int y, int margin)
        {
            long dx = x - t.CenterX;
            long dy = y - t.CenterY;
            long r = Math.Max(0, t.Radius - margin);
            return dx * dx + dy * dy <= r * r;
        }

        // ── 领地内危害区 ───────────────────────────────────────────────────────

        private static void PlaceHazardZones(WorldPlan plan, uint seed, int territoryIndex, PlannedTerritory t, TerritoryTemplate tpl)
        {
            int n = Math.Max(0, tpl.HazardZones);
            int rmin = Math.Max(1, tpl.HazardRadiusMin);
            int rmax = Math.Max(rmin, tpl.HazardRadiusMax);
            int radialMax = Math.Max(1, (int)Math.Round(tpl.HazardRadialMax * 1000f));
            int placedCount = 0;
            for (int i = 0; i < n; i++)
            {
                PlannedHazardZone placed = null;
                for (int k = 0; k < 8 && placed == null; k++)
                {
                    uint h = WorldGenMath.Hash(seed, territoryIndex * 64 + i, k, HazardSalt);
                    uint h2 = WorldGenMath.Hash(seed, territoryIndex * 64 + i, k, HazardSalt ^ 0x9E3779B9u);
                    int angle = (int)(h % 360u);
                    int permille = 150 + (int)((h >> 9) % (uint)Math.Max(1, radialMax - 150 + 1));
                    int r = rmin + (int)(h2 % (uint)(rmax - rmin + 1));
                    int dist = (int)((long)t.Radius * permille / 1000);
                    var z = new PlannedHazardZone
                    {
                        TerritoryId = t.Id,
                        Index = i,
                        CenterX = t.CenterX + (int)(((long)dist * WorldPlan.Cos(angle) + 32768) >> 16),
                        CenterY = t.CenterY + (int)(((long)dist * WorldPlan.Cos(angle - 90) + 32768) >> 16),
                        Radius = r,
                    };
                    if (HazardFits(plan, t, z, tpl))
                    {
                        placed = z;
                    }
                }
                if (placed == null)
                {
                    plan.Log.Add($"{t.Id} 的第 {i + 1} 处危害区：8 次试探都压在锚点或别的危害区上，跳过这一处");
                    continue;
                }
                plan.HazardZones.Add(placed);
                placedCount++;
            }
            if (n > 0)
            {
                plan.Log.Add($"{t.Id}：领地内危害区 {placedCount}/{n} 处");
            }
        }

        private static bool HazardFits(WorldPlan plan, PlannedTerritory t, PlannedHazardZone z, TerritoryTemplate tpl)
        {
            if (!Inside(t, z.CenterX, z.CenterY, z.Radius / 2))
            {
                return false;
            }
            foreach (PlannedAnchor a in plan.Anchors)
            {
                long dx = z.CenterX - a.OriginX;
                long dy = z.CenterY - a.OriginY;
                long need = (long)z.Radius + a.OuterRadius + Math.Max(0, tpl.AnchorClearance) / 2;
                if (dx * dx + dy * dy < need * need)
                {
                    return false;
                }
            }
            foreach (PlannedHazardZone o in plan.HazardZones)
            {
                if (o.TerritoryId != z.TerritoryId)
                {
                    continue;
                }
                long dx = z.CenterX - o.CenterX;
                long dy = z.CenterY - o.CenterY;
                long need = (long)(z.Radius + o.Radius) * 3 / 4;
                if (dx * dx + dy * dy < need * need)
                {
                    return false;
                }
            }
            return true;
        }

        // ── 查询 ───────────────────────────────────────────────────────────────

        /// <summary>离锚点平整范围外缘的净空检查：点 (x, y) 落在任一锚点的“平整范围 + 过渡带 + <paramref name="clearance"/>”里返回 true。O(锚点数)。</summary>
        public static bool NearAnyAnchor(WorldPlan plan, int x, int y, int clearance)
        {
            if (plan == null)
            {
                return false;
            }
            foreach (PlannedAnchor a in plan.Anchors)
            {
                if (a.IsReserve)
                {
                    continue;
                }
                long dx = x - a.OriginX;
                long dy = y - a.OriginY;
                long r = (long)a.OuterRadius + Math.Max(0, clearance);
                if (dx * dx + dy * dy <= r * r)
                {
                    return true;
                }
                // 入口在平整范围之外时（模板入口离原点远），入口周围也留同样的净空。
                long ex = x - a.EntryX;
                long ey = y - a.EntryY;
                long er = Math.Max(0, clearance) + Math.Max(a.EntryRadius, 4);
                if (ex * ex + ey * ey <= er * er)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>规划指纹的锚点 / 危害区部分（v3 起追加到 <see cref="WorldPlan.FullFingerprint"/>）。</summary>
        public static void AppendFingerprint(WorldPlan plan, StringBuilder sb)
        {
            foreach (PlannedAnchor a in plan.Anchors)
            {
                sb.Append("A").Append(a.Id).Append('@').Append(a.OriginX).Append(',').Append(a.OriginY).Append('f').Append(a.ForwardDeg)
                  .Append('e').Append(a.EntryX).Append(',').Append(a.EntryY).Append('k').Append(a.Attempts).Append(a.Fallback ? "F" : string.Empty).Append(';');
            }
            foreach (PlannedHazardZone z in plan.HazardZones)
            {
                sb.Append("H").Append(z.TerritoryId).Append(z.Index).Append('@').Append(z.CenterX).Append(',').Append(z.CenterY).Append('r').Append(z.Radius).Append(';');
            }
        }
    }
}
