using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.Campaign.Signal;
using BinGames.Sim.Nav;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG3-E2E-01：M3 出口旅程 FGJ-M3（里程碑文档 FG-M3；00 契约 IC-REQ-021）。从主菜单出发、全程正式输入（ADR-QA-017 三条通道），
    /// 在新游戏设置里**输入种子**开新档（42，FgWorldGenSelfCheck 基准里有它）；RTS 口径：左键选择、右键地面 = 移动 / 右键工作目标 = 派工，按钮只切状态。
    ///
    /// 里程碑原文：用输入的种子开新档，确认起始区保证 → 搭建“矿脉 → 精炼炉 → 零件工坊 → 仓库”的产线 → 人为制造一次堵塞，用诊断找到根源并修好 →
    /// 把整条产线存进布局库，在另一处放一份 → 撤销 → 升级传送带 → 存读档 → 远征一次，回来确认产量与后台一致。
    ///
    /// M3 没有采集 / 加工 / 制造建筑（提取钻、精炼炉、零件工坊在 FG4-ECO-02 / 03），产线用 M3 已有的正式物流件搭（ADR-QA-019 第 2 节，DEBT-FG3E2E01-01 → FGJ-M4 回补）：
    /// - 物品线：修好的仓库输出口 → 按格网规则现找的 T1 传送带路线（多段拖拽、自动转角）→ 归还核心输入口（家园废料循环，吞吐 = 核心输入口累计收货）；
    /// - 流体线：起始区保证的水源 → 泵 → 3 格管线 → 储罐（产量 = 泵累计抽取 / 储罐存量）。
    /// 堵塞 = 建造模式里把物品线中间一格误按旋转键原地反转；诊断 = “为什么不工作”追到那一格、点原因镜头飞过去；修好 = 再反转回来。
    /// 布局库 = 复制整条抽水线存进布局库，在按种子地形另找的一片水源上从布局库放一份（旋转对准）；撤销 / 重做这次放置；升级 = 物品线 T1 → T2（吞吐 60 → 120）；
    /// 存读档 = 暂停菜单“保存并返回主菜单”→ 读取，产线逐项一致；远征一次 = 三台出征破碎都市、家园无人观察时产线照常运行，回家核对产量与观察时的速率、与设计速率一致。
    /// </summary>
    public static class FgjM3Journey
    {
        public const string Id = "FGJ-M3";

        /// <summary>固定测试种子：与 FGJ-M0 / M1 / M2 同一颗（FgWorldGenSelfCheck 基准里有它）；在新游戏设置的种子框里输入。</summary>
        public const int TestSeed = FgjM0Journey.TestSeed;

        internal const string LayoutName = "抽水线（FGJ-M3）";

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M3 出口：输入种子开新档确认起始区保证 → 搭物品线与抽水线 → 误转一格造成堵塞、诊断找到根源修好 → 抽水线存进布局库在另一片水源放一份 → 撤销 / 重做 → 升级传送带 → 存读档 → 远征一次回家核对产量与后台一致",
            Seed = TestSeed,
            TotalTimeoutSeconds = 1500,
            OnFinish = Cleanup,
            // FG-TOOL-01：旅程断点。版本号在步骤语义 / 顺序变化时加 1；断点只登记界面中性（建造模式已关、没有面板开着、在家园）的步骤。
            // 迭代：bash tools/unity-journey.sh FGJ-M3 --from rate_t1；交付验收仍从主菜单完整跑。
            Version = 1,
            CheckpointAfter = new[] { "fac_close", "rate_t1", "build_close2", "loaded" },
            // 布局库在旅程临时目录里（不是存档的一部分）：断点把它连同存档一起放回后，重新载入。
            OnCheckpointRestored = c => LayoutLibrary.Reload(),
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "新游戏设置：在种子框输入“42”（提示行更新、分享短码跟着变）→ 点“开始” → 进入归还谷地", 150, null, TickNewGameTyped),
                S("seed", "输入的种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台工程机（不加任何进度夹具）", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库（物品线的起点）与发电机（装配站要电），拆残骸拿废料，修信号塔（出征要用），装配站造第三台机器（出征要三台）──
                S("sel_a", "左键点一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep1", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("sel_a2", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_tw", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("sel_b2", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage", "右键点开局残骸（情境命令：拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完（废料 +60）", 180, null, FgjM2Common.TickSalvageDone),
                S("wait_rep2", "等信号塔修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeSignalTower)),
                S("fac_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_produce", "点“生产 ERC-003”（现役蓝图）：出征要三台机器", 10, c => { c.SetInt("verA", FgjM1Journey.Erc003Record()?.ActiveVersion ?? 1); FgjM2Common.ClickProduce(c); },
                    c => FgjM2Common.TickProduceQueued(c, "verA"), retries: 1),
                S("fac_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),

                // ── 起始区保证在画面上：建造模式的地形叠加层里有水源，泵的放置预览写“这台泵抽水”；按格网规则现找路线与抽水线位置（B25）──
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("plan", "看地形：起始区 24 格内的水源、仓库输出口到归还核心输入口之间能铺的地面（按格网规则现找，不写死坐标）", 10, null, TickPlan),
                S("pump_pick", "建造栏“物流”分类点“泵”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolPump), retries: 2),
                S("pump_place", "鼠标先指着旁边的空地：状态行写“泵要放在水源或油井上”；再指着水源：预览合法，左键放下泵的虚影", 25, null, TickPumpPlaced, retries: 3),
                S("pipe_pick", "点“管线 T1”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolPipeT1), retries: 2),
                S("pipe_lay", "从泵旁按住左键拖 3 格管线（拖的时候写长度 / 成本 / 接入哪种流体）", 20, null, TickPipeLaid, retries: 3),
                S("tank_pick", "点“储罐”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolTank), retries: 2),
                S("tank_place", "在管线末端左键放下储罐的虚影", 20, null, TickTankPlaced, retries: 3),
                S("belt_pick", "点“传送带 T1”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolBeltT1), retries: 2),
                S("hb_put", "选中传送带 T1 后左键点底部快捷栏的空格子：放进快捷栏（手上仍是传送带 T1）", 15, null, TickHotbarPut, retries: 3),
                S("belt_lay", "从仓库输出口外侧一格起，一段一段按住左键拖到归还核心输入口外侧一格（自动转角；单格段先按旋转键转向再单击）", 90, null,
                    c => FgjM3Common.TickLayRoute(c, "route", c.GetInt("goalDir"), FgjM3Common.ToolBeltT1), retries: 2),
                S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("built", "机器取料施工：虚影逐格建成；仓库输出口开始往外推废料、核心输入口收货；泵抽水、储罐进水", 420, null, TickLineBuilt),
                S("rate_t1", "看物品线跑起来：核心输入口实测每分钟约 60 件（T1 满载）；悬停写设计 60、实测读数不超过设计（悬停的统计窗口 60 游戏秒含刚开跑的一段，会偏低）", 60, null,
                    c => TickRate(c, 60, "rateT1")),

                // ── 物品线把家园废料都推上了传送带（在途不算库存，FG-GAP-090）：后面的施工要材料，拆第二处残骸 ──
                S("salv2_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸（情境命令：拆解）", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完（废料约 +60）", 180, null, FgjM3Common.TickWreck2Done),

                // ── 人为制造一次堵塞 → 诊断找到根源 → 修好 ──
                // ── 建造模式关着时，快捷栏与入口按钮排在 HUD 层（第 1 轮审查：此前没有自动测试在这一层真实点过它们）──
                S("hb_closed", "建造模式关着时左键点底部快捷栏里的“传送带 T1”：建造模式打开并选中它（快捷栏在 HUD 层，没被常驻界面挡住）", 20, null, TickHotbarClosed, retries: 3),
                S("hb_close", "按建造键关闭建造模式（手上的条目随之放下）", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("jam_build", "左键点左侧“建造”入口按钮打开建造模式（入口与快捷栏同在 HUD 层；打开时手上不拿条目）", 20, null, TickEntryOpen, retries: 3),
                S("jam", "等物品线中段有货的一格，鼠标指着它误按旋转键：这一格原地反转（状态行写“现在朝…”），格上的货跟着掉头", 60, null, TickJam, retries: 2),
                S("jam_stall", "产线停了：核心输入口不再收货，仓库输出口推不出去", 60, null, TickStalled),
                S("diag_open", "按“为什么不工作”键（默认 Ctrl+O）：列出仓库“输出口推不出去 → 下游 → 根源”", 30, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, true),
                    TickDiagnosis, retries: 1),
                S("diag_click", "点根源那一条：镜头飞到那一格（建造模式不退出）", 15, null, TickDiagLocate, retries: 2),
                S("fix", "指着误转的那一格再按旋转键：转回原来的方向", 20, null, TickFix, retries: 2),
                S("flow_back", "修好了：核心输入口重新收货，“为什么不工作”里仓库那一条消失", 60, null, TickFlowBack),
                S("diag_close", "再按 Ctrl+O 关闭“为什么不工作”", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : DiagnosisPanelUIToolkit.IsOpen ? StepOutcome.Retry("面板没关") : StepOutcome.Done("关闭“为什么不工作”"), retries: 1),

                // ── 整条抽水线存进布局库，在另一处放一份 ──
                S("copy_key", "按复制键（默认 Ctrl+C）进入复制模式", 10, c => JourneyInput.PressAction(GameActionId.Copy),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : FgjM3Common.Mode.CopyMode ? StepOutcome.Done($"进入复制模式（建造栏写“{BuildModeHudUIToolkit.Instance?.ModeText}”）") : StepOutcome.Retry("没有进入复制模式"), retries: 1),
                S("copy_box", "按住左键拖框框住泵、管线与储罐：松开后复制下来、直接进入粘贴", 20, null, TickCopied, retries: 2),
                S("paste_exit", "右键退出粘贴", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.PasteMode, "退出粘贴，建造模式还开着"), retries: 2),
                S("lib_open", "按布局库键（默认 Ctrl+B）打开布局库", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, true),
                    c => c.StepElapsed < 0.8 ? StepOutcome.Wait : LayoutLibraryPanelUIToolkit.IsOpen ? StepOutcome.Done($"布局库打开（{LayoutLibrary.Count} 个布局；“{LayoutLibraryPanelUIToolkit.Instance?.EmptyText}”）") : StepOutcome.Retry("布局库没打开"), retries: 1),
                S("lib_save", "名字框输入“抽水线（FGJ-M3）”，点“保存剪贴板”：布局库多一行（带缩略图，写进玩家配置目录）", 15, null, TickLibSaved, retries: 1),
                S("lib_close", "再按布局库键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, false),
                    c => c.StepElapsed < 0.6 ? StepOutcome.Wait : !LayoutLibraryPanelUIToolkit.IsOpen && FgjM3Common.BuildOpen ? StepOutcome.Done("布局库关闭，建造模式还开着") : StepOutcome.Retry("布局库没关"), retries: 1),
                S("site2", "按种子地形另找一片水源（离第一条线 12 格外），方向键平移镜头过去", 60, null, TickSite2),
                S("lib_open2", "按布局库键打开布局库", 10, c => JourneyInput.PressToggleTo(GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, true),
                    c => c.StepElapsed < 0.8 ? StepOutcome.Wait : LayoutLibraryPanelUIToolkit.IsOpen ? StepOutcome.Done("布局库打开") : StepOutcome.Retry("布局库没打开"), retries: 1),
                S("lib_place", "点“抽水线（FGJ-M3）”那一行的“放置”：面板关掉，布局跟着鼠标", 10, null, TickLibPlace, retries: 1),
                S("paste_aim", "按旋转键把布局转到这片水源的方向，鼠标移过去让泵落在水上：预览 5 件都能放", 30, null, TickPasteAim, retries: 2),
                S("paste_click", "左键放下：5 件虚影，整次放置是撤销栈里的一步", 15, null, TickPasted, retries: 2),
                S("paste_exit2", "右键退出粘贴", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.PasteMode, "退出粘贴"), retries: 2),

                // ── 撤销 / 重做 ──
                S("undo", "按撤销键（默认 Ctrl+Z）：刚放下的 5 件虚影取消，状态行写“已撤销”", 10, c => JourneyInput.PressAction(GameActionId.Undo), TickUndone, retries: 1),
                S("redo", "按重做键（默认 Ctrl+Y）：5 件虚影放回原处", 10, c => JourneyInput.PressAction(GameActionId.Redo), TickRedone, retries: 1),
                // ── 物品线把家园库存都推上了传送带：第二条线的虚影在等材料，原因写明“另有 N 件在传送带上”与办法 → 端口面板把仓库输出改成“停止输出” ──
                S("copy_wait", "第二条抽水线的虚影开始等材料（家园库存都被仓库输出口推上了传送带）", 90, null, TickWaitingMaterials),
                S("cq_open", "按施工队列键（默认 Alt+B）：那一行写“等待材料……另有 N 件在传送带上……端口面板停止输出”", 10,
                    c => JourneyInput.PressToggleTo(GameActionId.ConstructionQueue, () => ConstructionQueuePanelUIToolkit.IsOpen, true), TickQueueSaysBelts, retries: 1),
                S("cq_close", "再按施工队列键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.ConstructionQueue, () => ConstructionQueuePanelUIToolkit.IsOpen, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : ConstructionQueuePanelUIToolkit.IsOpen ? StepOutcome.Retry("施工队列没关") : StepOutcome.Done("关闭施工队列（建造模式还开着）"), retries: 1),
                S("port_open", "建造模式里左键点仓库：打开端口面板", 15, null, c => FgjM3Common.TickPortOpen(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("port_stop", "仓库输出口的过滤下拉选“停止输出”：带上的废料陆续送进核心，库存回升", 90, null, c => TickPortFilter(c, BeltPortService.FilterOff), retries: 1),
                S("port_close", "点“关闭”关闭端口面板", 10, c => FgjM1Journey.ClickUi(c, FgjM3Common.PortHost, "BeltPortClose"),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !BeltPortPanelUIToolkit.IsOpen && FgjM3Common.BuildOpen ? StepOutcome.Done("端口面板关闭，建造模式还开着") : StepOutcome.Retry("端口面板没关：" + FgjM1Journey.UiFail(c)), retries: 1),
                S("copy_built", "机器把第二条抽水线建成：第二台泵开始抽水", 300, null, TickCopyBuilt),

                // ── 升级传送带 ──
                S("up_key", "按升级键（默认 U）进入升级规划", 10, c => JourneyInput.PressAction(GameActionId.UpgradePlan),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : FgjM3Common.Mode.UpgradeMode ? StepOutcome.Done($"进入升级规划（“{BuildModeHudUIToolkit.Instance?.ModeText}”）") : StepOutcome.Retry("没有进入升级规划"), retries: 1),
                S("up_boxes", "沿物品线一段一段拖框：每格生成 T1 → T2 升级施工（差额 1 废料 / 格）", 60, null, TickUpgradeBoxes, retries: 1),
                S("up_exit", "右键退出升级规划", 10, null, c => TickRightClickExit(c, () => !FgjM3Common.Mode.UpgradeMode, "退出升级规划"), retries: 2),
                S("up_wait", "机器送差额材料、逐格升完：整条物品线 T2", 300, null, TickUpgraded),
                S("port_open2", "左键点仓库打开端口面板", 15, null, c => FgjM3Common.TickPortOpen(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("port_all", "输出口改回“全部可存物品”：物品线重新跑起来", 30, null, c => TickPortFilter(c, BeltPortService.FilterAll), retries: 1),
                S("port_close2", "点“关闭”关闭端口面板", 10, c => FgjM1Journey.ClickUi(c, FgjM3Common.PortHost, "BeltPortClose"),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !BeltPortPanelUIToolkit.IsOpen ? StepOutcome.Done("端口面板关闭") : StepOutcome.Retry("端口面板没关：" + FgjM1Journey.UiFail(c)), retries: 1),
                S("rate_t2", "T2 物品线跑起来：指着传送带，建造栏状态行写设计吞吐 120；实测不低于升级前（家园废料总量有限，带上压不满时达不到 120）", 150, null, TickRateT2),
                S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),

                // ── 存读档 ──
                S("save_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("save_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("menu_back", "回到主菜单：存档里的建筑、规划、撤销栈与存档那一刻一致", 60, null, TickMenuAfterSave),
                S("load_list", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("loaded", "读档进入家园：传送带内核状态、等级、泵与储罐读数逐项一致；布局库仍在；产线照常运转", 120, null, TickLoaded),

                // ── 远征一次，回家核对产量与后台一致 ──
                S("speed3b", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("m3_wait", "第三台机器（ERC-003）出厂", 300, null, c => FgjM2Common.TickProduced(c, "verA", "m3")),
                S("m3_sel", "左键点新机", 20, c => FgjM1Journey.ClickMachine(c, "m3"), c => FgjM1Journey.TickSelected(c, "m3"), retries: 4),
                S("m3_out", "右键点旁边的空地：新机驶出装配站出口（右键地面 = 移动）", 30, null, TickDriveOut, retries: 3),
                S("prep_open", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                    FgjM1Journey.TickPrepOpen, retries: 2),
                S("prep_pick", "勾选出征名单（三台）", 20, null, FgjM3Common.TickRosterAny),
                S("prep_depart", "点“出发”：镜头飞到破碎都市，家园没人看着", 30, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), TickDeparted, retries: 1),
                S("away", "在破碎都市待一会儿（60 游戏秒以上），这期间家园一直没人观察", 120, null, TickAway),
                S("uplink", "机器列表点一台远征机器：接入", 25, c => FgjM1Journey.ClickMachineList(c, c.GetInt("other")), TickUplinked, retries: 3),
                S("evac_hold", "开到撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3),
                S("evac_confirm", "点“确认撤离”：远征队返回家园", 30, c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"), FgjM1Journey.TickReturnedHome, retries: 1),
                S("bg_check", "回到家园核对：离家期间物品线收货 = 观察时实测速率 × 时间 = 设计速率 × 时间；两台泵的抽水量与储罐存量按 300 升 / 分钟对得上；悬停读数一致", 30, null, TickBackgroundConsistent),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        // ── 进入游戏：输入种子 ─────────────────────────────────────────────────────────

        private static StepOutcome TickPlay(JourneyContext c)
        {
            StepOutcome o = JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                // 这条旅程要在新游戏设置里输入种子：不用测试种子覆盖（开局的种子框是随机种子，旅程像玩家一样改成 42）。
                CampaignRandomService.SeedOverrideForTests = null;
                IsolateLayoutLibrary(c);
            });
            return o.Status == JourneyStepStatus.Done ? StepOutcome.Done(o.Message.Replace($"新建战役的种子固定为 {TestSeed}", "新建战役不固定种子（在新游戏设置里输入）") + "；布局库改到临时目录") : o;
        }

        /// <summary>布局库写到本次的临时目录（不碰这台机器上玩家真实的布局库）。</summary>
        internal static void IsolateLayoutLibrary(JourneyContext c)
        {
            string dir = Path.Combine(c.Get("saves") ?? Path.GetTempPath(), "layouts");
            Directory.CreateDirectory(dir);
            LayoutLibrary.DirectoryOverrideForTests = dir;
            LayoutLibrary.Reload();
        }

        internal static StepOutcome TickNewGameTyped(JourneyContext c)
        {
            if (NewGamePanelUIToolkit.IsOpen)
            {
                long seen = c.GetLong("ngSeen");
                if (seen <= 0)
                {
                    c.SetLong("ngSeen", Math.Max(1, FgjM3Common.NowMs()));
                    return StepOutcome.Wait;
                }
                if (FgjM3Common.NowMs() - seen < 800)
                {
                    return StepOutcome.Wait;
                }
                NewGamePanelUIToolkit panel = NewGamePanelUIToolkit.Instance;
                TextField field = JourneyInput.FindUitk<TextField>("[NewGameHost]", "NewGameSeed");
                if (c.GetInt("ngTyped") == 0)
                {
                    if (field == null || !JourneyInput.IsClickable(field))
                    {
                        return StepOutcome.Retry("新游戏设置里找不到能点的种子框");
                    }
                    c.Set("ngRandom", field.value);
                    // 像玩家一样：点种子框（指针事件落在框上），把里面的随机种子改成 42（与在框里打字同一个值变化回调）。
                    if (!JourneyInput.ClickElement(field))
                    {
                        return StepOutcome.Retry("点不到种子框：" + JourneyInput.LastUiFailure);
                    }
                    field.value = TestSeed.ToString(CultureInfo.InvariantCulture);
                    c.SetInt("ngTyped", 1);
                    c.SetLong("ngTypedAt", FgjM3Common.NowMs());
                    return StepOutcome.Wait;
                }
                if (c.GetInt("ngTyped") == 1)
                {
                    if (FgjM3Common.NowMs() - c.GetLong("ngTypedAt") < 500)
                    {
                        return StepOutcome.Wait;
                    }
                    string hint = panel?.SeedHintText ?? string.Empty;
                    string share = JourneyInput.FindUitk<TextField>("[NewGameHost]", "NewGameShareCode")?.value ?? string.Empty;
                    bool parsed = panel != null && panel.TryCurrentSeed(out int seed, out bool fromText) && seed == TestSeed && !fromText;
                    if (!parsed || hint != GameText.Get("ui.newgame.seed_hint") || share.Length == 0)
                    {
                        return StepOutcome.Fail($"输入“{TestSeed}”后：解析 {parsed}、提示行“{hint}”、分享短码“{share}”");
                    }
                    c.Log($"种子框原来是随机种子 {c.Get("ngRandom")}；输入“{TestSeed}”：提示行“{hint}”，分享短码 {share}");
                    if (!JourneyInput.ClickUitk("[NewGameHost]", "NewGameStart"))
                    {
                        return StepOutcome.Retry("点不到“开始”：" + JourneyInput.LastUiFailure);
                    }
                    c.SetInt("ngTyped", 2);
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            return JourneyCommon.TickNewGame(c);
        }

        // ── 看地形、定路线与抽水线 ─────────────────────────────────────────────────────

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            if (!FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeWarehouse, true, out GridCell start, out int outDir, out string outKey)
                || !FgjM3Common.PortBeltCell(HomeValleyLayout.BuildingTypeCore, false, out GridCell goal, out int inFace, out string inKey))
            {
                return StepOutcome.Fail("找不到仓库输出口或归还核心输入口");
            }
            int goalDir = (inFace + 2) & 3; // 输入口的带要朝着建筑
            HashSet<long> avoid = FgjM3Common.AllPortCells(s);
            List<GridCell> route = FgjM3Common.PlanRoute(s, start, goal, goalDir, (outDir + 2) & 3, avoid, 10);
            if (route == null)
            {
                return StepOutcome.Fail($"仓库输出口外侧 {FgjM3Common.Cell(start)} 到核心输入口外侧 {FgjM3Common.Cell(goal)} 之间按格网规则找不到能铺的路线");
            }
            var busy = new HashSet<long>(route.Select(FgjM3Common.Key));
            foreach (GridCell r in route)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        busy.Add(FgjM3Common.Key(new GridCell(r.X + dx, r.Y + dy)));
                    }
                }
            }
            StartGuaranteeReport rep = WorldGenService.PlanFor(s)?.StartReport;
            StartGuaranteeItem waterItem = rep?.Items.FirstOrDefault(i => i.Terrain == "water");
            int radius = waterItem?.Radius ?? 24;
            if (!FgjM3Common.FindWaterLine(s, core, 3, radius, busy, out GridCell pump, out int wdir))
            {
                return StepOutcome.Fail($"起始区保证的 {radius} 格内找不到能放“泵 + 3 格管线 + 储罐”的水源");
            }
            c.Set("route", FgjM3Common.EncodeCells(route));
            c.SetInt("goalDir", goalDir);
            c.Set("outKey", outKey);
            c.Set("inKey", inKey);
            FgjM3Common.SetCell(c, "pump1", pump);
            c.SetInt("wdir1", wdir);
            int runs = FgjM3Common.Runs(route, goalDir).Count;
            float wd = Vector2.Distance(FgjM3Common.Ground(pump), FgjM3Common.Ground(core));
            string items = rep == null ? "（无报告）" : string.Join("、", rep.Items.Select(i => $"{GameText.Get(i.NameKey)} {i.Radius} 格内{(i.Satisfied ? "有" : "没有")}{(i.Stamped ? "（局部重生成）" : string.Empty)}"));
            return StepOutcome.Done($"起始区保证：{rep?.FlatRadius} 格内悬崖 {rep?.CliffsInFlat} 格、污染最高 {rep?.MaxPollutionInFlat}；{items}；" +
                                    $"离核心 {wd:F1} 格有水源 {FgjM3Common.Cell(pump)}（抽水线朝{FgjM3Common.DirName(wdir)}）；物品线路线 {route.Count} 格、{runs} 段（仓库输出口外侧 {FgjM3Common.Cell(start)} → 核心输入口外侧 {FgjM3Common.Cell(goal)}，最后一格朝{FgjM3Common.DirName(goalDir)}）");
        }

        private static GridCell[] Line1(JourneyContext c) => FgjM3Common.WaterLine(FgjM3Common.GetCell(c, "pump1"), c.GetInt("wdir1"));

        private static GridCell[] Line2(JourneyContext c) => FgjM3Common.WaterLine(FgjM3Common.GetCell(c, "pump2"), c.GetInt("wdir2"));

        private static List<GridCell> Route(JourneyContext c) => FgjM3Common.DecodeCells(c.Get("route"));

        // ── 放泵、管线、储罐 ─────────────────────────────────────────────────────────

        private static StepOutcome TickPumpPlaced(JourneyContext c)
        {
            GridCell[] line = Line1(c);
            GridCell pump = line[0];
            GridCell dry = line[1]; // 抽水线的第一格管线：不是水源
            // ① 指着不是水源的格子：状态行写原因（B06）。
            if (!FgjM3Common.Once(c, "dry", () => FgjM3Common.TryHover(FgjM3Common.Ground(dry))))
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "drySeen"))
            {
                string st = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
                if (FgjM3Common.Mode.HoverCell != dry || !st.Contains("泵要放在水源或油井上"))
                {
                    return FgjM3Common.SinceMs(c, "dry") < 2500 ? StepOutcome.Wait : StepOutcome.Retry($"指着不是水源的 {FgjM3Common.Cell(dry)} 时状态行没写原因（“{st}”）");
                }
                c.Set("pumpDry", st.Replace("\n", " "));
                FgjM3Common.Mark(c, "drySeen");
            }
            // ② 指着水源：预览合法（没有原因行）。
            if (!FgjM3Common.Once(c, "hover", () => FgjM3Common.TryHover(FgjM3Common.Ground(pump))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "hover") < 500)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "seen"))
            {
                string status = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
                BeltPathPlan pv = FgjM3Common.Mode.ToolPreview;
                if (FgjM3Common.Mode.HoverCell != pump || pv == null || !pv.Ok || status.Contains("泵要放在"))
                {
                    return FgjM3Common.SinceMs(c, "hover") < 2000 ? StepOutcome.Wait : StepOutcome.Retry($"指着水源 {FgjM3Common.Cell(pump)} 时泵的预览不能放（{status}）");
                }
                FgjM3Common.Mark(c, "seen");
            }
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(pump))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return HomeValleyConstruction.TryFindPlannedCell(St, pump, out _, out _)
                ? StepOutcome.Done($"指着空地 {FgjM3Common.Cell(dry)}：状态行“{c.Get("pumpDry")}”；指着水源 {FgjM3Common.Cell(pump)}：预览合法；左键放下泵的虚影（放下不扣料，废料 {St.Scrap}）")
                : StepOutcome.Retry($"点了水源后没有泵的虚影（状态行“{FgjM3Common.Mode.StatusText}”）");
        }

        private static StepOutcome TickPipeLaid(JourneyContext c)
        {
            GridCell[] line = Line1(c);
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(line[1]), FgjM3Common.Ground(line[FgjM3Common.PipeCells]))))
            {
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (info.Length > 0)
            {
                c.Set("pipeInfo", info.Replace("\n", " "));
            }
            if (FgjM3Common.SinceMs(c, "drag") < 700)
            {
                return StepOutcome.Wait;
            }
            int planned = Enumerable.Range(1, FgjM3Common.PipeCells).Count(i => HomeValleyConstruction.TryFindPlannedCell(St, line[i], out _, out _));
            return planned == FgjM3Common.PipeCells
                ? StepOutcome.Done($"拖出 {planned} 格管线虚影（拖的时候写“{c.Get("pipeInfo")}”）")
                : StepOutcome.Retry($"拖完只有 {planned}/{FgjM3Common.PipeCells} 格管线虚影（状态行“{FgjM3Common.Mode.StatusText}”）");
        }

        private static StepOutcome TickTankPlaced(JourneyContext c)
        {
            GridCell tank = Line1(c)[FgjM3Common.PipeCells + 1];
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(tank))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return HomeValleyConstruction.TryFindPlannedCell(St, tank, out _, out _)
                ? StepOutcome.Done($"左键放下储罐的虚影（{FgjM3Common.Cell(tank)}）")
                : StepOutcome.Retry($"没有储罐的虚影（状态行“{FgjM3Common.Mode.StatusText}”）");
        }

        // ── 建成与速率 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickLineBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> route = Route(c);
            GridCell[] line = Line1(c);
            if (!FgjM3Common.AllBuilt(route, line, out int unbuilt))
            {
                if (c.StepElapsed > 30 && c.GetInt("builtLog") < (int)(c.StepElapsed / 30))
                {
                    c.SetInt("builtLog", (int)(c.StepElapsed / 30));
                    var q = new List<HomeValleyConstruction.QueueEntry>();
                    HomeValleyConstruction.CollectQueue(St, q);
                    c.Log($"还有 {unbuilt} 格没建成；废料 {St.Scrap}；施工队列 {q.Count} 项");
                }
                return StepOutcome.Wait;
            }
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out bool inOn);
            long pushed = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeWarehouse, c.Get("outKey"), out bool outOn);
            long pumped = FgjM3Common.PumpTotalMl(line[0]);
            long tank = FgjM3Common.TankMl(line[line.Length - 1]);
            if (!inOn || !outOn || got <= 0 || pumped <= 0 || tank <= 0)
            {
                return c.StepElapsed > 400 ? StepOutcome.Fail($"建成后没跑起来：仓库输出口接上 {outOn}（推上 {pushed}）、核心输入口接上 {inOn}（收货 {got}）、泵 {pumped} 毫升、储罐 {tank} 毫升") : StepOutcome.Wait;
            }
            return StepOutcome.Done($"全部建成（{c.StepElapsed:F0} 秒，3 倍速）：仓库输出口推上 {pushed} 件、核心输入口收货 {got} 件；泵累计 {pumped / 1000.0:F1} 升、储罐 {tank / 1000.0:F1} 升；废料 {St.Scrap}");
        }

        /// <summary>
        /// 实测物品线吞吐：核心输入口在至少 20 游戏秒里的收货件数 / 分钟，与设计吞吐比（±8%）。悬停摘要核对设计吞吐，并核对悬停的实测读数
        /// 在统计中、且不超过设计（悬停的统计窗口是 60 游戏秒，含刚开跑那一段，此时会偏低——上一版证据里核心实测 60、悬停实测 48 正是这个原因；
        /// 第 1 轮审查：步骤名原来写“悬停读数与之一致”，实际只核对了设计值，已改成照实写）。
        /// </summary>
        private static StepOutcome TickRate(JourneyContext c, int expectPerMin, string key)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out bool on);
            if (!FgjM3Common.Done(c, "t0"))
            {
                if (c.StepElapsed < 1.0)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.SetLong(FgjM3Common.SK(c, "got0"), got);
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            double dt = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            if (dt < 20.0)
            {
                return StepOutcome.Wait;
            }
            double perMin = (got - c.GetLong(FgjM3Common.SK(c, "got0"))) / dt * 60.0;
            List<GridCell> route = Route(c);
            GridCell probe = route[route.Count / 2];
            string hover = BeltNetworkService.DescribeCell(probe) ?? string.Empty;
            c.Set(key, perMin.ToString("R", CultureInfo.InvariantCulture));
            bool rated = FgjM3Common.BeltInfo(probe, out BeltCellInfo info) && info.RatedItemsPerMinute == expectPerMin && hover.Contains($"（设计 {expectPerMin}）");
            bool hoverMeasured = info.WindowSeconds > 0f && info.ThroughputPerMinute <= expectPerMin * 1.08f;
            return on && Math.Abs(perMin - expectPerMin) <= expectPerMin * 0.08 && rated && hoverMeasured
                ? StepOutcome.Done($"{dt:F0} 游戏秒里核心输入口收货 {perMin:F1} 件 / 分钟（设计 {expectPerMin}）；悬停 {FgjM3Common.Cell(probe)}：“{hover.Replace("\n", " ")}”" +
                                   $"（悬停实测 {info.ThroughputPerMinute:F1} 是 {info.WindowSeconds:F0} 游戏秒窗口的平均，含开跑段，不超过设计）")
                : StepOutcome.Fail($"物品线吞吐不对：实测 {perMin:F1} 件 / 分钟（设计 {expectPerMin}）、接上 {on}、设计值 {info.RatedItemsPerMinute}、悬停实测 {info.ThroughputPerMinute:F1}（窗口 {info.WindowSeconds:F0} 秒）、悬停“{hover}”");
        }

        /// <summary>
        /// 升级后：整条线 T2、悬停写设计吞吐 120；实测吞吐（稳定 20 游戏秒后测 30 游戏秒）不低于升级前的实测（家园废料总量有限，带上压不满时达不到设计值——
        /// 设计值本身由 [传送带正式化] / [分流合流] 自检在满载条件下断言，FGT-LOG-005）。这一段的实测值是后面“离家期间产量”的对照基准。
        /// </summary>
        private static StepOutcome TickRateT2(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out bool on);
            if (!FgjM3Common.Done(c, "t0"))
            {
                // 输出口刚改回“全部”：物品从仓库走到核心要一整趟（T2 满载时 27 格约 54 游戏秒），先等两趟让带上的件数稳定，再测 60 游戏秒。
                if (!FgjM3Common.Done(c, "s0"))
                {
                    c.SetLong(FgjM3Common.SK(c, "start"), GameClock.Ticks);
                    FgjM3Common.Mark(c, "s0");
                }
                if (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "start")) < 120L * GameClock.StepHz)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.SetLong(FgjM3Common.SK(c, "got0"), got);
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            double dt = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            if (dt < 60.0)
            {
                return StepOutcome.Wait;
            }
            double perMin = (got - c.GetLong(FgjM3Common.SK(c, "got0"))) / dt * 60.0;
            double t1 = double.Parse(c.Get("rateT1", "0"), CultureInfo.InvariantCulture);
            List<GridCell> route = Route(c);
            GridCell probe = route[route.Count / 2];
            string hover = BeltNetworkService.DescribeCell(probe) ?? string.Empty;
            bool rated = FgjM3Common.BeltInfo(probe, out BeltCellInfo info) && info.Tier == 1 && info.RatedItemsPerMinute == 120 && hover.Contains("120");
            c.Set("rateT2", perMin.ToString("R", CultureInfo.InvariantCulture));
            int items = BeltPortService.StoreItemsOnBelts(St);
            return on && rated && perMin >= t1 * 0.95 && perMin <= 120 * 1.08
                ? StepOutcome.Done($"{dt:F0} 游戏秒里核心输入口收货 {perMin:F1} 件 / 分钟（升级前 {t1:F1}；设计 120，此刻带上 {items} 件、家园库存 {St.Scrap}）；悬停 {FgjM3Common.Cell(probe)}：“{hover.Replace("\n", " ")}”")
                : StepOutcome.Fail($"升级后吞吐不对：实测 {perMin:F1}（升级前 {t1:F1}）、等级 {info.Tier}、设计 {info.RatedItemsPerMinute}、接上 {on}、悬停“{hover}”");
        }

        // ── 快捷栏与入口按钮（建造模式关着时在 HUD 层）────────────────────────────────

        /// <summary>
        /// 第 1 轮审查（P2）：建造模式关着时建造栏文档降到 HUD 层（BuildModeHudUIToolkit.ClosedHudOrder），此前没有自动测试在这一层真实点过快捷栏 / 入口按钮。
        /// 先在建造模式开着时把传送带 T1 放进快捷栏（选中后点空格子，与冒烟同一入口，这里走真实指针事件）；后面建造模式关着时再从快捷栏点它。
        /// </summary>
        private static StepOutcome TickHotbarPut(JourneyContext c)
        {
            CampaignState st = St;
            int slot = -1;
            for (int i = 0; i < BuildCatalog.HotbarSlots && slot < 0; i++)
            {
                string id = BuildCatalog.HotbarId(st, i);
                if (id == null || id == FgjM3Common.ToolBeltT1)
                {
                    slot = i;
                }
            }
            if (slot < 0)
            {
                return StepOutcome.Fail("快捷栏 10 格都放着别的条目");
            }
            if (!FgjM3Common.Once(c, "click", () =>
                {
                    if (!JourneyInput.ClickUitk(FgjM3Common.BuildHost, "HotbarSlot" + slot.ToString(CultureInfo.InvariantCulture)))
                    {
                        c.Set("uiFail", JourneyInput.LastUiFailure);
                    }
                    return true;
                }))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 400)
            {
                return StepOutcome.Wait;
            }
            c.SetInt("hbSlot", slot);
            return BuildCatalog.HotbarId(st, slot) == FgjM3Common.ToolBeltT1 && FgjM3Common.EntrySelected(FgjM3Common.ToolBeltT1)
                ? StepOutcome.Done($"选中传送带 T1 后左键点快捷栏第 {slot + 1} 格：放进快捷栏（格子写“{BuildModeHudUIToolkit.Instance?.HotbarSlotText(slot)}”），手上仍是传送带 T1")
                : StepOutcome.Retry($"点快捷栏第 {slot + 1} 格后格子里是 {BuildCatalog.HotbarId(st, slot) ?? "空"}、选中 {FgjM3Common.Mode?.SelectedEntryId}（{c.Get("uiFail")}）");
        }

        /// <summary>建造模式关着时，建造栏文档在 HUD 层；点快捷栏格子（真实指针事件，点之前按面板拾取核对没被同一面板上更高层的常驻界面挡住）。</summary>
        private static StepOutcome TickHotbarClosed(JourneyContext c)
        {
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            int slot = c.GetInt("hbSlot");
            if (!FgjM3Common.Done(c, "pre"))
            {
                int layer = hud?.Document != null ? Mathf.RoundToInt(hud.Document.sortingOrder) : -1;
                if (FgjM3Common.BuildOpen || hud == null || !hud.HotbarVisible || layer != BuildModeHudUIToolkit.ClosedHudOrder)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait
                        : StepOutcome.Fail($"建造模式关着时快捷栏应可见、文档在 HUD 层 {BuildModeHudUIToolkit.ClosedHudOrder}：建造模式开着 {FgjM3Common.BuildOpen}、快捷栏可见 {hud?.HotbarVisible}、分层 {layer}");
                }
                c.SetInt(FgjM3Common.SK(c, "layer"), layer);
                FgjM3Common.Mark(c, "pre");
            }
            if (!FgjM3Common.Once(c, "click", () =>
                {
                    if (!JourneyInput.ClickUitk(FgjM3Common.BuildHost, "HotbarSlot" + slot.ToString(CultureInfo.InvariantCulture)))
                    {
                        c.Set("uiFail", JourneyInput.LastUiFailure);
                    }
                    return true;
                }))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            int after = hud?.Document != null ? Mathf.RoundToInt(hud.Document.sortingOrder) : -1;
            return FgjM3Common.BuildOpen && FgjM3Common.EntrySelected(FgjM3Common.ToolBeltT1) && hud.PanelVisible && after == BuildModeHudUIToolkit.Order
                ? StepOutcome.Done($"建造模式关着（建造栏文档在 HUD 层 {c.GetInt(FgjM3Common.SK(c, "layer"))}）时左键点快捷栏第 {slot + 1} 格：建造模式打开并选中传送带 T1，建造栏回到第 {after} 层")
                : StepOutcome.Retry($"点快捷栏第 {slot + 1} 格后建造模式开着 {FgjM3Common.BuildOpen}、选中 {FgjM3Common.Mode?.SelectedEntryId}、分层 {after}（{c.Get("uiFail")}）");
        }

        /// <summary>建造模式关着时点左侧“建造”入口按钮（同在 HUD 层，真实指针事件 + 拾取核对）：打开建造模式，手上不拿条目（后面要指着传送带按旋转键）。</summary>
        private static StepOutcome TickEntryOpen(JourneyContext c)
        {
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "pre"))
            {
                int layer = hud?.Document != null ? Mathf.RoundToInt(hud.Document.sortingOrder) : -1;
                if (FgjM3Common.BuildOpen || hud == null || !hud.EntryVisible || layer != BuildModeHudUIToolkit.ClosedHudOrder)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait
                        : StepOutcome.Fail($"建造模式关着时入口按钮应可见、文档在 HUD 层：建造模式开着 {FgjM3Common.BuildOpen}、入口可见 {hud?.EntryVisible}、分层 {layer}");
                }
                FgjM3Common.Mark(c, "pre");
            }
            if (!FgjM3Common.Once(c, "click", () =>
                {
                    if (!JourneyInput.ClickUitk(FgjM3Common.BuildHost, "BuildEntryButton"))
                    {
                        c.Set("uiFail", JourneyInput.LastUiFailure);
                    }
                    return true;
                }))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            bool none = FgjM3Common.Mode != null && FgjM3Common.Mode.SelectedEntryId == null;
            return FgjM3Common.BuildOpen && hud.PanelVisible && InputRouter.ActiveContext == InputContext.Build && none
                ? StepOutcome.Done($"左键点左侧“{JourneyInput.FindUitk<Button>(FgjM3Common.BuildHost, "BuildEntryButton")?.text}”入口按钮（HUD 层）：建造模式打开，建造栏 {hud.CategoryCount} 个分类、手上不拿条目")
                : StepOutcome.Retry($"点入口按钮后建造模式开着 {FgjM3Common.BuildOpen}、选中 {FgjM3Common.Mode?.SelectedEntryId}（{c.Get("uiFail")}）");
        }

        // ── 堵塞 → 诊断 → 修好 ─────────────────────────────────────────────────────

        /// <summary>
        /// 误转的那一格：段内不在两头的一格（前后都是同一段的带，避免与转角、端口相邻），从最长一段的中间往两边找，取此刻**带上有货**的一格。
        /// 第 1 轮审查（P1）：被误转的格上有货时，货跟着掉头、这一格自己也停成“末端”——它的原因必须让玩家转这一格，而不是去转方向正确的上游那格；
        /// 上一版碰巧误转了一个空格，没覆盖到。没有有货的格时返回 false（等货流过来）。
        /// </summary>
        private static bool TryJamCell(JourneyContext c, out GridCell cell, out int dir)
        {
            List<GridCell> route = Route(c);
            List<(int a, int b, int dir)> runs = FgjM3Common.Runs(route, c.GetInt("goalDir"));
            foreach ((int a, int b, int d) r in runs.OrderByDescending(r => r.b - r.a))
            {
                int mid = (r.a + r.b) / 2;
                for (int off = 0; off <= (r.b - r.a) / 2 + 1; off++)
                {
                    foreach (int i in new[] { mid - off, mid + off })
                    {
                        if (i > r.a && i < r.b && FgjM3Common.BeltInfo(route[i], out BeltCellInfo info) && info.Count > 0 && (int)info.Dir == r.d)
                        {
                            cell = route[i];
                            dir = r.d;
                            return true;
                        }
                    }
                }
            }
            cell = default;
            dir = 0;
            return false;
        }

        private static StepOutcome TickJam(JourneyContext c)
        {
            string pick = FgjM3Common.SK(c, "pick");
            if (!FgjM3Common.Done(c, "pick"))
            {
                if (!TryJamCell(c, out GridCell cand, out int cd))
                {
                    JourneyCommon.ResumeIfAutoPaused(c);
                    return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("物品线段内一直没有有货的格（仓库没有往外推？）");
                }
                FgjM3Common.SetCell(c, pick + ".cell", cand);
                c.SetInt(pick + ".dir", cd);
                FgjM3Common.Mark(c, "pick");
            }
            GridCell x = FgjM3Common.GetCell(c, pick + ".cell");
            int dir = c.GetInt(pick + ".dir");
            if (!FgjM3Common.HoverThenPress(c, "rot", x, GameActionId.Rotate))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rot.press") < 500)
            {
                return StepOutcome.Wait;
            }
            bool reversed = FgjM3Common.BeltInfo(x, out BeltCellInfo info) && (int)info.Dir == ((dir + 2) & 3);
            string status = FgjM3Common.Mode.StatusText ?? string.Empty;
            if (!reversed)
            {
                return StepOutcome.Retry($"按旋转键后 {FgjM3Common.Cell(x)} 没有反转（朝{FgjM3Common.DirName((int)info.Dir)}；状态行“{status}”）");
            }
            FgjM3Common.SetCell(c, "jam", x);
            c.SetInt("jamDir", dir);
            c.SetInt("jamItems", info.Count);
            c.SetLong("jamAtMs", FgjM3Common.NowMs());
            c.SetLong("jamAtTick", GameClock.Ticks);
            return StepOutcome.Done($"指着 {FgjM3Common.Cell(x)}（格上 {info.Count} 件货）按旋转键：原地反转，状态行“{status.Split('\n').FirstOrDefault()}”；撤销栈顶是“{PlanHistory.StepName(PlanHistory.PeekUndo(St))}”");
        }

        private static StepOutcome TickStalled(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out _);
            long pushed = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeWarehouse, c.Get("outKey"), out _);
            string k = FgjM3Common.SK(c, "last");
            if (c.Get(k) != got + "/" + pushed)
            {
                c.Set(k, got + "/" + pushed);
                c.SetLong(k + ".tick", GameClock.Ticks);
                return StepOutcome.Wait;
            }
            double still = (GameClock.Ticks - c.GetLong(k + ".tick")) / (double)GameClock.StepHz;
            return still >= 5.0
                ? StepOutcome.Done($"产线停了：{still:F0} 游戏秒里核心输入口收货停在 {got} 件、仓库输出口推上停在 {pushed} 件")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickDiagnosis(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            if (!DiagnosisPanelUIToolkit.IsOpen || panel == null || !panel.PanelVisible)
            {
                return StepOutcome.Retry("按 Ctrl+O 后“为什么不工作”没有打开");
            }
            string wh = HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeWarehouse);
            int row = -1;
            for (int i = 0; i < panel.RowCount; i++)
            {
                if (panel.SubjectText(i).Contains(wh))
                {
                    row = i;
                    break;
                }
            }
            GridCell x = FgjM3Common.GetCell(c, "jam");
            GridCell upstream = FgjM3Common.Step(x, (c.GetInt("jamDir") + 2) & 3);
            // 第 1 轮审查（P1 / P2）：根源要点名误转的那一格（上游格的原因写“正前方 x 朝向相反”；x 上有货时它自己也是根源，写“这一格朝向反了”），
            // 不能出现让玩家去转方向正确的上游格的步骤（“正前方 upstream 的传送带朝向相反”）；这里是确定的情况，也不该写成中性的“方向错的那一格”。
            string blameX = $"正前方（{x.X}, {x.Y}）的传送带朝向相反";
            string blameUp = $"正前方（{upstream.X}, {upstream.Y}）的传送带朝向相反";
            const string selfText = "这一格朝向反了";
            int root = -1;
            bool namesX = false, selfAtX = false, contradicts = false, unsure = false;
            var steps = new List<string>();
            for (int i = 0; i < panel.StepButtonCount; i++)
            {
                DiagStep st = panel.StepTarget(i);
                if (st == null)
                {
                    continue;
                }
                steps.Add($"{st.Code}:{st.Text}");
                string t = st.Text ?? string.Empty;
                bool nearX = st.HasPosition && Vector2.Distance(st.Position, FgjM3Common.Ground(x)) <= 0.5f;
                namesX |= t.Contains(blameX);
                selfAtX |= nearX && t.Contains(selfText) && t.Contains($"正前方（{upstream.X}, {upstream.Y}）");
                contradicts |= t.Contains(blameUp) || (t.Contains(selfText) && !nearX);
                unsure |= t.Contains("方向错的那一格");
                if ((st.Code == DiagCode.BeltTerminal || st.Code == DiagCode.BeltDownstream || st.Code == DiagCode.BeltLoop) && st.HasPosition
                    && (t.Contains(blameX) || (nearX && t.Contains(selfText))))
                {
                    root = i;
                }
            }
            int jamItems = c.GetInt("jamItems");
            string hoverX = BeltNetworkService.DescribeCell(x) ?? string.Empty;
            string hoverUp = BeltNetworkService.DescribeCell(upstream) ?? string.Empty;
            bool hoverOk = hoverUp.Contains(blameX) && (jamItems == 0 || hoverX.Contains(selfText)) && !hoverX.Contains(blameUp) && !hoverUp.Contains(blameUp);
            bool ok = row >= 0 && root >= 0 && namesX && !contradicts && !unsure && (jamItems == 0 || selfAtX) && hoverOk;
            if (!ok)
            {
                return c.StepElapsed < 20
                    ? StepOutcome.Wait
                    : StepOutcome.Fail($"“为什么不工作”没有准确点名误转的那一格 {FgjM3Common.Cell(x)}：仓库那一行 {row}、点名 {namesX}、误转格自己写明 {selfAtX}（格上原有 {jamItems} 件）、" +
                                       $"出现让玩家转上游格的步骤 {contradicts}、中性写法 {unsure}；悬停 x“{hoverX}”/ 上游“{hoverUp}”；各条 [{string.Join(" | ", steps)}]");
            }
            c.SetInt("diagRoot", root);
            c.Set("diagRootText", panel.StepTarget(root).Text);
            c.SetInt("fly0", WorldView.FlyCount);
            return StepOutcome.Done($"“为什么不工作”（停靠左侧、非模态）列出 {panel.RowCount} 个停工对象，其中“{panel.SubjectText(row)}”：[{string.Join(" → ", steps.Where(s => s.Length > 0))}]；" +
                                   $"根源点名误转的 {FgjM3Common.Cell(x)}（上游格写“{blameX}”{(jamItems > 0 ? $"；误转格上原有 {jamItems} 件货，它自己的原因写“{selfText}…指着这一格”" : string.Empty)}），" +
                                   $"没有让玩家去转上游格 {FgjM3Common.Cell(upstream)} 的步骤；悬停两格同一说法；点“{panel.StepTarget(root).Text}”");
        }

        private static StepOutcome TickDiagLocate(JourneyContext c)
        {
            if (!FgjM3Common.Once(c, "click", () =>
                {
                    if (!JourneyInput.ClickUitk(FgjM3Common.DiagHost, "DgStep" + c.GetInt("diagRoot").ToString(CultureInfo.InvariantCulture)))
                    {
                        c.Set("uiFail", JourneyInput.LastUiFailure);
                    }
                    return true;
                }))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 1500)
            {
                return StepOutcome.Wait;
            }
            DiagStep st = DiagnosisPanelUIToolkit.Instance?.StepTarget(c.GetInt("diagRoot"));
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            bool flew = WorldView.FlyCount > c.GetInt("fly0") && st != null && Mathf.Abs(f.x - st.Position.x) < 1.5f && Mathf.Abs(f.y - st.Position.y) < 1.5f;
            double secs = (FgjM3Common.NowMs() - c.GetLong("jamAtMs")) / 1000.0;
            double gameSecs = (GameClock.Ticks - c.GetLong("jamAtTick")) / (double)GameClock.StepHz;
            c.Set("findSecs", secs.ToString("F1", CultureInfo.InvariantCulture));
            return flew && FgjM3Common.BuildOpen && DiagnosisPanelUIToolkit.IsOpen
                ? StepOutcome.Done($"点“{st.Text}”：镜头飞到 ({st.Position.x:F0}, {st.Position.y:F0})，建造模式与面板都还开着；从误转到点中根源 {secs:F1} 真实秒（{gameSecs:F0} 游戏秒；机器人口径，只作 FGR-BAL-061“30 秒找到原因”的自动化参照，真人数据见试玩）")
                : StepOutcome.Retry($"点根源后镜头没飞过去（飞行次数 {WorldView.FlyCount}，焦点 ({f.x:F1}, {f.y:F1})；{c.Get("uiFail")}）");
        }

        private static StepOutcome TickFix(JourneyContext c)
        {
            GridCell x = FgjM3Common.GetCell(c, "jam");
            int dir = c.GetInt("jamDir");
            if (!FgjM3Common.HoverThenPress(c, "rot", x, GameActionId.Rotate))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rot.press") < 500)
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.BeltInfo(x, out BeltCellInfo info) && (int)info.Dir == dir
                ? StepOutcome.Done($"指着 {FgjM3Common.Cell(x)} 再按旋转键：转回朝{FgjM3Common.DirName(dir)}（“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”）")
                : StepOutcome.Retry($"再按旋转键后 {FgjM3Common.Cell(x)} 朝{FgjM3Common.DirName((int)info.Dir)}");
        }

        private static StepOutcome TickFlowBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out _);
            if (!FgjM3Common.Done(c, "g0"))
            {
                c.SetLong(FgjM3Common.SK(c, "got0"), got);
                FgjM3Common.Mark(c, "g0");
                return StepOutcome.Wait;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            string wh = HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeWarehouse);
            bool listed = panel != null && Enumerable.Range(0, panel.RowCount).Any(i => panel.SubjectText(i).Contains(wh));
            long gained = got - c.GetLong(FgjM3Common.SK(c, "got0"));
            if (gained >= 5 && !listed)
            {
                return StepOutcome.Done($"修好后核心输入口又收了 {gained} 件；“为什么不工作”里仓库那一条消失（剩 {panel?.RowCount} 个停工对象）");
            }
            return StepOutcome.Wait;
        }

        // ── 布局库 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickCopied(JourneyContext c)
        {
            GridCell[] line = Line1(c);
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(line[0]), FgjM3Common.Ground(line[line.Length - 1]))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "drag") < 800)
            {
                return StepOutcome.Wait;
            }
            PlanEntryBlock clip = HomeValleyBuildMode.Clipboard;
            int n = PlanEntries.CountOf(clip);
            bool kinds = clip != null && clip.Ids.Count(x => x == FgjM3Common.ToolPump) == 1 && clip.Ids.Count(x => x == FgjM3Common.ToolTank) == 1;
            return n == line.Length && kinds && FgjM3Common.Mode.PasteMode
                ? StepOutcome.Done($"松开：复制了 {n} 件（泵、{FgjM3Common.PipeCells} 格管线、储罐），直接进入粘贴")
                : StepOutcome.Retry($"复制结果不对：{n} 件（{string.Join(",", clip?.Ids ?? Array.Empty<string>())}）、粘贴模式 {FgjM3Common.Mode.PasteMode}");
        }

        /// <summary>右键退出某个模式：右键点一格空地（不在任何东西上）。</summary>
        private static StepOutcome TickRightClickExit(JourneyContext c, Func<bool> exited, string what)
        {
            GridCell core = HomeGridService.CorePivot(St);
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            var at = new Vector2(Mathf.Round(f.x), Mathf.Round(f.y));
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(at, 1)))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rc") < 500)
            {
                return StepOutcome.Wait;
            }
            return exited() && FgjM3Common.BuildOpen ? StepOutcome.Done("右键" + what) : StepOutcome.Retry($"右键后没有{what}（核心 {FgjM3Common.Cell(core)}）");
        }

        private static StepOutcome TickLibSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            LayoutLibraryPanelUIToolkit panel = LayoutLibraryPanelUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "save"))
            {
                TextField name = JourneyInput.FindUitk<TextField>(FgjM3Common.LibHost, "LayoutLibraryName");
                if (name == null || !JourneyInput.ClickElement(name))
                {
                    return StepOutcome.Retry("点不到布局库的名字框：" + JourneyInput.LastUiFailure);
                }
                c.SetInt("lib0", LayoutLibrary.Count);
                name.value = LayoutName; // 与在框里打字同一个值变化回调
                if (!JourneyInput.ClickUitk(FgjM3Common.LibHost, "LayoutLibrarySave"))
                {
                    return StepOutcome.Retry("点不到“保存剪贴板”：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "save");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            panel?.Refresh();
            int row = RowOf(LayoutName);
            bool ok = LayoutLibrary.Count == c.GetInt("lib0") + 1 && row >= 0 && panel != null && panel.RowThumbnail(row) != null && File.Exists(LayoutLibrary.FilePath);
            return ok
                ? StepOutcome.Done($"布局库多一行“{panel.RowName(row)}”（{panel.RowInfo(row)}，带缩略图），写进布局库文件 {Path.GetFileName(LayoutLibrary.FilePath)}")
                : StepOutcome.Retry($"保存后布局库 {LayoutLibrary.Count} 个（原 {c.GetInt("lib0")}），找不到“{LayoutName}”那一行");
        }

        private static int RowOf(string name)
        {
            LayoutLibraryPanelUIToolkit panel = LayoutLibraryPanelUIToolkit.Instance;
            for (int i = 0; panel != null && i < 64; i++)
            {
                string n = panel.RowName(i);
                if (string.IsNullOrEmpty(n))
                {
                    break;
                }
                if (n == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private static StepOutcome TickSite2(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "found"))
            {
                GridCell[] l1 = Line1(c);
                var avoid = new HashSet<long>();
                foreach (GridCell p in l1.Concat(Route(c)))
                {
                    for (int dy = -3; dy <= 3; dy++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            avoid.Add(FgjM3Common.Key(new GridCell(p.X + dx, p.Y + dy)));
                        }
                    }
                }
                if (!FgjM3Common.FindWaterLine(s, l1[0], 12, 48, avoid, out GridCell pump2, out int wdir2))
                {
                    return StepOutcome.Fail("第一条抽水线 12～48 格外按种子地形找不到另一片能放抽水线的水源");
                }
                FgjM3Common.SetCell(c, "pump2", pump2);
                c.SetInt("wdir2", wdir2);
                FgjM3Common.Mark(c, "found");
            }
            GridCell[] l2 = Line2(c);
            Vector2 mid = (FgjM3Common.Ground(l2[0]) + FgjM3Common.Ground(l2[l2.Length - 1])) * 0.5f;
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Clear(FgjM3Common.Ground(l2[0]), out _) || !FgjM3Common.Clear(FgjM3Common.Ground(l2[l2.Length - 1]), out _))
            {
                FgjM3Common.PanTo(mid);
                return StepOutcome.Wait;
            }
            GridCell[] l1b = Line1(c);
            return StepOutcome.Done($"另一片水源 {FgjM3Common.Cell(l2[0])}（离第一条线 {Vector2.Distance(FgjM3Common.Ground(l2[0]), FgjM3Common.Ground(l1b[0])):F0} 格，抽水线朝{FgjM3Common.DirName(c.GetInt("wdir2"))}），方向键平移镜头到那里");
        }

        private static StepOutcome TickLibPlace(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "place"))
            {
                int row = RowOf(LayoutName);
                if (row < 0)
                {
                    return StepOutcome.Fail($"布局库里没有“{LayoutName}”");
                }
                Button place = LayoutLibraryPanelUIToolkit.Instance.RowButton(row, "LlPlace");
                if (place == null || !JourneyInput.ClickElement(place))
                {
                    return StepOutcome.Retry("点不到“放置”：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "place");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.9)
            {
                return StepOutcome.Wait;
            }
            return !LayoutLibraryPanelUIToolkit.IsOpen && FgjM3Common.Mode.PasteMode && PlanEntries.CountOf(FgjM3Common.Mode.PasteSource) == FgjM3Common.PipeCells + 2
                ? StepOutcome.Done($"点“放置”：布局库关掉，建造模式进入粘贴（{PlanEntries.CountOf(FgjM3Common.Mode.PasteSource)} 件跟着鼠标）")
                : StepOutcome.Retry($"点“放置”后：布局库开着 {LayoutLibraryPanelUIToolkit.IsOpen}、粘贴模式 {FgjM3Common.Mode.PasteMode}");
        }

        private static StepOutcome TickPasteAim(JourneyContext c)
        {
            HomeValleyBuildMode mode = FgjM3Common.Mode;
            int q = (c.GetInt("wdir2") - c.GetInt("wdir1") + 4) & 3;
            if ((mode.PasteQuarter & 3) != q)
            {
                if (JourneyInput.Holding || FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "rot")) < 300)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "rot"), FgjM3Common.NowMs());
                JourneyInput.PressAction(GameActionId.Rotate);
                return StepOutcome.Wait;
            }
            GridCell pump2 = FgjM3Common.GetCell(c, "pump2");
            string ak = FgjM3Common.SK(c, "anchor");
            if (string.IsNullOrEmpty(c.Get(ak)))
            {
                FgjM3Common.SetCell(c, ak, FgjM3Common.Step(pump2, c.GetInt("wdir2"), 2));
                c.Set(ak, "1");
            }
            GridCell anchor = FgjM3Common.GetCell(c, ak);
            if (!FgjM3Common.Once(c, "hover" + anchor.X + "_" + anchor.Y, () => FgjM3Common.TryHover(FgjM3Common.Ground(anchor))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "hover" + anchor.X + "_" + anchor.Y) < 500)
            {
                return StepOutcome.Wait;
            }
            PastePlan pv = mode.PastePreview;
            PasteItem pumpItem = pv?.Items.FirstOrDefault(i => i.Id == FgjM3Common.ToolPump);
            if (pv == null || pumpItem == null || mode.HoverCell != anchor)
            {
                return FgjM3Common.SinceMs(c, "hover" + anchor.X + "_" + anchor.Y) < 2000 ? StepOutcome.Wait : StepOutcome.Retry("粘贴预览没有跟着鼠标");
            }
            if (pumpItem.Cell != pump2)
            {
                if (c.GetInt(FgjM3Common.SK(c, "adjust")) >= 3)
                {
                    return StepOutcome.Fail($"对不准：泵落在 {FgjM3Common.Cell(pumpItem.Cell)}，要落在 {FgjM3Common.Cell(pump2)}");
                }
                c.SetInt(FgjM3Common.SK(c, "adjust"), c.GetInt(FgjM3Common.SK(c, "adjust")) + 1);
                FgjM3Common.SetCell(c, ak, new GridCell(anchor.X + pump2.X - pumpItem.Cell.X, anchor.Y + pump2.Y - pumpItem.Cell.Y));
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (pv.OkCount != FgjM3Common.PipeCells + 2 || pv.BadCount != 0)
            {
                return StepOutcome.Fail($"对准后预览不是全部能放：能放 {pv.OkCount}、不能放 {pv.BadCount}（“{info.Replace("\n", " ")}”）");
            }
            FgjM3Common.SetCell(c, "anchor2", anchor);
            return StepOutcome.Done($"按旋转键 {q} 次、鼠标移到 {FgjM3Common.Cell(anchor)}：泵落在水源 {FgjM3Common.Cell(pump2)}，预览 {pv.OkCount} 件都能放（“{info.Replace("\n", " ")}”）");
        }

        private static StepOutcome TickPasted(JourneyContext c)
        {
            GridCell anchor = FgjM3Common.GetCell(c, "anchor2");
            if (!FgjM3Common.Done(c, "b0"))
            {
                c.SetInt(FgjM3Common.SK(c, "planned0"), HomeValleyConstruction.PlannedCellCount(St));
                FgjM3Common.Mark(c, "b0");
            }
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(anchor))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 600)
            {
                return StepOutcome.Wait;
            }
            PlanApplyResult r = FgjM3Common.Mode.LastPaste;
            int planned = HomeValleyConstruction.PlannedCellCount(St);
            GridCell[] l2 = Line2(c);
            int onSite = l2.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            c.SetInt("plannedAfterPaste", planned);
            return r != null && r.PlacedPieces == l2.Length && onSite == l2.Length && PlanHistory.PeekUndo(St) == PlanStepKind.Paste
                ? StepOutcome.Done($"左键放下 {r.PlacedPieces} 件虚影（泵在水源上，规划格 {c.GetInt(FgjM3Common.SK(c, "planned0"))} → {planned}），撤销栈顶是“{PlanHistory.StepName(PlanStepKind.Paste)}”")
                : StepOutcome.Retry($"放下后：结果 {r?.PlacedPieces} 件、现场虚影 {onSite}/{l2.Length}、撤销栈顶 {PlanHistory.PeekUndo(St)}");
        }

        private static StepOutcome TickUndone(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            GridCell[] l2 = Line2(c);
            int onSite = l2.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _) || FgjM3Common.PipeInfo(p, out _));
            string status = FgjM3Common.Mode.StatusText ?? string.Empty;
            return onSite == 0 && PlanHistory.PeekRedo(St) == PlanStepKind.Paste && status.Contains(GameText.Format("plan.undo.done", PlanHistory.StepName(PlanStepKind.Paste)))
                ? StepOutcome.Done($"Ctrl+Z：刚放下的 {l2.Length} 件虚影全部取消（没取过料，废料 {St.Scrap} 不变）；状态行“{status.Split('\n').FirstOrDefault()}”；重做栈顶是“{PlanHistory.StepName(PlanHistory.PeekRedo(St))}”")
                : StepOutcome.Retry($"撤销后现场还有 {onSite} 件、重做栈顶 {PlanHistory.PeekRedo(St)}、状态行“{status}”");
        }

        private static StepOutcome TickRedone(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            GridCell[] l2 = Line2(c);
            int onSite = l2.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            return onSite == l2.Length && PlanHistory.PeekUndo(St) == PlanStepKind.Paste
                ? StepOutcome.Done($"Ctrl+Y：{onSite} 件虚影放回原处（泵仍在水源 {FgjM3Common.Cell(l2[0])}）；状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”")
                : StepOutcome.Retry($"重做后现场虚影 {onSite}/{l2.Length}、撤销栈顶 {PlanHistory.PeekUndo(St)}");
        }

        private static StepOutcome TickCopyBuilt(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            GridCell[] l2 = Line2(c);
            if (!FgjM3Common.AllBuilt(null, l2, out int unbuilt))
            {
                return StepOutcome.Wait;
            }
            long pumped = FgjM3Common.PumpTotalMl(l2[0]);
            long tank = FgjM3Common.TankMl(l2[l2.Length - 1]);
            if (pumped <= 0 || tank <= 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"第二条抽水线建成（{c.StepElapsed:F0} 秒）：泵累计 {pumped / 1000.0:F1} 升、储罐 {tank / 1000.0:F1} 升；废料 {St.Scrap}");
        }

        // ── 等材料 → 端口面板停止输出 ─────────────────────────────────────────────────

        private static StepOutcome TickWaitingMaterials(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            GridCell[] l2 = Line2(c);
            var seen = new List<string>();
            foreach (GridCell p in l2)
            {
                if (!HomeValleyConstruction.TryFindPlannedCell(St, p, out PlannedBeltRecord plan, out _))
                {
                    continue;
                }
                WorkOrderRecord o = HomeValleyWorkOrders.FindActiveBuild(St, HomeValleyConstruction.BeltPlanPrefix + plan.PlanId);
                string status = HomeValleyConstruction.DescribeStatus(St, o);
                seen.Add(status);
                if (o != null && o.State == WorkOrderState.Waiting && status.Contains("在传送带上"))
                {
                    return StepOutcome.Done($"{FgjM3Common.Cell(p)} 的施工单在等材料：家园库存 {St.Scrap}，带上 {BeltPortService.StoreItemsOnBelts(St)} 件");
                }
            }
            return c.StepElapsed < 80 ? StepOutcome.Wait : StepOutcome.Fail($"第二条抽水线的虚影没有进入“等材料”（废料 {St.Scrap}；状态 [{string.Join(" | ", seen)}]）");
        }

        /// <summary>施工队列（玩家查“为什么还没建”的入口）里那一行写明“另有 N 件在传送带上”与办法（端口面板“停止输出”）。</summary>
        internal static StepOutcome TickQueueSaysBelts(JourneyContext c)
        {
            if (c.StepElapsed < 0.7)
            {
                return StepOutcome.Wait;
            }
            ConstructionQueuePanelUIToolkit panel = ConstructionQueuePanelUIToolkit.Instance;
            if (!ConstructionQueuePanelUIToolkit.IsOpen || panel == null)
            {
                return StepOutcome.Retry("施工队列没打开");
            }
            for (int i = 0; i < panel.VisibleRowCount; i++)
            {
                string st = panel.RowStatus(i);
                if (st.Contains("等待材料") && st.Contains("在传送带上") && st.Contains("停止输出"))
                {
                    return StepOutcome.Done($"施工队列 {panel.VisibleRowCount} 行，其中“{panel.RowName(i)}：{st}”");
                }
            }
            return StepOutcome.Fail($"施工队列里没有写明在途件数的行（{string.Join(" | ", Enumerable.Range(0, panel.VisibleRowCount).Select(i => panel.RowName(i) + "：" + panel.RowStatus(i)))}）");
        }

        private static StepOutcome TickPortFilter(JourneyContext c, int filter)
        {
            string outKey = c.Get("outKey");
            if (!FgjM3Common.Done(c, "set"))
            {
                if (c.StepElapsed < 0.4)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "pushed0"), FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeWarehouse, outKey, out _));
                c.SetInt(FgjM3Common.SK(c, "scrap0"), St.Scrap);
                if (!FgjM3Common.PickPortFilter(c, outKey, filter, out string why))
                {
                    return StepOutcome.Retry("选不了过滤：" + why);
                }
                FgjM3Common.Mark(c, "set");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord wh = FgjM3Common.Home(HomeValleyLayout.BuildingTypeWarehouse);
            BeltPortService.Binding bind = BeltPortService.Find(wh.BuildingId, outKey);
            int now = bind?.Record != null ? bind.Record.Filter : -99;
            if (now != filter)
            {
                return StepOutcome.Retry($"下拉选“{BeltPortService.FilterName(filter)}”后端口过滤是 {now}");
            }
            long pushed = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeWarehouse, outKey, out _);
            if (filter == BeltPortService.FilterOff)
            {
                // 仓库不再往外推；带上的废料陆续送进核心，库存回升到能施工。
                if (St.Scrap < 40)
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Done($"输出口改成“停止输出”（行里原来写“{c.Get(FgjM3Common.SK(c, "portRowText"))}”）：仓库不再往外推，带上的废料送进核心，家园库存 {c.GetInt(FgjM3Common.SK(c, "scrap0"))} → {St.Scrap}");
            }
            return pushed > c.GetLong(FgjM3Common.SK(c, "pushed0")) + 3
                ? StepOutcome.Done($"输出口改回“{BeltPortService.FilterName(filter)}”：仓库重新往外推（又推上 {pushed - c.GetLong(FgjM3Common.SK(c, "pushed0"))} 件），家园库存 {St.Scrap}")
                : StepOutcome.Wait;
        }

        // ── 升级 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickUpgradeBoxes(JourneyContext c)
        {
            List<GridCell> route = Route(c);
            List<(int a, int b, int dir)> runs = FgjM3Common.Runs(route, c.GetInt("goalDir"));
            string ri = FgjM3Common.SK(c, "run");
            int k = c.GetInt(ri);
            if (k >= runs.Count)
            {
                int planned = route.Count(p => HomeValleyConstruction.TryFindUpgradeCell(St, p, out _, out _));
                return planned == route.Count && PlanHistory.PeekUndo(St) == PlanStepKind.Upgrade
                    ? StepOutcome.Done($"{runs.Count} 次拖框：物品线 {planned} 格全部生成 T1 → T2 升级施工（撤销栈顶“{PlanHistory.StepName(PlanStepKind.Upgrade)}”；状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”）")
                    : StepOutcome.Retry($"拖框后升级施工 {planned}/{route.Count} 格");
            }
            (int a, int b, int dir) run = runs[k];
            string rk = "u" + k.ToString(CultureInfo.InvariantCulture);
            if (!FgjM3Common.Once(c, rk, () => FgjM3Common.TryDrag(FgjM3Common.Ground(route[run.a]), FgjM3Common.Ground(route[run.b]))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, rk) < 600)
            {
                return StepOutcome.Wait;
            }
            c.SetInt(ri, k + 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickUpgraded(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> route = Route(c);
            int min = FgjM3Common.MinTier(route, out int max, out int missing);
            bool pending = route.Any(p => HomeValleyConstruction.TryFindUpgradeCell(St, p, out _, out _));
            if (min == 1 && max == 1 && missing == 0 && !pending)
            {
                return StepOutcome.Done($"整条物品线 {route.Count} 格升到 T2（{c.StepElapsed:F0} 秒）；废料 {St.Scrap}");
            }
            return StepOutcome.Wait;
        }

        // ── 存读档 ──────────────────────────────────────────────────────────────────

        private static IEnumerable<GridCell> Pumps(JourneyContext c) => new[] { Line1(c)[0], Line2(c)[0] };

        private static IEnumerable<GridCell> Tanks(JourneyContext c) => new[] { Line1(c)[FgjM3Common.PipeCells + 1], Line2(c)[FgjM3Common.PipeCells + 1] };

        private static StepOutcome TickPauseForSave(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            c.Set("pre", FgjM3Common.StateDigest(St));
            c.Set("preK", FgjM3Common.KernelDigest(Route(c), Pumps(c), Tanks(c)));
            c.Set("preStable", FgjM3Common.StableDigest(St));
            c.SetLong("preCons", FgjM3Common.Conserved());
            c.Set("prePumps", string.Join(",", Pumps(c).Select(p => FgjM3Common.PumpTotalMl(p).ToString(CultureInfo.InvariantCulture))));
            c.Set("preTanks", string.Join(",", Tanks(c).Select(p => FgjM3Common.TankMl(p).ToString(CultureInfo.InvariantCulture))));
            c.Set("preTiers", string.Join(string.Empty, Route(c).Select(p => FgjM3Common.BeltInfo(p, out BeltCellInfo i) ? i.Tier.ToString(CultureInfo.InvariantCulture) : "x")));
            c.SetLong("preTicks", GameClock.Ticks);
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：{c.Get("pre")}；{c.Get("preK")}");
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            string disk = FgjM3Common.StateDigest(onDisk.State);
            return disk == c.Get("pre")
                ? StepOutcome.Done($"回到主菜单；存档里 {disk}，与存档那一刻逐项一致")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {disk}");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "cmp"))
            {
                // 读档后第一帧就核对（战略暂停不进存档，读档后世界立刻开跑）。
                string report = CompareAfterLoad(c, out bool ok);
                if (!ok)
                {
                    return StepOutcome.Fail("读档后不一致：" + report);
                }
                bool lib = LayoutLibrary.All.Any(l => l.Name == LayoutName);
                if (!lib)
                {
                    return StepOutcome.Fail("读档后布局库里没有“" + LayoutName + "”（布局库跨存档，存在玩家配置目录）");
                }
                c.Set("loadReport", report);
                c.SetLong(FgjM3Common.SK(c, "got0"), FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out _));
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                FgjM3Common.Mark(c, "cmp");
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out _) - c.GetLong(FgjM3Common.SK(c, "got0"));
            double dt = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            return got >= 5 && dt > 0
                ? StepOutcome.Done($"读档进入家园：{c.Get("loadReport")}；布局库跨存档仍在；读档后 {dt:F1} 游戏秒里核心输入口又收 {got} 件（照常运转）")
                : StepOutcome.Wait;
        }

        /// <summary>
        /// 读档后第一帧对照存档前：静态部分（家园建筑、撤销 / 重做栈）逐项相等；物流守恒量相等（物品只在库存、端口、带上、地面、货舱之间移动）；
        /// 物品线每格等级相等；两台泵累计与两个储罐 = 存档值 + 读档后已走步数 × 泵速（储罐满了按余量，±2 步）；读档后还没走步时再逐字比对传送带内核状态哈希。
        /// </summary>
        private static string CompareAfterLoad(JourneyContext c, out bool ok)
        {
            long dTicks = GameClock.Ticks - c.GetLong("preTicks");
            string stable = FgjM3Common.StableDigest(St);
            long cons = FgjM3Common.Conserved();
            string tiers = string.Join(string.Empty, Route(c).Select(p => FgjM3Common.BeltInfo(p, out BeltCellInfo i) ? i.Tier.ToString(CultureInfo.InvariantCulture) : "x"));
            long[] p0 = c.Get("prePumps").Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            long[] k0 = c.Get("preTanks").Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            GridCell[] pumps = Pumps(c).ToArray();
            GridCell[] tanks = Tanks(c).ToArray();
            bool fluid = true;
            var fl = new List<string>();
            for (int i = 0; i < pumps.Length; i++)
            {
                long cap = FgjM3Common.TankCapMl(tanks[i]);
                double expect = Math.Min(FgjM3Common.PumpMlPerStep * dTicks, cap - k0[i]);
                long dp = FgjM3Common.PumpTotalMl(pumps[i]) - p0[i];
                long dk = FgjM3Common.TankMl(tanks[i]) - k0[i];
                bool one = dTicks >= 0 && Math.Abs(dp - expect) <= FgjM3Common.PumpMlPerStep * 2 + 1 && dp == dk;
                fluid &= one;
                fl.Add($"泵{i + 1} {p0[i] / 1000.0:F2} → {(p0[i] + dp) / 1000.0:F2} 升（按 {dTicks} 步应多 {expect / 1000.0:F2}）");
            }
            bool hash = dTicks > 0 || FgjM3Common.KernelDigest(Route(c), Pumps(c), Tanks(c)) == c.Get("preK");
            ok = dTicks >= 0 && dTicks <= GameClock.StepHz * 5 && stable == c.Get("preStable") && cons == c.GetLong("preCons") && tiers == c.Get("preTiers") && fluid && hash;
            return $"读档后第一帧（已走 {dTicks} 步）：建筑与撤销栈 {(stable == c.Get("preStable") ? "一致" : "不一致：" + stable + " ≠ " + c.Get("preStable"))}；" +
                   $"物流守恒量 {c.GetLong("preCons")} → {cons}；物品线等级 {tiers}（存档前 {c.Get("preTiers")}）；{string.Join("、", fl)}；" +
                   (dTicks == 0 ? $"传送带内核状态哈希逐字一致（{hash}）" : "读档后已开跑，传送带内核哈希不逐字比（任意时刻存读档逐字段一致由 [存读档、后台一致性] B 段断言）");
        }

        // ── 远征一次 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickDriveOut(JourneyContext c)
        {
            int id = c.GetInt("m3");
            if (!FgjM2Common.HomePos(id, out Vector2 at))
            {
                return StepOutcome.Fail("找不到新机的位置");
            }
            string gk = FgjM3Common.SK(c, "dest");
            if (string.IsNullOrEmpty(c.Get(gk)))
            {
                GridCell from = GridCell.FromWorld(at);
                GridCell? dest = null;
                for (int r = 5; r <= 12 && dest == null; r++)
                {
                    foreach (int d in new[] { 1, 3, 0, 2 })
                    {
                        GridCell p = FgjM3Common.Step(from, d, r);
                        if (!FgjM3Common.NearBuilding(p, 2) && HomeGridService.ValidateBeltCell(St, p).Ok && AreaPassable(p, 1) && FgjM3Common.Clear(FgjM3Common.Ground(p), out _))
                        {
                            dest = p;
                            break;
                        }
                    }
                }
                if (dest == null)
                {
                    return StepOutcome.Fail("新机旁边找不到能开过去的空地");
                }
                FgjM3Common.SetCell(c, gk, dest.Value);
                c.Set(gk, "1");
            }
            GridCell to = FgjM3Common.GetCell(c, gk);
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(FgjM3Common.Ground(to), 1)))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rc") < 1000)
            {
                return StepOutcome.Wait;
            }
            bool work = HomeValleyWorkOrders.FindActiveOrderForMachine(St, id) != null;
            return MachineRegistry.TryGetRecord(id, out MachineRecord m) && !m.IsInFactory && !work
                ? StepOutcome.Done($"右键空地 {FgjM3Common.Cell(to)}：{FgjM3Common.Label(id)} 接到移动命令（不是工作），驶出装配站出口")
                : StepOutcome.Retry($"右键后 {FgjM3Common.Label(id)} 还占着装配站出口（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        /// <summary>周围 (2×half+1)² 格在寻路内核里对己方机器都可通行（开局平地里的一小块空地）。</summary>
        private static bool AreaPassable(GridCell g, int half)
        {
            NavKernel k = GameLogic.Campaign.Nav.NavService.Kernel;
            if (k == null)
            {
                return false;
            }
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    if (!k.Passable(g.X + x, g.Y + y, NavConst.ClassPlayer))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static StepOutcome TickDeparted(JourneyContext c)
        {
            StepOutcome o = FgjM1Journey.TickDeparted(c);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            List<int> roster = FgjM1Journey.Roster(c);
            c.SetInt("other", roster[0]);
            RecordAwayStart(c);
            return StepOutcome.Done(o.Message + $"；离家时：核心输入口累计 {c.GetLong("away.got")} 件，泵 {c.Get("away.pumps")} 毫升，储罐 {c.Get("away.tanks")} 毫升（第 {c.GetLong("away.tick")} 步）");
        }

        private static void RecordAwayStart(JourneyContext c)
        {
            c.SetLong("away.tick", GameClock.Ticks);
            c.SetLong("away.got", FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out _));
            c.Set("away.pumps", string.Join(",", Pumps(c).Select(p => FgjM3Common.PumpTotalMl(p).ToString(CultureInfo.InvariantCulture))));
            c.Set("away.tanks", string.Join(",", Tanks(c).Select(p => FgjM3Common.TankMl(p).ToString(CultureInfo.InvariantCulture))));
            c.SetInt("away.homeFrames", 0);
            c.SetInt("away.frames", 0);
        }

        private static void CountObserved(JourneyContext c)
        {
            int f = Time.frameCount;
            if (c.GetInt("away.lastFrame") == f)
            {
                return;
            }
            c.SetInt("away.lastFrame", f);
            c.SetInt("away.frames", c.GetInt("away.frames") + 1);
            if (WorldView.ObservedSiteId == HomeValleyLayout.RegionId)
            {
                c.SetInt("away.homeFrames", c.GetInt("away.homeFrames") + 1);
            }
        }

        private static StepOutcome TickAway(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CountObserved(c);
            double dt = (GameClock.Ticks - c.GetLong("away.tick")) / (double)GameClock.StepHz;
            if (dt < 60.0)
            {
                return StepOutcome.Wait;
            }
            return c.GetInt("away.homeFrames") == 0
                ? StepOutcome.Done($"在破碎都市 {dt:F0} 游戏秒（{c.GetInt("away.frames")} 帧，镜头一直在破碎都市，家园没人观察）")
                : StepOutcome.Fail($"远征期间有 {c.GetInt("away.homeFrames")} 帧在看家园（这一段要证明“不观察”）");
        }

        private static StepOutcome TickUplinked(JourneyContext c)
        {
            CountObserved(c);
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {FgjM3Common.Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {FgjM3Common.Label(b)}（准备开到撤离点）");
        }

        private static StepOutcome TickBackgroundConsistent(JourneyContext c)
        {
            if (c.StepElapsed < 0.3)
            {
                return StepOutcome.Wait;
            }
            long t0 = c.GetLong("away.tick");
            long t1 = GameClock.Ticks;
            double dt = (t1 - t0) / (double)GameClock.StepHz;
            long got = FgjM3Common.PortTotal(HomeValleyLayout.BuildingTypeCore, c.Get("inKey"), out bool on) - c.GetLong("away.got");
            double observed = double.Parse(c.Get("rateT2", "0"), CultureInfo.InvariantCulture);
            double awayRate = got / dt * 60.0;
            double expectGot = observed / 60.0 * dt;
            bool beltOk = on && observed > 0 && Math.Abs(got - expectGot) <= Math.Max(3.0, expectGot * 0.03);
            long[] p0 = c.Get("away.pumps").Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            long[] k0 = c.Get("away.tanks").Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            GridCell[] pumps = Pumps(c).ToArray();
            GridCell[] tanks = Tanks(c).ToArray();
            double mlPerSec = PipeNetworkService.Kernel.Config.PumpLitersPerMinute * 1000.0 / 60.0;
            double mlPerStep = mlPerSec / GameClock.StepHz;
            var lines = new List<string>();
            bool fluidOk = true;
            int unsaturated = 0;
            for (int i = 0; i < pumps.Length; i++)
            {
                long pumped = FgjM3Common.PumpTotalMl(pumps[i]) - p0[i];
                long stock = FgjM3Common.TankMl(tanks[i]);
                long cap = FgjM3Common.TankCapMl(tanks[i]);
                double expect = Math.Min(mlPerSec * dt, cap - k0[i]);
                bool ok = Math.Abs(pumped - expect) <= mlPerStep * 3 + 1 && Math.Abs(stock - (k0[i] + pumped)) <= 1;
                unsaturated += k0[i] + mlPerSec * dt < cap ? 1 : 0;
                fluidOk &= ok;
                lines.Add($"泵{i + 1} 离家期间抽 {pumped / 1000.0:F1} 升（按 {PipeNetworkService.Kernel.Config.PumpLitersPerMinute:F0} 升 / 分钟、储罐余量应为 {expect / 1000.0:F1} 升），储罐 {k0[i] / 1000.0:F1} → {stock / 1000.0:F1} 升");
            }
            GridCell probe = Route(c)[Route(c).Count / 2];
            string beltHover = BeltNetworkService.DescribeCell(probe) ?? string.Empty;
            string tankHover = PipeNetworkService.TryDescribeHover(St, tanks[1], out string title, out string body) ? title + " " + body.Split('\n').FirstOrDefault() : string.Empty;
            string report = $"离家 {dt:F1} 游戏秒（{c.GetInt("away.frames")} 帧里看家园 {c.GetInt("away.homeFrames")} 帧）：核心输入口收货 {got} 件 = {awayRate:F1} 件 / 分钟（观察时实测 {observed:F1}，按它应收 {expectGot:F0} 件）；" +
                            string.Join("；", lines) + $"；悬停读数“{beltHover.Replace("\n", " ")}”“{tankHover}”";
            return beltOk && fluidOk && unsaturated >= 1 && c.GetInt("away.homeFrames") == 0
                ? StepOutcome.Done("产量与后台一致：" + report)
                : StepOutcome.Fail($"产量与后台对不上（物品线 {beltOk}、流体 {fluidOk}、没满罐的泵 {unsaturated}）：" + report);
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次");
            c.Log(FgjM3Common.FrameReport("产线建成 / 测速 / 升级 / 离家段"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
