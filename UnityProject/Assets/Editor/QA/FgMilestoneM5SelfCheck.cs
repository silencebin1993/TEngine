using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG5-E2E-01 自检 [M5 出口]：里程碑出口的自动化部分里能在编辑模式下逐语义断言的那些（旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M5 / FGJ-M5R 跑）。
    ///
    /// A. 旅程登记：FGJ-M5 覆盖出口旅程原文七步（加密固件进解析台破解、研究节点解锁新建筑、远征回放拿线索、模拟熔合失败不消耗、正式熔合进配方书、
    ///    靶场投影接入测试、情报面板看到突袭预报），并回补 DEBT-FG5RND03-05 / 04-05 / 05-10 / 06-03 / 01-07 / 04-07 / 05-06 / 06-04 的旅程段；
    ///    FGJ-M5R 覆盖 IC-REQ-022 六类（每类出错 → 恢复两端）；种子不同；都登记断点。
    /// A2. 旅程源码只走输入通道：不直接调研究 / 熔合 / 靶场 / 解析 / 情报 / 放置 / 施工 / 存档等业务方法；七处夹具只在登记的夹具方法里。
    /// A3. DEBT-FG5RND01-05：研发树开放前写成的旧旅程（FGJ-M3 / M3R / M4 / M4R）开局登记“已研究”进度夹具，夹具只记与旧档迁移同一口径的节点。
    /// B. 规划不依赖固定坐标（B25）：FGJ-M5 的整套研发建筑规划在 5 个种子上都找得到、逐件过放置校验（监听站只差研究）且互不重叠；FGJ-M5R 的在 2 个种子上找得到。
    /// C. 缺口清零：门禁第一个里程碑是 M5 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M5 的试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：FG05 FGT-RND-001～009 各有自检段且登记在全量自检，另有旅程步骤。
    /// F. 技术数据收支按来源分项（DEBT-FG5RND06-04）：账本事务按来源归类、收支累计、真文件存读档逐项一致；统计面板“研发”页的文本按存档里的累计写。
    /// G. 新档的技术数据供给（DEBT-FG5RND01-08）：开局技术数据够研究开放前开局可造的全部节点；只在主菜单“新建”时发一次，自检用的空白战役不经过。
    /// H. 研发加成的数值来源行（DEBT-FG5RND01-07）：生产建筑速度 / 实验室效率 / 仓库容量各写“研发 +X%（来自节点）”，没有加成时不写。
    /// I. 界面：统计面板“研发”页签与建筑面板来源行接线、研发树窗口固定高度（FGJ-M5R 抓到：悬停节点详情变长 → 窗口变高、整棵树挪位置）、布局探针。
    /// </summary>
    public static class FgMilestoneM5SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/M5 出口")]
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
            Line("\n[M5 出口] FG5-E2E-01：旅程登记与正式输入、旧旅程研究夹具、规划不依赖固定坐标、缺口清零、试玩包、需求覆盖、技术数据收支分项、新档技术数据、研发加成来源行、界面");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckLegacyResearchFixture);
            Step(CheckPlannerSeeds);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckBehaviour);
            Step(CheckUi);
            Line($"  · [M5 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static List<string> Missing(JourneyDef d, IEnumerable<(string What, string[] Ids)> map) =>
            map.Where(x => d == null || x.Ids.Any(id => d.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M5 覆盖出口旅程原文七步与回补；FGJ-M5R 覆盖 IC-REQ-022 六类；种子不同；两条都登记断点");
            JourneyDef m5 = JourneyCatalog.Create(FgjM5Journey.Id);
            JourneyDef m5r = JourneyCatalog.Create(FgjM5ReverseJourney.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("把远征带回的加密固件送进解析台，破解后裸跑标记消失", new[] { "databox", "evac_confirm", "sc_raw", "sc_raw_read", "ana_chip", "ana_cracked", "sc_unmarked" }),
                ("研究一个节点，解锁新建筑", new[] { "rt_open", "rt_post", "rt_done", "post_new", "post_place", "post_built" }),
                ("从远征回放拿到线索", new[] { "fc_attack", "clue", "fu_clue" }),
                ("模拟熔合，失败不消耗固件", new[] { "fu_fail_pick", "fu_fail" }),
                ("正式熔合成功，配方进入配方书", new[] { "fu_hit", "fu_formal", "fu_done", "fu_book" }),
                ("在靶场投影新蓝图并接入测试", new[] { "burn_chip", "cb_mix", "cb_save3", "rng_send", "rng_uplink", "rng_run1", "rng_end1", "rng_compare" }),
                ("在情报面板看到下一次突袭的预报", new[] { "intel_raid", "intel_open", "intel_map", "raid_arrive" }),
                ("回补：新档技术数据够研究（DEBT-FG5RND01-08）", new[] { "start_tech" }),
                ("回补：研发建筑从正式入口建造、研究与“新”标记（DEBT-FG5RND01-04 / 05-10）", new[] { "m5_place2", "m5_built2", "rt_gather", "post_new", "intel_new" }),
                ("回补：黑匣子从阵亡到研究用上这份技术数据（DEBT-FG5RND06-03）", new[] { "bb_kill", "bb_done", "bb_read" }),
                ("回补：混合固件装进出厂的机器（DEBT-FG5RND04-05，修复轮）", new[] { "fac_open3", "fac_mix", "wait_mix" }),
                ("回补：研发加成来源行（DEBT-FG5RND01-07）", new[] { "bonus_open", "bonus_read" }),
                ("回补：统计面板研发页（DEBT-FG5RND06-04 / 04-07 / 05-06）", new[] { "stats_open", "stats_read" }),
                ("回补：靶场测试中存档（FGR-RND-031）", new[] { "save_esc", "save_menu", "save_loaded" }),
            };
            List<string> missing = Missing(m5, exit);
            Expect(Wellformed(m5) && missing.Count == 0 && m5.Seed == FgjM5Journey.TestSeed,
                $"FGJ-M5：{m5?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m5?.Seed}；出口旅程七步与回补都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string What, string[] Ids)[]
            {
                ("路径失败（缺关键材料等待、缺前置被拒 → 补上）", new[] { "r1_key", "r1_prereq", "r1_pre_q", "r1_t3_q", "r1_fixture", "r1_ok" }),
                ("资源不足（正式熔合缺芯片基板 → 补上）", new[] { "r2_short", "r2_fixture", "r2_formal" }),
                ("目标死亡（熔合中合成台被毁 → 回滚 → 重建再熔合）", new[] { "r3_destroy", "r3_panel", "r3_rebuild", "r3_built", "r3_formal", "r3_done" }),
                ("断链（断电研究暂停 → 来电恢复）", new[] { "r4_off1", "r4_stalled", "r4_on1", "r4_back" }),
                ("暂停与倍速、移出队列进度保留", new[] { "r5_pause", "r5_frozen", "r5_half", "r5_triple", "r5_remove", "r5_requeue", "r5_continue" }),
                ("存读档", new[] { "r6_quit", "r6_menu", "r6_load", "r6_loaded" }),
            };
            List<string> missingR = Missing(m5r, reverse);
            Expect(Wellformed(m5r) && missingR.Count == 0 && m5r.Seed != m5.Seed,
                $"FGJ-M5R：{m5r?.Steps.Count} 步；六类反向场景齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}；种子 {m5r?.Seed} ≠ {m5?.Seed}（B25）");
            bool ck = m5 != null && m5r != null && m5.CheckpointAfter.Length >= 2 && m5r.CheckpointAfter.Length >= 1
                      && m5.CheckpointAfter.All(id => m5.Steps.Any(s => s.Id == id)) && m5r.CheckpointAfter.All(id => m5r.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM5Journey.Id) && JourneyCatalog.Ids.Contains(FgjM5ReverseJourney.Id) && JourneyCatalog.Ids.Contains(FgjM4Journey.Id),
                $"两条 M5 旅程登记在旅程登记表、前序旅程仍在；断点：FGJ-M5 [{string.Join(", ", m5?.CheckpointAfter ?? Array.Empty<string>())}]、FGJ-M5R [{string.Join(", ", m5r?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M4 的清单 + M5 的研究 / 熔合 / 靶场 / 解析 / 情报）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Redo(", "PlanHistory.Paste(", "PlanHistory.Upgrade(", "PlanHistory.Place(", "PlanHistory.ToggleDemolish(", "PlanHistory.Relocate(",
            "PlanHistory.PlaceBeltPath(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "HomeGridService.TryToggleDemolish", "HomeGridService.TryUpgrade",
            "BeltNetworkService.TryPlace", "BeltNetworkService.TrySet", "BeltNetworkService.TryRemove", "BeltNetworkService.TryReverse", "PipeNetworkService.TryPlace",
            "BeltPortService.TrySetFilter", "LayoutLibrary.TrySave", ".StartPaste(", "HomeValleyWorkOrders.TryAssign", "HomeValleyWorkOrders.TryCreate",
            "ExpeditionDepartureService.TryDepart", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "UiConfirmDialog.Cancel(", "ProductionService.TrySet",
            "HomeValleyFactory.TryEnqueue", ".SetScrapSubstitute(", "SignalCoreService.Try", "BuildingOps.Try", "ProductionPanelUIToolkit.Open(", "ExpeditionReturnService.TryConfirm",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "HomeValleyPowerGrid.TrySet", "HomeValleyPowerGrid.TryToggleShutdown", "PrimitiveInventory.TryMove",
            "ResearchService.TryEnqueue", "ResearchService.TryDequeue", "ResearchService.TryMove", ".ClickNode(", ".RightClickNode(", ".DropQueueAt(", ".BeginQueueDrag(",
            "ResearchTreePanelUIToolkit.Open", "FusionService.Simulate(", "FusionService.EnqueueFormal", "FusionService.Cancel(", "TestRangeService.SendBlueprint(",
            "TestRangeService.SendFirmware(", "TestRangeService.ProjectBlueprint(", "TestRangeService.Uplink(", "TestRangeService.EndTest(", "TestRangeService.SetCompare(",
            "FirmwareRestoreService.TryRestore(", "HomeValleyAnalysis.TryEnqueue", "HomeValleyAnalysis.TryCancel", "RawFirmwareService.GrantRecovered(", "IntelService.GrantFromDataCore(",
        };

        private static readonly string[] JourneyFiles =
        {
            "FgjM5Common.cs", "FgjM5Journey.cs", "FgjM5Journey.Expedition.cs", "FgjM5Journey.Lab.cs", "FgjM5Journey.Late.cs", "FgjM5ReverseJourney.cs",
        };

        /// <summary>夹具：只能出现在这些方法体里，每种各若干处（DEBT 编号见 ADR-QA-022）。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("HomeInventory.Add(", "ApplyKeyFixture", 1, "监听阵列核 DEBT-FG5RND05-09"),
            ("HomeInventory.Add(", "ApplySubstrateFixture", 1, "芯片基板 DEBT-FG5E2E01-02"),
            ("RegionQuestItems = ", "ApplyDataboxFixture", 1, "带回的协议数据盒 DEBT-FG5E2E01-03"),
            ("DispatchRaidFromTerritory(", "ApplyRaidFixture", 1, "派出突袭 DEBT-FG5E2E01-04"),
            ("MachineRegistry.ApplyDamage(", "ApplyDeathFixture", 1, "家园机器阵亡 DEBT-FG5RND06-03"),
            ("BuildingOps.ApplyDamage(", "ApplyDestroyFixture", 1, "合成台被毁 DEBT-FG5E2E01-05"),
            ("ResearchService.CompleteForTests(", "ApplyLegacyResearchFixture", 1, "旧旅程已研究 DEBT-FG5RND01-05"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. M5 旅程源码只走输入通道，不直接调业务方法；七处夹具只在登记的夹具方法里");
            var hits = new List<string>();
            var src = new StringBuilder();
            var codeAll = new StringBuilder();
            foreach (string f in JourneyFiles)
            {
                string text = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/" + f);
                if (text == null)
                {
                    Fail("找不到旅程源码 " + f);
                    return;
                }
                src.Append(text);
                string code = StripComments(text);
                codeAll.Append(code);
                hits.AddRange(ForbiddenCalls.Where(code.Contains).Select(x => f + ":" + x));
            }
            Expect(hits.Count == 0, $"旅程源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            string all = codeAll.ToString();
            var bad = new List<string>();
            var seen = new List<string>();
            foreach (string token in Fixtures.Select(x => x.Token).Distinct())
            {
                int total = Count(all, token);
                int inside = 0;
                foreach ((string t, string method, int count, string debt) in Fixtures.Where(x => x.Token == token))
                {
                    string body = MethodBody(all, method);
                    int n = body == null ? 0 : Count(body, t);
                    inside += n;
                    seen.Add($"{debt}：{method} {n} 处");
                    if (n != count)
                    {
                        bad.Add($"{method} 里 {t} {n} 处（应 {count}）");
                    }
                }
                if (total != inside)
                {
                    bad.Add($"{token} 在夹具方法外还有 {total - inside} 处");
                }
            }
            Expect(bad.Count == 0, $"夹具只在登记的方法里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string raw = src.ToString();
            string[] inputs =
            {
                "FgjM3Common.PressBuild(", "GameActionId.OpenResearch", "GameActionId.OpenSignalCore", "GameActionId.TogglePause", "GameActionId.SpeedHalf", "GameActionId.SpeedTriple",
                "GameActionId.Cancel", "JourneyInput.ClickElement(", "JourneyInput.ClickUitk(", "JourneyInput.DragUitk(", "\"PrFusion\"", "\"ConfirmOk\"", "\"SignalPrint\"",
                "\"SignalEquip\"", "\"BeltPortClose\"", "\"BpRepair\"", "\"BpEnable\"", "\"StatsTabResearch\"", "\"PauseSaveQuit\"", "\"m_btn_Load\"",
            };
            List<string> missing = inputs.Where(x => !raw.Contains(x)).ToList();
            bool rts = raw.Contains("RightClickFcEnemy") || raw.Contains("TickFcClueReactions");
            Expect(missing.Count == 0 && rts,
                "旅程用到的正式输入：建造 / 研发树 / 信号核 / 暂停 / 倍速 / Esc 键，UI Toolkit 点击与拖动画布，合成台 / 确认框 / 刻印 / 装入 / 端口 / 重建 / 启用禁用 / 统计页签 / 存档 / 读取按钮；" +
                $"RTS：远征里左键选机器、右键敌人攻击（{rts}）{(missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing))}");
        }

        private static int Count(string text, string token)
        {
            int n = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        /// <summary>取 “void 方法名(” 的方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBody(string code, string method)
        {
            Match m = Regex.Match(code, @"void\s+" + Regex.Escape(method) + @"\s*\(");
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

        // ── A3. 旧旅程的“已研究”夹具 ─────────────────────────────────────────────────

        private static void CheckLegacyResearchFixture()
        {
            Line("  · A3. DEBT-FG5RND01-05：研发树开放前写成的旧旅程开局登记“已研究”进度夹具（研究本身由 FGJ-M5 / M5R 走正式入口）");
            var bad = new List<string>();
            foreach (string id in new[] { FgjM3Journey.Id, FgjM3ReverseJourney.Id, FgjM4Journey.Id, FgjM4ReverseJourney.Id })
            {
                JourneyDef d = JourneyCatalog.Create(id);
                int w = d?.Steps.FindIndex(s => s.Id == "workers") ?? -1;
                int f = d?.Steps.FindIndex(s => s.Id == "research_fixture") ?? -1;
                if (d == null || f < 0 || f != w + 1 || d.Steps[f].OnEnter == null)
                {
                    bad.Add($"{id}（workers 第 {w} 步、夹具第 {f} 步）");
                }
            }
            string common = StripComments(ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM5Common.cs") ?? string.Empty);
            string body = MethodBody(common, "ApplyLegacyResearchFixture") ?? string.Empty;
            bool scoped = body.Contains("ResearchService.MigratedNodes()") && body.Contains("CompleteForTests(");
            List<ResearchNodeDef> legacy = ResearchService.MigratedNodes();
            bool noKey = legacy.Count > 0 && legacy.All(n => n.IsReady && n.Unlocks.Length > 0 && n.Unlocks.All(u => u != "build:" + ResearchCatalog.LabTypeId));
            Expect(bad.Count == 0 && scoped && noKey,
                $"FGJ-M3 / M3R / M4 / M4R 在“记下开局两台机器”之后登记 research_fixture 一步（{(bad.Count == 0 ? "四条都在" : "缺：" + string.Join("、", bad))}）；" +
                $"夹具只记“研发树开放前就能建”的 {legacy.Count} 个节点（与旧档迁移同一口径，不含仿真实验室本身：{scoped && noKey}）");
        }

        // ── B. 规划不依赖固定坐标 ───────────────────────────────────────────────────

        private static void CheckPlannerSeeds()
        {
            Line("  · B. B25：FGJ-M5 的整套研发建筑规划在 5 个种子上都找得到，每一件都过放置校验（监听站只差研究）、互不重叠；FGJ-M5R 的在 2 个种子上找得到");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写世界）");
                return;
            }
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            try
            {
                ReloadContent();
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                var lines = new List<string>();
                var bad = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (int seed in new[] { 42, 1, 7, 9301, 2026 })
                {
                    CampaignState s = FgProductionSelfCheck.NewWorld(seed, true, 300);
                    var specs = new[]
                    {
                        new FgjM5Common.SiteSpec { Key = "lab", Type = FgjM5Common.Lab, MinR = 6 },
                        new FgjM5Common.SiteSpec { Key = "gallery", Type = FgjM5Common.Gallery, MinR = 6 },
                        new FgjM5Common.SiteSpec { Key = "synth", Type = FgjM5Common.Synth, MinR = 6 },
                        new FgjM5Common.SiteSpec { Key = "range", Type = FgjM5Common.Range, MinR = 8 },
                        new FgjM5Common.SiteSpec { Key = "post", Type = FgjM5Common.Post, MinR = 6 },
                    };
                    FgjM4Common.FactoryPlan plan = FgjM5Common.PlanSites(s, true, true, specs, 0, out string why);
                    if (plan == null)
                    {
                        bad.Add($"种子 {seed}：{why}");
                        continue;
                    }
                    string err = VerifyPlan(s, plan);
                    if (err != null)
                    {
                        bad.Add($"种子 {seed}：{err}");
                        continue;
                    }
                    lines.Add($"{seed}：{plan.Buildings.Count} 座、{plan.Routes.Sum(r => r.Path.Count)} 格带、发电机 2 ×{plan.Generators.Count}");
                }
                Expect(bad.Count == 0, $"FGJ-M5 研发建筑规划 5 个种子都成（{string.Join("；", lines)}；{sw.ElapsedMilliseconds} ms）{(bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad))}");
                var bad2 = new List<string>();
                var lines2 = new List<string>();
                foreach (int seed in new[] { 1, 42 })
                {
                    CampaignState s = FgProductionSelfCheck.NewWorld(seed, true, 300);
                    var specs = new[]
                    {
                        new FgjM5Common.SiteSpec { Key = "lab", Type = FgjM5Common.Lab, MinR = 6 },
                        new FgjM5Common.SiteSpec { Key = "synth", Type = FgjM5Common.Synth, MinR = 6 },
                    };
                    FgjM4Common.FactoryPlan plan = FgjM5Common.PlanSites(s, false, false, specs, 0, out string why, 1.0);
                    string err = plan == null ? why : VerifyPlan(s, plan);
                    if (err != null)
                    {
                        bad2.Add($"种子 {seed}：{err}");
                        continue;
                    }
                    lines2.Add($"{seed}：{string.Join("、", plan.Buildings.Select(b => b.Label + " " + FgjM4Common.Cell(b.Pivot)))}");
                }
                Expect(bad2.Count == 0, $"FGJ-M5R 实验室与合成台规划在 2 个种子上都成（{string.Join("；", lines2)}）{(bad2.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad2))}");
            }
            finally
            {
                CleanupWorld(originalSession, originalSlot, hadCamera, originalAutoPause);
            }
        }

        /// <summary>规划逐件复核：建筑过放置校验（不看造价；只差研究的算过），带每格过传送带校验，建筑占地 / 带 / 发电机互不重叠。</summary>
        private static string VerifyPlan(CampaignState s, FgjM4Common.FactoryPlan plan)
        {
            var seen = new Dictionary<long, string>();
            bool Claim(GridCell c, string who, out string clash)
            {
                long k = FgjM4Common.Key(c);
                if (seen.TryGetValue(k, out string other))
                {
                    clash = $"{FgjM4Common.Cell(c)} 被 {other} 与 {who} 同时占用";
                    return false;
                }
                seen[k] = who;
                clash = null;
                return true;
            }
            bool PlaceOk(string type, GridCell pivot, int rot)
            {
                GridPlacementResult r = HomeGridService.ValidatePlacement(s, type, pivot, rot, checkCost: false);
                return r.Ok || r.Reasons.All(x => x.Code == GridBlockReason.Locked);
            }
            foreach (FgjM4Common.Placed b in plan.Buildings)
            {
                if (!PlaceOk(b.Type, b.Pivot, b.Rot))
                {
                    return $"{b.Label} {FgjM4Common.Cell(b.Pivot)} 放不下";
                }
                foreach (GridCell c in FgjM4Common.Footprint(b.Type, b.Pivot, b.Rot))
                {
                    if (!Claim(c, b.Key, out string clash))
                    {
                        return clash;
                    }
                }
            }
            foreach (GridCell g in plan.Generators)
            {
                if (!PlaceOk(HomeValleyLayout.BuildingTypeGenerator2, g, 0))
                {
                    return $"发电机 2 {FgjM4Common.Cell(g)} 放不下";
                }
                foreach (GridCell c in FgjM4Common.Footprint(HomeValleyLayout.BuildingTypeGenerator2, g, 0))
                {
                    if (!Claim(c, "发电机2", out string clash))
                    {
                        return clash;
                    }
                }
            }
            foreach (FgjM4Common.Route r in plan.Routes)
            {
                foreach (GridCell c in r.Path)
                {
                    if (!HomeGridService.ValidateBeltCell(s, c).Ok)
                    {
                        return $"路线 {r.Key} 的 {FgjM4Common.Cell(c)} 不能放传送带";
                    }
                    if (!Claim(c, r.Key, out string clash))
                    {
                        return clash;
                    }
                }
            }
            return null;
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M5 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0, debtRows = 0, m5Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
                status[cols[0]] = st;
                if (raw.StartsWith("| FG-GAP-", StringComparison.Ordinal))
                {
                    gapRows++;
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 5)
                    {
                        m5Rows++;
                        if (!st.StartsWith("Closed", StringComparison.Ordinal) && !st.StartsWith("部分关闭", StringComparison.Ordinal))
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || FirstMilestoneOf(cols[6]) != 5)
                {
                    continue;
                }
                m5Rows++;
                if (!st.StartsWith("Closed", StringComparison.Ordinal) && !st.StartsWith("部分关闭", StringComparison.Ordinal) && !st.StartsWith("顺延", StringComparison.Ordinal))
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 90 && debtRows > 280 && m5Rows >= 15, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M5 的 {m5Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M5 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（待用户试玩的顺延写明理由）"
                    : $"FG-M5 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            // 修复轮（审查 P1）：旅程抓到的两处玩家看得到的问题同样登记（-06 通知弹出条盖住面板按钮 → FG6-DEF-06；-07 靶场短测试看不出核心固件差别 → FG15-BAL-01）。
            string[] mine = { "DEBT-FG5E2E01-01", "DEBT-FG5E2E01-02", "DEBT-FG5E2E01-03", "DEBT-FG5E2E01-04", "DEBT-FG5E2E01-05", "DEBT-FG5E2E01-06", "DEBT-FG5E2E01-07" };
            string[] closedHere =
            {
                "DEBT-FG5RND01-04", "DEBT-FG5RND01-05", "DEBT-FG5RND01-07", "DEBT-FG5RND01-08", "DEBT-FG5RND03-05", "DEBT-FG5RND04-05", "DEBT-FG5RND04-07",
                "DEBT-FG5RND05-06", "DEBT-FG5RND05-10", "DEBT-FG5RND06-04",
            };
            List<string> notClosed = closedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            bool registered = mine.All(status.ContainsKey);
            bool pending = status.TryGetValue("DEBT-FG5E2E01-01", out string p1) && p1.Contains("待用户");
            Expect(registered && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG5E2E01-01 待用户试玩、-02 芯片基板夹具、-03 协议数据盒夹具、-04 突袭派出夹具、-05 建筑被毁夹具、-06 通知弹出条盖住面板按钮、-07 靶场短测试看不出核心固件差别）；" +
                $"由本 Story 关闭的 {closedHere.Length} 条都是 Closed" + (notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed)));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M5 材料齐全（真人试玩由用户择时进行，DEBT-FG5E2E01-01，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M5-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M5-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M5-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M5-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool tasks = new[] { "解析台", "研究", "线索", "模拟熔合", "正式熔合", "靶场", "情报" }.All(script.Contains);
            bool questions = form.Contains("FG05") && table.Contains("FGJ-M5");
            bool registered = readme.Contains("FG-M5") && readme.Contains("DEBT-FG5E2E01-01") && readme.Contains("不阻塞");
            Expect(tasks && questions && registered,
                $"脚本按出口旅程七步给任务卡（{tasks}）；问卷对照 FG05、对照表对照 FGJ-M5（{questions}）；目录登记“待用户试玩、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FGT-RND-001～009 各有自检段且登记在全量自检，另有旅程步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            var map = new (string Test, string File, string Registered, string Journey, string Step)[]
            {
                ("FGT-RND-001", "FgResearchSelfCheck.cs", "FgResearchSelfCheck", FgjM5ReverseJourney.Id, "r5_continue"),
                ("FGT-RND-002", "FgResearchSelfCheck.cs", "FgResearchSelfCheck", FgjM5ReverseJourney.Id, "r1_prereq"),
                ("FGT-RND-003", "FgAnalysisBenchSelfCheck.cs", "FgAnalysisBenchSelfCheck", FgjM5Journey.Id, "ana_cracked"),
                ("FGT-RND-004", "FgTestRangeSelfCheck.cs", "FgTestRangeSelfCheck", FgjM5Journey.Id, "rng_end1"),
                ("FGT-RND-005", "FgFusionSelfCheck.cs", "FgFusionSelfCheck", FgjM5ReverseJourney.Id, "r3_done"),
                ("FGT-RND-006", "FgFusionSelfCheck.cs", "FgFusionSelfCheck", FgjM5Journey.Id, "burn_chip"),
                ("FGT-RND-007", "FgFusionSelfCheck.cs", "FgFusionSelfCheck", FgjM5Journey.Id, "clue"),
                ("FGT-RND-008", "FgIntelSelfCheck.cs", "FgIntelSelfCheck", FgjM5Journey.Id, "raid_arrive"),
                ("FGT-RND-009", "FgBlackBoxSelfCheck.cs", "FgBlackBoxSelfCheck", FgjM5Journey.Id, "bb_read"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered, string journey, string step) in map)
            {
                string src = ReadRepo(qa + file);
                bool journeyOk = JourneyCatalog.Create(journey)?.Steps.Any(s => s.Id == step) ?? false;
                if (src == null || !src.Contains(test) || !segments.Contains(registered) || !journeyOk)
                {
                    bad.Add($"{test}（{file} / {journey} {step}）");
                }
            }
            bool m5 = segments.Contains("FgMilestoneM5SelfCheck");
            Expect(bad.Count == 0 && m5,
                $"{map.Length} 项自动验收各有自检段且登记在全量自检、各有旅程步骤；[M5 出口] 本身登记在全量自检（{m5}）" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── F / G / H. 行为 ─────────────────────────────────────────────────────────

        private static void CheckBehaviour()
        {
            Line("  · F. 技术数据收支按来源分项与统计面板“研发”页；G. 新档技术数据；H. 研发加成来源行");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return;
            }
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            GameLanguage originalLanguage = GameSettings.Language;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-m5exit-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ReloadContent();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                NotificationCenter.AutoPauseHandler = null;
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                GameRoot.BindWorldProviders();
                CheckTechFlow();
                CheckStartTech();
                CheckBonusLines();
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                CleanupWorld(originalSession, originalSlot, hadCamera, originalAutoPause);
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
        }

        private static void Commit(CampaignState s, string tx, long amount, bool produce)
        {
            if (produce)
            {
                CampaignEconomyLedger.ProposeProduce(s, tx, "m5exit", CampaignEconomyLedger.ResourceTechData, amount);
            }
            else
            {
                CampaignEconomyLedger.ProposeConsume(s, tx, "m5exit", CampaignEconomyLedger.ResourceTechData, amount);
            }
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            CampaignEconomyLedger.Commit(s, tx);
        }

        private static string FlowDigest(CampaignState s) =>
            string.Join(";", (s.Research?.TechFlow ?? Array.Empty<TechFlowRecord>()).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}+{r.Income}-{r.Expense}"))
            + $"|start={s.Research?.TechStart}|int={IntelService.StateOf(s)?.Interruptions}|tech={s.TechData}";

        private static void CheckTechFlow()
        {
            // F1 归类：账本事务 ID → 来源 / 去向（与各系统的事务前后缀同一口径）。
            var cases = new (string Tx, string Want)[]
            {
                ("analysis_q3:techdata", TechDataFlow.Analysis),
                ("analysis-wreck:w12", TechDataFlow.Wreck),
                (BlackBoxService.InstanceIdOf(7) + ":p20", TechDataFlow.BlackBox),
                ("firmware_restore_tx_fw_coolant_1", TechDataFlow.Restore),
                ("blueprint_reaction_charge_tx_erc_003_4", TechDataFlow.Blueprint),
                ("journey_fixture_techdata_42", TechDataFlow.Other),
            };
            List<string> wrong = cases.Where(x => TechDataFlow.ClassifyTransaction(x.Tx) != x.Want).Select(x => $"{x.Tx} → {TechDataFlow.ClassifyTransaction(x.Tx)}（应 {x.Want}）").ToList();
            Expect(wrong.Count == 0, $"F1 技术数据事务按来源归类：解析台首次解析 / 解析台处理残骸 / 黑匣子 / 数据复原 / 蓝图反应费 / 其它{(wrong.Count == 0 ? string.Empty : "；错：" + string.Join("；", wrong))}");

            // F2 经账本提交即记账（收入 / 支出各自累计），实验室 / 熔合的取用走 Spend / Unspend。
            CampaignState s = FgProductionSelfCheck.NewWorld(5803, true, 300);
            CampaignFgStateDomains.EnsureAll(s);
            long t0 = s.TechData;
            Commit(s, "analysis_q1:techdata", 12, true);
            Commit(s, "analysis-wreck:a", 1, true);
            Commit(s, BlackBoxService.InstanceIdOf(3) + ":p20", 20, true);
            Commit(s, "firmware_restore_tx_x_1", 4, false);
            Commit(s, "blueprint_reaction_charge_tx_y_1", 2, false);
            TechDataFlow.Spend(s, TechDataFlow.Lab, 3);
            TechDataFlow.Spend(s, TechDataFlow.FusionSim, 5);
            TechDataFlow.Spend(s, TechDataFlow.Fusion, 30);
            TechDataFlow.Unspend(s, TechDataFlow.Fusion, 30);
            bool sums = TechDataFlow.IncomeOf(s, TechDataFlow.Analysis) == 12 && TechDataFlow.IncomeOf(s, TechDataFlow.Wreck) == 1 && TechDataFlow.IncomeOf(s, TechDataFlow.BlackBox) == 20
                        && TechDataFlow.ExpenseOf(s, TechDataFlow.Restore) == 4 && TechDataFlow.ExpenseOf(s, TechDataFlow.Blueprint) == 2 && TechDataFlow.ExpenseOf(s, TechDataFlow.Lab) == 3
                        && TechDataFlow.ExpenseOf(s, TechDataFlow.FusionSim) == 5 && TechDataFlow.ExpenseOf(s, TechDataFlow.Fusion) == 0
                        && TechDataFlow.TotalIncome(s) == 33 && TechDataFlow.TotalExpense(s) == 14 && s.TechData == t0 + 33 - 6;
            Expect(sums, $"F2 账本提交即按来源记收支（收入 33 = 解析 12 + 残骸 1 + 黑匣子 20；支出 14 = 复原 4 + 蓝图 2 + 实验室 3 + 模拟 5，正式熔合中止退回后为 0）；技术数据 {t0} → {s.TechData}：{FlowDigest(s)}");

            // F3 统计面板“研发”页：文本按存档里的累计写，每个来源 / 去向一行。
            s.Research.TechStart = 370;
            IntelState intel = IntelService.StateOf(s);
            if (intel != null)
            {
                intel.Interruptions = 2;
            }
            var lines = new List<(string Text, string Cls)>();
            ResearchStats.Build(s, lines);
            string Inc(string src, long v) => GameText.Format("stats.research.income", GameText.Get("stats.research.src." + src), v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string Exp(string sink, long v) => GameText.Format("stats.research.expense", GameText.Get("stats.research.sink." + sink), v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            bool rows = lines.Any(l => l.Text == Inc(TechDataFlow.BlackBox, 20)) && lines.Any(l => l.Text == Inc(TechDataFlow.Analysis, 12)) && lines.Any(l => l.Text == Exp(TechDataFlow.Restore, 4))
                        && lines.Any(l => l.Text == Exp(TechDataFlow.Lab, 3)) && lines.Count(l => l.Cls == "st-row-title") == 5
                        && lines.Any(l => l.Text.Contains("370")) && TechDataFlow.IncomeSources.Length + TechDataFlow.ExpenseSinks.Length == 10;
            Expect(rows, $"F3 统计面板“研发”页 {lines.Count} 行：技术数据（库存 / 开局带来 / 收入 / 支出）+ 4 个来源 + 6 个去向、研究、熔合、情报、黑匣子五段；例：“{lines.FirstOrDefault(l => l.Text == Inc(TechDataFlow.BlackBox, 20)).Text}”");

            // F4 真文件存读档：收支分项、开局带来、破译中断次数逐项一致。
            string before = FlowDigest(s);
            const int slot = 7;
            SaveResult save = CampaignSaveService.Save(slot, s, SaveReason.Manual);
            LoadResult load = CampaignSaveService.Load(slot);
            string after = load.Success ? FlowDigest(load.State) : "读档失败：" + load.Reason;
            Expect(save.Success && after == before, $"F4 真文件存读档：技术数据收支分项、开局带来、破译中断次数写进存档，读档后逐项一致（{before}）" + (after == before ? string.Empty : $"\n读档后：{after}"));
        }

        private static void CheckStartTech()
        {
            int start = ResearchService.StartTechData;
            int cost = ResearchService.LegacyOpenTechCost();
            CampaignState s = FgProductionSelfCheck.NewWorld(5811, true, 300);
            CampaignFgStateDomains.EnsureAll(s);
            long t0 = s.TechData;
            int start0 = s.Research.TechStart;
            ResearchService.ApplyNewGameStart(s);
            string menu = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/MainMenuUI/MainMenuUI.cs") ?? string.Empty;
            string create = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/CampaignState.cs") ?? string.Empty;
            bool wiring = menu.Contains("ResearchService.ApplyNewGameStart(state)") && !create.Contains("ApplyNewGameStart");
            Expect(start >= cost && start0 == 0 && s.TechData == t0 + start && s.Research.TechStart == start && wiring,
                $"G 新档开局技术数据 {start}（research.start_tech_data）≥ 研发树开放前开局可造的 {ResearchService.LegacyOpenNodes().Count} 个节点 {ResearchService.LegacyOpenPoints()} 研究点要的 {cost}；" +
                $"只在主菜单“新建”发一次（自检用的空白战役开局 0，{wiring}），记在存档“开局带来”（统计面板对账）");
        }

        private static void CheckBonusLines()
        {
            CampaignState s = FgProductionSelfCheck.NewWorld(5821, true, 300);
            CampaignFgStateDomains.EnsureAll(s);
            CampaignSession.Set(0, s);
            string recycler0 = ResearchService.BonusLine(s, "recycler");
            string lab0 = ResearchService.BonusLine(s, FgjM5Common.Lab);
            string wh0 = ResearchService.BonusLine(s, HomeValleyLayout.BuildingTypeWarehouse);
            ResearchService.CompleteForTests(s, FgjM5Common.NodeGather, "industry.eff_lab_1", "logistics.storage_cap_1");
            string recycler = ResearchService.BonusLine(s, "recycler") ?? string.Empty;
            string lab = ResearchService.BonusLine(s, FgjM5Common.Lab) ?? string.Empty;
            string wh = ResearchService.BonusLine(s, HomeValleyLayout.BuildingTypeWarehouse) ?? string.Empty;
            string nGather = ResearchCatalog.Find(FgjM5Common.NodeGather)?.Name ?? "?";
            string nLab = ResearchCatalog.Find("industry.eff_lab_1")?.Name ?? "?";
            string nCap = ResearchCatalog.Find("logistics.storage_cap_1")?.Name ?? "?";
            ItemDef solid = ItemCatalog.Find(FgjM5Common.Substrate);
            string item = solid != null ? ResearchService.ItemBonusLine(s, solid) ?? string.Empty : string.Empty;
            bool ok = recycler0 == null && lab0 == null && wh0 == null
                      && recycler.Contains("10%") && recycler.Contains(nGather) && Math.Abs(ResearchService.SpeedFactor(s, "recycler") - 1.1) < 1e-6
                      && lab.Contains("10%") && lab.Contains(nLab) && wh.Contains("15%") && wh.Contains(nCap) && item.Contains("15%");
            Expect(ok, $"H 研发加成来源行：没研究时不写；研究后回收站“{recycler}”、仿真实验室“{lab}”、仓库“{wh}”、物资悬停（芯片基板）“{item}”——与真实生效的倍率同一读口");
        }

        // ── I. 界面 ────────────────────────────────────────────────────────────────

        private const string StatsUxml = "Assets/GameRes/Raw/UI/UiKit/StatsPanel.uxml";
        private const string ProductionUxml = "Assets/GameRes/Raw/UI/UiKit/ProductionPanel.uxml";
        private const string ResearchUxml = "Assets/GameRes/Raw/UI/UiKit/ResearchTreePanel.uxml";

        private static void CheckUi()
        {
            Line("  · I. 界面：统计面板“研发”页签与建筑面板来源行接线、研发树窗口固定高度、布局探针");
            string stats = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/StatsPanelUIToolkit.cs") ?? string.Empty;
            string prod = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/ProductionPanelUIToolkit.cs") ?? string.Empty;
            string items = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/ItemsPanelUIToolkit.cs") ?? string.Empty;
            string statsUxml = ReadRepo("TEngine/UnityProject/" + StatsUxml) ?? string.Empty;
            string prodUxml = ReadRepo("TEngine/UnityProject/" + ProductionUxml) ?? string.Empty;
            bool wiring = statsUxml.Contains("name=\"StatsTabResearch\"") && stats.Contains("\"StatsTabResearch\"") && stats.Contains("ResearchStats.Build(")
                          && prodUxml.Contains("name=\"BpResearchLine\"") && prod.Contains("ResearchService.BonusLine(") && items.Contains("ResearchLine(");
            Expect(wiring, "统计面板有“研发”页签（StatsTabResearch → ResearchStats）；建筑面板有研发加成行（BpResearchLine → ResearchService.BonusLine）；物资悬停写研发加成（ItemHover.ResearchLine）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过布局探针");
                return;
            }
            CheckResearchWindowStable();
            foreach ((string uxml, string root) in new[] { (StatsUxml, "StatsPanelRoot"), (ProductionUxml, "ProductionPanelRoot") })
            {
                string probe = UiToolkitLayoutProbe.Probe(uxml, root);
                Expect(probe.StartsWith("PASS", StringComparison.Ordinal), $"{Path.GetFileName(uxml)} 布局探针（四种分辨率 + 超长文字压测 + USS 体检）：" + probe.Split('\n').FirstOrDefault()
                    + (probe.StartsWith("PASS", StringComparison.Ordinal) ? string.Empty : "\n" + string.Join("\n", probe.Split('\n').Skip(1).Take(12))));
            }
        }

        /// <summary>
        /// FGJ-M5R 抓到并修掉：研发树窗口原来 height: auto，悬停节点时右侧详情文字变长 → 窗口变高、在居中的遮罩里上下挪 → 整棵树换位置、光标下变成别的节点。
        /// 这里把详情写短 / 写很长各排一次版，画布视口与窗口的位置、尺寸必须完全不变（两种分辨率）。
        /// </summary>
        private static void CheckResearchWindowStable()
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ResearchUxml);
            var src = AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath);
            if (vta == null || src == null)
            {
                Fail("研发树 UXML 或 PanelSettings 加载失败");
                return;
            }
            var bad = new List<string>();
            var seen = new List<string>();
            string longText = string.Join("\n", Enumerable.Range(0, 24).Select(i => $"第 {i + 1} 行：解锁监听站（占地 3×3，耗电 10）；需要关键材料监听阵列核 ×1——主线首领给出。"));
            foreach (Vector2Int res in new[] { new Vector2Int(1920, 1080), new Vector2Int(1280, 720) })
            {
                PanelSettings settings = Object.Instantiate(src);
                settings.hideFlags = HideFlags.HideAndDontSave;
                settings.scale = 1f;
                var rt = new RenderTexture(res.x, res.y, 0) { hideFlags = HideFlags.HideAndDontSave };
                settings.targetTexture = rt;
                var go = new GameObject("__M5ResearchProbe") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    var doc = go.AddComponent<UIDocument>();
                    doc.panelSettings = settings;
                    doc.visualTreeAsset = vta;
                    VisualElement root = doc.rootVisualElement;
                    root.Q<VisualElement>("ResearchRoot")?.RemoveFromClassList("uk-hidden");
                    Label body = root.Q<Label>("ResearchDetailBody");
                    VisualElement viewport = root.Q<VisualElement>("ResearchViewport");
                    VisualElement window = root.Q<VisualElement>("ResearchWindow");
                    body.text = "解锁监听站。";
                    UiToolkitLayoutProbe.ForceLayout(root);
                    Rect v0 = viewport.worldBound;
                    Rect w0 = window.worldBound;
                    body.text = longText;
                    UiToolkitLayoutProbe.ForceLayout(root);
                    Rect v1 = viewport.worldBound;
                    Rect w1 = window.worldBound;
                    if (v0 != v1 || w0 != w1)
                    {
                        bad.Add($"{res.x}x{res.y} 详情变长后视口 {v0} → {v1}、窗口 {w0} → {w1}");
                    }
                    seen.Add($"{res.x}x{res.y} 窗口 {w0.height:F0}px 高、视口 {v0.height:F0}px");
                }
                finally
                {
                    Object.DestroyImmediate(go);
                    Object.DestroyImmediate(settings);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            Expect(bad.Count == 0, $"研发树窗口固定高度：详情一行 / 24 行时窗口与画布视口的位置、尺寸都不变（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        private static void ReloadContent()
        {
            ConfigSystem.Instance.Load();
            GameText.Reload();
            GridContent.Reload();
            WorldGenContent.Reload();
            FgContentTables.Reload();
            ItemCatalog.Reload();
            ProducerCatalog.Reload();
            BuildingOps.Reload();
            ResearchCatalog.Reload();
            FusionCatalog.Reload();
            IntelCatalog.Reload();
            GameClock.ReloadTuning();
        }

        private static void CleanupWorld(CampaignState originalSession, int originalSlot, bool hadCamera, Func<bool> originalAutoPause)
        {
            try
            {
                WorldSimulation.UnloadAll();
            }
            catch (Exception e)
            {
                Fail("收尾 UnloadAll 抛异常：" + e.Message);
            }
            GameClock.ResetSession();
            NotificationCenter.AutoPauseHandler = originalAutoPause;
            InputRouter.DebugSetReader(null);
            InputRouter.Reset();
            ProductionService.ResetForTests();
            ResearchService.ResetForTests();
            BuildingOps.ResetForTests();
            HomeGridService.Invalidate();
            BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
            MachineRegistry.ResetForNewCampaign();
            HomeValleyWorkOrders.ResetSessionState();
            if (!hadCamera && Camera.main != null)
            {
                Object.DestroyImmediate(Camera.main.gameObject);
            }
            if (originalSession != null)
            {
                CampaignSession.Set(originalSlot, originalSession);
            }
            else
            {
                CampaignSession.Clear();
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "production", "design", "full-game")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "TEngine", "UnityProject", "DesignDocs")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static string ReadRepo(string relative)
        {
            string root = RepoRoot();
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
            Debug.LogWarning("[M5 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
