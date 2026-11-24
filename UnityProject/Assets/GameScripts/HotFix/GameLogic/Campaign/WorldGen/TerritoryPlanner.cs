using System;
using System.Collections.Generic;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;

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
        public static void Plan(WorldPlan plan, uint seed, WorldGenVersion v, List<WorldGenZone> zones, IReadOnlyDictionary<string, int> anchorFirstAttempt = null)
        {
            if (!WorldGenContent.HasTerritoryContent(v))
            {
                return;
            }
            PlaceAnchors(plan, seed, v, anchorFirstAttempt);
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
                if (EnclosureForTests != null && EnclosureForTests(a))
                {
                    // 自检注入（FG15-QA-03）：平整范围外围一圈 6 格宽的悬崖（先盖悬崖圆盘，下面的平整形状再盖回内圈），驱动粗粒度连通性判定的换候选路径。
                    zones.Add(WorldPlan.Stamp(a.OriginX, a.OriginY, outer + 6, GridContent.TerrainCode("cliff")));
                }
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
                AddWaterRing(zones, a, flat);
            }
        }

        /// <summary>
        /// FG11-REG-01（生成器 v4，DEBT-FG10BOSS01-02“湖心平台四周没有湖”）：平整范围外 waterRing 格宽的一圈强制为水面（Stamp，被平整范围覆盖回可建地），
        /// 再沿“原点 → 归还核心”方向铺一条 causewayWidth 半宽的堤道（一串无过渡带的平整圆盘，穿过水环一直铺到领地圈外），保证从家园走得到平台。
        /// 只看锚点行与规划层，确定；as3 全是 0 = 空操作（v3 逐格不变）。
        /// </summary>
        private static void AddWaterRing(List<WorldGenZone> zones, PlannedAnchor a, byte flat)
        {
            int ring = a.Row != null ? a.Row.WaterRing : 0;
            int half = a.Row != null ? a.Row.CausewayWidth : 0;
            if (ring <= 0 || half <= 0)
            {
                return;
            }
            int outer = a.FootprintRadius + ring;
            zones.Add(WorldPlan.Stamp(a.OriginX, a.OriginY, outer, GridContent.TerrainCode("water")));
            // 模板 +Y 背向核心 → 朝核心 = ForwardDeg + 180。
            int toward = a.ForwardDeg + 180;
            long fx = WorldPlan.Cos(toward);
            long fy = WorldPlan.Cos(toward - 90);
            int step = Math.Max(1, half);
            // 堤道一直铺到领地圈外（湖区本身按地貌风格水面偏多，只穿过水环会在湖里断头：FgWorldGenHomeSelfCheck FGT-GEN-004 种子 1 实测）。
            int length = outer + 2 + (a.Territory != null ? a.Territory.Radius : 0);
            // FG11-E2E-01（FG-GAP-164 实跑补正）：湖区内部实测八成以上是 3 级污染，有涂层的中继也建不进去；堤道上污染压到“有涂层中继可建”的最高级，
            // 让中继链沿堤道建到平台（只有堤道这一条，其余湖区不变）。
            int polCap = CausewayPollutionCap;
            for (int d = Math.Max(0, a.FootprintRadius - half); d <= length; d += step)
            {
                int cx = a.OriginX + (int)((d * fx + 32768) >> 16);
                int cy = a.OriginY + (int)((d * fy + 32768) >> 16);
                zones.Add(new WorldGenZone
                {
                    Kind = WorldGenZoneKind.Flatten,
                    CenterX = cx,
                    CenterY = cy,
                    X1 = cx,
                    Y1 = cy,
                    Radius = half,
                    HazardWidth = 0,
                    Code = flat,
                    ValueQ = polCap + 1,
                    BoxMinX = cx - half,
                    BoxMinY = cy - half,
                    BoxMaxX = cx + half,
                    BoxMaxY = cy + half,
                });
            }
        }

        /// <summary>
        /// v4 堤道上的污染上限（级）：取 v4 定稿时“有防腐涂层的信号中继还能建的最高级”（grid.relay_coated_block_level 3 − 1 = 2）的字面值，
        /// 不跟运行时调参走——生成结果只随生成器版本变（FGR-GEN-061），调中继阻挡等级不会悄悄改已有 v4 世界。进生成输入清单（<see cref="WorldGenInputs"/> 的 ring 段）；
        /// FgHeatDataCitySelfCheck A1 断言它仍低于当前阻挡等级（否则堤道上建不了中继）。
        /// </summary>
        public const int CausewayPollutionCap = 2;

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

        private static void PlaceAnchors(WorldPlan plan, uint seed, WorldGenVersion v, IReadOnlyDictionary<string, int> anchorFirstAttempt)
        {
            List<AnchorSite> rows = WorldGenContent.AnchorsFor(v);
            int maxAttempts = Math.Max(1, v.PlanMaxAttempts);
            for (int i = 0; i < rows.Count; i++)
            {
                AnchorSite row = rows[i];
                PlannedTerritory t = plan.Find(row.TerritoryId);
                if (t == null)
                {
                    plan.Failures.Add($"锚点 {row.Id}：领地 {row.TerritoryId} 不在规划层里（表配置错误）"); // loc-ignore: 开发者自检 / 诊断文本
                    continue;
                }
                PlannedAnchor placed = null;
                string lastWhy = null;
                int k0 = anchorFirstAttempt != null && anchorFirstAttempt.TryGetValue(row.Id, out int first) ? Math.Max(0, Math.Min(first, maxAttempts - 1)) : 0;
                if (k0 > 0)
                {
                    lastWhy = "粗粒度连通性判定：前一个候选被地形围住"; // loc-ignore: 开发者自检 / 诊断文本
                }
                for (int k = k0; k < maxAttempts && placed == null; k++)
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
                        plan.Failures.Add($"锚点 {row.Id}：试探与逐度扫描都找不到满足约束的位置（{lastWhy}），已放在 ({placed.OriginX},{placed.OriginY})（约束冲突，需要调表）"); // loc-ignore: 开发者自检 / 诊断文本
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
                return "原点不在领地里"; // loc-ignore: 开发者自检 / 诊断文本
            }
            if (!Inside(t, c.EntryX, c.EntryY, 0))
            {
                return "入口不在领地里"; // loc-ignore: 开发者自检 / 诊断文本
            }
            // FG8-GEN-02 修复轮：入口离领地边界至少一个平整半径——走向入口的队伍要先进领地（顶部领地名称提示、行军“进入领地”提醒）再到入口；
            // 入口贴着边界时，远征队在到达半径（expedition.march.arrive_radius）外就进了地点，一路都没进过领地（冒烟 StepMarchArrived 实测）。
            if (!Inside(t, c.EntryX, c.EntryY, c.FootprintRadius))
            {
                return "入口离领地边界不足一个平整半径"; // loc-ignore: 开发者自检 / 诊断文本
            }
            long cdx = c.OriginX - plan.CoreX;
            long cdy = c.OriginY - plan.CoreY;
            if (WorldGenMath.Isqrt(cdx * cdx + cdy * cdy) - c.OuterRadius <= plan.HomeZoneRadius)
            {
                return "进入家园区"; // loc-ignore: 开发者自检 / 诊断文本
            }
            foreach (PlannedAnchor o in plan.Anchors)
            {
                long dx = c.OriginX - o.OriginX;
                long dy = c.OriginY - o.OriginY;
                long need = (long)c.FootprintRadius + o.FootprintRadius + Math.Max(c.Row.MinGap, o.Row.MinGap);
                if (dx * dx + dy * dy < need * need)
                {
                    return $"与锚点 {o.Id} 的平整范围太近"; // loc-ignore: 开发者自检 / 诊断文本
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

        // ── 粗粒度连通性判定（FG15-QA-03，FGR-GEN-090 第 2 条 / DEBT-FG8GEN02-09）──────────────────────────────

        /// <summary>从这个生成器版本起，规划层对非 Demo 锚点做粗粒度连通性判定、被围住就按子种子换候选（v1～v4 不做，旧存档的生成结果不变，FGR-GEN-061）。</summary>
        public const int ReachRegenFromVersion = 5;

        /// <summary>判定范围：锚点平整半径 + 过渡带 + 水环之外再放这么多格；从入口出发的可走连通块要碰到这个方框的边才算“没被地形围住”。
        /// 是生成输入（列进 v5 起的生成输入清单），不入调参表：改它就要新增生成器版本。</summary>
        public const int EscapeMargin = 48;

        /// <summary>自检注入：返回 true 的候选在平整范围外围一圈悬崖（只用来驱动换候选路径的正负向断言）。</summary>
        public static Func<PlannedAnchor, bool> EnclosureForTests;

        /// <summary>要做粗粒度连通性判定的锚点：要校验可达、且不是 Demo 远征地点（那两个由 ExpeditionSiteAnchors 的 8 次候选负责）。</summary>
        public static bool NeedsEscapeCheck(PlannedAnchor a) => a != null && a.Row != null && a.Row.ReachCheck == 1 && a.Kind != KindDemo;

        /// <summary>
        /// 与存档无关的粗粒度连通性判定（只读生成出来的地形，不读玩家修改）：在以锚点原点为中心、半边长
        /// 平整半径 + 过渡带 + 水环 + <see cref="EscapeMargin"/> 的方框里，从入口格 4 邻接漫水（悬崖 / 水面不可走），碰到方框边 = 没被地形整体围住。
        /// 只证明“锚点附近没有闭合的悬崖 / 水圈”；远处的隔断（河流）由浅滩周期保证（check_luban R29），真寻路可达仍由运行时
        /// <see cref="TerritoryAnchorChecks"/> 与种子测试集（FgSeedSuiteSelfCheck）兜底。
        /// 开销：深度优先、先往外走，区块按需生成——开阔地形里每个锚点只生成从入口到框边一条线上的几个区块；被围住时只漫完围住的那一块。
        /// </summary>
        public static bool EscapesLocally(WorldTerrainSource src, PlannedAnchor a, out int reached)
        {
            reached = 0;
            int r = a.FootprintRadius + Math.Max(0, a.BlendWidth) + Math.Max(0, a.Row?.WaterRing ?? 0) + EscapeMargin;
            int n = 2 * r + 1;
            int x0 = a.OriginX - r;
            int y0 = a.OriginY - r;
            int ex = a.EntryX - x0;
            int ey = a.EntryY - y0;
            if (ex < 0 || ey < 0 || ex >= n || ey >= n)
            {
                return false;
            }
            int cs = src.Params.ChunkSize;
            byte cliff = src.Params.CodeCliff;
            byte water = src.Params.CodeWater;
            // 区块按需生成（Burst 整块）：深度优先、先走离原点更远的一格，开阔地形里一路往外，只碰到一条线上的几个区块（审查前实测整框预生成约 200 个区块 / 世界，会让建世界多卡一下）。
            var chunks = new Dictionary<long, byte[]>();
            var pol = new byte[cs * cs];
            bool Walk(int gx, int gy)
            {
                int wx = x0 + gx;
                int wy = y0 + gy;
                long cx = WorldGenMath.FloorDiv(wx, cs);
                long cy = WorldGenMath.FloorDiv(wy, cs);
                long key = (cx << 32) ^ (uint)cy;
                if (!chunks.TryGetValue(key, out byte[] t))
                {
                    t = new byte[cs * cs];
                    src.FillChunk((int)cx, (int)cy, cs, t, pol);
                    chunks[key] = t;
                }
                byte c0 = t[(int)(wy - cy * cs) * cs + (int)(wx - cx * cs)];
                return c0 != cliff && c0 != water;
            }
            // 起点：入口格本身不可走时（例如白潮滩头这种没有平整范围的预留区，入口落在水面上）取入口半径内最近的可走格——
            // 与寻路把终点落到最近可走格、运行时可达校验按入口半径判到达一致；半径内一格可走的都没有 = 被围住。
            int start = -1;
            int rs = Math.Max(4, a.EntryRadius);
            for (int ring = 0; ring <= rs && start < 0; ring++)
            {
                for (int dy = -ring; dy <= ring && start < 0; dy++)
                {
                    for (int dx = -ring; dx <= ring && start < 0; dx++)
                    {
                        int sx = ex + dx;
                        int sy = ey + dy;
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring && sx >= 0 && sy >= 0 && sx < n && sy < n && Walk(sx, sy))
                        {
                            start = sy * n + sx;
                        }
                    }
                }
            }
            if (start < 0)
            {
                return false;
            }
            var seen = new bool[n * n];
            var stack = new Stack<int>();
            var next = new int[4];
            var far = new long[4];
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                reached++;
                int kx = k % n;
                int ky = k / n;
                if (kx == 0 || ky == 0 || kx == n - 1 || ky == n - 1)
                {
                    return true;
                }
                int m = 0;
                for (int d = 0; d < 4; d++)
                {
                    int nx = kx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = ky + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    int idx = ny * n + nx;
                    if (!seen[idx] && Walk(nx, ny))
                    {
                        seen[idx] = true;
                        next[m] = idx;
                        far[m] = (long)(nx - r) * (nx - r) + (long)(ny - r) * (ny - r);
                        m++;
                    }
                }
                // 离原点近的先压栈，远的后压、先弹出（确定：同距离按方向序）。
                for (int i = 1; i < m; i++)
                {
                    for (int j = i; j > 0 && far[j - 1] > far[j]; j--)
                    {
                        (far[j - 1], far[j]) = (far[j], far[j - 1]);
                        (next[j - 1], next[j]) = (next[j], next[j - 1]);
                    }
                }
                for (int i = 0; i < m; i++)
                {
                    stack.Push(next[i]);
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
