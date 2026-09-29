using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG2-E2E-01：M2 出口旅程 FGJ-M2（ProjectA_FullGame_Milestones.md FG-M2）：
    /// 从固件库筛选“流体类” → 在靶场前的预备区组合两台机器 → 触发一条第一幕开放的具名反应，看到首次慢放和图鉴解锁 → 在统计里看到这条反应的伤害归因。
    ///
    /// 全程从主菜单“新建”出发、固定测试种子 42、走正式输入（<see cref="JourneyInput"/>）；RTS 口径：左键选择 / 框选、右键地面 = 移动、右键敌人 = 攻击，
    /// UI 按钮只切状态。家园前置用 Demo 的正式早期循环（修发电机与信号塔、拆残骸拿废料）。
    ///
    /// 与里程碑文字的对应（ADR-QA-018）：
    /// - 固件来源：FG-GAP-050 的临时来源——解析台“数据复原”（技术数据 → 固件刻录数据），复原冷却液（流体，浸湿）与电弧（电磁，电击），刻印冷却液芯片；
    ///   技术数据用进度夹具补 12（DEBT-FG2E2E01-01：新档没有技术数据，Demo 要先远征带回模块解析）。
    /// - “靶场前的预备区”：靶场建筑在 FG5-RND-03，当前内容里对应物是家园的低威胁残骸靶（训练靶）；预备区 = 靶前 16 格（超出训练靶自动交战距离 12，
    ///   机器到了不会自己开火）。两台 ERC-003 分别在蓝图固件槽装冷却液 / 电弧，生产出来开到预备区、框选成一组、右键靶 = 一起攻击 → “短路”第一次触发：
    ///   首次慢放、镜头轻推、首次弹字、图鉴解锁（旅程再打开图鉴反应页签看条目）。
    /// - 统计：伤害归因按“远征的一次出击 / 家园的一次突袭”记场次（FGR-FW-043，家园平时训练不算），所以带这两台出征破碎都市、框选后右键敌人打出短路，
    ///   再从暂停菜单打开统计面板看到这条反应的伤害占比。靶场落地后旅程改回靶场（DEBT-FG2E2E01-03）。
    /// </summary>
    public static class FgjM2Journey
    {
        public const string Id = "FGJ-M2";

        /// <summary>固定测试种子：与 FGJ-M0 / FGJ-M1 同一颗（FgWorldGenSelfCheck 基准里有它）。</summary>
        public const int TestSeed = FgjM0Journey.TestSeed;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M2 出口：数据复原冷却液与电弧 → 固件库筛选流体类 → 预备区组合两台机器 → 右键靶打出短路（首次慢放、图鉴解锁）→ 远征再打出短路 → 统计里看到伤害归因",
            Seed = TestSeed,
            TotalTimeoutSeconds = 900,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { FgjM2Common.ResetSampling(); FgjM2Common.IsolateCodex(c); })),
                S("menu_new", "主菜单点“新建”（固定测试种子）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("fixture", "进度夹具：技术数据 +12（等同解析了破碎都市的协议数据盒）", 10, FgjM2Common.ApplyTechFixture, FgjM2Common.TickTechFixture),

                // ── 家园前置（Demo 正式早期循环）──
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !UnityEngine.Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_worker_a", "左键点一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_gen", "右键点受损的发电机（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("sel_worker_b", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_tower", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("wait_repairs", "等发电机与信号塔修好", 300, null, FgjM1Journey.TickRepairsDone),
                S("salv_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("salvage", "右键点开局残骸（情境命令：拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完（废料 +60，两台组合机的造价）", 120, null, FgjM2Common.TickSalvageDone),

                // ── 固件来源：解析台“数据复原”（FG-GAP-050 临时来源）──
                S("ana_open", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("ana_wet", "“数据复原”下拉选冷却液，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwWet),
                    c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwWet), retries: 1),
                S("ana_wet_ok", "确认框点“复原”：冷却液解锁（技术数据 −4）", 10, c => FgjM2Common.ClickConfirm(c, true), c => FgjM2Common.TickRestored(c, FgjM2Common.FwWet)),
                S("ana_shock", "下拉选电弧，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwShock),
                    c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwShock), retries: 1),
                S("ana_shock_ok", "确认框点“复原”：电弧解锁（敌方加密，同时视为已破解）", 10, c => FgjM2Common.ClickConfirm(c, true), c => FgjM2Common.TickRestored(c, FgjM2Common.FwShock)),
                S("ana_close", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print", "刻印下拉选冷却液，点“刻印”：得到一枚冷却液芯片", 10, c => FgjM2Common.PrintFirmware(c, FgjM2Common.FwWet), c => FgjM2Common.TickPrinted(c, FgjM2Common.FwWet), retries: 1),
                S("sc_print2", "刻印下拉选电弧，点“刻印”：再得到一枚电弧芯片（电磁类）", 10, c => FgjM2Common.PrintFirmware(c, FgjM2Common.FwShock), c => FgjM2Common.TickPrinted(c, FgjM2Common.FwShock), retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

                // ── 第 1 步：从固件库筛选“流体类” ──
                S("lib_open", "按固件库键（默认 I）打开固件库", 10, c => FgjM1Journey.PressIf(GameActionId.OpenFirmware, !FirmwareLibraryPanelUIToolkit.IsOpen), FgjM2Common.TickLibraryOpen, retries: 1),
                S("lib_fluid", "类别下拉选“流体”：列表只剩流体类", 10, FgjM2Common.FilterFluid, FgjM2Common.TickFilteredFluid, retries: 1),
                S("lib_row", "点选冷却液那一行：详情写明标签与参与的反应", 10, FgjM2Common.ClickWetRow, FgjM2Common.TickWetDetail, retries: 2),
                S("lib_close", "再按固件库键关闭固件库", 10, c => FgjM1Journey.PressIf(GameActionId.OpenFirmware, FirmwareLibraryPanelUIToolkit.IsOpen), FgjM2Common.TickLibraryClosed, retries: 1),

                // ── 第 2 步：在靶场前的预备区组合两台机器 ──
                S("cb_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_wet", "固件槽 1 下拉选冷却液", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwWet), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwWet), retries: 1),
                S("cb_save", "点“保存”", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"), c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwWet, "verWet"), retries: 1),
                S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_wet", "点“生产 ERC-003”（冷却液版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verWet"), retries: 1),
                S("fac_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("cb_open2", "再点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick2", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_shock", "固件槽 1 下拉改选电弧", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwShock), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwShock), retries: 1),
                S("cb_save2", "点“保存”（新版本；已排的冷却液订单不受影响）", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"),
                    c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwShock, "verShock"), retries: 1),
                S("cb_close2", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open2", "左键点装配站", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_shock", "点“生产 ERC-003”（电弧版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verShock"), retries: 1),
                S("fac_close2", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("wait_wet", "等冷却液机出厂", 240, null, c => FgjM2Common.TickProduced(c, "verWet", "wetM")),
                S("sel_wet", "左键点冷却液机", 20, SelectWetAndPlanStaging, c => FgjM1Journey.TickSelected(c, "wetM"), retries: 4),
                S("out_wet", "右键点靶前预备区的地面：它驶出装配站、开过去", 20, FgjM2Common.RightClickStaging, c => FgjM2Common.TickMovingToStaging(c, "wetM"), retries: 4),
                S("wait_shock", "等电弧机出厂", 240, null, c => FgjM2Common.TickProduced(c, "verShock", "shockM")),
                S("sel_shock", "左键点电弧机", 20, c => FgjM1Journey.ClickMachine(c, "shockM"), c => FgjM1Journey.TickSelected(c, "shockM"), retries: 4),
                S("out_shock", "右键点同一块预备区", 20, FgjM2Common.RightClickStaging, c => FgjM2Common.TickMovingToStaging(c, "shockM"), retries: 4),
                S("staged", "两台都到了预备区", 120, null, FgjM2Common.TickBothStaged),
                S("box", "在预备区拖出选择框：两台组成一组", 15, FgjM2Common.BoxSelectStaging, c => FgjM2Common.TickPairSelected(c, GameRoot.HomeValley.SquadCommands), retries: 4),

                // ── 第 3 步：触发第一幕开放的具名反应，看到首次慢放和图鉴解锁 ──
                S("attack_dummy", "右键点低威胁靶（右键敌人 = 攻击）：两台一起打，打出“短路”", 90, c => { FgjM2Common.BeginSampling(); FgjM2Common.RightClickDummy(c); },
                    c => FgjM2Common.TickFirstConduct(c, GameRoot.HomeValley.Combat, "家园低威胁靶上"), retries: 3),
                S("codex_open", "按图鉴键（默认 C）打开图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, !MechanicCodexPanelUIToolkit.IsOpen), FgjM2Common.TickCodexOpen, retries: 1),
                S("codex_tab", "点“反应”页签", 10, FgjM2Common.ClickReactionTab, FgjM2Common.TickReactionTab, retries: 1),
                S("codex_entry", "点“短路”条目：已解锁，写着配料与能提供配料的固件", 10, FgjM2Common.ClickConductEntry, FgjM2Common.TickConductEntry, retries: 2),
                S("codex_close", "再按图鉴键关闭图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, MechanicCodexPanelUIToolkit.IsOpen), FgjM2Common.TickCodexClosed, retries: 1),

                // ── 第 4 步：在统计里看到这条反应的伤害归因（伤害归因按远征 / 突袭记场次）──
                S("prep_open", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                    FgjM1Journey.TickPrepOpen, retries: 2),
                S("prep_pick", "勾选出征名单（组合的两台 + 再一台满足人数）", 20, null, FgjM2Common.TickRosterPicked),
                S("prep_depart", "点“出发”（有在办工作时确认中断）", 30, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), FgjM1Journey.TickDeparted, retries: 1),
                S("fc_sel_a", "左键点冷却液机", 20, c => FgjM2Common.ClickFc(c, c.GetInt("wetM"), false), c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM")), retries: 4),
                S("fc_sel_b", "按住 Shift 左键点电弧机（加选）", 20, c => FgjM2Common.ClickFc(c, c.GetInt("shockM"), true),
                    c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM"), c.GetInt("shockM")), retries: 4),
                S("fc_attack", "右键点最近的敌人：两台一起打，远征场次记下短路", 120, FgjM2Common.RightClickFcEnemy, FgjM2Common.TickFcConduct, retries: 3),
                S("stats_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), FgjM2Common.TickPauseOpen, retries: 1),
                S("stats_open", "点“统计”：战斗 · 反应伤害归因里有短路的占比", 10, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseStats"), FgjM2Common.TickStatsOpen, retries: 1),
                S("stats_close", "点“关闭”关闭统计面板", 10, c => FgjM1Journey.ClickUi(c, "[StatsPanelHost]", "StatsPanelClose"), FgjM2Common.TickStatsClosed, retries: 1),
                S("pause_close", "按 Esc 关闭暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, PauseMenuUIToolkit.IsOpen), FgjM2Common.TickPauseClosed, retries: 1),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        /// <summary>左键点冷却液机；同时按它的实际位置与靶的锚点定下预备区（靶前 16 格，走不到就绕靶换方向；不写死坐标）。</summary>
        internal static void SelectWetAndPlanStaging(JourneyContext c)
        {
            if (c.Get("stageX", string.Empty).Length == 0 && FgjM2Common.HomePos(c.GetInt("wetM"), out UnityEngine.Vector2 p))
            {
                UnityEngine.Vector2 stage = FgjM2Common.StagingPoint(p);
                FgjM2Common.SetPoint(c, "stage", stage);
                c.Log($"预备区：低威胁靶朝装配站出口方向 {FgjM2Common.StagingDistance:F0} 格（{stage.x:F0},{stage.y:F0}）");
            }
            FgjM1Journey.ClickMachine(c, "wetM");
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM2Common.FrameReport());
            FgjM2Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            JourneyCommon.Cleanup(c);
        }
    }
}
