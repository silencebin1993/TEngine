using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG5-E2E-01：M5 两条旅程（FGJ-M5 / FGJ-M5R）共用的部分。
    /// ① 研发建筑的位置（B25：按种子地形现找，不写死坐标）——仿真实验室、黑匣子陈列馆、电路合成台、靶场、固件刻录台（与仓库输出口之间一条传送带）、监听站（研究完再放，先留位置）、
    ///    压在废墟上的回收站（M5 一次要造六座研发建筑，开局废料不够，像玩家一样先造回收站拆废墟出废料）、按需的发电机 2；全部在归还核心配电范围里（不用要研究的 T2 电塔）。
    /// ② 面板操作：研发树（筛选分支、点节点加入队列）、解析台（送芯片破解）、信号核（装芯片）、电路合成台（按线索选固件、模拟、正式熔合）、刻录台（刻录目标）、
    ///    靶场（接入投影、结束、对比）、情报（行、在地图上查看、点通知）、统计面板“研发”页。
    /// ③ 进度夹具（只在登记的 Apply*Fixture 方法里，[M5 出口] A2 扫描源码守护；每处一条 DEBT，见 ADR-QA-022）。
    /// 全部玩家动作走 <see cref="JourneyInput"/> 的正式输入通道；规划与读数只读游戏状态。
    /// </summary>
    internal static class FgjM5Common
    {
        internal const string Lab = ResearchCatalog.LabTypeId;
        internal const string Gallery = BlackBoxService.TypeId;
        internal const string Synth = "circuit_synth";
        internal const string Range = "test_range";
        internal const string Burner = "firmware_burner";
        internal const string Post = "listening_post";
        internal const string KeyItem = "listening_array_core";
        internal const string Substrate = "chip_substrate";
        internal const string NodeGather = "industry.eff_gather_1";
        internal const string NodePost = "signal.listening_post";
        internal const string NodeLabT3 = "industry.lab_t3";
        internal const string ResearchHost = "[ResearchTreeHost]";
        internal const string FusionHost = "[FusionPanelHost]";
        internal const string RangeHost = "[TestRangeHost]";
        internal const string IntelHost = "[IntelPanelHost]";
        internal const string StatsHost = "[StatsPanelHost]";
        internal const string SignalHost = "[SignalCoreHost]";
        internal const string BlackBoxHost = "[BlackBoxPanelHost]";
        internal const string OverlayHost = "[UiKitOverlayHost]";

        /// <summary>夹具给的芯片基板件数：正式熔合 2 + 刻录台量产一枚 3（FG05 初值；代替 M4 产线的产出）。</summary>
        internal const int SubstrateFixture = 5;

        internal static CampaignState St => CampaignSession.Current;

        /// <summary>UI 点击没成：暂时的（悬停提示正在收起等）就等下一帧，否则算一次重试。</summary>
        internal static StepOutcome UiRetry(string what) => JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry(what + JourneyInput.LastUiFailure);

        /// <summary>熔合任务记的是芯片实例编号（PartA / PartB）；按固件种类数芯片时先换成固件 ID（实例熔掉以后就查不到了，入队时记下）。</summary>
        internal static string DefOf(string partId) => PrimitiveInventory.Find(St, partId)?.CardDefId ?? partId;

        internal static string Cell(GridCell c) => FgjM3Common.Cell(c);

        internal static string Name(string type) => HomeGridService.DisplayName(type);

        internal static string FwName(string id) => FirmwareKinds.DisplayName(id) ?? id;

        internal static long NowMs() => FgjM2Common.NowMs();

        // ── 旧旅程的“已研究”进度夹具（DEBT-FG5RND01-05）────────────────────────────────

        /// <summary>
        /// 进度夹具（DEBT-FG5RND01-05）：FGJ-M3 / M3R / M4 / M4R 写于研发树开放之前，测的是传送带 / 管线 / 电网 / 生产本身。
        /// 开局把“研发树开放前就能建”的节点（<see cref="ResearchService.MigratedNodes"/>，与旧档迁移同一口径，含要关键材料的超控阵列——FGJ-M4R 要造它）记为已研究，其余全部走正式入口；
        /// 研究本身（实验室、队列、缺关键材料 / 缺前置被拒、断电暂停、存读档）由 FGJ-M5 / M5R 从正式入口走。
        /// </summary>
        internal static void ApplyLegacyResearchFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null)
            {
                return;
            }
            string[] ids = ResearchService.MigratedNodes().Select(n => n.Id).ToArray();
            c.SetInt("legacyNodes", ids.Length);
            ResearchService.CompleteForTests(s, ids);
        }

        internal static StepOutcome TickLegacyResearchFixture(JourneyContext c)
        {
            CampaignState s = St;
            List<ResearchNodeDef> nodes = ResearchService.MigratedNodes();
            int done = nodes.Count(n => ResearchService.IsCompleted(s, n.Id));
            return nodes.Count > 0 && done == nodes.Count
                ? StepOutcome.Done($"进度夹具：研发树开放前就能建的 {done} 个节点记为已研究（与旧档迁移同一口径）；研究本身由 FGJ-M5 / M5R 走正式入口")
                : StepOutcome.Fail($"夹具没有生效：{done} / {nodes.Count} 个节点已研究");
        }

        // ── ① 规划 ────────────────────────────────────────────────────────────────────

        /// <summary>占地 + 端口外侧一格记为已用（新建筑、新路线都绕开）。</summary>
        internal static void ReserveIn(P.FactoryPlan plan, string type, GridCell pivot, int rot)
        {
            foreach (GridCell c in P.Footprint(type, pivot, rot))
            {
                plan.Used.Add(P.Key(c));
            }
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsFor(type, pivot, rot, ports);
            foreach (PortPlacement p in ports)
            {
                plan.Used.Add(P.Key(P.Step(p.Cell, (int)p.Dir)));
            }
        }

        /// <summary>
        /// 在归还核心配电范围里找一块能放 <paramref name="type"/> 的地（放置校验、不压已有建筑 / 规划、四周留一圈空，端口外侧也空着）。
        /// 按离核心由近到远（切比雪夫环）找，最远 <paramref name="maxR"/> 格。
        /// </summary>
        internal static bool FindSite(CampaignState s, P.FactoryPlan plan, string type, int minR, int maxR, out GridCell pivot)
        {
            pivot = default;
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            for (int r = minR; r <= maxR; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell g = P.Add(core, dx, dy);
                        if (P.NearestDist(type, g, 0, core) > coreR - 0.5f || !PlaceableLater(s, type, g, avoid))
                        {
                            continue;
                        }
                        List<GridCell> fp = P.Footprint(type, g, 0);
                        bool clear = true;
                        foreach (GridCell c in fp)
                        {
                            for (int d = 0; d < 4 && clear; d++)
                            {
                                GridCell n = P.Step(c, d);
                                clear &= fp.Contains(n) || !avoid.Contains(P.Key(n));
                            }
                        }
                        var ports = new List<PortPlacement>(4);
                        HomeGridService.PortsFor(type, g, 0, ports);
                        foreach (PortPlacement p in ports)
                        {
                            GridCell o = P.Step(p.Cell, (int)p.Dir);
                            clear &= !avoid.Contains(P.Key(o)) && HomeGridService.ValidateBeltCell(s, o).Ok;
                        }
                        if (!clear)
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

        /// <summary>
        /// 规划用的放置校验：与 <see cref="P.Placeable"/> 相同，只是“还没研究（锁定）”不算放不下——监听站要等研究完成才放，规划时先按地形与占地找好位置。
        /// 真正放置时走建造模式的正式校验（锁定照样拒绝）。
        /// </summary>
        internal static bool PlaceableLater(CampaignState s, string type, GridCell g, ISet<long> avoid)
        {
            GridPlacementResult r = HomeGridService.ValidatePlacement(s, type, g, 0, checkCost: false);
            if (!r.Ok && r.Reasons.Any(x => x.Code != GridBlockReason.Locked))
            {
                return false;
            }
            return avoid == null || P.Footprint(type, g, 0).All(c => !avoid.Contains(P.Key(c)));
        }

        /// <summary>
        /// 固件刻录台放在仓库输出口附近，一条 T1 传送带从仓库输出口送芯片基板到刻录台输入口（路线最短的那个位置）；刻录台也要在核心配电范围里。
        /// </summary>
        internal static bool PlanBurnerLine(CampaignState s, P.FactoryPlan plan, out P.Placed burner, out P.Route route, out string why)
        {
            burner = null;
            route = null;
            why = null;
            if (!FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, true, out GridCell start, out int outward, out string whOut))
            {
                why = "仓库没有输出口";
                return false;
            }
            GridCell core = HomeGridService.CorePivot(s);
            float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            HashSet<long> avoid = P.BaseAvoid(s);
            avoid.UnionWith(plan.Used);
            avoid.Remove(P.Key(start));
            List<GridCell> best = null;
            GridCell bestPivot = default;
            int bestGoalDir = 0;
            for (int r = 3; r <= 12; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        GridCell g = P.Add(start, dx, dy);
                        if (P.NearestDist(Burner, g, 0, core) > coreR - 0.5f || !P.Placeable(s, Burner, g, 0, avoid)
                            || !P.PortOutside(Burner, g, 0, Burner + ".in0", out GridCell inCell, out int face) || avoid.Contains(P.Key(inCell)))
                        {
                            continue;
                        }
                        var avoidPlus = new HashSet<long>(avoid);
                        foreach (GridCell c in P.Footprint(Burner, g, 0))
                        {
                            avoidPlus.Add(P.Key(c));
                        }
                        List<GridCell> path = P.PlanRoute(s, start, inCell, P.Opp(face), P.Opp(outward), avoidPlus, 6, P.BeltOk(s));
                        if (path != null && (best == null || path.Count < best.Count))
                        {
                            best = path;
                            bestPivot = g;
                            bestGoalDir = P.Opp(face);
                        }
                    }
                }
                if (best != null && r >= 5)
                {
                    break;
                }
            }
            if (best == null)
            {
                why = "仓库输出口附近找不到能放固件刻录台、又能铺带过去的位置";
                return false;
            }
            burner = new P.Placed { Key = "burner", Type = Burner, Pivot = bestPivot, Rot = 0, Label = Name(Burner) };
            plan.Buildings.Add(burner);
            ReserveIn(plan, Burner, bestPivot, 0);
            route = new P.Route { Key = "r_burner", Path = best, GoalDir = bestGoalDir, Label = "仓库输出口 → 固件刻录台" };
            plan.Routes.Add(route);
            foreach (GridCell c in best)
            {
                plan.Used.Add(P.Key(c));
            }
            plan.Note = whOut;
            return true;
        }

        internal sealed class SiteSpec
        {
            public string Key;
            public string Type;
            public int MinR;
        }

        /// <summary>
        /// 整套研发建筑规划：回收站（<paramref name="withRecycler"/>）→ 刻录台与供料带（<paramref name="withBurner"/>）→ 其余研发建筑 → 发电机 2（耗电不够时）。
        /// <paramref name="extraDemand"/> = 之后才放的建筑（监听站）的耗电，一起算进发电量。失败返回 null 与原因。
        /// </summary>
        internal static P.FactoryPlan PlanSites(CampaignState s, bool withRecycler, bool withBurner, IEnumerable<SiteSpec> specs, int extraDemand, out string why, double powerMargin = 1.05)
        {
            why = null;
            var plan = new P.FactoryPlan { Origin = HomeGridService.CorePivot(s) };
            if (withRecycler && P.PlanRecycler(s, plan, "仓库输入口", out _, out why) == null)
            {
                why = "回收站：" + why;
                return null;
            }
            if (withBurner && !PlanBurnerLine(s, plan, out _, out _, out why))
            {
                return null;
            }
            foreach (SiteSpec sp in specs)
            {
                if (!FindSite(s, plan, sp.Type, sp.MinR, 26, out GridCell g))
                {
                    why = $"核心配电范围里放不下{Name(sp.Type)}";
                    return null;
                }
                plan.Buildings.Add(new P.Placed { Key = sp.Key, Type = sp.Type, Pivot = g, Rot = 0, Label = Name(sp.Type) });
                ReserveIn(plan, sp.Type, g, 0);
            }
            if (!P.PlanGenerators(s, plan, extraDemand, out why, powerMargin))
            {
                return null;
            }
            return plan;
        }

        /// <summary>规划存进旅程变量：每座建筑的位置与类型、每条路线的格子与终点方向（跨域重载 / 断点续跑都能取回）。</summary>
        internal static void SavePlan(JourneyContext c, P.FactoryPlan plan)
        {
            foreach (P.Placed b in plan.Buildings)
            {
                P.Remember(c, b);
            }
            foreach (P.Route r in plan.Routes)
            {
                P.SavePath(c, "path." + r.Key, r.Path);
                c.SetInt("path." + r.Key + ".dir", r.GoalDir);
            }
            c.Set("plan.gens", FgjM3Common.EncodeCells(plan.Generators));
        }

        internal static string Describe(P.FactoryPlan plan, GridCell core) =>
            string.Join("；", plan.Buildings.Select(b => $"{b.Label} {Cell(b.Pivot)}（相对核心 {b.Pivot.X - core.X},{b.Pivot.Y - core.Y}）")) +
            (plan.Generators.Count > 0 ? $"；发电机 2 ×{plan.Generators.Count}" : string.Empty) +
            $"；路线 {string.Join("、", plan.Routes.Select(r => r.Label + " " + r.Path.Count + " 格"))}；{plan.PowerNote}";

        // ── 等施工 ────────────────────────────────────────────────────────────────────

        /// <summary>这些建筑（旅程变量里的键）全部建成、接上电，这些路线全部建成。每 30 秒记一行进度（施工队列、废料、电网）。</summary>
        internal static StepOutcome TickBuilt(JourneyContext c, string[] keys, string[] routes, bool gens, string what)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var notBuilt = new List<string>();
            foreach (string k in keys)
            {
                if (!P.BuildingReady(s, c.Get(k + ".type"), P.P(c, k), out _))
                {
                    notBuilt.Add(k);
                }
            }
            if (gens)
            {
                foreach (GridCell g in FgjM3Common.DecodeCells(c.Get("plan.gens")))
                {
                    if (!P.BuildingReady(s, HomeValleyLayout.BuildingTypeGenerator2, g, out _))
                    {
                        notBuilt.Add("发电机2" + Cell(g));
                    }
                }
            }
            int cells = 0;
            foreach (string r in routes)
            {
                cells += P.LoadPath(c, "path." + r).Count(p => !P.RouteBuilt(s, new List<GridCell> { p }, true));
            }
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                var q = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, q);
                BuildingRecord rec = P.Bld(c, "recycler");
                ProductionService.Producer rp = rec != null ? P.Prod(rec) : null;
                c.Log($"{what}：还没建成 [{string.Join(",", notBuilt)}]、带还差 {cells} 格；废料 {s.Scrap}；施工队列 {q.Count} 项" +
                      (rp != null ? $"；回收站已拆出 {rp.Rec.RuinRecovered} 废料" : string.Empty) + "；" + P.PowerLine(s));
            }
            if (notBuilt.Count > 0 || cells > 0)
            {
                return StepOutcome.Wait;
            }
            var unpowered = new List<string>();
            foreach (string k in keys)
            {
                BuildingRecord b = P.Bld(c, k);
                if (b != null && b.PowerState != BuildingPowerState.Powered && P.BuildingDemand(b.BuildingTypeId) > 0)
                {
                    unpowered.Add($"{Name(b.BuildingTypeId)}：{b.PowerState}");
                }
            }
            if (unpowered.Count > 0)
            {
                // 刚建成的这一步电网还没重算：等一会儿；一直没电就照实失败（发电规划没算够）。
                string k0 = FgjM3Common.SK(c, "unp");
                if (c.GetLong(k0) == 0)
                {
                    c.SetLong(k0, NowMs());
                }
                return NowMs() - c.GetLong(k0) > 60000 ? StepOutcome.Fail("建成了但没电：" + string.Join("；", unpowered) + "；" + P.PowerLine(s)) : StepOutcome.Wait;
            }
            return StepOutcome.Done($"{what}全部建成、接上电（{c.StepElapsed:F0} 秒，3 倍速）：{string.Join("、", keys.Select(k => Name(c.Get(k + ".type")) + " " + Cell(P.P(c, k))))}；废料 {s.Scrap}；{P.PowerLine(s)}");
        }

        // ── ③ 进度夹具（每处登记 DEBT；只在这些方法里）──────────────────────────────────

        /// <summary>
        /// 关键材料夹具（DEBT-FG5RND05-09 已登记、本 Story 写明）：监听阵列核只由主线首领“寂听主脑”给出（FG9-FAC-01，FG-M9）；
        /// 走与入库同一入口（HomeInventory.Add）放进核心保管库，之后的研究、放置、施工、破译全走正式流程。
        /// </summary>
        internal static void ApplyKeyFixture(JourneyContext c)
        {
            c.SetInt("key0", HomeInventory.Stock(St, KeyItem));
            HomeInventory.Add(St, KeyItem, 1, clampToSpace: false);
        }

        internal static StepOutcome TickKeyFixture(JourneyContext c) =>
            HomeInventory.Stock(St, KeyItem) == c.GetInt("key0") + 1
                ? StepOutcome.Done($"进度夹具：核心保管库里放进 1 件{ItemCatalog.NameOf(KeyItem)}（首领掉落在 FG9-FAC-01；DEBT-FG5RND05-09）")
                : StepOutcome.Fail($"夹具没有生效：{ItemCatalog.NameOf(KeyItem)} {HomeInventory.Stock(St, KeyItem)}");

        /// <summary>
        /// 芯片基板夹具（DEBT-FG5E2E01-02）：芯片基板要“采集 → 精炼 → 电子组装台”一整条 M4 产线（FGJ-M4 已从正式入口验证），M5 出口旅程不重造这条产线；
        /// 走与入库同一入口放进仓库 <see cref="SubstrateFixture"/> 件，之后的正式熔合、刻录台量产（经仓库输出口 → 传送带 → 刻录台输入口）全走正式流程。
        /// </summary>
        internal static void ApplySubstrateFixture(JourneyContext c)
        {
            c.SetInt("sub0", HomeInventory.Stock(St, Substrate));
            HomeInventory.Add(St, Substrate, SubstrateFixture, clampToSpace: false);
        }

        internal static StepOutcome TickSubstrateFixture(JourneyContext c) =>
            HomeInventory.Stock(St, Substrate) >= c.GetInt("sub0") + SubstrateFixture
                ? StepOutcome.Done($"进度夹具：仓库里放进 {SubstrateFixture} 件{ItemCatalog.NameOf(Substrate)}（等同 M4 产线的产出，产线由 FGJ-M4 验证；DEBT-FG5E2E01-02）")
                : StepOutcome.Fail($"夹具没有生效：{ItemCatalog.NameOf(Substrate)} {HomeInventory.Stock(St, Substrate)}");

        // ── 研发树 ────────────────────────────────────────────────────────────────────

        internal static ResearchTreePanelUIToolkit Tree()
        {
            ResearchTreePanelUIToolkit p = ResearchTreePanelUIToolkit.Instance;
            p?.Refresh();
            return p;
        }

        internal static StepOutcome TickTreeOpen(JourneyContext c, bool want)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            ResearchTreePanelUIToolkit p = Tree();
            if (ResearchTreePanelUIToolkit.IsOpen != want)
            {
                return StepOutcome.Retry($"按研发树键后研发树{(want ? "没打开" : "没关")}");
            }
            return StepOutcome.Done(want ? $"按研发树键（默认 {InputDisplay.ForAction(GameActionId.OpenResearch)}）打开研发树：{p?.NodeViewCount} 个节点；状态“{p?.StatusText}”" : "再按研发树键关闭研发树");
        }

        /// <summary>
        /// 研发树里把 <paramref name="nodeId"/> 加入队列：先点它所在分支的筛选按钮（整棵树很大，节点可能在视口外），再左键点节点。
        /// <paramref name="expectRefusal"/> 非空 = 预期被拒（反向旅程）：面板消息写原因、不进队列。
        /// </summary>
        /// <summary>点节点那一刻的诊断：节点按钮的屏幕框、视口框、面板在中心点拾取到的元素链。</summary>
        private static string NodeProbe(ResearchTreePanelUIToolkit p, string nodeId)
        {
            Button b = p.NodeButton(nodeId);
            if (b == null || b.panel == null)
            {
                return "节点按钮不在面板上";
            }
            Rect r = b.worldBound;
            VisualElement top = b.panel.Pick(r.center);
            var chain = new List<string>();
            for (VisualElement e = top; e != null && chain.Count < 6; e = e.parent)
            {
                chain.Add(string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name);
            }
            VisualElement vp = JourneyInput.FindUitk<VisualElement>(ResearchHost, "ResearchViewport");
            return $"节点框 {r}、视口框 {vp?.worldBound}、拾取链 {string.Join(" < ", chain)}";
        }

        internal static StepOutcome TickQueueNode(JourneyContext c, string nodeId, string expectRefusal = null)
        {
            ResearchTreePanelUIToolkit p = Tree();
            if (!ResearchTreePanelUIToolkit.IsOpen || p == null)
            {
                return StepOutcome.Fail("研发树没开");
            }
            CampaignState s = St;
            if (expectRefusal == null && ResearchService.QueueIndex(s, nodeId) >= 0)
            {
                return StepOutcome.Done($"“{NodeName(nodeId)}”在研究队列第 {ResearchService.QueueIndex(s, nodeId) + 1} 位（“{p.NodeStateText(nodeId)}”）；状态“{p.StatusText}”");
            }
            string branch = nodeId.Split('.')[0];
            if (!FgjM3Common.Done(c, "filter"))
            {
                int idx = -1;
                for (int i = 0; i < p.FilterButtonCount; i++)
                {
                    if (p.FilterId(i) == branch)
                    {
                        idx = i;
                    }
                }
                if (idx < 0 || !JourneyInput.ClickElement(p.FilterButton(idx)))
                {
                    return UiRetry($"点不到“{branch}”分支筛选按钮：");
                }
                FgjM3Common.Mark(c, "filter");
                c.SetLong(FgjM3Common.SK(c, "fAt"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "fAt")) < 500)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "node"))
            {
                // 节点不完整地在画布视口里：像玩家一样按住画布空白处拖过来（研发树的平移方式），下一帧再点。
                Button nb0 = p.NodeButton(nodeId);
                VisualElement vp = JourneyInput.FindUitk<VisualElement>(ResearchHost, "ResearchViewport");
                if (nb0 != null && vp != null && p.NodeVisible(nodeId))
                {
                    JourneyInput.SyncLayout(vp);
                    Rect vr = vp.worldBound;
                    Rect nr = nb0.worldBound;
                    bool inside = nr.xMin >= vr.xMin + 4f && nr.xMax <= vr.xMax - 4f && nr.yMin >= vr.yMin + 4f && nr.yMax <= vr.yMax - 4f;
                    if (!inside)
                    {
                        if (c.GetInt(FgjM3Common.SK(c, "drags")) >= 6)
                        {
                            return StepOutcome.Retry($"拖了 6 次画布，节点“{NodeName(nodeId)}”还不在视口里（节点 {nr}、视口 {vr}）");
                        }
                        if (!JourneyInput.DragUitk(vp, vr.center - nr.center, e => e == vp || e.name == "ResearchCanvas" || e.name == "ResearchLinks", out string dragWhy))
                        {
                            return StepOutcome.Retry("拖不动研发树画布：" + dragWhy);
                        }
                        c.SetInt(FgjM3Common.SK(c, "drags"), c.GetInt(FgjM3Common.SK(c, "drags")) + 1);
                        c.SetLong(FgjM3Common.SK(c, "fAt"), NowMs());
                        return StepOutcome.Wait;
                    }
                }
                if (!p.NodeVisible(nodeId) || !JourneyInput.ClickElement(p.NodeButton(nodeId)))
                {
                    return UiRetry($"筛到“{branch}”后点不到节点“{NodeName(nodeId)}”：");
                }
                c.Set(FgjM3Common.SK(c, "probe"), NodeProbe(p, nodeId) + (JourneyInput.LastClickNote.Length > 0 ? "；" + JourneyInput.LastClickNote : string.Empty));
                FgjM3Common.Mark(c, "node");
                c.SetLong(FgjM3Common.SK(c, "nAt"), NowMs());
                c.SetInt(FgjM3Common.SK(c, "denied0"), Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied));
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "nAt")) < 600)
            {
                return StepOutcome.Wait;
            }
            int q = ResearchService.QueueIndex(s, nodeId);
            if (expectRefusal != null)
            {
                string msg = p.MessageText;
                return q < 0 && msg.Contains(expectRefusal)
                    ? StepOutcome.Done($"左键点“{NodeName(nodeId)}”被拒、不进队列：面板写“{msg}”；节点状态“{p.NodeStateText(nodeId)}”")
                    : StepOutcome.Fail($"点“{NodeName(nodeId)}”应被拒（含“{expectRefusal}”），实际队列位置 {q}、消息“{msg}”；详情节点 {p.DetailNodeId}；点击时 {c.Get(FgjM3Common.SK(c, "probe"))}");
            }
            return q >= 0
                ? StepOutcome.Done($"左键点“{NodeName(nodeId)}”（{ResearchCatalogCost(nodeId)} 研究点）：加入研究队列第 {q + 1} 位；面板“{p.MessageText}”；状态“{p.StatusText}”")
                : StepOutcome.Retry($"点了“{NodeName(nodeId)}”没进队列：“{p.MessageText}”；详情节点 {p.DetailNodeId}；点击时 {c.Get(FgjM3Common.SK(c, "probe"))}");
        }

        internal static string NodeName(string id) => ResearchCatalog.TryGet(id, out ResearchNodeDef n) ? n.Name : id;

        internal static int ResearchCatalogCost(string id) => ResearchCatalog.TryGet(id, out ResearchNodeDef n) ? n.Cost : -1;

        // ── 通用：建造模式里点建筑 → 通用面板 → 面板上的按钮 ─────────────────────────────

        /// <summary>建造模式里点 <paramref name="key"/> 那座建筑打开通用面板，再点面板上的 <paramref name="button"/>（先滚进可见区），等 <paramref name="opened"/> 为真。</summary>
        internal static StepOutcome TickSubPanel(JourneyContext c, string key, string button, Func<bool> opened, string what)
        {
            if (opened())
            {
                if (ProductionPanelUIToolkit.IsOpen && !FgjM3Common.Done(c, "pp"))
                {
                    // 子页面打开后通用面板被隐藏（UiEscapeStack 子页）；不需要额外动作。
                    FgjM3Common.Mark(c, "pp");
                }
                return c.StepElapsed < 0.6 ? StepOutcome.Wait : StepOutcome.Done($"左键点{Name(c.Get(key + ".type"))} {Cell(P.P(c, key))}：通用面板上点“{JourneyInput.FindUitk<Button>(P.PanelHost, button)?.text}”——{what}");
            }
            BuildingRecord b = P.Bld(c, key);
            if (b == null)
            {
                return StepOutcome.Fail($"{Cell(P.P(c, key))} 没有{Name(c.Get(key + ".type"))}");
            }
            if (!(ProductionPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.BuildingId == b.BuildingId))
            {
                return P.TickOpenPanel(c, b.BuildingTypeId, P.P(c, key), "sp").Status == JourneyStepStatus.Retry
                    ? StepOutcome.Retry($"点{Name(b.BuildingTypeId)}后通用面板没打开")
                    : StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "btn", () => FgjM3Common.ClickPanelBodyButton(P.PanelHost, "ProductionPanelBody", button)))
            {
                P.LogStuck(c, "btn", () => $"“{button}”还点不到：{JourneyInput.LastUiFailure}");
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "btn") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点了“{button}”后{what}没打开：{JourneyInput.LastUiFailure}");
        }

        /// <summary>按 Esc 关掉最上层（子页面关掉后通用面板回来了就再点它的“关闭”），直到 <paramref name="closed"/> 且通用面板也关了。</summary>
        internal static StepOutcome TickCloseAll(JourneyContext c, Func<bool> closed, string what)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (closed() && !ProductionPanelUIToolkit.IsOpen)
            {
                return c.StepElapsed < 0.4 ? StepOutcome.Wait : StepOutcome.Done($"{what}关闭（通用面板也关了）");
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "esc")) < 700)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "esc"), NowMs());
            if (closed() && ProductionPanelUIToolkit.IsOpen)
            {
                JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose");
            }
            else
            {
                JourneyInput.PressAction(GameActionId.Cancel);
            }
            return c.StepElapsed > 8 ? StepOutcome.Retry($"{what}关不掉（{JourneyInput.LastUiFailure}）") : StepOutcome.Wait;
        }

        // ── 信号核：装芯片 ─────────────────────────────────────────────────────────────

        /// <summary>信号核面板里先点第 <paramref name="slot"/> 号槽，再点基元仓里那枚芯片（<paramref name="partId"/>），再点“装入”。</summary>
        internal static StepOutcome TickEquipChip(JourneyContext c, int slot, Func<string> partId, string what)
        {
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            string part = partId();
            if (!SignalCoreHudUIToolkit.IsOpen || hud == null || string.IsNullOrEmpty(part))
            {
                return StepOutcome.Fail($"信号核面板没开或没有要装的芯片（{part}）");
            }
            if (SignalCoreService.SlotChip(St, slot)?.PartId == part)
            {
                return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done($"{what}：第 {slot + 1} 号槽“{hud.SlotText(slot)}”（{SignalCoreService.SummaryText(St)}）");
            }
            if (!FgjM3Common.Done(c, "slot"))
            {
                if (!JourneyInput.ClickElement(hud.SlotButton(slot)))
                {
                    return UiRetry("点不到槽位：");
                }
                FgjM3Common.Mark(c, "slot");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "bag"))
            {
                int idx = -1;
                for (int i = 0; i < hud.VisibleBagItemCount; i++)
                {
                    if (hud.BagItemPartId(i) == part)
                    {
                        idx = i;
                    }
                }
                if (idx < 0)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"基元仓列表里没有这枚芯片（{part}）");
                }
                bool? clicked = P.ClickInView(hud.BagButton(idx));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return UiRetry("点不到基元仓里的芯片：");
                }
                FgjM3Common.Mark(c, "bag");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "eq", () => JourneyInput.ClickUitk(SignalHost, "SignalEquip")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "eq") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点“装入”后第 {slot + 1} 号槽不是这枚芯片（{hud.FeedbackText}）");
        }

        // ── 电路合成台 ─────────────────────────────────────────────────────────────────

        internal static FusionPanelUIToolkit Fusion()
        {
            FusionPanelUIToolkit p = FusionPanelUIToolkit.Instance;
            p?.Refresh(force: true);
            return p;
        }

        internal static int ChipCount(string fw) => St.PrimitiveChips?.Count(x => x != null && x.CardDefId == fw) ?? 0;

        /// <summary>合成台固件下拉选 <paramref name="fw"/>（确认能点、没被挡住再设值——与在弹出菜单里点那一项同一个值变化回调）。</summary>
        internal static bool PickParent(bool slotA, string fw, out string why)
        {
            why = null;
            FusionPanelUIToolkit p = Fusion();
            DropdownField d = p?.ParentField(slotA);
            int idx = p?.ParentIndexOf(fw) ?? -1;
            if (d == null || idx < 0 || idx >= d.choices.Count)
            {
                why = $"固件{(slotA ? "A" : "B")}下拉里没有“{FwName(fw)}”（{d?.choices?.Count ?? 0} 条）";
                return false;
            }
            bool? ok = P.PickableInView(d);
            if (ok != true)
            {
                why = ok == null ? "滚动中" : "下拉框点不到";
                return false;
            }
            d.value = d.choices[idx];
            return true;
        }

        // ── 情报 ────────────────────────────────────────────────────────────────────

        internal static IntelRecord RaidForecastFor(string groupId) =>
            IntelService.StateOf(St)?.Records?.FirstOrDefault(r => r != null && r.Kind == IntelCatalog.KindRaid && r.Subject == groupId);

        // ── 存读档摘要（M5 研发域）────────────────────────────────────────────────────

        /// <summary>
        /// 研发域在存档里的摘要（与存档那一刻 / 读档后第一帧对照）：研究（点数、零头、已完成、进度、队列）、熔合（已发现、线索、在办 / 结束的任务）、
        /// 情报（条目与是否过时）、黑匣子（个数与已分析世界步、已入账点数）、靶场（测试记录条数）、技术数据收支（各来源 / 去处）、技术数据与芯片基板库存。
        /// <paramref name="withProgress"/> = false 时去掉读档后世界一开跑就会变的量（研究点零头、黑匣子进度、破译进度、库存）。
        /// </summary>
        internal static string Digest(CampaignState s, bool withProgress)
        {
            if (s?.Research == null)
            {
                return "（没有研发域）";
            }
            ResearchState r = s.Research;
            var sb = new StringBuilder();
            sb.Append("研究点=").Append(withProgress ? r.Points.ToString(CultureInfo.InvariantCulture) + "+" + r.PointsMilli.ToString(CultureInfo.InvariantCulture) : "-");
            sb.Append("｜已完成=").Append(string.Join(",", (r.CompletedNodes ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal)));
            sb.Append("｜队列=").Append(string.Join(",", r.Queue ?? Array.Empty<string>()));
            if (withProgress)
            {
                sb.Append("｜进度=").Append(string.Join(",", (r.Progress ?? Array.Empty<ResearchProgressRecord>()).Select(p => p.NodeId + ":" + p.Invested)));
            }
            FusionState f = r.Fusion;
            sb.Append("｜配方=").Append(string.Join(",", f?.Discovered ?? Array.Empty<string>()));
            sb.Append("｜线索=").Append(string.Join(",", (f?.Clues ?? Array.Empty<FusionClueRecord>()).Select(x => x.RecipeId + (x.Full ? "*" : "?") + x.Count)));
            sb.Append("｜熔合=").Append(string.Join(",", (f?.Jobs ?? Array.Empty<FusionJobRecord>()).Select(j => j.RecipeId + ":" + j.State)));
            sb.Append("｜熔合统计=").Append(f == null ? "-" : $"{f.Simulations}/{f.SimulationMisses}/{f.Fused}/{f.RolledBack}/{f.TechSpent}");
            IntelState it = r.Intel;
            sb.Append("｜情报=").Append(string.Join(",", (it?.Records ?? Array.Empty<IntelRecord>()).Select(x => x.Kind + ":" + x.Serial + (x.Outdated ? "过时" : string.Empty))));
            BlackBoxState bb = r.BlackBoxes;
            sb.Append("｜黑匣子=").Append(string.Join(",", (bb?.Boxes ?? Array.Empty<BlackBoxRecord>()).Select(b => b.MachineLogicId + (withProgress ? ":" + b.Work + "/" + b.PointsGranted : string.Empty) + (b.Done ? "完" : string.Empty))));
            sb.Append("｜靶场记录=").Append((r.Range?.History?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
            // 读档后已经走了几步时，实验室可能刚开一个新周期（取 1 技术数据）：不带进度的摘要不比实验室那一项。
            sb.Append("｜收支=").Append(string.Join(",", (r.TechFlow ?? Array.Empty<TechFlowRecord>())
                .Where(x => withProgress || x.Key != TechDataFlow.Lab).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + ":" + x.Income + "/" + x.Expense)));
            sb.Append("｜开局=").Append(r.TechStart.ToString(CultureInfo.InvariantCulture));
            if (withProgress)
            {
                sb.Append("｜技术数据=").Append(s.TechData.ToString(CultureInfo.InvariantCulture));
                sb.Append("｜芯片基板=").Append(HomeInventory.Stock(s, Substrate).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
