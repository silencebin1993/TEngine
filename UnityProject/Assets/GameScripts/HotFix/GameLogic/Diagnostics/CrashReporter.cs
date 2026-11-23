using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.Diagnostics
{
    /// <summary>上一次运行没有正常退出时留下的信息（下次启动时由会话标记还原）。</summary>
    public sealed class CrashInfo
    {
        public string StartedUtc;
        public string RunLogName;
        public string LastException;
        /// <summary>最近一次自动存档的完整路径（没有 = null）。</summary>
        public string LatestAutosave;
        public string CrashLogPath;
    }

    /// <summary>
    /// FG15-SYS-03（FG15 FGR-SYS-060 崩溃处理 / FGR-SYS-062 日志轮换）。
    /// - 每次运行写一份运行日志 <c>Logs/run-时间.log</c>，只保留最近 <see cref="KeptRuns"/> 次（崩溃记录 <c>crash-*.txt</c> 同样保留 5 份）；
    /// - 启动时写会话标记，正常退出（<see cref="Application.quitting"/>）删除；下次启动还看到标记 = 上次崩溃 / 被强制结束：
    ///   用标记里的最后一条异常与上次运行日志的结尾写崩溃记录，并记下最近一次自动存档的位置；
    /// - 未处理异常（会结束进程的那种）当场写崩溃记录；
    /// - 主菜单打开时询问是否生成反馈包（<see cref="OfferFeedbackPackAfterCrash"/>，只问一次）。
    /// 隐私：写进日志的每一行都先过 <see cref="Scrub"/>：用户目录、用户名、电脑名换成占位符。
    /// 位置：真机在玩家配置目录（persistentDataPath）；编辑器在工程的 Library/BinGamesDiag（影子工程与用户的编辑器互不干扰）。
    /// 热更程序集加载之前的崩溃由 Unity 自己的 Player.log / Player-prev.log 记录，反馈包一并收进去。
    /// </summary>
    public static class CrashReporter
    {
        public const int KeptRuns = 5;
        public const string MarkerName = "session.marker";
        /// <summary>崩溃记录里附的运行日志结尾：最多读最后这么多字节（取其中最后 80 行）。</summary>
        public const int CrashTailBytes = 64 * 1024;

        /// <summary>单份运行日志的上限（字符数，约等于字节）：超过后只记错误 / 异常（日志刷屏时文件、启动读结尾、打包都不会无限变大）。自检可调小。</summary>
        public static long RunLogCapChars { get; set; } = DefaultRunLogCapChars;
        public const long DefaultRunLogCapChars = 4L << 20;

        /// <summary>自检专用：把日志 / 反馈包 / 本地统计的根目录改到临时目录（用完置回 null）。</summary>
        public static string RootOverrideForTests { get; set; }

        /// <summary>自检专用：batchmode 下也弹“上次意外退出”询问（正式 batchmode 没有人能回答，不弹）。</summary>
        public static bool PromptInBatchModeForTests { get; set; }

        private static string _root;

        public static string Root => RootOverrideForTests ?? (_root ??= Application.isEditor
            ? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "BinGamesDiag")
            : Application.persistentDataPath);

        public static string LogDir => Path.Combine(Root, "Logs");
        public static string MarkerPath => Path.Combine(LogDir, MarkerName);

        public static bool Installed { get; private set; }
        public static string CurrentRunLog { get; private set; }
        public static CrashInfo PreviousCrash { get; private set; }
        public static bool CrashPromptHandled { get; private set; }
        /// <summary>运行日志写不进去（磁盘满 / 被占用）后不再尝试，游戏照常运行。</summary>
        public static bool LogWriteFailed { get; private set; }

        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _startedUtc;
        private static string _lastException;
        private static string _crashLogPath;
        private static bool _markerDirty;
        private static long _logChars;
        private static bool _capNoted;
        private static float _nextMarkerAt;
        private static string _saveDir;
        private static string _profile;
        private static Regex _userWord;
        private static Regex _machineWord;
        private static readonly Regex UsersPath = new Regex(@"(?i)([\\/])(Users|home)([\\/])[^\\/\r\n""'<>|?*]+", RegexOptions.Compiled);

        /// <summary>热更入口最先调用（GameApp.StartGameLogic）。写不了盘只记警告，不影响启动。</summary>
        public static void Install()
        {
            if (Installed)
            {
                return;
            }
            Installed = true;
            try
            {
                StartSession(DateTime.UtcNow);
            }
            catch (Exception e)
            {
                LogWriteFailed = true;
                Debug.LogWarning($"[CrashReporter] 运行日志不可用：{e.Message}");
            }
            Application.logMessageReceivedThreaded += RecordLog;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordUnhandled(e.ExceptionObject as Exception);
            Application.quitting += OnQuitting;
        }

        /// <summary>正常退出（关窗口 / Alt+F4 / 菜单退出）：先把本地统计这一局写盘（FGR-SYS-100，不丢最后不到 60 秒），再结束日志会话。</summary>
        public static void OnQuitting()
        {
            try
            {
                LocalPlayStats.EndSession();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CrashReporter] 退出时写本地统计失败：" + e.Message);
            }
            EndSession();
        }

        /// <summary>开始一次运行：认上一次的残留标记 → 轮换旧日志 → 开新的运行日志 → 写新标记。主线程调用（自检直接调）。</summary>
        public static void StartSession(DateTime nowUtc)
        {
            CaptureMainThreadValues();
            Directory.CreateDirectory(LogDir);
            PreviousCrash = DetectPreviousCrash();
            CrashPromptHandled = false;
            _startedUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture);
            _lastException = null;
            _crashLogPath = null;
            string name = "run-" + nowUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".log";
            Rotate(LogDir, "run-*.log", KeptRuns - 1, null);
            Rotate(LogDir, "crash-*.txt", KeptRuns, null);
            lock (Gate)
            {
                CloseWriter();
                CurrentRunLog = Path.Combine(LogDir, name);
                _logChars = 0;
                _capNoted = false;
                _writer = new StreamWriter(new FileStream(CurrentRunLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                LogWriteFailed = false;
                _writer.WriteLine($"# BinGames {Campaign.CampaignSaveService.ProductVersion} ({Application.version}) Unity {Application.unityVersion}");
                _writer.WriteLine($"# started {_startedUtc}  os {SystemInfo.operatingSystem}");
                _writer.Flush();
            }
            WriteMarker();
        }

        /// <summary>正常退出：关日志、删标记（下次启动不再当作崩溃）。</summary>
        public static void EndSession()
        {
            lock (Gate)
            {
                CloseWriter();
            }
            TryDelete(MarkerPath);
        }

        /// <summary>自检专用：模拟进程被强制结束——日志句柄放掉，标记留在盘上。</summary>
        public static void AbandonSessionForTests()
        {
            lock (Gate)
            {
                CloseWriter();
            }
        }

        public static void ResetForTests()
        {
            lock (Gate)
            {
                CloseWriter();
            }
            CurrentRunLog = null;
            PreviousCrash = null;
            CrashPromptHandled = false;
            LogWriteFailed = false;
            _lastException = null;
            _crashLogPath = null;
            _markerDirty = false;
            RunLogCapChars = DefaultRunLogCapChars;
        }

        /// <summary>Unity 日志回调（任意线程）。不在这里打 Unity 日志（会递归）。</summary>
        public static void RecordLog(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                if (_writer == null)
                {
                    return;
                }
                try
                {
                    bool severe = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
                    if (_logChars >= RunLogCapChars && !severe)
                    {
                        if (!_capNoted)
                        {
                            _capNoted = true;
                            _writer.WriteLine($"# run log reached {RunLogCapChars} chars: only errors / exceptions are recorded from here");
                        }
                        return;
                    }
                    _logChars += 40 + (condition?.Length ?? 0) + (severe ? stackTrace?.Length ?? 0 : 0);
                    _writer.Write('[');
                    _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    _writer.Write("] [");
                    _writer.Write(type.ToString());
                    _writer.Write("] ");
                    _writer.WriteLine(Scrub(condition));
                    if (severe)
                    {
                        if (!string.IsNullOrEmpty(stackTrace))
                        {
                            _writer.WriteLine(Scrub(stackTrace));
                        }
                        _writer.Flush(); // 崩溃前的最后几行必须已经落盘
                    }
                    if (type == LogType.Exception)
                    {
                        _lastException = Truncate(Scrub(condition + "\n" + FirstLines(stackTrace, 8)), 4000);
                        _markerDirty = true;
                    }
                }
                catch (Exception)
                {
                    // 磁盘满 / 文件被删：放弃运行日志，游戏照常运行（不能因为写日志失败再抛异常）。
                    LogWriteFailed = true;
                    CloseWriter();
                }
            }
        }

        /// <summary>会结束进程的未处理异常：当场写崩溃记录（任意线程）。</summary>
        public static void RecordUnhandled(Exception e)
        {
            try
            {
                string text = Truncate(Scrub(e != null ? e.ToString() : "unknown"), 8000);
                lock (Gate)
                {
                    _lastException = text;
                    _writer?.WriteLine("[unhandled] " + text);
                    _writer?.Flush();
                }
                _crashLogPath = WriteCrashLog(new CrashInfo
                {
                    StartedUtc = _startedUtc,
                    RunLogName = Path.GetFileName(CurrentRunLog ?? string.Empty),
                    LastException = text,
                    LatestAutosave = LatestAutosave(SaveDir),
                }, CurrentRunLog);
                WriteMarker();
            }
            catch (Exception)
            {
                // 进程正在倒下，写不了就算了。
            }
        }

        /// <summary>主线程每帧调用（UiKitInputPump）：有新异常时最多每秒刷新一次会话标记。O(1)。</summary>
        public static void Tick()
        {
            if (!_markerDirty || Time.realtimeSinceStartup < _nextMarkerAt)
            {
                return;
            }
            _nextMarkerAt = Time.realtimeSinceStartup + 1f;
            WriteMarker();
        }

        /// <summary>把运行日志缓冲写盘（打反馈包前调用）。</summary>
        public static void Flush()
        {
            lock (Gate)
            {
                try
                {
                    _writer?.Flush();
                }
                catch (Exception)
                {
                    LogWriteFailed = true;
                    CloseWriter();
                }
            }
        }

        /// <summary>只保留 <paramref name="keep"/> 份（文件名带时间，按名字新→旧），返回删掉的份数。<paramref name="protect"/> 永不删。</summary>
        public static int Rotate(string dir, string pattern, int keep, string protect)
        {
            if (!Directory.Exists(dir))
            {
                return 0;
            }
            int deleted = 0;
            foreach (string f in Directory.GetFiles(dir, pattern).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Math.Max(0, keep)))
            {
                if (protect != null && string.Equals(f, protect, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (TryDelete(f))
                {
                    deleted++;
                }
            }
            return deleted;
        }

        /// <summary>最近一次自动存档（campaign_slotN.autoK.json 里写入时间最新的一份）；没有 = null。</summary>
        public static string LatestAutosave(string saveDir)
        {
            try
            {
                if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir))
                {
                    return null;
                }
                return Directory.GetFiles(saveDir, "campaign_slot*.auto*.json")
                    .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>当前存档目录（测试替换后跟着变）。</summary>
        public static string SaveDir => Campaign.CampaignSaveService.SaveDirectoryOverrideForTests ?? _saveDir ?? Campaign.CampaignSaveService.SaveDirectory;

        /// <summary>隐私脱敏：用户目录 → &lt;USERPROFILE&gt;，任何 Users/名字 路径段 → &lt;USER&gt;，用户名 / 电脑名整词 → &lt;USER&gt; / &lt;PC&gt;。</summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }
            if (_profile == null)
            {
                CaptureMainThreadValues();
            }
            string s = text;
            if (!string.IsNullOrEmpty(_profile))
            {
                s = ReplaceIgnoreCase(s, _profile, "<USERPROFILE>");
                s = ReplaceIgnoreCase(s, _profile.Replace('\\', '/'), "<USERPROFILE>");
            }
            s = UsersPath.Replace(s, "$1$2$3<USER>");
            if (_userWord != null)
            {
                s = _userWord.Replace(s, "<USER>");
            }
            if (_machineWord != null)
            {
                s = _machineWord.Replace(s, "<PC>");
            }
            return s;
        }

        /// <summary>主菜单打开时调用：上次没有正常退出就问一次是否生成反馈包。返回是否弹了询问。</summary>
        public static bool OfferFeedbackPackAfterCrash()
        {
            CrashInfo crash = PreviousCrash;
            if (crash == null || CrashPromptHandled)
            {
                return false;
            }
            CrashPromptHandled = true;
            if (Application.isBatchMode && !PromptInBatchModeForTests)
            {
                Debug.LogWarning("[CrashReporter] 上次运行没有正常退出（batchmode 不弹询问）：" + crash.CrashLogPath);
                return false;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Get("diag.crash.title"),
                ConfirmText = GameText.Get("diag.crash.make_pack"),
                CancelText = GameText.Get("diag.crash.skip"),
                OnConfirm = () => FeedbackPack.Request(FeedbackPack.Trigger.AfterCrash),
            };
            req.Lines.Add(GameText.Get("diag.crash.body"));
            req.Lines.Add(crash.LatestAutosave != null
                ? GameText.Format("diag.crash.autosave_0", Path.GetFileName(crash.LatestAutosave))
                : GameText.Get("diag.crash.no_autosave"));
            UiConfirmDialog.Show(req);
            return true;
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private static void CaptureMainThreadValues()
        {
            try
            {
                if (Campaign.CampaignSaveService.SaveDirectoryOverrideForTests == null)
                {
                    _saveDir = Campaign.CampaignSaveService.SaveDirectory; // 后台线程不能碰 persistentDataPath，先在主线程记下
                }
            }
            catch (Exception)
            {
                // 存档目录取不到只影响“最近一次自动存档”一项。
            }
            _profile = SafeEnv(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty;
            _userWord = WordRegex(SafeEnv(() => Environment.UserName));
            _machineWord = WordRegex(SafeEnv(() => Environment.MachineName));
        }

        private static Regex WordRegex(string word) =>
            string.IsNullOrEmpty(word) || word.Length < 3 ? null : new Regex(@"(?i)(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])");

        private static string SafeEnv(Func<string> f)
        {
            try
            {
                return f();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static CrashInfo DetectPreviousCrash()
        {
            string marker = MarkerPath;
            if (!File.Exists(marker))
            {
                return null;
            }
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (string line in File.ReadAllLines(marker, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        kv[line.Substring(0, eq)] = line.Substring(eq + 1).Replace("\\n", "\n");
                    }
                }
            }
            catch (Exception)
            {
                // 标记读不了也算一次异常退出，只是信息少一些。
            }
            kv.TryGetValue("run_log", out string runLog);
            kv.TryGetValue("crash_log", out string crashLog);
            var info = new CrashInfo
            {
                StartedUtc = kv.TryGetValue("started", out string st) ? st : string.Empty,
                RunLogName = runLog ?? string.Empty,
                LastException = kv.TryGetValue("last_exception", out string ex) && ex.Length > 0 ? ex : null,
                LatestAutosave = LatestAutosave(SaveDir),
            };
            string crashPath = string.IsNullOrEmpty(crashLog) ? null : Path.Combine(LogDir, Path.GetFileName(crashLog));
            info.CrashLogPath = crashPath != null && File.Exists(crashPath)
                ? crashPath // 未处理异常当场已写过，不重复写
                : WriteCrashLog(info, string.IsNullOrEmpty(runLog) ? null : Path.Combine(LogDir, Path.GetFileName(runLog)));
            TryDelete(marker);
            return info;
        }

        /// <summary>写崩溃记录：开始时间、运行日志名、最后一条异常、最近一次自动存档的位置、运行日志结尾 80 行。返回路径（写不了 = null）。</summary>
        private static string WriteCrashLog(CrashInfo info, string runLogPath)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                string path = Path.Combine(LogDir, "crash-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
                var sb = new StringBuilder();
                sb.AppendLine("# BinGames crash record");
                sb.AppendLine("session_started=" + info.StartedUtc);
                sb.AppendLine("run_log=" + info.RunLogName);
                sb.AppendLine("latest_autosave=" + (info.LatestAutosave != null ? Scrub(info.LatestAutosave) : "(none)"));
                sb.AppendLine("last_exception:");
                sb.AppendLine(info.LastException ?? "(none recorded: native crash or forced termination)");
                if (runLogPath != null && File.Exists(runLogPath))
                {
                    sb.AppendLine("--- run log tail ---");
                    foreach (string l in Tail(ReadShared(runLogPath, CrashTailBytes), 80))
                    {
                        sb.AppendLine(l);
                    }
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteMarker()
        {
            _markerDirty = false;
            try
            {
                var sb = new StringBuilder();
                sb.Append("started=").AppendLine(_startedUtc);
                sb.Append("run_log=").AppendLine(Path.GetFileName(CurrentRunLog ?? string.Empty));
                sb.Append("version=").AppendLine(Campaign.CampaignSaveService.ProductVersion);
                sb.Append("crash_log=").AppendLine(Path.GetFileName(_crashLogPath ?? string.Empty));
                sb.Append("last_exception=").AppendLine((_lastException ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "\\n"));
                File.WriteAllText(MarkerPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 标记写不了：下次启动认不出这次崩溃，但不影响游戏。
            }
        }

        private static void CloseWriter()
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
                // 关不掉（磁盘满时 Flush 失败）也要把引用放掉。
            }
            _writer = null;
        }

        /// <summary>读一份可能正被写着的文件（运行日志）；超过 <paramref name="maxBytes"/> 时只读结尾那么多（开头可能截断半个字符）。</summary>
        public static string ReadShared(string path, long maxBytes = long.MaxValue)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                if (fs.Length > maxBytes)
                {
                    fs.Seek(-maxBytes, SeekOrigin.End);
                }
                return r.ReadToEnd();
            }
        }

        private static IEnumerable<string> Tail(string text, int n)
        {
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            return lines.Skip(Math.Max(0, lines.Length - n));
        }

        private static string FirstLines(string text, int n) =>
            string.IsNullOrEmpty(text) ? string.Empty : string.Join("\n", text.Replace("\r", string.Empty).Split('\n').Take(n));

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private static string ReplaceIgnoreCase(string s, string find, string with)
        {
            int i = s.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return s;
            }
            var sb = new StringBuilder();
            int from = 0;
            while (i >= 0)
            {
                sb.Append(s, from, i - from).Append(with);
                from = i + find.Length;
                i = s.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
            }
            return sb.Append(s, from, s.Length - from).ToString();
        }

        internal static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception)
            {
                // 被别的进程占用：下次再删。
            }
            return false;
        }
    }
}
