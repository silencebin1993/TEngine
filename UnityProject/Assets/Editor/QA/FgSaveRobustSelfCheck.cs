using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.WorldGen;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-SYS-01 存档稳健性与云存档的自动验收（FGR-SYS-001～007；FGT-SYS-001～003）。全部走真实的 <see cref="CampaignSaveService"/> /
    /// <see cref="CampaignAutoSaveService"/> / <see cref="CloudSaveService"/> 读写临时目录里的真实文件（不碰玩家槽位）：
    /// A 断电式：外部 pwsh 子进程编译游戏同一份 <see cref="SaveFileCommit"/> 源码循环写档，父进程在写入途中强制结束它，之后按玩家路径读档 / 读备份；
    /// B 后台写入：自动存档主线程阻塞 ≤ 100 ms、写盘与缩略图编码在后台线程、写入中再存档排队合并、读档 / 同步写入等后台写完；
    /// C 槽位：手动槽不限（分页）、3 份轮换只覆盖本槽自己的旧代、快速存档、读副本前另存更新的主档、删除 / 清理保留文件 / 打开目录只由玩家触发；
    /// D 列表只读头部：大存档列表耗时、正文被改只在读档时发现并登记、截断在列表就能看出；
    /// E 存档卡：缩略图、真实游玩时长（暂停不计、倍速不影响）、复制种子 / 短码；
    /// F 云存档：上传 / 下载 / 冲突（两边时间与进度）/ 选择后落选的一份另存保留 / 离线；
    /// G FG-EMG 存档域往返（内核快照里的在飞弹体、斑块、残骸、累积量；发现记录、地面标记、长期留存物）与 v2 旧档缺字段取缺省；
    /// H 自动存档时机：定时（5 / 10 / 20 / 关，真实时间，暂停不计）、静默夜开始帧末存档（攻城中推迟、换了战役作废）；
    /// I Demo 的 6 个时机之“蓝图保存”“启动信标”：正式服务入口成功后自动存档；
    /// J 界面入口：真 UXML 挂上存档管理窗口 / 云冲突窗口，按钮走 clicked（缩略图、复制、读副本、删除与清理二次确认、保留本机 / 云端、不选就关）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSaveRobustSelfCheck）。
    /// </summary>
    public static class FgSaveRobustSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;

        [MenuItem("QA/自检/FG 存档稳健性与云存档")]
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
            Line("\n[存档稳健性] FG15-SYS-01 存档稳健性与云存档（FGR-SYS-001～007，FGT-SYS-001～003）");
            GameLanguage originalLanguage = GameSettings.Language;
            int originalAuto = GameSettings.AutoSaveMinutes;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgrobust-" + Guid.NewGuid().ToString("N"));
            Func<SaveThumbnail.Shot> thumbHandler = SaveThumbnail.CaptureHandler;
            Action<string> openDir = CampaignSaveService.OpenDirectoryHandler;
            Action<string> clip = CampaignSlotText.ClipboardHandler;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                MachineRegistry.ResetForNewCampaign();

                Run("A", CheckPowerCut);
                Run("B", CheckBackgroundWrites);
                Run("C", CheckSlotsAndRotation);
                Run("D", CheckHeaderOnlyList);
                Run("E", CheckCard);
                Run("F", CheckCloud);
                Run("G", CheckEmgDomains);
                Run("H", CheckTimers);
                Run("I", CheckDemoMoments);
                Run("J", CheckPanels);
            }
            catch (Exception e)
            {
                Fail($"存档稳健性自检抛异常：{e}");
            }
            finally
            {
                CampaignSaveService.WaitForPendingWrites();
                CampaignSaveService.PumpBackgroundResults();
                CampaignSaveService.BackgroundDelayMsForTests = 0;
                CampaignAutoSaveService.ForceBackgroundForTests = false;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CampaignSaveService.CrashPointForTests = SaveCrashPoint.None;
                SaveThumbnail.CaptureHandler = thumbHandler;
                CampaignSaveService.OpenDirectoryHandler = openDir;
                CampaignSlotText.ClipboardHandler = clip;
                CloudSaveService.Provider = null;
                CloudSaveService.Conflicts.Clear();
                GameLogic.UI.Kit.SavePanelUIToolkit.Close();
                CampaignSession.Clear();
                IntervalAutoSave.ResetForTests();
                MachineRegistry.ResetForNewCampaign();
                GameSettings.SetAutoSaveMinutes(originalAuto);
                GameSettings.SetLanguage(originalLanguage);
                GameLogic.Core.StrategyClock.Reset();
                GameLogic.Core.InputRouter.SetGameplayPaused(false);
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            return _fail;
        }

        private static void Run(string name, Action check)
        {
            try
            {
                ClearAll();
                check();
            }
            catch (Exception e)
            {
                Fail($"{name} 段抛异常：{e}");
            }
            finally
            {
                CampaignSaveService.WaitForPendingWrites();
                CampaignSaveService.PumpBackgroundResults();
                CampaignSaveService.BackgroundDelayMsForTests = 0;
                CampaignAutoSaveService.ForceBackgroundForTests = false;
                CampaignSession.Clear();
            }
        }

        // ── A. 断电式：外部进程写到一半被强制结束（FGT-SYS-001）──────────────────────

        private static void CheckPowerCut()
        {
            Line("  · A. 写入中途强制结束进程（FGT-SYS-001）：外部子进程编译游戏同一份 SaveFileCommit.cs 循环写档，父进程在写入途中把它强制结束（TerminateProcess，清理代码一行都不执行）");
            string pwsh = FindPwsh();
            if (pwsh == null)
            {
                Fail("A 找不到 PowerShell 7（pwsh），断电式测试无法运行");
                return;
            }
            const int slot = 0;
            CampaignState a = Big("fgrobust-power", 111, 6000);
            CampaignState b = Big("fgrobust-power", 222, 6000);
            string fileA = Path.Combine(_dir, "power_a.bin");
            string fileB = Path.Combine(_dir, "power_b.bin");
            CampaignSaveService.Save(7, a, SaveReason.Manual);
            File.Copy(CampaignSaveService.SlotPath(7), fileA, true);
            CampaignSaveService.Save(7, b, SaveReason.Manual);
            File.Copy(CampaignSaveService.SlotPath(7), fileB, true);
            ClearSlot(7);
            string src = Path.GetFullPath("Assets/GameScripts/HotFix/GameLogic/Campaign/SaveFileCommit.cs");
            string main = CampaignSaveService.SlotPath(slot);
            int[] delays = { 30, 80, 140, 220, 310, 430, 560, 720 };
            int rounds = 0;
            int midWrite = 0;
            int restored = 0;
            var bad = new List<string>();
            foreach (int delay in delays)
            {
                ClearSlot(slot);
                CampaignSaveService.Save(slot, a, SaveReason.Manual);
                CampaignSaveService.Save(slot, b, SaveReason.Manual); // 主档 = b、备份 = a，两份都完整
                string script =
                    $"$ErrorActionPreference='Stop'; Add-Type -Path '{src}'; $a=[IO.File]::ReadAllBytes('{fileA}'); $b=[IO.File]::ReadAllBytes('{fileB}'); " +
                    "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); $i=0; " +
                    $"while ($true) {{ if ($i % 2) {{ $d=$b }} else {{ $d=$a }}; [GameLogic.Campaign.SaveFileCommit]::WriteAtomic('{main}', $d, $true); $i++ }}";
                var psi = new ProcessStartInfo(pwsh, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process p = Process.Start(psi))
                {
                    Task<string> ready = p.StandardOutput.ReadLineAsync();
                    if (!ready.Wait(90000) || ready.Result != "ready")
                    {
                        string err = p.HasExited ? p.StandardError.ReadToEnd() : "（90 秒内没有就绪）";
                        try { p.Kill(); } catch { }
                        Fail($"A 子进程没能开始写档：{err.Trim()}");
                        return;
                    }
                    Thread.Sleep(delay);
                    p.Kill(); // Windows 上即 TerminateProcess：与断电一样，进程里的 finally / 清理一行都不执行
                    p.WaitForExit(10000);
                }
                rounds++;
                bool tmpLeft = File.Exists(main + ".tmp");
                if (tmpLeft)
                {
                    midWrite++;
                }
                // 玩家下一次打开游戏：列表 → 读取；主档读不了就点“读取备份”。
                CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
                LoadResult r = CampaignSaveService.Load(slot);
                if (!r.Success)
                {
                    CampaignSlotMetadata after = CampaignSaveService.GetSlotMetadata(slot);
                    if (after.HasBackup && CampaignSaveService.RestoreFromBak(slot))
                    {
                        restored++;
                        r = CampaignSaveService.Load(slot);
                    }
                }
                int scrap = r.Success ? r.State.Scrap : -1;
                // 之后的一次存档照常成功（残留的临时文件被覆盖），读回最新一份。
                CampaignState next = CampaignState.CreateNew("fgrobust-power", "Standard", 3);
                next.Scrap = 333;
                SaveResult resave = CampaignSaveService.Save(slot, next, SaveReason.Manual);
                LoadResult again = CampaignSaveService.Load(slot);
                if (!r.Success || (scrap != 111 && scrap != 222) || meta.State == CampaignSlotState.Empty
                    || !resave.Success || !again.Success || again.State.Scrap != 333 || File.Exists(main + ".tmp"))
                {
                    bad.Add($"延迟 {delay} ms：列表 {meta.State}，读档 {r.Outcome}/{scrap}，再存 {resave.Outcome}，再读 {again.State?.Scrap}");
                }
            }
            Expect(bad.Count == 0 && rounds == delays.Length && midWrite >= 1,
                $"A {rounds} 次在写档途中强制结束（{midWrite} 次停在临时文件写了一半 / 还没替换）：每次都读到完整的上一版或这一版（从备份恢复 {restored} 次），之后存档照常、临时文件被覆盖" +
                (bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad)));
        }

        private static string FindPwsh()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>();
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    candidates.Add(Path.Combine(dir.Trim(), "pwsh.exe"));
                }
            }
            candidates.Add(@"C:\Program Files\PowerShell\7\pwsh.exe");
            candidates.Add(Path.Combine(local, @"Programs\PowerShell\7\pwsh.exe"));
            candidates.Add(Path.Combine(local, @"Microsoft\WindowsApps\pwsh.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        // ── B. 后台写入（FGR-SYS-002 / FGT-SYS-003）──────────────────────────────────

        private static void CheckBackgroundWrites()
        {
            Line("  · B. 自动存档不卡顿（FGT-SYS-003）：主线程只拍快照，写盘、校验、缩略图编码在后台线程；写入中再存档排队、读档等写完");
            const int slot = 0;
            CampaignState big = Big("fgrobust-bg", 300, 4000);
            CampaignSession.Set(slot, big);
            CampaignAutoSaveService.ForceBackgroundForTests = true;
            SaveThumbnail.Shot shot = null;
            SaveThumbnail.CaptureHandler = () => shot = SaveThumbnail.Placeholder(CampaignSession.Current);
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var sw = Stopwatch.StartNew();
            SaveResult r = CampaignAutoSaveService.SaveAuto(SaveReason.Interval, toast: false);
            double block = sw.Elapsed.TotalMilliseconds;
            bool pending = CampaignSaveService.PendingWrites > 0;
            CampaignSaveService.WaitForPendingWrites();
            CampaignSaveService.PumpBackgroundResults();
            LoadResult back = CampaignSaveService.Load(slot);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            long bytes = new FileInfo(CampaignSaveService.SlotPath(slot)).Length;
            ExpectPerf(r.Success && pending && CampaignSaveService.LastBackgroundResult.Success && CampaignSaveService.LastBackgroundThreadId != mainThread
                       && shot != null && shot.EncodeThreadId != 0 && shot.EncodeThreadId != mainThread
                       && back.Success && back.State.Scrap == 300 && meta.ThumbnailJpg != null && CampaignSaveService.LastCopySlot == CampaignSaveService.AutoSlot(slot, 0),
                $"B 压力档（{bytes / 1024.0 / 1024.0:F1} MB）定时自动存档：主线程 {block:F1} ms（快照 {CampaignSaveService.LastPrepareMs:F1} ms：{CampaignSaveService.LastPrepareBreakdown}），写盘 {CampaignSaveService.LastBackgroundCommitMs:F0} ms 在后台线程 {CampaignSaveService.LastBackgroundThreadId}（主线程 {mainThread}），" +
                $"缩略图在线程 {shot?.EncodeThreadId} 编码；读回一致、带缩略图、写了第 1 份轮换自动存档",
                PerfGate.Le(block, 100, "自动存档主线程阻塞 ms"));

            // FGR-SYS-002“主线程阻塞 ≤ 100 毫秒，其余在后台完成”：游戏中只有出发 / 到达的回滚基线同步写（调用方当场复制基线文件、失败就中止出发）。
            SaveReason[] syncReasons = Enum.GetValues(typeof(SaveReason)).Cast<SaveReason>().Where(x => !CampaignAutoSaveService.InBackground(x)).OrderBy(x => x).ToArray();
            Expect(syncReasons.SequenceEqual(new[] { SaveReason.NewCampaign, SaveReason.Manual, SaveReason.ExpeditionDepartConfirm }.OrderBy(x => x)),
                $"B 游戏中同步写的只剩 [{string.Join("、", syncReasons)}]（新建战役、手动存档、出发 / 到达回滚基线）；进入家园、远征结算、蓝图保存、首领战、启动信标、结局、后日谈、定时、预警、揭示、终战、静默夜、快速存档都在后台写盘");
            var swSync = Stopwatch.StartNew();
            SaveResult depart = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm, toast: false);
            double syncBlock = swSync.Elapsed.TotalMilliseconds;
            Expect(depart.Success && CampaignSaveService.PendingWrites == 0 && CampaignSaveService.Load(slot).State?.LastSaveReason == SaveReason.ExpeditionDepartConfirm,
                "B 出发 / 到达回滚基线同步写：返回时文件已落盘（调用方紧接着复制基线）");
            Line($"  ⚠ 性能提示（DEBT-FG15SYS01-05，交 FG15-SYS-02，不作本卡门槛）：同一压力档出发前回滚基线同步存档主线程 {syncBlock:F1} ms（{CampaignSaveService.LastSyncSaveBreakdown}）");

            // 写入进行中又触发存档：排队；还没开始写的被更新的快照取代；读档等写完，读到最新一份。
            CampaignSaveService.BackgroundDelayMsForTests = 400;
            int coalescedBefore = CampaignSaveService.CoalescedWrites;
            CampaignState s1 = Small("fgrobust-q", 1);
            CampaignState s2 = Small("fgrobust-q", 2);
            CampaignState s3 = Small("fgrobust-q", 3);
            ClearSlot(1);
            CampaignSaveService.SaveInBackground(1, s1, SaveReason.Interval, null, SaveCopyKind.None);
            Thread.Sleep(50); // 工作线程已取走 s1、正在“写”
            CampaignSaveService.SaveInBackground(1, s2, SaveReason.Interval, null, SaveCopyKind.None);
            CampaignSaveService.SaveInBackground(1, s3, SaveReason.Interval, null, SaveCopyKind.None);
            int queued = CampaignSaveService.PendingWrites;
            var wait = Stopwatch.StartNew();
            LoadResult latest = CampaignSaveService.Load(1);
            double waited = wait.Elapsed.TotalMilliseconds;
            CampaignState bak = LoadState(CampaignSaveService.BakPath(1));
            Expect(queued == 2 && CampaignSaveService.CoalescedWrites == coalescedBefore + 1 && latest.Success && latest.State.Scrap == 3 && waited >= 300
                   && bak?.Scrap == 1,
                $"B 写入中又存两次：排队 {queued}（第 3 次取代了还没开始写的第 2 次），读档等了 {waited:F0} ms、读到最新（废料 {latest.State?.Scrap}），备份是第 1 次（{bak?.Scrap}）");

            // 不同副本类型交错排队（正在写 X，排着 自动 1、快速；又来 自动 2）：合并后的新快照排到队尾，战役槽主档最后是最新的自动 2，不被更早的快速存档盖回去。
            ClearSlot(1);
            CampaignSaveService.SaveInBackground(1, Small("fgrobust-q", 21), SaveReason.Interval, null, SaveCopyKind.None);
            Thread.Sleep(50); // 工作线程已取走 21、正在“写”
            CampaignSaveService.SaveInBackground(1, Small("fgrobust-q", 22), SaveReason.Interval, null, SaveCopyKind.Auto);
            CampaignSaveService.SaveInBackground(1, Small("fgrobust-q", 23), SaveReason.QuickSave, null, SaveCopyKind.Quick);
            CampaignSaveService.SaveInBackground(1, Small("fgrobust-q", 24), SaveReason.Interval, null, SaveCopyKind.Auto);
            LoadResult interleaved = CampaignSaveService.Load(1);
            CampaignState quickCopy = LoadState(CampaignSaveService.SlotPath(CampaignSaveService.QuickSlot(1)));
            Expect(interleaved.State?.Scrap == 24 && quickCopy?.Scrap == 23,
                $"B 自动 / 快速交错排队后又来一次自动存档：主档 = 最新的 24（{interleaved.State?.Scrap}），快速存档 = 23（{quickCopy?.Scrap}）");

            // 同步写入（出发等事务边界）排在后台写入之后：顺序不乱。
            CampaignSaveService.SaveInBackground(1, Small("fgrobust-q", 4), SaveReason.Interval, null, SaveCopyKind.None);
            SaveResult sync = CampaignSaveService.Save(1, Small("fgrobust-q", 5), SaveReason.Manual);
            CampaignSaveService.BackgroundDelayMsForTests = 0;
            LoadResult ordered = CampaignSaveService.Load(1);
            CampaignState orderedBak = LoadState(CampaignSaveService.BakPath(1));
            Expect(sync.Success && ordered.State?.Scrap == 5 && orderedBak?.Scrap == 4,
                $"B 后台写入还没落盘时同步存档：先等后台写完再写，主档 = 后写的（{ordered.State?.Scrap}），备份 = 先写的（{orderedBak?.Scrap}）");

            // 后台写盘失败（目录被占成文件）：主线程那一段返回成功，失败在下一帧给提示并记原因；旧档不动。
            string blocked = Path.Combine(_dir, "blocked");
            File.WriteAllText(blocked, "x");
            CampaignSaveService.SaveDirectoryOverrideForTests = blocked;
            SaveResult refused = CampaignSaveService.SaveInBackground(0, Small("fgrobust-q", 6), SaveReason.Interval, null, SaveCopyKind.None);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            Expect(!refused.Success && refused.Outcome == SaveOutcome.DirectoryNotWritable && CampaignSaveService.PendingWrites == 0,
                $"B 存档目录不可写：在主线程那一段就拒绝（{refused.Outcome}），不排队");
            int failsBefore = CampaignSaveService.BackgroundFailures;
            string mainPath = CampaignSaveService.SlotPath(2);
            Directory.CreateDirectory(mainPath + ".tmp"); // 临时文件位置被一个目录占住：后台写临时文件失败
            CampaignSaveService.SaveInBackground(2, Small("fgrobust-q", 7), SaveReason.Interval, null, SaveCopyKind.None);
            CampaignSaveService.WaitForPendingWrites();
            CampaignSaveService.PumpBackgroundResults();
            Directory.Delete(mainPath + ".tmp");
            Expect(CampaignSaveService.BackgroundFailures == failsBefore + 1 && CampaignSaveService.LastBackgroundResult.Outcome == SaveOutcome.TempWriteFailed && !File.Exists(mainPath),
                $"B 后台写临时文件失败：下一帧记失败并提示（{CampaignSaveService.LastBackgroundResult.Outcome}），没有半份主档");
        }

        // ── C. 槽位、轮换、快速存档、永不自动删除（FGR-SYS-001 / 003，FG-GAP-013）──────────────

        private static void CheckSlotsAndRotation()
        {
            Line("  · C. 手动槽不限（分页）、3 份轮换只覆盖本槽旧代、快速存档、读副本、删除 / 清理 / 打开目录只由玩家做");
            for (int i = 0; i < 7; i++)
            {
                CampaignSaveService.Save(i, Small("fgrobust-many-" + i, 10 + i), SaveReason.NewCampaign);
            }
            int[] listed = CampaignSaveService.ListedManualSlots();
            int[] page3 = CampaignSaveService.PageSlots(listed, 2);
            CampaignSlotMetadata[] all = CampaignSaveService.GetAllSlotMetadata();
            Expect(CampaignSaveService.NextFreeSlot() == 7 && listed.SequenceEqual(Enumerable.Range(0, 8)) && CampaignSaveService.PageCount(listed) == 3
                   && page3.SequenceEqual(new[] { 6, 7 }) && all.Length == 8 && all[7].State == CampaignSlotState.Empty
                   && CampaignSaveService.ResolveContinueSlot(all) == 6,
                $"C 7 个战役槽 + 末尾空槽 7：分 {CampaignSaveService.PageCount(listed)} 页、第 3 页 [{string.Join(",", page3)}]，新建用槽 {CampaignSaveService.NextFreeSlot()}，“继续”指向最新的槽 6");

            // 轮换：本槽第 4 次自动存档覆盖本槽最旧的那份；别的槽、保留文件一个不少。
            const int home = 3;
            CampaignState mine = Small("fgrobust-many-3", 0);
            CampaignSession.Set(home, mine);
            string otherAuto = CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(4, 0));
            CampaignSession.Set(4, Small("fgrobust-many-4", 400));
            CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete, toast: false);
            byte[] otherBytes = File.ReadAllBytes(otherAuto);
            File.WriteAllText(CampaignSaveService.KeepPath(home, "manual"), "kept");
            CampaignSession.Set(home, mine);
            var written = new List<int>();
            for (int i = 1; i <= 4; i++)
            {
                mine.Scrap = 100 + i;
                CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete, toast: false);
                written.Add(CampaignSaveService.LastCopySlot);
                Thread.Sleep(15); // 存档时间（毫秒精度）区分新旧
            }
            CampaignSlotMetadata[] copies = CampaignSaveService.CopyMetadata(home);
            int[] scraps = Enumerable.Range(0, 3).Select(k => LoadState(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(home, k)))?.Scrap ?? -1).ToArray();
            Expect(written.SequenceEqual(new[] { CampaignSaveService.AutoSlot(home, 0), CampaignSaveService.AutoSlot(home, 1), CampaignSaveService.AutoSlot(home, 2), CampaignSaveService.AutoSlot(home, 0) })
                   && scraps.SequenceEqual(new[] { 104, 102, 103 }) && copies.Take(3).All(m => m.State == CampaignSlotState.Ready && m.Kind == SaveSlotKind.Auto && m.HomeSlot == home)
                   && File.ReadAllBytes(otherAuto).SequenceEqual(otherBytes) && CampaignSaveService.PreservedFiles(home).Length == 1
                   && CampaignSaveService.Load(home).State?.Scrap == 104,
                $"C 第 4 次自动存档覆盖本槽最旧的自动存档 1（三份 = [{string.Join(",", scraps)}]），战役槽主档是最新（104）；槽 4 的自动存档逐字节不变、保留文件还在");

            // 在这个槽新建战役：旧战役的轮换自动存档挪成保留文件，新战役的轮换不会覆盖别的战役的档。
            CampaignSaveService.Save(home, Small("fgrobust-new-3", 1), SaveReason.NewCampaign);
            bool autosMoved = Enumerable.Range(0, 3).All(k => !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(home, k))))
                              && CampaignSaveService.PreservedFiles(home).Count(f => f.Contains(".keep-auto-")) == 3;
            Expect(autosMoved, "C 在已有战役的槽位新建：旧战役的 3 份自动存档另存为 keep-auto 保留，不会被新战役的轮换覆盖");

            // 快速存档 + 读副本：活动槽位回到战役槽，战役槽里更新的那份主档先另存（keep-superseded）。
            CampaignState q = Small("fgrobust-new-3", 50);
            CampaignSession.Set(home, q);
            SaveResult quick = CampaignAutoSaveService.QuickSave();
            q.Scrap = 60;
            CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete, toast: false);
            bool hasQuick = CampaignAutoSaveService.HasQuickSave();
            RestoreResult rq = CampaignRestoreOrchestrator.Restore(CampaignSaveService.QuickSlot(home));
            int keepsBefore = CampaignSaveService.PreservedFiles(home).Count(f => f.Contains(".keep-superseded-"));
            CampaignSession.Set(CampaignSaveService.QuickSlot(home), rq.State);
            int keepsAfter = CampaignSaveService.PreservedFiles(home).Count(f => f.Contains(".keep-superseded-"));
            CampaignState superseded = LoadState(CampaignSaveService.PreservedFiles(home).LastOrDefault(f => f.Contains(".keep-superseded-")));
            Expect(quick.Success && hasQuick && rq.Success && rq.State.Scrap == 50 && CampaignSession.ActiveSlotIndex == home && keepsAfter == keepsBefore + 1
                   && superseded?.Scrap == 60 && CampaignSlotText.SlotTitle(CampaignSaveService.QuickSlot(home)).Contains("快速存档")
                   && CampaignSlotText.SlotTitle(CampaignSaveService.AutoSlot(home, 1)).Contains("自动存档 2"),
                $"C 快速存档后读快速存档：读回废料 {rq.State?.Scrap}、活动槽位 = 战役槽 {CampaignSession.ActiveSlotIndex}，战役槽里更新的主档（60）先另存为 keep-superseded");

            // 删除 / 清理 / 打开目录：只在玩家点了之后发生。
            string opened = null;
            CampaignSaveService.OpenDirectoryHandler = d => opened = d;
            string dirShown = CampaignSaveService.OpenSaveDirectory();
            int keeps = CampaignSaveService.PreservedFiles(home).Length;
            int deleted = CampaignSaveService.DeleteSlot(home);
            bool familyGone = !File.Exists(CampaignSaveService.SlotPath(home)) && !File.Exists(CampaignSaveService.BakPath(home))
                              && !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.QuickSlot(home)))
                              && Enumerable.Range(0, 3).All(k => !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(home, k))));
            int keepsAfterDelete = CampaignSaveService.PreservedFiles(home).Length;
            bool othersIntact = CampaignSaveService.GetSlotMetadata(2).State == CampaignSlotState.Ready && File.Exists(otherAuto);
            int cleaned = CampaignSaveService.DeletePreservedFiles(home);
            Expect(opened == _dir && dirShown == _dir && deleted >= 3 && familyGone && keepsAfterDelete == keeps && keeps > 0 && othersIntact
                   && cleaned == keeps && CampaignSaveService.PreservedFiles(home).Length == 0 && CampaignSaveService.GetSlotMetadata(home).State == CampaignSlotState.Empty,
                $"C 打开存档目录（{Path.GetFileName(opened ?? "")}）；删除槽 {home}：删掉 {deleted} 个存档文件、{keeps} 个保留文件不动、别的槽不受影响；清理保留文件删掉 {cleaned} 个");
        }

        // ── D. 列表只读头部（DEBT-FG0SAVE01-08）────────────────────────────────

        private static void CheckHeaderOnlyList()
        {
            Line("  · D. 存档列表只读头部（卡片 + 长度校验），正文的完整校验留到读档");
            for (int i = 0; i < 3; i++)
            {
                CampaignSaveService.Save(i, Big("fgrobust-list-" + i, 500 + i, 4000), SaveReason.Manual);
            }
            long size = new FileInfo(CampaignSaveService.SlotPath(0)).Length;
            var sw = Stopwatch.StartNew();
            CampaignSlotMetadata[] all = CampaignSaveService.GetAllSlotMetadata();
            double listMs = sw.Elapsed.TotalMilliseconds;
            ExpectPerf(all.Take(3).All(m => m.State == CampaignSlotState.Ready && m.HeaderOnly),
                $"D 三个 {size / 1024.0 / 1024.0:F1} MB 的存档列表：{listMs:F1} ms（只读头部；FG0-SAVE-01 整份读 3 × 12.8 MB 约 400 ms）",
                PerfGate.Le(listMs, 100, "大存档列表 ms"));

            // 正文里改一个字（长度不变）：列表照常显示可读；读档时校验和不符 → 登记读档失败，列表随即显示损坏、提供读取备份，“继续”不再指向它。
            CampaignSaveService.Save(1, Big("fgrobust-list-1", 601, 4000), SaveReason.Manual);
            string path = CampaignSaveService.SlotPath(1);
            byte[] raw = File.ReadAllBytes(path);
            int at = IndexOf(raw, Encoding.ASCII.GetBytes("\\\"Scrap\\\":601"));
            raw[at + 10] = (byte)'7';
            File.WriteAllBytes(path, raw);
            CampaignSlotMetadata listed = CampaignSaveService.GetSlotMetadata(1);
            LoadResult load = CampaignSaveService.Load(1);
            CampaignSlotMetadata afterLoad = CampaignSaveService.GetSlotMetadata(1);
            Expect(at > 0 && listed.State == CampaignSlotState.Ready && listed.HeaderOnly && !load.Success && load.Reason == SaveFailureReason.Checksum
                   && afterLoad.State == CampaignSlotState.Corrupt && afterLoad.HasBackup && CampaignSlotText.ActionLabel(afterLoad) == GameText.Get("save.action.restore")
                   && CampaignSaveService.ResolveContinueSlot() != 1,
                $"D 正文被改：列表 {listed.State}（只读头部）→ 读档 {load.Reason} → 列表 {afterLoad.State}、按钮“{CampaignSlotText.ActionLabel(afterLoad)}”，“继续”不再指向它");

            // 截断：列表就能看出（不用读正文）；卡片被改：卡片校验和不符。
            File.WriteAllBytes(CampaignSaveService.SlotPath(2), File.ReadAllBytes(CampaignSaveService.SlotPath(2)).Take((int)(size / 2)).ToArray());
            CampaignSlotMetadata torn = CampaignSaveService.GetSlotMetadata(2);
            CampaignSaveService.Save(0, Small("fgrobust-list-0", 9), SaveReason.Manual);
            string p0 = CampaignSaveService.SlotPath(0);
            File.WriteAllText(p0, File.ReadAllText(p0).Replace("\\\"Act\\\":1", "\\\"Act\\\":3"));
            CampaignSlotMetadata cardTampered = CampaignSaveService.GetSlotMetadata(0);
            Expect(torn.State == CampaignSlotState.Corrupt && torn.FailureReason == SaveFailureReason.Truncated && torn.HeaderOnly
                   && cardTampered.State == CampaignSlotState.Corrupt && cardTampered.FailureReason == SaveFailureReason.Checksum,
                $"D 截断的存档在列表里就显示 {torn.FailureReason}；卡片被改显示 {cardTampered.FailureReason}");
        }

        // ── E. 存档卡：缩略图、真实时长、复制种子 / 短码（FGR-SYS-007，DEBT-FG0SAVE01-01，FG-GAP-019，DEBT-FG3GEN01-02）──────────

        private static void CheckCard()
        {
            Line("  · E. 存档卡：缩略图、真实游玩时长（暂停不计、倍速不影响）、复制种子 / 分享短码");
            const int slot = 0;
            CampaignState s = MainMenuUI.BuildNewCampaignState("fgrobust-card", 20261123, Campaign.WorldGen.WorldSettings.Resolve(Campaign.WorldGen.WorldGenVersions.Current,
                Campaign.WorldGen.WorldGenContent.DefaultPresetId), Campaign.Defense.DifficultyService.Preset("Standard"));
            CampaignSaveService.Save(slot, s, SaveReason.NewCampaign);
            CampaignSession.Set(slot, s);
            // 真实时长：世界运行、未暂停才累计；倍速（3x）不改变累计速度；在主菜单 / 暂停时不加。
            GameLogic.Core.StrategyClock.Reset();
            CampaignSaveService.TickRealPlay(0.5f, true, false);
            GameLogic.Core.StrategyClock.SetSpeed(3f);
            CampaignSaveService.TickRealPlay(0.5f, true, false);
            CampaignSaveService.TickRealPlay(0.5f, true, true);
            CampaignSaveService.TickRealPlay(0.5f, false, false);
            GameLogic.Core.StrategyClock.Reset();
            double real = s.RealPlaySeconds;
            s.RealPlaySeconds = 3725; // 1 小时 02 分
            s.PlaySeconds = 3 * 3725f;
            SaveThumbnail.CaptureHandler = () => SaveThumbnail.Placeholder(CampaignSession.Current);
            SaveResult saved = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual, SaveCopyKind.None, false);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            var tex = new Texture2D(2, 2);
            bool decoded = meta.ThumbnailJpg != null && tex.LoadImage(meta.ThumbnailJpg);
            int w = tex.width;
            int h = tex.height;
            UnityEngine.Object.DestroyImmediate(tex);
            string card = CampaignSlotText.CardText(meta);
            Expect(Math.Abs(real - 1.0) < 1e-6 && saved.Success && decoded && w == SaveThumbnail.Width && h == SaveThumbnail.Height
                   && card.Contains("实际游玩 1 小时 02 分") && card.Contains("3 小时 06 分") && !GameText.ContainsMarker(card),
                $"E 真实时长 1x 0.5 秒 + 3x 0.5 秒 = {real:F1} 秒（暂停 / 不在世界里不计）；存档带 {w}×{h} 缩略图；卡片“{card.Replace("\n", " / ")}”");
            var old = new CampaignSlotMetadata { State = CampaignSlotState.Ready, RealPlaySeconds = 0 };
            Expect(CampaignSlotText.RealTime(old) == GameText.Get("save.card.real_time_unknown"), "E 旧档没有真实时长：显示“未记录”，不显示成 0 分");

            string copied = null;
            CampaignSlotText.ClipboardHandler = t => copied = t;
            bool seedOk = CampaignSlotText.Copy(CampaignSlotText.SeedText(meta)) && copied == "20261123";
            string code = CampaignSlotText.ShareCode(meta);
            bool codeOk = CampaignSlotText.Copy(code) && copied == Campaign.WorldGen.WorldGenService.ShareCode(s)
                          && Campaign.WorldGen.WorldSettings.TryDecodeShareCode(copied, out int seed, out _, out _) == Campaign.WorldGen.WorldSettings.ShareError.None && seed == 20261123;
            var legacy = new CampaignSlotMetadata { State = CampaignSlotState.Ready, GeneratorVersion = 0 };
            Expect(seedOk && codeOk && CampaignSlotText.ShareCode(legacy) == null && !CampaignSlotText.Copy(null),
                $"E 存档卡复制种子（{CampaignSlotText.SeedText(meta)}）与分享短码（{code}，与暂停菜单同一编码、能解回同一种子）；旧版本地形没有短码时不复制");
        }

        // ── F. 云存档（FGR-SYS-006）──────────────────────────────────────────

        private static void CheckCloud()
        {
            Line("  · F. 云存档（本机目录模拟云端；Steam Remote Storage 由 FG15-SYS-04 接入）：上传 / 下载 / 冲突（两边时间与进度）/ 落选的一份另存保留 / 离线");
            string cloudDir = Path.Combine(_dir, "_cloud");
            var cloud = new LocalFolderCloudProvider(cloudDir);
            CloudSaveService.Provider = null;
            Expect(!CloudSaveService.Sync().Ran, "F 没有云存档提供者（离线 / 非 Steam）：不对账，游戏照常");
            CloudSaveService.Provider = cloud;

            CampaignSaveService.Save(0, Small("fgrobust-cloud-0", 10), SaveReason.NewCampaign);
            CloudSyncReport first = CloudSaveService.Sync();
            bool uploaded = File.Exists(Path.Combine(cloudDir, "campaign_slot0.json")) && !File.Exists(Path.Combine(cloudDir, "campaign_slot0.json.bak"));

            // 另一台机器（云端）存了槽 1 并改了槽 0（本机没改）→ 下载。
            string other = Path.Combine(_dir, "_other");
            CampaignSaveService.SaveDirectoryOverrideForTests = other;
            CampaignSaveService.Save(0, Small("fgrobust-cloud-0", 11), SaveReason.Manual);
            CampaignSaveService.Save(1, Small("fgrobust-cloud-1", 20), SaveReason.NewCampaign);
            File.Copy(CampaignSaveService.SlotPath(0), Path.Combine(cloudDir, "campaign_slot0.json"), true);
            File.Copy(CampaignSaveService.SlotPath(1), Path.Combine(cloudDir, "campaign_slot1.json"), true);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            CloudSyncReport down = CloudSaveService.Sync();
            bool pulled = CampaignSaveService.Load(0).State?.Scrap == 11 && CampaignSaveService.Load(1).State?.Scrap == 20;
            Expect(first.Ran && first.Uploaded == 1 && uploaded && down.Downloaded == 2 && down.Conflicts == 0 && pulled,
                $"F 第一次对账上传本机新档（{first.Uploaded} 份，备份不上云）；云端改过、本机没改 → 下载 {down.Downloaded} 份");

            // 两边都改了槽 0：冲突，对话框显示两边的存档时间和进度。
            CampaignState local = Small("fgrobust-cloud-0", 12);
            local.Progress.Act = 2;
            local.Clock.Day = 9;
            local.PlaySeconds = 600;
            CampaignSaveService.Save(0, local, SaveReason.Manual);
            CampaignSaveService.SaveDirectoryOverrideForTests = other;
            CampaignState remote = Small("fgrobust-cloud-0", 13);
            remote.Progress.Act = 3;
            remote.Clock.Day = 15;
            CampaignSaveService.Save(0, remote, SaveReason.Manual);
            File.Copy(CampaignSaveService.SlotPath(0), Path.Combine(cloudDir, "campaign_slot0.json"), true);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            CloudSyncReport clash = CloudSaveService.Sync();
            CloudSaveConflict c = CloudSaveService.Conflicts.FirstOrDefault();
            string text = c == null ? string.Empty : CloudConflictText.Body(c);
            Expect(clash.Conflicts == 1 && c != null && c.Slot == 0 && c.Local.Act == 2 && c.Local.Day == 9 && c.Cloud.Act == 3 && c.Cloud.Day == 15
                   && !string.IsNullOrEmpty(c.Local.WrittenAtUtc) && !string.IsNullOrEmpty(c.Cloud.WrittenAtUtc)
                   && text.Contains("本机") && text.Contains("云端") && text.Contains("第 2 幕") && text.Contains("第 15 日") && !GameText.ContainsMarker(text)
                   && CampaignSaveService.Load(0).State?.Scrap == 12,
                $"F 两边都改过：冲突 1 处，本机 / 云端各显示存档时间与进度，未选之前什么都不覆盖：“{text.Replace("\n", " / ")}”");

            // 选“保留云端”：本机那份另存 keep-local，本机换成云端；选“保留本机”的路径同样另存云端那份。
            int keepsBefore = CampaignSaveService.PreservedFiles(0).Length;
            bool resolved = CloudSaveService.Resolve(c, keepLocal: false);
            LoadResult afterPick = CampaignSaveService.Load(0);
            CampaignState keptLocal = LoadState(CampaignSaveService.PreservedFiles(0).LastOrDefault(f => f.Contains(".keep-local-")));
            CloudSyncReport quiet = CloudSaveService.Sync();
            Expect(resolved && afterPick.State?.Scrap == 13 && keptLocal?.Scrap == 12 && CampaignSaveService.PreservedFiles(0).Length == keepsBefore + 1
                   && quiet.Conflicts == 0 && quiet.Uploaded == 0 && quiet.Downloaded == 0,
                "F 选“保留云端”：本机换成云端那份（13），本机那份另存为 keep-local（12）；再对账没有新冲突");

            CampaignSaveService.Save(1, Small("fgrobust-cloud-1", 21), SaveReason.Manual);
            CampaignSaveService.SaveDirectoryOverrideForTests = other;
            CampaignSaveService.Save(1, Small("fgrobust-cloud-1", 22), SaveReason.Manual);
            File.Copy(CampaignSaveService.SlotPath(1), Path.Combine(cloudDir, "campaign_slot1.json"), true);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            CloudSaveService.Sync();
            CloudSaveConflict c1 = CloudSaveService.Conflicts.FirstOrDefault();
            bool keptCloudSide = c1 != null && CloudSaveService.Resolve(c1, keepLocal: true);
            CampaignState cloudNow = LoadState(Path.Combine(cloudDir, "campaign_slot1.json"));
            CampaignState keptCloud = LoadState(CampaignSaveService.PreservedFiles(1).LastOrDefault(f => f.Contains(".keep-cloud-")));
            Expect(keptCloudSide && cloudNow?.Scrap == 21 && keptCloud?.Scrap == 22 && CampaignSaveService.Load(1).State?.Scrap == 21,
                "F 选“保留本机”：云端换成本机那份（21），云端那份另存为本机的 keep-cloud（22）");

            // 云端被别处删掉：不删本机，重新上传；本机玩家亲手删除：同步删掉云端那份。离线：不对账。
            File.Delete(Path.Combine(cloudDir, "campaign_slot1.json"));
            CloudSyncReport reup = CloudSaveService.Sync();
            CampaignSaveService.DeleteSlot(1);
            CloudSyncReport del = CloudSaveService.Sync();
            cloud.Online = false;
            CloudSyncReport offline = CloudSaveService.Sync();
            Expect(reup.Uploaded == 1 && File.Exists(CampaignSaveService.SlotPath(1)) == false && del.Deleted == 1 && !File.Exists(Path.Combine(cloudDir, "campaign_slot1.json"))
                   && !offline.Ran,
                $"F 云端那份被别处删了：本机不删、重新上传（{reup.Uploaded}）；玩家在本机删除后对账删掉云端那份（{del.Deleted}）；离线时不对账");
        }

        // ── G. FG-EMG 存档域（FG19 FGR-EMG-210～212，FGT-EMG-041）───────────────────

        private static void CheckEmgDomains()
        {
            Line("  · G. FG-EMG 新存档域往返（内核快照：在飞弹体 / 斑块 / 残骸 / 累积量；战役：发现记录 / 地面标记 / 长期留存物）+ v2 旧档缺字段取缺省、不升 schema");
            CombatConfig cfg = Campaign.Combat.CombatSite.ConfigFromTuning(); // 正式配置（斑块 / 涌现开着，与地点同一套）
            cfg.NavEnabled = 0;
            using (var k = new CombatKernel(cfg, 16))
            {
                Campaign.Combat.CombatSite.ApplyContent(k); // 物质 / 规则表与地点同一套（斑块要认得物质）
                int w = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 40f, Damage = 5f, Cooldown = 1f, ProjectileSpeed = 4f, ProjectileRadius = 0.1f, ProjectileLife = 20f });
                int me = k.Spawn(new CombatSpawn
                {
                    Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                    Position = new double2(0, 0), Home = new double2(0, 0), Radius = 0.4f, Speed = 4f, Health = 100f, MaxHealth = 100f, Weapon = w, BehaviorProfile = -1,
                });
                int foe = k.Spawn(new CombatSpawn
                {
                    Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire,
                    Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                    Position = new double2(20, 0), Home = new double2(20, 0), Radius = 0.4f, Speed = 0f, Health = 100f, MaxHealth = 100f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
                });
                k.SpawnPatch(EmgMatterConst.Acid, new double2(10, 5), 2f, 1f, 30f);
                k.SpawnWreckAt(new double2(-6, 3), 1.2f, 50f, 3f, 20f);
                k.AddAccum(foe, 0, 0.4f);
                k.FireAt(me, foe, k.Time);
                k.Step(1f / 60f, k.Time + 1f / 60f);
                k.ClearCues();
                k.AddStructHitForTests(new EmgStructHit { Key = 5, Pos = new double2(10, 5), Radius = 2f, Damage = 1.5f, Matter = 1 }); // 待发布的斑块蚀设施条目（内核事件队列，段 43）
                ulong hash = k.StateHash();
                int proj = k.ProjectileCount;
                int patches = k.PatchCount;
                int wrecks = k.WreckCount;
                byte[] snap = k.Serialize();

                CampaignState s = Small("fgrobust-emg", 70);
                CampaignFgStateDomains.EnsureAll(s);
                s.Combat.Sites = new[] { new CombatSiteRecord { SiteId = "home_valley", FormatVersion = CombatConst.FormatVersion, Payload = Convert.ToBase64String(snap), Units = 2 } };
                s.GrammarDiscoveries = new[] { new GrammarDiscoveryRecord { EntryId = "grm.selfcheck", Tick = 42, SiteId = "home_valley" } };
                s.World.GroundMarks = new[] { new GroundMarkRecord { Id = 1, Kind = 2, X = 3f, Y = 4f, Radius = 1.5f } };
                s.World.NextGroundMarkId = 2;
                CampaignSession.Set(0, s);
                CampaignAutoSaveService.ForceBackgroundForTests = true;
                CampaignAutoSaveService.SaveAuto(SaveReason.Interval, toast: false);
                LoadResult back = CampaignSaveService.Load(0);
                ulong hash2 = 0;
                int proj2 = -1, patches2 = -1, wrecks2 = -1, queue2 = -1;
                CombatSiteRecord site = back.State?.Combat?.Sites?.FirstOrDefault();
                if (site != null)
                {
                    using (var k2 = new CombatKernel(cfg, 16))
                    {
                        Campaign.Combat.CombatSite.ApplyContent(k2);
                        k2.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 40f, Damage = 5f, Cooldown = 1f, ProjectileSpeed = 4f, ProjectileRadius = 0.1f, ProjectileLife = 20f });
                        k2.Load(Convert.FromBase64String(site.Payload));
                        hash2 = k2.StateHash();
                        proj2 = k2.ProjectileCount;
                        patches2 = k2.PatchCount;
                        wrecks2 = k2.WreckCount;
                        queue2 = k2.StructHitsPending;
                    }
                }
                Expect(back.Success && proj >= 1 && patches >= 1 && wrecks >= 1 && hash2 == hash && proj2 == proj && patches2 == patches && wrecks2 == wrecks && queue2 == 1
                       && back.State.GrammarDiscoveries.Length == 1 && back.State.GrammarDiscoveries[0].EntryId == "grm.selfcheck"
                       && back.State.World.GroundMarks.Length == 1 && Math.Abs(back.State.World.GroundMarks[0].Radius - 1.5f) < 1e-6f
                       && back.State.SchemaVersion == CampaignSaveService.CurrentSchemaVersion,
                    $"G 后台写盘存读档：内核快照（在飞弹体 {proj2}、斑块 {patches2}、残骸 {wrecks2}、待发布事件 {queue2}、累积量）状态哈希一致；发现记录、地面标记原样；schema 仍为 {CampaignSaveService.CurrentSchemaVersion}");
            }

            // v2 旧档：正文里没有这些新字段（FG15-SYS-01 的真实时长也没有）→ 读入取缺省（空数组 / 0），不报错、不升 schema。
            CampaignState legacy = Small("fgrobust-legacy", 80);
            CampaignFgStateDomains.EnsureAll(legacy);
            legacy.NormalizeForSave();
            string json = JsonUtility.ToJson(legacy);
            foreach (string field in new[] { "GrammarDiscoveries", "GroundMarks", "PersistBlocks", "RealPlaySeconds", "ReactionFirstTriggers" })
            {
                json = StripField(json, field);
            }
            string card = "{\"CampaignId\":\"fgrobust-legacy\",\"DifficultyId\":\"Standard\",\"Act\":1}";
            var env = new LegacyEnvelope
            {
                SchemaVersion = 2, ContentVersion = CampaignSaveService.CurrentContentVersion, Checksum = CampaignSaveService.ComputeChecksum(json, card),
                WrittenAtUtc = DateTime.UtcNow.ToString("o"), ProductVersion = "0.2", CardJson = card, PayloadJson = json,
            };
            File.WriteAllText(CampaignSaveService.SlotPath(1), JsonUtility.ToJson(env));
            CampaignSlotMetadata lm = CampaignSaveService.GetSlotMetadata(1);
            LoadResult old = CampaignSaveService.Load(1);
            Expect(!json.Contains("GrammarDiscoveries") && !json.Contains("RealPlaySeconds") && lm.State == CampaignSlotState.Ready && !lm.HeaderOnly
                   && old.Success && old.State.Scrap == 80 && old.State.GrammarDiscoveries != null && old.State.GrammarDiscoveries.Length == 0
                   && old.State.World.GroundMarks != null && old.State.World.GroundMarks.Length == 0 && old.State.RealPlaySeconds == 0
                   && old.State.ReactionFirstTriggers != null && old.State.SchemaVersion == 2 && CampaignSlotText.RealTime(lm) == GameText.Get("save.card.real_time_unknown"),
                "G v2 旧档（旧信封、正文缺发现记录 / 地面标记 / 留存物 / 真实时长）：列表照旧整份校验、可读；读入后新字段取缺省（空 / 0）、schema 仍为 2");
        }

        // ── H. 自动存档时机（FGR-SYS-002）────────────────────────────────────

        private static void CheckTimers()
        {
            Line("  · H. 定时自动存档（5 / 10 / 20 分钟或关闭；真实时间、暂停不计、倍速无关）与静默夜开始时的帧末自动存档");
            Expect(GameSettings.AutoSaveChoices.SequenceEqual(new[] { 0, 5, 10, 20 }), $"H 定时选项 = 关 / 5 / 10 / 20 分钟（FGR-SYS-002）：[{string.Join(",", GameSettings.AutoSaveChoices)}]");
            CampaignSession.Set(0, Small("fgrobust-timer", 1));
            IntervalAutoSave.ResetForTests();
            int calls = 0;
            IntervalAutoSave.SaveHandler = () =>
            {
                calls++;
                return SaveResult.Ok;
            };
            GameSettings.SetAutoSaveMinutes(20);
            for (int i = 0; i < 1199; i++)
            {
                IntervalAutoSave.Tick(1f, true, false);
            }
            IntervalAutoSave.Tick(600f, true, true); // 暂停 10 分钟不计
            int beforeLast = calls;
            GameLogic.Core.StrategyClock.SetSpeed(3f);
            IntervalAutoSave.Tick(1f, true, false);
            GameLogic.Core.StrategyClock.Reset();
            Expect(beforeLast == 0 && calls == 1, $"H 20 分钟档：真实 19 分 59 秒 + 暂停 10 分钟不存，第 20 分钟（3x 下照样按真实时间）存 1 次（{calls}）");

            // 静默夜开始：模拟步里只记一笔，帧末写；攻城进行中推迟、结束后的第一个帧末写。
            CampaignState s = Small("fgrobust-silent", 5);
            CampaignFgStateDomains.EnsureAll(s);
            CampaignSaveService.Save(0, s, SaveReason.NewCampaign);
            CampaignSession.Set(0, s);
            SetSilentPending(s);
            var siege = Campaign.Defense.SiegeService.StateOf(s);
            bool siegeWas = siege != null && siege.TheaterActive;
            if (siege != null)
            {
                siege.TheaterActive = true;
            }
            int saves = Campaign.Env.SilentNightService.StartSaves;
            bool deferred = !Campaign.Env.SilentNightService.FrameUpdate() && Campaign.Env.SilentNightService.PendingStartSave;
            if (siege != null)
            {
                siege.TheaterActive = siegeWas;
            }
            s.Scrap = 6;
            bool wrote = Campaign.Env.SilentNightService.FrameUpdate();
            Expect(siege != null && deferred && wrote && Campaign.Env.SilentNightService.StartSaves == saves + 1 && !Campaign.Env.SilentNightService.PendingStartSave
                   && CampaignSaveService.Load(0).State?.LastSaveReason == SaveReason.SilentNightStart && CampaignSaveService.Load(0).State?.Scrap == 6
                   && File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(0, 0))),
                "H 静默夜开始：攻城进行中推迟（请求保留），之后第一个帧末自动存档一次（原因 = 静默夜开始，写进轮换自动存档）");

            // 推迟期间玩家“保存并返回”、读了另一个战役：这次请求属于原来的战役，新战役的帧末不写、不占轮换。
            SetSilentPending(s);
            CampaignState other = Small("fgrobust-silent-other", 7);
            CampaignSaveService.Save(1, other, SaveReason.NewCampaign);
            CampaignSession.Set(1, other);
            int saves2 = Campaign.Env.SilentNightService.StartSaves;
            bool wroteOther = Campaign.Env.SilentNightService.FrameUpdate();
            Expect(!wroteOther && !Campaign.Env.SilentNightService.PendingStartSave && Campaign.Env.SilentNightService.StartSaves == saves2
                   && CampaignSaveService.Load(1).State?.LastSaveReason == SaveReason.NewCampaign && !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(1, 0))),
                "H 静默夜存档推迟期间换了战役：请求作废，新战役第一个帧末不写“静默夜开始”自动存档、不占轮换");
        }

        private static void SetSilentPending(CampaignState s)
        {
            typeof(Campaign.Env.SilentNightService).GetProperty("PendingStartSave").SetValue(null, true);
            typeof(Campaign.Env.SilentNightService).GetField("_pendingStartSaveFor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, s);
        }

        // ── I. Demo 自动存档时机“蓝图保存”“启动信标”（FGR-SYS-002）─────────────────

        private static CampaignState NewCard(string id) => MainMenuUI.BuildNewCampaignState(id, 20261123,
            WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId), Campaign.Defense.DifficultyService.Preset("Standard"));

        private static void CheckDemoMoments()
        {
            Line("  · I. Demo 6 个自动存档时机之“蓝图保存”“启动信标”：保存当前战役的蓝图、确认启动信标之后各自动存档一次（游戏中后台写盘）");
            const int slot = 0;
            CampaignState s = NewCard("fgrobust-moments");
            CampaignSaveService.Save(slot, s, SaveReason.NewCampaign);
            CampaignSession.Set(slot, s);
            string auto0 = CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(slot, 0));

            // 编辑模式直调服务（别的自检这样用）：不写盘、不导出机器记录。
            var board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(Campaign.Regions.HomeValleyLayout.Erc003ChassisId, "comp_gun", null, null, Array.Empty<string>());
            Campaign.Blueprint.BlueprintSaveResult quiet = Campaign.Blueprint.BlueprintEditorService.TrySave(s, board, null, "fgrobust-bp0", true);
            bool noSave = quiet.Success && !File.Exists(auto0) && CampaignSaveService.Load(slot).State?.LastSaveReason == SaveReason.NewCampaign;

            CampaignAutoSaveService.ForceBackgroundForTests = true; // = 游戏中
            CampaignSaveService.BackgroundDelayMsForTests = 200; // 让“还在后台写”这一刻可观察
            Campaign.Blueprint.BlueprintSaveResult sv = Campaign.Blueprint.BlueprintEditorService.TrySave(s, board, null, "fgrobust-bp1", true);
            bool queued = CampaignSaveService.PendingWrites > 0;
            CampaignSaveService.WaitForPendingWrites();
            CampaignSaveService.PumpBackgroundResults();
            CampaignSaveService.BackgroundDelayMsForTests = 0;
            CampaignState afterBp = CampaignSaveService.Load(slot).State;
            Expect(noSave && sv.Success && queued && afterBp?.LastSaveReason == SaveReason.BlueprintSaved && afterBp.BlueprintRecords.Any(r => r.BlueprintId == sv.BlueprintId)
                   && LoadState(auto0)?.LastSaveReason == SaveReason.BlueprintSaved,
                $"I 蓝图编辑器保存成功（{sv.FailureReason ?? "成功"}）→ 自动存档一次（后台写盘：{queued}；原因 = 蓝图保存，读回含新蓝图，写进第 1 份轮换）；编辑模式直调服务不写盘（{noSave}）");

            // 存的不是当前战役（另一份状态）：不写。
            CampaignState stranger = NewCard("fgrobust-stranger");
            Campaign.Blueprint.BlueprintSaveResult sv2 = Campaign.Blueprint.BlueprintEditorService.TrySave(stranger, board, null, "fgrobust-bp2", true);
            CampaignSaveService.WaitForPendingWrites();
            bool untouched = sv2.Success && !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(slot, 1)))
                             && CampaignSaveService.Load(slot).State?.BlueprintRecords.All(r => r.BlueprintId != sv2.BlueprintId) == true;
            Expect(untouched, "I 保存的不是当前战役的蓝图：不触发自动存档");

            // 启动信标：未通电被拒不存；通电后确认启动 → 自动存档一次，读回时演出接着播。
            s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>())
                .Where(o => o.ObjectiveId != CampaignObjectiveTracker.Obj09)
                .Append(new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj09, State = ObjectiveState.Completed }).ToArray();
            var beacon = new BuildingRecord
            {
                BuildingId = Campaign.Regions.HomeValleyLayout.RegionId + ":fgrobust-beacon",
                BuildingTypeId = Campaign.Regions.HomeValleyLayout.BuildingTypeBeacon,
                RegionId = Campaign.Regions.HomeValleyLayout.RegionId,
                ConstructionState = BuildingConstructionState.Operational,
                PowerState = BuildingPowerState.Unpowered,
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(beacon).ToArray();
            var refused = Campaign.Regions.HomeValleyBeacon.TryStartLaunch(s);
            CampaignSaveService.WaitForPendingWrites();
            bool refusedQuiet = !refused.Success && !File.Exists(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(slot, 1)));
            beacon.PowerState = BuildingPowerState.Powered;
            var start = Campaign.Regions.HomeValleyBeacon.TryStartLaunch(s);
            CampaignSaveService.WaitForPendingWrites();
            CampaignSaveService.PumpBackgroundResults();
            CampaignState afterBeacon = CampaignSaveService.Load(slot).State;
            Expect(refusedQuiet && start.Success && afterBeacon?.LastSaveReason == SaveReason.BeaconLaunchEnter && afterBeacon != null && Campaign.Regions.HomeValleyBeacon.IsLaunching(afterBeacon)
                   && LoadState(CampaignSaveService.SlotPath(CampaignSaveService.AutoSlot(slot, 1)))?.LastSaveReason == SaveReason.BeaconLaunchEnter,
                $"I 启动信标：没通电被拒（{refused.FailureReason}）不存；确认启动后自动存档一次（原因 = 启动信标，写进第 2 份轮换），读回时信标仍在演出中（{(afterBeacon != null && Campaign.Regions.HomeValleyBeacon.IsLaunching(afterBeacon))}）");
        }

        // ── J. 界面入口：存档管理窗口与云冲突窗口（真 UXML）────────────────────────

        private const string SavePanelUxml = "Assets/GameRes/Raw/UI/UiKit/SavePanel.uxml";

        private static bool Click(UnityEngine.UIElements.Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(UnityEngine.UIElements.Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(UnityEngine.UIElements.EventBase) }, null);
            using (UnityEngine.UIElements.ClickEvent evt = UnityEngine.UIElements.ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private static bool Hidden(UnityEngine.UIElements.VisualElement root, string name) => root.Q<UnityEngine.UIElements.VisualElement>(name).ClassListContains("uk-hidden");

        private static void CheckPanels()
        {
            Line("  · J. 界面入口（真 UXML）：存档管理窗口（缩略图、复制、打开目录、读副本、删除 / 清理二次确认）与云冲突窗口（两边时间与进度、保留本机 / 云端、不选就关）");
            const int home = 0;
            CampaignState s = NewCard("fgrobust-ui");
            CampaignSaveService.Save(home, s, SaveReason.NewCampaign);
            CampaignSession.Set(home, s);
            SaveThumbnail.CaptureHandler = () => SaveThumbnail.Placeholder(CampaignSession.Current);
            CampaignAutoSaveService.SaveWithExport(home, SaveReason.Manual, SaveCopyKind.None, false);
            CampaignAutoSaveService.SaveAuto(SaveReason.HomeEntryComplete, toast: false);
            CampaignAutoSaveService.QuickSave();
            File.WriteAllText(CampaignSaveService.KeepPath(home, "manual"), "kept");
            string copied = null;
            string opened = null;
            CampaignSlotText.ClipboardHandler = t => copied = t;
            CampaignSaveService.OpenDirectoryHandler = d => opened = d;

            UnityEngine.UIElements.VisualElement root = FgProductionSelfCheck.MountUxml(SavePanelUxml, out GameObject go);
            try
            {
                var panel = go.AddComponent<GameLogic.UI.Kit.SavePanelUIToolkit>();
                panel.BindView(root);
                int loaded = -1;
                int changed = 0;
                GameLogic.UI.Kit.SavePanelUIToolkit.OpenManage(home, x => loaded = x, () => changed++);
                CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(home);
                var info = root.Q<UnityEngine.UIElements.Label>("SaveInfo");
                var load0 = root.Q<UnityEngine.UIElements.Button>("SaveCopyLoad0");
                var load1 = root.Q<UnityEngine.UIElements.Button>("SaveCopyLoad1");
                var load3 = root.Q<UnityEngine.UIElements.Button>("SaveCopyLoad3");
                string row0 = root.Q<UnityEngine.UIElements.Label>("SaveCopyText0").text;
                string row3 = root.Q<UnityEngine.UIElements.Label>("SaveCopyText3").text;
                Expect(GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen && !GameLogic.UI.Kit.SavePanelUIToolkit.InConflictMode && !Hidden(root, "SaveRoot") && !Hidden(root, "SaveManage") && Hidden(root, "SaveConflict")
                       && panel.ThumbnailTexture != null && panel.ThumbnailTexture.width == SaveThumbnail.Width && info.text == CampaignSlotText.CardText(meta)
                       && load0.enabledSelf && !load1.enabledSelf && load3.enabledSelf && row0.Contains("自动存档 1") && row3.Contains("快速存档")
                       && root.Q<UnityEngine.UIElements.Button>("SaveCleanKeep").text.Contains("1") && !GameText.ContainsMarker(row0 + row3 + info.text),
                    $"J 存档卡“管理”打开存档管理：{panel.ThumbnailTexture?.width}×{panel.ThumbnailTexture?.height} 缩略图 + 卡片文字；副本行“{row0}”“{row3}”（有档的能读、空的灰）；清理保留文件按钮“{root.Q<UnityEngine.UIElements.Button>("SaveCleanKeep").text}”");

                bool seed = Click(root.Q<UnityEngine.UIElements.Button>("SaveCopySeed")) && copied == "20261123";
                bool code = Click(root.Q<UnityEngine.UIElements.Button>("SaveCopyCode")) && copied == CampaignSlotText.ShareCode(meta) && copied != null;
                bool dir = Click(root.Q<UnityEngine.UIElements.Button>("SaveOpenDir")) && opened == _dir;
                string feedback = root.Q<UnityEngine.UIElements.Label>("SaveFeedback").text;
                Expect(seed && code && dir && feedback.Contains(copied ?? "?"), $"J 管理窗口按钮：复制种子、复制分享短码（“{copied}”，提示“{feedback}”）、打开存档目录（{Path.GetFileName(opened ?? "")}）");

                bool clickedLoad = Click(load3);
                Expect(clickedLoad && loaded == CampaignSaveService.QuickSlot(home) && !GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen,
                    $"J 读取快速存档那一行：交给主菜单读槽 {loaded}（= 快速存档槽），窗口关上");

                GameLogic.UI.Kit.SavePanelUIToolkit.OpenManage(home, x => loaded = x, () => changed++);
                int keepsBeforeDelete = CampaignSaveService.PreservedFiles(home).Length;
                bool asked = Click(root.Q<UnityEngine.UIElements.Button>("SaveDelete")) && panel.ConfirmPending && !Hidden(root, "SaveConfirm") && File.Exists(CampaignSaveService.SlotPath(home));
                bool backCancels = Click(root.Q<UnityEngine.UIElements.Button>("SaveClose")) && !panel.ConfirmPending && GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen && File.Exists(CampaignSaveService.SlotPath(home));
                Click(root.Q<UnityEngine.UIElements.Button>("SaveDelete"));
                bool deleted = Click(root.Q<UnityEngine.UIElements.Button>("SaveConfirmYes")) && !File.Exists(CampaignSaveService.SlotPath(home))
                               && CampaignSaveService.GetSlotMetadata(home).State == CampaignSlotState.Empty && keepsBeforeDelete > 0 && CampaignSaveService.PreservedFiles(home).Length == keepsBeforeDelete && changed == 1
                               && panel.ThumbnailTexture == null;
                Expect(asked && backCancels && deleted,
                    $"J 删除存档（{asked}/{backCancels}/{deleted}：主档 {File.Exists(CampaignSaveService.SlotPath(home))}、状态 {CampaignSaveService.GetSlotMetadata(home).State}、保留 {keepsBeforeDelete}→{CampaignSaveService.PreservedFiles(home).Length}、刷新 {changed}、缩略图 {panel.ThumbnailTexture != null}）：先弹确认（文件还在），“关闭”先取消确认、窗口不关；再点删除并确认 → 删掉、保留文件不动、主菜单刷新 {changed} 次、缩略图释放");

                bool askClean = Click(root.Q<UnityEngine.UIElements.Button>("SaveCleanKeep")) && panel.ConfirmPending;
                bool cleaned = Click(root.Q<UnityEngine.UIElements.Button>("SaveConfirmYes")) && CampaignSaveService.PreservedFiles(home).Length == 0 && changed == 2;
                bool closed = Click(root.Q<UnityEngine.UIElements.Button>("SaveClose")) && !GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen;
                Expect(askClean && cleaned && closed, "J 清理保留文件：确认后才删、主菜单刷新；“关闭”关上窗口");

                // 云冲突窗口：两边时间与进度；不选就关什么都不覆盖、下次再问；选“保留本机”云端那份另存为保留文件。
                string cloudDir = Path.Combine(_dir, "_cloudJ");
                string other = Path.Combine(_dir, "_otherJ");
                CloudSaveService.Provider = new LocalFolderCloudProvider(cloudDir);
                CampaignSaveService.Save(1, Small("fgrobust-ui-cloud", 10), SaveReason.NewCampaign);
                CloudSaveService.Sync();
                CampaignState local = Small("fgrobust-ui-cloud", 12);
                local.Progress.Act = 2;
                local.Clock.Day = 9;
                CampaignSaveService.Save(1, local, SaveReason.Manual);
                CampaignSaveService.SaveDirectoryOverrideForTests = other;
                CampaignState remote = Small("fgrobust-ui-cloud", 13);
                remote.Progress.Act = 3;
                remote.Clock.Day = 15;
                CampaignSaveService.Save(1, remote, SaveReason.Manual);
                File.Copy(CampaignSaveService.SlotPath(1), Path.Combine(cloudDir, "campaign_slot1.json"), true);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                CloudSyncReport clash = CloudSaveService.Sync();
                int done = 0;
                GameLogic.UI.Kit.SavePanelUIToolkit.OpenConflicts(() => done++);
                string body = root.Q<UnityEngine.UIElements.Label>("SaveConflictText").text;
                bool shown = clash.Conflicts == 1 && GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen && GameLogic.UI.Kit.SavePanelUIToolkit.InConflictMode && Hidden(root, "SaveManage") && !Hidden(root, "SaveConflict")
                             && body == CloudConflictText.Body(CloudSaveService.Conflicts[0]) && body.Contains("本机") && body.Contains("云端") && body.Contains("第 2 幕") && body.Contains("第 15 日")
                             && root.Q<UnityEngine.UIElements.Button>("SaveKeepLocal").text.Length > 0;
                bool skipped = Click(root.Q<UnityEngine.UIElements.Button>("SaveClose")) && done == 1 && !GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen && CloudSaveService.Conflicts.Count == 1
                               && CampaignSaveService.Load(1).State?.Scrap == 12;
                GameLogic.UI.Kit.SavePanelUIToolkit.OpenConflicts(() => done++);
                bool picked = Click(root.Q<UnityEngine.UIElements.Button>("SaveKeepLocal")) && done == 2 && CloudSaveService.Conflicts.Count == 0 && !GameLogic.UI.Kit.SavePanelUIToolkit.IsOpen
                              && CampaignSaveService.Load(1).State?.Scrap == 12 && LoadState(Path.Combine(cloudDir, "campaign_slot1.json"))?.Scrap == 12
                              && LoadState(CampaignSaveService.PreservedFiles(1).LastOrDefault(f => f.Contains(".keep-cloud-")))?.Scrap == 13;
                Expect(shown && skipped && picked,
                    $"J 云冲突窗口：“{body.Replace("\n", " / ")}”；不选就关 → 什么都不覆盖、冲突留到下次（{skipped}）；点“保留本机” → 云端换成本机那份、云端那份另存为 keep-cloud（{picked}）");
            }
            finally
            {
                GameLogic.UI.Kit.SavePanelUIToolkit.Close();
                UnityEngine.Object.DestroyImmediate(go);
                CloudSaveService.Provider = null;
                CloudSaveService.Conflicts.Clear();
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class LegacyEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        /// <summary>从 JsonUtility 输出里删掉一个字段（值是数组 / 对象 / 标量都行），模拟没有这个字段的旧档。</summary>
        private static string StripField(string json, string field)
        {
            string key = "\"" + field + "\":";
            int at;
            while ((at = json.IndexOf(key, StringComparison.Ordinal)) >= 0)
            {
                int i = at + key.Length;
                int depth = 0;
                bool str = false;
                for (; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (str)
                    {
                        if (ch == '\\')
                        {
                            i++;
                        }
                        else if (ch == '"')
                        {
                            str = false;
                        }
                        continue;
                    }
                    if (ch == '"')
                    {
                        str = true;
                    }
                    else if (ch == '[' || ch == '{')
                    {
                        depth++;
                    }
                    else if (ch == ']' || ch == '}')
                    {
                        if (depth == 0)
                        {
                            break;
                        }
                        depth--;
                    }
                    else if (ch == ',' && depth == 0)
                    {
                        break;
                    }
                }
                int start = at;
                int end = i;
                if (end < json.Length && json[end] == ',')
                {
                    end++;
                }
                else if (start > 0 && json[start - 1] == ',')
                {
                    start--;
                }
                json = json.Remove(start, end - start);
            }
            return json;
        }

        private static CampaignState Small(string id, int scrap)
        {
            CampaignState s = CampaignState.CreateNew(id, "Standard", 7);
            s.Scrap = scrap;
            return s;
        }

        /// <summary>大存档（区块差异 × <paramref name="chunks"/>，每份 1 KB；4000 ≈ 4 MB 正文，加上资源事务 / 事件约 13 MB 与 FG0-SAVE-01 压力档同量级）。</summary>
        private static CampaignState Big(string id, int scrap, int chunks)
        {
            CampaignState s = Small(id, scrap);
            s.ResourceTransactions = Enumerable.Range(0, 40000).Select(i => new ResourceTransactionRecord
            {
                TransactionId = $"tx_{i:D6}", OwnerId = "perf", ResourceType = "Scrap", Requested = 5, Consumed = 5, State = ResourceTransactionState.Committed,
            }).ToArray();
            s.GroundItems = Enumerable.Range(0, 10000).Select(i => new GroundItemRecord
            {
                GroundItemId = $"g_{i:D6}", RegionId = "home_valley", Position = new Vector2(i % 100, i / 100), ResourceType = "Scrap", Amount = 3,
            }).ToArray();
            s.EventLedger = Enumerable.Range(0, 20000).Select(i => new EventLedgerEntry { EventId = $"e_{i:D6}", Category = "perf", Payload = "p" }).ToArray();
            string diff = new string('d', 1024);
            s.World.ChunkDiffs = Enumerable.Range(0, chunks).Select(i => new ChunkDiffRecord { SurfaceId = "surface_main", ChunkX = i % 64, ChunkY = i / 64, DiffPayload = diff }).ToArray();
            return s;
        }

        /// <summary>按真实读档路径读任意一个存档文件（拷到临时槽 9 再 Load）。文件不在 = null。</summary>
        private static CampaignState LoadState(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            ClearSlot(9);
            File.Copy(path, CampaignSaveService.SlotPath(9), true);
            LoadResult r = CampaignSaveService.Load(9);
            ClearSlot(9);
            return r.Success ? r.State : null;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j])
                {
                    j++;
                }
                if (j == pattern.Length)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void ClearSlot(int slot)
        {
            CampaignSaveService.WaitForPendingWrites();
            foreach (string p in new[] { CampaignSaveService.SlotPath(slot), CampaignSaveService.BakPath(slot), CampaignSaveService.TempPath(slot) })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }

        private static void ClearAll()
        {
            CampaignSaveService.WaitForPendingWrites();
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            if (Directory.Exists(_dir))
            {
                foreach (string f in Directory.GetFiles(_dir))
                {
                    File.Delete(f);
                }
            }
        }

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

        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

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
