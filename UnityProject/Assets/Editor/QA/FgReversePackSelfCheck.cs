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
    /// FG15-QA-01 反向旅程全集（IC-REQ-022）：把各里程碑已有的反向场景合成一套回归包，一键入口 <c>bash tools/unity-reverse-pack.sh</c>
    /// （先跑清单里的反向旅程，再在同一进程里跑清单里的自检段，本段排在全部段之后核对本轮结果并写回归报告）。
    ///
    /// A. 回归包清单（tools/reverse_pack.txt，单一来源）：清单里的旅程都在旅程登记表、登记表里每条反向旅程（FGJ-M*R）都在清单里；
    ///    清单里的段都已登记、覆盖矩阵引用到的段全在清单里；本段登记在全部段之后。
    /// B. 覆盖矩阵：FG-M0～M14、FG-EMG 阶段 1～4、EMG-R × IC-REQ-022 六类，每格指向一条反向旅程的步骤或一条起真实系统的断言
    ///    （复用各里程碑自检的 ReverseSteps / ReverseMap，不另抄一份）；不适用的格写明理由。本进程跑过的段再核本轮失败 0、通过 &gt; 0；
    ///    一键回归时（环境变量 BINGAMES_REVERSE_PACK_START）每一格都必须本轮跑过，旅程格的步骤必须在本轮报告里打勾。
    /// C. 旧断言新口径（卡片“必须同时交付”第 2 条，底稿 06 §3.3）：旅程源码不按旧表下标读反应计数、不断言倍率；
    ///    FGJ-M1 / M2 / M6 / M10 的反应计数走 CombatSite.ReactionCountOf（稳定键）。
    /// D. 回归报告（只在一键回归时）：逐条旅程 / 段对照 M10-BASELINE.md 第 2 节结论与第 11 节差异表，判“无差异 / 预期差异 / 退化”，
    ///    写 production/qa/evidence/_reverse-pack-report.md；任何一条“退化”本段失败。
    /// E. 负向矩阵：用合成输入驱动 A～D 的判定函数（漏登反向旅程、段未登记、标签只在注释里、本轮失败 / 没跑 / 报告陈旧、旧口径断言、分类器各分支）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgReversePackSelfCheck，排在最后）；单段：bash tools/unity-validate.sh --segment FgReversePackSelfCheck。
    /// </summary>
    public static class FgReversePackSelfCheck
    {
        public const string SegmentName = "FgReversePackSelfCheck";
        internal const string ManifestPath = "tools/reverse_pack.txt";
        internal const string BaselinePath = "production/design/build-variety-theme-fit/M10-BASELINE.md";
        internal const string ReportPath = "production/qa/evidence/_reverse-pack-report.md";
        internal const string EvidenceDir = "production/qa/evidence/";
        internal const string PackStartEnv = "BINGAMES_REVERSE_PACK_START";
        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";

        internal static readonly string[] Six = { "资源不足", "目标死亡", "断网 / 失联", "暂停与倍速", "存读档", "路径失败" };

        internal static readonly string[] Milestones =
        {
            "FG-M0", "FG-M1", "FG-M2", "FG-M3", "FG-M4", "FG-M5", "FG-M6", "FG-M7", "FG-M8", "FG-M9", "FG-M10", "FG-M11", "FG-M12", "FG-M13", "FG-M14",
            "FG-EMG-1", "FG-EMG-2", "FG-EMG-3", "FG-EMG-4", "FG-EMG-R",
        };

        internal enum CellKind { Journey, Expect, Method, Source }

        /// <summary>覆盖矩阵的一格：Kind = Journey 时 Source = 旅程 ID、Label = 逗号分隔的步骤 ID；否则 Source = 段名、File = 段源文件、Label = 断言原文（Expect：只认 Expect( 参数）/ 方法名（Method）/
        /// 源码原文（Source：注释以外的代码里出现即可——M9～M13 自检的反向表本来就按这个口径，标签可能落在检查小节标题或传给断言助手的参数里）。</summary>
        internal sealed class Cell
        {
            public string Milestone;
            public int Cat;
            public string What;
            public CellKind Kind;
            public string Source;
            public string File;
            public string Label;
        }

        /// <summary>不适用的格（里程碑, 类别, 理由）。</summary>
        internal static readonly (string Milestone, int Cat, string Why)[] NotApplicable =
        {
            ("FG-EMG-3", 5, "阶段 3 交付底盘特性 / 协同 / 元程序 / 节拍，没有改寻路或通行的系统（EMG4-E2E-02 映射表同一裁决）"),
            ("FG-EMG-R", 0, "EMG-R 补回被性能砍掉的寻路 / 自卫 / 容量 / 节拍 / 线索 / 计数表，没有新的消耗型资源；出口卡 EMGR-E2E-01 负向只要求按改动点抽查存读档 / 暂停 / 倍速 / 静默夜 / 覆盖外与路径失败"),
            ("FG-EMG-R", 1, "同上：EMG-R 的改动点不涉及目标死亡后的流程（阵亡结算沿用 FG-EMG 阶段 1～4，由 FG-EMG-1～4 的格覆盖）"),
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("QA/自检/反向回归包")]
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
            long packStart = PackStart();
            Line($"\n[反向回归包] FG15-QA-01：清单门禁、IC-REQ-022 覆盖矩阵（{Milestones.Length} 个里程碑 / 阶段 × 六类）与本轮运行期交叉核对、旧断言新口径、回归报告、负向矩阵" +
                 (packStart > 0 ? $"（一键回归模式，本轮开始于 {DateTimeOffset.FromUnixTimeSeconds(packStart).ToLocalTime():yyyy-MM-dd HH:mm:ss}）" : "（普通模式：只核映射与本进程已跑的段）"));
            Step(CheckManifest);
            Step(() => CheckCoverage(packStart));
            Step(CheckLegacyAssertions);
            Step(() => CheckReport(packStart));
            Step(CheckNegativeMatrix);
            Line($"  · [反向回归包] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static long PackStart()
        {
            string v = Environment.GetEnvironmentVariable(PackStartEnv);
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t > 0 ? t : 0;
        }

        // ── A. 回归包清单 ─────────────────────────────────────────────────────────

        internal static void ParseManifest(string text, out List<string> journeys, out List<string> segments)
        {
            journeys = new List<string>();
            segments = new List<string>();
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] p = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && p[0] == "journey")
                {
                    journeys.Add(p[1]);
                }
                else if (p.Length >= 2 && p[0] == "segment")
                {
                    segments.Add(p[1]);
                }
            }
        }

        private static readonly Regex ReverseJourneyId = new Regex(@"^FGJ-M\d+R$");

        /// <summary>清单门禁（纯函数，负向矩阵用合成输入驱动）。registered = 全量自检登记顺序。</summary>
        internal static List<string> ManifestProblems(IReadOnlyList<string> journeys, IReadOnlyList<string> segments, IEnumerable<string> catalogIds,
                                                      IReadOnlyList<string> registered, IEnumerable<string> requiredSegments)
        {
            var bad = new List<string>();
            var catalog = new HashSet<string>(catalogIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var reg = new HashSet<string>(registered ?? Array.Empty<string>(), StringComparer.Ordinal);
            var segSet = new HashSet<string>(segments, StringComparer.Ordinal);
            bad.AddRange(journeys.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => $"旅程 {g.Key} 在清单里出现 {g.Count()} 次"));
            bad.AddRange(segments.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => $"段 {g.Key} 在清单里出现 {g.Count()} 次"));
            bad.AddRange(journeys.Where(j => !catalog.Contains(j)).Select(j => $"清单里的旅程 {j} 不在旅程登记表"));
            bad.AddRange(catalog.Where(id => ReverseJourneyId.IsMatch(id) && !journeys.Contains(id)).OrderBy(x => x).Select(id => $"反向旅程 {id} 登记了但不在回归包清单"));
            bad.AddRange(segments.Where(s => !reg.Contains(s)).Select(s => $"清单里的段 {s} 没有登记在全量自检"));
            bad.AddRange((requiredSegments ?? Enumerable.Empty<string>()).Distinct().Where(s => !segSet.Contains(s)).OrderBy(x => x).Select(s => $"覆盖矩阵引用的段 {s} 不在回归包清单"));
            if (registered == null || registered.Count == 0 || registered[registered.Count - 1] != SegmentName)
            {
                bad.Add($"{SegmentName} 不是全量自检的最后一段（要在别的段跑完后核本轮结果）");
            }
            if (!segSet.Contains(SegmentName))
            {
                bad.Add($"清单里没有 {SegmentName}（一键回归时由它写报告）");
            }
            return bad;
        }

        private static void CheckManifest()
        {
            Line($"  · A. 回归包清单（{ManifestPath}）：旅程都已登记、登记表里的反向旅程都在包里、段都已登记、覆盖矩阵引用的段全在包里、本段排最后");
            string text = ReadRepo(ManifestPath);
            ParseManifest(text, out List<string> journeys, out List<string> segments);
            List<Cell> cells = Cells();
            IEnumerable<string> required = cells.Where(c => c.Kind != CellKind.Journey).Select(c => c.Source)
                .Concat(Enumerable.Range(1, 14).Select(i => $"FgMilestoneM{i}SelfCheck"))
                .Concat(new[] { "FgEmgEpicExitSelfCheck" });
            List<string> bad = ManifestProblems(journeys, segments, JourneyCatalog.Ids, CellFrameworkValidate.SegmentNames, required);
            Expect(text != null && journeys.Count > 0 && segments.Count > 0 && bad.Count == 0,
                $"清单 {journeys.Count} 条旅程（{string.Join(" ", journeys)}）、{segments.Count} 段{(text == null ? "；清单文件不存在" : string.Empty)}" +
                (bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad)));
            List<string> journeyCells = cells.Where(c => c.Kind == CellKind.Journey).Select(c => c.Source).Distinct().ToList();
            List<string> lost = journeyCells.Where(j => !journeys.Contains(j)).ToList();
            Expect(lost.Count == 0, $"覆盖矩阵用到的反向旅程（{string.Join(" / ", journeyCells)}）都在回归包里{(lost.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lost))}");
        }

        // ── B. 覆盖矩阵 ─────────────────────────────────────────────────────────

        /// <summary>类别归一：先看“R1～R6”前缀，否则取最早出现的关键词；“承接 …”与非六类条目返回 -1（仍做运行期核对，不计入六类）。</summary>
        internal static int CategoryOf(string what)
        {
            string s = (what ?? string.Empty).TrimStart();
            if (s.Length >= 2 && s[0] == 'R' && s[1] >= '1' && s[1] <= '6')
            {
                return s[1] - '1';
            }
            if (s.StartsWith("承接", StringComparison.Ordinal))
            {
                return -1;
            }
            string[][] keys =
            {
                new[] { "资源", "条件不足" }, new[] { "目标死亡", "阵亡" }, new[] { "断网", "断链", "失联" },
                new[] { "暂停", "倍速" }, new[] { "存读档" }, new[] { "路径", "地点失败" },
            };
            int best = -1, at = int.MaxValue;
            for (int c = 0; c < keys.Length; c++)
            {
                foreach (string k in keys[c])
                {
                    int i = s.IndexOf(k, StringComparison.Ordinal);
                    if (i >= 0 && i < at)
                    {
                        at = i;
                        best = c;
                    }
                }
            }
            return best;
        }

        private static IEnumerable<Cell> JourneyCells(string milestone, string journeyId, IEnumerable<(string Kind, string[] Ids)> steps) =>
            steps.Select(x => new Cell { Milestone = milestone, Cat = CategoryOf(x.Kind), What = x.Kind, Kind = CellKind.Journey, Source = journeyId, Label = string.Join(",", x.Ids) });

        private static IEnumerable<Cell> MapCells(string milestone, IEnumerable<(string Category, string Segment, string File, string Label)> map) =>
            map.Select(x => new Cell { Milestone = milestone, Cat = CategoryOf(x.Category), What = x.Category, Kind = CellKind.Source, Source = x.Segment, File = x.File, Label = x.Label });

        private static Cell MethodCell(string milestone, int cat, string what, string segment, string file, string method) =>
            new Cell { Milestone = milestone, Cat = cat, What = what, Kind = CellKind.Method, Source = segment, File = file, Label = method };

        private static Cell ExpectCell(string milestone, int cat, string what, string segment, string file, string label) =>
            new Cell { Milestone = milestone, Cat = cat, What = what, Kind = CellKind.Expect, Source = segment, File = file, Label = label };

        /// <summary>覆盖矩阵全表：M1～M6 / M9～M14 / FG-EMG 复用各里程碑自检的反向表；M7 / M8 / EMG-R 是起真实系统的检查方法或断言；
        /// FG-M0 里程碑原文没有反向旅程，它交付的格网建造 / 寻路 / 存档 / 统一时钟由 FGJ-M3R 六类覆盖（FG-GAP-271）。</summary>
        internal static List<Cell> Cells()
        {
            var cells = new List<Cell>();
            cells.AddRange(JourneyCells("FG-M0", FgjM3ReverseJourney.Id, FgMilestoneM3SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M1", FgjM1ReverseJourney.Id, FgMilestoneM1SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M2", FgjM2ReverseJourney.Id, FgMilestoneM2SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M3", FgjM3ReverseJourney.Id, FgMilestoneM3SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M4", FgjM4ReverseJourney.Id, FgMilestoneM4SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M5", FgjM5ReverseJourney.Id, FgMilestoneM5SelfCheck.ReverseSteps));
            cells.AddRange(JourneyCells("FG-M6", FgjM6ReverseJourney.Id, FgMilestoneM6SelfCheck.ReverseSteps));
            const string m7 = "FgMilestoneM7SelfCheck", m7f = "FgMilestoneM7SelfCheck.cs", m8 = "FgMilestoneM8SelfCheck", m8f = "FgMilestoneM8SelfCheck.World.cs";
            cells.Add(MethodCell("FG-M7", 0, "资源不足：净化塔缺冷却液 → 原因 → 恢复", m7, m7f, "CheckResourceShortage"));
            cells.Add(MethodCell("FG-M7", 1, "目标死亡：回收机被摧毁", m7, m7f, "CheckTargetDeath"));
            cells.Add(MethodCell("FG-M7", 2, "断网 / 失联：静默夜断信号", m7, m7f, "CheckSignalLoss"));
            cells.Add(MethodCell("FG-M7", 3, "暂停与倍速：静默夜 + 酸雨中暂停与 0.5x / 3x", m7, m7f, "CheckPauseAndSpeed"));
            cells.Add(MethodCell("FG-M7", 4, "存读档：静默夜 + 酸雨 + 缺液中真文件存读档（FGT-ENV-008）", m7, m7f, "CheckSaveLoad"));
            cells.Add(MethodCell("FG-M7", 5, "路径失败：遗迹被悬崖围住", m7, m7f, "CheckPathFailure"));
            cells.Add(MethodCell("FG-M8", 0, "资源不足：投送站没有燃油 → 管线恢复", m8, m8f, "CheckSignalLossAndFuel"));
            cells.Add(MethodCell("FG-M8", 1, "目标死亡：回收舱在飞时目标阵亡 → 货物舱捡回", m8, m8f, "CheckRecallTargetDeath"));
            cells.Add(MethodCell("FG-M8", 2, "断网 / 失联：远征队走出覆盖 → 中继恢复", m8, m8f, "CheckSignalLossAndFuel"));
            cells.Add(MethodCell("FG-M8", 3, "暂停与倍速：行军 + 投送中暂停 / 0.5x / 3x / 观察一致（FGT-EXP-012）", m8, m8f, "CheckPauseSpeedObserved"));
            cells.Add(MethodCell("FG-M8", 4, "存读档：领地内行军 + 投送途中真文件存读档（FGT-EXP-011）", m8, m8f, "CheckSaveLoad"));
            cells.Add(MethodCell("FG-M8", 5, "路径失败：锚点被围截断 → 投送舱接回", m8, m8f, "CheckPathFailure"));
            cells.AddRange(MapCells("FG-M9", FgMilestoneM9SelfCheck.ReverseMap));
            cells.AddRange(MapCells("FG-M10", FgMilestoneM10SelfCheck.ReverseMap));
            cells.AddRange(MapCells("FG-M11", FgMilestoneM11SelfCheck.ReverseMap));
            cells.AddRange(MapCells("FG-M12", FgMilestoneM12SelfCheck.ReverseMap));
            cells.AddRange(MapCells("FG-M13", FgMilestoneM13SelfCheck.ReverseMap));
            cells.AddRange(FgMilestoneM14SelfCheck.ReverseMap.Select(x => ExpectCell("FG-M14", CategoryOf(x.Category), x.Category, x.Segment, x.Segment + ".cs", x.Label)));
            cells.AddRange(FgEmgEpicExitSelfCheck.ReverseMap.Select(x => ExpectCell("FG-EMG-" + x.Stage.ToString(CultureInfo.InvariantCulture), CategoryOf(x.What), x.What, x.Segment, x.File, x.Label)));
            // EMG-R：出口卡 EMGR-E2E-01 证据 B5 的抽查表（改动点 × 存读档 / 暂停 / 倍速 / 静默夜 / 覆盖外 + 路径失败）落到起真实系统的检查
            cells.Add(MethodCell("FG-EMG-R", 2, "断网 / 失联：远征覆盖外 / 静默夜卡位自动重试后仍截断 → 可定位提示（EMGR-NAV-01）", "FgExpeditionNavRetrySelfCheck", "FgExpeditionMarchSelfCheck.cs", "CheckTruncationOutOfCoverage"));
            cells.Add(MethodCell("FG-EMG-R", 2, "断网 / 失联：家园信号断开时教义自卫照常（EMGR-CMD-01）", "FgSelfDefenseSelfCheck", "FgNavSelfCheck.SelfDefense.cs", "CheckSdHomeSignal"));
            cells.Add(ExpectCell("FG-EMG-R", 3, "暂停与倍速：节拍规则暂停 600 步不变（EMGR-RULE-01）", "FgEmgRuleTickSelfCheck", "FgEmgRuleSelfCheck.Tick.cs", "暂停 600 步：电解次数不变"));
            cells.Add(ExpectCell("FG-EMG-R", 3, "暂停与倍速：结构单位接触暂停不变（EMGR-MAT-01）", "FgEmgCapacitySelfCheck", "FgEmgCapacitySelfCheck.cs", "E2 暂停（不步进）时结构单位血量"));
            cells.Add(MethodCell("FG-EMG-R", 4, "存读档：自卫标记进快照（EMGR-CMD-01）", "FgSelfDefenseSelfCheck", "FgNavSelfCheck.SelfDefense.cs", "CheckSdSnapshot"));
            cells.Add(MethodCell("FG-EMG-R", 4, "存读档：结构单位接触中途快照续跑一致（EMGR-MAT-01）", "FgEmgCapacitySelfCheck", "FgEmgCapacitySelfCheck.cs", "CheckSaveLoad"));
            cells.Add(MethodCell("FG-EMG-R", 5, "路径失败：右键攻击封死的目标 → “无法到达”结束、可定位提示、可改命令（EMGR-NAV-01）", "FgNavAttackSelfCheck", "FgNavSelfCheck.cs", "CheckAttackUnreachable"));
            return cells;
        }

        /// <summary>每个里程碑 × 六类至少一格（或登记了不适用）。</summary>
        internal static List<string> Holes(IEnumerable<Cell> cells, IEnumerable<(string Milestone, int Cat, string Why)> na, IEnumerable<string> milestones)
        {
            var have = new HashSet<string>(cells.Where(c => c.Cat >= 0).Select(c => c.Milestone + "#" + c.Cat), StringComparer.Ordinal);
            var skip = new HashSet<string>(na.Select(x => x.Milestone + "#" + x.Cat), StringComparer.Ordinal);
            var holes = new List<string>();
            foreach (string m in milestones)
            {
                for (int c = 0; c < Six.Length; c++)
                {
                    string key = m + "#" + c;
                    if (!have.Contains(key) && !skip.Contains(key))
                    {
                        holes.Add($"{m}·{Six[c]}");
                    }
                }
            }
            return holes;
        }

        /// <summary>一格的运行期核对（纯函数：读源码 / 段登记 / 段计时 / 旅程定义 / 本轮旅程报告都由调用方给）。返回问题描述，null = 通过；ran = 本轮是否跑过。</summary>
        internal static string CellProblem(Cell c, Func<string, string> readQaSource, Func<string, bool> registered, Func<string, (int Pass, int Fail)?> timing,
                                           Func<string, JourneyDef> journey, Func<string, JourneyRun> runOf, bool packMode, out bool ran)
        {
            ran = false;
            if (c.Kind == CellKind.Journey)
            {
                JourneyDef d = journey(c.Source);
                string[] ids = c.Label.Split(',');
                List<string> missing = ids.Where(id => d == null || d.Steps.All(s => s.Id != id)).ToList();
                if (missing.Count > 0)
                {
                    return $"{c.Source} 没有步骤 {string.Join(" / ", missing)}";
                }
                JourneyRun run = runOf(c.Source);
                if (run == null)
                {
                    return packMode ? $"{c.Source} 本轮没有新报告（旅程没跑或报告早于本轮开始）" : null;
                }
                ran = true;
                List<string> unchecked_ = ids.Where(id => !run.PassedSteps.Contains(id)).ToList();
                return unchecked_.Count == 0 ? null : $"{c.Source} 本轮报告里步骤 {string.Join(" / ", unchecked_)} 没有打勾（结论 {run.Verdict}{(run.FailLine == null ? string.Empty : "：" + run.FailLine)}）";
            }
            if (!registered(c.Source))
            {
                return $"段 {c.Source} 没有登记在全量自检";
            }
            string src = readQaSource(c.File);
            bool mapped = c.Kind == CellKind.Method
                ? src != null && Regex.IsMatch(src, @"\b(private|internal|public)\s+static\s+void\s+" + Regex.Escape(c.Label) + @"\s*\(")
                : c.Kind == CellKind.Source
                    ? src != null && StripComments(src).Contains(c.Label)
                    : src != null && FgEmgEpicExitSelfCheck.InExpectCall(src, c.Label);
            if (!mapped)
            {
                return c.Kind == CellKind.Method ? $"{c.File} 里没有检查方法 {c.Label}"
                    : c.Kind == CellKind.Source ? $"{c.File} 的代码里（注释除外）没有“{c.Label}”" : $"{c.File} 里“{c.Label}”不在任何 Expect( 的参数里";
            }
            (int Pass, int Fail)? t = timing(c.Source);
            if (t == null)
            {
                return packMode ? $"段 {c.Source} 本轮没有跑" : null;
            }
            ran = true;
            return t.Value.Fail == 0 && t.Value.Pass > 0 ? null : $"段 {c.Source} 本轮 {t.Value.Pass} 通过 / {t.Value.Fail} 失败";
        }

        private static void CheckCoverage(long packStart)
        {
            bool pack = packStart > 0;
            Line("  · B. IC-REQ-022 覆盖矩阵：每个里程碑 / 阶段 × 六类都有一格（或写明不适用）；每格的步骤 / 断言仍在、所在段已登记；本轮跑过的再核结果" +
                 (pack ? "（一键回归：每格都必须本轮跑过）" : string.Empty));
            List<Cell> cells = Cells();
            List<string> holes = Holes(cells, NotApplicable, Milestones);
            Expect(holes.Count == 0, $"覆盖矩阵 {cells.Count} 格，{Milestones.Length} 个里程碑 / 阶段 × 六类无空格（不适用 {NotApplicable.Length} 格：{string.Join("；", NotApplicable.Select(x => x.Milestone + "·" + Six[x.Cat]))}）" +
                                     (holes.Count == 0 ? string.Empty : "；空格：" + string.Join("、", holes)));
            var segs = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            string Src(string file)
            {
                if (!sources.TryGetValue(file, out string s))
                {
                    s = ReadRepo(Qa + file);
                    sources[file] = s;
                }
                return s;
            }
            var runs = new Dictionary<string, JourneyRun>(StringComparer.Ordinal);
            JourneyRun RunOf(string id)
            {
                if (!runs.TryGetValue(id, out JourneyRun r))
                {
                    r = pack ? FreshRun(id, packStart, out _) : null;
                    runs[id] = r;
                }
                return r;
            }
            foreach (string m in Milestones)
            {
                List<Cell> mine = cells.Where(c => c.Milestone == m).ToList();
                var bad = new List<string>();
                int ran = 0;
                foreach (Cell c in mine)
                {
                    string p = CellProblem(c, Src, segs.Contains, TimingTuple, JourneyCatalog.Create, RunOf, pack, out bool r);
                    if (r)
                    {
                        ran++;
                    }
                    if (p != null)
                    {
                        bad.Add($"{(c.Cat >= 0 ? Six[c.Cat] : "承接")}（{p}）");
                    }
                }
                string cats = string.Join(" / ", Enumerable.Range(0, Six.Length).Select(i => $"{Six[i]} {mine.Count(c => c.Cat == i)}"));
                Expect(bad.Count == 0, $"{m}：{mine.Count} 格（{cats}），本轮已跑 {ran} 格" + (bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad)));
            }
        }

        private static (int Pass, int Fail)? TimingTuple(string segment)
        {
            CellFrameworkValidate.SegmentTiming t = CellFrameworkValidate.TimingOf(segment);
            return t == null ? ((int, int)?)null : (t.Pass, t.Fail);
        }

        // ── C. 旧断言新口径 ──────────────────────────────────────────────────────

        private static readonly Regex IndexedReactionCount = new Regex(@"\.ReactionCountOf\(\s*(\d|[a-z]\w*Index\b)|\bReactionCount\s*\[");
        private static readonly Regex MultiplierAssert = new Regex(@"\b(ReactionDamageMul\w*|DamageMultiplier|ReactionMultiplier\w*|ReactionMul\b|ReactionDamageScale)\b");

        /// <summary>FG-EMG 阶段 2 回改后的口径（纯函数）：不按旧表下标读反应计数、不断言反应倍率；requireStable 里的文件必须按稳定键读计数。</summary>
        internal static List<string> LegacyAssertProblems(IEnumerable<(string File, string Src)> files, IEnumerable<string> requireStable)
        {
            var bad = new List<string>();
            var all = files.ToList();
            foreach ((string file, string src) in all)
            {
                string code = StripComments(src ?? string.Empty);
                if (IndexedReactionCount.IsMatch(code))
                {
                    bad.Add($"{file} 按旧表下标读反应计数（{IndexedReactionCount.Match(code).Value}）");
                }
                if (MultiplierAssert.IsMatch(code))
                {
                    bad.Add($"{file} 读反应倍率（{MultiplierAssert.Match(code).Value}）");
                }
            }
            foreach (string f in requireStable)
            {
                string src = all.FirstOrDefault(x => x.File == f).Src;
                if (src == null || !StripComments(src).Contains("CombatSite.ReactionCountOf("))
                {
                    bad.Add($"{f} 没有按稳定键（CombatSite.ReactionCountOf）读反应计数");
                }
            }
            return bad;
        }

        private static string StripComments(string src) => Regex.Replace(src, @"//[^\n]*", string.Empty);

        internal static readonly string[] StableCountFiles = { "FgjM1Journey.cs", "FgjM2Common.cs", "FgjM6Journey.Raids.cs", "FgjM10Journey.cs" };

        private static void CheckLegacyAssertions()
        {
            Line("  · C. 旧断言新口径（FGJ-M1、M2、M5、M6、M10，底稿 06 §3.3 / M10-BASELINE 第 3 节）：旅程源码不按旧表下标读反应计数、不读反应倍率；四处计数按稳定键");
            string root = F.LocateRepo();
            string dir = root == null ? null : Path.Combine(root, Bots.Replace('/', Path.DirectorySeparatorChar));
            var files = dir != null && Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.cs").Select(p => (Path.GetFileName(p), File.ReadAllText(p, Encoding.UTF8).Replace("\r\n", "\n"))).ToList()
                : new List<(string, string)>();
            List<string> bad = LegacyAssertProblems(files, StableCountFiles);
            Expect(files.Count > 0 && bad.Count == 0, $"旅程源码 {files.Count} 个文件：无旧下标计数、无倍率断言；{string.Join(" / ", StableCountFiles)} 按稳定键读计数" +
                                                      (bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad)));
            string m10 = files.FirstOrDefault(x => x.Item1 == "FgjM10Journey.cs").Item2 ?? string.Empty;
            int literals = files.Where(x => x.Item1.StartsWith("FgjM", StringComparison.Ordinal) && !StableCountFiles.Contains(x.Item1))
                .Sum(x => Regex.Matches(StripComments(x.Item2), "\"reaction_").Count);
            Expect(m10.Contains("ReactionNames") && literals == 0,
                $"反应 ID 字面量只在 FGJ-M1 / M2 / M6 的稳定键常量与 FgjM10Journey.ReactionNames 映射里（其余 FGJ-M* 源码 {literals} 处）");
        }

        // ── D. 回归报告 ─────────────────────────────────────────────────────────

        internal sealed class JourneyRun
        {
            public string Verdict = "MISSING";
            public int Steps;
            public int Total;
            public double Seconds;
            public bool FastForward;
            public string FailLine;
            public readonly HashSet<string> PassedSteps = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly Regex StepHeader = new Regex(@"^\[\s*[\d.]+s\] \[(\d+)/(\d+)\] (\S+)");
        private static readonly Regex VerdictLine = new Regex(@"结论：(PASS|FAIL)（(.*)");
        private static readonly Regex Seconds = new Regex(@"用时 ([\d.]+) 秒");

        /// <summary>解析 unity-journey.sh 的文本报告（纯函数）：步骤头“[k/N] id”之后的 ✓ = 该步通过；末行“结论：PASS / FAIL”。</summary>
        internal static JourneyRun ParseJourneyReport(string text)
        {
            var r = new JourneyRun();
            if (string.IsNullOrEmpty(text))
            {
                return r;
            }
            string cur = null;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                Match h = StepHeader.Match(raw);
                if (h.Success)
                {
                    cur = h.Groups[3].Value;
                    r.Steps = int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture);
                    r.Total = int.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (cur != null && raw.Contains("✓"))
                {
                    r.PassedSteps.Add(cur);
                }
                if (raw.Contains("✗") && r.FailLine == null)
                {
                    string t = raw.Trim().Replace("✗", "×"); // 报告行里的 ✗ 不原样带进自检输出（会被计成一条失败）
                    r.FailLine = t.Length > 160 ? t.Substring(0, 160) : t;
                }
                Match v = VerdictLine.Match(raw);
                if (v.Success)
                {
                    r.Verdict = v.Groups[1].Value;
                    Match s = Seconds.Match(raw);
                    r.Seconds = s.Success ? double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    r.FastForward = raw.Contains("快进开启");
                    if (r.Verdict == "FAIL" && r.FailLine == null)
                    {
                        r.FailLine = v.Groups[2].Value.Length > 160 ? v.Groups[2].Value.Substring(0, 160) : v.Groups[2].Value;
                    }
                }
            }
            return r;
        }

        /// <summary>本轮的旅程报告：文件不存在 = MISSING；修改时间早于本轮开始 = STALE（返回 null，原因写进 state）。</summary>
        private static JourneyRun FreshRun(string id, long packStart, out string state)
        {
            string root = F.LocateRepo();
            string path = root == null ? null : Path.Combine(root, (EvidenceDir + "_journey-" + id + ".txt").Replace('/', Path.DirectorySeparatorChar));
            if (path == null || !File.Exists(path))
            {
                state = "MISSING";
                return null;
            }
            long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds();
            if (mtime < packStart)
            {
                state = "STALE";
                return null;
            }
            JourneyRun r = ParseJourneyReport(File.ReadAllText(path, Encoding.UTF8));
            state = r.Verdict;
            return r;
        }

        internal enum Verdict { NoDiff, Expected, Regression }

        internal static string VerdictName(Verdict v) => v == Verdict.NoDiff ? "无差异" : v == Verdict.Expected ? "预期差异" : "退化";

        /// <summary>分类器（纯函数）：本轮没通过 / 没报告 / 报告陈旧 = 退化（要当场修，不靠差异表放行）；通过时差异表里有登记行 = 预期差异（列行号），
        /// 基线记为不稳定 / 失败而本轮通过 = 预期差异（基线口径），其余 = 无差异。</summary>
        internal static Verdict Classify(string current, string baseline, IReadOnlyList<int> diffLines, out string why, IReadOnlyList<int> openRegressions = null)
        {
            if (current != "PASS")
            {
                why = current == "STALE" ? "本轮没有新报告（报告早于本轮开始）" : current == "MISSING" ? "本轮没有报告" : "本轮失败";
                return Verdict.Regression;
            }
            if (openRegressions != null && openRegressions.Count > 0)
            {
                // 差异表里判定为“退化”、没标“已修”的行：本轮碰巧通过也不放行（FG15-QA-01 修复第 1 轮，审查 P2）。
                why = "差异表判定为退化、没标已修：M10-BASELINE.md:" + string.Join(" / :", openRegressions.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                return Verdict.Regression;
            }
            if (diffLines != null && diffLines.Count > 0)
            {
                why = "差异表登记 M10-BASELINE.md:" + string.Join(" / :", diffLines.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                return Verdict.Expected;
            }
            if (!string.IsNullOrEmpty(baseline) && (baseline.Contains("不稳定") || baseline.Contains("FAIL")))
            {
                why = $"基线记为“{baseline}”（第 2 节），本轮通过";
                return Verdict.Expected;
            }
            why = string.IsNullOrEmpty(baseline) ? "基线没有这一条（M10 之后新增），本轮通过" : $"基线 {baseline}，本轮通过，差异表无登记";
            return string.IsNullOrEmpty(baseline) ? Verdict.Expected : Verdict.NoDiff;
        }

        /// <summary>M10-BASELINE 第 2 节表里某旅程的结论格；第 11 节差异表里提到该名字的行号（旅程 ID 按词边界匹配，“FGJ-M1R～M6R”算全部 R 变体）。</summary>
        internal static void BaselineOf(string[] lines, string name, out string baseline, out List<int> diffLines) => BaselineOf(lines, name, out baseline, out diffLines, out _);

        /// <summary>同上；<paramref name="openRegressions"/> = 提到该名字、“判定”栏写退化且没写已修的行号（不进 <paramref name="diffLines"/>）。</summary>
        internal static void BaselineOf(string[] lines, string name, out string baseline, out List<int> diffLines, out List<int> openRegressions)
        {
            baseline = null;
            diffLines = new List<int>();
            openRegressions = new List<int>();
            int sec2 = Array.FindIndex(lines, l => l.StartsWith("## 2.", StringComparison.Ordinal));
            int sec3 = Array.FindIndex(lines, l => l.StartsWith("## 3.", StringComparison.Ordinal));
            int sec11 = Array.FindIndex(lines, l => l.StartsWith("## 11.", StringComparison.Ordinal));
            if (sec2 >= 0)
            {
                for (int i = sec2; i < (sec3 > sec2 ? sec3 : lines.Length); i++)
                {
                    if (lines[i].StartsWith("| " + name + " |", StringComparison.Ordinal))
                    {
                        baseline = lines[i].Split('|')[2].Trim();
                        break;
                    }
                }
            }
            if (sec11 < 0)
            {
                return;
            }
            var token = new Regex(@"(?<![\w-])" + Regex.Escape(name) + @"(?![\w])");
            bool reverse = ReverseJourneyId.IsMatch(name);
            for (int i = sec11 + 1; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("| ", StringComparison.Ordinal) || lines[i].StartsWith("| 阶段 |", StringComparison.Ordinal) || lines[i].StartsWith("|---", StringComparison.Ordinal))
                {
                    continue;
                }
                string row = lines[i];
                if (token.IsMatch(row) || (reverse && (row.Contains("M1R～M6R") || row.Contains("R 变体"))))
                {
                    string[] cells = row.Split('|');
                    string verdict = cells.Length > 5 ? cells[5] : string.Empty; // | 阶段 | 旅程 / 自检 | 旧结果 | 新结果 | 判定 | 依据 |
                    (IsOpenRegression(verdict) ? openRegressions : diffLines).Add(i + 1);
                }
            }
        }

        // “无退化 / 非退化 / 不算退化 / 不是退化 / 没有退化 / 退化 0”是否定写法，不算未修退化（FG15-QA-01 修复第 2 轮，审查 P0）。
        private static readonly Regex NegatedRegression = new Regex(@"(无|非|不算|不是|没有)退化|退化\s*0(?![\d.])");

        /// <summary>差异表“判定”栏是否为未修退化：去掉否定写法后仍含“退化”，且没写“已修”。</summary>
        internal static bool IsOpenRegression(string verdict) =>
            !string.IsNullOrEmpty(verdict) && NegatedRegression.Replace(verdict, string.Empty).Contains("退化") && !verdict.Contains("已修");

        private static void CheckReport(long packStart)
        {
            // 普通模式也核：真实差异表里，清单每条旅程 / 段都没有被判成未修退化的行（单段跑就能拦住分类器误判与真实未修退化）。
            ParseManifest(ReadRepo(ManifestPath) ?? string.Empty, out List<string> packJourneys, out List<string> packSegments);
            string[] realBase = (ReadRepo(BaselinePath) ?? string.Empty).Split('\n');
            var openRows = new List<string>();
            int diffRows = 0;
            foreach (string name in packJourneys.Concat(packSegments.Where(s => s != SegmentName)))
            {
                BaselineOf(realBase, name, out _, out List<int> diff, out List<int> open);
                diffRows += diff.Count;
                openRows.AddRange(open.Select(x => $"{name}@:{x.ToString(CultureInfo.InvariantCulture)}"));
            }
            Expect(realBase.Length > 1 && packJourneys.Count > 0 && openRows.Count == 0,
                $"真实差异表：清单 {packJourneys.Count} 条旅程、{packSegments.Count - 1} 段没有未修退化行（依据行共 {diffRows} 处）" + (openRows.Count == 0 ? string.Empty : "；未修退化：" + string.Join("、", openRows)));
            if (packStart <= 0)
            {
                Line("  · D. 回归报告：普通模式不写报告（一键回归 bash tools/unity-reverse-pack.sh 跑完旅程后由本段写 " + ReportPath + "）；判定函数由 E 段负向矩阵覆盖");
                return;
            }
            Line("  · D. 回归报告：逐条旅程 / 段对照 M10-BASELINE.md 第 2 节结论与第 11 节差异表，判“无差异 / 预期差异 / 退化”");
            ParseManifest(ReadRepo(ManifestPath), out List<string> journeys, out List<string> segments);
            string[] baseLines = (ReadRepo(BaselinePath) ?? string.Empty).Split('\n');
            var md = new StringBuilder();
            md.AppendLine("# 反向回归包报告（FG15-QA-01）");
            md.AppendLine();
            md.AppendLine($"- 本轮开始：{DateTimeOffset.FromUnixTimeSeconds(packStart).ToLocalTime():yyyy-MM-dd HH:mm:ss}；写报告：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；一键入口 `bash tools/unity-reverse-pack.sh`，清单 `{ManifestPath}`。");
            md.AppendLine("- 口径：渲染管线为 Built-in（FG15-PL-01 URP 合流之前；合流后由 FG15-E2E-01 用同一入口复跑）。旅程由 tools/unity-journeys-parallel.sh 多车道跑，默认 20 倍空等快进；用时只作参考，不作判定（快进与车道抢 CPU 都会改变用时，逐项比较用时以不快进的完整跑为准）。");
            md.AppendLine("- 判定：本轮失败 / 没有报告 / 报告早于本轮 = **退化**（当场查根因修，不靠差异表放行）；通过且差异表（M10-BASELINE 第 11 节）有登记行 = **预期差异**（列行号，内容以该行“判定 / 依据”两栏为准）；基线没有或记为不稳定而本轮通过 = 预期差异（写明基线口径）；其余 = 无差异。");
            md.AppendLine();
            md.AppendLine("## 旅程");
            md.AppendLine();
            md.AppendLine("| 旅程 | 本轮 | 步数 | 用时（秒） | 快进 | 基线（第 2 节） | 判定 | 依据 |");
            md.AppendLine("|---|---|---|---|---|---|---|---|");
            var regress = new List<string>();
            foreach (string id in journeys)
            {
                JourneyRun r = FreshRun(id, packStart, out string state);
                BaselineOf(baseLines, id, out string baseline, out List<int> diff, out List<int> open);
                Verdict v = Classify(r?.Verdict ?? state, baseline, diff, out string why, open);
                if (v == Verdict.Regression)
                {
                    regress.Add($"{id}（{why}{(r?.FailLine == null ? string.Empty : "：" + r.FailLine)}）");
                }
                md.AppendLine($"| {id} | {r?.Verdict ?? state} | {(r == null ? "—" : r.Steps + "/" + r.Total)} | {(r == null ? "—" : r.Seconds.ToString("0.0", CultureInfo.InvariantCulture))} | {(r != null && r.FastForward ? "开" : "关")} | {Esc(baseline ?? "（无）")} | {VerdictName(v)} | {Esc(why + (r?.FailLine == null ? string.Empty : "；" + r.FailLine))} |");
            }
            md.AppendLine();
            md.AppendLine("## 自检段（同一进程本轮结果）");
            md.AppendLine();
            md.AppendLine("| 段 | 本轮 通过 / 失败 / 警告 | 用时（秒） | 判定 | 依据 |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (string seg in segments.Where(s => s != SegmentName))
            {
                CellFrameworkValidate.SegmentTiming t = CellFrameworkValidate.TimingOf(seg);
                string cur = t == null ? "MISSING" : t.Fail == 0 && t.Pass > 0 ? "PASS" : "FAIL";
                BaselineOf(baseLines, seg, out _, out List<int> diff, out List<int> open);
                Verdict v = Classify(cur, "PASS", diff, out string why, open);
                if (v == Verdict.Regression)
                {
                    regress.Add($"{seg}（{why}）");
                }
                md.AppendLine($"| {seg} | {(t == null ? "没跑" : $"{t.Pass} / {t.Fail} / {t.Warn}")} | {(t == null ? "—" : t.Seconds.ToString("0.0", CultureInfo.InvariantCulture))} | {VerdictName(v)} | {Esc(why)} |");
            }
            md.AppendLine();
            md.AppendLine("## IC-REQ-022 覆盖矩阵（格数）");
            md.AppendLine();
            md.AppendLine("| 里程碑 / 阶段 | " + string.Join(" | ", Six) + " |");
            md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", Six.Length)));
            List<Cell> cells = Cells();
            foreach (string m in Milestones)
            {
                md.AppendLine($"| {m} | " + string.Join(" | ", Enumerable.Range(0, Six.Length).Select(i =>
                {
                    int n = cells.Count(c => c.Milestone == m && c.Cat == i);
                    return n > 0 ? n.ToString(CultureInfo.InvariantCulture) : NotApplicable.Any(x => x.Milestone == m && x.Cat == i) ? "不适用" : "空";
                })) + " |");
            }
            md.AppendLine();
            md.AppendLine($"结论：{(regress.Count == 0 ? "PASS" : "FAIL")}（旅程 {journeys.Count} 条、段 {segments.Count - 1} 段；退化 {regress.Count} 条{(regress.Count == 0 ? string.Empty : "：" + string.Join("；", regress))}）");
            string root = F.LocateRepo();
            bool written = false;
            if (root != null)
            {
                string path = Path.Combine(root, ReportPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, md.ToString(), new UTF8Encoding(false));
                written = true;
            }
            Expect(written, $"回归报告已写 {ReportPath}");
            Expect(regress.Count == 0, $"回归包没有退化：旅程 {journeys.Count} 条、段 {segments.Count - 1} 段" + (regress.Count == 0 ? string.Empty : "；退化：" + string.Join("；", regress)));
        }

        private static string Esc(string s) => (s ?? string.Empty).Replace("|", "／").Replace("\n", " ");

        // ── E. 负向矩阵 ─────────────────────────────────────────────────────────

        private static void CheckNegativeMatrix()
        {
            Line("  · E. 负向矩阵：合成输入驱动清单门禁、覆盖空格、格核对、旧口径扫描、报告解析与分类器，每种坏情况都必须被拦下");
            string[] reg = { "SegA", "SegB", SegmentName };
            List<string> ok = ManifestProblems(new[] { "FGJ-M1R" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1", "FGJ-M1R" }, reg, new[] { "SegA" });
            List<string> lostJourney = ManifestProblems(new[] { "FGJ-M1R" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1R", "FGJ-M9R" }, reg, new[] { "SegA" });
            List<string> unknownJourney = ManifestProblems(new[] { "FGJ-M1R", "FGJ-X" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1R" }, reg, new[] { "SegA" });
            List<string> unregistered = ManifestProblems(new[] { "FGJ-M1R" }, new[] { "SegZ", SegmentName }, new[] { "FGJ-M1R" }, reg, new string[0]);
            List<string> lostSeg = ManifestProblems(new[] { "FGJ-M1R" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1R" }, reg, new[] { "SegA", "SegB" });
            List<string> notLast = ManifestProblems(new[] { "FGJ-M1R" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1R" }, new[] { "SegA", SegmentName, "SegB" }, new[] { "SegA" });
            List<string> dup = ManifestProblems(new[] { "FGJ-M1R", "FGJ-M1R" }, new[] { "SegA", SegmentName }, new[] { "FGJ-M1R" }, reg, new[] { "SegA" });
            Expect(ok.Count == 0 && lostJourney.Any(x => x.Contains("FGJ-M9R")) && unknownJourney.Any(x => x.Contains("FGJ-X")) && unregistered.Any(x => x.Contains("SegZ"))
                   && lostSeg.Any(x => x.Contains("SegB")) && notLast.Any(x => x.Contains("最后一段")) && dup.Any(x => x.Contains("2 次")),
                $"清单门禁：正常清单无问题 {ok.Count == 0}；漏登反向旅程 / 清单旅程未登记 / 段未登记 / 漏掉矩阵引用的段 / 本段不在最后 / 重复，各被拦下（{lostJourney.Count}/{unknownJourney.Count}/{unregistered.Count}/{lostSeg.Count}/{notLast.Count}/{dup.Count}）");

            var full = Enumerable.Range(0, Six.Length).Select(i => new Cell { Milestone = "X", Cat = i }).ToList();
            List<string> hole = Holes(full.Where(c => c.Cat != 4), new (string, int, string)[0], new[] { "X" });
            List<string> skipped = Holes(full.Where(c => c.Cat != 4), new[] { ("X", 4, "理由") }, new[] { "X" });
            List<string> extraOnly = Holes(new[] { new Cell { Milestone = "Y", Cat = -1 } }, new (string, int, string)[0], new[] { "Y" });
            Expect(hole.SequenceEqual(new[] { "X·存读档" }) && skipped.Count == 0 && extraOnly.Count == Six.Length,
                $"覆盖空格：缺存读档 → 报“X·存读档”{string.Join("、", hole)}；登记不适用后不报；只有承接格（类别 -1）不算覆盖六类（报 {extraOnly.Count} 格）");
            Expect(CategoryOf("R3 恢复：摧毁尖刺") == 2 && CategoryOf("断链（目标死亡之后再请一波") == 2 && CategoryOf("路径失败（缺关键材料等待") == 5
                   && CategoryOf("暂停与倍速、移出队列进度保留") == 3 && CategoryOf("承接 DEBT-FG5E2E01-05：真实突袭") == -1 && CategoryOf("精英 / 首领不可劫持") == -1
                   && CategoryOf("资源不足（装配站缺材料）") == 0 && CategoryOf("R6 规模 / 地点失败") == 5,
                "类别归一：R 前缀优先；否则按最早出现的关键词（“断链（目标死亡…”归断链）；承接项与非六类条目不计入六类");

            const string src = "Expect(ok, $\"甲 断言 {n}\");\n// 乙 只在注释\nLine(\"丙 小节标题\");\nprivate static void CheckFoo()\n{\n}\n";
            Func<string, string> read = f => f == "Q.cs" ? src : null;
            Func<string, bool> regd = s => s == "SegQ";
            JourneyDef def = new JourneyDef { Id = "J", Steps = new List<JourneyStep> { new JourneyStep { Id = "s1" }, new JourneyStep { Id = "s2" } } };
            Func<string, JourneyDef> jd = id => id == "J" ? def : null;
            var passRun = new JourneyRun { Verdict = "PASS" };
            passRun.PassedSteps.Add("s1");
            passRun.PassedSteps.Add("s2");
            var halfRun = new JourneyRun { Verdict = "FAIL", FailLine = "✗ s2 超时" };
            halfRun.PassedSteps.Add("s1");
            Cell E(string label) => new Cell { Kind = CellKind.Expect, Source = "SegQ", File = "Q.cs", Label = label };
            Cell M(string label) => new Cell { Kind = CellKind.Method, Source = "SegQ", File = "Q.cs", Label = label };
            Cell J(string steps) => new Cell { Kind = CellKind.Journey, Source = "J", Label = steps };
            Cell S(string label) => new Cell { Kind = CellKind.Source, Source = "SegQ", File = "Q.cs", Label = label };
            (int, int)? Ran(int p, int f) => (p, f);
            string P(Cell c, (int, int)? t, JourneyRun run, bool pack) => CellProblem(c, read, regd, _ => t, jd, _ => run, pack, out _);
            bool good = P(E("甲 断言"), Ran(3, 0), null, false) == null && P(M("CheckFoo"), Ran(3, 0), null, true) == null && P(J("s1,s2"), null, passRun, true) == null
                        && P(E("甲 断言"), null, null, false) == null && P(S("丙 小节标题"), Ran(3, 0), null, false) == null;
            var caught = new List<(string What, string Problem)>
            {
                ("标签只在注释里", P(E("乙 只在注释"), Ran(3, 0), null, false)),
                ("标签只在小节标题里（Expect 口径）", P(E("丙 小节标题"), Ran(3, 0), null, false)),
                ("标签只在注释里（源码口径）", P(S("乙 只在注释"), Ran(3, 0), null, false)),
                ("检查方法不存在", P(M("CheckBar"), Ran(3, 0), null, false)),
                ("段没有登记", P(new Cell { Kind = CellKind.Expect, Source = "SegZ", File = "Q.cs", Label = "甲 断言" }, Ran(3, 0), null, false)),
                ("本轮失败 1", P(E("甲 断言"), Ran(3, 1), null, false)),
                ("本轮通过 0", P(E("甲 断言"), Ran(0, 0), null, false)),
                ("一键回归时段没跑", P(E("甲 断言"), null, null, true)),
                ("旅程缺步骤", P(J("s1,s9"), null, passRun, true)),
                ("一键回归时旅程没有新报告", P(J("s1,s2"), null, null, true)),
                ("本轮报告里步骤没打勾", P(J("s1,s2"), null, halfRun, true)),
            };
            List<string> missed = caught.Where(x => x.Problem == null).Select(x => x.What).ToList();
            Expect(good && missed.Count == 0, $"格核对：正常的断言 / 方法 / 旅程 / 源码格通过 {good}；{caught.Count} 种坏情况全部被拦下" + (missed.Count == 0 ? string.Empty : "；漏拦：" + string.Join("、", missed)));

            List<string> legacyBad = LegacyAssertProblems(new[]
            {
                ("A.cs", "int n = site.Kernel.ReactionCountOf(3);"), ("B.cs", "float m = r.ReactionMultiplier;"), ("C.cs", "// site.Kernel.ReactionCountOf(3) 旧写法只在注释"),
            }, new[] { "C.cs" });
            List<string> legacyOk = LegacyAssertProblems(new[] { ("D.cs", "int n = CombatSite.ReactionCountOf(site.Kernel, \"reaction_conduct\");") }, new[] { "D.cs" });
            Expect(legacyBad.Count == 3 && legacyBad.Any(x => x.StartsWith("A.cs")) && legacyBad.Any(x => x.StartsWith("B.cs")) && legacyBad.Any(x => x.StartsWith("C.cs") && x.Contains("稳定键")) && legacyOk.Count == 0,
                $"旧口径扫描：下标计数、倍率、要求稳定键的文件没用稳定键各报一条（{legacyBad.Count}）；注释里的旧写法不算；稳定键写法无问题 {legacyOk.Count == 0}");

            const string rep = "[    0.0s] 旅程 J「x」\n[    0.3s] [1/3] s1 第一步\n[    1.0s]   ✓ 好了\n[    1.1s] [2/3] s2 第二步\n[    9.0s]   ✗ s2 超时：没看到\n[    9.0s] 结论：FAIL（s2 超时，报错 0 条，用时 9.0 秒）\n";
            const string repOk = "[    0.3s] [1/2] s1 a\n[    1.0s]   ✓ ok\n[    1.1s] [2/2] s2 b\n[    2.0s]   ✓ ok\n[    2.0s] 结论：PASS（完成，报错 0 条，用时 2.0 秒；快进开启，倍数 20）\n";
            JourneyRun bad = ParseJourneyReport(rep), fine = ParseJourneyReport(repOk), none = ParseJourneyReport(null);
            Expect(bad.Verdict == "FAIL" && bad.PassedSteps.SetEquals(new[] { "s1" }) && bad.FailLine != null && bad.FailLine.Contains("s2 超时") && bad.Steps == 2
                   && fine.Verdict == "PASS" && fine.PassedSteps.Count == 2 && fine.FastForward && Math.Abs(fine.Seconds - 2.0) < 1e-6 && none.Verdict == "MISSING",
                $"报告解析：失败报告只记已打勾的 s1、失败行“{bad.FailLine}”、停在第 {bad.Steps} 步；通过报告两步打勾、识别快进与用时；空报告 = MISSING");

            Verdict v1 = Classify("FAIL", "PASS（报错 0）", new[] { 400 }, out string w1);
            Verdict v2 = Classify("MISSING", "PASS（报错 0）", new int[0], out _);
            Verdict v3 = Classify("STALE", null, new int[0], out string w3);
            Verdict v4 = Classify("PASS", "PASS（报错 0）", new[] { 394, 550 }, out string w4);
            Verdict v5 = Classify("PASS", "PASS（报错 0）", new int[0], out _);
            Verdict v6 = Classify("PASS", "**不稳定（FG-GAP-149）**", new int[0], out _);
            Verdict v7 = Classify("PASS", null, new int[0], out _);
            Verdict v8 = Classify("PASS", "PASS（报错 0）", new[] { 394 }, out string w8, new[] { 412 });
            Expect(v1 == Verdict.Regression && v2 == Verdict.Regression && v3 == Verdict.Regression && w3.Contains("早于") && v4 == Verdict.Expected && w4.Contains(":394") && w4.Contains(":550")
                   && v5 == Verdict.NoDiff && v6 == Verdict.Expected && v7 == Verdict.Expected && v8 == Verdict.Regression && w8.Contains(":412"),
                $"分类器：失败（即使差异表有登记，{w1}）/ 没报告 / 报告陈旧 = 退化；通过 + 差异表登记 = 预期差异（{w4}）；通过且无登记 = 无差异；基线不稳定或没有而本轮通过 = 预期差异；" +
                $"通过但差异表有未修的退化行 = 退化（{w8}）");

            string[] baseFake =
            {
                "## 2. 旧旅程结论", "| FGJ-M1R | PASS（报错 0） | 111.0 秒 | 同上 |", "| FGJ-M10 | PASS | 1 秒 | x |", "## 3. 清单", "## 11. 差异表",
                "| 阶段 | 旅程 / 自检 | 旧结果 | 新结果 | 判定 | 依据 |", "|---|---|---|---|---|---|",
                "| 甲 | FGJ-M1 drive_fire | a | b | 预期差异 | x |", "| 乙 | FGJ-M1R～M6R 复跑 | a | b | 预期差异 | x |", "| 丙 | FGJ-M10 op_fight | a | b | 预期差异 | x |",
                "| 丁 | FGJ-M10 op_end | a | b | 退化（待修） | x |", "| 戊 | FGJ-M10 op_x | a | b | 退化 → 已修 | x |",
                // 否定写法（真实差异表里出现过）：都不是未修退化。
                "| 己 | FGJ-M10 op_y | a | b | 预期差异（无退化） | x |", "| 庚 | FGJ-M10 op_z | a | b | 无退化（新增行为只在真物质斑块上出现） | x |",
                "| 辛 | FGJ-M10 门禁 | a | b | 预期差异（门禁收紧，非退化） | x |", "| 壬 | FGJ-M10 满载 | a | b | 预期差异（LANDING 6.3 不算退化） | x |",
                "| 癸 | FGJ-M10 汇总 | a | b | 预期差异；产品退化 0 | x |", "| 子 | FGJ-M10 op_w | a | b | 预期差异；另有退化（待查） | x |",
                "| 丑 | R 变体 复跑 | a | b | 预期差异（无退化） | x |",
            };
            BaselineOf(baseFake, "FGJ-M1R", out string b1, out List<int> d1, out List<int> o1);
            BaselineOf(baseFake, "FGJ-M1", out string b2, out List<int> d2);
            BaselineOf(baseFake, "FGJ-M6R", out string b3, out List<int> d3, out List<int> o3);
            Expect(b1 == "PASS（报错 0）" && d1.SequenceEqual(new[] { 9, 19 }) && o1.Count == 0 && b2 == null && d2.SequenceEqual(new[] { 8 }) && b3 == null && d3.SequenceEqual(new[] { 9, 19 }) && o3.Count == 0,
                $"基线读取：第 2 节结论格（FGJ-M1R = {b1}）；差异表按词边界匹配（FGJ-M1 不误配 FGJ-M10 / FGJ-M1R 行：{string.Join(",", d2)}），“M1R～M6R”“R 变体”算全部 R 变体（FGJ-M6R：{string.Join(",", d3)}，“预期差异（无退化）”不算未修退化 {o3.Count == 0}）");
            BaselineOf(baseFake, "FGJ-M10", out _, out List<int> d4, out List<int> o4);
            Expect(d4.SequenceEqual(new[] { 10, 12, 13, 14, 15, 16, 17 }) && o4.SequenceEqual(new[] { 11, 18 }),
                $"判定栏：写“退化”没写“已修”的行单列为未修退化（{string.Join(",", o4)}）；“退化 → 已修”、预期差异行与“无退化 / 非退化 / 不算退化 / 退化 0”否定写法进依据（{string.Join(",", d4)}）");
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

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
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
