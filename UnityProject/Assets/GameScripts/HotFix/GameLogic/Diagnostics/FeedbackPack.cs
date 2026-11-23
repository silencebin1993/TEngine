using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using UnityEngine;

namespace GameLogic.Diagnostics
{
    /// <summary>
    /// FG15-SYS-03（FG15 FGR-SYS-061 反馈包；FGT-SYS-006）：按反馈包键（默认 Ctrl+F8，可重绑；F8 本身是快捷栏 8）、设置页按钮或崩溃后的询问，
    /// 把当前存档、最近一次自动存档、日志、截图、设置打包成一个 zip，只写在本机用户目录（<see cref="PackDir"/>），不联网、不上传。
    /// - 主线程只做：收集路径与文本（README 内容说明按当前语言）、帧末截图拷像素；读文件、PNG 编码、压缩都在后台线程，暂停 / 任意倍速下都能按，不卡帧；
    /// - 先写 <c>.zip.tmp</c> 再改名：磁盘空间不足（事前估算不够或写到一半失败）时删掉半成品、提示原因，存档只读不改；
    /// - 日志进包前再脱敏一次（<see cref="CrashReporter.Scrub"/>）；系统信息只有版本、系统、处理器、显卡、内存、分辨率，不含用户名、电脑名、设备编号。
    /// </summary>
    public static class FeedbackPack
    {
        public enum Trigger
        {
            Hotkey = 0,
            AfterCrash = 1,
            Settings = 2,
        }

        public sealed class Result
        {
            public bool Ok;
            public string Path;
            /// <summary>失败原因文本键（diag.pack.disk_full / diag.pack.failed_0）；成功 = null。</summary>
            public string ErrorKey;
            public string Detail;
            public Trigger Trigger;
            public readonly List<string> Entries = new List<string>();
        }

        private sealed class Inputs
        {
            public string Dir;
            public string Stamp;
            public Trigger Trigger;
            public string Readme;
            public string SystemInfoText;
            public string SettingsJson;
            public readonly List<(string Entry, string Path, bool Scrub)> Files = new List<(string, string, bool)>();
            public byte[] Png;
        }

        /// <summary>估算之外再留的余量（字节）。</summary>
        public const long FreeSpaceMargin = 1L << 20;

        /// <summary>帧末截图最多等这么久（真实秒）；不渲染（最小化等）时不带截图继续打包，不会一直“正在生成”。</summary>
        public const float CaptureTimeoutSeconds = 2f;

        public static string PackDir => Path.Combine(CrashReporter.Root, "FeedbackPacks");

        public static bool Busy { get; private set; }
        public static Result LastResult { get; private set; }
        public static int CompletedCount { get; private set; }

        /// <summary>“打开文件夹”的实际动作（自检替换成记录路径）。</summary>
        public static Action<string> OpenFolderHandler = dir => Application.OpenURL("file:///" + dir.Replace('\\', '/'));

        // ── 自检注入 ──
        /// <summary>替换截图（返回 PNG 字节；null = 没有截图）。设置后不走帧末截图。</summary>
        public static Func<byte[]> ScreenshotForTests { get; set; }
        /// <summary>替换可用空间查询（参数 = 目标目录；-1 = 查不到）。</summary>
        public static Func<string, long> FreeSpaceForTests { get; set; }
        /// <summary>替换输出流（参数 = .tmp 路径），用来模拟写到一半磁盘满。</summary>
        public static Func<string, Stream> OpenOutputForTests { get; set; }
        /// <summary>true：在调用线程里同步打包（自检）；false：后台线程。</summary>
        public static bool SynchronousForTests { get; set; }

        private static readonly ConcurrentQueue<Result> Finished = new ConcurrentQueue<Result>();

