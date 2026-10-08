using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG6-E2E-01：M6 两条旅程（FGJ-M6 / FGJ-M6R）共用的部分。
    /// ① 家园防线的位置（B25：按种子地形现找，不写死坐标）——压在废墟上的回收站与供料带、两座仿真实验室、离核心最近一片油井上的精炼链（泵 → 原油管线 → 精炼塔 →
    ///    燃油管线 + 储罐；酸液口直接接废液池）、围着归还核心的六座轻型炮塔与一座护盾发生器、核心外一圈屏障（按格子能不能放切成几段直线拖）、按需的发电机 2；
    ///    火墙等第一次突袭之后按预警条上的抵达点再规划（离抵达点最近的炮塔改装成燃迹炮塔 + 旁边的漏油陷阱发射器 + 从储罐接过去的燃油管线，必要时从屏障底下穿地下管线）。
    /// ② 正式输入的操作：建造栏拖屏障、通用面板 → 炮塔 / 防御面板、远征准备面板的“关闭信号塔广播”、点通知弹出条、突袭条 / 远征小窗的按钮。
    /// ③ 进度夹具（只在登记的 Apply*Fixture 方法里，[M6 出口] A2 扫描源码守护；每处一条 DEBT，见 ADR-QA-023）。
    /// 全部玩家动作走 <see cref="JourneyInput"/> 的正式输入通道；规划与读数只读游戏状态。
    /// </summary>
    internal static class FgjM6Common
    {
        internal const string Lab = ResearchCatalog.LabTypeId;
        internal const string Turret = TurretCatalog.LightTypeId;
        internal const string Shield = "shield_gen";
        internal const string Wall = "barrier_t2";
        internal const string Trap = "trap_emitter";
        internal const string NodeRefinery = "industry.refinery_tower";
        internal const string NodePole = "energy.pole_t2";
        internal const string NodeBarrier = "defense.barrier";
        internal const string NodeShield = "defense.shield";
        internal const string NodeTrap = "defense.trap";
        internal const string NodeDrone = "defense.repair_drone";
        internal const string NodeRebuild = "defense.auto_rebuild";
        internal const string FwOil = "fw_oilleak";
        internal const string FwBurn = "fw_burntrail";
        internal const string TurretHost = "[TurretPanelHost]";
        internal const string DefenseHost = "[DefensePanelHost]";
        internal const string RaidHost = "[RaidWarningHost]";
        internal const string PrepHost = "[HomeValleyExpeditionPrepHost]";
        internal const string RulesHost = "[RulesPanelHost]";
        internal const string NotifyHost = "[NotificationHudHost]";
        internal const string ResultHost = "[RaidResultHost]";
        internal const string AwayHost = "[AwayReportHost]";
        internal const string CircuitHost = "[HomeValleyCircuitBoardHost]";
        /// <summary>六座轻型炮塔的旅程键（t1 = 第一座，开局最先造）。</summary>
        internal static readonly string[] TurretKeys = { "t1", "t2", "t3", "t4", "t5", "t6" };
        /// <summary>核心外屏障圈离核心外框的格数（试几圈，取能放的格最多的那一圈）。</summary>
        internal const int RingMinMargin = 6;
        internal const int RingMaxMargin = 8;

        internal static CampaignState St => CampaignSession.Current;

        internal static string Cell(GridCell c) => FgjM3Common.Cell(c);

        internal static long NowMs() => FgjM2Common.NowMs();

        internal static string Name(string type) => HomeGridService.DisplayName(type);

        internal static Vector2 CoreCenter(CampaignState s)
        {
            HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
            return new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        }

        // ── ① 家园规划 ────────────────────────────────────────────────────────────────

        internal sealed class HomePlan
        {
            public P.FactoryPlan Plan;
            public P.RefineryPlan Refinery;
            public readonly List<GridCell> Turrets = new List<GridCell>();
            public GridCell ShieldAt;
            public readonly List<(GridCell A, GridCell B)> WallRuns = new List<(GridCell, GridCell)>();
            public int WallCells;
            public int RingMargin;
            public int RingGaps;
            public bool NeedPoles;
        }

        /// <summary>规划时“只差研究”不算放不下（屏障 / 护盾 / 精炼塔要研究完才放，先按地形与占地找好位置；真正放置时走建造模式的正式校验）。</summary>
        internal static bool PlaceableLater(CampaignState s, string type, GridCell g, ISet<long> avoid) => M.PlaceableLater(s, type, g, avoid);

        /// <summary>
        /// 整套家园规划（FGJ-M6 plan 步与 [M6 出口] B 段同一个入口）：回收站 → 精炼链 → 六座炮塔与护盾（围着核心）→ 核心外一圈屏障 → 两座实验室 → 发电机 2。
        /// 失败返回 null 与原因。
        /// </summary>
        internal static HomePlan PlanHome(CampaignState s, out string why)
        {
            why = null;
            var hp = new HomePlan { Plan = new P.FactoryPlan { Origin = HomeGridService.CorePivot(s) } };
            P.FactoryPlan plan = hp.Plan;
            if (P.PlanRecycler(s, plan, "仓库输入口", out _, out why) == null)
            {
                why = "回收站：" + why;
                return null;
            }
            try
            {
                P.PlanLockedOk = true; // 精炼塔要研究完才放：规划时先按地形找好位置，真正放置时走建造模式的正式校验
                hp.Refinery = P.PlanRefinery(s, plan.Used, out why);
            }
            finally
            {
                P.PlanLockedOk = false;
            }
            if (hp.Refinery == null)
            {
                why = "精炼链：" + why;
                return null;
            }
            P.RefineryPlan r = hp.Refinery;
            foreach (GridCell c in P.Footprint(P.Tower, r.Tower, 0).Concat(P.Footprint(P.Pond, r.Pond, 0)).Concat(r.Crude).Concat(new[] { r.Pump, r.FuelPipe, r.Tank, r.AcidPipe }).Concat(r.Poles))
            {
                plan.Used.Add(P.Key(c));
            }
            // 燃油网（管线 + 储罐）四邻也留空：之后从储罐接出去的燃油管线要从这里起头。
            foreach (GridCell c in new[] { r.FuelPipe, r.Tank })
            {
                for (int d = 0; d < 4; d++)
                {
                    plan.Used.Add(P.Key(P.Step(c, d)));
                }
            }
            hp.NeedPoles = r.Poles.Count > 0;
            Vector2 cc = CoreCenter(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            float half = Mathf.Max(cmax.X - cmin.X, cmax.Y - cmin.Y) * 0.5f;
            // 炮塔：六个方向各一座，离核心外框约 3～4 格（在屏障圈里面），射程 18 米盖住整个核心与圈外的来路。
            for (int i = 0; i < TurretKeys.Length; i++)
            {
                GridCell g = default;
                bool found = false;
                // 这个方向放不下（水 / 悬崖 / 残骸）时左右各偏 20°、再远一点试。
                foreach (float off in new[] { 0f, 20f, -20f, 40f, -40f })
                {
                    float ang = (i * 60f + 15f + off) * Mathf.Deg2Rad;
                    Vector2 want = cc + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (half + 4.5f);
                    if (FindNear(s, plan, Turret, want, 4, 1, out g))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    why = $"核心周围第 {i + 1} 个方向（±40°）放不下轻型炮塔";
                    return null;
                }
                hp.Turrets.Add(g);
                plan.Buildings.Add(new P.Placed { Key = TurretKeys[i], Type = Turret, Pivot = g, Rot = 0, Label = Name(Turret) + " " + (i + 1) });
                M.ReserveIn(plan, Turret, g, 0);
            }
            // 护盾发生器：贴着核心（护盾半径 9 米罩住核心）。
            if (!FindNear(s, plan, Shield, cc + new Vector2(0f, half + 2.5f), 5, 0, out GridCell sh) && !FindNear(s, plan, Shield, cc - new Vector2(0f, half + 2.5f), 5, 0, out sh))
            {
                why = "核心旁边放不下护盾发生器";
                return null;
            }
            hp.ShieldAt = sh;
            plan.Buildings.Add(new P.Placed { Key = "shield", Type = Shield, Pivot = sh, Rot = 0, Label = Name(Shield) });
            M.ReserveIn(plan, Shield, sh, 0);
            if (!PlanRing(s, hp, cmin, cmax, out why))
            {
                return null;
            }
            foreach (string key in new[] { "lab1", "lab2" })
            {
                if (!M.FindSite(s, plan, Lab, 6, 30, out GridCell g))
                {
                    why = "核心配电范围里放不下仿真实验室";
                    return null;
                }
                plan.Buildings.Add(new P.Placed { Key = key, Type = Lab, Pivot = g, Rot = 0, Label = Name(Lab) });
                M.ReserveIn(plan, Lab, g, 0);
            }
            // 电：精炼塔 / 废液池（不在 plan.Buildings 里）+ 之后才放的陷阱发射器、燃迹炮塔；护盾按重启档的 1.5 倍算。
            int extra = P.BuildingDemand(P.Tower) + P.BuildingDemand(P.Pond) + P.BuildingDemand(Trap) + P.BuildingDemand(Turret) + P.BuildingDemand(Shield) / 2;
            if (!P.PlanGenerators(s, plan, extra, out why))
            {
                return null;
            }
            return hp;
        }

        /// <summary>在 <paramref name="want"/> 附近（切比雪夫环，至多 <paramref name="maxR"/> 格）找能放 <paramref name="type"/> 的位置：只差研究也算、核心配电范围里、不压已有规划，四周留 <paramref name="clear"/> 格空。</summary>
        internal static bool FindNear(CampaignState s, P.FactoryPlan plan, string type, Vector2 want, int maxR, int clear, out GridCell pivot)
        {
            pivot = default;
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var c0 = new GridCell(Mathf.RoundToInt(want.x), Mathf.RoundToInt(want.y));
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
                        GridCell g = P.Add(c0, dx, dy);
                        if (P.NearestDist(type, g, 0, core) > coreR - 0.5f || !PlaceableLater(s, type, g, avoid))
                        {
                            continue;
                        }
                        List<GridCell> fp = P.Footprint(type, g, 0);
                        bool ok = true;
                        for (int k = 0; k < fp.Count && ok; k++)
                        {
                            for (int ex = -clear; ex <= clear && ok; ex++)
                            {
                                for (int ey = -clear; ey <= clear && ok; ey++)
                                {
                                    GridCell n = P.Add(fp[k], ex, ey);
                                    ok = fp.Contains(n) || !avoid.Contains(P.Key(n));
                                }
                            }
                        }
                        if (ok)
                        {
                            pivot = g;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 核心外一圈屏障：在外框外 <see cref="RingMinMargin"/>～<see cref="RingMaxMargin"/> 格里挑能放的格最多的那一圈；能放 = 只差研究也算、不压规划 / 路线 / 已有建筑、敌方能走
        /// （地形本来就挡住的格不放）。按“上边 → 右边 → 下边 → 左边”的顺序把连续能放的格切成直线段（一段 = 一次按住左键拖），放不下的格就是缺口（机器从缺口进出）。
        /// 一个缺口都没有时在离仓库最近的那一格留一个缺口。
        /// </summary>
        private static bool PlanRing(CampaignState s, HomePlan hp, GridCell cmin, GridCell cmax, out string why)
        {
            why = null;
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(hp.Plan.Used);
            List<GridCell> bestRing = null;
            List<bool> bestOk = null;
            int bestCount = -1;
            int bestMargin = 0;
            for (int m = RingMinMargin; m <= RingMaxMargin; m++)
            {
                List<GridCell> ring = RingCells(cmin, cmax, m);
                var ok = new List<bool>(ring.Count);
                int n = 0;
                foreach (GridCell c in ring)
                {
                    bool can = !avoid.Contains(P.Key(c)) && PlaceableLater(s, Wall, c, null) && GameLogic.Campaign.Nav.NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile);
                    ok.Add(can);
                    n += can ? 1 : 0;
                }
                if (n > bestCount)
                {
                    bestCount = n;
                    bestRing = ring;
                    bestOk = ok;
                    bestMargin = m;
                }
            }
            if (bestRing == null || bestCount < 12)
            {
                why = $"核心外一圈能放屏障的格太少（{bestCount} 格）";
                return false;
            }
            int gaps = bestOk.Count(x => !x);
            if (gaps == 0)
            {
                BuildingRecord wh = FgjM3Common.Home(HomeValleyLayout.BuildingTypeWarehouse);
                Vector2 to = wh != null ? wh.Position : CoreCenter(s);
                int idx = Enumerable.Range(0, bestRing.Count).OrderBy(i => Vector2.SqrMagnitude(new Vector2(bestRing[i].X, bestRing[i].Y) - to)).First();
                bestOk[idx] = false;
                gaps = 1;
            }
            hp.RingMargin = bestMargin;
            hp.RingGaps = gaps;
            // 切段：沿圈走，方向变了（转角）或遇到放不下的格就断开。
            int start = -1;
            for (int i = 0; i <= bestRing.Count; i++)
            {
                bool ok = i < bestRing.Count && bestOk[i];
                bool turn = i > 0 && i < bestRing.Count && start >= 0 && i - start >= 1 && !Collinear(bestRing[start], bestRing[i - 1], bestRing[i]);
                if (start >= 0 && (!ok || turn))
                {
                    hp.WallRuns.Add((bestRing[start], bestRing[i - 1]));
                    start = -1;
                }
                if (ok && start < 0)
                {
                    start = i;
                }
            }
            hp.WallCells = bestCount - (bestOk.Count(x => !x) - (bestRing.Count - bestCount));
            foreach ((GridCell a, GridCell b) in hp.WallRuns)
            {
                foreach (GridCell c in Line(a, b))
                {
                    hp.Plan.Used.Add(P.Key(c));
                }
            }
            hp.WallCells = hp.WallRuns.Sum(w => Line(w.A, w.B).Count);
            return true;
        }

        private static bool Collinear(GridCell a, GridCell b, GridCell c) => (a.X == b.X && b.X == c.X) || (a.Y == b.Y && b.Y == c.Y);

        /// <summary>外框外 m 格的那一圈，按上边（左 → 右）、右边（上 → 下）、下边（右 → 左）、左边（下 → 上）的顺序，每个转角只出现一次。</summary>
        internal static List<GridCell> RingCells(GridCell cmin, GridCell cmax, int m)
        {
            int x0 = cmin.X - m, x1 = cmax.X + m, y0 = cmin.Y - m, y1 = cmax.Y + m;
            var list = new List<GridCell>();
            for (int x = x0; x <= x1; x++)
            {
                list.Add(new GridCell(x, y1));
            }
            for (int y = y1 - 1; y >= y0; y--)
            {
                list.Add(new GridCell(x1, y));
            }
            for (int x = x1 - 1; x >= x0; x--)
            {
                list.Add(new GridCell(x, y0));
            }
            for (int y = y0 + 1; y < y1; y++)
            {
                list.Add(new GridCell(x0, y));
            }
            return list;
        }

        /// <summary>两格之间的直线（同一行或同一列）。</summary>
        internal static List<GridCell> Line(GridCell a, GridCell b)
        {
            var list = new List<GridCell>();
            int dx = Math.Sign(b.X - a.X), dy = Math.Sign(b.Y - a.Y);
            int n = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            for (int i = 0; i <= n; i++)
            {
                list.Add(new GridCell(a.X + dx * i, a.Y + dy * i));
            }
            return list;
        }

        internal static void SaveHome(JourneyContext c, HomePlan hp)
        {
            M.SavePlan(c, hp.Plan);
            P.RefineryPlan r = hp.Refinery;
            P.SetP(c, "ref.tower", r.Tower);
            P.SetP(c, "ref.pond", r.Pond);
            P.SetP(c, "ref.pump", r.Pump);
            P.SetP(c, "ref.fuel", r.FuelPipe);
            P.SetP(c, "ref.tank", r.Tank);
            P.SetP(c, "ref.acid", r.AcidPipe);
            P.SavePath(c, "path.crude", r.Crude);
            c.Set("ref.poles", FgjM3Common.EncodeCells(r.Poles));
            c.SetInt("walls.n", hp.WallRuns.Count);
            for (int i = 0; i < hp.WallRuns.Count; i++)
            {
                P.SetP(c, "walls.a" + i, hp.WallRuns[i].A);
                P.SetP(c, "walls.b" + i, hp.WallRuns[i].B);
            }
            c.SetInt("walls.cells", hp.WallCells);
            c.SetInt("walls.margin", hp.RingMargin);
        }

        internal static List<(GridCell A, GridCell B)> LoadWalls(JourneyContext c)
        {
            var list = new List<(GridCell, GridCell)>();
            int n = c.GetInt("walls.n");
            for (int i = 0; i < n; i++)
            {
                list.Add((P.P(c, "walls.a" + i), P.P(c, "walls.b" + i)));
            }
            return list;
        }

        internal static string DescribeHome(HomePlan hp, GridCell core) =>
            $"{M.Describe(hp.Plan, core)}；精炼塔 {Cell(hp.Refinery.Tower)}、油井泵 {Cell(hp.Refinery.Pump)}、原油管线 {hp.Refinery.Crude.Count} 格、储罐 {Cell(hp.Refinery.Tank)}、废液池 {Cell(hp.Refinery.Pond)}" +
            $"{(hp.NeedPoles ? $"、T2 电塔 {hp.Refinery.Poles.Count} 座" : string.Empty)}；屏障圈（外框外 {hp.RingMargin} 格）{hp.WallRuns.Count} 段 {hp.WallCells} 格、缺口 {hp.RingGaps} 处";

        // ── 反向旅程的家园规划（FGJ-M6R：把核心完全围死 + 一座闸门、两座炮塔、陷阱发射器与抽水线）──────────────

        internal sealed class ReversePlan
        {
            public P.FactoryPlan Plan;
            public readonly List<(GridCell A, GridCell B)> WallRuns = new List<(GridCell, GridCell)>();
            public GridCell GateAt;
            public int WallCells;
            public int RingMargin;
            public GridCell TrapAt;
            public GridCell Pump;
            public readonly List<GridCell> Water = new List<GridCell>();
        }

        /// <summary>
        /// FGJ-M6R 规划：核心外 4～8 格挑一圈“敌方能走的格都能放屏障”、圈里还放得下两座炮塔与撑得起全家园用电的发电机 2 的（完全围死，地形 / 建筑本来就挡住的格不用放），
        /// 离仓库最近的那一格留作闸门位（先空着让机器进出，“路径失败”那一段再放闸门），其余切成直线段拖；离核心最近的一条抽水线（泵 + 3 格管线，按种子地形现找）末端旁边放陷阱发射器
        /// （装拖尾固件、抽水供给）；回收站。
        /// </summary>
        internal static ReversePlan PlanReverse(CampaignState s, out string why)
        {
            why = null;
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            HashSet<long> avoid = P.BaseAvoid(s);
            BuildingRecord wh = FgjM3Common.Home(HomeValleyLayout.BuildingTypeWarehouse);
            Vector2 cc = CoreCenter(s);
            Vector2 to = wh != null ? wh.Position : cc;
            float half = Mathf.Max(cmax.X - cmin.X, cmax.Y - cmin.Y) * 0.5f;
            ReversePlan rp = null;
            string lastWhy = "核心外 4～8 格找不到一圈能完全围死的位置（总有敌方能走、却放不下屏障的格）";
            for (int m = 4; m <= 8 && rp == null; m++)
            {
                List<GridCell> ring = RingCells(cmin, cmax, m);
                bool closable = true;
                var open = new List<GridCell>();
                foreach (GridCell c in ring)
                {
                    if (!GameLogic.Campaign.Nav.NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile))
                    {
                        continue;
                    }
                    if (avoid.Contains(P.Key(c)) || !PlaceableLater(s, Wall, c, null))
                    {
                        closable = false;
                        break;
                    }
                    open.Add(c);
                }
                if (!closable || open.Count < 8)
                {
                    continue;
                }
                var cand = new ReversePlan { Plan = new P.FactoryPlan { Origin = HomeGridService.CorePivot(s) }, RingMargin = m };
                // 闸门放在一条边的中段（不在转角），离仓库最近。
                cand.GateAt = open.Where(c => !IsCorner(c, cmin, cmax, m)).OrderBy(c => Vector2.SqrMagnitude(new Vector2(c.X, c.Y) - to)).First();
                int start = -1;
                for (int i = 0; i <= ring.Count; i++)
                {
                    bool ok = i < ring.Count && ring[i] != cand.GateAt && open.Contains(ring[i]);
                    bool turn = i > 0 && i < ring.Count && start >= 0 && !Collinear(ring[start], ring[i - 1], ring[i]);
                    if (start >= 0 && (!ok || turn))
                    {
                        cand.WallRuns.Add((ring[start], ring[i - 1]));
                        start = -1;
                    }
                    if (ok && start < 0)
                    {
                        start = i;
                    }
                }
                cand.WallCells = cand.WallRuns.Sum(w => Line(w.A, w.B).Count);
                foreach (GridCell c in ring)
                {
                    cand.Plan.Used.Add(P.Key(c));
                    // 圈内外各一格也留空：机器施工要站人，敌人也要有路走到墙前。
                    foreach (int d in new[] { 0, 1, 2, 3 })
                    {
                        cand.Plan.Used.Add(P.Key(P.Step(c, d)));
                    }
                }
                // 两座炮塔放在圈里、核心两侧（对角），四周不贴别的建筑。
                bool turrets = true;
                for (int i = 0; i < 2 && turrets; i++)
                {
                    float ang = (i * 180f + 45f) * Mathf.Deg2Rad;
                    Vector2 want = cc + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (half + m * 0.5f);
                    if (!FindInside(s, cand.Plan, Turret, want, cmin, cmax, m, out GridCell g))
                    {
                        turrets = false;
                        lastWhy = $"外框外 {m} 格那一圈里放不下第 {i + 1} 座炮塔";
                        break;
                    }
                    string key = "t" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    cand.Plan.Buildings.Add(new P.Placed { Key = key, Type = Turret, Pivot = g, Rot = 0, Label = Name(Turret) + " " + (i + 1) });
                    M.ReserveIn(cand.Plan, Turret, g, 0);
                }
                // 发电机 2 也放在圈里：开局那台发电机在圈外，突袭里破坏型敌人先拆发电设施——圈里的发电机要单独撑得起全家园的用电（核心自带的那点不算多）。
                if (turrets)
                {
                    string g2 = HomeValleyLayout.BuildingTypeGenerator2;
                    int per = Math.Max(1, P.BuildingSupply(g2));
                    int demand = P.BuildingDemand(Turret) * 2 + P.BuildingDemand(Trap) + P.BuildingDemand("recycler");
                    foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
                    {
                        if (b != null && b.RegionId == HomeValleyLayout.RegionId)
                        {
                            demand += P.BuildingDemand(b.BuildingTypeId);
                        }
                    }
                    int need = Math.Max(1, (int)Math.Ceiling((demand * 1.05 - P.BuildingSupply(HomeValleyLayout.BuildingTypeCore)) / per));
                    for (int k = 0; k < need; k++)
                    {
                        float ang = (k * 180f + 135f) * Mathf.Deg2Rad;
                        Vector2 want = cc + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (half + m * 0.5f);
                        if (!FindInside(s, cand.Plan, g2, want, cmin, cmax, m, out GridCell gg))
                        {
                            turrets = false;
                            lastWhy = $"外框外 {m} 格那一圈里放不下第 {k + 1}/{need} 台发电机 2（全家园用电 {demand}）";
                            break;
                        }
                        cand.Plan.Generators.Add(gg);
                        M.ReserveIn(cand.Plan, g2, gg, 0);
                    }
                }
                if (turrets)
                {
                    rp = cand;
                }
            }
            if (rp == null)
            {
                why = lastWhy;
                return null;
            }
            P.FactoryPlan plan = rp.Plan;
            if (P.PlanRecycler(s, plan, "仓库输入口", out _, out why) == null)
            {
                why = "回收站：" + why;
                return null;
            }            // 抽水线：泵 + 3 格管线（不要储罐），末端管线旁边一格放陷阱发射器；都在核心配电范围里。
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> used = P.BaseAvoid(s);
            used.UnionWith(plan.Used);
            bool water = false;
            for (int r = 6; r <= 24 && !water; r++)
            {
                if (!FgjM3Common.FindWaterLine(s, core, r, r, used, out GridCell pump, out int dir))
                {
                    continue;
                }
                GridCell[] line = FgjM3Common.WaterLine(pump, dir);
                GridCell last = line[3];
                foreach (int side in new[] { (dir + 1) & 3, (dir + 3) & 3 })
                {
                    GridCell trap = P.Step(last, side);
                    if (!used.Contains(P.Key(trap)) && PlaceableLater(s, Trap, trap, used) && P.NearestDist(Trap, trap, 0, core) <= coreR - 0.5f)
                    {
                        rp.Pump = pump;
                        rp.Water.AddRange(new[] { line[1], line[2], line[3] });
                        rp.TrapAt = trap;
                        water = true;
                        break;
                    }
                }
                if (!water)
                {
                    foreach (GridCell c in line)
                    {
                        used.Add(P.Key(c));
                    }
                    r--; // 同一圈换一处水源再找
                    if (used.Count > 200000)
                    {
                        break;
                    }
                }
            }
            if (!water)
            {
                why = "核心配电范围里找不到能放抽水线 + 陷阱发射器的水源";
                return null;
            }
            foreach (GridCell c in new[] { rp.Pump, rp.TrapAt }.Concat(rp.Water))
            {
                plan.Used.Add(P.Key(c));
            }
            return rp;
        }

        /// <summary>在屏障圈（外框外 <paramref name="m"/> 格）里面、<paramref name="want"/> 附近找能放 <paramref name="type"/> 的位置：占地整个在圈里、只差研究也算、核心配电范围里、不压已有规划。</summary>
        private static bool FindInside(CampaignState s, P.FactoryPlan plan, string type, Vector2 want, GridCell cmin, GridCell cmax, int m, out GridCell pivot)
        {
            pivot = default;
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            var c0 = new GridCell(Mathf.RoundToInt(want.x), Mathf.RoundToInt(want.y));
            int maxR = (cmax.X - cmin.X) + (cmax.Y - cmin.Y) + 2 * m;
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
                        GridCell g = P.Add(c0, dx, dy);
                        List<GridCell> fp = P.Footprint(type, g, 0);
                        if (!fp.All(c => c.X > cmin.X - m && c.X < cmax.X + m && c.Y > cmin.Y - m && c.Y < cmax.Y + m))
                        {
                            continue;
                        }
                        if (P.NearestDist(type, g, 0, core) > coreR - 0.5f || !PlaceableLater(s, type, g, avoid))
                        {
                            continue;
                        }
                        pivot = g;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsCorner(GridCell c, GridCell cmin, GridCell cmax, int m) =>
            (c.X == cmin.X - m || c.X == cmax.X + m) && (c.Y == cmin.Y - m || c.Y == cmax.Y + m);

        // ── 火墙规划（第一次突袭之后：按玩家在预警条上看到的来袭方向）──────────────────────────

        internal sealed class FirewallPlan
        {
            public GridCell TrapAt;
            public int TrapRot;
            public GridCell TurretAt;
            public GridCell PipeEnd;
            public List<GridCell> Pipe;
            /// <summary>FG6-E2E-01 修复轮：改装的那座已建炮塔（旅程键 t1～t6）；空 = 新放一座。</summary>
            public string TurretKey;
            /// <summary>油膜带离抵达点最近的距离（米）。</summary>
            public float Cover;
            /// <summary>管线从屏障底下穿过去：圈外一段（储罐 → 圈外地下口外侧）、一对地下口（圈外口朝里 = <see cref="UnderDir"/>，圈里口朝外）、圈里一段（圈里地下口里侧 → 终点）。</summary>
            public bool Underground;
            public List<GridCell> PipeOut;
            public List<GridCell> PipeIn;
            public GridCell UnderOut;
            public GridCell UnderIn;
            public int UnderDir;
        }

        /// <summary>
        /// FG6-E2E-01 修复轮（复审 P1“火墙始终没接敌”）：火墙按真实来路放——第一次突袭预警条上的抵达点（<paramref name="arrival"/>，世界坐标）就是敌人展开攻城的地方。
        /// 挑离抵达点最近的那座已建轻型炮塔（在屏障圈里，射程 18 米罩得住圈外抵达点）改装成燃迹炮塔（炮塔面板蓝图下拉换到“燃迹 + 接入口”的新版本，不另造炮塔、不另花废料），
        /// 它旁边一格放陷阱发射器、朝抵达点铺油膜带（“一条线” 8 米，越过屏障铺到圈外敌人站的地方），两者之间那一格是燃油管线的终点。
        /// 燃油管线从储罐边接出来（不碰原油 / 酸液两张管网）：能直接铺过去就直接铺；被屏障圈隔开（缺口被传送带占着）时用一对地下管线口从屏障底下穿进圈
        /// （圈外一口朝里、圈里一口朝外，跨度以内自动配对）。按废料（管线格 + 地下口）+ 油膜带离抵达点的距离挑最省的。失败返回 null 与原因。
        /// </summary>
        internal static FirewallPlan PlanFirewallNear(CampaignState s, Vector2 arrival, IList<(string Key, GridCell Pivot)> turrets, GridCell tank, GridCell fuelPipe,
            IEnumerable<GridCell> otherNets, out string why, IEnumerable<GridCell> extraAvoid = null)
        {
            why = null;
            HashSet<long> avoid = P.BaseAvoid(s);
            foreach (GridCell c in extraAvoid ?? Array.Empty<GridCell>())
            {
                avoid.Add(P.Key(c)); // 规划里还没铺下的传送带等（[M6 出口] B 段只登记建筑与屏障）
            }
            var keepOut = new HashSet<long>(avoid);
            foreach (GridCell c in otherNets ?? Array.Empty<GridCell>())
            {
                keepOut.Add(P.Key(c));
                for (int k = 0; k < 4; k++)
                {
                    keepOut.Add(P.Key(P.Step(c, k)));
                }
            }
            bool PipeCell(GridCell c, PipePieceKind kind) =>
                !keepOut.Contains(P.Key(c)) && HomeGridService.ValidatePipeCell(s, c, kind).Ok && PipeNetworkService.SourceFluidAt(s, c) == 0;
            var starts = new List<GridCell>();
            foreach (GridCell n in new[] { tank, fuelPipe })
            {
                for (int k = 0; k < 4; k++)
                {
                    GridCell st = P.Step(n, k);
                    if (st != tank && st != fuelPipe && PipeCell(st, PipePieceKind.Pipe))
                    {
                        starts.Add(st);
                    }
                }
            }
            if (starts.Count == 0)
            {
                why = "储罐旁边没有能接出燃油管线的空格";
                return null;
            }
            int pipeCost = Math.Max(1, PlanEntries.CostOf(P.PipeT1));
            int underCost = Math.Max(1, PlanEntries.CostOf(UndergroundPipeTool));
            int span = PipeNetworkService.UndergroundSpan(0);
            Vector2 cc = CoreCenter(s);
            int Cheb(GridCell c) => Mathf.Max(Mathf.Abs(Mathf.RoundToInt(c.X - cc.x)), Mathf.Abs(Mathf.RoundToInt(c.Y - cc.y)));
            // 屏障底下穿过去的候选：屏障格 W 两侧（同一行 / 列）圈外 O、圈里 I 放一对地下管线口，O 外侧、I 里侧各一格接地面管线。
            var walls = new List<GridCell>();
            var fpScratch = new List<GridCell>(4);
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && DefenseCatalog.IsDraggable(b.BuildingTypeId))
                {
                    HomeGridService.FootprintOf(b, fpScratch);
                    walls.AddRange(fpScratch);
                }
            }
            var crossings = new List<(GridCell O, GridCell I, int Dir, GridCell GO, GridCell GI)>();
            foreach (GridCell w in walls)
            {
                for (int k = 0; k < 4; k++)
                {
                    GridCell o = P.Step(w, k);
                    GridCell i = P.Step(w, (k + 2) & 3);
                    if (!(Cheb(o) > Cheb(w) && Cheb(i) < Cheb(w)))
                    {
                        continue;
                    }
                    GridCell go = P.Step(o, k);
                    GridCell gi = P.Step(i, (k + 2) & 3);
                    if (span >= 1 && PipeCell(o, PipePieceKind.Underground) && PipeCell(i, PipePieceKind.Underground) && PipeCell(go, PipePieceKind.Pipe) && PipeCell(gi, PipePieceKind.Pipe))
                    {
                        crossings.Add((o, i, (k + 2) & 3, go, gi)); // O 朝里（朝 I）、I 朝外（朝 O）
                    }
                }
            }
            var r1Cache = new Dictionary<int, List<GridCell>>();
            List<GridCell> Route(GridCell goal, ISet<long> ko)
            {
                List<GridCell> bestR = null;
                foreach (GridCell st in starts)
                {
                    List<GridCell> r = st == goal ? new List<GridCell> { st } : P.PlanRoute(s, st, goal, 0, -1, ko, 16, P.PipeOk(s));
                    if (r != null && (bestR == null || r.Count < bestR.Count))
                    {
                        bestR = r;
                    }
                }
                return bestR;
            }
            float len = DefenseCatalog.TrapLineLength;
            FirewallPlan best = null;
            float bestScore = float.MaxValue;
            var tried = new List<string>();
            foreach ((string key, GridCell pivot) in turrets.OrderBy(t => Vector2.Distance(TurretCenter(t.Pivot), arrival)).Take(3))
            {
                List<GridCell> fp = P.Footprint(Turret, pivot, 0);
                var fpSet = new HashSet<long>(fp.Select(P.Key));
                int spots = 0;
                int found = 0;
                foreach (GridCell f in fp)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        GridCell end = P.Step(f, k);
                        if (fpSet.Contains(P.Key(end)) || !PipeCell(end, PipePieceKind.Pipe))
                        {
                            continue;
                        }
                        for (int j = 0; j < 4; j++)
                        {
                            GridCell trap = P.Step(end, j);
                            if (fpSet.Contains(P.Key(trap)) || !P.Placeable(s, Trap, trap, 0, avoid))
                            {
                                continue;
                            }
                            // 朝向：四个方向里油膜带（陷阱中心起 len 米）离抵达点最近的那个。
                            int rot = -1;
                            float cover = float.MaxValue;
                            for (int r = 0; r < 4; r++)
                            {
                                var a = new Vector2(trap.X, trap.Y);
                                Vector2 b = a + new Vector2(DX[r], DY[r]) * len;
                                float dd = SegmentDistance(arrival, a, b);
                                if (dd < cover)
                                {
                                    cover = dd;
                                    rot = r;
                                }
                            }
                            if (cover > 3.5f)
                            {
                                continue;
                            }
                            spots++;
                            var ko = new HashSet<long>(keepOut) { P.Key(trap) };
                            List<GridCell> direct = Route(end, ko);
                            if (direct != null)
                            {
                                found++;
                                float score = direct.Count * pipeCost + cover * 4f;
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    best = new FirewallPlan { TrapAt = trap, TrapRot = rot * 90, TurretAt = pivot, TurretKey = key, PipeEnd = end, Pipe = direct, Cover = cover };
                                }
                                continue;
                            }
                            // 直接铺不过去：试离储罐 + 离这座炮塔近的几处屏障底下穿过去。
                            var order = Enumerable.Range(0, crossings.Count)
                                .OrderBy(x => Mathf.Abs(crossings[x].O.X - tank.X) + Mathf.Abs(crossings[x].O.Y - tank.Y) + Mathf.Abs(crossings[x].I.X - end.X) + Mathf.Abs(crossings[x].I.Y - end.Y))
                                .Take(24);
                            foreach (int x in order)
                            {
                                (GridCell o, GridCell i, int dir, GridCell go, GridCell gi) = crossings[x];
                                if (i == trap || go == trap || gi == trap)
                                {
                                    continue;
                                }
                                if (!r1Cache.TryGetValue(x, out List<GridCell> r1))
                                {
                                    var ko1 = new HashSet<long>(keepOut) { P.Key(o), P.Key(i) };
                                    r1 = Route(go, ko1);
                                    r1Cache[x] = r1;
                                }
                                if (r1 == null || r1.Contains(trap) || r1.Contains(end))
                                {
                                    continue;
                                }
                                var ko2 = new HashSet<long>(keepOut) { P.Key(trap), P.Key(o), P.Key(i) };
                                foreach (GridCell c1 in r1)
                                {
                                    ko2.Add(P.Key(c1));
                                }
                                List<GridCell> r2 = gi == end ? new List<GridCell> { gi } : P.PlanRoute(s, gi, end, 0, -1, ko2, 16, P.PipeOk(s));
                                if (r2 == null)
                                {
                                    continue;
                                }
                                found++;
                                float score = (r1.Count + r2.Count) * pipeCost + 2 * underCost + cover * 4f;
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    best = new FirewallPlan
                                    {
                                        TrapAt = trap, TrapRot = rot * 90, TurretAt = pivot, TurretKey = key, PipeEnd = end, Cover = cover,
                                        Pipe = r1.Concat(new[] { o, i }).Concat(r2).ToList(), PipeOut = r1, PipeIn = r2, UnderOut = o, UnderIn = i, UnderDir = dir, Underground = true,
                                    };
                                }
                            }
                        }
                    }
                }
                tried.Add($"{key} {Cell(pivot)}（离抵达点 {Vector2.Distance(TurretCenter(pivot), arrival):F1} 米）：旁边能放陷阱且油膜带罩得住抵达点的位置 {spots} 处、管线接得过去的方案 {found} 个");
            }
            if (best == null)
            {
                why = $"离抵达点最近的三座炮塔都改不成火墙（屏障底下能穿管线的位置 {crossings.Count} 处）：" + string.Join("；", tried);
            }
            return best;
        }

        /// <summary>
        /// FG6-E2E-01 修复轮（承接 DEBT-FG5E2E01-05 进旅程）：电路合成台放在屏障圈外、离突袭抵达点（<paramref name="arrival"/>）最近、在归还核心配电范围里的空地上——
        /// 突袭部队在抵达点展开，够不着圈里的核心，按职能偏好先打最近的其它建筑（FGR-DEF-030）。离闸门至少 3 格（不挡机器进出）。失败返回 null 与原因。
        /// </summary>
        internal static GridCell? PlanSynthNear(CampaignState s, string synthType, Vector2 arrival, int ringMargin, GridCell gate, out string why)
        {
            why = null;
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = P.BaseAvoid(s);
            var at = new GridCell(Mathf.RoundToInt(arrival.x), Mathf.RoundToInt(arrival.y));
            GridCell? best = null;
            float bestD = float.MaxValue;
            int tried = 0;
            for (int dy = -10; dy <= 10; dy++)
            {
                for (int dx = -10; dx <= 10; dx++)
                {
                    var pivot = new GridCell(at.X + dx, at.Y + dy);
                    List<GridCell> fp = P.Footprint(synthType, pivot, 0);
                    if (fp.Any(c => c.X >= cmin.X - ringMargin - 1 && c.X <= cmax.X + ringMargin + 1 && c.Y >= cmin.Y - ringMargin - 1 && c.Y <= cmax.Y + ringMargin + 1))
                    {
                        continue; // 要在屏障圈外（圈外再空一格）
                    }
                    if (fp.Any(c => Mathf.Abs(c.X - gate.X) + Mathf.Abs(c.Y - gate.Y) <= 3))
                    {
                        continue;
                    }
                    var center = new Vector2((float)fp.Average(c => c.X), (float)fp.Average(c => c.Y));
                    if (Vector2.Distance(center, new Vector2(core.X, core.Y)) > coreR - 2f)
                    {
                        continue;
                    }
                    tried++;
                    float d = Vector2.Distance(center, arrival);
                    if (d < bestD && P.Placeable(s, synthType, pivot, 0, avoid))
                    {
                        bestD = d;
                        best = pivot;
                    }
                }
            }
            if (best == null)
            {
                why = $"抵达点 {arrival} 附近（屏障圈外、核心配电范围里）放不下{Name(synthType)}（看过 {tried} 个位置）";
            }
            return best;
        }

        /// <summary>地下管线口 T1（建造栏“物流”分类；一对口跨度以内自动配对，从屏障底下穿过去）。</summary>
        internal const string UndergroundPipeTool = "pipe_underground_t1";

        private static Vector2 TurretCenter(GridCell pivot) => new Vector2(pivot.X + 0.5f, pivot.Y + 0.5f);

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float l2 = ab.sqrMagnitude;
            float t = l2 <= 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return Vector2.Distance(p, a + ab * t);
        }

        private static readonly int[] DX = { 0, 1, 0, -1 };
        private static readonly int[] DY = { 1, 0, -1, 0 };

        // ── 精炼链动作（建造栏点条目、指着格子单击、按住左键分段拖）────────────────────────────

        internal static List<string> RefineryActions(JourneyContext c)
        {
            var a = new List<string>();
            foreach (GridCell pole in FgjM3Common.DecodeCells(c.Get("ref.poles")))
            {
                a.Add(P.ActB(new P.Placed { Type = P.PoleT2, Pivot = pole, Rot = 0, Label = "T2 电塔 " + Cell(pole) }));
            }
            a.Add(P.ActB(new P.Placed { Type = P.Tower, Pivot = P.P(c, "ref.tower"), Rot = 0, Label = Name(P.Tower) }));
            a.Add(P.ActB(new P.Placed { Type = P.Pond, Pivot = P.P(c, "ref.pond"), Rot = 0, Label = Name(P.Pond) }));
            a.Add(P.ActC(P.PipeT1, P.P(c, "ref.fuel"), "燃油口外一格管线"));
            a.Add(P.ActC(P.TankTool, P.P(c, "ref.tank"), "储罐（存燃油）"));
            a.Add(P.ActC(P.PipeT1, P.P(c, "ref.acid"), "酸液口 → 废液池西口（一格管线）"));
            a.Add(P.ActC(P.PumpTool, P.P(c, "ref.pump"), "油井上的泵"));
            a.Add(P.ActQ(P.PipeT1, "path.crude", "原油管线 → 精炼塔南口"));
            return a;
        }

        // ── 屏障：按住左键拖一段 ─────────────────────────────────────────────────────────

        internal static bool WallPlannedOrBuilt(CampaignState s, GridCell cell)
        {
            BuildingRecord b = HomeGridService.BuildingAt(s, cell);
            return b != null && DefenseCatalog.IsDraggable(b.BuildingTypeId);
        }

        /// <summary>
        /// 屏障圈逐段铺：建造栏点“防御”分类里的屏障 T2，每段按住左键从一端拖到另一端（单格段单击），松开后核对这一段每格都有屏障虚影；全部铺完 = Done。
        /// 拖拽“全有或全无”：某段被拒（某格放不下）时状态行写原因，如实失败。
        /// </summary>
        internal static StepOutcome TickWalls(JourneyContext c)
        {
            CampaignState s = St;
            List<(GridCell A, GridCell B)> runs = LoadWalls(c);
            int i = c.GetInt("walls.i");
            while (i < runs.Count && Line(runs[i].A, runs[i].B).All(x => WallPlannedOrBuilt(s, x)))
            {
                i++;
                c.SetInt("walls.i", i);
                c.Log($"屏障第 {i}/{runs.Count} 段 ✓");
            }
            if (i >= runs.Count)
            {
                return StepOutcome.Done($"屏障圈 {runs.Count} 段 {c.GetInt("walls.cells")} 格全部放下虚影（按住左键拖拽，每段一步撤销）；状态行“{HomeValleyBuildMode.Current?.StatusText?.Replace("\n", " / ")}”；废料 {s.Scrap}");
            }
            if (!FgjM3Common.BuildOpen)
            {
                if (NowMs() - c.GetLong("walls.openAt") > 1200)
                {
                    c.SetLong("walls.openAt", NowMs());
                    FgjM3Common.PressBuild(true);
                }
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.EntrySelected(Wall))
            {
                if (NowMs() - c.GetLong("walls.pickAt") < 400)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong("walls.pickAt", NowMs());
                if (!FgjM3Common.PickEntry(Wall, out string why, out bool scrolling) && !scrolling)
                {
                    return StepOutcome.Retry("建造栏选不中屏障 T2：" + why);
                }
                return StepOutcome.Wait;
            }
            (GridCell a, GridCell b) = runs[i];
            string k = "walls.r" + i.ToString(CultureInfo.InvariantCulture) + "." + c.Attempt.ToString(CultureInfo.InvariantCulture);
            if (c.GetInt(k) == 0)
            {
                bool sent = a == b ? FgjM3Common.TryClick(FgjM3Common.Ground(a)) : FgjM3Common.TryDrag(FgjM3Common.Ground(a), FgjM3Common.Ground(b));
                if (sent)
                {
                    c.SetInt(k, 1);
                    c.SetLong(k + ".at", NowMs());
                    c.Set("walls.drag", BuildModeHudUIToolkit.Instance?.DragInfoText?.Replace("\n", " ") ?? string.Empty);
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(k + ".at") < 2500)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Retry($"第 {i + 1} 段屏障 {Cell(a)}→{Cell(b)} 松开后没有铺满虚影（状态行“{HomeValleyBuildMode.Current?.StatusText}”）");
        }

        /// <summary>施工诊断：家园机器在哪、在办什么、站的格能不能走，没完成的工单（等待原因）。施工等太久时写进报告。</summary>
        internal static string WorkDiag(CampaignState s)
        {
            IEnumerable<string> ms = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Select(m =>
            {
                Vector2 at = GameRoot.HomeValley?.LivePosition(m.LogicId) ?? Vector2.zero;
                GridCell mc = GameLogic.Campaign.Nav.NavService.CellOf(at.x, at.y);
                WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId);
                bool pass = GameLogic.Campaign.Nav.NavService.PassableNow(mc.X, mc.Y, BinGames.Sim.Nav.NavConst.ClassPlayer);
                string nav = string.Empty;
                CombatSite site = HomeSite;
                if (site != null && site.TryGetMachineUnit(m.LogicId, out int unit) && site.Kernel.TryGetNavState(unit, out BinGames.Sim.Combat.CombatNavState ns, out BinGames.Sim.Nav.NavFailReason nf))
                {
                    nav = $" 寻路 {ns}{(ns == BinGames.Sim.Combat.CombatNavState.Failed ? "/" + nf : string.Empty)} 剩 {site.RemainingRoute(unit):F0}";
                }
                return $"{FgjM1Journey.Label(m.LogicId)}@{at.x:F1},{at.y:F1}{(pass ? string.Empty : "（站在挡路格）")} {o?.Kind}/{o?.State}{nav}";
            });
            IEnumerable<string> orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled)
                .GroupBy(o => $"{o.Kind}/{o.State}{(string.IsNullOrEmpty(o.FailureReason) ? string.Empty : "/" + o.FailureReason)}").Select(g => $"{g.Key}×{g.Count()}").Take(8);
            return $"机器 [{string.Join("；", ms)}]；工单 [{string.Join("；", orders)}]；废料 {s.Scrap}";
        }

        // ── 等施工 ────────────────────────────────────────────────────────────────────

        internal static bool Ready(CampaignState s, string type, GridCell pivot) => P.BuildingReady(s, type, pivot, out _);

        /// <summary>屏障圈：已建成的格数 / 规划格数。</summary>
        internal static int WallsBuilt(JourneyContext c, out int total)
        {
            CampaignState s = St;
            total = 0;
            int built = 0;
            foreach ((GridCell a, GridCell b) in LoadWalls(c))
            {
                foreach (GridCell x in Line(a, b))
                {
                    total++;
                    BuildingRecord w = HomeGridService.BuildingAt(s, x);
                    if (w != null && DefenseCatalog.IsDraggable(w.BuildingTypeId) && !HomeValleyController.IsPlannedGhost(w) && w.ConstructionState == BuildingConstructionState.Operational)
                    {
                        built++;
                    }
                }
            }
            return built;
        }

        // ── 研发树：依次排进队列 ───────────────────────────────────────────────────────

        /// <summary>研发树开着时把 <paramref name="nodes"/> 依次排进队列（每个节点：分支筛选 → 拖画布 → 左键点节点，<see cref="M.TickQueueNode"/>）；已完成 / 已在队列的跳过。</summary>
        internal static StepOutcome TickQueueAll(JourneyContext c, string[] nodes)
        {
            CampaignState s = St;
            int i = c.GetInt(FgjM3Common.SK(c, "qi"));
            while (i < nodes.Length && (ResearchService.IsCompleted(s, nodes[i]) || ResearchService.QueueIndex(s, nodes[i]) >= 0))
            {
                if (c.GetInt(FgjM3Common.SK(c, "qlog" + i)) == 0)
                {
                    c.SetInt(FgjM3Common.SK(c, "qlog" + i), 1);
                    c.Log($"“{M.NodeName(nodes[i])}”（{M.ResearchCatalogCost(nodes[i])} 研究点）{(ResearchService.IsCompleted(s, nodes[i]) ? "已研究" : $"在研究队列第 {ResearchService.QueueIndex(s, nodes[i]) + 1} 位")}");
                }
                i++;
                c.SetInt(FgjM3Common.SK(c, "qi"), i);
            }
            if (i >= nodes.Length)
            {
                return StepOutcome.Done($"研究队列：{string.Join("、", (s.Research?.Queue ?? Array.Empty<string>()).Select(M.NodeName))}；状态“{M.Tree()?.StatusText}”");
            }
            // 每个节点用一组独立的子键（TickQueueNode 用 SK 记筛选 / 点击）：借用 Attempt 不变、换节点时清掉它的标记。
            string marker = FgjM3Common.SK(c, "node" + i);
            if (c.GetInt(marker) == 0)
            {
                c.SetInt(marker, 1);
                foreach (string sub in new[] { "filter", "node", "drags", "fAt", "nAt" })
                {
                    c.SetInt(FgjM3Common.SK(c, sub), 0);
                    c.SetLong(FgjM3Common.SK(c, sub), 0);
                }
            }
            StepOutcome o = M.TickQueueNode(c, nodes[i]);
            if (o.Status == JourneyStepStatus.Done)
            {
                c.Log(o.Message);
                return StepOutcome.Wait;
            }
            return o;
        }

        /// <summary>等 <paramref name="nodes"/> 全部研究完成（每 30 秒记一行进度：研究点、队列、实验室状态、技术数据）。</summary>
        internal static StepOutcome TickResearched(JourneyContext c, params string[] nodes)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            List<string> left = nodes.Where(n => !ResearchService.IsCompleted(s, n)).ToList();
            if (left.Count == 0)
            {
                return StepOutcome.Done($"研究完成：{string.Join("、", nodes.Select(M.NodeName))}（{c.StepElapsed:F0} 秒，3 倍速）；技术数据 {s.TechData}；研究队列 [{string.Join("、", (s.Research?.Queue ?? Array.Empty<string>()).Select(M.NodeName))}]");
            }
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"等研究：还差 [{string.Join("、", left.Select(M.NodeName))}]；队列 [{string.Join("、", (s.Research?.Queue ?? Array.Empty<string>()).Select(M.NodeName))}]；" +
                      $"工作中的实验室 {ResearchService.BuiltLabCount(s)} 座；技术数据 {s.TechData}；{P.PowerLine(s)}；暴露 {s.SignalExposure:F1}");
            }
            return StepOutcome.Wait;
        }

        // ── 远征准备面板：关闭 / 打开信号塔广播（降暴露的正式手段，FG01 / FG16）──────────────────

        /// <summary>
        /// 远征准备面板里的“关闭信号塔主动广播”勾选框点到 <paramref name="off"/>，再点“关闭”。面板没开时先左键点信号塔（同一步里平移镜头后补点）。
        /// 完成时记下暴露值与广播状态。
        /// </summary>
        internal static StepOutcome TickBroadcast(JourneyContext c, bool off)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            HomeValleyController hv = GameRoot.HomeValley;
            if (s.SignalTowerBroadcastOff == off && FgjM3Common.Done(c, "tog"))
            {
                if (hv.IsExpeditionPrepPanelOpen)
                {
                    if (!FgjM3Common.Once(c, "close", () => JourneyInput.ClickUitk(PrepHost, "CloseButton")))
                    {
                        return M.UiRetry("远征准备面板的“关闭”点不到：");
                    }
                    return FgjM3Common.SinceMs(c, "close") < 2500 ? StepOutcome.Wait : StepOutcome.Retry("远征准备面板关不掉：" + JourneyInput.LastUiFailure);
                }
                return StepOutcome.Done($"远征准备面板勾选框“关闭信号塔主动广播”{(off ? "勾上" : "取消")}（暴露 {s.SignalExposure:F1}、带宽 {s.SignalBandwidth:F0}），点“关闭”");
            }
            if (!hv.IsExpeditionPrepPanelOpen)
            {
                if (!JourneyInput.WorldClickSettled(c.StepElapsed, 0.8))
                {
                    return StepOutcome.Wait;
                }
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "open")) > 2500)
                {
                    c.SetLong(FgjM3Common.SK(c, "open"), NowMs());
                    FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, true);
                }
                return c.StepElapsed > 20 ? StepOutcome.Retry("点信号塔后远征准备面板没有打开（" + JourneyInput.LastWorldClickTrace + "）") : StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            Toggle t = JourneyInput.FindUitk<Toggle>(PrepHost, "TowerBroadcastOffToggle");
            if (t == null)
            {
                return StepOutcome.Fail("远征准备面板里没有“关闭信号塔主动广播”勾选框");
            }
            if (s.SignalTowerBroadcastOff != off)
            {
                if (!FgjM3Common.Once(c, "tog", () => JourneyInput.ClickElement(t)))
                {
                    return M.UiRetry("点不到“关闭信号塔主动广播”：");
                }
                return FgjM3Common.SinceMs(c, "tog") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点了勾选框后广播状态仍是 {s.SignalTowerBroadcastOff}：{JourneyInput.LastUiFailure}");
            }
            FgjM3Common.Mark(c, "tog");
            return StepOutcome.Wait;
        }

        // ── 通知弹出条 ─────────────────────────────────────────────────────────────────

        /// <summary>通知弹出条里找 <paramref name="typeId"/> 那一条（UI Toolkit 弹出条，userData = 通知条目），派发真实指针事件点它。返回 null = 还没出现。</summary>
        internal static bool? ClickToast(string typeId, out NotificationEntry entry, Func<NotificationEntry, bool> extra = null)
        {
            entry = null;
            VisualElement list = JourneyInput.FindUitk<VisualElement>(NotifyHost, "ToastList");
            if (list == null)
            {
                return null;
            }
            foreach (VisualElement t in list.Children())
            {
                if (t.userData is NotificationEntry e && e.Type?.Id == typeId && (extra == null || extra(e)) && JourneyInput.IsClickable(t))
                {
                    entry = e;
                    return JourneyInput.ClickElement(t);
                }
            }
            return null;
        }

        // ── 突袭读数 ───────────────────────────────────────────────────────────────────

        /// <summary>还没结束的计划（筹备 / 排定 / 预警 / 出发），按序号。</summary>
        internal static List<RaidPlanRecord> LivePlans(CampaignState s) =>
            RaidDirectorService.Plans(s).Where(p => p != null && p.State < RaidDirectorService.StateEnded).OrderBy(p => p.Serial).ToList();

        internal static string PlanLine(RaidPlanRecord p) =>
            p == null ? "（没有）" : $"{p.PlanId}（{p.Trigger}，{p.Level} 级{(p.FirstRaid ? "·第一次" : string.Empty)}，{RaidDirectorService.FactionText(p)}，{p.UnitTotal} 台，状态 {p.State}，" +
                                   $"预警第 {p.WarnTick} 步、抵达第 {p.ArrivalTick} 步，来袭方向 {RaidDirectorService.DirectionText(p)}）";

        internal static string ResultLine(RaidResultRecord r) =>
            r == null ? "（没有结算）" : $"第 {r.Serial} 份 {r.PlanId}：{RaidResultService.OutcomeText(r)}；展开 {r.Unfolded}、击毁 {r.Killed}、离场 {r.Exited}；" +
                                       $"损失 建筑 {r.LostBuildings} / 炮塔 {r.LostTurrets} / 防御 {r.LostDefenses} / 机器 {r.LostMachines}（{string.Join("、", r.Losses.Select(x => x.Name))}）；" +
                                       $"贡献 {string.Join("、", r.Contrib.Select(x => x.Name + "×" + x.Kills))}；反应 {string.Join("、", r.Reactions.Select(x => x.ReactionId + "×" + x.Count))}";

        internal static BuildingRecord Core(CampaignState s) => s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);

        internal static CombatSite HomeSite => GameRoot.HomeValley?.Combat;

        /// <summary>家园战斗内核里活着的攻城单位（突袭者）位置；没有返回空表。</summary>
        internal static List<(int Id, Vector2 Pos)> Raiders()
        {
            var list = new List<(int, Vector2)>();
            CombatSite site = HomeSite;
            if (site == null)
            {
                return list;
            }
            var ids = new List<int>();
            foreach (TransitGroupRecord g in St.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>())
            {
                if (g == null)
                {
                    continue;
                }
                ids.Clear();
                site.SiegeRaiderIds(SiegeService.KeyOf(g), ids);
                foreach (int id in ids)
                {
                    if (site.Kernel.TryGetUnit(id, out BinGames.Sim.Combat.CombatUnitView v) && v.Alive)
                    {
                        list.Add((id, new Vector2((float)v.Position.x, (float)v.Position.y)));
                    }
                }
            }
            return list;
        }
    }
}
