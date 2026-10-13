using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG7-E2E-01：FGJ-M7 的规划（只读游戏状态，B25：按种子地形现找，不写死坐标）。
    /// 冷却液线：离核心最近的一片水源上放一格泵 → 管线送进调配站南口（水）；调配站西侧输入口接稀土提取钻的传送带；东口（冷却液）→ 管线 → 净化塔南口。
    /// 两张管网（水 / 冷却液）互不相邻；调配站与净化塔都在归还核心配电范围里；净化塔放在半径内污染格最多的地方（让归还面积看得见地涨）。
    /// 提取钻要压在已探索的稀土矿脉上，规划在探索之后单独做（<see cref="PlanDrill"/>），不在核心配电范围里时串 T2 电塔。
    /// </summary>
    internal static class FgjM7Common
    {
        internal const string Blender = "blending_station";
        internal const string Purifier = "purifier";
        internal const string Storage = "energy_storage";
        internal const string NodePole = "energy.pole_t2";
        internal const string NodeRoof = "environment.roof";
        internal const string NodeFuelGen = "energy.fuel_generator";
        internal const string NodeStorage = "energy.storage";

        internal static CampaignState St => CampaignSession.Current;

        internal static string Cell(GridCell c) => FgjM3Common.Cell(c);

        internal sealed class CoolantPlan
        {
            public GridCell Blender;
            public GridCell Purifier;
            public GridCell Pump;
            public List<GridCell> Water;
            public List<GridCell> Coolant;
            public GridCell ItemIn;
            public int ItemGoalDir;
            public int Score;
        }

        private static IEnumerable<GridCell> Ring(GridCell c, int r)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r)
                    {
                        yield return new GridCell(c.X + dx, c.Y + dy);
                    }
                }
            }
        }

        private static IEnumerable<GridCell> WithNeighbors(IEnumerable<GridCell> cells)
        {
            foreach (GridCell c in cells)
            {
                yield return c;
                for (int d = 0; d < 4; d++)
                {
                    yield return P.Step(c, d);
                }
            }
        }

        /// <summary>净化塔放在 <paramref name="pivot"/> 时半径内生成污染 &gt; 0 的格数（规划评分；读格网，不改状态）。</summary>
        internal static int PollutedInRadius(CampaignState s, GridCell pivot, int radius)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            int n = 0;
            int r2 = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy <= r2 && map.GetPollution(new GridCell(pivot.X + dx, pivot.Y + dy)) > 0)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        /// <summary>
        /// 冷却液线规划（见类说明）。<paramref name="plan"/> 已有的占用（实验室、回收站、发电机等）都绕开；成功时把调配站 / 净化塔 / 泵 / 两条管线记进 plan 的占用。
        /// </summary>
        internal static CoolantPlan PlanCoolant(CampaignState s, P.FactoryPlan plan, out string why)
        {
            why = null;
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HomeGridMap map = HomeGridService.MapFor(s);
            int water = PipeNetworkService.FluidId("water");
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            int radius = PurificationService.RadiusOfTier(Purifier, 1);
            var pumps = new List<GridCell>();
            for (int r = 2; r <= 40 && pumps.Count < 8; r++)
            {
                foreach (GridCell u in Ring(core, r))
                {
                    if (pumps.Count < 8 && !avoid.Contains(P.Key(u)) && map.IsExplored(u) && PipeNetworkService.SourceFluidAt(s, u) == water
                        && HomeGridService.ValidatePipeCell(s, u, PipePieceKind.Pump).Ok && Math.Abs(u.X - core.X) + Math.Abs(u.Y - core.Y) > 0)
                    {
                        pumps.Add(u);
                    }
                }
            }
            if (pumps.Count == 0)
            {
                why = "核心 40 格内找不到已探索、能放泵的水源";
                return null;
            }
            CoolantPlan best = null;
            int bestCost = int.MaxValue;
            int tried = 0;
            foreach (GridCell u in pumps)
            {
                int validBlenders = 0;
                for (int r = 3; r <= 12 && validBlenders < 6; r++)
                {
                    foreach (GridCell b in Ring(u, r))
                    {
                        if (validBlenders >= 6)
                        {
                            break;
                        }
                        if (P.NearestDist(Blender, b, 0, core) > coreR - 1f || !P.Placeable(s, Blender, b, 0, avoid))
                        {
                            continue;
                        }
                        if (!P.FluidPortOutside(Blender, b, 0, Blender + ".fin0", out GridCell wIn, out _)
                            || !P.FluidPortOutside(Blender, b, 0, Blender + ".fout0", out GridCell cOut, out _)
                            || !P.PortOutside(Blender, b, 0, Blender + ".in0", out GridCell itemIn, out int itemFace))
                        {
                            continue;
                        }
                        var occupied = new HashSet<long>(avoid);
                        foreach (GridCell f in P.Footprint(Blender, b, 0))
                        {
                            occupied.Add(P.Key(f));
                        }
                        occupied.Add(P.Key(itemIn));
                        bool PipeCellOk(GridCell x) => !occupied.Contains(P.Key(x)) && HomeGridService.ValidatePipeCell(s, x, PipePieceKind.Pipe).Ok
                                                       && PipeNetworkService.SourceFluidAt(s, x) == 0;
                        if (!PipeCellOk(wIn) || !PipeCellOk(cOut) || avoid.Contains(P.Key(itemIn)) || !HomeGridService.ValidateBeltCell(s, itemIn).Ok)
                        {
                            continue;
                        }
                        // 水管：从泵的某个邻格到南口，绕开冷却液口与它的四邻。
                        var keepW = new HashSet<long>(occupied);
                        foreach (GridCell x in WithNeighbors(new[] { cOut }))
                        {
                            keepW.Add(P.Key(x));
                        }
                        if (keepW.Contains(P.Key(wIn)) || keepW.Contains(P.Key(u)))
                        {
                            continue;
                        }
                        List<GridCell> wpath = null;
                        for (int d = 0; d < 4; d++)
                        {
                            GridCell start = P.Step(u, d);
                            if (keepW.Contains(P.Key(start)) && start != wIn)
                            {
                                continue;
                            }
                            List<GridCell> route = start == wIn ? (PipeCellOk(wIn) ? new List<GridCell> { wIn } : null)
                                : PipeCellOk(start) ? P.PlanRoute(s, start, wIn, 0, -1, keepW, 8, P.PipeOk(s)) : null;
                            if (route != null && (wpath == null || route.Count < wpath.Count))
                            {
                                wpath = route;
                            }
                        }
                        tried++;
                        if (wpath == null)
                        {
                            continue;
                        }
                        validBlenders++;
                        // 冷却液管：东口外一格 → 净化塔南口外一格；离水管网（含泵）至少隔一格。
                        var keepC = new HashSet<long>(occupied);
                        foreach (GridCell x in WithNeighbors(wpath.Concat(new[] { u })))
                        {
                            keepC.Add(P.Key(x));
                        }
                        if (keepC.Contains(P.Key(cOut)))
                        {
                            continue;
                        }
                        var avoidP = new HashSet<long>(occupied);
                        foreach (GridCell x in wpath.Concat(new[] { u, cOut }))
                        {
                            avoidP.Add(P.Key(x));
                        }
                        int validPur = 0;
                        for (int pr = 2; pr <= 9 && validPur < 8; pr++)
                        {
                            foreach (GridCell pv in Ring(cOut, pr))
                            {
                                if (validPur >= 8)
                                {
                                    break;
                                }
                                if (P.NearestDist(Purifier, pv, 0, core) > coreR - 1f || !P.Placeable(s, Purifier, pv, 0, avoidP)
                                    || !PurificationService.TryIntakeCell(Purifier, pv, 0, out GridCell intake))
                                {
                                    continue;
                                }
                                if (intake != cOut && (keepC.Contains(P.Key(intake)) || !PipeCellOk(intake)))
                                {
                                    continue;
                                }
                                int score = PollutedInRadius(s, pv, radius);
                                if (score < 20)
                                {
                                    continue;
                                }
                                var keepCP = new HashSet<long>(keepC);
                                foreach (GridCell f in P.Footprint(Purifier, pv, 0))
                                {
                                    keepCP.Add(P.Key(f));
                                }
                                List<GridCell> cpath = intake == cOut ? new List<GridCell> { cOut } : P.PlanRoute(s, cOut, intake, 0, -1, keepCP, 8, P.PipeOk(s));
                                if (cpath == null)
                                {
                                    continue;
                                }
                                validPur++;
                                int cost = wpath.Count + cpath.Count - score / 2;
                                if (cost < bestCost)
                                {
                                    bestCost = cost;
                                    best = new CoolantPlan
                                    {
                                        Blender = b, Purifier = pv, Pump = u, Water = wpath, Coolant = cpath, ItemIn = itemIn, ItemGoalDir = P.Opp(itemFace), Score = score,
                                    };
                                }
                            }
                        }
                    }
                }
                if (best != null)
                {
                    break;
                }
            }
            if (best == null)
            {
                why = $"{pumps.Count} 处水源附近都放不下“调配站 + 净化塔（半径内至少 20 格污染）”或铺不通两条互不相邻的管线（试了 {tried} 个调配站位置）";
                return null;
            }
            M.ReserveIn(plan, Blender, best.Blender, 0);
            M.ReserveIn(plan, Purifier, best.Purifier, 0);
            plan.Buildings.Add(new P.Placed { Key = "blender", Type = Blender, Pivot = best.Blender, Rot = 0, Label = HomeGridService.DisplayName(Blender) });
            plan.Buildings.Add(new P.Placed { Key = "purifier", Type = Purifier, Pivot = best.Purifier, Rot = 0, Label = HomeGridService.DisplayName(Purifier) });
            foreach (GridCell x in best.Water.Concat(best.Coolant).Concat(new[] { best.Pump, best.ItemIn }))
            {
                plan.Used.Add(P.Key(x));
            }
            return best;
        }

        /// <summary>离 <paramref name="from"/> 最近的稀土矿脉格（不看迷雾；地形按种子生成）。</summary>
        internal static bool NearestRare(CampaignState s, GridCell from, out GridCell at) => P.NearestTerrain(s, from, "ore_rare", 90, out at);

        /// <summary>提取钻 + 传送带到调配站西侧输入口；不在配电范围里时串 T2 电塔。只在矿脉已探索时成功。</summary>
        internal static P.FactoryPlan PlanDrill(CampaignState s, GridCell itemIn, int goalDir, IEnumerable<GridCell> reserved, out List<GridCell> poles, out string why)
        {
            poles = null;
            var plan = new P.FactoryPlan { Origin = HomeGridService.CorePivot(s) };
            foreach (GridCell x in reserved)
            {
                plan.Used.Add(P.Key(x));
            }
            plan.Used.Remove(P.Key(itemIn));
            P.Route route = PlanRareDrill(s, plan, itemIn, goalDir, 90, out P.Placed drill, out why);
            if (route == null)
            {
                return null;
            }
            route.Key = "r_rare";
            route.Label = "稀土提取钻 → 调配站西侧输入口";
            poles = P.PlanPoles(s, plan, new[] { drill }, out why);
            return poles == null ? null : plan;
        }

        /// <summary>
        /// 稀土提取钻：与 <see cref="P.PlanDrill"/> 同一做法（由近到远、取传送带代价最小），但脚下稀土矿脉格必须多于金属矿脉格——
        /// 提取钻“脚下哪种矿脉格多就采哪种（一样多时采金属矿）”，压着金属矿的钻会把金属矿送进调配站、把输入带堵死（第 1 次运行实测）。
        /// </summary>
        private static P.Route PlanRareDrill(CampaignState s, P.FactoryPlan plan, GridCell goal, int goalDir, int maxR, out P.Placed drill, out string why)
        {
            drill = null;
            why = null;
            byte rare = P.TerrainCode("ore_rare");
            byte metal = P.TerrainCode("ore_metal");
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var cands = new List<(int d, GridCell p)>();
            for (int dy = -maxR; dy <= maxR; dy++)
            {
                for (int dx = -maxR; dx <= maxR; dx++)
                {
                    var p = new GridCell(goal.X + dx, goal.Y + dy);
                    List<GridCell> fp = P.Footprint(P.Drill, p, 0);
                    int r = fp.Count(c => P.IsTerrain(s, c, rare));
                    int m = fp.Count(c => P.IsTerrain(s, c, metal));
                    if (r > m)
                    {
                        cands.Add((Math.Abs(dx) + Math.Abs(dy), p));
                    }
                }
            }
            P.Route best = null;
            int bestCost = int.MaxValue;
            int ok = 0;
            foreach (var cnd in cands.OrderBy(x => x.d))
            {
                if (ok >= 6)
                {
                    break;
                }
                if (!P.Placeable(s, P.Drill, cnd.p, 0, avoid) || !P.PortOutside(P.Drill, cnd.p, 0, P.Drill + ".out0", out GridCell outCell, out _) || avoid.Contains(P.Key(outCell)))
                {
                    continue;
                }
                var avoidPlus = new HashSet<long>(avoid);
                foreach (GridCell c in P.Footprint(P.Drill, cnd.p, 0))
                {
                    avoidPlus.Add(P.Key(c));
                }
                List<GridCell> path = P.PlanRoute(s, outCell, goal, goalDir, 3, avoidPlus, 10, P.BeltOk(s));
                if (path == null)
                {
                    continue;
                }
                ok++;
                int cost = P.RouteCost(path, goalDir);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = new P.Route { Key = "r_rare", Path = path, GoalDir = goalDir };
                    drill = new P.Placed { Key = "drillR", Type = P.Drill, Pivot = cnd.p, Rot = 0, Label = HomeGridService.DisplayName(P.Drill) + "（稀土）" };
                }
            }
            if (best == null)
            {
                why = cands.Count == 0 ? $"调配站输入口 {Cell(goal)} 周围 {maxR} 格内没有“稀土格多于金属格”的提取钻位置" : $"{cands.Count} 个稀土位置都放不下提取钻或铺不通到 {Cell(goal)} 的带（多半还没探索）";
                return null;
            }
            plan.Buildings.Add(drill);
            M.ReserveIn(plan, P.Drill, drill.Pivot, 0);
            foreach (GridCell c in best.Path)
            {
                plan.Used.Add(P.Key(c));
            }
            plan.Routes.Add(best);
            return best;
        }

        /// <summary>选一座建筑的耐久（日志 / 断言用）。</summary>
        internal static float Hp(BuildingRecord b) => b?.Health ?? -1f;
    }
}
