using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG3-E2E-01：M3 反向旅程 FGJ-M3R（00 契约 IC-REQ-022：资源不足、目标死亡、断网 / 失联、暂停、存读档、路径失败——每类都是“出错 → 看到原因 → 用玩家能做的事恢复”）。
    /// 测试种子 1（与 FGJ-M3 的 42 不同，路线、水源、搬迁位置都按规则现找，B25）。全程正式输入，RTS 口径。M3 的内容是建造与物流，六类都落在建造 / 物流上：
    ///
    /// 1. 资源不足：废料花在造机器上之后，拖一条比库存还贵的 T3 传送带——拖的时候写“还差多少”，虚影照放；施工用光库存后剩下的格“等待材料”（悬停与施工队列写明）→
    ///    右键残骸拆解 → 到货自动开工、全部建成。
    /// 2. 路径失败：① 传送带拖过仓库——整条被拒（全有或全无），写明哪一格为什么，什么都没放 → 绕开重拖成功；② 右键一个机器走不到的地方（按种子地形找，
    ///    家园起始区保证可通行时沿用 FGJ-M1R 的悬崖圈地形夹具，DEBT-FG1E2E01-05）→ 命令结束、可定位通知 → 右键走得到的地方 → 到达。
    /// 3. 暂停：战略暂停中照样放泵、拖管线、放储罐（规划），世界不走、施工不动 → 恢复后施工、泵抽水；0.5x 与 3x 下泵按游戏时间的抽水速率相同；再暂停泵与储罐读数冻结。
    /// 4. 目标死亡：机器正把材料运去建一个储罐虚影时，玩家在拆除模式取消了这个虚影 → 机器把这一趟材料退回仓库，库存一件不少、没有留下孤儿虚影。
    /// 5. 断网 / 失联（电网断开）：把维修台搬出归还核心的配电范围 → 没电（“为什么不工作”写“未接入电网”）→ 放一座电塔、左键工程机右键电塔虚影“去建这个”→ 接通；
    ///    拆电塔弹确认框写明后果 → 先点取消（什么都不变）→ 再确认：机器上门拆掉（全额返还）、维修台断电 → Ctrl+Z 撤销拆除：电塔虚影放回、重建后接通。
    /// 6. 存读档：施工进行到一半（有的格建成、有的还是虚影）时“保存并返回主菜单”→ 读取：施工、撤销栈、库存逐项一致 → 继续施工完成 → 读档后按 Ctrl+Z 仍能撤销存档前的最后一步。
    /// </summary>
    public static class FgjM3ReverseJourney
    {
        public const string Id = "FGJ-M3R";

        /// <summary>测试种子 1（FgWorldGenSelfCheck 基准里有它；与 FGJ-M3 不同）。</summary>
        public const int TestSeed = FgjM1ReverseJourney.TestSeed;

        private const string ToolBeltT3 = "belt_t3";
        private const string TypePole = "power_pole";

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M3 反向：库存不够时施工等材料再恢复、传送带拖过建筑被拒与机器走不到、暂停中规划与倍速、运送途中取消目标、电网断开与撤销拆除、施工到一半存读档",
            Seed = TestSeed,
            TotalTimeoutSeconds = 1500,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { FgjM3Common.ResetSampling(); FgjM3Journey.IsolateLayoutLibrary(c); })),
                S("menu_new", "主菜单点“新建”（测试种子 1）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台工程机", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_a", "左键点一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep1", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("fac_open", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryOpen, retries: 1),
                S("fac_produce", "点“生产 ERC-003”：废料花在造机器上", 10, c => { c.SetInt("verA", FgjM1Journey.Erc003Record()?.ActiveVersion ?? 1); FgjM2Common.ClickProduce(c); },
                    c => FgjM2Common.TickProduceQueued(c, "verA"), retries: 1),
                S("fac_close", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                    FgjM1Journey.TickFactoryClosed, retries: 1),

                // ── 1. 资源不足 ──
                S("build_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r1_pick", "点“传送带 T3”（每格 3 废料）", 10, null, c => FgjM3Common.TickPick(c, ToolBeltT3), retries: 2),
                S("r1_plan", "按格网规则找一块能铺 L 形长带的空地：造价比库存多", 10, null, TickR1Plan),
                S("r1_drag", "按住左键拖出这条 T3 传送带：拖的时候写“成本…还差多少”，虚影照放、不扣料", 20, null, TickR1Dragged, retries: 2),
                S("r1_wait", "施工用光库存：剩下的格“等待材料”，指着它状态行写明还差多少", 240, null, TickR1Waiting),
                S("r1_queue", "按施工队列键（默认 Alt+B）：队列里那一行也写“等待材料”", 10, c => JourneyInput.PressToggleTo(GameActionId.ConstructionQueue, () => ConstructionQueuePanelUIToolkit.IsOpen, true),
                    TickR1Queue, retries: 1),
                S("r1_queue_close", "再按施工队列键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.ConstructionQueue, () => ConstructionQueuePanelUIToolkit.IsOpen, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : ConstructionQueuePanelUIToolkit.IsOpen ? StepOutcome.Retry("施工队列没关") : StepOutcome.Done("关闭施工队列"), retries: 1),
                S("r1_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r1_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("r1_salvage", "右键点开局残骸（拆解）拿废料", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("r1_done", "废料到了：等材料的格自动开工，整条 T3 传送带建成", 300, null, TickR1Built),

                // ── 2. 路径失败 ──
                S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r2_pick", "点“传送带 T1”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolBeltT1), retries: 2),
                S("r2_block", "按住左键从一座建筑的一侧直线拖到另一侧（穿过建筑）：整条被拒，写明哪一格为什么，什么都没放", 20, null, TickR2Blocked, retries: 2),
                S("r2_detour", "绕开那座建筑重拖（一段一段）：放下", 60, null, c => FgjM3Common.TickLayRoute(c, "detour", c.GetInt("detourDir"), FgjM3Common.ToolBeltT1), retries: 1),
                S("r2_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r2_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("r2_find", "按种子地形找一个走不到的地方，镜头平移过去（方向键）", 60, FgjM1ReverseJourney.FindUnreachable, c => FgjM1ReverseJourney.TickPannedTo(c, "bad")),
                S("r2_bad", "右键点那里：命令结束，通知写明原因（可定位）+ 拒绝音", 30, c => FgjM1ReverseJourney.RightClickTarget(c, "bad"), FgjM1ReverseJourney.TickUnreachableReported),
                S("r2_home", "按回到归还核心键", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), FgjM1ReverseJourney.TickCameraHome, retries: 1),
                S("r2_ok", "再右键点一个走得到的地方：照常到达", 60, FgjM1ReverseJourney.RightClickReachable, FgjM1ReverseJourney.TickArrived),

                S("r2_built", "绕行的传送带建成", 180, null, c => FgjM3Common.AllBuilt(FgjM3Common.DecodeCells(c.Get("detour")), null, out int u)
                    ? StepOutcome.Done($"绕行的 {FgjM3Common.DecodeCells(c.Get("detour")).Count} 格传送带建成；库存 {St.Scrap}") : StepOutcome.Wait),
                S("sv2_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("sv2", "右键点第二处开局残骸（拆解）", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("sv2_wait", "等残骸拆完", 180, null, FgjM3Common.TickWreck2Done),
                // ── 3. 暂停：暂停中规划、施工不动；倍速只改快慢不改结果 ──
                S("r3_pause", "按暂停键（战略暂停）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("r3_build", "暂停中按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r3_plan", "看地形：起始区里的水源", 10, null, TickR3Plan),
                S("r3_pump_pick", "点“泵”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolPump), retries: 2),
                S("r3_pump", "暂停中左键把泵放在水源上（规划照样能做）", 20, null, c => TickPlacePiece(c, 0), retries: 3),
                S("r3_pipe_pick", "点“管线 T1”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolPipeT1), retries: 2),
                S("r3_pipe", "暂停中拖 3 格管线", 20, null, TickR3Pipe, retries: 3),
                S("r3_tank_pick", "点“储罐”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolTank), retries: 2),
                S("r3_tank", "暂停中放下储罐", 20, null, c => TickPlacePiece(c, FgjM3Common.PipeCells + 1), retries: 3),
                S("r3_frozen", "暂停 1.5 真实秒：世界步数不变、施工单一个都没开工", 15, null, TickR3Frozen),
                S("r3_resume", "按暂停键恢复：机器开工，抽水线建成，泵开始抽水", 300, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false), TickR3Built),
                S("r3_half", "按 0.5 倍速键：泵按游戏时间仍是每秒 5 升（真实时间变慢）", 60, c => JourneyInput.PressAction(GameActionId.SpeedHalf), c => TickR3Rate(c, 0.5f, "half"), retries: 1),
                S("r3_triple", "按 3 倍速键：泵按游戏时间仍是每秒 5 升（真实时间变快）", 60, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickR3Rate(c, 3f, "triple"), retries: 1),
                S("r3_pause2", "再按暂停：泵累计与储罐读数冻结", 15, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), TickR3PausedReadouts, retries: 1),
                S("r3_resume2", "按暂停键恢复", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Retry("没恢复") : StepOutcome.Done($"恢复（{GameClock.Speed}x）"), retries: 1),
                S("r3_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),

                // ── 4. 目标死亡：运送途中目标没了 ──
                S("r4_speed", "按 1 倍速键（看清机器运料）", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedNormal, !Mathf.Approximately(GameClock.Speed, 1f)),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : Mathf.Approximately(GameClock.Speed, 1f) ? StepOutcome.Done("1 倍速") : StepOutcome.Retry($"倍速 {GameClock.Speed}x"), retries: 1),
                S("r4_build", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r4_pick", "点“储罐”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolTank), retries: 2),
                S("r4_place", "在离仓库较远（22 格外）的空地放一个储罐虚影", 20, null, TickR4Placed, retries: 3),
                S("r4_carry", "机器取了料、正运往现场", 120, null, TickR4Carrying),
                S("r4_demo", "按拆除模式键（默认 X）", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, true),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没进拆除模式"), retries: 1),
                S("r4_cancel", "左键点那个储罐虚影：取消规划（目标没了）", 20, null, TickR4Cancelled, retries: 2),
                S("r4_refund", "机器把这一趟材料退回仓库：库存一件不少，施工单作废、不留孤儿虚影", 120, null, TickR4Refunded),
                S("r4_demo_off", "再按拆除模式键退出", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("退出拆除模式") : StepOutcome.Retry("没退出"), retries: 1),
                S("r4_speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 5. 断网 / 失联：电网断开 ──
                S("r6_plan", "按格网规则找：归还核心配电范围外能放维修台的地方、接回去的电塔位置", 10, null, TickR6Plan),
                S("r6_reloc", "按搬迁键（默认 E）", 10, c => JourneyInput.PressToggleTo(GameActionId.RelocateMode, () => FgjM3Common.Mode.RelocateMode, true),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.RelocateMode ? StepOutcome.Done("进入搬迁模式") : StepOutcome.Retry("没进搬迁模式"), retries: 1),
                S("r6_pickup", "左键点维修台：点起来跟着鼠标", 20, null, TickR6PickedUp, retries: 3),
                S("r6_drop", "左键点配电范围外的新位置：出现搬迁虚影", 20, null, TickR6Dropped, retries: 3),
                S("r6_reloc_off", "再按搬迁键退出", 10, c => JourneyInput.PressToggleTo(GameActionId.RelocateMode, () => FgjM3Common.Mode.RelocateMode, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !FgjM3Common.Mode.RelocateMode ? StepOutcome.Done("退出搬迁模式") : StepOutcome.Retry("没退出"), retries: 1),
                S("r6_moved", "机器搬完：维修台到了新位置，没电（不在任何电网的覆盖里）", 300, null, TickR6Moved),
                S("r6_diag", "按 Ctrl+O：“为什么不工作”写维修台“未接入电网”", 30, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, true),
                    c => TickR6Diagnosed(c, true), retries: 1),
                S("r6_diag_close", "再按 Ctrl+O 关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenDiagnosis, () => DiagnosisPanelUIToolkit.IsOpen, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : DiagnosisPanelUIToolkit.IsOpen ? StepOutcome.Retry("没关") : StepOutcome.Done("关闭“为什么不工作”"), retries: 1),
                S("r6_pause", "按暂停键（战略暂停：规划与派工照样能做，劳动岗不会抢先接单）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true),
                    FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("r6_pole_pick", "“能源”分类点“电塔 T1”", 10, null, c => FgjM3Common.TickPick(c, TypePole), retries: 2),
                S("r6_pole", "在核心配电范围边上、离维修台 8 格内左键放一座电塔虚影（预览写会接入哪个电网）", 20, null, TickR6PolePlaced, retries: 3),
                S("r6_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r6_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("r6_go", "右键点电塔虚影：“去建这个”，施工单交给这台机器", 20, null, TickR6GoBuild, retries: 3),
                S("r6_resume", "按暂停键恢复", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Retry("没恢复") : StepOutcome.Done("恢复，这台机器去建电塔"), retries: 1),
                S("r6_powered", "电塔建成：维修台接进电网、有电", 240, null, c => TickR6Power(c, BuildingPowerState.Powered)),
                S("r6_build", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r6_demo", "按拆除模式键", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, true),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没进拆除模式"), retries: 1),
                S("r6_ask", "左键点电塔：弹确认框，写明拆掉的后果（维修台会失去供电）", 20, null, TickR6AskDemolish, retries: 2),
                S("r6_cancel", "确认框点“取消”：什么都不变", 10, c => FgjM1Journey.ClickUi(c, "[UiKitOverlayHost]", "ConfirmCancel"), TickR6Cancelled, retries: 1),
                S("r6_ask2", "再左键点电塔：确认框", 20, null, TickR6AskDemolish, retries: 2),
                S("r6_ok", "点“确认”：电塔标记拆除", 10, c => FgjM1Journey.ClickUi(c, "[UiKitOverlayHost]", "ConfirmOk"), TickR6Marked, retries: 1),
                S("r6_cut", "机器上门拆掉电塔（全额返还）：维修台断电", 240, null, TickR6Cut),
                S("r6_undo", "按撤销键（默认 Ctrl+Z）：撤销这次拆除，电塔虚影放回原处", 10, c => JourneyInput.PressAction(GameActionId.Undo), TickR6UndoGhost, retries: 1),
                S("r6_back", "机器重建电塔：维修台重新接通", 240, null, c => TickR6Power(c, BuildingPowerState.Powered)),
                S("r6_demo_off", "按拆除模式键退出", 10, c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, false),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("退出拆除模式") : StepOutcome.Retry("没退出"), retries: 1),

                // ── 6. 存读档：施工进行到一半 ──
                S("r5_pick", "点“传送带 T1”", 10, null, c => FgjM3Common.TickPick(c, FgjM3Common.ToolBeltT1), retries: 2),
                S("r5_drag", "按住左键拖一段 10 格传送带虚影（撤销栈最上面一步）", 20, null, TickR5Dragged, retries: 2),
                S("r5_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r5_mid", "等施工进行到一半：有的格建成、有的还是虚影", 120, null, TickR5Midway),
                S("r5_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickR5Pause, retries: 1),
                S("r5_save", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("r5_menu", "回到主菜单：存档里施工进度、施工单、撤销栈、库存与存档那一刻一致", 60, null, TickR5Disk),
                S("r5_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("r5_loaded", "读档进入家园：逐项一致", 120, null, TickR5Loaded),
                S("r5_done", "读档后施工接着做完：10 格全部建成", 240, null, TickR5Finished),
                S("r5_build", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("r5_undo", "按撤销键：读档后仍能撤销存档前的最后一步（这段传送带，建成的立即拆掉全额返还）", 15, c => JourneyInput.PressAction(GameActionId.Undo), TickR5Undone, retries: 1),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static int GroundScrap() =>
            St?.GroundItems?.Where(g => g != null && g.RegionId == HomeValleyLayout.RegionId && g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount) ?? 0;

        private static int CargoScrap() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Sum(m => HomeValleyConstruction.CargoScrap(m));

        private static string Status => FgjM3Common.Mode?.StatusText ?? string.Empty;

        private static string OneLine(string s) => (s ?? string.Empty).Replace("\n", " ");

        // ── 1. 资源不足 ──────────────────────────────────────────────────────────

        private static StepOutcome TickR1Plan(JourneyContext c)
        {
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            HashSet<long> ports = FgjM3Common.AllPortCells(s);
            int stock = s.Scrap;
            int perCell = 3;
            int n = Math.Max(10, stock / (perCell * 2) + 4); // 两段各 n 格：总成本明显高于库存
            foreach (int r in Enumerable.Range(8, 20))
            {
                for (int a = 0; a < 16; a++)
                {
                    double ang = a * Math.PI / 8;
                    var start = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                    foreach ((int sx, int sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                    {
                        var end = new GridCell(start.X + sx * n, start.Y + sy * n);
                        var cells = new List<GridCell>();
                        HomeGridService.BuildBeltPath(start, end, BeltDir.East, cells, new List<BeltDir>());
                        bool ok = cells.All(p => HomeGridService.ValidateBeltCell(s, p).Ok && !ports.Contains(FgjM3Common.Key(p))
                                                 && Enumerable.Range(0, 4).All(d => HomeGridService.BuildingAt(s, FgjM3Common.Step(p, d)) == null && !ports.Contains(FgjM3Common.Key(FgjM3Common.Step(p, d)))));
                        if (!ok)
                        {
                            continue;
                        }
                        c.Set("r1path", FgjM3Common.EncodeCells(cells));
                        FgjM3Common.SetCell(c, "r1a", start);
                        FgjM3Common.SetCell(c, "r1b", end);
                        return StepOutcome.Done($"L 形 {cells.Count} 格 T3（{perCell} 废料 / 格，共 {cells.Count * perCell}），此刻库存 {stock}：从 {FgjM3Common.Cell(start)} 拖到 {FgjM3Common.Cell(end)}");
                    }
                }
            }
            return StepOutcome.Fail("核心附近找不到能铺这条 L 形传送带的空地");
        }

        private static List<GridCell> R1Path(JourneyContext c) => FgjM3Common.DecodeCells(c.Get("r1path"));

        private static StepOutcome TickR1Dragged(JourneyContext c)
        {
            GridCell a = FgjM3Common.GetCell(c, "r1a");
            GridCell b = FgjM3Common.GetCell(c, "r1b");
            if (!FgjM3Common.Done(c, "s0"))
            {
                c.SetInt("r1scrap0", St.Scrap);
                FgjM3Common.Mark(c, "s0");
            }
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(a), FgjM3Common.Ground(b))))
            {
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (info.Length > 0)
            {
                c.Set("r1info", OneLine(info));
            }
            if (FgjM3Common.SinceMs(c, "drag") < 800)
            {
                return StepOutcome.Wait;
            }
            List<GridCell> path = R1Path(c);
            int planned = path.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            string seen = c.Get("r1info", string.Empty);
            return planned == path.Count && seen.Contains("还差") && St.Scrap == c.GetInt("r1scrap0")
                ? StepOutcome.Done($"拖的时候建造栏写“{seen}”（成本高于库存，只警告不拦）；松开放下 {planned} 格 T3 虚影，不扣料（库存 {St.Scrap}）")
                : StepOutcome.Retry($"拖完：虚影 {planned}/{path.Count}、建造栏“{seen}”、库存 {c.GetInt("r1scrap0")} → {St.Scrap}");
        }

        private static WorkOrderRecord OrderAt(GridCell cell)
        {
            if (!HomeValleyConstruction.TryFindPlannedCell(St, cell, out PlannedBeltRecord plan, out _))
            {
                return null;
            }
            return HomeValleyWorkOrders.FindActiveBuild(St, HomeValleyConstruction.BeltPlanPrefix + plan.PlanId);
        }

        private static StepOutcome TickR1Waiting(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> path = R1Path(c);
            foreach (GridCell p in path)
            {
                WorkOrderRecord o = OrderAt(p);
                string status = HomeValleyConstruction.DescribeStatus(St, o);
                if (o != null && o.State == WorkOrderState.Waiting && status.Contains("等待材料") && St.Scrap < 3)
                {
                    int built = path.Count(q => FgjM3Common.BeltInfo(q, out _) && !HomeValleyConstruction.TryFindPlannedCell(St, q, out _, out _));
                    int unbuilt = path.Count(q => HomeValleyConstruction.TryFindPlannedCell(St, q, out _, out _));
                    return StepOutcome.Done($"库存用光（{St.Scrap}）：已建成 {built} 格、还有 {unbuilt} 格是虚影；{FgjM3Common.Cell(p)} 的施工单“{status}”");
                }
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickR1Queue(JourneyContext c)
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
                if (panel.RowStatus(i).Contains("等待材料"))
                {
                    return StepOutcome.Done($"施工队列 {panel.VisibleRowCount} 行，其中“{panel.RowName(i)}：{panel.RowStatus(i)}”");
                }
            }
            return StepOutcome.Fail($"施工队列里没有“等待材料”的行（{string.Join(" | ", Enumerable.Range(0, panel.VisibleRowCount).Select(i => panel.RowName(i) + "：" + panel.RowStatus(i)))}）");
        }

        private static StepOutcome TickR1Built(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> path = R1Path(c);
            if (!FgjM3Common.AllBuilt(path, null, out int unbuilt))
            {
                return StepOutcome.Wait;
            }
            int t3 = path.Count(p => FgjM3Common.BeltInfo(p, out BeltCellInfo i) && i.Tier == 2);
            return t3 == path.Count
                ? StepOutcome.Done($"残骸的废料到了，等材料的格自动开工：{path.Count} 格 T3 全部建成（{c.StepElapsed:F0} 秒）；库存 {St.Scrap}")
                : StepOutcome.Fail($"建成的 T3 只有 {t3}/{path.Count} 格");
        }

        // ── 2. 路径失败 ─────────────────────────────────────────────────────────

        private static StepOutcome TickR2Blocked(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "plan"))
            {
                // 穿过一座开局建筑的直线（横穿或竖穿），两端外扩 2～4 格、都能铺；再按格网规则现找一条绕开它的路线（B25：种子 1 的仓库一侧是悬崖，所以逐座逐向找）。
                HashSet<long> ports = FgjM3Common.AllPortCells(s);
                var why = new List<string>();
                string[] types =
                {
                    HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeAssemblyStation, HomeValleyLayout.BuildingTypeAnalysisBench,
                    HomeValleyLayout.BuildingTypeRepairBay, HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeSignalTower,
                };
                foreach (string type in types)
                {
                    BuildingRecord bld = FgjM3Common.Home(type);
                    if (bld == null || !GridContent.TryGetBuilding(type, out GameConfig.fg.BuildingGrid g))
                    {
                        continue;
                    }
                    GridMath.FootprintBounds(new GridCell(bld.GridX, bld.GridY), g.FootprintW, g.FootprintH, (int)bld.Rotation, out GridCell min, out GridCell max);
                    for (int axis = 0; axis < 2 && !FgjM3Common.Done(c, "plan"); axis++)
                    {
                        int lo = axis == 0 ? min.Y : min.X;
                        int hi = axis == 0 ? max.Y : max.X;
                        for (int line = lo; line <= hi && !FgjM3Common.Done(c, "plan"); line++)
                        {
                            for (int k = 2; k <= 4 && !FgjM3Common.Done(c, "plan"); k++)
                            {
                                GridCell a = axis == 0 ? new GridCell(min.X - k, line) : new GridCell(line, min.Y - k);
                                GridCell b = axis == 0 ? new GridCell(max.X + k, line) : new GridCell(line, max.Y + k);
                                int n = axis == 0 ? b.X - a.X : b.Y - a.Y;
                                string bad = null;
                                for (int i = 0; i <= n && bad == null; i++)
                                {
                                    GridCell q = axis == 0 ? new GridCell(a.X + i, line) : new GridCell(line, a.Y + i);
                                    BuildingRecord occ = HomeGridService.BuildingAt(s, q);
                                    if (ports.Contains(FgjM3Common.Key(q)))
                                    {
                                        bad = $"{FgjM3Common.Cell(q)} 是端口外侧格";
                                    }
                                    else if (occ != null && occ.BuildingId != bld.BuildingId)
                                    {
                                        bad = $"{FgjM3Common.Cell(q)} 有别的建筑";
                                    }
                                    else if (occ == null && !HomeGridService.ValidateBeltCell(s, q).Ok)
                                    {
                                        bad = $"{FgjM3Common.Cell(q)} 不能铺（{HomeGridService.ValidateBeltCell(s, q).Describe()}）";
                                    }
                                }
                                if (bad != null)
                                {
                                    if (k == 4)
                                    {
                                        why.Add($"{type} {(axis == 0 ? "y" : "x")}={line}：{bad}");
                                    }
                                    continue;
                                }
                                int dir = axis == 0 ? 1 : 0;
                                List<GridCell> detour = FgjM3Common.PlanRoute(s, a, b, dir, (dir + 2) & 3, ports, 6);
                                if (detour == null)
                                {
                                    why.Add($"{type} {(axis == 0 ? "y" : "x")}={line}：绕不过去");
                                    continue;
                                }
                                if (!FgjM3Common.Clear(FgjM3Common.Ground(a), out _) && !FgjM3Common.Clear(FgjM3Common.Ground(b), out _) && Vector2.Distance(FgjM3Common.Ground(a), FgjM3Common.Ground(b)) > 30f)
                                {
                                    continue;
                                }
                                FgjM3Common.SetCell(c, "r2a", a);
                                FgjM3Common.SetCell(c, "r2b", b);
                                c.Set("r2building", HomeGridService.DisplayName(type));
                                c.Set("detour", FgjM3Common.EncodeCells(detour));
                                c.SetInt("detourDir", dir);
                                c.SetInt("r2planned0", HomeValleyConstruction.PlannedCellCount(s));
                                c.SetInt("r2scrap0", s.Scrap);
                                FgjM3Common.Mark(c, "plan");
                            }
                        }
                    }
                    if (FgjM3Common.Done(c, "plan"))
                    {
                        break;
                    }
                }
                if (!FgjM3Common.Done(c, "plan"))
                {
                    return StepOutcome.Fail($"开局建筑两侧都找不到能对穿的直线：{string.Join("；", why.Take(8))}");
                }
            }
            GridCell ra = FgjM3Common.GetCell(c, "r2a");
            GridCell rb = FgjM3Common.GetCell(c, "r2b");
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(ra), FgjM3Common.Ground(rb))))
            {
                string info = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
                return StepOutcome.Wait;
            }
            string st = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
            if (st.Contains("不能"))
            {
                c.Set("r2dragStatus", OneLine(st));
            }
            if (FgjM3Common.SinceMs(c, "drag") < 800)
            {
                return StepOutcome.Wait;
            }
            bool nothing = HomeValleyConstruction.PlannedCellCount(s) == c.GetInt("r2planned0") && s.Scrap == c.GetInt("r2scrap0");
            string status = Status;
            bool refused = !FgjM3Common.Mode.LastResult.Success && FgjM3Common.Mode.StatusIsError;
            return nothing && refused
                ? StepOutcome.Done($"从 {FgjM3Common.Cell(ra)} 拖到 {FgjM3Common.Cell(rb)}（穿过{c.Get("r2building")}）：整条被拒，状态行“{OneLine(status)}”；拖的时候“{c.Get("r2dragStatus", "-")}”；一格都没放、库存不变")
                : StepOutcome.Retry($"穿过仓库的拖拽没被拒：规划格 {c.GetInt("r2planned0")} → {HomeValleyConstruction.PlannedCellCount(s)}、结果 {FgjM3Common.Mode.LastResult.Success}、状态行“{status}”");
        }

        // ── 3. 暂停 ──────────────────────────────────────────────────────────────

        private static GridCell[] Line(JourneyContext c) => FgjM3Common.WaterLine(FgjM3Common.GetCell(c, "pump"), c.GetInt("wdir"));

        private static StepOutcome TickR3Plan(JourneyContext c)
        {
            CampaignState s = St;
            GridCell core = HomeGridService.CorePivot(s);
            var avoid = new HashSet<long>();
            foreach (GridCell p in R1Path(c).Concat(FgjM3Common.DecodeCells(c.Get("detour"))))
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        avoid.Add(FgjM3Common.Key(new GridCell(p.X + dx, p.Y + dy)));
                    }
                }
            }
            if (!FgjM3Common.FindWaterLine(s, core, 3, 30, avoid, out GridCell pump, out int dir))
            {
                return StepOutcome.Fail("起始区里找不到能放抽水线的水源");
            }
            FgjM3Common.SetCell(c, "pump", pump);
            c.SetInt("wdir", dir);
            c.SetLong("pauseTicks", GameClock.Ticks);
            return StepOutcome.Done($"水源 {FgjM3Common.Cell(pump)}（抽水线朝{FgjM3Common.DirName(dir)}）；暂停中（第 {GameClock.Ticks} 步）");
        }

        private static StepOutcome TickPlacePiece(JourneyContext c, int index)
        {
            GridCell cell = Line(c)[index];
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(cell))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return HomeValleyConstruction.TryFindPlannedCell(St, cell, out _, out _)
                ? StepOutcome.Done($"左键放下虚影 {FgjM3Common.Cell(cell)}（暂停中 {GameClock.Paused}）")
                : StepOutcome.Retry($"没有虚影（状态行“{Status}”）");
        }

        private static StepOutcome TickR3Pipe(JourneyContext c)
        {
            GridCell[] line = Line(c);
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(line[1]), FgjM3Common.Ground(line[FgjM3Common.PipeCells]))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "drag") < 700)
            {
                return StepOutcome.Wait;
            }
            int planned = Enumerable.Range(1, FgjM3Common.PipeCells).Count(i => HomeValleyConstruction.TryFindPlannedCell(St, line[i], out _, out _));
            return planned == FgjM3Common.PipeCells ? StepOutcome.Done($"暂停中拖出 {planned} 格管线虚影") : StepOutcome.Retry($"管线虚影 {planned}/{FgjM3Common.PipeCells}");
        }

        private static StepOutcome TickR3Frozen(JourneyContext c)
        {
            GridCell[] line = Line(c);
            if (!FgjM3Common.Done(c, "t0"))
            {
                c.SetLong(FgjM3Common.SK(c, "ticks"), GameClock.Ticks);
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            var orders = line.Select(OrderAt).Where(o => o != null).Distinct().ToList();
            bool noneStarted = orders.All(o => o.State == WorkOrderState.Ready || o.State == WorkOrderState.Waiting) && orders.Count > 0;
            bool frozen = GameClock.Paused && GameClock.Ticks == c.GetLong(FgjM3Common.SK(c, "ticks")) && GameClock.Ticks == c.GetLong("pauseTicks");
            return frozen && noneStarted && line.All(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _))
                ? StepOutcome.Done($"暂停中：5 件虚影都在（{orders.Count} 张施工单都在待分配 / 等待，没人开工），世界停在第 {GameClock.Ticks} 步")
                : StepOutcome.Fail($"暂停中状态不对：暂停 {GameClock.Paused}、步数 {c.GetLong("pauseTicks")} → {GameClock.Ticks}、施工单 [{string.Join(",", orders.Select(o => o.State))}]");
        }

        private static StepOutcome TickR3Built(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            GridCell[] line = Line(c);
            if (GameClock.Paused)
            {
                return c.StepElapsed > 3 ? StepOutcome.Retry("按了暂停键没有恢复") : StepOutcome.Wait;
            }
            if (!FgjM3Common.AllBuilt(null, line, out _) || FgjM3Common.PumpTotalMl(line[0]) <= 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"恢复后机器开工：抽水线建成，泵开始抽水（{FgjM3Common.PumpTotalMl(line[0]) / 1000.0:F1} 升）");
        }

        private static StepOutcome TickR3Rate(JourneyContext c, float speed, string key)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            GridCell pump = Line(c)[0];
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
                c.SetLong(FgjM3Common.SK(c, "ml0"), FgjM3Common.PumpTotalMl(pump));
                c.SetLong(FgjM3Common.SK(c, "real0"), FgjM3Common.NowMs());
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            double gameSec = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"))) / (double)GameClock.StepHz;
            if (gameSec < 8.0)
            {
                return StepOutcome.Wait;
            }
            double realSec = (FgjM3Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "real0"))) / 1000.0;
            double litres = (FgjM3Common.PumpTotalMl(pump) - c.GetLong(FgjM3Common.SK(c, "ml0"))) / 1000.0;
            double perGameSec = litres / gameSec;
            double perRealSec = litres / realSec;
            double design = PipeNetworkService.Kernel.Config.PumpLitersPerMinute / 60.0;
            c.Set("rate." + key, perRealSec.ToString("R", CultureInfo.InvariantCulture));
            string ratio = string.Empty;
            bool ratioOk = true;
            if (key == "triple" && double.TryParse(c.Get("rate.half"), NumberStyles.Float, CultureInfo.InvariantCulture, out double half) && half > 0)
            {
                double r = perRealSec / half;
                ratioOk = r > 3.0;
                ratio = $"；按真实时间是 0.5x 时的 {r:F1} 倍（理论 6 倍，帧节奏有抖动）";
            }
            return Math.Abs(perGameSec - design) <= design * 0.03 && ratioOk
                ? StepOutcome.Done($"{speed}x：{gameSec:F1} 游戏秒（{realSec:F1} 真实秒）抽 {litres:F1} 升 = 每游戏秒 {perGameSec:F2} 升（设计 {design:F2}）{ratio}")
                : StepOutcome.Fail($"{speed}x 时泵的速率不对：每游戏秒 {perGameSec:F2} 升（设计 {design:F2}）{ratio}");
        }

        private static StepOutcome TickR3PausedReadouts(JourneyContext c)
        {
            GridCell[] line = Line(c);
            if (!FgjM3Common.Done(c, "t0"))
            {
                if (c.StepElapsed < 0.4 || !GameClock.Paused)
                {
                    return c.StepElapsed > 3 ? StepOutcome.Retry("没暂停") : StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "ml"), FgjM3Common.PumpTotalMl(line[0]));
                c.SetLong(FgjM3Common.SK(c, "tank"), FgjM3Common.TankMl(line[line.Length - 1]));
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 2.0)
            {
                return StepOutcome.Wait;
            }
            bool same = FgjM3Common.PumpTotalMl(line[0]) == c.GetLong(FgjM3Common.SK(c, "ml")) && FgjM3Common.TankMl(line[line.Length - 1]) == c.GetLong(FgjM3Common.SK(c, "tank"));
            return same
                ? StepOutcome.Done($"暂停 1.5 真实秒：泵累计 {c.GetLong(FgjM3Common.SK(c, "ml")) / 1000.0:F1} 升、储罐 {c.GetLong(FgjM3Common.SK(c, "tank")) / 1000.0:F1} 升都没变")
                : StepOutcome.Fail("暂停中泵 / 储罐读数在变");
        }

        // ── 4. 目标死亡 ────────────────────────────────────────────────────────────

        private static StepOutcome TickR4Placed(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "found"))
            {
                BuildingRecord wh = FgjM3Common.Home(HomeValleyLayout.BuildingTypeWarehouse);
                var from = new GridCell(wh.GridX, wh.GridY);
                HashSet<long> ports = FgjM3Common.AllPortCells(s);
                GridCell? spot = null;
                for (int r = 22; r <= 34 && spot == null; r++)
                {
                    for (int a = 0; a < 24 && spot == null; a++)
                    {
                        double ang = a * Math.PI / 12;
                        var p = new GridCell(from.X + (int)Math.Round(Math.Cos(ang) * r), from.Y + (int)Math.Round(Math.Sin(ang) * r));
                        if (HomeGridService.ValidatePipeCell(s, p, PipePieceKind.Tank).Ok && PipeNetworkService.SourceFluidAt(s, p) == 0 && !ports.Contains(FgjM3Common.Key(p))
                            && Enumerable.Range(0, 4).All(d => !PipeNetworkService.TryGetPiece(FgjM3Common.Step(p, d), out _, out _) && !HomeValleyConstruction.TryFindPlannedCell(s, FgjM3Common.Step(p, d), out _, out _))
                            && FgjM3Common.Clear(FgjM3Common.Ground(p), out _))
                        {
                            spot = p;
                        }
                    }
                }
                if (spot == null)
                {
                    return StepOutcome.Fail("离仓库 22～34 格的画面里找不到能放储罐的空地");
                }
                FgjM3Common.SetCell(c, "r4", spot.Value);
                c.SetInt("r4total", s.Scrap + GroundScrap() + CargoScrap());
                FgjM3Common.Mark(c, "found");
            }
            GridCell cell = FgjM3Common.GetCell(c, "r4");
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(cell))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return HomeValleyConstruction.TryFindPlannedCell(s, cell, out _, out _)
                ? StepOutcome.Done($"在 {FgjM3Common.Cell(cell)} 放下储罐虚影（离仓库较远，机器要运一段路）；此刻库存 + 地面 + 货舱共 {c.GetInt("r4total")} 废料")
                : StepOutcome.Retry($"没有储罐虚影（状态行“{Status}”）");
        }

        private static StepOutcome TickR4Carrying(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            WorkOrderRecord o = OrderAt(FgjM3Common.GetCell(c, "r4"));
            if (o == null)
            {
                return StepOutcome.Fail("储罐虚影的施工单不见了");
            }
            if (o.State != WorkOrderState.Reserved || o.Leg == 1 || o.AssignedMachineLogicId <= 0
                || !MachineRegistry.TryGetRecord(o.AssignedMachineLogicId, out MachineRecord m) || HomeValleyConstruction.CargoScrap(m) <= 0)
            {
                return StepOutcome.Wait;
            }
            c.SetInt("r4machine", o.AssignedMachineLogicId);
            c.SetInt("r4cargo", HomeValleyConstruction.CargoScrap(m));
            return StepOutcome.Done($"{FgjM3Common.Label(o.AssignedMachineLogicId)} 取了 {c.GetInt("r4cargo")} 废料正运往现场（“{HomeValleyConstruction.DescribeStatus(St, o)}”）");
        }

        private static StepOutcome TickR4Cancelled(JourneyContext c)
        {
            GridCell cell = FgjM3Common.GetCell(c, "r4");
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(cell))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return !HomeValleyConstruction.TryFindPlannedCell(St, cell, out _, out _)
                ? StepOutcome.Done($"拆除模式点储罐虚影：取消规划（状态行“{OneLine(Status)}”）")
                : StepOutcome.Retry($"点了之后虚影还在（状态行“{Status}”）");
        }

        private static StepOutcome TickR4Refunded(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int id = c.GetInt("r4machine");
            if (!MachineRegistry.TryGetRecord(id, out MachineRecord m))
            {
                return StepOutcome.Fail("那台机器不见了");
            }
            int cargo = HomeValleyConstruction.CargoScrap(m);
            int total = St.Scrap + GroundScrap() + CargoScrap();
            if (cargo > 0 || total != c.GetInt("r4total"))
            {
                return StepOutcome.Wait;
            }
            GridCell cell = FgjM3Common.GetCell(c, "r4");
            bool orphan = HomeValleyConstruction.TryFindPlannedCell(St, cell, out _, out _) || PipeNetworkService.TryGetPiece(cell, out _, out _);
            WorkOrderRecord active = HomeValleyWorkOrders.FindActiveOrderForMachine(St, id);
            return !orphan
                ? StepOutcome.Done($"{FgjM3Common.Label(id)} 把这一趟的 {c.GetInt("r4cargo")} 废料退回仓库：库存 + 地面 + 货舱仍是 {total}（一件不少），现场没有留下虚影或储罐；它现在的工单：{(active == null ? "无（空闲）" : active.Kind + " " + active.TargetId)}")
                : StepOutcome.Fail($"取消后现场还留着虚影或储罐 {FgjM3Common.Cell(cell)}");
        }

        // ── 5. 断网 / 失联（电网断开）──────────────────────────────────────────────────

        private static BuildingRecord Bay => FgjM3Common.Home(HomeValleyLayout.BuildingTypeRepairBay);

        private static StepOutcome TickR6Plan(JourneyContext c)
        {
            CampaignState s = St;
            BuildingRecord bay = Bay;
            GridCell core = HomeGridService.CorePivot(s);
            GridContent.TryGetBuilding(HomeValleyLayout.BuildingTypeRepairBay, out GameConfig.fg.BuildingGrid g);
            float coreRadius = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
            foreach (int r in Enumerable.Range(31, 6))
            {
                for (int a = 0; a < 36; a++)
                {
                    double ang = a * Math.PI / 18;
                    var p = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (!HomeGridService.ValidatePlacement(s, bay.BuildingTypeId, p, (int)bay.Rotation, asPlayerPlacement: false, ignoreBuildingId: bay.BuildingId, checkCost: false).Ok)
                    {
                        continue;
                    }
                    GridMath.FootprintBounds(p, g.FootprintW, g.FootprintH, (int)bay.Rotation, out GridCell min, out GridCell max);
                    float nearest = float.MaxValue;
                    for (int y = min.Y; y <= max.Y; y++)
                    {
                        for (int x = min.X; x <= max.X; x++)
                        {
                            nearest = Mathf.Min(nearest, Vector2.Distance(new Vector2(x, y), FgjM3Common.Ground(core)));
                        }
                    }
                    if (nearest <= coreRadius + 2f || !FgjM3Common.Clear(FgjM3Common.Ground(p), out _))
                    {
                        continue;
                    }
                    // 电塔：从核心往新位置的方向走到核心配电半径以内 3 格处（两者相连），离维修台最近的占地格不超过 8 格。
                    Vector2 dirv = (FgjM3Common.Ground(p) - FgjM3Common.Ground(core)).normalized;
                    GridCell? pole = null;
                    for (float d = coreRadius - 3f; d >= coreRadius - 8f && pole == null; d -= 1f)
                    {
                        var q = new GridCell(core.X + Mathf.RoundToInt(dirv.x * d), core.Y + Mathf.RoundToInt(dirv.y * d));
                        float toBay = float.MaxValue;
                        for (int y = min.Y; y <= max.Y; y++)
                        {
                            for (int x = min.X; x <= max.X; x++)
                            {
                                toBay = Mathf.Min(toBay, Vector2.Distance(new Vector2(x, y), FgjM3Common.Ground(q)));
                            }
                        }
                        if (toBay <= 7.5f && HomeGridService.ValidatePlacement(s, TypePole, q, 0, checkCost: false).Ok
                            && (q.X < min.X - 1 || q.X > max.X + 1 || q.Y < min.Y - 1 || q.Y > max.Y + 1))
                        {
                            pole = q;
                        }
                    }
                    if (pole == null)
                    {
                        continue;
                    }
                    FgjM3Common.SetCell(c, "bayTo", p);
                    FgjM3Common.SetCell(c, "pole", pole.Value);
                    c.Set("bayId", bay.BuildingId);
                    return StepOutcome.Done($"维修台现在在 ({bay.GridX}, {bay.GridY})、有电；新位置 {FgjM3Common.Cell(p)}（最近的占地格离核心 {nearest:F1} 格 > 配电半径 {coreRadius:F0}）；电塔位置 {FgjM3Common.Cell(pole.Value)}");
                }
            }
            return StepOutcome.Fail("核心配电范围外找不到能放维修台、又能用一座电塔接回来的位置");
        }

        private static StepOutcome TickR6PickedUp(JourneyContext c)
        {
            BuildingRecord bay = Bay;
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(new Vector2(bay.GridX, bay.GridY))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.Mode.CarryBuildingId == bay.BuildingId
                ? StepOutcome.Done($"维修台被点起来（“{OneLine(Status)}”）")
                : StepOutcome.Retry($"没点起来（{FgjM3Common.Mode.CarryBuildingId}；“{Status}”）");
        }

        private static StepOutcome TickR6Dropped(JourneyContext c)
        {
            GridCell to = FgjM3Common.GetCell(c, "bayTo");
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(to))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 600)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(St, c.Get("bayId"));
            return ghost != null && ghost.GridX == to.X && ghost.GridY == to.Y
                ? StepOutcome.Done($"左键点新位置：搬迁虚影在 {FgjM3Common.Cell(to)}（“{OneLine(Status)}”），完工前维修台照常在原地")
                : StepOutcome.Retry($"没有搬迁虚影（“{Status}”）");
        }

        private static StepOutcome TickR6Moved(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord bay = Bay;
            GridCell to = FgjM3Common.GetCell(c, "bayTo");
            if (bay == null || HomeGridService.FindRelocationGhost(St, c.Get("bayId")) != null || bay.GridX != to.X || bay.GridY != to.Y
                || bay.ConstructionState != BuildingConstructionState.Operational)
            {
                return StepOutcome.Wait;
            }
            return bay.PowerState == BuildingPowerState.Unpowered
                ? StepOutcome.Done($"机器搬完（{c.StepElapsed:F0} 秒）：维修台到了 {FgjM3Common.Cell(to)}，没电（{bay.PowerState}）")
                : c.StepElapsed > 60 ? StepOutcome.Fail($"搬到配电范围外后供电状态是 {bay.PowerState}") : StepOutcome.Wait;
        }

        private static StepOutcome TickR6Diagnosed(JourneyContext c, bool expectUnconnected)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            DiagnosisPanelUIToolkit panel = DiagnosisPanelUIToolkit.Instance;
            if (!DiagnosisPanelUIToolkit.IsOpen || panel == null)
            {
                return StepOutcome.Retry("“为什么不工作”没打开");
            }
            string name = HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeRepairBay);
            for (int i = 0; i < panel.StepButtonCount; i++)
            {
                DiagStep st = panel.StepTarget(i);
                if (st != null && st.Code == DiagCode.Unconnected && st.TargetId == c.Get("bayId"))
                {
                    return StepOutcome.Done($"“为什么不工作”列出维修台：“{st.Text}”");
                }
            }
            return c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail($"“为什么不工作”里没有维修台“未接入电网”（{panel.RowCount} 行：{string.Join(" | ", Enumerable.Range(0, panel.RowCount).Select(panel.SubjectText))}；名字 {name}）");
        }

        private static StepOutcome TickR6PolePlaced(JourneyContext c)
        {
            GridCell pole = FgjM3Common.GetCell(c, "pole");
            if (!FgjM3Common.Once(c, "hover", () => FgjM3Common.TryHover(FgjM3Common.Ground(pole))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "hover") < 500)
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "seen"))
            {
                string preview = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
                if (FgjM3Common.Mode.Preview == null || !FgjM3Common.Mode.Preview.Ok)
                {
                    return FgjM3Common.SinceMs(c, "hover") < 2000 ? StepOutcome.Wait : StepOutcome.Retry($"电塔预览不能放（“{preview}”）");
                }
                c.Set("polePreview", OneLine(preview));
                FgjM3Common.Mark(c, "seen");
            }
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(pole))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 500)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = HomeGridService.BuildingAt(St, pole);
            if (b == null || !HomeValleyController.IsPlannedGhost(b))
            {
                return StepOutcome.Retry($"没有电塔虚影（“{Status}”）");
            }
            c.Set("poleId", b.BuildingId);
            return StepOutcome.Done($"指着 {FgjM3Common.Cell(pole)}：预览“{c.Get("polePreview")}”；左键放下电塔虚影");
        }

        private static StepOutcome TickR6GoBuild(JourneyContext c)
        {
            string id = c.Get("poleId");
            Transform t = FgjM1Journey.FindNamed("Building_" + HomeValleyController.LocalKey(id));
            if (t == null)
            {
                return StepOutcome.Fail("画面里找不到电塔虚影");
            }
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(new Vector2(t.position.x, t.position.z), 1)))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "rc") < 800)
            {
                return StepOutcome.Wait;
            }
            WorkOrderRecord o = HomeValleyWorkOrders.FindActiveBuild(St, id);
            BuildingRecord pole = HomeGridService.FindBuilding(St, id);
            bool built = pole != null && pole.ConstructionState == BuildingConstructionState.Operational;
            int a = c.GetInt("workerA");
            return built || (o != null && o.AssignedMachineLogicId == a)
                ? StepOutcome.Done($"右键点电塔虚影：施工单交给 {FgjM3Common.Label(a)}（“{(o != null ? HomeValleyConstruction.DescribeStatus(St, o) : "已建成")}”）；虚影保留（不是修复、不是移动）")
                : StepOutcome.Retry($"右键后施工单在 {o?.AssignedMachineLogicId}（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        private static StepOutcome TickR6Power(JourneyContext c, BuildingPowerState want)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord pole = HomeGridService.FindBuilding(St, c.Get("poleId")) ?? HomeGridService.BuildingAt(St, FgjM3Common.GetCell(c, "pole"));
            BuildingRecord bay = Bay;
            if (pole == null || pole.ConstructionState != BuildingConstructionState.Operational || bay.PowerState != want)
            {
                return StepOutcome.Wait;
            }
            c.Set("poleId", pole.BuildingId);
            return StepOutcome.Done($"电塔建成（{c.StepElapsed:F0} 秒）：维修台 {bay.PowerState}；库存 {St.Scrap}");
        }

        private static StepOutcome TickR6AskDemolish(JourneyContext c)
        {
            GridCell pole = FgjM3Common.GetCell(c, "pole");
            if (!FgjM3Common.Done(c, "s0"))
            {
                c.SetInt(FgjM3Common.SK(c, "scrap0"), St.Scrap + GroundScrap());
                FgjM3Common.Mark(c, "s0");
            }
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
                return StepOutcome.Retry($"拆除模式点电塔没有弹确认框（“{Status}”）");
            }
            string lines = string.Join("；", r.Consequences.Concat(r.Lines));
            c.SetInt("demoScrap0", St.Scrap + GroundScrap());
            return lines.Length > 0
                ? StepOutcome.Done($"确认框“{r.Title}”：{lines}")
                : StepOutcome.Fail($"确认框没写后果（“{r.Title}”）");
        }

        private static StepOutcome TickR6Cancelled(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string id = c.Get("poleId");
            BuildingRecord pole = HomeGridService.FindBuilding(St, id);
            return !UiConfirmDialog.IsOpen && pole != null && !HomeGridService.IsMarkedForDemolish(St, id) && Bay.PowerState == BuildingPowerState.Powered
                ? StepOutcome.Done("点“取消”：电塔没被标记拆除，维修台照常有电")
                : StepOutcome.Fail($"取消后：确认框开着 {UiConfirmDialog.IsOpen}、标记拆除 {HomeGridService.IsMarkedForDemolish(St, id)}、维修台 {Bay.PowerState}");
        }

        private static StepOutcome TickR6Marked(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string id = c.Get("poleId");
            return !UiConfirmDialog.IsOpen && HomeGridService.IsMarkedForDemolish(St, id)
                ? StepOutcome.Done($"点“确认”：电塔标记拆除（“{OneLine(Status)}”），等机器上门")
                : StepOutcome.Retry($"确认后没有标记拆除（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickR6Cut(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            string id = c.Get("poleId");
            if (HomeGridService.FindBuilding(St, id) != null)
            {
                return StepOutcome.Wait;
            }
            int refund = St.Scrap + GroundScrap() - c.GetInt("demoScrap0");
            bool full = refund == PoleCost();
            return Bay.PowerState == BuildingPowerState.Unpowered && full
                ? StepOutcome.Done($"机器上门拆掉电塔（施工完成后再拆，DEBT-FG0QA01-02）：全额返还 {refund} 废料；维修台断电（{Bay.PowerState}）")
                : c.StepElapsed > 60 ? StepOutcome.Fail($"拆掉后：返还 {refund}（造价 {PoleCost()}）、维修台 {Bay.PowerState}") : StepOutcome.Wait;
        }

        private static int PoleCost() => HomeValleyLayout.BuildProfile.TryGetValue(TypePole, out (int ScrapCost, float Seconds) p) ? p.ScrapCost : -1;

        private static StepOutcome TickR6UndoGhost(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = HomeGridService.BuildingAt(St, FgjM3Common.GetCell(c, "pole"));
            return b != null && HomeValleyController.IsPlannedGhost(b) && b.BuildingTypeId == TypePole
                ? StepOutcome.Done($"Ctrl+Z：撤销拆除，电塔虚影放回原处（“{OneLine(Status)}”）")
                : StepOutcome.Retry($"撤销后原处没有电塔虚影（“{Status}”）");
        }

        // ── 6. 存读档 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickR5Dragged(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "found"))
            {
                GridCell core = HomeGridService.CorePivot(s);
                HashSet<long> ports = FgjM3Common.AllPortCells(s);
                GridCell? start = null;
                for (int r = 6; r <= 24 && start == null; r++)
                {
                    for (int a = 0; a < 24 && start == null; a++)
                    {
                        double ang = a * Math.PI / 12;
                        var p = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                        bool ok = Enumerable.Range(0, 10).All(i =>
                        {
                            var q = new GridCell(p.X + i, p.Y);
                            return HomeGridService.ValidateBeltCell(s, q).Ok && !ports.Contains(FgjM3Common.Key(q))
                                   && Enumerable.Range(0, 4).All(d => !BeltNetworkService.Kernel.HasCell(FgjM3Common.Step(q, d).X, FgjM3Common.Step(q, d).Y)
                                                                      && !HomeValleyConstruction.TryFindPlannedCell(s, FgjM3Common.Step(q, d), out _, out _));
                        }) && FgjM3Common.Clear(FgjM3Common.Ground(p), out _) && FgjM3Common.Clear(new Vector2(p.X + 9, p.Y), out _);
                        if (ok)
                        {
                            start = p;
                        }
                    }
                }
                if (start == null)
                {
                    return StepOutcome.Fail("画面里找不到能铺 10 格传送带的空地");
                }
                FgjM3Common.SetCell(c, "r5a", start.Value);
                c.SetInt("r5undo0", PlanHistory.UndoSteps(s));
                FgjM3Common.Mark(c, "found");
            }
            GridCell a0 = FgjM3Common.GetCell(c, "r5a");
            if (!FgjM3Common.Once(c, "drag", () => FgjM3Common.TryDrag(FgjM3Common.Ground(a0), new Vector2(a0.X + 9, a0.Y))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "drag") < 800)
            {
                return StepOutcome.Wait;
            }
            int planned = Enumerable.Range(0, 10).Count(i => HomeValleyConstruction.TryFindPlannedCell(s, new GridCell(a0.X + i, a0.Y), out _, out _));
            return planned == 10 && PlanHistory.PeekUndo(s) == PlanStepKind.Belts
                ? StepOutcome.Done($"拖出 10 格传送带虚影（{FgjM3Common.Cell(a0)} 起朝东），撤销栈顶是“{PlanHistory.StepName(PlanStepKind.Belts)}”（共 {PlanHistory.UndoSteps(s)} 步）")
                : StepOutcome.Retry($"虚影 {planned}/10、撤销栈顶 {PlanHistory.PeekUndo(s)}");
        }

        private static IEnumerable<GridCell> R5Cells(JourneyContext c)
        {
            GridCell a0 = FgjM3Common.GetCell(c, "r5a");
            return Enumerable.Range(0, 10).Select(i => new GridCell(a0.X + i, a0.Y));
        }

        private static StepOutcome TickR5Midway(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<GridCell> cells = R5Cells(c).ToList();
            int planned = cells.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            int built = cells.Count(p => FgjM3Common.BeltInfo(p, out _) && !HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            if (built >= 2 && planned >= 2)
            {
                return StepOutcome.Done($"施工到一半：{built} 格建成、{planned} 格还是虚影");
            }
            return built == 10 ? StepOutcome.Fail("还没来得及存档就全部建成了") : StepOutcome.Wait;
        }

        private static string Digest(CampaignState s, JourneyContext c)
        {
            var sb = new StringBuilder(FgjM3Common.StateDigest(s));
            IEnumerable<WorkOrderRecord> orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .Where(o => o != null && o.Kind == WorkOrderKind.Build && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed)
                .OrderBy(o => o.WorkOrderId, StringComparer.Ordinal);
            sb.Append("｜施工单=").Append(string.Join(",", orders.Select(o => $"{o.TargetId}:{o.State}:{o.Progress.ToString("0.###", CultureInfo.InvariantCulture)}:{o.Leg}")));
            bool live = ReferenceEquals(s, CampaignSession.Current);
            IEnumerable<MachineRecord> machines = live ? MachineRegistry.AllRecords : (s.MachineRecords ?? Array.Empty<MachineRecord>());
            sb.Append("｜货舱=").Append(machines.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Sum(HomeValleyConstruction.CargoScrap));
            sb.Append("｜段=").Append(string.Join(string.Empty, R5Cells(c).Select(p => HomeValleyConstruction.TryFindPlannedCell(s, p, out _, out _) ? "虚" : "成")));
            return sb.ToString();
        }

        private static StepOutcome TickR5Pause(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            c.Set("pre", Digest(St, c));
            c.Set("preK", FgjM3Common.KernelDigest(R5Cells(c).Concat(R1Path(c)), new[] { Line(c)[0] }, new[] { Line(c)[FgjM3Common.PipeCells + 1] }));
            c.Set("preStable", FgjM3Common.StableDigest(St));
            c.SetLong("preTicks", GameClock.Ticks);
            c.Set("preOrders", OrdersDigest(St));
            c.Set("preSeg", SegDigest(St, c));
            return StepOutcome.Done($"暂停菜单打开；存档前：{c.Get("pre")}；{c.Get("preK")}");
        }

        private static StepOutcome TickR5Disk(JourneyContext c)
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
            string disk = Digest(onDisk.State, c);
            return disk == c.Get("pre")
                ? StepOutcome.Done($"回到主菜单；存档里 {disk}，与存档那一刻逐项一致")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {disk}");
        }

        private static StepOutcome TickR5Loaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                return StepOutcome.Wait;
            }
            // 读档后第一帧就核对（战略暂停不进存档，读档后世界立刻开跑）。
            long dTicks = GameClock.Ticks - c.GetLong("preTicks");
            if (dTicks == 0)
            {
                string live = Digest(St, c);
                string liveK = FgjM3Common.KernelDigest(R5Cells(c).Concat(R1Path(c)), new[] { Line(c)[0] }, new[] { Line(c)[FgjM3Common.PipeCells + 1] });
                return live == c.Get("pre") && liveK == c.Get("preK")
                    ? StepOutcome.Done($"读档后第一帧（还没走步）：{live}；{liveK}（与存档前逐项一致）")
                    : StepOutcome.Fail($"读档后不一致：\n      存档前 {c.Get("pre")}；{c.Get("preK")}\n      读档后 {live}；{liveK}");
            }
            // 已经走了几步：静态部分逐项相等；施工单集合相同、进度不倒退；存档前建成的格仍建成；泵按步数多抽。
            string stable = FgjM3Common.StableDigest(St);
            string orders = OrdersDigest(St);
            bool sameOrders = SameOrdersNoRegression(c.Get("preOrders"), orders);
            string seg = SegDigest(St, c);
            string preSeg = c.Get("preSeg");
            bool builtKept = seg.Length == preSeg.Length && Enumerable.Range(0, seg.Length).All(i => preSeg[i] != '成' || seg[i] == '成');
            bool ok = dTicks > 0 && dTicks <= GameClock.StepHz * 5 && stable == c.Get("preStable") && sameOrders && builtKept;
            return ok
                ? StepOutcome.Done($"读档后第一帧（已走 {dTicks} 步）：建筑与撤销栈一致（{stable.Split('｜').Last()}）；施工单与存档时相同、进度不倒退（{orders}）；这段传送带 {preSeg} → {seg}（建成的仍建成）")
                : StepOutcome.Fail($"读档后不一致（已走 {dTicks} 步）：建筑与撤销栈 {stable}（存档前 {c.Get("preStable")}）；施工单 {orders}（存档前 {c.Get("preOrders")}）；传送带 {seg}（存档前 {preSeg}）");
        }

        private static string OrdersDigest(CampaignState s) =>
            string.Join(",", (s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .Where(o => o != null && o.Kind == WorkOrderKind.Build && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed)
                .OrderBy(o => o.WorkOrderId, StringComparer.Ordinal)
                .Select(o => $"{o.TargetId}:{o.Progress.ToString("0.###", CultureInfo.InvariantCulture)}"));

        /// <summary>施工单集合相同（读档后已完工的允许消失），每张单的进度不小于存档时。</summary>
        private static bool SameOrdersNoRegression(string pre, string now)
        {
            Dictionary<string, float> Parse(string x) => (x ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split(':')).Where(a => a.Length >= 2)
                .ToDictionary(a => string.Join(":", a.Take(a.Length - 1)), a => float.Parse(a[a.Length - 1], CultureInfo.InvariantCulture));
            Dictionary<string, float> a0 = Parse(pre);
            Dictionary<string, float> a1 = Parse(now);
            return a1.Keys.All(a0.ContainsKey) && a1.All(kv => kv.Value + 1e-4f >= a0[kv.Key]);
        }

        private static string SegDigest(CampaignState s, JourneyContext c) =>
            string.Join(string.Empty, R5Cells(c).Select(p => HomeValleyConstruction.TryFindPlannedCell(s, p, out _, out _) ? "虚" : "成"));

        private static StepOutcome TickR5Finished(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!Mathf.Approximately(GameClock.Speed, 3f) && c.GetInt(FgjM3Common.SK(c, "sp")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "sp"), 1);
                JourneyInput.PressAction(GameActionId.SpeedTriple);
            }
            return FgjM3Common.AllBuilt(R5Cells(c), null, out _)
                ? StepOutcome.Done($"读档后施工接着做完：10 格全部建成（{c.StepElapsed:F0} 秒）")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickR5Undone(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            int left = R5Cells(c).Count(p => FgjM3Common.BeltInfo(p, out _) || HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
            string status = Status;
            return left == 0 && status.Contains(GameText.Format("plan.undo.done", PlanHistory.StepName(PlanStepKind.Belts)))
                ? StepOutcome.Done($"读档后按 Ctrl+Z：撤销存档前最后一步（{PlanHistory.StepName(PlanStepKind.Belts)}）——10 格建成的传送带立即拆掉、全额返还（“{OneLine(status)}”；撤销栈剩 {PlanHistory.UndoSteps(St)} 步）")
                : StepOutcome.Retry($"撤销后还剩 {left} 格、状态行“{status}”");
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次");
            c.Log(FgjM3Common.FrameReport("施工 / 倍速 / 搬迁段"));
            FgjM3Common.ResetSampling();
            LayoutLibrary.DirectoryOverrideForTests = null;
            LayoutLibrary.Reload();
            JourneyCommon.Cleanup(c);
        }
    }
}