        /// <summary>开始生成反馈包（主线程）。已经在生成时返回 false 并提示。</summary>
        public static bool Request(Trigger trigger)
        {
            if (Busy)
            {
                Notify(false, GameText.Get("diag.pack.busy"));
                return false;
            }
            Busy = true;
            Inputs inputs;
            try
            {
                inputs = Gather(trigger);
            }
            catch (Exception e)
            {
                Finished.Enqueue(Fail(trigger, "diag.pack.failed_0", e.Message));
                Pump();
                return false;
            }
            Notify(true, GameText.Get("diag.pack.started"));
            if (ScreenshotForTests != null || Application.isBatchMode)
            {
                inputs.Png = ScreenshotForTests?.Invoke();
                StartBuild(inputs);
            }
            else
            {
                CaptureRunner.Run(inputs);
            }
            return true;
        }

        /// <summary>主线程每帧调用（UiKitInputPump）：取回后台结果并告诉玩家。没有结果时 O(1)。</summary>
        public static void Pump()
        {
            CaptureRunner.CheckTimeout();
            while (Finished.TryDequeue(out Result r))
            {
                Busy = false;
                LastResult = r;
                CompletedCount++;
                string text = r.Ok ? GameText.Format("diag.pack.saved_0", r.Path)
                    : r.ErrorKey == "diag.pack.disk_full" ? GameText.Get(r.ErrorKey) : GameText.Format("diag.pack.failed_0", r.Detail ?? string.Empty);
                Debug.Log("[FeedbackPack] " + (r.Ok ? "已生成 " + CrashReporter.Scrub(r.Path) : "失败 " + r.ErrorKey + " " + r.Detail));
                bool inWorld = CampaignSession.Current != null && Stage.GameRoot.AnyRegionActive;
                if (inWorld)
                {
                    Campaign.Feedback.FeedbackCues.Raise(r.Ok ? Campaign.Feedback.FeedbackCueId.SaveComplete : Campaign.Feedback.FeedbackCueId.Failure, text);
                    continue;
                }
                // 主菜单 / 崩溃询问之后没有通知条：用确认框告诉玩家结果，成功时可一键打开文件夹。
                var req = new ConfirmRequest
                {
                    Title = GameText.Get(r.Ok ? "diag.pack.done_title" : "diag.pack.fail_title"),
                    ConfirmText = GameText.Get(r.Ok ? "diag.pack.open_folder" : "diag.pack.close"),
                    CancelText = GameText.Get("diag.pack.close"),
                };
                req.Lines.Add(text);
                if (r.Ok)
                {
                    string dir = Path.GetDirectoryName(r.Path);
                    req.OnConfirm = () => OpenFolderHandler?.Invoke(dir);
                }
                UiConfirmDialog.Show(req);
            }
        }

        public static void ResetForTests()
        {
            while (Finished.TryDequeue(out _))
            {
            }
            Busy = false;
            LastResult = null;
            CompletedCount = 0;
            ScreenshotForTests = null;
            FreeSpaceForTests = null;
            OpenOutputForTests = null;
            SynchronousForTests = false;
        }

        /// <summary>后台结果是否已经回来（自检等待用）。</summary>
        public static bool HasPendingResult => !Finished.IsEmpty;

        // ── 主线程：收集 ────────────────────────────────────────────────────

