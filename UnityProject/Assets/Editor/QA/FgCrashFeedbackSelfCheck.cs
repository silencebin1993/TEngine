using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Diagnostics;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools
{
    /// <summary>
    /// FG15-SYS-03 崩溃处理与反馈包（FG15 FGR-SYS-060～062，验收 FGT-SYS-006）+ 同批承接 FG-GAP-047（FGR-SYS-100 本地统计）、FG-GAP-012（配置表损坏界面）。
    /// A 日志轮换（保留最近 5 次运行、崩溃记录 5 份）；B 正常退出不误报；C 强制结束 / 原生崩溃 → 下次启动认出、写崩溃记录、记下最近一次自动存档；
    /// D 未处理异常当场写崩溃记录、下次启动不重复写；E 隐私脱敏；F 反馈包内容完整、只写在本地（含后台线程路径、忙时拒绝、暂停 + 3x 不受影响）；
    /// G 负向：磁盘空间不足（事前估算不够 / 写到一半磁盘满）、其他写失败——不留半成品、存档字节不变；H 下次启动询问（只问一次、batchmode 不弹、中英文）；
    /// I 反馈包键（默认 Ctrl+F8、可重绑、不和快捷栏 F8 冲突、模态 / 暂停下可按）；J 本地统计（默认不记不写、同意后各项、暂停与倍速按真实时间、写盘读回、导出、撤回同意）；
    /// K 配置表缺失 / 截断 → 损坏提示（指出文件、退出、打开日志、中英文）；L 设置页三项。
    /// 全部在临时目录里跑（CrashReporter.RootOverrideForTests / CampaignSaveService.SaveDirectoryOverrideForTests），不碰玩家真实目录。
    /// </summary>
    public static class FgCrashFeedbackSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _root;
        private static string _saves;
        private static readonly List<string> Opened = new List<string>();
        private static int _quits;

        [MenuItem("QA/自检/FG 崩溃处理与反馈包")]
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
            Line("\n[崩溃处理与反馈包] 日志轮换、崩溃认定与记录、隐私脱敏、F8 反馈包（内容 / 只存本地 / 磁盘不足）、下次启动询问、本地统计（FG-GAP-047）、配置表损坏提示（FG-GAP-012）（FG15-SYS-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过");
                return 0;
            }
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Action<string> originalOpen = FeedbackPack.OpenFolderHandler;
            Action originalQuit = ConfigIntegrity.QuitHandler;
            _root = Path.Combine(Path.GetTempPath(), "bingames-crashfb-" + Guid.NewGuid().ToString("N"));
            _saves = Path.Combine(_root, "Campaigns");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.SetLocalStatsConsent(false);
                GameSettings.ResetKeyBindingsToDefault();
                Directory.CreateDirectory(_saves);
                CrashReporter.RootOverrideForTests = _root;
                CampaignSaveService.SaveDirectoryOverrideForTests = _saves;
                FeedbackPack.OpenFolderHandler = d => Opened.Add(d);
                ConfigIntegrity.QuitHandler = () => _quits++;
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；临时根目录 {CrashReporter.Scrub(_root)}");
                Step(CheckRotation);
                Step(CheckCleanExit);
                Step(CheckCrashDetected);
                Step(CheckUnhandled);
                Step(CheckPrivacy);
                Step(CheckPackContents);
                Step(CheckPackThreadedBusyPausedSpeed);
                Step(CheckDiskFull);
                Step(CheckCrashPrompt);
                Step(CheckHotkey);
                Step(CheckLocalStats);
                Step(CheckConfigCorrupt);
                Step(CheckSettingsItems);
            }
            catch (Exception e)
            {
                Fail($"自检抛异常：{e}");
            }
            finally
            {
                CrashReporter.ResetForTests();
                FeedbackPack.ResetForTests();
                LocalPlayStats.ResetForTests();
                CrashReporter.RootOverrideForTests = null;
                CrashReporter.PromptInBatchModeForTests = false;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                FeedbackPack.OpenFolderHandler = originalOpen;
                ConfigIntegrity.QuitHandler = originalQuit;
                UiConfirmDialog.DiscardAll();
                UiEscapeStack.Clear();
                InputRouter.Reset();
                InputRouter.DebugSetReader(null);
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_root, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [崩溃处理与反馈包] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A 日志轮换 ───────────────────────────────────────────────────────

        private static void CheckRotation()
        {
            Line("  A 日志轮换（FGR-SYS-062）");
            var t0 = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
            var names = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                CrashReporter.StartSession(t0.AddMinutes(i));
                names.Add(Path.GetFileName(CrashReporter.CurrentRunLog));
                CrashReporter.EndSession();
            }
            string[] left = Directory.GetFiles(CrashReporter.LogDir, "run-*.log").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Expect(left.Length == CrashReporter.KeptRuns && left.SequenceEqual(names.Skip(3)),
                $"连续运行 8 次后只剩最近 5 次的运行日志（{string.Join(", ", left)}）");
            for (int i = 0; i < 7; i++)
            {
                File.WriteAllText(Path.Combine(CrashReporter.LogDir, $"crash-20261101-00000{i}-000.txt"), "x");
            }
            CrashReporter.StartSession(t0.AddMinutes(20));
            CrashReporter.EndSession();
            int crashes = Directory.GetFiles(CrashReporter.LogDir, "crash-*.txt").Length;
            int runs = Directory.GetFiles(CrashReporter.LogDir, "run-*.log").Length;
            Expect(crashes == 5 && runs == 5, $"崩溃记录也只留 5 份（{crashes}），再运行一次运行日志仍是 5 份（{runs}）且含本次");

            CrashReporter.StartSession(t0.AddMinutes(21));
            CrashReporter.RunLogCapChars = 4000;
            string filler = new string('x', 100);
            for (int i = 0; i < 200; i++)
            {
                CrashReporter.RecordLog("spam " + i + " " + filler, string.Empty, LogType.Warning);
            }
            CrashReporter.RecordLog("boom-after-cap", "at Foo.Bar()", LogType.Exception);
            CrashReporter.Flush();
            string capped = CrashReporter.ReadShared(CrashReporter.CurrentRunLog);
            string tail = CrashReporter.ReadShared(CrashReporter.CurrentRunLog, 300);
            Expect(capped.Length < 4000 + 1000 && capped.Contains("only errors / exceptions") && capped.Contains("boom-after-cap") && !capped.Contains("spam 199")
                   && tail.Length <= 300 && tail.Contains("boom-after-cap"),
                $"单份运行日志有上限：刷屏的警告到上限后不再写（{capped.Length} 字符），之后的异常照记；读结尾只读最后一段");
            CrashReporter.EndSession();
            CrashReporter.RunLogCapChars = CrashReporter.DefaultRunLogCapChars;
            foreach (string f in Directory.GetFiles(CrashReporter.LogDir))
            {
                File.Delete(f);
            }
        }

        // ── B / C / D 崩溃认定 ────────────────────────────────────────────────

        private static void CheckCleanExit()
        {
            Line("  B 正常退出不误报");
            CrashReporter.StartSession(DateTime.UtcNow);
            bool markerWhileRunning = File.Exists(CrashReporter.MarkerPath);
            CrashReporter.EndSession();
            bool markerAfter = File.Exists(CrashReporter.MarkerPath);
            CrashReporter.StartSession(DateTime.UtcNow.AddSeconds(1));
            Expect(markerWhileRunning && !markerAfter && CrashReporter.PreviousCrash == null && !CrashReporter.OfferFeedbackPackAfterCrash(),
                $"运行中有会话标记 {markerWhileRunning}、正常退出删掉 {!markerAfter}、下次启动不当作崩溃、不弹询问");
            CrashReporter.EndSession();
        }

        private static void WriteSaves()
        {
            File.WriteAllText(Path.Combine(_saves, "campaign_slot0.json"), "{\"slot\":0,\"campaign\":\"crashfb\"}");
            string a1 = Path.Combine(_saves, "campaign_slot0.auto1.json");
            string a2 = Path.Combine(_saves, "campaign_slot0.auto2.json");
            File.WriteAllText(a1, "{\"auto\":1}");
            File.WriteAllText(a2, "{\"auto\":2}");
            File.SetLastWriteTimeUtc(a1, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(a2, DateTime.UtcNow.AddMinutes(-5));
        }

        private static void CheckCrashDetected()
        {
            Line("  C 强制结束 / 原生崩溃 → 下次启动认出并写崩溃记录（FGR-SYS-060）");
            WriteSaves();
            CrashReporter.StartSession(DateTime.UtcNow);
            CrashReporter.RecordLog("普通一行 crashfb-line", string.Empty, LogType.Log);
            CrashReporter.RecordLog("NullReferenceException: boom-7781", "at Foo.Bar () [0x0001]", LogType.Exception);
            CrashReporter.Tick();
            string marker = File.ReadAllText(CrashReporter.MarkerPath);
            Expect(marker.Contains("boom-7781"), "出了异常后会话标记里记下最后一条异常（崩溃时来不及写也有它）");
            CrashReporter.AbandonSessionForTests(); // 进程被强制结束：标记留在盘上
            CrashReporter.StartSession(DateTime.UtcNow.AddSeconds(2));
            CrashInfo c = CrashReporter.PreviousCrash;
            string log = c?.CrashLogPath != null && File.Exists(c.CrashLogPath) ? File.ReadAllText(c.CrashLogPath) : string.Empty;
            Expect(c != null && c.LastException != null && c.LastException.Contains("boom-7781"), "下次启动认出上次没有正常退出，带着最后一条异常");
            Expect(c != null && c.LatestAutosave != null && c.LatestAutosave.EndsWith("campaign_slot0.auto2.json"), $"记下最近一次自动存档的位置（写入时间最新的一份：{Path.GetFileName(c?.LatestAutosave ?? "null")}）");
            Expect(log.Contains("boom-7781") && log.Contains("campaign_slot0.auto2.json") && log.Contains("crashfb-line"),
                "崩溃记录文件含最后一条异常、最近一次自动存档、上次运行日志的结尾");
            Expect(File.Exists(CrashReporter.MarkerPath) && !File.ReadAllText(CrashReporter.MarkerPath).Contains("boom-7781"), "新的一次运行写了自己的标记（旧异常不带过来）");
            CrashReporter.AbandonSessionForTests();
            CrashReporter.StartSession(DateTime.UtcNow.AddSeconds(3));
            string native = CrashReporter.PreviousCrash?.CrashLogPath != null ? File.ReadAllText(CrashReporter.PreviousCrash.CrashLogPath) : string.Empty;
            Expect(CrashReporter.PreviousCrash != null && CrashReporter.PreviousCrash.LastException == null && native.Contains("none recorded"),
                "没有托管异常的原生崩溃 / 被强制结束同样认出，崩溃记录写明“没有记录到异常”");
            CrashReporter.EndSession();
        }

        private static void CheckUnhandled()
        {
            Line("  D 未处理异常当场写崩溃记录");
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(1));
            int before = Directory.GetFiles(CrashReporter.LogDir, "crash-*.txt").Length;
            CrashReporter.RecordUnhandled(new InvalidOperationException("fatal-4410"));
            string[] after = Directory.GetFiles(CrashReporter.LogDir, "crash-*.txt");
            string fresh = after.OrderByDescending(File.GetLastWriteTimeUtc).First();
            Expect(after.Length == before + 1 && File.ReadAllText(fresh).Contains("fatal-4410") && File.ReadAllText(fresh).Contains("auto2.json"),
                "未处理异常当场写出崩溃记录（含异常与最近一次自动存档）");
            CrashReporter.AbandonSessionForTests();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(2));
            int final = Directory.GetFiles(CrashReporter.LogDir, "crash-*.txt").Length;
            Expect(CrashReporter.PreviousCrash != null && string.Equals(CrashReporter.PreviousCrash.CrashLogPath, fresh, StringComparison.OrdinalIgnoreCase) && final == after.Length,
                $"下次启动认出同一份崩溃记录，不重复写（{final} 份）");
            CrashReporter.EndSession();
        }

        // ── E 隐私 ───────────────────────────────────────────────────────────

        private static List<string> PrivateTokens()
        {
            var t = new List<string>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) t.Add(profile);
            if (Environment.UserName.Length >= 3) t.Add(Environment.UserName);
            if (Environment.MachineName.Length >= 3) t.Add(Environment.MachineName);
            return t;
        }

        private static string Leaks(string text) =>
            string.Join(" / ", PrivateTokens().Where(k => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(k) + @"(?![A-Za-z0-9])")).Select(k => "“" + k.Length + " 字符的私人信息”"));

        private static void CheckPrivacy()
        {
            Line("  E 隐私脱敏");
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(3));
            CrashReporter.RecordLog($"open {profile}\\AppData\\LocalLow\\x.json by {Environment.UserName} on {Environment.MachineName}; other C:/Users/SomeoneElse/Desktop/a.txt",
                $"at {profile.Replace('\\', '/')}/Projects/a.cs:12", LogType.Error);
            CrashReporter.Flush();
            string log = CrashReporter.ReadShared(CrashReporter.CurrentRunLog);
            string leak = Leaks(log);
            Expect(leak.Length == 0 && log.Contains("<USERPROFILE>") && log.Contains("Users/<USER>/Desktop"),
                "运行日志里用户目录、用户名、电脑名、任何 Users/名字 路径都换成占位符" + (leak.Length > 0 ? "（泄漏：" + leak + "）" : string.Empty));
            CrashReporter.EndSession();
        }

        // ── F 反馈包 ─────────────────────────────────────────────────────────

        private static byte[] TinyPng()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        private static Dictionary<string, byte[]> ReadZip(string path)
        {
            var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var z = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    using (Stream s = e.Open())
                    using (var m = new MemoryStream())
                    {
                        s.CopyTo(m);
                        d[e.FullName] = m.ToArray();
                    }
                }
            }
            return d;
        }

        private static string Text(Dictionary<string, byte[]> zip, string entry) => zip.TryGetValue(entry, out byte[] b) ? Encoding.UTF8.GetString(b) : string.Empty;

        private static CampaignState InCampaign()
        {
            CampaignState s = CampaignState.CreateNew("crashfb", "Standard", 4242);
            CampaignSession.Set(0, s);
            return s;
        }

        private static void CheckPackContents()
        {
            Line("  F 反馈包内容完整、只写在本地（FGR-SYS-061 / FGT-SYS-006）");
            InCampaign();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(4));
            CrashReporter.RecordLog($"pack-line {Environment.UserName}", string.Empty, LogType.Warning);
            FeedbackPack.ResetForTests();
            FeedbackPack.SynchronousForTests = true;
            FeedbackPack.ScreenshotForTests = TinyPng;
            byte[] saveBytes = File.ReadAllBytes(Path.Combine(_saves, "campaign_slot0.json"));
            bool started = FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            FeedbackPack.Pump();
            FeedbackPack.Result r = FeedbackPack.LastResult;
            Expect(started && r != null && r.Ok && File.Exists(r.Path), "按下即生成，结果成功、zip 在盘上");
            if (r == null || !r.Ok)
            {
                Fail("反馈包失败：" + r?.ErrorKey + " " + r?.Detail);
                return;
            }
            string full = Path.GetFullPath(r.Path);
            Expect(full.StartsWith(Path.GetFullPath(FeedbackPack.PackDir), StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(FeedbackPack.PackDir).StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase),
                "zip 写在本机用户目录下的 FeedbackPacks（只存本地）");
            Expect(Directory.GetFiles(FeedbackPack.PackDir).Length == 1 && !Directory.GetFiles(FeedbackPack.PackDir, "*.tmp").Any(), "目录里只有这一个 zip，没有残留 .tmp");
            Dictionary<string, byte[]> zip = ReadZip(r.Path);
            string[] need = { "README.txt", "system.txt", "saves/campaign_slot0.json", "saves/campaign_slot0.auto2.json", "screenshot.png", "settings.json" };
            string[] missing = need.Where(n => !zip.ContainsKey(n)).ToArray();
            int runLogs = zip.Keys.Count(k => k.StartsWith("logs/run-")), crashLogs = zip.Keys.Count(k => k.StartsWith("logs/crash-"));
            Expect(missing.Length == 0 && runLogs >= 1 && runLogs <= 5 && crashLogs >= 1,
                $"包里有说明、系统信息、当前存档、最近一次自动存档、截图、设置、最近的运行日志（{runLogs} 份）与崩溃记录（{crashLogs} 份）" + (missing.Length > 0 ? "，缺 " + string.Join(",", missing) : string.Empty));
            Expect(zip.TryGetValue("saves/campaign_slot0.json", out byte[] packed) && packed.SequenceEqual(saveBytes)
                   && File.ReadAllBytes(Path.Combine(_saves, "campaign_slot0.json")).SequenceEqual(saveBytes),
                "存档原样进包（字节一致），原文件不变");
            string readme = Text(zip, "README.txt");
            bool listsAll = zip.Keys.Where(k => k != "README.txt" && k != "screenshot.png" && k != "settings.json").All(k => readme.Contains(k));
            Expect(readme.Contains(GameText.Get("diag.readme.title")) && readme.Contains(GameText.Get("diag.readme.not_included")) && readme.Contains("只保存在你的电脑上") && listsAll,
                "README 内容说明：标题、只存本地 / 不上传、包含与不包含的内容、逐个列出包里的文件");
            string leaks = Leaks(string.Join("\n", zip.Where(kv => !kv.Key.StartsWith("saves/") && kv.Key != "screenshot.png").Select(kv => Encoding.UTF8.GetString(kv.Value))));
            Expect(leaks.Length == 0 && Text(zip, "logs/" + Path.GetFileName(CrashReporter.CurrentRunLog)).Contains("pack-line <USER>"),
                "包里的文本文件不含用户名 / 电脑名 / 用户目录（日志里换成占位符）" + (leaks.Length > 0 ? "：" + leaks : string.Empty));
            string sys = Text(zip, "system.txt");
            Expect(sys.Contains("product=") && sys.Contains("os=") && sys.Contains("trigger=Hotkey") && !sys.Contains(SystemInfo.deviceUniqueIdentifier),
                "系统信息有版本 / 系统 / 触发方式，不含设备唯一编号");
            Expect(UiConfirmDialog.Current != null && UiConfirmDialog.Current.Title == GameText.Get("diag.pack.done_title")
                   && UiConfirmDialog.Current.Lines.Any(l => l.Contains(r.Path)), "不在游戏世界里时用确认框告诉玩家结果与路径");
            UiConfirmDialog.Confirm();
            Expect(Opened.LastOrDefault() == Path.GetDirectoryName(r.Path), "“打开文件夹”打开反馈包所在目录");
            string src = string.Join("\n", Directory.GetFiles(Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Diagnostics"), "*.cs").Select(File.ReadAllText));
            string[] net = { "UnityWebRequest", "HttpClient", "WebClient", "System.Net", "Socket", "Steamworks", "WWW(" };
            string[] hits = net.Where(src.Contains).ToArray();
            Expect(hits.Length == 0, "崩溃 / 反馈包 / 统计代码里没有任何联网接口（不会自动上传）" + (hits.Length > 0 ? "：" + string.Join(",", hits) : string.Empty));
            CrashReporter.EndSession();
        }

        private static void CheckPackThreadedBusyPausedSpeed()
        {
            Line("  F2 后台线程打包、忙时拒绝、暂停 + 3x 下照常且不改时钟");
            FeedbackPack.ResetForTests();
            FeedbackPack.ScreenshotForTests = TinyPng;
            GameClock.SetPaused(true);
            GameClock.SetSpeed(3f);
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            bool first = FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            bool second = FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!FeedbackPack.HasPendingResult && sw.ElapsedMilliseconds < 20000)
            {
                Thread.Sleep(20);
            }
            FeedbackPack.Pump();
            Expect(first && !second && FeedbackPack.LastResult != null && FeedbackPack.LastResult.Ok && !FeedbackPack.Busy,
                $"后台线程打包成功（{sw.ElapsedMilliseconds} ms）；正在打包时再按被拒绝（只出一个包）");
            Expect(GameClock.Paused && Math.Abs(GameClock.Speed - 3f) < 0.001f && Thread.CurrentThread.ManagedThreadId == mainThread,
                "暂停中、3x 倍速下都能打包，打包不改暂停与倍速");
            GameClock.SetPaused(false);
            GameClock.SetSpeed(0.5f);
            FeedbackPack.SynchronousForTests = true;
            Expect(FeedbackPack.Request(FeedbackPack.Trigger.Hotkey) && Pumped().Ok && Math.Abs(GameClock.Speed - 0.5f) < 0.001f, "0.5x 下同样能打包");
            GameClock.SetSpeed(1f);
            UiConfirmDialog.DiscardAll();
        }

        private static FeedbackPack.Result Pumped()
        {
            FeedbackPack.Pump();
            return FeedbackPack.LastResult ?? new FeedbackPack.Result();
        }

        // ── G 负向：磁盘空间不足 ─────────────────────────────────────────────

        private sealed class DiskFullStream : Stream
        {
            private readonly FileStream _inner;
            private readonly long _cap;

            public DiskFullStream(string path, long cap)
            {
                _inner = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _cap = cap;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_inner.Length + count > _cap)
                {
                    // 与 Windows 真实磁盘满同一个错误：ERROR_DISK_FULL（0x80070070）。
                    throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
                }
                _inner.Write(buffer, offset, count);
                _inner.Flush(); // 真的写到盘上，确认半成品是真文件、失败后被删掉
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
            }
        }

        private static void CheckDiskFull()
        {
            Line("  G 负向：磁盘空间不足 / 写失败");
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(5));
            string save = Path.Combine(_saves, "campaign_slot0.json");
            string auto = Path.Combine(_saves, "campaign_slot0.auto2.json");
            string h1 = Hash(save), h2 = Hash(auto);
            int zipsBefore = Directory.GetFiles(FeedbackPack.PackDir, "*.zip").Length;
            File.WriteAllText(Path.Combine(FeedbackPack.PackDir, "feedback-20260101-000000.zip.tmp"), "half"); // 上次打包途中被结束留下的

            FeedbackPack.ResetForTests();
            FeedbackPack.SynchronousForTests = true;
            FeedbackPack.ScreenshotForTests = TinyPng;
            FeedbackPack.FreeSpaceForTests = _ => 4096;
            FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            FeedbackPack.Result a = Pumped();
            Expect(!a.Ok && a.ErrorKey == "diag.pack.disk_full" && UiConfirmDialog.Current != null && UiConfirmDialog.Current.Title == GameText.Get("diag.pack.fail_title")
                   && UiConfirmDialog.Current.Lines.Contains(GameText.Get("diag.pack.disk_full")),
                "事前估算空间不够：不开始写，提示“磁盘空间不足，反馈包没有生成……存档没有受影响”");
            UiConfirmDialog.DiscardAll();

            FeedbackPack.FreeSpaceForTests = null;
            string tmpSeen = null;
            FeedbackPack.OpenOutputForTests = p =>
            {
                tmpSeen = p;
                return new DiskFullStream(p, 2048);
            };
            FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            FeedbackPack.Result b = Pumped();
            Expect(!b.Ok && b.ErrorKey == "diag.pack.disk_full" && tmpSeen != null && !File.Exists(tmpSeen),
                "写到一半磁盘满（真实写盘到 2 KB 后报 ERROR_DISK_FULL）：按磁盘满提示，半个压缩包（.tmp）已删除");
            UiConfirmDialog.DiscardAll();

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            FeedbackPack.OpenOutputForTests = p => throw new UnauthorizedAccessException($"denied-crashfb Access to the path '{Path.Combine(profile, "AppData", "x.zip.tmp")}' is denied.");
            FeedbackPack.Request(FeedbackPack.Trigger.Hotkey);
            FeedbackPack.Result c = Pumped();
            string shown = string.Join(" | ", UiConfirmDialog.Current?.Lines ?? new List<string>());
            Expect(!c.Ok && c.ErrorKey == "diag.pack.failed_0" && (c.Detail ?? string.Empty).Contains("denied-crashfb") && !FeedbackPack.Busy,
                "其他写失败（没有权限）：提示失败原因，不卡在“正在生成”");
            Expect(!string.IsNullOrEmpty(profile) && c.Detail.Contains("<USERPROFILE>") && c.Detail.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0
                   && shown.Contains("denied-crashfb") && shown.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0,
                $"失败原因里的用户目录已脱敏（显示、通知历史、存档里都不会带出用户名）：{c.Detail}");
            UiConfirmDialog.DiscardAll();
            FeedbackPack.OpenOutputForTests = null;

            Expect(Hash(save) == h1 && Hash(auto) == h2 && Directory.GetFiles(FeedbackPack.PackDir, "*.zip").Length == zipsBefore && !Directory.GetFiles(FeedbackPack.PackDir, "*.tmp").Any(),
                "三种失败后：存档与自动存档字节不变，没有新增 zip，也没有残留 .tmp（上次被结束留下的半成品也已清掉）");
            Expect(FeedbackPack.IsDiskFull(new IOException("x", unchecked((int)0x80070027))) && FeedbackPack.IsDiskFull(new IOException("Disk full. Path C:\\a"))
                   && !FeedbackPack.IsDiskFull(new IOException("Sharing violation", unchecked((int)0x80070020))), "磁盘满判定：ERROR_HANDLE_DISK_FULL / Mono“Disk full”是，共享冲突不是");
            CrashReporter.EndSession();
        }

        // ── H 下次启动询问 ──────────────────────────────────────────────────

        private static void CheckCrashPrompt()
        {
            Line("  H 下次启动询问是否生成反馈包（FGR-SYS-060）");
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(6));
            CrashReporter.AbandonSessionForTests();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(7));
            CrashReporter.PromptInBatchModeForTests = false;
            bool batchSilent = !CrashReporter.OfferFeedbackPackAfterCrash() && UiConfirmDialog.Current == null;
            Expect(!Application.isBatchMode || batchSilent, "batchmode（没有人能回答）不弹询问，只记警告");

            CrashReporter.AbandonSessionForTests();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(8));
            CrashReporter.PromptInBatchModeForTests = true;
            FeedbackPack.ResetForTests();
            FeedbackPack.SynchronousForTests = true;
            FeedbackPack.ScreenshotForTests = TinyPng;
            bool asked = CrashReporter.OfferFeedbackPackAfterCrash();
            ConfirmRequest q = UiConfirmDialog.Current;
            Expect(asked && q != null && q.Title == GameText.Get("diag.crash.title") && q.ConfirmText == GameText.Get("diag.crash.make_pack")
                   && q.Lines.Any(l => l.Contains("campaign_slot0.auto2.json")),
                "上次没有正常退出：主菜单弹“上次游戏意外退出”，写明最近一次自动存档，按钮“生成反馈包 / 不用了”");
            Expect(!CrashReporter.OfferFeedbackPackAfterCrash(), "同一次运行只问一次（回到主菜单不再问）");
            UiConfirmDialog.Confirm();
            FeedbackPack.Result r = Pumped();
            Expect(r.Ok && r.Trigger == FeedbackPack.Trigger.AfterCrash && Text(ReadZip(r.Path), "system.txt").Contains("previous_session_crashed=True"),
                "选“生成反馈包”：生成一个包，系统信息里注明上次崩溃与崩溃记录");
            UiConfirmDialog.DiscardAll();

            CrashReporter.AbandonSessionForTests();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(9));
            GameSettings.SetLanguage(GameLanguage.En);
            CrashReporter.OfferFeedbackPackAfterCrash();
            bool en = UiConfirmDialog.Current != null && UiConfirmDialog.Current.Title == GameText.Get("diag.crash.title", GameLanguage.En) && !string.IsNullOrEmpty(UiConfirmDialog.Current.Title)
                      && !GameText.ContainsMarker(UiConfirmDialog.Current.Title + string.Join("", UiConfirmDialog.Current.Lines));
            UiConfirmDialog.Cancel();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en && FeedbackPack.CompletedCount == 1, "英文界面下询问是英文、没有缺键标记；选“不用了”不生成包");
            CrashReporter.EndSession();
            CrashReporter.PromptInBatchModeForTests = false;
        }

        // ── I 反馈包键 ──────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public bool GetKey(KeyCode key) => key == Down || Held.Contains(key);
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static int Press(Keys keys, KeyCode key, bool ctrl)
        {
            int before = FeedbackPack.CompletedCount;
            keys.Held.Clear();
            if (ctrl)
            {
                keys.Held.Add(KeyCode.LeftControl);
            }
            keys.Down = key;
            UiKitInputPump.Process();
            keys.Down = KeyCode.None;
            keys.Held.Clear();
            InputRouter.DebugClearConsumedKeys();
            return FeedbackPack.CompletedCount - before;
        }

        private static void CheckHotkey()
        {
            Line("  I 反馈包键（FGR-SYS-061“F8，可重绑”）");
            CampaignSession.Clear();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(10));
            FeedbackPack.ResetForTests();
            FeedbackPack.SynchronousForTests = true;
            FeedbackPack.ScreenshotForTests = TinyPng;
            GameSettings.ResetKeyBindingsToDefault();
            InputChord chord = GameSettings.KeyBindings.GetChord(GameActionId.CreateFeedbackPack);
            bool inCatalog = InputActionCatalog.TryGet(GameActionId.CreateFeedbackPack, out InputActionDef def) && def.Status == InputActionStatus.Wired && def.Contexts == InputContext.All;
            bool noConflict = !GameSettings.KeyBindings.FindAllConflicts().Any(p => p.A == GameActionId.CreateFeedbackPack || p.B == GameActionId.CreateFeedbackPack);
            Expect(inCatalog && chord.Key == KeyCode.F8 && chord.Mods == InputModifier.Ctrl && noConflict && InputBindingSet.IsRebindable(GameActionId.CreateFeedbackPack),
                "动作登记在表里（全部上下文、已接入、可重绑），默认 Ctrl+F8，和快捷栏 8（F8）等不冲突");
            var keys = new Keys();
            InputRouter.Reset();
            InputRouter.DebugSetReader(keys);
            InputRouter.SetScope(InputScope.Strategy);
            int made = Press(keys, KeyCode.F8, ctrl: true);
            Expect(made == 1 && FeedbackPack.LastResult.Ok && FeedbackPack.LastResult.Trigger == FeedbackPack.Trigger.Hotkey, "主菜单 / 战略视角按 Ctrl+F8 生成反馈包");
            UiConfirmDialog.DiscardAll();
            InputRouter.SetBuildMode(true);
            int plain = Press(keys, KeyCode.F8, ctrl: false);
            InputRouter.SetBuildMode(false);
            Expect(plain == 0, "建造模式里单按 F8 仍是快捷栏 8，不会误打包");
            UiConfirmDialog.Show(new ConfirmRequest { Title = "modal-crashfb" });
            InputRouter.SetGameplayPaused(true);
            int modal = Press(keys, KeyCode.F8, ctrl: true);
            InputRouter.SetGameplayPaused(false);
            UiConfirmDialog.DiscardAll();
            Expect(modal == 1, "暂停中、确认框 / 面板开着时也能按（界面上下文）");
            var conflicts = new List<GameActionId>();
            RebindResult rb = GameSettings.TryRebind(GameActionId.CreateFeedbackPack, new InputChord(KeyCode.F11, InputModifier.Ctrl), conflicts);
            int oldKey = Press(keys, KeyCode.F8, ctrl: true);
            int newKey = Press(keys, KeyCode.F11, ctrl: true);
            UiConfirmDialog.DiscardAll();
            GameSettings.ResetKeyBindingsToDefault();
            Expect(rb == RebindResult.Ok && oldKey == 0 && newKey == 1, $"改绑到 Ctrl+F11 后旧键不再触发、新键触发（{rb}）");
            InputRouter.Reset();
            InputRouter.DebugSetReader(null);
            CrashReporter.EndSession();
        }

        // ── J 本地统计（FG-GAP-047） ─────────────────────────────────────────

        private static void CheckLocalStats()
        {
            Line("  J 本地统计（FG-GAP-047 → FGR-SYS-100：需同意、只存本机、可导出）");
            LocalPlayStats.ResetForTests();
            GameSettings.SetLocalStatsConsent(false);
            CampaignState s = InCampaign();
            for (int i = 0; i < 5; i++)
            {
                LocalPlayStats.Tick(30f);
            }
            LocalPlayStats.EndSession();
            string exported = LocalPlayStats.Export(out string err);
            Expect(!GameSettings.LocalStatsConsent && LocalPlayStats.Current == null && !Directory.Exists(LocalPlayStats.Dir) && exported == null && err == "no_data",
                "默认不同意：不记录、不建任何文件，导出提示没有数据");

            GameSettings.SetLocalStatsConsent(true);
            s.Raids.Results.Results = new[] { new RaidResultRecord { Serial = 1, EndTick = 5, Outcome = "withdrawn" } }; // 开始记录前就结束的
            LocalPlayStats.Tick(1f);
            LocalPlayStats.Tick(2f);
            PlaySessionStat cur = LocalPlayStats.Current;
            Expect(cur != null && Math.Abs(cur.PlaySeconds - 3f) < 0.001f && Math.Abs(cur.ActSeconds[0] - 3f) < 0.001f, $"同意后开始记一局：游玩时长与第一幕用时按真实秒累计（{cur?.PlaySeconds}）");

            s.SignalCore.UplinkMachineLogicId = 7;
            LocalPlayStats.Tick(2f);
            s.SignalCore.UplinkMachineLogicId = 0;
            LocalPlayStats.Tick(1f);
            s.SignalCore.UplinkMachineLogicId = 9;
            LocalPlayStats.Tick(4f);
            s.SignalCore.UplinkMachineLogicId = 0;
            Expect(cur.Uplinks == 2 && Math.Abs(cur.UplinkSeconds - 6f) < 0.001f && Math.Abs(cur.AvgUplinkSeconds - 3f) < 0.001f,
                $"接入次数 {cur.Uplinks}、接入总时长 {cur.UplinkSeconds}、平均 {cur.AvgUplinkSeconds} 秒");

            s.Progress.Act = 2;
            LocalPlayStats.Tick(5f);
            Expect(Math.Abs(cur.ActSeconds[1] - 5f) < 0.001f, "进入第二幕后时间记到第二幕");

            s.Raids.Results.Results = new[]
            {
                new RaidResultRecord { Serial = 1, EndTick = 5, Outcome = "withdrawn" },
                new RaidResultRecord { Serial = 2, EndTick = 40, Outcome = "destroyed" },
                new RaidResultRecord { Serial = 3, EndTick = -1, Outcome = "open" },
            };
            LocalPlayStats.Tick(1.5f);
            s.Raids.Results.Results[2].EndTick = 60;
            s.Raids.Results.Results[2].Outcome = "core_lost";
            LocalPlayStats.Tick(1.5f);
            LocalPlayStats.Tick(1.5f);
            Expect(cur.RaidsDestroyed == 1 && cur.RaidsWithdrawn == 0 && cur.RaidsCoreLost == 1,
                $"突袭结果按结束的结算记录计（全歼 {cur.RaidsDestroyed} / 撤退 {cur.RaidsWithdrawn} / 核心被毁 {cur.RaidsCoreLost}），开始记录前结束的不算、每场只算一次");

            MachineRegistry.ResetForNewCampaign();
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, Vector2.zero, 100f, 100f);
            bool died = m.Success && MachineRegistry.MarkDeadByLogicId(m.LogicId).Success;
            MachineRegistry.MarkDeadByLogicId(m.LogicId); // 重复标记不重复计
            Expect(died && cur.MachinesLost == 1, $"机器阵亡数从机器阵亡的唯一翻转点计（{cur.MachinesLost}）");
            MachineRegistry.ResetForNewCampaign();

            float before = cur.PlaySeconds;
            GameClock.SetPaused(true);
            LocalPlayStats.Tick(2f);
            GameClock.SetPaused(false);
            GameClock.SetSpeed(3f);
            LocalPlayStats.Tick(2f);
            GameClock.SetSpeed(0.5f);
            LocalPlayStats.Tick(2f);
            GameClock.SetSpeed(1f);
            Expect(Math.Abs(cur.PlaySeconds - before - 6f) < 0.001f, "暂停、3x、0.5x 下都按真实时间记游玩时长（不按游戏时间放大缩小）");

            bool noFileYet = !File.Exists(LocalPlayStats.FilePath);
            LocalPlayStats.Tick(61f);
            bool flushed = File.Exists(LocalPlayStats.FilePath);
            LocalPlayStats.EndSession();
            PlayStatsFile back = LocalPlayStats.Load();
            PlaySessionStat b = back.Sessions.LastOrDefault();
            Expect(noFileYet && flushed && back.Sessions.Count == 1 && b != null && Math.Abs(b.PlaySeconds - cur.PlaySeconds) < 0.01f && b.Uplinks == 2
                   && b.RaidsDestroyed == 1 && b.RaidsCoreLost == 1 && b.MachinesLost == 1 && Math.Abs(b.ActSeconds[1] - cur.ActSeconds[1]) < 0.01f && b.ActSeconds[1] >= 5f,
                "每 60 秒写一次盘、离开一局时写盘；从文件读回与内存一致（写读往返）");

            CampaignState s2 = CampaignState.CreateNew("crashfb-2", "Standard", 4243);
            CampaignSession.Set(1, s2);
            LocalPlayStats.Tick(4f);
            CampaignSession.Clear();
            LocalPlayStats.Tick(1f); // 回到主菜单 = 这一局结束
            string path = LocalPlayStats.Export(out err);
            string json = path != null ? File.ReadAllText(path) : string.Empty;
            Expect(path != null && LocalPlayStats.Load().Sessions.Count == 2 && json.Contains("\"PlaySeconds\"") && json.Contains("\"AvgUplinkSeconds\"")
                   && Path.GetFullPath(path).StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase),
                "第二局另记一条；导出成本机带时间的文件（含各项字段名）");

            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(30));
            CampaignSession.Set(1, s2);
            LocalPlayStats.Tick(7f); // 不到 60 秒，还没写过盘
            CrashReporter.OnQuitting(); // Application.quitting（关窗口 / Alt+F4）
            PlaySessionStat quitLast = LocalPlayStats.Load().Sessions.LastOrDefault();
            Expect(LocalPlayStats.Load().Sessions.Count == 3 && quitLast != null && Math.Abs(quitLast.PlaySeconds - 7f) < 0.01f && LocalPlayStats.Current == null && !File.Exists(CrashReporter.MarkerPath),
                "直接退出游戏：这一局（不到 60 秒）也写进统计文件，会话标记删除（不当作崩溃）");
            CampaignSession.Clear();

            long size = new FileInfo(LocalPlayStats.FilePath).Length;
            GameSettings.SetLocalStatsConsent(false);
            CampaignSession.Set(1, s2);
            LocalPlayStats.Tick(100f);
            LocalPlayStats.EndSession();
            Expect(LocalPlayStats.Current == null && new FileInfo(LocalPlayStats.FilePath).Length == size, "撤回同意后立即停止记录，不再写文件");

            File.WriteAllText(LocalPlayStats.FilePath, "{broken");
            Expect(LocalPlayStats.Load().Sessions.Count == 0, "统计文件损坏时按空表处理，不抛异常");
            CampaignSession.Clear();
        }

        // ── K 配置表损坏（FG-GAP-012） ───────────────────────────────────────

        private static byte[] RealBytes(string file) =>
            File.ReadAllBytes(Path.Combine(Application.dataPath, "GameRes", "Raw", "Configs", "bytes", file + ".bytes"));

        private static void CheckConfigCorrupt()
        {
            Line("  K 配置表损坏 / 缺失 → “文件损坏，请校验完整性”（FG-GAP-012，IC-REQ-013、B06 / B11）");
            // 与冷启动同一条读表路径（ConfigSystem.Load → LoadByteBuf → LastRequestedFile），只把字节来源换成构造的坏文件。
            Expect(ConfigIntegrity.Probe(new ConfigSystem(RealBytes)) == null, "完整的表：探测通过");
            string missing = ConfigIntegrity.Probe(new ConfigSystem(f => f == "fg_tbbuilding" ? null : RealBytes(f)));
            string truncated = ConfigIntegrity.Probe(new ConfigSystem(f =>
            {
                byte[] all = RealBytes(f);
                return f != "fg_tbloctext" ? all : all.Take(all.Length / 3).ToArray();
            }));
            Expect(missing != null && missing.Contains("fg_tbbuilding") && missing.Contains("not found") && truncated != null && truncated.Contains("fg_tbloctext"),
                $"缺文件 / 文件被截断都探测得出，并指出是哪个文件（{missing} | {truncated}）");

            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(11));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            UiConfirmDialog.Show(ConfigIntegrity.BuildRequest(truncated));
            ConfirmRequest q = UiConfirmDialog.Current;
            Expect(q != null && q.Title == "文件损坏，请校验完整性" && q.ConfirmText == "退出游戏" && q.Lines.Any(l => l.Contains("验证游戏文件的完整性")) && q.Lines.Any(l => l.Contains("fg_tbloctext")),
                "提示写明原因、修法（Steam 校验完整性 / 重装）与损坏的文件；文字不依赖（可能已经坏了的）文本表");
            int quits = _quits, opened = Opened.Count;
            UiConfirmDialog.Cancel();
            bool reshown = UiConfirmDialog.Current != null && UiConfirmDialog.Current.Title == q.Title;
            Expect(Opened.Count == opened + 1 && Opened.Last() == CrashReporter.LogDir && reshown, "“打开日志文件夹”打开日志目录，提示留在屏幕上（不会只剩空屏幕）");
            UiConfirmDialog.Confirm();
            Expect(_quits == quits + 1 && UiConfirmDialog.Current == null, "“退出游戏”安全退出");
            GameSettings.SetLanguage(GameLanguage.En);
            ConfirmRequest en = ConfigIntegrity.BuildRequest(truncated);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(ConfigIntegrity.IsEnglish() == false && en.Title == ConfigIntegrity.Text["title"].En && en.ConfirmText == "Quit game", "英文设置下提示是英文（语言直接读设置 JSON，不经过坏表）");
            UiConfirmDialog.DiscardAll();
            CheckCorruptScreenRealPath(truncated);

            string app = File.ReadAllText(Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/GameApp.cs"));
            int install = app.IndexOf("CrashReporter.Install()", StringComparison.Ordinal);
            int probe = app.IndexOf("ConfigIntegrity.Probe()", StringComparison.Ordinal);
            int startup = app.IndexOf("GameRoot.Startup()", StringComparison.Ordinal);
            int menu = app.IndexOf("ShowUIAsync<GameLogic.MainMenuUI>", StringComparison.Ordinal);
            Expect(install >= 0 && install < probe && probe < startup && startup < menu, "冷启动顺序：先装崩溃记录 → 探测配置表 → 才启动框架与主菜单（坏表不进主菜单）");
            string menuSrc = File.ReadAllText(Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/UI/MainMenuUI/MainMenuUI.cs"));
            Expect(menuSrc.Contains("CrashReporter.OfferFeedbackPackAfterCrash()") && menuSrc.Contains("LocalPlayStats.EndSession()"), "主菜单打开时问崩溃反馈包、结束本地统计这一局");
            CrashReporter.EndSession();
        }

        /// <summary>K2：真实执行坏表入口 ShowCorruptScreen → UiKitRuntime.MountOverlayOnly（编辑模式下宿主不加载 UXML；带图形的渲染交冒烟 / FG15-ACC-04）。</summary>
        private static void CheckCorruptScreenRealPath(string error)
        {
            bool wasMounted = UiKitRuntime.SetMountedForTests(false);
            GameObject host = null;
            var logged = new List<string>();
            Application.LogCallback capture = (c, st, t) =>
            {
                logged.Add(t + " " + c);
                CrashReporter.RecordLog(c, st, t);
            };
            Application.logMessageReceived += capture;
            try
            {
                int quits = _quits, opened = Opened.Count;
                host = ConfigIntegrity.ShowCorruptScreen(error);
                Application.logMessageReceived -= capture;
                CrashReporter.Flush();
                string runLog = CrashReporter.ReadShared(CrashReporter.CurrentRunLog);
                Expect(host != null && host.name == "[UiKitOverlayHost]" && host.GetComponent<UiKitOverlayUIToolkit>() != null && host.GetComponent<CorruptScreenGuard>() != null
                       && UiKitRuntime.Mounted && UiKitRuntime.MountOverlayOnly() == null,
                    "坏表入口真实执行：只挂浮层宿主（带按键 / 兜底守卫），之后不会再挂第二个");
                Expect(logged.Any(l => l.StartsWith("Error") && l.Contains("fg_tbloctext")) && runLog.Contains("配置表损坏或缺失") && runLog.Contains("fg_tbloctext"),
                    "错误写进运行日志（反馈包里能看到），点名坏文件");
                ConfirmRequest q = UiConfirmDialog.Current;
                Expect(q != null && q.Title == ConfigIntegrity.Text["title"].Zh && q.Lines.Any(l => l.Contains("fg_tbloctext")), "确认框弹出“文件损坏，请校验完整性”");
                CorruptScreenGuard.HandleKeys(esc: true, enter: false);
                Expect(Opened.Count == opened + 1 && Opened.Last() == CrashReporter.LogDir && UiConfirmDialog.Current?.Title == q?.Title && _quits == quits,
                    "Esc（物理键，不经过可能坏掉的按键表）：打开日志文件夹，提示留着");
                Expect(CorruptScreenGuard.NeedsFallback(false, CorruptScreenGuard.FallbackAfterSeconds) && !CorruptScreenGuard.NeedsFallback(false, 1f)
                       && !CorruptScreenGuard.NeedsFallback(true, 60f),
                    $"浮层 {CorruptScreenGuard.FallbackAfterSeconds} 秒还没加载出来（界面资源也坏了）就改用 IMGUI 画同一个提示；加载出来就不画");
                CorruptScreenGuard.HandleKeys(esc: false, enter: true);
                CorruptScreenGuard.HandleKeys(esc: false, enter: true);
                Expect(_quits == quits + 1 && !UiConfirmDialog.IsOpen, "回车：退出游戏（只退一次；没有提示时按键不动作）");
            }
            finally
            {
                Application.logMessageReceived -= capture;
                if (host != null)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
                UiKitRuntime.SetMountedForTests(wasMounted);
                UiConfirmDialog.DiscardAll();
            }
        }

        // ── L 设置页 ───────────────────────────────────────────────────────

        private static void CheckSettingsItems()
        {
            Line("  L 设置 → 游戏性：本地统计开关、导出、生成反馈包");
            GameSettings.SetLocalStatsConsent(false);
            SettingItem toggle = SettingsCatalog.All.FirstOrDefault(i => i.Id == "gameplay.local_stats");
            SettingItem export = SettingsCatalog.All.FirstOrDefault(i => i.Id == "gameplay.export_stats");
            SettingItem pack = SettingsCatalog.All.FirstOrDefault(i => i.Id == "gameplay.feedback_pack");
            Expect(toggle != null && toggle.Kind == SettingKind.Toggle && !toggle.GetBool() && export != null && pack != null
                   && !GameText.ContainsMarker(toggle.Name + export.Name + pack.Name), "三项都在游戏性分类里，默认不同意，名字有中文文本");
            toggle.SetBool(true);
            bool on = GameSettings.LocalStatsConsent;
            SettingsCatalog.ResetCategory(SettingsCategory.Gameplay);
            Expect(on && !GameSettings.LocalStatsConsent, "开关写设置；游戏性“恢复默认”回到不同意");
            if (Directory.Exists(LocalPlayStats.Dir))
            {
                Directory.Delete(LocalPlayStats.Dir, true);
            }
            export.Invoke();
            Expect(SettingsCatalog.LastExportText == GameText.Get("diag.stats.no_consent") && UiConfirmDialog.Current != null && UiConfirmDialog.Current.Title == GameText.Get("diag.stats.export_title"),
                "没同意时点导出：说明要先在设置里打开统计");
            UiConfirmDialog.DiscardAll();
            CrashReporter.StartSession(DateTime.UtcNow.AddMinutes(12));
            FeedbackPack.ResetForTests();
            FeedbackPack.SynchronousForTests = true;
            FeedbackPack.ScreenshotForTests = TinyPng;
            pack.Invoke();
            FeedbackPack.Result r = Pumped();
            Expect(r.Ok && r.Trigger == FeedbackPack.Trigger.Settings, "设置页“生成反馈包”走同一入口");
            UiConfirmDialog.DiscardAll();
            CrashReporter.EndSession();
        }

        // ── 工具 ───────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                UiEscapeStack.Clear();
                FeedbackPack.ResetForTests();
                CrashReporter.AbandonSessionForTests();
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
        }

        private static void Line(string text) => _report.AppendLine(text);
    }
}
