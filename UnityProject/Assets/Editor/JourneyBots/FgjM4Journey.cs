using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG4-E2E-01：M4 出口旅程 FGJ-M4（里程碑文档 FG-M4；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42）、全程正式输入（ADR-QA-017 三条通道），
    /// RTS 口径：左键选择、右键地面 = 移动 / 右键工作目标 = 派工，按钮只切状态 / 发起动作。
    ///
    /// 里程碑原文：从原料一路生产出一台新机器 → 设置“零件保持 50 个”的规则，看它自动排产 → 精炼塔的酸液堵住，接上废液池恢复 →
    /// 远征一次，回来看离家报告 → 清空所有机器，看应急打印把家园救回来。
    /// 另外回补 DEBT-FG3E2E01-01：在真实配方产线上再走一遍堵塞诊断、布局库放一份、撤销 / 重做、升级传送带与离家后台核对；DEBT-FG4ECO03-06：一张图上用传送带把采矿到装配连起来。
    /// 产线位置全部按种子地形现找（<see cref="FgjM4Common.PlanBlock"/> 等，B25）。
    /// </summary>
    public static partial class FgjM4Journey
    {
        public const string Id = "FGJ-M4";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("research_fixture", "进度夹具：研发树开放前开局就能建的内容记为已研究（DEBT-FG5RND01-05；研究本身在 FGJ-M5 / M5R 走正式入口）", 10, FgjM5Common.ApplyLegacyResearchFixture, FgjM5Common.TickLegacyResearchFixture),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库（成品入库）、发电机、信号塔（出征），拆两处开局残骸拿废料 ──
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep1", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("sel_a2", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_tw", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("sel_b2", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage", "右键点开局残骸（情境命令：拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完（废料 +60）", 180, null, FgjM2Common.TickSalvageDone),
                S("wait_rep2", "等信号塔修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeSignalTower)),
                S("salv2_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸（情境命令：拆解）", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完（废料约 +60）", 180, null, FgjM3Common.TickWreck2Done),

                // ── 第 1 步：从原料一路生产出一台新机器 ──
                S("plan", "看地形：金属矿脉、废墟、稀土矿脉在哪；规划工厂块（精炼炉 → 分流器 → 零件工坊 ×2 + 电子组装台 → 组件工坊）、回收站、成品干线（按格网规则现找，不写死坐标）", 30, null, TickPlan),
                S("exp_sel", "稀土矿脉在已探索区外时：左键点一台机器", 20, c => { if (NeedExplore(c)) FgjM1Journey.ClickMachine(c, "workerB"); },
                    c => NeedExplore(c) ? FgjM1Journey.TickSelected(c, "workerB") : StepOutcome.Done("稀土矿脉已在已探索区里，不用派机器探路"), retries: 4),
                S("exp_go", "右键点稀土矿脉旁的地面：机器开过去探路（右键地面 = 移动）", 60, null, TickExploreGo, retries: 3),
                S("exp_wait", "等机器走到、那一带变成已探索：规划稀土提取钻与回工厂的带、电塔", 240, null, TickExploreWait),
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("eco_place1", "先放发电机 2（电不够时）与回收站（压在废墟上）和它到输入口的带（建造栏点分类与条目、指着格子单击、按住左键分段拖）", 300, null, c => P.TickActions(c, "eco1", 40), retries: 2),
                S("eco_close1", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("eco_built1", "机器取料施工：发电机与回收站建成，回收站开始拆废墟出废料", 900, null, TickFirstBatchBuilt),
                S("eco_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("eco_place", "再按规划逐个放：电塔、精炼炉、两座零件工坊、电子组装台、组件工坊、四个分流器、块内九条带、四股成品带与收集带、成品干线、两座提取钻与它们的带" +
                               "（旋转键、指着格子单击、按住左键分段拖）", 900, null, c => P.TickActions(c, "eco", 40), retries: 2),
                S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("eco_built", "机器取料施工（回收站先建起来拆废墟出废料，攒够了再建后面的）：全部建成、都接上电", 1500, null, TickEcoBuilt),
            };
            // 配方：在建造模式里点每座配方建筑打开通用面板，配方下拉选上，关闭面板。
            steps.Add(S("rec_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1));
            foreach ((string key, string type, int x, int y, string recipe) in FgjM4Common.BlockBuildings)
            {
                string name = HomeGridServiceName(type);
                string rname = RecipeName(recipe);
                steps.Add(S("rec_" + key + "_open", $"左键点{name}（{key}）：打开通用面板", 20, null, c => P.TickOpenPanel(c, c.Get(key + ".type"), P.P(c, key)), retries: 2));
                steps.Add(S("rec_" + key + "_pick", $"配方下拉选“{rname}”：选中即生效", 15, null, c => P.TickPickRecipe(c, recipe), retries: 1));
                steps.Add(S("rec_" + key + "_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1));
            }
            steps.AddRange(new[]
            {
                S("rec_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("line_run", "产线跑起来：提取钻采矿 → 精炼炉出合金 → 两座零件工坊、电子组装台、组件工坊都开工；成品进库存", 400, null, TickLineRunning),
                S("asm_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("asm_sub", "“缺材料时用废料代付”开着就点掉（这台要用产线的材料造）", 10, null, TickSubstituteOff, retries: 1),
                S("asm_produce", "点“生产 搬运机”：入队，等产线的材料", 10, c => FgjM1Journey.ClickUi(c, P.FactoryHost, "ProduceBtn_Hauler"), TickHaulerQueued, retries: 1),
                S("asm_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("machine", "装配站从产线取料（不用废料代付）造出搬运机：取料逐项是结构材 / 零件 / 电子件 / 作战组件，废料不动，“用产线材料造出的机器”+1", 600, null, TickHaulerBuilt),
            });
            steps.AddRange(PhaseSteps());
            steps.AddRange(LaterSteps());
            return new JourneyDef
            {
                Id = Id,
                Title = "M4 出口：从原料一路生产出一台新机器 → “零件保持 50 个”规则自动排产 → 精炼塔酸液堵住、接废液池恢复 → 远征一次看离家报告 → 清空所有机器看应急打印救回家园",
                Seed = TestSeed,
                TotalTimeoutSeconds = 3000,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "salv2_wait", "eco_built", "machine" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string HomeGridServiceName(string type) => HomeGridService.DisplayName(type);

        private static string RecipeName(string id) => ItemCatalog.TryGetRecipe(id, out RecipeDef r) ? r.Name : id;

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM3Journey.IsolateLayoutLibrary(c);
            });

        // ── 规划 ──────────────────────────────────────────────────────────────────────

        private static bool NeedExplore(JourneyContext c) => c.GetInt("needExplore") == 1;

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            var res = new List<string>();
            foreach (string t in new[] { "ore_metal", "ore_rare", "ruin", "oil", "water" })
            {
                res.Add(P.NearestTerrain(s, core, t, 140, out GridCell at)
                    ? $"{t} 最近 {P.Cell(at)}（离核心 {Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(core.X, core.Y)):F1} 格，{(P.Explored(s, at) ? "已探索" : "未探索")}）"
                    : $"{t} 140 格内没有");
            }
            List<FgjM4Common.FactoryPlan> blocks = P.PlanBlocks(s, 6, out string why);
            if (blocks.Count == 0)
            {
                return StepOutcome.Fail("工厂块放不下：" + why + "；" + string.Join("；", res));
            }
            var fails = new List<string>();
            foreach (FgjM4Common.FactoryPlan plan in blocks)
            {
                // 先接金属矿（矿到精炼炉的带最受地形约束），再看稀土矿脉：已探索就接上并铺成品出路、回收站、电塔；没探索就先派机器探路（exp_*）。
                if (!P.TryFinish(s, plan, false, out _, out string w))
                {
                    fails.Add(P.Cell(plan.Origin) + " " + w);
                    continue;
                }
                FgjM4Common.Placed eb = plan.B("eb");
                P.PortOutside(eb.Type, eb.Pivot, 0, eb.Type + ".in1", out GridCell ebIn1, out _);
                if (!P.NearestTerrain(s, ebIn1, "ore_rare", 90, out GridCell rare))
                {
                    fails.Add(P.Cell(plan.Origin) + " 电子组装台 90 格内没有稀土矿脉");
                    continue;
                }
                P.SetP(c, "rare", rare);
                bool explored = P.Explored(s, rare);
                if (explored && !FinishPlan(c, s, plan, out w))
                {
                    fails.Add(P.Cell(plan.Origin) + " " + w);
                    continue;
                }
                c.SetInt("needExplore", explored ? 0 : 1);
                if (!explored)
                {
                    SavePlan(c, plan);
                }
                return StepOutcome.Done($"{string.Join("；", res)}。工厂块原点 {P.Cell(plan.Origin)}（相对核心 {plan.Origin.X - core.X},{plan.Origin.Y - core.Y}" +
                                        (fails.Count > 0 ? $"；前 {fails.Count} 个原点没成：{string.Join("；", fails)}" : string.Empty) + $"）：{P.Describe(plan, core)}；" +
                                        (explored ? w : "稀土矿脉 " + P.Cell(rare) + " 要先派机器探路"));
            }
            return StepOutcome.Fail("规划不出工厂：" + string.Join("；", fails));
        }

        /// <summary>稀土矿脉已探索：接稀土提取钻、铺成品出路（收集带 → 仓库 / 核心输入口）、回收站、电塔，生成动作队列。</summary>
        private static bool FinishPlan(JourneyContext c, CampaignState s, FgjM4Common.FactoryPlan plan, out string report)
        {
            if (!P.TryFinish(s, plan, true, out List<GridCell> poles, out string why))
            {
                report = why;
                return false;
            }
            SavePlan(c, plan);
            c.Set("plan.poles", FgjM3Common.EncodeCells(poles));
            BuildActions(c, plan, poles);
            c.Set("plan.gens", FgjM3Common.EncodeCells(plan.Generators));
            report = $"电：{plan.PowerNote}；成品干线进{plan.TrunkSink}、回收站废料进{plan.RecyclerSink}；路线 {string.Join("、", plan.Routes.Select(r => r.Key + " " + r.Path.Count + " 格"))}；T2 电塔 {poles.Count} 座；动作队列 {c.GetInt("eco.n")} 个";
            return true;
        }

        /// <summary>规划存进旅程变量（跨域重载 / 断点续跑都能取回）：每条路线存格子串，建筑存枢轴与类型，动作队列在放置前生成。</summary>
        private static void SavePlan(JourneyContext c, FgjM4Common.FactoryPlan plan)
        {
            P.SetP(c, "plan.origin", plan.Origin);
            c.Set("plan.recyclerSink", plan.RecyclerSink ?? string.Empty);
            c.Set("plan.buildings", string.Join(",", plan.Buildings.Select(b => b.Key)));
            foreach (FgjM4Common.Placed b in plan.Buildings)
            {
                P.Remember(c, b);
            }
            c.Set("plan.splitters", string.Join(";", plan.Splitters.Select(x => $"{x.key},{x.cell.X},{x.cell.Y},{x.dir}")));
            c.Set("plan.routes", string.Join(",", plan.Routes.Select(r => r.Key)));
            foreach (FgjM4Common.Route r in plan.Routes)
            {
                P.SavePath(c, "path." + r.Key, r.Path);
                c.SetInt("path." + r.Key + ".dir", r.GoalDir);
                c.Set("path." + r.Key + ".label", r.Label ?? r.Key);
                c.Set("path." + r.Key + ".tool", r.Tool);
            }
            c.Set("plan.poles", c.Get("plan.poles", string.Empty));
            c.Set("plan.trunkSink", plan.TrunkSink);
            c.Set("plan.used", string.Join(";", plan.Used.Select(k => k.ToString(CultureInfo.InvariantCulture))));
        }

        /// <summary>从旅程变量还原规划（追加稀土提取钻与电塔时用）。</summary>
        private static FgjM4Common.FactoryPlan LoadPlan(JourneyContext c)
        {
            var plan = new FgjM4Common.FactoryPlan { Origin = P.P(c, "plan.origin") };
            foreach (string k in c.Get("plan.buildings", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                plan.Buildings.Add(new FgjM4Common.Placed
                {
                    Key = k, Type = c.Get(k + ".type"), Pivot = P.P(c, k), Rot = 0, Recipe = c.Get(k + ".recipe"), Label = HomeGridService.DisplayName(c.Get(k + ".type")),
                });
            }
            foreach (string sp in c.Get("plan.splitters", string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] a = sp.Split(',');
                plan.Splitters.Add((a[0], new GridCell(int.Parse(a[1], CultureInfo.InvariantCulture), int.Parse(a[2], CultureInfo.InvariantCulture)), int.Parse(a[3], CultureInfo.InvariantCulture)));
            }
            foreach (string k in c.Get("plan.routes", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                plan.Routes.Add(new FgjM4Common.Route { Key = k, Path = P.LoadPath(c, "path." + k), GoalDir = c.GetInt("path." + k + ".dir"), Label = c.Get("path." + k + ".label"), Tool = c.Get("path." + k + ".tool", P.BeltT1) });
            }
            foreach (string k in c.Get("plan.used", string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                plan.Used.Add(long.Parse(k, CultureInfo.InvariantCulture));
            }
            plan.TrunkSink = c.Get("plan.trunkSink");
            plan.RecyclerSink = c.Get("plan.recyclerSink");
            return plan;
        }

        // ── 探路（稀土矿脉在已探索区外）────────────────────────────────────────────────

        private static StepOutcome TickExploreGo(JourneyContext c)
        {
            if (!NeedExplore(c))
            {
                return StepOutcome.Done("不用探路");
            }
            GridCell rare = P.P(c, "rare");
            int id = c.GetInt("workerB");
            // 指着矿脉旁边一格能走的地面右键（目标不在画面里时先按方向键平移，与玩家一样）。
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(FgjM3Common.Ground(rare), 1)))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rc") < 1200)
            {
                return StepOutcome.Wait;
            }
            return MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.IsAlive && HomeValleyWorkOrders.FindActiveOrderForMachine(St, id) == null
                ? StepOutcome.Done($"右键 {P.Cell(rare)}：{FgjM3Common.Label(id)} 接到移动命令开过去（方向键平移镜头 {c.GetInt(FgjM3Common.SK(c, "rc") + ".pans")} 次）")
                : StepOutcome.Retry($"右键后 {FgjM3Common.Label(id)} 没有接到移动命令（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        private static StepOutcome TickExploreWait(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!NeedExplore(c))
            {
                return StepOutcome.Done("稀土矿脉已在已探索区里，规划时已经接上");
            }
            FgjM4Common.FactoryPlan plan = LoadPlan(c);
            GridCell rare = P.P(c, "rare");
            if (!P.Explored(s, rare))
            {
                return StepOutcome.Wait;
            }
            // 机器到了附近：试着规划（还没探索到能放的格时继续等，最多到超时）。
            if (FgjM3Common.NowMs() - c.GetLong("expTry") < 1500)
            {
                return StepOutcome.Wait;
            }
            c.SetLong("expTry", FgjM3Common.NowMs());
            if (!FinishPlan(c, s, plan, out string report))
            {
                c.Set("expWhy", report);
                return c.StepElapsed < 220 ? StepOutcome.Wait : StepOutcome.Fail("探路后仍规划不出：" + report);
            }
            FgjM4Common.Placed dr = plan.B("drillR");
            FgjM4Common.Route rr = plan.R("drillR");
            return StepOutcome.Done($"稀土提取钻 {P.Cell(dr.Pivot)}，带 {rr.Path.Count} 格回电子组装台稀土口；{report}");
        }

        private static void BuildActions(JourneyContext c, FgjM4Common.FactoryPlan plan, List<GridCell> poles)
        {
            // 第一批：先造发电机与回收站（开局只有一座 80 电，开局建筑已经吃掉大半；没电的回收站拆不出废料，后面的施工会一直等材料）。
            var first = new List<string>();
            foreach (GridCell g in plan.Generators)
            {
                first.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {P.Cell(g)}");
            }
            FgjM4Common.Placed rec = plan.B("recycler");
            first.Add(P.ActB(rec));
            first.Add(P.ActR(P.BeltT1, "path.r_recycler", plan.R("r_recycler").GoalDir, "回收站废料带 → " + plan.RecyclerSink));
            P.SetActions(c, "eco1", first);
            var acts = new List<string>();
            foreach (GridCell pole in poles)
            {
                acts.Add($"B|{P.PoleT2}|{pole.X}|{pole.Y}|0|T2 电塔 {P.Cell(pole)}");
            }
            foreach (string k in new[] { "furnace", "pwS", "pwP", "eb", "cw" })
            {
                acts.Add(P.ActB(plan.B(k)));
            }
            foreach (var sp in plan.Splitters)
            {
                acts.Add(P.ActT(P.SplitterTool, sp.cell, sp.dir, "分流器 " + sp.key + " " + P.Cell(sp.cell)));
            }
            foreach (FgjM4Common.Route r in plan.Routes)
            {
                if (r.Key == "r_recycler" || r.Key == "drillM" || r.Key == "drillR")
                {
                    continue;
                }
                acts.Add(P.ActR(P.BeltT1, "path." + r.Key, r.GoalDir, r.Label));
            }
            foreach (string k in new[] { "drillM", "drillR" })
            {
                FgjM4Common.Placed d = plan.B(k);
                acts.Add(P.ActB(d));
                acts.Add(P.ActR(P.BeltT1, "path." + k, plan.R(k).GoalDir, (k == "drillM" ? "金属" : "稀土") + "提取钻 → " + (k == "drillM" ? "精炼炉" : "电子组装台稀土口")));
            }
            P.SetActions(c, "eco", acts);
            c.SetInt("eco.total", first.Count + acts.Count);
        }

        // ── 施工完成 ──────────────────────────────────────────────────────────────────

        private static IEnumerable<string> PlanKeys(JourneyContext c) => c.Get("plan.buildings", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        private static IEnumerable<string> RouteKeys(JourneyContext c) => c.Get("plan.routes", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        private static StepOutcome TickFirstBatchBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var notBuilt = new List<string>();
            foreach (GridCell g in FgjM3Common.DecodeCells(c.Get("plan.gens")))
            {
                if (!P.BuildingReady(s, HomeValleyLayout.BuildingTypeGenerator2, g, out _))
                {
                    notBuilt.Add("发电机2" + P.Cell(g));
                }
            }
            if (!P.BuildingReady(s, P.Recycler, P.P(c, "recycler"), out BuildingRecord rec))
            {
                notBuilt.Add("回收站");
            }
            int unbuilt = P.LoadPath(c, "path.r_recycler").Count(p => !P.RouteBuilt(s, new List<GridCell> { p }, true));
            ProductionService.Producer rp = rec != null ? P.Prod(rec) : null;
            if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                c.Log($"第一批：还没建成 [{string.Join(",", notBuilt)}]、带还差 {unbuilt} 格；废料 {s.Scrap}；{P.PowerLine(s)}");
            }
            if (notBuilt.Count > 0 || unbuilt > 0 || rp == null || rp.Rec.RuinRecovered < 4)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"发电机 2 ×{FgjM3Common.DecodeCells(c.Get("plan.gens")).Count} 与回收站建成（{c.StepElapsed:F0} 秒）：回收站拆出 {rp.Rec.RuinRecovered} 废料（{ProductionService.StateText(rp)}）；{P.PowerLine(s)}；废料 {s.Scrap}");
        }

        private static StepOutcome TickEcoBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var notBuilt = new List<string>();
            foreach (string k in PlanKeys(c))
            {
                if (!P.BuildingReady(s, c.Get(k + ".type"), P.P(c, k), out _))
                {
                    notBuilt.Add(k);
                }
            }
            int unbuiltCells = 0;
            foreach (string k in RouteKeys(c))
            {
                unbuiltCells += P.LoadPath(c, "path." + k).Count(p => !P.RouteBuilt(s, new List<GridCell> { p }, true));
            }
            foreach (GridCell pole in FgjM3Common.DecodeCells(c.Get("plan.poles")))
            {
                if (!P.BuildingReady(s, P.PoleT2, pole, out _))
                {
                    notBuilt.Add("电塔" + P.Cell(pole));
                }
            }
            foreach (GridCell g in FgjM3Common.DecodeCells(c.Get("plan.gens")))
            {
                if (!P.BuildingReady(s, HomeValleyLayout.BuildingTypeGenerator2, g, out _))
                {
                    notBuilt.Add("发电机2" + P.Cell(g));
                }
            }
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                var q = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, q);
                ProductionService.Producer rp = P.Prod(P.Bld(c, "recycler"));
                c.Log($"施工中：还没建成 [{string.Join(",", notBuilt)}]、带还差 {unbuiltCells} 格；废料 {s.Scrap}；施工队列 {q.Count} 项；回收站已拆出 {rp?.Rec.RuinRecovered ?? 0} 废料" +
                      (rp != null ? $"（{ProductionService.StateText(rp)}：{ProductionService.ReasonText(s, rp)}）" : string.Empty) + "；" + P.PowerLine(s));
            }
            if (notBuilt.Count > 0 || unbuiltCells > 0)
            {
                return StepOutcome.Wait;
            }
            var unpowered = new List<string>();
            foreach (string k in PlanKeys(c))
            {
                BuildingRecord b = P.Bld(c, k);
                if (b.PowerState != BuildingPowerState.Powered)
                {
                    unpowered.Add($"{HomeGridService.DisplayName(b.BuildingTypeId)} {P.Cell(P.P(c, k))}：{b.PowerState}");
                }
            }
            if (unpowered.Count > 0)
            {
                return c.StepElapsed > 1400 ? StepOutcome.Fail("建成了但没电：" + string.Join("；", unpowered)) : StepOutcome.Wait;
            }
            return StepOutcome.Done($"全部建成（{c.StepElapsed:F0} 秒，3 倍速）、都接上电；废料 {s.Scrap}；回收站已拆出 {P.Prod(P.Bld(c, "recycler"))?.Rec.RuinRecovered} 废料");
        }

        private static StepOutcome TickLineRunning(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            var lines = new List<string>();
            bool all = true;
            foreach (string k in new[] { "drillM", "drillR", "furnace", "pwS", "pwP", "eb", "cw" })
            {
                long done = P.Completed(P.Bld(c, k));
                all &= done >= 1;
                lines.Add($"{k} 完成 {done} 次");
            }
            if (!all)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log("产线：" + string.Join("、", lines) + "；" + string.Join("；", new[] { "furnace", "pwS", "pwP", "eb", "cw" }.Select(k => k + " " + ProductionService.ReasonText(St, P.Prod(P.Bld(c, k))))));
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done("产线全部开工：" + string.Join("、", lines) + "；库存 " + P.StockLine("alloy", "structural", "part", "electronic", "combat_component"));
        }

        // ── 装配站 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickSubstituteOff(JourneyContext c) => TickSubstitute(c, false);

        /// <summary>装配站面板的“缺材料时用废料代付”开关点到 <paramref name="on"/>（已经是就不点）。</summary>
        private static StepOutcome TickSubstitute(JourneyContext c, bool on)
        {
            // 面板刚打开 / 开关刚切换时这一帧还在重排（材料与代付说明几行会变长），等排好版再判定、再进下一步点“生产”。
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (AssemblyMaterials.ScrapSubstitute(St) == on)
            {
                if (FgjM3Common.Done(c, "tog") && FgjM3Common.SinceMs(c, "tog") < 600)
                {
                    return StepOutcome.Wait; // 刚点了开关：等面板按新状态重排完，下一步再点“生产”
                }
                return StepOutcome.Done($"“缺材料时用废料代付”已{(on ? "开" : "关")}（“{FactoryPanelUIToolkitNote()}”）");
            }
            Toggle t = JourneyInput.FindUitk<Toggle>(P.FactoryHost, "SubstituteToggle");
            if (!FgjM3Common.Once(c, "tog", () => JourneyInput.ClickElement(t)))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tog") < 1200 ? StepOutcome.Wait : StepOutcome.Retry($"点了代付开关后它还{(on ? "关" : "开")}着：" + JourneyInput.LastUiFailure);
        }

        private static string FactoryPanelUIToolkitNote() => JourneyInput.FindUitk<Label>(P.FactoryHost, "SubstituteNote")?.text ?? string.Empty;

        private static FactoryQueueItemRecord HaulerItem() =>
            St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintHaulerId);

        private static StepOutcome TickHaulerQueued(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FactoryQueueItemRecord item = HaulerItem();
            if (item == null)
            {
                return StepOutcome.Retry($"点“生产”后队列里没有搬运机（{FgjM1Journey.UiFail(c)}；{JourneyInput.FindUitk<Label>(P.FactoryHost, "ProduceHintLabel")?.text}）");
            }
            c.Set("haulerItem", item.QueueItemId);
            c.SetLong("fromLine0", St.Economy.MachinesFromLine);
            c.SetInt("scrapAtQueue", St.Scrap);
            c.Set("haulers0", string.Join(",", MachineRegistry.AllRecords.Where(m => m != null && m.ChassisId == HomeValleyLayout.Erc002ChassisId).Select(m => m.LogicId.ToString(CultureInfo.InvariantCulture))));
            return StepOutcome.Done($"生产队列：搬运机（{item.State}；{HomeValleyFactory.DescribeWait(item) ?? item.BlockedReason}）；材料说明“{JourneyInput.FindUitk<Label>(P.FactoryHost, "MaterialsLabel")?.text?.Replace("\n", " / ")}”");
        }

        private static StepOutcome TickHaulerBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            FactoryQueueItemRecord item = HomeValleyFactory.Find(St, c.Get("haulerItem"));
            if (item == null)
            {
                return StepOutcome.Fail("队列里找不到这一单");
            }
            if (item.State == FactoryQueueState.Failed || item.State == FactoryQueueState.Cancelled)
            {
                return StepOutcome.Fail($"搬运机生产 {item.State}：{item.BlockedReason}");
            }
            if (item.State != FactoryQueueState.Completed)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"装配站：{item.State}（{HomeValleyFactory.DescribeWait(item) ?? item.BlockedReason}）；库存 {P.StockLine("structural", "part", "electronic", "combat_component")}；装配站缓存 {AssemblyMaterials.DescribeStacks(AssemblyMaterials.Buffer(St))}");
                }
                return StepOutcome.Wait;
            }
            var before = new HashSet<string>(c.Get("haulers0", string.Empty).Split(','));
            MachineRecord m = MachineRegistry.AllRecords.FirstOrDefault(x => x != null && x.IsAlive && x.ChassisId == HomeValleyLayout.Erc002ChassisId && !before.Contains(x.LogicId.ToString(CultureInfo.InvariantCulture)));
            string taken = string.Join(",", (item.Taken ?? Array.Empty<ItemStackRecord>()).Where(x => x != null && x.Amount > 0).OrderBy(x => x.ItemId, StringComparer.Ordinal).Select(x => x.ItemId + "=" + x.Amount));
            bool fromLine = St.Economy.MachinesFromLine == c.GetLong("fromLine0") + 1;
            bool noSub = item.SubstituteScrap == 0;
            bool mats = taken.Contains("structural=") && taken.Contains("part=") && taken.Contains("electronic=") && taken.Contains("combat_component=");
            if (m == null)
            {
                return StepOutcome.Wait;
            }
            c.SetInt("hauler", m.LogicId);
            return fromLine && noSub && mats
                ? StepOutcome.Done($"{FgjM3Common.Label(m.LogicId)} 出厂：装配站取料 {taken}，废料代付 0；“用产线材料造出的机器”{c.GetLong("fromLine0")} → {St.Economy.MachinesFromLine}（从金属 / 稀土矿脉开始，经精炼炉、零件工坊、电子组装台、组件工坊，全部走传送带）")
                : StepOutcome.Fail($"搬运机不是用产线材料造的：取料 {taken}、代付 {item.SubstituteScrap}、产线计数 {c.GetLong("fromLine0")} → {St.Economy.MachinesFromLine}");
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            c.Log(FgjM3Common.FrameReport("产线施工 / 运转 / 离家段"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
