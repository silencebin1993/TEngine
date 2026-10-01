using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG-TOOL-01（用户 2026-09-30 明确要求）：性能断言只测一次、超线降为“性能警告”。
    ///
    /// 判定规则（阈值本身不改）：
    /// - 每项指标“测量值 ≤ 阈值”（或调用方原本写的“&lt; 阈值”）都满足 → 通过（✓）；
    /// - 有指标超出阈值、但都不超过阈值的 2 倍 → 性能警告（⚠，**不计入断言失败**，不让全量自检 / 冒烟 / 基线对比判 FAIL），
    ///   报告里写明数字、超出倍数与测量时整机 CPU 占用（用户会同时开游戏等程序）；
    /// - 任一指标超过阈值 2 倍、测量值不是有限数（量坏了）→ 失败（✗）。
    /// 功能条件（规模没达标、结果不对、分配超标……）不属于性能，照常硬判失败：调用方把它们放在 <c>functionalOk</c> 里。
    ///
    /// “只测一次”：本类不提供重测入口；调用方不得在超线后重测、不得等负载下降再量。
    /// 警告逐条汇总（<see cref="Warnings"/>），全量自检末尾列出，留给 FG15-SYS-02 性能门禁统一处理。
    /// </summary>
    public static class PerfGate
    {
        /// <summary>超过阈值的这个倍数才算失败。</summary>
        public const double FailFactor = 2.0;

        /// <summary>警告行前缀（unity-validate.sh 按 “⚠ 性能警告” 计数；不含 ✓ / ✗，不会被当成通过或失败）。</summary>
        public const string WarnTag = "⚠ 性能警告";

        public enum Level
        {
            Pass,
            Warn,
            Fail,
        }

        /// <summary>一项“越小越好”的性能指标：测量值与阈值。<see cref="Strict"/> = 原断言写的是“&lt;”（等于阈值也算超线）。</summary>
        public readonly struct Metric
        {
            public readonly string Name;
            public readonly double Value;
            public readonly double Limit;
            public readonly bool Strict;

            public Metric(string name, double value, double limit, bool strict)
            {
                Name = name;
                Value = value;
                Limit = limit;
                Strict = strict;
            }

            /// <summary>测量值不是有限数（NaN / 无穷）= 量坏了。</summary>
            public bool Broken => double.IsNaN(Value) || double.IsInfinity(Value) || double.IsNaN(Limit) || double.IsInfinity(Limit);

            /// <summary>超出阈值（与原断言同一比较方式）。</summary>
            public bool Over => Broken || (Strict ? !(Value < Limit) : !(Value <= Limit));

            /// <summary>超过阈值 2 倍（阈值 ≤ 0 时，测量值 &gt; 0 即算超过 2 倍）。</summary>
            public bool OverFailFactor => Broken || (Limit > 0 ? Value > Limit * FailFactor : Value > 0);

            public double Ratio => Limit > 0 ? Value / Limit : (Value > 0 ? double.PositiveInfinity : 0);
        }

        /// <summary>测量值 ≤ 阈值。</summary>
        public static Metric Le(double value, double limit, string name = null) => new Metric(name, value, limit, false);

        /// <summary>测量值 &lt; 阈值（原断言写的是严格小于时用它，保持通过边界不变）。</summary>
        public static Metric Lt(double value, double limit, string name = null) => new Metric(name, value, limit, true);

        public static Level Judge(params Metric[] metrics)
        {
            if (metrics == null || metrics.Length == 0)
            {
                return Level.Pass;
            }
            bool over = false;
            foreach (Metric m in metrics)
            {
                if (m.Over && m.OverFailFactor)
                {
                    return Level.Fail;
                }
                over |= m.Over;
            }
            return over ? Level.Warn : Level.Pass;
        }

        /// <summary>超线指标的说明：“p95 2.61 超阈值 2（1.31 倍）”（不含“【】”，以免打断比对工具剥离追加说明）。</summary>
        public static string DescribeOver(Metric[] metrics)
        {
            var parts = new List<string>();
            foreach (Metric m in metrics.Where(x => x.Over))
            {
                string name = string.IsNullOrEmpty(m.Name) ? "测量值" : m.Name;
                parts.Add(m.Broken
                    ? $"{name} {m.Value.ToString(CultureInfo.InvariantCulture)}（不是有限数，测量坏了）"
                    : $"{name} {F(m.Value)} 超阈值 {F(m.Limit)}（{(double.IsInfinity(m.Ratio) ? "∞" : m.Ratio.ToString("0.00", CultureInfo.InvariantCulture))} 倍）");
            }
            return string.Join("、", parts);
        }

        // ── 汇总 ────────────────────────────────────────────────────────────────

        private static readonly List<string> WarningList = new List<string>();
        private static readonly List<string> JudgedList = new List<string>();
        private static int _passCount;
        private static int _failCount;

        /// <summary>本轮（全量自检 / 单段 / 冒烟 / 基线）出现过的性能警告，按出现顺序。</summary>
        public static IReadOnlyList<string> Warnings => WarningList;

        /// <summary>本轮经本类判定的性能断言（消息原文，按出现顺序）：用来核对“带计时阈值的断言是否都走了这里”。</summary>
        public static IReadOnlyList<string> Judged => JudgedList;

        /// <summary>自检用：记下当前汇总（警告、判定清单、计数），之后 <see cref="RestoreStateForTests"/> 原样放回——自检里故意造的警告不混进全量自检的汇总。</summary>
        public static object SaveStateForTests() => (WarningList.ToList(), JudgedList.Count, _passCount, _failCount);

        public static void RestoreStateForTests(object state)
        {
            (List<string> warnings, int judged, int pass, int fail) = ((List<string>, int, int, int))state;
            WarningList.Clear();
            WarningList.AddRange(warnings);
            if (JudgedList.Count > judged)
            {
                JudgedList.RemoveRange(judged, JudgedList.Count - judged);
            }
            _passCount = pass;
            _failCount = fail;
        }

        /// <summary>开始新一轮：清空警告汇总与判定清单，重置 CPU 取样窗口。</summary>
        public static void ResetRun()
        {
            WarningList.Clear();
            JudgedList.Clear();
            _passCount = 0;
            _failCount = 0;
            MarkWindow();
        }

        /// <summary>
        /// 用调用方自己的断言函数报告一条“功能条件 + 性能指标”的断言：功能条件不满足或性能超阈值 2 倍 → <paramref name="expect"/>(false, …)；
        /// 性能超线不到 2 倍 → <paramref name="line"/> 写一行性能警告（不调用 expect，不计失败也不计通过）；否则 expect(true, message)。
        /// 返回判定等级（功能失败按 <see cref="Level.Fail"/>）。
        /// </summary>
        public static Level Expect(bool functionalOk, string message, Metric[] perf, Action<bool, string> expect, Action<string> line)
        {
            Level level = Judge(perf);
            JudgedList.Add(message);
            if (!functionalOk)
            {
                _failCount++;
                expect(false, message);
                MarkWindow();
                return Level.Fail;
            }
            switch (level)
            {
                case Level.Fail:
                    _failCount++;
                    expect(false, $"{message}　【性能超过阈值 {FailFactor:0} 倍判失败：{DescribeOver(perf)}；{CpuText()}】");
                    break;
                case Level.Warn:
                    // 追加说明放在“　【……】”里：tools/validate-assert-diff.py 比对断言名单时整段剥掉，警告与原来的通过算同一条断言。
                    string w = $"{message}　【{DescribeOver(perf)}；不到 {FailFactor:0} 倍，记警告不计失败、不重测；{CpuText()}】";
                    WarningList.Add(w);
                    line("  " + WarnTag + "：" + w);
                    break;
                default:
                    _passCount++;
                    expect(true, message);
                    break;
            }
            MarkWindow();
            return level;
        }

        // ── 整机 CPU 占用 ───────────────────────────────────────────────────────

        private static long _idle0;
        private static long _total0;
        private static long _proc0;
        private static readonly Stopwatch Wall = new Stopwatch();

        /// <summary>CPU 取样窗口从现在开始（全量自检每段开始、每条性能断言之后各调一次：警告里报的是“这段测量期间”的占用）。</summary>
        public static void MarkWindow()
        {
            if (TryGetSystemTimes(out long idle, out long total))
            {
                _idle0 = idle;
                _total0 = total;
            }
            _proc0 = TryGetProcessTime(out long proc) ? proc : 0;
            Wall.Restart();
        }

        /// <summary>
        /// 从上次 <see cref="MarkWindow"/> 到现在的整机 CPU 平均占用（与本进程占用）。窗口不足 0.25 秒时当场再取 0.25 秒（只在出警告时发生，不改变测量本身）。
        /// 非 Windows / 取不到时返回 null。
        /// </summary>
        public static (double machine, double self)? CpuUsage()
        {
            if (!TryGetSystemTimes(out _, out _))
            {
                return null;
            }
            if (!Wall.IsRunning)
            {
                MarkWindow(); // 进 Play 重载域后还没取过样（冒烟）：从现在起当场取样
            }
            if (Wall.Elapsed.TotalSeconds < 0.25)
            {
                System.Threading.Thread.Sleep(250);
            }
            if (!TryGetSystemTimes(out long idle, out long total))
            {
                return null;
            }
            long dTotal = total - _total0;
            long dIdle = idle - _idle0;
            if (dTotal <= 0)
            {
                return null;
            }
            double machine = Math.Max(0, Math.Min(1, 1.0 - (double)dIdle / dTotal));
            // 本进程占用：GetProcessTimes（Unity 的 Mono 里 Process.TotalProcessorTime 恒为 0，不能用）。100 纳秒为单位，与整机同一口径。
            double self = 0;
            if (TryGetProcessTime(out long procNow) && _proc0 > 0)
            {
                self = Math.Max(0, Math.Min(1, (double)(procNow - _proc0) / dTotal));
            }
            return (machine, self);
        }

        public static string CpuText()
        {
            (double machine, double self)? u = CpuUsage();
            return u.HasValue
                ? $"测量期间整机 CPU 平均占用 {u.Value.machine:P0}（其中本 Unity 进程 {u.Value.self:P0}，{Environment.ProcessorCount} 线程）"
                : "整机 CPU 占用取不到（非 Windows）";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public long Value => ((long)High << 32) | Low;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

        /// <summary>本进程累计 CPU 时间（kernel + user，100 纳秒为单位，所有线程合计）。</summary>
        private static bool TryGetProcessTime(out long total)
        {
            total = 0;
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
            {
                return false;
            }
            try
            {
                if (!GetProcessTimes(GetCurrentProcess(), out _, out _, out FileTime k, out FileTime u))
                {
                    return false;
                }
                total = k.Value + u.Value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Windows GetSystemTimes：idle / (kernel + user)，kernel 时间里含 idle。</summary>
        private static bool TryGetSystemTimes(out long idle, out long total)
        {
            idle = 0;
            total = 0;
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
            {
                return false;
            }
            try
            {
                if (!GetSystemTimes(out FileTime i, out FileTime k, out FileTime u))
                {
                    return false;
                }
                idle = i.Value;
                total = k.Value + u.Value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string F(double v) => v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture);

        /// <summary>汇总段（全量自检末尾）：没有警告时一行；有警告时逐条列出。</summary>
        public static void AppendSummary(StringBuilder report)
        {
            report.AppendLine($"性能断言 {JudgedList.Count} 条经 PerfGate 判定：通过 {_passCount}、性能警告 {WarningList.Count}、失败 {_failCount}" +
                              $"（警告 = 超阈值不到 {FailFactor:0} 倍，不计入失败；只测一次不重测；留给 FG15-SYS-02 性能门禁统一处理）");
            for (int i = 0; i < WarningList.Count; i++)
            {
                report.AppendLine($"  警告 {i + 1}. {WarningList[i]}");
            }
            string dump = Environment.GetEnvironmentVariable("BINGAMES_PERF_JUDGED_OUT");
            if (!string.IsNullOrEmpty(dump))
            {
                try
                {
                    System.IO.File.WriteAllLines(dump, JudgedList);
                }
                catch (Exception)
                {
                    // 只是核对用的清单，写不了不影响结论。
                }
            }
        }
    }
}
