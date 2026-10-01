using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG-TOOL-01：自检里调 python 的共用入口，带“同一轮只真跑一次”的共享。
    ///
    /// 背景：全量自检里有 6 个段各自真跑 <c>check_luban.py --selftest</c>（197 个用例，串行约 58 秒），输入完全相同（自测夹具只来自
    /// tools/cell_tables 下的 Python 源数据），每段只是在同一份输出里核对自己负责的规则——同一轮重复跑 6 次约 350 秒，占全量一半。
    /// <see cref="RunShared"/>：同一轮全量自检（<see cref="ClearShared"/> 之后）里，参数相同且 tools/cell_tables 全部文件内容指纹相同的命令只真跑一次，
    /// 之后的段拿同一次的退出码与输出照常断言；输入一变（指纹不同）就重新真跑。单独跑某一段时没有共享，照常真跑。
    /// 只用于“结果只取决于 tools/cell_tables 内容、不写任何东西”的命令（自测）；会读真实表 / 会写文件的命令仍用各段自己的 RunPython。
    /// </summary>
    public static class QaPython
    {
        private static readonly Dictionary<string, (int code, string output)> Shared = new Dictionary<string, (int, string)>(StringComparer.Ordinal);

        /// <summary>本轮共享里真跑的次数与复用的次数（报告与自检用）。</summary>
        public static int SharedRuns { get; private set; }

        public static int SharedHits { get; private set; }

        private static bool _active;

        /// <summary>当前是否在一轮全量 / 分段自检里（共享打开）。</summary>
        public static bool Active => _active;

        /// <summary>开始新一轮（全量 / 分段自检开头调用）：清空共享结果并打开共享。</summary>
        public static void ClearShared()
        {
            Shared.Clear();
            SharedRuns = 0;
            SharedHits = 0;
            _active = true;
        }

        /// <summary>一轮结束（全量 / 分段自检末尾调用）：关闭共享并清空——之后单独从菜单跑某一段照常真跑。</summary>
        public static void EndShared()
        {
            Shared.Clear();
            _active = false;
        }

        /// <summary>同一轮里参数与 tools/cell_tables 内容都相同就复用第一次真跑的结果；<paramref name="reused"/> = 这次是复用的。</summary>
        public static (int code, string output) RunShared(string root, string args, out bool reused, int timeoutMs = 180000)
        {
            if (!_active)
            {
                reused = false;
                return Run(root, args, timeoutMs);
            }
            string key = args + "|" + InputFingerprint(root);
            if (Shared.TryGetValue(key, out (int code, string output) hit))
            {
                SharedHits++;
                reused = true;
                return hit;
            }
            (int code, string output) r = Run(root, args, timeoutMs);
            Shared[key] = r;
            SharedRuns++;
            reused = false;
            return r;
        }

        /// <summary>tools/cell_tables 下全部文件（不含 __pycache__）的相对路径 + 内容指纹。</summary>
        public static string InputFingerprint(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return "no-root";
            }
            string dir = Path.Combine(root, "tools", "cell_tables");
            if (!Directory.Exists(dir))
            {
                return "no-dir";
            }
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => f.IndexOf("__pycache__", StringComparison.Ordinal) < 0)
                .Select(f => (rel: f.Substring(dir.Length).Replace('\\', '/').TrimStart('/'), full: f))
                .OrderBy(x => x.rel, StringComparer.Ordinal);
            using (SHA256 sha = SHA256.Create())
            {
                foreach ((string rel, string full) in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(rel + "\n");
                    sha.TransformBlock(name, 0, name.Length, null, 0);
                    byte[] data = File.ReadAllBytes(full);
                    sha.TransformBlock(data, 0, data.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        /// <summary>真跑一次 python（与各段原来的 RunPython 同一口径：UTF-8、合并 stdout / stderr、超时杀进程）。</summary>
        public static (int code, string output) Run(string root, string args, int timeoutMs = 180000)
        {
            var psi = new ProcessStartInfo("python", args)
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
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            proc.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        return (-1, $"python {args} 超过 {timeoutMs / 1000} 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
            }
        }
    }
}
