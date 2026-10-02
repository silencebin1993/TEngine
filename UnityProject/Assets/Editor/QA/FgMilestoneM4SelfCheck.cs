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
using GameLogic.Campaign.Logistics;
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
    /// FG4-E2E-01 自检 [M4 出口]：里程碑出口的自动化部分里，能在编辑模式下逐语义断言的那些（旅程本身在 Play 里由 tools/unity-journey.sh 跑）。
    ///
    /// A. 旅程登记：FGJ-M4 覆盖出口旅程原文每一步（从原料到机器 / “零件保持 50 个”规则自动排产 / 酸液堵塞接废液池恢复 / 远征看离家报告 / 清空机器看应急打印），
    ///    以及 DEBT-FG3E2E01-01 要回补的“真实配方产线上的堵塞诊断、布局库、撤销 / 重做、升级、离家后台核对”；FGJ-M4R 覆盖 IC-REQ-022 六类（每类出错 → 恢复两端）；
    ///    种子不同；两条旅程都登记了断点（DEBT-FGTOOL01-01）。
    /// A2. 旅程源码只走输入通道：不直接调放置 / 施工 / 配方 / 规则 / 装配 / 信号核 / 出征 / 存档等业务方法；两处夹具（清空机器的伤害夹具、关键材料的进度夹具）只在登记的夹具方法里。
    /// B. 规划不依赖固定坐标（B25）：FGJ-M4 的整套产线规划在 5 个种子上都找得到、每一件都通过放置校验且互不重叠；FGJ-M4R 的小产线与超控阵列、FGJ-M4 的精炼塔规划在 2 个种子上找得到。
    /// C. 缺口清零（里程碑出口第 5 条）：最迟里程碑是 M4 的缺口全部 Closed；首个门禁是 FG-M4 的延后项全部 Closed / 部分关闭 / 顺延；本 Story 新登记的在表里。
    /// D. 试玩包：FG-M4 的试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板齐全（M4 不是必试玩里程碑，作为可选试玩交付、不阻塞）。
    /// E. 需求覆盖（IC-REQ-020）：FG04 FGT-ECO-001～010 每一项都有自检段且登记在全量自检，五项另有旅程步骤。
    /// F. 装配站面板布局（旧旅程回归抓到并修掉）：材料 / 代付 / 缓存几行加长后，生产按钮不被压扁、不被提示文字盖住（四种分辨率）；布局探针通过。
    /// G. DEBT-FG4ECO11-06：工单面板的施工单原因与施工队列同一口径（行为由 [超控阵列] E8b 断言；这里守住接线）。
    /// H. “为什么不工作”的缺料链穿过分流器找到上游（FGJ-M4 诊断抓到并修掉：经分流器供料的建筑原来被写成“输入带上没有能供应的建筑”）；
    ///    H2：输入带中途一格转反（带被截成两段）时点名那一格，不写“没有供应者”，转回来后恢复生产。
    /// </summary>
    public static class FgMilestoneM4SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/M4 出口")]
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
            Line("\n[M4 出口] FG4-E2E-01：旅程登记与正式输入、规划不依赖固定坐标、缺口清零、试玩包、需求覆盖、装配站面板布局、工单面板原因接线");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckPlannerSeeds);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckFactoryPanelLayout);
            Step(CheckWorkOrderReasonWiring);
            Step(CheckDiagnosisThroughSplitter);
            Line($"  · [M4 出口] 断言通过 {_pass}，失败 {_fail}");
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
            Line("  · A. 旅程登记：FGJ-M4 覆盖出口旅程原文每一步与 DEBT-FG3E2E01-01 的回补；FGJ-M4R 覆盖 IC-REQ-022 六类；种子不同；两条都登记断点");
            JourneyDef m4 = JourneyCatalog.Create(FgjM4Journey.Id);
            JourneyDef m4r = JourneyCatalog.Create(FgjM4ReverseJourney.Id);
            var exit = new (string Step, string[] Ids)[]
            {
                ("从原料一路生产出一台新机器", new[] { "plan", "eco_place1", "eco_built1", "eco_place", "eco_built", "line_run", "asm_sub", "asm_produce", "machine" }),
                ("设置“零件保持 50 个”的规则", new[] { "rules_open", "rule_new", "rule_factory", "rule_enable" }),
                ("看它自动排产", new[] { "rule_fires", "rule_panel_read", "rule_done" }),
                ("精炼塔的酸液堵住", new[] { "ref_place", "ref_built", "acid_jam", "tower_read" }),
                ("接上废液池恢复", new[] { "pond_place", "pond_ok" }),
                ("远征一次", new[] { "prep_depart", "away", "evac_confirm" }),
                ("回来看离家报告", new[] { "report", "report_click" }),
                ("清空所有机器", new[] { "wipe_ghost", "wipe" }),
                ("看应急打印把家园救回来", new[] { "print", "rescued" }),
                ("DEBT-FG3E2E01-01 真实配方产线上的堵塞诊断", new[] { "jam", "jam_stall", "diag_open", "diag_click", "fix", "flow_back" }),
                ("DEBT-FG3E2E01-01 布局库放一份、撤销 / 重做", new[] { "copy_box", "lib_save", "site2", "lib_place", "paste_click", "undo", "redo" }),
                ("DEBT-FG3E2E01-01 升级与离家后台核对", new[] { "up_boxes", "up_wait", "rate", "bg_check" }),
            };
            List<string> missing = exit.Where(x => m4 == null || x.Ids.Any(id => m4.Steps.All(s => s.Id != id))).Select(x => x.Step).ToList();
            Expect(Wellformed(m4) && missing.Count == 0 && m4.Seed == FgjM4Journey.TestSeed,
                $"FGJ-M4：{m4?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m4?.Seed}；出口旅程各步与回补都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string Kind, string[] Ids)[]
            {
                ("路径失败（放错位置）", new[] { "r1_pick", "r1_bad", "m2_place" }),
                ("资源不足（装配站缺材料）", new[] { "r2_sub_off", "r2_produce", "r2_sub_on", "r2_done" }),
                ("暂停与倍速", new[] { "r3_pause", "r3_frozen", "r3_resume", "r3_half", "r3_triple" }),
                ("目标死亡（拆掉工作中的下游）", new[] { "r4_click", "r4_gone", "r4_undo", "r4_back" }),
                ("断链（超控阵列断电）", new[] { "r5_built", "r5_slot", "r5_ask", "r5_cut", "r5_undo", "r5_back" }),
                ("存读档", new[] { "r6_quit", "r6_menu", "r6_load", "r6_loaded" }),
            };
            List<string> missingR = reverse.Where(x => m4r == null || x.Ids.Any(id => m4r.Steps.All(s => s.Id != id))).Select(x => x.Kind).ToList();
            Expect(Wellformed(m4r) && missingR.Count == 0 && m4r.Seed != m4.Seed,
                $"FGJ-M4R：{m4r?.Steps.Count} 步；六类反向场景齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}；种子 {m4r?.Seed} ≠ {m4?.Seed}（B25）");
            bool ck = m4 != null && m4r != null && m4.CheckpointAfter.Length >= 2 && m4r.CheckpointAfter.Length >= 1
                      && m4.CheckpointAfter.All(id => m4.Steps.Any(s => s.Id == id)) && m4r.CheckpointAfter.All(id => m4r.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM4Journey.Id) && JourneyCatalog.Ids.Contains(FgjM4ReverseJourney.Id) && JourneyCatalog.Ids.Contains(FgjM3Journey.Id),
                $"两条 M4 旅程登记在旅程登记表、前序旅程仍在；断点（DEBT-FGTOOL01-01）：FGJ-M4 [{string.Join(", ", m4?.CheckpointAfter ?? Array.Empty<string>())}]、FGJ-M4R [{string.Join(", ", m4r?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
            // FGJ-M4 续跑抓到（DEBT-FGTOOL01-01）：步骤作用域的变量（s<步序>.<尝试>.<名字>）不随断点恢复——续跑的步序与完整跑不同，
            // 恢复了会让同一序号的新步骤以为“已经点过”、一直等到超时重试。旅程自己的变量（规划结果、建筑位置）照常恢复。
            bool scoped = !JourneyCheckpoints.IsRestoredVar("s43.0.panel.click") && !JourneyCheckpoints.IsRestoredVar("s45.1.close.at")
                          && JourneyCheckpoints.IsRestoredVar("pwS.type") && JourneyCheckpoints.IsRestoredVar("plan.origin") && !JourneyCheckpoints.IsRestoredVar("saves");
            Expect(scoped, "断点续跑恢复旅程变量时跳过步骤作用域的键（s<步序>.<尝试>.*）与本次运行的存档目录，规划结果等旅程变量照常恢复");
        }

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M3 的清单 + M4 的配方 / 规则 / 装配 / 信号核 / 建筑开关 / 报告）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Redo(", "PlanHistory.Paste(", "PlanHistory.Upgrade(", "PlanHistory.Place(", "PlanHistory.ToggleDemolish(", "PlanHistory.Relocate(",
            "PlanHistory.PlaceBeltPath(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "HomeGridService.TryToggleDemolish", "HomeGridService.TryUpgrade",
            "BeltNetworkService.TryPlace", "BeltNetworkService.TrySet", "BeltNetworkService.TryRemove", "BeltNetworkService.TryReverse", "PipeNetworkService.TryPlace",
            "PipeNetworkService.TryRemove", "PipeNetworkService.TrySet", "BeltPortService.TrySetFilter", "LayoutLibrary.TrySave", ".StartPaste(", "RootCauseDiagnosis.Locate",
            "HomeValleyWorkOrders.TryAssign", "HomeValleyWorkOrders.TryCreate", "ExpeditionDepartureService.TryDepart", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(",
            "UiConfirmDialog.Cancel(", ".SetTerrain(", ".SetPollution(", "ProductionService.TrySet", "ProductionService.CopySettings", "StandingRuleService.Try",
            "RulesPanelUIToolkit.PickForTests", ".ToggleRow(", "HomeValleyFactory.TryEnqueue", "AssemblyMaterials.SetScrapSubstitute", ".SetScrapSubstitute(",
            "SignalCoreService.Try", "BuildingOps.Try", "AwayReportPanelUIToolkit.OpenReport(", "AwayReportPanelUIToolkit.PickForTests", "ProductionPanelUIToolkit.Open(",
            "ExpeditionReturnService.TryConfirm", "GameClock.SetPaused(", "GameClock.SetSpeed(", "SoftlockService.Step(", "HomeValleyPowerGrid.TrySet",
            "OverrideArrayService.Try", "PrimitiveInventory.TryMove",
        };

        private static readonly string[] JourneyFiles = { "FgjM4Common.cs", "FgjM4Journey.cs", "FgjM4Journey.Phases.cs", "FgjM4Journey.Later.cs", "FgjM4ReverseJourney.cs" };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. M4 旅程源码只走输入通道，不直接调业务方法；夹具只在登记的夹具方法里");
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
            // 夹具：伤害夹具只在 ApplyWipeFixture（家园还没有正式突袭），关键材料只在 ApplyKeyFixture（首领在 FG-M9 以后），各一处。
            string code2 = codeAll.ToString();
            int damage = Regex.Matches(code2, @"MachineRegistry\.ApplyDamage\(").Count;
            int give = Regex.Matches(code2, @"HomeInventory\.Add\(").Count;
            bool wipeIn = Regex.IsMatch(code2, @"void ApplyWipeFixture\([^)]*\)\s*\{[^}]*?(?:\{[^}]*\}[^}]*?)*MachineRegistry\.ApplyDamage\(", RegexOptions.Singleline);
            bool keyIn = Regex.IsMatch(code2, @"void ApplyKeyFixture\([^)]*\)\s*\{[^}]*HomeInventory\.Add\(", RegexOptions.Singleline);
            Expect(damage == 1 && give == 1 && wipeIn && keyIn,
                $"夹具只有两处：伤害夹具 {damage} 处（在 ApplyWipeFixture：{wipeIn}，DEBT-FG4E2E01-01）、关键材料进度夹具 {give} 处（在 ApplyKeyFixture：{keyIn}，DEBT-FG4E2E01-02）");
            string all = src.ToString();
            string[] inputs =
            {
                "FgjM3Common.PressBuild(", "GameActionId.Rotate", "GameActionId.Copy", "GameActionId.LayoutLibrary", "GameActionId.Undo", "GameActionId.Redo",
                "GameActionId.UpgradePlan", "GameActionId.OpenDiagnosis", "GameActionId.OpenRules", "GameActionId.OpenSignalCore", "GameActionId.DemolishMode",
                "GameActionId.TogglePause", "GameActionId.SpeedHalf", "GameActionId.SpeedTriple", "GameActionId.Interact", "JourneyInput.ClickElement(", "JourneyInput.ClickUitk(",
                "JourneyInput.ScrollIntoView(", "\"ProduceBtn_Hauler\"", "\"SubstituteToggle\"", "\"RrToggle\"", "\"RulesPanelClose\"", "\"ProductionPanelClose\"",
                "\"SignalSlot2\"", "\"SignalEquip\"", "\"SignalPrint\"", "\"ConfirmOk\"", "\"DepartButton\"", "\"LlPlace\"", "\"LayoutLibrarySave\"", "\"DgStep\"",
            };
            List<string> missing = inputs.Where(x => !all.Contains(x)).ToList();
            bool rts = Regex.IsMatch(all, @"TryClick\([^;]*,\s*1\)") && all.Contains("ClickMachine(");
            Expect(missing.Count == 0 && rts,
                $"旅程用到的正式输入：建造 / 旋转 / 复制 / 布局库 / 撤销 / 重做 / 升级 / 诊断 / 规则 / 信号核 / 拆除 / 暂停 / 倍速 / 交互键、UI Toolkit 点击与滚轮、装配站 / 规则 / 信号核 / 确认框 / 出发 / 布局库 / 诊断按钮；" +
                $"RTS：左键选机器、右键地面{(missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing))}");
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

        // ── B. 规划不依赖固定坐标 ───────────────────────────────────────────────────

        private static void CheckPlannerSeeds()
        {
            Line("  · B. B25：FGJ-M4 的整套产线规划在 5 个种子上都找得到，每一件都过放置校验、互不重叠；FGJ-M4R 的小产线与 FGJ-M4 的精炼塔规划在 2 个种子上找得到");
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
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                var lines = new List<string>();
                var bad = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (int seed in new[] { 42, 1, 7, 9301, 2026 })
                {
                    CampaignState s = FgProductionSelfCheck.NewWorld(seed, true, 300);
                    FgjM4Common.FactoryPlan plan = FgjM4Common.PlanAll(s, out List<GridCell> poles, out string why);
                    if (plan == null)
                    {
                        bad.Add($"种子 {seed}：{why}");
                        continue;
                    }
                    string err = VerifyPlan(s, plan, poles);
                    if (err != null)
                    {
                        bad.Add($"种子 {seed}：{err}");
                        continue;
                    }
                    GridCell core = HomeGridService.CorePivot(s);
                    lines.Add($"{seed}：块原点相对核心 {plan.Origin.X - core.X},{plan.Origin.Y - core.Y}、{plan.Routes.Sum(r => r.Path.Count)} 格带、电塔 {poles.Count}、发电机 2 ×{plan.Generators.Count}");
                }
                Expect(bad.Count == 0, $"整套产线规划 5 个种子都成（{string.Join("；", lines)}；{sw.ElapsedMilliseconds} ms）{(bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad))}");
                var bad2 = new List<string>();
                var lines2 = new List<string>();
                foreach (int seed in new[] { 42, 1 })
                {
                    CampaignState s = FgProductionSelfCheck.NewWorld(seed, true, 300);
                    FgjM4Common.MiniPlan mp = FgjM4Common.PlanMini(s, out string why);
                    FgjM4Common.RefineryPlan rp = FgjM4Common.PlanRefinery(s, null, out string why2);
                    if (mp == null || rp == null)
                    {
                        bad2.Add($"种子 {seed}：小产线 {why ?? "成"}、精炼塔 {why2 ?? "成"}");
                        continue;
                    }
                    GridCell core = HomeGridService.CorePivot(s);
                    float coreR = HomeValleyPowerGrid.CoverRadiusOf(HomeValleyLayout.BuildingTypeCore);
                    bool outside = FgjM4Common.NearestDist(GameLogic.Campaign.Signal.OverrideArrayService.TypeId, mp.Array, 0, core) > coreR;
                    bool poleLinks = Vector2.Distance(new Vector2(mp.Pole.X, mp.Pole.Y), new Vector2(core.X, core.Y)) <= coreR;
                    bool acidPond = FgjM4Common.FluidPortOutside(FgjM4Common.Pond, rp.Pond, 0, FgjM4Common.Pond + ".fin0", out GridCell pin, out _) && pin == rp.AcidPipe;
                    if (!outside || !poleLinks || !acidPond)
                    {
                        bad2.Add($"种子 {seed}：阵列在配电范围外 {outside}、电塔接得上核心 {poleLinks}、酸液格就是废液池入口 {acidPond}");
                        continue;
                    }
                    lines2.Add($"{seed}：阵列只由电塔 {FgjM4Common.Cell(mp.Pole)} 供电、油井 {FgjM4Common.Cell(rp.Pump)} → 精炼塔 {FgjM4Common.Cell(rp.Tower)}（原油管线 {rp.Crude.Count} 格）");
                }
                Expect(bad2.Count == 0, $"反向旅程小产线 + 超控阵列、精炼塔规划在 2 个种子上都成（{string.Join("；", lines2)}）{(bad2.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad2))}");
            }
            finally
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
        }

        /// <summary>规划逐件复核：每座建筑过放置校验（不看造价），每格带过传送带放置校验，建筑占地 / 带 / 分流器 / 电塔互不重叠。</summary>
        private static string VerifyPlan(CampaignState s, FgjM4Common.FactoryPlan plan, List<GridCell> poles)
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
            foreach (FgjM4Common.Placed b in plan.Buildings)
            {
                if (!HomeGridService.ValidatePlacement(s, b.Type, b.Pivot, b.Rot, checkCost: false).Ok)
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
                if (!HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeGenerator2, g, 0, checkCost: false).Ok)
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
            foreach (GridCell p in poles)
            {
                if (!Claim(p, "电塔", out string clash) || !HomeGridService.ValidatePlacement(s, FgjM4Common.PoleT2, p, 0, checkCost: false).Ok)
                {
                    return clash ?? $"电塔 {FgjM4Common.Cell(p)} 放不下";
                }
            }
            foreach (var sp in plan.Splitters)
            {
                if (!Claim(sp.cell, sp.key, out string clash))
                {
                    return clash;
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
            Line("  · C. 里程碑出口第 5 条：属于 FG-M4 的缺口全部 Closed、延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0, debtRows = 0, m4Rows = 0;
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
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 4)
                    {
                        m4Rows++;
                        bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || st.StartsWith("部分关闭", StringComparison.Ordinal);
                        if (!ok)
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || FirstMilestoneOf(cols[6]) != 4)
                {
                    continue;
                }
                m4Rows++;
                bool okD = st.StartsWith("Closed", StringComparison.Ordinal) || st.StartsWith("部分关闭", StringComparison.Ordinal) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!okD)
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 90 && debtRows > 250 && m4Rows >= 15, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M4 的 {m4Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M4 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（占位美术与待用户复核的顺延写明理由）"
                    : $"FG-M4 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            string[] mine = { "DEBT-FG4E2E01-01", "DEBT-FG4E2E01-02", "DEBT-FG4E2E01-03" };
            string[] closedHere = { "DEBT-FG3E2E01-01", "DEBT-FG3E2E01-05", "DEBT-FGTOOL01-01", "DEBT-FG4ECO03-06", "DEBT-FG4ECO11-05", "DEBT-FG4ECO11-06" };
            List<string> notClosed = closedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            bool registered = mine.All(status.ContainsKey);
            bool pending = status.TryGetValue("DEBT-FG4E2E01-03", out string p3) && p3.Contains("待用户");
            Expect(registered && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG4E2E01-01 清空机器的伤害夹具 → FG6-E2E-01、-02 关键材料进度夹具 → 首领落地、-03 可选试玩待用户）；" +
                "DEBT-FG3E2E01-01 / -05、DEBT-FGTOOL01-01、DEBT-FG4ECO03-06、DEBT-FG4ECO11-05 / -06 由本 Story 关闭" + (notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed)));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M4 材料齐全（M4 不是必试玩里程碑，作为可选试玩交付，真人试玩由用户择时进行、不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M4-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M4-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M4-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M4-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool questions = new[] { "FG04 §9 Q1", "FG04 §9 Q2", "FG04 §9 Q3" }.All(q => form.Contains(q) || table.Contains(q));
            bool tasks = script.Contains("零件") && script.Contains("50") && script.Contains("废液池") && script.Contains("离家报告") && script.Contains("应急打印");
            bool registered = readme.Contains("FG-M4") && readme.Contains("DEBT-FG4E2E01-03") && readme.Contains("不阻塞");
            Expect(questions && tasks && registered,
                $"问卷覆盖 FG04 第 9 节三个问题（{questions}）；脚本按出口旅程五步给任务卡（{tasks}）；目录登记“可选试玩、待用户、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FGT-ECO-001～010 各有自检段且登记在全量自检；五项另有旅程步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            var map = new (string Test, string File, string Registered, string Journey, string Step)[]
            {
                ("FGT-ECO-001", "FgManufacturingSelfCheck.cs", "FgManufacturingSelfCheck", FgjM4Journey.Id, "machine"),
                ("FGT-ECO-002", "FgBuildingOpsSelfCheck.cs", "FgBuildingOpsSelfCheck", null, null),
                ("FGT-ECO-003", "FgStandingRulesSelfCheck.cs", "FgStandingRulesSelfCheck", FgjM4Journey.Id, "rule_done"),
                ("FGT-ECO-004", "FgMachineRosterSelfCheck.cs", "FgMachineRosterSelfCheck", null, null),
                ("FGT-ECO-005", "FgSoftlockSelfCheck.cs", "FgSoftlockSelfCheck", FgjM4Journey.Id, "rescued"),
                ("FGT-ECO-006", "FgSoftlockSelfCheck.cs", "FgSoftlockSelfCheck", FgjM4Journey.Id, "pond_ok"),
                ("FGT-ECO-007", "FgAwayReportSelfCheck.cs", "FgAwayReportSelfCheck", FgjM4Journey.Id, "report"),
                ("FGT-ECO-008", "FgProductionStatsSelfCheck.cs", "FgProductionStatsSelfCheck", null, null),
                ("FGT-ECO-009", "FgAwayReportSelfCheck.cs", "FgAwayReportSelfCheck", null, null),
                ("FGT-ECO-010", "FgOverrideArraySelfCheck.cs", "FgOverrideArraySelfCheck", FgjM4ReverseJourney.Id, "r5_back"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered, string journey, string step) in map)
            {
                string src = ReadRepo(qa + file);
                bool journeyOk = journey == null || (JourneyCatalog.Create(journey)?.Steps.Any(s => s.Id == step) ?? false);
                if (src == null || !src.Contains(test) || !segments.Contains(registered) || !journeyOk)
                {
                    bad.Add($"{test}（{file}{(journey != null ? " / " + journey + " " + step : string.Empty)}）");
                }
            }
            bool m4 = segments.Contains("FgMilestoneM4SelfCheck");
            Expect(bad.Count == 0 && m4,
                $"{map.Length} 项自动验收各有自检段且登记在全量自检，其中 ECO-001 / 003 / 005 / 006 / 007 / 010 另有旅程步骤；[M4 出口] 本身登记在全量自检（{m4}）" +
                (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── F. 装配站面板布局 ──────────────────────────────────────────────────────

        private const string FactoryUxml = "Assets/GameRes/Raw/UI/Factory/FactoryPanel.uxml";

        private static void CheckFactoryPanelLayout()
        {
            Line("  · F. 装配站面板（旧旅程回归抓到）：材料 / 代付 / 缓存几行加长后，生产按钮不被压扁、不被提示文字盖住；布局探针通过");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过");
                return;
            }
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FactoryUxml);
            var src = AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath);
            if (vta == null || src == null)
            {
                Fail("装配站面板 UXML 或 PanelSettings 加载失败");
                return;
            }
            string materials = string.Join("\n", Enumerable.Range(0, 3).Select(i =>
                "战斗履带 ERC-003的材料：零件 ×4（装配站 0、仓库 12）、结构材 ×4（装配站 0、仓库 6）、电子件 ×2（装配站 0、仓库 3）、作战组件 ×2（装配站 1、仓库 0） 全部用废料：120 废料"));
            var bad = new List<string>();
            var seen = new List<string>();
            foreach (Vector2Int res in new[] { new Vector2Int(1920, 1080), new Vector2Int(1280, 720), new Vector2Int(2560, 1080), new Vector2Int(1280, 1024) })
            {
                PanelSettings settings = Object.Instantiate(src);
                settings.hideFlags = HideFlags.HideAndDontSave;
                settings.scale = 1f;
                var rt = new RenderTexture(res.x, res.y, 0) { hideFlags = HideFlags.HideAndDontSave };
                settings.targetTexture = rt;
                var go = new GameObject("__M4FactoryProbe") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    var doc = go.AddComponent<UIDocument>();
                    doc.panelSettings = settings;
                    doc.visualTreeAsset = vta;
                    VisualElement root = doc.rootVisualElement;
                    root.Q<Label>("ProduceHintLabel").text = "维修机：先让解析台通电运转后解锁";
                    root.Q<Label>("MaterialsLabel").text = materials;
                    root.Q<Label>("SubstituteNote").text = "已开：材料不齐时，还缺的按废料当量折算用废料代付（产线够用时不会动用废料）";
                    root.Q<Label>("BufferLabel").text = "装配站材料缓存：结构材 2、零件 3、电子件 2、作战组件 1";
                    root.Q<Toggle>("SubstituteToggle").label = "缺材料时用废料代付";
                    foreach (string b in new[] { "ProduceBtn_Erc003", "ProduceBtn_Hauler", "ProduceBtn_Hover" })
                    {
                        root.Q<Button>(b).text = "战斗履带 ERC-003（45 秒）";
                    }
                    UiToolkitLayoutProbe.ForceLayout(root);
                    var buttons = new[] { "ProduceBtn_Erc003", "ProduceBtn_Hauler", "ProduceBtn_Hover" }.Select(n => root.Q<VisualElement>(n)).ToList();
                    var others = new[] { "ProduceHintLabel", "MaterialsLabel", "SubstituteToggle", "SubstituteNote", "BufferLabel", "TabBar", "Title" }.Select(n => root.Q<VisualElement>(n)).ToList();
                    foreach (VisualElement b in buttons)
                    {
                        Rect r = b.worldBound;
                        if (r.height < 20f)
                        {
                            bad.Add($"{res.x}x{res.y} {b.name} 只有 {r.height:F0}px 高（被压扁）");
                        }
                        foreach (VisualElement o in others.Concat(buttons.Where(x => x != b)))
                        {
                            Rect q = o.worldBound;
                            if (q.width > 0.5f && q.height > 0.5f && r.Overlaps(q) && Rect.MinMaxRect(Mathf.Max(r.xMin, q.xMin), Mathf.Max(r.yMin, q.yMin), Mathf.Min(r.xMax, q.xMax), Mathf.Min(r.yMax, q.yMax)).height > 1f)
                            {
                                bad.Add($"{res.x}x{res.y} {b.name} 与 {o.name} 重叠");
                            }
                        }
                    }
                    Rect panel = root.Q<VisualElement>("FactoryPanelRoot").worldBound;
                    seen.Add($"{res.x}x{res.y} 面板 {panel.height:F0}px 高、按钮 {buttons[0].worldBound.height:F0}px");
                }
                finally
                {
                    Object.DestroyImmediate(go);
                    Object.DestroyImmediate(settings);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            Expect(bad.Count == 0, $"四种分辨率下生产按钮都 ≥ 20px 高、不与提示 / 材料 / 代付 / 缓存几行重叠（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad.Take(8)))}");
            string probe = UiToolkitLayoutProbe.Probe(FactoryUxml, "FactoryPanelRoot");
            Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "装配站面板布局探针（四种分辨率 + 超长文字压测 + USS 体检）：" + probe.Split('\n').FirstOrDefault()
                + (probe.StartsWith("PASS", StringComparison.Ordinal) ? string.Empty : "\n" + string.Join("\n", probe.Split('\n').Skip(1).Take(12))));
        }

        // ── G. 工单面板原因接线 ─────────────────────────────────────────────────────

        private static void CheckWorkOrderReasonWiring()
        {
            Line("  · G. DEBT-FG4ECO11-06：工单面板的施工单原因走与施工队列同一个描述（行为由 [超控阵列] E8b 断言）");
            string panel = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/WorkOrder/WorkOrderPanelUIToolkit.cs") ?? string.Empty;
            string orders = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyWorkOrders.cs") ?? string.Empty;
            string selfCheck = ReadRepo("TEngine/UnityProject/Assets/Editor/QA/FgOverrideArraySelfCheck.cs") ?? string.Empty;
            bool row = panel.Contains("ReasonText(ruleState, order)") && panel.Contains("HomeValleyConstruction.DescribeStatus(state, order)");
            bool command = orders.Contains("DescribeMaterialsReason(CampaignSession.Current, reason)");
            bool tested = selfCheck.Contains("E8b DEBT-FG4ECO11-06") && selfCheck.Contains("WorkOrderPanelUIToolkit.ReasonText(s, o)");
            Expect(row && command && tested,
                $"工单面板每行的原因按这张工单描述（施工单等材料时与施工队列 / 悬停同一句，{row}）；右键被拒的说明带当前状态（{command}）；[超控阵列] E8b 在“关键材料正被搬回”时逐字比对（{tested}）");
        }

        // ── H. 诊断穿过分流器 ────────────────────────────────────────────────────

        private static void CheckDiagnosisThroughSplitter()
        {
            Line("  · H. “为什么不工作”的缺料链穿过分流器找到上游（FGJ-M4 抓到：分流器出口接出的带是另一个网络，原来写“输入带上没有能供应的建筑”）；输入带中途一格转反时点名那一格");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过");
                return;
            }
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                CampaignState s = FgProductionSelfCheck.NewWorld(9461, true, 300);
                GridCell? o = FgProductionSelfCheck.FindArea(s, 14, 3);
                if (o == null)
                {
                    Fail("H 找不到 14×3 的空地");
                    return;
                }
                GridCell drillAt = FgProductionSelfCheck.At(o.Value, 0, 1);
                FgProductionSelfCheck.SetFootprint(s, "extraction_drill", drillAt, "ore_metal");
                FgProductionSelfCheck.PowerUp(s);
                BuildingRecord drill = FgProductionSelfCheck.Built(s, "extraction_drill", "h_drill", drillAt);
                BuildingRecord furnace = FgProductionSelfCheck.Built(s, "refinery_furnace", "h_furnace", FgProductionSelfCheck.At(drillAt, 7, 0));
                GridCell inBelt = FgProductionSelfCheck.At(drillAt, 2, 0);
                // 提取钻输出口外一格朝东 → 分流器（朝东）→ 左口（北）一格朝东、再一格朝东、再一格朝南 → 精炼炉输入口外那一格（朝东）。
                bool laid = BeltNetworkService.TryPlace(s, inBelt, BinGames.Sim.Logistics.BeltDir.East, 0).Ok
                            && BeltNetworkService.TryPlaceNode(s, FgProductionSelfCheck.At(inBelt, 1, 0), BinGames.Sim.Logistics.BeltDir.East, 0, BinGames.Sim.Logistics.BeltNodeKind.Splitter).Ok
                            && BeltNetworkService.TryPlace(s, FgProductionSelfCheck.At(inBelt, 1, 1), BinGames.Sim.Logistics.BeltDir.East, 0).Ok
                            && BeltNetworkService.TryPlace(s, FgProductionSelfCheck.At(inBelt, 2, 1), BinGames.Sim.Logistics.BeltDir.East, 0).Ok
                            && BeltNetworkService.TryPlace(s, FgProductionSelfCheck.At(inBelt, 3, 1), BinGames.Sim.Logistics.BeltDir.South, 0).Ok
                            && BeltNetworkService.TryPlace(s, FgProductionSelfCheck.At(inBelt, 3, 0), BinGames.Sim.Logistics.BeltDir.East, 0).Ok
                            // 精炼炉输出口外 4 格传送带（末端不接，与 [生产建筑] D 段同一个死路）：没有输出带时合金一出来就堵在输出口，测不到缺料。
                            && FgProductionSelfCheck.Belts(s, FgProductionSelfCheck.At(drillAt, 9, 0), BinGames.Sim.Logistics.BeltDir.East, 4);
                if (!laid || drill == null || furnace == null)
                {
                    Fail("H 测试准备：铺不了“提取钻 → 分流器 → 精炼炉”");
                    return;
                }
                FgProductionSelfCheck.Resync(s);
                ProductionService.TrySetRecipe(s, furnace.BuildingId, "alloy_ore", out _);
                // 先确认这条线真的经分流器供料（不是摆设）：开着时精炼炉出合金。
                // 等到第一份合金（最多 60 游戏秒：配方生效、矿走完分流器那一圈都要时间）；全程产出控制在输出死路放得下的件数以内（4 格带 + 输出缓存）。
                FgProductionSelfCheck.StepUntil(() => ProductionService.TryGet(s, furnace.BuildingId, out ProductionService.Producer q) && q.Rec.Completed > 0, 60);
                long made = ProductionService.TryGet(s, furnace.BuildingId, out ProductionService.Producer fp) ? fp.Rec.Completed : -1;
                HomeValleyPowerGrid.TryToggleShutdown(s, drill.BuildingId);
                FgProductionSelfCheck.Resync(s);
                FgProductionSelfCheck.Seconds(30f);
                DiagReport rep = RootCauseDiagnosis.DiagnoseBuilding(s, furnace);
                DiagChain chain = rep?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Input);
                bool starved = fp != null && fp.State == ProdState.MissingInput;
                bool upstream = chain != null && chain.Root != null && chain.Root.Code == DiagCode.ProdUpstream && chain.Root.TargetId == drill.BuildingId;
                bool noFalse = chain != null && chain.Steps.All(x => x.Code != DiagCode.ProdNoSupplier);
                Expect(made > 0 && starved && upstream && noFalse,
                    $"提取钻 → 分流器 → 精炼炉：开着时精炼炉出了 {made} 次合金；关停提取钻后精炼炉“{(fp != null ? ProductionService.StateText(fp) : "?")}”，“为什么不工作”穿过分流器找到上游提取钻（{upstream}），" +
                    $"没有“输入带上没有能供应的建筑”的误判（{noFalse}）：{(chain == null ? "（没有链）" : RootCauseDiagnosis.ChainText(chain))}");

                // H2（FGJ-M4 抓到的第二处）：提取钻开回来，把分流器之后的一格转反（与前一格顶住）——精炼炉输入口那一段带没有上游，
                // 原来写“输入带上没有能供应金属矿的建筑：在上游接一座……”，实际是带断在转反的那一格；应点名那一格。转回来后产线恢复。
                HomeValleyPowerGrid.TryToggleShutdown(s, drill.BuildingId);
                FgProductionSelfCheck.Resync(s);
                FgProductionSelfCheck.Seconds(12f);
                GridCell flipped = FgProductionSelfCheck.At(inBelt, 2, 1);
                bool reversed = BeltNetworkService.TryReverse(s, flipped).Ok;
                FgProductionSelfCheck.Resync(s);
                FgProductionSelfCheck.Seconds(30f);
                ProductionService.TryGet(s, furnace.BuildingId, out fp);
                DiagReport rep2 = RootCauseDiagnosis.DiagnoseBuilding(s, furnace);
                DiagChain chain2 = rep2?.Chains.FirstOrDefault(c => c.Category == DiagCategory.Input);
                bool starved2 = fp != null && fp.State == ProdState.MissingInput;
                DiagStep root2 = chain2?.Root;
                bool namesFlip = root2 != null && root2.Code == DiagCode.BeltTerminal && root2.HasPosition
                                 && Mathf.RoundToInt(root2.Position.x) == flipped.X && Mathf.RoundToInt(root2.Position.y) == flipped.Y
                                 && root2.Text.Contains($"({flipped.X}, {flipped.Y})") && root2.Text.Contains("朝向反了");
                bool noFalse2 = chain2 != null && chain2.Steps.All(x => x.Code != DiagCode.ProdNoSupplier);
                long before = fp != null ? fp.Rec.Completed : -1;
                string state2 = fp != null ? ProductionService.StateText(fp) : "?";
                bool back = BeltNetworkService.TryReverse(s, flipped).Ok;
                FgProductionSelfCheck.Resync(s);
                FgProductionSelfCheck.Seconds(20f);
                long after = ProductionService.TryGet(s, furnace.BuildingId, out ProductionService.Producer fp2) ? fp2.Rec.Completed : -1;
                Expect(reversed && starved2 && namesFlip && noFalse2 && back && after > before,
                    $"分流器后一格 {flipped.X}, {flipped.Y} 转反：精炼炉“{state2}”，“为什么不工作”点名转反的那一格（{namesFlip}）、" +
                    $"没有“没有供应者”的误判（{noFalse2}）：{(chain2 == null ? "（没有链）" : RootCauseDiagnosis.ChainText(chain2))}；转回来后精炼炉完成 {before} → {after}");
            }
            finally
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
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

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
            Debug.LogWarning("[M4 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
