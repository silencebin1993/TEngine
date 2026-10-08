using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG6-E2E-01：M6 出口旅程 FGJ-M6（里程碑文档 FG-M6；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度）、全程正式输入（ADR-QA-017 三条通道），
    /// RTS 口径：左键选择、右键地面 = 移动 / 右键敌人 = 攻击 / 右键工作目标 = 派工，按钮只切状态 / 发起动作。
    ///
    /// 里程碑原文：造第一座炮塔，挡住第一次（1 级）突袭 → 布置“漏油带 + 燃迹炮塔”火墙 → 出发远征 → 远征途中家园遇袭：一次跳回家园接入炮塔防守，
    /// 一次让信号留在远征队那边、家园自己守 → 回来看离家报告里的突袭时间线 → 被摧毁的建筑已经自动重建。
    ///
    /// 突袭全部由正式触发产生（暴露越过阈值 → 突袭导演排定 → 预警 → 行进 → 到达 → 攻城；DEBT-FG6DEF04-09），没有任何突袭夹具。
    /// 暴露由家园的高功率生产推高；旅程像玩家一样用远征准备面板的“关闭信号塔主动广播”（FG01 / FG16 的降暴露手段）控制节奏：
    /// 第一次突袭排定后关广播（不让暴露冲过 90 引来周期性 4 级突袭），第一波预警后开广播（引来第二波），第二波排定后、出发远征前关广播，
    /// 跳回家园防守第二波时再开广播（引来第三波）。为什么必须这样做见 ADR-QA-023 第 2 节（干跑实测：不降暴露第 6～7 日必来 4 级 66 台把核心打空）。
    /// 家园防线位置全部按种子地形现找（<see cref="FgjM6Common.PlanHome"/>，B25）；火墙按第一次突袭时玩家在预警条上看到的来袭方向规划。
    /// </summary>
    public static partial class FgjM6Journey
    {
        public const string Id = "FGJ-M6";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private static CampaignState St => CampaignSession.Current;

        /// <summary>第一批（不用研究）：回收站与供料带、发电机 2、两座实验室、第一座炮塔。</summary>
        private static readonly string[] BatchA = { "recycler", "lab1", "lab2", "t1" };

        /// <summary>第二批：其余五座炮塔（不用研究）。</summary>
        private static readonly string[] BatchTurrets = { "t2", "t3", "t4", "t5", "t6" };

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("difficulty", "新档难度是标准、导演开局宽限到第 5 个游戏日（第一次突袭固定 1 级、规模 × 0.6）", 10, null, TickDifficulty),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库、发电机、信号塔，拆两处开局残骸拿废料（与 FGJ-M5 同一套正式输入）──
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

                // ── 规划与第一批建筑（不用研究）：回收站、发电机 2、两座仿真实验室、第一座炮塔（造第一座炮塔）──
                S("plan", "看地形：规划回收站（压在废墟上）、两座仿真实验室、离核心最近的油井上的精炼链、围着核心的六座炮塔与护盾、核心外一圈屏障、发电机 2", 30, null, TickPlan),
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m6_place1", "放发电机 2、回收站与它到仓库输入口的带、两座仿真实验室、第一座轻型炮塔（建造栏“防御”分类选轻型炮塔：炮塔蓝图下拉默认“基础炮塔·连射器”、悬停画射程圈）", 300, null,
                    c => P.TickActions(c, "m6a", 40), retries: 2),
                S("build_close1", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("rt_open", "按研发树键（默认 K）打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("rt_queue1", "依次点分支筛选、左键点节点排进研究队列：工业 · 精炼塔、防御 · 屏障与闸门、防御 · 护盾发生器、防御 · 陷阱发射器、防御 · 维修无人机站（队列上限 5）", 90, null,
                    c => D.TickQueueAll(c, QueueOne(c)), retries: 2),
                S("rt_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),
                S("m6_built1", "机器取料施工：回收站、发电机 2、两座实验室、第一座炮塔建成接电（第一座炮塔装着默认蓝图、在战斗内核里待命）", 1200, null, TickBuiltFirst),
                S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m6_place2", "其余五座轻型炮塔围着核心放下（同一张默认蓝图）", 200, null, c => P.TickActions(c, "m6t", 40), retries: 2),
                S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("wait_ref", "等研究：工业 · 精炼塔（要电塔时连同能源 · 电塔 T2）", 900, null, c => D.TickResearched(c, RefineryNodes(c))),
                S("build_open3", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m6_place3", "放精炼链：精炼塔、废液池、燃油口外一格管线与储罐、酸液口接废液池、油井上的泵、原油管线（建造栏点条目、指着格子单击、按住左键拖）", 300, null,
                    c => P.TickActions(c, "m6r", 40), retries: 2),
                S("build_close3", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                // ── 暴露：第一次突袭排定后关信号塔广播（第一次突袭固定 1 级；暴露再往上冲到 90 会开始 4 级周期计时）──
                S("exp_first", "看暴露：高功率生产把暴露推过 30，突袭导演排定第一次突袭（暴露面板“家园突袭：已排定 1 波”；开局宽限到第 5 个游戏日）", 2400, null, TickFirstPlanned),
                S("exp_off1", "左键点信号塔打开远征准备面板，勾上“关闭信号塔主动广播”（每 10 游戏秒暴露 −2，带宽 −3），点“关闭”", 60, null, c => D.TickBroadcast(c, true), retries: 2),
                S("wait_def", "等研究：防御 · 屏障与闸门、防御 · 护盾发生器", 1500, null, c => D.TickResearched(c, D.NodeBarrier, D.NodeShield)),
                S("build_open4", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m6_place4", "护盾发生器贴着核心放下", 120, null, c => P.TickActions(c, "m6s", 40), retries: 2),
                S("m6_walls", "核心外一圈屏障 T2：建造栏“防御”分类选屏障 T2，每段按住左键从一端拖到另一端（拖拽中地面画敌方来路预览，放下的是虚影）", 400, null, D.TickWalls, retries: 2),
                S("build_close4", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("exp_falling", "暴露回落到 20 以下（30 档重新武装），第一次突袭的计划不变", 900, null, TickExposureFalling),
                S("rt_open2", "按研发树键打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("rt_queue2", "排进研究队列：防御 · 自动重建（维修无人机站之后）", 60, null, c => D.TickQueueAll(c, QueueTwo(c)), retries: 2),
                S("rt_close2", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),
                S("m6_built2", "机器取料施工（废料不够时等回收站拆出来）：六座炮塔、护盾发生器、精炼链与屏障圈全部建成；精炼塔开始出燃油、酸液进废液池", 2400, null, TickBuiltDefense),
            };
            // 开局（放第一座炮塔之前）先给默认炮塔蓝图标接入口：之后造的炮塔都能接入。
            steps.InsertRange(steps.FindIndex(x => x.Id == "plan"), UplinkPortSteps());
            steps.AddRange(PrepSteps());
            steps.AddRange(RaidSteps());
            return new JourneyDef
            {
                Id = Id,
                Title = "M6 出口：造第一座炮塔挡住第一次（1 级）突袭 → 布置“漏油带 + 燃迹炮塔”火墙 → 出发远征 → 远征途中家园遇袭（一次跳回家园接入炮塔防守、一次留在远征队家园自己守）→ 回来看离家报告里的突袭时间线 → 被摧毁的建筑已经自动重建",
                Seed = TestSeed,
                TotalTimeoutSeconds = 6300,
                OnFinish = Cleanup,
                Version = 2,
                CheckpointAfter = new[] { "m6_built2", "ready", "fw_ready" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM2Common.IsolateCodex(c);
                FgjM3Journey.IsolateLayoutLibrary(c);
            });

        private static StepOutcome TickDifficulty(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            bool std = s.DifficultyId == "Standard" && !DifficultyService.StoryOnly(s) && Mathf.Approximately(DifficultyService.Scale(s), 1f) && Mathf.Approximately(DifficultyService.Frequency(s), 1f);
            long grace = RaidDirectorService.GraceEndTick;
            return std && grace > 0
                ? StepOutcome.Done($"难度标准（规模 ×{DifficultyService.Scale(s):0.#}、频率 ×{DifficultyService.Frequency(s):0.#}、预警 ×{DifficultyService.Warning(s):0.#}）；开局宽限到第 {grace} 步（第 {RaidCatalog.FirstRaidMinDays:0} 个游戏日），" +
                                   $"第一次普通突袭固定 1 级、预算 × {RaidCatalog.FirstRaidScale:0.##}；暴露 {s.SignalExposure:F1}")
                : StepOutcome.Fail($"新档难度不对：{s.DifficultyId}，只有剧情 {DifficultyService.StoryOnly(s)}、规模 {DifficultyService.Scale(s)}、频率 {DifficultyService.Frequency(s)}；宽限 {grace}");
        }

        // ── 规划 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            D.HomePlan hp = D.PlanHome(s, out string why);
            if (hp == null)
            {
                return StepOutcome.Fail("家园防线规划不出来：" + why);
            }
            D.SaveHome(c, hp);
            c.SetInt("needPoles", hp.NeedPoles ? 1 : 0);
            var first = new List<string>();
            foreach (GridCell g in hp.Plan.Generators)
            {
                first.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {D.Cell(g)}");
            }
            first.Add(P.ActB(hp.Plan.B("recycler")));
            first.Add(P.ActR(P.BeltT1, "path.r_recycler", hp.Plan.R("r_recycler").GoalDir, "回收站废料带 → 仓库输入口"));
            first.Add(P.ActB(hp.Plan.B("lab1")));
            first.Add(P.ActB(hp.Plan.B("lab2")));
            first.Add(P.ActB(hp.Plan.B("t1")));
            P.SetActions(c, "m6a", first);
            P.SetActions(c, "m6t", BatchTurrets.Select(k => P.ActB(hp.Plan.B(k))));
            P.SetActions(c, "m6r", D.RefineryActions(c));
            P.SetActions(c, "m6s", new[] { P.ActB(hp.Plan.B("shield")) });
            return StepOutcome.Done($"规划（全部在归还核心配电范围里）：{D.DescribeHome(hp, core)}");
        }

        /// <summary>第一批研究队列（队列上限 5）：精炼塔（要电塔时先排电塔 T2）、屏障与闸门、护盾发生器、陷阱发射器、维修无人机站（放不下时挪到第二批）。</summary>
        private static string[] QueueOne(JourneyContext c)
        {
            var list = new List<string>();
            if (c.GetInt("needPoles") == 1)
            {
                list.Add(D.NodePole);
            }
            list.AddRange(new[] { D.NodeRefinery, D.NodeBarrier, D.NodeShield, D.NodeTrap, D.NodeDrone });
            return list.Take(ResearchService.QueueMax).ToArray();
        }

        private static string[] QueueTwo(JourneyContext c)
        {
            var list = new List<string> { D.NodeDrone, D.NodeRebuild };
            return list.ToArray();
        }

        private static string[] RefineryNodes(JourneyContext c) => c.GetInt("needPoles") == 1 ? new[] { D.NodePole, D.NodeRefinery } : new[] { D.NodeRefinery };

        // ── 施工 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickBuiltFirst(JourneyContext c)
        {
            StepOutcome o = M.TickBuilt(c, BatchA, new[] { "r_recycler" }, true, "第一批建筑");
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            CampaignState s = St;
            BuildingRecord t = P.Bld(c, "t1");
            TurretRecord r = t != null ? TurretService.Find(s, t.BuildingId) : null;
            bool inKernel = r != null && D.HomeSite != null && D.HomeSite.TryGetTurretUnit(r.Serial, out _);
            if (r == null || r.BlueprintId != TurretService.DefaultLightBlueprintId || !inKernel || t.PowerState != BuildingPowerState.Powered)
            {
                // 刚建成的这一刻炮塔对账（每 0.5 游戏秒一次）还没把它放进战斗内核：等一会儿。
                string k0 = FgjM3Common.SK(c, "turretWait");
                if (c.GetLong(k0) == 0)
                {
                    c.SetLong(k0, NowMs());
                }
                if (NowMs() - c.GetLong(k0) < 15000)
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Fail($"第一座炮塔建成后状态不对：记录 {(r == null ? "没有" : r.BlueprintId + " v" + r.BlueprintVersion)}、在内核里 {inKernel}、电 {t?.PowerState}");
            }
            c.Set("t1.id", t.BuildingId);
            return StepOutcome.Done(o.Message + $"；第一座炮塔 {t.BuildingId}：装“{TurretService.BlueprintName(s, r.BlueprintId)}” v{r.BlueprintVersion}、射程 {TurretService.RangeOfTurret(s, t.BuildingId):F0} 米、" +
                                   $"目标模式 {TurretCatalog.ModeName(r.TargetMode)}，在家园战斗内核里待命；暴露 {s.SignalExposure:F1}（用电需求 {s.PowerDemand:F0}）");
        }

        private static StepOutcome TickBuiltDefense(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var notBuilt = new List<string>();
            foreach (string k in BatchTurrets.Concat(new[] { "shield" }))
            {
                if (!P.BuildingReady(s, c.Get(k + ".type"), P.P(c, k), out _))
                {
                    notBuilt.Add(k);
                }
            }
            foreach (string k in new[] { "ref.tower", "ref.pond" })
            {
                if (!P.BuildingReady(s, k == "ref.tower" ? P.Tower : P.Pond, P.P(c, k), out _))
                {
                    notBuilt.Add(k);
                }
            }
            var pipes = new List<GridCell> { P.P(c, "ref.fuel"), P.P(c, "ref.tank"), P.P(c, "ref.acid"), P.P(c, "ref.pump") };
            pipes.AddRange(P.LoadPath(c, "path.crude"));
            int pipeLeft = pipes.Count(x => !P.RouteBuilt(s, new List<GridCell> { x }, false));
            int walls = D.WallsBuilt(c, out int wallTotal);
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                var q = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, q);
                c.Log($"防线施工：还没建成 [{string.Join(",", notBuilt)}]、管线还差 {pipeLeft} 格、屏障 {walls}/{wallTotal}；废料 {s.Scrap}；施工队列 {q.Count} 项；{P.PowerLine(s)}；暴露 {s.SignalExposure:F1}");
            }
            if (notBuilt.Count > 0 || pipeLeft > 0 || walls < wallTotal)
            {
                return StepOutcome.Wait;
            }
            // 精炼塔出燃油（储罐有油）、酸液进废液池（塔没有被酸液堵住）。
            long tank = FgjM3Common.TankMl(P.P(c, "ref.tank"));
            BuildingRecord tower = P.BuildingAtPivot(s, P.Tower, P.P(c, "ref.tower"));
            long done = P.Completed(tower);
            if (tank <= 0 || done <= 0)
            {
                return c.StepElapsed < 2300 ? StepOutcome.Wait : StepOutcome.Fail($"精炼链建成了但没出燃油：储罐 {tank} 毫升、精炼塔完成 {done} 次（{ProductionReason(tower)}）");
            }
            BuildingRecord shield = P.Bld(c, "shield");
            DefenseRecord dr = shield != null ? DefenseService.Find(s, shield.BuildingId) : null;
            return StepOutcome.Done($"防线全部建成：六座炮塔、护盾发生器（{dr?.ShieldState}）、屏障圈 {walls} 格；精炼塔完成 {done} 次、储罐燃油 {tank / 1000} 升（{c.StepElapsed:F0} 秒，3 倍速）；" +
                                   $"废料 {s.Scrap}；{P.PowerLine(s)}；暴露 {s.SignalExposure:F1}");
        }

        private static string ProductionReason(BuildingRecord b) =>
            b == null ? "没有精炼塔" : BuildingStatusService.Evaluate(St, b).Reason?.Replace("\n", " / ") ?? string.Empty;

        // ── 暴露 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickFirstPlanned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            RaidPlanRecord first = D.LivePlans(s).FirstOrDefault(p => p.FirstRaid);
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"暴露 {s.SignalExposure:F1}（用电需求 {s.PowerDemand:F0}，高功率每游戏小时 +{CampaignExposureLedger.HighPowerRatePerHour(s.PowerDemand):F2}）；已排定 {d?.RaidCount} 波；{RaidDirectorService.StatusLine(s, GameClock.Ticks)?.Replace("\n", " ")}");
            }
            if (first == null || first.State < RaidDirectorService.StateScheduled || s.SignalExposure < 35f)
            {
                return StepOutcome.Wait;
            }
            float added = CampaignExposureLedger.TotalAdded(s, ExposureSourceKind.HighPower);
            bool ok = first.Trigger == RaidCatalog.TriggerExposure && first.Level == 1 && first.ArrivalTick >= RaidDirectorService.GraceEndTick && added > 0f;
            c.Set("plan1", first.PlanId);
            return ok
                ? StepOutcome.Done($"暴露 {s.SignalExposure:F1}（高功率生产累计 +{added:F1}）：突袭导演排定第一次突袭 {D.PlanLine(first)}；预算 {first.Budget}（1 级 × {RaidCatalog.FirstRaidScale:0.##}）；抵达不早于开局宽限终点（第 {RaidDirectorService.GraceEndTick} 步）")
                : StepOutcome.Fail($"第一次突袭不对：{D.PlanLine(first)}；高功率累计 {added:F1}；宽限终点 {RaidDirectorService.GraceEndTick}");
        }

        private static StepOutcome TickExposureFalling(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "e0set")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "e0set"), 1);
                c.Set(FgjM3Common.SK(c, "e0"), s.SignalExposure.ToString("R", CultureInfo.InvariantCulture));
            }
            RaidPlanRecord p1 = RaidDirectorService.FindPlan(s, c.Get("plan1"));
            if (s.SignalExposure >= 20f)
            {
                return StepOutcome.Wait;
            }
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            bool kept = p1 != null && p1.State < RaidDirectorService.StateEnded && p1.Level == 1 && p1.FirstRaid;
            bool rearmed = (d.ThresholdFired & 1) == 0 && d.Level4NextTick < 0;
            // 关广播是负的暴露变化：分项累计记在 Removed（正的进 Added），这里取“关广播累计降了多少”，写成负数。
            float off = -(s.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>()).Where(t => t != null && t.Kind == ExposureSourceKind.TowerOff).Sum(t => t.Removed);
            return kept && rearmed && off < 0f
                ? StepOutcome.Done($"关广播后暴露 {c.Get(FgjM3Common.SK(c, "e0"))} → {s.SignalExposure:F1}（关闭信号塔广播累计 {off:F1}）：30 档重新武装、没有 4 级周期计时；第一次突袭照旧 {D.PlanLine(p1)}")
                : StepOutcome.Fail($"关广播后不对：第一次突袭 {D.PlanLine(p1)}；阈值位 {d.ThresholdFired}、4 级计时 {d.Level4NextTick}；关广播累计 {off:F1}");
        }

        // ── 收尾 ──────────────────────────────────────────────────────────────────────

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            c.Log(FgjM3Common.FrameReport("家园防线施工 / 三波突袭 / 远征段"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
