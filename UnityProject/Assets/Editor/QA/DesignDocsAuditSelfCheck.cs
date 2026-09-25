using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-DOC-01 的自动验收：GDD 0.2 合并、Demo 文档归档冻结、README 必读顺序、TERM-MIGRATION 0.2 名表与题材审计词表、
    /// Demo 回归引用、设计版本切换。读的是仓库里真实的文档（影子工程从上级目录找到仓库根），不是字段或常量存在性。
    /// 每条检查都是纯函数（输入文本，输出问题列表）；负向矩阵把同一批函数喂进篡改过的副本，确认坏掉时真的报错。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（tools/unity-validate.sh 默认跑它）。
    /// </summary>
    public static class DesignDocsAuditSelfCheck
    {
        private const string FrozenMark = "Demo 版本冻结（0.1）";
        private const string ArchivedGddName = "ProjectA_GDD_0.1_Demo.md";
        private const string BioGddName = "ProjectA_GDD_bio-mechanical_archived-2026-09-18.md";
        private const string DebtSectionHeading = "## 延后项登记（DEBT）";

        // FG-STORY-CARDS FG0-DOC-01 验收："Grep 旧语义（AI-safe、关键局部接管、连续直控计入暴露）只出现在归档里"。
        internal static readonly Regex OldSemantics = new Regex("AI-safe|关键局部接管|连续直控计入暴露");
        private static readonly Regex LinkPattern = new Regex(@"\]\(([^)\s]+)\)");
        private static readonly Regex DemoIdPattern = new Regex(@"\b(?:ERD|AC)-[A-Z]+-\d{3}\b");
        private static readonly Regex BacktickWord = new Regex("`([^`]+)`");

        // 历史技术参考（detailed/01～05、migration/）里的"GDD §x"——含"§7、§16.1""§8–§11"这类列举。
        private static readonly Regex HistoricalGddRef = new Regex(@"GDD ?§(\d+(?:\.\d+)?)((?:[、/–～-]§?\d+(?:\.\d+)?)*)");

        // 每个会话自动加载的入口与规则文件（相对仓库根）。它们不归 Story 工人改，所以只审计"仍指向 Demo 的行有没有 DEBT 承接"。
        internal static readonly string[] EntryPointFiles =
        {
            "CLAUDE.md", "AGENTS.md", "TEngine/UnityProject/CLAUDE.md", "TEngine/UnityProject/AGENTS.md",
            ".claude/rules/projecta-spec-completeness.md", ".claude/rules/projecta-fullgame.md", ".claude/rules/design-versioning.md",
            "production/session-state/DIGEST.md",
        };

        // 把阶段 / 需求 / 验收 / 队列权威指向 Demo 0.1，或仍是 FG 开启前门禁原文的写法。
        internal static readonly Regex DemoAuthority = new Regex(
            @"阶段 ?[=：] ?`?(?:TEngine/UnityProject/DesignDocs/)?ProjectA_Milestones\.md" +
            @"|逐项需求/验收 = `production/design/earth-reclamation/`" +
            @"|验收以 `production/design/earth-reclamation/` 为准" +
            @"|先在 `production/design/earth-reclamation/REQUIREMENT-TO-PLAYABLE-TRACE\.md`" +
            @"|队列以 production/design/earth-reclamation/STORY-BOARD\.md 为准" +
            @"|用户明确宣布开启 FG-M0 之前" +
            @"|合并 GDD 时把脚本 `DEFAULT_MAJOR` 改为");

        // 同一行明确写了"冻结 / 0.1 / Demo 回归"的，是在描述 Demo 的冻结地位，不是把它当现行权威。
        private static readonly Regex FrozenContext = new Regex(@"冻结|0\.1|Demo 回归");

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：设计文档 0.2 合并与 Demo 归档")]
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

        /// <summary>把结果追加到 <paramref name="report"/>，返回失败条数。</summary>
        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[文档] GDD 0.2 合并与 Demo 文档归档（FG0-DOC-01）");

            Repo repo = Repo.Locate(Directory.GetCurrentDirectory());
            if (repo == null)
            {
                Fail("找不到仓库根（向上 6 层内没有同时含 production/design/full-game 与 TEngine/UnityProject/DesignDocs 的目录），文档审计无法进行");
                return _fail;
            }

            CheckGddVersions(repo);
            CheckDecisionsMerged(repo);
            CheckChangeListCheckedOff(repo);
            CheckOldSemanticsOnlyInArchive(repo);
            CheckReadmeOrder(repo);
            CheckDemoDocsFrozen(repo);
            CheckDemoRegressionReferences(repo);
            CheckRelativeLinks(repo);
            CheckTermMigrationAndLexicon(repo);
            CheckHistoricalGddRefs(repo);
            CheckEntryPointsDebt(repo);
            CheckDesignVersionCutover(repo);
            CheckNegativeMatrix(repo);
            return _fail;
        }

        // ── 仓库定位与读取 ──────────────────────────────────────────────

        private sealed class Repo
        {
            public string Root;
            public string DesignDocs => Path.Combine(Root, "TEngine", "UnityProject", "DesignDocs");
            public string EarthReclamation => Path.Combine(Root, "production", "design", "earth-reclamation");
            public string FullGame => Path.Combine(Root, "production", "design", "full-game");
            public string ProductionDesign => Path.Combine(Root, "production", "design");
            public string Gdd => Path.Combine(DesignDocs, "ProjectA_GDD.md");
            public string ArchivedGdd => Path.Combine(DesignDocs, "Archive", ArchivedGddName);
            public string FullGameDesign => Path.Combine(DesignDocs, "ProjectA_FullGame_Design.md");
            public string FullGameMilestones => Path.Combine(DesignDocs, "ProjectA_FullGame_Milestones.md");
            public string DemoMilestones => Path.Combine(DesignDocs, "ProjectA_Milestones.md");
            public string Readme => Path.Combine(DesignDocs, "README.md");
            public string TermMigration => Path.Combine(EarthReclamation, "TERM-MIGRATION.md");
            public string Fg02 => Path.Combine(DesignDocs, "fullgame", "FG02_Firmware_Reactions_Morph.md");
            public string BioGdd => Path.Combine(DesignDocs, "Archive", BioGddName);
            public string GapRegister => Path.Combine(FullGame, "FG-GAP-REGISTER.md");
            public string Relative(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

            /// <summary>真工程（仓库根/TEngine/UnityProject）与影子工程（仓库根/.unity-validate-clone）都能向上找到仓库根。</summary>
            public static Repo Locate(string start)
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "production", "design", "full-game")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "TEngine", "UnityProject", "DesignDocs")))
                    {
                        return new Repo { Root = dir.FullName };
                    }
                }
                return null;
            }
        }

        private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;

        private static string[] Lines(string text) => text == null ? Array.Empty<string>() : text.Split('\n');

        private static string Head(string text, int lines) => string.Join("\n", Lines(text).Take(lines));

        /// <summary>从以 <paramref name="headingPrefix"/> 开头的标题行起，到下一个同级或更高级标题为止。</summary>
        internal static string Section(string text, string headingPrefix)
        {
            string[] lines = Lines(text);
            int start = Array.FindIndex(lines, l => l.StartsWith(headingPrefix, StringComparison.Ordinal));
            if (start < 0)
            {
                return null;
            }
            int level = lines[start].TakeWhile(c => c == '#').Count();
            int end = lines.Length;
            for (int i = start + 1; i < lines.Length; i++)
            {
                int l = lines[i].TakeWhile(c => c == '#').Count();
                if (l > 0 && l <= level && lines[i].Length > l && lines[i][l] == ' ')
                {
                    end = i;
                    break;
                }
            }
            return string.Join("\n", lines.Skip(start).Take(end - start));
        }

        /// <summary>从表头行（以 <paramref name="headerPrefix"/> 开头）起的连续表格行，含表头与分隔行。</summary>
        internal static List<string> Table(string text, string headerPrefix)
        {
            var rows = new List<string>();
            string[] lines = Lines(text);
            int start = Array.FindIndex(lines, l => l.StartsWith(headerPrefix, StringComparison.Ordinal));
            for (int i = start; start >= 0 && i < lines.Length && lines[i].StartsWith("|", StringComparison.Ordinal); i++)
            {
                rows.Add(lines[i]);
            }
            return rows;
        }

        private static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

        /// <summary>按行首 ID 列取表格行：<c>| D-01 | …</c> → {D-01: 整行}。</summary>
        internal static Dictionary<string, string> RowsById(string text, string idPattern)
        {
            var map = new Dictionary<string, string>();
            var re = new Regex(@"^\| (" + idPattern + @") \|");
            foreach (string line in Lines(text))
            {
                Match m = re.Match(line);
                if (m.Success)
                {
                    map[m.Groups[1].Value] = line.TrimEnd();
                }
            }
            return map;
        }

        // ── 检查（纯函数：输入文本 → 问题列表） ──────────────────────────

        internal static List<string> ProblemsGddVersions(string gdd, string archived)
        {
            var p = new List<string>();
            if (gdd == null) { p.Add("ProjectA_GDD.md 不存在"); return p; }
            if (archived == null) { p.Add("Archive/" + ArchivedGddName + " 不存在"); return p; }
            if (!Head(gdd, 12).Contains("版本：0.2")) p.Add("ProjectA_GDD.md 头部不是 版本：0.2");
            if (gdd.Contains(FrozenMark)) p.Add("ProjectA_GDD.md 带了 Demo 冻结横幅");
            if (!Head(archived, 12).Contains(FrozenMark)) p.Add("归档 GDD 头部缺\"" + FrozenMark + "\"横幅");
            if (!Head(archived, 12).Contains("版本：0.1")) p.Add("归档 GDD 头部不是 版本：0.1");
            return p;
        }

        internal static List<string> ProblemsDecisionsMerged(string milestones, string gdd)
        {
            var p = new List<string>();
            string section1 = Section(milestones ?? string.Empty, "## 1. 默认决策登记");
            Dictionary<string, string> source = RowsById(section1 ?? string.Empty, @"D-\d{2}");
            Dictionary<string, string> merged = RowsById(Section(gdd ?? string.Empty, "## 附录 A") ?? string.Empty, @"D-\d{2}");
            if (source.Count < 22) p.Add($"里程碑第 1 节只解析到 {source.Count} 条默认决策（应 ≥22）");
            foreach (KeyValuePair<string, string> kv in source)
            {
                if (!merged.TryGetValue(kv.Key, out string row)) p.Add($"{kv.Key} 没有并入 GDD 附录 A");
                else if (row != kv.Value) p.Add($"{kv.Key} 在 GDD 附录 A 与里程碑第 1 节不一致");
            }
            foreach (string id in merged.Keys.Except(source.Keys)) p.Add($"GDD 附录 A 多出里程碑没有的 {id}");
            return p;
        }

        internal static List<string> ProblemsChangeList(string fullGameDesign, string gdd)
        {
            var p = new List<string>();
            Dictionary<string, string> source = RowsById(Section(fullGameDesign ?? string.Empty, "## 16.") ?? string.Empty, @"C-\d{2}");
            string appendixB = Section(gdd ?? string.Empty, "## 附录 B") ?? string.Empty;
            Dictionary<string, string> done = RowsById(appendixB, @"[CS]-\d{2}");
            if (source.Count < 14) p.Add($"正式版设计案第 16 章只解析到 {source.Count} 条变更（应 ≥14）");
            var headings = new HashSet<string>(Lines(gdd ?? string.Empty)
                .Select(l => Regex.Match(l, @"^#{2,3} (\d+(?:\.\d+)?)[.　 ]"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value));
            foreach (string id in source.Keys)
            {
                if (!done.TryGetValue(id, out string row)) { p.Add($"变更 {id} 没有进 GDD 附录 B"); continue; }
                string[] cells = Cells(row);
                if (!cells.Last().StartsWith("已勾销", StringComparison.Ordinal)) p.Add($"变更 {id} 状态不是已勾销：{cells.Last()}");
                // 落点列里的 §x.y 必须是 GDD 0.2 里真实存在的章节。
                foreach (Match m in Regex.Matches(cells[cells.Length - 2], @"§(\d+(?:\.\d+)?)"))
                {
                    if (!headings.Contains(m.Groups[1].Value)) p.Add($"变更 {id} 的落点 §{m.Groups[1].Value} 在 GDD 0.2 里不存在");
                }
            }
            for (int i = 1; i <= 6; i++)
            {
                string sid = $"S-{i:00}";
                if (!done.TryGetValue(sid, out string row)) p.Add($"派生文档同步 {sid} 缺失");
                else if (!Cells(row).Last().StartsWith("已勾销", StringComparison.Ordinal)) p.Add($"派生文档同步 {sid} 未勾销");
            }
            return p;
        }

        /// <summary>返回旧语义命中位置；归档目录不算，Story 卡片只允许定义验收 Grep 的那一行。</summary>
        internal static List<string> OldSemanticHits(IEnumerable<KeyValuePair<string, string>> docs)
        {
            var hits = new List<string>();
            foreach (KeyValuePair<string, string> doc in docs)
            {
                string norm = doc.Key.Replace('\\', '/');
                if (norm.Contains("/Archive/")) continue;
                bool isCards = norm.EndsWith("/FG-STORY-CARDS.md", StringComparison.Ordinal);
                string[] lines = Lines(doc.Value);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!OldSemantics.IsMatch(lines[i])) continue;
                    if (isCards && lines[i].Contains("Grep 旧语义")) continue;
                    hits.Add($"{norm}:{i + 1}");
                }
            }
            return hits;
        }

        internal static List<string> ProblemsReadme(string readme)
        {
            var p = new List<string>();
            string order = Section(readme ?? string.Empty, "## 必读顺序");
            if (order == null) { p.Add("README 缺\"## 必读顺序\"一节"); return p; }
            List<string> items = Lines(order).Where(l => Regex.IsMatch(l, @"^\d+\. ")).ToList();
            if (items.Count < 5) p.Add($"README 必读顺序只有 {items.Count} 项");
            if (items.Count == 0 || !items[0].Contains("](ProjectA_GDD.md)")) p.Add("README 必读第 1 项不是 ProjectA_GDD.md（GDD 0.2）");
            foreach (string must in new[] { "ProjectA_FullGame_Milestones.md", "fullgame/FG00_Completeness_Addendum.md", "full-game/FG-STORY-BOARD.md", "full-game/FG-STORY-CARDS.md" })
            {
                if (!items.Any(l => l.Contains(must))) p.Add("README 必读顺序缺 " + must);
            }
            foreach (string demo in new[] { "DEMO-IMPLEMENTATION-SPEC", "DEMO-CONTENT-LOCK", "DEMO-ACCEPTANCE", "earth-reclamation/STORY-BOARD", "earth-reclamation/MILESTONES", "](ProjectA_Milestones.md)", ArchivedGddName })
            {
                if (items.Any(l => l.Contains(demo))) p.Add("README 必读顺序仍列着 Demo 文档 " + demo);
            }
            string demoSection = Section(readme, "## Demo（0.1");
            if (demoSection == null || !demoSection.Contains("Archive/" + ArchivedGddName)) p.Add("README 缺 Demo 冻结段或未指向归档 GDD 0.1");
            return p;
        }

        internal static List<string> ProblemsFrozen(IEnumerable<KeyValuePair<string, string>> demoDocs, string termMigration)
        {
            var p = new List<string>();
            foreach (KeyValuePair<string, string> doc in demoDocs)
            {
                if (!Head(doc.Value, 6).Contains(FrozenMark)) p.Add(Path.GetFileName(doc.Key) + " 头部缺\"" + FrozenMark + "\"");
            }
            if (termMigration == null || !Head(termMigration, 6).Contains("版本：0.2")) p.Add("TERM-MIGRATION 头部不是 版本：0.2");
            if (termMigration != null && Head(termMigration, 6).Contains(FrozenMark)) p.Add("TERM-MIGRATION 已升 0.2，不应带 Demo 冻结横幅");
            return p;
        }

        /// <summary>Demo ERD 声明产品上级的行必须指向归档 GDD 0.1，不能再指向已升 0.2 的 ProjectA_GDD.md。</summary>
        internal static List<string> ProblemsDemoUpstream(IEnumerable<KeyValuePair<string, string>> demoDocs)
        {
            var p = new List<string>();
            foreach (KeyValuePair<string, string> doc in demoDocs)
            {
                foreach (string line in Lines(doc.Value).Take(15))
                {
                    if (line.Contains(FrozenMark)) continue;
                    bool declaresUpstream = Regex.IsMatch(line, "产品上级|上位权威|产品最高权威|权威顺序|产品权威");
                    if (declaresUpstream && line.Contains("ProjectA_GDD") && !line.Contains(ArchivedGddName))
                    {
                        p.Add(Path.GetFileName(doc.Key) + " 的上级声明仍指向 ProjectA_GDD.md：" + line.Trim());
                    }
                }
            }
            return p;
        }

        /// <summary>Demo ERD 引用的"GDD §x"必须在归档 GDD 0.1 里有对应章节。</summary>
        internal static List<string> ProblemsGddSectionRefs(IEnumerable<KeyValuePair<string, string>> demoDocs, string archivedGdd, out int refCount)
        {
            var p = new List<string>();
            var headings = new HashSet<string>(Lines(archivedGdd ?? string.Empty)
                .Select(l => Regex.Match(l, @"^#{2,3} (\d+(?:\.\d+)?)[.　 ]"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value));
            refCount = 0;
            foreach (KeyValuePair<string, string> doc in demoDocs)
            {
                foreach (Match m in Regex.Matches(doc.Value, @"GDD ?§(\d+(?:\.\d+)?)((?:/§?\d+(?:\.\d+)?)*)"))
                {
                    var ids = new List<string> { m.Groups[1].Value };
                    ids.AddRange(Regex.Matches(m.Groups[2].Value, @"\d+(?:\.\d+)?").Cast<Match>().Select(x => x.Value));
                    foreach (string id in ids)
                    {
                        refCount++;
                        if (!headings.Contains(id)) p.Add($"{Path.GetFileName(doc.Key)} 引用的 GDD §{id} 在归档 GDD 0.1 里找不到");
                    }
                }
            }
            return p;
        }

        internal static List<string> MissingDemoIds(IEnumerable<string> citedIds, string demoDocsUnion)
        {
            return citedIds.Where(id => !demoDocsUnion.Contains(id)).ToList();
        }

        internal static List<string> BrokenLinks(IEnumerable<KeyValuePair<string, string>> docs, out int linkCount)
        {
            var broken = new List<string>();
            linkCount = 0;
            foreach (KeyValuePair<string, string> doc in docs)
            {
                string dir = Path.GetDirectoryName(doc.Key);
                foreach (Match m in LinkPattern.Matches(doc.Value))
                {
                    string target = m.Groups[1].Value;
                    if (target.StartsWith("http", StringComparison.Ordinal) || target.StartsWith("#", StringComparison.Ordinal) || target.StartsWith("mailto", StringComparison.Ordinal)) continue;
                    target = target.Split('#')[0];
                    if (target.Length == 0) continue;
                    linkCount++;
                    string full = Path.GetFullPath(Path.Combine(dir, target));
                    if (!File.Exists(full) && !Directory.Exists(full)) broken.Add($"{Path.GetFileName(doc.Key)} → {m.Groups[1].Value}");
                }
            }
            return broken;
        }

        internal static List<string> BacktickWords(string section)
        {
            string line = Lines(section ?? string.Empty).FirstOrDefault(l => l.StartsWith("`", StringComparison.Ordinal));
            return line == null ? new List<string>() : BacktickWord.Matches(line).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>§6 各表里"新名 / 正式名 / 机械名"列的全部玩家可见名。</summary>
        internal static List<string> FormalNames(string termMigration)
        {
            var names = new List<string>();
            string[] lines = Lines(Section(termMigration ?? string.Empty, "## 6.") ?? string.Empty);
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (!lines[i].StartsWith("|", StringComparison.Ordinal) || !lines[i + 1].StartsWith("|---", StringComparison.Ordinal)) continue;
                string[] header = Cells(lines[i]);
                int[] cols = Enumerable.Range(0, header.Length).Where(c => Regex.IsMatch(header[c], "新名|正式名|机械名")).ToArray();
                for (int r = i + 2; r < lines.Length && lines[r].StartsWith("|", StringComparison.Ordinal); r++)
                {
                    string[] cells = Cells(lines[r]);
                    foreach (int c in cols.Where(c => c < cells.Length))
                    {
                        string name = cells[c].Replace("**", string.Empty).Replace("(已定)", string.Empty).Trim();
                        if (name.Length > 0 && !name.StartsWith("—", StringComparison.Ordinal)) names.Add(name);
                    }
                }
            }
            return names;
        }

        /// <summary>词表 ↔ TERM-MIGRATION 逐词一致，且词表不误伤任何正式名。</summary>
        internal static List<string> ProblemsLexicon(string termMigration, IList<string> legacyWords, IList<string> fgWords, out int formalCount)
        {
            var p = new List<string>();
            List<string> doc23 = BacktickWords(Section(termMigration ?? string.Empty, "### 2.3"));
            List<string> doc67 = BacktickWords(Section(termMigration ?? string.Empty, "### 6.7"));
            if (doc23.Count < 10) p.Add($"TERM-MIGRATION §2.3 只解析到 {doc23.Count} 个禁用词");
            if (doc67.Count < 5) p.Add($"TERM-MIGRATION §6.7 只解析到 {doc67.Count} 个禁用词");
            foreach (string w in doc23.Except(legacyWords)) p.Add($"§2.3 的禁用词\"{w}\"不在题材审计词表里");
            foreach (string w in legacyWords.Except(doc23)) p.Add($"词表里的\"{w}\"不在 §2.3");
            foreach (string w in doc67.Except(fgWords)) p.Add($"§6.7 的禁用词\"{w}\"不在题材审计词表里");
            foreach (string w in fgWords.Except(doc67)) p.Add($"词表里的\"{w}\"不在 §6.7");
            var lexicon = new Regex(string.Join("|", legacyWords.Concat(fgWords).Select(Regex.Escape)));
            List<string> formal = FormalNames(termMigration);
            formalCount = formal.Count;
            if (formal.Count < 100) p.Add($"§6 只解析到 {formal.Count} 个正式名（应 ≥100），反例不足");
            foreach (string name in formal)
            {
                Match m = lexicon.Match(name);
                if (m.Success) p.Add($"正式名\"{name}\"被禁用词\"{m.Value}\"误伤");
            }
            foreach (string w in legacyWords.Concat(fgWords))
            {
                if (!lexicon.IsMatch("界面文字：" + w + "已就绪")) p.Add($"禁用词\"{w}\"嵌在句中时没有被命中");
            }
            return p;
        }

        /// <summary>TERM-MIGRATION §6 的表必须与源文档逐行一致（源文档改名时必须同步）。</summary>
        internal static List<string> ProblemsNameTableSync(string termMigration, string fullGameDesign, string fg02, string gdd)
        {
            var p = new List<string>();
            var pairs = new (string header, string source, string sourceName)[]
            {
                ("| 层 | 旧名 | 新名 | 归属 |", fullGameDesign, "正式版设计案 5.1"),
                ("| 旧 ID | 新名 | 类别 | 种类 | 作用 |", fullGameDesign, "正式版设计案 5.4"),
                ("| 旧器官 | 载体 | 新名 | 说明 |", fullGameDesign, "正式版设计案 5.6"),
                ("| 旧标签 | 新名 | 旧标签 | 新名 |", fullGameDesign, "正式版设计案 5.7"),
                ("| 旧名 | 机械名（提案） | 旧名 | 机械名（提案） |", fg02, "FG02 FGR-FW-041"),
                ("| 类别 | 内部代号 | 正式名 |", gdd, "GDD 0.2 附录 C"),
            };
            string sec6 = Section(termMigration ?? string.Empty, "## 6.") ?? string.Empty;
            foreach ((string header, string source, string sourceName) in pairs)
            {
                List<string> mine = Table(sec6, header);
                List<string> theirs = Table(source ?? string.Empty, header);
                if (theirs.Count < 3) p.Add($"{sourceName} 的名表没找到");
                else if (!mine.SequenceEqual(theirs)) p.Add($"TERM-MIGRATION §6 与 {sourceName} 的名表不一致（{mine.Count} 行 vs {theirs.Count} 行）");
            }
            if (Table(sec6, "| 旧 ID | 新名 |").Count - 2 != 44) p.Add("TERM-MIGRATION §6.2 固件名表不是 44 条");
            return p;
        }

        internal static List<string> ProblemsDesignVersions(string script)
        {
            var p = new List<string>();
            if (script == null) { p.Add("tools/design_versions.py 不存在"); return p; }
            if (!Regex.IsMatch(script, "^DEFAULT_MAJOR = \"0\\.2\"", RegexOptions.Multiline)) p.Add("design_versions.py 的 DEFAULT_MAJOR 不是 \"0.2\"");
            if (!Regex.IsMatch(script, "^LEGACY_DEFAULT_MAJOR = \"0\\.1\"", RegexOptions.Multiline)) p.Add("design_versions.py 缺 LEGACY_DEFAULT_MAJOR = \"0.1\"（历史提交会被重算成 0.2 重复登记）");
            if (!Regex.IsMatch(script, @"^DEFAULT_MAJOR_CUTOVER_TS = \d{10}\b", RegexOptions.Multiline)) p.Add("design_versions.py 缺切换时刻 DEFAULT_MAJOR_CUTOVER_TS");
            if (!script.Contains("def selftest(")) p.Add("design_versions.py 缺 --selftest");
            return p;
        }

        /// <summary>
        /// 历史技术参考里的"GDD §x"写于生物机械版时代：文件头必须注明章节号指生物机械版（归档件名），
        /// 且每个被引用的章节在该归档件里真实存在——否则读者会拿章节号去翻现行 GDD 0.2，读到完全无关的内容。
        /// </summary>
        internal static List<string> ProblemsHistoricalGddRefs(IEnumerable<KeyValuePair<string, string>> docs, string bioGdd, out int refCount, out int docCount)
        {
            var p = new List<string>();
            var headings = new HashSet<string>(Lines(bioGdd ?? string.Empty)
                .Select(l => Regex.Match(l, @"^#{2,3} (\d+(?:\.\d+)?)[.　 ]"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value));
            refCount = 0;
            docCount = 0;
            foreach (KeyValuePair<string, string> doc in docs)
            {
                MatchCollection refs = HistoricalGddRef.Matches(doc.Value ?? string.Empty);
                if (refs.Count == 0) continue;
                docCount++;
                if (!Head(doc.Value, 12).Contains(BioGddName))
                {
                    p.Add(Path.GetFileName(doc.Key) + " 引用了 GDD 章节号，但文件头没有注明章节号指生物机械版（" + BioGddName + "）");
                }
                foreach (Match m in refs)
                {
                    var ids = new List<string> { m.Groups[1].Value };
                    ids.AddRange(Regex.Matches(m.Groups[2].Value, @"\d+(?:\.\d+)?").Cast<Match>().Select(x => x.Value));
                    foreach (string id in ids)
                    {
                        refCount++;
                        if (!headings.Contains(id)) p.Add($"{Path.GetFileName(doc.Key)} 引用的 GDD §{id} 在生物机械版归档里找不到");
                    }
                }
            }
            return p;
        }

        /// <summary>入口文件里仍把权威指向 Demo 0.1 的行，返回"相对路径:行号"。</summary>
        internal static List<string> StaleEntryPoints(IEnumerable<KeyValuePair<string, string>> files)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, string> f in files)
            {
                string[] lines = Lines(f.Value);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (DemoAuthority.IsMatch(lines[i]) && !FrozenContext.IsMatch(lines[i])) stale.Add($"{f.Key}:{i + 1}");
                }
            }
            return stale;
        }

        /// <summary>每条仍指向 Demo 的入口行，都必须被 FG-GAP-REGISTER 的 DEBT 段一条 Open 行按文件点名（00 契约 IC-REQ-003）。</summary>
        internal static List<string> ProblemsEntryPointDebt(IEnumerable<string> stale, string register)
        {
            var p = new List<string>();
            List<string> open = Lines(Section(register ?? string.Empty, DebtSectionHeading) ?? string.Empty)
                .Where(l => l.StartsWith("| DEBT-", StringComparison.Ordinal))
                .Where(l => !Regex.IsMatch(Cells(l).Last(), "Closed|已关闭")).ToList();
            foreach (string s in stale)
            {
                string rel = s.Substring(0, s.LastIndexOf(':'));
                // 反引号前缀保证"CLAUDE.md"不会被"TEngine/UnityProject/CLAUDE.md"顶替。
                bool named = open.Any(r => r.Contains("`" + rel + ":") || r.Contains("`" + rel + "`"));
                if (!named) p.Add($"{s} 仍把权威指向 Demo 0.1，但 FG-GAP-REGISTER 的 DEBT 段没有 Open 行点名 `{rel}`");
            }
            return p;
        }

        /// <summary>
        /// 真跑 <c>python tools/design_versions.py --selftest</c>：6 个归属用例 + 全部历史提交按切换点重算，不得有一条被重算成 0.2。
        /// 这是切换函数的行为验证；<see cref="ProblemsDesignVersions"/> 只是配置存在性前置。
        /// </summary>
        internal static List<string> ProblemsDesignVersionsSelftest(string root, out int cases, out int commits)
        {
            var p = new List<string>();
            cases = 0;
            commits = 0;
            var psi = new System.Diagnostics.ProcessStartInfo("python", "tools/design_versions.py --selftest")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        p.Add("design_versions.py --selftest 超过 120 秒没有结束");
                        return p;
                    }
                    string output = stdout.Result + stderr.Result;
                    Match m = Regex.Match(output, @"selftest：(\d+) 个归属用例、(\d+) 条历史提交；通过");
                    if (m.Success)
                    {
                        cases = int.Parse(m.Groups[1].Value);
                        commits = int.Parse(m.Groups[2].Value);
                    }
                    if (proc.ExitCode != 0 || !m.Success)
                    {
                        string tail = string.Join(" / ", Lines(output.Replace("\r", string.Empty)).Where(l => l.Length > 0).Reverse().Take(4).Reverse());
                        p.Add($"design_versions.py --selftest 退出码 {proc.ExitCode}：{tail}");
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                p.Add("无法启动 python 运行 design_versions.py --selftest：" + e.Message);
            }
            return p;
        }

        // ── 真实文档上的检查 ─────────────────────────────────────────────

        private static void CheckGddVersions(Repo repo)
        {
            List<string> p = ProblemsGddVersions(Read(repo.Gdd), Read(repo.ArchivedGdd));
            Expect(p.Count == 0, "ProjectA_GDD.md 是 0.2；Demo 的 GDD 0.1 原文在 Archive/ 并标\"Demo 版本冻结\"" + Detail(p));
        }

        private static void CheckDecisionsMerged(Repo repo)
        {
            List<string> p = ProblemsDecisionsMerged(Read(repo.FullGameMilestones), Read(repo.Gdd));
            int n = RowsById(Section(Read(repo.FullGameMilestones) ?? string.Empty, "## 1. 默认决策登记") ?? string.Empty, @"D-\d{2}").Count;
            Expect(p.Count == 0, $"里程碑第 1 节 {n} 条默认决策逐行并入 GDD 0.2 附录 A" + Detail(p));
        }

        private static void CheckChangeListCheckedOff(Repo repo)
        {
            List<string> p = ProblemsChangeList(Read(repo.FullGameDesign), Read(repo.Gdd));
            Expect(p.Count == 0, "正式版设计案第 16 章变更清单与派生文档同步 S-01～S-06 在 GDD 附录 B 逐条勾销，落点章节真实存在" + Detail(p));
        }

        private static void CheckOldSemanticsOnlyInArchive(Repo repo)
        {
            List<KeyValuePair<string, string>> docs = AllMarkdown(repo.DesignDocs).Concat(AllMarkdown(repo.ProductionDesign)).ToList();
            List<string> hits = OldSemanticHits(docs);
            // 扫描器自证：归档 GDD 0.1 里必须能扫到旧语义，否则正则或读取失效会静默通过。
            int archivedHits = Lines(Read(repo.ArchivedGdd)).Count(l => OldSemantics.IsMatch(l));
            Expect(hits.Count == 0 && archivedHits >= 2 && docs.Count >= 80,
                $"旧语义（AI-safe、关键局部接管、连续直控计入暴露）只出现在归档里：扫描 {docs.Count} 份文档，归档外 {hits.Count} 处，归档 GDD 0.1 内 {archivedHits} 处" + Detail(hits));
        }

        private static void CheckReadmeOrder(Repo repo)
        {
            List<string> p = ProblemsReadme(Read(repo.Readme));
            Expect(p.Count == 0, "DesignDocs README 必读顺序以 GDD 0.2 开头并切到正式版文档，Demo 文档只在冻结段" + Detail(p));
        }

        private static List<KeyValuePair<string, string>> DemoDocs(Repo repo)
        {
            List<KeyValuePair<string, string>> docs = AllMarkdown(repo.EarthReclamation, SearchOption.TopDirectoryOnly)
                .Where(d => Path.GetFileName(d.Key) != "TERM-MIGRATION.md").ToList();
            docs.Add(new KeyValuePair<string, string>(repo.DemoMilestones, Read(repo.DemoMilestones) ?? string.Empty));
            return docs;
        }

        private static void CheckDemoDocsFrozen(Repo repo)
        {
            List<KeyValuePair<string, string>> docs = DemoDocs(repo);
            List<string> p = ProblemsFrozen(docs, Read(repo.TermMigration));
            Expect(p.Count == 0 && docs.Count >= 18, $"{docs.Count} 份 Demo 派生文档头部标\"Demo 版本冻结\"（未删除），TERM-MIGRATION 升为 0.2" + Detail(p));
        }

        private static void CheckDemoRegressionReferences(Repo repo)
        {
            List<KeyValuePair<string, string>> docs = DemoDocs(repo);
            List<string> upstream = ProblemsDemoUpstream(docs);
            Expect(upstream.Count == 0, "Demo ERD 的产品上级声明都指向归档 GDD 0.1" + Detail(upstream));

            List<string> sectionRefs = ProblemsGddSectionRefs(docs, Read(repo.ArchivedGdd), out int refCount);
            Expect(sectionRefs.Count == 0 && refCount >= 10, $"Demo ERD 引用的 {refCount} 处\"GDD §x\"在归档 GDD 0.1 里都有对应章节" + Detail(sectionRefs));

            // Demo 回归测试（自检、业务注释）引用的 ERD / AC 编号必须仍能在冻结的 ERD 文档里找到。
            var cited = new SortedSet<string>();
            foreach (string root in new[] { Path.Combine(Application.dataPath, "Editor"), Path.Combine(Application.dataPath, "GameScripts") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string cs in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    foreach (Match m in DemoIdPattern.Matches(File.ReadAllText(cs, Encoding.UTF8))) cited.Add(m.Value);
                }
            }
            string union = string.Join("\n", AllMarkdown(repo.EarthReclamation, SearchOption.TopDirectoryOnly).Select(d => d.Value));
            List<string> missing = MissingDemoIds(cited, union);
            Expect(missing.Count == 0 && cited.Count >= 50, $"代码与自检引用的 {cited.Count} 个 ERD / AC 编号都能在冻结的 Demo ERD 里找到" + Detail(missing));
        }

        private static void CheckRelativeLinks(Repo repo)
        {
            var docs = new List<KeyValuePair<string, string>>();
            docs.AddRange(AllMarkdown(repo.DesignDocs, SearchOption.TopDirectoryOnly));
            foreach (string sub in new[] { "Archive", "fullgame", "detailed" }) docs.AddRange(AllMarkdown(Path.Combine(repo.DesignDocs, sub), SearchOption.TopDirectoryOnly));
            docs.AddRange(AllMarkdown(repo.EarthReclamation, SearchOption.TopDirectoryOnly));
            docs.AddRange(AllMarkdown(repo.FullGame, SearchOption.AllDirectories));
            docs.AddRange(AllMarkdown(repo.ProductionDesign, SearchOption.TopDirectoryOnly));
            List<string> broken = BrokenLinks(docs, out int linkCount);
            Expect(broken.Count == 0 && linkCount >= 150, $"{docs.Count} 份现行与冻结文档的 {linkCount} 个相对链接全部可达（归档移动没有断链）" + Detail(broken));
        }

        private static void CheckTermMigrationAndLexicon(Repo repo)
        {
            string tm = Read(repo.TermMigration);
            List<string> sync = ProblemsNameTableSync(tm, Read(repo.FullGameDesign), Read(repo.Fg02), Read(repo.Gdd));
            Expect(sync.Count == 0, "TERM-MIGRATION 0.2 §6 名表与正式版设计案、FG02、GDD 附录 C 逐行一致（固件 44 条）" + Detail(sync));

            List<string> lex = ProblemsLexicon(tm, ThemeLexicon.LegacyBioWords, ThemeLexicon.FgNameTableWords, out int formalCount);
            Expect(lex.Count == 0,
                $"题材审计词表（{ThemeLexicon.LegacyBioWords.Length} 旧生物词 + {ThemeLexicon.FgNameTableWords.Length} 个 0.2 名表词）与 TERM-MIGRATION §2.3/§6.7 逐词一致，{formalCount} 个正式名零误伤" + Detail(lex));

            // 各扫描方读到的就是这张表：字幕 / 目标 / 面板审计共用的正则必须命中 0.2 新增词。
            bool shared = ReferenceEquals(FeedbackCueSelfCheck.ForbiddenWords, ThemeLexicon.Forbidden) &&
                          ThemeLexicon.FgNameTableWords.All(w => FeedbackCueSelfCheck.ForbiddenWords.IsMatch("旧文案" + w));
            Expect(shared, "字幕 / 目标 / 面板题材审计读的是同一张 0.2 词表（新增词被真实扫描正则命中）");
        }

        private static void CheckDesignVersionCutover(Repo repo)
        {
            List<string> p = ProblemsDesignVersions(Read(Path.Combine(repo.Root, "tools", "design_versions.py")));
            Expect(p.Count == 0, "tools/design_versions.py 默认大版本已切到 0.2，且配置了切换时刻" + Detail(p));

            List<string> run = ProblemsDesignVersionsSelftest(repo.Root, out int cases, out int commits);
            Expect(run.Count == 0 && cases >= 6 && commits >= 200,
                $"真跑 design_versions.py --selftest：{cases} 个归属用例通过，{commits} 条历史提交按切换点重算后没有一条被重复登记为 0.2" + Detail(run));
        }

        private static void CheckHistoricalGddRefs(Repo repo)
        {
            var docs = AllMarkdown(Path.Combine(repo.DesignDocs, "detailed"), SearchOption.TopDirectoryOnly)
                .Where(d => !Path.GetFileName(d.Key).StartsWith("00_", StringComparison.Ordinal))
                .Concat(AllMarkdown(Path.Combine(repo.DesignDocs, "migration"), SearchOption.TopDirectoryOnly)).ToList();
            List<string> p = ProblemsHistoricalGddRefs(docs, Read(repo.BioGdd), out int refCount, out int docCount);
            Expect(p.Count == 0 && docCount >= 5 && refCount >= 10,
                $"历史技术参考（detailed/01～05、migration/）中 {docCount} 份引用 GDD 章节号的文件都在文件头注明指生物机械版，{refCount} 处章节在该归档里都存在" + Detail(p));
        }

        private static void CheckEntryPointsDebt(Repo repo)
        {
            var files = EntryPointFiles.Where(rel => File.Exists(repo.Relative(rel)))
                .Select(rel => new KeyValuePair<string, string>(rel, Read(repo.Relative(rel)))).ToList();
            List<string> stale = StaleEntryPoints(files);
            List<string> p = ProblemsEntryPointDebt(stale, Read(repo.GapRegister));
            Expect(p.Count == 0 && files.Count >= 6,
                $"扫描 {files.Count} 份会话入口 / 规则文件：{stale.Count} 行仍把权威指向 Demo 0.1，全部由 FG-GAP-REGISTER 的 Open DEBT 承接" +
                (stale.Count > 0 ? "（" + string.Join("、", stale) + "）" : "（无，可把对应 DEBT 改为 Closed）") + Detail(p));
        }

        // ── 负向矩阵：把同一批检查喂进篡改过的副本，必须报错 ──────────────

        private static void CheckNegativeMatrix(Repo repo)
        {
            string gdd = Read(repo.Gdd) ?? string.Empty;
            string archived = Read(repo.ArchivedGdd) ?? string.Empty;
            string ms = Read(repo.FullGameMilestones) ?? string.Empty;
            string fd = Read(repo.FullGameDesign) ?? string.Empty;
            string readme = Read(repo.Readme) ?? string.Empty;
            string tm = Read(repo.TermMigration) ?? string.Empty;
            string oneDemo = Read(Path.Combine(repo.EarthReclamation, "DEMO-ACCEPTANCE.md")) ?? string.Empty;
            string gddPath = repo.Gdd;
            string bio = Read(repo.BioGdd) ?? string.Empty;
            string register = Read(repo.GapRegister) ?? string.Empty;

            var cases = new List<(string name, bool detected)>
            {
                ("GDD 仍是 0.1", ProblemsGddVersions(gdd.Replace("版本：0.2", "版本：0.1"), archived).Count > 0),
                ("归档 GDD 丢了冻结横幅", ProblemsGddVersions(gdd, archived.Replace(FrozenMark, "Demo")).Count > 0),
                ("GDD 附录 A 漏并一条决策", ProblemsDecisionsMerged(ms, RemoveLineStartingWith(gdd, "| D-07 |")).Count > 0),
                ("里程碑改了决策但 GDD 没同步", ProblemsDecisionsMerged(ms.Replace("| D-04 | 拆除全额返还", "| D-04 | 拆除返还一半"), gdd).Count > 0),
                ("变更清单一条未勾销", ProblemsChangeList(fd, ReplaceInLineStartingWith(gdd, "| C-03 |", "| 已勾销 |", "| 未勾销 |")).Count > 0),
                ("变更落点指向不存在的章节", ProblemsChangeList(fd, ReplaceInLineStartingWith(gdd, "| C-07 |", "§10", "§99")).Count > 0),
                ("现行文档残留旧语义", OldSemanticHits(new[] { new KeyValuePair<string, string>(gddPath, gdd + "\n玩家只能用 AI-safe 动作。") }).Count > 0),
                ("Story 卡片在验收行以外写旧语义", OldSemanticHits(new[] { new KeyValuePair<string, string>("x/full-game/FG-STORY-CARDS.md", "关键局部接管是常态") }).Count > 0),
                ("归档目录里的旧语义不算违规", OldSemanticHits(new[] { new KeyValuePair<string, string>("x/DesignDocs/Archive/a.md", "AI-safe") }).Count == 0),
                ("README 必读第 1 项不是 GDD 0.2", ProblemsReadme(readme.Replace("1. [ProjectA_GDD.md](ProjectA_GDD.md)", "1. [DEMO-IMPLEMENTATION-SPEC.md](x/DEMO-IMPLEMENTATION-SPEC.md)")).Count > 0),
                ("README 必读顺序混进 Demo 文档", ProblemsReadme(readme.Replace("\n2. [ProjectA_FullGame_Milestones.md]", "\n2. [DEMO-ACCEPTANCE.md](x) [ProjectA_FullGame_Milestones.md]")).Count > 0),
                ("Demo 文档漏标冻结", ProblemsFrozen(new[] { new KeyValuePair<string, string>("DEMO-ACCEPTANCE.md", oneDemo.Replace(FrozenMark, string.Empty)) }, tm).Count > 0),
                ("TERM-MIGRATION 没升 0.2", ProblemsFrozen(Array.Empty<KeyValuePair<string, string>>(), tm.Replace("版本：0.2", "版本：0.1")).Count > 0),
                ("Demo ERD 上级仍指向 0.2 GDD", ProblemsDemoUpstream(new[] { new KeyValuePair<string, string>("SYSTEMS-SPEC.md", "# t\n> 产品上级：TEngine/UnityProject/DesignDocs/ProjectA_GDD.md\n") }).Count > 0),
                ("Demo ERD 引用归档里不存在的 GDD 章节", ProblemsGddSectionRefs(new[] { new KeyValuePair<string, string>("x.md", "见 GDD §42.9") }, archived, out _).Count > 0),
                ("代码引用的 ERD 编号在 ERD 文档里消失", MissingDemoIds(new[] { "AC-THEME-001" }, oneDemo.Replace("AC-THEME-001", "AC-THEME-XXX")).Count > 0),
                ("断开的相对链接", BrokenLinks(new[] { new KeyValuePair<string, string>(gddPath, "[x](Archive/不存在的文件.md)") }, out _).Count > 0),
                ("TERM-MIGRATION §6.7 多了词表里没有的词", ProblemsLexicon(tm.Replace("`化工` `无人舰群`", "`化工` `无人舰群` `代号X`"), ThemeLexicon.LegacyBioWords, ThemeLexicon.FgNameTableWords, out _).Count > 0),
                ("词表多了文档里没有的词", ProblemsLexicon(tm, ThemeLexicon.LegacyBioWords, ThemeLexicon.FgNameTableWords.Concat(new[] { "代号X" }).ToList(), out _).Count > 0),
                ("词表过宽误伤正式名（\"灼烧\"会误伤\"腐蚀灼烧\"）", ProblemsLexicon(tm.Replace("`苛性灼烧`", "`灼烧`"), ThemeLexicon.LegacyBioWords, ThemeLexicon.FgNameTableWords.Select(w => w == "苛性灼烧" ? "灼烧" : w).ToList(), out _).Any(x => x.Contains("误伤"))),
                ("TERM-MIGRATION 名表与源文档不同步", ProblemsNameTableSync(tm.Replace("| gene_spindle | 霰射 |", "| gene_spindle | 散射 |"), fd, Read(repo.Fg02), gdd).Count > 0),
                ("design_versions 只改 DEFAULT_MAJOR、不设切换点", ProblemsDesignVersions("DEFAULT_MAJOR = \"0.2\"\n").Count > 0),
                ("历史技术参考引用 GDD 章节号却没注明版本", ProblemsHistoricalGddRefs(new[] { new KeyValuePair<string, string>("x.md", "# t\n> 上级：GDD §7、§16.1") }, bio, out _, out _).Count > 0),
                ("历史技术参考引用生物机械版里不存在的章节", ProblemsHistoricalGddRefs(new[] { new KeyValuePair<string, string>("x.md", "# t\n> 注：" + BioGddName + "\n见 GDD §7、§42.9") }, bio, out _, out _).Count > 0),
                ("入口文件仍指 Demo 却没有 DEBT 承接", ProblemsEntryPointDebt(StaleEntryPoints(new[] { new KeyValuePair<string, string>("CLAUDE.md", "产品 = x，阶段 = `ProjectA_Milestones.md`") }), string.Empty).Count > 0),
                ("DEBT 改成 Closed 但入口仍指 Demo", ProblemsEntryPointDebt(new[] { "CLAUDE.md:15" }, ReplaceInLineStartingWith(register, "| DEBT-FG0DOC01-01 |", "| Open |", "| Closed |")).Count > 0),
                ("只点名 TEngine/UnityProject/CLAUDE.md 不能顶替根 CLAUDE.md", ProblemsEntryPointDebt(new[] { "CLAUDE.md:15" }, DebtSectionHeading + "\n| DEBT-X | `TEngine/UnityProject/CLAUDE.md:71` | Open |").Count > 0),
                ("入口规则仍要求先查 Demo 的 REQUIREMENT-TO-PLAYABLE-TRACE 被识别", StaleEntryPoints(new[] { new KeyValuePair<string, string>(".claude/rules/x.md", "2. 并先在 `production/design/earth-reclamation/REQUIREMENT-TO-PLAYABLE-TRACE.md` 找到") }).Count == 1),
                ("入口里写明冻结语境的 Demo 路径不算违规", StaleEntryPoints(new[] { new KeyValuePair<string, string>("CLAUDE.md", "Demo 0.1 冻结文档：阶段 = `ProjectA_Milestones.md`") }).Count == 0),
            };
            List<string> undetected = cases.Where(c => !c.detected).Select(c => c.name).ToList();
            Expect(undetected.Count == 0, $"负向矩阵 {cases.Count} 项：每种文档倒退都被对应检查拦下" + Detail(undetected));
        }

        // ── 小工具 ──────────────────────────────────────────────────────

        private static IEnumerable<KeyValuePair<string, string>> AllMarkdown(string dir, SearchOption option = SearchOption.AllDirectories)
        {
            if (!Directory.Exists(dir)) return Enumerable.Empty<KeyValuePair<string, string>>();
            return Directory.GetFiles(dir, "*.md", option).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => new KeyValuePair<string, string>(f, Read(f))).ToList();
        }

        private static string RemoveLineStartingWith(string text, string prefix) =>
            string.Join("\n", Lines(text).Where(l => !l.StartsWith(prefix, StringComparison.Ordinal)));

        private static string ReplaceInLineStartingWith(string text, string prefix, string oldValue, string newValue) =>
            string.Join("\n", Lines(text).Select(l => l.StartsWith(prefix, StringComparison.Ordinal) ? l.Replace(oldValue, newValue) : l));

        private static string Detail(List<string> problems) =>
            problems.Count == 0 ? string.Empty : "：" + string.Join("；", problems.Take(12)) + (problems.Count > 12 ? $"……共 {problems.Count} 处" : string.Empty);

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
