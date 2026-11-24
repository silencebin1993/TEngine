using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-QA-02 缺口清零与验收矩阵（FG00 第 8 节、00 契约 IC-REQ-020）。读真实文档与登记册，不读本地证据：
    /// A 需求覆盖：FGxx 文档里定义的每条 FGR 都在 production/qa/acc/matrix/FGR-coverage.md（tools/fg_req_coverage.py 生成）里有一行，没有“待核”；
    ///   已映射 / 人工映射 / 流程类 / 约束类 / 测试基础设施类：引用的代码与测试文件都存在，测试至少一处落在全量自检登记的段、旅程机器人或冒烟里；
    ///   被取代的 FGR-FW-040～042、FGT-FW-003 标“已由 FG19 取代”并指向已覆盖的 FGR-EMG / FGT-EMG；FGR-EMG 全部覆盖；
    ///   未来阶段 / 待用户 / 承接未完成卡 写明去处（承接卡在看板上没完成；引用的 FG-GAP / DEBT 在登记册里）。
    /// B 登记册清零口径：每条要么 Closed，要么承接一张未完成的卡，要么标待用户 / 未来阶段；“承接卡全完成却没关”必须为 0。
    ///   本卡（<see cref="SelfCard"/>）按已完成判：承接本卡的条目必须已关或改承接后续卡，看板改 Completed 前后结果一致（修复轮 1）。
    /// C 本卡登记与总控补充 (11)⑥⑦ 的文字改动（FG-GAP-148 改回 Open、DEBT-FG15BAL01-05 最终口径、E2E-01 首领时长、ADR-BAL-002 旧档口径、DEBT-FG15QA02-01 与 FG-GAP-272 写进承接卡），
    ///   承接本卡的 FG-GAP-162 / DEBT-FG6DEF05-05 / DEBT-FG8OUT01-02 的处理（功能本身的行为断言在 FgClarityFactionSelfCheck B7b）。
    /// N 负向矩阵：合成输入驱动同一组纯函数（新增需求没映射、测试不在全量链、文件不存在、取代目标没覆盖、承接卡已完成、登记册陈旧条目、承接本卡却没关、状态里顺带提到本卡）。
    /// 矩阵的“已映射”= 需求 ID 追溯，不等于断言逐条覆盖；逐卡核断言由 FG15-ACC-01 做。
    /// 单段：bash tools/unity-validate.sh --segment FgReqCoverageSelfCheck。
    /// </summary>
    public static class FgReqCoverageSelfCheck
    {
        public const string SegmentName = "FgReqCoverageSelfCheck";
        /// <summary>本卡：登记册 / 矩阵清点里按已完成判（不能靠“本卡还没完成”让承接本卡的条目过关）。与 tools/fg_req_coverage.py 的 SELF 一致。</summary>
        internal const string SelfCard = "FG15-QA-02";
        private const string Matrix = "production/qa/acc/matrix/FGR-coverage.md";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";
        private const string Board = "production/design/full-game/FG-STORY-BOARD.md";
        private const string Cards = "production/design/full-game/FG-STORY-CARDS.md";
        private const string DocsDir = "TEngine/UnityProject/DesignDocs/fullgame";
        private const string UnityRoot = "TEngine/UnityProject/";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("QA/自检/需求覆盖与缺口清零")]
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
            Line("\n[需求覆盖] FG15-QA-02：FGR 需求覆盖矩阵（IC-REQ-020）、登记册清零口径、本卡登记与文字改动、负向矩阵");
            Step(CheckCoverage);
            Step(CheckRegister);
            Step(CheckEdits);
            Step(CheckNegative);
            Line($"  · [需求覆盖] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 纯函数（正式检查与负向矩阵共用）──────────────────────────────────────

        private static readonly Regex DefRe = new Regex(@"^(?:#{1,6}\s*|\*\*|-\s+\*\*|\|\s*\*{0,2}|\d+\.\s+\*\*)(FGR-[A-Z0-9]+-\d{3}[a-z]?)");
        private static readonly Regex StoryRe = new Regex(@"\b(?:FG\d+[A-Z]?|EMG\d|EMGR)-[A-Z0-9]+-\d+\b");
        private static readonly Regex ReqRe = new Regex(@"FG[RT]-[A-Z0-9]+-\d{3}[a-z]?");
        private static readonly Regex EntryRe = new Regex(@"\b(?:FG-GAP-\d{3}|DEBT-[A-Z0-9]+-\d{2})\b");
        internal static readonly string[] Covered = { "已映射", "已映射（人工）", "流程类", "约束类", "测试基础设施类" };
        internal static readonly string[] Routed = { "未来阶段", "待用户", "承接未完成卡", "规格非目标", "流程类（人工审查守门）" };
        private static readonly string[] UserMark = { "待用户", "用户择时", "用户拍板", "用户决定", "用户确认", "用户试玩", "用户复核", "美术", "音频", "真人试玩", "表现泳道" };
        private static readonly string[] FutureMark = { "FG18-", "FG-M7S" };
        private static readonly string[] ClosedPrefix = { "Closed", "已关闭", "作废", "已完成" };

        internal sealed class Row
        {
            public string Id, Kind, Note;
            public List<string> Code = new List<string>(), Test = new List<string>();
        }

        /// <summary>文档里定义的 FGR（标题 / 粗体 / 表格首列第一次出现）。</summary>
        internal static HashSet<string> DefinedIds(IEnumerable<string> docs)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string doc in docs)
            {
                foreach (string raw in doc.Split('\n'))
                {
                    Match m = DefRe.Match(raw.Trim());
                    if (m.Success)
                    {
                        ids.Add(m.Groups[1].Value);
                    }
                }
            }
            return ids;
        }

        internal static Dictionary<string, Row> ParseMatrix(string text)
        {
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                if (!raw.StartsWith("| FGR-", StringComparison.Ordinal) && !raw.StartsWith("| FGT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] c = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                if (c.Length < 8)
                {
                    continue;
                }
                rows[c[0]] = new Row { Id = c[0], Code = Refs(c[4]), Test = Refs(c[5]), Kind = c[6], Note = c[7] };
            }
            return rows;
        }

        private static List<string> Refs(string cell) =>
            cell.Split(new[] { "<br>" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                .Where(x => x != "—" && !x.StartsWith("（另", StringComparison.Ordinal)).ToList();

        private static string PathOf(string reference) => Regex.Replace(reference, @":\d+$", string.Empty);

        /// <summary>测试引用是否在自动链里：全量自检登记的段（文件名点号前即段类名）、旅程机器人、冒烟、全量入口本身。</summary>
        internal static bool InChain(string reference, ICollection<string> segments)
        {
            string p = PathOf(reference);
            string name = Path.GetFileName(p);
            string cls = name.Split('.')[0];
            return segments.Contains(cls) || p.Contains("/JourneyBots/") || cls == "PlaySmokeTest" || cls == "CellFrameworkValidate";
        }

        internal static List<string> CoverageProblems(ICollection<string> defined, IReadOnlyDictionary<string, Row> rows, ICollection<string> segments,
                                                      Func<string, bool> exists, IReadOnlyDictionary<string, string> board, ICollection<string> registerIds)
        {
            var bad = new List<string>();
            bad.AddRange(defined.Where(id => !rows.ContainsKey(id)).OrderBy(x => x).Select(id => "未映射（矩阵里没有这一行）：" + id));
            bad.AddRange(rows.Keys.Where(id => id.StartsWith("FGR-", StringComparison.Ordinal) && !defined.Contains(id)).OrderBy(x => x).Select(id => "矩阵过期（文档里已没有）：" + id));
            foreach (Row r in rows.Values)
            {
                if (Covered.Contains(r.Kind))
                {
                    if (r.Code.Count == 0 || r.Test.Count == 0)
                    {
                        bad.Add($"{r.Id}（{r.Kind}）缺代码或测试映射");
                        continue;
                    }
                    foreach (string x in r.Code.Concat(r.Test).Where(x => x.Contains("/")))
                    {
                        if (!exists(PathOf(x)))
                        {
                            bad.Add($"{r.Id} 引用的文件不存在：{x}");
                        }
                    }
                    if (!r.Test.Any(t => InChain(t, segments)))
                    {
                        bad.Add($"{r.Id} 的测试不在全量自检 / 旅程 / 冒烟链里：{string.Join("、", r.Test)}");
                    }
                }
                else if (r.Kind == "已由 FG19 取代")
                {
                    var targets = ReqRe.Matches(r.Note ?? string.Empty).Cast<Match>().Select(m => m.Value).ToList();
                    if (targets.Count == 0)
                    {
                        bad.Add($"{r.Id} 标了取代却没写取代它的需求");
                    }
                    foreach (string t in targets.Where(t => t.StartsWith("FGR-", StringComparison.Ordinal)))
                    {
                        if (!rows.TryGetValue(t, out Row tr) || !Covered.Contains(tr.Kind))
                        {
                            bad.Add($"{r.Id} 的取代目标 {t} 没有覆盖（{tr?.Kind ?? "不在矩阵"}）");
                        }
                    }
                }
                else if (Routed.Contains(r.Kind))
                {
                    if (string.IsNullOrWhiteSpace(r.Note))
                    {
                        bad.Add($"{r.Id}（{r.Kind}）没写去处");
                    }
                    if (r.Kind == "承接未完成卡")
                    {
                        var open = StoryRe.Matches(r.Note ?? string.Empty).Cast<Match>().Select(m => m.Value)
                            .Where(s => s != SelfCard && board.TryGetValue(s, out string st) && st != "Completed").ToList();
                        if (open.Count == 0)
                        {
                            bad.Add($"{r.Id} 写“承接未完成卡”，但说明里没有看板上未完成的卡");
                        }
                    }
                    foreach (Match m in EntryRe.Matches(r.Note ?? string.Empty))
                    {
                        if (!registerIds.Contains(m.Value))
                        {
                            bad.Add($"{r.Id} 引用的登记条目不在登记册：{m.Value}");
                        }
                    }
                }
                else
                {
                    bad.Add($"{r.Id} 判定“{r.Kind}”不是允许的取值（待核 = 还没映射）");
                }
            }
            return bad;
        }

        internal static Dictionary<string, string> ParseBoard(string text)
        {
            var board = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string[] c = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                if (c.Length >= 5 && Regex.IsMatch(c[0], @"^[\d.]+$"))
                {
                    board[c[1].Split(' ')[0]] = c[3];
                }
            }
            return board;
        }

        /// <summary>登记册每条的去处：Closed / 承接未完成卡 / 未来阶段 / 待用户 / 需处理（返回 编号 → 类别）。</summary>
        internal static Dictionary<string, string> ClassifyRegister(string text, IReadOnlyDictionary<string, string> board)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] c = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                if (c[0] == "DEBT-ID" || c.Length < 5)
                {
                    continue;
                }
                bool gap = c[0].StartsWith("FG-GAP", StringComparison.Ordinal);
                string st = c[c.Length - 1].TrimStart('*');
                string owner = gap ? string.Join(" ", c.Skip(4).Take(c.Length - 5)) : string.Join(" ", c.Skip(Math.Max(0, c.Length - 4)).Take(3));
                string cat;
                if (ClosedPrefix.Any(p => st.StartsWith(p, StringComparison.Ordinal)))
                {
                    cat = "Closed";
                }
                else if (StoryRe.Matches(owner + " " + st).Cast<Match>().Any(m => m.Value != SelfCard && board.TryGetValue(m.Value, out string s) && s != "Completed" && s != "Done"))
                {
                    cat = "承接未完成卡";
                }
                else if (FutureMark.Any(k => owner.Contains(k) || st.Contains(k)))
                {
                    cat = "未来阶段";
                }
                else if (UserMark.Any(k => owner.Contains(k) || st.Contains(k)))
                {
                    cat = "待用户";
                }
                else
                {
                    cat = "需处理";
                }
                map[c[0]] = cat;
            }
            return map;
        }

        // ── A 需求覆盖 ─────────────────────────────────────────────────────────

        private static void CheckCoverage()
        {
            Line("  · A. 需求覆盖（IC-REQ-020）：文档里每条 FGR 都在矩阵里，引用的代码 / 测试存在、测试在全量链，被取代项指向已覆盖的 FG19 需求，其余写明去处");
            string root = F.LocateRepo();
            string[] docs = Directory.GetFiles(Path.Combine(root, DocsDir), "*.md").Select(p => File.ReadAllText(p, Encoding.UTF8).Replace("\r\n", "\n")).ToArray();
            HashSet<string> defined = DefinedIds(docs);
            Dictionary<string, Row> rows = ParseMatrix(ReadRepo(Matrix));
            var segs = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            Dictionary<string, string> board = ParseBoard(ReadRepo(Board));
            Dictionary<string, string> reg = ClassifyRegister(ReadRepo(Register), board);
            List<string> bad = CoverageProblems(defined, rows, segs, Exists, board, reg.Keys);
            int emg = defined.Count(id => id.StartsWith("FGR-EMG-", StringComparison.Ordinal));
            int emgCovered = defined.Count(id => id.StartsWith("FGR-EMG-", StringComparison.Ordinal) && rows.TryGetValue(id, out Row r) && Covered.Contains(r.Kind));
            int emgFuture = defined.Count(id => id.StartsWith("FGR-EMG-", StringComparison.Ordinal) && rows.TryGetValue(id, out Row r) && r.Kind == "未来阶段");
            Expect(defined.Count >= 480 && rows.Count >= defined.Count, $"文档定义 FGR {defined.Count} 条（解析没有漏），矩阵 {rows.Count} 行（含被取代的测试 ID）");
            Expect(bad.Count == 0, bad.Count == 0
                ? $"全部 {defined.Count} 条有去处：" + string.Join("、", rows.Values.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))
                : $"覆盖问题 {bad.Count} 条：" + string.Join("；", bad.Take(12)));
            string[] sup = { "FGR-FW-040", "FGR-FW-041", "FGR-FW-042", "FGT-FW-003" };
            var supBad = sup.Where(id => !rows.TryGetValue(id, out Row r) || r.Kind != "已由 FG19 取代" || !ReqRe.IsMatch(r.Note ?? string.Empty)).ToList();
            Expect(supBad.Count == 0 && rows.TryGetValue("FGT-FW-003", out Row t3) && t3.Note.Contains("FGT-EMG-030") && t3.Note.Contains("FGT-EMG-070"),
                "被取代项 FGR-FW-040～042、FGT-FW-003 都标“已由 FG19 取代”并指向取代它的 FG19 需求（FGT-FW-003 → FGT-EMG-030 / 070），没有删除" + (supBad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", supBad)));
            Expect(emg >= 100 && emgCovered + emgFuture == emg && emgFuture == 1,
                $"FGR-EMG {emg} 条：已覆盖 {emgCovered}、未来阶段 {emgFuture}（只有 FGR-EMG-151 冻结桥随 FG18 水面地形）");
        }

        // ── B 登记册清零口径 ─────────────────────────────────────────────────────

        private static void CheckRegister()
        {
            Line("  · B. 登记册：每条 Closed / 承接未完成卡 / 未来阶段 / 待用户，“需处理”为 0；生成的清点表与登记册一致");
            Dictionary<string, string> board = ParseBoard(ReadRepo(Board));
            Dictionary<string, string> reg = ClassifyRegister(ReadRepo(Register), board);
            var stale = reg.Where(kv => kv.Value == "需处理").Select(kv => kv.Key).ToList();
            string summary = string.Join("、", reg.GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            Expect(reg.Count > 1000 && stale.Count == 0, stale.Count == 0
                ? $"登记册 {reg.Count} 条全部有去处（{summary}）"
                : $"承接卡全部完成却没关 / 没有去处的 {stale.Count} 条：{string.Join("、", stale.Take(20))}");
            string status = ReadRepo("production/qa/acc/QA-02-register-status.md") ?? string.Empty;
            Match total = Regex.Match(status, @"\| \*\*合计\*\* \| (\d+) \|");
            Expect(total.Success && int.Parse(total.Groups[1].Value) == reg.Count && !status.Contains("## 需处理"),
                $"清点表 QA-02-register-status.md 合计 {(total.Success ? total.Groups[1].Value : "?")} = 登记册 {reg.Count} 条、没有“需处理”一节（登记册改了要重跑 python tools/fg_req_coverage.py）");
        }

        // ── C 本卡登记与文字改动 ───────────────────────────────────────────────

        private static string CardBody(string cards, string id)
        {
            int at = cards.IndexOf("### " + id + " ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cards.IndexOf("\n### ", at + 10, StringComparison.Ordinal);
            return at < 0 ? string.Empty : end < 0 ? cards.Substring(at) : cards.Substring(at, end - at);
        }

        private static string RowOf(string register, string id)
        {
            foreach (string raw in register.Split('\n'))
            {
                if (raw.StartsWith("| " + id + " |", StringComparison.Ordinal))
                {
                    return raw;
                }
            }
            return null;
        }

        private static string StatusOf(string row) => row == null ? null : row.Trim().Trim('|').Split('|').Last().Trim();

        private static void CheckEdits()
        {
            Line("  · C. 本卡登记与总控补充 (11)⑥⑦：FG-GAP-148 改回 Open、DEBT-FG15BAL01-05 最终口径、E2E-01 首领时长、ADR-BAL-002 旧档口径、新登记写进承接卡正文");
            string register = ReadRepo(Register) ?? string.Empty;
            string cards = ReadRepo(Cards) ?? string.Empty;
            string s148 = StatusOf(RowOf(register, "FG-GAP-148"));
            Expect(s148 != null && s148.StartsWith("Open（数值已调，时长缺口转 DEBT-FG15BAL01-02）", StringComparison.Ordinal), $"FG-GAP-148 状态以“Open（数值已调，时长缺口转 DEBT-FG15BAL01-02）”开头（{s148?.Substring(0, Math.Min(30, s148.Length))}）");
            string s05 = StatusOf(RowOf(register, "DEBT-FG15BAL01-05")) ?? string.Empty;
            Expect(s05.Contains("1.538 ms") && s05.Contains("2.550 ms") && s05.Contains("最终口径"), "DEBT-FG15BAL01-05 追加了最终复验合跑读数（找目标 1.538 ms、推荐配置 p99 2.550 ms）并写明哪组是最终口径");
            string e2e = CardBody(cards, "FG15-E2E-01");
            Expect(!e2e.Contains("1.0～3.4") && e2e.Contains("寂听主脑 1.0～1.5、铸造主核心 0.7～1.8 游戏分"), "FG15-E2E-01 承接行的首领时长与登记册一致（寂听主脑 1.0～1.5、铸造主核心 0.7～1.8 游戏分）");
            string adrDir = Path.Combine(F.LocateRepo(), "production", "design", "full-game", "adr");
            string adr = Directory.GetFiles(adrDir, "ADR-BAL-002*.md").Select(p => File.ReadAllText(p, Encoding.UTF8)).FirstOrDefault() ?? string.Empty;
            Expect(adr.Contains("只作用于新生成的机器") && adr.Contains("旧档里已有的机器保持原耐久") && adr.Contains("不做迁移"), "ADR-BAL-002 写明出厂耐久 100 → 800 只作用于新生成的机器、旧档已有机器保持原耐久、不做迁移");
            string debt = RowOf(register, "DEBT-FG15QA02-01");
            string gap = RowOf(register, "FG-GAP-272");
            Expect(debt != null && debt.Contains("HomeValleyWorkOrders") && debt.Contains("唤醒钩子") && debt.Contains("FG15-ACC-05") && CardBody(cards, "FG15-ACC-05").Contains("DEBT-FG15QA02-01"),
                "DEBT-FG15QA02-01（等空间返还单移出 HomeValleyWorkOrders 逐步遍历、写工单 / 地面物处加唤醒钩子）登记并写进 FG15-ACC-05 正文承接行");
            Expect(gap != null && gap.Contains("FGR-EVT-060") && gap.Contains("FGR-EVT-070") && CardBody(cards, "FG15-ACC-02").Contains("FG-GAP-272")
                   && new[] { "DEBT-FG0UX01-12", "DEBT-FG4ECO01-01", "DEBT-FG4ECO03-07", "DEBT-EMG3CHS01-03" }.All(id => CardBody(cards, "FG15-ACC-02").Contains(id)),
                "FG-GAP-272（可选事件 EVT-060 / 070 无卡）与本卡改承接的 4 条都写进 FG15-ACC-02 正文承接行");
            string s162 = StatusOf(RowOf(register, "FG-GAP-162")) ?? string.Empty;
            string def05 = StatusOf(RowOf(register, "DEBT-FG6DEF05-05")) ?? string.Empty;
            string act02 = StatusOf(RowOf(register, "DEBT-FG9ACT101-02")) ?? string.Empty;
            string out02 = RowOf(register, "DEBT-FG8OUT01-02") ?? string.Empty;
            Expect(s162.StartsWith("Closed", StringComparison.Ordinal) && s162.Contains("FgClarityFactionSelfCheck")
                   && def05.StartsWith("Closed", StringComparison.Ordinal) && act02.Contains("DEBT-FG6DEF05-05")
                   && out02.Contains("FG15-ACC-02") && CardBody(cards, "FG15-ACC-02").Contains("DEBT-FG8OUT01-02"),
                "承接本卡的 3 条（规则 (9)③）：FG-GAP-162 做掉关闭（统计面板敌方状态效果，FgClarityFactionSelfCheck B7b）；DEBT-FG6DEF05-05 复核关闭、余项并入 DEBT-FG9ACT101-02；"
                + "DEBT-FG8OUT01-02（按前哨站记产量要改账本结构与存档）改承接 FG15-ACC-02 并写进该卡正文");
        }

        // ── N 负向矩阵 ─────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            Line("  · N. 负向矩阵（合成输入）：新增需求没映射、测试不在全量链、文件不存在、取代目标没覆盖、承接卡已完成、待核、登记册陈旧条目");
            var segs = new HashSet<string>(StringComparer.Ordinal) { "FgOkSelfCheck" };
            var board = new Dictionary<string, string>(StringComparer.Ordinal) { ["FG1-A-01"] = "Completed", ["FG15-Z-01"] = "Pending" };
            var reg = new HashSet<string>(StringComparer.Ordinal) { "FG-GAP-001" };
            Func<string, bool> exists = p => !p.Contains("Missing");
            string docs = "**FGR-AAA-001 甲**\n### FGR-AAA-002 乙\n| FGR-AAA-003 | 丙 |\n引用 FGR-AAA-009 不算定义\n";
            HashSet<string> defined = DefinedIds(new[] { docs });
            string head = "| 需求 | 标题 | 定义 | 承接卡 | 代码 | 自动测试 | 判定 | 说明 |\n";
            string ok = head
                        + "| FGR-AAA-001 | 甲 | x | — | Assets/A.cs:1 | Assets/Editor/QA/FgOkSelfCheck.cs:2 | 已映射 |  |\n"
                        + "| FGR-AAA-002 | 乙 | x | — | — | — | 已由 FG19 取代 | → FGR-AAA-001 |\n"
                        + "| FGR-AAA-003 | 丙 | x | — | — | — | 承接未完成卡 | FG15-Z-01 交付 |\n";
            List<string> Problems(string m, ICollection<string> d = null, IReadOnlyDictionary<string, string> b = null) => CoverageProblems(d ?? defined, ParseMatrix(m), segs, exists, b ?? board, reg);
            Expect(defined.SetEquals(new[] { "FGR-AAA-001", "FGR-AAA-002", "FGR-AAA-003" }), "N0 定义解析：标题 / 粗体 / 表格首列算定义，正文引用不算");
            Expect(Problems(ok).Count == 0, "N0 正向对照：已映射、取代、承接未完成卡三种合规行 → 0 个问题");
            var plus = new HashSet<string>(defined) { "FGR-AAA-004" };
            Expect(Problems(ok, plus).Any(x => x.Contains("未映射") && x.Contains("FGR-AAA-004")), "N1 文档新增一条 FGR、矩阵没跟上 → 报“未映射”");
            Expect(Problems(ok.Replace("FgOkSelfCheck.cs:2", "FgNoSuchSelfCheck.cs:2")).Any(x => x.Contains("不在全量自检")), "N2 测试指向没登记在全量自检的段 → 报不在全量链");
            Expect(Problems(ok.Replace("Assets/A.cs:1", "Assets/Missing.cs:1")).Any(x => x.Contains("文件不存在")), "N3 代码引用的文件不存在 → 报出");
            Expect(Problems(ok.Replace("| 已映射 |  |", "| 待用户 | 用户定 |")).Any(x => x.Contains("取代目标")), "N4 取代目标本身没覆盖（只是待用户）→ 报取代目标没覆盖");
            Expect(Problems(ok.Replace("FG15-Z-01 交付", "FG1-A-01 交付")).Any(x => x.Contains("承接未完成卡")), "N5 “承接未完成卡”写的是已完成的卡 → 报出");
            Expect(Problems(ok.Replace("| 已映射 |  |", "| 待核（未映射） |  |")).Any(x => x.Contains("不是允许的取值")), "N6 留着“待核”行 → 报出");
            Expect(Problems(ok + "| FGR-AAA-003x | 丁 | x | — | — | — | 待用户 | FG-GAP-999 |\n").Any(x => x.Contains("矩阵过期") || x.Contains("不在登记册")), "N7 说明引用登记册里没有的条目 / 文档里已删的需求 → 报出");
            string regText = "| FG-GAP-001 | a | b | c | FG1-A-01 | M1 | Open |\n"
                             + "| FG-GAP-002 | a | b | c | FG15-Z-01 | M15 | Open |\n"
                             + "| FG-GAP-003 | a | b | c | 美术批次 | M15 | Open |\n"
                             + "| FG-GAP-004 | a | b | c | FG1-A-01 | M1 | Closed（FG1-A-01） |\n"
                             + "| DEBT-X01-01 | s | a | b | c | FG18-SEA-01 | 开工时 | 段 | Open |\n"
                             + "| FG-GAP-005 | a | b | c | " + SelfCard + " | M15 | Open |\n"
                             + "| DEBT-X01-02 | s | a | b | c | FG18-SEA-01 | 开工时 | 段 | Open（" + SelfCard + " 补状态列） |\n";
            Dictionary<string, string> cls = ClassifyRegister(regText, board);
            Expect(cls["FG-GAP-001"] == "需处理" && cls["FG-GAP-002"] == "承接未完成卡" && cls["FG-GAP-003"] == "待用户" && cls["FG-GAP-004"] == "Closed" && cls["DEBT-X01-01"] == "未来阶段",
                $"N8 登记册：承接卡已完成却 Open → 需处理；承接未完成卡 / 美术 / FG18 / Closed 各归其类（{string.Join("、", cls.Select(kv => kv.Key + "=" + kv.Value))}）");
            var selfBoard = new Dictionary<string, string>(board, StringComparer.Ordinal) { [SelfCard] = "InProgress" };
            Dictionary<string, string> selfCls = ClassifyRegister(regText, selfBoard);
            Expect(selfCls["FG-GAP-005"] == "需处理" && selfCls["DEBT-X01-02"] == "未来阶段",
                $"N9 承接的是本卡（看板上仍 InProgress）、条目 Open → 仍报需处理（不能靠本卡没完成过关，{selfCls["FG-GAP-005"]}）；状态里顺带提到本卡的 FG18 条目归未来阶段（{selfCls["DEBT-X01-02"]}）");
            Expect(Problems(ok.Replace("FG15-Z-01 交付", SelfCard + " 交付"), null, selfBoard).Any(x => x.Contains("承接未完成卡")),
                "N10 矩阵行写“承接未完成卡”却只指向本卡 → 报出（本卡按已完成判）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static bool Exists(string relative)
        {
            string root = F.LocateRepo();
            string rel = relative.StartsWith("Assets/", StringComparison.Ordinal) ? UnityRoot + relative : relative;
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) || Directory.Exists(p);
        }

        private static string ReadRepo(string relative)
        {
            string root = F.LocateRepo();
            string path = root == null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
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
            Debug.LogWarning("[需求覆盖] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
