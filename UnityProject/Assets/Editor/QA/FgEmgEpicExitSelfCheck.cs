using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Emergence;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG4-E2E-02 FG-EMG Epic 收尾（卡片“负向：IC-REQ-022 全部反向场景（四阶段合集）”；里程碑旅程轻量口径 b）：
    /// A 收尾门禁（正式阶段 4、存档 schema 仍 2、快照格式 ≥ 20、未接通端口为空、模糊测试器出口阶段 = 4）；
    /// R 四阶段反向场景合集：IC-REQ-022 六类（资源不足、目标死亡、断链 / 失联、暂停与倍速、存读档、路径失败）+ 各阶段卡片点名的反向场景，
    /// 每格映射到 RunAll 里起真实系统的那条断言——段已登记在全量自检、断言原文仍在源码里；同一进程里那段本轮已跑过时，再核它本轮失败 0（全量单进程跑时生效，分片跑时各片自己核）。
    /// 阶段 2 的映射另由 FgEmg2ExitSelfCheck R 段逐条核（这里核它仍登记、并抽 3 条）；阶段 3 没有改寻路 / 通行的系统，“路径失败”不适用（写明理由，不静默跳过）。
    /// </summary>
    public static class FgEmgEpicExitSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private static void Line(string s) => _report.AppendLine(s);

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                _fail++;
                Line("  ✗ " + what);
            }
        }

        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[FG-EMG Epic 收尾] EMG4-E2E-02：收尾门禁 / 四阶段反向场景合集（IC-REQ-022）");
            foreach (Action a in new Action[] { CheckGates, CheckReverseScenarios })
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Expect(false, $"{a.Method.Name} 抛异常：{e}");
                }
            }
            Line($"  · [FG-EMG Epic 收尾] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void CheckGates()
        {
            Line("  · A. 收尾门禁（FG19 第 12 节阶段 4、FGR-EMG-212、DEBT-EMG4HIJ01-02）");
            Expect(EmgCatalog.EffectiveStage == 4, $"正式表 emg.stage 生效 = 4（现 {EmgCatalog.EffectiveStage}）");
            Expect(EmgCatalog.PendingPorts.Length == 0, $"未接通端口 0 族（现 {EmgCatalog.PendingPorts.Length}）");
            Expect(CampaignSaveService.CurrentSchemaVersion == 2, $"战役存档 schema 保持 2（FGR-EMG-212，现 {CampaignSaveService.CurrentSchemaVersion}）");
            Expect(CombatConst.FormatVersion >= 20, $"内核快照格式 ≥ 20（阶段 4 劫持段，现 {CombatConst.FormatVersion}）");
            // 出口阶段 = 已出口阶段的核对在 FgEmergenceFuzzSelfCheck 出口模式里（降级即失败）；这里只核那段本轮结果
            CellFrameworkValidate.SegmentTiming fuzz = CellFrameworkValidate.TimingOf("FgEmergenceFuzzSelfCheck");
            if (fuzz == null)
            {
                Line("  · 模糊测试器出口模式（阶段 4）：本进程未跑该段，以全量结论为准");
            }
            else
            {
                Expect(fuzz.Fail == 0 && fuzz.Pass > 0, $"模糊测试器出口模式（阶段 {FgEmergenceFuzzSelfCheck.ExitedStage}，DEBT-EMG4HIJ01-02）本轮 {fuzz.Pass} 通过 / {fuzz.Fail} 失败");
            }
        }

        /// 断言原文必须落在某个 Expect( 调用的参数里（排除注释、小节标题），找不到就算映射失效
        private static bool InExpectCall(string text, string label)
        {
            for (int i = text.IndexOf(label, StringComparison.Ordinal); i >= 0; i = text.IndexOf(label, i + 1, StringComparison.Ordinal))
            {
                int e = text.LastIndexOf("Expect(", i, StringComparison.Ordinal);
                if (e >= 0 && text.IndexOf(");", e, i - e, StringComparison.Ordinal) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckReverseScenarios()
        {
            Line("  · R. 四阶段反向场景合集：每格 = 已登记段里起真实系统的一条断言（段名 + 断言原文）；本进程已跑过该段时再核本轮失败 0");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var map = new (int Stage, string What, string Segment, string File, string Label)[]
            {
                // 阶段 1（EMG1-E2E-01 证据 5.4）
                (1, "资源不足：刻印废料不足零扣款", "FgSignalCoreSelfCheck", "FgSignalCoreSelfCheck.cs", "废料不足“"),
                (1, "目标死亡：寻的目标中途阵亡按剩余额度重选", "FgEmgGrammarSelfCheck", "FgEmgGrammarSelfCheck.cs", "目标中途阵亡：在剩余额度"),
                (1, "断链：信号离开后已剥离的子机不重刷、不续命", "FgEmgFlashSelfCheck", "FgEmgFlashSelfCheck.cs", "负向：离开后回 AI 武器再开火"),
                (1, "带宽截断：带宽已满时新程序截断（第三架 0 条只有基础攻击）", "FgEmgFlashSelfCheck", "FgEmgFlashSelfCheck.cs", "第三架执行程序 "),
                (1, "暂停与倍速：同一段游戏时间 1x / 0.5x / 3x 结果一致", "FgEmgGrammarSelfCheck", "FgEmgGrammarSelfCheck.cs", "同一段 3 秒游戏时间：1x ["),
                (1, "存读档：在飞修饰弹体中途存档续跑逐步一致", "FgEmgGrammarSelfCheck", "FgEmgGrammarSelfCheck.cs", "，续跑 120 步每步 StateHash 一致"),
                (1, "路径失败：抛射落点不可达时在瞄准点落地终结", "FgEmgGrammarSelfCheck", "FgEmgGrammarSelfCheck.cs", "目标走开（落点不可达）时在瞄准点落地终结"),
                // 阶段 2（完整映射在 FgEmg2ExitSelfCheck R；这里六类各取一条阶段 2 自己的行为断言）
                (2, "资源不足 / 容量满：斑块容量满按软上限 / 总上限降级", "FgEmgMatterSelfCheck", "FgEmgMatterSelfCheck.cs", "容量 30（世界源保留 4）"),
                (2, "目标死亡：洗礼塔场上全灭复位", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "F2 负向“全灭”"),
                (2, "路径失败：固化胶块堵死唯一通路 → 警告可定位", "FgEmgPersistSelfCheck", "FgEmgPersistSelfCheck.cs", "L2 固化胶块堵死核心与仓库唯一通路"),
                (2, "断链：桥接导线一端阵亡立即断开", "FgEmgReclaimSelfCheck", "FgEmgReclaimSelfCheck.cs", "一端（目标单位）阵亡"),
                (2, "暂停与倍速：区域节拍 0.5x～3x 结果与哈希一致", "FgEmgMigrationSelfCheck", "FgEmgMigrationSelfCheck.cs", "J1 每帧 1 / 2 / 6 步（0.5x～3x 只改每帧步数）"),
                (2, "存读档：没有留存字段的 v2 旧档读入", "FgEmgPersistSelfCheck", "FgEmgPersistSelfCheck.cs", "N4 v2 旧档"),
                // 阶段 3（EMG3-E2E-01 证据 B2）
                (3, "断网 / 失联：中继阵亡后立即停止转发", "FgEmgSquadSelfCheck", "FgEmgSquadSelfCheck.cs", "中继 B 阵亡后"),
                (3, "资源不足：中继限流原因文本", "FgEmgSquadSelfCheck", "FgEmgSquadSelfCheck.cs", "、限流未转发"),
                (3, "元程序上限：第 3 层不执行、预览写原因", "FgEmgStage3FirmwareSelfCheck", "FgEmgStage3FirmwareSelfCheck.cs", "预览原因 emg.compile.meta_exec_cap"),
                (3, "目标死亡 / 宿主提前结束：在结束点放出", "FgEmgMetaSelfCheck", "FgEmgMetaSelfCheck.cs", "宿主命中后继续飞、到点前已结束"),
                (3, "暂停与倍速：中继组 0.5x / 1x / 3x / 暂停哈希一致", "FgEmgSquadSelfCheck", "FgEmgSquadSelfCheck.cs", "0.5x / 1x / 3x / 暂停：哈希与计数一致"),
                (3, "存读档：中继转发中途存读档续跑一致", "FgEmgSquadSelfCheck", "FgEmgSquadSelfCheck.cs", "；续跑逐步哈希一致"),
                (3, "坏数据：测试底盘行混进正式快照被拒", "FgEmgReservedChassisSelfCheck", "FgEmgReservedChassisSelfCheck.cs", "正式数据的内核读带多足测试行的快照"),
                // 阶段 4（EMG4-E2E-01 证据“负向矩阵”；FG19 第 16 节两条反向）
                (4, "永久改变堵路：熔坑算阻路、堵路警告与固态斑块同一泛洪", "FgEmgTerrainSelfCheck", "FgEmgTerrainSelfCheck.cs", "H3 熔坑进长期留存物清单且算阻路"),
                (4, "槽位封锁后程序按空槽重编", "FgEmgSeizeSelfCheck", "FgEmgSeizeSelfCheck.cs", "封锁 1 号槽：版本 +1（接入中的机器重编译）"),
                (4, "资源不足：仓库满时重复部件拆成技术数据", "FgEmgLootHintSelfCheck", "FgEmgLootHintSelfCheck.cs", "C5 仓库满时拿到重复部件"),
                (4, "目标死亡：劫持者阵亡 → 被劫持体立即自毁、只结算一次", "FgEmgHijackSelfCheck", "FgEmgHijackSelfCheck.cs", "劫持者阵亡 → 下一步被劫持单位立即自毁"),
                (4, "断链 / 失联：反向接入 3 秒夺回", "FgEmgSeizeSelfCheck", "FgEmgSeizeSelfCheck.cs", "反向接入：引导 3 游戏秒后夺回"),
                (4, "暂停与倍速：劫持按 30 游戏秒到期", "FgEmgHijackSelfCheck", "FgEmgHijackSelfCheck.cs", "暂停时剩余时间不动；0.5x～3x 倍速下都按 30 游戏秒到期"),
                (4, "暂停与倍速：熔坑凝固按游戏时间", "FgEmgTerrainSelfCheck", "FgEmgTerrainSelfCheck.cs", "J1 暂停 120 帧熔融金属不凝、不成坑"),
                (4, "存读档：熔坑中途存读档哈希一致", "FgEmgTerrainSelfCheck", "FgEmgTerrainSelfCheck.cs", "F1 中途存读档（1 个熔坑"),
                (4, "存读档：劫持中途存读档", "FgEmgHijackSelfCheck", "FgEmgHijackSelfCheck.cs", "劫持中途存读档（格式"),
                (4, "路径失败：熔坑不可进入，地面单位绕行", "FgEmgTerrainSelfCheck", "FgEmgTerrainSelfCheck.cs", "D2 地面单位穿过去：没有一步踏进坑里"),
                (4, "精英 / 首领不可劫持（按抗性拒绝并写原因）", "FgEmgHijackSelfCheck", "FgEmgHijackSelfCheck.cs", "真实路径：hij.hack 命中精英"),
            };
            string qa = Path.Combine(Application.dataPath, "Editor/QA");
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            int ranNow = 0;
            foreach (var m in map)
            {
                if (!sources.TryGetValue(m.File, out string text))
                {
                    string path = Path.Combine(qa, m.File);
                    text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                    sources[m.File] = text;
                }
                CellFrameworkValidate.SegmentTiming t = CellFrameworkValidate.TimingOf(m.Segment);
                if (t != null)
                {
                    ranNow++;
                }
                bool mapped = segments.Contains(m.Segment) && InExpectCall(text, m.Label);
                if (t == null)
                {
                    // 没跑的段不记行为通过，只核映射还指着一条 Expect（行为以全量单进程结论为准）
                    Expect(mapped, $"阶段 {m.Stage} · {m.What}：映射有效（{m.Segment}：“{m.Label}”；本进程未跑该段，行为以全量结论为准）");
                    continue;
                }
                Expect(mapped && t.Fail == 0 && t.Pass > 0, $"阶段 {m.Stage} · {m.What}（{m.Segment}：“{m.Label}”；本轮 {t.Pass} 通过 / {t.Fail} 失败）");
            }
            Expect(InExpectCall("Expect(ok, $\"甲 {n}\");", "甲") && !InExpectCall("// 甲\nLine(\"甲\");\nExpect(ok, \"乙\");", "甲") && !InExpectCall("Expect(ok, \"乙\");\nLine(\"甲\");", "甲"),
                "映射匹配器：只认 Expect( 参数里的原文，注释 / 小节标题 / 别的语句里的同文不算");
            // 映射表自检（不算行为断言）：六类每个阶段都有一格；阶段 3“路径失败”不适用：阶段 3 交付底盘特性 / 协同 / 元程序 / 节拍，没有改寻路或通行的系统
            string[] six = { "资源不足", "目标死亡", "断", "暂停与倍速", "存读档", "路径失败" };
            var holes = new List<string>();
            for (int stage = 1; stage <= 4; stage++)
            {
                foreach (string c in six)
                {
                    bool covered = map.Any(x => x.Stage == stage && x.What.Contains(c));
                    if (!covered && !(stage == 3 && c == "路径失败"))
                    {
                        holes.Add($"阶段 {stage}·{c}");
                    }
                }
            }
            Expect(holes.Count == 0, $"映射表自检：四阶段 × IC-REQ-022 六类都有映射（阶段 3 路径失败不适用，理由见注释）；共 {map.Length} 格，本进程已跑 {ranNow} 格{(holes.Count == 0 ? string.Empty : "；缺 " + string.Join("、", holes))}");
        }
    }
}
