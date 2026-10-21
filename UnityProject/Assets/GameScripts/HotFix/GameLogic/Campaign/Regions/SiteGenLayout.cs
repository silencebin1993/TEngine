using System;
using System.Collections.Generic;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>远征地点里的一个领地驻军据点（地点局部坐标 + 星球格 + 等级）。</summary>
    public sealed class SiteGarrisonPoint
    {
        public int Index;
        public string OutpostId;
        public Vector2 Local;
        public int CellX;
        public int CellY;
        public int Tier;
    }

    /// <summary>一个远征地点内部的生成内容（全部是地点局部坐标，与 Demo 布局同一坐标系：+Y 朝外 / 朝核心区）。</summary>
    public sealed class SiteGenContent
    {
        public string SiteId;
        public string TerritoryId;
        public PlannedAnchor Anchor;
        public SiteGen Row;
        /// <summary>地点窗口（相机与机器活动范围）：中心与半宽 / 半高。</summary>
        public Vector2 Center;
        public float HalfX;
        public float HalfY;
        /// <summary>星球真实地形里不可通行的格（悬崖 / 水面）→ 障碍圆（x, y, 半径）。</summary>
        public readonly List<Vector3> Obstacles = new List<Vector3>(64);
        /// <summary>每个障碍圆对应的地形字节值（表现层按地形上色）。</summary>
        public readonly List<byte> ObstacleTerrain = new List<byte>(64);
        /// <summary>可以穿过的废墟碎石格（只做表现，不挡路）。</summary>
        public readonly List<Vector2> Rubble = new List<Vector2>(64);
        public readonly List<SiteGarrisonPoint> Garrisons = new List<SiteGarrisonPoint>(4);
        /// <summary>熔渣热区（x, y, 半径）。</summary>
        public readonly List<Vector3> SlagZones = new List<Vector3>(4);
        /// <summary>取样窗口里与入口连通的可走格数 / 全部可走格数（自检读点：驻军都放在连通的格上）。</summary>
        public int ReachableSamples;
        public int WalkableSamples;
        /// <summary>修复轮（第 1 轮审查 P1“外圈真实地形被截断”）：窗口里不可通行的取样格数 / 被障碍圆盖住的格数（自检断言两者相等：一格不丢）、
        /// 合并档位（0 = 每格一个圆 + 内部大圆；1 = 细长地形相邻两格合一个圆；2 = 3×3 合一个圆）。</summary>
        public int BlockedSamples;
        public int CoveredBlocked;
        public int MergeLevel;
        /// <summary>修复轮（第 1 轮审查 P1“核心门只按 Y 线判断”）：外圈的上沿。铸造前哨 = 核心门封锁线——封锁线以上只有 Demo 手工的核心区（核心区两侧与上方是要塞外墙外的禁区，
        /// 不放生成内容、不能进入），核心门 / 首领场地“越过 Y 线”的判定因此仍然等价于“进了核心区”；破碎都市没有核心门，= 正无穷。</summary>
        public float OuterMaxY = float.PositiveInfinity;
        /// <summary>驻军放不下时的说明（可见日志；空 = 全部放下）。</summary>
        public string Problem = string.Empty;

        public bool InDemoRect(Vector2 p, float pad) =>
            Mathf.Abs(p.x) <= Row.DemoHalfX + pad && p.y >= Row.DemoMinY - pad && p.y <= Row.DemoMaxY + pad;

        public bool InWindow(Vector2 p, float pad) =>
            Mathf.Abs(p.x - Center.x) <= HalfX - pad && Mathf.Abs(p.y - Center.y) <= HalfY - pad;

        /// <summary>外圈上沿以上、又不在 Demo 矩形里的禁区（铸造前哨核心区两侧 / 上方）。</summary>
        public bool InOuterBand(Vector2 p) => p.y > OuterMaxY && !InDemoRect(p, 0f);

        /// <summary>机器能去的地方：Demo 手工布局的矩形，或窗口里外圈上沿以下的部分。</summary>
        public bool Playable(Vector2 p) => InDemoRect(p, 0f) || (InWindow(p, 0f) && p.y <= OuterMaxY);

        /// <summary>移动命令的目的地夹进能去的范围（窗口外 → 窗口边；禁区 → 离它最近的 Demo 矩形边或外圈上沿）。O(1)。</summary>
        public Vector2 ClampToPlayable(Vector2 p)
        {
            if (Playable(p))
            {
                return p;
            }
            var q = new Vector2(Mathf.Clamp(p.x, Center.x - HalfX, Center.x + HalfX), Mathf.Clamp(p.y, Center.y - HalfY, Center.y + HalfY));
            if (Playable(q))
            {
                return q;
            }
            var inDemo = new Vector2(Mathf.Clamp(q.x, -Row.DemoHalfX, Row.DemoHalfX), Mathf.Clamp(q.y, Row.DemoMinY, Row.DemoMaxY));
            var outer = new Vector2(q.x, OuterMaxY);
            return (inDemo - q).sqrMagnitude <= (outer - q).sqrMagnitude ? inDemo : outer;
        }

        public SiteGarrisonPoint GarrisonByOutpost(string outpostId)
        {
            foreach (SiteGarrisonPoint g in Garrisons)
            {
                if (g.OutpostId == outpostId)
                {
                    return g;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// FG9-ACT1-01（承接 DEBT-FG0ARCH01-01 / DEBT-FG8EXP03-06 / DEBT-FG9FAC02-02；ADR-NAR-002）：远征地点内部的“生成的领地内容”——纯函数（世界种子、生成器规划层、
    /// 锚点、fg.TbSiteGen），不进存档、读档重算；不是世界生成输入（不改星球地形，v3 冻结基准不变）。
    ///
    /// - 地形：地点窗口里每 sampleStep 格取一次**星球真实地形**（锚点朝向变换 <see cref="TerritoryPlanner.ToWorld"/>，与区块生成同一个纯整数采样函数，Burst）；
    ///   不可通行的格（fg.TbGridTerrain.navCost = 0：悬崖、水面）成为障碍圆，废墟碎石只做表现。Demo 手工布局的矩形里不放任何生成内容（Demo 内容原样回归）。
    /// - 驻军据点：按（世界种子, 地点, 序号, 第几次试探）放在窗口里、Demo 矩形外至少 margin 米、与入口连通的可走格上、彼此至少 spacing 米；等级按子种子。
    /// - 熔渣热区（铸造前哨）：同样按种子放在 Demo 矩形之外（不改 Demo 主核心战的热量节奏，ADR-FAC-003 第 9 节第 13 条）。
    /// 开销：每个存档每个地点算一次（约 1,600 个取样格 + 至多 garrisons × tries 次试探，一次 Burst 批量取样），之后 O(1) 取缓存。
    /// 没有生成器规划层或锚点的世界（v1 / v2、自检里的旧原型世界）返回 null：地点内部保持 Demo 原样。
    /// </summary>
    public static class SiteGenLayout
    {
        private const uint GarrisonSalt = 0x53474152u; // "SGAR"
        private const uint SlagSalt = 0x53534C47u;     // "SSLG"

        private static readonly Dictionary<string, SiteGenContent> Cache = new Dictionary<string, SiteGenContent>();
        private static object _cacheState;
        private static object _cachePlan;
        private static object _cacheTable;

        /// <summary>自检读点：算过几次（缓存命中不算）。</summary>
        public static int BuildCount { get; private set; }

        public static string[] SiteIds => ExpeditionSiteAnchors.SiteIds;

        public static void ResetCache()
        {
            Cache.Clear();
            _cacheState = null;
            _cachePlan = null;
            _cacheTable = null;
        }

        public static SiteGen RowOf(string siteId)
        {
            TbSiteGen table = Table();
            if (table == null || string.IsNullOrEmpty(siteId))
            {
                return null;
            }
            foreach (SiteGen r in table.DataList)
            {
                if (r != null && r.Site == siteId)
                {
                    return r;
                }
            }
            return null;
        }

        private static TbSiteGen Table()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbSiteGen;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>本存档里这个远征地点的生成内容；没有（旧世界 / 表没加载）返回 null。</summary>
        public static SiteGenContent For(CampaignState s, string siteId)
        {
            if (s == null || string.IsNullOrEmpty(siteId) || !WorldGenService.UsesWorldGen(s))
            {
                return null;
            }
            WorldPlan plan = WorldGenService.PlanFor(s);
            TbSiteGen table = Table();
            if (plan == null || table == null)
            {
                return null;
            }
            if (!ReferenceEquals(_cacheState, s) || !ReferenceEquals(_cachePlan, plan) || !ReferenceEquals(_cacheTable, table))
            {
                Cache.Clear();
                _cacheState = s;
                _cachePlan = plan;
                _cacheTable = table;
            }
            if (Cache.TryGetValue(siteId, out SiteGenContent hit))
            {
                return hit;
            }
            SiteGenContent built = Build(s, siteId, plan);
            Cache[siteId] = built;
            return built;
        }

        private static SiteGenContent Build(CampaignState s, string siteId, WorldPlan plan)
        {
            SiteGen row = RowOf(siteId);
            PlannedAnchor anchor = TerritoryAnchorChecks.AnchorOfSite(s, siteId);
            WorldTerrainSource src = WorldGenService.ContextFor(s)?.Source;
            if (row == null || anchor == null || src == null)
            {
                return null;
            }
            BuildCount++;
            var c = new SiteGenContent
            {
                SiteId = siteId,
                TerritoryId = row.TerritoryId,
                Anchor = anchor,
                Row = row,
                Center = new Vector2(0f, (row.MinY + row.MaxY) * 0.5f),
                HalfX = row.HalfX,
                HalfY = (row.MaxY - row.MinY) * 0.5f,
                OuterMaxY = siteId == FoundryOutpostLayout.RegionId ? FoundryOutpostLayout.CoreGateBlockLineY : float.PositiveInfinity,
            };
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            int key = Array.IndexOf(SiteIds, siteId) + 1;

            // ── 取样格（窗口内每 step 格一个，行优先；Demo 矩形里的格只参与连通，不产生障碍）──
            int step = Math.Max(1, row.SampleStep);
            int nx = Math.Max(1, Mathf.FloorToInt(2f * row.HalfX / step) + 1);
            int ny = Math.Max(1, Mathf.FloorToInt((row.MaxY - row.MinY) / step) + 1);
            int n = nx * ny;
            var xs = new int[n];
            var ys = new int[n];
            var local = new Vector2[n];
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    var p = new Vector2(-row.HalfX + i * step, row.MinY + j * step);
                    local[k] = p;
                    TerritoryPlanner.ToWorld(anchor, p.x, p.y, out xs[k], out ys[k]);
                }
            }
            var terrain = new byte[n];
            WorldGenKernel.SampleCells(in src.Params, src.Rects, src.Zones, xs, ys, n, terrain);
            var walk = new bool[n];
            var blocked = new bool[n];
            for (int k = 0; k < n; k++)
            {
                // Demo 矩形里一律当可走（Demo 布局在平整范围里；就算过渡带落进来，也不让生成地形改 Demo 内容）。
                // 修复轮：外圈上沿以上的禁区（铸造前哨核心区两侧 / 上方）不可进入，也不放障碍（没人能走到那里）。
                bool demo = c.InDemoRect(local[k], 0f);
                bool band = !demo && c.InOuterBand(local[k]);
                bool passable = NavCostOf(terrain[k]) > 0;
                walk[k] = demo || (!band && passable);
                blocked[k] = !demo && !band && !passable;
                if (walk[k])
                {
                    c.WalkableSamples++;
                }
                if (walk[k] && !demo && GridContent.TerrainByCode(terrain[k])?.Id == "ruin" && c.Rubble.Count < 240)
                {
                    c.Rubble.Add(local[k]);
                }
            }
            // ── 障碍圆（修复轮，第 1 轮审查 P1）：原来按离中心由近到远只留 obstacleMax 个、其余丢掉（四到七成悬崖 / 水面在地点里不存在）。
            //    现在用“内切大圆 + 逐档合并”盖住全部不可通行格：先按到最近可走格的距离从大到小放圆（内部一个大圆盖住一片），
            //    放完还超过上限就逐档放宽合并（细长地形相邻两格 / 3×3 合一个圆），保证一格不丢；最后一档仍超上限才截断并写进 Problem（自检断言没有截断）。
            CoverObstacles(c, row, step, nx, ny, local, terrain, blocked);
            // ── 与入口连通的可走格（4 邻接洪泛；入口 = Demo 撤离点所在的取样格）——按放好的障碍圆重算（合并档位的圆会多盖住一点可走格，
            //    驻军只放在“按真实障碍圆仍连通”的格上）──
            for (int k = 0; k < n; k++)
            {
                if (walk[k] && !c.InDemoRect(local[k], 0f) && InObstacle(c, local[k], 0.5f))
                {
                    walk[k] = false;
                }
            }
            bool[] reach = Flood(walk, nx, ny, SampleIndex(c, row, step, nx, ny, EntryLocal(siteId)));
            for (int k = 0; k < n; k++)
            {
                if (reach[k])
                {
                    c.ReachableSamples++;
                }
            }

            // ── 驻军据点 ──
            int tries = Math.Max(1, Mathf.RoundToInt(Tuning("site.garrison.tries", 24f)));
            for (int gi = 0; gi < Math.Max(0, row.Garrisons); gi++)
            {
                SiteGarrisonPoint placed = null;
                for (int t = 0; t < tries && placed == null; t++)
                {
                    uint h = WorldGenMath.Hash(seed, key * 64 + gi, t, GarrisonSalt);
                    uint h2 = WorldGenMath.Hash(seed, key * 64 + gi, t, GarrisonSalt ^ 0x9E3779B9u);
                    float fx = (h & 0xFFFF) / 65535f;
                    float fy = ((h >> 16) & 0xFFFF) / 65535f;
                    var p = new Vector2(Mathf.Round((-row.HalfX + 4f + fx * (2f * row.HalfX - 8f)) * 2f) / 2f,
                        Mathf.Round((row.MinY + 4f + fy * (row.MaxY - row.MinY - 8f)) * 2f) / 2f);
                    if (c.InDemoRect(p, row.Margin) || !BelowOuterEdge(c, p, GarrisonEdgePad) || !NearReachable(reach, c, row, step, nx, ny, p) || TooClose(c, p, row.Spacing))
                    {
                        continue;
                    }
                    int span = Math.Max(0, row.TierMax - row.TierMin);
                    TerritoryPlanner.ToWorld(anchor, p.x, p.y, out int wx, out int wy);
                    placed = new SiteGarrisonPoint
                    {
                        Index = gi,
                        OutpostId = SiteGarrisonService.OutpostIdOf(siteId, gi),
                        Local = p,
                        CellX = wx,
                        CellY = wy,
                        Tier = row.TierMin + (int)(h2 % (uint)(span + 1)),
                    };
                }
                if (placed == null)
                {
                    // 兜底（FGR-GEN-090 同一思路）：从子种子给的起点按取样格顺序扫一遍，取第一个合格的格——仍是种子的纯函数，只要窗口里有合格的格就一定放得下。
                    uint hs = WorldGenMath.Hash(seed, key * 64 + gi, tries, GarrisonSalt);
                    for (int q = 0; q < n && placed == null; q++)
                    {
                        int k = (int)((hs + (uint)q * 7919u) % (uint)n);
                        Vector2 p = local[k];
                        if (c.InDemoRect(p, row.Margin) || !c.InWindow(p, 4f) || !BelowOuterEdge(c, p, GarrisonEdgePad) || !NearReachable(reach, c, row, step, nx, ny, p) || TooClose(c, p, row.Spacing))
                        {
                            continue;
                        }
                        TerritoryPlanner.ToWorld(anchor, p.x, p.y, out int wx, out int wy);
                        placed = new SiteGarrisonPoint
                        {
                            Index = gi,
                            OutpostId = SiteGarrisonService.OutpostIdOf(siteId, gi),
                            Local = p,
                            CellX = wx,
                            CellY = wy,
                            Tier = row.TierMin + (int)(hs % (uint)(Math.Max(0, row.TierMax - row.TierMin) + 1)),
                        };
                    }
                }
                if (placed == null)
                {
                    c.Problem += $"驻军据点 {gi + 1}：{tries} 次试探与逐格扫描都没有合适的落点（窗口里可走且与入口连通的格太少）；";
                    continue;
                }
                c.Garrisons.Add(placed);
            }

            // ── 熔渣热区 ──
            for (int zi = 0; zi < Math.Max(0, row.SlagZones); zi++)
            {
                for (int t = 0; t < tries; t++)
                {
                    uint h = WorldGenMath.Hash(seed, key * 64 + zi, t, SlagSalt);
                    uint h2 = WorldGenMath.Hash(seed, key * 64 + zi, t, SlagSalt ^ 0x9E3779B9u);
                    float r = row.SlagRadiusMin + (row.SlagRadiusMax - row.SlagRadiusMin) * ((h2 & 0xFFFF) / 65535f);
                    float fx = (h & 0xFFFF) / 65535f;
                    float fy = ((h >> 16) & 0xFFFF) / 65535f;
                    var p = new Vector2(-row.HalfX + r + fx * (2f * row.HalfX - 2f * r), row.MinY + r + fy * (row.MaxY - row.MinY - 2f * r));
                    if (c.InDemoRect(p, r + 2f) || !BelowOuterEdge(c, p, r + 2f) || OverlapsZone(c, p, r))
                    {
                        continue;
                    }
                    c.SlagZones.Add(new Vector3(p.x, p.y, r));
                    break;
                }
            }
            if (!string.IsNullOrEmpty(c.Problem))
            {
                Log.Warning($"[SiteGenLayout] {siteId} 种子 {s.World?.WorldSeed}：{c.Problem}");
            }
            return c;
        }

        /// <summary>入口（Demo 撤离点）的局部坐标。</summary>
        public static Vector2 EntryLocal(string siteId) =>
            siteId == FoundryOutpostLayout.RegionId ? FoundryOutpostLayout.EntryEvac.Position : FracturedCityLayout.EntryEvac.Position;

        private static int NavCostOf(byte code) => GridContent.TerrainByCode(code)?.NavCost ?? 1;

        private static int SampleIndex(SiteGenContent c, SiteGen row, int step, int nx, int ny, Vector2 p)
        {
            int i = Mathf.Clamp(Mathf.RoundToInt((p.x + row.HalfX) / step), 0, nx - 1);
            int j = Mathf.Clamp(Mathf.RoundToInt((p.y - row.MinY) / step), 0, ny - 1);
            return j * nx + i;
        }

        private static bool[] Flood(bool[] walk, int nx, int ny, int start)
        {
            var seen = new bool[walk.Length];
            if (start < 0 || start >= walk.Length || !walk[start])
            {
                return seen;
            }
            var stack = new Stack<int>(64);
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                int i = k % nx;
                int j = k / nx;
                if (i > 0) Visit(k - 1);
                if (i < nx - 1) Visit(k + 1);
                if (j > 0) Visit(k - nx);
                if (j < ny - 1) Visit(k + nx);
            }
            return seen;

            void Visit(int q)
            {
                if (!seen[q] && walk[q])
                {
                    seen[q] = true;
                    stack.Push(q);
                }
            }
        }

        /// <summary>点所在的取样格与它周围一圈都与入口连通（驻军不会落在孤岛、也不会紧贴悬崖 / 水边）。</summary>
        private static bool NearReachable(bool[] reach, SiteGenContent c, SiteGen row, int step, int nx, int ny, Vector2 p)
        {
            int ci = Mathf.RoundToInt((p.x + row.HalfX) / step);
            int cj = Mathf.RoundToInt((p.y - row.MinY) / step);
            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    int i = ci + di;
                    int j = cj + dj;
                    if (i < 0 || j < 0 || i >= nx || j >= ny || !reach[j * nx + i])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>驻军据点离外圈上沿至少这么远（驻军单位围着据点排开，整圈都在封锁线以下）。</summary>
        private const float GarrisonEdgePad = 8f;

        private static bool BelowOuterEdge(SiteGenContent c, Vector2 p, float pad) => float.IsPositiveInfinity(c.OuterMaxY) || p.y <= c.OuterMaxY - pad;

        /// <summary>
        /// 修复轮（第 1 轮审查 P1）：用障碍圆盖住窗口里全部不可通行的取样格。
        /// 每格的“净空” = 到最近一个可走格（或窗口外）的距离；按净空从大到小放圆：圆心在这一格，半径 = 净空 − 半格 + 单格圆的外伸（不比单格圆更多地压到可走格上），
        /// 圆里的不可通行格都算盖住。档位 0 的最小半径 = 单格圆；超上限就换档（1：最小半径够盖住正交相邻格，细长悬崖两格一个圆；2：够盖住斜邻格，3×3 一个圆）。
        /// 每档 O(不可通行格数 × 半径²)，整个地点每个存档算一次。
        /// </summary>
        private static void CoverObstacles(SiteGenContent c, SiteGen row, int step, int nx, int ny, Vector2[] local, byte[] terrain, bool[] blocked)
        {
            int n = nx * ny;
            var cells = new List<int>(64);
            for (int k = 0; k < n; k++)
            {
                if (blocked[k])
                {
                    cells.Add(k);
                }
            }
            c.BlockedSamples = cells.Count;
            if (cells.Count == 0)
            {
                return;
            }
            const int Reach = 8;
            var clear = new float[n];
            foreach (int k in cells)
            {
                int i = k % nx;
                int j = k / nx;
                float best = (Reach + 1) * step;
                for (int dj = -Reach; dj <= Reach; dj++)
                {
                    for (int di = -Reach; di <= Reach; di++)
                    {
                        int ii = i + di;
                        int jj = j + dj;
                        bool free = ii < 0 || jj < 0 || ii >= nx || jj >= ny || !blocked[jj * nx + ii];
                        if (free)
                        {
                            best = Mathf.Min(best, step * Mathf.Sqrt(di * di + dj * dj));
                        }
                    }
                }
                clear[k] = best;
            }
            cells.Sort((a, b) =>
            {
                int d = clear[b].CompareTo(clear[a]);
                return d != 0 ? d : a.CompareTo(b);
            });
            float overhang = Mathf.Max(0f, row.ObstacleRadius - step * 0.5f);
            float[] minRadius = { row.ObstacleRadius, step + 0.05f, step * 1.4143f + 0.05f };
            int cap = Math.Max(0, row.ObstacleMax);
            var circles = new List<(int Cell, float Radius)>(cells.Count);
            var covered = new bool[n];
            for (int level = 0; level < minRadius.Length; level++)
            {
                circles.Clear();
                Array.Clear(covered, 0, n);
                foreach (int k in cells)
                {
                    if (covered[k])
                    {
                        continue;
                    }
                    float r = Mathf.Max(minRadius[level], clear[k] - step * 0.5f + overhang);
                    // Demo 手工布局的矩形不被合并的大圆压进去（最多和单格圆一样外伸）：Demo 内容原样回归。
                    Vector2 p = local[k];
                    float dx = Mathf.Max(0f, Mathf.Abs(p.x) - row.DemoHalfX);
                    float dy = Mathf.Max(0f, Mathf.Max(row.DemoMinY - p.y, p.y - row.DemoMaxY));
                    r = Mathf.Min(r, Mathf.Max(row.ObstacleRadius, Mathf.Sqrt(dx * dx + dy * dy) + overhang));
                    circles.Add((k, r));
                    int i = k % nx;
                    int j = k / nx;
                    int rc = Mathf.CeilToInt(r / step);
                    for (int dj = -rc; dj <= rc; dj++)
                    {
                        for (int di = -rc; di <= rc; di++)
                        {
                            int ii = i + di;
                            int jj = j + dj;
                            if (ii < 0 || jj < 0 || ii >= nx || jj >= ny)
                            {
                                continue;
                            }
                            int q = jj * nx + ii;
                            if (blocked[q] && step * Mathf.Sqrt(di * di + dj * dj) < r)
                            {
                                covered[q] = true;
                            }
                        }
                    }
                }
                c.MergeLevel = level;
                if (circles.Count <= cap)
                {
                    break;
                }
            }
            if (circles.Count > cap)
            {
                // 最后一档仍超上限：离窗口中心由近到远保留（可见日志；自检断言不发生）。
                circles.Sort((a, b) =>
                {
                    int d = (local[a.Cell] - c.Center).sqrMagnitude.CompareTo((local[b.Cell] - c.Center).sqrMagnitude);
                    return d != 0 ? d : a.Cell.CompareTo(b.Cell);
                });
                c.Problem += $"地形障碍合并到第 {c.MergeLevel} 档仍有 {circles.Count} 个圆（上限 {cap}），远处的被截断；";
                circles.RemoveRange(cap, circles.Count - cap);
            }
            foreach ((int k, float r) in circles)
            {
                c.Obstacles.Add(new Vector3(local[k].x, local[k].y, r));
                c.ObstacleTerrain.Add(terrain[k]);
            }
            foreach (int k in cells)
            {
                if (InObstacle(c, local[k]))
                {
                    c.CoveredBlocked++;
                }
            }
        }

        private static bool TooClose(SiteGenContent c, Vector2 p, float spacing)
        {
            foreach (SiteGarrisonPoint g in c.Garrisons)
            {
                if ((g.Local - p).sqrMagnitude < spacing * spacing)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool OverlapsZone(SiteGenContent c, Vector2 p, float r)
        {
            foreach (Vector3 z in c.SlagZones)
            {
                float need = z.z + r;
                if ((new Vector2(z.x, z.y) - p).sqrMagnitude < need * need)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>局部坐标是不是落在某个地形障碍圆里（自检 / 机器出生落点用）。O(障碍数)。</summary>
        public static bool InObstacle(SiteGenContent c, Vector2 p, float pad = 0f)
        {
            if (c == null)
            {
                return false;
            }
            foreach (Vector3 o in c.Obstacles)
            {
                float rr = o.z + pad;
                if ((new Vector2(o.x, o.y) - p).sqrMagnitude < rr * rr)
                {
                    return true;
                }
            }
            return false;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
