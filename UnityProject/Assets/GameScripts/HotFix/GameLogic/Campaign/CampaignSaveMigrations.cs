using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG0-SAVE-01（FGR-ARC-008 / FGR-SYS-004）：一级存档升级器，把存档正文从 <see cref="FromVersion"/> 升到
    /// <c>FromVersion + 1</c>。
    ///
    /// 升级器操作的是**正文 JSON 文本**而不是 <see cref="CampaignState"/> 对象：旧版本的字段在当前类型里可能已经
    /// 不存在（改名、拆分），只有文本层面才看得见。简单改动用 <see cref="SaveMigrationJson"/> 的顶层字段工具；
    /// 复杂改动可以在升级器里定义一个"旧版本快照"类型，用 JsonUtility 读出来再写成新结构。
    ///
    /// 什么时候需要写升级器：字段改名、拆分合并、单位或语义变化。只是**新增**字段时不需要——JsonUtility 对缺失字段
    /// 补默认值，状态域缺失由 <see cref="CampaignFgStateDomains.EnsureAll"/> 补空域。
    /// 升级器抛出任何异常都视为升级失败：读档返回失败，磁盘上的原文件不动。
    /// </summary>
    public interface ISaveMigrationStep
    {
        int FromVersion { get; }
        /// <summary>给开发看的一句话说明（进日志与自检报告）。</summary>
        string Description { get; }
        string Migrate(string payloadJson);
    }

    public enum SaveMigrationOutcome
    {
        /// <summary>已是目标版本或升级成功。</summary>
        Ok = 0,
        /// <summary>链上缺少某一级升级器。</summary>
        Missing = 1,
        /// <summary>某一级升级器抛异常或返回空。</summary>
        Failed = 2,
        /// <summary>版本号不在链的范围内（低于最早正式版或高于目标）。</summary>
        OutOfRange = 3,
    }

    public readonly struct SaveMigrationRun
    {
        public readonly SaveMigrationOutcome Outcome;
        public readonly string PayloadJson;
        /// <summary>失败发生在哪一级（从哪个版本往上升时失败 / 缺失）。成功时为 0。</summary>
        public readonly int FailedFromVersion;
        public readonly string Message;
        public readonly int[] AppliedFromVersions;

        public SaveMigrationRun(SaveMigrationOutcome outcome, string payloadJson, int failedFromVersion, string message, int[] applied)
        {
            Outcome = outcome;
            PayloadJson = payloadJson;
            FailedFromVersion = failedFromVersion;
            Message = message;
            AppliedFromVersions = applied ?? Array.Empty<int>();
        }

        public bool Success => Outcome == SaveMigrationOutcome.Ok;
    }

    /// <summary>逐级升级链：从 <see cref="MinVersion"/> 到 <see cref="TargetVersion"/>，每一级恰好一个升级器。
    /// 构造时拒绝重复、越界的升级器；缺级允许构造（便于测试"缺升级器"的负向路径），但 <see cref="MissingSteps"/>
    /// 会列出来，正式链由自检断言没有缺级。</summary>
    public sealed class SaveMigrationChain
    {
        private readonly Dictionary<int, ISaveMigrationStep> _steps = new Dictionary<int, ISaveMigrationStep>();

        public int MinVersion { get; }
        public int TargetVersion { get; }

        public SaveMigrationChain(int minVersion, int targetVersion, IEnumerable<ISaveMigrationStep> steps)
        {
            if (minVersion < 1 || targetVersion < minVersion)
            {
                throw new ArgumentException($"升级链范围非法：{minVersion} → {targetVersion}");
            }
            MinVersion = minVersion;
            TargetVersion = targetVersion;
            foreach (ISaveMigrationStep step in steps ?? Array.Empty<ISaveMigrationStep>())
            {
                if (step == null)
                {
                    throw new ArgumentException("升级链里有空的升级器");
                }
                if (step.FromVersion < minVersion || step.FromVersion >= targetVersion)
                {
                    throw new ArgumentException($"升级器 v{step.FromVersion}→v{step.FromVersion + 1} 超出链范围 {minVersion}→{targetVersion}");
                }
                if (_steps.ContainsKey(step.FromVersion))
                {
                    throw new ArgumentException($"升级器 v{step.FromVersion}→v{step.FromVersion + 1} 重复登记");
                }
                _steps.Add(step.FromVersion, step);
            }
        }

        public int StepCount => _steps.Count;

        /// <summary>缺失的升级级别（每个元素是"从哪个版本升"）。</summary>
        public int[] MissingSteps =>
            Enumerable.Range(MinVersion, TargetVersion - MinVersion).Where(v => !_steps.ContainsKey(v)).ToArray();

        /// <summary>该版本的存档能否升到目标版本（不执行，只看链是否完整）。</summary>
        public bool CanUpgradeFrom(int version, out int missingFrom)
        {
            missingFrom = 0;
            if (version < MinVersion || version > TargetVersion)
            {
                missingFrom = version;
                return false;
            }
            for (int v = version; v < TargetVersion; v++)
            {
                if (!_steps.ContainsKey(v))
                {
                    missingFrom = v;
                    return false;
                }
            }
            return true;
        }

        public SaveMigrationRun Run(int fromVersion, string payloadJson)
        {
            if (fromVersion < MinVersion || fromVersion > TargetVersion)
            {
                return new SaveMigrationRun(SaveMigrationOutcome.OutOfRange, null, fromVersion,
                    $"存档版本 {fromVersion} 不在升级链 {MinVersion}→{TargetVersion} 内", null);
            }
            if (!CanUpgradeFrom(fromVersion, out int missing))
            {
                return new SaveMigrationRun(SaveMigrationOutcome.Missing, null, missing,
                    $"缺少升级器 v{missing}→v{missing + 1}", null);
            }

            string json = payloadJson;
            var applied = new List<int>();
            for (int v = fromVersion; v < TargetVersion; v++)
            {
                ISaveMigrationStep step = _steps[v];
                try
                {
                    json = step.Migrate(json);
                }
                catch (Exception e)
                {
                    return new SaveMigrationRun(SaveMigrationOutcome.Failed, null, v,
                        $"升级器 v{v}→v{v + 1}（{step.Description}）失败：{e.Message}", applied.ToArray());
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new SaveMigrationRun(SaveMigrationOutcome.Failed, null, v,
                        $"升级器 v{v}→v{v + 1}（{step.Description}）返回空正文", applied.ToArray());
                }
                applied.Add(v);
            }
            return new SaveMigrationRun(SaveMigrationOutcome.Ok, json, 0, null, applied.ToArray());
        }
    }

    /// <summary>存档版本号与正式升级链的唯一出处。</summary>
    public static class CampaignSaveMigrations
    {
        /// <summary>Demo（0.1）的存档格式版本。正式版不迁移 Demo 存档（FGR-ARC-008），读到时明确提示。</summary>
        public const int LastDemoSchemaVersion = 1;

        /// <summary>正式版（0.2）的第一个存档格式版本。升级链从这里起步。</summary>
        public const int FirstFullGameSchemaVersion = 2;

        /// <summary>
        /// 正式升级链。新增存档格式版本时：把 <see cref="CampaignSaveService.CurrentSchemaVersion"/> 加 1，
        /// 并在这里登记"从旧版本升一级"的升级器；自检 [存档v2] 会断言正式链没有缺级。
        /// 0.2 起步只有 v2 一个版本，所以链上还没有升级器。
        /// </summary>
        public static readonly SaveMigrationChain Production = new SaveMigrationChain(
            FirstFullGameSchemaVersion, CampaignSaveService.CurrentSchemaVersion, Array.Empty<ISaveMigrationStep>());

        /// <summary>自检专用：换成带假想未来版本的链（例如 v2→v3→v4），验证逐级升级真的被执行。用完置回 null。</summary>
        public static SaveMigrationChain OverrideForTests { get; set; }

        public static SaveMigrationChain Active => OverrideForTests ?? Production;
    }

    /// <summary>
    /// 升级器用的顶层 JSON 字段工具（只处理最外层对象的成员，嵌套内容原样保留）。JsonUtility 不提供 DOM，
    /// 这里用一个只认 JSON 语法结构的小扫描器定位成员，字符串里的引号、括号、转义都不会误判。
    /// 输入不是合法 JSON 对象时抛 <see cref="FormatException"/>（升级器失败 → 读档失败，原文件不动）。
    /// </summary>
    public static class SaveMigrationJson
    {
        private readonly struct Member
        {
            public readonly string Key;
            public readonly int KeyStart;
            public readonly int ValueStart;
            public readonly int ValueEnd;

            public Member(string key, int keyStart, int valueStart, int valueEnd)
            {
                Key = key;
                KeyStart = keyStart;
                ValueStart = valueStart;
                ValueEnd = valueEnd;
            }
        }

        public static bool HasField(string json, string name) => Scan(json, out _).Any(m => m.Key == name);

        /// <summary>取某个顶层成员的原始 JSON 值文本（字符串带引号）；不存在返回 null。</summary>
        public static string GetRaw(string json, string name)
        {
            foreach (Member m in Scan(json, out _))
            {
                if (m.Key == name)
                {
                    return json.Substring(m.ValueStart, m.ValueEnd - m.ValueStart);
                }
            }
            return null;
        }

        /// <summary>改名；旧字段不存在时原样返回；新名字已存在时抛异常（避免静默覆盖）。</summary>
        public static string RenameField(string json, string oldName, string newName)
        {
            List<Member> members = Scan(json, out _);
            if (members.Any(m => m.Key == newName))
            {
                throw new FormatException($"改名目标 {newName} 已存在");
            }
            foreach (Member m in members)
            {
                if (m.Key == oldName)
                {
                    int keyEnd = SkipString(json, m.KeyStart);
                    return json.Substring(0, m.KeyStart) + Quote(newName) + json.Substring(keyEnd);
                }
            }
            return json;
        }

        /// <summary>设置（替换或新增）一个顶层成员，<paramref name="rawValue"/> 是合法的 JSON 值文本。</summary>
        public static string SetRaw(string json, string name, string rawValue)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                throw new ArgumentException("rawValue 不能为空");
            }
            List<Member> members = Scan(json, out int closeBrace);
            foreach (Member m in members)
            {
                if (m.Key == name)
                {
                    return json.Substring(0, m.ValueStart) + rawValue + json.Substring(m.ValueEnd);
                }
            }
            string insert = (members.Count > 0 ? "," : string.Empty) + Quote(name) + ":" + rawValue;
            return json.Substring(0, closeBrace) + insert + json.Substring(closeBrace);
        }

        /// <summary>删除一个顶层成员（连同相邻的逗号）；不存在时原样返回。</summary>
        public static string RemoveField(string json, string name)
        {
            List<Member> members = Scan(json, out _);
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Key != name)
                {
                    continue;
                }
                int start = members[i].KeyStart;
                int end = members[i].ValueEnd;
                if (i + 1 < members.Count)
                {
                    end = members[i + 1].KeyStart; // 吃掉后面的逗号
                }
                else if (i > 0)
                {
                    start = members[i - 1].ValueEnd; // 最后一个：吃掉前面的逗号
                }
                return json.Substring(0, start) + json.Substring(end);
            }
            return json;
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\');
                }
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static List<Member> Scan(string json, out int closeBrace)
        {
            if (json == null)
            {
                throw new FormatException("JSON 为空");
            }
            var list = new List<Member>();
            int i = SkipWs(json, 0);
            Require(json, i, '{');
            i++;
            while (true)
            {
                i = SkipWs(json, i);
                Require(json, i);
                if (json[i] == '}')
                {
                    closeBrace = i;
                    return list;
                }
                Require(json, i, '"');
                int keyStart = i;
                int keyEnd = SkipString(json, i);
                string key = Unescape(json.Substring(keyStart + 1, keyEnd - keyStart - 2));
                i = SkipWs(json, keyEnd);
                Require(json, i, ':');
                i = SkipWs(json, i + 1);
                int valueStart = i;
                int valueEnd = SkipValue(json, i);
                list.Add(new Member(key, keyStart, valueStart, valueEnd));
                i = SkipWs(json, valueEnd);
                Require(json, i);
                if (json[i] == ',')
                {
                    i++;
                    continue;
                }
                Require(json, i, '}');
                closeBrace = i;
                return list;
            }
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            return i;
        }

        private static void Require(string s, int i, char? c = null)
        {
            if (i >= s.Length)
            {
                throw new FormatException("JSON 截断");
            }
            if (c.HasValue && s[i] != c.Value)
            {
                throw new FormatException($"JSON 第 {i} 个字符应为 '{c.Value}'，实际 '{s[i]}'");
            }
        }

        /// <summary>i 指向开头的引号，返回闭合引号之后的位置。</summary>
        private static int SkipString(string s, int i)
        {
            Require(s, i, '"');
            i++;
            while (true)
            {
                Require(s, i);
                char c = s[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    return i + 1;
                }
                i++;
            }
        }

        private static int SkipValue(string s, int i)
        {
            Require(s, i);
            char c = s[i];
            if (c == '"')
            {
                return SkipString(s, i);
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (true)
                {
                    Require(s, i);
                    char d = s[i];
                    if (d == '"')
                    {
                        i = SkipString(s, i);
                        continue;
                    }
                    if (d == '{' || d == '[')
                    {
                        depth++;
                    }
                    else if (d == '}' || d == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return i + 1;
                        }
                    }
                    i++;
                }
            }
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            if (i == start)
            {
                throw new FormatException($"JSON 第 {i} 个字符处缺少值");
            }
            return i;
        }

        private static string Unescape(string raw)
        {
            if (raw.IndexOf('\\') < 0)
            {
                return raw;
            }
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length)
                {
                    i++;
                }
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }
    }
}