        private static Inputs Gather(Trigger trigger)
        {
            CrashReporter.Flush();
            var inp = new Inputs
            {
                Dir = PackDir,
                Stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                Trigger = trigger,
                SettingsJson = PlayerPrefs.GetString(GameSettings.PrefsKey, string.Empty),
            };
            string logDir = CrashReporter.LogDir;
            if (Directory.Exists(logDir))
            {
                foreach (string f in Directory.GetFiles(logDir, "run-*.log").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(CrashReporter.KeptRuns))
                {
                    inp.Files.Add(("logs/" + Path.GetFileName(f), f, true));
                }
                foreach (string f in Directory.GetFiles(logDir, "crash-*.txt").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(CrashReporter.KeptRuns))
                {
                    inp.Files.Add(("logs/" + Path.GetFileName(f), f, true));
                }
            }
            // 热更程序集加载之前的崩溃只在 Unity 自己的 Player.log 里（编辑器的 Editor.log 是整个编辑器的，不收）。
            if (!Application.isEditor && !string.IsNullOrEmpty(Application.consoleLogPath))
            {
                string player = Application.consoleLogPath;
                string prev = Path.Combine(Path.GetDirectoryName(player) ?? string.Empty, Path.GetFileNameWithoutExtension(player) + "-prev.log");
                foreach (string f in new[] { player, prev })
                {
                    if (File.Exists(f))
                    {
                        inp.Files.Add(("logs/unity-" + Path.GetFileName(f), f, true));
                    }
                }
            }
            string saveDir = CrashReporter.SaveDir;
            string current = CampaignSession.HasActiveCampaign ? CampaignSaveService.SlotPathIn(saveDir, CampaignSession.ActiveSlotIndex) : NewestManualSave(saveDir);
            string auto = CrashReporter.LatestAutosave(saveDir);
            foreach (string f in new[] { current, auto }.Where(p => p != null && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                inp.Files.Add(("saves/" + Path.GetFileName(f), f, false));
            }
            inp.SystemInfoText = BuildSystemInfo(trigger);
            inp.Readme = BuildReadme(inp, current, auto);
            return inp;
        }

        private static string NewestManualSave(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return null;
            }
            return Directory.GetFiles(dir, "campaign_slot*.json")
                .Where(f => !Path.GetFileName(f).Contains(".auto") && !Path.GetFileName(f).Contains(".quick"))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        private static string BuildSystemInfo(Trigger trigger)
        {
            var sb = new StringBuilder();
            sb.AppendLine("product=" + CampaignSaveService.ProductVersion + " (" + Application.version + ")");
            sb.AppendLine("unity=" + Application.unityVersion + (Application.isEditor ? " editor" : string.Empty));
            sb.AppendLine("os=" + SystemInfo.operatingSystem);
            sb.AppendLine("cpu=" + SystemInfo.processorType + " x" + SystemInfo.processorCount);
            sb.AppendLine("ram_mb=" + SystemInfo.systemMemorySize);
            sb.AppendLine("gpu=" + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsMemorySize + " MB)");
            sb.AppendLine("screen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode);
            sb.AppendLine("language=" + GameLanguageCodes.ToCode(GameSettings.Language));
            sb.AppendLine("trigger=" + trigger);
            sb.AppendLine("in_campaign=" + (CampaignSession.Current != null) + " slot=" + CampaignSession.ActiveSlotIndex);
            CrashInfo crash = CrashReporter.PreviousCrash;
            sb.AppendLine("previous_session_crashed=" + (crash != null) + (crash != null ? " crash_log=" + Path.GetFileName(crash.CrashLogPath ?? string.Empty) : string.Empty));
            sb.AppendLine("run_log_write_failed=" + CrashReporter.LogWriteFailed);
            return CrashReporter.Scrub(sb.ToString());
        }

        /// <summary>反馈包内容说明（卡片“必须同时交付”）：放在包里第一份文件，按当前语言。</summary>
        private static string BuildReadme(Inputs inp, string current, string auto)
        {
            var sb = new StringBuilder();
            sb.AppendLine(GameText.Get("diag.readme.title"));
            sb.AppendLine();
            sb.AppendLine(GameText.Format("diag.readme.local_only_0", CrashReporter.Scrub(inp.Dir)));
            sb.AppendLine(GameText.Get("diag.readme.how_to_send"));
            sb.AppendLine();
            sb.AppendLine(GameText.Get("diag.readme.contents"));
            sb.AppendLine("- README.txt: " + GameText.Get("diag.readme.item.readme"));
            sb.AppendLine("- system.txt: " + GameText.Get("diag.readme.item.system"));
            sb.AppendLine("- logs/: " + GameText.Get("diag.readme.item.logs"));
            sb.AppendLine("- saves/: " + GameText.Get("diag.readme.item.saves"));
            sb.AppendLine("- screenshot.png: " + GameText.Get("diag.readme.item.screenshot"));
            sb.AppendLine("- settings.json: " + GameText.Get("diag.readme.item.settings"));
            sb.AppendLine();
            sb.AppendLine(GameText.Get("diag.readme.not_included"));
            sb.AppendLine();
            sb.AppendLine(GameText.Get("diag.readme.files"));
            foreach ((string entry, _, _) in inp.Files)
            {
                sb.AppendLine("  " + entry);
            }
            sb.AppendLine(current != null ? "  current_save=" + Path.GetFileName(current) : "  current_save=(none)");
            sb.AppendLine(auto != null ? "  latest_autosave=" + Path.GetFileName(auto) : "  latest_autosave=(none)");
            return sb.ToString();
        }

        // ── 后台：打包 ──────────────────────────────────────────────────────

        private static void StartBuild(Inputs inputs)
        {
            if (SynchronousForTests)
            {
                Finished.Enqueue(Build(inputs));
                return;
            }
            Task.Run(() => Finished.Enqueue(Build(inputs)));
        }

        private static Result Build(Inputs inp)
        {
            string tmp = null;
            try
            {
                Directory.CreateDirectory(inp.Dir);
                foreach (string stale in Directory.GetFiles(inp.Dir, "*.zip.tmp"))
                {
                    CrashReporter.TryDelete(stale); // 上次打包途中进程被结束留下的半成品（同一时刻只有一个打包，见 Busy）
                }
                string final = Path.Combine(inp.Dir, "feedback-" + inp.Stamp + ".zip");
                for (int n = 2; File.Exists(final); n++)
                {
                    final = Path.Combine(inp.Dir, "feedback-" + inp.Stamp + "-" + n + ".zip");
                }
                tmp = final + ".tmp";
                long estimate = 64 * 1024 + (inp.Png?.Length ?? 2 * 1024 * 1024) + Encoding.UTF8.GetByteCount(inp.SettingsJson ?? string.Empty);
                foreach ((_, string path, _) in inp.Files)
                {
                    estimate += SafeLength(path);
                }
                long free = FreeSpaceForTests != null ? FreeSpaceForTests(inp.Dir) : QueryFreeSpace(inp.Dir);
                if (free >= 0 && free < estimate + FreeSpaceMargin)
                {
                    return Fail(inp.Trigger, "diag.pack.disk_full", $"free {free} < need {estimate + FreeSpaceMargin}");
                }
                var result = new Result { Ok = true, Trigger = inp.Trigger };
                using (Stream output = OpenOutputForTests != null ? OpenOutputForTests(tmp) : new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    AddText(zip, result, "README.txt", inp.Readme);
                    AddText(zip, result, "system.txt", inp.SystemInfoText);
                    foreach ((string entry, string path, bool scrub) in inp.Files)
                    {
                        if (!File.Exists(path))
                        {
                            continue;
                        }
                        if (scrub)
                        {
                            AddText(zip, result, entry, CrashReporter.Scrub(CrashReporter.ReadShared(path, CrashReporter.DefaultRunLogCapChars)));
                        }
                        else
                        {
                            // 存档原样进包（校验和要能对上，开发者才读得了档），只读、共享打开，不碰原文件。
                            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (Stream dst = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal).Open())
                            {
                                src.CopyTo(dst);
                            }
                            result.Entries.Add(entry);
                        }
                    }
                    if (inp.Png != null && inp.Png.Length > 0)
                    {
                        using (Stream dst = zip.CreateEntry("screenshot.png", System.IO.Compression.CompressionLevel.NoCompression).Open())
                        {
                            dst.Write(inp.Png, 0, inp.Png.Length);
                        }
                        result.Entries.Add("screenshot.png");
                    }
                    AddText(zip, result, "settings.json", inp.SettingsJson ?? string.Empty);
                }
                File.Move(tmp, final);
                result.Path = final;
                return result;
            }
            catch (Exception e)
            {
                if (tmp != null)
                {
                    CrashReporter.TryDelete(tmp); // 不留半个压缩包
                }
                return IsDiskFull(e) ? Fail(inp.Trigger, "diag.pack.disk_full", e.Message) : Fail(inp.Trigger, "diag.pack.failed_0", e.Message);
            }
        }

        private static void AddText(ZipArchive zip, Result result, string entry, string text)
        {
            using (var w = new StreamWriter(zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
            {
                w.Write(text ?? string.Empty);
            }
            result.Entries.Add(entry);
        }

        /// <summary>磁盘满：Win32 ERROR_DISK_FULL(112) / ERROR_HANDLE_DISK_FULL(39)，或 Mono 的“Disk full”文字。</summary>
        public static bool IsDiskFull(Exception e)
        {
            for (Exception x = e; x != null; x = x.InnerException)
            {
                if (!(x is IOException))
                {
                    continue;
                }
                int code = x.HResult & 0xFFFF;
                string m = x.Message ?? string.Empty;
                if (code == 112 || code == 39 || m.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.IndexOf("not enough space", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static long QueryFreeSpace(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1; // 查不到就照写，写失败再按磁盘满处理
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>失败原因会显示给玩家、进通知历史（随存档落盘、再被下一个反馈包原样带走），所以异常文字里的路径同样脱敏。</summary>
        private static Result Fail(Trigger trigger, string key, string detail) => new Result { Ok = false, ErrorKey = key, Detail = CrashReporter.Scrub(detail), Trigger = trigger };

        private static void Notify(bool ok, string text)
        {
            if (CampaignSession.Current != null && Stage.GameRoot.AnyRegionActive)
            {
                Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, text);
            }
        }

        /// <summary>帧末截图：等到 WaitForEndOfFrame 才能读屏幕；拷完像素就交给后台编码，主线程不编 PNG。</summary>
        private sealed class CaptureRunner : MonoBehaviour
        {
            private static CaptureRunner _instance;
            private static Inputs _pending;
            private static float _deadline;

            public static void Run(Inputs inputs)
            {
                _pending = inputs;
                _deadline = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
                if (_instance == null)
                {
                    var go = new GameObject("[FeedbackPackCapture]");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<CaptureRunner>();
                }
                _instance.StartCoroutine(_instance.Capture(inputs));
            }

            private IEnumerator Capture(Inputs inputs)
            {
                yield return new WaitForEndOfFrame();
                if (_pending != inputs)
                {
                    yield break; // 已超时、不带截图打包了
                }
                _pending = null;
                byte[] raw = null;
                UnityEngine.Experimental.Rendering.GraphicsFormat format = default;
                int w = 0, h = 0;
                try
                {
                    Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                    if (tex != null)
                    {
                        raw = tex.GetRawTextureData();
                        format = tex.graphicsFormat;
                        w = tex.width;
                        h = tex.height;
                        Destroy(tex);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[FeedbackPack] 截图失败，反馈包不含截图：" + e.Message);
                }
                Task.Run(() =>
                {
                    try
                    {
                        inputs.Png = raw != null ? ImageConversion.EncodeArrayToPNG(raw, format, (uint)w, (uint)h) : null;
                    }
                    catch (Exception)
                    {
                        inputs.Png = null;
                    }
                    Finished.Enqueue(Build(inputs));
                });
            }

            /// <summary>主线程（Pump）：截图等太久就放弃截图、直接打包。</summary>
            public static void CheckTimeout()
            {
                if (_pending == null || Time.realtimeSinceStartup < _deadline)
                {
                    return;
                }
                Inputs inputs = _pending;
                _pending = null;
                Debug.LogWarning("[FeedbackPack] 帧末截图超时（画面没有渲染），反馈包不含截图");
                StartBuild(inputs);
            }
        }
    }
}
