using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEngine;
using UnityEngine.UIElements;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG4-E2E-01：M4 反向旅程 FGJ-M4R（00 契约 IC-REQ-022 六类，落在家园经济上）。从主菜单“新建”出发（测试种子 1，与 FGJ-M4 不同，B25），全程正式输入。
    /// 1. 路径失败：提取钻指着不是矿脉的格——预览写原因（要放在金属 / 稀土矿脉上，为什么），单击不放、出拒绝音；改指矿脉放下。
    /// 2. 资源不足：装配站关掉“废料代付”排产搬运机——队列写缺什么、还差几件；打开代付——按废料当量补齐、出厂。
    /// 3. 暂停与倍速：暂停时提取钻 / 精炼炉的完成次数与进度冻结；0.5x 与 3x 按游戏时间同速（按真实时间约差 6 倍）。
    /// 4. 目标死亡：拆掉正在工作的精炼炉（提取钻的下游）——机器上门拆、缓存里的料退回仓库，提取钻输出堵塞并写明原因；Ctrl+Z 放回虚影、重建后配方仍在、产线恢复。
    /// 5. 断链：超控阵列只由一座 T2 电塔供电（关键材料用进度夹具，首领在 FG-M9 以后才有）；第 3 槽装上固件后拆掉电塔——阵列断电、第 3 槽失效、固件保留、通知；
    ///    Ctrl+Z 重建电塔——自动恢复（DEBT-FG4ECO11-05 / FGT-ECO-010 的“断电时失效，来电后恢复”）。
    /// 6. 存读档：产线跑着时保存并返回主菜单、读取——生产建筑的配方 / 进度 / 缓存 / 完成次数、超控阵列与信号核槽位、装配站队列逐项一致，读档后接着生产。
    /// </summary>
    public static class FgjM4ReverseJourney
    {
        public const string Id = "FGJ-M4R";

        public const int TestSeed = 1;

        private const string KeyMaterial = "listening_array_core";

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M4 反向：提取钻放错位置被拒→放到矿脉｜装配站缺材料→开代付出厂｜暂停冻结、0.5x / 3x 同速｜拆掉工作中的精炼炉→退料、下游堵塞→撤销重建恢复｜拆掉超控阵列的电塔→第 3 槽失效→撤销重建自动恢复｜存读档生产状态逐项一致",
            Seed = TestSeed,
            TotalTimeoutSeconds = 2600,
            OnFinish = Cleanup,
            Version = 1,
            CheckpointAfter = new[] { "salv2_wait", "m2_built" },
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { FgjM2Common.ResetSampling(); FgjM3Common.ResetSampling(); })),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 测试种子 1）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("research_fixture", "进度夹具：研发树开放前开局就能建的内容记为已研究（DEBT-FG5RND01-05；研究本身在 FGJ-M5 / M5R 走正式入口）", 10, FgjM5Common.ApplyLegacyResearchFixture, FgjM5Common.TickLegacyResearchFixture),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("sel_b2", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage", "右键点开局残骸（情境命令：拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完（废料 +60）", 180, null, FgjM2Common.TickSalvageDone),
                S("salv2_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完", 180, null, FgjM3Common.TickWreck2Done),

                S("plan", "看地形：规划一条小产线（金属提取钻 → 精炼炉 → 输入口）、回收站、核心配电范围外由一座 T2 电塔供电的超控阵列、需要的发电机 2", 30, null, TickPlan),
                S("m1_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m1_place", "先放发电机 2 与回收站和它的带", 300, null, c => P.TickActions(c, "m1", 40), retries: 2),
                S("m1_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("m1_built", "发电机与回收站建成，回收站开始拆废墟出废料", 900, null, c => TickBuilt(c, "m1keys", false)),

                // ── 1. 路径失败：提取钻放错位置 ──
                S("r1_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r1_pick", "“采集”分类点“提取钻”", 10, null, c => FgjM3Common.TickPick(c, P.Drill), retries: 2),
                S("r1_bad", "指着矿脉旁边不是矿脉的格：预览写“要放在金属或稀土矿脉上（为什么）”；单击不放、出拒绝音", 30, null, TickR1Rejected, retries: 2),
                S("m2_place", "改指矿脉放下提取钻；再放精炼炉、提取钻到精炼炉的带、精炼炉到输入口的带", 300, null, c => P.TickActions(c, "m2", 40), retries: 2),
                S("m2_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("m2_built", "提取钻、精炼炉与两段带建成、接上电", 900, null, c => TickBuilt(c, "m2keys", true)),
                S("rec_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("rec_furnace", "左键点精炼炉：打开通用面板", 20, null, c => P.TickOpenPanel(c, P.Furnace, P.P(c, "furnace")), retries: 2),
                S("rec_pick", "配方下拉选“合金（矿）”", 15, null, c => P.TickPickRecipe(c, "alloy_ore"), retries: 1),
                S("rec_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
                S("rec_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("line", "小产线跑起来：提取钻出矿、精炼炉出合金、合金进库存", 300, null, TickLineRuns),

                // ── 2. 资源不足：装配站缺材料 ──
                S("r2_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("r2_sub_off", "“缺材料时用废料代付”开着就点掉", 10, null, c => TickSubstitute(c, false), retries: 1),
                S("r2_produce", "点“生产 搬运机”：队列写缺什么、还差几件（没有零件 / 结构材产线）", 20, c => FgjM1Journey.ClickUi(c, P.FactoryHost, "ProduceBtn_Hauler"), TickR2Short, retries: 1),
                S("r2_sub_on", "点开“缺材料时用废料代付”：按废料当量补齐缺的材料", 10, null, c => TickSubstitute(c, true), retries: 1),
                S("r2_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("r2_done", "装配站用废料代付补齐、搬运机出厂（代付的废料记在这一单上）", 400, null, TickR2Produced),

                // ── 3. 暂停与倍速 ──
                S("r3_pause", "按暂停键（战略暂停）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("r3_frozen", "暂停 1.5 真实秒：提取钻与精炼炉的完成次数与进度不变", 15, null, TickR3Frozen),
                S("r3_resume", "按暂停键恢复", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done("恢复运行") : StepOutcome.Retry("还暂停着"), retries: 1),
                S("r3_half", "按 0.5 倍速键：提取钻按游戏时间仍是表里的速率（真实时间变慢）", 90, c => JourneyInput.PressAction(GameActionId.SpeedHalf), c => TickR3Rate(c, 0.5f, "half"), retries: 1),
                S("r3_triple", "按 3 倍速键：提取钻按游戏时间仍是表里的速率（真实时间变快）", 60, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickR3Rate(c, 3f, "triple"), retries: 1),

                // ── 4. 目标死亡：拆掉正在工作的精炼炉 ──
                S("r4_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r4_demo", "按拆除模式键（默认 X）", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, true),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没进拆除模式"), retries: 1),
                S("r4_click", "左键点正在工作的精炼炉：标记拆除（有确认框就点“确认”）", 30, null, TickR4Marked, retries: 2),
                S("r4_gone", "机器上门拆掉精炼炉：缓存里的矿 / 合金退回仓库、造价全额返还；提取钻输出堵塞，原因写明下游", 300, null, TickR4Gone),
                S("r4_undo", "按撤销键（默认 Ctrl+Z）：撤销这次拆除，精炼炉虚影放回原处", 10, c => JourneyInput.PressAction(GameActionId.Undo), TickR4UndoGhost, retries: 1),
                S("r4_demo_off", "按拆除模式键退出", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("退出拆除模式") : StepOutcome.Retry("还在拆除模式"), retries: 1),
                S("r4_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r4_back", "机器重建精炼炉：配方仍是“合金（矿）”（配方记忆），提取钻恢复出矿、精炼炉恢复出合金", 400, null, TickR4Back),

                // ── 5. 断链：超控阵列只由一座电塔供电 ──
                S("r5_fixture", "进度夹具：核心保管库 +1 监听阵列核（关键材料由首领给出，首领在 FG-M9 以后，DEBT-FG4E2E01-02）", 10, ApplyKeyFixture, TickKeyFixture),
                S("r5_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r5_place", "放一座 T2 电塔（接核心电网）与核心配电范围外的超控阵列（只由这座电塔供电）", 120, null, c => P.TickActions(c, "m3", 40), retries: 2),
                S("r5_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r5_built", "机器从归还核心取关键材料施工：超控阵列建成、经电塔接上电，信号核解锁第 3 槽", 900, null, TickR5Built),
                S("r5_sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("r5_print", "刻印下拉选一种固件，点“刻印”", 15, null, TickR5Printed, retries: 1),
                S("r5_slot", "点第 3 槽，点“装入”：固件装进第 3 槽", 15, null, TickR5Equipped, retries: 1),
                S("r5_sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                S("r5_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r5_demo", "按拆除模式键", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, true),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没进拆除模式"), retries: 1),
                S("r5_ask", "左键点电塔：弹确认框，写明拆掉的后果（1 座建筑——超控阵列——失去电网连接）", 20, null, TickR5Ask, retries: 2),
                S("r5_ok", "点“确认”：电塔标记拆除", 10, c => FgjM1Journey.ClickUi(c, "[UiKitOverlayHost]", "ConfirmOk"), TickR5Marked, retries: 1),
                S("r5_cut", "机器上门拆掉电塔：超控阵列断电，第 3 槽失效（固件留在槽里），HUD 与通知写明", 300, null, TickR5Offline),
                S("r5_undo", "按撤销键：撤销拆除，电塔虚影放回原处", 10, c => JourneyInput.PressAction(GameActionId.Undo), TickR5UndoGhost, retries: 1),
                S("r5_demo_off", "按拆除模式键退出", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("退出拆除模式") : StepOutcome.Retry("还在拆除模式"), retries: 1),
                S("r5_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r5_back", "机器重建电塔：超控阵列来电，第 3 槽自动恢复（不用重装固件），恢复通知", 300, null, TickR5Online),

                // ── 6. 存读档 ──
                S("r6_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("r6_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("r6_menu", "回到主菜单：存档里生产建筑的配方 / 进度 / 缓存 / 完成次数、信号核槽位、超控阵列与存档那一刻一致", 60, null, TickMenuAfterSave),
                S("r6_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("r6_loaded", "读档进入家园：第一帧逐项一致；读档后接着生产", 120, null, TickLoaded),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        // ── 规划 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            FgjM4Common.MiniPlan mp = P.PlanMini(s, out string why);
            if (mp == null)
            {
                return StepOutcome.Fail("规划不出小产线：" + why);
            }
            foreach (FgjM4Common.Placed b in mp.Plan.Buildings)
            {
                P.Remember(c, b);
            }
            P.SetP(c, "pole", mp.Pole);
            P.SavePath(c, "path.r_recycler", mp.Plan.R("r_recycler").Path);
            c.SetInt("path.r_recycler.dir", mp.Plan.R("r_recycler").GoalDir);
            P.SavePath(c, "path.drillM", mp.DrillRoute);
            c.SetInt("path.drillM.dir", mp.Plan.R("drillM").GoalDir);
            P.SavePath(c, "path.r_out", mp.OutRoute);
            c.SetInt("path.r_out.dir", mp.OutDir);
            c.Set("gens", FgjM3Common.EncodeCells(mp.Plan.Generators));
            c.Set("poles", FgjM3Common.EncodeCells(mp.Poles));
            var m1 = new List<string>();
            foreach (GridCell g in mp.Plan.Generators)
            {
                m1.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {P.Cell(g)}");
            }
            m1.Add(P.ActB(mp.Plan.B("recycler")));
            m1.Add(P.ActR(P.BeltT1, "path.r_recycler", mp.Plan.R("r_recycler").GoalDir, "回收站废料带 → " + mp.Plan.RecyclerSink));
            P.SetActions(c, "m1", m1);
            c.Set("m1keys", "recycler");
            var m2 = new List<string>();
            foreach (GridCell pl in mp.Poles)
            {
                m2.Add($"B|{P.PoleT2}|{pl.X}|{pl.Y}|0|T2 电塔 {P.Cell(pl)}");
            }
            m2.Add(P.ActB(mp.Plan.B("drillM")));
            m2.Add(P.ActB(mp.Plan.B("furnace")));
            m2.Add(P.ActR(P.BeltT1, "path.drillM", mp.Plan.R("drillM").GoalDir, "提取钻 → 精炼炉"));
            m2.Add(P.ActR(P.BeltT1, "path.r_out", mp.OutDir, "精炼炉 → " + mp.OutSink));
            P.SetActions(c, "m2", m2);
            c.Set("m2keys", "drillM,furnace");
            P.SetActions(c, "m3", new[]
            {
                $"B|{P.PoleT2}|{mp.Pole.X}|{mp.Pole.Y}|0|T2 电塔 {P.Cell(mp.Pole)}（超控阵列唯一的供电）",
                P.ActB(mp.Plan.B("array")),
            });
            GridCell core = HomeGridService.CorePivot(s);
            return StepOutcome.Done($"精炼炉 {P.Cell(mp.Furnace)}、提取钻 {P.Cell(mp.Drill)}（带 {mp.DrillRoute.Count} 格）、精炼炉 → {mp.OutSink}（{mp.OutRoute.Count} 格）；" +
                                    $"回收站 {P.Cell(mp.Plan.B("recycler").Pivot)}；超控阵列 {P.Cell(mp.Array)}（离核心 {P.NearestDist(GameLogic.Campaign.Signal.OverrideArrayService.TypeId, mp.Array, 0, core):F1} 格，核心配电半径外）由 T2 电塔 {P.Cell(mp.Pole)} 供电；电：{mp.Plan.PowerNote}");
        }

        private static StepOutcome TickBuilt(JourneyContext c, string keysKey, bool needPower)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var missing = new List<string>();
            foreach (string k in c.Get(keysKey, string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!P.BuildingReady(s, c.Get(k + ".type"), P.P(c, k), out BuildingRecord b) || (needPower && b.PowerState != BuildingPowerState.Powered))
                {
                    missing.Add(k);
                }
            }
            foreach (GridCell g in FgjM3Common.DecodeCells(c.Get("gens")))
            {
                if (!P.BuildingReady(s, HomeValleyLayout.BuildingTypeGenerator2, g, out _))
                {
                    missing.Add("发电机2");
                }
            }
            string routes = keysKey == "m1keys" ? "path.r_recycler" : "path.drillM,path.r_out";
            int unbuilt = routes.Split(',').Sum(r => P.LoadPath(c, r).Count(p => !P.RouteBuilt(s, new List<GridCell> { p }, true)));
            if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                c.Log($"施工中：还没好 [{string.Join(",", missing)}]、带还差 {unbuilt} 格；废料 {s.Scrap}；{P.PowerLine(s)}");
            }
            if (missing.Count > 0 || unbuilt > 0)
            {
                return StepOutcome.Wait;
            }
            if (keysKey == "m1keys" && (P.Prod(P.Bld(c, "recycler"))?.Rec.RuinRecovered ?? 0) < 4)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"建成（{c.StepElapsed:F0} 秒）：{c.Get(keysKey)}；废料 {s.Scrap}；{P.PowerLine(s)}");
        }

        // ── 1. 路径失败 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickR1Rejected(JourneyContext c)
        {
            CampaignState s = St;
            GridCell drill = P.P(c, "drillM");
            string bk = FgjM3Common.SK(c, "bad");
            if (string.IsNullOrEmpty(c.Get(bk)))
            {
                byte metal = P.TerrainCode("ore_metal");
                byte rare = P.TerrainCode("ore_rare");
                GridCell? bad = null;
                for (int r = 3; r <= 12 && bad == null; r++)
                {
                    for (int dy = -r; dy <= r && bad == null; dy++)
                    {
                        for (int dx = -r; dx <= r && bad == null; dx++)
                        {
                            var p = new GridCell(drill.X + dx, drill.Y + dy);
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                            {
                                continue;
                            }
                            List<GridCell> fp = P.Footprint(P.Drill, p, 0);
                            if (fp.Any(x => P.IsTerrain(s, x, metal) || P.IsTerrain(s, x, rare)))
                            {
                                continue;
                            }
                            GridPlacementResult v = HomeGridService.ValidatePlacement(s, P.Drill, p, 0, checkCost: false);
                            if (v.Reasons.Count == 1 && v.Reasons[0].Code == GridBlockReason.NeedsTerrain && FgjM3Common.Clear(FgjM3Common.Ground(p), out _))
                            {
                                bad = p;
                            }
                        }
                    }
                }
                if (bad == null)
                {
                    return StepOutcome.Fail("提取钻旁边找不到“只因为不是矿脉而不能放”的空地");
                }
                FgjM3Common.SetCell(c, bk, bad.Value);
                c.Set(bk, "1");
                c.SetInt(FgjM3Common.SK(c, "deny0"), Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied));
            }
            GridCell at = FgjM3Common.GetCell(c, bk);
            if (FgjM3Common.Mode.GhostRotation % 360 != 0)
            {
                if (FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "rot")) > 300)
                {
                    c.SetLong(FgjM3Common.SK(c, "rot"), FgjM3Common.NowMs());
                    JourneyInput.PressAction(GameActionId.Rotate);
                }
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "hover", () => FgjM3Common.TryHover(FgjM3Common.Ground(at))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "hover") < 600)
            {
                return StepOutcome.Wait;
            }
            string status = FgjM3Common.Mode.StatusText ?? string.Empty;
            GridPlacementResult pv = FgjM3Common.Mode.Preview;
            if (FgjM3Common.Mode.HoverCell != at || pv == null || pv.Ok)
            {
                return FgjM3Common.SinceMs(c, "hover") < 2000 ? StepOutcome.Wait : StepOutcome.Retry($"指着 {P.Cell(at)} 时预览没有拒绝（状态行“{status}”）");
            }
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(at))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 700)
            {
                return StepOutcome.Wait;
            }
            bool placed = P.BuildingAtPivot(St, P.Drill, at) != null;
            int denied = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) - c.GetInt(FgjM3Common.SK(c, "deny0"));
            string after = FgjM3Common.Mode.StatusText ?? string.Empty;
            bool reason = (status + after).Contains("矿脉") && (status + after).Contains("提取钻");
            return !placed && denied >= 1 && reason
                ? StepOutcome.Done($"指着 {P.Cell(at)}（空地）：状态行“{status.Split('\n').FirstOrDefault()}”；单击不放、拒绝音 {denied} 次（“{after.Split('\n').FirstOrDefault()}”）")
                : StepOutcome.Fail($"放错位置没被正确拒绝：放下了 {placed}、拒绝音 {denied}、状态行“{status}” / “{after}”");
        }

        // ── 小产线 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickLineRuns(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            long d = P.Completed(P.Bld(c, "drillM"));
            long f = P.Completed(P.Bld(c, "furnace"));
            int alloy = P.Stock("alloy");
            return d >= 4 && f >= 2 && alloy >= 1
                ? StepOutcome.Done($"小产线跑起来：提取钻 {d} 次、精炼炉 {f} 次、库存合金 {alloy}")
                : StepOutcome.Wait;
        }

        // ── 2. 资源不足 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickSubstitute(JourneyContext c, bool want)
        {
            // 面板刚打开 / 开关刚切换时这一帧还在重排（材料与代付说明几行会变长），等排好版再判定、再进下一步点“生产”。
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (AssemblyMaterials.ScrapSubstitute(St) == want)
            {
                if (FgjM3Common.Done(c, "tog") && FgjM3Common.SinceMs(c, "tog") < 600)
                {
                    return StepOutcome.Wait; // 刚点了开关：等面板按新状态重排完，下一步再点“生产”
                }
                return StepOutcome.Done($"“缺材料时用废料代付”{(want ? "已开" : "已关")}（“{JourneyInput.FindUitk<Label>(P.FactoryHost, "SubstituteNote")?.text}”）");
            }
            Toggle t = JourneyInput.FindUitk<Toggle>(P.FactoryHost, "SubstituteToggle");
            if (!FgjM3Common.Once(c, "tog", () => JourneyInput.ClickElement(t)))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tog") < 1200 ? StepOutcome.Wait : StepOutcome.Retry("点了代付开关后没变：" + JourneyInput.LastUiFailure);
        }

        private static FactoryQueueItemRecord HaulerItem() =>
            St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintHaulerId);

        private static StepOutcome TickR2Short(JourneyContext c)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            FactoryQueueItemRecord item = HaulerItem();
            if (item == null)
            {
                return StepOutcome.Retry($"点“生产”后队列里没有搬运机（{FgjM1Journey.UiFail(c)}）");
            }
            string wait = HomeValleyFactory.DescribeWait(item) ?? item.BlockedReason ?? string.Empty;
            c.Set("r2item", item.QueueItemId);
            c.SetInt("r2scrap0", St.Scrap);
            c.SetLong("r2line0", St.Economy.MachinesFromLine);
            bool waiting = item.State == FactoryQueueState.WaitingResources && !item.MaterialsTaken;
            bool says = wait.Contains("缺") && (wait.Contains("结构材") || wait.Contains("零件") || wait.Contains("电子件"));
            return waiting && says && !Localization.GameText.ContainsMarker(wait)
                ? StepOutcome.Done($"搬运机入队、等材料：“{wait.Replace("\n", " ")}”（没有零件 / 结构材产线，材料一件没取、废料不动）")
                : StepOutcome.Fail($"缺材料时队列不对：{item.State}、已取料 {item.MaterialsTaken}、“{wait}”");
        }

        private static StepOutcome TickR2Produced(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            FactoryQueueItemRecord item = HomeValleyFactory.Find(St, c.Get("r2item"));
            if (item == null || item.State == FactoryQueueState.Failed || item.State == FactoryQueueState.Cancelled)
            {
                return StepOutcome.Fail($"这一单 {item?.State}：{item?.BlockedReason}");
            }
            if (item.State != FactoryQueueState.Completed)
            {
                return StepOutcome.Wait;
            }
            return item.SubstituteScrap > 0
                ? StepOutcome.Done($"打开代付后搬运机出厂：缺的材料按废料当量代付 {item.SubstituteScrap} 废料（“用产线材料造出的机器”计数不变：{c.GetLong("r2line0")} → {St.Economy.MachinesFromLine}）")
                : StepOutcome.Fail($"出厂了但没有记代付（代付 {item.SubstituteScrap}）");
        }

        // ── 3. 暂停与倍速 ────────────────────────────────────────────────────────────

        private static StepOutcome TickR3Frozen(JourneyContext c)
        {
            ProductionService.Producer d = P.Prod(P.Bld(c, "drillM"));
            ProductionService.Producer f = P.Prod(P.Bld(c, "furnace"));
            string Snap() => $"{GameClock.Ticks}/{d.Rec.Completed}/{d.Rec.Progress}/{f.Rec.Completed}/{f.Rec.Progress}";
            if (!FgjM3Common.Done(c, "s0"))
            {
                c.Set(FgjM3Common.SK(c, "snap"), Snap());
                FgjM3Common.Mark(c, "s0");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            return Snap() == c.Get(FgjM3Common.SK(c, "snap")) && GameClock.Paused
                ? StepOutcome.Done($"暂停 {c.StepElapsed:F1} 真实秒：世界步 / 提取钻完成次数与进度 / 精炼炉完成次数与进度都不变（{Snap()}）")
                : StepOutcome.Fail($"暂停中变了：{c.Get(FgjM3Common.SK(c, "snap"))} → {Snap()}");
        }

        private static StepOutcome TickR3Rate(JourneyContext c, float speed, string key)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            ProductionService.Producer d = P.Prod(P.Bld(c, "drillM"));
            if (!FgjM3Common.Done(c, "t0"))
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                if (!Mathf.Approximately(GameClock.Speed, speed))
                {
                    return StepOutcome.Retry($"按倍速键后是 {GameClock.Speed}x");
                }
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.SetLong(FgjM3Common.SK(c, "d0"), d.Rec.Completed);
                c.SetLong(FgjM3Common.SK(c, "real0"), FgjM3Common.NowMs());
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            double gameSec = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            if (gameSec < 20.0)
            {
                return StepOutcome.Wait;
            }
            double realSec = (FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "real0"))) / 1000.0;
            long n = d.Rec.Completed - c.GetLong(FgjM3Common.SK(c, "d0"));
            double perGameMin = n / gameSec * 60.0;
            double perRealMin = n / realSec * 60.0;
            double design = 60.0 / Math.Max(0.01, d.Def.CycleSeconds) * Math.Max(1, d.Def.CycleAmount);
            c.Set("rate." + key, perRealMin.ToString("R", CultureInfo.InvariantCulture));
            string ratio = string.Empty;
            bool ratioOk = true;
            if (key == "triple" && double.TryParse(c.Get("rate.half"), NumberStyles.Float, CultureInfo.InvariantCulture, out double half) && half > 0)
            {
                double r = perRealMin / half;
                ratioOk = r > 3.0;
                ratio = $"；按真实时间是 0.5x 时的 {r:F1} 倍（理论 6 倍，帧节奏有抖动）";
            }
            return Math.Abs(perGameMin - design) <= Math.Max(2.0, design * 0.08) && ratioOk
                ? StepOutcome.Done($"{speed}x：{gameSec:F1} 游戏秒（{realSec:F1} 真实秒）提取钻出矿 {n} 份 = 每游戏分钟 {perGameMin:F1}（表里 {design:F0}）{ratio}")
                : StepOutcome.Fail($"{speed}x 时提取钻的速率不对：每游戏分钟 {perGameMin:F1}（表里 {design:F0}）{ratio}");
        }

        // ── 4. 目标死亡：拆掉工作中的精炼炉 ─────────────────────────────────────────────

        private static StepOutcome TickR4Marked(JourneyContext c)
        {
            CampaignState s = St;
            BuildingRecord f = P.Bld(c, "furnace");
            if (f == null)
            {
                return StepOutcome.Fail("精炼炉不见了");
            }
            if (!FgjM3Common.Done(c, "s0"))
            {
                ProductionService.Producer p = P.Prod(f);
                c.SetInt("r4.ore0", P.Stock("metal_ore"));
                c.SetInt("r4.alloy0", P.Stock("alloy"));
                c.SetInt("r4.scrap0", s.Scrap);
                c.SetInt("r4.invested", f.InvestedScrap);
                c.SetInt("r4.held", ProductionService.Total(p.Rec.In) + ProductionService.Total(p.Rec.Out));
                c.Set("r4.id", f.BuildingId);
                FgjM3Common.Mark(c, "s0");
            }
            if (HomeGridService.IsMarkedForDemolish(s, c.Get("r4.id")))
            {
                return StepOutcome.Done($"精炼炉标记拆除（缓存里 {c.GetInt("r4.held")} 件料），等机器上门；状态行“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”");
            }
            if (UiConfirmDialog.IsOpen)
            {
                if (!FgjM3Common.Once(c, "ok", () => JourneyInput.ClickUitk("[UiKitOverlayHost]", "ConfirmOk")))
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(P.P(c, "furnace")))))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "click") < 2500 ? StepOutcome.Wait : StepOutcome.Retry($"点精炼炉后没有标记拆除（状态行“{FgjM3Common.Mode.StatusText}”）");
        }

        private static StepOutcome TickR4Gone(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (HomeGridService.FindBuilding(s, c.Get("r4.id")) != null)
            {
                return StepOutcome.Wait;
            }
            ProductionService.Producer d = P.Prod(P.Bld(c, "drillM"));
            string reason = d != null ? ProductionService.ReasonText(s, d) : string.Empty;
            if (d == null || d.State != ProdState.OutputBlocked)
            {
                return c.StepElapsed < 120 ? StepOutcome.Wait : StepOutcome.Fail($"精炼炉拆掉后提取钻没有输出堵塞（{d?.State}：{reason}）");
            }
            int back = P.Stock("metal_ore") + P.Stock("alloy") - c.GetInt("r4.ore0") - c.GetInt("r4.alloy0");
            int scrapBack = s.Scrap - c.GetInt("r4.scrap0");
            int invested = c.GetInt("r4.invested");
            bool held = back >= c.GetInt("r4.held");
            // 造价全额返还：废料至少涨了实际投入那么多（这段时间回收站还在出废料，所以只能断言“不少于”，多出来的是回收站的产出）。
            bool refunded = invested > 0 && scrapBack >= invested;
            return held && refunded && !Localization.GameText.ContainsMarker(reason)
                ? StepOutcome.Done($"机器上门拆掉精炼炉：缓存里的 {c.GetInt("r4.held")} 件料退回仓库（金属矿 + 合金库存 +{back}，含提取钻这段时间的产出）、实际投入的 {invested} 废料全额返还" +
                                   $"（废料 {c.GetInt("r4.scrap0")} → {s.Scrap}，+{scrapBack}，多出的是回收站同期产出）；" +
                                   $"提取钻“{ProductionService.StateText(d)}：{reason.Replace("\n", " ")}”")
                : StepOutcome.Fail($"拆除后退料 / 返还不对：库存 +{back}（缓存 {c.GetInt("r4.held")}）、废料 +{scrapBack}（实际投入 {invested}）、提取钻“{reason}”");
        }

        private static StepOutcome TickR4UndoGhost(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = HomeGridService.BuildingAt(St, P.P(c, "furnace"));
            return b != null && HomeValleyController.IsPlannedGhost(b) && b.BuildingTypeId == P.Furnace
                ? StepOutcome.Done($"Ctrl+Z：撤销拆除，精炼炉虚影放回原处（“{FgjM3Common.Mode.StatusText?.Split('\n').FirstOrDefault()}”）")
                : StepOutcome.Retry($"撤销后原处没有精炼炉虚影（“{FgjM3Common.Mode.StatusText}”）");
        }

        private static StepOutcome TickR4Back(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!P.BuildingReady(St, P.Furnace, P.P(c, "furnace"), out BuildingRecord f))
            {
                return StepOutcome.Wait;
            }
            ProductionService.Producer p = P.Prod(f);
            if (!FgjM3Common.Done(c, "f0"))
            {
                c.SetLong(FgjM3Common.SK(c, "f0v"), p.Rec.Completed);
                c.SetLong(FgjM3Common.SK(c, "d0v"), P.Completed(P.Bld(c, "drillM")));
                FgjM3Common.Mark(c, "f0");
                return StepOutcome.Wait;
            }
            long fd = p.Rec.Completed - c.GetLong(FgjM3Common.SK(c, "f0v"));
            long dd = P.Completed(P.Bld(c, "drillM")) - c.GetLong(FgjM3Common.SK(c, "d0v"));
            if (p.Recipe == null || p.Recipe.Id != "alloy_ore")
            {
                return StepOutcome.Fail($"重建后精炼炉的配方是 {p.Recipe?.Id ?? "没选"}（应沿用上一次的“合金（矿）”）");
            }
            return fd >= 1 && dd >= 2
                ? StepOutcome.Done($"精炼炉重建（配方仍是“{p.Recipe.Name}”）：精炼炉又完成 {fd} 次、提取钻又出 {dd} 份矿")
                : StepOutcome.Wait;
        }

        // ── 5. 断链：超控阵列 ─────────────────────────────────────────────────────────

        /// <summary>进度夹具（DEBT-FG4E2E01-02）：关键材料“监听阵列核”由首领给出，首领在 FG-M9 以后；按入库同一入口（<see cref="HomeInventory.Add(CampaignState, string, int, bool)"/>）放进核心保管库。之后取料、施工、供电、装槽全走正式流程。</summary>
        private static void ApplyKeyFixture(JourneyContext c)
        {
            c.SetInt("key0", HomeInventory.Stock(St, KeyMaterial));
            HomeInventory.Add(St, KeyMaterial, 1, clampToSpace: false);
        }

        private static StepOutcome TickKeyFixture(JourneyContext c) =>
            HomeInventory.Stock(St, KeyMaterial) == c.GetInt("key0") + 1
                ? StepOutcome.Done($"核心保管库监听阵列核 {c.GetInt("key0")} → {HomeInventory.Stock(St, KeyMaterial)}（进度夹具；其余全走正式入口）")
                : StepOutcome.Fail("夹具没生效");

        private static StepOutcome TickR5Built(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!P.BuildingReady(s, P.PoleT2, P.P(c, "pole"), out _) || !P.BuildingReady(s, OverrideArrayService.TypeId, P.P(c, "array"), out BuildingRecord arr)
                || arr.PowerState != BuildingPowerState.Powered)
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"施工中：废料 {s.Scrap}、保管库监听阵列核 {HomeInventory.Stock(s, KeyMaterial)}；{P.PowerLine(s)}");
                }
                return StepOutcome.Wait;
            }
            int unlocked = SignalCoreService.UnlockedSlots(s);
            int active = SignalCoreService.ActiveSlots(s);
            return unlocked >= 3 && active >= 3
                ? StepOutcome.Done($"超控阵列建成、经电塔接上电：信号核解锁 {unlocked} 槽、生效 {active} 槽；保管库监听阵列核 {HomeInventory.Stock(s, KeyMaterial)}（取去施工了）")
                : StepOutcome.Fail($"阵列建成了但槽位不对：解锁 {unlocked}、生效 {active}");
        }

        private static StepOutcome TickR5Printed(JourneyContext c)
        {
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "print"))
            {
                DropdownField print = JourneyInput.FindUitk<DropdownField>("[SignalCoreHost]", "SignalPrintChoice");
                string choice = print?.choices?.FirstOrDefault(x => !string.IsNullOrEmpty(x));
                if (print == null || choice == null || !JourneyInput.IsClickable(print)) // 确认能点没被挡住后设值（不点开弹出菜单，见 FgjM4Common.PickableInView）
                {
                    return StepOutcome.Retry("刻印下拉不能用：" + JourneyInput.LastUiFailure);
                }
                print.value = choice;
                c.SetInt("scrapBeforePrint", St.Scrap);
                c.SetInt("bag0", SignalCoreService.BagFirmware(St).Count);
                if (!JourneyInput.ClickUitk("[SignalCoreHost]", "SignalPrint"))
                {
                    return StepOutcome.Retry("点不到“刻印”：" + JourneyInput.LastUiFailure);
                }
                c.Set("printChoice", choice);
                FgjM3Common.Mark(c, "print");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            PrimitiveChipRecord chip = PrimitiveInventory.Find(St, hud?.SelectedPartId);
            return chip != null && chip.State == PrimitiveChipState.Bag && SignalCoreService.BagFirmware(St).Count == c.GetInt("bag0") + 1
                ? StepOutcome.Done($"刻印“{c.Get("printChoice")}”：一枚芯片进基元仓、被选中（扣 {c.GetInt("scrapBeforePrint") - St.Scrap} 废料）")
                : StepOutcome.Retry($"刻印没成功（{hud?.FeedbackText}）");
        }

        private static StepOutcome TickR5Equipped(JourneyContext c)
        {
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            if (!FgjM3Common.Done(c, "slot"))
            {
                if (!JourneyInput.ClickUitk("[SignalCoreHost]", "SignalSlot2"))
                {
                    return StepOutcome.Retry("点不到第 3 槽：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "slot");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "equip"))
            {
                if (c.StepElapsed < 0.5)
                {
                    return StepOutcome.Wait;
                }
                if (!JourneyInput.ClickUitk("[SignalCoreHost]", "SignalEquip"))
                {
                    return StepOutcome.Retry("点不到“装入”：" + JourneyInput.LastUiFailure);
                }
                FgjM3Common.Mark(c, "equip");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            string id = SignalCoreService.SlotContentId(St, 2);
            c.Set("slot3", id);
            return id.Length > 0 && SignalCoreService.IsSlotActive(St, 2)
                ? StepOutcome.Done($"固件装进第 3 槽（{FirmwareKinds.DisplayName(id)}）；HUD“{hud?.EntryText}”")
                : StepOutcome.Retry($"第 3 槽还是空的（{hud?.FeedbackText}）");
        }

        private static StepOutcome TickR5Ask(JourneyContext c)
        {
            GridCell pole = P.P(c, "pole");
            BuildingRecord b = P.BuildingAtPivot(St, P.PoleT2, pole);
            if (b == null)
            {
                return StepOutcome.Fail("电塔不见了");
            }
            c.Set("poleId", b.BuildingId);
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(pole))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 600)
            {
                return StepOutcome.Wait;
            }
            ConfirmRequest r = UiConfirmDialog.Current;
            if (r == null)
            {
                return StepOutcome.Retry($"拆除模式点电塔没有弹确认框（“{FgjM3Common.Mode.StatusText}”）");
            }
            string lines = string.Join("；", r.Consequences.Concat(r.Lines));
            c.SetInt("poleScrap0", St.Scrap);
            c.SetInt("poleInvested", b.InvestedScrap);
            // 这座电塔是超控阵列唯一的供电：确认框写明“拆掉后 1 座建筑失去电网连接”（FG3-LOG-06 / FG00 B04 的后果行；只写数量、不点名，
            // 点名是哪座、第几槽会失效由断电后的通知、HUD 与建筑面板写，见 r5_off）。
            string orphanLine = GameText.Format("power.demolish.orphans", 1);
            return lines.Contains(orphanLine)
                ? StepOutcome.Done($"确认框“{r.Title}”：{lines}")
                : StepOutcome.Fail($"确认框没写拆掉后超控阵列（1 座建筑）会失去电网连接（“{r.Title}”：{lines}）");
        }

        private static StepOutcome TickR5Marked(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string id = c.Get("poleId");
            return !UiConfirmDialog.IsOpen && HomeGridService.IsMarkedForDemolish(St, id)
                ? StepOutcome.Done("点“确认”：电塔标记拆除，等机器上门")
                : StepOutcome.Retry($"确认后没有标记拆除（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickR5Offline(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (HomeGridService.FindBuilding(s, c.Get("poleId")) != null)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord arr = P.BuildingAtPivot(s, OverrideArrayService.TypeId, P.P(c, "array"));
            string hud = SignalCoreHudUIToolkit.Instance?.EntryText ?? string.Empty;
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type?.Id == "override_offline");
            bool offline = arr != null && arr.PowerState != BuildingPowerState.Powered && SignalCoreService.ActiveSlots(s) == 2 && !SignalCoreService.IsSlotActive(s, 2);
            bool kept = SignalCoreService.SlotContentId(s, 2) == c.Get("slot3");
            if (!offline || note == null)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"拆掉电塔后：阵列 {arr?.PowerState}、生效槽 {SignalCoreService.ActiveSlots(s)}、通知 {note != null}");
            }
            int poleBack = s.Scrap - c.GetInt("poleScrap0");
            bool poleRefund = c.GetInt("poleInvested") > 0 && poleBack >= c.GetInt("poleInvested");
            return kept && hud.Contains("失效") && poleRefund
                ? StepOutcome.Done($"电塔拆掉（实际投入的 {c.GetInt("poleInvested")} 废料全额返还；废料 +{poleBack}，多出的是回收站同期产出）：超控阵列 {arr.PowerState}，第 3 槽失效、固件留在槽里（{FirmwareKinds.DisplayName(c.Get("slot3"))}）；HUD“{hud}”；通知“{note.Text}”")
                : StepOutcome.Fail($"失效表现 / 返还不对：固件还在 {kept}、HUD“{hud}”、废料 +{poleBack}（实际投入 {c.GetInt("poleInvested")}）");
        }

        private static StepOutcome TickR5UndoGhost(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = HomeGridService.BuildingAt(St, P.P(c, "pole"));
            return b != null && HomeValleyController.IsPlannedGhost(b) && b.BuildingTypeId == P.PoleT2
                ? StepOutcome.Done("Ctrl+Z：撤销拆除，电塔虚影放回原处")
                : StepOutcome.Retry($"撤销后原处没有电塔虚影（“{FgjM3Common.Mode.StatusText}”）");
        }

        private static StepOutcome TickR5Online(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord arr = P.BuildingAtPivot(s, OverrideArrayService.TypeId, P.P(c, "array"));
            if (!P.BuildingReady(s, P.PoleT2, P.P(c, "pole"), out _) || arr == null || arr.PowerState != BuildingPowerState.Powered || !SignalCoreService.IsSlotActive(s, 2))
            {
                return StepOutcome.Wait;
            }
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type?.Id == "override_online");
            string hud = SignalCoreHudUIToolkit.Instance?.EntryText ?? string.Empty;
            return SignalCoreService.SlotContentId(s, 2) == c.Get("slot3") && !hud.Contains("失效")
                ? StepOutcome.Done($"电塔重建：超控阵列来电，第 3 槽自动恢复（固件仍是 {FirmwareKinds.DisplayName(c.Get("slot3"))}，没重装）；HUD“{hud}”；通知“{note?.Text}”")
                : StepOutcome.Fail($"恢复不对：第 3 槽 {SignalCoreService.SlotContentId(s, 2)}、HUD“{hud}”");
        }

        // ── 6. 存读档 ─────────────────────────────────────────────────────────────────

        /// <summary>生产相关的存档摘要：生产建筑（配方 / 进度 / 输入输出缓存 / 完成次数）、超控阵列、信号核槽位、装配站队列与机器数、库存。</summary>
        private static string Digest(CampaignState s, bool withProgress)
        {
            var parts = new List<string>();
            foreach (ProducerRecord r in (s.Economy?.Producers ?? Array.Empty<ProducerRecord>()).Where(x => x != null).OrderBy(x => x.BuildingId, StringComparer.Ordinal))
            {
                parts.Add($"{r.BuildingId}:{r.RecipeId}:{r.Completed}:{(withProgress ? r.Progress.ToString(CultureInfo.InvariantCulture) : "-")}:" +
                          string.Join("+", (r.In ?? Array.Empty<ItemStackRecord>()).Where(x => x != null && x.Amount > 0).Select(x => x.ItemId + "=" + x.Amount)) + "/" +
                          string.Join("+", (r.Out ?? Array.Empty<ItemStackRecord>()).Where(x => x != null && x.Amount > 0).Select(x => x.ItemId + "=" + x.Amount)));
            }
            BuildingRecord arr = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).FirstOrDefault(b => b != null && b.BuildingTypeId == OverrideArrayService.TypeId);
            parts.Add($"阵列:{arr?.ConstructionState}/{arr?.Tier}");
            parts.Add("槽:" + string.Join(",", Enumerable.Range(0, SignalCoreService.MaxSlots).Select(i => SignalCoreService.SlotContentId(s, i))));
            parts.Add("队列:" + string.Join(",", (s.FactoryQueues ?? Array.Empty<FactoryQueueItemRecord>()).Where(q => q != null).Select(q => q.BlueprintId + "=" + q.State)));
            parts.Add($"合金:{HomeInventory.Stock(s, "alloy")}");
            return string.Join("｜", parts);
        }

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
            ProductionService.Producer f = P.Prod(P.Bld(c, "furnace"));
            c.Set("pre", Digest(St, true));
            c.Set("preStable", Digest(St, false));
            c.SetLong("preTicks", GameClock.Ticks);
            c.SetLong("preFurnace", f.Rec.Completed);
            c.Set("preSlots", string.Join(",", Enumerable.Range(0, SignalCoreService.MaxSlots).Select(i => SignalCoreService.SlotContentId(St, i))));
            c.SetInt("preActive", SignalCoreService.ActiveSlots(St));
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：{c.Get("pre")}");
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
            // 读盘的状态没有运行时内核：只比生产记录、阵列、队列、库存（槽位比对放到读档后，信号核要在运行时初始化）。
            string disk = Digest(onDisk.State, true);
            string want = c.Get("pre");
            string diskCore = string.Join("｜", disk.Split('｜').Where(x => !x.StartsWith("槽:", StringComparison.Ordinal)));
            string wantCore = string.Join("｜", want.Split('｜').Where(x => !x.StartsWith("槽:", StringComparison.Ordinal)));
            return diskCore == wantCore
                ? StepOutcome.Done($"回到主菜单；存档里 {diskCore}，与存档那一刻逐项一致")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {wantCore}\n      存档里 {diskCore}");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "cmp"))
            {
                long dTicks = GameClock.Ticks - c.GetLong("preTicks");
                string slots = string.Join(",", Enumerable.Range(0, SignalCoreService.MaxSlots).Select(i => SignalCoreService.SlotContentId(s, i)));
                string stable = Digest(s, false);
                bool exact = dTicks != 0 || Digest(s, true) == c.Get("pre");
                bool ok = dTicks >= 0 && dTicks <= GameClock.StepHz * 5 && slots == c.Get("preSlots") && SignalCoreService.ActiveSlots(s) == c.GetInt("preActive") && exact
                          && (dTicks > 0 || stable == c.Get("preStable"));
                // 读档后世界立刻开跑：完成次数可能多出一两次（按步数核对），配方 / 阵列 / 槽位 / 队列必须完全一致。
                string pre = c.Get("preStable");
                // 合金库存也随生产走（FG5-E2E-01 回归抓到：读档后走了 4 步正好出一件合金，62 → 63）；已走步数 > 0 时只比形状。
                string Strip(string d) => string.Join("｜", d.Split('｜').Select(x => x.Contains(':') && x.Split(':').Length >= 5 ? string.Join(":", x.Split(':').Take(2))
                    : dTicks > 0 && x.StartsWith("合金:", StringComparison.Ordinal) ? "合金" : x));
                bool sameShape = Strip(stable) == Strip(pre);
                if (!ok || !sameShape)
                {
                    return StepOutcome.Fail($"读档后不一致（已走 {dTicks} 步）：槽位 {slots} vs {c.Get("preSlots")}、生效槽 {SignalCoreService.ActiveSlots(s)} vs {c.GetInt("preActive")}；\n      存档前 {pre}\n      读档后 {stable}");
                }
                c.Set("loadNote", $"读档后第一帧（已走 {dTicks} 步）：生产建筑配方、超控阵列、信号核槽位（{slots}，生效 {SignalCoreService.ActiveSlots(s)}）、装配站队列一致" + (dTicks == 0 ? "；进度 / 缓存 / 完成次数逐字一致" : string.Empty));
                c.SetLong(FgjM3Common.SK(c, "f0"), P.Completed(P.Bld(c, "furnace")));
                FgjM3Common.Mark(c, "cmp");
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            long more = P.Completed(P.Bld(c, "furnace")) - c.GetLong(FgjM3Common.SK(c, "f0"));
            return more >= 2 ? StepOutcome.Done($"{c.Get("loadNote")}；读档后精炼炉又完成 {more} 次（接着生产）") : StepOutcome.Wait;
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            c.Log(FgjM3Common.FrameReport("小产线施工 / 运转段"));
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            JourneyCommon.Cleanup(c);
        }
    }
}
