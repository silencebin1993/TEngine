using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Beacon;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEngine;
using UnityEngine.UIElements;
using Mega = GameLogic.EditorTools.FgMegastructureSelfCheck;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG13-E2E-01：M13 出口旅程 FGJ-M13（里程碑文档 FG-M13；FG11 FGT-END-009；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择 / Shift 加选、右键地面 = 移动、右键敌人 = 攻击，按钮 / 快捷键只切状态；等待段用正式 3 倍速。
    ///
    /// 里程碑原文：完成五个巨构阶段 → 揭示 → 守住三波清洗 → 进攻白潮滩头，摧毁轨道锚点 → 击败先遣旗舰（阶段三用反向接入夺回被夺取的机器）→ 结局 → 选择继续。
    /// 轻量口径 a：每个新核心系统至少被真实输入触发一次并断言结果——
    /// 返航工地（建造菜单“终局”页放下，FGR-END-001）→ 第五阶段真实送料 / 计时完工（完工突袭 FGR-END-002、舰队消息、信标点火）→ 选中工地点“点亮信标…”→ 确认点亮 → 跳过揭示演出（FGR-END-010）→
    /// 作战队守住三波清洗（登陆舱、轨道打击预警、每波前自动存档，FGR-END-020）→ 右键滩头目标摧毁 3 座轨道锚点 + 登陆信标（FGR-END-021）→ 先遣旗舰：右键信标（阶段一）→ 护盾节点（阶段二）→
    /// 选中被根权限夺取的机器按接入键反向接入夺回（阶段三）→ 右键中继 → 右键核心（FGR-END-022）→ 跳过结局演出 → 结算（FGR-END-031）→ 点“继续”进入后日谈（FGR-END-032）。
    ///
    /// 夹具（都写在步骤标题与报告里；被测行为全走正式系统）：DEBT-FG13E2E01-01 开局第三幕（正式任务完成入口发巨构蓝图）、前四个巨构阶段记为已完工（真实五阶段由 FgMegastructureSelfCheck D1 覆盖）、
    /// 返航工地施工虚影记为建成（施工由 FGJ-M3 覆盖）、工地的传送带 / 管线供给与供电夹具（产线与电网由 FGJ-M3 / M4 覆盖、真实电网供电由 FgMegastructureSelfCheck N5 覆盖）；
    /// 作战队 36 台 ERC-003（重炮 / 机枪各半）、耐久 5000（战斗力培养由 FGJ-M1～M11 覆盖；本旅程考核终局系统，不赌战斗临界结果）；事件频率设为“关闭”、第一次信号幽灵记为已发生（事件由 FGJ-M12 覆盖）；
    /// 往滩头的信号中继链（建中继由 FGJ-M8 覆盖：右键下令与反向接入都要信号覆盖）；DEBT-FG13E2E01-02 阶段二四个护盾节点只认本族端口，作战队只带动能重炮：先真实右键一个节点、
    /// 记下闸门变化，再由夹具击毁四个节点（端口矩阵由 FgFlagshipSelfCheck C2 / C3 覆盖）。
    /// 用法：bash tools/unity-journey.sh FGJ-M13；断点见 <see cref="Checkpoints"/>（--from 续跑）。
    /// </summary>
    public static class FgjM13Journey
    {
        public const string Id = "FGJ-M13";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const int SquadSize = 36;
        private const float SquadHp = 5000f;
        private const int ReinforceSize = 24;
        private const float ReinforceCells = 50f;

        private static readonly string[] Checkpoints = { "mega_done", "reveal_state", "p1_fight", "purge_done", "beach_done", "fs_reclaim", "fs_done" };

        private static CampaignState St => CampaignSession.Current;

        private static CombatSite Home => GameRoot.HomeValley?.Combat;

        private static RegionSquadCommandSystem Squad => GameRoot.HomeValley?.SquadCommands;

        /// <summary>FG15-QA-03（FGR-GEN-091“其中 3 个种子跑完整的旅程机器人”）：同一套 M13 步骤按另一个种子新建战役（主菜单 → 结局）。
        /// 种子 42 就是 FGJ-M13 本身；其它种子的 ID 是 FGJ-SEED-{种子}（在 <see cref="JourneyCatalog"/> 登记，种子取 <see cref="FgSeedSuiteSelfCheck.JourneySeeds"/>）。</summary>
        public static string SeedJourneyId(int seed) => seed == TestSeed ? Id : "FGJ-SEED-" + seed.ToString(CultureInfo.InvariantCulture);

        public static JourneyDef Build() => Build(TestSeed);

        public static JourneyDef Build(int seed)
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", $"存档槽 → 新游戏设置（种子 = 固定测试种子 {seed}，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, c.Seed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("m13_fix", "开局夹具（DEBT-FG13E2E01-01）：主线“巨构蓝图”经正式任务完成入口完成 → 第三幕、建造菜单解锁返航工地；前四个巨构阶段记为已完工（真实五阶段由 FgMegastructureSelfCheck D1 覆盖）；" +
                              $"难度建造者、事件频率“关闭”、第一次信号幽灵记为已发生（FGJ-M12 覆盖）；核心旁 {SquadSize} 台 ERC-003 作战队（重炮 / 机枪各半，耐久 {SquadHp:0}）", 30, ApplyFixture, TickFixture),

                // ── 1. 返航工程：放工地 → 第五阶段完工 ──
                S("m_build", "按建造键打开建造模式", 15, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("m_pick", "点“终局”分类页签、再点“返航工地”（拿到巨构蓝图后解锁）", 20, null, c => FgjM3Common.TickPick(c, MegastructureService.TypeId), retries: 2),
                S("m_place", "左键点核心外按种子地形找到的 12×12 空地：放下返航工地施工虚影（正式放置校验，只能一座）", 30, null, TickPlaceSite, retries: 2),
                S("m_close", "再按建造键关闭建造模式", 15, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("m_rig", "工地夹具（DEBT-FG13E2E01-01）：施工虚影记为建成（施工由 FGJ-M3 覆盖）；西口 / 南口传送带送巨构构件、东口管线送冷却液、工地接上 500 供电（产线 / 电网由 FGJ-M3 / M4 覆盖）", 20, ApplyRig, TickRig),
                S("m_stage5", "第五阶段“信标点火”：传送带 / 管线持续送料、持续供电 2 游戏小时 → 完工：舰队消息、更高等级的完工突袭排上、信标点火（五个阶段全部完工）", 300, null, TickStage5),
                S("mega_done", "信标点火通知（“选中工地，点‘点亮信标…’”）；完工突袭预警在册", 30, null, TickMegaDone),

                // ── 2. 揭示 ──
                S("r_open", "左键点返航工地：生产面板写“全部完工”，出现“点亮信标…”按钮", 40, null, TickOpenSite, retries: 2),
                S("r_btn", "点生产面板“点亮信标…”：打开信标确认面板（点亮模式）", 20, null, TickRevealButton, retries: 2),
                S("r_confirm", "信标确认面板点“确认点亮”（二次确认）：演出前自动存档、揭示演出待播", 20, null, TickRevealConfirm, retries: 2),
                S("r_skip", "揭示演出接管画面：点“跳过演出”→ 确认框点“跳过”", 60, null, TickRevealSkip),
                S("r_close", "再按建造键关闭建造模式（点工地时开的）", 15, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("reveal_state", "揭示落定：顶栏“轨道威胁”、主线“守住家园”、演出后自动存档（RevealComplete）", 30, null, TickRevealState),

                // ── 3. 三波清洗 ──
                S("p_start", "清洗开始：顶栏写清洗波次与倒计时", 120, null, TickPurgeStart),
                S("p_def", "防线夹具（DEBT-FG13E2E01-01）：核心旁两座维修无人机站 + 仓库维修件（建造、供电、维修件产线由 FGJ-M6 覆盖），波与波之间自动修核心", 20, ApplyRepairStations, TickRepairStations),
                S("purge_done", "撑过清洗：事件账本 endgame_purge_cleared、主线完成、核心完好", 30, null, TickPurgeDone),

                // ── 4. 白潮滩头 ──
                S("b_land", "白潮滩头在预留区着陆：顶栏登陆场进度、地图标出 3 座轨道锚点 + 登陆信标；中继夹具（建中继由 FGJ-M8 覆盖）把信号覆盖延到滩头", 120, ApplyBeachRelays, TickBeachLanded),
                S("b_sel", "左键点一台作战队、Shift 左键加选其余几台", 60, null, c => SelectTeam(c), retries: 2),
                S("b_fight", "右键滩头目标（攻击）：屏蔽塔、3 座轨道锚点与登陆信标逐个摧毁，切断持续投送", 900, null, TickBeachFight),
                S("beach_done", "滩头切断：事件账本 endgame_beachhead_cleared、主线完成", 30, null, TickBeachDone),

                // ── 5. 先遣旗舰 ──
                S("fs_start", "先遣旗舰开战：阶段一“轨道压制”，首领 HUD 写信标数、散布与下次轰炸", 120, null, TickFlagshipStart),
                S("fs_sel1", "左键点一台作战队、Shift 左键加选其余几台", 60, null, c => SelectTeam(c), retries: 2),
                S("fs_p1", "右键旗舰信标（攻击）：四座信标逐个摧毁 → 阶段二“要塞”", 600, null, c => TickFlagshipGoals(c, FlagshipService.PhaseOrbital)),
                S("fs_p2", "阶段二：右键一个护盾节点（作战队只带动能重炮），记下闸门变化；夹具击毁四个节点（DEBT-FG13E2E01-02，端口矩阵由 FgFlagshipSelfCheck C2 / C3 覆盖）→ 阶段三“根权限”", 120, null, TickFortress),
                S("fs_seized", "阶段三：根权限每 20 秒夺取离中继最近的一台作战机器（共用夺控状态机）", 90, null, TickSeized),
                S("fs_selseized", "左键点被夺取的那台机器（选中）", 60, null, TickSelectSeized, retries: 2),
                S("fs_reclaim", "按“接入 / 退出接入”键（默认 M）：反向接入（信号覆盖内）3 秒后夺回、镜头不切直控", 40,
                    c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickReclaim, retries: 1),
                // 根权限每 20 游戏秒夺取一台：3 倍速下逐台点选要 ~50 游戏秒，途中被夺取的机器会掉出选择集（第 20 次实跑）——按正式打法先战略暂停再点选。
                S("fs_pause", "按暂停键（战略暂停中点选）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : GameClock.Paused ? StepOutcome.Done("战略暂停已开启") : StepOutcome.Retry("按了暂停键，世界没有暂停"), retries: 1),
                S("fs_sel3", "暂停中：左键点一台作战队、Shift 左键加选其余没被夺取的几台", 60, null, c => SelectTeam(c, true), retries: 2),
                S("fs_resume", "按暂停键继续", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => !GameClock.Paused, true),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !GameClock.Paused ? StepOutcome.Done($"世界继续运行（第 {GameClock.Ticks} 步）") : StepOutcome.Retry("按了暂停键，世界仍在暂停"), retries: 1),
                S("fs_p3", "右键旗舰中继（攻击）：三座中继摧毁 → 阶段四核心暴露、被夺取的机器全部归还", 600, null, c => TickFlagshipGoals(c, FlagshipService.PhaseRoot)),
                S("fs_p4", "右键暴露的旗舰核心（攻击）：核心摧毁 = 击退先遣旗舰", 600, null, c => TickFlagshipGoals(c, FlagshipService.PhaseBurst)),
                S("fs_done", "击退：事件账本 endgame_flagship_defeated、主线完成、首领 HUD“已被击退”", 30, null, TickFlagshipDone),

                // ── 6. 结局、结算、继续 ──
                S("e_cut", "结局演出接管画面：点“跳过演出”→ 确认框点“跳过”；结局落盘后打开结算", 90, null, TickEndingCutscene),
                S("e_stats", "结算画面：统计（归还面积 / 天数 / 反应 / 熔合配方 / 击退突袭）、幸存机器与纪念墙、结语", 20, null, TickSettlement),
                S("e_handoff", "交接：正式结局自动存档写下的这一份（结算开着、还没选“继续”）连同代码指纹抄给 FGJ-M14（DEBT-FG14E2E01-01：后日谈从真实结局存档进入）", 15, null, TickHandoff),
                S("e_continue", "结算点“继续”：进入后日谈、结算关闭、存档允许", 30, null, TickContinue, retries: 2),
            };
            steps.InsertRange(steps.FindIndex(x => x.Id == "purge_done"), PurgeWaveSteps());
            return new JourneyDef
            {
                Id = SeedJourneyId(seed),
                Title = (seed == TestSeed ? string.Empty : $"种子测试集（种子 {seed}）：") + "M13 出口：放返航工地、第五阶段完工 → 点亮信标、揭示 → 守住三波清洗 → 摧毁白潮滩头锚点 → 击败先遣旗舰（阶段三反向接入夺回）→ 结局 → 结算点“继续”",
                Seed = seed,
                TotalTimeoutSeconds = 3000,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = Checkpoints,
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static long NowMs() => FgjM2Common.NowMs();

        private static List<int> Team(JourneyContext c) => FgjM1Journey.Roster(c);

        private static bool Alive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive;

        private static List<int> FreeTeam(JourneyContext c) => Team(c).Where(id => Alive(id) && !SignalSeizeService.IsSeized(id)).ToList();

        private static Vector2 PosOf(int id) => FgjM8Journey.PosOf(id);

        private static Vector2 CorePos => FgjM8Journey.CorePos;

        private static string Cell(Vector2 p) => $"({F(p.x, "0")},{F(p.y, "0")})";

        private static string Short(string s) => s == null ? string.Empty : s.Length > 120 ? s.Substring(0, 120) + "…" : s;

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") : "阵亡")}{(SignalSeizeService.IsSeized(id) ? "（被夺取）" : string.Empty)}" : $"{id}?"));

        private static bool CoreAlive()
        {
            BuildingRecord core = St?.BuildingRecords?.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore && b.RegionId == HomeValleyLayout.RegionId);
            return core != null && core.ConstructionState != BuildingConstructionState.Destroyed;
        }

        private static string OrbitalHud() => JourneyInput.FindUitk<Label>("[WorldBarHost]", "WorldOrbitalThreat")?.text ?? string.Empty;

        private static string QuestState(string id) => CampaignObjectiveTracker.FindQuest(St, id)?.State.ToString() ?? "无";

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, c.Seed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM2Common.IsolateCodex(c);
                FgjM3Journey.IsolateLayoutLibrary(c);
            });

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log($"终局计数（全程）：巨构完工 {MegastructureService.StateOf(St)?.Done?.Length ?? -1}；揭示 {RevealService.Revealed(St)}；清洗 {PurgeWaveService.StateOf(St)?.Launched ?? -1} 发动 / {PurgeWaveService.StateOf(St)?.Cleared ?? -1} 撑过；" +
                  $"滩头 {BeachheadService.Done(St)}；旗舰 阶段 {FlagshipService.PhaseOf(St)}、夺取 {FlagshipService.Seizes}、阶段存档 {FlagshipService.PhaseSaves}；结局 {EndingService.Reached(St)}、后日谈 {St?.Progress?.IsPostgame}；" +
                  $"共用夺控 {CombatSite.SeizeStarted} / 归还 {CombatSite.SeizeEndedCount}；作战队 {TeamHp(c)}");
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("fix.done") == 1)
            {
                return;
            }
            c.SetInt("fix.done", 1);
            c.SetInt("act0", s.Progress?.Act ?? 0);
            Mega.CompleteMegaQuest(s); // 正式任务完成入口：发巨构蓝图（事件账本）→ 资料库 → 第三幕
            if (CampaignObjectiveTracker.FindQuest(s, "main.megastructure") == null)
            {
                // 主线链：第二幕的“巨构蓝图”记为已完成（与 FgMegastructureSelfCheck R 段同一夹具），“返航工程 → 守住家园 → …”按前置正式接上。
                s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                    .Append(new ObjectiveRecord { ObjectiveId = "main.megastructure", DefId = "main.megastructure", State = ObjectiveState.Completed }).ToArray();
            }
            CampaignObjectiveTracker.Recompute(s);
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            // 难度：建造者（DEBT-FG13E2E01-01 待用户复核）——标准难度下第五阶段完工的 4 级突袭（第三幕系数 2.4，约 105 台、阵营按暴露份额，种子 42 是静默隐形部队）
            // 在核心旁 12 格展开、5 游戏秒拆掉核心，要整套第三幕防线（炮塔 / 墙 / 标记器，FGJ-M6 / M8 覆盖）才守得住；建造者只放行剧情突袭与清洗波次（FG16 第 3 节、FG13-END-01 待复核决策 10），
            // 清洗波次照来（规模 0.5、敌人耐久 / 伤害 0.8）。标准难度下“更高等级的完工突袭”由 FgMegastructureSelfCheck D1 覆盖。
            c.Set("diff0", s.DifficultyId ?? string.Empty);
            s.DifficultyId = DifficultyService.Builder;
            DirectorEventState d = EventDirectorService.StateOf(s);
            if (d != null && EventDirectorService.Schedule(s, SignalGhostService.EventId) == null)
            {
                d.Schedules = (d.Schedules ?? Array.Empty<EventDirectorScheduleRecord>()).Append(new EventDirectorScheduleRecord { EventId = SignalGhostService.EventId, Occurrences = 1 }).ToArray();
            }
            MegastructureState m = MegastructureService.StateOf(s);
            m.StageIndex = MegastructureService.Stages.Count - 1;
            m.Done = MegastructureService.Stages.Take(m.StageIndex).Select(st => new MegaStageDoneRecord { Id = st.Id, Tick = GameClock.Ticks, Day = 1 }).ToArray();
            m.SolidDelivered = m.FluidDeliveredMl = m.PoweredTicks = 0;
            m.WindowStartTick = -1;

            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { ComponentCatalog.CompCannonId, ComponentCatalog.CompGunId }).Distinct().ToArray();
            // 两种作战蓝图各一半：重炮（溅射，打登陆舱 / 结构）+ 机枪（射速快，打成群的清洗无人机）。
            string bpId = FgjM12Journey.SaveBlueprint(c, "bp.id", "重炮作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>()));
            string bpGun = FgjM12Journey.SaveBlueprint(c, "bp.gun", "机枪作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            if (bpId.Length == 0 || bpGun.Length == 0)
            {
                c.Set("fix.err", "保存作战蓝图失败：" + c.Get("bp.id.note") + " / " + c.Get("bp.gun.note"));
                return;
            }
            Vector2 core = CorePos;
            var ids = new List<int>();
            var used = new List<GridCell>();
            for (int i = 0; i < SquadSize; i++)
            {
                GridCell at = default;
                for (int tr = i; tr < i + 40; tr++)
                {
                    at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(core.x - 9f + (tr % 6) * 3f), Mathf.RoundToInt(core.y - 9f - (tr / 6) * 3f)), 10);
                    if (!used.Any(x => Math.Abs(x.X - at.X) + Math.Abs(x.Y - at.Y) < 2))
                    {
                        break;
                    }
                }
                used.Add(at);
                int id = FgjM12Journey.SpawnMachine(s, i % 2 == 0 ? bpId : bpGun, new Vector2(at.X, at.Y), SquadHp);
                if (id > 0)
                {
                    ids.Add(id);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            // 工地位置：按种子地形在核心外找（与 FgMegastructureSelfCheck 同一函数，B25），另要求玩家放置校验通过（已探索、不越界）。
            GridCell? pivot = Mega.FindSitePivot(s, p => P.Placeable(s, MegastructureService.TypeId, p, 0, null));
            if (pivot == null)
            {
                c.Set("fix.err", "核心外找不到能放 12×12 返航工地的空地");
                return;
            }
            FgjM3Common.SetCell(c, "site", pivot.Value);
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            StepOutcome? cut = JourneyCommon.TickDismissCutscene(c);
            if (cut.HasValue)
            {
                return cut.Value;
            }
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("fix.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("fix.err"));
            }
            CampaignState s = St;
            List<int> team = Team(c);
            bool inKernel = team.Count == SquadSize && team.All(id => Home != null && Home.TryGetMachineUnit(id, out _));
            if (!inKernel)
            {
                return c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Fail($"夹具后作战队没全进家园内核（{team.Count} 台）");
            }
            bool unlocked = BuildCatalog.TryGet(MegastructureService.TypeId, out BuildEntry entry) && BuildCatalog.IsUnlocked(s, entry);
            string msg = $"第 {c.GetInt("act0")} → {s.Progress.Act} 幕；难度 {c.Get("diff0")} → {s.DifficultyId}（只放行剧情突袭 {DifficultyService.StoryOnly(s)}、规模 {DifficultyService.Scale(s)}）；巨构蓝图 {MegastructureService.HasBlueprint(s)}、返航工地解锁 {unlocked}；巨构进度：第 {MegastructureService.StateOf(s).StageIndex + 1}/{MegastructureService.Stages.Count} 阶段" +
                         $"（已完工 {MegastructureService.StateOf(s).Done.Length}）；事件频率 {EventDirectorService.StateOf(s)?.Frequency}；作战队 {team.Count} 台；工地选址 {FgjM3Common.Cell(FgjM3Common.GetCell(c, "site"))}";
            return s.Progress.Act >= 3 && MegastructureService.HasBlueprint(s) && unlocked ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 1. 返航工程 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickPlaceSite(JourneyContext c)
        {
            CampaignState s = St;
            GridCell pivot = FgjM3Common.GetCell(c, "site");
            BuildingRecord b = s.BuildingRecords?.FirstOrDefault(x => x != null && x.BuildingTypeId == MegastructureService.TypeId);
            if (b != null)
            {
                c.Set("site.id", b.BuildingId);
                string msg = $"左键 {FgjM3Common.Cell(pivot)}：放下返航工地（{b.ConstructionState}，中心格 ({b.GridX},{b.GridY})）；建造栏状态“{FgjM3Common.Mode?.StatusText}”";
                return b.ConstructionState == BuildingConstructionState.Planned ? StepOutcome.Done(msg) : StepOutcome.Fail(msg + "（应为施工虚影）");
            }
            if (!FgjM3Common.Once(c, "click", () => !JourneyInput.Holding && FgjM3Common.TryClick(FgjM3Common.Ground(pivot))))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "click") < 1500 ? StepOutcome.Wait
                : StepOutcome.Retry($"左键 {FgjM3Common.Cell(pivot)} 后没有返航工地（状态行“{FgjM3Common.Mode?.StatusText}”）");
        }

        /// <summary>返航工地只能一座（FGR-END-001）：按类型找。</summary>
        private static BuildingRecord Site() => St?.BuildingRecords?.FirstOrDefault(x => x != null && x.BuildingTypeId == MegastructureService.TypeId);

        private static void ApplyRig(JourneyContext c)
        {
            CampaignState s = St;
            BuildingRecord b = Site();
            if (s == null || b == null || c.GetInt("rig.done") == 1)
            {
                return;
            }
            c.SetInt("rig.done", 1);
            b.ConstructionState = BuildingConstructionState.Operational;
            b.Health = BuildingOps.MaxDurability(MegastructureService.TypeId);
            HomeValleyPowerGrid.Recompute(s);
            MegaStageDef st = MegastructureService.Current(s);
            int parts = (int)(st.SolidAmount / 2 + 40);
            long liters = st.FluidMl / 1000 + 400;
            Mega.Rig rig = Mega.Supply(s, b, new GridCell(b.GridX, b.GridY), parts, liters, powered: true);
            c.Set("rig.note", $"两口各 {parts} 件构件、冷却液 {liters} 升；送料口 ({rig.FeedCell.X},{rig.FeedCell.Y})");
        }

        private static StepOutcome TickRig(JourneyContext c)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = Site();
            return b != null && b.ConstructionState == BuildingConstructionState.Operational
                ? StepOutcome.Done($"工地建成（耐久 {F(b.Health, "0")}）、供电 {b.PowerState}；{c.Get("rig.note")}")
                : StepOutcome.Fail($"工地夹具没生效：{b?.ConstructionState}");
        }

        private static StepOutcome TickStage5(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            BuildingRecord b = Site();
            if (c.GetInt(FgjM3Common.SK(c, "init")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "init"), 1);
                c.SetInt("raid.serial0", RaidDirectorService.StateOf(s).NextPlanSerial);
                c.SetInt("raid.skip0", RaidDirectorService.StateOf(s).TriggersSkipped);
                c.SetInt("mega.notes0", NotificationCenter.History.Count);
            }
            if (b != null && b.PowerState != BuildingPowerState.Powered && !MegastructureService.AllDone(s))
            {
                // 供电夹具（DEBT-FG13E2E01-01）：电网结算把夹具置的有电改回去时再置一次，记次数。
                Mega.SetPower(b, true);
                c.SetInt("power.reassert", c.GetInt("power.reassert") + 1);
            }
            MegastructureState m = MegastructureService.StateOf(s);
            if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                c.Log($"[{F(c.StepElapsed, "0")}s] 第 {m.StageIndex + 1} 阶段：构件 {m.SolidDelivered}、冷却液 {m.FluidDeliveredMl / 1000} 升、供电 {m.PoweredTicks} 步；面板“{Short(b != null ? MegastructureService.PanelText(s, b, b.PowerState == BuildingPowerState.Powered) : "无工地")}”");
            }
            if (!MegastructureService.AllDone(s))
            {
                return StepOutcome.Wait;
            }
            MegaStageDef last = MegastructureService.Stages[MegastructureService.Stages.Count - 1];
            bool msg = NarrativeCatalog.TryGetByTrigger(MegastructureService.NarrativeTriggerPrefix + last.Id, out NarrativeEntryDef nd) && NarrativeService.Has(s, nd.Id);
            RaidPlanRecord plan = RaidDirectorService.Plans(s).Where(p => p != null && p.Serial >= c.GetInt("raid.serial0") && Array.IndexOf(p.Triggers ?? Array.Empty<string>(), RaidCatalog.TriggerMegastructure) >= 0)
                .OrderBy(p => p.Serial).LastOrDefault();
            if (c.GetLong(FgjM3Common.SK(c, "doneAt")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "doneAt"), NowMs());
            }
            int skipped = RaidDirectorService.StateOf(s).TriggersSkipped - c.GetInt("raid.skip0");
            if (plan == null && skipped == 0 && NowMs() - c.GetLong(FgjM3Common.SK(c, "doneAt")) < 10000)
            {
                return StepOutcome.Wait; // 完工把“巨构”触发放进突袭导演的队列，导演下一次排期才生成计划
            }
            c.SetInt("mega.raid", plan?.Serial ?? -1);
            string text = $"第五阶段“{last.Id}”完工（已完工 {m.Done.Length} 个，用时 {F(c.StepElapsed, "0")} 真实秒；供电夹具补置 {c.GetInt("power.reassert")} 次）；舰队消息进资料库 {msg}；" +
                          $"完工突袭：{(plan != null ? $"{plan.PlanId} {plan.Level} 级（触发 {string.Join("+", plan.Triggers)}）" : "没有排")}，建造者难度只放行剧情突袭 → 导演记跳过 {skipped} 次";
            bool raidOk = DifficultyService.StoryOnly(s) ? plan == null && skipped >= 1 : plan != null && plan.Level >= 4;
            return msg && m.Done.Length == MegastructureService.Stages.Count && raidOk
                ? StepOutcome.Done(text) : StepOutcome.Fail(text + "（要求五阶段全部完工、舰队消息；建造者：完工触发被导演跳过，其余难度：完工突袭 4 级）");
        }

        private static StepOutcome TickMegaDone(JourneyContext c)
        {
            CampaignState s = St;
            bool notified = NotificationCenter.History.Any(e => e?.Type?.Id == RevealService.BeaconReadyNotifyType);
            bool awaiting = RevealService.AwaitingConfirm(s);
            string msg = $"信标点火通知 {notified}；等待玩家点亮 {awaiting}；揭示前顶栏无轨道威胁“{OrbitalHud()}”；任务“返航工程”{QuestState("main.homecoming")}";
            if (!notified || !awaiting)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail(msg);
            }
            return RevealService.Revealed(s) || QuestState("main.homecoming") != "Active" ? StepOutcome.Fail(msg + "（要求还没揭示、主线“返航工程”进行中）") : StepOutcome.Done(msg);
        }

        // ── 2. 揭示 ─────────────────────────────────────────────────────────────────

        private static StepOutcome TickOpenSite(JourneyContext c)
        {
            BuildingRecord b = Site();
            StepOutcome o = P.TickOpenPanel(c, MegastructureService.TypeId, new GridCell(b.GridX, b.GridY));
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            Button reveal = JourneyInput.FindUitk<Button>("[ProductionPanelHost]", "PrReveal");
            bool shown = reveal != null && reveal.resolvedStyle.display != DisplayStyle.None && !reveal.ClassListContains("bn-hidden");
            string text = MegastructureService.PanelText(St, b, b.PowerState == BuildingPowerState.Powered);
            return shown && text.Contains("全部完工")
                ? StepOutcome.Done($"{o.Message}；面板“{Short(text)}”；按钮“{reveal.text}”")
                : c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"面板“{Short(text)}”、点亮按钮显示 {shown}");
        }

        private static StepOutcome TickRevealButton(JourneyContext c)
        {
            if (BeaconLaunchPanelUIToolkit.RevealOpen)
            {
                return StepOutcome.Done($"点“点亮信标…”：生产面板关闭 {!ProductionPanelUIToolkit.IsOpen}、信标确认面板（点亮模式）打开：“{Short(BeaconLaunchPanelUIToolkit.RevealStatusText(St))}”");
            }
            // 按钮在面板滚动区底部：像玩家一样先在滚动区上滚到它露出来再点。
            if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[ProductionPanelHost]", "ProductionPanelBody"), JourneyInput.FindUitk<VisualElement>("[ProductionPanelHost]", "PrReveal")))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Retry("滚了 10 秒“点亮信标…”仍不在可见区");
            }
            if (!FgjM3Common.Once(c, "click", () => JourneyInput.ClickUitk("[ProductionPanelHost]", "PrReveal")))
            {
                return JourneyInput.LastUiTransient || c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("点不到“点亮信标…”：" + JourneyInput.LastUiFailure);
            }
            return FgjM3Common.SinceMs(c, "click") < 1500 ? StepOutcome.Wait : StepOutcome.Retry("点了“点亮信标…”，信标确认面板没打开");
        }

        private static StepOutcome TickRevealConfirm(JourneyContext c)
        {
            CampaignState s = St;
            if (RevealService.Pending(s) || NarrativeCutscenePlayerUIToolkit.IsShowing)
            {
                return StepOutcome.Done($"点“确认点亮”（点击 {BeaconLaunchPanelUIToolkit.RevealConfirmClicks} 次）：确认面板收起 {!BeaconLaunchPanelUIToolkit.RevealOpen}、揭示演出待播 / 开播；存档原因 {s.LastSaveReason}");
            }
            if (!FgjM3Common.Once(c, "click", () => JourneyInput.ClickUitk("[BeaconLaunchPanelHost]", "ConfirmButton")))
            {
                return JourneyInput.LastUiTransient || c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("点不到“确认点亮”：" + JourneyInput.LastUiFailure);
            }
            return FgjM3Common.SinceMs(c, "click") < 2000 ? StepOutcome.Wait
                : StepOutcome.Retry($"点了“确认点亮”没有开始揭示（拒绝“{BeaconLaunchPanelUIToolkit.LastRevealDeny}”）");
        }

        private static StepOutcome TickRevealSkip(JourneyContext c)
        {
            if (c.GetInt(FgjM3Common.SK(c, "saw")) == 0 && NarrativeCutscenePlayerUIToolkit.IsShowing)
            {
                c.SetInt(FgjM3Common.SK(c, "saw"), 1);
                c.SetInt("rv.save_pre", (int)St.LastSaveReason);
            }
            StepOutcome? cut = JourneyCommon.TickDismissCutscene(c);
            if (cut.HasValue)
            {
                return cut.Value;
            }
            return c.GetInt(FgjM3Common.SK(c, "saw")) == 1 && c.GetInt("cutsceneSkipped") == 1 && RevealService.Revealed(St)
                ? StepOutcome.Done($"揭示演出播放（演出前存档原因 {(SaveReason)c.GetInt("rv.save_pre")}）→ 点“跳过演出”并确认 → 演出收进资料库 {NarrativeService.Has(St, RevealService.EntryId)}")
                : c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail($"演出出现 {c.GetInt(FgjM3Common.SK(c, "saw"))}、跳过 {c.GetInt("cutsceneSkipped")}、已揭示 {RevealService.Revealed(St)}");
        }

        private static StepOutcome TickRevealState(JourneyContext c)
        {
            CampaignState s = St;
            string hud = OrbitalHud();
            string msg = $"顶栏“{hud}”；主线 返航工程 {QuestState("main.homecoming")}、守住家园 {QuestState("main.purge")}；最近存档原因 {s.LastSaveReason}；演出前存档 {(SaveReason)c.GetInt("rv.save_pre")}";
            bool ok = RevealService.OrbitalThreatActive(s) && hud.Contains("轨道威胁") && QuestState("main.homecoming") == "Completed" && QuestState("main.purge") != "无"
                      && (SaveReason)c.GetInt("rv.save_pre") == SaveReason.CutsceneEnter && s.LastSaveReason == SaveReason.RevealComplete;
            return ok ? StepOutcome.Done(msg) : c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        // ── 3. 清洗 ─────────────────────────────────────────────────────────────────

        /// <summary>点选前线作战队：名单在这次尝试开始时定下（点选途中有机器被夺取时不让名单错位）；<paramref name="paused"/> = 玩家已按暂停键、暂停中点选。</summary>
        private static StepOutcome SelectTeam(JourneyContext c, bool paused = false)
        {
            string key = FgjM3Common.SK(c, "ids");
            if (c.Get(key, string.Empty).Length == 0)
            {
                c.Set(key, string.Join(",", FieldTeam(c)));
            }
            List<int> ids = c.Get(key).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
            return FgjM12Journey.SelectMachines(c, ids, "作战队", paused);
        }

        /// <summary>前线作战队：离核心 300 格以外的（滩头增援及其后）；没有就是全队。留在家园的清洗幸存者不跟着来回点选（滩头离核心约 1100 格）。</summary>
        private static List<int> FieldTeam(JourneyContext c)
        {
            List<int> t = FreeTeam(c);
            List<int> far = t.Where(id => Vector2.Distance(PosOf(id), CorePos) > 300f).ToList();
            return far.Count > 0 ? far : t;
        }

        private static StepOutcome TickPurgeStart(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            string hud = OrbitalHud();
            return PurgeWaveService.Started(St) && hud.Contains("清洗")
                ? StepOutcome.Done($"清洗开始：顶栏“{hud}”；主线“{Short(PurgeWaveService.QuestProgressText(St))}”")
                : StepOutcome.Wait;
        }

        // 每波：读突袭预报的来袭方向（出发地 = 轨道登陆点；攻城队伍在核心外约 12 格的集结点展开，登陆舱就落在那里、落地投放清洗无人机）→
        // 在核心与集结点之间按来袭方向排一片散开的驻防点（6×6、间距 3 格）：逐台左键点选、右键自己的驻防点 → 全选按守卫键，每台守自己的位置 → 撑过这一波。
        // 散开是正式打法（不是夹具）：机身结构积热满了会熔穿、每 2 秒在脚下漏熔融金属，别的机器站进去照常吃热（CombatLogic.Rules 接触注热，只排除自己漏的）；
        // 第 16 次运行 36 台挤成两团驻防，第 2 波后互相烫成过热、熔融金属持续掉血，第 3 波来之前全队烫死（第 17 次诊断：36 台过热、最后受击全是队友）。
        private static IEnumerable<JourneyStep> PurgeWaveSteps()
        {
            for (int k = 1; k <= PurgeWaveService.WaveCount; k++)
            {
                int w = k;
                yield return S($"p{w}_plan", $"第 {w}/3 波：突袭预报排上，读来袭方向（出发地 = 轨道登陆点），在核心外 {PostNear:0}～{PostNear + PostGap * 5:0} 格排 36 个散开的驻防点", 1200, null, c => TickWavePlan(c, w));
                yield return S($"p{w}_spread", "逐台左键点选、右键各自的驻防点（移动；散开站位，免得熔穿漏出的熔融金属互相回灌热量）", 240, null, c => TickSpread(c, w));
                yield return S($"p{w}_sel", "左键点第一台、Shift 左键加选全部作战队", 90, null, c => FgjM12Journey.SelectMachines(c, FreeTeam(c), "作战队"), retries: 2);
                yield return S($"p{w}_hold", "按守卫键（默认 G）：每台守自己的驻防点", 15, c => JourneyInput.PressAction(GameActionId.CommandGuard), c => TickWaveHold(c, w, "全队"), retries: 1);
                yield return S($"p{w}_fight", w < PurgeWaveService.WaveCount
                    ? $"守住第 {w} 波：登陆舱落地投放清洗无人机、驻防的作战队迎击、维修无人机修核心 → 这一波打完"
                    : "守住第 3 波 → 撑过 3 波：三波都在预警时自动存档、轨道打击提前 30 秒在顶栏标出落点", 900, null, c => TickWaveFight(c, w));
            }
        }

        private const float PostNear = 3f;
        private const float PostGap = 3f;

        /// <summary>按来袭方向排 6 行 × 6 列驻防点：行沿来袭方向（核心外 PostNear 格起、每行 PostGap 格），列垂直于来袭方向；按种子地形取最近可通行格并去重（B25）。</summary>
        internal static List<Vector2> SpreadPosts(Vector2 from, int count)
        {
            Vector2 core = CorePos;
            Vector2 dir = (from - core).sqrMagnitude > 1f ? (from - core).normalized : Vector2.right;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            var used = new HashSet<(int, int)>();
            var posts = new List<Vector2>();
            for (int j = 0; j < count; j++)
            {
                Vector2 p = core + dir * (PostNear + PostGap * (j / 6)) + perp * (PostGap * (j % 6 - 2.5f));
                GridCell cell = default;
                for (int t = 0; t < 6; t++)
                {
                    Vector2 q = p + perp * (t % 2 == 0 ? t / 2 : -(t / 2 + 1));
                    cell = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(q.x), Mathf.RoundToInt(q.y)), 4);
                    if (!used.Contains((cell.X, cell.Y)))
                    {
                        break;
                    }
                }
                used.Add((cell.X, cell.Y));
                posts.Add(new Vector2(cell.X, cell.Y));
            }
            return posts;
        }

        private static RaidPlanRecord WavePlan(int wave) =>
            RaidDirectorService.Plans(St).FirstOrDefault(p => p != null && p.StoryId == PurgeWaveService.WaveId(wave) && p.State != RaidDirectorService.StateCancelled && p.State < RaidDirectorService.StateEnded);

        private static TransitGroupRecord WaveGroup(RaidPlanRecord plan) =>
            plan == null ? null : (St?.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>()).FirstOrDefault(g => g != null && g.PlanId == plan.PlanId);

        private static Vector2 GetV(JourneyContext c, string key)
        {
            string[] p = c.Get(key, "0,0").Split(',');
            return new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        }

        private static StepOutcome TickWavePlan(JourneyContext c, int w)
        {
            StepOutcome? bad = PurgeWatch(c);
            if (bad.HasValue)
            {
                return bad.Value;
            }
            RaidPlanRecord plan = WavePlan(w);
            if (plan == null || plan.State < RaidDirectorService.StateScheduled)
            {
                return StepOutcome.Wait;
            }
            TransitGroupRecord g = WaveGroup(plan);
            Vector2 from = g != null ? new Vector2((float)g.PosX, (float)g.PosY) : new Vector2(plan.OriginX, plan.OriginY);
            SetV(c, "pg.from", from);
            List<Vector2> posts = SpreadPosts(from, FreeTeam(c).Count);
            c.Set("pg.plan" + w, plan.PlanId);
            return StepOutcome.Done($"第 {w} 波预报 {plan.PlanId}（{plan.UnitTotal} 台、等级 {plan.Level}、状态 {plan.State}）：出发地 {Cell(new Vector2(plan.OriginX, plan.OriginY))}，驻防点 {posts.Count} 个、最近 {Cell(posts.FirstOrDefault())} 最远 {Cell(posts.LastOrDefault())}；核心耐久 {F(FgjM6Common.Core(St)?.Health ?? -1, "0")}");
        }

        /// <summary>逐台：左键点选这一台（选择集只剩它）→ 右键它的驻防点；全部下令后等八成到位（或来犯者已到）。</summary>
        /// <param name="watch">每帧的共同检查（null = M13 清洗波次的 <see cref="PurgeWatch"/>；FGJ-M14 后日谈周期清洗传自己的）。</param>
        internal static StepOutcome TickSpread(JourneyContext c, int w, Func<JourneyContext, StepOutcome?> watch = null)
        {
            StepOutcome? bad = (watch ?? PurgeWatch)(c);
            if (bad.HasValue)
            {
                return bad.Value;
            }
            List<int> team = FreeTeam(c);
            List<Vector2> posts = SpreadPosts(GetV(c, "pg.from"), team.Count);
            string kj = FgjM3Common.SK(c, "j"), kph = FgjM3Common.SK(c, "ph"), kat = FgjM3Common.SK(c, "at"), ktry = FgjM3Common.SK(c, "try");
            int j = c.GetInt(kj);
            if (j >= team.Count)
            {
                int there = Enumerable.Range(0, team.Count).Count(i => Vector2.Distance(PosOf(team[i]), posts[i]) < 2f);
                bool raid = FgjM6Common.Raiders().Count > 0;
                return there * 10 >= team.Count * 8 || raid || c.StepElapsed > 150
                    ? StepOutcome.Done($"{team.Count} 台逐台右键各自的驻防点，到位 {there} 台{(raid ? "（来犯者已到）" : string.Empty)}；点选重试 {c.GetInt("pg.spread_retry")} 次、跳过 {c.GetInt("pg.spread_skip")} 台")
                    : StepOutcome.Wait;
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(kat) < 450)
            {
                return StepOutcome.Wait;
            }
            int id = team[j];
            IReadOnlyList<int> sel = Squad?.Selection;
            if (c.GetInt(kph) == 0)
            {
                if (sel != null && sel.Count == 1 && sel[0] == id)
                {
                    c.SetInt(kph, 1);
                    return StepOutcome.Wait;
                }
                if (c.GetInt(ktry) >= 4 || !FgjM1Journey.HomeMarker(id, out HomeValleyMachineMarker m))
                {
                    c.SetInt("pg.spread_skip", c.GetInt("pg.spread_skip") + 1);
                    c.SetInt(kj, j + 1);
                    c.SetInt(ktry, 0);
                    return StepOutcome.Wait;
                }
                if (!JourneyCommon.PanToward(m.Position, 0.15f))
                {
                    return StepOutcome.Wait;
                }
                if (!JourneyInput.TryVisiblePointOf(m.View.transform, out Vector3 screen, out _))
                {
                    FgjM3Common.PanTo(m.Position);
                    return StepOutcome.Wait;
                }
                if (c.GetInt(ktry) > 0)
                {
                    c.SetInt("pg.spread_retry", c.GetInt("pg.spread_retry") + 1);
                }
                JourneyInput.ClickScreen(screen);
                c.SetInt(ktry, c.GetInt(ktry) + 1);
                c.SetLong(kat, NowMs());
                return StepOutcome.Wait;
            }
            if (FgjM3Common.TryClick(posts[j], 1))
            {
                c.SetInt(kj, j + 1);
                c.SetInt(kph, 0);
                c.SetInt(ktry, 0);
                c.SetLong(kat, NowMs());
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickWaveHold(JourneyContext c, int w, string who)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"第 {w} 波{who}：选中 {Squad?.Selection?.Count ?? 0} 台驻防（命令排队 {Squad?.QueuedCommandCount ?? 0}）");
        }

        private static StepOutcome TickWaveFight(JourneyContext c, int w)
        {
            StepOutcome? bad = PurgeWatch(c);
            if (bad.HasValue)
            {
                return bad.Value;
            }
            CampaignState s = St;
            PurgeWaveState p = PurgeWaveService.StateOf(s);
            if (w < PurgeWaveService.WaveCount)
            {
                return p.Cleared >= w
                    ? StepOutcome.Done($"第 {w} 波打完（同时在场的来犯者最多 {c.GetInt("pg.raiders_max")} 台；核心耐久 {F(FgjM6Common.Core(s)?.Health ?? -1, "0")}；维修件 {HomeInventory.Stock(s, BuildingOps.RepairKitId)}）；{TeamHp(c)}")
                    : StepOutcome.Wait;
            }
            if (!PurgeWaveService.Done(s))
            {
                return StepOutcome.Wait;
            }
            string msg = $"撑过 3 波（同时在场的来犯者最多 {c.GetInt("pg.raiders_max")} 台；维修站供电夹具补置 {c.GetInt("def.reassert")} 次；核心耐久 {F(FgjM6Common.Core(s)?.Health ?? -1, "0")}；维修件 {HomeInventory.Stock(s, BuildingOps.RepairKitId)}）；每波预警自动存档到第 {c.GetInt("pg.saved_max")} 波；轨道打击 30 秒内落点倒计时在顶栏出现过 {c.GetInt("pg.strike_seen") == 1}；{TeamHp(c)}";
            return c.GetInt("pg.saved_max") >= 3 && c.GetInt("pg.strike_seen") == 1 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg + "（要求三波都在预警时存档、看到轨道打击预警）");
        }

        /// <summary>清洗期间每帧的共同检查与记录：维修站供电夹具、核心 / 作战队存活、轨道打击预警、每波预警存档、来犯者峰值、每 20 秒一行日志。返回非空 = 失败。</summary>
        private static StepOutcome? PurgeWatch(JourneyContext c)
        {
            foreach (BuildingRecord b in Stations())
            {
                if (b.PowerState != BuildingPowerState.Powered)
                {
                    Mega.SetPower(b, true); // 供电夹具：电网结算改回去时再置一次（DEBT-FG13E2E01-01）
                    c.SetInt("def.reassert", c.GetInt("def.reassert") + 1);
                }
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            PurgeWaveState p = PurgeWaveService.StateOf(s);
            if (!CoreAlive())
            {
                return StepOutcome.Fail($"清洗中归还核心被毁（第 {p.Launched}/3 波）；{TeamHp(c)}");
            }
            if (FreeTeam(c).Count == 0)
            {
                return StepOutcome.Fail("作战队全灭");
            }
            double strikeIn = PurgeWaveService.NextStrikeLandsIn(s, p, GameClock.Ticks);
            if (strikeIn > 0 && strikeIn <= PurgeWaveService.StrikeWarnSeconds + 0.01 && OrbitalHud().Contains("轨道打击"))
            {
                c.SetInt("pg.strike_seen", 1);
            }
            if (p.SavedRetryWave > c.GetInt("pg.saved_max"))
            {
                c.SetInt("pg.saved_max", p.SavedRetryWave);
                c.Log($"第 {p.SavedRetryWave} 波预警：自动存档（原因 {s.LastSaveReason}，读档重试从第 {p.SavedRetryWave} 波开始）");
            }
            List<(int Id, Vector2 Pos)> raiders = FgjM6Common.Raiders();
            c.SetInt("pg.raiders_max", Math.Max(c.GetInt("pg.raiders_max"), raiders.Count));
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 清洗 {p.Launched} 发动 / {p.Cleared} 撑过；顶栏“{OrbitalHud()}”；在场来犯者 {raiders.Count}；核心耐久 {F(FgjM6Common.Core(s)?.Health ?? -1, "0")}；维修件 {HomeInventory.Stock(s, BuildingOps.RepairKitId)}；{TeamHp(c)}");
                c.Log("    诊断：" + Diag(c, raiders));
            }
            return null;
        }

        /// <summary>每 20 秒一行诊断：在场来犯者（种类 / 阵营 / 位置 / 耐久）、作战队过热台数与最高热量、受损作战队最后一次受击的攻击者。</summary>
        private static string Diag(JourneyContext c, List<(int Id, Vector2 Pos)> raiders)
        {
            CombatSite site = Home;
            if (site == null)
            {
                return "无家园";
            }
            string Unit(int uid)
            {
                if (!site.Kernel.TryGetUnit(uid, out BinGames.Sim.Combat.CombatUnitView v))
                {
                    return $"#{uid}?";
                }
                string mach = site.TryGetMachineOfUnit(uid, out int mid) ? Label(mid) : "-";
                return $"#{uid}[{v.Kind}/{v.Faction}/{mach}/{v.Behavior}] {Cell(new Vector2((float)v.Position.x, (float)v.Position.y))} 耐久 {F(v.Health, "0")}/{F(v.MaxHealth, "0")} 标记 {v.MarkedUntil:0.#}";
            }
            var parts = new List<string> { "来犯者 " + string.Join("；", raiders.Take(4).Select(r => Unit(r.Id))) };
            int hot = 0;
            float maxHeat = 0f;
            var hitBy = new Dictionary<int, int>();
            foreach (int id in FreeTeam(c))
            {
                if (site.TryGetMachineHeat(id, out float h, out bool over))
                {
                    hot += over ? 1 : 0;
                    maxHeat = Math.Max(maxHeat, h);
                }
                if (site.TryGetMachineUnit(id, out int uid) && site.Kernel.TryGetUnitEmg(uid, out _, out BinGames.Sim.Combat.EmgUnitState e) && e.LastHitBy > 0)
                {
                    hitBy[e.LastHitBy] = hitBy.TryGetValue(e.LastHitBy, out int n) ? n + 1 : 1;
                }
            }
            parts.Add($"过热 {hot} 台、最高热量 {F(maxHeat)}");
            parts.Add("最后受击来自 " + string.Join("；", hitBy.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Value} 台← {Unit(x.Key)}")));
            return string.Join(" | ", parts);
        }

        /// <summary>离作战队重心最近的活着的家园敌人（内核里的活敌人，按记录找、不写死 ID）；<paramref name="filter"/> 限定实例。</summary>
        private static bool NearestHostile(JourneyContext c, Func<RegionEnemyRecord, bool> filter, float maxDist, out string id, out Vector2 at)
        {
            id = null;
            at = default;
            CombatSite site = Home;
            List<int> team = FieldTeam(c);
            if (site == null || team.Count == 0)
            {
                return false;
            }
            Vector2 center = team.Aggregate(Vector2.zero, (a, x) => a + PosOf(x)) / team.Count;
            float best = maxDist;
            foreach (RegionEnemyRecord e in St.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || !e.IsAlive || filter != null && !filter(e) || !site.IsEnemyAlive(e.EnemyInstanceId) || !site.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 p))
                {
                    continue;
                }
                float d = Vector2.Distance(center, p);
                if (d < best)
                {
                    best = d;
                    id = e.EnemyInstanceId;
                    at = p;
                }
            }
            return id != null;
        }

        /// <summary>每 3 秒右键一次目标（攻击）；目标换了立即右键。</summary>
        private static void RightClickEnemy(JourneyContext c, string id, Vector2 at, string counter)
        {
            bool changed = c.Get(FgjM3Common.SK(c, "tgt"), string.Empty) != id;
            if (JourneyInput.Holding || !changed && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) < 3000)
            {
                return;
            }
            if (FgjM3Common.TryClick(at, 1))
            {
                c.Set(FgjM3Common.SK(c, "tgt"), id);
                c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                c.SetInt(counter, c.GetInt(counter) + 1);
            }
        }

        private static List<BuildingRecord> Stations() =>
            (St?.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(b => b != null && b.BuildingTypeId == RepairDroneCatalog.StationTypeId && b.ConstructionState == BuildingConstructionState.Operational).ToList();

        private static void ApplyRepairStations(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("def.done") == 1)
            {
                return;
            }
            c.SetInt("def.done", 1);
            var made = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                GridCell? at = FgProductionSelfCheck.FindFree(s, RepairDroneCatalog.StationTypeId, 6f, 14f);
                if (at.HasValue)
                {
                    BuildingRecord b = FgProductionSelfCheck.Built(s, RepairDroneCatalog.StationTypeId, "m13rep" + i, at.Value);
                    b.Health = BuildingOps.MaxDurability(RepairDroneCatalog.StationTypeId);
                    Mega.SetPower(b, true);
                    made.Add(FgjM3Common.Cell(at.Value));
                }
            }
            // 维修件夹具（DEBT-FG13E2E01-01）：维修无人机每修一步扣仓库维修件（RepairDroneService.PayKits），没有就停工“缺维修件”；维修件产线由 FGJ-M6 覆盖。
            int kits = HomeInventory.Add(s, BuildingOps.RepairKitId, 120, clampToSpace: false);
            RepairDroneService.Sync(s);
            c.Set("def.note", string.Join("、", made) + $"；仓库放入维修件 {kits}（现有 {HomeInventory.Stock(s, BuildingOps.RepairKitId)}）");
        }

        private static StepOutcome TickRepairStations(JourneyContext c)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            List<BuildingRecord> st = Stations();
            return st.Count == 2 ? StepOutcome.Done($"维修无人机站 {c.Get("def.note")}（供电 {string.Join("/", st.Select(b => b.PowerState))}）") : StepOutcome.Fail($"维修无人机站只建成 {st.Count} 座（{c.Get("def.note")}）");
        }

        private static StepOutcome TickPurgeDone(JourneyContext c)
        {
            CampaignState s = St;
            string msg = $"事件账本 {PurgeWaveService.Done(s)}；主线 守住家园 {QuestState("main.purge")}；核心完好 {CoreAlive()}；顶栏“{OrbitalHud()}”";
            return PurgeWaveService.Done(s) && QuestState("main.purge") == "Completed" && CoreAlive() ? StepOutcome.Done(msg) : c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        // ── 4. 滩头 ─────────────────────────────────────────────────────────────────

        private static bool IsTower(RegionEnemyRecord e) => EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) && row.Role == "countersignal";

        private static void ApplyBeachRelays(JourneyContext c)
        {
            // 着陆前 / 着陆时都可能进这一步：在 Tick 里等着陆后再建中继。
        }

        private static StepOutcome TickBeachLanded(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!BeachheadService.Landed(s) || !BeachheadService.TryPoint(s, BeachheadService.BeaconPoint, out Vector2 beacon))
            {
                return StepOutcome.Wait;
            }
            if (c.Get("bh.relays", string.Empty).Length == 0)
            {
                // 第二幕已完成防腐涂层主线（FGJ-M10 覆盖，与 FGJ-M11 同一夹具）：中继可建在 1～2 级污染上（FG-GAP-164 第 3 项）。
                // 第 19 次实跑：没有涂层时中继链在 (881,-141) 之后“推不动：Pollution”，滩头 24 台全在信号覆盖外，右键攻击收不到（“N 台在信号覆盖外”303 次）。
                CampaignEventLedger.TryGrant(s, CampaignObjectiveTracker.BlueprintEventId(AcidMarshService.CoatingBlueprintId), "QuestBlueprint", s.PlaySeconds, "main.anticorrosion");
                AcidMarshService.InvalidateCoating();
                var relays = new List<string>();
                FlagshipService.TryZone(s, out Vector2 zone);
                Vector2 last = FgjM10Journey.RelayChain(c, CorePos, SignalCoverageService.CoreRadius, beacon, "m13_relay", relays);
                if (Vector2.Distance(last, zone) > 150f)
                {
                    last = FgjM10Journey.RelayChain(c, last, 200f, zone, "m13_zrelay", relays);
                }
                HomeGridService.RevealArea(s, beacon, 80f);
                HomeGridService.RevealArea(s, zone, 80f);
                SignalCoverageService.Invalidate();
                c.Set("bh.relays", string.Join(" ", relays) + " ");
                // 增援夹具（DEBT-FG13E2E01-01）：滩头离核心约 1100 格，清洗打完作战队所剩无几；在登陆信标往核心方向 50 格处补一支 24 台作战队（造车 / 远征行军由 FGJ-M1～M8 覆盖），散开 3 格站位。
                Vector2 toCore = (CorePos - beacon).sqrMagnitude > 1f ? (CorePos - beacon).normalized : Vector2.left;
                Vector2 rally = beacon + toCore * ReinforceCells, side = new Vector2(-toCore.y, toCore.x);
                var roster = new List<string>(c.Get("roster", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                int added = 0;
                for (int i = 0; i < ReinforceSize; i++)
                {
                    Vector2 p = rally + side * (3f * (i % 6 - 2.5f)) + toCore * (3f * (i / 6));
                    GridCell at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), 4);
                    int id = FgjM12Journey.SpawnMachine(s, c.Get(i % 2 == 0 ? "bp.id" : "bp.gun"), new Vector2(at.X, at.Y), SquadHp);
                    if (id > 0)
                    {
                        roster.Add(id.ToString(CultureInfo.InvariantCulture));
                        added++;
                    }
                }
                c.Set("roster", string.Join(",", roster));
                c.Set("bh.reinf", $"{added}/{ReinforceSize} 台于 {Cell(rally)} 附近");
            }
            int alive = BeachheadService.TargetsAlive(s, out int total, out bool beaconAlive);
            string hud = BeachheadService.HudText(s);
            string msg = $"着陆于 {Cell(beacon)}（离核心 {F(Vector2.Distance(beacon, CorePos), "0")} 格）：目标 {alive}/{total}（登陆信标 {beaconAlive}）；顶栏“{hud}”；主线“{Short(BeachheadService.QuestProgressText(s))}”；中继夹具 {c.Get("bh.relays")}；增援夹具 {c.Get("bh.reinf")}";
            // 信号覆盖：滩头四个目标与增援队都要在覆盖内（覆盖外的机器收不到右键命令，FGR 信号覆盖规则）。
            int outOfCov = FieldTeam(c).Count(id => SignalCoverageService.IsMachineOutOfCoverage(id));
            msg += $"；增援在覆盖外 {outOfCov} 台";
            return total == 4 && alive == 4 && hud.Length > 0 && c.Get("bh.reinf", "0/").StartsWith(ReinforceSize + "/") && outOfCov == 0 ? StepOutcome.Done(msg)
                : c.StepElapsed < 5 && outOfCov > 0 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        private static bool BeachGoal(RegionEnemyRecord e) =>
            BeachheadService.IsBeachheadInstance(e.EnemyInstanceId) && (IsTower(e) || BeachheadService.Encounters().Any(x => x.Id == e.EnemyInstanceId && BeachheadService.IsTargetRow(x)));

        private static StepOutcome TickBeachFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            if (!CoreAlive())
            {
                return StepOutcome.Fail("滩头期间归还核心被毁");
            }
            if (FreeTeam(c).Count == 0)
            {
                return StepOutcome.Fail("作战队全灭");
            }
            bool towersAlive = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Any(e => e != null && e.IsAlive && BeachheadService.IsBeachheadInstance(e.EnemyInstanceId) && IsTower(e) && Home.IsEnemyAlive(e.EnemyInstanceId));
            if (BeachheadService.Done(s) && !towersAlive)
            {
                return StepOutcome.Done($"右键攻击 {c.GetInt("bh.rc")} 次：屏蔽塔、3 座轨道锚点与登陆信标全部摧毁；{TeamHp(c)}");
            }
            // 先打离作战队最近的目标；附近（25 格内）有护卫 / 投送下来的敌人先清掉。
            if (!NearestHostile(c, e => BeachheadService.IsBeachheadInstance(e.EnemyInstanceId) && !BeachGoal(e), 25f, out string id, out Vector2 at)
                && !NearestHostile(c, BeachGoal, 9999f, out id, out at))
            {
                return c.StepElapsed < 600 ? StepOutcome.Wait : StepOutcome.Fail("找不到滩头目标");
            }
            RightClickEnemy(c, id, at, "bh.rc");
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 滩头目标 {BeachheadService.TargetsAlive(s, out int t, out _)}/{t}、屏蔽塔在 {towersAlive}；当前目标 {id}@{Cell(at)}；右键 {c.GetInt("bh.rc")} 次；{TeamHp(c)}");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickBeachDone(JourneyContext c)
        {
            CampaignState s = St;
            string msg = $"事件账本 {BeachheadService.Done(s)}；主线“{Short(BeachheadService.QuestProgressText(s))}”；顶栏“{OrbitalHud()}”";
            return BeachheadService.Done(s) ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 5. 先遣旗舰 ─────────────────────────────────────────────────────────────

        private static StepOutcome TickFlagshipStart(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (FlagshipService.PhaseOf(s) != FlagshipService.PhaseOrbital)
            {
                return StepOutcome.Wait;
            }
            string hud = FlagshipService.HudText(s, GameClock.Ticks);
            return StepOutcome.Done($"阶段一：首领 HUD“{hud}”；顶栏“{OrbitalHud()}”；主线 main.flagship {QuestState("main.flagship")}");
        }

        private static StepOutcome TickFlagshipGoals(JourneyContext c, int phase)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            int now = FlagshipService.PhaseOf(s);
            if (now > phase)
            {
                return StepOutcome.Done($"右键攻击 {c.GetInt("fs.rc" + phase)} 次：{FlagshipService.PhaseName(phase)}目标全部摧毁 → {(now == FlagshipService.PhaseDone ? "已被击退" : FlagshipService.PhaseName(now))}；HUD“{FlagshipService.HudText(s, GameClock.Ticks)}”；{TeamHp(c)}");
            }
            if (!CoreAlive())
            {
                return StepOutcome.Fail("旗舰战期间归还核心被毁");
            }
            if (FreeTeam(c).Count == 0)
            {
                return c.StepElapsed < 30 ? StepOutcome.Wait : StepOutcome.Fail("作战队全灭 / 全被夺取");
            }
            var goals = new HashSet<string>(FlagshipService.AllEncounters().Where(e => FlagshipService.IsGoalRow(phase, e)).Select(e => e.Id));
            if (!NearestHostile(c, e => goals.Contains(e.EnemyInstanceId), 9999f, out string id, out Vector2 at))
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"{FlagshipService.PhaseName(phase)}找不到可打的目标（阶段 {now}）");
            }
            RightClickEnemy(c, id, at, "fs.rc" + phase);
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] HUD“{FlagshipService.HudText(s, GameClock.Ticks)}”；当前目标 {id}@{Cell(at)}；右键 {c.GetInt("fs.rc" + phase)} 次；{TeamHp(c)}");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickFortress(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int phase = FlagshipService.PhaseOf(s);
            if (phase > FlagshipService.PhaseFortress)
            {
                return StepOutcome.Done($"{c.Get("fs.node_note")}；夹具击毁 {c.GetInt("fs.node_kills")} 个节点 → {FlagshipService.PhaseName(phase)}；HUD“{FlagshipService.HudText(s, GameClock.Ticks)}”");
            }
            if (phase != FlagshipService.PhaseFortress)
            {
                return StepOutcome.Fail($"阶段 {phase}，不在要塞阶段");
            }
            List<EnemyEncounter> nodes = FlagshipService.Encounters(EnemyDefs.ConditionBoss).Where(FlagshipService.IsNodeRow).ToList();
            if (c.Get("fs.node", string.Empty).Length == 0)
            {
                if (!NearestHostile(c, e => nodes.Any(n => n.Id == e.EnemyInstanceId), 9999f, out string nid, out Vector2 nat) || !FlagshipService.TryNode(s, nid, out byte fam, out float g0, out float max))
                {
                    return c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail("要塞阶段找不到护盾节点");
                }
                c.Set("fs.node", nid);
                c.Set("fs.node_g0", F(g0, "0.##"));
                c.Set("fs.node_fam", FlagshipService.WeaknessText(fam));
                c.SetLong("fs.node_t0", (long)(c.StepElapsed * 1000));
                SetV(c, "fs.node_at", nat);
            }
            string node = c.Get("fs.node");
            if (c.StepElapsed * 1000 - c.GetLong("fs.node_t0") < 40000 && c.GetInt("fs.node_kills") == 0)
            {
                // 真实右键一个节点（攻击）：动能重炮不是它的本族端口，闸门不该被削（FGR-EMG-161）。
                if (Home.TryGetEnemyPosition(node, out Vector2 at))
                {
                    RightClickEnemy(c, node, at, "fs.rc2");
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt("fs.node_kills") == 0)
            {
                FlagshipService.TryNode(s, node, out _, out float g1, out float max1);
                c.Set("fs.node_note", $"右键护盾节点 {node}（只怕：{c.Get("fs.node_fam")}）{c.GetInt("fs.rc2")} 次、约 40 秒：闸门 {c.Get("fs.node_g0")} → {F(g1, "0.##")}/{F(max1, "0")}（悬停写明只怕一族端口）");
                foreach (EnemyEncounter n in nodes)
                {
                    if (Home.TryGetEnemyUnit(n.Id, out int u))
                    {
                        Home.Kernel.Kill(u, 0);
                        c.SetInt("fs.node_kills", c.GetInt("fs.node_kills") + 1);
                    }
                }
            }
            return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"节点击毁后没有进入阶段三（阶段 {FlagshipService.PhaseOf(s)}）");
        }

        internal static void SetV(JourneyContext c, string key, Vector2 v) => c.Set(key, F(v.x, "0.##") + "," + F(v.y, "0.##"));

        private static StepOutcome TickSeized(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            FlagshipState st = FlagshipService.StateOf(s);
            int seized = st.Seized.FirstOrDefault(id => SignalSeizeService.TryGetSeize(id, out bool down, out int src) && src == SignalSeizeService.SourceRoot && !down && Team(c).Contains(id));
            if (seized == 0)
            {
                return FlagshipService.PhaseOf(s) == FlagshipService.PhaseRoot ? StepOutcome.Wait : StepOutcome.Fail($"阶段 {FlagshipService.PhaseOf(s)}，不在根权限阶段");
            }
            c.SetInt("fs.seized", seized);
            c.SetInt("fs.release0", CombatSite.SeizeEndedCount);
            return StepOutcome.Done($"根权限夺取 {Label(seized)}（{Cell(PosOf(seized))}，来源根权限、共用夺控）；HUD“{FlagshipService.HudText(s, GameClock.Ticks)}”；顶栏“{OrbitalHud()}”");
        }

        private static StepOutcome TickSelectSeized(JourneyContext c)
        {
            int t = c.GetInt("fs.seized");
            if (!SignalSeizeService.IsSeized(t))
            {
                return StepOutcome.Fail($"选中前 {Label(t)} 已不在夺控中");
            }
            return FgjM12Journey.SelectMachines(c, new List<int> { t }, "被夺取的机器");
        }

        private static StepOutcome TickReclaim(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int t = c.GetInt("fs.seized");
            if (c.GetLong(FgjM3Common.SK(c, "t0")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "t0"), GameClock.Ticks);
            }
            if (WorldView.Director != null && (WorldView.Director.Mode == ViewMode.Direct || WorldView.Director.HeadingDirect))
            {
                c.SetInt("fs.camera", 1);
            }
            if (SignalSeizeService.IsSeized(t))
            {
                if (Home != null && Home.TryGetSeizedMachine(t, out _, out _, out float left, out _) && left > 0f)
                {
                    c.SetInt(FgjM3Common.SK(c, "began"), 1);
                    c.Set("fs.feedback", SignalUplinkService.LastFeedbackText ?? string.Empty);
                }
                if (c.StepElapsed > 4 && c.GetInt(FgjM3Common.SK(c, "began")) == 0)
                {
                    return StepOutcome.Retry($"按接入键后没有开始反向接入：反馈“{SignalUplinkService.LastFeedbackText}”、拒绝 {SignalUplinkService.LastFailure}；选择集 [{string.Join("、", (Squad?.Selection ?? Array.Empty<int>()).Select(Label))}]");
                }
                return c.StepElapsed < 30 ? StepOutcome.Wait : StepOutcome.Fail($"按键 30 秒后 {Label(t)} 仍被夺取（“{SignalUplinkService.LastFeedbackText}”）");
            }
            double took = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "t0"))) / (double)GameClock.StepHz;
            bool camOk = c.GetInt("fs.camera") == 0 && WorldView.Director.Mode == ViewMode.Strategy;
            bool mine = Home != null && Home.TryGetMachineUnit(t, out int u) && Home.Kernel.TryGetUnit(u, out CombatUnitView v) && v.Faction == CombatFaction.Player;
            string msg = $"按接入键（“{Short(c.Get("fs.feedback"))}”）→ {F(took, "0.00")} 游戏秒后 {Label(t)} 夺回、回到己方 {mine}；镜头一直在战略视角 {camOk}；归还计数 +{CombatSite.SeizeEndedCount - c.GetInt("fs.release0")}";
            return camOk && mine && took >= 2.5 && took <= 4.0 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg + "（要求 3 秒左右夺回、不切直控）");
        }

        private static StepOutcome TickFlagshipDone(JourneyContext c)
        {
            CampaignState s = St;
            bool anySeized = Team(c).Any(SignalSeizeService.IsSeized);
            string msg = $"事件账本 {FlagshipService.Done(s)}；主线 main.flagship {QuestState("main.flagship")}；HUD“{FlagshipService.HudText(s, GameClock.Ticks)}”；阶段存档 {FlagshipService.PhaseSaves} 次；作战队仍被夺取 {anySeized}";
            return FlagshipService.Done(s) && QuestState("main.flagship") == "Completed" && !anySeized && FlagshipService.PhaseSaves >= 4
                ? StepOutcome.Done(msg) : c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        // ── 6. 结局 ─────────────────────────────────────────────────────────────────

        private static StepOutcome TickEndingCutscene(JourneyContext c)
        {
            if (c.GetInt(FgjM3Common.SK(c, "saw")) == 0 && NarrativeCutscenePlayerUIToolkit.IsShowing)
            {
                c.SetInt(FgjM3Common.SK(c, "saw"), 1);
            }
            StepOutcome? cut = JourneyCommon.TickDismissCutscene(c);
            if (cut.HasValue)
            {
                return cut.Value;
            }
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "saw")) == 1 && EndingSettlementPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Done($"结局演出播放 → 跳过并确认 → 结局落盘（账本 {EndingService.Reached(s)}、存档原因 {s.LastSaveReason}）→ 结算打开、世界暂停 {GameClock.Paused}");
            }
            return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"演出出现 {c.GetInt(FgjM3Common.SK(c, "saw"))}、结局 {EndingService.Reached(s)}、结算打开 {EndingSettlementPanelUIToolkit.IsOpen}");
        }

        private static StepOutcome TickSettlement(JourneyContext c)
        {
            CampaignState s = St;
            EndingStats st = EndingService.Stats(s);
            bool blocked = EndingService.BlocksSave(s);
            int survivors = Team(c).Count(Alive);
            string msg = $"统计：归还面积 {st.ReturnedArea}、{st.Days} 天、反应 {st.Reactions}、熔合配方 {st.FusionRecipes}、击退突袭 {st.RaidsRepelled}；作战队幸存 {survivors}/{Team(c).Count}（家园 {SquadSize} + 滩头增援 {ReinforceSize}）；结算期间存档被拦 {blocked}";
            return st.Days >= 1 && st.RaidsRepelled >= 1 && blocked && survivors > 0 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 结局存档交接给 FGJ-M14（FG15-QA-01 修复第 1 轮：DEBT-FG14E2E01-01 的通关存档夹具改由本旅程打出的真实结局提供）──

        /// <summary>交接目录（断点根目录下，gitignore、只在本机；回归包 / 并行脚本把 FGJ-M13 与 FGJ-M14 排在同一车道、M13 在前）。</summary>
        internal static string HandoffDir => Path.Combine(JourneyCheckpoints.RootDir, "handoff", Id + "-ending");

        private const string HandoffSave = "ending.sav";
        private const string HandoffEnv = "env.txt";

        private static StepOutcome TickHandoff(JourneyContext c)
        {
            int slot = CampaignSession.ActiveSlotIndex;
            LoadResult disk = CampaignSaveService.Load(slot);
            if (!disk.Success || !EndingService.Reached(disk.State) || disk.State.Progress.IsPostgame)
            {
                return StepOutcome.Fail($"槽位 {slot + 1} 不是结局存档：读取 {disk.Outcome}、结局 {(disk.Success && EndingService.Reached(disk.State))}、后日谈 {disk.State?.Progress.IsPostgame}");
            }
            // FG15-QA-03：种子测试集的同步骤旅程（FGJ-SEED-*）交接到各自的目录，不覆盖 FGJ-M14 读的那一份。
            string dir = c.Journey.Id == Id ? HandoffDir : HandoffDir + "-" + c.Journey.Id;
            Directory.CreateDirectory(dir);
            File.Copy(CampaignSaveService.SlotPath(slot), Path.Combine(dir, HandoffSave), true);
            JourneyCheckpoints.EnvInfo env = JourneyCheckpoints.EnvInfo.Current();
            File.WriteAllLines(Path.Combine(dir, HandoffEnv), new[]
            {
                "code=" + env.CodeHash, "schema=" + env.SaveSchema.ToString(CultureInfo.InvariantCulture), "content=" + env.ContentVersion.ToString(CultureInfo.InvariantCulture),
                "generator=" + env.GeneratorVersion.ToString(CultureInfo.InvariantCulture), "slot=" + slot.ToString(CultureInfo.InvariantCulture),
                "ticks=" + disk.State.Clock.Ticks.ToString(CultureInfo.InvariantCulture), "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                // 指纹只核交接那一刻的代码；从断点续跑（尤其 --allow-code-change）时前半段世界不是这份代码跑出来的，如实记下，FGJ-M14 读入时写进结果。
                "resumed=" + (c.Journey.ResumedFrom ?? string.Empty), "oldcode=" + (JourneyBotHost.ResumedWithOldCode ? "1" : "0"),
            });
            return StepOutcome.Done($"结局存档（槽位 {slot + 1}，第 {disk.State.Clock.Ticks} 步，存档原因 {disk.State.LastSaveReason}）抄到 {dir}；代码指纹 {env.CodeHash}；{Provenance(c.Journey.ResumedFrom, JourneyBotHost.ResumedWithOldCode ? "1" : "0")}");
        }

        /// <summary>
        /// FGJ-M14 读入交接的结局存档：写进测试存档目录（<see cref="CampaignSaveService.SaveDirectory"/>，进 Play 时已改到临时目录）的同一槽位，之后由玩家路径“读取”。
        /// 没有交接、或交接来自另一份代码 / 存档格式 / 内容 / 生成器版本时返回 false 并写明“先重跑 FGJ-M13”——不退回夹具。
        /// </summary>
        internal static bool TryImportEnding(out int slot, out long ticks, out string note)
        {
            slot = 0;
            ticks = 0;
            string dir = HandoffDir;
            string save = Path.Combine(dir, HandoffSave);
            string envPath = Path.Combine(dir, HandoffEnv);
            if (!File.Exists(save) || !File.Exists(envPath))
            {
                note = $"没有 FGJ-M13 留下的结局存档交接（{dir}）：先跑 bash tools/unity-journey.sh FGJ-M13（回归包 / 并行脚本会把两条排在同一车道、M13 在前）";
                return false;
            }
            Dictionary<string, string> kv = File.ReadAllLines(envPath).Select(l => l.Split(new[] { '=' }, 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            JourneyCheckpoints.EnvInfo env = JourneyCheckpoints.EnvInfo.Current();
            string V(string k) => kv.TryGetValue(k, out string v) ? v : "?";
            string I(int v) => v.ToString(CultureInfo.InvariantCulture);
            if (V("code") != env.CodeHash || V("schema") != I(env.SaveSchema) || V("content") != I(env.ContentVersion) || V("generator") != I(env.GeneratorVersion))
            {
                note = $"结局存档交接（{V("at")}）来自另一份代码：指纹 {V("code")} / 格式 {V("schema")} / 内容 {V("content")} / 生成器 {V("generator")}，" +
                       $"当前 {env.CodeHash} / {env.SaveSchema} / {env.ContentVersion} / {env.GeneratorVersion}：先重跑 FGJ-M13";
                return false;
            }
            slot = int.Parse(V("slot"), CultureInfo.InvariantCulture);
            ticks = long.Parse(V("ticks"), CultureInfo.InvariantCulture);
            File.Copy(save, CampaignSaveService.SlotPath(slot), true);
            note = $"读入 FGJ-M13 {V("at")} 交接的结局存档 → 槽位 {slot + 1}（第 {ticks} 步，代码指纹一致 {env.CodeHash}；{Provenance(kv.TryGetValue("resumed", out string r) ? r : null, V("oldcode"))}）";
            return true;
        }

        /// <summary>交接存档的来历：完整跑 / 从断点续跑 / 从旧代码断点续跑（结论只供迭代参考）/ 旧交接没记。</summary>
        private static string Provenance(string resumedFrom, string oldCode) =>
            resumedFrom == null ? "交接没记来历（修复第 2 轮之前写的）"
            : resumedFrom.Length == 0 ? "FGJ-M13 从主菜单完整跑出的结局"
            : oldCode == "1" ? $"FGJ-M13 从旧代码断点“{resumedFrom}”续跑（--allow-code-change）：前半段世界是旧代码跑出来的，结论只供迭代参考"
            : $"FGJ-M13 从断点“{resumedFrom}”续跑（断点与当前代码同一指纹）";

        internal static StepOutcome TickContinue(JourneyContext c)
        {
            CampaignState s = St;
            if (s.Progress.IsPostgame && !EndingSettlementPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Done($"点“继续”：后日谈 {s.Progress.IsPostgame}、结算关闭、存档不再被拦 {!EndingService.BlocksSave(s)}、存档原因 {s.LastSaveReason}、世界暂停 {GameClock.Paused}");
            }
            if (!FgjM3Common.Once(c, "click", () => JourneyInput.ClickUitk("[EndingSettlementHost]", "EsContinue")))
            {
                return JourneyInput.LastUiTransient || c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("点不到“继续”：" + JourneyInput.LastUiFailure);
            }
            return FgjM3Common.SinceMs(c, "click") < 2000 ? StepOutcome.Wait : StepOutcome.Retry("点了“继续”没有进入后日谈");
        }
    }
}
