using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
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
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG6-E2E-01：M6 反向旅程 FGJ-M6R（00 契约 IC-REQ-022 六类，落在家园防御上；种子 1，与 FGJ-M6 不同，B25）。从主菜单“新建”出发、全程正式输入。
    /// 每一类都走“出错 → 看得到原因 → 恢复”两端：
    /// 1. 资源不足：陷阱发射器装上拖尾（耗水），抽水线少接一格 → 状态写“缺流体”、一轮都不铺 → 补上那一格管线 → 开始铺；
    /// 2. 路径失败：屏障圈只差留给闸门的那一格，拿屏障 T2 指着它 → 放置预览警告“放在这里后，机器将无法到达……”（只警告不阻止）→ 不放墙、改放闸门（只挡敌方）→
    ///    闸门建成：敌方寻路被圈死、自己的机器照样从闸门进出。（完全围死时敌人破墙——FGR-DEF-032——只在圈外够得着的目标全打完之后才发生：攻城按职能先打够得着的，
    ///    开局建筑都在圈外，真实突袭里时机不可控；由 [攻城] 自检 FgSiegeSelfCheck 覆盖，见 ADR-QA-023 第 6 节）
    /// 3. 暂停与倍速：攻城进行中按暂停 → 敌人、陷阱、世界步都不动；0.5x / 3x 下世界按游戏时间走（真实时间差 6 倍），陷阱还在时按游戏时间每秒铺一轮；突袭后“重建 / 修复”拆坏的建筑；
    /// 4. 存读档：攻城进行中保存并回主菜单 → 存档里的攻城 / 结算 / 导演状态与存档那一刻一致 → 读档后攻城接着打、这一波只结算一次；
    /// 5. 断链：攻城中禁用全部还在发电的发电机（开局那台、圈里的发电机 2）→ 炮塔缺电停火（原因写明）→ 逐台启用 → 炮塔恢复开火；
    /// 6. 目标死亡：接入的机器开进敌群被打死 → 信号回到归还核心（原因写明）、黑匣子回收；家园机器全灭 → 应急打印救回家园。
    /// 突袭用“剧情节点触发”入口排定（进度夹具 ApplyRaidFixture：家园突袭的正式触发 = 暴露越过阈值，FGJ-M6 已从正式入口走三波；这里要 3 波、每波在可控时刻）；
    /// 屏障 / 陷阱研究用进度夹具 ApplyResearchFixture（研究本身由 FGJ-M6 走正式入口）。夹具只在这两个方法里（[M6 出口] A2 守护），见 ADR-QA-023 第 5 节。
    /// </summary>
    public static class FgjM6ReverseJourney
    {
        public const string Id = "FGJ-M6R";

        public const int TestSeed = 1;

        private const string FwWater = "fw_trail";

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M6 反向旅程：资源不足（陷阱缺水）、路径失败（围死没留闸门 → 预览警告 → 改放闸门）、暂停与倍速（攻城中）、存读档（攻城中）、断链（断电炮塔停火）、目标死亡（接入的机器被打死、全灭应急打印）",
            Seed = TestSeed,
            TotalTimeoutSeconds = 4800,
            OnFinish = Cleanup,
            Version = 2,
            CheckpointAfter = new[] { "built", "r1_flow", "pf_built", "rb_wall", "rs_wall" },
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 1）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_gen", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep1", "等仓库与发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeGenerator)),
                S("sel_a2", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_tw", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("sel_b2", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage", "右键点开局残骸（拆解）", 20, FgjM2Common.RightClickWreck, FgjM2Common.TickSalvageOrdered, retries: 4),
                S("salv_wait", "等残骸拆完", 180, null, FgjM2Common.TickSalvageDone),
                S("wait_rep2", "等信号塔修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeSignalTower)),
                S("salv2_sel", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("salvage2", "右键点第二处开局残骸（拆解）", 20, FgjM3Common.RightClickWreck2, FgjM3Common.TickWreck2Ordered, retries: 4),
                S("salv2_wait", "等残骸拆完", 180, null, FgjM3Common.TickWreck2Done),
                S("research_fixture", "进度夹具：研发树“防御 · 屏障与闸门”“防御 · 陷阱发射器”记为已研究（研究由 FGJ-M6 走正式入口）", 10, ApplyResearchFixture, TickResearchFixture),

                // ── 布防：屏障圈（留出闸门位）、圈里两座炮塔与发电机 2、陷阱发射器与少接一格的抽水线 ──
                S("plan", "看地形：核心外挑一圈能完全围死的位置（离仓库最近的一格留作闸门位）、圈里两座炮塔与发电机 2、离核心最近的抽水线与陷阱发射器、回收站", 30, null, TickPlan),
                S("build_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("place", "放发电机 2、回收站与带、两座炮塔、陷阱发射器、泵与抽水线（故意少接泵旁那一格）", 300, null, c => P.TickActions(c, "rp", 40), retries: 2),
                S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("built0", "机器取料施工：炮塔、发电机 2、回收站、陷阱发射器、泵与管线建成接电（屏障圈后面再拖）", 1500, null, c => TickBuilt(c, false)),

                // ── 1. 资源不足：陷阱缺水 → 补上管线 ──
                S("r1_open", "建造模式里左键点陷阱发射器，通用面板上点“陷阱…”：防御面板打开", 30, null,
                    c => M.TickSubPanel(c, "trap", "PrDefense", () => DefensePanelUIToolkit.IsOpen, "防御面板（陷阱发射器）打开"), retries: 2),
                S("r1_fw", "固件下拉选“拖尾”（每轮耗水）、点“一片区域”", 20, null, TickTrapWater, retries: 1),
                S("r1_short", "抽水线少一格：状态写“缺流体”（原因与怎么办）、一轮都不铺", 40, null, TickTrapShort),
                S("r1_close", "Esc 关掉防御面板与通用面板", 15, null, c => M.TickCloseAll(c, () => !DefensePanelUIToolkit.IsOpen, "防御面板"), retries: 1),
                S("r1_fix", "建造栏选管线，单击补上泵旁那一格", 60, null, c => P.TickActions(c, "rpfix", 30), retries: 2),
                S("r1_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("r1_flow", "机器补上那格管线：水流到陷阱发射器，开始一轮轮铺减速带，“缺流体”的原因消失", 300, null, TickTrapFlowing),

                // ── 屏障圈（闸门位先空着；资源不足那一段先做，免得圈外的抽水线要绕远路）──
                S("walls_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("walls", "屏障 T2 按住左键一段段拖，把核心围起来（闸门位先空着，机器从那里进出）", 300, null, D.TickWalls, retries: 2),
                S("walls_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("built", "机器取料施工：屏障圈建成（闸门位空着）", 1500, null, c => TickBuilt(c, true)),

                // ── 2. 路径失败：没留闸门就把核心围死 → 放置预览警告“机器将无法到达……” → 改放闸门 ──
                S("pf_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("pf_hover", "建造栏选屏障 T2，光标指着圈上留给闸门的那一格：状态行警告“放在这里后，机器将无法到达……（仍可放置）”——不点", 40, null, TickSealWarning, retries: 2),
                S("pf_gate", "改选闸门、单击那一格（闸门只挡敌方，放行自己的机器，不再警告）", 60, null, c => P.TickActions(c, "pfgate", 30), retries: 2),
                S("pf_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("pf_built", "机器取料建成闸门：敌方寻路被圈死（从圈里出不去），自己的机器照样从闸门进出（己方寻路走得出去）", 300, null, TickGateBuilt),

                // ── 3. 暂停与倍速（第一波）──
                S("ra_fix", "进度夹具：剧情节点触发一波 1 级突袭（正式的暴露触发由 FGJ-M6 覆盖）", 10, ApplyRaidFixture, c => TickRaidRequested(c, "ra")),
                S("ra_warn", "等预警：左上角预警条一行（倒计时、来袭方向）", 600, null, c => TickWarned(c, "ra")),
                S("ra_siege", "部队到达、攻城进行中（核心被屏障 + 闸门完全围住，敌人先打圈外够得着的建筑）", 400, null, c => TickSieging(c, "ra")),
                S("ra_pause", "按暂停键（攻城中）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), FgjM1ReverseJourney.TickPausedNow, retries: 1),
                S("ra_frozen", "暂停 1.5 真实秒：攻城敌人的位置与耐久、陷阱铺设、世界步都不动", 15, null, TickSiegeFrozen),
                S("ra_resume", "按暂停键恢复", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, false),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done("恢复") : StepOutcome.Retry("还在暂停"), retries: 1),
                S("ra_half", "按 0.5 倍速键：世界按游戏时间走（真实时间变慢），陷阱还在就按游戏时间每秒铺一轮", 60, c => JourneyInput.PressAction(GameActionId.SpeedHalf), c => TickLayRate(c, 0.5f, "half"), retries: 1),
                S("ra_triple", "按 3 倍速键：世界走得是 0.5x 时的约 6 倍（陷阱还在就照样每游戏秒一轮）", 40, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickLayRate(c, 3f, "triple"), retries: 1),
                S("ra_end", "这一波结束（撤退或全歼），核心没被打到", 400, null, c => TickRaidOver(c, "ra")),
                S("ra_wall", "这一波拆成废墟的屏障 / 闸门 / 圈里的建筑逐座重建：左键点它，通用面板上点“重建 / 修复”（被摧毁的留着原样的虚影），机器修好；没有拆坏的就跳过", 600, null, TickWallRestored),

                // ── 4. 存读档（第二波）──
                S("rb_fix", "进度夹具：剧情节点再触发一波", 10, ApplyRaidFixture, c => TickRaidRequested(c, "rb")),
                S("rb_warn", "等预警", 600, null, c => TickWarned(c, "rb")),
                S("rb_siege", "部队到达、攻城进行中", 400, null, c => TickSieging(c, "rb")),
                S("rb_esc", "按 Esc 打开暂停菜单（攻城中，世界暂停）", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("rb_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("rb_menu", "回到主菜单：存档里攻城 / 结算 / 突袭导演与存档那一刻逐项一致", 60, null, TickMenuAfterSave),
                S("rb_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("rb_loaded", "读档进入家园：攻城接着打——同一支部队、同一个剧场，敌人一台不多一台不少（阵亡 + 离场 + 在场 = 存档时在场）", 120, null, TickLoadedSiege),
                S("rb_speed", "按 3 倍速键（读档后从 1x 开始）", 10, c => JourneyInput.PressAction(GameActionId.SpeedTriple), FgjM1Journey.TickSpeed3, retries: 1),
                S("rb_end", "这一波照常结束，只结算一次", 400, null, c => TickRaidOver(c, "rb")),
                S("rb_wall", "这一波拆成废墟的屏障 / 闸门 / 圈里的建筑逐座重建（下一波之前把圈补上）", 600, null, TickWallRestored),

                // ── 承接 DEBT-FG5E2E01-05：真实突袭打坏熔合中的合成台 → 熔合回滚（父固件先复原 / 刻印；合成台按第一波预警条上的抵达点放在屏障圈外）──
                S("fu_ana_open", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, FgjM2Common.TickAnalysisOpen, retries: 4),
                S("fu_ana_n2", "“数据复原”下拉选液氮，点“复原”：弹出确认框", 10, c => FgjM2Common.PickAndRestore(c, FwPartner), c => FgjM2Common.TickConfirmShown(c, FwPartner), retries: 1),
                S("fu_ana_ok", "确认框点“复原”：液氮解锁", 10, c => FgjM2Common.ClickConfirm(c, true), c => TickRestored(c, FwPartner)),
                S("fu_ana_close", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
                S("fu_sc_open", "按信号核键打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("fu_print_ov", "刻印下拉选过载，点“刻印”", 60, null, c => FgjM5Journey.TickPrint(c, FirmwareCatalog.FwOverloadId), retries: 1),
                S("fu_print_n2", "刻印下拉选液氮，点“刻印”", 60, null, c => FgjM5Journey.TickPrint(c, FwPartner), retries: 1),
                S("fu_print_ov2", "再刻一枚过载（排两项正式熔合用）", 60, null, c => FgjM5Journey.TickPrint(c, FirmwareCatalog.FwOverloadId), retries: 1),
                S("fu_print_n22", "再刻一枚液氮", 60, null, c => FgjM5Journey.TickPrint(c, FwPartner), retries: 1),
                S("fu_sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                S("fu_plan", "看第一波预警条上的抵达点（敌人在那里展开）：在屏障圈外、离抵达点最近、核心配电范围里的空地规划电路合成台", 30, null, TickSynthPlan),
                S("fu_build_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("fu_place", "放电路合成台", 120, null, c => P.TickActions(c, "fusyn", 30), retries: 2),
                S("fu_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("fu_built", "机器取料施工：电路合成台建成接电", 600, null, c => M.TickBuilt(c, new[] { "synth" }, Array.Empty<string>(), false, "电路合成台")),
                S("fu_open", "建造模式里左键点电路合成台，通用面板上点“熔合…”", 40, null, c => M.TickSubPanel(c, "synth", "PrFusion", FusionOpen, "合成台面板打开"), retries: 2),
                S("fu_pick", "固件 A 选过载、固件 B 选液氮", 15, null, TickPickPair, retries: 1),
                S("fu_sim", "点“模拟熔合”：有配方，只花模拟费", 10, null, TickSimHit, retries: 1),
                S("fu_sub", "进度夹具：芯片基板放进仓库（DEBT-FG5E2E01-02）", 10, ApplySubstrateFixture, M.TickSubstrateFixture),
                S("fu_close", "按 Esc 关闭合成台面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !FusionPanelUIToolkit.IsOpen, "合成台面板"), retries: 1),
                S("fu_build_close2", "按建造键关闭建造模式（开着的话）", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("rs_fix", "进度夹具：剧情节点再触发一波", 10, ApplyRaidFixture, c => TickRaidRequested(c, "rs")),
                S("rs_warn", "等预警（来袭方向与第一波相同：合成台就在抵达点旁边）", 600, null, c => TickWarned(c, "rs")),
                S("rs_slow", "按 1 倍速键（部队快到了，留出在合成台上操作的时间）", 10, c => JourneyInput.PressAction(GameActionId.SpeedNormal), c => TickSpeedIs(c, 1f), retries: 1),
                S("fu_open2", "左键点电路合成台，通用面板上点“熔合…”", 40, null, c => M.TickSubPanel(c, "synth", "PrFusion", FusionOpen, "合成台面板打开"), retries: 2),
                S("fu_pick2", "固件 A 选过载、固件 B 选液氮", 15, null, TickPickPair, retries: 1),
                S("fu_eta", "看预警条：部队离抵达只剩 20 游戏秒以内（合成台面板开着等）", 300, null, TickRaidClose),
                S("fu_formal", "点“正式熔合…”，确认框点“确认”：入队（两枚父固件被预留，芯片基板 ×2 与技术数据记在任务上）", 20, null, c => TickFormalQueued(c, 1), retries: 1),
                S("fu_pick3", "（第一项入队后）看固件 A / B 还是过载 / 液氮，不是就重新选", 15, null, TickPickPair, retries: 1),
                S("fu_formal2", "再点一次“正式熔合…”、确认：第二项排在后面（这台合成台上一共 60 游戏秒的熔合）", 20, null, c => TickFormalQueued(c, 2), retries: 1),
                S("rs_siege", "部队到达、展开攻城（合成台就在抵达点旁边）", 400, null, c => TickSieging(c, "rs")),
                S("fu_rolled", "攻城单位拆掉正在熔合的电路合成台：熔合事务回滚——父固件预留释放、芯片基板与技术数据退回、通知“熔合中止”；突袭结算记下合成台这处损失", 300, null, TickRaidRolledBack),
                S("fu_close2", "按 Esc 关掉还开着的合成台面板", 15, null, c => M.TickCloseAll(c, () => !FusionPanelUIToolkit.IsOpen, "合成台面板"), retries: 1),
                S("rs_fast", "按 3 倍速键", 10, c => JourneyInput.PressAction(GameActionId.SpeedTriple), FgjM1Journey.TickSpeed3, retries: 1),
                S("rs_end", "这一波结束（只结算一次）", 400, null, c => TickRaidOver(c, "rs")),
                S("rs_wall", "这一波拆成废墟的屏障 / 闸门 / 圈里的建筑逐座重建", 600, null, TickWallRestored),

                // ── 5. 断链（第三波攻城中、敌人进了圈里炮塔的射程、炮塔正在开火时断电 → 停火 → 逐台启用 → 恢复开火）──
                S("rc_fix", "进度夹具：剧情节点再触发一波", 10, ApplyRaidFixture, c => TickRaidRequested(c, "rc")),
                S("rc_warn", "等预警", 600, null, c => TickWarned(c, "rc")),
                S("rc_list", "记下现在还在发电的发电机（开局那台、圈里的发电机 2）与还活着的家园机器", 10, null, TickListGens),
                S("rc_siege", "部队到达、攻城进行中", 400, null, c => TickSieging(c, "rc")),
                S("rc_engage", "敌人进了圈里炮塔的射程，炮塔正在开火（数着两座炮塔的开火次数）", 180, null, TickTurretsEngaged),
                GenOpen("rc_gen0", 0), GenToggle("rc_off0", 0, true), GenClose("rc_close0", 0),
                GenOpen("rc_gen1", 1), GenToggle("rc_off1", 1, true), GenClose("rc_close1", 1),
                GenOpen("rc_gen2", 2), GenToggle("rc_off2", 2, true), GenClose("rc_close2", 2),
                S("rc_nopower", "断链：家园断电——炮塔状态写明缺电；敌人还在射程里，开火次数不再增加", 90, null, TickTurretsNoPower),
                GenOpen("rc_gen_on0", 0), GenToggle("rc_on0", 0, false), GenClose("rc_closeon0", 0),
                GenOpen("rc_gen_on1", 1), GenToggle("rc_on1", 1, false), GenClose("rc_closeon1", 1),
                GenOpen("rc_gen_on2", 2), GenToggle("rc_on2", 2, false), GenClose("rc_closeon2", 2),
                S("rc_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("rc_power", "来电：炮塔恢复有电，敌人还在射程里就接着开火（开火次数又增加）", 120, null, TickTurretsPowered),

                // ── 6. 目标死亡（两台机器先开到抵达点；部队一到就接入第一台冲进敌群）──
                S("rc_sel_a0", "左键点第一台家园机器", 20, c => FgjM1Journey.ClickMachine(c, "victimA"), c => FgjM1Journey.TickSelected(c, "victimA"), retries: 4),
                S("rc_send_a", "右键点预警条写的抵达点（右键地面 = 移动）：它从闸门出圈去迎敌", 30, c => RightClickArrival(c, "victimA"), c => TickSentToArrival(c, "victimA"), retries: 2),
                S("rc_sel_b0", "左键点第二台家园机器（只剩一台就跳过）", 20, c => { if (c.GetInt("victimB") > 0) { FgjM1Journey.ClickMachine(c, "victimB"); } },
                    c => c.GetInt("victimB") > 0 ? FgjM1Journey.TickSelected(c, "victimB") : StepOutcome.Done("只剩一台机器，跳过"), retries: 4),
                S("rc_send_b", "右键点抵达点：第二台也去", 30, c => RightClickArrival(c, "victimB"), c => TickSentToArrival(c, "victimB"), retries: 2),
                S("rc_charge", "部队一到：左键点第一台、按接入键（直控）冲进敌群——机器被打死，信号回到归还核心或弹到最近能接入的机器（原因写明），黑匣子回收；这一波打完还活着就再来一波", 1500, null, TickUplinkedDies),
                S("rc_sel_b", "看还有哪些家园机器活着（第一台阵亡后信号回核心或弹到另一台）", 10, null, c => StepOutcome.Done($"还活着的家园机器：{string.Join("、", Victims(c).Where(MachineAlive).Select(FgjM1Journey.Label))}（信号在 {(SignalPresence.AtCore ? "归还核心" : FgjM1Journey.Label(SignalPresence.CurrentMachineLogicId))}）")),
                S("rc_attack", "剩下的家园机器：信号在谁身上就直控它开进敌群，否则左键选中、右键点进攻的敌人（右键敌人 = 攻击）——都被打死、家园机器全灭 → 应急打印一台机器救回家园；这一波打完还有活着的就再来一波", 1500, null, TickWipedAndPrinted, retries: 1),
                S("rc_back", "第三波（含加的几波）结束：结算记着阵亡的家园机器，炮塔照常守住", 400, null, TickPowerBack),

                // ── 承接 DEBT-FG5RND06-03：真实突袭打死家园机器 → 黑匣子 → 陈列馆分析 → 技术数据 → 研究 ──
                S("bb_plan", "看地形：在核心配电范围里规划黑匣子陈列馆与仿真实验室（电不够就加发电机 2）", 30, null, TickBlackBoxPlan),
                S("bb_build_open", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("bb_place", "放陈列馆、仿真实验室（电不够时连同发电机 2）", 200, null, c => P.TickActions(c, "bbp", 40), retries: 2),
                S("bb_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("bb_built", "机器取料施工：陈列馆、实验室建成接电（陈列馆开始分析第三波阵亡机器的黑匣子）", 900, null, c => M.TickBuilt(c, new[] { "gallery", "lab" }, Array.Empty<string>(), true, "陈列馆与实验室")),
                S("bb_rt_open", "按研发树键（默认 K）打开研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, true), c => M.TickTreeOpen(c, true), retries: 1),
                S("bb_rt_q", "点“工业”分支、左键点“工业 · 实验室 T2”排进研究队列（仿真实验室把技术数据换成研究点）", 15, null, c => M.TickQueueNode(c, NodeLabT2), retries: 2),
                S("bb_rt_close", "再按研发树键关闭研发树", 10, c => JourneyInput.PressToggleTo(GameActionId.OpenResearch, () => ResearchTreePanelUIToolkit.IsOpen, false), c => M.TickTreeOpen(c, false), retries: 1),
                S("bb_done", "陈列馆分析完第三波阵亡机器的一个黑匣子：技术数据 +20 逐点入账（统计“黑匣子”收入），这期间实验室取技术数据换研究点", 1500, null, TickBlackBoxResearched),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM2Common.IsolateCodex(c);
            });

        private static long NowMs() => FgjM2Common.NowMs();

        // ── 进度夹具（只在这两个方法里；[M6 出口] A2 守护）───────────────────────────────

        /// <summary>
        /// 进度夹具（DEBT-FG6E2E01-02，ADR-QA-023 第 5 节）：研发树“防御 · 屏障与闸门”“防御 · 陷阱发射器”记为已研究——研究本身（实验室、排队、研究点）由 FGJ-M6 走正式入口；
        /// 反向旅程测的是防御出错与恢复，不再花 10 分钟研究。之后的放置、施工、配置全走正式流程。
        /// </summary>
        private static void ApplyResearchFixture(JourneyContext c) => ResearchService.CompleteForTests(St, D.NodeBarrier, D.NodeTrap);

        private static StepOutcome TickResearchFixture(JourneyContext c) =>
            ResearchService.IsCompleted(St, D.NodeBarrier) && ResearchService.IsCompleted(St, D.NodeTrap)
                ? StepOutcome.Done("进度夹具：屏障与闸门、陷阱发射器记为已研究（研究由 FGJ-M6 走正式入口；DEBT-FG6E2E01-02）")
                : StepOutcome.Fail("研究夹具没有生效");

        /// <summary>
        /// 进度夹具（DEBT-FG6E2E01-03，ADR-QA-023 第 5 节）：剧情节点触发一波 1 级突袭（突袭导演的剧情入口，不受开局宽限与最短间隔限制）。
        /// 家园突袭的正式触发（暴露越过阈值 → 排定 → 预警）由 FGJ-M6 三波从正式入口覆盖；这里要在可控的时刻来三波。之后的筹备、预警、行进、到达、攻城、撤退、结算全是正式流程。
        /// </summary>
        private static void ApplyRaidFixture(JourneyContext c)
        {
            CampaignState s = St;
            c.SetInt(FgjM3Common.SK(c, "n0"), RaidDirectorService.StateOf(s)?.NextPlanSerial ?? 0);
            RaidDirectorService.RequestStoryRaid(s, RaidDirectorService.MostStimulatedFaction(s), 1);
        }

        private static StepOutcome TickRaidRequested(JourneyContext c, string key)
        {
            CampaignState s = St;
            RaidPlanRecord p = D.LivePlans(s).Where(x => x.Exempt && x.Serial >= c.GetInt(FgjM3Common.SK(c, "n0"))).OrderBy(x => x.Serial).FirstOrDefault();
            if (p == null)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail("剧情触发后没有新的突袭计划");
            }
            c.Set(key + ".plan", p.PlanId);
            c.Set(key + ".plans", p.PlanId);
            return StepOutcome.Done($"进度夹具：剧情节点触发一波突袭 {D.PlanLine(p)}（DEBT-FG6E2E01-03）");
        }

        // ── 规划与施工 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 第三波：攻城只有一个游戏小时（FGR-DEF-032），1 级部队在时限里不一定打得死冲上去的机器（2 级又会在断电时把核心拆掉）。
        /// 这一波结束了、要打死的机器还活着，就用同一个进度夹具再请一波 1 级（至多 3 次），接着冲。返回 true = 正在等新的一波排定。
        /// </summary>
        private static bool CallAnotherWaveIfNeeded(JourneyContext c)
        {
            CampaignState s = St;
            string pending = c.Get("rc.next");
            if (!string.IsNullOrEmpty(pending))
            {
                RaidPlanRecord p = D.LivePlans(s).Where(x => x.Exempt && x.Serial >= c.GetInt("rc.n0")).OrderBy(x => x.Serial).FirstOrDefault();
                if (p == null)
                {
                    return true;
                }
                c.Set("rc.next", string.Empty);
                c.Set("rc.plan", p.PlanId);
                c.Set("rc.plans", c.Get("rc.plans") + "," + p.PlanId);
                c.Log($"又一波（剧情节点，进度夹具）：{D.PlanLine(p)}");
                return false;
            }
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get("rc.plan"));
            if (r == null || r.EndTick < 0 || D.Raiders().Count > 0)
            {
                return false;
            }
            if (c.GetInt("rc.extra") >= 3)
            {
                return false;
            }
            c.SetInt("rc.extra", c.GetInt("rc.extra") + 1);
            c.SetInt("rc.n0", RaidDirectorService.StateOf(s)?.NextPlanSerial ?? 0);
            c.Set("rc.next", "1");
            ApplyRaidFixture(c);
            return true;
        }

        private static IEnumerable<RaidResultRecord> RcResults(JourneyContext c) =>
            (c.Get("rc.plans") ?? string.Empty).Split(',').Where(x => !string.IsNullOrEmpty(x)).Select(x => RaidResultService.FindByPlan(St, x)).Where(x => x != null);

        private static StepOutcome TickPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            D.ReversePlan rp = D.PlanReverse(s, out string why);
            if (rp == null)
            {
                return StepOutcome.Fail("反向旅程布防规划不出来：" + why);
            }
            M.SavePlan(c, rp.Plan);
            var trap = new P.Placed { Key = "trap", Type = D.Trap, Pivot = rp.TrapAt, Rot = 0, Label = D.Name(D.Trap) };
            P.Remember(c, trap);
            P.SetP(c, "gate", rp.GateAt);
            P.SetP(c, "pump", rp.Pump);
            P.SetP(c, "gap", rp.Water[0]);
            c.Set("water", FgjM3Common.EncodeCells(rp.Water));
            c.SetInt("walls.n", rp.WallRuns.Count);
            for (int i = 0; i < rp.WallRuns.Count; i++)
            {
                P.SetP(c, "walls.a" + i, rp.WallRuns[i].A);
                P.SetP(c, "walls.b" + i, rp.WallRuns[i].B);
            }
            c.SetInt("walls.cells", rp.WallCells);
            c.SetInt("walls.margin", rp.RingMargin);
            var a = new List<string>();
            foreach (GridCell g in rp.Plan.Generators)
            {
                a.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {D.Cell(g)}");
            }
            a.Add(P.ActB(rp.Plan.B("recycler")));
            a.Add(P.ActR(P.BeltT1, "path.r_recycler", rp.Plan.R("r_recycler").GoalDir, "回收站废料带 → 仓库输入口"));
            a.Add(P.ActB(rp.Plan.B("t1")));
            a.Add(P.ActB(rp.Plan.B("t2")));
            a.Add(P.ActB(trap));
            a.Add(P.ActC(P.PumpTool, rp.Pump, "泵（水源上）"));
            a.Add(P.ActC(P.PipeT1, rp.Water[1], "抽水线第 2 格"));
            a.Add(P.ActC(P.PipeT1, rp.Water[2], "抽水线第 3 格（陷阱发射器旁边）"));
            P.SetActions(c, "rp", a);
            P.SetActions(c, "rpfix", new[] { P.ActC(P.PipeT1, rp.Water[0], "补上泵旁那一格管线") });
            P.SetActions(c, "pfgate", new[] { $"B|gate|{rp.GateAt.X}|{rp.GateAt.Y}|0|闸门 {D.Cell(rp.GateAt)}" });
            return StepOutcome.Done($"规划：屏障圈（核心外框外 {rp.RingMargin} 格）{rp.WallRuns.Count} 段 {rp.WallCells} 格 + 闸门位 {D.Cell(rp.GateAt)}（先空着）；" +
                                    $"圈里炮塔 {D.Cell(rp.Plan.B("t1").Pivot)}、{D.Cell(rp.Plan.B("t2").Pivot)}，发电机 2 {string.Join("、", rp.Plan.Generators.Select(D.Cell))}；" +
                                    $"泵 {D.Cell(rp.Pump)} → 管线 {string.Join("、", rp.Water.Select(D.Cell))} → 陷阱发射器 {D.Cell(rp.TrapAt)}；回收站 {D.Cell(rp.Plan.B("recycler").Pivot)}");
        }

        private static StepOutcome TickBuilt(JourneyContext c, bool wallsToo)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            var notBuilt = new List<string>();
            foreach (string k in new[] { "recycler", "t1", "t2", "trap" })
            {
                if (!P.BuildingReady(s, c.Get(k + ".type"), P.P(c, k), out _))
                {
                    notBuilt.Add(k);
                }
            }
            foreach (GridCell g in FgjM3Common.DecodeCells(c.Get("plan.gens")))
            {
                if (!P.BuildingReady(s, HomeValleyLayout.BuildingTypeGenerator2, g, out _))
                {
                    notBuilt.Add("发电机2");
                }
            }
            List<GridCell> water = FgjM3Common.DecodeCells(c.Get("water"));
            int pipes = new[] { P.P(c, "pump"), water[1], water[2] }.Count(x => !P.RouteBuilt(s, new List<GridCell> { x }, false));
            int walls = D.WallsBuilt(c, out int total);
            if (!wallsToo)
            {
                walls = total; // 屏障圈在资源不足那一段之后再拖
            }
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"布防施工：还没建成 [{string.Join(",", notBuilt)}]、管线还差 {pipes}、屏障 {walls}/{total}；废料 {s.Scrap}；{P.PowerLine(s)}");
            }
            if (notBuilt.Count > 0 || pipes > 0 || walls < total)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done(wallsToo
                ? $"屏障圈建成：{walls} 格把核心围住（闸门位空着）；废料 {s.Scrap}；{P.PowerLine(s)}"
                : $"布防建成：圈里两座炮塔与发电机 2、回收站、陷阱发射器、泵与两格管线；废料 {s.Scrap}；{P.PowerLine(s)}");
        }

        // ── 发电机槽（断链段：攻城开始前还在发电的那几台，至多 3 台）──────────────────────────

        private const int GenSlots = 3;

        private static StepOutcome TickListGens(JourneyContext c)
        {
            List<BuildingRecord> gens = St.BuildingRecords
                .Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && !HomeValleyController.IsPlannedGhost(b) && b.ConstructionState == BuildingConstructionState.Operational
                            && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2))
                .OrderBy(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator ? 0 : 1).ThenBy(b => b.GridX).ThenBy(b => b.GridY).Take(GenSlots).ToList();
            if (gens.Count == 0)
            {
                return StepOutcome.Fail("家园里一台在发电的发电机都没有（突袭前就断电了）：" + P.PowerLine(St));
            }
            c.SetInt("gens.n", gens.Count);
            for (int i = 0; i < gens.Count; i++)
            {
                c.Set("gens.t" + i.ToString(CultureInfo.InvariantCulture), gens[i].BuildingTypeId);
                P.SetP(c, "gens.p" + i.ToString(CultureInfo.InvariantCulture), new GridCell(gens[i].GridX, gens[i].GridY));
            }
            // 第三波要打死的家园机器：此刻还活着的（前两波里可能已有机器阵亡、应急打印过），至多两台。
            List<MachineRecord> alive = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();
            c.Set("victims", string.Join(",", alive.Select(m => m.LogicId.ToString(CultureInfo.InvariantCulture))));
            if (alive.Count == 0)
            {
                return StepOutcome.Fail("第三波来之前家园一台机器都没有（应急打印没救回来？）");
            }
            c.SetInt("victimA", alive[0].LogicId);
            c.SetInt("victimB", alive.Count > 1 ? alive[1].LogicId : 0);
            return StepOutcome.Done($"还在发电的发电机 {gens.Count} 台：{string.Join("、", gens.Select(b => $"{BuildingOps.NameOf(b)} {D.Cell(new GridCell(b.GridX, b.GridY))}"))}；{P.PowerLine(St)}；" +
                                    $"家园机器 {string.Join("、", alive.Select(m => FgjM1Journey.Label(m.LogicId)))}");
        }

        private static StepOutcome OnGen(JourneyContext c, int i, Func<string, GridCell, StepOutcome> act)
        {
            string k = i.ToString(CultureInfo.InvariantCulture);
            if (i >= c.GetInt("gens.n"))
            {
                return StepOutcome.Done($"没有第 {i + 1} 台发电机，跳过");
            }
            string type = c.Get("gens.t" + k);
            GridCell pivot = P.P(c, "gens.p" + k);
            BuildingRecord b = P.BuildingAtPivot(St, type, pivot);
            if (b == null || b.ConstructionState == BuildingConstructionState.Destroyed || BuildingOps.Durability(b) <= 0.5f)
            {
                // 这一波里被拆了（废墟虚影等重建）：开关它没有意义，跳过（来电靠剩下的发电机）。
                if (ProductionPanelUIToolkit.IsOpen)
                {
                    JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose");
                }
                return StepOutcome.Done($"{D.Name(type)} {D.Cell(pivot)} 这一波里被拆了，跳过");
            }
            return act(type, pivot);
        }

        private static JourneyStep GenOpen(string id, int i) =>
            S(id, $"建造模式里左键点第 {i + 1} 台发电机（没有就跳过）", 40, null, c => OnGen(c, i, (t, p) => P.TickOpenPanel(c, t, p)), retries: 2);

        private static JourneyStep GenToggle(string id, int i, bool disable) =>
            S(id, disable ? "点“禁用”" : "点“启用”", 15, null, c => OnGen(c, i, (t, p) => TickToggle(c, t, p, disable)), retries: 1);

        private static JourneyStep GenClose(string id, int i) =>
            S(id, "点“关闭”", 10, null, c => OnGen(c, i, (t, p) => P.TickClosePanel(c)), retries: 1);

        // ── 1. 资源不足 ───────────────────────────────────────────────────────────────

        private static StepOutcome TickTrapWater(JourneyContext c)
        {
            CampaignState s = St;
            DefensePanelUIToolkit p = DefensePanelUIToolkit.Instance;
            if (p == null || !DefensePanelUIToolkit.IsOpen || !p.TrapSectionVisible)
            {
                return StepOutcome.Fail("防御面板没开或没有陷阱一段");
            }
            p.Refresh(force: true);
            BuildingRecord tb = P.Bld(c, "trap");
            c.Set("trap.id", tb?.BuildingId ?? string.Empty);
            DefenseRecord r = tb != null ? DefenseService.Find(s, tb.BuildingId) : null;
            if (r != null && r.TrapFirmware == FwWater && FgjM3Common.Done(c, "area"))
            {
                return FgjM3Common.SinceMs(c, "area") < 800 ? StepOutcome.Wait
                    : r.TrapPattern == 1 ? StepOutcome.Done($"固件选“{M.FwName(FwWater)}”、点“一片区域”（补给“{p.TrapSupplyText.Replace("\n", " ")}”）") : StepOutcome.Retry("点了“一片区域”铺设方式没变");
            }
            if (r == null || r.TrapFirmware != FwWater)
            {
                int idx = -1;
                for (int i = 0; i < p.FirmwareIds.Count; i++)
                {
                    if (p.FirmwareIds[i] == FwWater)
                    {
                        idx = i;
                    }
                }
                DropdownField d = p.FirmwareField;
                if (idx < 0 || d == null || idx >= d.choices.Count)
                {
                    return StepOutcome.Fail($"陷阱固件下拉里没有“{M.FwName(FwWater)}”（{string.Join("、", p.FirmwareChoices)}）");
                }
                if (FgjM3Common.Done(c, "pick"))
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry($"选了拖尾后陷阱固件仍是“{r?.TrapFirmware}”（“{p.MessageText}”）");
                }
                if (!JourneyInput.IsClickable(d))
                {
                    return M.UiRetry("陷阱固件下拉点不到：");
                }
                d.value = d.choices[idx];
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "area", () => JourneyInput.ClickElement(p.PatternAreaButton)))
            {
                return M.UiRetry("点不到“一片区域”：");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickTrapShort(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            DefenseRecord r = DefenseService.Find(s, c.Get("trap.id"));
            BuildingRecord tb = HomeGridService.FindBuilding(s, c.Get("trap.id"));
            if (!FgjM3Common.Done(c, "t0"))
            {
                c.SetLong(FgjM3Common.SK(c, "l0"), r?.TrapLays ?? 0);
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            if (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0")) < GameClock.StepHz * 10)
            {
                return StepOutcome.Wait;
            }
            DefensePanelUIToolkit p = DefensePanelUIToolkit.Instance;
            p?.Refresh(force: true);
            string reason = tb != null ? BuildingStatusService.Evaluate(s, tb).Reason ?? string.Empty : string.Empty;
            string supply = p?.TrapSupplyText ?? string.Empty;
            bool none = (r?.TrapLays ?? 0) == c.GetLong(FgjM3Common.SK(c, "l0"));
            bool said = (reason + supply).Contains("缺") || (reason + supply).Contains("没接管线") || (reason + supply).Contains("管线");
            return none && said
                ? StepOutcome.Done($"10 游戏秒一轮都没铺（累计 {r?.TrapLays}）；状态“{reason.Replace("\n", " / ")}”；面板补给“{supply.Replace("\n", " ")}”")
                : StepOutcome.Fail($"缺水时：铺了 {(r?.TrapLays ?? 0) - c.GetLong(FgjM3Common.SK(c, "l0"))} 轮、状态“{reason}”、补给“{supply}”");
        }

        private static StepOutcome TickTrapFlowing(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            DefenseRecord r = DefenseService.Find(s, c.Get("trap.id"));
            BuildingRecord tb = HomeGridService.FindBuilding(s, c.Get("trap.id"));
            if (!FgjM3Common.Done(c, "t0"))
            {
                c.SetLong(FgjM3Common.SK(c, "l0"), r?.TrapLays ?? 0);
                FgjM3Common.Mark(c, "t0");
            }
            long lays = (r?.TrapLays ?? 0) - c.GetLong(FgjM3Common.SK(c, "l0"));
            string reason = tb != null ? BuildingStatusService.Evaluate(s, tb).Reason ?? string.Empty : string.Empty;
            if (lays < 3)
            {
                int tick = (int)(c.StepElapsed / 30);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    GridCell gap = P.P(c, "gap");
                    bool pipe = P.RouteBuilt(s, new List<GridCell> { gap }, false);
                    IEnumerable<string> ms = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Select(m =>
                    {
                        FgjM1ReverseJourney.LivePos(m.LogicId, out Vector2 at);
                        GridCell mc = NavService.CellOf(at.x, at.y);
                        WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(s, m.LogicId);
                        BuildingRecord under = HomeGridService.BuildingAt(s, mc);
                        return $"{FgjM1Journey.Label(m.LogicId)}@{at.x:F1},{at.y:F1} 格 {D.Cell(mc)} 可走 {NavService.PassableNow(mc.X, mc.Y, BinGames.Sim.Nav.NavConst.ClassPlayer)}{(under != null ? " 压在 " + under.BuildingTypeId : string.Empty)} {o?.Kind}/{o?.State}{(string.IsNullOrEmpty(o?.FailureReason) ? string.Empty : "/" + o.FailureReason)}";
                    });
                    IEnumerable<string> around = new[] { 0, 1, 2, 3 }.Select(d => P.Step(gap, d)).Select(n => $"{D.Cell(n)}:{(NavService.PassableNow(n.X, n.Y, BinGames.Sim.Nav.NavConst.ClassPlayer) ? "可走" : "挡")}{(HomeGridService.BuildingAt(s, n) is BuildingRecord nb ? "/" + nb.BuildingTypeId : string.Empty)}");
                    IEnumerable<string> orders = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled)
                        .Select(o => $"{o.Kind}/{o.State}/{o.FailureReason}/{o.TargetId}").Take(6);
                    c.Log($"补管线：泵旁那格 {D.Cell(gap)} 建成 {pipe}（四邻 {string.Join(" ", around)}）；陷阱又铺 {lays} 轮、状态“{reason.Replace("\n", " / ")}”；机器 [{string.Join("；", ms)}]；工单 [{string.Join("；", orders)}]；废料 {s.Scrap}");
                }
                return StepOutcome.Wait;
            }
            return !reason.Contains("缺")
                ? StepOutcome.Done($"补上管线后水流到陷阱发射器：又铺了 {lays} 轮减速带；状态“{reason.Replace("\n", " / ")}”")
                : StepOutcome.Fail($"铺上了但状态还写缺：“{reason}”");
        }

        // ── 突袭（剧情入口排定之后全是正式流程）────────────────────────────────────────

        private static StepOutcome TickWarned(JourneyContext c, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, c.Get(key + ".plan"));
            if (p == null)
            {
                return StepOutcome.Fail("突袭计划不见了");
            }
            if (p.State < RaidDirectorService.StateWarned)
            {
                return StepOutcome.Wait;
            }
            RaidWarningHudUIToolkit hud = RaidWarningHudUIToolkit.Instance;
            hud?.Refresh(force: true);
            bool row = hud != null && hud.PanelVisible && hud.RowCount >= 1;
            if (!row)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"发了预警却没有预警条（{hud?.PanelVisible} / {hud?.RowCount}；建造模式开着 {FgjM3Common.BuildOpen}）");
            }
            Vector2 ap = hud.RowTarget(0);
            c.Set(key + ".apx", ap.x.ToString("R", CultureInfo.InvariantCulture));
            c.Set(key + ".apy", ap.y.ToString("R", CultureInfo.InvariantCulture));
            return StepOutcome.Done($"预警条“{hud.RowText(0).Replace("\n", " / ")}”（抵达点 {ap}）；{D.PlanLine(p)}");
        }

        private static StepOutcome TickSieging(JourneyContext c, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get(key + ".plan"));
            SiegeState st = SiegeService.StateOf(s);
            if (r == null || r.UnfoldTick < 0 || st == null || !st.TheaterActive || D.Raiders().Count == 0)
            {
                return StepOutcome.Wait;
            }
            return c.StepElapsed < 1 ? StepOutcome.Wait : StepOutcome.Done($"部队到达、展开攻城 {r.Unfolded} 台（剧场开，在场 {D.Raiders().Count} 台）");
        }

        // ── 2. 路径失败 ───────────────────────────────────────────────────────────────

        /// <summary>“放在这里后，机器将无法到达 {0}（仍可放置）”的固定前缀（按当前语言从文本键取，不写死中文）。</summary>
        private static string CutOffHead()
        {
            string full = GameLogic.Localization.GameText.Format("nav.build.unreachable_warning", "\u0001");
            int i = full.IndexOf('\u0001');
            return i > 0 ? full.Substring(0, i) : full;
        }

        /// <summary>建造栏选屏障 T2、光标指着闸门位：预览合法、带“机器将无法到达……”的警告（状态行里看得到），并且不点。</summary>
        private static StepOutcome TickSealWarning(JourneyContext c)
        {
            CampaignState s = St;
            GridCell gate = P.P(c, "gate");
            if (!FgjM3Common.BuildOpen)
            {
                return StepOutcome.Retry("建造模式没开");
            }
            if (!FgjM3Common.EntrySelected(D.Wall))
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "pickAt")) < 500)
                {
                    return StepOutcome.Wait;
                }
                c.SetLong(FgjM3Common.SK(c, "pickAt"), NowMs());
                return !FgjM3Common.PickEntry(D.Wall, out string why, out bool scrolling) && !scrolling ? StepOutcome.Retry("建造栏选不中屏障 T2：" + why) : StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "hover", () => FgjM3Common.TryHover(FgjM3Common.Ground(gate))))
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode bm = HomeValleyBuildMode.Current;
            GridPlacementResult pr = bm?.Preview;
            string head = CutOffHead();
            string warn = pr?.Warnings.FirstOrDefault(w => w.StartsWith(head, StringComparison.Ordinal));
            string status = BuildModeHudUIToolkit.Instance?.StatusLabelText ?? string.Empty;
            bool onCell = bm != null && bm.HoverCell == gate && pr != null && pr.TypeId == D.Wall;
            if (!onCell || warn == null || !status.Contains(warn))
            {
                if (FgjM3Common.SinceMs(c, "hover") < 2500)
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Retry($"指着闸门位 {D.Cell(gate)}（光标在 {D.Cell(bm?.HoverCell ?? default)}，预览 {pr?.TypeId} 合法 {pr?.Ok}）没有看到“机器将无法到达”的警告：状态行“{status.Replace("\n", " / ")}”");
            }
            bool placed = HomeGridService.BuildingAt(s, gate) != null;
            if (!pr.Ok || placed)
            {
                return StepOutcome.Fail($"预览应合法且不阻止（只警告），也不该放下：合法 {pr.Ok}、已放 {placed}");
            }
            c.Set("pf.warn", warn);
            return StepOutcome.Done($"屏障 T2 指着闸门位 {D.Cell(gate)}：预览合法（仍可放置），状态行另起一行警告“{warn}”——没点，改放闸门");
        }

        /// <summary>
        /// 闸门建成：从闸门里面一格出发，敌方类别在屏障圈的外接框里泛洪出不去（被圈死），己方类别走得出去（从闸门放行）。
        /// 用游戏里的实时可通行格（含建筑挡路位）泛洪，不走独立地形内核（那个不含建筑）。
        /// </summary>
        private static StepOutcome TickGateBuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            GridCell gate = P.P(c, "gate");
            if (!P.BuildingReady(s, "gate", gate, out _))
            {
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "bt", () => true) || FgjM3Common.SinceMs(c, "bt") < 600)
            {
                return StepOutcome.Wait; // 建成那一刻标脏，下一步开头寻路格才挡上：等半秒再泛洪
            }
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            int m = c.GetInt("walls.margin");
            GridCell inner = gate.X == cmin.X - m ? new GridCell(gate.X + 1, gate.Y)
                : gate.X == cmax.X + m ? new GridCell(gate.X - 1, gate.Y)
                : gate.Y == cmin.Y - m ? new GridCell(gate.X, gate.Y + 1)
                : new GridCell(gate.X, gate.Y - 1);
            bool hostileOut = Escapes(inner, cmin, cmax, m, BinGames.Sim.Nav.NavConst.ClassHostile, out int hostileCells);
            bool playerOut = Escapes(inner, cmin, cmax, m, BinGames.Sim.Nav.NavConst.ClassPlayer, out int playerCells);
            var cut = new List<string>();
            int cutGate = NavService.PlacementCutsOff(s, "gate", gate, 0, cut);
            if (hostileOut || !playerOut)
            {
                return c.StepElapsed < 30 ? StepOutcome.Wait
                    : StepOutcome.Fail($"闸门建成后：敌方从圈里走得出去 {hostileOut}（泛洪 {hostileCells} 格）、己方走得出去 {playerOut}（泛洪 {playerCells} 格）——应一个被圈死、一个放行");
            }
            return StepOutcome.Done($"闸门 {D.Cell(gate)} 建成：敌方寻路从圈里出不去（圈内泛洪 {hostileCells} 格就到头），自己的机器从闸门走得出去；闸门放置不会让任何建筑变得机器到不了（{cutGate} 座）；废料 {s.Scrap}");
        }

        /// <summary>从 <paramref name="start"/> 按实时可通行格四向泛洪（屏障圈外接框外扩 1 格为界），碰到圈外的格 = 走得出去。</summary>
        private static bool Escapes(GridCell start, GridCell cmin, GridCell cmax, int m, int cls, out int visited)
        {
            int x0 = cmin.X - m - 1, x1 = cmax.X + m + 1, y0 = cmin.Y - m - 1, y1 = cmax.Y + m + 1;
            var seen = new HashSet<long> { P.Key(start) };
            var q = new Queue<GridCell>();
            q.Enqueue(start);
            visited = 0;
            while (q.Count > 0)
            {
                GridCell g = q.Dequeue();
                visited++;
                if (g.X < cmin.X - m || g.X > cmax.X + m || g.Y < cmin.Y - m || g.Y > cmax.Y + m)
                {
                    return true;
                }
                for (int d = 0; d < 4; d++)
                {
                    GridCell n = P.Step(g, d);
                    if (n.X < x0 || n.X > x1 || n.Y < y0 || n.Y > y1 || !seen.Add(P.Key(n)) || !NavService.PassableNow(n.X, n.Y, cls))
                    {
                        continue;
                    }
                    q.Enqueue(n);
                }
            }
            return false;
        }

        private static string SiegeSnap(CampaignState s)
        {
            List<(int Id, Vector2 Pos)> raiders = D.Raiders();
            float hp = 0f;
            CombatSite site = D.HomeSite;
            foreach ((int id, Vector2 _) in raiders)
            {
                if (site != null && site.Kernel.TryGetUnit(id, out BinGames.Sim.Combat.CombatUnitView v))
                {
                    hp += v.Health;
                }
            }
            string pos = string.Join(";", raiders.OrderBy(x => x.Id).Select(x => $"{x.Id}@{x.Pos.x:F2},{x.Pos.y:F2}"));
            return $"步 {GameClock.Ticks}｜在场 {raiders.Count}｜总耐久 {hp:F1}｜陷阱 {DefenseService.StateOf(s)?.TotalLays}｜{pos}";
        }

        private static StepOutcome TickSiegeFrozen(JourneyContext c)
        {
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "s0"))
            {
                c.Set(FgjM3Common.SK(c, "snap"), SiegeSnap(s));
                FgjM3Common.Mark(c, "s0");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            string now = SiegeSnap(s);
            bool any = D.Raiders().Count > 0;
            return now == c.Get(FgjM3Common.SK(c, "snap")) && GameClock.Paused && any
                ? StepOutcome.Done($"暂停 {c.StepElapsed:F1} 真实秒：攻城一动不动（{now.Split('｜').Take(4).Aggregate((a, b) => a + "｜" + b)}）")
                : StepOutcome.Fail($"暂停中攻城变了（或没有在场的敌人 {any}）：\n      {c.Get(FgjM3Common.SK(c, "snap"))}\n      {now}");
        }

        /// <summary>陷阱按游戏时间每 trap.lay_seconds（1）秒铺一轮：在 <paramref name="speed"/> 倍速下数 6 游戏秒，按游戏秒每秒一轮、按真实秒是 0.5x 时的约 6 倍。</summary>
        private static StepOutcome TickLayRate(JourneyContext c, float speed, string key)
        {
            CampaignState s = St;
            DefenseRecord r = DefenseService.Find(s, c.Get("trap.id"));
            if (!FgjM3Common.Done(c, "t0"))
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                if (!Mathf.Approximately(GameClock.Speed, speed) || GameClock.Paused)
                {
                    return StepOutcome.Retry($"按倍速键后是 {GameClock.Speed}x（暂停 {GameClock.Paused}）");
                }
                c.SetLong(FgjM3Common.SK(c, "tick0"), GameClock.Ticks);
                c.SetLong(FgjM3Common.SK(c, "l0"), r?.TrapLays ?? 0);
                c.SetLong(FgjM3Common.SK(c, "real0"), NowMs());
                FgjM3Common.Mark(c, "t0");
                return StepOutcome.Wait;
            }
            long dTicks = GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "tick0"));
            if (dTicks < GameClock.StepHz * 6)
            {
                return StepOutcome.Wait;
            }
            double gameSec = dTicks / (double)GameClock.StepHz;
            double realSec = (NowMs() - c.GetLong(FgjM3Common.SK(c, "real0"))) / 1000.0;
            double rate = gameSec / Math.Max(1e-3, realSec);
            c.Set("rate." + key, rate.ToString("R", CultureInfo.InvariantCulture));
            string ratio = string.Empty;
            bool ratioOk = true;
            if (key == "triple" && double.TryParse(c.Get("rate.half"), NumberStyles.Float, CultureInfo.InvariantCulture, out double half) && half > 0)
            {
                double q = rate / half;
                ratioOk = q > 3.0;
                ratio = $"；世界走得是 0.5x 时的 {q:F1} 倍（理论 6 倍，帧节奏有抖动）";
            }
            bool rateOk = Math.Abs(rate - speed) <= speed * 0.35;
            // 陷阱还在（没被拆、有水）时，按游戏时间每 trap.lay_seconds 秒铺一轮，与倍速无关。
            long lays = (r?.TrapLays ?? 0) - c.GetLong(FgjM3Common.SK(c, "l0"));
            double perGame = lays / gameSec;
            double want = 1.0 / Math.Max(0.01, DefenseCatalog.TrapLaySeconds);
            BuildingRecord trapB = P.Bld(c, "trap");
            bool trapAlive = trapB != null && trapB.ConstructionState == BuildingConstructionState.Operational && lays > 0;
            bool layOk = !trapAlive || Math.Abs(perGame - want) <= want * 0.2 + 0.17;
            string layText = trapAlive ? $"陷阱铺 {lays} 轮 = 每游戏秒 {perGame:F2} 轮（表里每 {DefenseCatalog.TrapLaySeconds} 游戏秒一轮）" : "陷阱这时没在铺（被拆 / 断水），只看世界步";
            return rateOk && ratioOk && layOk
                ? StepOutcome.Done($"{speed}x：{gameSec:F1} 游戏秒用了 {realSec:F1} 真实秒（每真实秒 {rate:F2} 游戏秒）；{layText}{ratio}")
                : StepOutcome.Fail($"{speed}x 时不对：每真实秒 {rate:F2} 游戏秒（应约 {speed}）；{layText}{ratio}");
        }

        private static StepOutcome TickRaidOver(JourneyContext c, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (RaidResultService.IsCoreLost(s))
            {
                return StepOutcome.Fail("归还核心被摧毁");
            }
            string plan = c.Get(key + ".plan");
            RaidResultRecord r = RaidResultService.FindByPlan(s, plan);
            if (r == null || r.EndTick < 0)
            {
                return StepOutcome.Wait;
            }
            int results = RaidResultService.All(s).Count(x => x != null && x.PlanId == plan);
            c.SetInt(key + ".res", r.Serial);
            return results == 1
                ? StepOutcome.Done($"这一波结束：{D.ResultLine(r)}（只有 1 份结算）")
                : StepOutcome.Fail($"同一波结算了 {results} 次：{D.ResultLine(r)}");
        }

        /// <summary>这一波拆坏的建筑（优先屏障 / 闸门）：被摧毁 → 通用面板“重建”；只受损 → “修复”；一座都没坏就跳过。</summary>
        private static StepOutcome TickWallRestored(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string tk = FgjM3Common.SK(c, "target");
            int n = c.GetInt(FgjM3Common.SK(c, "n"));
            if (string.IsNullOrEmpty(c.Get(tk)))
            {
                // 这一波拆坏的屏障 / 闸门 / 圈里的建筑逐座修（屏障优先：圈上有洞，下一波直接走进来）。
                BuildingRecord pick = n >= 10 ? null : s.BuildingRecords
                    .Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore && !HomeValleyController.IsPlannedGhost(b) && OnOrInsideRing(c, b)
                                && (b.ConstructionState != BuildingConstructionState.Operational && b.ConstructionState != BuildingConstructionState.Disabled || BuildingOps.Durability(b) <= 0.5f))
                    .OrderBy(b => DefenseCatalog.IsDraggable(b.BuildingTypeId) ? 0 : 1).ThenBy(b => BuildingOps.Durability(b)).FirstOrDefault();
                // 只修被拆成废墟的（圈上的洞）；掉了耐久还立着的墙照样挡路，不在这一类里测。
                if (pick == null)
                {
                    if (ProductionPanelUIToolkit.IsOpen || FgjM3Common.BuildOpen)
                    {
                        if (NowMs() - c.GetLong(FgjM3Common.SK(c, "cl")) > 900)
                        {
                            c.SetLong(FgjM3Common.SK(c, "cl"), NowMs());
                            if (ProductionPanelUIToolkit.IsOpen)
                            {
                                JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose");
                            }
                            else
                            {
                                FgjM3Common.PressBuild(false);
                            }
                        }
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Done(n == 0 ? "这一波没有拆坏屏障 / 闸门 / 圈里的建筑，跳过修复" : $"这一波拆坏的 {n} 座都修好了（{c.Get(FgjM3Common.SK(c, "done"))}）；屏障圈又完整；废料 {s.Scrap}");
                }
                c.Set(tk, pick.BuildingId);
                c.Log($"修复目标 {n + 1}：{BuildingOps.NameOf(pick)} {D.Cell(new GridCell(pick.GridX, pick.GridY))}（{pick.ConstructionState}，耐久 {BuildingOps.Durability(pick):F0}/{BuildingOps.MaxDurability(pick.BuildingTypeId):F0}）");
            }
            BuildingRecord w = HomeGridService.FindBuilding(s, c.Get(tk));
            if (w == null)
            {
                return StepOutcome.Fail("要修的那座建筑连虚影都没了");
            }
            bool full = w.ConstructionState == BuildingConstructionState.Operational && BuildingOps.Durability(w) > 0.5f;
            if (full)
            {
                c.Set(FgjM3Common.SK(c, "done"), (string.IsNullOrEmpty(c.Get(FgjM3Common.SK(c, "done"))) ? string.Empty : c.Get(FgjM3Common.SK(c, "done")) + "、") + $"{BuildingOps.NameOf(w)} {D.Cell(new GridCell(w.GridX, w.GridY))}");
                c.SetInt(FgjM3Common.SK(c, "n"), n + 1);
                c.Set(tk, string.Empty);
                return StepOutcome.Wait;
            }
            WorkOrderRecord order = HomeValleyWorkOrders.FindActiveRepair(s, w.BuildingId);
            if (order != null)
            {
                if (ProductionPanelUIToolkit.IsOpen && NowMs() - c.GetLong(FgjM3Common.SK(c, "cl")) > 900)
                {
                    c.SetLong(FgjM3Common.SK(c, "cl"), NowMs());
                    JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose");
                }
                return StepOutcome.Wait; // 机器取料修 / 重建
            }
            string k = n.ToString(CultureInfo.InvariantCulture);
            if (!(ProductionPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.BuildingId == w.BuildingId))
            {
                StepOutcome o = P.TickOpenPanel(c, w.BuildingTypeId, new GridCell(w.GridX, w.GridY), "wall" + k);
                return o.Status == JourneyStepStatus.Retry ? o : StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "rep" + k, () => FgjM3Common.ClickPanelBodyButton(P.PanelHost, "ProductionPanelBody", "BpRepair")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "rep" + k) < 2500 ? StepOutcome.Wait : StepOutcome.Retry($"点“重建 / 修复”后没有工单：{JourneyInput.LastUiFailure}");
        }

        /// <summary>建筑在屏障圈上或圈里（机器从闸门进出就够得着；圈外被拆的留给玩家以后处理，不在这一类里测）。</summary>
        private static bool OnOrInsideRing(JourneyContext c, BuildingRecord b)
        {
            HomeGridService.TryGetCoreBounds(St, out GridCell cmin, out GridCell cmax);
            int m = c.GetInt("walls.margin");
            return b.GridX >= cmin.X - m && b.GridX <= cmax.X + m && b.GridY >= cmin.Y - m && b.GridY <= cmax.Y + m;
        }

        // ── 4. 存读档 ─────────────────────────────────────────────────────────────────

        private static string SaveDigest(CampaignState s) =>
            SiegeService.Snapshot(s) + "‖" + RaidResultService.Snapshot(s) + "‖" + RaidDirectorService.Snapshot(s);

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
            CampaignState s = St;
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get("rb.plan"));
            c.Set("pre", SaveDigest(s));
            c.SetLong("preTicks", GameClock.Ticks);
            c.SetInt("pre.alive", D.Raiders().Count);
            c.SetInt("pre.killed", r?.Killed ?? 0);
            c.SetInt("pre.exited", r?.Exited ?? 0);
            return StepOutcome.Done($"暂停菜单打开（世界暂停，攻城中在场 {D.Raiders().Count} 台、已击毁 {r?.Killed}）");
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
            string got = SaveDigest(onDisk.State);
            return got == c.Get("pre")
                ? StepOutcome.Done($"回到主菜单；存档里攻城 / 结算 / 突袭导演与存档那一刻逐项一致（{got.Length} 字）")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {got}");
        }

        private static StepOutcome TickLoadedSiege(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get("rb.plan"));
            SiegeState st = SiegeService.StateOf(s);
            long dTicks = GameClock.Ticks - c.GetLong("preTicks");
            int alive = D.Raiders().Count;
            int dk = (r?.Killed ?? 0) - c.GetInt("pre.killed");
            int de = (r?.Exited ?? 0) - c.GetInt("pre.exited");
            bool ok = r != null && st != null && st.TheaterActive && dTicks >= 0 && dTicks <= GameClock.StepHz * 5 && alive + dk + de == c.GetInt("pre.alive");
            if (!ok && c.StepElapsed < 3)
            {
                return StepOutcome.Wait;
            }
            return ok
                ? StepOutcome.Done($"读档进入家园（已走 {dTicks} 步）：攻城接着打——同一个剧场、同一份结算（第 {r.Serial} 份）；在场 {alive} + 新击毁 {dk} + 新离场 {de} = 存档时在场 {c.GetInt("pre.alive")}")
                : StepOutcome.Fail($"读档后攻城不对：剧场 {st?.TheaterActive}、结算 {r?.Serial}、已走 {dTicks} 步、在场 {alive} + 击毁 {dk} + 离场 {de} vs 存档时 {c.GetInt("pre.alive")}");
        }

        // ── 5. 断链 / 6. 目标死亡 ─────────────────────────────────────────────────────

        private static StepOutcome TickToggle(JourneyContext c, string type, GridCell pivot, bool disable)
        {
            BuildingRecord b = P.BuildingAtPivot(St, type, pivot);
            if (b == null)
            {
                return StepOutcome.Fail($"{D.Cell(pivot)} 没有{D.Name(type)}");
            }
            if (BuildingOps.IsDisabled(b) == disable)
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                if (!disable && P.Bld(c, "t1") is BuildingRecord t1 && t1.PowerState == BuildingPowerState.Powered)
                {
                    c.SetInt("pw.seen", 1); // 来电那一刻炮塔有电了（之后突袭可能又拆掉发电机，那是另一回事）
                }
                return StepOutcome.Done($"{D.Name(type)} {D.Cell(pivot)} 已{(disable ? "禁用" : "启用")}（{b.ConstructionState}；{P.PowerLine(St)}）");
            }
            if (!FgjM3Common.Once(c, "tg", () => FgjM3Common.ClickPanelBodyButton(P.PanelHost, "ProductionPanelBody", "BpEnable")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "tg") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点“{(disable ? "禁用" : "启用")}”没生效：" + JourneyInput.LastUiFailure);
        }

        private static StepOutcome TickUplinkedDies(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int a = c.GetInt("victimA");
            bool alive = MachineRegistry.TryGetRecord(a, out MachineRecord m) && m.IsAlive;
            if (!alive)
            {
                JourneyInput.ReleaseKeys();
                if (c.GetInt(FgjM3Common.SK(c, "up")) == 0)
                {
                    // 接入之前就被打死了（部队一展开就集火）：换第二台当接入的那台，接着来。
                    if (VictimBAlive(c))
                    {
                        c.Log($"{FgjM1Journey.Label(a)} 接入之前就被打死了，换 {FgjM1Journey.Label(c.GetInt("victimB"))} 来接入");
                        c.SetInt("victimA", c.GetInt("victimB"));
                        c.SetInt("victimB", 0);
                        c.SetInt("aEarly", a);
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Fail($"{FgjM1Journey.Label(a)} 接入之前就被打死了，也没有第二台可以接入");
                }
                int now = SignalPresence.CurrentMachineLogicId;
                bool bounced = now > 0 && now != a && MachineRegistry.TryGetRecord(now, out MachineRecord nb) && nb.IsAlive;
                string fb = SignalUplinkService.LastFeedbackText ?? string.Empty;
                if (c.StepElapsed < 1.5 || !(SignalPresence.AtCore || bounced) || !fb.Contains("阵亡"))
                {
                    return FgjM3Common.SinceMs(c, "deadAt") is long since && since > 20000 ? StepOutcome.Fail($"接入的机器阵亡后信号既没回到归还核心、也没弹到另一台机器（“{fb}”）")
                        : FgjM3Common.Once(c, "deadAt", () => true) ? StepOutcome.Wait : StepOutcome.Wait;
                }
                c.SetInt("bOnline", bounced && now == c.GetInt("victimB") ? 1 : 0);
                bool box = BlackBoxService.Find(s, a) != null;
                return box
                    ? StepOutcome.Done($"{FgjM1Journey.Label(a)} 被突袭部队打死：信号{(bounced ? "弹到最近能接入的 " + FgjM1Journey.Label(now) : "回到归还核心")}（“{fb}”）；黑匣子回收进陈列馆队列" +
                                       (c.GetInt("aEarly") > 0 ? $"（{FgjM1Journey.Label(c.GetInt("aEarly"))} 在接入之前已被打死）" : string.Empty) + $"；第三波共 {(c.Get("rc.plans") ?? string.Empty).Split(',').Length} 波")
                    : StepOutcome.Fail($"{FgjM1Journey.Label(a)} 阵亡了但黑匣子没回收");
            }
            List<(int Id, Vector2 Pos)> raiders = D.Raiders();
            bool hasMarker = FgjM1Journey.HomeMarker(a, out HomeValleyMachineMarker mk);
            bool up = SignalPresence.CurrentMachineLogicId == a;
            c.SetInt(FgjM3Common.SK(c, "up"), up ? 1 : 0);
            int tick = (int)(c.StepElapsed / 20);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                Vector2 me0 = hasMarker ? mk.Position : Vector2.zero;
                string near = raiders.Count == 0 ? "没有" : $"{raiders.OrderBy(x => Vector2.Distance(x.Pos, me0)).First().Pos} 距 {raiders.Min(x => Vector2.Distance(x.Pos, me0)):F1}";
                RaidResultRecord rr = RaidResultService.FindByPlan(s, c.Get("rc.plan"));
                c.Log($"冲进敌群：{FgjM1Journey.Label(a)} 在 {me0}（耐久 {m?.Health:F0}，{(up ? "接入中" : "没接入")}）；在场敌人 {raiders.Count}，最近 {near}；这一波 {(rr == null ? "未开始" : rr.EndTick >= 0 ? "已结束" : "进行中")}；{GameClock.Speed}x");
            }
            if (!hasMarker || GameClock.Paused)
            {
                return StepOutcome.Wait; // 暂停着：等暂停键那一下生效，别用别的按键把它盖掉
            }
            if (raiders.Count > 0)
            {
                if (!up)
                {
                    // 部队到了：左键点它、按接入键（玩家的做法）。
                    if (GameRoot.HomeValley.SelectedMachineLogicId != a)
                    {
                        if (NowMs() - c.GetLong(FgjM3Common.SK(c, "selAt")) > 1500)
                        {
                            c.SetLong(FgjM3Common.SK(c, "selAt"), NowMs());
                            FgjM1Journey.ClickMachine(c, "victimA");
                        }
                        return StepOutcome.Wait;
                    }
                    if (NowMs() - c.GetLong(FgjM3Common.SK(c, "keyAt")) > 2000)
                    {
                        c.SetLong(FgjM3Common.SK(c, "keyAt"), NowMs());
                        JourneyInput.PressAction(GameActionId.ToggleCameraView);
                    }
                    return StepOutcome.Wait;
                }
                if (!JourneyInput.Holding)
                {
                    Vector2 me = mk.Position;
                    Vector2 goal = raiders.OrderBy(x => Vector2.Distance(x.Pos, me)).First().Pos;
                    FgjM1Journey.DriveToward(me, ThroughGate(c, me, goal), 0.5);
                }
                return StepOutcome.Wait;
            }
            // 没有敌人在场：接入着就先退出（直控锁 1x，等下一波太慢），这一波打完它还活着就再请一波。
            if (up)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "leaveAt")) > 2000)
                {
                    c.SetLong(FgjM3Common.SK(c, "leaveAt"), NowMs());
                    JourneyInput.ReleaseKeys();
                    JourneyInput.PressAction(GameActionId.ToggleCameraView);
                }
                return StepOutcome.Wait;
            }
            CallAnotherWaveIfNeeded(c);
            return StepOutcome.Wait;
        }

        /// <summary>直控开车时屏障圈挡在中间：机器在圈里、敌人在圈外（或反过来）就先开到闸门里侧一格、再到外侧一格，再朝敌人开（玩家也得从闸门出去）。</summary>
        private static Vector2 ThroughGate(JourneyContext c, Vector2 me, Vector2 goal)
        {
            HomeGridService.TryGetCoreBounds(St, out GridCell cmin, out GridCell cmax);
            int m = c.GetInt("walls.margin");
            bool Inside(Vector2 p) => p.x > cmin.X - m + 0.5f && p.x < cmax.X + m - 0.5f && p.y > cmin.Y - m + 0.5f && p.y < cmax.Y + m - 0.5f;
            if (Inside(me) == Inside(goal))
            {
                return goal;
            }
            GridCell gate = P.P(c, "gate");
            var dir = gate.X == cmin.X - m ? new Vector2(1f, 0f) : gate.X == cmax.X + m ? new Vector2(-1f, 0f) : gate.Y == cmin.Y - m ? new Vector2(0f, 1f) : new Vector2(0f, -1f);
            var g = new Vector2(gate.X, gate.Y);
            Vector2 near = Inside(me) ? g + dir : g - dir; // 自己这一侧的闸门口
            Vector2 far = Inside(me) ? g - dir * 1.5f : g + dir * 1.5f;
            // 还没对准闸门口就先开到门口；对准了（离门口近、或已在门里）就穿过去。
            return Vector2.Distance(me, near) > 0.8f && Vector2.Distance(me, g) > 0.7f ? near : far;
        }

        private static bool VictimBAlive(JourneyContext c) =>
            c.GetInt("victimB") > 0 && MachineRegistry.TryGetRecord(c.GetInt("victimB"), out MachineRecord m) && m.IsAlive;

        private static Vector2 ArrivalOf(JourneyContext c)
        {
            RaidPlanRecord rp = RaidDirectorService.FindPlan(St, c.Get("rc.plan"));
            return rp != null ? new Vector2((float)rp.ArriveX, (float)rp.ArriveY) : D.CoreCenter(St);
        }

        private static void RightClickArrival(JourneyContext c, string key)
        {
            if (c.GetInt(key) <= 0)
            {
                return;
            }
            Vector2 at = ArrivalOf(c);
            if (!JourneyInput.OnScreen(at, 0.05f))
            {
                FgjM3Common.PanTo(at);
                JourneyInput.DeferWorldClick(() => RightClickArrival(c, key));
                return;
            }
            JourneyInput.Click(at, 1);
        }

        private static StepOutcome TickSentToArrival(JourneyContext c, string key)
        {
            int b = c.GetInt(key);
            if (b <= 0)
            {
                return StepOutcome.Done("只剩一台机器，跳过");
            }
            if (!JourneyInput.WorldClickSettled(c.StepElapsed, 0.6))
            {
                return StepOutcome.Wait;
            }
            CombatSite site = D.HomeSite;
            bool moving = site != null && site.TryGetMachineUnit(b, out int unit) && site.Kernel.TryGetNavState(unit, out BinGames.Sim.Combat.CombatNavState ns, out _)
                          && (ns == BinGames.Sim.Combat.CombatNavState.Following || ns == BinGames.Sim.Combat.CombatNavState.Awaiting || ns == BinGames.Sim.Combat.CombatNavState.NeedRoute);
            Vector2 at = FgjM1Journey.HomeMarker(b, out HomeValleyMachineMarker mk) ? mk.Position : Vector2.zero;
            float d = Vector2.Distance(at, ArrivalOf(c));
            if (!moving && d > 3f)
            {
                if (c.StepElapsed < 5)
                {
                    return StepOutcome.Wait;
                }
                if (c.Attempt < 2)
                {
                    return StepOutcome.Retry($"右键抵达点后 {FgjM1Journey.Label(b)} 没有动身");
                }
                // 提前去迎敌只是让部队一到就打得到它；没动身也不影响后面（右键敌人进攻时它会自己过去）。
                WorkOrderRecord o = HomeValleyWorkOrders.FindActiveOrderForMachine(St, b);
                return StepOutcome.Done($"{FgjM1Journey.Label(b)} 没有动身（在 {at}，离抵达点 {d:F1} 米，在办 {o?.Kind}/{o?.State}）——后面右键敌人进攻时再过去");
            }
            return StepOutcome.Done(d <= 3f ? $"{FgjM1Journey.Label(b)} 已经在抵达点附近（{d:F1} 米）" : $"{FgjM1Journey.Label(b)} 动身去抵达点 {ArrivalOf(c)}（从闸门出圈）");
        }

        private static List<int> Victims(JourneyContext c) =>
            (c.Get("victims") ?? string.Empty).Split(',').Where(x => !string.IsNullOrEmpty(x)).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();

        private static bool MachineAlive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.IsAlive;

        /// <summary>
        /// 第三波开始时还活着的家园机器一台台打死：信号弹到哪台就直控它开进敌群，否则选中第一台还活着的、右键敌人进攻；这一波打完还有活着的就再请一波。
        /// 全部阵亡 → 家园机器全灭 → 应急打印出一台新的（编号比它们都大），通知照常。
        /// </summary>
        private static StepOutcome TickWipedAndPrinted(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            List<int> victims = Victims(c);
            List<int> alive = victims.Where(MachineAlive).ToList();
            if (alive.Count > 0)
            {
                if (GameClock.Paused || CallAnotherWaveIfNeeded(c))
                {
                    return StepOutcome.Wait;
                }
                List<(int Id, Vector2 Pos)> rs = D.Raiders();
                int up = SignalPresence.CurrentMachineLogicId;
                if (alive.Contains(up))
                {
                    if (rs.Count > 0 && !JourneyInput.Holding && FgjM1Journey.HomeMarker(up, out HomeValleyMachineMarker bm))
                    {
                        Vector2 me = bm.Position;
                        FgjM1Journey.DriveToward(me, ThroughGate(c, me, rs.OrderBy(x => Vector2.Distance(x.Pos, me)).First().Pos), 0.5);
                    }
                    return StepOutcome.Wait;
                }
                if (rs.Count == 0)
                {
                    return StepOutcome.Wait;
                }
                int v = alive[0];
                c.SetInt("victimCur", v);
                if (GameRoot.HomeValley.SelectedMachineLogicId != v)
                {
                    if (NowMs() - c.GetLong(FgjM3Common.SK(c, "selAt")) > 1500)
                    {
                        c.SetLong(FgjM3Common.SK(c, "selAt"), NowMs());
                        FgjM1Journey.ClickMachine(c, "victimCur");
                    }
                    return StepOutcome.Wait;
                }
                if (!FgjM2Common.IsAttacking(D.HomeSite, v) && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 4000)
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    RightClickRaiderFor(v);
                }
                return StepOutcome.Wait;
            }
            int max = victims.Count > 0 ? victims.Max() : 0;
            MachineRecord printed = MachineRegistry.AllRecords.Where(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId && x.LogicId > max).OrderByDescending(x => x.LogicId).FirstOrDefault();
            bool note = NotificationCenter.History.Any(e => e.Type?.Id != null && e.Type.Id.Contains("emergency"));
            if (printed == null || !note)
            {
                return c.StepElapsed < 380 ? StepOutcome.Wait : StepOutcome.Fail($"家园机器全灭后没有应急打印（家园机器 {SoftlockService.CountHomeMachines()} 台，应急打印通知 {note}）");
            }
            int boxes = victims.Count(id => BlackBoxService.Find(s, id) != null);
            return StepOutcome.Done($"{string.Join("、", victims.Select(FgjM1Journey.Label))} 都被突袭部队打死、家园机器全灭 → 应急打印出 {FgjM1Journey.Label(printed.LogicId)}（{printed.ChassisId}，应急打印通知）；黑匣子回收 {boxes}/{victims.Count}；第三波共 {(c.Get("rc.plans") ?? string.Empty).Split(',').Length} 波");
        }

        private static void RightClickRaiderFor(int id)
        {
            List<(int Id, Vector2 Pos)> raiders = D.Raiders();
            if (raiders.Count == 0 || !FgjM1Journey.HomeMarker(id, out HomeValleyMachineMarker mk))
            {
                return;
            }
            Vector2 at = raiders.OrderBy(x => Vector2.Distance(x.Pos, mk.Position)).First().Pos;
            if (!JourneyInput.OnScreen(at, 0.05f))
            {
                FgjM3Common.PanTo(at);
                JourneyInput.DeferWorldClick(() => RightClickRaiderFor(id));
                return;
            }
            JourneyInput.Click(at, 1);
        }

        private static StepOutcome TickPowerBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord t = P.Bld(c, "t1");
            if (t != null && t.PowerState == BuildingPowerState.Powered)
            {
                c.SetInt("pw.seen", 1);
            }
            if (t == null || c.GetInt("pw.seen") == 0)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"启用发电机后炮塔一直没电：{t?.PowerState}；{P.PowerLine(s)}");
            }
            if (RaidResultService.IsCoreLost(s))
            {
                return StepOutcome.Fail("归还核心被摧毁");
            }
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get("rc.plan"));
            if (r == null || r.EndTick < 0)
            {
                return StepOutcome.Wait;
            }
            int turretKills = r.Contrib.Where(x => x.Kind == "turret").Sum(x => x.Kills);
            int victims = Math.Max(1, Victims(c).Count);
            int lost = RcResults(c).Sum(x => x.LostMachines);
            // 来电后炮塔恢复（有电、状态不再写缺电）；这一波剩下的时间（攻城一个游戏小时）里打没打到人看来电时敌人还剩多少，只记录不硬性要求。
            string reason = BuildingStatusService.Evaluate(s, t).Reason ?? string.Empty;
            string gens = string.Join("、", St.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator2)).Select(b => $"{BuildingOps.NameOf(b)} {b.ConstructionState}"));
            return lost >= victims
                ? StepOutcome.Done($"启用发电机那一刻炮塔来电恢复（现在 {t.PowerState}，状态“{reason.Replace("\n", " / ")}”；发电机 [{gens}]；这一波炮塔击毁 {turretKills} 台）：{D.ResultLine(r)}")
                : StepOutcome.Fail($"这一波结算不对（应记 {victims} 台机器损失、炮塔来电恢复）：炮塔 {t.PowerState}“{reason}”；{D.ResultLine(r)}");
        }

        // ── 承接 DEBT-FG5E2E01-05：真实突袭打坏熔合中的合成台 → 回滚 ─────────────────────────────

        private const string FwPartner = "fw_nitrogen";

        private const string NodeLabT2 = "industry.lab_t2";

        private static bool FusionOpen() => FusionPanelUIToolkit.IsOpen && FusionPanelUIToolkit.Instance != null && FusionPanelUIToolkit.Instance.PanelVisible;

        /// <summary>进度夹具（DEBT-FG5E2E01-02，与 FGJ-M5 同一个夹具）：芯片基板放进仓库——基板的 M4 产线由 FGJ-M4 验证；之后的正式熔合、被毁回滚全走正式流程。</summary>
        private static void ApplySubstrateFixture(JourneyContext c) => M.ApplySubstrateFixture(c);

        private static StepOutcome TickRestored(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            bool ok = !UiConfirmDialog.IsOpen && MechanicalContentUnlock.IsUnlocked(s, fwId) && SignalCoreService.PrintableFirmware(s).Contains(fwId);
            return ok ? StepOutcome.Done($"复原“{M.FwName(fwId)}”：解锁、可刻印（复原费 {FirmwareRestoreService.CostOf(fwId)} 技术数据）")
                : (c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"复原“{M.FwName(fwId)}”后没有解锁"));
        }

        private static StepOutcome TickSpeedIs(JourneyContext c, float speed)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return Mathf.Approximately(GameClock.Speed, speed) ? StepOutcome.Done($"{speed}x") : StepOutcome.Retry($"按倍速键后是 {GameClock.Speed}x");
        }

        /// <summary>合成台放在第一波预警条上的抵达点旁边（屏障圈外、核心配电范围里）：同一阵营的剧情突袭从同一方向来，玩家看过第一波就知道敌人在哪儿展开。</summary>
        private static StepOutcome TickSynthPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            if (string.IsNullOrEmpty(c.Get("ra.apx")))
            {
                return StepOutcome.Fail("第一波预警时没记下抵达点");
            }
            var at = new Vector2(float.Parse(c.Get("ra.apx"), CultureInfo.InvariantCulture), float.Parse(c.Get("ra.apy"), CultureInfo.InvariantCulture));
            GridCell? pivot = D.PlanSynthNear(s, M.Synth, at, c.GetInt("walls.margin"), P.P(c, "gate"), out string why);
            if (!pivot.HasValue)
            {
                return StepOutcome.Fail("合成台规划不出来：" + why);
            }
            var synth = new P.Placed { Key = "synth", Type = M.Synth, Pivot = pivot.Value, Rot = 0, Label = D.Name(M.Synth) };
            P.Remember(c, synth);
            P.SetActions(c, "fusyn", new[] { P.ActB(synth) });
            Vector2 center = new Vector2(pivot.Value.X + 1f, pivot.Value.Y + 1f);
            return StepOutcome.Done($"第一波抵达点 {at}：电路合成台放在 {D.Cell(pivot.Value)}（屏障圈外，离抵达点约 {Vector2.Distance(center, at):F1} 米，核心配电范围里）；废料 {s.Scrap}");
        }

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
            return hit && p.FormalButton.enabledSelf
                ? StepOutcome.Done($"模拟熔合：“{p.ResultText.Replace("\n", " / ")}”（{GameText.Get(def.NameKey)}）")
                : StepOutcome.Fail($"模拟没有出配方：“{p.ResultText}”");
        }

        private static TransitGroupRecord RaidGroup(string planId) =>
            GameLogic.Campaign.WorldSim.WorldTransitSystem.Groups(St).FirstOrDefault(g => g != null && g.PlanId == planId);

        /// <summary>部队离抵达只剩 20 游戏秒以内（预警条倒计时；或已经到了）：这时排两项正式熔合（各 30 游戏秒），部队到达展开时第二项一定还在熔合。</summary>
        private static StepOutcome TickRaidClose(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!FusionOpen())
            {
                return StepOutcome.Retry("合成台面板被关掉了");
            }
            TransitGroupRecord g = RaidGroup(c.Get("rs.plan"));
            RaidResultRecord r = RaidResultService.FindByPlan(St, c.Get("rs.plan"));
            bool arrived = r != null && r.UnfoldTick >= 0 || g != null && g.State != TransitGroupState.Marching;
            double eta = g != null ? GameLogic.Campaign.WorldSim.WorldTransitSystem.EtaSeconds(g) : 0;
            if (!arrived && (g == null || eta > 20))
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done(arrived ? "部队已经到达" : $"部队离抵达还有 {eta:F0} 游戏秒");
        }

        private static int SubstrateTotal()
        {
            ItemDistribution.Invalidate();
            return (int)ItemDistribution.Get(St, ItemCatalog.Find(M.Substrate)).Total;
        }

        /// <summary>点“正式熔合…”、确认框“确认”：第 <paramref name="n"/> 项入队。第一项入队之前记下守恒的基准（芯片基板总量、熔合的技术数据支出、两种父固件的芯片数、通知条数）。</summary>
        private static StepOutcome TickFormalQueued(JourneyContext c, int n)
        {
            FusionPanelUIToolkit p = M.Fusion();
            string synthId = P.Bld(c, "synth")?.BuildingId;
            if (!FgjM3Common.Done(c, "formal"))
            {
                if (n == 1)
                {
                    c.SetInt("fu.sub0", SubstrateTotal());
                    c.SetLong("fu.exp0", TechDataFlow.ExpenseOf(St, TechDataFlow.Fusion));
                    c.SetInt("fu.a0", M.ChipCount(FirmwareCatalog.FwOverloadId));
                    c.SetInt("fu.b0", M.ChipCount(FwPartner));
                    c.SetInt("fu.notes0", NotificationCenter.History.Count);
                }
                c.SetInt(FgjM3Common.SK(c, "jobs0"), FusionService.JobsOf(St, synthId).Count());
                if (p == null || !JourneyInput.ClickElement(p.FormalButton))
                {
                    return M.UiRetry("点不到“正式熔合…”：");
                }
                FgjM3Common.Mark(c, "formal");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "ok"))
            {
                if (c.StepElapsed < 0.4)
                {
                    return StepOutcome.Wait;
                }
                if (!UiConfirmDialog.IsOpen)
                {
                    return StepOutcome.Retry("点“正式熔合…”后没有弹出确认框：" + p?.MessageText);
                }
                if (!JourneyInput.ClickUitk(M.OverlayHost, "ConfirmOk"))
                {
                    return M.UiRetry("确认框点“确认”失败：");
                }
                FgjM3Common.Mark(c, "ok");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FusionJobRecord job = FusionService.JobsOf(St, synthId).OrderByDescending(j => j.Serial).FirstOrDefault();
            if (job == null || FusionService.JobsOf(St, synthId).Count() <= c.GetInt(FgjM3Common.SK(c, "jobs0")))
            {
                return StepOutcome.Retry("确认后队列里没有这项熔合：" + p?.MessageText);
            }
            c.Set("fu.job" + n.ToString(CultureInfo.InvariantCulture), job.JobId);
            int reserved = St.PrimitiveChips?.Count(x => x != null && x.ReservedByTransactionId == job.JobId) ?? 0;
            return reserved == 2
                ? StepOutcome.Done($"第 {n} 项入队：{FusionService.StateText(job)}（{job.Duration:0.#} 秒）；两枚父固件被这项预留、芯片基板 {job.Substrate} 件与技术数据 {job.Tech} 记在任务上")
                : StepOutcome.Fail($"第 {n} 项入队后预留了 {reserved} 枚");
        }

        /// <summary>
        /// 真实攻城单位拆掉正在熔合的合成台（FGR-RND-04x 被毁回滚，FG05 负向“正式熔合途中被突袭打断”）：每一项进行中的熔合回滚（原因“被毁”），
        /// 父固件预留释放、芯片基板与技术数据退回（已经熔合完的那一项照常消耗）；通知“熔合中止”；这一波的突袭结算把合成台记为损失。
        /// </summary>
        private static StepOutcome TickRaidRolledBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            BuildingRecord b = P.Bld(c, "synth");
            if (b == null)
            {
                return StepOutcome.Fail("电路合成台连废墟都不见了");
            }
            bool destroyed = (b.ConstructionState == BuildingConstructionState.Damaged || b.ConstructionState == BuildingConstructionState.Destroyed);
            var jobs = new[] { c.Get("fu.job1"), c.Get("fu.job2") }.Select(id => FusionService.StateOf(s)?.Jobs?.FirstOrDefault(j => j != null && j.JobId == id)).ToList();
            if (jobs.Any(j => j == null))
            {
                return StepOutcome.Fail("熔合任务不见了");
            }
            if (!destroyed)
            {
                if (jobs.All(j => j.State == FusionJobState.Done))
                {
                    return StepOutcome.Fail($"两项熔合都做完了合成台还没被拆（{BuildingOps.Durability(b):F0}/{BuildingOps.MaxDurability(b?.BuildingTypeId):F0}）：攻城单位没去打合成台；{D.ResultLine(RaidResultService.FindByPlan(s, c.Get("rs.plan")))}");
                }
                int tick = (int)(c.StepElapsed / 10);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"合成台耐久 {BuildingOps.Durability(b):F0}/{BuildingOps.MaxDurability(b?.BuildingTypeId):F0}；熔合 [{string.Join("；", jobs.Select(FusionService.StateText))}]；在场敌人 {D.Raiders().Count}");
                }
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1 || jobs.Any(j => j.State != FusionJobState.RolledBack && j.State != FusionJobState.Done))
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"合成台被毁后熔合没回滚：[{string.Join("；", jobs.Select(j => j.State + "/" + j.Reason))}]");
            }
            List<FusionJobRecord> rolled = jobs.Where(j => j.State == FusionJobState.RolledBack).ToList();
            List<FusionJobRecord> done = jobs.Where(j => j.State == FusionJobState.Done).ToList();
            bool reasons = rolled.Count > 0 && rolled.All(j => j.Reason == "destroyed");
            bool chips = rolled.All(j => PrimitiveInventory.Find(s, j.PartA) is PrimitiveChipRecord ca && string.IsNullOrEmpty(ca.ReservedByTransactionId)
                                         && PrimitiveInventory.Find(s, j.PartB) is PrimitiveChipRecord cb && string.IsNullOrEmpty(cb.ReservedByTransactionId))
                         && M.ChipCount(FirmwareCatalog.FwOverloadId) == c.GetInt("fu.a0") - done.Count && M.ChipCount(FwPartner) == c.GetInt("fu.b0") - done.Count;
            int sub = SubstrateTotal();
            bool subOk = sub == c.GetInt("fu.sub0") - done.Sum(j => j.Substrate);
            long exp = TechDataFlow.ExpenseOf(s, TechDataFlow.Fusion);
            bool techOk = exp == c.GetLong("fu.exp0") + done.Sum(j => j.Tech);
            bool note = NotificationCenter.History.Skip(c.GetInt("fu.notes0")).Any(e => e.Type?.Id == "fusion_rolled_back");
            RaidResultRecord rr = RaidResultService.FindByPlan(s, c.Get("rs.plan"));
            bool loss = rr != null && rr.Losses.Any(x => x.Id == b.BuildingId);
            if (!loss && c.StepElapsed < 20)
            {
                return StepOutcome.Wait; // 结算的损失表随攻城对账（siege.sync_seconds）写进来
            }
            return reasons && chips && subOk && techOk && note && loss
                ? StepOutcome.Done($"突袭部队拆掉了正在熔合的合成台（{b.ConstructionState}）：回滚 {rolled.Count} 项（{string.Join("、", rolled.Select(j => FusionService.StateText(j)))}，原因“被毁”）、完成 {done.Count} 项；" +
                                   $"父固件预留释放（过载 {M.ChipCount(FirmwareCatalog.FwOverloadId)}、液氮 {M.ChipCount(FwPartner)} 枚）；芯片基板总量 {c.GetInt("fu.sub0")} → {sub}、熔合的技术数据支出 {c.GetLong("fu.exp0")} → {exp}（回滚的全额退回）；" +
                                   $"通知“熔合中止”；突袭结算记下合成台这处损失（{D.ResultLine(rr)}）")
                : StepOutcome.Fail($"回滚不完整：原因 {reasons}、固件 {chips}、芯片基板 {subOk}（{c.GetInt("fu.sub0")} → {sub}）、技术数据 {techOk}（{c.GetLong("fu.exp0")} → {exp}）、通知 {note}、结算损失 {loss}；[{string.Join("；", jobs.Select(j => j.State + "/" + j.Reason))}]");
        }

        // ── 断链：数着圈里两座炮塔的开火次数（内核里炮塔下一发的时刻往后跳一次 = 开了一发）────────────────

        private static bool TurretView(JourneyContext c, string key, out BinGames.Sim.Combat.CombatUnitView v)
        {
            v = default;
            BuildingRecord b = P.Bld(c, key);
            TurretRecord t = b != null ? TurretService.Find(St, b.BuildingId) : null;
            CombatSite site = D.HomeSite;
            return t != null && site != null && site.TryGetTurretUnit(t.Serial, out int unit) && site.Kernel.TryGetUnit(unit, out v);
        }

        /// <summary>每帧调：两座炮塔各自“下一发的时刻”往后跳了几次（= 开了几发），累计在旅程变量里；返回两座合计。</summary>
        private static int SampleFire(JourneyContext c)
        {
            int total = 0;
            foreach (string k in new[] { "t1", "t2" })
            {
                string key = "fire." + k;
                if (TurretView(c, k, out BinGames.Sim.Combat.CombatUnitView v))
                {
                    string last = c.Get(key + ".at");
                    if (!string.IsNullOrEmpty(last) && double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out double prev) && v.NextFireAt > prev + 1e-6)
                    {
                        c.SetInt(key, c.GetInt(key) + 1);
                    }
                    c.Set(key + ".at", v.NextFireAt.ToString("R", CultureInfo.InvariantCulture));
                }
                total += c.GetInt(key);
            }
            return total;
        }

        /// <summary>圈里两座炮塔的射程里有没有在场的突袭单位（射程取炮塔读数，没读到按 18 米）。</summary>
        private static bool RaiderInRange(JourneyContext c, out float nearest)
        {
            nearest = float.MaxValue;
            List<(int Id, Vector2 Pos)> raiders = D.Raiders();
            bool any = false;
            foreach (string k in new[] { "t1", "t2" })
            {
                BuildingRecord b = P.Bld(c, k);
                if (b == null)
                {
                    continue;
                }
                float range = TurretService.TryGetReadout(St, b.BuildingId, out TurretReadout ro) && ro.Range > 0.5f ? ro.Range : 18f;
                if (range > 0.5f)
                {
                    c.Set("t.range", range.ToString("R", CultureInfo.InvariantCulture));
                }
                foreach ((int _, Vector2 pos) in raiders)
                {
                    float d = Vector2.Distance(pos, b.Position);
                    nearest = Mathf.Min(nearest, d);
                    any |= d <= range;
                }
            }
            return any;
        }

        private static StepOutcome TickTurretsEngaged(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int fire = SampleFire(c);
            if (!FgjM3Common.Done(c, "e0"))
            {
                c.SetInt(FgjM3Common.SK(c, "f0"), fire);
                FgjM3Common.Mark(c, "e0");
                return StepOutcome.Wait;
            }
            bool inRange = RaiderInRange(c, out float near);
            int shots = fire - c.GetInt(FgjM3Common.SK(c, "f0"));
            if (inRange && shots > 0)
            {
                return StepOutcome.Done($"敌人进了圈里炮塔的射程（最近 {near:F1} 米），两座炮塔在开火（这几秒开了 {shots} 发）；{P.PowerLine(St)}");
            }
            return c.StepElapsed < 170 ? StepOutcome.Wait : StepOutcome.Fail($"等不到敌人进射程、炮塔开火：射程里有敌人 {inRange}（最近 {near:F1} 米）、开火 {shots} 发");
        }

        private static StepOutcome TickTurretsNoPower(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int fire = SampleFire(c);
            var lines = new List<string>();
            bool allOff = true;
            foreach (string k in new[] { "t1", "t2" })
            {
                BuildingRecord t = P.Bld(c, k);
                if (t == null)
                {
                    return StepOutcome.Fail("炮塔不见了");
                }
                string reason = BuildingStatusService.Evaluate(s, t).Reason ?? string.Empty;
                bool off = t.PowerState != BuildingPowerState.Powered && reason.Contains("没有电");
                allOff &= off;
                lines.Add($"{t.BuildingId}：{t.PowerState}，“{reason.Replace("\n", " / ")}”");
            }
            if (!allOff)
            {
                return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("禁用发电机后炮塔仍有电（或状态没写缺电）：" + string.Join("；", lines));
            }
            long now = GameClock.Ticks;
            if (!FgjM3Common.Done(c, "off"))
            {
                c.SetLong(FgjM3Common.SK(c, "offAt"), now);
                FgjM3Common.Mark(c, "off");
                return StepOutcome.Wait;
            }
            long since = now - c.GetLong(FgjM3Common.SK(c, "offAt"));
            if (since < GameClock.StepHz)
            {
                c.SetInt(FgjM3Common.SK(c, "f0"), fire); // 断电后先等 1 游戏秒（已经出膛的那一发不算），从这里开始数
                return StepOutcome.Wait;
            }
            if (RaiderInRange(c, out float near))
            {
                c.SetInt(FgjM3Common.SK(c, "seen"), c.GetInt(FgjM3Common.SK(c, "seen")) + 1);
                c.Set(FgjM3Common.SK(c, "near"), near.ToString("F1", CultureInfo.InvariantCulture));
            }
            int shots = fire - c.GetInt(FgjM3Common.SK(c, "f0"));
            if (shots > 0)
            {
                return StepOutcome.Fail($"断电后炮塔还在开火（{shots} 发）：" + string.Join("；", lines));
            }
            if (since < GameClock.StepHz * 6)
            {
                return StepOutcome.Wait;
            }
            return c.GetInt(FgjM3Common.SK(c, "seen")) > 0
                ? StepOutcome.Done($"家园断电：{P.PowerLine(s)}；炮塔停火 [{string.Join("；", lines)}]——敌人就在射程里（最近 {c.Get(FgjM3Common.SK(c, "near"))} 米），{since / (double)GameClock.StepHz - 1:F1} 游戏秒里两座炮塔一发没开（开火累计停在 {fire}）")
                : StepOutcome.Fail($"断电这几秒射程里没有敌人，停火看不出来：{string.Join("；", lines)}");
        }

        private static StepOutcome TickTurretsPowered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int fire = SampleFire(c);
            var lines = new List<string>();
            bool all = true;
            foreach (string k in new[] { "t1", "t2" })
            {
                BuildingRecord t = P.Bld(c, k);
                string reason = t != null ? BuildingStatusService.Evaluate(s, t).Reason ?? string.Empty : "不见了";
                all &= t != null && t.PowerState == BuildingPowerState.Powered && !reason.Contains("没有电");
                lines.Add($"{t?.BuildingId}：{t?.PowerState}，“{reason.Replace("\n", " / ")}”");
            }
            if (!all)
            {
                return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("启用发电机后炮塔还是没电：" + string.Join("；", lines) + "；" + P.PowerLine(s));
            }
            c.SetInt("pw.seen", 1);
            if (!FgjM3Common.Done(c, "on"))
            {
                c.SetInt(FgjM3Common.SK(c, "f0"), fire);
                FgjM3Common.Mark(c, "on");
                return StepOutcome.Wait;
            }
            bool inRange = RaiderInRange(c, out float near);
            int shots = fire - c.GetInt(FgjM3Common.SK(c, "f0"));
            if (shots > 0)
            {
                return StepOutcome.Done($"来电：{P.PowerLine(s)}；炮塔恢复 [{string.Join("；", lines)}]，接着开火（来电后 {shots} 发；射程里有敌人 {inRange}，最近 {near:F1} 米）");
            }
            if (D.Raiders().Count == 0)
            {
                return StepOutcome.Fail("来电时这一波已经没有在场的敌人，恢复开火看不出来：" + string.Join("；", lines));
            }
            return c.StepElapsed < 110 ? StepOutcome.Wait : StepOutcome.Fail($"来电后炮塔一直没开火（射程里有敌人 {inRange}，最近 {near:F1} 米）：" + string.Join("；", lines));
        }

        // ── 承接 DEBT-FG5RND06-03：黑匣子 → 陈列馆分析 → 技术数据 → 研究 ─────────────────────────────

        private static StepOutcome TickBlackBoxPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            var specs = new[]
            {
                new M.SiteSpec { Key = "gallery", Type = M.Gallery, MinR = 6 },
                new M.SiteSpec { Key = "lab", Type = M.Lab, MinR = 6 },
            };
            P.FactoryPlan plan = M.PlanSites(s, withRecycler: false, withBurner: false, specs, extraDemand: 0, out string why);
            if (plan == null)
            {
                return StepOutcome.Fail("陈列馆与实验室规划不出来：" + why);
            }
            M.SavePlan(c, plan);
            var acts = new List<string>();
            foreach (GridCell g in plan.Generators)
            {
                acts.Add($"B|{HomeValleyLayout.BuildingTypeGenerator2}|{g.X}|{g.Y}|0|发电机 2 {D.Cell(g)}");
            }
            acts.Add(P.ActB(plan.B("gallery")));
            acts.Add(P.ActB(plan.B("lab")));
            P.SetActions(c, "bbp", acts);
            int boxes = Victims(c).Count(id => BlackBoxService.Find(s, id) != null);
            return StepOutcome.Done($"规划：{M.Describe(plan, HomeGridService.CorePivot(s))}；陈列馆队列里第三波阵亡机器的黑匣子 {boxes} 个");
        }

        private static StepOutcome TickBlackBoxResearched(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "b0"))
            {
                c.SetLong(FgjM3Common.SK(c, "pts0"), s.Research?.PointsProduced ?? 0);
                c.SetLong(FgjM3Common.SK(c, "lab0"), TechDataFlow.ExpenseOf(s, TechDataFlow.Lab));
                c.SetLong(FgjM3Common.SK(c, "inc0"), TechDataFlow.IncomeOf(s, TechDataFlow.BlackBox));
                FgjM3Common.Mark(c, "b0");
                return StepOutcome.Wait;
            }
            List<BlackBoxRecord> boxes = Victims(c).Select(id => BlackBoxService.Find(s, id)).Where(x => x != null).ToList();
            BlackBoxRecord done = boxes.FirstOrDefault(x => x.Done);
            long income = TechDataFlow.IncomeOf(s, TechDataFlow.BlackBox) - c.GetLong(FgjM3Common.SK(c, "inc0"));
            long points = (s.Research?.PointsProduced ?? 0) - c.GetLong(FgjM3Common.SK(c, "pts0"));
            long lab = TechDataFlow.ExpenseOf(s, TechDataFlow.Lab) - c.GetLong(FgjM3Common.SK(c, "lab0"));
            int per = BlackBoxService.PointsPerBox;
            if (done == null || income < per || points <= 0 || lab <= 0)
            {
                int tick = (int)(c.StepElapsed / 60);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"黑匣子分析中：[{string.Join("；", boxes.Select(x => $"{FgjM1Journey.Label(x.MachineLogicId)} 已入账 {x.PointsGranted}/{per}{(x.Done ? " ✓" : string.Empty)}"))}]；“黑匣子”收入 +{income}；研究点 +{points}、实验室取技术数据 {lab}（工作中的陈列馆 {BlackBoxService.WorkingCount(s)} 座）");
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"{FgjM1Journey.Label(done.MachineLogicId)}（第三波被突袭部队打死）的黑匣子分析完：技术数据 +{done.PointsGranted}（统计“黑匣子”收入 +{income}）；" +
                                   $"同一时期仿真实验室取了 {lab} 件技术数据、研究点 +{points}（技术数据进同一个库存，研究照常用上）");
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
