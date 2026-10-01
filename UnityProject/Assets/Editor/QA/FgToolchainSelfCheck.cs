using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG-TOOL-01：验证提速工具链自身的行为（不进 Play、不依赖场景，并入全量自检）。
    /// A 性能判定（PerfGate）：边界（等于阈值 / 超线不到 2 倍 / 恰好 2 倍 / 超 2 倍 / 量坏了）、严格小于、多指标、功能条件失败、
    ///   通过 / 警告 / 失败三种输出（警告不调用断言函数 = 不计通过也不计失败）、警告汇总与 CPU 占用、只测一次（判定不回调测量）；
    ///   性能基线对比的判定线 = 原“退化 10% 且超出容差”的边界。
    /// B 分段计时：数断言行（与 unity-validate.sh 同一口径）、耗时排行按耗时从大到小且不含 ✓ / ✗、全量自检的段名清单（唯一、包含本段与各出口自检）、
    ///   段内剖析把行对到写出时刻。
    /// C 旅程断点：校验的负向矩阵（没有断点 / 步骤不存在 / 不是登记的断点步骤 / 格式 / 版本 / 种子 / 该步之前的步骤变了 / 存档格式 / 生成器 / 代码指纹）与
    ///   正向（该步之后的步骤变了照样能用；--allow-code-change 放行并写明警告）；续跑旅程的组成；运行器只在登记步骤调写断点、写断点失败不改结论、
    ///   续跑报告写明“断点续跑”；变量快照；代码指纹（确定、内容 / 路径变化即变、与枚举顺序无关）；目录复制；元数据存读往返；拒绝报告的格式；
    ///   全部旅程登记的断点步骤都存在；编辑模式下不是中性状态（不在游戏中）；代码指纹含界面布局与预制体、不含 Fonts / 图片 / .meta。
    /// D 同一轮自检里共享 python 自测。
    /// E 改表主路径：打开并行自测的入口脚本都有 main 保护；真跑 step7_check_luban.py（gen_all 第 7 步 / run_luban.sh）通过。
    /// </summary>
    public static class FgToolchainSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/自检：验证提速工具链（FG-TOOL-01）")]
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
            Line("\n[验证提速工具链] 性能判定、分段计时、旅程断点（FG-TOOL-01）");
            try
            {
                CheckPerfGateJudge();
                CheckPerfGateOutput();
                CheckBaselineLine();
                CheckSegmentTiming();
                CheckCheckpointValidation();
                CheckResumeDef();
                CheckRunnerHooks();
                CheckCodeHashAndFiles();
                CheckCatalogCheckpoints();
                CheckSharedPython();
                CheckLubanEntryScripts();
            }
            catch (Exception e)
            {
                Fail($"验证提速工具链自检抛异常：{e}");
            }
            Line($"  · [验证提速工具链] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 性能判定 ───────────────────────────────────────────────────────

        private static void CheckPerfGateJudge()
        {
            Line("  · A. 性能判定：超线不到 2 倍 = 警告（不计失败），超 2 倍 / 量坏了 = 失败；阈值与比较方式（≤ / <）不变");
            var cases = new (PerfGate.Metric m, PerfGate.Level want, string what)[]
            {
                (PerfGate.Le(2.0, 2.0), PerfGate.Level.Pass, "≤：等于阈值 → 通过"),
                (PerfGate.Le(1.0, 2.0), PerfGate.Level.Pass, "≤：低于阈值 → 通过"),
                (PerfGate.Le(2.6, 2.0), PerfGate.Level.Warn, "≤：超线 1.3 倍 → 警告"),
                (PerfGate.Le(4.0, 2.0), PerfGate.Level.Warn, "≤：恰好 2 倍 → 警告（超过 2 倍才失败）"),
                (PerfGate.Le(4.01, 2.0), PerfGate.Level.Fail, "≤：2.005 倍 → 失败"),
                (PerfGate.Lt(2.0, 2.0), PerfGate.Level.Warn, "<：等于阈值 → 超线（与原断言的严格小于一致）→ 警告"),
                (PerfGate.Lt(1.99, 2.0), PerfGate.Level.Pass, "<：低于阈值 → 通过"),
                (PerfGate.Le(double.NaN, 2.0), PerfGate.Level.Fail, "测量值 NaN（量坏了）→ 失败"),
                (PerfGate.Le(double.PositiveInfinity, 2.0), PerfGate.Level.Fail, "测量值无穷 → 失败"),
                (PerfGate.Le(0.1, 0.0), PerfGate.Level.Fail, "阈值 0、测量值 > 0 → 失败（没有“2 倍”可言）"),
                (PerfGate.Le(0.0, 0.0), PerfGate.Level.Pass, "阈值 0、测量值 0 → 通过"),
            };
            var bad = cases.Where(c => PerfGate.Judge(c.m) != c.want).Select(c => $"{c.what}（得到 {PerfGate.Judge(c.m)}）").ToList();
            Expect(bad.Count == 0, $"单指标判定 {cases.Length} 种边界全部符合{(bad.Count == 0 ? string.Empty : "；不符：" + string.Join("；", bad))}");

            Expect(PerfGate.Judge(PerfGate.Le(1, 2), PerfGate.Le(3, 2)) == PerfGate.Level.Warn
                   && PerfGate.Judge(PerfGate.Le(3, 2), PerfGate.Le(5, 2)) == PerfGate.Level.Fail
                   && PerfGate.Judge(PerfGate.Le(1, 2), PerfGate.Le(1, 2)) == PerfGate.Level.Pass
                   && PerfGate.Judge() == PerfGate.Level.Pass,
                "多指标：一项超线（不到 2 倍）→ 警告；任一项超 2 倍 → 失败；全部达标或没有性能指标 → 通过");

            string d = PerfGate.DescribeOver(new[] { PerfGate.Le(2.6, 2.0, "p95 ms"), PerfGate.Le(1.0, 2.0, "平均 ms") });
            Expect(d.Contains("p95 ms 2.6 超阈值 2") && d.Contains("1.30 倍") && !d.Contains("平均") && !d.Contains("【") && !d.Contains("】"),
                $"超线说明只列超线的指标、写明数字与倍数，不含“【】”（比对工具靠它剥离追加说明）：“{d}”");
        }

        private static void CheckPerfGateOutput()
        {
            var lines = new List<string>();
            var calls = new List<(bool ok, string msg)>();
            void FakeExpect(bool ok, string msg) => calls.Add((ok, msg));
            void FakeLine(string l) => lines.Add(l);

            // 本段自己的警告不能混进全量自检末尾的汇总：先记下当前汇总，测完原样放回。
            object saved = PerfGate.SaveStateForTests();
            int warn0 = PerfGate.Warnings.Count;
            int judged0 = PerfGate.Judged.Count;
            int measured = 0;
            double Measure()
            {
                measured++;
                return 2.6;
            }

            PerfGate.Level pass = PerfGate.Expect(true, "甲", new[] { PerfGate.Le(1.0, 2.0) }, FakeExpect, FakeLine);
            PerfGate.Level warn = PerfGate.Expect(true, "乙", new[] { PerfGate.Le(Measure(), 2.0, "p95 ms") }, FakeExpect, FakeLine);
            PerfGate.Level fail = PerfGate.Expect(true, "丙", new[] { PerfGate.Le(5.0, 2.0, "p95 ms") }, FakeExpect, FakeLine);
            PerfGate.Level func = PerfGate.Expect(false, "丁", new[] { PerfGate.Le(1.0, 2.0) }, FakeExpect, FakeLine);
            bool warnLine = lines.Count == 1 && lines[0].StartsWith("  " + PerfGate.WarnTag + "：乙　【") && lines[0].EndsWith("】")
                            && lines[0].Contains("不到 2 倍，记警告不计失败、不重测") && lines[0].Contains("CPU");
            Expect(pass == PerfGate.Level.Pass && warn == PerfGate.Level.Warn && fail == PerfGate.Level.Fail && func == PerfGate.Level.Fail
                   && calls.Count == 3 && calls[0] == (true, "甲") && !calls[1].ok && calls[1].msg.StartsWith("丙　【性能超过阈值 2 倍判失败") && calls[2] == (false, "丁")
                   && warnLine && measured == 1,
                $"输出：通过 → 断言函数(真)；警告 → 只写一行性能警告（末尾“　【超线说明；CPU 占用】”）、不调断言函数（不计通过也不计失败）；超 2 倍 → 断言函数(假)并写明原因；" +
                $"功能条件不满足 → 断言函数(假)，不论性能；测量只发生一次（判定不回头重测，测量调用 {measured} 次）");

            bool inSummary = PerfGate.Warnings.Count == warn0 + 1 && PerfGate.Warnings.Last().StartsWith("乙") && PerfGate.Judged.Count == judged0 + 4;
            var sb = new StringBuilder();
            PerfGate.AppendSummary(sb);
            string summary = sb.ToString();
            Expect(inSummary && summary.Contains($"性能警告 {PerfGate.Warnings.Count}") && summary.Contains("乙") && !summary.Contains("✓") && !summary.Contains("✗")
                   && !summary.Contains(PerfGate.WarnTag + "："),
                "警告进汇总（全量自检末尾逐条列出，汇总行不带通过 / 失败 / 性能警告三种行标记，脚本不会把汇总重复计数）；四次判定都记进判定清单");
            PerfGate.RestoreStateForTests(saved);

            // 本进程忙 300 毫秒（单线程）：整机与本进程占用都必须量得到（> 0），且本进程不超过整机——曾经“本进程”恒为 0（Mono 的 Process.TotalProcessorTime 取不到）。
            PerfGate.MarkWindow();
            var busy = System.Diagnostics.Stopwatch.StartNew();
            double sink = 0;
            while (busy.ElapsedMilliseconds < 300)
            {
                sink += Math.Sqrt(busy.ElapsedTicks % 1000 + 1);
            }
            (double machine, double self)? cpu = PerfGate.CpuUsage();
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            Expect(!windows || (cpu.HasValue && sink > 0 && cpu.Value.machine > 0 && cpu.Value.machine <= 1 && cpu.Value.self > 0.01 && cpu.Value.self <= cpu.Value.machine + 0.02),
                $"整机 / 本进程 CPU 占用可取（Windows GetSystemTimes / GetProcessTimes）：本进程单线程忙 300 毫秒时整机 {cpu?.machine:P0}、本进程 {cpu?.self:P0}（{Environment.ProcessorCount} 线程）");
        }

        private static void CheckBaselineLine()
        {
            // 原判定：m > b × 1.1 且 m − b > 容差 才算退化。判定线 = max(b × 1.1, b + 容差)，两种写法在每个样本上结论相同。
            var rnd = new System.Random(7);
            int same = 0;
            const int n = 2000;
            for (int i = 0; i < n; i++)
            {
                double b = rnd.NextDouble() * 10;
                double floor = rnd.NextDouble() * 2;
                double m = rnd.NextDouble() * 20;
                bool old = m > b * (1 + FgPerfBaseline.RegressionLimit) && m - b > floor;
                bool now = m > FgPerfBaseline.RegressionLine(b, floor);
                same += old == now ? 1 : 0;
            }
            Expect(same == n && FgPerfBaseline.RegressionLine(10, 0.5) == 11 && FgPerfBaseline.RegressionLine(1, 0.5) == 1.5,
                $"性能基线对比的判定线 = 原“退化超过 10% 且超出绝对容差”的边界（{n} 个随机样本 {same} 个结论相同）；超线后按同一规则分警告 / 失败");
        }

        // ── B. 分段计时 ───────────────────────────────────────────────────────

        private static void CheckSegmentTiming()
        {
            Line("  · B. 分段计时与段内剖析");
            CellFrameworkValidate.CountMarks("  ✓ 甲\n  ✗ 乙\n  ⚠ 性能警告：丙　【x】\n  · 说明\n  ✓ 丁\n性能警告 1 条（汇总）", out int p, out int f, out int w);
            Expect(p == 2 && f == 1 && w == 1, $"数断言行（与 unity-validate.sh 同一口径：通过 / 失败 / 性能警告三种行标记，汇总行不算）：通过 {p}、失败 {f}、警告 {w}");

            var timings = new List<CellFrameworkValidate.SegmentTiming>
            {
                new CellFrameworkValidate.SegmentTiming { Name = "慢", Seconds = 9.5, Pass = 3 },
                new CellFrameworkValidate.SegmentTiming { Name = "快", Seconds = 0.5, Pass = 1 },
                new CellFrameworkValidate.SegmentTiming { Name = "中", Seconds = 3.0, Pass = 2, Warn = 1 },
            };
            var sb = new StringBuilder();
            CellFrameworkValidate.AppendTimingRanking(sb, timings, 13.2);
            string text = sb.ToString();
            int iSlow = text.IndexOf("| 慢", StringComparison.Ordinal);
            int iMid = text.IndexOf("| 中", StringComparison.Ordinal);
            int iFast = text.IndexOf("| 快", StringComparison.Ordinal);
            Expect(iSlow > 0 && iSlow < iMid && iMid < iFast && text.Contains("共 3 段") && text.Contains("2/0/1") && !text.Contains("✓") && !text.Contains("✗")
                   && text.Contains("各段耗时排行结束"),
                "耗时排行按耗时从大到小、带每段的通过 / 失败 / 警告条数、整轮合计；不带通过 / 失败行标记（不会被计成断言）");

            IReadOnlyList<string> names = CellFrameworkValidate.SegmentNames;
            string[] must = { "ValidateData", "FgMilestoneM3SelfCheck", "FgLogisticsGateSelfCheck", "JourneyRunnerSelfCheck", nameof(FgToolchainSelfCheck) };
            Expect(names.Count >= 109 && names.Distinct().Count() == names.Count && must.All(names.Contains) && names[0] == "ValidateData",
                $"全量自检按段登记：{names.Count} 段、段名唯一，含本段与各里程碑出口自检（--segment 按这些名字点名）");

            var samples = new List<(double t, int len)> { (0.0, 0), (1.0, 10), (1.02, 25), (4.0, 25), (4.02, 40) };
            string prof = SegmentProfiler.Describe("样例", 4.1, "  ✓ 第一行一\n  ✓ 第二行二二二二\n  ✓ 第三行三三三三三三\n", 0, samples, 2);
            Expect(SegmentProfiler.TimeReached(samples, 11) == 1.02 && SegmentProfiler.TimeReached(samples, 30) == 4.02 && SegmentProfiler.TimeReached(samples, 99) == 4.02
                   && prof.IndexOf("第三行", StringComparison.Ordinal) > 0 && prof.IndexOf("第三行", StringComparison.Ordinal) < prof.IndexOf("第一行", StringComparison.Ordinal)
                   && !prof.Contains("✓"),
                "段内剖析：按报告长度采样把每行对到写出时刻，按与上一行的间隔从大到小列出（不带通过行标记，写在单独文件里）");
        }

        // ── C. 旅程断点 ───────────────────────────────────────────────────────

        private static JourneyDef SampleDef(int version = 1, string titleB = "乙", string titleD = "丁")
        {
            JourneyStep S(string id, string title) => JourneyCommon.S(id, title, 10, null, c => StepOutcome.Done());
            return new JourneyDef
            {
                Id = "TEST-CKPT",
                Title = "断点自检",
                Seed = 42,
                Version = version,
                Steps = new List<JourneyStep> { S("play", "进 Play"), S("a", "甲"), S("b", titleB), S("c", "丙"), S("d", titleD) },
                CheckpointAfter = new[] { "b", "c" },
            };
        }

        private static JourneyCheckpoints.Meta MetaFor(JourneyDef def, string step, JourneyCheckpoints.EnvInfo env)
        {
            int idx = def.Steps.FindIndex(s => s.Id == step);
            return new JourneyCheckpoints.Meta
            {
                journey = def.Id,
                journeyVersion = def.Version,
                stepId = step,
                stepIndex = idx,
                stepsHash = JourneyCheckpoints.StepsHash(def, idx),
                seed = def.Seed,
                codeHash = env.CodeHash,
                saveSchema = env.SaveSchema,
                contentVersion = env.ContentVersion,
                generatorVersion = env.GeneratorVersion,
                slot = 0,
                speed = 3f,
                vars = new List<JourneyCheckpoints.Var> { new JourneyCheckpoints.Var { k = "saves", v = "旧目录" }, new JourneyCheckpoints.Var { k = "workerA", v = "7" } },
            };
        }

        private static void CheckCheckpointValidation()
        {
            Line("  · C. 旅程断点：与当前旅程版本或代码不匹配时拒绝使用并提示重新完整跑；该步之后的改动不影响");
            var env = new JourneyCheckpoints.EnvInfo { CodeHash = "aaaa1111", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 };
            JourneyDef def = SampleDef();
            JourneyCheckpoints.Meta good = MetaFor(def, "b", env);

            bool okGood = JourneyCheckpoints.Validate(def, "b", good, env, false, out string why0, out string warn0);
            Expect(okGood && why0 == null && warn0 == null, "正向：版本、步骤、种子、存档格式、生成器、代码指纹都一致 → 可用");

            var negatives = new List<(string what, Func<JourneyCheckpoints.Meta> meta, JourneyDef d, string step, JourneyCheckpoints.EnvInfo e, string expect)>
            {
                ("没有断点", () => null, def, "b", env, "没有"),
                ("步骤不存在", () => good, def, "zz", env, "没有步骤"),
                ("不是登记的断点步骤", () => MetaFor(def, "a", env), def, "a", env, "不是登记的断点步骤"),
                ("断点格式不同", () => { JourneyCheckpoints.Meta m = MetaFor(def, "b", env); m.format = 99; return m; }, def, "b", env, "断点格式"),
                ("旅程版本变了", () => good, SampleDef(version: 2), "b", env, "旅程版本不匹配"),
                ("种子变了", () => { JourneyCheckpoints.Meta m = MetaFor(def, "b", env); m.seed = 1; return m; }, def, "b", env, "种子不匹配"),
                ("断点之前的步骤标题变了", () => good, SampleDef(titleB: "乙（改）"), "b", env, "之前的步骤变了"),
                ("存档格式变了", () => good, def, "b", new JourneyCheckpoints.EnvInfo { CodeHash = env.CodeHash, SaveSchema = 3, ContentVersion = 1, GeneratorVersion = 2 }, "存档格式"),
                ("世界生成器版本变了", () => good, def, "b", new JourneyCheckpoints.EnvInfo { CodeHash = env.CodeHash, SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 3 }, "生成器"),
                ("游戏代码 / 配置表变了", () => good, def, "b", new JourneyCheckpoints.EnvInfo { CodeHash = "bbbb2222", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 }, "代码或配置表"),
            };
            var wrong = new List<string>();
            foreach ((string what, Func<JourneyCheckpoints.Meta> meta, JourneyDef d, string step, JourneyCheckpoints.EnvInfo e, string expect) in negatives)
            {
                bool ok = JourneyCheckpoints.Validate(d, step, meta(), e, false, out string why, out _);
                if (ok || why == null || !why.Contains(expect))
                {
                    wrong.Add($"{what}（{(ok ? "放行了" : why)}）");
                }
            }
            Expect(wrong.Count == 0, $"负向矩阵 {negatives.Count} 种都拒绝并写明原因{(wrong.Count == 0 ? string.Empty : "；不对：" + string.Join("；", wrong))}");

            bool laterOk = JourneyCheckpoints.Validate(SampleDef(titleD: "丁（改）"), "b", good, env, false, out string whyLater, out _);
            Expect(laterOk && whyLater == null, "断点之后的步骤改了（迭代时最常见：修后面的步骤）→ 断点照样能用");

            var changed = new JourneyCheckpoints.EnvInfo { CodeHash = "bbbb2222", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 };
            bool allowed = JourneyCheckpoints.Validate(def, "b", good, changed, true, out string whyAllow, out string warnAllow);
            Expect(allowed && whyAllow == null && warnAllow != null && warnAllow.Contains("--allow-code-change") && warnAllow.Contains("只供迭代参考"),
                $"代码指纹不同时只有显式 --allow-code-change 才放行，并写明“{warnAllow}”");

            string refusal = JourneyBotHost.RefusalReport("FGJ-M3", "rate_t1", "旅程版本不匹配");
            Expect(refusal.Contains("请重新完整跑：bash tools/unity-journey.sh FGJ-M3") && refusal.Contains("结论：FAIL（断点不可用，没有开跑）") && refusal.Contains("旅程版本不匹配"),
                "断点不可用时的报告：写明原因、“请重新完整跑”的命令、结论 FAIL（不开跑，命令行退出码 3）");
        }

        private static void CheckResumeDef()
        {
            var env = new JourneyCheckpoints.EnvInfo { CodeHash = "aaaa1111", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 };
            JourneyDef def = SampleDef();
            bool restored = false;
            def.OnCheckpointRestored = c => restored = true;
            JourneyDef r = JourneyCheckpoints.BuildResumeDef(def, "b", "不存在的目录", MetaFor(def, "b", env), null);
            string[] ids = r.Steps.Select(s => s.Id).ToArray();
            string[] want = { "play", "ckpt_files", "ckpt_menu", "ckpt_slot", "ckpt_loaded", "ckpt_speed", "c", "d" };
            Expect(ids.SequenceEqual(want) && r.ResumedFrom == "b" && r.CheckpointAfter.Length == 0 && r.Version == def.Version && r.Seed == def.Seed
                   && r.OnCheckpointRestored == def.OnCheckpointRestored && !restored,
                $"续跑旅程 = 进 Play + 断点前置（放回文件 → 主菜单“读取”→ 点槽位 → 读档进家园恢复变量 → 恢复倍速）+ 断点之后的步骤：{string.Join(" → ", ids)}；续跑不再写断点");
            Expect(JourneyCheckpoints.NotRestoredVars.Contains("saves"),
                "续跑恢复旅程变量时不恢复本次运行自己的临时存档目录（saves 用新建的那个）");
        }

        private sealed class FakeClock : IJourneyClock
        {
            public double T;
            public double Now => T;
        }

        private static void CheckRunnerHooks()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            JourneyDef def = SampleDef();
            def.Steps[1].OnEnter = c => c.Set("workerA", "7");
            var written = new List<(string id, int index, int vars)>();
            var r = new JourneyRunner(def, store, clock)
            {
                CheckpointWriter = (runner, step, index) => written.Add((step.Id, index, runner.SnapshotVars().Count)),
            };
            r.Start();
            for (int i = 0; i < 100 && r.Tick(); i++)
            {
                clock.T += 1;
            }
            Expect(r.Result == JourneyRunner.ResultPass && written.Count == 2 && written[0] == ("b", 2, 1) && written[1] == ("c", 3, 1)
                   && r.SnapshotVars().TryGetValue("workerA", out string w) && w == "7" && r.SummaryJson(true).Contains("\"checkpoints\":[\"b\",\"c\"]"),
                $"运行器只在登记的断点步骤（b、c）完成后调写断点，参数是该步与序号；变量快照带着步骤写下的变量；摘要 JSON 列出写过的断点（{string.Join("、", written.Select(x => x.id))}）");

            var clock2 = new FakeClock();
            var r2 = new JourneyRunner(SampleDef(), new MemoryJourneyStore(), clock2)
            {
                CheckpointWriter = (runner, step, index) => throw new IOException("磁盘满了"),
            };
            r2.Start();
            for (int i = 0; i < 100 && r2.Tick(); i++)
            {
                clock2.T += 1;
            }
            Expect(r2.Result == JourneyRunner.ResultPass && r2.Lines.Count(l => l.Contains("没写成（不影响结论）") && l.Contains("磁盘满了")) == 2,
                "写断点失败（例如磁盘满）只记一行，不改变旅程结论（断点只是迭代工具）");

            var env = new JourneyCheckpoints.EnvInfo { CodeHash = "aaaa1111", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 };
            JourneyDef plain = SampleDef();
            JourneyDef resumed = JourneyCheckpoints.BuildResumeDef(plain, "c", "不存在的目录", MetaFor(plain, "c", env), null);
            resumed.Steps = resumed.Steps.Where(s => !s.Id.StartsWith("ckpt_", StringComparison.Ordinal)).ToList(); // 前置步骤要真进 Play，这里只看报告口径
            var clock3 = new FakeClock();
            var r3 = new JourneyRunner(resumed, new MemoryJourneyStore(), clock3);
            r3.Start();
            for (int i = 0; i < 100 && r3.Tick(); i++)
            {
                clock3.T += 1;
            }
            Expect(r3.Result == JourneyRunner.ResultPass && r3.Lines.Any(l => l.Contains("⚠ 断点续跑") && l.Contains("交付验收必须从主菜单完整跑"))
                   && r3.Lines.Any(l => l.Contains("结论：PASS") && l.Contains("断点续跑：从“c”之后，只用于迭代，不是交付验收"))
                   && r3.SummaryJson(true).Contains("\"resumedFrom\":\"c\""),
                "续跑的报告开头与结论行都写明“断点续跑、只用于迭代、交付验收必须完整跑”，摘要 JSON 记 resumedFrom");
        }

        private static void CheckCodeHashAndFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bingames-ckpt-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                string assets = Path.Combine(dir, "Assets");
                Directory.CreateDirectory(Path.Combine(assets, "GameScripts", "B"));
                Directory.CreateDirectory(Path.Combine(assets, "GameScripts", "A"));
                Directory.CreateDirectory(Path.Combine(assets, "GameRes", "Raw", "Configs"));
                File.WriteAllText(Path.Combine(assets, "GameScripts", "A", "x.cs"), "class X {}");
                File.WriteAllText(Path.Combine(assets, "GameScripts", "B", "y.cs"), "class Y {}");
                File.WriteAllText(Path.Combine(assets, "GameScripts", "B", "y.cs.meta"), "guid: 1");
                File.WriteAllText(Path.Combine(assets, "GameRes", "Raw", "Configs", "t.bytes"), "1234");
                string h1 = JourneyCheckpoints.ComputeCodeHash(assets, out int n1);
                string h2 = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(assets, "GameScripts", "B", "y.cs.meta"), "guid: 2");
                string hMeta = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(assets, "GameScripts", "B", "y.cs"), "class Y { int z; }");
                string hCode = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(assets, "GameScripts", "B", "y.cs"), "class Y {}");
                File.WriteAllText(Path.Combine(assets, "GameRes", "Raw", "Configs", "t.bytes"), "1235");
                string hTable = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(assets, "GameRes", "Raw", "Configs", "t.bytes"), "1234");
                File.Move(Path.Combine(assets, "GameScripts", "A", "x.cs"), Path.Combine(assets, "GameScripts", "A", "x2.cs"));
                string hRename = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                Expect(h1 == h2 && n1 == 3 && hMeta == h1 && hCode != h1 && hTable != h1 && hRename != h1 && h1.Length == 64,
                    $"代码指纹：同一份内容两次相同（{n1} 个文件）；改 .meta 不变；改源码 / 改配置表 / 改文件名都会变（SHA-256，按相对路径排序，与目录枚举顺序无关）");

                // 审查修复：界面布局与预制体也进指纹（改了按钮 / 组件参数，断点之前的步骤跑出来的世界会不同）；batchmode 每次改写的 Fonts 不进。
                string ui = Path.Combine(assets, "GameRes", "Raw", "UI", "Panel");
                string fonts = Path.Combine(assets, "GameRes", "Raw", "UI", "Fonts");
                Directory.CreateDirectory(ui);
                Directory.CreateDirectory(fonts);
                string hNoUi = JourneyCheckpoints.ComputeCodeHash(assets, out int nNoUi);
                File.WriteAllText(Path.Combine(ui, "p.uxml"), "<ui:UXML/>");
                File.WriteAllText(Path.Combine(ui, "p.uss"), ".a {}");
                File.WriteAllText(Path.Combine(ui, "p.prefab"), "--- !u!1 &1");
                File.WriteAllText(Path.Combine(ui, "p.uxml.meta"), "guid: 3");
                File.WriteAllText(Path.Combine(ui, "icon.png"), "png");
                File.WriteAllText(Path.Combine(fonts, "font SDF.asset"), "atlas 1");
                string hUi1 = JourneyCheckpoints.ComputeCodeHash(assets, out int nUi);
                File.WriteAllText(Path.Combine(ui, "p.uxml"), "<ui:UXML><ui:Button/></ui:UXML>");
                string hUxml = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(ui, "p.uxml"), "<ui:UXML/>");
                File.WriteAllText(Path.Combine(ui, "p.prefab"), "--- !u!1 &2");
                string hPrefab = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                File.WriteAllText(Path.Combine(ui, "p.prefab"), "--- !u!1 &1");
                File.WriteAllText(Path.Combine(fonts, "font SDF.asset"), "atlas 2");
                File.WriteAllText(Path.Combine(ui, "icon.png"), "png2");
                File.WriteAllText(Path.Combine(ui, "p.uxml.meta"), "guid: 4");
                string hFont = JourneyCheckpoints.ComputeCodeHash(assets, out _);
                Expect(hUi1 != hNoUi && nUi == nNoUi + 3 && hUxml != hUi1 && hPrefab != hUi1 && hFont == hUi1,
                    $"代码指纹也含 GameRes/Raw 的界面布局与预制体（.prefab / .uxml / .uss，{nNoUi} → {nUi} 个文件）：改 UXML / 预制体会变；" +
                    "batchmode 会改写的 Fonts 目录、图片、.meta 不进指纹（改了不变）");

                string from = Path.Combine(dir, "from");
                Directory.CreateDirectory(Path.Combine(from, "layouts"));
                File.WriteAllText(Path.Combine(from, "campaign_slot0.json"), "{}");
                File.WriteAllText(Path.Combine(from, "layouts", "抽水线.json"), "[]");
                string to = Path.Combine(dir, "to");
                Directory.CreateDirectory(to);
                File.WriteAllText(Path.Combine(to, "campaign_slot0.json"), "旧");
                int copied = JourneyCheckpoints.CopyTree(from, to);
                Expect(copied == 2 && File.ReadAllText(Path.Combine(to, "campaign_slot0.json")) == "{}" && File.Exists(Path.Combine(to, "layouts", "抽水线.json"))
                       && JourneyCheckpoints.CopyTree(Path.Combine(dir, "没有"), to) == 0,
                    "断点文件放回：整棵目录树复制（含子目录里的布局库文件、覆盖同名存档）；源不存在时复制 0 个");

                var env = new JourneyCheckpoints.EnvInfo { CodeHash = "aaaa1111", SaveSchema = 2, ContentVersion = 1, GeneratorVersion = 2 };
                JourneyCheckpoints.Meta meta = MetaFor(SampleDef(), "c", env);
                string mdir = Path.Combine(dir, "meta");
                Directory.CreateDirectory(mdir);
                File.WriteAllText(Path.Combine(mdir, JourneyCheckpoints.MetaFile), JsonUtility.ToJson(meta, true));
                JourneyCheckpoints.Meta back = JourneyCheckpoints.ReadMeta(mdir);
                File.WriteAllText(Path.Combine(mdir, JourneyCheckpoints.MetaFile), "{坏了");
                JourneyCheckpoints.Meta broken = JourneyCheckpoints.ReadMeta(mdir);
                Expect(back != null && back.stepsHash == meta.stepsHash && back.vars.Count == 2 && back.vars[1].k == "workerA" && back.vars[1].v == "7"
                       && Math.Abs(back.speed - 3f) < 1e-6 && JourneyCheckpoints.Validate(SampleDef(), "c", back, env, false, out _, out _)
                       && broken == null && JourneyCheckpoints.ReadMeta(Path.Combine(dir, "没有")) == null,
                    "断点元数据存读往返（步骤指纹、旅程变量、倍速原样读回且读回后照样通过校验）；文件坏了 / 不存在读成“没有断点”（随后按没有断点拒绝）");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
            }
        }

        private static void CheckCatalogCheckpoints()
        {
            var bad = new List<string>();
            var registered = new List<string>();
            foreach (string id in JourneyCatalog.Ids)
            {
                JourneyDef def = JourneyCatalog.Create(id);
                foreach (string s in def.CheckpointAfter ?? Array.Empty<string>())
                {
                    int idx = def.Steps.FindIndex(x => x.Id == s);
                    if (idx <= 0)
                    {
                        bad.Add($"{id}/{s}");
                    }
                    else
                    {
                        registered.Add($"{id}/{s}");
                    }
                }
            }
            JourneyDef m3 = JourneyCatalog.Create(FgjM3Journey.Id);
            Expect(bad.Count == 0 && m3.CheckpointAfter.Length >= 2 && m3.OnCheckpointRestored != null && m3.Version >= 1,
                $"旅程登记的断点步骤都在旅程里（不是第 1 步）：{string.Join("、", registered)}；FGJ-M3 登记了断点与读档后的恢复（布局库）{(bad.Count == 0 ? string.Empty : "；不存在：" + string.Join("、", bad))}");
            Expect(!JourneyCheckpoints.NeutralState(out string why) && why == "不在游戏中",
                $"写断点前查中性状态：编辑模式里不在游戏中 → 不写（“{why}”）");
            string root = JourneyCheckpoints.RootDir;
            Expect(!string.IsNullOrEmpty(root) && (Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_CHECKPOINT_DIR") != null || root.EndsWith(".journey-checkpoints", StringComparison.Ordinal)),
                $"断点放在仓库根的 .journey-checkpoints（gitignore，只在本机）：{root}");
        }

        // ── D. 同一轮自检里共享 python 自测 ─────────────────────────────────────

        private static void CheckSharedPython()
        {
            Line("  · D. 同一轮自检里参数与输入都相同的 python 自测只真跑一次（check_luban --selftest 原来一轮跑 6 遍）");
            string root = Path.Combine(Path.GetTempPath(), "bingames-qapython-" + Guid.NewGuid().ToString("N"));
            bool ownRound = !QaPython.Active; // 单独从菜单跑本段时自己开一轮，测完关掉
            try
            {
                if (ownRound)
                {
                    QaPython.ClearShared();
                }
                string tables = Path.Combine(root, "tools", "cell_tables");
                Directory.CreateDirectory(tables);
                File.WriteAllText(Path.Combine(tables, "a.py"), "x = 1");
                const string args = "-c \"import random; print(random.random())\"";
                (int c1, string o1) = QaPython.RunShared(root, args, out bool reused1);
                (int c2, string o2) = QaPython.RunShared(root, args, out bool reused2);
                File.WriteAllText(Path.Combine(tables, "a.py"), "x = 2");
                (int c3, string o3) = QaPython.RunShared(root, args, out bool reused3);
                (int c4, string o4) = QaPython.RunShared(root, args + " ", out bool reused4);
                bool ran = c1 == 0 && c2 == 0 && c3 == 0 && c4 == 0 && o1.Trim().Length > 0;
                Expect(ran && !reused1 && reused2 && o2 == o1 && !reused3 && o3 != o1 && !reused4 && o4 != o3,
                    "同一轮里：参数与输入相同 → 复用第一次真跑的退出码与输出；tools/cell_tables 里任一文件内容变了 → 重新真跑；参数不同 → 各跑各的" +
                    $"（随机数输出 {o1.Trim()} / {o2.Trim()} / {o3.Trim()}）");
                string f1 = QaPython.InputFingerprint(root);
                File.WriteAllText(Path.Combine(tables, "b.md"), "说明");
                string f2 = QaPython.InputFingerprint(root);
                Directory.CreateDirectory(Path.Combine(tables, "__pycache__"));
                File.WriteAllText(Path.Combine(tables, "__pycache__", "a.cpython.pyc"), "缓存");
                string f3 = QaPython.InputFingerprint(root);
                Expect(f1 != f2 && f2 == f3 && f1.Length == 64, "输入指纹：新增任何文件就变；__pycache__ 不算输入");
            }
            finally
            {
                if (ownRound)
                {
                    QaPython.EndShared();
                }
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
            }
            string temp = Path.GetTempPath();
            (int c5, string o5) = QaPython.RunShared(temp, "-c \"import random; print(random.random())\"", out bool reused5);
            (int c6, string o6) = QaPython.RunShared(temp, "-c \"import random; print(random.random())\"", out bool reused6);
            if (ownRound)
            {
                Expect(c5 == 0 && c6 == 0 && !reused5 && !reused6 && o5 != o6, "一轮结束后（单独从菜单跑某一段时）不共享：每次都真跑（两次随机数不同）");
            }
            else
            {
                Expect(QaPython.Active && c5 == 0 && reused6 && o6 == o5, "全量 / 分段自检进行中共享一直开着：后面的段复用前面真跑的结果");
            }
        }

        // ── E. 改表主路径：check_luban 自测并行后，gen_all 第 7 步 / run_luban.sh 仍然跑得通 ─────────

        /// <summary>
        /// 审查 P0：自测改成多进程后，Windows 的子进程会把入口脚本按 __mp_main__ 重新导入；入口脚本没有 main 保护、顶层直接调 selftest() 时
        /// 每个子进程又去开进程池，启动即崩（step7 → BrokenProcessPool，gen_all 第 7 步与 run_luban.sh 失败）。全量自检原来只走 check_luban.py --selftest
        /// （自带保护），测不出来。这里①静态扫描：打开并行（selftest(parallel=True)）的入口脚本都有 main 保护；②真跑 step7_check_luban.py
        /// （gen_all.py 第 7 步与 run_luban.sh 调的就是它）：退出码 0、自测全部通过、真实数据检查通过、没有进程池崩溃。
        /// </summary>
        private static void CheckLubanEntryScripts()
        {
            Line("  · E. 改表主路径：gen_all.py 第 7 步 / run_luban.sh 调的 step7_check_luban.py 在自测并行后照样跑得通");
            string repo = JourneyCheckpoints.RepoRoot();
            if (repo == null)
            {
                Fail("找不到仓库根（tools/cell_tables/gen_all.py），没法验证改表主路径");
                return;
            }
            string tables = Path.Combine(repo, "tools", "cell_tables");
            var openers = new List<string>();
            var unguarded = new List<string>();
            foreach (string py in Directory.GetFiles(tables, "*.py", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(py, Encoding.UTF8);
                if (!text.Contains("selftest(parallel=True)"))
                {
                    continue;
                }
                openers.Add(Path.GetFileName(py));
                if (!text.Contains("if __name__ == \"__main__\":"))
                {
                    unguarded.Add(Path.GetFileName(py));
                }
            }
            Expect(openers.Count >= 2 && openers.Contains("check_luban.py") && openers.Contains("step7_check_luban.py") && unguarded.Count == 0,
                $"打开并行自测的入口脚本都有 if __name__ == \"__main__\" 保护（扫到 {openers.Count} 个：{string.Join("、", openers)}" +
                $"{(unguarded.Count == 0 ? string.Empty : "；没有保护：" + string.Join("、", unguarded))}）");

            string gen = File.ReadAllText(Path.Combine(tables, "gen_all.py"), Encoding.UTF8);
            string luban = File.Exists(Path.Combine(tables, "run_luban.sh")) ? File.ReadAllText(Path.Combine(tables, "run_luban.sh"), Encoding.UTF8) : string.Empty;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            (int code, string output) = QaPython.Run(repo, "tools/cell_tables/step7_check_luban.py", 300000);
            sw.Stop();
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(output ?? string.Empty, @"自测 (\d+)/(\d+) 通过");
            bool allSelf = m.Success && m.Groups[1].Value == m.Groups[2].Value && int.Parse(m.Groups[2].Value) >= 100;
            bool crashed = (output ?? string.Empty).Contains("BrokenProcessPool") || (output ?? string.Empty).Contains("Traceback");
            Expect(gen.Contains("step7_check_luban.py") && luban.Contains("step7_check_luban.py") && code == 0 && allSelf && !crashed
                   && output.Contains("Luban 已知坑检查：通过"),
                $"真跑 step7_check_luban.py（gen_all.py 第 7 步与 run_luban.sh 的入口）：退出码 {code}、{(m.Success ? m.Value : "没有自测结论行")}、真实数据检查通过、" +
                $"没有进程池崩溃（{sw.Elapsed.TotalSeconds:F0} 秒）{(code == 0 && allSelf && !crashed ? string.Empty : "：" + Tail(output))}");
        }

        private static string Tail(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "（无输出）";
            }
            string t = s.Trim();
            return t.Length <= 600 ? t : "…" + t.Substring(t.Length - 600);
        }

        // ── 工具 ──────────────────────────────────────────────────────────────

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
