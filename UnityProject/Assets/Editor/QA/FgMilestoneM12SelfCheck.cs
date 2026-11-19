using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-E2E-01 自检 [M12 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M12 跑）。
    /// 轻量口径 b（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——事件段的六类反向在 M12 各 Story 的自检里都是真实系统
    /// （事件导演、震动值与蠕虫、信号幽灵与内核夺控、轨道坠落与家园内核、诱饵终端与伏击、流浪商队与交易、真文件存读档）跑出来的断言，已并入全量自检；本段 R 守护这张映射：
    /// 每一类至少一条断言仍在源码里、所在段仍登记在全量自检，删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M12 覆盖里程碑原文（震动 80 警告 → 蠕虫分节击毁 → 第一次信号幽灵反向接入 → 轨道坠落回收残骸 → 标记器识别诱饵终端 → 商队交易）
    ///    与卡片承接（FG12-EVT-03 幽灵段、FG12-EVT-04 坠落段）；固定种子；断点都是旅程里的步骤；反向接入用共用夺控事件计数（卡片“必须同时交付”，FGR-EMG-165）。
    /// A2. FGJ-M12 源码只走输入通道；登记的夹具（DEBT-FG12E2E01-01 / 02）只在登记的方法里、恰好 N 处。
    /// R1～R6. IC-REQ-022 六类反向映射（R5 = FGT-EVT-009 存读档一致，六个事件各一条）+ 里程碑风险“事件没有预警或不可应对” + 承接项（DEBT-EMG4HIJ02-01 覆盖外 / 干扰中被拒、FG12-EVT-04 审查 P2 两条）。
    /// C. 缺口清零：门禁第一个里程碑是 M12 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延；本 Story 的登记在表里；夹具债改承接写进 FG15-QA-01 卡片正文、试玩债写进 FG15-QA-02。
    /// D. 试玩包：FG-M12 四份材料齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”六项有自检段且登记在全量自检，并各有 FGJ-M12 步骤。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM12SelfCheck）；单段：bash tools/unity-validate.sh --segment FgMilestoneM12SelfCheck。
    /// </summary>
    public static class FgMilestoneM12SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";
        private const string Cards = "production/design/full-game/FG-STORY-CARDS.md";

        [MenuItem("QA/自检/M12 出口")]
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
            Line("\n[M12 出口] FG12-E2E-01：旅程登记与共用夺控计数、正式输入与夹具守护、IC-REQ-022 六类反向（真实系统断言映射，含 FGT-EVT-009）与承接项、缺口清零、试玩包、里程碑需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Line($"  · [M12 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static string JourneyCode() => StripComments(ReadRepo(Bots + "FgjM12Journey.cs") ?? string.Empty);

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M12 覆盖里程碑原文与 FG12-EVT-03 / 04 承接段；固定种子；断点都是旅程里的步骤；反向接入用共用夺控事件计数");
            JourneyDef m12 = JourneyCatalog.Create(FgjM12Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子、3 倍速", new[] { "menu_new", "new_game", "seed", "speed3" }),
                ("采矿让震动值升到 80，看到警告", new[] { "vib_fix", "vib_warn" }),
                ("蠕虫破土，分节击毁（右键攻击）", new[] { "w_sel", "w_go", "worm_crack", "worm_up", "worm_fight", "worm_dead" }),
                ("第一次信号幽灵，用反向接入夺回（FG12-EVT-03 承接：选中 → 接入键 → 3 秒夺回、镜头不切直控、历史与奖励）", new[] { "ghost_seize", "ghost_sel", "ghost_key", "ghost_done" }),
                ("轨道坠落，回收残骸（FG12-EVT-04 承接：地图 + 小地图落点圈、右键撤圈、撞击结果、回收）", new[] { "orbit_plan", "o_in", "orbit_warn", "o_out", "orbit_hit", "orbit_loot" }),
                ("用标记器识别出一个诱饵终端", new[] { "decoy_fix", "d_sel", "d_go", "decoy_seen" }),
                ("与商队完成一笔交易", new[] { "caravan_come", "caravan_open", "caravan_buy", "caravan_close" }),
            };
            List<string> missing = exit.Where(x => m12 == null || x.Ids.Any(id => m12.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            bool wellformed = m12 != null && m12.Steps.Count > 0 && m12.Steps.Select(x => x.Id).Distinct().Count() == m12.Steps.Count
                              && m12.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && m12.TotalTimeoutSeconds > 0;
            Expect(wellformed && missing.Count == 0 && m12.Seed == FgjM0Journey.TestSeed,
                $"FGJ-M12：{m12?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m12?.Seed}；里程碑原文与承接段都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m12 != null && m12.CheckpointAfter.Length >= 1 && m12.CheckpointAfter.All(id => m12.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM12Journey.Id) && JourneyCatalog.Ids.Contains(FgjM11Journey.Id),
                $"FGJ-M12 登记在旅程登记表、前序旅程 FGJ-M11 仍在；断点 [{string.Join(", ", m12?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
            // 共用夺控事件计数（FGR-EMG-165 evt.seize / evt.release）：旅程与夺控自检读同一个内核计数，不在旅程里另造判定。
            string code = JourneyCode();
            string seize = ReadRepo(Qa + "FgEmgSeizeSelfCheck.cs") ?? string.Empty;
            bool shared = code.Contains("CombatSite.SeizeStarted") && code.Contains("CombatSite.SeizeEndedCount") && code.Contains("CombatSite.SeizeEndedCount == c.GetInt(\"gh.release0\") + 1")
                          && seize.Contains("CombatSite.SeizeStarted == started + 1") && seize.Contains("CombatSite.SeizeEndedCount");
            Expect(shared, "反向接入步骤的断言用共用夺控事件计数：FGJ-M12 ghost_seize / ghost_done（CombatSite.SeizeStarted / SeizeEndedCount，归还 +1）与 FgEmgSeizeSelfCheck 读同一内核计数");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "BuildingOps.Try",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request",
            "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "ExpeditionDepartureService.TryDepart(",
            "ExpeditionMarchService.IssueCommand(", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(", "WorldView.FlyTo(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", ".ApplyDamage(", "CampaignObjectiveTracker.Complete", "RaidDirectorService.RequestStoryRaid(", "WorldTransitSystem.Dispatch",
            "LootPickService.Grant", "SignalSeizeService.Begin", "SignalSeizeService.BlockSlot(", "EventDirectorService.Complete(", "EventDirectorService.SetHistoryReward(",
            "FieldLootService.On", "IntelService.Grant", "NarrativeService.Grant(", "CampaignEventLedger.TryGrant(", "PrimitiveInventory.Grant", "EnemySpawnBuilder.",
            // 夹具 token：只在登记的方法里免扫（见 Fixtures）。
            "s.Progress.Act = ", "TerritoryControlService.MarkLeaderDefeated(", "s.UnlockedContentIds = ", "BlueprintEditorService.TrySave(", "MachineRegistry.SpawnMachine(",
            "MachineLoadoutRegistry.Register(", "FgProductionSelfCheck.Built(", "FgProductionSelfCheck.PowerUp(", "FgProductionSelfCheck.SetFootprint(", "HomeVib.Value = ",
            "d.LastMajorTick = ", "SkipMajorGap(c", "b.Health = ", "HomeGridService.RevealArea(", "RelayChain(", "EventDirectorService.Request(St, ", "EventDirectorService.Request(s, ",
            "HomeInventory.Add(",
        };

        /// <summary>夹具（ADR-QA-029 第 4 节）：token 只能出现在登记的方法体里，且恰好 N 处。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("s.Progress.Act = ", "ApplyFixture", 1, "开局第 2 幕 DEBT-FG12E2E01-01"),
            ("TerritoryControlService.MarkLeaderDefeated(", "ApplyFixture", 1, "铸造 / 澄净 / 静默平定 DEBT-FG12E2E01-01"),
            ("s.UnlockedContentIds = ", "ApplyTeam", 1, "重炮 / 机枪 / 标记器记为已获得 DEBT-FG12E2E01-01"),
            ("BlueprintEditorService.TrySave(", "SaveBlueprint", 1, "重炮作战 / 标记器作战蓝图 DEBT-FG12E2E01-01"),
            ("MachineRegistry.SpawnMachine(", "SpawnMachine", 1, "作战队 6 台 + 标记器 1 台 DEBT-FG12E2E01-01"),
            ("MachineLoadoutRegistry.Register(", "SpawnMachine", 1, "出厂机器的装配登记 DEBT-FG12E2E01-01"),
            ("FgProductionSelfCheck.Built(", "ApplyTeam", 1, "两座仓库 DEBT-FG12E2E01-01"),
            ("FgProductionSelfCheck.Built(", "ApplyVibFixture", 1, "10 座提取钻 DEBT-FG12E2E01-01"),
            ("FgProductionSelfCheck.Built(", "ApplyOrbitPlan", 1, "落点圈电线杆 DEBT-FG12E2E01-01"),
            ("FgProductionSelfCheck.PowerUp(", "ApplyVibFixture", 1, "家园供电 DEBT-FG12E2E01-01"),
            ("FgProductionSelfCheck.SetFootprint(", "ApplyVibFixture", 1, "提取钻占地铺稀土矿脉 DEBT-FG12E2E01-01"),
            ("HomeVib.Value = ", "ApplyVibFixture", 1, "家园震动值预置 79 DEBT-FG12E2E01-01"),
            ("b.Health = ", "ApplyOrbitPlan", 1, "电线杆满耐久 DEBT-FG12E2E01-01"),
            ("HomeGridService.RevealArea(", "ApplyOrbitPlan", 1, "落点揭雾 DEBT-FG12E2E01-01"),
            ("HomeGridService.RevealArea(", "ApplyDecoyFixture", 2, "诱饵终端与停车点揭雾 DEBT-FG12E2E01-01"),
            ("RelayChain(", "ApplyDecoyFixture", 1, "诱饵终端沿线中继（每座过正式放置校验）DEBT-FG12E2E01-01"),
            ("HomeInventory.Add(", "ApplyCaravanRequest", 1, "交易材料 DEBT-FG12E2E01-01"),
            ("d.LastMajorTick = ", "SkipMajorGap", 1, "重大威胁间隔记到 2 日前 DEBT-FG12E2E01-02"),
            ("SkipMajorGap(c", "ApplyVibFixture", 1, "蠕虫前跳过间隔 DEBT-FG12E2E01-02"),
            ("SkipMajorGap(c", "ApplyOrbitRequest", 1, "坠落前跳过间隔 DEBT-FG12E2E01-02"),
            ("EventDirectorService.Request(St, ", "ApplyOrbitRequest", 1, "请求轨道坠落 DEBT-FG12E2E01-02"),
            ("EventDirectorService.Request(s, ", "ApplyCaravanRequest", 1, "请求流浪商队 DEBT-FG12E2E01-02"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M12 源码只走输入通道；夹具（DEBT-FG12E2E01-01 / 02）只在登记的方法里");
            string code = JourneyCode();
            if (!code.Contains("class FgjM12Journey"))
            {
                Fail("找不到旅程源码 FgjM12Journey.cs");
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
            Expect(hits.Count == 0, $"FGJ-M12 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种；登记夹具方法里的登记调用除外）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
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
            Expect(bad.Count == 0, $"夹具只在登记的方法里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string[] inputs =
            {
                "FgjM3Common.TryClick(", "JourneyInput.ClickScreen(", "JourneyInput.ShiftClickScreen(", "FgjM10Journey.TickSelectTeam", "GameActionId.SpeedTriple",
                "GameActionId.ToggleCameraView", "JourneyInput.ClickElement(", "JourneyInput.ClickUitk(",
            };
            List<string> miss = inputs.Where(x => !code.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：世界点击（左键选 / 右键地面移动 / 右键敌人攻击）、Shift 加选、3 倍速键、接入 / 退出接入键（反向接入）、主视图左键点商队、交易面板按钮" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（事件段）：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；R5 = FGT-EVT-009 六个事件各一条存读档；里程碑风险“事件没有预警或不可应对”；承接项各有断言");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var map = new (string Category, string Segment, string File, string Label)[]
            {
                ("R1 条件不足：交易材料不足 → 买不了、不改任何状态", "FgCaravanSelfCheck", "FgCaravanSelfCheck.cs", "N1 材料不足：买不了、不改任何状态"),
                ("R1 条件不足：没有标记器 / 不在扫描半径里 → 不识别诱饵", "FgDecoyTerminalSelfCheck", "FgDecoyTerminalSelfCheck.cs", "N5 负向：没有标记器的机器站在旁边不识别"),
                ("R1 条件不足：事件频率设为关闭 → 只留剧情固定与点位事件", "FgEventDirectorSelfCheck", "FgEventDirectorSelfCheck.cs", "R3 负向“事件频率设为关闭”"),
                ("R1 条件不足：80 警告后提取钻全部关停 → 到点照常请求", "FgWormSelfCheck", "FgWormSelfCheck.cs", "C8 负向“80 警告后把提取钻全部关停”"),
                ("R2 目标死亡：蠕虫分节击毁 → 每节掉落、整条倒下", "FgWormSelfCheck", "FgWormSelfCheck.cs", "C5 分节击毁"),
                ("R2 目标死亡：被夺控的己方机器被打到 0 → 瘫痪而不阵亡", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "C1 击倒（FGR-EVT-022"),
                ("R2 目标死亡：预警中目标全部失效 → 剧情第一次撤回成推迟", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "E2 预警中目标全部失效"),
                ("R2 目标死亡 / 失败：没打退诱饵伏击 → 时限到以超时结束", "FgDecoyTerminalSelfCheck", "FgDecoyTerminalSelfCheck.cs", "N2 负向“没打退伏击”"),
                ("R3 失联：远征里的机器被夺控，在干扰场里 / 覆盖外按接入键被拒，回覆盖内夺回", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "B2 负向“选中的机器在远征”"),
                ("R3 失联：没瘫痪的被夺控机器离开地点 → 归还并通知", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "G2 没瘫痪的被夺控机器离开地点"),
                ("R3 失联：预警中家园被卸载 → 撞击照常结算、不崩溃", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "S3 家园未载入时撞击"),
                ("R4 暂停与倍速：事件导演", "FgEventDirectorSelfCheck", "FgEventDirectorSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：震动与蠕虫", "FgWormSelfCheck", "FgWormSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：信号幽灵预警 / 夺控", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：轨道坠落预警 / 撞击", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：诱饵触发与伏击", "FgDecoyTerminalSelfCheck", "FgDecoyTerminalSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R4 暂停与倍速：商队到达与停留", "FgCaravanSelfCheck", "FgCaravanSelfCheck.cs", "T1 暂停与 0.5x～3x"),
                ("R5 存读档（FGT-EVT-009）：事件导演状态（排程、进行中、历史、随机数种子与序号、首次说明、频率）", "FgEventDirectorSelfCheck", "FgEventDirectorSelfCheck.cs", "S1 负向“事件进行中存档”：预警中真文件存档"),
                ("R5 存读档（FGT-EVT-009）：各地点震动值、蠕虫记录与内核蠕虫节", "FgWormSelfCheck", "FgWormSelfCheck.cs", "S1 负向“事件进行中存档”：蠕虫在地面上"),
                ("R5 存读档（FGT-EVT-009）：信号幽灵预警与内核夺控", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "负向“事件进行中存档”——预警中（还剩 3 秒）"),
                ("R5 存读档（FGT-EVT-009）：轨道坠落预警中", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "S1 负向“事件进行中存档”——预警中（还剩 30 秒）"),
                ("R5 存读档（FGT-EVT-009）：撞击后坠落残骸", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "S2 撞击后存档 → 读档"),
                ("R5 存读档（FGT-EVT-009）：诱饵伏击进行中", "FgDecoyTerminalSelfCheck", "FgDecoyTerminalSelfCheck.cs", "S1 伏击进行中真文件存读档"),
                ("R5 存读档（FGT-EVT-009）：商队停留中、商品与库存", "FgCaravanSelfCheck", "FgCaravanSelfCheck.cs", "S1 负向“事件进行中存档”——商队停留中"),
                ("R6 路径 / 地点失败：处理器找不到合适地点 → 随机 / 条件放弃这次、剧情与点位留着再试", "FgEventDirectorSelfCheck", "FgEventDirectorSelfCheck.cs", "F7 没有处理器的事件不安排"),
                ("R6 路径 / 地点失败：落点是悬崖 / 水面 → 改选可走落点", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "A2b 负向：悬崖 / 水面格"),
                ("R6 路径 / 地点失败：请求落在核心旁 → 改到 12 格外", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "A2 负向：请求落在核心旁 2 格"),
                ("R6 路径 / 地点失败：商队从迷雾外走到停靠点", "FgCaravanSelfCheck", "FgCaravanSelfCheck.cs", "N4b FGR-EVT-050“从迷雾外来"),
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
            // FGT-EVT-009：六个事件（导演 + 五种）各至少一条真文件存读档断言。
            var saveSegs = map.Where(x => x.Category.StartsWith("R5", StringComparison.Ordinal)).Select(x => x.Segment).Distinct().ToList();
            Expect(saveSegs.Count == 6, $"FGT-EVT-009 存读档一致：事件导演与五种事件各有真文件存读档断言（{string.Join("、", saveSegs)}）");
            // 里程碑风险“事件没有预警或不可应对”：预警断言 + 旅程里玩家可见的预警步骤。
            var warn = new (string Segment, string File, string Label)[]
            {
                ("FgWormSelfCheck", "FgWormSelfCheck.cs", "B3 / C1 警告到点仍在 80 以上"),
                ("FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "B1c 预警期间什么都不发生"),
                ("FgEventDirectorSelfCheck", "FgEventDirectorSelfCheck.cs", "F5 预警中的重大威胁遇上静默夜开始"),
            };
            JourneyDef m12 = JourneyCatalog.Create(FgjM12Journey.Id);
            bool warnOk = warn.All(x => segments.Contains(x.Segment) && (ReadRepo(Qa + x.File) ?? string.Empty).Contains(x.Label));
            bool journey = m12 != null && new[] { "ghost_seize", "vib_warn", "worm_crack", "orbit_warn", "o_out", "decoy_seen" }.All(id => m12.Steps.Any(s => s.Id == id));
            Expect(warnOk && journey, "里程碑风险“事件没有预警或不可应对”：蠕虫 80 警告到点才请求、破土前 30 秒预警（FgWormSelfCheck B3 / C1）、坠落 60 秒预警期间什么都不发生（B1c）、预警中遇静默夜撤回并收回效果（导演 F5）；" +
                                      "FGJ-M12 真实输入走过幽灵杂波预警 → 反向接入、80 警告 → 地面开裂 → 布防、落点圈预警 → 撤圈、标记器识别诱饵");
            var carried = new (string What, string Segment, string File, string Label)[]
            {
                ("DEBT-EMG4HIJ02-01 / FG12-EVT-03：干扰场里 / 覆盖外按接入键被拒", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "覆盖外被拒"),
                ("FG12-EVT-04 审查 P2：撞击完成后存读档，坠落残骸（位置 / 耐久 / 质量）仍在", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "S2 撞击后存档 → 读档：坠落残骸仍在落点"),
                ("FG12-EVT-04 审查 P2：家园未载入时撞击不崩溃、建筑受创、材料掉落、不生成内核残骸、不波及单位", "FgOrbitalDropSelfCheck", "FgOrbitalDropSelfCheck.cs", "不生成内核残骸"),
                ("FG-GAP-245：按接入键反向接入时镜头留在战略视角", "FgSignalGhostSelfCheck", "FgSignalGhostSelfCheck.cs", "镜头不切直控（{cameraStayed}"),
            };
            List<string> lost = carried.Where(x => !segments.Contains(x.Segment) || !(ReadRepo(Qa + x.File) ?? string.Empty).Contains(x.Label)).Select(x => x.What).ToList();
            Expect(lost.Count == 0, $"承接项 {carried.Length} 条各有起真实系统的断言、所在段登记在全量自检{(lost.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lost))}");
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
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 12)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered = { "DEBT-FG12E2E01-01", "DEBT-FG12E2E01-02", "DEBT-FG12E2E01-03" };

        /// <summary>承接写着本里程碑 / 本卡的条目（含门禁是 M12 出口的跨阶段承接）：出口时都要 Closed。</summary>
        private static readonly string[] ClosedHere =
        {
            "DEBT-EMG4HIJ02-01", "DEBT-FG12EVT01-02", "FG-GAP-245", "DEBT-FG4ECO02-04", "DEBT-FG8LOOT02-05", "DEBT-FG9UX01-03", "DEBT-FG6DEF09-02", "DEBT-FG0ARCH06-02", "FG-GAP-099",
        };

        private static readonly string[] FixtureDebts = { "DEBT-FG12E2E01-01", "DEBT-FG12E2E01-02" };

        private static string CardBody(string cards, string id)
        {
            int at = cards.IndexOf("### " + id + " ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cards.IndexOf("\n### ", at + 10, StringComparison.Ordinal);
            return at < 0 ? string.Empty : end < 0 ? cards.Substring(at) : cards.Substring(at, end - at);
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M12 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（按门禁列的第一个里程碑判定）；M12 承接项都已 Closed；本 Story 的登记；夹具债写进 FG15-QA-01 正文、试玩债写进 FG15-QA-02");
            string text = ReadRepo(Register);
            if (text == null)
            {
                Fail("找不到 " + Register + "（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            int gapRows = 0, debtRows = 0, m12Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
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
                if (gap)
                {
                    gapRows++;
                }
                else
                {
                    debtRows++;
                }
                string gate = gap ? (cols.Length >= 7 ? cols[5] : null) : (cols.Length >= 9 ? cols[6] : null);
                owners[cols[0]] = gap ? (cols.Length >= 7 ? cols[4] : string.Empty) : (cols.Length >= 9 ? cols[5] : string.Empty);
                if (gate == null || FirstMilestoneOf(gate) != 12)
                {
                    continue;
                }
                m12Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Expect(gapRows > 200 && debtRows > 500 && m12Rows >= 6, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M12 的 {m12Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M12 的缺口 / 延后项全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明理由与新门禁）"
                    : $"FG-M12 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            Expect(notClosed.Count == 0, $"承接写着 M12 各卡 / M12 出口的 {ClosedHere.Length} 条都是 Closed（状态格以 Closed 开头）{(notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed))}");
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG12E2E01-03", out string p) && p.Contains("待用户");
            string cards = ReadRepo(Cards) ?? string.Empty;
            string qa01 = CardBody(cards, "FG15-QA-01");
            string qa02 = CardBody(cards, "FG15-QA-02");
            List<string> uncarried = FixtureDebts.Where(id => !owners.TryGetValue(id, out string o) || !o.Contains("FG15-QA-01") || !qa01.Contains(id)).ToList();
            bool playCarried = owners.TryGetValue("DEBT-FG12E2E01-03", out string po) && po.Contains("FG15-QA-02") && qa02.Contains("DEBT-FG12E2E01-03");
            Expect(unregistered.Count == 0 && pending && uncarried.Count == 0 && playCarried,
                $"本 Story 的登记在表里（夹具 DEBT-FG12E2E01-01 / 02、试玩 -03 待用户）；夹具债承接 FG15-QA-01、试玩债承接 FG15-QA-02，两张卡正文都有承接行（规则第 7 条）" +
                (unregistered.Count + uncarried.Count == 0 && playCarried ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；承接缺：{string.Join("、", uncarried)}；试玩承接 {playCarried}"));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M12 材料齐全（可选试玩，真人试玩由用户择时进行，DEBT-FG12E2E01-03，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M12-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M12-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M12-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M12-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            int at = readme.IndexOf("## FG-M12 事件", StringComparison.Ordinal);
            string section = at >= 0 ? readme.Substring(at) : string.Empty;
            bool registered = section.Contains("DEBT-FG12E2E01-03") && section.Contains("不阻塞") && section.Contains("FGJ-M12");
            bool covers = script.Contains("信号幽灵") && script.Contains("蠕虫") && script.Contains("轨道坠落") && script.Contains("诱饵终端") && script.Contains("商队")
                          && script.Contains("事件频率") && table.Contains("T9");
            Expect(registered && covers, $"目录登记“待用户试玩、不阻塞”并写明 FGJ-M12 断点存档（{registered}）；脚本覆盖里程碑原文五种事件 + FG10 第 14 节三问、对照表 T1～T9（{covers}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M12“必须完成”六项有自检段且登记在全量自检，并各有 FGJ-M12 步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m12 = JourneyCatalog.Create(FgjM12Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("事件导演框架 FGR-EVT-001～006（FG12-EVT-01）", "FgEventDirectorSelfCheck", "orbit_warn"),
                ("震动值与掘进蠕虫 FGR-EVT-010～014（FG12-EVT-02）", "FgWormSelfCheck", "worm_dead"),
                ("信号幽灵 FGR-EVT-020～023（FG12-EVT-03）", "FgSignalGhostSelfCheck", "ghost_done"),
                ("轨道坠落 FGR-EVT-030、031（FG12-EVT-04）", "FgOrbitalDropSelfCheck", "orbit_loot"),
                ("诱饵终端 FGR-EVT-040、041（FG12-EVT-05）", "FgDecoyTerminalSelfCheck", "decoy_seen"),
                ("流浪商队 FGR-EVT-050、051（FG12-EVT-06）", "FgCaravanSelfCheck", "caravan_buy"),
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = m12?.Steps.Any(s => s.Id == step) ?? false;
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM12SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，并各有 FGJ-M12 旅程步骤；[M12 出口] 本身登记在全量自检" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
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
            Debug.LogWarning("[M12 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
