using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-E2E-01 自检 [M13 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M13 跑，FGT-END-009）。
    /// 轻量口径 b（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——终局六个系统（返航工程、揭示、白潮舰队单位、清洗波次、白潮滩头、先遣旗舰、结局）
    /// 的六类反向在 M13 各 Story 的自检里都是真实系统跑出来的断言，已并入全量自检；本段 R 守护这张映射：每一类至少一条断言仍在源码里、所在段仍登记在全量自检，
    /// 删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M13 覆盖里程碑原文（五个巨构阶段 → 揭示 → 守住三波清洗 → 滩头摧毁轨道锚点 → 先遣旗舰（阶段三反向接入夺回）→ 结局 → 选择继续）；
    ///    固定种子；断点都是旅程里的步骤；反向接入步骤读共用夺控事件计数（FGR-EMG-165，与 FgEmgSeizeSelfCheck 同一内核计数）。
    /// A2. FGJ-M13 源码只走输入通道；登记的夹具（DEBT-FG13E2E01-01 / 02）只在登记的方法里、恰好 N 处。
    /// R1～R6. IC-REQ-022 六类反向映射（R5 存读档：六段各一条真文件存读档）+ 里程碑风险“根权限可能夺取玩家所在的机器”（FGT-END-007）+ FG11 自动验收 FGT-END-001～008 映射。
    /// C. 缺口清零：门禁第一个里程碑是 M13 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延；M13 承接项都已 Closed；本 Story 的登记在表里、承接写进卡片正文。
    /// D. 试玩包：FG-M13 四份材料齐全，含 FG11 第 12 节三问、FG02 第 9 节第 4 问与 FG19 第 17 节，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”各项有自检段且登记在全量自检，并各有 FGJ-M13 步骤。
    /// F. 本卡产品修复的行为守护：固件表每一行的获取途径都能识别（白潮预制固件 “fleet” = 区域战场掉落，不再报“未知的获取途径”）、每一行的图标文件都在热更资源里。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM13SelfCheck）；单段：bash tools/unity-validate.sh --segment FgMilestoneM13SelfCheck。
    /// </summary>
    public static class FgMilestoneM13SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";
        private const string Cards = "production/design/full-game/FG-STORY-CARDS.md";
        private const string Icons = "TEngine/UnityProject/Assets/GameRes/Raw/UI/Icons/";

        [MenuItem("QA/自检/M13 出口")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[M13 出口] FG13-E2E-01：旅程登记与共用夺控计数、正式输入与夹具守护、IC-REQ-022 六类反向（真实系统断言映射）与里程碑风险、缺口清零、试玩包、里程碑需求覆盖、本卡产品修复");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckFirmwareSourcesAndIcons);
            Line($"  · [M13 出口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        private static string JourneyCode() => StripComments(ReadRepo(Bots + "FgjM13Journey.cs") ?? string.Empty);

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M13 覆盖里程碑原文；固定种子；断点都是旅程里的步骤；反向接入用共用夺控事件计数");
            JourneyDef m13 = JourneyCatalog.Create(FgjM13Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子、3 倍速", new[] { "menu_new", "new_game", "seed", "speed3" }),
                ("完成五个巨构阶段（建造菜单“终局”页放下返航工地、第五阶段真实送料计时完工）", new[] { "m_build", "m_pick", "m_place", "m_stage5", "mega_done" }),
                ("揭示（选中工地点“点亮信标…”→ 确认点亮 → 跳过演出 → 揭示落定）", new[] { "r_open", "r_btn", "r_confirm", "r_skip", "reveal_state" }),
                ("守住三波清洗（每波读预报、散开驻防、守卫键、撑过）", new[] { "p_start", "p1_fight", "p2_fight", "p3_fight", "purge_done" }),
                ("进攻白潮滩头，摧毁轨道锚点（右键攻击）", new[] { "b_land", "b_sel", "b_fight", "beach_done" }),
                ("击败先遣旗舰，阶段三用反向接入夺回被夺取的机器", new[] { "fs_start", "fs_p1", "fs_p2", "fs_seized", "fs_selseized", "fs_reclaim", "fs_p3", "fs_p4", "fs_done" }),
                ("结局 → 结算 → 选择继续", new[] { "e_cut", "e_stats", "e_continue" }),
            };
            List<string> missing = exit.Where(x => m13 == null || x.Ids.Any(id => m13.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            bool wellformed = m13 != null && m13.Steps.Count > 0 && m13.Steps.Select(x => x.Id).Distinct().Count() == m13.Steps.Count
                              && m13.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && m13.TotalTimeoutSeconds > 0;
            Expect(wellformed && missing.Count == 0 && m13.Seed == FgjM0Journey.TestSeed,
                $"FGJ-M13：{m13?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m13?.Seed}；里程碑原文七段都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m13 != null && m13.CheckpointAfter.Length >= 1 && m13.CheckpointAfter.All(id => m13.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM13Journey.Id) && JourneyCatalog.Ids.Contains(FgjM12Journey.Id),
                $"FGJ-M13 登记在旅程登记表、前序旅程 FGJ-M12 仍在；断点 [{string.Join(", ", m13?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
            string code = JourneyCode();
            string seize = ReadRepo(Qa + "FgEmgSeizeSelfCheck.cs") ?? string.Empty;
            bool shared = code.Contains("CombatSite.SeizeStarted") && code.Contains("c.SetInt(\"fs.release0\", CombatSite.SeizeEndedCount)")
                          && code.Contains("CombatSite.SeizeEndedCount - c.GetInt(\"fs.release0\")")
                          && seize.Contains("CombatSite.SeizeStarted == started + 1") && seize.Contains("CombatSite.SeizeEndedCount");
            Expect(shared, "阶段三反向接入步骤读共用夺控事件计数：FGJ-M13 fs_seized / fs_reclaim（CombatSite.SeizeStarted / SeizeEndedCount）与 FgEmgSeizeSelfCheck 读同一内核计数");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "BuildingOps.Try",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request",
            "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "ExpeditionDepartureService.TryDepart(",
            "ExpeditionMarchService.IssueCommand(", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(", "WorldView.FlyTo(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", ".ApplyDamage(", "CampaignObjectiveTracker.Complete", "RaidDirectorService.RequestStoryRaid(", "WorldTransitSystem.Dispatch",
            "LootPickService.Grant", "SignalSeizeService.Begin", "SignalSeizeService.BlockSlot(", "SignalSeizeService.Release", "EventDirectorService.Complete(", "EventDirectorService.SetHistoryReward(",
            "FieldLootService.On", "IntelService.Grant", "NarrativeService.Grant(", "PrimitiveInventory.Grant", "EnemySpawnBuilder.", "EventDirectorService.Request(",
            "MegastructureService.Complete", "MegastructureService.Advance", "FlagshipService.Begin", "FlagshipService.Advance", "PurgeWaveService.Begin", "BeachheadService.Begin",
            "EndingService.Begin(", "EndingService.OpenSettlement(", "EndingService.ContinuePostgame(", "RevealService.Confirm(", "MachineRegistry.SpawnMachine(", "MachineLoadoutRegistry.Register(",
            // 夹具 token：只在登记的方法里免扫（见 Fixtures）。
            "s.Progress.Act = ", "s.UnlockedContentIds = ", "FgjM12Journey.SaveBlueprint(", "FgjM12Journey.SpawnMachine(", "FgProductionSelfCheck.Built(", "FgProductionSelfCheck.PowerUp(",
            "HomeInventory.Add(", "b.Health = ", "HomeGridService.RevealArea(", "RelayChain(", "CampaignEventLedger.TryGrant(", "Kernel.Kill(", "s.DifficultyId = ",
            "EventDirectorService.SetFrequency(", "m.StageIndex = ", "m.Done = ", "s.ObjectiveRecords = ", "d.Schedules = ", "Mega.CompleteMegaQuest(", "Mega.Supply(", "Mega.SetPower(",
            "b.ConstructionState = ",
        };

        /// <summary>夹具（ADR-QA-030 第 4 节）：token 只能出现在登记的方法体里，且恰好 N 处。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("Mega.CompleteMegaQuest(", "ApplyFixture", 1, "正式任务完成入口发巨构蓝图、进第三幕 DEBT-FG13E2E01-01"),
            ("s.ObjectiveRecords = ", "ApplyFixture", 1, "主线“巨构蓝图”记为已完成 DEBT-FG13E2E01-01"),
            ("EventDirectorService.SetFrequency(", "ApplyFixture", 1, "事件频率关闭 DEBT-FG13E2E01-01"),
            ("s.DifficultyId = ", "ApplyFixture", 1, "难度建造者 DEBT-FG13E2E01-01"),
            ("d.Schedules = ", "ApplyFixture", 1, "第一次信号幽灵记为已发生 DEBT-FG13E2E01-01"),
            ("m.StageIndex = ", "ApplyFixture", 1, "前四阶段记为已完工 DEBT-FG13E2E01-01"),
            ("m.Done = ", "ApplyFixture", 1, "前四阶段完工记录 DEBT-FG13E2E01-01"),
            ("s.UnlockedContentIds = ", "ApplyFixture", 1, "重炮 / 机枪记为已获得 DEBT-FG13E2E01-01"),
            ("FgjM12Journey.SaveBlueprint(", "ApplyFixture", 2, "重炮作战 / 机枪作战蓝图 DEBT-FG13E2E01-01"),
            ("FgjM12Journey.SpawnMachine(", "ApplyFixture", 1, "作战队 36 台耐久 5000 DEBT-FG13E2E01-01"),
            ("FgjM12Journey.SpawnMachine(", "TickBeachLanded", 1, "滩头增援 24 台 DEBT-FG13E2E01-01"),
            ("b.ConstructionState = ", "ApplyRig", 1, "工地施工虚影记为建成 DEBT-FG13E2E01-01"),
            ("b.Health = ", "ApplyRig", 1, "工地满耐久 DEBT-FG13E2E01-01"),
            ("Mega.Supply(", "ApplyRig", 1, "工地构件 / 冷却液供给与供电 DEBT-FG13E2E01-01"),
            ("Mega.SetPower(", "TickStage5", 1, "工地供电被电网结算改回时再置 DEBT-FG13E2E01-01"),
            ("Mega.SetPower(", "PurgeWatch", 1, "维修站供电被电网结算改回时再置 DEBT-FG13E2E01-01"),
            ("Mega.SetPower(", "ApplyRepairStations", 1, "维修站供电 DEBT-FG13E2E01-01"),
            ("FgProductionSelfCheck.Built(", "ApplyRepairStations", 1, "核心旁两座维修无人机站 DEBT-FG13E2E01-01"),
            ("b.Health = ", "ApplyRepairStations", 1, "维修站满耐久 DEBT-FG13E2E01-01"),
            ("HomeInventory.Add(", "ApplyRepairStations", 1, "仓库 120 维修件 DEBT-FG13E2E01-01"),
            ("CampaignEventLedger.TryGrant(", "TickBeachLanded", 1, "防腐涂层蓝图记入事件账本 DEBT-FG13E2E01-01"),
            ("RelayChain(", "TickBeachLanded", 2, "往登陆信标 / 竞技场的中继链（每座过正式放置校验）DEBT-FG13E2E01-01"),
            ("HomeGridService.RevealArea(", "TickBeachLanded", 2, "登陆信标与竞技场揭雾 DEBT-FG13E2E01-01"),
            ("Kernel.Kill(", "TickFortress", 1, "阶段二四个护盾节点由夹具击毁 DEBT-FG13E2E01-02"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M13 源码只走输入通道；夹具（DEBT-FG13E2E01-01 / 02）只在登记的方法里");
            string code = JourneyCode();
            if (!code.Contains("class FgjM13Journey"))
            {
                Fail("找不到旅程源码 FgjM13Journey.cs");
                return;
            }
            string scan = code;
            foreach (var (token, method, _, _) in Fixtures)
            {
                string body = MethodBody(scan, method);
                if (body != null)
                {
                    scan = scan.Replace(body, body.Replace(token, string.Empty));
                }
            }
            List<string> hits = ForbiddenCalls.Where(scan.Contains).ToList();
            Expect(hits.Count == 0, $"FGJ-M13 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种；登记夹具方法里的登记调用除外）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            var bad = new List<string>();
            var seen = new List<string>();
            foreach ((string token, string method, int count, string debt) in Fixtures)
            {
                int total = Count(code, token);
                string body = MethodBody(code, method);
                int n = body == null ? 0 : Count(body, token);
                seen.Add($"{debt}：{method} {n} 处");
                int registered = Fixtures.Where(x => x.Token == token).Sum(x => { string b = MethodBody(code, x.Method); return b == null ? 0 : Count(b, token); });
                if (n != count || total != registered)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、登记方法外 {total - registered} 处");
                }
            }
            Expect(bad.Count == 0, $"夹具只在登记的方法里（{Fixtures.Length} 项：{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string[] inputs =
            {
                "FgjM3Common.TryClick(", "JourneyInput.ClickScreen(", "JourneyInput.ClickUitk(", "JourneyInput.ScrollIntoView(", "JourneyInput.PressAction(", "JourneyInput.PressToggleTo(",
                "FgjM12Journey.SelectMachines(", "RightClickEnemy(", "GameActionId.SpeedTriple", "GameActionId.CommandGuard", "GameActionId.TogglePause", "GameActionId.ToggleCameraView",
            };
            List<string> miss = inputs.Where(x => !code.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：世界点击（左键选 / Shift 加选 / 右键地面移动 / 右键敌人攻击）、建造菜单与面板按钮（UI Toolkit）、滚动区滚动、3 倍速键、守卫键、暂停键、接入键（反向接入）" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（终局段）：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；R5 六段各一条真文件存读档；里程碑风险“根权限可能夺取玩家所在的机器”；FGT-END-001～008 映射");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string mega = "FgMegastructureSelfCheck.cs", reveal = "FgMegastructureSelfCheck.Reveal.cs", ending = "FgMegastructureSelfCheck.Ending.cs";
            var map = new (string Category, string Segment, string File, string Label)[]
            {
                ("R1 条件不足：没有巨构蓝图时返航工地放置被拦、写明蓝图来源", "FgMegastructureSelfCheck", mega, "B1 有巨构蓝图才能开工"),
                ("R1 条件不足：构件源与冷却液同时断 → 进度停在原处不倒退、写缺料，恢复后接着走", "FgMegastructureSelfCheck", mega, "N4 负向“物资供给中断”"),
                ("R1 条件不足：真实电网供不上 500 → 工地没电、供电计时不动", "FgMegastructureSelfCheck", mega, "N5 持续供电：真实电网供不上 500 时工地没电"),
                ("R1 条件不足：玩家只有 1 台机器 → 根权限空转", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "F1 负向：玩家只有 1 台机器"),
                ("R2 目标死亡：工地被摧毁 → 三项停在原处、面板写被摧毁", "FgMegastructureSelfCheck", mega, "N2 负向“工地被摧毁”"),
                ("R2 目标死亡：清洗中家园核心被摧毁 → 存档被拒、清洗不再推进、失败页写明", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "C1 负向：第 2 波来袭时核心被摧毁"),
                ("R2 目标死亡：倒计时中摧毁轨道打击信标 → 打击取消、圈内机器不掉血", "FgFleetFactionSelfCheck", "FgFleetFactionSelfCheck.cs", "D2 负向：倒计时第 5 秒摧毁信标"),
                ("R2 目标死亡：登陆信标被毁 → 通知、掉废料、焦土恢复", "FgBeachheadSelfCheck", "FgBeachheadSelfCheck.cs", "F1 登陆信标被毁"),
                ("R2 目标死亡：被夺取的机器被击倒 → 瘫痪不阵亡", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "D3 被夺取的机器被击倒后瘫痪"),
                ("R2 目标死亡：阶段二 / 三竞技场里可用机器全灭 → 从该阶段开始重试", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "G2 负向：阶段三中可用机器全灭"),
                ("R2 目标死亡：没击退旗舰不投放结局 / 不开结算 / 按钮无效", "FgEndingSelfCheck", ending, "E5 负向：没击退不投放"),
                ("R3 失联：被夺取的机器在信号覆盖外按接入键被拒，回覆盖内夺回", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "D4 反向接入（正式入口 SignalUplinkService.Request）：覆盖外被拒"),
                ("R3 失联：屏蔽塔范围内拒绝接入并写明原因、已接入的不被踢出", "FgFleetFactionSelfCheck", "FgFleetFactionSelfCheck.cs", "E1 屏蔽塔（FG09 第 8 章"),
                ("R3 失联：家园不被观察时滩头着陆、投送与舱落地结果与观察时一致", "FgBeachheadSelfCheck", "FgBeachheadSelfCheck.cs", "K1 家园不被观察时滩头着陆"),
                ("R3 失联：家园观察 / 不观察时信标点火 → 待点亮一致", "FgRevealSelfCheck", reveal, "R6 家园观察 / 不观察一致"),
                ("R4 暂停与倍速：返航工程", "FgMegastructureSelfCheck", mega, "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：揭示演出（真实秒、与倍速无关）", "FgRevealSelfCheck", reveal, "R5 暂停与倍速："),
                ("R4 暂停与倍速：轨道打击", "FgFleetFactionSelfCheck", "FgFleetFactionSelfCheck.cs", "K1 暂停 6 秒轨道打击不落"),
                ("R4 暂停与倍速：清洗波次", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "E1 清洗波次按游戏时间："),
                ("R4 暂停与倍速：滩头持续投送", "FgBeachheadSelfCheck", "FgBeachheadSelfCheck.cs", "J1 持续投送按游戏时间："),
                ("R4 暂停与倍速：根权限夺取间隔", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "J1 根权限夺取间隔按游戏时间："),
                ("R4 暂停与倍速：结算期间", "FgEndingSelfCheck", ending, "E6 暂停与倍速矩阵"),
                ("R5 存读档：返航工程推进中", "FgMegastructureSelfCheck", mega, "S1 推进中真文件存读档"),
                ("R5 存读档：揭示演出中退出游戏 → 读回演出前自动存档重新播放", "FgRevealSelfCheck", reveal, "R2 负向“揭示演出中退出游戏”"),
                ("R5 存读档：清洗第 1 波已发动、轨道打击倒计时中", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "F1 存读档：存档时第 1 波已发动"),
                ("R5 存读档：核心被毁后读最近自动存档回到第 2 波开始前", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "C2 读取最近自动存档：回到第 2 波开始前"),
                ("R5 存读档：滩头已投送 1 舱、登陆信标已毁", "FgBeachheadSelfCheck", "FgBeachheadSelfCheck.cs", "I1 存读档：存档时已投送 1 舱"),
                ("R5 存读档：旗舰阶段三已夺取 1 台", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "I1 存读档：阶段三已夺取 1 台时存档"),
                ("R5 存读档：结局演出中退出 / 结算返回主菜单 / 读回结局存档", "FgEndingSelfCheck", ending, "E3 中途退出与读档"),
                ("R6 路径 / 地点失败：滩头锚点在多个种子下落在预留区里、可走格上（不在悬崖 / 水面）", "FgBeachheadSelfCheck", "FgBeachheadSelfCheck.cs", "在悬崖 / 水面上"),
                ("R6 路径 / 地点失败：旗舰点位在多个种子下落在竞技场里、可走格上", "FgFlagshipSelfCheck", "FgFlagshipSelfCheck.cs", "在悬崖 / 水面上"),
                ("R6 路径 / 地点失败：家园只剩核心时轨道打击落在核心外（不直接轰核心）", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "家园只剩核心时落在核心外"),
                ("R6 路径 / 地点失败：建筑都只是规划虚影时打击不锁定它们", "FgPurgeWaveSelfCheck", "FgPurgeWaveSelfCheck.cs", "D4 负向：建筑都只是规划虚影时不锁定它们"),
            };
            var bad = new List<string>();
            var cats = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string category, string segment, string file, string label) in map)
            {
                string src = ReadRepo(Qa + file);
                if (src == null || !src.Contains(label) || !segments.Contains(segment))
                {
                    bad.Add($"{category.Substring(0, 2)}（{segment} / “{label}”）");
                }
                else
                {
                    cats.Add(category.Substring(0, 2));
                }
            }
            Expect(bad.Count == 0 && cats.Count == 6, $"六类反向各有起真实系统的断言（{map.Length} 条，覆盖 {string.Join(" / ", cats.OrderBy(x => x))}）且所在段都登记在全量自检" +
                                                    (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
            var saveSegs = map.Where(x => x.Category.StartsWith("R5", StringComparison.Ordinal)).Select(x => x.Segment).Distinct().ToList();
            Expect(saveSegs.Count == 6, $"存读档：返航工程、揭示、清洗、滩头、旗舰、结局六段各有真文件存读档断言（{string.Join("、", saveSegs)}）");
            // 里程碑风险“根权限可能夺取玩家所在的机器”（FGT-END-007）：自检断言 + 旅程里玩家可见的夺取 → 反向接入步骤。
            string flag = ReadRepo(Qa + "FgFlagshipSelfCheck.cs") ?? string.Empty;
            JourneyDef m13 = JourneyCatalog.Create(FgjM13Journey.Id);
            bool risk = segments.Contains("FgFlagshipSelfCheck") && flag.Contains("离中继最近的是玩家所在的那台（不夺取）") && flag.Contains("F1 负向：玩家只有 1 台机器——接入在它里面时根权限每次都空转");
            bool journey = m13 != null && new[] { "fs_seized", "fs_selseized", "fs_reclaim" }.All(id => m13.Steps.Any(s => s.Id == id));
            Expect(risk && journey, "里程碑风险“根权限可能夺取玩家所在的机器”：阶段三不夺取玩家所在的那台、夺次近的（FgFlagshipSelfCheck D1，FGT-END-007）、只剩接入的那台时空转（F1）；" +
                                    "FGJ-M13 真实输入走过夺取 → 选中被夺取的机器 → 接入键 3 秒夺回（fs_seized / fs_selseized / fs_reclaim）");
            var fgt = new (string Id, string Segment)[]
            {
                ("FGT-END-001 剧情节点逐个出现、进资料库", "FgRevealSelfCheck"), ("FGT-END-002 巨构", "FgMegastructureSelfCheck"), ("FGT-END-003 揭示", "FgRevealSelfCheck"),
                ("FGT-END-004 清洗波次", "FgPurgeWaveSelfCheck"), ("FGT-END-005 登陆场", "FgBeachheadSelfCheck"), ("FGT-END-006 先遣旗舰三阶段", "FgFlagshipSelfCheck"),
                ("FGT-END-007 根权限不夺取玩家所在的机器", "FgFlagshipSelfCheck"), ("FGT-END-008 结局", "FgEndingSelfCheck"),
            };
            List<string> lost = fgt.Where(x => !segments.Contains(x.Segment)).Select(x => x.Id).ToList();
            Expect(lost.Count == 0, $"FG11 第 11 节自动验收 FGT-END-001～008 各有登记在全量自检的段，FGT-END-009 = FGJ-M13{(lost.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lost))}");
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        private static bool PartialRemainderDeferred(string gate, string st)
        {
            if (!st.StartsWith("部分关闭", StringComparison.Ordinal) || !st.Contains("剩"))
            {
                return false;
            }
            foreach (Match m in FirstMilestone.Matches(gate ?? string.Empty))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 13)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered = { "DEBT-FG13E2E01-01", "DEBT-FG13E2E01-02", "DEBT-FG13E2E01-03", "FG-GAP-249", "FG-GAP-250", "FG-GAP-251" };

        /// <summary>承接写着 M13 各卡的条目（含门禁写 M13 的跨阶段承接）：出口时都要 Closed。</summary>
        private static readonly string[] ClosedHere = { "DEBT-FG6E2E01-03", "DEBT-FG8QST01-03", "DEBT-FG9NAR01-01", "DEBT-FG9UX01-02", "FG-GAP-248" };

        private static readonly string[] FixtureDebts = { "DEBT-FG13E2E01-01", "DEBT-FG13E2E01-02" };

        private static readonly string[] BalanceGaps = { "FG-GAP-249", "FG-GAP-250", "FG-GAP-251" };

        private static string CardBody(string cards, string id)
        {
            int at = cards.IndexOf("### " + id + " ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cards.IndexOf("\n### ", at + 10, StringComparison.Ordinal);
            return at < 0 ? string.Empty : end < 0 ? cards.Substring(at) : cards.Substring(at, end - at);
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M13 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（按门禁列的第一个里程碑判定）；M13 承接项都已 Closed；本 Story 的登记与卡片正文承接");
            string text = ReadRepo(Register);
            if (text == null)
            {
                Fail("找不到 " + Register + "（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            int gapRows = 0, debtRows = 0, m13Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                bool gap = raw.StartsWith("| FG-GAP-", StringComparison.Ordinal);
                if (!gap && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1].TrimStart('*');
                status[cols[0]] = st;
                rows[cols[0]] = raw;
                if (gap)
                {
                    gapRows++;
                }
                else
                {
                    debtRows++;
                }
                string gate = gap ? (cols.Length >= 7 ? cols[5] : null) : (cols.Length >= 9 ? cols[6] : null);
                if (gate == null || FirstMilestoneOf(gate) != 13)
                {
                    continue;
                }
                m13Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Expect(gapRows > 200 && debtRows > 500 && m13Rows >= 4, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M13 的 {m13Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M13 的缺口 / 延后项全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明理由与新门禁）"
                    : $"FG-M13 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            Expect(notClosed.Count == 0, $"承接写着 M13 各卡的 {ClosedHere.Length} 条都是 Closed（状态格以 Closed 开头）{(notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed))}");
            string cards = ReadRepo(Cards) ?? string.Empty;
            string qa01 = CardBody(cards, "FG15-QA-01");
            string qa02 = CardBody(cards, "FG15-QA-02");
            string bal = CardBody(cards, "FG15-BAL-01");
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG13E2E01-03", out string p) && p.Contains("待用户");
            List<string> uncarried = FixtureDebts.Where(id => !rows.TryGetValue(id, out string r) || !r.Contains("FG15-QA-01") || !qa01.Contains(id)).ToList();
            uncarried.AddRange(BalanceGaps.Where(id => !rows.TryGetValue(id, out string r) || !r.Contains("FG15-BAL-01") || !bal.Contains(id)));
            bool playCarried = rows.TryGetValue("DEBT-FG13E2E01-03", out string pr) && pr.Contains("FG15-QA-02") && qa02.Contains("DEBT-FG13E2E01-03");
            Expect(unregistered.Count == 0 && pending && uncarried.Count == 0 && playCarried,
                "本 Story 的登记在表里（夹具 DEBT-FG13E2E01-01 / 02、试玩 -03 待用户、平衡观察 FG-GAP-249～251）；夹具债承接 FG15-QA-01、试玩债承接 FG15-QA-02、平衡观察承接 FG15-BAL-01，三张卡正文都有承接行（规则第 7 条）" +
                (unregistered.Count + uncarried.Count == 0 && playCarried ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；承接缺：{string.Join("、", uncarried)}；试玩承接 {playCarried}"));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M13 材料齐全（必试玩，真人试玩由用户择时进行，DEBT-FG13E2E01-03，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M13-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M13-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M13-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M13-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            int at = readme.IndexOf("## FG-M13 第三幕与终局", StringComparison.Ordinal);
            string section = at >= 0 ? readme.Substring(at) : string.Empty;
            bool registered = section.Contains("DEBT-FG13E2E01-03") && section.Contains("不阻塞") && section.Contains("FGJ-M13");
            bool covers = script.Contains("FG11 第 12 节") && script.Contains("FG02 第 9 节第 4 问") && script.Contains("FG19 第 17 节") && script.Contains("根权限")
                          && script.Contains("揭示") && script.Contains("清洗") && script.Contains("滩头") && script.Contains("旗舰") && script.Contains("结局")
                          && script.Contains("5 名玩家") && table.Contains("T10") && table.Contains("5 人试玩达到 FG-M13 标准");
            Expect(registered && covers, $"目录登记“待用户试玩、不阻塞”并写明 FGJ-M13 断点存档（{registered}）；脚本覆盖里程碑原文六段 + FG11 第 12 节三问、FG02 第 9 节第 4 问、FG19 第 17 节、5 人，对照表 T1～T10（{covers}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M13“必须完成”各项有自检段且登记在全量自检，并各有 FGJ-M13 步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m13 = JourneyCatalog.Create(FgjM13Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("返航工程 FGR-END-001、002（FG13-END-01）", "FgMegastructureSelfCheck", "mega_done"),
                ("揭示 FGR-END-010（FG13-END-02）", "FgRevealSelfCheck", "reveal_state"),
                ("舰队单位与白潮专属语法 FG09 第 8 章、FGR-FAC-008（FG13-FAC-01）", "FgFleetFactionSelfCheck", "p1_fight"),
                ("清洗波次 FGR-END-020（FG13-END-03）", "FgPurgeWaveSelfCheck", "purge_done"),
                ("白潮滩头 FGR-END-021（FG13-END-04）", "FgBeachheadSelfCheck", "beach_done"),
                ("先遣旗舰 FGR-END-022（FG13-END-05）", "FgFlagshipSelfCheck", "fs_done"),
                ("结局、结算与纪念 FGR-END-030～032（FG13-END-06）", "FgEndingSelfCheck", "e_continue"),
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = m13?.Steps.Any(s => s.Id == step) ?? false;
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM13SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，并各有 FGJ-M13 旅程步骤；[M13 出口] 本身登记在全量自检" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── F. 本卡产品修复 ────────────────────────────────────────────────────────

        private static readonly string[] KnownSources = { "base", "terminal", "cache", "relic", "salvage", "elite", "boss", "fusion", "fleet" };

        private static void CheckFirmwareSourcesAndIcons()
        {
            Line("  · F. 本卡产品修复：固件获取途径全部可识别（白潮预制固件 fleet）、每条固件的图标都在热更资源里（FGJ-M13 第 1 次运行“新建”时 6 条报错的回归守护）");
            IReadOnlyList<GameConfig.fg.FirmwareKind> rows = FirmwareKinds.Rows;
            if (rows == null || rows.Count == 0)
            {
                Fail($"固件表没有行（{FirmwareKinds.LoadError}）");
                return;
            }
            var errors = new List<string>();
            void OnLog(string msg, string stack, LogType type)
            {
                if ((type == LogType.Error || type == LogType.Exception) && msg != null && msg.Contains("[FirmwareCatalog]"))
                {
                    errors.Add(msg);
                }
            }
            Application.logMessageReceived += OnLog;
            var fleet = new List<string>();
            try
            {
                FirmwareCatalog.Invalidate();
                foreach (GameConfig.fg.FirmwareKind row in rows.Where(r => r != null && r.Source == "fleet"))
                {
                    bool ok = FirmwareCatalog.TryGet(row.Id, out MechanicalContentDef def) && def.Source == MechanicalContentSource.RegionSalvage;
                    fleet.Add($"{row.Id} {(ok ? "区域战场掉落" : "错")}");
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            List<string> unknown = rows.Where(r => r != null && !KnownSources.Contains(r.Source)).Select(r => $"{r.Id}:{r.Source}").ToList();
            Expect(errors.Count == 0 && unknown.Count == 0 && fleet.Count >= 3 && fleet.All(x => x.EndsWith("区域战场掉落", StringComparison.Ordinal)),
                $"重建固件目录：{rows.Count} 行获取途径都可识别、无“未知的获取途径”报错（{errors.Count} 条）；白潮预制固件 [{string.Join("、", fleet)}]" +
                (unknown.Count == 0 && errors.Count == 0 ? string.Empty : $"；未知：{string.Join("、", unknown)}；报错：{string.Join(" / ", errors.Take(3))}"));
            string root = F.LocateRepo();
            List<string> noIcon = rows.Where(r => r != null && !string.IsNullOrEmpty(r.Icon))
                .Where(r => root == null || !File.Exists(Path.Combine(root, (Icons + r.Icon + ".png").Replace('/', Path.DirectorySeparatorChar))))
                .Select(r => $"{r.Id}→{r.Icon}").ToList();
            Expect(noIcon.Count == 0, $"{rows.Count} 条固件的图标文件都在 GameRes/Raw/UI/Icons（含 icon_fw_pf_quench / shockguard / repulse）{(noIcon.Count == 0 ? string.Empty : "；缺：" + string.Join("、", noIcon))}");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Count(string text, string token)
        {
            int n = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        /// <summary>取方法声明（“static 返回类型 名字(”）之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBody(string code, string method)
        {
            Match m = Regex.Match(code, @"\bstatic\s+[\w<>\[\],.?]+\s+" + Regex.Escape(method) + @"\s*\(");
            if (!m.Success)
            {
                return null;
            }
            int open = code.IndexOf('{', m.Index);
            if (open < 0)
            {
                return null;
            }
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return code.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            foreach (string raw in code.Split('\n'))
            {
                string l = raw;
                int i = l.IndexOf("//", StringComparison.Ordinal);
                if (i >= 0 && !l.Substring(0, i).Contains("\""))
                {
                    l = l.Substring(0, i);
                }
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        private static string ReadRepo(string relative)
        {
            string root = F.LocateRepo();
            string path = root == null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
            Debug.LogWarning("[M13 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
