using System;
using System.Collections.Generic;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>规划层给出的一块领地（或白潮滩头预留区）。坐标为格网坐标。</summary>
    public sealed class PlannedTerritory
    {
        public string Id;
        public string NameKey;
        public string HazardKind;
        public string HazardKey;
        public int Act;
        public bool IsFaction;
        public int AngleDeg;
        public int Distance;
        public int CenterX;
        public int CenterY;
        public int Radius;
        public int HazardWidth;
        /// <summary>用了第几次确定性重试才放下（0 = 第一次就合法；FGR-GEN-090 的局部重生成计数）。</summary>
        public int Attempts;
        /// <summary>所有重试都失败、按兜底规则放置（仍确定，写进 <see cref="WorldPlan.Failures"/>）。</summary>
        public bool Fallback;

        public int OuterRadius => Radius + HazardWidth;
    }

    /// <summary>FG3-GEN-01（FGR-GEN-020）：规划层的一条主要河流——折线（格网坐标），每段半宽逐段加宽，按周期留浅滩。</summary>
    public sealed class PlannedRiver
    {
        public int Index;
        public readonly List<GridPoint> Points = new List<GridPoint>();
        public readonly List<int> HalfWidths = new List<int>();
        public int FordPeriod;
        public int FordWidth;
        public int Length;
    }

    /// <summary>FG3-GEN-01（FGR-GEN-020）：规划层的一条矿带（线段 + 半宽），矿带内对应矿种更密。</summary>
    public sealed class PlannedBelt
    {
        public int Index;
        public string Terrain;
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public int Radius;
        public int BonusQ;
    }

    /// <summary>格网上的一个点（规划层用；与 Grid.GridCell 同义，避免规划层依赖格网命名空间的类型）。</summary>
    public readonly struct GridPoint
    {
        public readonly int X;
        public readonly int Y;

        public GridPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-020、021 原型）：区域规划层——按（世界种子, 生成器版本, 世界设置）确定性计算各阵营领地的方向、
    /// 中心、范围、外圈危害带，以及白潮滩头预留区。计算量小（每个领地至多几十次整数试探），读档时重算，不进存档。
    ///
    /// 约束（FGR-GEN-021 / 090）：四个阵营领地方向两两夹角 ≥ 版本行 territoryMinAngle；所有领地（含危害带）与预留区互不重叠；
    /// 都不进入家园区（核心半径 = 版本行 homeZoneRadius）。某个领地的第 k 次试探不满足时，用派生子种子（种子, 领地序号, k）
    /// 确定性地重试——只重算失败的这一项，不重算整个世界；重试次数写进 <see cref="PlannedTerritory.Attempts"/> 与日志。
    /// 河流、矿带、据点分布、锚点落位属于 FG3-GEN-01 的正式规划层。
    /// 全部整数运算（角度用整数度，三角函数查 16.16 定点表），与平台无关。
    /// </summary>
    public sealed class WorldPlan
    {
        private const uint PlanSalt = 0x504C414Eu; // "PLAN"

        public readonly List<PlannedTerritory> Territories = new List<PlannedTerritory>();
        /// <summary>FG3-GEN-01：主要河流（版本行 riverCount；v1 没有）。</summary>
        public readonly List<PlannedRiver> Rivers = new List<PlannedRiver>();
        /// <summary>FG3-GEN-01：矿带（版本行 beltCount；v1 没有）。</summary>
        public readonly List<PlannedBelt> Belts = new List<PlannedBelt>();
        /// <summary>FG3-GEN-01：起始区保证的结果（<see cref="StartZoneGuarantee"/> 写入；v1 为 null）。</summary>
        public StartGuaranteeReport StartReport { get; internal set; }
        /// <summary>生成这份规划用的世界设置。</summary>
        public WorldSettings Settings { get; private set; }
        public int Seed { get; private set; }
        public int Version { get; private set; }
        public readonly List<string> Log = new List<string>();
        public readonly List<string> Failures = new List<string>();
        public int CoreX { get; private set; }
        public int CoreY { get; private set; }
        public int HomeZoneRadius { get; private set; }
        public int MinAngleDeg { get; private set; }

        /// <summary>给生成内核的形状：领地圆（阵营领地与白潮滩头；危害带宽度 0 的没有外圈），v2 起再加河流段、矿带与起始区保证点
        /// （<see cref="WorldGenZoneKind"/>）。v1 只有领地，与 FG0-ARCH-05 逐字节相同。</summary>
        public WorldGenZone[] Zones { get; private set; } = Array.Empty<WorldGenZone>();

        /// <summary>只含领地、河流、矿带（不含保证点）：起始区保证的“自然生成”在它上面校验。</summary>
        public WorldGenZone[] NaturalZones { get; private set; } = Array.Empty<WorldGenZone>();

        /// <summary>起始区保证点写回后重建 <see cref="Zones"/>。</summary>
        internal void SetStamps(IReadOnlyList<WorldGenZone> stamps)
        {
            var all = new WorldGenZone[NaturalZones.Length + (stamps?.Count ?? 0)];
            Array.Copy(NaturalZones, all, NaturalZones.Length);
            for (int i = 0; i < (stamps?.Count ?? 0); i++)
            {
                all[NaturalZones.Length + i] = stamps[i];
            }
            Zones = all;
        }

        /// <summary>格子所在的领地（在领地半径内）；不在任何领地返回 null。</summary>
        public PlannedTerritory TerritoryAt(int x, int y)
        {
            for (int i = 0; i < Territories.Count; i++)
            {
                PlannedTerritory t = Territories[i];
                long dx = x - t.CenterX;
                long dy = y - t.CenterY;
                if (dx * dx + dy * dy <= (long)t.Radius * t.Radius)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>格子所在的危害带（领地外圈）；不在任何危害带返回 null。</summary>
        public PlannedTerritory HazardAt(int x, int y)
        {
            for (int i = 0; i < Territories.Count; i++)
            {
                PlannedTerritory t = Territories[i];
                if (t.HazardWidth <= 0)
                {
                    continue;
                }
                long dx = x - t.CenterX;
                long dy = y - t.CenterY;
                long d2 = dx * dx + dy * dy;
                if (d2 > (long)t.Radius * t.Radius && d2 <= (long)t.OuterRadius * t.OuterRadius)
                {
                    return t;
                }
            }
            return null;
        }

        public PlannedTerritory Find(string id)
        {
            foreach (PlannedTerritory t in Territories)
            {
                if (t.Id == id)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>完整规划指纹（FG3-GEN-01）：领地 + 河流 + 矿带 + 起始区保证点。</summary>
        public string FullFingerprint()
        {
            var sb = new StringBuilder(Fingerprint());
            foreach (PlannedRiver r in Rivers)
            {
                sb.Append("R").Append(r.Index).Append(':');
                for (int i = 0; i < r.Points.Count; i++)
                {
                    sb.Append(r.Points[i].X).Append(',').Append(r.Points[i].Y).Append(i < r.HalfWidths.Count ? "w" + r.HalfWidths[i] : string.Empty).Append(' ');
                }
                sb.Append(';');
            }
            foreach (PlannedBelt b in Belts)
            {
                sb.Append("B").Append(b.Index).Append(b.Terrain).Append('@').Append(b.X0).Append(',').Append(b.Y0).Append('-').Append(b.X1).Append(',').Append(b.Y1)
                  .Append('r').Append(b.Radius).Append(';');
            }
            if (StartReport != null)
            {
                sb.Append(StartReport.Fingerprint());
            }
            return sb.ToString();
        }

        /// <summary>规划指纹（自检比对“同一输入 → 同一规划”）。</summary>
        public string Fingerprint()
        {
            var sb = new StringBuilder();
            foreach (PlannedTerritory t in Territories)
            {
                sb.Append(t.Id).Append('@').Append(t.CenterX).Append(',').Append(t.CenterY).Append('r').Append(t.Radius)
                  .Append('h').Append(t.HazardWidth).Append('a').Append(t.AngleDeg).Append('k').Append(t.Attempts).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>两个方向（整数度）之间的最小夹角（0～180）。</summary>
        public static int AngleBetween(int a, int b)
        {
            int d = ((a - b) % 360 + 360) % 360;
            return d > 180 ? 360 - d : d;
        }

        // ── 计算 ───────────────────────────────────────────────────────────────

        /// <summary>按（种子, 生成器版本, 世界设置）计算规划层。<paramref name="coreX"/>/<paramref name="coreY"/> 是归还核心枢轴格。
        /// 全部输入都随版本走（FGR-GEN-061）：家园区半径 / 最小夹角 / 重试次数取版本行，领地取版本行引用的领地集合，
        /// 预设也是按版本的集合取出来的——改当前表不会改变已有存档的规划。</summary>
        public static WorldPlan Compute(int worldSeed, WorldGenVersion version, WorldPreset preset, int coreX, int coreY) =>
            Compute(worldSeed, version, preset != null ? WorldSettings.FromPreset(preset, version?.Version ?? 1) : null, coreX, coreY);

        /// <summary>同上，世界设置已解析（v2 分项设置或 v1 预设）。只算领地、河流、矿带；起始区保证由 <see cref="StartZoneGuarantee"/> 接着算。</summary>
        public static WorldPlan Compute(int worldSeed, WorldGenVersion version, WorldSettings settings, int coreX, int coreY)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }
            var plan = new WorldPlan
            {
                CoreX = coreX,
                CoreY = coreY,
                HomeZoneRadius = version.HomeZoneRadius,
                MinAngleDeg = version.TerritoryMinAngle,
                Settings = settings,
                Seed = worldSeed,
                Version = version.Version,
            };
            int maxAttempts = Math.Max(1, version.PlanMaxAttempts);
            int distScaleQ = (int)Math.Round((settings?.TerritoryDistanceScale ?? 1f) * 65536.0);
            uint seed = unchecked((uint)worldSeed);
            IReadOnlyList<Territory> rows = WorldGenContent.TerritoriesFor(version);

            // 先放阵营领地（受夹角约束），再放预留区（只要求不重叠）；同类按表顺序。
            var ordered = new List<(Territory row, int index)>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Faction == 1)
                {
                    ordered.Add((rows[i], i));
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Faction != 1)
                {
                    ordered.Add((rows[i], i));
                }
            }

            foreach ((Territory row, int index) in ordered)
            {
                int hazard = row.HazardKind == "none" ? 0 : Math.Max(0, row.HazardWidth);
                int lo = (int)(((long)row.MinDistance * distScaleQ) >> 16);
                int hi = (int)(((long)row.MaxDistance * distScaleQ) >> 16);
                lo = Math.Max(lo, plan.HomeZoneRadius + row.Radius + hazard + 1); // 不进入家园区
                if (hi < lo)
                {
                    hi = lo;
                }
                PlannedTerritory placed = null;
                string lastWhy = null;
                for (int k = 0; k < maxAttempts && placed == null; k++)
                {
                    uint h = WorldGenMath.Hash(seed, index, k, PlanSalt);
                    int angle = (int)(h % 360u);
                    int dist = lo + (int)((h >> 9) % (uint)(hi - lo + 1));
                    PlannedTerritory cand = Make(row, hazard, angle, dist, coreX, coreY, k);
                    string why = plan.Reject(cand);
                    if (why == null)
                    {
                        placed = cand;
                        if (k > 0)
                        {
                            plan.Log.Add($"{row.Id}：第 {k + 1} 次试探才合法（前 {k} 次被拒，上一次原因：{lastWhy}；按子种子（种子, 领地, 次数）只重算这一项）");
                        }
                    }
                    lastWhy = why;
                }
                if (placed == null)
                {
                    // 兜底：从种子给出的方向起逐度扫描，距离从近到远；仍确定，写进失败日志。
                    uint h0 = WorldGenMath.Hash(seed, index, maxAttempts, PlanSalt);
                    int start = (int)(h0 % 360u);
                    for (int step = 0; step < 360 && placed == null; step++)
                    {
                        for (int dist = lo; dist <= hi && placed == null; dist += Math.Max(1, row.Radius / 4))
                        {
                            PlannedTerritory cand = Make(row, hazard, (start + step) % 360, dist, coreX, coreY, maxAttempts);
                            if (plan.Reject(cand) == null)
                            {
                                placed = cand;
                                placed.Fallback = true;
                            }
                        }
                    }
                    if (placed == null)
                    {
                        placed = Make(row, hazard, start, hi, coreX, coreY, maxAttempts);
                        placed.Fallback = true;
                        plan.Failures.Add($"{row.Id}：{maxAttempts} 次试探与逐度扫描都找不到满足约束的位置，已放在 {start}° / {hi} 格（约束冲突，需要调表）");
                    }
                    else
                    {
                        plan.Log.Add($"{row.Id}：{maxAttempts} 次试探都不合法，按逐度扫描兜底放在 {placed.AngleDeg}° / {placed.Distance} 格");
                    }
                }
                plan.Territories.Add(placed);
            }

            var zones = new List<WorldGenZone>(plan.Territories.Count);
            for (int i = 0; i < plan.Territories.Count; i++)
            {
                PlannedTerritory t = plan.Territories[i];
                zones.Add(new WorldGenZone { CenterX = t.CenterX, CenterY = t.CenterY, Radius = t.Radius, HazardWidth = t.HazardWidth });
            }
            // FG3-GEN-01：河流与矿带（版本行没有它们时什么也不加 —— v1 只有领地，结果不变）。
            plan.PlanRivers(seed, version, zones);
            plan.PlanBelts(seed, version, zones);
            plan.NaturalZones = zones.ToArray();
            plan.Zones = plan.NaturalZones;
            return plan;
        }

        private const uint RiverSalt = 0x52495652u; // "RIVR"
        private const uint BeltSalt = 0x42454C54u;  // "BELT"

        /// <summary>
        /// FG3-GEN-01（FGR-GEN-020）：主要河流。第 i 条河的起点在核心周围 riverStartDistance 格、方向由子种子（种子, i）给出，逐段向外流：
        /// 每段方向在上一段基础上偏转 ±riverTurnMax 度，但与“离心方向”的夹角不超过 riverOutwardMaxAngle 度（v2 = 75，河不会流回家园）。
        /// 半宽从 riverHalfWidth 沿河线性加宽 riverWidenTotal 格（v2 = 3）、不超过上限，
        /// 每隔 riverFordPeriod 格留 riverFordWidth 格宽的浅滩（可走可建），所以河流不会把领地隔断（FGT-GEN-004）。全整数运算。
        /// </summary>
        private void PlanRivers(uint seed, WorldGenVersion v, List<WorldGenZone> zones)
        {
            int count = Math.Max(0, v.RiverCount);
            int segs = Math.Max(1, v.RiverSegments);
            int step = Math.Max(1, v.RiverSegmentLength);
            for (int i = 0; i < count; i++)
            {
                uint h = WorldGenMath.Hash(seed, i, 0, RiverSalt);
                // 河的出发方向：均分 360° 再加子种子偏移，几条河朝不同方向流。
                int baseAngle = (int)((h % 360u) + (uint)(i * 360 / Math.Max(1, count))) % 360;
                var river = new PlannedRiver { Index = i, FordPeriod = Math.Max(2, v.RiverFordPeriod), FordWidth = Math.Max(1, v.RiverFordWidth) };
                int x = CoreX + (int)(((long)v.RiverStartDistance * Cos(baseAngle) + 32768) >> 16);
                int y = CoreY + (int)(((long)v.RiverStartDistance * Cos(baseAngle - 90) + 32768) >> 16);
                river.Points.Add(new GridPoint(x, y));
                int dir = baseAngle;
                int along = 0;
                for (int k = 0; k < segs; k++)
                {
                    uint hk = WorldGenMath.Hash(seed, i, k + 1, RiverSalt);
                    int turn = v.RiverTurnMax > 0 ? (int)(hk % (uint)(2 * v.RiverTurnMax + 1)) - v.RiverTurnMax : 0;
                    int radial = RadialAngle(x - CoreX, y - CoreY);
                    int next = dir + turn;
                    int off = SignedAngle(next, radial);
                    int outward = v.RiverOutwardMaxAngle;
                    if (off > outward)
                    {
                        next = radial + outward;
                    }
                    else if (off < -outward)
                    {
                        next = radial - outward;
                    }
                    dir = ((next % 360) + 360) % 360;
                    int nx = x + (int)(((long)step * Cos(dir) + 32768) >> 16);
                    int ny = y + (int)(((long)step * Cos(dir - 90) + 32768) >> 16);
                    int half = Math.Min(Math.Max(1, v.RiverHalfWidthMax), Math.Max(1, v.RiverHalfWidth) + k * Math.Max(0, v.RiverWidenTotal) / Math.Max(1, segs));
                    river.HalfWidths.Add(half);
                    zones.Add(Segment(WorldGenZoneKind.River, x, y, nx, ny, half, 0, river.FordPeriod, along, river.FordWidth));
                    long sx = nx - x;
                    long sy = ny - y;
                    along += WorldGenMath.Isqrt(sx * sx + sy * sy);
                    x = nx;
                    y = ny;
                    river.Points.Add(new GridPoint(x, y));
                }
                river.Length = along;
                Rivers.Add(river);
                Log.Add($"河流 {i}：从 {river.Points[0]} 向外 {segs} 段、全长 {along} 格，浅滩每 {river.FordPeriod} 格一处");
            }
        }

        /// <summary>
        /// FG3-GEN-01（FGR-GEN-020）：矿带。第 i 条的起点距离核心 [beltMinDistance, beltMaxDistance]、方向与走向由子种子（种子, i）给出；
        /// 每 beltRareEvery 条有一条是稀土矿带，其余是金属矿带。矿带内对应矿种的噪声阈值下降 beltOreBonus（矿更密、成带分布）。
        /// </summary>
        private void PlanBelts(uint seed, WorldGenVersion v, List<WorldGenZone> zones)
        {
            int count = Math.Max(0, v.BeltCount);
            if (count == 0)
            {
                return;
            }
            byte metal = Grid.GridContent.TerrainCode("ore_metal");
            byte rare = Grid.GridContent.TerrainCode("ore_rare");
            int bonus = WorldTerrainSource.Q(v.BeltOreBonus);
            for (int i = 0; i < count; i++)
            {
                uint h = WorldGenMath.Hash(seed, i, 0, BeltSalt);
                uint h2 = WorldGenMath.Hash(seed, i, 1, BeltSalt);
                int angle = (int)(h % 360u);
                int span = Math.Max(0, v.BeltMaxDistance - v.BeltMinDistance);
                int dist = v.BeltMinDistance + (int)((h >> 9) % (uint)(span + 1));
                int heading = (int)(h2 % 360u);
                int x0 = CoreX + (int)(((long)dist * Cos(angle) + 32768) >> 16);
                int y0 = CoreY + (int)(((long)dist * Cos(angle - 90) + 32768) >> 16);
                int x1 = x0 + (int)(((long)v.BeltLength * Cos(heading) + 32768) >> 16);
                int y1 = y0 + (int)(((long)v.BeltLength * Cos(heading - 90) + 32768) >> 16);
                bool isRare = v.BeltRareEvery > 0 && (i + 1) % v.BeltRareEvery == 0;
                var belt = new PlannedBelt
                {
                    Index = i,
                    Terrain = isRare ? "ore_rare" : "ore_metal",
                    X0 = x0,
                    Y0 = y0,
                    X1 = x1,
                    Y1 = y1,
                    Radius = Math.Max(1, v.BeltRadius),
                    BonusQ = bonus,
                };
                Belts.Add(belt);
                zones.Add(Segment(WorldGenZoneKind.Belt, x0, y0, x1, y1, belt.Radius, isRare ? rare : metal, bonus, 0, 0));
            }
        }

        /// <summary>线段形状（河流段 / 矿带）：包围盒 = 线段外扩半宽。</summary>
        public static WorldGenZone Segment(int kind, int x0, int y0, int x1, int y1, int radius, int code, int valueQ, int extra, int fordWidth) =>
            new WorldGenZone
            {
                Kind = kind,
                CenterX = x0,
                CenterY = y0,
                X1 = x1,
                Y1 = y1,
                Radius = radius,
                Code = code,
                ValueQ = valueQ,
                Extra = extra,
                HazardWidth = fordWidth,
                BoxMinX = Math.Min(x0, x1) - radius,
                BoxMinY = Math.Min(y0, y1) - radius,
                BoxMaxX = Math.Max(x0, x1) + radius,
                BoxMaxY = Math.Max(y0, y1) + radius,
            };

        /// <summary>保证点形状（圆盘）。</summary>
        public static WorldGenZone Stamp(int cx, int cy, int radius, int code) =>
            new WorldGenZone
            {
                Kind = WorldGenZoneKind.Stamp,
                CenterX = cx,
                CenterY = cy,
                X1 = cx,
                Y1 = cy,
                Radius = radius,
                Code = code,
                BoxMinX = cx - radius,
                BoxMinY = cy - radius,
                BoxMaxX = cx + radius,
                BoxMaxY = cy + radius,
            };

        /// <summary>向量 (dx, dy) 的方向（整数度，0 = +X，90 = +Y），查余弦表逐度比较，与平台无关。</summary>
        public static int RadialAngle(long dx, long dy)
        {
            if (dx == 0 && dy == 0)
            {
                return 0;
            }
            int best = 0;
            long bestDot = long.MinValue;
            for (int a = 0; a < 360; a++)
            {
                long dot = dx * Cos(a) + dy * Cos(a - 90);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = a;
                }
            }
            return best;
        }

        /// <summary>a 相对 b 的有符号夹角（-180～180）。</summary>
        public static int SignedAngle(int a, int b)
        {
            int d = ((a - b) % 360 + 360) % 360;
            return d > 180 ? d - 360 : d;
        }

        private static PlannedTerritory Make(Territory row, int hazard, int angle, int dist, int coreX, int coreY, int attempts)
        {
            int cos = Cos(angle);
            int sin = Cos(angle - 90);
            return new PlannedTerritory
            {
                Id = row.Id,
                NameKey = row.NameKey,
                HazardKind = row.HazardKind,
                HazardKey = row.HazardKey,
                Act = row.Act,
                IsFaction = row.Faction == 1,
                AngleDeg = angle,
                Distance = dist,
                CenterX = coreX + (int)(((long)dist * cos + 32768) >> 16),
                CenterY = coreY + (int)(((long)dist * sin + 32768) >> 16),
                Radius = row.Radius,
                HazardWidth = hazard,
                Attempts = attempts,
            };
        }

        /// <summary>不合法原因；合法返回 null。</summary>
        private string Reject(PlannedTerritory c)
        {
            long cdx = c.CenterX - CoreX;
            long cdy = c.CenterY - CoreY;
            int centerDist = WorldGenMath.Isqrt(cdx * cdx + cdy * cdy);
            if (centerDist - c.OuterRadius <= HomeZoneRadius)
            {
                return "进入家园区";
            }
            foreach (PlannedTerritory t in Territories)
            {
                if (c.IsFaction && t.IsFaction && AngleBetween(c.AngleDeg, t.AngleDeg) < MinAngleDeg)
                {
                    return $"与{t.Id}方向夹角不足";
                }
                long dx = c.CenterX - t.CenterX;
                long dy = c.CenterY - t.CenterY;
                long need = (long)c.OuterRadius + t.OuterRadius;
                if (dx * dx + dy * dy < need * need)
                {
                    return $"与{t.Id}重叠";
                }
            }
            return null;
        }

        /// <summary>整数度的余弦（16.16 定点，查表；表由 round(cos × 65536) 离线生成，与平台无关）。</summary>
        public static int Cos(int degrees)
        {
            int d = ((degrees % 360) + 360) % 360;
            return CosTable[d];
        }

        private static readonly int[] CosTable =
        {
            65536, 65526, 65496, 65446, 65376, 65287, 65177, 65048, 64898, 64729, 64540, 64332,
            64104, 63856, 63589, 63303, 62997, 62672, 62328, 61966, 61584, 61183, 60764, 60326,
            59870, 59396, 58903, 58393, 57865, 57319, 56756, 56175, 55578, 54963, 54332, 53684,
            53020, 52339, 51643, 50931, 50203, 49461, 48703, 47930, 47143, 46341, 45525, 44695,
            43852, 42995, 42126, 41243, 40348, 39441, 38521, 37590, 36647, 35693, 34729, 33754,
            32768, 31772, 30767, 29753, 28729, 27697, 26656, 25607, 24550, 23486, 22415, 21336,
            20252, 19161, 18064, 16962, 15855, 14742, 13626, 12505, 11380, 10252, 9121, 7987,
            6850, 5712, 4572, 3430, 2287, 1144, 0, -1144, -2287, -3430, -4572, -5712,
            -6850, -7987, -9121, -10252, -11380, -12505, -13626, -14742, -15855, -16962, -18064, -19161,
            -20252, -21336, -22415, -23486, -24550, -25607, -26656, -27697, -28729, -29753, -30767, -31772,
            -32768, -33754, -34729, -35693, -36647, -37590, -38521, -39441, -40348, -41243, -42126, -42995,
            -43852, -44695, -45525, -46341, -47143, -47930, -48703, -49461, -50203, -50931, -51643, -52339,
            -53020, -53684, -54332, -54963, -55578, -56175, -56756, -57319, -57865, -58393, -58903, -59396,
            -59870, -60326, -60764, -61183, -61584, -61966, -62328, -62672, -62997, -63303, -63589, -63856,
            -64104, -64332, -64540, -64729, -64898, -65048, -65177, -65287, -65376, -65446, -65496, -65526,
            -65536, -65526, -65496, -65446, -65376, -65287, -65177, -65048, -64898, -64729, -64540, -64332,
            -64104, -63856, -63589, -63303, -62997, -62672, -62328, -61966, -61584, -61183, -60764, -60326,
            -59870, -59396, -58903, -58393, -57865, -57319, -56756, -56175, -55578, -54963, -54332, -53684,
            -53020, -52339, -51643, -50931, -50203, -49461, -48703, -47930, -47143, -46341, -45525, -44695,
            -43852, -42995, -42126, -41243, -40348, -39441, -38521, -37590, -36647, -35693, -34729, -33754,
            -32768, -31772, -30767, -29753, -28729, -27697, -26656, -25607, -24550, -23486, -22415, -21336,
            -20252, -19161, -18064, -16962, -15855, -14742, -13626, -12505, -11380, -10252, -9121, -7987,
            -6850, -5712, -4572, -3430, -2287, -1144, 0, 1144, 2287, 3430, 4572, 5712,
            6850, 7987, 9121, 10252, 11380, 12505, 13626, 14742, 15855, 16962, 18064, 19161,
            20252, 21336, 22415, 23486, 24550, 25607, 26656, 27697, 28729, 29753, 30767, 31772,
            32768, 33754, 34729, 35693, 36647, 37590, 38521, 39441, 40348, 41243, 42126, 42995,
            43852, 44695, 45525, 46341, 47143, 47930, 48703, 49461, 50203, 50931, 51643, 52339,
            53020, 53684, 54332, 54963, 55578, 56175, 56756, 57319, 57865, 58393, 58903, 59396,
            59870, 60326, 60764, 61183, 61584, 61966, 62328, 62672, 62997, 63303, 63589, 63856,
            64104, 64332, 64540, 64729, 64898, 65048, 65177, 65287, 65376, 65446, 65496, 65526,
        };
    }
}
