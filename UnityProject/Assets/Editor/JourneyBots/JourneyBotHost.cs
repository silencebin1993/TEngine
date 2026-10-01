using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG0-QA-01：旅程的登记表。旅程按 ID 取（跨域重载后按存下的 ID 重建同一份定义）。
    /// 新里程碑的出口旅程（FGJ-M1……）在这里登记一行。
    /// </summary>
    public static class JourneyCatalog
    {
        private static readonly Dictionary<string, Func<JourneyDef>> Builders = new Dictionary<string, Func<JourneyDef>>(StringComparer.Ordinal)
        {
            { FgjM0Journey.Id, FgjM0Journey.Build },
            { FgjM1Journey.Id, FgjM1Journey.Build },
            { FgjM1ReverseJourney.Id, FgjM1ReverseJourney.Build },
            { FgjM2Journey.Id, FgjM2Journey.Build },
            { FgjM2ReverseJourney.Id, FgjM2ReverseJourney.Build },
            { FgjGenExtremeJourney.Id, FgjGenExtremeJourney.Build }, // FG3-GEN-01：FGT-GEN-009 极端世界设置下的第一幕旅程
            { FgjM3Journey.Id, FgjM3Journey.Build },
            { FgjM3ReverseJourney.Id, FgjM3ReverseJourney.Build },
        };

        public static IEnumerable<string> Ids => Builders.Keys;

        public static JourneyDef Create(string id) => id != null && Builders.TryGetValue(id, out Func<JourneyDef> b) ? b() : null;
    }

    /// <summary>
    /// FG0-QA-01：在编辑器里驱动一条旅程（真进 Play、走主菜单与正式输入）。
    /// 用法：<c>bash tools/unity-journey.sh FGJ-M0</c>（影子工程 batchmode，编辑器开着也行）；或菜单“BinGames/旅程机器人/FGJ-M0”。
    /// 进 Play 会重载域：旅程 ID 与进度存在 SessionState，<c>[InitializeOnLoad]</c> 重载后接着跑。
    /// 结果：文本报告（结论行“结论：PASS / FAIL”）+ 同名 JSON 摘要；batchmode 下按结果退出 0 / 1。
    /// </summary>
    [InitializeOnLoad]
    public static class JourneyBotHost
    {
        private const string Prefix = "BinGames.Journey.";
        private static JourneyRunner _runner;

        static JourneyBotHost()
        {
            string id = SessionState.GetString(Prefix + "Id", string.Empty);
            if (id.Length > 0 && SessionState.GetString(Prefix + "active", "0") == "1")
            {
                // 进 Play 重载域后按存下的 ID（与续跑的断点）重建同一份定义。
                JourneyDef def = BuildDef(id, SessionState.GetString(Prefix + "From", string.Empty), SessionState.GetString(Prefix + "FromDir", string.Empty),
                    SessionState.GetString(Prefix + "CodeWarning", string.Empty));
                if (def != null)
                {
                    _runner = new JourneyRunner(def, new SessionJourneyStore(Prefix), new EditorJourneyClock());
                    AttachCheckpointWriter(_runner);
                    Hook();
                }
            }
        }

        /// <summary>按 ID 建旅程；<paramref name="from"/> 非空 = 从该步断点续跑（断点已在开跑前校验过，这里只读元数据重组步骤）。</summary>
        private static JourneyDef BuildDef(string id, string from, string dir, string codeWarning)
        {
            JourneyDef def = JourneyCatalog.Create(id);
            if (def == null || string.IsNullOrEmpty(from))
            {
                return def;
            }
            JourneyCheckpoints.Meta meta = JourneyCheckpoints.ReadMeta(dir);
            return meta == null ? null : JourneyCheckpoints.BuildResumeDef(def, from, dir, meta, codeWarning);
        }

        /// <summary>完整跑（不是续跑）且没被关掉（BINGAMES_JOURNEY_CHECKPOINTS=0）时，在登记的断点步骤写断点。</summary>
        private static void AttachCheckpointWriter(JourneyRunner runner)
        {
            if (string.IsNullOrEmpty(runner.Journey.ResumedFrom) && SessionState.GetString(Prefix + "Checkpoints", "1") == "1")
            {
                runner.CheckpointWriter = (r, step, index) => JourneyCheckpoints.Write(r, step, index);
            }
        }

        public static JourneyRunner Current => _runner;

        /// <summary>
        /// 命令行入口：环境变量 BINGAMES_JOURNEY = 旅程 ID，BINGAMES_JOURNEY_OUT = 报告路径（缺省写临时目录）。
        /// FG-TOOL-01：BINGAMES_JOURNEY_FROM = 从该步断点续跑（BINGAMES_JOURNEY_ALLOW_CODE_CHANGE=1 允许代码指纹不同）；BINGAMES_JOURNEY_CHECKPOINTS=0 完整跑时不写断点。
        /// 断点不可用：报告写明原因与“请重新完整跑”，退出码 3。
        /// </summary>
        public static void RunFromCommandLine()
        {
            string id = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY");
            if (string.IsNullOrEmpty(id))
            {
                id = FgjM0Journey.Id;
            }
            string from = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_FROM");
            bool allowCode = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_ALLOW_CODE_CHANGE") == "1";
            bool checkpoints = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_CHECKPOINTS") != "0";
            int code = Run(id, Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_OUT"), from, allowCode, checkpoints);
            if (code != 0 && Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M0（M0 出口旅程）")]
        public static void RunFgjM0FromMenu() => Run(FgjM0Journey.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M1（M1 出口旅程）")]
        public static void RunFgjM1FromMenu() => Run(FgjM1Journey.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M1R（M1 反向旅程）")]
        public static void RunFgjM1ReverseFromMenu() => Run(FgjM1ReverseJourney.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M2（M2 出口旅程）")]
        public static void RunFgjM2FromMenu() => Run(FgjM2Journey.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M2R（M2 反向旅程）")]
        public static void RunFgjM2ReverseFromMenu() => Run(FgjM2ReverseJourney.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M3（M3 出口旅程）")]
        public static void RunFgjM3FromMenu() => Run(FgjM3Journey.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-M3R（M3 反向旅程）")]
        public static void RunFgjM3ReverseFromMenu() => Run(FgjM3ReverseJourney.Id, null);

        [MenuItem("BinGames/QA/旅程机器人/FGJ-GEN9（极端世界设置下的第一幕旅程）")]
        public static void RunFgjGenExtremeFromMenu() => Run(FgjGenExtremeJourney.Id, null);

        public static bool Run(string id, string reportPath) => Run(id, reportPath, null, false, true) == 0;

        /// <summary>开一趟旅程。返回 0 = 已开始；2 = 没有这条旅程；3 = 断点不可用（报告里写明原因与“请重新完整跑”）。</summary>
        public static int Run(string id, string reportPath, string from, bool allowCodeChange, bool checkpoints)
        {
            JourneyDef def = JourneyCatalog.Create(id);
            if (def == null)
            {
                Debug.LogError($"[JourneyBot] 没有旅程 {id}；可选：{string.Join(", ", JourneyCatalog.Ids)}");
                return 2;
            }
            if (string.IsNullOrEmpty(reportPath))
            {
                reportPath = Path.Combine(Path.GetTempPath(), $"bingames-journey-{id}{(string.IsNullOrEmpty(from) ? string.Empty : "-from-" + from)}.txt");
            }
            string dir = string.Empty;
            string codeWarning = string.Empty;
            if (!string.IsNullOrEmpty(from))
            {
                dir = JourneyCheckpoints.Dir(JourneyCheckpoints.RootDir, id, from);
                JourneyCheckpoints.Meta meta = JourneyCheckpoints.ReadMeta(dir);
                if (!JourneyCheckpoints.Validate(def, from, meta, JourneyCheckpoints.EnvInfo.Current(), allowCodeChange, out string why, out string warn))
                {
                    string text = RefusalReport(id, from, why);
                    Debug.LogError("[JourneyBot] " + text);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
                        File.WriteAllText(reportPath, text + Environment.NewLine);
                    }
                    catch (Exception)
                    {
                        // 报告写不了：命令行日志里已有原因。
                    }
                    return 3;
                }
                codeWarning = warn ?? string.Empty;
                def = JourneyCheckpoints.BuildResumeDef(def, from, dir, meta, codeWarning);
            }
            SessionState.SetString(Prefix + "Id", id);
            SessionState.SetString(Prefix + "From", from ?? string.Empty);
            SessionState.SetString(Prefix + "FromDir", dir);
            SessionState.SetString(Prefix + "CodeWarning", codeWarning);
            SessionState.SetString(Prefix + "Checkpoints", checkpoints ? "1" : "0");
            _runner = new JourneyRunner(def, new SessionJourneyStore(Prefix), new EditorJourneyClock()) { ReportPath = reportPath };
            AttachCheckpointWriter(_runner);
            _runner.Start();
            Hook();
            return 0;
        }

        /// <summary>断点不可用时的报告：原因 + 怎么办 + 结论行（与旅程报告同一口径，脚本照常按“结论：”取结果）。</summary>
        public static string RefusalReport(string id, string from, string why) =>
            $"旅程 {id} 不能从断点“{from}”续跑：{why}。" + Environment.NewLine +
            $"请重新完整跑：bash tools/unity-journey.sh {id}（完整跑会在登记的断点步骤重写断点）" + Environment.NewLine +
            "结论：FAIL（断点不可用，没有开跑）";

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceivedThreaded -= OnLogThreaded;
            Application.logMessageReceivedThreaded += OnLogThreaded;
        }

        private static void Unhook()
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceivedThreaded -= OnLogThreaded;
        }

        /// <summary>
        /// 任何线程的报错都算（作业、线程池里的异常只走 Threaded 回调）。回调可能在工作线程上：先进线程安全的队列，
        /// 主线程每帧取出再交给运行器（运行器读写 SessionState / 报告文件，只能在主线程）。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentQueue<(string condition, string stack, LogType type)> PendingLogs =
            new System.Collections.Concurrent.ConcurrentQueue<(string, string, LogType)>();

        private static void OnLogThreaded(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                PendingLogs.Enqueue((condition, stackTrace, type));
            }
        }

        private static void DrainLogs()
        {
            while (PendingLogs.TryDequeue(out (string condition, string stack, LogType type) e))
            {
                _runner?.OnLog(e.condition, e.stack, e.type);
            }
        }

        private static void Tick()
        {
            if (_runner == null)
            {
                Unhook();
                return;
            }
            DrainLogs();
            if (EditorApplication.isPlaying)
            {
                JourneyInput.KeepScripted();
            }
            if (_runner.Tick())
            {
                return;
            }
            bool pass = _runner.Result == JourneyRunner.ResultPass;
            Unhook();
            SessionState.SetString(Prefix + "Id", string.Empty);
            SessionState.SetString(Prefix + "From", string.Empty);
            Debug.Log($"[JourneyBot] {_runner.Journey.Id} {_runner.Result}：{(pass ? "完成" : _runner.FailStepId + " " + _runner.FailReason)}。报告 {_runner.ReportPath}");
            _runner = null;
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(pass ? 0 : 1);
            }
            else if (EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
            }
        }
    }
}
