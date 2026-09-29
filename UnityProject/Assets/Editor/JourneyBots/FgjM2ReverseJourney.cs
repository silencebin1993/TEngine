using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG2-E2E-01：M2 反向旅程 FGJ-M2R（00 契约 IC-REQ-022：资源不足、目标死亡、断网 / 失联、暂停、存读档、路径失败，每类都是“出错 → 看到原因 → 用玩家能做的事恢复”）。
    /// 测试种子 1（与 FGJ-M2 的 42 不同，证明预备区、走不到的地方、敌人都按规则现找，B25）。全程正式输入，RTS 口径。
    ///
    /// 1. 资源不足：开局发电机坏着，解析台“数据复原”被拒（没电，不弹确认框、不扣技术数据）→ 右键修好发电机 → 确认框先点“取消”（什么都不变）→ 再复原成功；
    ///    之后复原一条稀有固件被拒（技术数据不足，写明需要多少、怎么获得）。
    /// 2. 路径失败：刚出厂的冷却液机右键点一个走不到的地方（种子地形里找不到时用悬崖圈地形夹具，DEBT-FG1E2E01-05 同一写法）→ 命令结束、可定位通知、拒绝音 → 右键走得到的地方 → 到达。
    /// 3. 暂停：战略暂停中右键靶——命令排队、世界不走、不出反应 → 恢复后第一次打出短路 → 首次慢放期间再按暂停：慢放剩余时间与世界都冻结 → 恢复后慢放走完。
    /// 4. 目标死亡：训练靶被打空 → 两台的攻击命令结束，不自己去找别的目标 → 靶满血复位后右键再打：又出短路，但不再慢放（只第一次）。
    /// 5. 存读档：暂停菜单“保存并返回主菜单”→ 读取：首次触发记录、图鉴解锁、复原的固件、技术数据、两台的蓝图版本逐项一致 → 读档后右键靶：短路照常、不再慢放。
    /// 6. 断网 / 失联：带两台出征破碎都市，接入电弧机开进监听节点干扰场 → 断链、安全模式 → 框选两台右键敌人：安全模式的机器照常接令、蓝图里的固件不依赖信号、
    ///    照样打出短路 → 把它开出干扰场 → 自动退出安全模式。
    /// </summary>
    public static class FgjM2ReverseJourney
    {
        public const string Id = "FGJ-M2R";

        /// <summary>测试种子 1（FgWorldGenSelfCheck 基准里有它；与 FGJ-M2 不同）。</summary>
        public const int TestSeed = FgjM1ReverseJourney.TestSeed;

        private static Vector2 JamProbe => FracturedCityLayout.ListeningNode.Position + new Vector2(0f, -8f);

        private static Vector2 EvacPoint => FracturedCityLayout.EntryEvac.Position;

        private static CampaignState St => CampaignSession.Current;

        private static string Label(int id) => SignalPresence.MachineLabel(id);

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M2 反向：没电 / 技术数据不足 / 取消确认、走不到的预备区、暂停中下令与慢放冻结、训练靶打空再打、存读档后不再慢放、断链的机器照样打出反应",
            Seed = TestSeed,
            TotalTimeoutSeconds = 900,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { FgjM2Common.ResetSampling(); FgjM2Common.IsolateCodex(c); })),
                S("menu_new", "主菜单点“新建”（测试种子 1）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("fixture", "进度夹具：技术数据 +12", 10, FgjM2Common.ApplyTechFixture, FgjM2Common.TickTechFixture),

                // ── 1. 资源不足（没电 → 修发电机 → 取消 / 确认 → 技术数据不足）──
                S("r1_open", "左键点解析台（发电机坏着，解析台没电）", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("r1_nopower", "选冷却液点“复原”：被拒（没电），不弹确认框、不扣技术数据", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwWet),
                    c => FgjM2Common.TickRestoreDenied(c, FgjM2Common.FwWet, FirmwareRestoreService.CodeNoPower), retries: 1),
                S("r1_close", "点“关闭”", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("r1_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("r1_repair", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("r1_power", "等发电机修好（解析台恢复供电）", 300, null, FgjM2Common.TickBenchPowered),
                S("r1_open2", "再点解析台", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("r1_ask", "选冷却液点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwWet), c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwWet), retries: 1),
                S("r1_cancel", "确认框点“取消”：什么都不变", 10, c => FgjM2Common.ClickConfirm(c, false), c => FgjM2Common.TickCancelled(c, FgjM2Common.FwWet)),
                S("r1_ask2", "再点“复原”", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwWet), c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwWet), retries: 1),
                S("r1_wet_ok", "确认：冷却液复原成功", 10, c => FgjM2Common.ClickConfirm(c, true), c => FgjM2Common.TickRestored(c, FgjM2Common.FwWet)),
                S("r1_shock", "选电弧点“复原”", 10, c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwShock), c => FgjM2Common.TickConfirmShown(c, FgjM2Common.FwShock), retries: 1),
                S("r1_shock_ok", "确认：电弧复原成功", 10, c => FgjM2Common.ClickConfirm(c, true), c => FgjM2Common.TickRestored(c, FgjM2Common.FwShock)),
                S("r1_notech", "选反应增幅（稀有，12 技术数据）点“复原”：被拒（技术数据不足，写明需要多少、怎么获得）", 10,
                    c => FgjM2Common.PickAndRestore(c, FgjM2Common.FwEpic), c => FgjM2Common.TickRestoreDenied(c, FgjM2Common.FwEpic, FirmwareRestoreService.CodeNoTech), retries: 1),
                S("r1_close2", "点“关闭”", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),

                // ── 前置：信号塔（出征要用）、残骸（废料）、两台组合机 ──
                S("tw_sel", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("tw_repair", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("sv_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("sv_salvage", "右键点开局残骸（拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("sv_wait", "等残骸拆完（废料 +60）", 120, null, FgjM2Common.TickSalvageDone),
                S("cb_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick", "点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_wet", "固件槽 1 选冷却液", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwWet), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwWet), retries: 1),
                S("cb_save", "点“保存”", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"), c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwWet, "verWet"), retries: 1),
                S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open", "左键点装配站", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_wet", "生产 ERC-003（冷却液版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verWet"), retries: 1),
                S("fac_close", "关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("cb_open2", "再点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick2", "点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_shock", "固件槽 1 改选电弧", 10, c => FgjM2Common.PickFirmwareSlot0(c, FgjM2Common.FwShock), c => FgjM2Common.TickFirmwareSlot0(c, FgjM2Common.FwShock), retries: 1),
                S("cb_save2", "点“保存”", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"), c => FgjM2Common.TickSavedWithFirmware(c, FgjM2Common.FwShock, "verShock"), retries: 1),
                S("cb_close2", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("fac_open2", "左键点装配站", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_shock", "生产 ERC-003（电弧版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verShock"), retries: 1),
                S("fac_close2", "关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),
                S("wait_wet", "等冷却液机出厂", 240, null, c => FgjM2Common.TickProduced(c, "verWet", "wetM")),

                // ── 2. 路径失败（预备区选在走不到的地方）──
                S("r2_sel", "左键点冷却液机", 20, c => { c.SetInt("workerB", c.GetInt("wetM")); FgjM1Journey.ClickMachine(c, "wetM"); },
                    c => FgjM1Journey.TickSelected(c, "wetM"), retries: 4),
                S("r2_find", "按种子地形找一个走不到的地方，镜头平移过去（方向键）", 60, FgjM1ReverseJourney.FindUnreachable, c => FgjM1ReverseJourney.TickPannedTo(c, "bad")),
                S("r2_bad", "右键点那里：命令结束，通知写明原因（可定位）+ 拒绝音", 30, c => FgjM1ReverseJourney.RightClickTarget(c, "bad"), FgjM1ReverseJourney.TickUnreachableReported),
                S("r2_home", "按回到归还核心键", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), FgjM1ReverseJourney.TickCameraHome, retries: 1),
                S("r2_ok", "再右键点一个走得到的地方：照常到达（它也因此驶出了装配站出口）", 60, FgjM1ReverseJourney.RightClickReachable, FgjM1ReverseJourney.TickArrived),
                S("wait_shock", "等电弧机出厂", 240, null, c => FgjM2Common.TickProduced(c, "verShock", "shockM")),
                S("st_sel_a", "左键点冷却液机", 20, c => FgjM2Journey.SelectWetAndPlanStaging(c), c => FgjM1Journey.TickSelected(c, "wetM"), retries: 4),
                S("st_a", "右键点靶前预备区", 20, FgjM2Common.RightClickStaging, c => FgjM2Common.TickMovingToStaging(c, "wetM"), retries: 4),
                S("st_sel_b", "左键点电弧机", 20, c => FgjM1Journey.ClickMachine(c, "shockM"), c => FgjM1Journey.TickSelected(c, "shockM"), retries: 4),
                S("st_b", "右键点同一块预备区", 20, FgjM2Common.RightClickStaging, c => FgjM2Common.TickMovingToStaging(c, "shockM"), retries: 4),
                S("staged", "两台都到了预备区", 120, null, FgjM2Common.TickBothStaged),
                S("box", "在预备区拖出选择框：两台组成一组", 15, FgjM2Common.BoxSelectStaging, c => FgjM2Common.TickPairSelected(c, GameRoot.HomeValley.SquadCommands), retries: 4),

                // ── 3. 暂停：暂停中下令、首次慢放中再暂停 ──
                S("r3_pause", "按暂停键（战略暂停）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true),
                    FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("r3_order", "暂停中右键点低威胁靶：命令排队、世界不走、不出反应", 15, FgjM2Common.RightClickDummy, TickQueuedWhilePaused, retries: 3),
                S("r3_resume", "按暂停键恢复：两台开打，第一次打出短路；首次慢放期间立刻再按暂停", 90, ResumeForFirst, TickFirstThenPause),
                S("r3_frozen", "暂停中慢放不走：剩余慢放时间与世界步数都不变", 10, null, TickSlowFrozen),
                S("r3_resume2", "按暂停键恢复：慢放走完，回到正常节奏", 15, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false), TickSlowFinished, retries: 1),

                // ── 4. 目标死亡（训练靶被打空 → 命令结束 → 复位后再打）──
                S("r4_dead", "训练靶被打空：两台的攻击命令结束，不自己去找别的目标", 120, null, TickDummyDestroyed),
                S("r4_regen", "等训练靶满血复位", 60, null, TickDummyRegen),
                S("r4_box", "再框选预备区的两台", 15, FgjM2Common.BoxSelectStaging, c => FgjM2Common.TickPairSelected(c, GameRoot.HomeValley.SquadCommands), retries: 4),
                S("r4_again", "右键点训练靶：又打出短路，但不再慢放", 90, BeginAgain, TickConductAgainNoSlow, retries: 3),

                // ── 5. 存读档 ──
                S("r5_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("r5_save", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("r5_menu", "回到主菜单：存档里首次触发记录、复原的固件、技术数据、两台的蓝图版本与存档那一刻一致", 60, null, TickMenuAfterSave),
                S("r5_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("r5_loaded", "读档进入家园：逐项一致、图鉴短路条目仍解锁", 120, null, TickLoaded),
                S("r5_box", "框选预备区的两台", 15, FgjM2Common.BoxSelectStaging, c => FgjM2Common.TickPairSelected(c, GameRoot.HomeValley.SquadCommands), retries: 4),
                S("r5_again", "右键点训练靶：读档后照常打出短路、不再慢放（首次记录读回来了）", 90, BeginAgain, TickConductAgainNoSlow, retries: 3),

                // ── 6. 断网 / 失联（破碎都市干扰场）──
                S("r6_speed", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("r6_prep", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                    FgjM1Journey.TickPrepOpen, retries: 2),
                S("r6_pick", "勾选出征名单（组合的两台 + 再一台）", 20, null, FgjM2Common.TickRosterPicked),
                S("r6_depart", "点“出发”", 30, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), FgjM1Journey.TickDeparted, retries: 1),
                S("r6_uplink", "机器列表点电弧机：跨地点远距离跳转后接入", 25, c => FgjM1Journey.ClickMachineList(c, c.GetInt("shockM")), TickUplinkedShock, retries: 3),
                S("r6_jam", "开着它（WASD）驶入监听节点的干扰场：宽限后断链，进入安全模式", 60, null, TickDriveIntoJam),
                S("r6_sel_a", "战略视角左键点冷却液机", 20, c => FgjM2Common.ClickFc(c, c.GetInt("wetM"), false), c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM")), retries: 4),
                S("r6_sel_b", "按住 Shift 左键点安全模式的电弧机（加选）", 20, c => FgjM2Common.ClickFc(c, c.GetInt("shockM"), true),
                    c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM"), c.GetInt("shockM")), retries: 4),
                S("r6_attack", "右键点最近的敌人：安全模式的机器照常接令，蓝图里的固件不依赖信号，照样打出短路", 120, BeginFcAttack, TickFcConductWhileSafe, retries: 3),
                S("r6_sel_out", "左键点电弧机", 20, c => FgjM2Common.ClickFc(c, c.GetInt("shockM"), false), c => FgjM2Common.TickFcSelection(c, c.GetInt("shockM")), retries: 4),
                S("r6_out", "右键点撤离点附近的地面：驶出干扰场，2 游戏秒后自动退出安全模式", 90, c => JourneyInput.Click(EvacPoint + new Vector2(0f, 1f), button: 1), TickSafeModeRecovered),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        // ── 3. 暂停 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickQueuedWhilePaused(JourneyContext c)
        {
            if (c.GetInt("pausedT0Set") == 0)
            {
                c.SetInt("pausedT0Set", 1);
                c.SetLong("pausedT0", GameClock.Ticks);
            }
            if (FgjM2Common.PanPending(c, FgjM2Common.RightClickDummy))
            {
                c.SetLong("pausedT0", GameClock.Ticks);
                return StepOutcome.Wait;
            }
            if (FgjM2Common.NowMs() - c.GetLong("clickedAtMs") < 1500)
            {
                return StepOutcome.Wait;
            }
            RegionSquadCommandSystem squad = GameRoot.HomeValley.SquadCommands;
            int conduct = FgjM2Common.ReactionCount(GameRoot.HomeValley.Combat);
            bool queued = squad.QueuedCommandCount > 0 && squad.RecentEvents.Any(e => e.Contains("排队"));
            bool frozen = GameClock.Paused && GameClock.Ticks == c.GetLong("pausedT0");
            bool quiet = conduct == c.GetInt("conduct0") && !ReactionFeedback.IsFirstTriggered(St, FgjM2Common.Conduct);
            return queued && frozen && quiet
                ? StepOutcome.Done($"暂停中右键靶：“{squad.RecentEvents.LastOrDefault()}”；1.5 秒真实时间里世界停在第 {GameClock.Ticks} 步、没有反应")
                : StepOutcome.Retry($"暂停中下令不对：排队 {queued}（{squad.RecentEvents.LastOrDefault()}）、世界停住 {frozen}、没有反应 {quiet}");
        }

        private static void ResumeForFirst(JourneyContext c)
        {
            FgjM2Common.BeginSampling();
            c.SetInt("cmdSeen", 1); // 命令在暂停中已经下达（排队）：恢复后执行
            JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false);
        }

        /// <summary>恢复后第一次打出短路（同 FGJ-M2 的首次反馈核对）；慢放一开始就立刻再按暂停（先读状态再按）。</summary>
        private static StepOutcome TickFirstThenPause(JourneyContext c)
        {
            if (c.GetInt("firstDone") == 0)
            {
                if (!ReactionFeedback.IsFirstTriggered(St, FgjM2Common.Conduct))
                {
                    FgjM2Common.SampleFrame();
                    return FgjM2Common.ReissueIfDummyGone(c, GameRoot.HomeValley.Combat) ?? StepOutcome.Wait;
                }
                c.SetInt("firstDone", 1);
                c.Set("slowLeftAtFirst", GameClock.SlowMotionSecondsLeft.ToString("R", CultureInfo.InvariantCulture));
                if (GameClock.SlowMotionSecondsLeft > 0f)
                {
                    JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true);
                }
                return StepOutcome.Wait;
            }
            if (!GameClock.Paused)
            {
                return c.StepElapsed > 60 ? StepOutcome.Fail("首次慢放期间按了暂停，世界没有暂停") : StepOutcome.Wait;
            }
            ReactionFirstTriggerRecord rec = ReactionFeedback.FirstRecordOf(St, FgjM2Common.Conduct);
            bool slow = GameClock.SlowMotionStarts > c.GetInt("slow0");
            bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(FgjM2Common.Conduct));
            if (!slow || !codex || rec == null || GameClock.SlowMotionSecondsLeft <= 0f)
            {
                return StepOutcome.Fail($"恢复后第一次短路：首次记录 {rec != null}、慢放开始 {slow}、图鉴解锁 {codex}、暂停时慢放还剩 {GameClock.SlowMotionSecondsLeft:F3} 秒（慢放刚开始时 {c.Get("slowLeftAtFirst")}）");
            }
            c.Set("slowLeftPaused", GameClock.SlowMotionSecondsLeft.ToString("R", CultureInfo.InvariantCulture));
            c.SetLong("pausedTicks", GameClock.Ticks);
            c.SetInt("slowAfterFirst", GameClock.SlowMotionStarts);
            return StepOutcome.Done($"恢复后第一次打出短路（首次记录第 {rec.Tick} 步、图鉴解锁、慢放开始）；慢放还剩 {GameClock.SlowMotionSecondsLeft:F2} 秒时按暂停：世界暂停");
        }

        private static StepOutcome TickSlowFrozen(JourneyContext c)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            float left = float.Parse(c.Get("slowLeftPaused", "0"), CultureInfo.InvariantCulture);
            bool frozen = GameClock.Paused && Mathf.Approximately(GameClock.SlowMotionSecondsLeft, left) && GameClock.Ticks == c.GetLong("pausedTicks");
            return frozen
                ? StepOutcome.Done($"暂停 1 秒真实时间：慢放剩余 {left:F2} 秒不变、世界停在第 {GameClock.Ticks} 步（慢放在统一时钟里、暂停冻结）")
                : StepOutcome.Fail($"暂停中慢放在走：剩余 {left:F3} → {GameClock.SlowMotionSecondsLeft:F3}、步数 {c.GetLong("pausedTicks")} → {GameClock.Ticks}");
        }

        private static StepOutcome TickSlowFinished(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (GameClock.Paused)
            {
                return StepOutcome.Retry("按了暂停键，世界没有恢复");
            }
            if (GameClock.SlowMotionSecondsLeft > 0f || GameClock.Ticks <= c.GetLong("pausedTicks"))
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"恢复后慢放没走完（还剩 {GameClock.SlowMotionSecondsLeft:F3} 秒）");
            }
            return StepOutcome.Done($"恢复后慢放走完、节奏回到 {GameClock.Speed}x（慢放倍率 {GameClock.SlowMotionFactor}）；慢放总共只开始了 {GameClock.SlowMotionStarts - c.GetInt("slow0")} 次");
        }

        // ── 4. 目标死亡 ────────────────────────────────────────────────────────────

        private static CombatTargetRecord Dummy => HomeValleyCombatTargets.Find(St, HomeValleyCombatTargets.LowThreatTargetId);

        private static StepOutcome TickDummyDestroyed(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatTargetRecord t = Dummy;
            if (t == null)
            {
                return StepOutcome.Fail("家园没有低威胁靶");
            }
            if (t.Health > 0f)
            {
                return StepOutcome.Wait;
            }
            CombatSite site = GameRoot.HomeValley.Combat;
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            // 命令在靶被打空的那一步结束（内核：目标不可选中 → 攻击命令结束）；给两三步。
            if (FgjM2Common.IsAttacking(site, a) || FgjM2Common.IsAttacking(site, b))
            {
                return c.GetInt("deadWait") < 20 ? Bump(c, "deadWait") : StepOutcome.Fail("训练靶打空后两台还在执行攻击命令");
            }
            bool idle = !site.TryGetMachineUnit(a, out int ua) || !site.TryGetCommand(ua, out BinGames.Sim.Combat.CombatCommand ca) || ca.Kind == BinGames.Sim.Combat.CombatCommandKind.None;
            bool idleB = !site.TryGetMachineUnit(b, out int ub) || !site.TryGetCommand(ub, out BinGames.Sim.Combat.CombatCommand cb) || cb.Kind == BinGames.Sim.Combat.CombatCommandKind.None;
            return idle && idleB
                ? StepOutcome.Done($"训练靶被打空（{t.Health:F0}/{t.MaxHealth:F0}）：两台的攻击命令结束，没有自己去找别的目标（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）；靶 {t.RegenCooldownRemaining:F1} 秒后复位")
                : StepOutcome.Fail("训练靶打空后两台接着执行了别的命令（玩家没要求）");
        }

        private static StepOutcome Bump(JourneyContext c, string key)
        {
            c.SetInt(key, c.GetInt(key) + 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickDummyRegen(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatTargetRecord t = Dummy;
            return t != null && t.Health >= t.MaxHealth ? StepOutcome.Done($"训练靶满血复位（{t.Health:F0}）") : StepOutcome.Wait;
        }

        private static void BeginAgain(JourneyContext c)
        {
            c.SetInt("cmdSeen", 0);
            c.SetInt("slowAgain0", GameClock.SlowMotionStarts);
            c.SetInt("firstLen0", St.ReactionFirstTriggers?.Length ?? 0);
            FgjM2Common.RightClickDummy(c);
        }

        private static StepOutcome TickConductAgainNoSlow(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.8 || FgjM2Common.PanPending(c, FgjM2Common.RightClickDummy) || FgjM2Common.JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            CombatSite site = GameRoot.HomeValley.Combat;
            if (c.GetInt("cmdSeen") == 0)
            {
                // 下令成功 = 编队记下“攻击 已下达（2 台）”（靶只有 60 血，可能很快被打空、命令随之结束——由下面的“等复位再右键”处理）。
                if (!FgjM2Common.AttackIssued(c, GameRoot.HomeValley.SquadCommands, 2))
                {
                    return StepOutcome.Retry($"右键靶后没有下达攻击命令（{string.Join(" / ", FgjM2Common.NewEvents(c, GameRoot.HomeValley.SquadCommands))}）");
                }
                c.SetInt("cmdSeen", 1);
            }
            if (FgjM2Common.ReactionCount(site) <= c.GetInt("conduct0"))
            {
                return FgjM2Common.ReissueIfDummyGone(c, site) ?? StepOutcome.Wait;
            }
            ReactionFirstTriggerRecord rec = ReactionFeedback.FirstRecordOf(St, FgjM2Common.Conduct);
            bool noSlow = GameClock.SlowMotionStarts == c.GetInt("slowAgain0");
            bool oneRecord = (St.ReactionFirstTriggers?.Length ?? 0) == c.GetInt("firstLen0") && rec != null;
            ReactionLogEntry entry = ReactionLog.All.LastOrDefault(e => e.ReactionId == FgjM2Common.Conduct);
            return noSlow && oneRecord && entry != null && !entry.First
                ? StepOutcome.Done($"又打出“{FgjM2Common.ConductName}”（内核计数 {FgjM2Common.ReactionCount(site) - c.GetInt("conduct0")} 次）：不再慢放、首次记录仍是第 {rec.Tick} 步那一条；日志“{ReactionLog.Describe(St, entry)}”")
                : StepOutcome.Fail($"再打出短路时：没有慢放 {noSlow}、首次记录没变 {oneRecord}、日志非首次 {entry != null && !entry.First}");
        }

        // ── 5. 存读档 ──────────────────────────────────────────────────────────────

        private static string Digest(CampaignState s)
        {
            ReactionFirstTriggerRecord rec = s.ReactionFirstTriggers?.FirstOrDefault(r => r.ReactionId == FgjM2Common.Conduct);
            string unlocked = string.Join(",", (s.UnlockedContentIds ?? Array.Empty<string>()).Where(x => x == FgjM2Common.FwWet || x == FgjM2Common.FwShock).OrderBy(x => x, StringComparer.Ordinal));
            // 运行中的机器记录在 MachineRegistry（存档时才写回 state.MachineRecords）；读盘得到的状态用它自己的记录。
            bool live = ReferenceEquals(s, CampaignSession.Current);
            MachineRecord a = live ? (MachineRegistry.TryGetRecord(LastWet, out MachineRecord ra) ? ra : null) : s.MachineRecords?.FirstOrDefault(m => m.LogicId == LastWet);
            MachineRecord b = live ? (MachineRegistry.TryGetRecord(LastShock, out MachineRecord rb) ? rb : null) : s.MachineRecords?.FirstOrDefault(m => m.LogicId == LastShock);
            return $"首次={rec?.ReactionId}@{rec?.Tick}@{rec?.SiteId}｜复原={unlocked}｜技术数据={s.TechData}｜冷却液机=v{a?.BlueprintVersion}｜电弧机=v{b?.BlueprintVersion}";
        }

        private static int LastWet;
        private static int LastShock;

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
            LastWet = c.GetInt("wetM");
            LastShock = c.GetInt("shockM");
            c.Set("pre", Digest(St));
            c.SetLong("preTicks", GameClock.Ticks);
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：{c.Get("pre")}");
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LastWet = c.GetInt("wetM");
            LastShock = c.GetInt("shockM");
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            string disk = Digest(onDisk.State);
            return disk == c.Get("pre") && onDisk.State.Clock.Ticks == c.GetLong("preTicks")
                ? StepOutcome.Done($"回到主菜单；存档里 {disk}，与存档那一刻逐项一致")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {disk}");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LastWet = c.GetInt("wetM");
            LastShock = c.GetInt("shockM");
            string live = Digest(St);
            bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(FgjM2Common.Conduct));
            bool machines = FgjM1Journey.HomeMarker(LastWet, out _) && FgjM1Journey.HomeMarker(LastShock, out _);
            bool fw = GameRoot.HomeValley.Combat.TryGetMachineWeapon(LastWet, out MachineWeaponInfo wi) && wi.FirmwareIds.Contains(FgjM2Common.FwWet)
                      && GameRoot.HomeValley.Combat.TryGetMachineWeapon(LastShock, out MachineWeaponInfo si) && si.FirmwareIds.Contains(FgjM2Common.FwShock);
            c.SetInt("conduct0", FgjM2Common.ReactionCount(GameRoot.HomeValley.Combat));
            return live == c.Get("pre") && codex && machines && fw
                ? StepOutcome.Done($"读档进入家园：{live}；图鉴短路条目仍解锁；两台的生效固件仍是冷却液 / 电弧")
                : StepOutcome.Fail($"读档后不一致：{live}（存档前 {c.Get("pre")}）、图鉴 {codex}、机器 {machines}、固件 {fw}");
        }

        // ── 6. 断网 / 失联 ──────────────────────────────────────────────────────────

        private static StepOutcome TickUplinkedShock(JourneyContext c)
        {
            int b = c.GetInt("shockM");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(b)}（{SignalUplinkService.LastFeedbackText}）");
            }
            return c.StepElapsed < 2.5 ? StepOutcome.Wait : StepOutcome.Done($"跨地点远距离跳转后接入 {Label(b)}（镜头直控）");
        }

        private static StepOutcome TickDriveIntoJam(JourneyContext c)
        {
            FgjM2Common.SampleFrame();
            int b = c.GetInt("shockM");
            CampaignState s = St;
            if (SignalLinkService.IsInSafeMode(s, b))
            {
                JourneyInput.ReleaseKeys();
                if (c.GetLong("safeAtMs") == 0)
                {
                    c.SetLong("safeAtMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
                    return StepOutcome.Wait;
                }
                if (c.StepElapsed - c.GetLong("safeAtMs") / 1000.0 < 1.5)
                {
                    return StepOutcome.Wait;
                }
                SignalSafeModeRecord rec = s.SignalCore.SafeModes.First(m => m.LogicId == b);
                bool ok = rec.Reason == (int)SignalLinkBreakReason.Jammed && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy && SignalLinkView.BadgeWanted(b);
                return ok
                    ? StepOutcome.Done($"{Label(b)} 驶入干扰场：宽限后断链（信号弹回归还核心、镜头回战略），进入安全模式（原因：{SignalLinkService.ReasonName(SignalLinkBreakReason.Jammed)}，头顶图标）")
                    : StepOutcome.Fail($"断链后状态不对：原因 {rec.Reason}、信号在核心 {SignalPresence.AtCore}、镜头 {WorldView.Director.Mode}、图标 {SignalLinkView.BadgeWanted(b)}");
            }
            if (SignalPresence.CurrentMachineLogicId != b)
            {
                return StepOutcome.Fail($"驶入干扰场前信号已不在 {Label(b)} 里（{SignalUplinkService.LastFeedbackText}）");
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (FgjM2Common.FcPos(b, out Vector2 me))
            {
                FgjM1Journey.DriveToward(me, JamProbe, 0.4);
            }
            return StepOutcome.Wait;
        }

        private static void BeginFcAttack(JourneyContext c)
        {
            c.SetInt("safeReact", 0);
            c.SetInt("conductFc0", FgjM2Common.ReactionCount(GameRoot.FracturedCity?.Combat));
            FgjM2Common.RightClickFcEnemy(c);
        }

        /// <summary>安全模式的电弧机与冷却液机一起打：只要打出短路就算（伤害归因记进这次出击）；另记“短路发生时电弧机是否还在安全模式”。</summary>
        private static StepOutcome TickFcConductWhileSafe(JourneyContext c)
        {
            int b = c.GetInt("shockM");
            CombatSite site = GameRoot.FracturedCity?.Combat;
            if (site != null && FgjM2Common.ReactionCount(site) > c.GetInt("conductFc0") && c.GetInt("seenReact") == 0)
            {
                c.SetInt("seenReact", 1);
                c.SetInt("safeReact", SignalLinkService.IsInSafeMode(St, b) ? 1 : 0);
            }
            if (c.GetInt("attackChecked") == 0 && c.StepElapsed > 0.8 && c.Get("flyFirst") != "1" && !FgjM2Common.JustClicked(c))
            {
                c.SetInt("attackChecked", 1);
                // 安全模式的机器照常接令：编队记下“攻击 已下达（2 台）”（两台都接令，不因断链少一台）。
                if (!FgjM2Common.AttackIssued(c, GameRoot.FracturedCity.SquadCommands, 2))
                {
                    return StepOutcome.Fail($"右键敌人后没有两台都接到攻击命令（{string.Join(" / ", FgjM2Common.NewEvents(c, GameRoot.FracturedCity.SquadCommands))}）");
                }
                c.Log($"右键敌人：安全模式的 {Label(b)} 照常接到攻击命令（安全模式 {SignalLinkService.IsInSafeMode(St, b)}）");
            }
            StepOutcome o = FgjM2Common.TickFcConduct(c);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            bool fw = site.TryGetMachineWeapon(b, out MachineWeaponInfo info) && info.FirmwareIds.Contains(FgjM2Common.FwShock);
            return fw
                ? StepOutcome.Done(o.Message + $"；打出短路时 {Label(b)} {(c.GetInt("safeReact") == 1 ? "仍在安全模式" : "已驶出干扰场")}，生效固件仍含电弧（蓝图固件不依赖信号）")
                : StepOutcome.Fail($"断链后 {Label(b)} 的生效固件里没有电弧");
        }

        private static StepOutcome TickSafeModeRecovered(JourneyContext c)
        {
            FgjM2Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int b = c.GetInt("shockM");
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            if (SignalLinkService.IsInSafeMode(St, b))
            {
                return StepOutcome.Wait;
            }
            FgjM2Common.FcPos(b, out Vector2 at);
            return StepOutcome.Done($"{Label(b)} 驶出干扰场（离监听节点 {Vector2.Distance(at, FracturedCityLayout.ListeningNode.Position):F1} 格），2 游戏秒后自动退出安全模式（{c.StepElapsed:F1} 秒）；{FgjM2Common.FrameReport()}");
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM2Common.FrameReport());
            FgjM2Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            LastWet = 0;
            LastShock = 0;
            JourneyCommon.Cleanup(c);
        }
    }
}
