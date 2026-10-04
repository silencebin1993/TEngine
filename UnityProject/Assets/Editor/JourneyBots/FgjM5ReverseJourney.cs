using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
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
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG5-E2E-01：M5 反向旅程 FGJ-M5R（00 契约 IC-REQ-022；FG05 第 5 节负向与边界矩阵）。主菜单“新建”出发（固定测试种子 1，与 FGJ-M5 不同：B25）、全程正式输入。
    /// 六类“出错 → 玩家看得懂 → 恢复”，都落在 M5 的研发系统上：
    /// 1. 路径失败：研究“监听站”缺关键材料、“实验室 T3”缺前置——点节点被拒、面板写原因与出处；补上关键材料后排进队列。
    /// 2. 资源不足：仓库没有芯片基板时点“正式熔合…”——被拒、写缺什么还差几件，一样东西都不扣；补上芯片基板后入队。
    /// 3. 目标死亡：正式熔合进行中电路合成台被毁（伤害夹具，家园还没有突袭）——事务回滚：两枚父固件的预留释放、芯片基板与技术数据退回、通知“熔合中止”；
    ///    通用面板“重建”、机器施工把合成台修回来，再熔合一次成功（不复制、不丢失）。
    /// 4. 断链：两座发电机都禁用——仿真实验室缺电、研究暂停（通知“研究暂停”、进度保留在节点上）；重新启用 → 研究接着推进。
    /// 5. 暂停与倍速：暂停时世界步、实验室周期、研究进度都不动；0.5x 与 3x 按游戏时间实验室同速推进（按真实时间约 6 倍）；移出队列进度留在节点上，重新排队接着做。
    /// 6. 存读档：研究进行中保存、回主菜单、读取——研究 / 熔合 / 技术数据收支逐项一致，读档后研究接着推进。
    /// </summary>
    public static class FgjM5ReverseJourney
    {
        public const string Id = "FGJ-M5R";

        public const int TestSeed = 1;

        private static CampaignState St => CampaignSession.Current;

        private static readonly string[] Batch = { "lab", "synth" };
        private const string FwPartner = "fw_nitrogen";

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { FgjM2Common.ResetSampling(); FgjM3Common.ResetSampling(); FgjM2Common.IsolateCodex(c); })),
                S("menu_new", "主菜单点“新建”（测试种子 1）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("start_tech", "新档开局技术数据够研究开放前开局可造内容（DEBT-FG5RND01-08，第二个种子）", 10, null, FgjM5Journey.TickStartTech),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("salv_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("salvage", "右键点开局残骸（拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完", 180, null, FgjM2Common.TickSalvageDone),
                S("salv2_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完", 180, null, FgjM3Common.TickWreck2Done),

                // ── 1. 路径失败（研究的路走不通）──
                S("r1_open", "按研发树键（默认 K）打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("r1_key", "点“信号”分支、左键点“信号 · 监听站”：排进队列但写“⏸ 等关键材料”，详情写缺监听阵列核、从哪里来（主线首领）；研究点不会投给它", 15, null, TickKeyWaiting, retries: 1),
                S("r1_prereq", "点“工业”分支、左键点“工业 · 实验室 T3”：被拒——前置“实验室 T2”还没完成（写明可以先把前置排进队列）", 15, null,
                    c => M.TickQueueNode(c, M.NodeLabT3, GameText.Get("research.reason.prereq").Split('：')[0]), retries: 1),
                S("r1_pre_q", "按提示先点“工业 · 实验室 T2”（前置）：排进队列", 15, null, c => M.TickQueueNode(c, "industry.lab_t2"), retries: 2),
                S("r1_t3_q", "再点“工业 · 实验室 T3”：前置已在队列里，这次排进队列（排在前置后面）", 15, null, c => M.TickQueueNode(c, M.NodeLabT3), retries: 2),
                S("r1_fixture", "进度夹具：监听阵列核放进核心保管库（首领掉落在 FG9-FAC-01；DEBT-FG5RND05-09）", 10, M.ApplyKeyFixture, M.TickKeyFixture),
                S("r1_ok", "监听阵列核到了：“信号 · 监听站”不再等关键材料，回到队列正常排队（关键材料只作门槛、不消耗）", 15, null, TickKeyCleared),
                S("r1_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),

                // ── 熔合要的两枚父固件：先复原液氮、刻印过载与液氮（刻印要装配站运转，在禁用装配站之前做）──
                S("ana_open", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("ana_n2", "“数据复原”下拉选液氮，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FwPartner), c => FgjM2Common.TickConfirmShown(c, FwPartner), retries: 1),
                S("ana_n2_ok", "确认框点“复原”：液氮解锁", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, FwPartner)),
                S("ana_close", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
                S("sc_open", "按信号核键打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print_ov", "刻印下拉选过载，点“刻印”", 60, null, c => FgjM5Journey.TickPrint(c, FirmwareCatalog.FwOverloadId), retries: 1),
                S("sc_print_n2", "刻印下拉选液氮，点“刻印”", 60, null, c => FgjM5Journey.TickPrint(c, FwPartner), retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                // ── 省电：这一局不造机器，先把装配站禁用（废料只够实验室、合成台与之后的重建，不够再造发电机 2）──
                S("asm_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("asm_panel", "左键点装配站：通用面板打开", 40, null, c => P.TickOpenPanel(c, HomeValleyLayout.BuildingTypeAssemblyStation, AsmPivot()), retries: 2),
                S("asm_off", "点“禁用”：装配站停机、不耗电（玩家暂时不造机器）", 15, null, c => TickToggle(c, HomeValleyLayout.BuildingTypeAssemblyStation, AsmPivot(), true), retries: 1),
                S("asm_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
                S("asm_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),

                // ── 研发建筑：仿真实验室与电路合成台（按种子地形现找；电不够加发电机 2）──
                S("plan", "看地形：在核心配电范围里规划仿真实验室、电路合成台，电不够就加发电机 2", 30, null, TickPlan),
                S("build_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("place", "放发电机 2（电不够时）、仿真实验室、电路合成台", 200, null, c => P.TickActions(c, "r5", 40), retries: 2),
                S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("built", "机器取料施工：全部建成、接上电；实验室开始把技术数据换成研究点", 900, null, c => M.TickBuilt(c, Batch, Array.Empty<string>(), true, "实验室与合成台")),

                // ── 2. 资源不足（正式熔合缺芯片基板）──
                S("r2_open", "建造模式里左键点电路合成台，通用面板上点“熔合…”", 40, null,
                    c => M.TickSubPanel(c, "synth", "PrFusion", () => FusionPanelUIToolkit.IsOpen && FusionPanelUIToolkit.Instance != null && FusionPanelUIToolkit.Instance.PanelVisible, "合成台面板打开"), retries: 2),
                S("r2_pick", "固件 A 选过载、固件 B 选液氮", 15, null, TickPickPair, retries: 1),
                S("r2_sim", "点“模拟熔合”：有配方（热震尖峰），只花模拟费", 10, null, TickSimHit, retries: 1),
                S("r2_short", "点“正式熔合…”：仓库没有芯片基板——被拒，写明缺芯片基板、还差几件，两枚固件不预留、技术数据不扣", 10, null, TickFormalShort, retries: 1),
                S("r2_fixture", "进度夹具：芯片基板放进仓库（DEBT-FG5E2E01-02）", 10, M.ApplySubstrateFixture, M.TickSubstrateFixture),
                S("r2_formal", "再点“正式熔合…”，确认框点“确认”：入队，两枚父固件被预留、芯片基板 ×2 与技术数据记在任务上", 20, null, TickFormalQueued, retries: 1),

                // ── 3. 目标死亡（熔合中合成台被毁 → 回滚；重建后再熔合）──
                S("r3_destroy", "伤害夹具：熔合进行中电路合成台被毁（家园还没有突袭；伤害走建筑的唯一伤害入口）", 15, ApplyDestroyFixture, TickRolledBack),
                S("r3_close0", "按 Esc 关闭还开着的合成台面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !FusionPanelUIToolkit.IsOpen, "合成台面板"), retries: 1),
                S("r3_panel", "建造模式里左键点被毁的合成台：通用面板写“已摧毁”，有“重建”按钮", 40, null, c => P.TickOpenPanel(c, M.Synth, P.P(c, "synth")), retries: 2),
                S("r3_rebuild", "点“重建”：生成重建施工（按造价收费，机器上门）", 15, null, TickRebuildOrdered, retries: 1),
                S("r3_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
                S("r3_built", "机器施工：合成台重新运转", 600, null, TickSynthBack),
                S("r3_open", "左键点合成台，通用面板上点“熔合…”", 40, null,
                    c => M.TickSubPanel(c, "synth", "PrFusion", () => FusionPanelUIToolkit.IsOpen && FusionPanelUIToolkit.Instance != null && FusionPanelUIToolkit.Instance.PanelVisible, "合成台面板打开"), retries: 2),
                S("r3_pick", "固件 A 选过载、固件 B 选液氮（这一对已经模拟出配方）", 15, null, TickPickPair, retries: 1),
                S("r3_formal", "点“正式熔合…”，确认框点“确认”：入队", 20, null, TickFormalQueued, retries: 1),
                S("r3_done", "熔合完成：两枚父固件消耗、产出一枚混合固件；中止那一次没有复制也没有丢失任何东西", 300, null, TickFusedAfterRebuild),
                S("r3_close2", "按 Esc 关闭合成台面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !FusionPanelUIToolkit.IsOpen, "合成台面板"), retries: 1),

                // ── 4. 断链（发电机都禁用 → 实验室缺电、研究暂停 → 重新启用恢复）──
                S("r4_gen1", "建造模式里左键点发电机：通用面板打开", 40, null, c => P.TickOpenPanel(c, HomeValleyLayout.BuildingTypeGenerator, GenPivot(c, 0)), retries: 2),
                S("r4_off1", "点“禁用”", 15, c => c.SetInt("stall.n0", StallNotes()), c => TickToggle(c, HomeValleyLayout.BuildingTypeGenerator, GenPivot(c, 0), true), retries: 1),
                S("r4_close1", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
                S("r4_gen2", "左键点发电机 2：通用面板打开", 40, null, c => GenCount(c) < 2 ? StepOutcome.Done("没有发电机 2") : P.TickOpenPanel(c, HomeValleyLayout.BuildingTypeGenerator2, GenPivot(c, 1)), retries: 2),
                S("r4_off2", "点“禁用”", 15, null, c => GenCount(c) < 2 ? StepOutcome.Done("没有发电机 2") : TickToggle(c, HomeValleyLayout.BuildingTypeGenerator2, GenPivot(c, 1), true), retries: 1),
                S("r4_close2", "点“关闭”关闭通用面板", 10, null, c => ProductionPanelUIToolkit.IsOpen ? P.TickClosePanel(c) : StepOutcome.Done("通用面板没开"), retries: 1),
                S("r4_stalled", "全家园断电：仿真实验室缺电停工、研究暂停（通知“研究暂停”，研发树状态写原因），节点上已投入的进度保留", 120, null, TickStalled),
                S("r4_gen1b", "左键点发电机", 40, null, c => P.TickOpenPanel(c, HomeValleyLayout.BuildingTypeGenerator, GenPivot(c, 0)), retries: 2),
                S("r4_on1", "点“启用”", 15, null, c => TickToggle(c, HomeValleyLayout.BuildingTypeGenerator, GenPivot(c, 0), false), retries: 1),
                S("r4_close3", "点“关闭”", 10, null, P.TickClosePanel, retries: 1),
                S("r4_gen2b", "左键点发电机 2", 40, null, c => GenCount(c) < 2 ? StepOutcome.Done("没有发电机 2") : P.TickOpenPanel(c, HomeValleyLayout.BuildingTypeGenerator2, GenPivot(c, 1)), retries: 2),
                S("r4_on2", "点“启用”", 15, null, c => GenCount(c) < 2 ? StepOutcome.Done("没有发电机 2") : TickToggle(c, HomeValleyLayout.BuildingTypeGenerator2, GenPivot(c, 1), false), retries: 1),
                S("r4_close4", "点“关闭”", 10, null, c => ProductionPanelUIToolkit.IsOpen ? P.TickClosePanel(c) : StepOutcome.Done("通用面板没开"), retries: 1),
                S("r4_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r4_back", "来电：实验室恢复转换、研究从保留的进度接着推进", 120, null, TickResumed),

                // ── 5. 暂停与倍速；移出队列进度保留 ──
                S("r5_pause", "按暂停键（战略暂停）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("r5_frozen", "暂停 1.5 真实秒：世界步、实验室周期、研究进度都不动", 15, null, TickFrozen),
                S("r5_resume", "按暂停键恢复", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done("恢复") : StepOutcome.Retry("还在暂停"), retries: 1),
                S("r5_half", "按 0.5 倍速键：实验室按游戏时间同速转换（真实时间变慢）", 120, c => JourneyInput.PressAction(GameActionId.SpeedHalf), c => TickRate(c, 0.5f, "half"), retries: 1),
                S("r5_triple", "按 3 倍速键：实验室按游戏时间同速转换（真实时间变快）", 60, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickRate(c, 3f, "triple"), retries: 1),
                S("r5_rt_open", "按研发树键打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("r5_remove", "队列里研究中的那一项点“移出”：进度留在节点上（面板写“进度 n / m 保留”）", 15, null, TickRemoveKeepsProgress, retries: 1),
                S("r5_requeue", "再点那个节点：重新排进队列，从保留的进度接着做", 15, null, c => M.TickQueueNode(c, c.Get("rm.node")), retries: 2),
                S("r5_top", "重新排进来的节点排在队尾：在队列里点它那一行的“上移”，挪到第一位（先把它做完）", 20, null, TickMoveTop, retries: 1),
                S("r5_rt_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),
                S("r5_continue", "研究从保留的进度接着推进（不从 0 开始）", 120, null, TickContinued),

                // ── 6. 存读档 ──
                S("r6_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("r6_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("r6_menu", "回到主菜单：存档里研究 / 熔合 / 技术数据收支与存档那一刻逐项一致", 60, null, TickMenuAfterSave),
                S("r6_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("r6_loaded", "读档进入家园：第一帧逐项一致；读档后研究接着推进", 120, null, TickLoaded),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "M5 反向：研究缺关键材料 / 缺前置被拒再补上、正式熔合缺芯片基板被拒再补上、熔合中合成台被毁回滚再重建熔合、断电研究暂停再恢复、暂停与倍速 / 移出队列进度保留、研究中存读档",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2400,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "built", "r4_back" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        // ── 关键材料 ──────────────────────────────────────────────────────────────────

        /// <summary>缺关键材料的节点可以排进队列，但写“⏸ 等关键材料”、详情写缺什么与从哪里来；研究点跳过它（ResearchNodeState.WaitingKey）。</summary>
        private static StepOutcome TickKeyWaiting(JourneyContext c)
        {
            StepOutcome o = M.TickQueueNode(c, M.NodePost);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            ResearchTreePanelUIToolkit p = M.Tree();
            CampaignState s = St;
            ResearchNodeDef def = ResearchCatalog.Find(M.NodePost);
            ResearchNodeState st = ResearchService.StateOf(s, def);
            string key = ItemCatalog.NameOf(M.KeyItem);
            string stateText = p.NodeStateText(M.NodePost);
            string waiting = GameText.Get("research.state.waiting_key").Split('·')[0].Trim();
            bool detail = p.DetailNodeId == M.NodePost && p.DetailBody.Contains(key);
            return st == ResearchNodeState.WaitingKey && stateText.StartsWith(waiting, StringComparison.Ordinal) && detail
                ? StepOutcome.Done($"“{M.NodeName(M.NodePost)}”进了队列但写“{stateText}”；详情写“{FirstLineWith(p.DetailBody, key)}”；研究点不会投给它（{ResearchService.KeyShortfall(s, def)}）")
                : StepOutcome.Fail($"缺关键材料时：节点状态 {st}“{stateText}”（应以“{waiting}”开头）、详情写了{key} {detail}（详情节点 {p.DetailNodeId}）");
        }

        private static string FirstLineWith(string text, string word) => text.Split('\n').FirstOrDefault(l => l.Contains(word))?.Trim() ?? text;

        private static StepOutcome TickKeyCleared(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            ResearchNodeState st = ResearchService.StateOf(s, ResearchCatalog.Find(M.NodePost));
            string stateText = M.Tree()?.NodeStateText(M.NodePost) ?? string.Empty;
            return st != ResearchNodeState.WaitingKey && ResearchService.QueueIndex(s, M.NodePost) >= 0
                ? StepOutcome.Done($"监听阵列核放进了核心保管库：“{M.NodeName(M.NodePost)}”变成 {st}“{stateText}”，队列 [{string.Join("、", ResearchService.Queue(s).Select(M.NodeName))}]")
                : StepOutcome.Fail($"关键材料到了节点还是 {st}“{stateText}”");
        }

        // ── 规划 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            var specs = new[]
            {
                new M.SiteSpec { Key = "lab", Type = M.Lab, MinR = 6 },
                new M.SiteSpec { Key = "synth", Type = M.Synth, MinR = 6 },
            };
            // 本旅程不修信号塔与其它开局受损的建筑（不出征）、装配站已禁用：没修 / 禁用的建筑不在电网里，规划发电量时扣掉它们；发电量按刚好够算（不留 5% 余量）——
            // 省下发电机 2 的废料留给“重建合成台”。
            int towerOff = -(s.BuildingRecords ?? Array.Empty<BuildingRecord>())
                .Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.ConstructionState != BuildingConstructionState.Operational)
                .Sum(b => P.BuildingDemand(b.BuildingTypeId));
            P.FactoryPlan plan = M.PlanSites(s, withRecycler: false, withBurner: false, specs, extraDemand: towerOff, out string why, powerMargin: 1.0);
            if (plan == null)
            {
                return StepOutcome.Fail("规划不出来：" + why);
            }
            M.SavePlan(c, plan);
            var acts = new List<string>();
            foreach (GridCell g in plan.Generators)
            {
                acts.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {M.Cell(g)}");
            }
            acts.Add(P.ActB(plan.B("lab")));
            acts.Add(P.ActB(plan.B("synth")));
            P.SetActions(c, "r5", acts);
            return StepOutcome.Done($"规划：{M.Describe(plan, HomeGridService.CorePivot(s))}");
        }

        // ── 解析台 / 信号核（核对不比废料：施工同时在取料）──────────────────────────────

        private static StepOutcome TickRestored(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int cost = FirmwareRestoreService.CostOf(fwId);
            bool ok = !UiConfirmDialog.IsOpen && MechanicalContentUnlock.IsUnlocked(s, fwId) && SignalCoreService.PrintableFirmware(s).Contains(fwId);
            return ok ? StepOutcome.Done($"复原“{M.FwName(fwId)}”：解锁、可刻印（复原费 {cost} 技术数据）")
                : (c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"复原“{M.FwName(fwId)}”后没有解锁"));
        }

        // ── 熔合 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPickPair(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            if (p == null || !FusionPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("合成台面板没开");
            }
            if (p.SelectedA == FirmwareCatalog.FwOverloadId && p.SelectedB == FwPartner)
            {
                return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done($"固件 A = {M.FwName(p.SelectedA)}、B = {M.FwName(p.SelectedB)}");
            }
            if (M.NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 600)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "at"), M.NowMs());
            bool slotA = p.SelectedA != FirmwareCatalog.FwOverloadId;
            if (!M.PickParent(slotA, slotA ? FirmwareCatalog.FwOverloadId : FwPartner, out string why))
            {
                return why == "滚动中" ? StepOutcome.Wait : StepOutcome.Retry(why);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickSimHit(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            if (!FgjM3Common.Done(c, "sim"))
            {
                c.SetInt(FgjM3Common.SK(c, "s0"), FusionService.StateOf(St)?.Simulations ?? 0);
                if (!JourneyInput.ClickElement(p.SimulateButton))
                {
                    return M.UiRetry("点不到“模拟熔合”：");
                }
                FgjM3Common.Mark(c, "sim");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            bool hit = FusionCatalog.TryGetByPair(p.SelectedA, p.SelectedB, out FusionRecipeDef def) && (FusionService.StateOf(St)?.Simulations ?? 0) == c.GetInt(FgjM3Common.SK(c, "s0")) + 1;
            c.Set("recipe", def?.Id ?? string.Empty);
            return hit && p.FormalButton.enabledSelf
                ? StepOutcome.Done($"模拟熔合：“{p.ResultText.Replace("\n", " / ")}”（{GameText.Get(def.NameKey)}）")
                : StepOutcome.Fail($"模拟没有出配方：“{p.ResultText}”");
        }

        private static StepOutcome TickFormalShort(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            string synthId = P.Bld(c, "synth")?.BuildingId;
            if (!FgjM3Common.Done(c, "formal"))
            {
                c.SetInt(FgjM3Common.SK(c, "jobs0"), FusionService.JobsOf(St, synthId).Count());
                c.SetInt(FgjM3Common.SK(c, "res0"), St.PrimitiveChips?.Count(x => x != null && !string.IsNullOrEmpty(x.ReservedByTransactionId)) ?? 0);
                c.SetInt(FgjM3Common.SK(c, "sub0"), HomeInventory.Stock(St, M.Substrate));
                if (!JourneyInput.ClickElement(p.FormalButton))
                {
                    return M.UiRetry("点不到“正式熔合…”：");
                }
                FgjM3Common.Mark(c, "formal");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            if (UiConfirmDialog.IsOpen)
            {
                return StepOutcome.Fail("仓库没有芯片基板时也弹出了确认框（应当直接被拒）");
            }
            string msg = p.MessageText;
            bool nothing = FusionService.JobsOf(St, synthId).Count() == c.GetInt(FgjM3Common.SK(c, "jobs0"))
                           && (St.PrimitiveChips?.Count(x => x != null && !string.IsNullOrEmpty(x.ReservedByTransactionId)) ?? 0) == c.GetInt(FgjM3Common.SK(c, "res0"))
                           && HomeInventory.Stock(St, M.Substrate) == 0;
            string sub = ItemCatalog.NameOf(M.Substrate);
            return nothing && msg.Contains(sub)
                ? StepOutcome.Done($"点“正式熔合…”被拒：“{msg}”；队列、固件预留、仓库都没动")
                : StepOutcome.Fail($"缺芯片基板时：没入队 {nothing}、消息“{msg}”（应写明缺{sub}）");
        }

        private static StepOutcome TickFormalQueued(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            string synthId = P.Bld(c, "synth")?.BuildingId;
            if (!FgjM3Common.Done(c, "formal"))
            {
                c.SetInt(FgjM3Common.SK(c, "jobs0"), FusionService.JobsOf(St, synthId).Count());
                if (!JourneyInput.ClickElement(p.FormalButton))
                {
                    return M.UiRetry("点不到“正式熔合…”：");
                }
                FgjM3Common.Mark(c, "formal");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "ok"))
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                if (!UiConfirmDialog.IsOpen)
                {
                    return StepOutcome.Retry("点“正式熔合…”后没有弹出确认框：" + p.MessageText);
                }
                if (!JourneyInput.ClickUitk(M.OverlayHost, "ConfirmOk"))
                {
                    return M.UiRetry("确认框点“确认”失败：");
                }
                FgjM3Common.Mark(c, "ok");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            FusionJobRecord job = FusionService.JobsOf(St, synthId).OrderByDescending(j => j.Serial).FirstOrDefault(); // 最新的一项（JobsOf 把进行中的排前面）
            if (job == null || FusionService.JobsOf(St, synthId).Count() <= c.GetInt(FgjM3Common.SK(c, "jobs0")))
            {
                return StepOutcome.Retry("确认后队列里没有这项熔合：" + p.MessageText);
            }
            c.Set("job", job.JobId);
            c.SetInt("job.tech", job.Tech);
            c.SetInt("job.sub", job.Substrate);
            c.Set("def.a", M.DefOf(job.PartA));
            c.Set("def.b", M.DefOf(job.PartB));
            c.SetInt("chips.a", M.ChipCount(c.Get("def.a")));
            c.SetInt("chips.b", M.ChipCount(c.Get("def.b")));
            c.SetInt("tech.flow", (int)TechDataFlow.ExpenseOf(St, TechDataFlow.Fusion));
            int reserved = St.PrimitiveChips?.Count(x => x != null && x.ReservedByTransactionId == job.JobId) ?? 0;
            return reserved == 2
                ? StepOutcome.Done($"入队：{FusionService.StateText(job)}（{job.Duration:0.#} 秒）；两枚父固件被这项预留、芯片基板 {job.Substrate} 件与技术数据 {job.Tech} 记在任务上")
                : StepOutcome.Fail($"入队后预留了 {reserved} 枚");
        }

        /// <summary>伤害夹具（DEBT-FG5E2E01-05）：家园还没有正式突袭（FG6-DEF-04 / 08），熔合进行中的电路合成台用建筑的唯一伤害入口打到 0；之后的回滚、退回、通知、重建都是正式流程。</summary>
        private static void ApplyDestroyFixture(JourneyContext c)
        {
            CampaignState s = St;
            c.SetInt("sub.total0", SubstrateTotal());
            c.SetInt("tech0", s.TechData);
            c.SetInt("notes0", NotificationCenter.History.Count);
            BuildingOps.ApplyDamage(s, P.Bld(c, "synth")?.BuildingId, 1e6f);
        }

        private static int SubstrateTotal()
        {
            ItemDistribution.Invalidate();
            return (int)ItemDistribution.Get(St, ItemCatalog.Find(M.Substrate)).Total;
        }

        private static StepOutcome TickRolledBack(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FusionJobRecord job = FusionService.StateOf(St)?.Jobs?.FirstOrDefault(j => j != null && j.JobId == c.Get("job"));
            BuildingRecord b = P.Bld(c, "synth");
            if (job == null || job.State != FusionJobState.RolledBack)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail($"合成台被毁后熔合没回滚（{job?.State}，合成台 {b?.ConstructionState}）");
            }
            int reserved = St.PrimitiveChips?.Count(x => x != null && x.ReservedByTransactionId == job.JobId) ?? 0;
            bool chips = M.ChipCount(c.Get("def.a")) == c.GetInt("chips.a") && M.ChipCount(c.Get("def.b")) == c.GetInt("chips.b") && c.GetInt("chips.a") > 0 && c.GetInt("chips.b") > 0
                         && reserved == 0 && PrimitiveInventory.Find(St, job.PartA) != null && PrimitiveInventory.Find(St, job.PartB) != null;
            bool sub = SubstrateTotal() == c.GetInt("sub.total0");
            bool flow = TechDataFlow.ExpenseOf(St, TechDataFlow.Fusion) == c.GetInt("tech.flow") - c.GetInt("job.tech");
            bool note = NotificationCenter.History.Skip(c.GetInt("notes0")).Any(e => e.Type?.Id != null && e.Type.Id.StartsWith("fusion", StringComparison.Ordinal));
            bool destroyed = b != null && (b.ConstructionState == BuildingConstructionState.Damaged || b.ConstructionState == BuildingConstructionState.Destroyed);
            return chips && sub && flow && note && destroyed
                ? StepOutcome.Done($"合成台被毁（{b.ConstructionState}）：熔合回滚（{FusionService.StateText(job)}，原因 {job.Reason}）；两枚父固件的预留释放、仍在固件库；芯片基板总量（仓库 / 地面 / 熔合在办）不变 {SubstrateTotal()} 件；" +
                                   $"技术数据退回（统计“正式熔合”支出扣回到 {TechDataFlow.ExpenseOf(St, TechDataFlow.Fusion)}）；通知“熔合中止”")
                : StepOutcome.Fail($"回滚不完整：固件 {chips}、芯片基板总量 {sub}（{c.GetInt("sub.total0")} → {SubstrateTotal()}）、技术数据退回 {flow}、通知 {note}、合成台被毁 {destroyed}");
        }

        private static StepOutcome TickRebuildOrdered(JourneyContext c)
        {
            BuildingRecord b = P.Bld(c, "synth");
            if (b == null)
            {
                return StepOutcome.Fail("合成台不见了");
            }
            WorkOrderRecord order = HomeValleyWorkOrders.FindActiveRepair(St, b.BuildingId) ?? HomeValleyWorkOrders.FindActiveBuild(St, b.BuildingId);
            if (order != null)
            {
                return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done($"点“重建”：生成施工单（{order.Kind}，{order.State}）");
            }
            if (!FgjM3Common.Once(c, "rb", () => FgjM3Common.ClickPanelBodyButton(P.PanelHost, "ProductionPanelBody", "BpRepair")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "rb") < 1500 ? StepOutcome.Wait
                : StepOutcome.Retry($"点“重建”后没有施工单（“{JourneyInput.FindUitk<Button>(P.PanelHost, "BpRepair")?.text}”；面板“{ProductionPanelUIToolkit.Instance?.StateText}”；废料 {St.Scrap}；{JourneyInput.LastUiFailure}）");
        }

        private static StepOutcome TickSynthBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord b = P.Bld(c, "synth");
            return b != null && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered
                ? StepOutcome.Done($"合成台重建完成、接上电（废料 {St.Scrap}）")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickFusedAfterRebuild(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FusionJobRecord job = FusionService.StateOf(St)?.Jobs?.FirstOrDefault(j => j != null && j.JobId == c.Get("job"));
            if (job == null)
            {
                return StepOutcome.Fail("熔合任务不见了");
            }
            if (job.State == FusionJobState.RolledBack || job.State == FusionJobState.Cancelled)
            {
                return StepOutcome.Fail($"重建后的熔合 {job.State}：{job.Reason}");
            }
            if (job.State != FusionJobState.Done)
            {
                return StepOutcome.Wait;
            }
            FusionCatalog.TryGet(c.Get("recipe"), out FusionRecipeDef def);
            bool ok = M.ChipCount(c.Get("def.a")) == c.GetInt("chips.a") - 1 && M.ChipCount(c.Get("def.b")) == c.GetInt("chips.b") - 1 && def != null && M.ChipCount(def.MixId) == 1
                      && FusionService.StateOf(St).RolledBack == 1 && FusionService.StateOf(St).Fused == 1;
            return ok
                ? StepOutcome.Done($"重建后的熔合完成：两枚父固件各消耗一枚、产出一枚“{M.FwName(def.MixId)}”（整局：回滚 1 次、完成 1 次，没有多也没有少）")
                : StepOutcome.Fail($"熔合完成后数量不对：父固件 {M.ChipCount(c.Get("def.a"))} / {M.ChipCount(c.Get("def.b"))}（入队时 {c.GetInt("chips.a")} / {c.GetInt("chips.b")}）、混合固件 {M.ChipCount(def?.MixId)}");
        }

        // ── 断电 ──────────────────────────────────────────────────────────────────────

        private static GridCell AsmPivot()
        {
            BuildingRecord a = FgjM3Common.Home(HomeValleyLayout.BuildingTypeAssemblyStation);
            return a != null ? new GridCell(a.GridX, a.GridY) : default;
        }

        private static int GenCount(JourneyContext c) => 1 + FgjM3Common.DecodeCells(c.Get("plan.gens")).Count;

        private static GridCell GenPivot(JourneyContext c, int i)
        {
            if (i == 0)
            {
                BuildingRecord g = FgjM3Common.Home(HomeValleyLayout.BuildingTypeGenerator);
                return g != null ? new GridCell(g.GridX, g.GridY) : default;
            }
            List<GridCell> gens = FgjM3Common.DecodeCells(c.Get("plan.gens"));
            return gens.Count >= i ? gens[i - 1] : default;
        }

        private static StepOutcome TickToggle(JourneyContext c, string type, GridCell pivot, bool disable)
        {
            BuildingRecord b = P.BuildingAtPivot(St, type, pivot);
            if (b == null)
            {
                return StepOutcome.Fail($"{M.Cell(pivot)} 没有{M.Name(type)}");
            }
            if (BuildingOps.IsDisabled(b) == disable)
            {
                return c.StepElapsed < 0.6 ? StepOutcome.Wait : StepOutcome.Done($"{M.Name(type)} {M.Cell(pivot)} 已{(disable ? "禁用" : "启用")}（{P.PowerLine(St)}）");
            }
            if (!FgjM3Common.Once(c, "tg", () => FgjM3Common.ClickPanelBodyButton(P.PanelHost, "ProductionPanelBody", "BpEnable")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tg") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点“{(disable ? "禁用" : "启用")}”没生效：" + JourneyInput.LastUiFailure);
        }

        private static int StallNotes() => NotificationCenter.History.Where(e => e.Type?.Id == "research_stalled").Sum(e => e.Count);

        private static StepOutcome TickStalled(JourneyContext c)
        {
            CampaignState s = St;
            BuildingRecord lab = P.Bld(c, "lab");
            if (c.Get("stall.node").Length == 0)
            {
                c.Set("stall.node", ResearchService.Head(s)?.Id ?? string.Empty);
            }
            string node = c.Get("stall.node");
            LabState st = ResearchService.LabStateOf(s, lab);
            ResearchStatus rs = ResearchService.StatusOf(s);
            int notes = StallNotes();
            if (st != LabState.NoPower || rs != ResearchStatus.NoPower)
            {
                return StepOutcome.Wait;
            }
            int invested = ResearchService.Invested(s, node);
            if (!FgjM3Common.Done(c, "p"))
            {
                c.SetInt("stall.invested", invested);
                c.SetLong("stall.tick", GameClock.Ticks);
                FgjM3Common.Mark(c, "p");
                return StepOutcome.Wait;
            }
            if ((GameClock.Ticks - c.GetLong("stall.tick")) / (double)GameClock.StepHz < 40)
            {
                return StepOutcome.Wait;
            }
            bool kept = invested == c.GetInt("stall.invested");
            string reason = BuildingStatusService.Evaluate(s, lab).Reason;
            return kept && notes > c.GetInt("stall.n0")
                ? StepOutcome.Done($"断电：实验室“{reason}”；研究暂停（“{ResearchService.StatusText(s)}”），通知“研究暂停”；断电 40 游戏秒里“{M.NodeName(node)}”已投入的 {invested} 点不变")
                : StepOutcome.Fail($"断电后：进度保留 {kept}（{c.GetInt("stall.invested")} → {invested}）、研究暂停通知 {notes - c.GetInt("stall.n0")} 条");
        }

        private static StepOutcome TickResumed(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string node = c.Get("stall.node");
            int invested = ResearchService.Invested(s, node);
            bool done = ResearchService.IsCompleted(s, node);
            if (!done && invested <= c.GetInt("stall.invested"))
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"来电：实验室“{BuildingStatusService.Evaluate(s, P.Bld(c, "lab")).Reason}”；“{M.NodeName(node)}”从保留的 {c.GetInt("stall.invested")} 点接着推进（现在 {(done ? "已完成" : invested + " 点")}）");
        }

        // ── 暂停与倍速 ────────────────────────────────────────────────────────────────

        /// <summary>实验室累计推进的世界步：已完成周期数 × 每周期步数 + 本周期已推进的步数（只看这一座实验室）。</summary>
        private static long LabWork(JourneyContext c)
        {
            LabRecord rec = ResearchService.FindLab(St, P.Bld(c, "lab")?.BuildingId);
            return (St.Research.TechConsumed - (rec != null && rec.Loaded ? 1 : 0)) * ResearchService.CycleTicks(GameClock.StepHz) + (rec?.ProgressTicks ?? 0);
        }

        private static StepOutcome TickFrozen(JourneyContext c)
        {
            string Snap() => $"{GameClock.Ticks}/{LabWork(c)}/{St.Research.PointsProduced}/{St.Research.PointsMilli}";
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
                ? StepOutcome.Done($"暂停 {c.StepElapsed:F1} 真实秒：世界步 / 实验室推进 / 研究点都不变（{Snap()}）")
                : StepOutcome.Fail($"暂停中变了：{c.Get(FgjM3Common.SK(c, "snap"))} → {Snap()}");
        }

        private static StepOutcome TickRate(JourneyContext c, float speed, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
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
                c.SetLong(FgjM3Common.SK(c, "w0"), LabWork(c));
                c.SetLong(FgjM3Common.SK(c, "real0"), M.NowMs());
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            long dTicks = GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"));
            if (dTicks < GameClock.StepHz * 20)
            {
                return StepOutcome.Wait;
            }
            double realSec = (M.NowMs() - c.GetLong(FgjM3Common.SK(c, "real0"))) / 1000.0;
            long work = LabWork(c) - c.GetLong(FgjM3Common.SK(c, "w0"));
            double perGameSecond = work / (dTicks / (double)GameClock.StepHz);
            double perRealSecond = work / Math.Max(1e-3, realSec);
            c.Set("rate." + key, perRealSecond.ToString("R", CultureInfo.InvariantCulture));
            string ratio = string.Empty;
            bool ratioOk = true;
            if (key == "triple" && double.TryParse(c.Get("rate.half"), NumberStyles.Float, CultureInfo.InvariantCulture, out double half) && half > 0)
            {
                double r = perRealSecond / half;
                ratioOk = r > 3.0;
                ratio = $"；按真实时间是 0.5x 时的 {r:F1} 倍（理论 6 倍，帧节奏有抖动）";
            }
            // 实验室一直在工作：每游戏秒推进 StepHz 步（缺电 / 缺技术数据会停）。
            return Math.Abs(perGameSecond - GameClock.StepHz) <= GameClock.StepHz * 0.02 && ratioOk
                ? StepOutcome.Done($"{speed}x：{dTicks / (double)GameClock.StepHz:F1} 游戏秒（{realSec:F1} 真实秒）实验室推进 {work} 步 = 每游戏秒 {perGameSecond:F1} 步（满速 {GameClock.StepHz}）{ratio}")
                : StepOutcome.Fail($"{speed}x 时实验室推进不对：每游戏秒 {perGameSecond:F2} 步（满速 {GameClock.StepHz}）{ratio}");
        }

        private static StepOutcome TickRemoveKeepsProgress(JourneyContext c)
        {
            ResearchTreePanelUIToolkit p = M.Tree();
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "rm"))
            {
                string head = ResearchService.Head(s)?.Id;
                int row = -1;
                for (int i = 0; i < p.QueueRowVisibleCount; i++)
                {
                    if (head != null && p.QueueRowNode(i) == head)
                    {
                        row = i;
                    }
                }
                if (row < 0)
                {
                    return StepOutcome.Fail($"研究队列里找不到正在研究的那一项（{head}）");
                }
                c.Set("rm.node", head);
                c.SetInt("rm.invested", ResearchService.Invested(s, head));
                bool? clicked = P.ClickInView(p.QueueRowButton(row, 2));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到“移出”：");
                }
                FgjM3Common.Mark(c, "rm");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string node = c.Get("rm.node");
            int invested = ResearchService.Invested(s, node);
            return ResearchService.QueueIndex(s, node) < 0 && invested == c.GetInt("rm.invested") && invested > 0
                ? StepOutcome.Done($"点“移出”：“{M.NodeName(node)}”离开队列，已投入的 {invested} 点留在节点上（面板“{p.MessageText}”；队列剩 {ResearchService.Queue(s).Count} 项）")
                : StepOutcome.Fail($"移出后：还在队列 {ResearchService.QueueIndex(s, node)}、进度 {c.GetInt("rm.invested")} → {invested}");
        }

        private static StepOutcome TickMoveTop(JourneyContext c)
        {
            ResearchTreePanelUIToolkit p = M.Tree();
            string node = c.Get("rm.node");
            int q = ResearchService.QueueIndex(St, node);
            if (q < 0)
            {
                return StepOutcome.Fail($"“{M.NodeName(node)}”不在队列里");
            }
            if (q == 0)
            {
                return c.StepElapsed < 0.4 ? StepOutcome.Wait : StepOutcome.Done($"“{M.NodeName(node)}”上移到队列第 1 位（队列 [{string.Join("、", ResearchService.Queue(St).Select(M.NodeName))}]；状态“{p.StatusText}”）");
            }
            if (M.NowMs() - c.GetLong(FgjM3Common.SK(c, "up")) < 500)
            {
                return StepOutcome.Wait;
            }
            int row = -1;
            for (int i = 0; i < p.QueueRowVisibleCount; i++)
            {
                if (p.QueueRowNode(i) == node)
                {
                    row = i;
                }
            }
            if (row < 0)
            {
                return StepOutcome.Retry($"队列面板里找不到“{M.NodeName(node)}”那一行");
            }
            bool? clicked = P.ClickInView(p.QueueRowButton(row, 0));
            if (clicked == null)
            {
                return StepOutcome.Wait;
            }
            if (clicked == false)
            {
                return M.UiRetry("点不到“上移”：");
            }
            c.SetLong(FgjM3Common.SK(c, "up"), M.NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickContinued(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string node = c.Get("rm.node");
            int invested = ResearchService.Invested(s, node);
            bool done = ResearchService.IsCompleted(s, node);
            if (!done && invested <= c.GetInt("rm.invested"))
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"“{M.NodeName(node)}”重新排队后从 {c.GetInt("rm.invested")} 点接着做（现在 {(done ? "已完成" : invested + " 点")}，不是从 0 开始）");
        }

        // ── 存读档 ────────────────────────────────────────────────────────────────────

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
            c.Set("pre", M.Digest(St, true));
            c.Set("preStable", M.Digest(St, false));
            c.SetLong("preTicks", GameClock.Ticks);
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前研发域：{c.Get("pre")}");
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
            string got = M.Digest(onDisk.State, true);
            return got == c.Get("pre")
                ? StepOutcome.Done($"回到主菜单；存档里研发域与存档那一刻逐项一致：{got}")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {got}");
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
                string stable = M.Digest(s, false);
                if (stable != c.Get("preStable") || dTicks < 0 || dTicks > GameClock.StepHz * 5)
                {
                    return StepOutcome.Fail($"读档后不一致（已走 {dTicks} 步）：\n      存档前 {c.Get("preStable")}\n      读档后 {stable}");
                }
                c.Set("loadNote", $"读档后第一帧（已走 {dTicks} 步）：研究已完成 / 队列、配方书与线索、熔合任务、技术数据收支逐项一致");
                c.SetLong(FgjM3Common.SK(c, "w0"), LabWork(c));
                FgjM3Common.Mark(c, "cmp");
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            return LabWork(c) > c.GetLong(FgjM3Common.SK(c, "w0")) + GameClock.StepHz * 5
                ? StepOutcome.Done($"{c.Get("loadNote")}；读档后实验室接着转换（又推进 {LabWork(c) - c.GetLong(FgjM3Common.SK(c, "w0"))} 步）")
                : StepOutcome.Wait;
        }

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            c.Log($"方向键平移镜头 {JourneyCommon.PanPresses} 次；面板滚动区先滚滚轮再点 {FgjM3Common.PanelBodyScrolls} 次；换建筑前点“建造 [+]”展开收起的建造目录 {FgjM3Common.CatalogueExpands} 次");
            FgjM2Common.ResetSampling();
            FgjM3Common.ResetSampling();
            FgjM2Common.RestoreCodex();
            JourneyCommon.Cleanup(c);
        }
    }
}
