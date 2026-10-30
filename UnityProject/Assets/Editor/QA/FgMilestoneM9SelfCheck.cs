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
    /// FG9-E2E-01 自检 [M9 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M9 跑）。
    /// 轻量口径（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——第一幕的六类反向在本里程碑各 Story 的自检里都是真实系统（首领战内核、
    /// 世界模拟、节奏台账、真文件存读档、远征行军）跑出来的断言，已并入全量自检；本段 R 守护这张映射：每一类至少一条断言仍在源码里、所在段仍登记在全量自检，
    /// 删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M9 覆盖里程碑原文（主菜单新游戏 → 第一次远征 → 铸造前哨 → 击败铸造主核心 → 第一幕结束）；FGJ-M9A 是它的前 N 步；
    ///    FGJ-GEN9（DEBT-FG3GEN01-10）与 FGJ-M9 同一串步骤、只多点选极端世界设置；固定种子；断点都是旅程里的步骤。
    /// A2. FGJ-M9 源码只走输入通道（不直接调业务方法）；开局夹具只在 ApplyFixture 里；截断夹具已删（FG-GAP-136 产品修复：覆盖外截断由行军服务按最后的命令自动重试），旅程源码不改行军状态。
    /// A3. FGJ-M3 的 FG-GAP-145 诊断分支与逐帧诊断日志已清掉（DEBT-FG9ACT101-06 ②）。
    /// R1～R6. IC-REQ-022 反向场景映射（资源 / 条件不足、目标死亡、断网 / 失联、暂停与倍速、存读档、路径失败）。
    /// C. 缺口清零：门禁第一个里程碑是 M9 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明新门禁）；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M9 五份材料齐全，目录登记“待用户试玩、不阻塞”，并写明出口旅程断点可做试玩存档。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”7 条（拆成 9 项）里 8 项各有自检段且登记在全量自检，能进旅程的另有 FGJ-M9 步骤；FGR-FAC-009 嵌合机队标志组件未实现，登记 FG-GAP-150 顺延。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM9SelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgMilestoneM9SelfCheck.RunFromMenu。
    /// </summary>
    public static class FgMilestoneM9SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";

        [MenuItem("BinGames/QA/自检/M9 出口")]
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
            Line("\n[M9 出口] FG9-E2E-01：旅程登记与正式输入、夹具守护、M3 诊断清理、IC-REQ-022 六类反向（真实系统断言映射）、缺口清零、试玩包、里程碑需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckM3DiagnosticsRemoved);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Line($"  · [M9 出口] 断言通过 {_pass}，失败 {_fail}");
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

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static bool Wellformed(JourneyDef d) => d != null && d.Steps.Count > 0 && d.Steps.Select(x => x.Id).Distinct().Count() == d.Steps.Count
                                                        && d.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && d.TotalTimeoutSeconds > 0;

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M9 覆盖里程碑原文；FGJ-M9A 是它的前段；FGJ-GEN9 复用 FGJ-M9 的步骤；固定种子；断点都是旅程里的步骤");
            JourneyDef m9 = JourneyCatalog.Create(FgjM9Journey.Id);
            JourneyDef m9a = JourneyCatalog.Create(FgjM9FrontJourney.Id);
            JourneyDef gen9 = JourneyCatalog.Create(FgjGenExtremeJourney.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子、正式节奏", new[] { "menu_new", "new_game", "seed", "pace_wake" }),
                ("第一次远征破碎都市、走回家园、基础传送带开放（FGJ-M9A）", new[] { "prep_depart", "in_city", "walk_home", "belts_open" }),
                ("远征准备面板选铸造前哨、沿地形行军进入外围", new[] { "fo_prep_open", "fo_prep_pick", "fo_prep_dest", "depart", "fo_enter" }),
                ("在铸造前哨被干扰机断链（DEBT-FG1E2E01-03）", new[] { "fo_uplink", "fo_jam" }),
                ("RTS 口径：左键 / Shift 加选、右键敌人 = 攻击、右键地面 = 移动", new[] { "fo_select", "fo_clear", "fo_gate" }),
                ("击败铸造主核心（供能节点 → 阶段一 / 二 / 对空形态）", new[] { "fo_nodes", "fo_core", "fo_won" }),
                ("第一幕结束后资料库 / 图鉴读得到", new[] { "lib_open", "codex_entry" }),
            };
            List<string> missing = exit.Where(x => m9 == null || x.Ids.Any(id => m9.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            Expect(Wellformed(m9) && missing.Count == 0 && m9.Seed == FgjM3Journey.TestSeed,
                $"FGJ-M9：{m9?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m9?.Seed}；里程碑原文都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool prefix = m9a != null && m9 != null && m9a.Steps.Count < m9.Steps.Count
                          && m9a.Steps.Select(s => s.Id).SequenceEqual(m9.Steps.Take(m9a.Steps.Count).Select(s => s.Id));
            Expect(prefix, $"FGJ-M9A（{m9a?.Steps.Count} 步）原样是 FGJ-M9 的前段（卡片承接：FGJ-M9 从 FGJ-M9A 的步骤接着写）");
            bool sameSteps = gen9 != null && m9 != null && gen9.Steps.Select(s => s.Id).SequenceEqual(m9.Steps.Select(s => s.Id)) && gen9.Seed == m9.Seed;
            bool extremeTitle = gen9?.Steps.FirstOrDefault(s => s.Id == "new_game")?.Title?.Contains(FgWorldGenHomeSelfCheck.ExtremeCode) ?? false;
            Expect(sameSteps && extremeTitle && !JourneyBotHost.PrePacingJourney(FgjGenExtremeJourney.Id),
                $"FGJ-GEN9（FGT-GEN-009，DEBT-FG3GEN01-10）：与 FGJ-M9 同一串 {gen9?.Steps.Count} 步、同一种子，新游戏步骤点选极端设置 {FgWorldGenHomeSelfCheck.ExtremeCode}（{extremeTitle}），按正式节奏（不走旧规则开关）");
            bool ck = m9 != null && m9.CheckpointAfter.Length >= 1 && m9.CheckpointAfter.All(id => m9.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM9Journey.Id) && JourneyCatalog.Ids.Contains(FgjM9FrontJourney.Id) && JourneyCatalog.Ids.Contains(FgjM8Journey.Id),
                $"FGJ-M9 / M9A 登记在旅程登记表、前序旅程仍在；断点 [{string.Join(", ", m9?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M8 的清单 + M9 的伤害 / 首领 / 平定 / 出厂捷径）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "BeltNetworkService.TryPlace", "PipeNetworkService.TryPlace",
            "CampaignSaveService.Save(", "CampaignAutoSaveService.Save", "UiConfirmDialog.Confirm(", "BuildingOps.Try", "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(",
            "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request", "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(",
            ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "MachineRoster.Try", "ExpeditionDepartureService.TryDepart(", "ExpeditionMarchService.IssueCommand(",
            "DropPodService.TryRecall(", "DropPodService.TryLaunch(", "LootPickService.Grant(", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(",
            "WorldView.FlyTo(", "WorldView.ToggleFollowExpedition(", "TerritoryControlService.MarkLeaderDefeated(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", "FoundryOutpostRegion.TryDamageEnemy(", ".TryDamageEnemy(", "FoundryOutpostCoreBoss.ApplyDamage(", "SilentNightService.PlanDayForTests(",
        };

        /// <summary>开局夹具（ADR-QA-026 第 4 节，DEBT-FG9E2E01-01）：只能出现在 ApplyFixture 里。</summary>
        private static readonly (string Token, string Method, int Count)[] Fixtures =
        {
            ("MachineRegistry.SpawnMachine(", "ApplyFixture", 1),
            ("MachineLoadoutRegistry.Register(", "ApplyFixture", 1),
            ("BlueprintEditorService.TrySave(", "ApplyFixture", 2),
            ("s.SignalExposure = ", "ApplyFixture", 1),
            ("s.TechData += ", "ApplyFixture", 1),
            ("s.UnlockedContentIds = ", "ApplyFixture", 1),
            ("fo.State = RegionState.Available", "ApplyFixture", 1),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M9 源码只走输入通道；开局夹具只在 ApplyFixture；截断夹具已删（FG-GAP-136 产品修复），旅程源码不改行军状态");
            string text = ReadRepo(Bots + "FgjM9Journey.cs");
            string front = ReadRepo(Bots + "FgjM9FrontJourney.cs");
            string exp = ReadRepo(Bots + "FgjM8Journey.Expedition.cs");
            if (text == null || front == null || exp == null)
            {
                Fail("找不到旅程源码 FgjM9Journey.cs / FgjM9FrontJourney.cs / FgjM8Journey.Expedition.cs");
                return;
            }
            string code = StripComments(text);
            string codeFront = StripComments(front);
            List<string> hits = ForbiddenCalls.Where(x => code.Contains(x)).Select(x => "FgjM9Journey.cs:" + x)
                .Concat(ForbiddenCalls.Where(x => codeFront.Contains(x)).Select(x => "FgjM9FrontJourney.cs:" + x)).ToList();
            Expect(hits.Count == 0, $"FGJ-M9 / M9A 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            var bad = new List<string>();
            var seen = new List<string>();
            string body = MethodBodyAny(code, "ApplyFixture");
            foreach ((string token, string method, int count) in Fixtures)
            {
                int total = Count(code, token);
                int n = body == null ? 0 : Count(body, token);
                seen.Add($"{token.Trim().TrimEnd('(', '=').Trim()} {n} 处");
                if (n != count || total != n)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、方法外 {total - n} 处");
                }
            }
            Expect(bad.Count == 0, $"开局夹具只在 ApplyFixture 里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string ce = StripComments(exp);
            bool noFixture = !ce.Contains("haltFixture") && !ce.Contains("m.Halted = ") && !ce.Contains("m.OrdersPending = ") && !codeFront.Contains("m.Halted = ") && !code.Contains("m.Halted = ");
            bool shared = ce.Contains("TickDepart(JourneyContext c) => TickDepart(c, 190)") && codeFront.Contains("FgjM8Journey.TickDepart(c, 190)") && code.Contains("FgjM8Journey.TickDepart(c, 320)");
            Expect(noFixture && shared,
                $"截断夹具（DEBT-FG9E2E01-02）已删：覆盖外“路线被截断”由行军服务按最后的命令自动重试（FG-GAP-136），旅程源码不再改行军状态（{noFixture}）；FGJ-M8 / M9A / M9 共用同一个出发检查（{shared}）");
            string src = text + front;
            string[] inputs =
            {
                "GameActionId.Cancel", "GameActionId.OpenCodex", "FgjM1Journey.ClickUi(", "FgjM1Journey.ClickMachineList(", "FgjM1Journey.ClickBuildingIf(", "\"DepartButton\"",
                "JourneyInput.", "PressAction(",
            };
            List<string> miss = inputs.Where(x => !src.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：Esc / 图鉴键、UI Toolkit 面板点击（远征准备 / 暂停菜单 / 资料库 / 图鉴）、机器列表、左键建筑、远征准备面板“出发”、世界点击输入通道" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── A3. M3 诊断清理 ──────────────────────────────────────────────────────

        private static void CheckM3DiagnosticsRemoved()
        {
            Line("  · A3. FGJ-M3 的 FG-GAP-145 诊断分支与逐帧诊断日志已清掉（DEBT-FG9ACT101-06 ②）");
            string m3 = ReadRepo(Bots + "FgjM3Journey.cs");
            Expect(m3 != null && !m3.Contains("BINGAMES_M3_PRE_1X") && !m3.Contains("TrackLoop(") && !m3.Contains("SamplePorts(") && !m3.Contains("FG-GAP-145 诊断]"),
                "FgjM3Journey.cs 里没有环境变量诊断分支（BINGAMES_M3_PRE_1X）与每帧 TrackLoop / SamplePorts 诊断日志");
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（第一幕）：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；能进旅程的另有 FGJ-M9 步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m9 = JourneyCatalog.Create(FgjM9Journey.Id);
            var map = new (string Category, string Segment, string File, string Label)[]
            {
                ("R1 资源 / 条件不足：核心门三灯未亮 → 拒绝进入并逐项写明缺什么", "NegativePathSelfCheck", "NegativePathSelfCheck.cs", "核心门三灯未亮：拒绝进入并逐项写明缺什么"),
                ("R1 资源不足：严酷难度（资源更紧）第一幕仍能走完、核心不丢", "FgAct1BalanceSelfCheck", "FgAct1BalanceSelfCheck.cs", "C1 严酷（种子 42）：第一幕走完"),
                ("R2 目标死亡：铸造主核心阶段三窗口期内全灭 → 复位到开战前、记上次止步于、首领战前自动存档可重试", "FgFoundryFactionSelfCheck", "FgFoundryFactionSelfCheck.cs", "C8 负向“阶段三窗口期内全灭”"),
                ("R2 目标死亡：寂听主脑首领战全灭 → 大厅卸载、首领复位", "FgSilentFactionSelfCheck", "FgSilentFactionSelfCheck.cs", "C10 FGT-FAC-004 首领战全灭"),
                ("R3 断网 / 失联：干扰机的干扰场 10 米内被干扰、13 米外不干扰；击毁干扰机后干扰场随之解除", "FgEnemyTableSelfCheck", "FgEnemyTableSelfCheck.cs", "干扰场随之解除"),
                ("R3 恢复：撑过全频静默后可以再接入", "FgSilentFactionSelfCheck", "FgSilentFactionSelfCheck.cs", "静默结束后可以再接入"),
                ("R4 暂停与倍速：铸造主核心对空形态 → 狂暴在 0.5x / 1x（中途暂停）/ 2x / 3x 下逐位相同", "FgFoundryFactionSelfCheck", "FgFoundryFactionSelfCheck.cs", "C10 B09 / FGR-BASE-021"),
                ("R4 暂停与倍速：寂听主脑 26 游戏秒在 0.5x～3x（中途暂停）下逐位相同", "FgSilentFactionSelfCheck", "FgSilentFactionSelfCheck.cs", "C12 B09 / FGR-BASE-021"),
                ("R4 暂停与倍速：节奏台账暂停不推进、各倍速记在同一步", "FgAct1SelfCheck", "FgAct1SelfCheck.cs", "B8 暂停时节奏台账不推进"),
                ("R5 存读档：铸造主核心首领战中存档读档一致、续跑逐位相同", "FgFoundryFactionSelfCheck", "FgFoundryFactionSelfCheck.cs", "C7 FGT-FAC-003 首领战中存档"),
                ("R5 存读档：寂听主脑首领战中存档", "FgSilentFactionSelfCheck", "FgSilentFactionSelfCheck.cs", "C9 FGT-FAC-003 首领战中存档"),
                ("R5 存读档：节奏台账逐字段一致、读档不补发不重记", "FgAct1SelfCheck", "FgAct1SelfCheck.cs", "B9 存读档：节奏台账逐字段一致"),
                ("R6 路径失败（第一幕 0:04～8:30，没有中继 / 投送站）：覆盖外“路线被截断”→ 提示一次、按最后的“前进”自动重试、打通后进入地点", "FgExpeditionMarchSelfCheck", "FgExpeditionMarchSelfCheck.cs", "M11b 打通后下一次自动重试走进去并进入远征地点"),
                ("R6 路径失败（覆盖内 / 投送站解锁后）：行军“路线被截断”停下待命、提示原因 → 改用投送舱接回", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R6 恢复：在截断点旁呼叫投送舱"),
                ("R6 路径失败：门锁着时右键核心区禁区 → 机器停在封锁线外（不穿门）", "FgAct1SelfCheck", "FgAct1SelfCheck.cs", "C13 负向：门锁着时在外圈右键核心区一侧的禁区"),
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
            bool journey = m9 != null && new[] { "fo_jam", "fo_gate", "depart" }.All(id => m9.Steps.Any(s => s.Id == id));
            Expect(journey, "能进正向旅程的反向在 FGJ-M9 里另有真实输入步骤：被干扰机断链（fo_jam）、核心门三灯亮后才放行（fo_gate）、行军途中“路线被截断”点“前进”（depart）");
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        /// <summary>门禁 M9 的行写“部分关闭”时，只有写明“剩……”且门禁列另有晚于 M9 的里程碑（剩余部分的门禁）才算 M9 出口合格。</summary>
        private static bool PartialRemainderDeferred(string gate, string st)
        {
            if (!st.StartsWith("部分关闭", StringComparison.Ordinal) || !st.Contains("剩"))
            {
                return false;
            }
            foreach (Match m in FirstMilestone.Matches(gate ?? string.Empty))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 9)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered =
        {
            "DEBT-FG9E2E01-01", "DEBT-FG9E2E01-02", "DEBT-FG9E2E01-03", "DEBT-FG9E2E01-04", "FG-GAP-148", "FG-GAP-149", "FG-GAP-150",
        };

        private static readonly string[] ClosedHere =
        {
            "DEBT-FG1E2E01-03", "DEBT-FG3GEN01-10", "DEBT-FG9ACT101-06", "DEBT-FG6DEF03-05", "DEBT-FG6DEF04-12", "DEBT-FG9E2E01-02", "FG-GAP-146",
        };

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M9 的缺口全部 Closed / 部分关闭（剩余另有门禁），延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            var partialRows = new List<string>();
            int gapRows = 0, debtRows = 0, m9Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var gates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                bool gap = raw.StartsWith("| FG-GAP-", StringComparison.Ordinal);
                if (!gap && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
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
                gates[cols[0]] = gate;
                if (gate == null || FirstMilestoneOf(gate) != 9)
                {
                    continue;
                }
                m9Rows++;
                if (st.StartsWith("部分关闭", StringComparison.Ordinal))
                {
                    partialRows.Add(cols[0]);
                }
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || (!gap && st.StartsWith("顺延", StringComparison.Ordinal));
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Line("    门禁属于 FG-M9、状态“部分关闭”的行：" + (partialRows.Count == 0 ? "无" : string.Join("、", partialRows)));
            Expect(gapRows > 100 && debtRows > 300 && m9Rows >= 10, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M9 的 {m9Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M9 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（顺延写明理由与新门禁）"
                    : $"FG-M9 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG9BAL01-01", out string p1) && p1.Contains("待用户");
            Expect(unregistered.Count == 0 && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG9E2E01-01～04 旅程夹具 / 引导钩子隔离 / 读档性能，FG-GAP-148 首领战时长，FG-GAP-149 M6R 续跑失败，FG-GAP-150 嵌合机队标志组件）；M9 试玩仍是 DEBT-FG9BAL01-01 待用户；" +
                $"由本 Story 关闭的 {ClosedHere.Length} 条都是 Closed" + (unregistered.Count + notClosed.Count == 0 ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；没关：{string.Join("、", notClosed)}"));
            // 修复轮 2：FGJ-M6R 四次运行 1 PASS 3 FAIL，FG-GAP-149 重开、交 FG10-E2E-01；M9 出口只要求它已登记、没有被写成关闭、门禁晚于 M9。
            status.TryGetValue("FG-GAP-149", out string s149);
            gates.TryGetValue("FG-GAP-149", out string g149);
            // FG10-E2E-01：承接方修好并两次完整跑 PASS 后关闭（Closed 里写明 FG10-E2E-01）也算合格。
            // FG10-E2E-01 兜底（总控 2026-10-28 认可）：改承接 EMG1-E2E-01，门禁 EMG-1（FirstMilestoneOf 解析为 -1，同样晚于 M9）。
            // EMG1-E2E-01（2026-10-30）修好并两次完整跑 PASS 后关闭：Closed 里写明 EMG1-E2E-01 与两次 PASS 也算合格。
            bool closedByEmg = s149 != null && s149.StartsWith("Closed", StringComparison.Ordinal) && s149.Contains("EMG1-E2E-01") && s149.Contains("两次");
            bool emgGate = g149 != null && FirstMilestoneOf(g149) < 0 && g149.Contains("EMG-1") && s149 != null && (s149.Contains("改承接 EMG1-E2E-01") || closedByEmg);
            bool m6rOpen = s149 != null && (!s149.StartsWith("Closed", StringComparison.Ordinal) || s149.Contains("FG10-E2E-01") || closedByEmg) && g149 != null && (FirstMilestoneOf(g149) > 9 || emgGate)
                           && text.Contains("FG10-E2E-01（FG-M10 出口回归前修 FGJ-M6R 编排");
            Expect(m6rOpen, $"FGJ-M6R：FG-GAP-149 已登记（{s149?.Split('（')[0]}）、门禁 {g149} 晚于 M9、承接 FG10-E2E-01（兜底后改承接 EMG1-E2E-01）；未关闭，或已由承接方写明两次完整跑 PASS 后关闭（{closedByEmg}）");
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M9 材料齐全（必试玩，真人试玩由用户择时进行，DEBT-FG9BAL01-01，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M9-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M9-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M9-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M9-问题清单模板.md");
            string tuning = ReadRepo(dir + "FG-M9-调参记录.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && tuning != null && readme != null;
            Expect(exist, "五份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板、调参记录）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool standard = table.Contains("6～10") && table.Contains("20 分钟");
            int m9At = readme.IndexOf("## FG-M9", StringComparison.Ordinal);
            string m9Section = m9At >= 0 ? readme.Substring(m9At) : string.Empty;
            bool registered = m9Section.Contains("DEBT-FG9BAL01-01") && m9Section.Contains("不阻塞") && m9Section.Contains("FGJ-M9") && m9Section.Contains("FG-GAP-148");
            Expect(standard && registered && script.Contains("第一幕结束"),
                $"对照表写着 FGR-BAL-061 两条判据（6～10 小时、卡关不超过 20 分钟，{standard}）；目录登记“待用户试玩、不阻塞”，并写明 FGJ-M9 断点存档与首领战时长观察点 FG-GAP-148（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M9“必须完成”7 条拆成 9 项：8 项各有自检段且登记在全量自检，能进旅程的另有 FGJ-M9 步骤；FGR-FAC-009 未实现、登记 FG-GAP-150 顺延");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m9 = JourneyCatalog.Create(FgjM9Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("敌人全部入表（FGR-FAC-001，FG9-DATA-01）", "FgEnemyTableSelfCheck", "fo_enter"),
                ("叙事投放框架与资料库（FGR-NAR-001～004，FG9-NAR-01）", "FgNarrativeSelfCheck", "lib_open"),
                ("静默扩充与寂听主脑（FG09 第 4 章，FG9-FAC-01）", "FgSilentFactionSelfCheck", null), // 寂听主脑首领战：轻量口径 b，不进正向旅程
                ("铸造扩充与主核心阶段三（FG09 第 5 章，FG9-FAC-02）", "FgFoundryFactionSelfCheck", "fo_core"),
                ("通用基元与专属语法：静默专属行为（FGR-FAC-008，FG9-FAC-01 B 段）", "FgSilentFactionSelfCheck", null),
                ("通用基元与专属语法：铸造专属行为（FGR-FAC-008，FG9-FAC-02）", "FgFoundryFactionSelfCheck", "fo_core"),
                ("第一幕流程：解锁节奏、伏笔、引导钩子（FGR-NAR-010，FG9-ACT1-01）", "FgAct1SelfCheck", "belts_open"),
                ("正式版图鉴（FGR-UX-050、051，FG9-UX-01）", "FgCodexSelfCheck", "codex_entry"),
                ("第一幕平衡与试玩（FGT-BAL-004，FG9-BAL-01）", "FgAct1BalanceSelfCheck", null), // 6～10 小时节奏：节奏机器人 + 真人试玩
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = step == null || (m9?.Steps.Any(s => s.Id == step) ?? false);
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step ?? "无旅程步骤"}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM9SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，6 项另有 FGJ-M9 旅程步骤（寂听主脑首领战、静默专属行为、6～10 小时节奏由自检 / 试玩覆盖）；[M9 出口] 本身登记在全量自检" +
                (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
            // 第 9 项 FGR-FAC-009（嵌合机队阵营标志组件框架 + 静默 / 铸造标志组件）：M9 没有 Story 承接、没有实现——如实登记顺延，不算覆盖。
            string reg = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md") ?? string.Empty;
            string row = reg.Split('\n').FirstOrDefault(x => x.StartsWith("| FG-GAP-150 ", StringComparison.Ordinal)) ?? string.Empty;
            string[] cols = row.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
            bool deferred = cols.Length >= 7 && cols[2].Contains("FGR-FAC-009") && FirstMilestoneOf(cols[5]) > 9 && !cols[6].StartsWith("Closed", StringComparison.Ordinal) && cols[6].Contains("待用户");
            Expect(deferred, $"FGR-FAC-009 嵌合机队标志组件没有实现：已登记 FG-GAP-150（门禁晚于 M9、待用户复核顺延，{deferred}），不冒充已覆盖");
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

        /// <summary>取方法 “名字(JourneyContext” 之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBodyAny(string code, string method)
        {
            Match m = Regex.Match(code, @"\b" + Regex.Escape(method) + @"\s*\(\s*JourneyContext");
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
            Debug.LogWarning("[M9 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
