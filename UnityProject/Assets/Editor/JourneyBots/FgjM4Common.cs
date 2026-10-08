using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG4-E2E-01：M4 两条旅程（FGJ-M4 / FGJ-M4R）共用的部分。
    /// ① 产线规划（B25：按种子地形现找，不写死坐标）——一座“从原料到机器”的工厂块：精炼炉 → 分流器 → 两座零件工坊（结构材 / 零件）+ 电子组装台 → 组件工坊，
    ///    采矿用提取钻（金属矿脉 / 稀土矿脉）、废料用压在废墟上的回收站，成品经一条干线带（侧向汇入）送进仓库 / 归还核心输入口；没在归还核心配电范围里的建筑按需串 T2 电塔。
    ///    规划只读游戏状态（地形、占用、放置校验），不调放置、施工、配方等业务方法。
    /// ② 动作队列：把规划出的“放建筑 / 放分流器 / 拖一条路线”逐个用正式输入做出来（建造栏点分类与条目、旋转键、指着格子单击、按住左键拖），
    ///    每个动作做完核对它成了规划中的虚影（或已建成）才做下一个；同一步重试时已经做完的动作跳过（幂等）。
    /// ③ 面板操作：建造模式里点建筑打开通用面板、配方下拉选配方、关闭面板。
    /// 全部玩家动作走 <see cref="JourneyInput"/> 的正式输入通道（[M4 出口] A2 段扫描源码守护）。
    /// </summary>
    internal static class FgjM4Common
    {
        internal const string Drill = "extraction_drill";
        internal const string Furnace = "refinery_furnace";
        internal const string PartsWs = "parts_workshop";
        internal const string ElecBench = "electronics_bench";
        internal const string CompWs = "component_workshop";
        internal const string Recycler = "recycler";
        internal const string Tower = "refinery_tower";
        internal const string Pond = "waste_pond";
        internal const string PoleT2 = "power_pole_t2";
        internal const string SplitterTool = "splitter";
        internal const string BeltT1 = "belt_t1";
        internal const string PipeT1 = "pipe_t1";
        internal const string PumpTool = "pump";
        internal const string TankTool = "tank";
        internal const string FactoryHost = "[HomeValleyFactoryHost]";
        internal const string PanelHost = "[ProductionPanelHost]";
        internal const string RulesHost = "[RulesPanelHost]";
        internal const string AwayHost = "[AwayReportHost]";

        internal static CampaignState St => CampaignSession.Current;

        internal static HomeValleyBuildMode Mode => HomeValleyBuildMode.Current;

        internal static string Cell(GridCell c) => FgjM3Common.Cell(c);

        internal static long NowMs() => FgjM2Common.NowMs();

        // ── 方向 ────────────────────────────────────────────────────────────────────

        internal static int Left(int dir) => (dir + 3) & 3;

        internal static int Right(int dir) => (dir + 1) & 3;

        internal static int Opp(int dir) => (dir + 2) & 3;

        internal static GridCell Step(GridCell c, int d, int n = 1) => FgjM3Common.Step(c, d, n);

        internal static long Key(GridCell c) => FgjM3Common.Key(c);

        internal static GridCell Add(GridCell a, int dx, int dy) => new GridCell(a.X + dx, a.Y + dy);

        // ── 地形 ────────────────────────────────────────────────────────────────────

        internal static byte TerrainCode(string key) => GridContent.TryTerrainCode(key, out byte code) ? code : byte.MaxValue;

        internal static bool IsTerrain(CampaignState s, GridCell c, byte code) => code != byte.MaxValue && HomeGridService.MapFor(s).GetTerrain(c) == code;

        internal static bool Explored(CampaignState s, GridCell c) => HomeGridService.MapFor(s).IsExplored(c);

        /// <summary>按离 <paramref name="from"/> 由近到远（切比雪夫环）找第一格某种地形，最远 <paramref name="maxR"/> 格。不看迷雾（地形按种子生成，玩家在战略地图上也看得到资源）。</summary>
        internal static bool NearestTerrain(CampaignState s, GridCell from, string key, int maxR, out GridCell at)
        {
            at = default;
            byte code = TerrainCode(key);
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int r = 0; r <= maxR; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        if (map.GetTerrain(c) == code)
                        {
                            at = c;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // ── 建筑几何 ────────────────────────────────────────────────────────────────

        internal static List<GridCell> Footprint(string typeId, GridCell pivot, int rot)
        {
            var cells = new List<GridCell>(16);
            if (GridContent.TryGetBuilding(typeId, out GameConfig.fg.BuildingGrid g))
            {
                GridMath.FootprintCells(pivot, g.FootprintW, g.FootprintH, rot, cells);
            }
            return cells;
        }

        /// <summary>某类建筑放在（枢轴, 朝向）时某个端口外侧那一格（铺传送带 / 管线的格）与端口朝外的方向。</summary>
        internal static bool PortOutside(string typeId, GridCell pivot, int rot, string portKey, out GridCell cell, out int face)
        {
            cell = default;
            face = 0;
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsFor(typeId, pivot, rot, ports);
            foreach (PortPlacement p in ports)
            {
                if (p.PortId == portKey)
                {
                    face = (int)p.Dir;
                    cell = Step(p.Cell, face);
                    return true;
                }
            }
            return false;
        }

        /// <summary>流体口（fg.TbBuildingFluidPort）外侧一格：按与物品口同一套局部坐标与朝向规则换算。</summary>
        internal static bool FluidPortOutside(string typeId, GridCell pivot, int rot, string portKey, out GridCell cell, out int face)
        {
            cell = default;
            face = 0;
            if (!ProducerCatalog.TryGet(typeId, out ProducerDef pd))
            {
                return false;
            }
            foreach (FluidPortDef p in pd.FluidPorts)
            {
                if (p.Id != portKey)
                {
                    continue;
                }
                face = (int)GridMath.RotateDir(p.Dir, rot);
                cell = Step(GridMath.PortCell(pivot, p.LocalX, p.LocalY, rot), face);
                return true;
            }
            return false;
        }

        /// <summary>FG6-E2E-01：规划时“只差研究（锁定）”也算能放（FGJ-M6 在研究精炼塔之前就规划精炼链）。只在规划调用期间打开，默认关。</summary>
        internal static bool PlanLockedOk;

        internal static bool Placeable(CampaignState s, string typeId, GridCell pivot, int rot, ISet<long> avoid)
        {
            GridPlacementResult r = HomeGridService.ValidatePlacement(s, typeId, pivot, rot, checkCost: false);
            if (!r.Ok && !(PlanLockedOk && r.Reasons.All(x => x.Code == GridBlockReason.Locked)))
            {
                return false;
            }
            return avoid == null || Footprint(typeId, pivot, rot).All(c => !avoid.Contains(Key(c)));
        }

        // ── 路线（传送带 / 管线，Dijkstra：格 × 进入方向，转弯加价）───────────────────────────

        /// <summary>
        /// 与 <see cref="FgjM3Common.PlanRoute"/> 同一算法，多两点：<paramref name="cellOk"/> 决定一格能不能铺（传送带 / 管线各自的放置校验），
        /// 起点 = 终点时返回只有一格的路线（这一格的方向 = <paramref name="goalDir"/>，不能是 <paramref name="forbidFirst"/>）。
        /// </summary>
        internal static List<GridCell> PlanRoute(CampaignState s, GridCell start, GridCell goal, int goalDir, int forbidFirst, ISet<long> avoid, int margin, Func<GridCell, bool> cellOk)
        {
            if (start == goal)
            {
                return goalDir != forbidFirst && cellOk(start) ? new List<GridCell> { start } : null;
            }
            int minX = Math.Min(start.X, goal.X) - margin;
            int maxX = Math.Max(start.X, goal.X) + margin;
            int minY = Math.Min(start.Y, goal.Y) - margin;
            int maxY = Math.Max(start.Y, goal.Y) + margin;
            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            const int turn = 4;
            var okCache = new Dictionary<long, bool>();
            bool Ok(GridCell c)
            {
                if (c.X < minX || c.X > maxX || c.Y < minY || c.Y > maxY)
                {
                    return false;
                }
                long k = Key(c);
                if (okCache.TryGetValue(k, out bool v))
                {
                    return v;
                }
                v = (c == start || c == goal || avoid == null || !avoid.Contains(k)) && cellOk(c);
                okCache[k] = v;
                return v;
            }
            if (!Ok(start) || !Ok(goal))
            {
                return null;
            }
            int Idx(GridCell c) => (c.Y - minY) * w + (c.X - minX);
            int n = w * h * 4;
            var dist = new int[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = int.MaxValue;
                prev[i] = int.MinValue;
            }
            var pq = new SortedSet<(int cost, int state)>();
            for (int d = 0; d < 4; d++)
            {
                if (d == forbidFirst)
                {
                    continue;
                }
                GridCell nb = Step(start, d);
                if (!Ok(nb))
                {
                    continue;
                }
                int st = Idx(nb) * 4 + d;
                dist[st] = 1;
                prev[st] = -1;
                pq.Add((1, st));
            }
            int best = int.MaxValue;
            int bestState = -1;
            while (pq.Count > 0)
            {
                (int cost, int st) = pq.Min;
                pq.Remove(pq.Min);
                if (cost > dist[st] || cost >= best)
                {
                    continue;
                }
                int cellIdx = st / 4;
                int din = st % 4;
                var cell = new GridCell(minX + cellIdx % w, minY + cellIdx / w);
                if (cell == goal)
                {
                    int total = cost + (din != goalDir ? turn : 0);
                    if (total < best)
                    {
                        best = total;
                        bestState = st;
                    }
                    continue;
                }
                for (int d = 0; d < 4; d++)
                {
                    if (d == Opp(din))
                    {
                        continue;
                    }
                    GridCell nb = Step(cell, d);
                    if (nb == start || !Ok(nb))
                    {
                        continue;
                    }
                    int ns = Idx(nb) * 4 + d;
                    int nc = cost + 1 + (d != din ? turn : 0);
                    if (nc < dist[ns])
                    {
                        dist[ns] = nc;
                        prev[ns] = st;
                        pq.Add((nc, ns));
                    }
                }
            }
            if (bestState < 0)
            {
                return null;
            }
            var path = new List<GridCell>();
            int cur = bestState;
            while (cur >= 0)
            {
                int ci = cur / 4;
                path.Add(new GridCell(minX + ci % w, minY + ci / w));
                cur = prev[cur];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        internal static Func<GridCell, bool> BeltOk(CampaignState s) => c => HomeGridService.ValidateBeltCell(s, c).Ok;

        internal static Func<GridCell, bool> PipeOk(CampaignState s) => c => HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok && PipeNetworkService.SourceFluidAt(s, c) == 0;

        internal static int RouteCost(List<GridCell> path, int goalDir)
        {
            if (path == null)
            {
                return int.MaxValue;
            }
            List<int> dirs = FgjM3Common.RouteDirs(path, goalDir);
            int turns = 0;
            for (int i = 1; i < dirs.Count; i++)
            {
                turns += dirs[i] != dirs[i - 1] ? 1 : 0;
            }
            return path.Count + turns * 4;
        }

        // ── 工厂块（相对精炼炉枢轴，朝向一律 0：物品自西向东流）─────────────────────────────

        /// <summary>工厂块里的建筑（键、类型、相对枢轴）。两座零件工坊：一座做结构材、一座做零件（零件作战组件也要用，单独一座供料不用过滤）。</summary>
        internal static readonly (string key, string type, int x, int y, string recipe)[] BlockBuildings =
        {
            ("furnace", Furnace, 0, 0, "alloy_ore"),
            ("pwS", PartsWs, 8, 4, "structural"),
            ("pwP", PartsWs, 10, -2, "part"),
            ("eb", ElecBench, 10, -8, "electronic"),
            ("cw", CompWs, 17, -5, "combat_component"),
        };

        /// <summary>工厂块里的分流器（键、相对格、朝向）：默认 1 : 1 轮流、两口都收全部物品——一个口满了自动给另一个（ADR-LOG-004 第 3 节），不用设过滤。</summary>
        internal static readonly (string key, int x, int y, int dir)[] BlockSplitters =
        {
            ("s1", 4, 0, 1),
            ("s2", 6, -3, 1),
            ("s3", 13, -2, 1),
            ("s4", 13, -8, 1),
        };

        internal sealed class Route
        {
            public string Key;
            public string Tool = BeltT1;
            public List<GridCell> Path;
            public int GoalDir;
            public string Label;
        }

        internal sealed class Placed
        {
            public string Key;
            public string Type;
            public GridCell Pivot;
            public int Rot;
            public string Recipe;
            public string Label;
        }

        internal sealed class FactoryPlan
        {
            public GridCell Origin;
            public readonly List<Placed> Buildings = new List<Placed>();
            public readonly List<(string key, GridCell cell, int dir)> Splitters = new List<(string, GridCell, int)>();
            public readonly List<Route> Routes = new List<Route>();
            public readonly HashSet<long> Used = new HashSet<long>();
            public readonly List<GridCell> Generators = new List<GridCell>();
            public string TrunkSink;
            public string RecyclerSink;
            public string PowerNote = string.Empty;
            public string Note = string.Empty;

            public Placed B(string key) => Buildings.FirstOrDefault(b => b.Key == key);

            public Route R(string key) => Routes.FirstOrDefault(r => r.Key == key);
        }

        /// <summary>家园里已有建筑的端口外侧格 + 占地（规划时新路线、新建筑都要绕开）。</summary>
        internal static HashSet<long> BaseAvoid(CampaignState s)
        {
            HashSet<long> avoid = FgjM3Common.AllPortCells(s);
            var fp = new List<GridCell>(32);
            foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                HomeGridService.FootprintOf(b, fp);
                foreach (GridCell c in fp)
                {
                    avoid.Add(Key(c));
                }
            }
            return avoid;
        }

        private static void Reserve(FactoryPlan plan, string type, GridCell pivot, int rot)
        {
            foreach (GridCell c in Footprint(type, pivot, rot))
            {
                plan.Used.Add(Key(c));
            }
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsFor(type, pivot, rot, ports);
            foreach (PortPlacement p in ports)
            {
                plan.Used.Add(Key(Step(p.Cell, (int)p.Dir)));
            }
            if (ProducerCatalog.TryGet(type, out ProducerDef pd))
            {
                foreach (FluidPortDef fp in pd.FluidPorts)
                {
                    if (FluidPortOutside(type, pivot, rot, fp.Id, out GridCell fc, out _))
                    {
                        plan.Used.Add(Key(fc));
                    }
                }
            }
        }

        /// <summary>
        /// 工厂块里的固定成品带（相对原点、方向、格数）：三股成品与作战组件各走一段直带，从西侧汇入东边一条朝北的收集带（侧向汇入，FGR-LOG-021），
        /// 收集带出块后由 <see cref="PlanOutflows"/> 现找路线接到仓库 / 归还核心输入口。几何固定在块里，侧向汇入点按构造必然成立。
        /// </summary>
        internal static readonly (string key, int x, int y, int dir, int len, string label)[] BlockFixedBelts =
        {
            ("r_out_pws", 10, 4, 1, 12, "零件工坊（结构材）→ 收集带"),
            ("r_out_part", 13, -1, 1, 9, "分流器 3 左口（零件溢出）→ 收集带"),
            ("r_out_elec", 13, -9, 1, 9, "分流器 4 右口（电子件溢出）→ 收集带"),
            ("r_out_comb", 20, -5, 1, 2, "组件工坊（作战组件）→ 收集带"),
            ("r_collector", 22, -10, 0, 16, "收集带（朝北，四股从西侧汇入）"),
        };

        /// <summary>收集带出块后的第一格（相对原点）。</summary>
        internal const int CollectorExitX = 22;
        internal const int CollectorExitY = 6;

        /// <summary>
        /// 规划工厂块：在金属矿脉周围按地形逐个试原点，块里五座建筑都能放（放置校验，不看造价）、四个分流器格与固定成品带都能放传送带、块内九条带都铺得通。
        /// 返回可行原点里按“五座都在归还核心配电范围里优先、离金属矿脉近优先”排序的前 <paramref name="max"/> 个。
        /// </summary>
        internal static List<FactoryPlan> PlanBlocks(CampaignState s, int max, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            var found = new List<(int score, FactoryPlan plan)>();
            if (!NearestTerrain(s, core, "ore_metal", 40, out GridCell ore))
            {
                why = "核心 40 格内没有金属矿脉";
                return new List<FactoryPlan>();
            }
            HashSet<long> baseAvoid = BaseAvoid(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            int tried = 0;
            for (int r = 4; r <= 40; r += 2)
            {
                for (int dy = -r; dy <= r; dy += 2)
                {
                    for (int dx = -r; dx <= r; dx += 2)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell o = Add(ore, dx, dy);
                        tried++;
                        FactoryPlan p = TryBlockAt(s, o, baseAvoid);
                        if (p == null)
                        {
                            continue;
                        }
                        bool powered = p.Buildings.All(b => NearestDist(b.Type, b.Pivot, b.Rot, core) <= coreR - 0.5f);
                        found.Add(((powered ? 0 : 1000) + Math.Abs(o.X - ore.X) + Math.Abs(o.Y - ore.Y), p));
                    }
                }
                if (found.Count(f => f.score < 1000) >= max)
                {
                    break;
                }
            }
            if (found.Count == 0)
            {
                why = $"金属矿脉 {Cell(ore)} 周围试了 {tried} 个原点都放不下工厂块";
            }
            return found.OrderBy(f => f.score).Take(max).Select(f => f.plan).ToList();
        }

        internal static FactoryPlan PlanBlock(CampaignState s, out string why) => PlanBlocks(s, 1, out why).FirstOrDefault();

        /// <summary>建筑最近的占地格中心到 <paramref name="node"/> 的直线距离（电力覆盖的口径，FGR-LOG-060）。</summary>
        internal static float NearestDist(string type, GridCell pivot, int rot, GridCell node)
        {
            float best = float.MaxValue;
            foreach (GridCell c in Footprint(type, pivot, rot))
            {
                best = Mathf.Min(best, Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(node.X, node.Y)));
            }
            return best;
        }

        private static FactoryPlan TryBlockAt(CampaignState s, GridCell o, HashSet<long> baseAvoid)
        {
            var plan = new FactoryPlan { Origin = o };
            foreach (var b in BlockBuildings)
            {
                var pivot = Add(o, b.x, b.y);
                if (!Placeable(s, b.type, pivot, 0, baseAvoid) || Footprint(b.type, pivot, 0).Any(c => plan.Used.Contains(Key(c))))
                {
                    return null;
                }
                plan.Buildings.Add(new Placed { Key = b.key, Type = b.type, Pivot = pivot, Rot = 0, Recipe = b.recipe, Label = HomeGridService.DisplayName(b.type) });
                Reserve(plan, b.type, pivot, 0);
            }
            foreach (var sp in BlockSplitters)
            {
                GridCell c = Add(o, sp.x, sp.y);
                if (plan.Used.Contains(Key(c)) || baseAvoid.Contains(Key(c)) || !HomeGridService.ValidateBeltCell(s, c).Ok)
                {
                    return null;
                }
                plan.Splitters.Add((sp.key, c, sp.dir));
            }
            foreach (var sp in plan.Splitters)
            {
                plan.Used.Add(Key(sp.cell));
                plan.Used.Add(Key(Step(sp.cell, Left(sp.dir))));
                plan.Used.Add(Key(Step(sp.cell, Right(sp.dir))));
                plan.Used.Add(Key(Step(sp.cell, Opp(sp.dir))));
            }
            // 固定成品带与收集带：每格都要能放传送带、不压别的东西（成品带的第一格就是建筑输出口 / 分流器出口外侧那一格，已在保留集里，按“本带自己的格”放行）。
            var fixedCells = new HashSet<long>();
            foreach (var fb in BlockFixedBelts)
            {
                var path = new List<GridCell>(fb.len);
                for (int i = 0; i < fb.len; i++)
                {
                    path.Add(Step(Add(o, fb.x, fb.y), fb.dir, i));
                }
                plan.Routes.Add(new Route { Key = fb.key, Path = path, GoalDir = fb.dir, Label = fb.label });
            }
            var ownStarts = new HashSet<long>(plan.Routes.Select(r => Key(r.Path[0])));
            foreach (Route r in plan.Routes)
            {
                foreach (GridCell c in r.Path)
                {
                    long k = Key(c);
                    if (baseAvoid.Contains(k) || (plan.Used.Contains(k) && !ownStarts.Contains(k)) || fixedCells.Contains(k) || !HomeGridService.ValidateBeltCell(s, c).Ok)
                    {
                        return null;
                    }
                    fixedCells.Add(k);
                }
            }
            plan.Used.UnionWith(fixedCells);
            GridCell exit = Add(o, CollectorExitX, CollectorExitY);
            if (baseAvoid.Contains(Key(exit)) || plan.Used.Contains(Key(exit)) || !HomeGridService.ValidateBeltCell(s, exit).Ok)
            {
                return null;
            }
            var avoid = new HashSet<long>(baseAvoid);
            avoid.UnionWith(plan.Used);
            avoid.Add(Key(exit));
            GridCell Out(string key)
            {
                Placed b = plan.B(key);
                return PortOutside(b.Type, b.Pivot, 0, b.Type + ".out0", out GridCell c, out _) ? c : default;
            }
            GridCell In(string key, string port, out int dir)
            {
                Placed b = plan.B(key);
                PortOutside(b.Type, b.Pivot, 0, b.Type + "." + port, out GridCell c, out int face);
                dir = Opp(face);
                return c;
            }
            (GridCell cell, int dir) Sp(string key) => plan.Splitters.First(x => x.key == key) is var t ? (t.cell, t.dir) : default;
            bool Lay(string key, GridCell start, int forbid, GridCell goal, int goalDir, string label)
            {
                List<GridCell> path = PlanRoute(s, start, goal, goalDir, forbid, avoid, 6, BeltOk(s));
                if (path == null)
                {
                    return false;
                }
                plan.Routes.Add(new Route { Key = key, Path = path, GoalDir = goalDir, Label = label });
                foreach (GridCell c in path)
                {
                    avoid.Add(Key(c));
                    plan.Used.Add(Key(c));
                }
                return true;
            }
            (GridCell s1, int d1) = Sp("s1");
            (GridCell s2, int d2) = Sp("s2");
            (GridCell s3, int d3) = Sp("s3");
            (GridCell s4, int d4) = Sp("s4");
            if (!Lay("r_f_s1", Out("furnace"), 3, Step(s1, Opp(d1)), d1, "精炼炉 → 分流器 1")
                || !Lay("r_s1_pws", Step(s1, Left(d1)), Opp(Left(d1)), In("pwS", "in0", out int gpws), gpws, "分流器 1 左口 → 零件工坊（结构材）")
                || !Lay("r_s1_s2", Step(s1, Right(d1)), Opp(Right(d1)), Step(s2, Opp(d2)), d2, "分流器 1 右口 → 分流器 2")
                || !Lay("r_s2_pwp", Step(s2, Left(d2)), Opp(Left(d2)), In("pwP", "in0", out int gpwp), gpwp, "分流器 2 左口 → 零件工坊（零件）")
                || !Lay("r_s2_eb", Step(s2, Right(d2)), Opp(Right(d2)), In("eb", "in0", out int geb), geb, "分流器 2 右口 → 电子组装台（合金口）")
                || !Lay("r_pwp_s3", Out("pwP"), 3, Step(s3, Opp(d3)), d3, "零件工坊（零件）→ 分流器 3")
                || !Lay("r_eb_s4", Out("eb"), 3, Step(s4, Opp(d4)), d4, "电子组装台 → 分流器 4")
                || !Lay("r_s3_cw", Step(s3, Right(d3)), Opp(Right(d3)), In("cw", "in0", out int gcw0), gcw0, "分流器 3 右口 → 组件工坊（零件口）")
                || !Lay("r_s4_cw", Step(s4, Left(d4)), Opp(Left(d4)), In("cw", "in1", out int gcw1), gcw1, "分流器 4 左口 → 组件工坊（电子件口）"))
            {
                return null;
            }
            return plan;
        }

        /// <summary>
        /// 成品出路：收集带出块后的第一格起现找路线接到仓库 / 归还核心输入口（取代价小的那个）；回收站的废料带走另一个输入口。
        /// </summary>
        internal static bool PlanOutflows(CampaignState s, FactoryPlan plan, out string why)
        {
            why = null;
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var sinks = new List<(string name, GridCell cell, int dir)>();
            if (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, false, out GridCell wIn, out int wFace, out _))
            {
                sinks.Add(("仓库输入口", wIn, Opp(wFace)));
            }
            if (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeCore, false, out GridCell cIn, out int cFace, out _))
            {
                sinks.Add(("归还核心输入口", cIn, Opp(cFace)));
            }
            GridCell exit = Add(plan.Origin, CollectorExitX, CollectorExitY);
            List<GridCell> trunk = null;
            int trunkDir = 0;
            string trunkSink = null;
            foreach (var sink in sinks)
            {
                // 出口格不能朝南（那是指回收集带）。
                List<GridCell> p = PlanRoute(s, exit, sink.cell, sink.dir, 2, avoid, 16, BeltOk(s));
                if (p != null && (trunk == null || RouteCost(p, sink.dir) < RouteCost(trunk, trunkDir)))
                {
                    trunk = p;
                    trunkDir = sink.dir;
                    trunkSink = sink.name;
                }
            }
            if (trunk == null)
            {
                why = $"收集带出口 {Cell(exit)} 到仓库 / 核心输入口铺不出干线";
                return false;
            }
            plan.TrunkSink = trunkSink;
            plan.Routes.Add(new Route { Key = "r_trunk", Path = trunk, GoalDir = trunkDir, Label = "干线：收集带 → " + trunkSink });
            foreach (GridCell c in trunk)
            {
                plan.Used.Add(Key(c));
            }
            plan.RecyclerSink = sinks.Count > 1 ? sinks.First(x => x.name != trunkSink).name : trunkSink;
            return true;
        }

        /// <summary>
        /// 提取钻：在某种矿脉附近找一个能放的 2×2 枢轴（至少一格压在矿脉上），从它的输出口铺到 <paramref name="goal"/>（朝 <paramref name="goalDir"/>）。
        /// 按离目标由近到远试，取路线代价最小的。没探索的格放不了（返回 null 并写原因）。
        /// </summary>
        internal static Route PlanDrill(CampaignState s, FactoryPlan plan, string key, string ore, GridCell goal, int goalDir, int maxR, out Placed drill, out string why)
        {
            drill = null;
            why = null;
            byte code = TerrainCode(ore);
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var cands = new List<(int d, GridCell p)>();
            for (int dy = -maxR; dy <= maxR; dy++)
            {
                for (int dx = -maxR; dx <= maxR; dx++)
                {
                    var p = new GridCell(goal.X + dx, goal.Y + dy);
                    List<GridCell> fp = Footprint(Drill, p, 0);
                    if (!fp.Any(c => IsTerrain(s, c, code)))
                    {
                        continue;
                    }
                    cands.Add((Math.Abs(dx) + Math.Abs(dy), p));
                }
            }
            Route best = null;
            int bestCost = int.MaxValue;
            int ok = 0;
            foreach (var cnd in cands.OrderBy(x => x.d))
            {
                if (ok >= 6)
                {
                    break;
                }
                if (!Placeable(s, Drill, cnd.p, 0, avoid) || !PortOutside(Drill, cnd.p, 0, Drill + ".out0", out GridCell outCell, out _) || avoid.Contains(Key(outCell)))
                {
                    continue;
                }
                var avoidPlus = new HashSet<long>(avoid);
                foreach (GridCell c in Footprint(Drill, cnd.p, 0))
                {
                    avoidPlus.Add(Key(c));
                }
                List<GridCell> path = PlanRoute(s, outCell, goal, goalDir, 3, avoidPlus, 10, BeltOk(s));
                if (path == null)
                {
                    continue;
                }
                ok++;
                int cost = RouteCost(path, goalDir);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = new Route { Key = key, Path = path, GoalDir = goalDir };
                    drill = new Placed { Key = key, Type = Drill, Pivot = cnd.p, Rot = 0, Label = HomeGridService.DisplayName(Drill) };
                }
            }
            if (best == null)
            {
                why = cands.Count == 0 ? $"{ore} 不在 {Cell(goal)} 周围 {maxR} 格内" : $"{ore} 附近 {cands.Count} 个枢轴都放不下提取钻或铺不通到 {Cell(goal)} 的带（多半还没探索）";
                return null;
            }
            plan.Buildings.Add(drill);
            Reserve(plan, Drill, drill.Pivot, 0);
            foreach (GridCell c in best.Path)
            {
                plan.Used.Add(Key(c));
            }
            plan.Routes.Add(best);
            return best;
        }

        /// <summary>回收站：压在废墟上的 4×4（脚下废墟格越多越好，至少 8 格），输出口铺到 <paramref name="sinkName"/>。</summary>
        internal static Route PlanRecycler(CampaignState s, FactoryPlan plan, string sinkName, out Placed rec, out string why)
        {
            rec = null;
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            byte ruin = TerrainCode("ruin");
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var sink = sinkName == "仓库输入口"
                ? (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, false, out GridCell w, out int wf, out _) ? (w, Opp(wf)) : default)
                : (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeCore, false, out GridCell c0, out int cf, out _) ? (c0, Opp(cf)) : default);
            var cands = new List<(int score, GridCell p)>();
            for (int dy = -30; dy <= 30; dy++)
            {
                for (int dx = -30; dx <= 30; dx++)
                {
                    var p = Add(core, dx, dy);
                    int ruins = Footprint(Recycler, p, 0).Count(c => IsTerrain(s, c, ruin));
                    if (ruins >= 8)
                    {
                        cands.Add((ruins * 100 - (Math.Abs(dx) + Math.Abs(dy)), p));
                    }
                }
            }
            foreach (var cnd in cands.OrderByDescending(x => x.score).Take(40))
            {
                if (!Placeable(s, Recycler, cnd.p, 0, avoid) || !PortOutside(Recycler, cnd.p, 0, Recycler + ".out0", out GridCell outCell, out _) || avoid.Contains(Key(outCell)))
                {
                    continue;
                }
                var avoidPlus = new HashSet<long>(avoid);
                foreach (GridCell c in Footprint(Recycler, cnd.p, 0))
                {
                    avoidPlus.Add(Key(c));
                }
                if (PortOutside(Recycler, cnd.p, 0, Recycler + ".in0", out GridCell inCell, out _))
                {
                    avoidPlus.Add(Key(inCell));
                }
                List<GridCell> path = PlanRoute(s, outCell, sink.Item1, sink.Item2, 3, avoidPlus, 12, BeltOk(s));
                if (path == null)
                {
                    continue;
                }
                rec = new Placed { Key = "recycler", Type = Recycler, Pivot = cnd.p, Rot = 0, Label = HomeGridService.DisplayName(Recycler) };
                plan.Buildings.Insert(0, rec);
                Reserve(plan, Recycler, cnd.p, 0);
                var route = new Route { Key = "r_recycler", Path = path, GoalDir = sink.Item2, Label = "回收站 → " + sinkName };
                plan.Routes.Insert(0, route);
                foreach (GridCell c in path)
                {
                    plan.Used.Add(Key(c));
                }
                return route;
            }
            why = $"核心 30 格内找不到能放回收站、压着至少 8 格废墟、并能铺带到{sinkName}的位置（候选 {cands.Count} 个）";
            return null;
        }

        /// <summary>
        /// 电力：不在已有节点（归还核心 26 格 / 已有电塔）覆盖里的建筑，从最近的节点朝它串 T2 电塔（覆盖 16 格，节点间距不超过两者半径中较大的一个），
        /// 电塔落在能放、不压路线与建筑的格子上。返回要放的电塔格（按放置顺序）。
        /// </summary>
        internal static List<GridCell> PlanPoles(CampaignState s, FactoryPlan plan, IEnumerable<Placed> needPower, out string why)
        {
            why = null;
            var nodes = new List<(GridCell c, float r)> { (HomeGridService.CorePivot(s), HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore)) };
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && HomeValleyPowerGrid.IsPoleType(b.BuildingTypeId))
                {
                    nodes.Add((new GridCell(b.GridX, b.GridY), HomeValleyPowerGrid.CoverRadiusOf(b.BuildingTypeId)));
                }
            }
            float poleR = HomeValleyPowerGrid.CoverRadiusOf(PoleT2);
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var poles = new List<GridCell>();
            foreach (Placed b in needPower)
            {
                for (int guard = 0; guard < 12; guard++)
                {
                    (GridCell c, float r) near = nodes.OrderBy(n => NearestDist(b.Type, b.Pivot, b.Rot, n.c) - n.r).First();
                    if (NearestDist(b.Type, b.Pivot, b.Rot, near.c) <= near.r - 0.5f)
                    {
                        break;
                    }
                    float link = Math.Max(near.r, poleR) - 0.5f;
                    GridCell pick = default;
                    float pickD = float.MaxValue;
                    int R = (int)link;
                    for (int dy = -R; dy <= R; dy++)
                    {
                        for (int dx = -R; dx <= R; dx++)
                        {
                            var q = Add(near.c, dx, dy);
                            if (Vector2.Distance(new Vector2(q.X, q.Y), new Vector2(near.c.X, near.c.Y)) > link || avoid.Contains(Key(q)))
                            {
                                continue;
                            }
                            float d = NearestDist(b.Type, b.Pivot, b.Rot, q);
                            if (d < pickD && Placeable(s, PoleT2, q, 0, null))
                            {
                                pickD = d;
                                pick = q;
                            }
                        }
                    }
                    if (pickD == float.MaxValue)
                    {
                        why = $"{b.Label} {Cell(b.Pivot)} 接不上电：{Cell(near.c)} 附近没有能放 T2 电塔的格子";
                        return null;
                    }
                    poles.Add(pick);
                    nodes.Add((pick, poleR));
                    avoid.Add(Key(pick));
                    plan.Used.Add(Key(pick));
                }
            }
            return poles;
        }

        /// <summary>
        /// 整套规划（稀土矿脉已探索时）：工厂块 → 金属 / 稀土提取钻 → 成品出路 → 回收站 → 电塔。与旅程的 plan 步骤同一顺序；
        /// [M4 出口] 自检在种子测试集上调用它，证明规划不依赖固定坐标（B25）。
        /// </summary>
        internal static FactoryPlan PlanAll(CampaignState s, out List<GridCell> poles, out string why)
        {
            poles = null;
            List<FactoryPlan> blocks = PlanBlocks(s, 6, out why);
            var fails = new List<string>();
            foreach (FactoryPlan plan in blocks)
            {
                if (TryFinish(s, plan, true, out poles, out string w))
                {
                    why = fails.Count == 0 ? null : "前 " + fails.Count + " 个原点没成：" + string.Join("；", fails);
                    return plan;
                }
                fails.Add(Cell(plan.Origin) + " " + w);
            }
            why = blocks.Count == 0 ? why : string.Join("；", fails);
            return null;
        }

        /// <summary>一个工厂块原点上接矿、铺出路、放回收站、串电塔；<paramref name="withRare"/> = false 时只接金属矿（稀土提取钻要先探路）。</summary>
        internal static bool TryFinish(CampaignState s, FactoryPlan plan, bool withRare, out List<GridCell> poles, out string why)
        {
            poles = null;
            why = null;
            Placed f = plan.B("furnace");
            PortOutside(f.Type, f.Pivot, 0, f.Type + ".in0", out GridCell fIn, out int fFace);
            if (plan.B("drillM") == null && PlanDrill(s, plan, "drillM", "ore_metal", fIn, Opp(fFace), 40, out _, out why) == null)
            {
                why = "金属提取钻：" + why;
                return false;
            }
            if (!withRare)
            {
                return true;
            }
            if (plan.B("drillR") == null)
            {
                Placed eb = plan.B("eb");
                PortOutside(eb.Type, eb.Pivot, 0, eb.Type + ".in1", out GridCell ebIn1, out int ebFace);
                if (PlanDrill(s, plan, "drillR", "ore_rare", ebIn1, Opp(ebFace), 90, out _, out why) == null)
                {
                    why = "稀土提取钻：" + why;
                    return false;
                }
            }
            if (!PlanOutflows(s, plan, out why))
            {
                why = "成品出路：" + why;
                return false;
            }
            if (PlanRecycler(s, plan, plan.RecyclerSink, out _, out why) == null)
            {
                why = "回收站：" + why;
                return false;
            }
            poles = PlanPoles(s, plan, plan.Buildings, out why);
            if (poles == null)
            {
                return false;
            }
            foreach (GridCell pl in poles)
            {
                plan.Used.Add(Key(pl));
            }
            // 电量：开局建筑 + 整条产线 + 之后的精炼塔 / 废液池 / 复制的精炼炉一起算（FG04 第 3.3 节耗电表），不够就先造发电机 2。
            int extra = BuildingDemand(Tower) + BuildingDemand(Pond) + BuildingDemand(Furnace);
            return PlanGenerators(s, plan, extra, out why);
        }

        internal static int BuildingDemand(string type) => FgContentTables.TryGetBuilding(type, out GameConfig.fg.Building b) ? (int)Math.Ceiling(b.PowerDemand) : 0;

        internal static int BuildingSupply(string type) => FgContentTables.TryGetBuilding(type, out GameConfig.fg.Building b) ? (int)Math.Floor(b.PowerSupply) : 0;

        /// <summary>
        /// 家园电量够不够：现有建筑（开局建筑按修好算）+ 规划的建筑 + <paramref name="extraDemand"/> 的耗电，对比现有发电（燃油发电机不算——它要燃油）。
        /// 不够就在核心配电范围里找空地放发电机 2，放置顺序排在产线之前（没电的回收站拆不出废料，会卡住后面的施工）。
        /// </summary>
        internal static bool PlanGenerators(CampaignState s, FactoryPlan plan, int extraDemand, out string why, double margin = 1.05)
        {
            why = null;
            int demand = extraDemand;
            int supply = 0;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                demand += BuildingDemand(b.BuildingTypeId);
                if (!HomeValleyPowerGrid.IsFuelGeneratorType(b.BuildingTypeId))
                {
                    supply += BuildingSupply(b.BuildingTypeId);
                }
            }
            foreach (Placed p in plan.Buildings)
            {
                demand += BuildingDemand(p.Type);
            }
            int per = Math.Max(1, BuildingSupply(HomeValleyLayout.BuildingTypeGenerator2));
            int need = Math.Max(0, (int)Math.Ceiling((demand * margin - supply) / per));
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            string g2 = HomeValleyLayout.BuildingTypeGenerator2;
            for (int r = 6; r <= 24 && plan.Generators.Count < need; r++)
            {
                for (int dy = -r; dy <= r && plan.Generators.Count < need; dy++)
                {
                    for (int dx = -r; dx <= r && plan.Generators.Count < need; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell g = Add(core, dx, dy);
                        if (NearestDist(g2, g, 0, core) > coreR - 0.5f || !Placeable(s, g2, g, 0, avoid))
                        {
                            continue;
                        }
                        // 四周留一圈空，不贴着别的建筑 / 路线。
                        List<GridCell> fp = Footprint(g2, g, 0);
                        bool clear = true;
                        foreach (GridCell c in fp)
                        {
                            for (int d = 0; d < 4 && clear; d++)
                            {
                                GridCell n = Step(c, d);
                                clear &= fp.Contains(n) || !avoid.Contains(Key(n));
                            }
                        }
                        if (!clear)
                        {
                            continue;
                        }
                        plan.Generators.Add(g);
                        foreach (GridCell c in fp)
                        {
                            avoid.Add(Key(c));
                            plan.Used.Add(Key(c));
                        }
                    }
                }
            }
            plan.PowerNote = $"耗电 {demand}（含调用方给的修正 {extraDemand}：M4 = 之后才放的精炼塔 / 废液池 / 复制的精炼炉，M5R = 没修、不在电网里的建筑）、现有发电 {supply}、余量 ×{margin:0.00}，加发电机 2 ×{plan.Generators.Count}（每座 {per}）";
            if (plan.Generators.Count < need)
            {
                why = $"电不够（{plan.PowerNote}），核心配电范围里放不下第 {plan.Generators.Count + 1} 座发电机 2";
                return false;
            }
            return true;
        }

        /// <summary>核心所在电网此刻的发电 / 需要 / 实际供上 / 停机数（日志用，只读）。</summary>
        internal static string PowerLine(CampaignState s)
        {
            BuildingRecord core = FgjM3Common.Home(HomeValleyLayout.BuildingTypeCore);
            if (core == null || !HomeValleyPowerGrid.TryGetBuildingPower(s, core.BuildingId, out BuildingPowerInfo bi) || !HomeValleyPowerGrid.TryGetSubnetInfo(s, bi.Subnet, out BinGames.Sim.Logistics.PowerSubnetInfo info))
            {
                return "电网：读不到";
            }
            return $"电网：发电 {info.Supply:F0}、需要 {info.Demand:F0}、供上 {info.Delivered:F0}、停机 {info.Brownouts} 座";
        }

        // ── 精炼塔（酸液副产品）规划 ──────────────────────────────────────────────────

        internal sealed class RefineryPlan
        {
            public GridCell Tower;
            public GridCell Pond;
            public GridCell Pump;
            public GridCell FuelPipe;
            public GridCell Tank;
            public GridCell AcidPipe;
            public List<GridCell> Crude;
            public List<GridCell> Poles = new List<GridCell>();
        }

        /// <summary>
        /// 精炼塔：一格泵压在离核心最近的一片油井上，管线把原油送到精炼塔南口；北口（燃油）接一格管线 + 储罐；东口（酸液）先不接——
        /// 酸液无处可去时精炼塔输出堵塞（FG04 负向“酸液没有去处”），之后在东边放废液池、一格管线把酸液口与废液池西口连起来恢复（FGT-ECO-006）。
        /// 三张管网互不相邻（相邻的管线格会连成一张网）。塔与废液池不在配电范围里时串 T2 电塔。按种子地形现找（B25）。
        /// </summary>
        internal static RefineryPlan PlanRefinery(CampaignState s, ISet<long> used, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            HashSet<long> baseAvoid = BaseAvoid(s);
            if (used != null)
            {
                baseAvoid.UnionWith(used);
            }
            int oil = PipeNetworkService.FluidId("crude");
            byte oilCode = TerrainCode("oil");
            var pumps = new List<GridCell>();
            for (int r = 3; r <= 120 && pumps.Count < 6; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var u = Add(core, dx, dy);
                        if (!IsTerrain(s, u, oilCode) || baseAvoid.Contains(Key(u)) || !HomeGridService.ValidatePipeCell(s, u, PipePieceKind.Pump).Ok
                            || (oil > 0 && PipeNetworkService.SourceFluidAt(s, u) != oil))
                        {
                            continue;
                        }
                        pumps.Add(u);
                    }
                }
            }
            if (pumps.Count == 0)
            {
                why = "核心 120 格内找不到能放泵的油井（多半还没探索）";
                return null;
            }
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            RefineryPlan best = null;
            int bestCost = int.MaxValue;
            foreach (GridCell u in pumps)
            {
                for (int r = 5; r <= 26; r++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            {
                                continue;
                            }
                            var t = Add(u, dx, dy);
                            RefineryPlan p = TryRefineryAt(s, t, u, baseAvoid, out int cost);
                            if (p == null)
                            {
                                continue;
                            }
                            bool powered = NearestDist(Tower, p.Tower, 0, core) <= coreR - 0.5f && NearestDist(Pond, p.Pond, 0, core) <= coreR - 0.5f;
                            cost += powered ? 0 : 40;
                            if (cost < bestCost)
                            {
                                bestCost = cost;
                                best = p;
                            }
                        }
                    }
                    if (best != null && r >= 8)
                    {
                        break;
                    }
                }
                if (best != null)
                {
                    break;
                }
            }
            if (best == null)
            {
                why = $"{pumps.Count} 处油井附近都放不下“精炼塔 + 储罐 + 废液池”或铺不通原油管线";
                return null;
            }
            var plan = new FactoryPlan();
            plan.Used.UnionWith(baseAvoid);
            var need = new List<Placed>
            {
                new Placed { Key = "tower", Type = Tower, Pivot = best.Tower, Rot = 0, Label = HomeGridService.DisplayName(Tower) },
                new Placed { Key = "pond", Type = Pond, Pivot = best.Pond, Rot = 0, Label = HomeGridService.DisplayName(Pond) },
            };
            foreach (Placed b in need)
            {
                foreach (GridCell c in Footprint(b.Type, b.Pivot, 0))
                {
                    plan.Used.Add(Key(c));
                }
            }
            foreach (GridCell c in best.Crude.Concat(new[] { best.Pump, best.FuelPipe, best.Tank, best.AcidPipe }))
            {
                plan.Used.Add(Key(c));
            }
            best.Poles = PlanPoles(s, plan, need, out why) ?? new List<GridCell>();
            return why == null ? best : null;
        }

        private static RefineryPlan TryRefineryAt(CampaignState s, GridCell t, GridCell u, HashSet<long> avoid, out int cost)
        {
            cost = int.MaxValue;
            if (!Placeable(s, Tower, t, 0, avoid))
            {
                return null;
            }
            var p = new RefineryPlan { Tower = t, Pond = Add(t, 4, 0), Pump = u };
            if (!Placeable(s, Pond, p.Pond, 0, avoid)
                || !FluidPortOutside(Tower, t, 0, Tower + ".fout0", out GridCell fuelOut, out int fuelFace)
                || !FluidPortOutside(Tower, t, 0, Tower + ".fout1", out GridCell acidOut, out _)
                || !FluidPortOutside(Tower, t, 0, Tower + ".fin0", out GridCell crudeIn, out _)
                || !FluidPortOutside(Pond, p.Pond, 0, Pond + ".fin0", out GridCell pondIn, out _)
                || acidOut != pondIn)
            {
                return null;
            }
            p.FuelPipe = fuelOut;
            p.Tank = Step(fuelOut, fuelFace);
            p.AcidPipe = acidOut;
            var occupied = new HashSet<long>(avoid);
            foreach (GridCell c in Footprint(Tower, t, 0).Concat(Footprint(Pond, p.Pond, 0)))
            {
                occupied.Add(Key(c));
            }
            bool PipeCellOk(GridCell c, PipePieceKind kind) => !occupied.Contains(Key(c)) && HomeGridService.ValidatePipeCell(s, c, kind).Ok && PipeNetworkService.SourceFluidAt(s, c) == 0;
            if (!PipeCellOk(p.FuelPipe, PipePieceKind.Pipe) || !PipeCellOk(p.Tank, PipePieceKind.Tank) || !PipeCellOk(p.AcidPipe, PipePieceKind.Pipe) || !PipeCellOk(crudeIn, PipePieceKind.Pipe))
            {
                return null;
            }
            // 燃油网（管线 + 储罐）与酸液格的四邻不能再有别的管线；原油管线绕开它们与两座建筑。
            var keepOut = new HashSet<long>(occupied);
            foreach (GridCell c in new[] { p.FuelPipe, p.Tank, p.AcidPipe })
            {
                keepOut.Add(Key(c));
                for (int d = 0; d < 4; d++)
                {
                    keepOut.Add(Key(Step(c, d)));
                }
            }
            if (keepOut.Contains(Key(u)) || keepOut.Contains(Key(crudeIn)))
            {
                return null;
            }
            List<GridCell> best = null;
            for (int d = 0; d < 4; d++)
            {
                GridCell start = Step(u, d);
                if (keepOut.Contains(Key(start)) || !PipeCellOk(start, PipePieceKind.Pipe))
                {
                    continue;
                }
                List<GridCell> route = PlanRoute(s, start, crudeIn, 0, -1, keepOut, 10, PipeOk(s));
                if (route != null && (best == null || route.Count < best.Count))
                {
                    best = route;
                }
            }
            if (best == null)
            {
                return null;
            }
            p.Crude = best;
            cost = best.Count + Math.Abs(t.X - u.X) / 4;
            return p;
        }

        // ── 反向旅程用的小产线（提取钻 → 精炼炉 → 输入口）与超控阵列（经一座 T2 电塔供电）──────────────────

        internal sealed class MiniPlan
        {
            public FactoryPlan Plan = new FactoryPlan();
            public GridCell Furnace;
            public GridCell Drill;
            public List<GridCell> DrillRoute;
            public List<GridCell> OutRoute;
            public int OutDir;
            public string OutSink;
            public GridCell Pole;
            public GridCell Array;
            public List<GridCell> Poles = new List<GridCell>();
        }

        /// <summary>
        /// 反向旅程的小产线：金属矿脉附近一座精炼炉（合金）、一座提取钻连到它的输入口，精炼炉输出口连到仓库 / 核心输入口；回收站走另一个输入口。
        /// 超控阵列放在归还核心配电范围外、只由一座 T2 电塔供电（拆电塔 = 断链）。电量不够时加发电机 2。按种子地形现找（B25）。
        /// </summary>
        internal static MiniPlan PlanMini(CampaignState s, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            if (!NearestTerrain(s, core, "ore_metal", 40, out GridCell ore))
            {
                why = "核心 40 格内没有金属矿脉";
                return null;
            }
            HashSet<long> baseAvoid = BaseAvoid(s);
            var sinks = new List<(string name, GridCell cell, int dir)>();
            if (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, false, out GridCell wIn, out int wFace, out _))
            {
                sinks.Add(("仓库输入口", wIn, Opp(wFace)));
            }
            if (FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeCore, false, out GridCell cIn, out int cFace, out _))
            {
                sinks.Add(("归还核心输入口", cIn, Opp(cFace)));
            }
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            for (int r = 3; r <= 24; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell f = Add(ore, dx, dy);
                        if (!Placeable(s, Furnace, f, 0, baseAvoid) || NearestDist(Furnace, f, 0, core) > coreR - 1f)
                        {
                            continue;
                        }
                        var mp = new MiniPlan { Furnace = f };
                        mp.Plan.Buildings.Add(new Placed { Key = "furnace", Type = Furnace, Pivot = f, Rot = 0, Recipe = "alloy_ore", Label = HomeGridService.DisplayName(Furnace) });
                        Reserve(mp.Plan, Furnace, f, 0);
                        PortOutside(Furnace, f, 0, Furnace + ".in0", out GridCell fin, out int finFace);
                        PortOutside(Furnace, f, 0, Furnace + ".out0", out GridCell fout, out _);
                        Route dr = PlanDrill(s, mp.Plan, "drillM", "ore_metal", fin, Opp(finFace), 24, out Placed drill, out _);
                        if (dr == null || NearestDist(Drill, drill.Pivot, 0, core) > coreR - 1f)
                        {
                            continue;
                        }
                        var avoid = new HashSet<long>(baseAvoid);
                        avoid.UnionWith(mp.Plan.Used);
                        List<GridCell> best = null;
                        int bestDir = 0;
                        string bestSink = null;
                        foreach (var sink in sinks)
                        {
                            List<GridCell> p = PlanRoute(s, fout, sink.cell, sink.dir, 3, avoid, 12, BeltOk(s));
                            if (p != null && (best == null || RouteCost(p, sink.dir) < RouteCost(best, bestDir)))
                            {
                                best = p;
                                bestDir = sink.dir;
                                bestSink = sink.name;
                            }
                        }
                        if (best == null)
                        {
                            continue;
                        }
                        mp.Drill = drill.Pivot;
                        mp.DrillRoute = dr.Path;
                        mp.OutRoute = best;
                        mp.OutDir = bestDir;
                        mp.OutSink = bestSink;
                        mp.Plan.Routes.Add(new Route { Key = "r_out", Path = best, GoalDir = bestDir, Label = "精炼炉 → " + bestSink });
                        foreach (GridCell c in best)
                        {
                            mp.Plan.Used.Add(Key(c));
                        }
                        mp.Plan.TrunkSink = bestSink;
                        mp.Plan.RecyclerSink = sinks.Count > 1 ? sinks.First(x => x.name != bestSink).name : bestSink;
                        if (PlanRecycler(s, mp.Plan, mp.Plan.RecyclerSink, out _, out _) == null)
                        {
                            continue;
                        }
                        if (!PlanOverride(s, mp, out why))
                        {
                            return null;
                        }
                        mp.Poles = PlanPoles(s, mp.Plan, mp.Plan.Buildings.Where(b => b.Key != "array"), out why) ?? new List<GridCell>();
                        if (why != null)
                        {
                            return null;
                        }
                        foreach (GridCell pl in mp.Poles)
                        {
                            mp.Plan.Used.Add(Key(pl));
                        }
                        if (!PlanGenerators(s, mp.Plan, 0, out why))
                        {
                            return null;
                        }
                        return mp;
                    }
                }
            }
            why = $"金属矿脉 {Cell(ore)} 附近放不下“精炼炉 + 提取钻 + 到输入口的带 + 回收站”";
            return null;
        }

        /// <summary>
        /// 超控阵列（4×4）放在归还核心配电范围外（每格都离核心 &gt; 覆盖半径），由一座 T2 电塔供电：电塔离核心不超过两者半径中较大的一个（接上核心电网），
        /// 阵列最近的占地格离电塔不超过电塔半径。阵列四周留一圈空。在开局已探索区里找（B25）。
        /// </summary>
        internal static bool PlanOverride(CampaignState s, MiniPlan mp, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            float poleR = HomeValleyPowerGrid.CoverRadiusOf(PoleT2);
            string arr = GameLogic.Campaign.Signal.OverrideArrayService.TypeId;
            HashSet<long> avoid = BaseAvoid(s);
            avoid.UnionWith(mp.Plan.Used);
            for (int r = (int)coreR + 3; r <= (int)coreR + 12; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell a = Add(core, dx, dy);
                        if (NearestDist(arr, a, 0, core) <= coreR + 1.5f || !Placeable(s, arr, a, 0, avoid))
                        {
                            continue;
                        }
                        List<GridCell> fp = Footprint(arr, a, 0);
                        if (fp.Any(c => fp.Contains(c) && Enumerable.Range(0, 4).Any(d => !fp.Contains(Step(c, d)) && avoid.Contains(Key(Step(c, d))))))
                        {
                            continue;
                        }
                        // 电塔：在阵列与核心之间，接得上核心（≤ 核心半径）、盖得住阵列（≤ 电塔半径）。
                        GridCell? pole = null;
                        float bestD = float.MaxValue;
                        for (int py = -(int)poleR; py <= (int)poleR; py++)
                        {
                            for (int px = -(int)poleR; px <= (int)poleR; px++)
                            {
                                GridCell q = Add(a, px, py);
                                float toCore = Vector2.Distance(new Vector2(q.X, q.Y), new Vector2(core.X, core.Y));
                                if (toCore > Math.Max(coreR, poleR) - 0.5f || NearestDist(arr, a, 0, q) > poleR - 0.5f || avoid.Contains(Key(q)) || fp.Contains(q)
                                    || !HomeGridService.ValidatePlacement(s, PoleT2, q, 0, checkCost: false).Ok)
                                {
                                    continue;
                                }
                                float d = NearestDist(arr, a, 0, q);
                                if (d < bestD)
                                {
                                    bestD = d;
                                    pole = q;
                                }
                            }
                        }
                        if (pole == null)
                        {
                            continue;
                        }
                        mp.Array = a;
                        mp.Pole = pole.Value;
                        mp.Plan.Buildings.Add(new Placed { Key = "array", Type = arr, Pivot = a, Rot = 0, Label = HomeGridService.DisplayName(arr) });
                        foreach (GridCell c in fp)
                        {
                            mp.Plan.Used.Add(Key(c));
                        }
                        mp.Plan.Used.Add(Key(pole.Value));
                        return true;
                    }
                }
            }
            why = "核心配电范围外放不下“超控阵列 + 只由一座 T2 电塔供电”";
            return false;
        }

        // ── 动作队列（正式输入）──────────────────────────────────────────────────────

        /// <summary>
        /// 动作编码（'|' 分隔）：
        /// B|类型|x|y|朝向（度）|说明 = 放一座建筑的虚影；T|工具|x|y|方向|说明 = 单格带方向的物流件（分流器）；C|工具|x|y|说明 = 单格无方向的件（泵、储罐）；
        /// R|工具|路线键|终点方向|说明 = 一条传送带路线（分段拖拽）；Q|工具|路线键|说明 = 一条管线路线（分段拖拽，不核对方向）。
        /// </summary>
        internal static string ActB(Placed b) => $"B|{b.Type}|{b.Pivot.X}|{b.Pivot.Y}|{b.Rot}|{b.Label}";

        internal static string ActT(string tool, GridCell c, int dir, string label) => $"T|{tool}|{c.X}|{c.Y}|{dir}|{label}";

        internal static string ActC(string tool, GridCell c, string label) => $"C|{tool}|{c.X}|{c.Y}|{label}";

        internal static string ActR(string tool, string pathKey, int goalDir, string label) => $"R|{tool}|{pathKey}|{goalDir}|{label}";

        internal static string ActQ(string tool, string pathKey, string label) => $"Q|{tool}|{pathKey}|{label}";

        internal static void SetActions(JourneyContext c, string list, IEnumerable<string> actions)
        {
            List<string> a = actions.ToList();
            c.SetInt(list + ".n", a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                c.Set(list + ".a" + i.ToString(CultureInfo.InvariantCulture), a[i]);
            }
            c.SetInt(list + ".i", 0);
        }

        internal static void SavePath(JourneyContext c, string key, List<GridCell> path) => c.Set(key, FgjM3Common.EncodeCells(path));

        internal static List<GridCell> LoadPath(JourneyContext c, string key) => FgjM3Common.DecodeCells(c.Get(key));

        private static string AK(JourneyContext c, string list, int i, string k) =>
            list + ".a" + i.ToString(CultureInfo.InvariantCulture) + ".t" + c.Attempt.ToString(CultureInfo.InvariantCulture) + "." + k;

        private static bool AOnce(JourneyContext c, string key, Func<bool> act)
        {
            if (c.GetInt(key) == 1)
            {
                return true;
            }
            if (JourneyInput.Holding)
            {
                c.SetLong(key + ".ps", 0);
                return false;
            }
            long settle = c.GetLong(key + ".ps");
            if (settle == -1)
            {
                c.SetLong(key + ".ps", Math.Max(1, NowMs()));
                return false;
            }
            if (settle > 0 && NowMs() - settle < 350)
            {
                return false;
            }
            if (act())
            {
                c.SetInt(key, 1);
                c.SetLong(key + ".at", NowMs());
                return true;
            }
            c.SetLong(key + ".ps", -1);
            return false;
        }

        private static long ASince(JourneyContext c, string key) => c.GetInt(key) == 1 ? NowMs() - c.GetLong(key + ".at") : -1;

        internal static bool BeltPlannedOrBuilt(CampaignState s, GridCell cell) =>
            HomeValleyConstruction.TryFindPlannedCell(s, cell, out _, out _) || BeltNetworkService.TryGetPiece(cell, out _, out _);

        internal static bool PipePlannedOrBuilt(CampaignState s, GridCell cell) =>
            HomeValleyConstruction.TryFindPlannedCell(s, cell, out _, out _) || PipeNetworkService.TryGetPiece(cell, out _, out _);

        internal static BuildingRecord BuildingAtPivot(CampaignState s, string type, GridCell pivot)
        {
            BuildingRecord b = HomeGridService.BuildingAt(s, pivot);
            return b != null && b.BuildingTypeId == type && b.GridX == pivot.X && b.GridY == pivot.Y ? b : null;
        }

        private static bool IsPipeTool(string tool) => tool == PipeT1 || tool == PumpTool || tool == TankTool || tool.StartsWith("pipe", StringComparison.Ordinal);

        private static bool ActionDone(JourneyContext c, string[] a)
        {
            CampaignState s = St;
            switch (a[0])
            {
                case "B":
                    return BuildingAtPivot(s, a[1], new GridCell(I(a[2]), I(a[3]))) != null;
                case "T":
                case "C":
                {
                    var cell = new GridCell(I(a[2]), I(a[3]));
                    return IsPipeTool(a[1]) ? PipePlannedOrBuilt(s, cell) : BeltPlannedOrBuilt(s, cell);
                }
                case "R":
                case "Q":
                    return LoadPath(c, a[2]).All(p => IsPipeTool(a[1]) ? PipePlannedOrBuilt(s, p) : BeltPlannedOrBuilt(s, p));
            }
            return false;
        }

        private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

        /// <summary>把动作队列 <paramref name="list"/> 逐个做完（建造模式要开着）。全部做完 = Done（报告每个动作的结果摘要）。</summary>
        internal static StepOutcome TickActions(JourneyContext c, string list, double perActionTimeout = 25)
        {
            CampaignState s = St;
            int n = c.GetInt(list + ".n");
            int i = c.GetInt(list + ".i");
            if (i >= n)
            {
                return StepOutcome.Done($"{n} 个动作全部做完（{c.Get(list + ".log", string.Empty)}）；废料 {s.Scrap}");
            }
            if (!FgjM3Common.BuildOpen)
            {
                if (NowMs() - c.GetLong(list + ".openAt") > 1200)
                {
                    c.SetLong(list + ".openAt", NowMs());
                    FgjM3Common.PressBuild(true);
                }
                return StepOutcome.Wait;
            }
            string[] a = c.Get(list + ".a" + i.ToString(CultureInfo.InvariantCulture)).Split('|');
            string label = a[a.Length - 1];
            string started = AK(c, list, i, "start");
            if (c.GetLong(started) == 0)
            {
                c.SetLong(started, NowMs());
            }
            if (ActionDone(c, a))
            {
                c.Set(list + ".log", c.Get(list + ".log", string.Empty) + (i == 0 ? string.Empty : "；") + label);
                c.Log($"动作 {i + 1}/{n}：{label} ✓");
                c.SetInt(list + ".i", i + 1);
                return StepOutcome.Wait;
            }
            if ((NowMs() - c.GetLong(started)) / 1000.0 > perActionTimeout)
            {
                return StepOutcome.Retry($"动作 {i + 1}/{n}“{label}”{perActionTimeout:F0} 秒没做成（状态行“{Mode?.StatusText}”）");
            }
            string tool = a[1];
            if (!FgjM3Common.EntrySelected(tool))
            {
                string pk = AK(c, list, i, "pick");
                if (NowMs() - c.GetLong(pk) < 400)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(pk, NowMs());
                if (!FgjM3Common.PickEntry(tool, out string why, out bool scrolling) && !scrolling)
                {
                    return StepOutcome.Retry($"建造栏选不中 {tool}：{why}");
                }
                return StepOutcome.Wait;
            }
            switch (a[0])
            {
                case "B":
                case "T":
                    return TickSingle(c, list, i, a, tool, new GridCell(I(a[2]), I(a[3])), a[0] == "B" ? I(a[4]) : I(a[4]) * 90, a[0] == "B");
                case "C":
                    return TickSingle(c, list, i, a, tool, new GridCell(I(a[2]), I(a[3])), -1, false);
                case "R":
                case "Q":
                    return TickRoute(c, list, i, a[2], a[0] == "R" ? I(a[3]) : -1, a[0] == "R");
            }
            return StepOutcome.Fail("不认识的动作 " + a[0]);
        }

        /// <summary>单格动作：转到要的朝向（-1 = 不管朝向）→ 指着格子（建筑核对预览合法）→ 左键单击 → 核对成了规划中的虚影。</summary>
        private static StepOutcome TickSingle(JourneyContext c, string list, int i, string[] a, string tool, GridCell cell, int wantRot, bool building)
        {
            if (wantRot >= 0)
            {
                int want = GridMath.NormalizeRotation(wantRot);
                if (GridMath.NormalizeRotation(Mode.GhostRotation) != want)
                {
                    string rk = AK(c, list, i, "rot");
                    if (JourneyInput.Holding || NowMs() - c.GetLong(rk) < 250)
                    {
                        return StepOutcome.Wait;
                    }
                    c.SetLong(rk, NowMs());
                    JourneyInput.PressAction(GameActionId.Rotate);
                    return StepOutcome.Wait;
                }
            }
            string hk = AK(c, list, i, "hover");
            if (!AOnce(c, hk, () => FgjM3Common.TryHover(FgjM3Common.Ground(cell))))
            {
                return StepOutcome.Wait;
            }
            if (!Mode.HasHover || Mode.HoverCell != cell)
            {
                if (ASince(c, hk) > 1500)
                {
                    c.SetInt(hk, 0);
                }
                return StepOutcome.Wait;
            }
            if (building)
            {
                GridPlacementResult pv = Mode.Preview;
                if (pv == null || !pv.Ok)
                {
                    return ASince(c, hk) < 2000 ? StepOutcome.Wait : StepOutcome.Fail($"指着 {Cell(cell)} 时{HomeGridService.DisplayName(tool)}的预览不能放（状态行“{Mode.StatusText}”）");
                }
            }
            string ck = AK(c, list, i, "click");
            if (!AOnce(c, ck, () => FgjM3Common.TryClick(FgjM3Common.Ground(cell))))
            {
                return StepOutcome.Wait;
            }
            if (ASince(c, ck) < 2500)
            {
                return StepOutcome.Wait; // 下一帧起 ActionDone 会核对到虚影
            }
            return StepOutcome.Retry($"单击 {Cell(cell)} 后没有 {tool} 的虚影（状态行“{Mode.StatusText}”）");
        }

        /// <summary>一条路线：按方向切段，每段按住左键拖（单格段：传送带先按旋转键转向再单击）；松开后核对这一段每格都成了虚影（传送带还核对方向）。</summary>
        private static StepOutcome TickRoute(JourneyContext c, string list, int i, string pathKey, int goalDir, bool belt)
        {
            List<GridCell> path = LoadPath(c, pathKey);
            List<(int a, int b, int dir)> runs = FgjM3Common.Runs(path, Math.Max(0, goalDir));
            List<int> dirs = FgjM3Common.RouteDirs(path, Math.Max(0, goalDir));
            string ri = AK(c, list, i, "run");
            int k = c.GetInt(ri);
            if (k >= runs.Count)
            {
                return StepOutcome.Wait; // ActionDone 核对
            }
            (int a, int b, int dir) run = runs[k];
            string rk = AK(c, list, i, "r" + k.ToString(CultureInfo.InvariantCulture));
            GridCell ca = path[run.a];
            GridCell cb = path[run.b];
            CampaignState s = St;
            // 这一段已经全是虚影 / 建成（重试时）：跳过。
            if (Enumerable.Range(run.a, run.b - run.a + 1).All(x => belt ? BeltPlannedOrBuilt(s, path[x]) : PipePlannedOrBuilt(s, path[x])))
            {
                c.SetInt(ri, k + 1);
                return StepOutcome.Wait;
            }
            if (run.a == run.b)
            {
                if (belt)
                {
                    int want = run.dir * 90;
                    if (GridMath.NormalizeRotation(Mode.GhostRotation) != want)
                    {
                        if (JourneyInput.Holding || NowMs() - c.GetLong(rk + ".rot") < 250)
                        {
                            return StepOutcome.Wait;
                        }
                        c.SetLong(rk + ".rot", NowMs());
                        JourneyInput.PressAction(GameActionId.Rotate);
                        return StepOutcome.Wait;
                    }
                }
                if (!AOnce(c, rk, () => FgjM3Common.TryClick(FgjM3Common.Ground(ca))))
                {
                    return StepOutcome.Wait;
                }
            }
            else if (!AOnce(c, rk, () => FgjM3Common.TryDrag(FgjM3Common.Ground(ca), FgjM3Common.Ground(cb))))
            {
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (info.Length > 0 && run.a != run.b)
            {
                c.Set(list + ".dragInfo", info.Replace("\n", " "));
            }
            if (ASince(c, rk) < 500)
            {
                return StepOutcome.Wait;
            }
            for (int x = run.a; x <= run.b; x++)
            {
                bool okCell;
                if (belt)
                {
                    okCell = HomeValleyConstruction.TryFindPlannedCell(s, path[x], out PlannedBeltRecord p, out int idx) && p.Dirs != null && idx >= 0 && idx < p.Dirs.Length && p.Dirs[idx] == dirs[x];
                }
                else
                {
                    okCell = PipePlannedOrBuilt(s, path[x]);
                }
                if (!okCell)
                {
                    if (ASince(c, rk) < 1500)
                    {
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Retry($"第 {k + 1} 段 {Cell(ca)}→{Cell(cb)} 松开后 {Cell(path[x])} 不是{(belt ? "朝" + FgjM3Common.DirName(dirs[x]) + "的" : string.Empty)}虚影（状态行“{Mode?.StatusText}”）");
                }
            }
            c.SetInt(ri, k + 1);
            return StepOutcome.Wait;
        }

        // ── 等施工完成 ────────────────────────────────────────────────────────────────

        internal static bool BuildingReady(CampaignState s, string type, GridCell pivot, out BuildingRecord b)
        {
            b = BuildingAtPivot(s, type, pivot);
            return b != null && !HomeValleyController.IsPlannedGhost(b) && b.ConstructionState == BuildingConstructionState.Operational;
        }

        internal static bool RouteBuilt(CampaignState s, List<GridCell> path, bool belt) =>
            path.All(p => !HomeValleyConstruction.TryFindPlannedCell(s, p, out _, out _) && (belt ? BeltNetworkService.TryGetPiece(p, out _, out _) : PipeNetworkService.TryGetPiece(p, out _, out _)));

        // ── 面板：建造模式里点建筑 → 通用面板 → 配方下拉 ─────────────────────────────────────

        /// <summary>建造模式里（手上不拿条目）左键点建筑：打开它的通用面板。手上拿着条目时先右键放下（右键 = 取消选择，建造模式不退出）。</summary>
        internal static StepOutcome TickOpenPanel(JourneyContext c, string type, GridCell pivot, string k = "panel")
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord b = BuildingAtPivot(s, type, pivot);
            if (b == null)
            {
                return StepOutcome.Fail($"{Cell(pivot)} 没有{HomeGridService.DisplayName(type)}");
            }
            if (ProductionPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.BuildingId == b.BuildingId)
            {
                return StepOutcome.Done($"左键点{HomeGridService.DisplayName(type)} {Cell(pivot)}：通用面板打开（“{ProductionPanelUIToolkit.Instance?.TitleText}”，{ProductionPanelUIToolkit.Instance?.StateText}）");
            }
            if (!FgjM3Common.BuildOpen)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, k + ".open")) > 1200)
                {
                    c.SetLong(FgjM3Common.SK(c, k + ".open"), NowMs());
                    FgjM3Common.PressBuild(true);
                }
                LogStuck(c, k + ".b", () => "按了建造键，建造模式还没打开（" + JourneyInput.LastUiFailure + "）");
                return StepOutcome.Wait;
            }
            if (Mode.SelectedEntryId != null)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, k + ".rc")) > 800)
                {
                    c.SetLong(FgjM3Common.SK(c, k + ".rc"), NowMs());
                    FgjM3Common.TryClick(FgjM3Common.Ground(Add(pivot, 0, 0)), 1);
                }
                LogStuck(c, k + ".sel", () => $"手上还拿着建造条目 {Mode.SelectedEntryId}，右键放下");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, k + ".click", () => FgjM3Common.TryClick(FgjM3Common.Ground(pivot))))
            {
                LogStuck(c, k, () => FgjM3Common.Clear(FgjM3Common.Ground(pivot), out string why) ? "可以点了" : $"{HomeGridService.DisplayName(type)} {Cell(pivot)} 还点不到：{why}");
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, k + ".click") < 1500 ? StepOutcome.Wait
                : StepOutcome.Retry($"点{HomeGridService.DisplayName(type)} {Cell(pivot)} 后通用面板没打开（状态行“{Mode.StatusText}”）");
        }

        /// <summary>通用面板的配方下拉选 <paramref name="recipeId"/>（下拉框先滚进可见区、确认能点、没被挡住，再设值——与在弹出菜单里点那一项同一个值变化回调）。</summary>
        internal static StepOutcome TickPickRecipe(JourneyContext c, string recipeId)
        {
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            if (!ProductionPanelUIToolkit.IsOpen || panel == null)
            {
                return StepOutcome.Fail("通用面板没开");
            }
            if (!ItemCatalog.TryGetRecipe(recipeId, out RecipeDef recipe))
            {
                return StepOutcome.Fail("配方表里没有 " + recipeId);
            }
            if (!ProductionService.TryGet(St, ProductionPanelUIToolkit.BuildingId, out ProductionService.Producer p))
            {
                return StepOutcome.Fail("面板上的建筑不是生产建筑");
            }
            if (p.Recipe != null && p.Recipe.Id == recipeId)
            {
                return StepOutcome.Done($"配方下拉选“{recipe.Name}”：{panel.RecipeLineText.Replace("\n", " ")}（状态“{panel.StateText}”）");
            }
            DropdownField d = panel.RecipeField;
            ScrollView body = JourneyInput.FindUitk<ScrollView>(PanelHost, "ProductionPanelBody");
            if (d == null || !panel.RecipeDropdownVisible)
            {
                return StepOutcome.Fail("通用面板上没有配方下拉");
            }
            if (body != null && body.Contains(d) && !JourneyInput.ScrollIntoView(body, d))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.IsClickable(d))
            {
                return StepOutcome.Retry("配方下拉点不到");
            }
            if (!d.choices.Contains(recipe.Name))
            {
                return StepOutcome.Fail($"配方下拉里没有“{recipe.Name}”（{string.Join("、", d.choices)}）");
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "pick")) < 600)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "pick"), NowMs());
            d.value = recipe.Name;
            return StepOutcome.Wait;
        }

        internal static StepOutcome TickClosePanel(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!ProductionPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Done("点“关闭”关闭通用面板");
            }
            if (!FgjM3Common.Once(c, "close", () => JourneyInput.ClickUitk(PanelHost, "ProductionPanelClose")))
            {
                LogStuck(c, "close", () => "“关闭”还点不到：" + JourneyInput.LastUiFailure);
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "close") < 1500 ? StepOutcome.Wait : StepOutcome.Retry("点“关闭”后通用面板还开着：" + JourneyInput.LastUiFailure);
        }

        // ── 读数（只读）────────────────────────────────────────────────────────────────

        internal static int Stock(string itemId) => HomeInventory.Stock(St, itemId);

        internal static long Completed(BuildingRecord b) => b != null && ProductionService.TryGet(St, b.BuildingId, out ProductionService.Producer p) ? p.Rec.Completed : -1;

        internal static ProductionService.Producer Prod(BuildingRecord b) => b != null && ProductionService.TryGet(St, b.BuildingId, out ProductionService.Producer p) ? p : null;

        internal static string StockLine(params string[] items) => string.Join("、", items.Select(i => $"{ItemCatalog.NameOf(i)} {Stock(i)}"));

        internal static GridCell P(JourneyContext c, string key) => FgjM3Common.GetCell(c, key);

        internal static void SetP(JourneyContext c, string key, GridCell cell) => FgjM3Common.SetCell(c, key, cell);

        internal static BuildingRecord Bld(JourneyContext c, string key) => BuildingAtPivot(St, c.Get(key + ".type"), P(c, key));

        internal static void Remember(JourneyContext c, Placed b)
        {
            SetP(c, b.Key, b.Pivot);
            c.Set(b.Key + ".type", b.Type);
            c.Set(b.Key + ".recipe", b.Recipe ?? string.Empty);
        }

        /// <summary>
        /// 点面板里的控件：它在滚动区里时先滚到看得见（玩家也得先滚滚轮；这一帧只滚，返回 null = 下一帧再点），再经拾取确认没被挡住后点。
        /// </summary>
        internal static bool? ClickInView(VisualElement e) => InView(e, true);

        /// <summary>点不到时每 5 秒记一行原因（被什么挡着 / 不在画面里），卡住超时时日志里看得到为什么。</summary>
        internal static void LogStuck(JourneyContext c, string k, Func<string> why)
        {
            string key = FgjM3Common.SK(c, k + ".stuck");
            int bucket = (int)(c.StepElapsed / 5);
            if (bucket > c.GetInt(key))
            {
                c.SetInt(key, bucket);
                c.Log(why());
            }
        }

        /// <summary>
        /// 下拉框：滚到看得见、确认能点没被挡住（不真的点开弹出菜单——弹出菜单是盖满面板的一层，旅程随后直接设值，与在弹出菜单里点那一项同一个值变化回调，
        /// 沿用 <see cref="FgjM3Common.PickPortFilter"/> 的写法）。返回 null = 这一帧只滚了、下一帧再试。
        /// </summary>
        internal static bool? PickableInView(VisualElement e) => InView(e, false);

        private static bool? InView(VisualElement e, bool click)
        {
            for (VisualElement p = e?.parent; p != null; p = p.parent)
            {
                if (p is ScrollView sv)
                {
                    if (!JourneyInput.ScrollIntoView(sv, e))
                    {
                        FgjM3Common.PanelBodyScrolls++;
                        return null;
                    }
                    break;
                }
            }
            return click ? JourneyInput.ClickElement(e) : JourneyInput.IsClickable(e);
        }

        internal static string Describe(FactoryPlan plan, GridCell core) =>
            string.Join("；", plan.Buildings.Select(b => $"{b.Label} {Cell(b.Pivot)}（相对核心 {b.Pivot.X - core.X},{b.Pivot.Y - core.Y}）")) +
            $"；分流器 {plan.Splitters.Count} 个；路线 {plan.Routes.Count} 条共 {plan.Routes.Sum(r => r.Path.Count)} 格";
    }
}
