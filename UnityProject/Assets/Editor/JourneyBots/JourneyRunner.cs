using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>一步的结果。<see cref="Wait"/> = 条件还没满足，下次再看（受步骤超时约束）。</summary>
    public readonly struct StepOutcome
    {
        public readonly JourneyStepStatus Status;
        public readonly string Message;

        private StepOutcome(JourneyStepStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public static StepOutcome Wait => new StepOutcome(JourneyStepStatus.Pending, null);
        public static StepOutcome Done(string message = null) => new StepOutcome(JourneyStepStatus.Done, message);
        /// <summary>这次尝试失败但可以重来（例如点击没生效）：还有重试次数就重来，否则整条旅程失败。</summary>
        public static StepOutcome Retry(string message) => new StepOutcome(JourneyStepStatus.Retry, message);
        /// <summary>确定失败（状态不对、断言不过）：不重试，整条旅程失败。</summary>
        public static StepOutcome Fail(string message) => new StepOutcome(JourneyStepStatus.Fail, message);
    }

    public enum JourneyStepStatus
    {
        Pending,
        Done,
        Retry,
        Fail,
    }

    /// <summary>旅程的一步：进入时做一次 <see cref="OnEnter"/>（发输入），之后每次驱动调 <see cref="Tick"/> 看结果。</summary>
    public sealed class JourneyStep
    {
        public string Id;
        public string Title;
        public double TimeoutSeconds = 30;
        /// <summary>超时或 <see cref="StepOutcome.Retry"/> 后还能重来几次（0 = 不重试）。</summary>
        public int MaxRetries;
        public Action<JourneyContext> OnEnter;
        public Func<JourneyContext, StepOutcome> Tick;
        /// <summary>重来之前的收拾（例如关掉半开的面板）；之后会再调 <see cref="OnEnter"/>。</summary>
        public Action<JourneyContext> OnRetry;
    }

    /// <summary>一条旅程：固定种子 + 有序步骤。旅程只描述“做什么、看什么”，超时 / 重试 / 报告 / 跨域重载都归 <see cref="JourneyRunner"/>。</summary>
    public sealed class JourneyDef
    {
        public string Id;
        public string Title;
        /// <summary>固定测试种子（FGR-ARC-011：旅程在固定种子上运行，不依赖固定坐标）。</summary>
        public int Seed;
        public double TotalTimeoutSeconds = 600;
        /// <summary>运行期间出现 Error / Exception / Assert 日志即判失败（与冒烟同一口径）。</summary>
        public bool FailOnErrorLog = true;
        public List<JourneyStep> Steps = new List<JourneyStep>();
        /// <summary>结束（通过或失败）时的收拾，失败也会调用。</summary>
        public Action<JourneyContext, bool> OnFinish;

        /// <summary>FG-TOOL-01：旅程版本号。步骤的语义或顺序变了就加 1——旧断点自动作废（断点里记着版本，不一致拒绝使用）。</summary>
        public int Version = 1;

        /// <summary>
        /// FG-TOOL-01：完整跑到这些步骤完成时自动写断点（<see cref="JourneyCheckpoints"/>）。只登记“界面处于中性状态（没开面板、没进建造模式、在家园）、
        /// 后面步骤需要的状态都在存档 + 旅程变量里”的步骤；写断点时还会再查一遍中性状态，不满足就不写（记一行，不改变结论）。
        /// </summary>
        public string[] CheckpointAfter = Array.Empty<string>();

        /// <summary>FG-TOOL-01：从断点续跑、读档进家园之后调用——旅程自己在存档之外的状态（例如布局库目录）在这里重新载入。</summary>
        public Action<JourneyContext> OnCheckpointRestored;

        /// <summary>FG-TOOL-01：非空 = 这是从该步断点续跑的旅程（只用于迭代；报告结论里写明，交付验收必须完整跑）。</summary>
        public string ResumedFrom;
    }

    /// <summary>跨步骤、跨 Play 模式域重载都要保留的少量状态放这里（字符串键值）。</summary>
    public interface IJourneyStore
    {
        string GetString(string key, string fallback);
        void SetString(string key, string value);
        /// <summary>删掉一个键（之后 GetString 返回 fallback，而不是空串）。</summary>
        void Erase(string key);
    }

    public interface IJourneyClock
    {
        double Now { get; }
    }

    /// <summary>编辑器会话存储：进 Play 重载域后仍在（驱动冒烟用的同一种机制）。</summary>
    public sealed class SessionJourneyStore : IJourneyStore
    {
        private readonly string _prefix;

        public SessionJourneyStore(string prefix)
        {
            _prefix = prefix;
        }

        public string GetString(string key, string fallback) => SessionState.GetString(_prefix + key, fallback);
        public void SetString(string key, string value) => SessionState.SetString(_prefix + key, value ?? string.Empty);
        public void Erase(string key) => SessionState.EraseString(_prefix + key);
    }

    public sealed class MemoryJourneyStore : IJourneyStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string GetString(string key, string fallback) => Values.TryGetValue(key, out string v) ? v : fallback;
        public void SetString(string key, string value) => Values[key] = value ?? string.Empty;
        public void Erase(string key) => Values.Remove(key);
    }

    public sealed class EditorJourneyClock : IJourneyClock
    {
        public double Now => EditorApplication.timeSinceStartup;
    }

    /// <summary>步骤里能用的东西：旅程、当前步、持久键值、报告行。</summary>
    public sealed class JourneyContext
    {
        private readonly JourneyRunner _runner;

        internal JourneyContext(JourneyRunner runner)
        {
            _runner = runner;
        }

        public JourneyDef Journey => _runner.Journey;
        public int StepIndex => _runner.StepIndex;
        public int Attempt => _runner.Attempt;
        public double StepElapsed => _runner.StepElapsed;
        public int Seed => _runner.Journey.Seed;

        public string Get(string key, string fallback = null) => _runner.Store.GetString("v." + key, fallback);

        public void Set(string key, string value)
        {
            _runner.Store.SetString("v." + key, value);
            _runner.RememberVar(key);
        }
        public int GetInt(string key, int fallback = 0) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
        public long GetLong(string key, long fallback = 0) => long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;
        public void SetLong(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        /// <summary>写一行到报告（带相对时间）。</summary>
        public void Log(string line) => _runner.Write("    " + line);
    }

    /// <summary>
    /// FG0-QA-01（FG14 FGR-ARC-011）：旅程机器人框架。ER8 的冒烟脚本（<c>PlaySmokeTest</c>）是一个手写的 switch 状态机；
    /// 这里把“步骤推进、每步超时、有限次重试、总超时、报错收集、跨 Play 模式域重载续跑、报告”抽成通用部分，旅程只写步骤。
    ///
    /// 驱动方式与冒烟相同：<see cref="EditorApplication.update"/> 每次调 <see cref="Tick"/>；进 Play 会重载域，状态存在
    /// <see cref="IJourneyStore"/>（Editor 下是 SessionState），<c>[InitializeOnLoad]</c> 的 <see cref="JourneyBotHost"/> 重载后接着跑。
    /// 自检（<c>JourneyRunnerSelfCheck</c>）用内存存储 + 假时钟逐步驱动，证明失败路径上的行为（超时、重试、报告）。
    ///
    /// 报告：文本（逐步记录 + 结论行“结论：PASS / FAIL”）与 JSON 摘要（机器读，给性能基线对比等后续工具用）。
    /// </summary>
    public sealed class JourneyRunner
    {
        public const string ResultPass = "PASS";
        public const string ResultFail = "FAIL";

        private readonly IJourneyClock _clock;
        private readonly List<string> _memoryLines = new List<string>();
        private readonly JourneyContext _ctx;

        public JourneyDef Journey { get; }
        public IJourneyStore Store { get; }

        /// <summary>报告文本文件；为空时只记在内存（<see cref="Lines"/>）。</summary>
        public string ReportPath
        {
            get => Store.GetString("reportPath", string.Empty);
            set => Store.SetString("reportPath", value);
        }

        public IReadOnlyList<string> Lines => _memoryLines;

        /// <summary>
        /// FG-TOOL-01：登记的断点步骤（<see cref="JourneyDef.CheckpointAfter"/>）完成后调用，写断点（参数：运行器、刚完成的步骤、步骤序号）。
        /// 为空 = 不写断点（续跑、关掉断点时）。写断点失败只记一行，不改变旅程结论。自检注入假的写入函数验证调用时机。
        /// </summary>
        public Action<JourneyRunner, JourneyStep, int> CheckpointWriter;

        public JourneyRunner(JourneyDef journey, IJourneyStore store, IJourneyClock clock)
        {
            Journey = journey ?? throw new ArgumentNullException(nameof(journey));
            Store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? new EditorJourneyClock();
            _ctx = new JourneyContext(this);
        }

        public bool Active => Store.GetString("active", "0") == "1";
        public bool Finished => Store.GetString("result", string.Empty).Length > 0;
        public string Result => Store.GetString("result", string.Empty);
        public string FailStepId => Store.GetString("failStep", string.Empty);
        public string FailReason => Store.GetString("failReason", string.Empty);
        public int StepIndex => GetInt("step", 0);
        public int Attempt => GetInt("attempt", 0);
        public int Errors => GetInt("errors", 0);
        public double StepElapsed => _clock.Now - GetDouble("stepStart", _clock.Now);
        public double TotalElapsed => _clock.Now - GetDouble("start", _clock.Now);

        /// <summary>
        /// 记下步骤写过的变量名（“v.”键）：SessionState 不能列举键，开新一趟时靠这份名单把上一趟的变量全部删掉——
        /// 否则同一编辑器会话里第二次从菜单跑，“已点过存档槽”“到达记录”之类的残留会让步骤跳过或误判。
        /// </summary>
        internal void RememberVar(string key)
        {
            string keys = Store.GetString("vkeys", string.Empty);
            if (("\n" + keys + "\n").Contains("\n" + key + "\n"))
            {
                return;
            }
            Store.SetString("vkeys", keys.Length == 0 ? key : keys + "\n" + key);
        }

        /// <summary>开始（清掉上一次的状态，包括上一趟步骤写下的全部变量）。</summary>
        public void Start()
        {
            foreach (string k in new[] { "result", "failStep", "failReason", "durations", "retries", "entered", "checkpoints" })
            {
                Store.SetString(k, string.Empty);
            }
            foreach (string v in Store.GetString("vkeys", string.Empty).Split('\n'))
            {
                if (v.Length > 0)
                {
                    Store.Erase("v." + v);
                }
            }
            Store.Erase("vkeys");
            SetInt("step", 0);
            SetInt("attempt", 0);
            SetInt("errors", 0);
            SetDouble("start", _clock.Now);
            SetDouble("stepStart", _clock.Now);
            Store.SetString("active", "1");
            if (!string.IsNullOrEmpty(ReportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? ".");
                File.WriteAllText(ReportPath, string.Empty);
            }
            Write($"旅程 {Journey.Id}「{Journey.Title}」开始：种子 {Journey.Seed}，{Journey.Steps.Count} 步，总超时 {Journey.TotalTimeoutSeconds:F0} 秒");
            if (!string.IsNullOrEmpty(Journey.ResumedFrom))
            {
                Write($"⚠ 断点续跑：从“{Journey.ResumedFrom}”之后接着跑（v{Journey.Version}）——只用于迭代；交付验收必须从主菜单完整跑：bash tools/unity-journey.sh {Journey.Id}");
            }
        }

        /// <summary>收到一条日志（Host 把 Application.logMessageReceived 转过来）：Error / Exception / Assert 计入报错。</summary>
        public void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!Active || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert))
            {
                return;
            }
            // 编辑器自带搜索索引在 batchmode 下的内部异常，与游戏无关（冒烟同一例外）。
            if ((stackTrace ?? string.Empty).Contains("UnityEditor.Search."))
            {
                Write($"  - 忽略编辑器内部报错：{(condition ?? string.Empty).Trim()}");
                return;
            }
            SetInt("errors", Errors + 1);
            string firstFrames = string.Join(" | ", (stackTrace ?? string.Empty).Split('\n').Where(l => l.Trim().Length > 0).Take(4));
            Write($"  ✗ [{type}] {(condition ?? string.Empty).Trim()}  @ {firstFrames}");
        }

        /// <summary>推进一次。返回 true = 旅程仍在进行。</summary>
        public bool Tick()
        {
            if (!Active)
            {
                return false;
            }
            if (TotalElapsed > Journey.TotalTimeoutSeconds)
            {
                Finish(false, CurrentStep()?.Id ?? "-", $"总超时（{Journey.TotalTimeoutSeconds:F0} 秒）");
                return false;
            }
            if (Journey.FailOnErrorLog && Errors > 0)
            {
                Finish(false, CurrentStep()?.Id ?? "-", $"运行中出现 {Errors} 条报错");
                return false;
            }
            JourneyStep step = CurrentStep();
            if (step == null)
            {
                Finish(true, null, null);
                return false;
            }
            StepOutcome outcome;
            try
            {
                if (Store.GetString("entered", string.Empty) != StepKey())
                {
                    Store.SetString("entered", StepKey());
                    SetDouble("stepStart", _clock.Now);
                    Write($"[{StepIndex + 1}/{Journey.Steps.Count}] {step.Id} {step.Title}{(Attempt > 0 ? $"（第 {Attempt + 1} 次尝试）" : string.Empty)}");
                    step.OnEnter?.Invoke(_ctx);
                }
                outcome = step.Tick != null ? step.Tick(_ctx) : StepOutcome.Done();
            }
            catch (Exception e)
            {
                outcome = StepOutcome.Fail($"步骤抛异常：{e.GetType().Name}: {e.Message}");
            }

            switch (outcome.Status)
            {
                case JourneyStepStatus.Done:
                    AppendList("durations", $"{step.Id}={StepElapsed.ToString("F2", CultureInfo.InvariantCulture)}");
                    Write($"  ✓ {outcome.Message ?? "完成"}（{StepElapsed:F1} 秒）");
                    TryWriteCheckpoint(step);
                    SetInt("step", StepIndex + 1);
                    SetInt("attempt", 0);
                    // 完成一步后不在同一次驱动里连着跑下一步：给引擎一帧处理刚发出的输入。
                    return true;
                case JourneyStepStatus.Fail:
                    Finish(false, step.Id, outcome.Message ?? "失败");
                    return false;
                case JourneyStepStatus.Retry:
                    return RetryOrFail(step, outcome.Message ?? "需要重试");
                default:
                    if (StepElapsed > step.TimeoutSeconds)
                    {
                        return RetryOrFail(step, $"超时（{step.TimeoutSeconds:F0} 秒）");
                    }
                    return true;
            }
        }

        /// <summary>FG-TOOL-01：登记的断点步骤刚完成 → 写断点。失败只记一行（断点只是迭代工具，不能让验收旅程因此失败）。</summary>
        private void TryWriteCheckpoint(JourneyStep step)
        {
            if (CheckpointWriter == null || Journey.CheckpointAfter == null || Array.IndexOf(Journey.CheckpointAfter, step.Id) < 0)
            {
                return;
            }
            try
            {
                CheckpointWriter(this, step, StepIndex);
                AppendList("checkpoints", step.Id);
            }
            catch (Exception e)
            {
                Write($"  - 断点“{step.Id}”没写成（不影响结论）：{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>FG-TOOL-01：旅程步骤写过的全部变量（断点存档时一并保存）。</summary>
        public Dictionary<string, string> SnapshotVars()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string k in Store.GetString("vkeys", string.Empty).Split('\n'))
            {
                if (k.Length > 0)
                {
                    d[k] = Store.GetString("v." + k, string.Empty);
                }
            }
            return d;
        }

        private bool RetryOrFail(JourneyStep step, string why)
        {
            if (Attempt < step.MaxRetries)
            {
                Write($"  ↻ {why}，重试（{Attempt + 1}/{step.MaxRetries}）");
                AppendList("retries", $"{step.Id}:{why}");
                try
                {
                    step.OnRetry?.Invoke(_ctx);
                }
                catch (Exception e)
                {
                    Finish(false, step.Id, $"重试前收拾抛异常：{e.GetType().Name}: {e.Message}");
                    return false;
                }
                SetInt("attempt", Attempt + 1);
                return true;
            }
            string reason = step.MaxRetries > 0 ? $"{why}（已重试 {step.MaxRetries} 次）" : why;
            Finish(false, step.Id, reason);
            return false;
        }

        private JourneyStep CurrentStep()
        {
            int i = StepIndex;
            return i >= 0 && i < Journey.Steps.Count ? Journey.Steps[i] : null;
        }

        private string StepKey() => StepIndex.ToString(CultureInfo.InvariantCulture) + "#" + Attempt.ToString(CultureInfo.InvariantCulture);

        private void Finish(bool pass, string stepId, string reason)
        {
            if (!Active)
            {
                return;
            }
            Store.SetString("active", "0");
            Store.SetString("result", pass ? ResultPass : ResultFail);
            Store.SetString("failStep", stepId ?? string.Empty);
            Store.SetString("failReason", reason ?? string.Empty);
            if (!pass)
            {
                JourneyStep s = Journey.Steps.FirstOrDefault(x => x.Id == stepId);
                Write($"  ✗ 失败于 {stepId}{(s != null ? " " + s.Title : string.Empty)}：{reason}");
            }
            Write($"结论：{(pass ? ResultPass : ResultFail)}（{(pass ? "完成" : reason)}，报错 {Errors} 条，用时 {TotalElapsed:F1} 秒" +
                  $"{(string.IsNullOrEmpty(Journey.ResumedFrom) ? string.Empty : $"；断点续跑：从“{Journey.ResumedFrom}”之后，只用于迭代，不是交付验收")}）");
            try
            {
                Journey.OnFinish?.Invoke(_ctx, pass);
            }
            catch (Exception e)
            {
                Write($"  - 收尾抛异常（不改变结论）：{e.GetType().Name}: {e.Message}");
            }
            WriteSummaryJson(pass);
        }

        /// <summary>JSON 摘要：报告路径同名 .json。</summary>
        public string SummaryJson(bool pass)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"journey\":").Append(Quote(Journey.Id)).Append(',');
            sb.Append("\"seed\":").Append(Journey.Seed.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"result\":").Append(Quote(pass ? ResultPass : ResultFail)).Append(',');
            sb.Append("\"failStep\":").Append(Quote(FailStepId)).Append(',');
            sb.Append("\"failReason\":").Append(Quote(FailReason)).Append(',');
            sb.Append("\"errors\":").Append(Errors.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"seconds\":").Append(TotalElapsed.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"resumedFrom\":").Append(Quote(Journey.ResumedFrom ?? string.Empty)).Append(',');
            sb.Append("\"checkpoints\":[").Append(string.Join(",", List("checkpoints").Select(Quote))).Append("],");
            sb.Append("\"steps\":[");
            bool first = true;
            foreach (string d in List("durations"))
            {
                int eq = d.LastIndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                sb.Append("{\"id\":").Append(Quote(d.Substring(0, eq))).Append(",\"seconds\":").Append(d.Substring(eq + 1)).Append('}');
            }
            sb.Append("],\"retries\":[");
            sb.Append(string.Join(",", List("retries").Select(Quote)));
            sb.Append("]}");
            return sb.ToString();
        }

        private void WriteSummaryJson(bool pass)
        {
            string path = ReportPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                File.WriteAllText(Path.ChangeExtension(path, ".json"), SummaryJson(pass));
            }
            catch (Exception)
            {
                // 摘要写不了不改变结论（文本报告里已有结论行）。
            }
        }

        public List<string> List(string key)
        {
            string raw = Store.GetString(key, string.Empty);
            return raw.Length == 0 ? new List<string>() : raw.Split('\n').ToList();
        }

        private void AppendList(string key, string item)
        {
            string raw = Store.GetString(key, string.Empty);
            Store.SetString(key, raw.Length == 0 ? item.Replace('\n', ' ') : raw + "\n" + item.Replace('\n', ' '));
        }

        internal void Write(string line)
        {
            string stamped = $"[{TotalElapsed,7:F1}s] {line}";
            _memoryLines.Add(stamped);
            string path = ReportPath;
            if (!string.IsNullOrEmpty(path))
            {
                File.AppendAllText(path, stamped + Environment.NewLine);
            }
        }

        private int GetInt(string key, int fallback) =>
            int.TryParse(Store.GetString(key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private void SetInt(string key, int value) => Store.SetString(key, value.ToString(CultureInfo.InvariantCulture));

        private double GetDouble(string key, double fallback) =>
            double.TryParse(Store.GetString(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        private void SetDouble(string key, double value) => Store.SetString(key, value.ToString("R", CultureInfo.InvariantCulture));

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
