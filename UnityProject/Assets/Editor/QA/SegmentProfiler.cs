using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG-TOOL-01：自检段内耗时剖析（可选，<c>bash tools/unity-validate.sh --profile ...</c> 打开）。
    /// 后台线程每 20 毫秒记一次“报告写到第几个字符”；段跑完后把每一行报告对到它被写出的时刻，
    /// 行与上一行之间的时间 = 产生这一行（这条断言或说明）花的时间。只读报告长度、不碰被测代码，不改变任何断言。
    /// 用来找最慢的几段里到底是哪一步慢（提速时只动测量方式，不削弱断言）。
    /// </summary>
    public sealed class SegmentProfiler
    {
        private readonly StringBuilder _report;
        private readonly int _from;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(double t, int len)> _samples = new List<(double, int)>(4096);
        private readonly object _gate = new object();
        private volatile bool _stop;
        private readonly Thread _thread;

        private SegmentProfiler(StringBuilder report)
        {
            _report = report;
            _from = report.Length;
            _thread = new Thread(Loop) { IsBackground = true, Name = "自检段剖析" };
            _thread.Start();
        }

        public static SegmentProfiler Start(StringBuilder report) => new SegmentProfiler(report);

        private void Loop()
        {
            while (!_stop)
            {
                int len;
                try
                {
                    len = _report.Length; // 主线程在追加：读到的可能是追加前后的值，误差不超过一个采样周期
                }
                catch (Exception)
                {
                    len = -1;
                }
                if (len >= 0)
                {
                    lock (_gate)
                    {
                        _samples.Add((_clock.Elapsed.TotalSeconds, len));
                    }
                }
                Thread.Sleep(20);
            }
        }

        /// <summary>停止采样，返回这一段里最慢的 <paramref name="top"/> 行（耗时 = 与上一行之间的时间）。</summary>
        public string Finish(string segment, double segmentSeconds, int top = 8)
        {
            _stop = true;
            _thread.Join(200);
            double end = _clock.Elapsed.TotalSeconds;
            List<(double t, int len)> samples;
            lock (_gate)
            {
                samples = _samples.ToList();
            }
            samples.Add((end, _report.Length));
            string text = _report.ToString(_from, _report.Length - _from);
            return Describe(segment, segmentSeconds, text, _from, samples, top);
        }

        /// <summary>纯计算（自检直接调）：按采样把每行对到写出时刻，列出与上一行间隔最长的几行。</summary>
        public static string Describe(string segment, double segmentSeconds, string text, int offset, IReadOnlyList<(double t, int len)> samples, int top)
        {
            var rows = new List<(double gap, string line)>();
            int pos = offset;
            double prev = 0;
            foreach (string raw in text.Split('\n'))
            {
                pos += raw.Length + 1;
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0)
                {
                    continue;
                }
                double t = TimeReached(samples, Math.Min(pos, offset + text.Length));
                rows.Add((t - prev, line.Trim()));
                prev = t;
            }
            var sb = new StringBuilder();
            sb.Append("[").Append(segment).Append("] ").Append(segmentSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" 秒，最慢的行：\n");
            foreach ((double gap, string line) in rows.OrderByDescending(r => r.gap).Take(top))
            {
                string shown = line.Length > 110 ? line.Substring(0, 110) + "…" : line;
                sb.Append("  ").Append(gap.ToString("F2", CultureInfo.InvariantCulture).PadLeft(7)).Append(" 秒 ← ").Append(shown.Replace("✓", "(过)").Replace("✗", "(败)")).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>报告长度第一次达到 <paramref name="len"/> 的采样时刻（采样单调递增）。</summary>
        public static double TimeReached(IReadOnlyList<(double t, int len)> samples, int len)
        {
            int lo = 0;
            int hi = samples.Count - 1;
            if (hi < 0)
            {
                return 0;
            }
            if (samples[hi].len < len)
            {
                return samples[hi].t;
            }
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (samples[mid].len >= len)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return samples[lo].t;
        }
    }
}
