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
    /// FG14-E2E-01 自检 [M14 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M14 跑）。
    /// 轻量口径 b（用户 2026-10-12）：IC-REQ-022 反向场景与 FG12 第 4 节负向矩阵用起真实系统的单段自检覆盖——后日谈（FgPostgameSelfCheck）、自定义沙盒（FgSandboxSelfCheck）、
    /// 沙盒的种子与世界设置（FgWorldGenHomeSelfCheck）的反向断言都是真实系统跑出来的，已并入全量自检；本段 R 守护这张映射：每一条断言仍在源码里、所在段仍登记在全量自检，
    /// 删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M14 覆盖里程碑原文（通关存档选择继续 → 撑过两次周期清洗 → 回主菜单用自定义种子开一局关闭突袭的沙盒，确认没有突袭），顺序正确；固定种子、沙盒种子与之不同；断点都是旅程里的步骤。
    /// A2. FGJ-M14 源码只走输入通道；登记的夹具（DEBT-FG14E2E01-02 时间 / 触发；结局存档带来的 DEBT-FG13E2E01-01 维修站供电再置）只在登记的方法里、恰好 N 处（DEBT-FG14E2E01-01 通关存档夹具 FG15-QA-01 已删）。
    /// R. IC-REQ-022 六类反向映射（R5 三段真文件存读档）+ FG12 第 4 节负向矩阵四行 + 里程碑风险“高等级清洗超出性能预算（含涌现满载）”+ FG12 第 6 节 FGT-SBX-001～005 映射。
    /// C. 缺口清零：门禁第一个里程碑是 M14 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延；M14 承接项都已 Closed；本 Story 的登记在表里、承接写进卡片正文。
    /// D. 试玩包：FG-M14 四份材料齐全，含 FG12 第 7 节两问，目录登记“可选试玩、待用户、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”各项有自检段且登记在全量自检，玩家主路径各有 FGJ-M14 步骤。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM14SelfCheck）；单段：bash tools/unity-validate.sh --segment FgMilestoneM14SelfCheck。
    /// </summary>
    public static class FgMilestoneM14SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";
        private const string Cards = "production/design/full-game/FG-STORY-CARDS.md";

        [MenuItem("QA/自检/M14 出口")]
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
            Line("\n[M14 出口] FG14-E2E-01：旅程登记与反向路径顺序、正式输入与夹具守护、IC-REQ-022 六类反向与 FG12 负向矩阵（真实系统断言映射）、里程碑风险、缺口清零、试玩包、里程碑需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Line($"  · [M14 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static string JourneyCode() => StripComments(ReadRepo(Bots + "FgjM14Journey.cs") ?? string.Empty);

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M14 覆盖里程碑原文且按“继续 → 两次清洗 → 沙盒”顺序；固定种子、沙盒自定义种子；断点都是旅程里的步骤");
            JourneyDef m14 = JourneyCatalog.Create(FgjM14Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从 FGJ-M13 打出的真实结局存档出发（固定种子）", new[] { "e_import", "e_load", "e_loaded", "seed" }),
                ("通关存档（返回主菜单 → 再读档回到结算）选择继续，进入后日谈并看到说明、3 倍速", new[] { "e_menu", "e_load2", "e_loaded2", "e_continue", "pg_intro", "speed3b" }),
                ("撑过两次周期清洗（读计划、右键散开驻防、守卫、右键敌人交战、撑过）", new[] { "p1_plan", "p1_spread", "p1_sel", "p1_fight", "p1_done", "p2_plan", "p2_fight", "p2_done", "pg_stats" }),
                ("坚守记录可查、保存并回主菜单、存档卡显示后日谈坚守天数", new[] { "rec_open", "rec_close", "save_quit", "menu_card" }),
                ("回主菜单“沙盒模式”用自定义种子、关闭突袭开一局，确认没有突袭、沙盒设置显示突袭关闭", new[] { "sb_open", "sb_setup", "sb_enter", "sb_check", "sb_noraid", "sb_panel" }),
            };
            List<string> missing = exit.Where(x => m14 == null || x.Ids.Any(id => m14.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            bool wellformed = m14 != null && m14.Steps.Count > 0 && m14.Steps.Select(x => x.Id).Distinct().Count() == m14.Steps.Count
                              && m14.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && m14.TotalTimeoutSeconds > 0;
            Expect(wellformed && missing.Count == 0 && m14.Seed == FgjM0Journey.TestSeed && FgjM14Journey.SandboxSeed != FgjM0Journey.TestSeed,
                $"FGJ-M14：{m14?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m14?.Seed}、沙盒自定义种子 {FgjM14Journey.SandboxSeed}（与固定种子不同）；里程碑原文五段都有对应步骤" +
                (missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing)));
            int At(string id) => m14?.Steps.FindIndex(s => s.Id == id) ?? -1;
            bool order = At("e_continue") >= 0 && At("e_continue") < At("p1_done") && At("p1_done") < At("p2_done") && At("p2_done") < At("menu_card")
                         && At("menu_card") < At("sb_open") && At("sb_open") < At("sb_noraid");
            Expect(order, $"反向路径顺序：继续（第 {At("e_continue")} 步）→ 第 1 级撑过（{At("p1_done")}）→ 第 2 级撑过（{At("p2_done")}）→ 存档卡（{At("menu_card")}）→ 沙盒模式（{At("sb_open")}）→ 没有突袭（{At("sb_noraid")}）");
            bool ck = m14 != null && m14.CheckpointAfter.Length >= 1 && m14.CheckpointAfter.All(id => m14.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM14Journey.Id) && JourneyCatalog.Ids.Contains(FgjM13Journey.Id),
                $"FGJ-M14 登记在旅程登记表、前序旅程 FGJ-M13 仍在；断点 [{string.Join(", ", m14?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(",
            "BuildingOps.Try", "GameClock.SetPaused(", "GameClock.SetSpeed(", "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request",
            "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(", "Kernel.Kill(",
            "WorldView.FlyTo(", ".ApplyDamage(", "CampaignObjectiveTracker.Complete", "MachineRegistry.SpawnMachine(", "MachineLoadoutRegistry.Register(", "EnemySpawnBuilder.",
            "EndingService.Begin(", "EndingService.OpenSettlement(", "EndingService.ContinuePostgame(", "RevealService.Confirm(", "FlagshipService.Begin", "PurgeWaveService.Begin",
            "PostgameService.OnStarted(", "PostgameService.WorldStep(", "PostgameService.OnCoreDestroyed(", "PostgameService.OnOrbitalDrop(",
            "SandboxService.ApplyNewGame(", "SandboxService.TryChange(", "SandboxService.TryExchange(", "SandboxService.NoteSuppressedRaid(", "CampaignSession.Set(", "WorldTransitSystem.Dispatch",
            // 夹具 token：只在登记的方法里免扫（见 Fixtures）。
            "s.Progress.Act = ", "EventDirectorService.SetFrequency(", "pw.Launched = ", ".StartTick = ", ".DoneTick = ", "fs.Phase = ", "CampaignEventLedger.TryGrant(",
            "s.UnlockedContentIds = ", "FgjM12Journey.SaveBlueprint(", "FgjM12Journey.SpawnMachine(", "FgProductionSelfCheck.Built(", "b.Health = ", "Mega.SetPower(", "HomeInventory.Add(",
            "EndingService.EndingSaveHandler(", "GameClock.SkipForTests(", "RaidDirectorService.RequestStoryRaid(", "RaidDirectorService.OnPurificationTowerTargeted(",
            "RaidDirectorService.OnMegastructureStageCompleted(", "s.SignalExposure = ", "CampaignAutoSaveService.SaveAuto(", "RepairDroneService.Sync(",
        };

        /// <summary>夹具（ADR-QA-031 第 3 节）：token 只能出现在登记的方法体里，且恰好 N 处。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("Mega.SetPower(", "Watch", 1, "结局存档里 FGJ-M13 的维修站供电被电网结算改回时再置 DEBT-FG13E2E01-01"),
            ("GameClock.SkipForTests(", "SkipToPurge", 1, "两次清洗之间的 7 个游戏日跳到发动前 30 游戏秒 DEBT-FG14E2E01-02"),
            ("GameClock.SkipForTests(", "TickNoRaid", 2, "沙盒三个游戏日每日跳到日末前 / 跳到静默夜前 2 游戏秒 DEBT-FG14E2E01-02"),
            ("RaidDirectorService.RequestStoryRaid(", "ApplyRaidTriggers", 1, "正式触发入口：剧情报复 DEBT-FG14E2E01-02"),
            ("RaidDirectorService.OnPurificationTowerTargeted(", "ApplyRaidTriggers", 1, "正式触发入口：净化塔被盯上 DEBT-FG14E2E01-02"),
            ("RaidDirectorService.OnMegastructureStageCompleted(", "ApplyRaidTriggers", 1, "正式触发入口：巨构阶段完工 DEBT-FG14E2E01-02"),
            ("s.SignalExposure = ", "ApplyRaidTriggers", 1, "信号暴露拉满 DEBT-FG14E2E01-02"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M14 源码只走输入通道；夹具（DEBT-FG14E2E01-02、结局存档带来的 DEBT-FG13E2E01-01 供电再置）只在登记的方法里；通关存档夹具 DEBT-FG14E2E01-01 已删（改读 FGJ-M13 的真实结局存档）");
            string code = JourneyCode();
            if (!code.Contains("class FgjM14Journey"))
            {
                Fail("找不到旅程源码 FgjM14Journey.cs");
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
            Expect(hits.Count == 0, $"FGJ-M14 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种；登记夹具方法里的登记调用除外）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
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
                "FgjM3Common.TryClick(", "JourneyInput.ClickUitk(", "JourneyInput.ClickElement(", "JourneyInput.PressAction(", "FgjM12Journey.SelectMachines(", "FgjM13Journey.TickSpread(",
                "FgjM13Journey.TickContinue", "GameActionId.SpeedTriple", "GameActionId.CommandGuard", "GameActionId.Cancel",
            };
            List<string> miss = inputs.Where(x => !code.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：世界点击（左键点选 / Shift 全选 / 右键地面移动 / 右键敌人攻击）、结算与暂停菜单与新游戏面板按钮（UI Toolkit）、3 倍速键、守卫键、Esc" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        private const string Pg = "FgPostgameSelfCheck", Sb = "FgSandboxSelfCheck", Wg = "FgWorldGenHomeSelfCheck";

        internal static readonly (string Category, string Segment, string Label)[] ReverseMap =
        {
            ("R1 条件不足：后日谈第 7 个游戏日之前清洗不来", Pg, "B2 第 7 日之前不来"),
            ("R1 条件不足：后日谈里的普通 / 剧情突袭不占清洗序号、不算清洗", Pg, "C1 负向：后日谈里的普通"),
            ("R1 条件不足：研究点不够时不换关键材料", Sb, "E2 关键材料用研究点换：不够不换"),
            ("R1 条件不足：沙盒没有剧情——逐个触发剧情节点不投放、不演出，建完巨构也不点亮信标（FG12 第 4 节第 4 行）", Sb, "E1 没有剧情"),
            ("R1 条件不足：沙盒关闭突袭——各种正式触发全部丢掉、静默夜照常来但没有进攻（FG12 第 4 节第 3 行）", Sb, "C1 关闭突袭时确实没有突袭"),
            ("R2 目标死亡：后日谈中核心被突袭摧毁 → 同一步坚守结束、显示坚守总结（FG12 第 4 节第 1 行，FGR-SBX-005）", Pg, "H1 后日谈中核心被突袭摧毁"),
            ("R2 目标死亡：核心被毁后读取自动存档继续（核心完好、坚守没有结束、起算点不变）", Pg, "H2 读取自动存档继续"),
            ("R2 目标死亡：清洗来犯者全歼 → 记撑过、击毁数 = 突袭结算", Pg, "B3 第 1 级清洗全歼"),
            ("R3 失联：家园不被观察时后日谈清洗与统计的结果与观察时一致", Pg, "G1 家园不被观察时后日谈清洗与统计的结果与观察时一致"),
            ("R3 失联：观察 / 不观察时沙盒设置的效果与修改记录一致", Sb, "I1 观察 / 不观察时沙盒设置的效果与修改记录一致"),
            ("R3 失联：静默夜断信号——沙盒关静默夜后那一夜不来，开着时照常断信号", Sb, "E0b 静默夜：关"),
            ("R4 暂停与倍速：周期清洗与坚守天数按游戏时间", Pg, "E1 周期清洗与坚守天数按游戏时间"),
            ("R4 暂停与倍速：沙盒（关突袭穿过静默夜、天气）", Sb, "G1 暂停与 0.5x～3x"),
            ("R5 存读档：后日谈标记、清洗计时与等级、进行中的那一次、坚守统计与历史", Pg, "F1 存读档：后日谈标记"),
            ("R5 存读档：后日谈存档损坏 → 读档失败、原文件不变、正在进行的后日谈不受影响", Pg, "F1 负向：后日谈存档损坏"),
            ("R5 存读档：沙盒标记、设置、修改记录读回", Sb, "H1 存读档：沙盒标记"),
            ("R5 存读档：沙盒存档损坏 → 读档失败、原文件不变、正在进行的沙盒不受影响", Sb, "H1 负向：沙盒存档损坏"),
            ("R5 存读档：旧档没有沙盒域 → 全部标准档", Sb, "H2 旧档没有沙盒域"),
            ("R5 存读档：存档的世界设置代码坏了 → 读档拒绝、原文件不变", Wg, "负向：存档的世界设置代码在生成器 v2 里不存在"),
            ("R6 规模 / 地点失败：清洗等级非常高 → 同时存在 ≤ 200，靠精英化与单位强度（FG12 第 4 节第 2 行）", Pg, "D1 高等级清洗"),
            ("R6 规模 / 地点失败：等级没有上限、预算越过 int 不塌", Pg, "D1b 等级没有上限"),
            ("R6 规模 / 地点失败：第 31 级清洗真实攻城、登陆舱按落点投放", Pg, "D2 第 31 级清洗真实攻城"),
            ("R6 路径失败：自定义种子 × 领地远近下从核心真寻路走到各领地与锚点入口", Wg, "FGT-GEN-004 可达"),
            ("R6 地点失败：自定义种子下起始区四级保证（含极端世界设置）", Wg, "个种子全部满足起始区四级保证"),
        };

        private static string SegmentFile(string segment) => segment + ".cs";

        /// <summary>标签只算出现在 Expect( 的参数里（与 Expect( 之间没有语句结束符）；Fail( 的报错文字、注释不算，免得映射空转。</summary>
        private static bool InExpect(string src, string label)
        {
            for (int i = src?.IndexOf(label, StringComparison.Ordinal) ?? -1; i >= 0; i = src.IndexOf(label, i + 1, StringComparison.Ordinal))
            {
                int e = src.LastIndexOf("Expect(", i, StringComparison.Ordinal);
                if (e >= 0 && src.IndexOf(';', e, i - e) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（后日谈 / 沙盒 / 沙盒世界设置）：每条起真实系统的断言仍在源码里、所在段登记在全量自检；FG12 第 4 节四行；里程碑风险；FGT-SBX-001～005 映射");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var bad = new List<string>();
            var cats = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string category, string segment, string label) in ReverseMap)
            {
                string src = ReadRepo(Qa + SegmentFile(segment));
                if (!InExpect(src, label) || !segments.Contains(segment))
                {
                    bad.Add($"{category.Substring(0, 2)}（{segment} / “{label}”）");
                }
                else
                {
                    cats.Add(category.Substring(0, 2));
                }
            }
            Expect(bad.Count == 0 && cats.Count == 6, $"六类反向各有起真实系统的断言（{ReverseMap.Length} 条，覆盖 {string.Join(" / ", cats.OrderBy(x => x))}）且所在段都登记在全量自检" +
                                                    (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
            var saveSegs = ReverseMap.Where(x => x.Category.StartsWith("R5", StringComparison.Ordinal)).Select(x => x.Segment).Distinct().ToList();
            Expect(saveSegs.Count == 3, $"存读档：后日谈、沙盒、世界设置三段各有真文件存读档 / 读档拒绝断言（{string.Join("、", saveSegs)}）");
            string[] fg12Neg = { "第 4 节第 1 行", "第 4 节第 2 行", "第 4 节第 3 行", "第 4 节第 4 行" };
            List<string> lostNeg = fg12Neg.Where(k => ReverseMap.All(x => !x.Category.Contains(k))).ToList();
            Expect(lostNeg.Count == 0, "FG12 第 4 节负向与边界矩阵四行（后日谈中核心被毁、清洗等级非常高、沙盒关闭突袭、沙盒走到剧情节点）各映射到一条真实系统断言" +
                                       (lostNeg.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lostNeg)));
            // 里程碑风险“高等级清洗超出性能预算（含涌现满载预算，FG19 FGR-EMG-203）”：编成上限（自检）+ 带图形设备的帧时间探针（出口跑）+ 涌现满载 1x 预算（全量）。
            string probe = ReadRepo(Qa + "FgPostgamePerfProbe.cs") ?? string.Empty;
            bool risk = segments.Contains(Pg) && segments.Contains("FgEmergenceFuzzSelfCheck") && (ReadRepo(Qa + SegmentFile(Pg)) ?? string.Empty).Contains("D1 高等级清洗")
                        && probe.Contains("清洗含涌现满载：p95 帧时间") && probe.Contains("1000.0 / 120.0");
            Expect(risk, "里程碑风险“高等级清洗超出性能预算（含涌现满载）”：编成同时存在 ≤ 200（FgPostgameSelfCheck D1）、第 31 级清洗叠涌现满载的 120 帧探针（FgPostgamePerfProbe B，出口跑 tools/unity-gpu-probes.sh）、涌现满载 1x 预算（FgEmergenceFuzzSelfCheck H）");
            var fgt = new (string Id, string Segment, string Label)[]
            {
                ("FGT-SBX-001 结局后继续、周期清洗按时到来且等级递增", Pg, "B4 第 14 日"),
                ("FGT-SBX-002 高等级清洗不超出性能预算", Pg, "D1 高等级清洗（FGT-SBX-002"),
                ("FGT-SBX-003 坚守总结与统计正确", Pg, "B5 坚守统计（FGR-SBX-003）"),
                ("FGT-SBX-004 沙盒各设置项生效；关掉剧情后关键材料可以用研究点换取", Sb, "E2 关键材料用研究点换"),
                ("FGT-SBX-005 沙盒存档不触发剧情类成就", Sb, "F1 FGT-SBX-005"),
            };
            List<string> lost = fgt.Where(x => !segments.Contains(x.Segment) || !InExpect(ReadRepo(Qa + SegmentFile(x.Segment)), x.Label)).Select(x => x.Id).ToList();
            Expect(lost.Count == 0, $"FG12 第 6 节自动验收 FGT-SBX-001～005 各有登记在全量自检的段与断言{(lost.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lost))}");
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
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 14)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered = { "DEBT-FG14E2E01-01", "DEBT-FG14E2E01-02", "DEBT-FG14E2E01-03", "FG-GAP-254" };

        /// <summary>承接写着 M14 各卡的条目（含门禁写 M14 的跨阶段承接）：出口时都要 Closed。</summary>
        private static readonly string[] ClosedHere = { "DEBT-FG13END06-02", "FG-GAP-252", "DEBT-FG14SBX02-02", "DEBT-FG14SBX02-03" };

        private static readonly string[] FixtureDebts = { "DEBT-FG14E2E01-01", "DEBT-FG14E2E01-02" };

        private static string CardBody(string cards, string id)
        {
            int at = cards.IndexOf("### " + id + " ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cards.IndexOf("\n### ", at + 10, StringComparison.Ordinal);
            return at < 0 ? string.Empty : end < 0 ? cards.Substring(at) : cards.Substring(at, end - at);
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M14 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（按门禁列的第一个里程碑判定）；M14 承接项都已 Closed；本 Story 的登记与卡片正文承接");
            string text = ReadRepo(Register);
            if (text == null)
            {
                Fail("找不到 " + Register + "（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            int gapRows = 0, debtRows = 0, m14Rows = 0;
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
                if (gate == null || FirstMilestoneOf(gate) != 14)
                {
                    continue;
                }
                m14Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Expect(gapRows > 200 && debtRows > 500 && m14Rows >= 4, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M14 的 {m14Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M14 的缺口 / 延后项全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明理由与新门禁）"
                    : $"FG-M14 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            Expect(notClosed.Count == 0, $"承接写着 M14 各卡的 {ClosedHere.Length} 条都是 Closed（状态格以 Closed 开头）{(notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed))}");
            string cards = ReadRepo(Cards) ?? string.Empty;
            string qa01 = CardBody(cards, "FG15-QA-01");
            string qa02 = CardBody(cards, "FG15-QA-02");
            string bal = CardBody(cards, "FG15-BAL-01");
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG14E2E01-03", out string p) && p.Contains("待用户");
            List<string> uncarried = FixtureDebts.Where(id => !rows.TryGetValue(id, out string r) || !r.Contains("FG15-QA-01") || !qa01.Contains(id)).ToList();
            if (!rows.TryGetValue("FG-GAP-254", out string g) || !g.Contains("FG15-BAL-01") || !bal.Contains("FG-GAP-254"))
            {
                uncarried.Add("FG-GAP-254");
            }
            bool playCarried = rows.TryGetValue("DEBT-FG14E2E01-03", out string pr) && pr.Contains("FG15-QA-02") && qa02.Contains("DEBT-FG14E2E01-03");
            Expect(unregistered.Count == 0 && pending && uncarried.Count == 0 && playCarried,
                "本 Story 的登记在表里（夹具 DEBT-FG14E2E01-01 / 02、试玩 -03 待用户、平衡观察 FG-GAP-254）；夹具债承接 FG15-QA-01、试玩债承接 FG15-QA-02、平衡观察承接 FG15-BAL-01，三张卡正文都有承接行（规则第 7 条）" +
                (unregistered.Count + uncarried.Count == 0 && playCarried ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；承接缺：{string.Join("、", uncarried)}；试玩承接 {playCarried}"));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M14 材料齐全（里程碑文档没写“试玩：要”= 可选试玩；真人试玩由用户择时进行，DEBT-FG14E2E01-03，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M14-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M14-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M14-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M14-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            int at = readme.IndexOf("## FG-M14 后日谈与沙盒", StringComparison.Ordinal);
            string section = at >= 0 ? readme.Substring(at) : string.Empty;
            bool registered = section.Contains("DEBT-FG14E2E01-03") && section.Contains("不阻塞") && section.Contains("FGJ-M14");
            bool covers = script.Contains("FG12 第 7 节") && script.Contains("后日谈") && script.Contains("周期清洗") && script.Contains("坚守") && script.Contains("沙盒")
                          && script.Contains("种子") && script.Contains("关闭突袭") && table.Contains("T8") && form.Contains("Q1") && issues.Contains("严重度");
            Expect(registered && covers, $"目录登记“待用户试玩、不阻塞”并写明 FGJ-M14 断点存档（{registered}）；脚本覆盖里程碑原文（后日谈继续、周期清洗、坚守统计、自定义种子关闭突袭的沙盒）+ FG12 第 7 节两问，对照表 T1～T8（{covers}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M14“必须完成”各项有自检段且登记在全量自检；玩家主路径各有 FGJ-M14 步骤（低频 / 失败路径只在自检，轻量口径 b）");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m14 = JourneyCatalog.Create(FgjM14Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("FGR-SBX-001 继续游戏（FG14-SBX-01）", Pg, "e_continue"),
                ("FGR-SBX-002 周期清洗（FG14-SBX-01）", Pg, "p2_done"),
                ("FGR-SBX-003 坚守统计（FG14-SBX-01）", Pg, "pg_stats"),
                ("FGR-SBX-004 后日谈内容（轨道坠落更频繁、最后一组人类遗产；低频，只在自检）", Pg, null),
                ("FGR-SBX-005 失败（后日谈中核心被毁；失败路径，只在自检）", Pg, null),
                ("FGR-SBX-010 沙盒入口（FG14-SBX-02）", Sb, "sb_open"),
                ("FGR-SBX-011 沙盒设置项（FG14-SBX-02）", Sb, "sb_setup"),
                ("FGR-GEN-070、071 沙盒的种子与世界设置（含领地距离；FG14-SBX-02）", Wg, "sb_check"),
                ("FGR-GEN-070、071 沙盒照用 FG17 生成参数（领地远）", Sb, "sb_check"),
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = step == null || (m14?.Steps.Any(s => s.Id == step) ?? false);
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM14SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，主路径 7 项各有 FGJ-M14 旅程步骤；[M14 出口] 本身登记在全量自检" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
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
            Debug.LogWarning("[M14 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
