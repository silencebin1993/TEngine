using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Analysis;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEngine;
using UnityEngine.UIElements;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG5-E2E-01：M5 出口旅程 FGJ-M5（里程碑文档 FG-M5；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42）、全程正式输入（ADR-QA-017 三条通道），
    /// RTS 口径：左键选择、右键地面 = 移动 / 右键敌人 = 攻击 / 右键工作目标 = 派工，按钮只切状态 / 发起动作。
    ///
    /// 里程碑原文：把远征带回的加密固件送进解析台，破解后裸跑标记消失 → 研究一个节点，解锁新建筑 → 从远征回放拿到线索 → 模拟熔合，失败不消耗固件 →
    /// 正式熔合成功，配方进入配方书 → 在靶场投影新蓝图并接入测试 → 在情报面板看到下一次突袭的预报。
    /// 同一条旅程回补 FG5-RND-01～06 顺延到 M5 出口的延后项：建实验室 → 攒研究点 → 研究 → 建出新建筑（DEBT-FG5RND01-04）、研发加成来源行（01-07）、
    /// 新档技术数据供给（01-08）、建靶场 → 从蓝图编辑器送到靶场 → 接入投影 → 两次对比（03-05）、远征打出反应 → 线索 → 合成台 → 模拟 → 正式熔合 → 刻录台量产 → 装进机器（04-05）、
    /// 研究 → 放置 → 施工 → 出情报 → 点通知（05-10）、建陈列馆 → 机器阵亡 → 黑匣子分析 → 研究用上这份技术数据（06-03）、统计面板“研发”页（04-07 / 05-06 / 06-04）。
    /// 研发建筑位置全部按种子地形现找（<see cref="FgjM5Common.PlanSites"/>，B25）。夹具只在 FgjM5Common / 本类的 Apply*Fixture 方法里（ADR-QA-022 第 4 节）。
    /// </summary>
    public static partial class FgjM5Journey
    {
        public const string Id = "FGJ-M5";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private static CampaignState St => CampaignSession.Current;

        private static readonly string[] BatchA = { "recycler" };
        private static readonly string[] BatchB = { "lab", "gallery", "synth", "range", "burner" };

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("start_tech", "新档开局带来的技术数据够研究“研发树开放前开局就能建造的内容”（DEBT-FG5RND01-08）；研究门槛真实生效", 10, null, TickStartTech),
                S("workers", "记下开局两台机器（之后用到的进度夹具都单独成步并写明 DEBT，见 ADR-QA-022 第 5 节）", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库、发电机、信号塔，拆两处开局残骸拿废料（与 FGJ-M4 同一套正式输入）──
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

                // ── 研发建筑的位置（按种子地形现找）；先造回收站（拆废墟出废料）与发电机 2 ──
                S("plan", "看地形：规划回收站（压在废墟上）、仿真实验室、黑匣子陈列馆、电路合成台、靶场、固件刻录台（与仓库输出口之间一条带）、监听站的位置，电不够就加发电机 2", 30, null, TickPlan),
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m5_place1", "先放回收站与它到仓库输入口的带、发电机 2（建造栏点分类与条目、指着格子单击、按住左键分段拖）", 240, null, c => P.TickActions(c, "m5a", 40), retries: 2),
                S("build_close1", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("m5_built1", "机器取料施工：回收站与发电机 2 建成，回收站开始拆废墟出废料", 900, null, c => M.TickBuilt(c, BatchA, new[] { "r_recycler" }, true, "回收站与发电机 2")),
            };

            // ── 数据复原两条固件、刻印三枚芯片、过载装进信号核 1 号槽（第 6 步“接入测试核心固件”要用）──
            steps.AddRange(new[]
            {
                S("ana_open", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("ana_wet", "“数据复原”下拉选冷却液，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwWet), c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwWet), retries: 1),
                S("ana_wet_ok", "确认框点“复原”：冷却液解锁（技术数据 −复原费）", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, FgjM2Common.FwWet)),
                S("ana_shock", "下拉选电弧，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwShock), c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwShock), retries: 1),
                S("ana_shock_ok", "确认框点“复原”：电弧解锁（敌方加密，同时视为已破解）", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, FgjM2Common.FwShock)),
                S("ana_close", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print_wet", "刻印下拉选冷却液，点“刻印”：得到一枚冷却液芯片", 90, null, c => TickPrint(c, FgjM2Common.FwWet), retries: 1),
                S("sc_print_shock", "刻印下拉选电弧，点“刻印”：再得到一枚电弧芯片", 90, null, c => TickPrint(c, FgjM2Common.FwShock), retries: 1),
                S("sc_print_ov", "刻印下拉选过载，点“刻印”：得到一枚过载芯片（核心固件）", 90, null, c => TickPrint(c, FirmwareCatalog.FwOverloadId), retries: 1),
                S("sc_equip_ov", "点 1 号槽、点基元仓里的过载、点“装入”：过载进信号核 1 号槽", 20, null,
                    c => M.TickEquipChip(c, 0, () => St.PrimitiveChips?.LastOrDefault(x => x != null && x.CardDefId == FirmwareCatalog.FwOverloadId)?.PartId, "过载装进信号核"), retries: 2),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

                // ── 研发建筑：先给仓库输出口设好过滤（只送芯片基板），再放仿真实验室、陈列馆、合成台、靶场、刻录台与供料带 ──
                S("sub_fixture", "进度夹具：芯片基板放进仓库（芯片基板要一整条 M4 产线，FGJ-M4 已验证；DEBT-FG5E2E01-02）", 10, M.ApplySubstrateFixture, M.TickSubstrateFixture),
                S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("port_open", "建造模式里左键点仓库，通用面板上点“端口…”：打开端口面板", 20, null, c => FgjM3Common.TickPortOpen(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("port_sub", "仓库输出口的过滤下拉选“停止输出”（刻录台的供料带还没接、刻什么也没选：先不往外送，免得把芯片基板和别的物品堆到带上）", 20, null,
                    c => TickPortFilter(c, BeltPortService.FilterOff), retries: 1),
                S("port_close", "点“关闭”关闭端口面板", 10, c => FgjM1Journey.ClickUi(c, FgjM3Common.PortHost, "BeltPortClose"), TickPortClosed, retries: 1),
                S("m5_place2", "放仿真实验室、黑匣子陈列馆、电路合成台、靶场（8×8）、固件刻录台与仓库输出口到刻录台的带", 300, null, c => P.TickActions(c, "m5b", 40), retries: 2),
                S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),

                // ── 第 2 步前半：研究一个节点（监听站，研究完解锁新建筑）；顺带研究“采集优化 I”（研发加成来源行，DEBT-FG5RND01-07）──
                S("key_fixture", "进度夹具：监听阵列核放进核心保管库（首领掉落在 FG9-FAC-01；DEBT-FG5RND05-09）", 10, M.ApplyKeyFixture, M.TickKeyFixture),
                S("rt_open", "按研发树键（默认 K）打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("rt_gather", "点“工业”分支筛选，左键点“工业 · 采集优化 I”：加入研究队列（还没有实验室：状态写明要先建一座）", 15, null, c => M.TickQueueNode(c, M.NodeGather), retries: 2),
                S("rt_post", "点“信号”分支筛选，左键点“信号 · 监听站”：加入研究队列（关键材料已在保管库，研究不消耗它）", 15, null, c => M.TickQueueNode(c, M.NodePost), retries: 2),
                S("rt_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),

                // ── 远征要用的两台 ERC-003：一台装冷却液（浸湿）、一台装电弧（电击），远征里一起打同一个敌人会打出“短路”反应（线索的来源）──
                S("cb_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_wet", "固件槽 1 下拉选冷却液", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwWet), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwWet), retries: 1),
                S("cb_save", "点“保存”", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"), c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwWet, "verWet"), retries: 1),
                S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 2),
                S("fac_sub", "“缺材料时用废料代付”没开就点开（M5 家园没有零件产线，机器用废料按当量补）", 10, null, c => TickSubstitute(c, true), retries: 1),
                S("fac_wet", "点“生产 ERC-003”（冷却液版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verWet"), retries: 1),
                S("fac_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 2),
                S("cb_open2", "再点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick2", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_shock", "固件槽 1 下拉改选电弧", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwShock), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwShock), retries: 1),
                S("cb_save2", "点“保存”（新版本；已排的冷却液订单不受影响）", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"),
                    c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwShock, "verShock"), retries: 1),
                S("cb_close2", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open2", "左键点装配站", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 2),
                S("fac_shock", "点“生产 ERC-003”（电弧版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verShock"), retries: 1),
                S("fac_close2", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 2),
                S("wait_wet", "等冷却液机出厂（废料不够时等回收站拆出来）", 600, null, c => FgjM2Common.TickProduced(c, "verWet", "wetM")),
                S("wet_sel", "左键点冷却液机（停在装配站出口）", 20, c => FgjM1Journey.ClickMachine(c, "wetM"), c => FgjM1Journey.TickSelected(c, "wetM"), retries: 4),
                S("wet_out", "右键点旁边的空地：冷却液机驶出出口（右键地面 = 移动）", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "wetM"), retries: 3),
                S("wait_shock", "等电弧机出厂", 600, null, c => FgjM2Common.TickProduced(c, "verShock", "shockM")),
                S("shock_sel", "左键点电弧机", 20, c => FgjM1Journey.ClickMachine(c, "shockM"), c => FgjM1Journey.TickSelected(c, "shockM"), retries: 4),
                S("shock_out", "右键点旁边的空地：电弧机驶出出口", 30, null, c => FgjM3Journey.TickDriveOutOf(c, "shockM"), retries: 3),
                S("m5_built2", "机器取料施工（废料不够时等回收站拆出来）：五座研发建筑与刻录台供料带全部建成、接上电", 1500, null,
                    c => M.TickBuilt(c, BatchB, new[] { "r_burner" }, true, "研发建筑")),
            });
            steps.AddRange(ExpeditionSteps());
            steps.AddRange(LabSteps());
            steps.AddRange(LateSteps());
            return new JourneyDef
            {
                Id = Id,
                Title = "M5 出口：带回的加密固件送进解析台破解、裸跑标记消失 → 研究一个节点解锁新建筑 → 从远征拿到线索 → 模拟熔合失败不消耗固件 → 正式熔合成功进配方书 → 靶场投影新蓝图并接入测试 → 情报面板看到下一次突袭的预报",
                Seed = TestSeed,
                TotalTimeoutSeconds = 4200,
                OnFinish = Cleanup,
                Version = 2, // 修复轮：研发段末尾加了“按混合固件版蓝图出厂一台”四步（fac_open3～wait_mix）
                CheckpointAfter = new[] { "m5_built2", "evac_confirm", "build_close3" },
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

        // ── 开局技术数据（DEBT-FG5RND01-08）────────────────────────────────────────────

        internal static StepOutcome TickStartTech(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int start = ResearchService.StartTechData;
            List<ResearchNodeDef> legacy = ResearchService.LegacyOpenNodes();
            int points = ResearchService.LegacyOpenPoints();
            int cost = ResearchService.LegacyOpenTechCost();
            bool gate = ResearchGate.TreeAvailable && !BuildCatalog.IsUnlocked(s, "research:" + "logistics.splitter") && !ResearchService.IsCompleted(s, "logistics.splitter");
            c.SetInt("tech.start", s.TechData);
            bool ok = s.TechData == start && s.Research.TechStart == start && start >= cost && legacy.Count >= 10 && gate;
            string list = string.Join("、", legacy.Select(n => n.Name + " " + n.Cost));
            return ok
                ? StepOutcome.Done($"新档开局技术数据 {s.TechData}（research.start_tech_data，存档里记“开局带来 {s.Research.TechStart}”）≥ 研发树开放前开局可造内容 {legacy.Count} 个节点 {points} 研究点 × " +
                                   $"{ResearchService.TechPerMinute:0.#}/{ResearchService.PointsPerMinute:0.#} = {cost} 技术数据（{list}）；研究门槛真实生效（分流器要先研究）")
                : StepOutcome.Fail($"开局技术数据不对：库存 {s.TechData}、调参 {start}、存档开局 {s.Research.TechStart}、需要 {cost}（{legacy.Count} 个节点 {points} 点）；门槛生效 {gate}");
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
            var specs = new[]
            {
                new M.SiteSpec { Key = "lab", Type = M.Lab, MinR = 6 },
                new M.SiteSpec { Key = "gallery", Type = M.Gallery, MinR = 6 },
                new M.SiteSpec { Key = "synth", Type = M.Synth, MinR = 6 },
                new M.SiteSpec { Key = "range", Type = M.Range, MinR = 8 },
                new M.SiteSpec { Key = "post", Type = M.Post, MinR = 6 },
            };
            P.FactoryPlan plan = M.PlanSites(s, withRecycler: true, withBurner: true, specs, extraDemand: 0, out string why);
            if (plan == null)
            {
                return StepOutcome.Fail("研发建筑规划不出来：" + why);
            }
            M.SavePlan(c, plan);
            FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, true, out _, out _, out string outKey);
            c.Set("outKey", outKey);
            var first = new List<string>();
            foreach (GridCell g in plan.Generators)
            {
                first.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {M.Cell(g)}");
            }
            P.Placed rec = plan.B("recycler");
            first.Add(P.ActB(rec));
            first.Add(P.ActR(P.BeltT1, "path.r_recycler", plan.R("r_recycler").GoalDir, "回收站废料带 → 仓库输入口"));
            P.SetActions(c, "m5a", first);
            var second = new List<string>();
            foreach (string k in BatchB)
            {
                second.Add(P.ActB(plan.B(k)));
            }
            second.Add(P.ActR(P.BeltT1, "path.r_burner", plan.R("r_burner").GoalDir, "仓库输出口 → 固件刻录台（只送芯片基板）"));
            P.SetActions(c, "m5b", second);
            P.SetActions(c, "m5c", new[] { P.ActB(plan.B("post")) });
            return StepOutcome.Done($"规划（全部在归还核心配电范围里，不用要研究的 T2 电塔）：{M.Describe(plan, core)}；仓库输出口 {outKey}");
        }

        // ── 解析台：数据复原（结果核对不比废料——回收站同时在往家园送废料）──────────────────

        private static StepOutcome TickRestored(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int cost = FirmwareRestoreService.CostOf(fwId);
            bool unlocked = MechanicalContentUnlock.IsUnlocked(s, fwId);
            bool cracked = !FirmwareKinds.IsRaw(s, fwId);
            bool printable = SignalCoreService.PrintableFirmware(s).Contains(fwId);
            string result = AnalysisPanelUIToolkit.Instance?.ResultText ?? string.Empty;
            bool ok = !UiConfirmDialog.IsOpen && unlocked && cracked && printable && s.TechData == c.GetInt("tech0") - cost
                      && FirmwareRestoreService.RestoredCount == c.GetInt("restored0") + 1;
            if (!ok)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait
                    : StepOutcome.Fail($"复原“{M.FwName(fwId)}”后状态不对：解锁 {unlocked}、已破解 {cracked}、可刻印 {printable}、技术数据 {c.GetInt("tech0")} → {s.TechData}（应 −{cost}）；结果“{result}”");
            }
            return StepOutcome.Done($"点确认框的“复原”：“{result}”；技术数据 {c.GetInt("tech0")} → {s.TechData}（统计面板记作“数据复原”支出）；{M.FwName(fwId)} 已解锁、可刻印");
        }

        // ── 信号核刻印（结果核对不比废料，理由同上）──────────────────────────────────────

        /// <summary>
        /// 刻印一枚：顶栏废料不够一次刻印费时先等（回收站在拆废墟出废料，玩家同样等废料够了再点），够了在刻印下拉选中再点“刻印”，核对芯片进基元仓并被选中。
        /// </summary>
        internal static StepOutcome TickPrint(JourneyContext c, string fwId)
        {
            if (!FgjM3Common.Done(c, "print"))
            {
                JourneyCommon.ResumeIfAutoPaused(c);
                if (St.Scrap < SignalCoreService.FirmwareChipPrintScrap)
                {
                    if (!FgjM3Common.Done(c, "poor"))
                    {
                        FgjM3Common.Mark(c, "poor");
                        c.Log($"废料 {St.Scrap} 不够一次刻印（{SignalCoreService.FirmwareChipPrintScrap}），等回收站拆出废料");
                    }
                    return StepOutcome.Wait;
                }
                Print(c, fwId);
                FgjM3Common.Mark(c, "print");
                c.SetLong(FgjM3Common.SK(c, "printAt"), M.NowMs());
                return StepOutcome.Wait;
            }
            if (M.NowMs() - c.GetLong(FgjM3Common.SK(c, "printAt")) < 500)
            {
                return StepOutcome.Wait;
            }
            return TickPrinted(c, fwId);
        }

        private static void Print(JourneyContext c, string fwId)
        {
            DropdownField print = JourneyInput.FindUitk<DropdownField>(M.SignalHost, "SignalPrintChoice");
            string name = M.FwName(fwId);
            string choice = print?.choices?.FirstOrDefault(x => x.Contains(name));
            c.Set("printChoice", choice ?? string.Empty);
            c.SetInt("chips0." + fwId, M.ChipCount(fwId));
            if (print != null && choice != null)
            {
                print.value = choice;
                FgjM1Journey.ClickUi(c, M.SignalHost, "SignalPrint");
            }
        }

        private static StepOutcome TickPrinted(JourneyContext c, string fwId)
        {
            if (c.Get("printChoice").Length == 0)
            {
                return StepOutcome.Fail($"刻印下拉里没有“{M.FwName(fwId)}”");
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            PrimitiveChipRecord chip = PrimitiveInventory.Find(St, hud?.SelectedPartId);
            return chip != null && chip.CardDefId == fwId && M.ChipCount(fwId) == c.GetInt("chips0." + fwId) + 1
                ? StepOutcome.Done($"刻印一枚“{M.FwName(fwId)}”芯片（进基元仓、被选中），扣 {SignalCoreService.FirmwareChipPrintScrap} 废料")
                : StepOutcome.Retry($"刻印没成功（{hud?.FeedbackText}；{FgjM1Journey.UiFail(c)}）");
        }

        // ── 仓库输出口过滤 ────────────────────────────────────────────────────────────

        /// <summary>仓库输出口过滤改成芯片基板（选好刻录目标之后才开始送料）。</summary>
        internal static StepOutcome TickPortSubstrate(JourneyContext c) => TickPortFilter(c, ItemCatalog.Find(M.Substrate)?.BeltId ?? -1);

        internal static StepOutcome TickPortFilter(JourneyContext c, int filter)
        {
            string outKey = c.Get("outKey");
            if (!FgjM3Common.Done(c, "set"))
            {
                if (c.StepElapsed < 0.4)
                {
                    return StepOutcome.Wait;
                }
                if (!FgjM3Common.PickPortFilter(c, outKey, filter, out string why))
                {
                    return StepOutcome.Retry("选不了过滤：" + why);
                }
                FgjM3Common.Mark(c, "set");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord wh = FgjM3Common.Home(HomeValleyLayout.BuildingTypeWarehouse);
            BeltPortService.Binding bind = BeltPortService.Find(wh.BuildingId, outKey);
            int now = bind?.Record != null ? bind.Record.Filter : -99;
            return now == filter
                ? StepOutcome.Done($"仓库输出口过滤改成“{BeltPortService.FilterName(filter)}”（行里原来写“{c.Get(FgjM3Common.SK(c, "portRowText"))}”）")
                : StepOutcome.Retry($"下拉选“{BeltPortService.FilterName(filter)}”后端口过滤是 {now}");
        }

        private static StepOutcome TickPortClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (BeltPortPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("端口面板没关：" + FgjM1Journey.UiFail(c));
            }
            if (ProductionPanelUIToolkit.IsOpen)
            {
                if (!FgjM3Common.Once(c, "pp", () => JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose")))
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            return FgjM3Common.BuildOpen ? StepOutcome.Done("端口面板关闭（通用面板也关了），建造模式还开着") : StepOutcome.Retry("建造模式被关掉了");
        }

        // ── 装配站代付开关 ────────────────────────────────────────────────────────────

        private static StepOutcome TickSubstitute(JourneyContext c, bool on)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (AssemblyMaterials.ScrapSubstitute(St) == on)
            {
                if (FgjM3Common.Done(c, "tog") && FgjM3Common.SinceMs(c, "tog") < 600)
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Done($"“缺材料时用废料代付”已{(on ? "开" : "关")}（“{JourneyInput.FindUitk<Label>(P.FactoryHost, "SubstituteNote")?.text}”）");
            }
            Toggle t = JourneyInput.FindUitk<Toggle>(P.FactoryHost, "SubstituteToggle");
            if (!FgjM3Common.Once(c, "tog", () => JourneyInput.ClickElement(t)))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tog") < 1200 ? StepOutcome.Wait : StepOutcome.Retry($"点了代付开关后它还{(on ? "关" : "开")}着：" + JourneyInput.LastUiFailure);
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            c.Log(FgjM3Common.FrameReport("研发建筑施工 / 远征 / 研发段"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
