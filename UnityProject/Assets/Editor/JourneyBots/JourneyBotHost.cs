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
                JourneyDef def = JourneyCatalog.Create(id);
                if (def != null)
                {
                    _runner = new JourneyRunner(def, new SessionJourneyStore(Prefix), new EditorJourneyClock());
                    Hook();
                }
            }
        }

        public static JourneyRunner Current => _runner;

        /// <summary>命令行入口：环境变量 BINGAMES_JOURNEY = 旅程 ID，BINGAMES_JOURNEY_OUT = 报告路径（缺省写临时目录）。</summary>
        public static void RunFromCommandLine()
        {
            string id = Environment.GetEnvironmentVariable("BINGAMES_JOURNEY");
            if (string.IsNullOrEmpty(id))
            {
                id = FgjM0Journey.Id;
            }
            if (!Run(id, Environment.GetEnvironmentVariable("BINGAMES_JOURNEY_OUT")) && Application.isBatchMode)
            {
                EditorApplication.Exit(2);
            }
        }

        [MenuItem("BinGames/旅程机器人/FGJ-M0（M0 出口旅程）")]
        public static void RunFgjM0FromMenu() => Run(FgjM0Journey.Id, null);

        [MenuItem("BinGames/旅程机器人/FGJ-M1（M1 出口旅程）")]
        public static void RunFgjM1FromMenu() => Run(FgjM1Journey.Id, null);

        [MenuItem("BinGames/旅程机器人/FGJ-M1R（M1 反向旅程）")]
        public static void RunFgjM1ReverseFromMenu() => Run(FgjM1ReverseJourney.Id, null);

        public static bool Run(string id, string reportPath)
        {
            JourneyDef def = JourneyCatalog.Create(id);
            if (def == null)
            {
                Debug.LogError($"[JourneyBot] 没有旅程 {id}；可选：{string.Join(", ", JourneyCatalog.Ids)}");
                return false;
            }
            if (string.IsNullOrEmpty(reportPath))
            {
                reportPath = Path.Combine(Path.GetTempPath(), $"bingames-journey-{id}.txt");
            }
            SessionState.SetString(Prefix + "Id", id);
            _runner = new JourneyRunner(def, new SessionJourneyStore(Prefix), new EditorJourneyClock()) { ReportPath = reportPath };
            _runner.Start();
            Hook();
            return true;
        }

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
