using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-QA-01 负向：旅程机器人在失败路径上的行为（重试、超时、报告）。用内存存储 + 假时钟逐步驱动框架本身，
    /// 不进 Play、不依赖场景，所以能放进全量自检（CellFrameworkValidate.RunAll）。
    /// </summary>
    public static class JourneyRunnerSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private sealed class FakeClock : IJourneyClock
        {
            public double T;
            public double Now => T;
        }

        [MenuItem("BinGames/QA/自检/旅程机器人框架")]
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
            Line("\n[旅程机器人] 旅程机器人框架（FG0-QA-01）");
            try
            {
                CheckHappyPath();
                CheckTimeout();
                CheckRetryThenPass();
                CheckRetriesExhausted();
                CheckExplicitFailAndException();
                CheckTotalTimeoutAndErrorLog();
                CheckResumeAfterReload();
                CheckTransientUiRetry();
                CheckCatalog();
            }
            catch (Exception e)
            {
                Fail($"旅程机器人自检抛异常：{e}");
            }
            Line($"  · [旅程机器人] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static JourneyStep Step(string id, Func<JourneyContext, StepOutcome> tick, double timeout = 5, int retries = 0,
            Action<JourneyContext> onEnter = null, Action<JourneyContext> onRetry = null) => new JourneyStep
        {
            Id = id,
            Title = id,
            TimeoutSeconds = timeout,
            MaxRetries = retries,
            Tick = tick,
            OnEnter = onEnter,
            OnRetry = onRetry,
        };

        private static JourneyRunner NewRunner(FakeClock clock, MemoryJourneyStore store, params JourneyStep[] steps)
        {
            var def = new JourneyDef { Id = "TEST", Title = "自检", Seed = 42, TotalTimeoutSeconds = 100, Steps = steps.ToList() };
            return new JourneyRunner(def, store, clock);
        }

        /// <summary>驱动到结束（每次推进假时钟 dt 秒），返回驱动次数；超过上限视为卡死。</summary>
        private static int Drive(JourneyRunner r, FakeClock clock, double dt = 1, int max = 1000)
        {
            int n = 0;
            while (r.Tick() && n < max)
            {
                clock.T += dt;
                n++;
            }
            return n;
        }

        private static void CheckHappyPath()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            int entered = 0;
            int waited = 0;
            JourneyRunner r = NewRunner(clock, store,
                Step("a", c => StepOutcome.Done("甲"), onEnter: c => entered++),
                Step("b", c => ++waited < 3 ? StepOutcome.Wait : StepOutcome.Done(), onEnter: c => entered++),
                Step("c", c => c.GetInt("x") == 7 ? StepOutcome.Done() : StepOutcome.Fail("跨步状态丢了"), onEnter: c => { entered++; c.SetInt("x", 7); }));
            r.Start();
            Drive(r, clock);
            string json = r.SummaryJson(true);
            Expect(r.Result == JourneyRunner.ResultPass && entered == 3 && r.FailStepId.Length == 0
                   && r.Lines.Any(l => l.Contains("结论：PASS")) && json.Contains("\"id\":\"b\"") && json.Contains("\"result\":\"PASS\""),
                $"正常路径：三步依次完成（每步进入一次、等待条件满足才前进、跨步状态保留），报告有结论行与逐步耗时（{json.Length} 字节 JSON）");
        }

        private static void CheckTimeout()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            bool laterRan = false;
            JourneyRunner r = NewRunner(clock, store,
                Step("stuck", c => StepOutcome.Wait, timeout: 3),
                Step("later", c => { laterRan = true; return StepOutcome.Done(); }));
            r.Start();
            int ticks = Drive(r, clock);
            Expect(r.Result == JourneyRunner.ResultFail && r.FailStepId == "stuck" && r.FailReason.Contains("超时") && !laterRan && ticks < 10
                   && r.Lines.Any(l => l.Contains("失败于 stuck")),
                $"条件一直不满足：{ticks} 次驱动后按步骤超时失败，报告写明失败步骤与原因（{r.FailReason}），后面的步骤不再执行");
        }

        private static void CheckRetryThenPass()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            int enters = 0;
            int retries = 0;
            JourneyRunner r = NewRunner(clock, store,
                Step("flaky", c => c.Attempt < 2 ? StepOutcome.Retry("点击没生效") : StepOutcome.Done(), retries: 2,
                    onEnter: c => enters++, onRetry: c => retries++),
                Step("slow", c => c.Attempt == 0 ? StepOutcome.Wait : StepOutcome.Done(), timeout: 2, retries: 1));
            r.Start();
            Drive(r, clock);
            List<string> logged = r.List("retries");
            Expect(r.Result == JourneyRunner.ResultPass && enters == 3 && retries == 2 && logged.Count == 3
                   && logged.Count(x => x.StartsWith("slow:") && x.Contains("超时")) == 1,
                $"重试：点击没生效的步骤第 3 次成功（进入 {enters} 次、收拾 {retries} 次）；超时的步骤重来一次后成功；报告记下 {logged.Count} 次重试及原因");
        }

        private static void CheckRetriesExhausted()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            int enters = 0;
            JourneyRunner r = NewRunner(clock, store, Step("never", c => StepOutcome.Retry("按钮不可点"), retries: 2, onEnter: c => enters++));
            r.Start();
            Drive(r, clock);
            Expect(r.Result == JourneyRunner.ResultFail && enters == 3 && r.FailReason.Contains("按钮不可点") && r.FailReason.Contains("已重试 2 次"),
                $"重试用完：尝试 {enters} 次后失败，原因写明最后一次的失败与已重试次数（{r.FailReason}）");
        }

        private static void CheckExplicitFailAndException()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            JourneyRunner r = NewRunner(clock, store, Step("assert", c => StepOutcome.Fail("状态指纹不一致"), retries: 5));
            r.Start();
            Drive(r, clock);
            var clock2 = new FakeClock();
            JourneyRunner r2 = NewRunner(clock2, new MemoryJourneyStore(), Step("boom", c => throw new InvalidOperationException("空引用")));
            r2.Start();
            Drive(r2, clock2);
            Expect(r.Result == JourneyRunner.ResultFail && r.FailReason == "状态指纹不一致" && r.List("retries").Count == 0
                   && r2.Result == JourneyRunner.ResultFail && r2.FailStepId == "boom" && r2.FailReason.Contains("InvalidOperationException"),
                "断言失败不重试（即使允许重试）；步骤抛异常按失败报告异常类型，不会把驱动循环带崩");
        }

        private static void CheckTotalTimeoutAndErrorLog()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            var def = new JourneyDef
            {
                Id = "TEST", Title = "总超时", Seed = 1, TotalTimeoutSeconds = 5,
                Steps = new List<JourneyStep> { Step("long", c => StepOutcome.Wait, timeout: 100) },
            };
            var r = new JourneyRunner(def, store, clock);
            r.Start();
            Drive(r, clock);
            bool finishCalled = false;
            var clock2 = new FakeClock();
            var def2 = new JourneyDef
            {
                Id = "TEST", Title = "报错", Seed = 1,
                Steps = new List<JourneyStep> { Step("x", c => StepOutcome.Wait, timeout: 100) },
                OnFinish = (c, pass) => finishCalled = !pass,
            };
            var r2 = new JourneyRunner(def2, new MemoryJourneyStore(), clock2);
            r2.Start();
            r2.Tick();
            r2.OnLog("NullReferenceException: boom", "at Foo.Bar()", LogType.Exception);
            r2.OnLog("编辑器内部", "at UnityEditor.Search.Index()", LogType.Error);
            r2.OnLog("普通日志", string.Empty, LogType.Log);
            clock2.T += 1;
            Drive(r2, clock2);
            Expect(r.Result == JourneyRunner.ResultFail && r.FailReason.Contains("总超时")
                   && r2.Result == JourneyRunner.ResultFail && r2.Errors == 1 && r2.FailReason.Contains("1 条报错") && finishCalled,
                "总超时兜底；运行中出现一条异常日志即失败（编辑器搜索索引的内部报错与普通日志不算），失败时也执行收尾");
        }

        private static void CheckResumeAfterReload()
        {
            var clock = new FakeClock();
            var store = new MemoryJourneyStore();
            int enters = 0;
            Func<JourneyStep[]> steps = () => new[]
            {
                Step("one", c => StepOutcome.Done()),
                Step("two", c => c.StepElapsed >= 2 ? StepOutcome.Done() : StepOutcome.Wait, timeout: 10, onEnter: c => enters++),
                Step("three", c => StepOutcome.Done()),
            };
            JourneyRunner r = NewRunner(clock, store, steps());
            r.Start();
            r.Tick();
            clock.T += 1;
            r.Tick();
            int stepBefore = r.StepIndex;
            // 进 Play 重载域：旧实例丢了，按同一份存储重建（Host 的静态构造做的事）。
            JourneyRunner resumed = NewRunner(clock, store, steps());
            clock.T += 1;
            Drive(resumed, clock);
            Expect(stepBefore == 1 && resumed.Result == JourneyRunner.ResultPass && enters == 1,
                "域重载后续跑：新实例从同一步接着等（进入动作不重复执行、步骤计时不清零），然后跑完");
        }

        /// <summary>
        /// FG5-E2E-01 修复轮（审查 P2）：暂时的 UI 遮挡免计重试只认“这一次尝试里那次点击失败”且步骤报的就是它；
        /// 同一步里与点击无关的失败、上一步留下的暂时失败都照常计重试；免计重试前同样执行 OnRetry。
        /// </summary>
        private static void CheckTransientUiRetry()
        {
            const string toast = "控件 X 被通知弹出条挡着（自检），等它收起再点";
            try
            {
                // ① 点击被弹出条挡住、步骤报的就是这次点击失败：不算重试（允许重试 0 次也能过），1 秒后重做这一步，重做前收拾一次。
                var clock = new FakeClock();
                int cleaned = 0;
                JourneyRunner r = NewRunner(clock, new MemoryJourneyStore(),
                    Step("toast", c =>
                    {
                        if (c.Attempt == 0)
                        {
                            JourneyInput.DebugMarkTransient(toast);
                            return StepOutcome.Retry("点“生产”后队列是空的（" + JourneyInput.LastUiFailure + "）");
                        }
                        return StepOutcome.Done();
                    }, onRetry: c => cleaned++));
                r.Start();
                Drive(r, clock, 0.25);
                bool freeOk = r.Result == JourneyRunner.ResultPass && r.List("transient").Count == 1 && r.List("retries").Count == 0 && cleaned == 1;

                // ② 同一次尝试里先有一次暂时遮挡、随后报的是与点击无关的失败：照常算重试（不允许重试就失败）。
                var clock2 = new FakeClock();
                JourneyRunner r2 = NewRunner(clock2, new MemoryJourneyStore(),
                    Step("logic", c =>
                    {
                        JourneyInput.DebugMarkTransient(toast);
                        return StepOutcome.Retry("仓库里的合金数量不对");
                    }));
                r2.Start();
                Drive(r2, clock2, 0.25);
                bool logicOk = r2.Result == JourneyRunner.ResultFail && r2.FailStepId == "logic" && r2.List("transient").Count == 0;

                // ③ 上一步留下的暂时失败不算到下一步头上（进入一步时清掉）：下一步报同样的文字也照常算重试。
                var clock3 = new FakeClock();
                JourneyRunner r3 = NewRunner(clock3, new MemoryJourneyStore(),
                    Step("before", c =>
                    {
                        JourneyInput.DebugMarkTransient(toast);
                        return StepOutcome.Done();
                    }),
                    Step("after", c => StepOutcome.Retry("还是没点上（" + toast + "）")));
                r3.Start();
                Drive(r3, clock3, 0.25);
                bool staleOk = r3.Result == JourneyRunner.ResultFail && r3.FailStepId == "after" && r3.List("transient").Count == 0;

                Expect(freeOk && logicOk && staleOk,
                    $"暂时遮挡免计重试：报的就是本次点击失败才免计（免计 {r.List("transient").Count}、重试 {r.List("retries").Count}、收拾 {cleaned}）；" +
                    $"与点击无关的失败照常计重试（{r2.Result}）；上一步留下的暂时失败不带进下一步（{r3.Result}）");
            }
            finally
            {
                JourneyInput.BeginStepAttempt(); // 不把自检留下的标志带给后面的旅程
            }
        }

        private static void CheckCatalog()
        {
            JourneyDef m0 = JourneyCatalog.Create(FgjM0Journey.Id);
            bool ids = m0 != null && m0.Steps.Count > 0 && m0.Steps.Select(s => s.Id).Distinct().Count() == m0.Steps.Count;
            bool bounded = m0 != null && m0.Steps.All(s => s.TimeoutSeconds > 0 && s.Tick != null) && m0.TotalTimeoutSeconds > 0;
            Expect(ids && bounded && m0.Seed == FgjM0Journey.TestSeed && JourneyCatalog.Create("NOPE") == null,
                $"登记表：FGJ-M0 有 {m0?.Steps.Count ?? 0} 步，步骤 ID 不重复、每步都有超时与检查，种子固定为 {FgjM0Journey.TestSeed}；未知 ID 返回空");
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
