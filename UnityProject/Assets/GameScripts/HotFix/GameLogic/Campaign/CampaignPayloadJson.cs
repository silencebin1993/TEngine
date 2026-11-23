using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG15-SYS-02（DEBT-FG15SYS01-01）：存档正文 JSON 按域并行序列化——自动存档主线程快照里正文序列化与状态大小成正比（15.2 MB 压力档约 83 ms）。
    /// 做法：顶层的大数组（≥ <see cref="MinArrayElements"/> 个元素，按 <see cref="ChunkElements"/> 切块）与各个域对象分别在线程池上序列化，
    /// 其余字段照旧整份序列化成骨架，再按骨架里第一层的键把各段拼回原位。输出与 <c>JsonUtility.ToJson(state)</c> 逐字节相同（存档字节不变）：
    /// 本进程第一次在可以多花时间的地方（同步存档 / 读档）另跑一次整份序列化比对，一致才在后台存档里用并行版；不一致或抛异常就记错误、以后一直整份序列化。
    /// 主线程在并行段里一直等着（不改状态），序列化期间状态不被别的代码看到被临时置空的字段。
    /// </summary>
    public static class CampaignPayloadJson
    {
        public const int MinArrayElements = 256;
        public const int ChunkElements = 2048;

        private static FieldInfo[] _fields;
        private static int _verified; // 0 = 还没比对过，1 = 与整份序列化一致，-1 = 不一致 / 出错（一直整份序列化）

        /// <summary>自检专用：强制整份序列化（对照组）。</summary>
        public static bool ForceSerialForTests { get; set; }

        /// <summary>最近一次并行序列化的段数（0 = 整份序列化）。</summary>
        public static int LastParts { get; private set; }

        /// <summary>本进程的比对结果（自检读）：0 未比对、1 一致、-1 不一致。</summary>
        public static int Verified => _verified;

        /// <summary>序列化正文。<paramref name="canVerify"/> = 调用方能多花一次整份序列化的时间（同步存档、读档）：还没比对过时在这里比对。</summary>
        public static string Serialize(CampaignState state, bool canVerify)
        {
            LastParts = 0;
            if (ForceSerialForTests || _verified < 0 || (_verified == 0 && !canVerify))
            {
                return JsonUtility.ToJson(state);
            }
            string json = TrySerializeParallel(state);
            if (_verified == 0)
            {
                Verify(state, json);
            }
            return _verified > 0 && json != null ? json : JsonUtility.ToJson(state);
        }

        /// <summary>后台存档用（FG15-SYS-02 修复轮）：主线程只把各段并行序列化成不可变的字符串（快照到此完成），拼成整串留给写盘线程调返回的委托——
        /// 主线程窗口少分配、少拷一份正文（15 MB 正文约 30 MB），窗口里触发整堆回收的机会随之减半。没比对过 / 已回退时同 <see cref="Serialize"/>（当场得到整串）。</summary>
        public static Func<string> SerializeDeferred(CampaignState state, bool canVerify)
        {
            LastParts = 0;
            if (!ForceSerialForTests && _verified > 0)
            {
                List<(string src, int start, int length)> pieces = TryPieces(state);
                if (pieces != null)
                {
                    return () => Join(pieces);
                }
            }
            string json = Serialize(state, canVerify);
            return () => json;
        }

        /// <summary>读档后调用（读档本来就是阻塞的长操作）：本进程还没比对过就比对一次，之后的后台存档才用并行版。</summary>
        public static void VerifyOnce(CampaignState state)
        {
            if (_verified == 0 && state != null && !ForceSerialForTests)
            {
                Verify(state, TrySerializeParallel(state));
            }
        }

        /// <summary>自检专用：重置比对结果。</summary>
        public static void ResetForTests() => _verified = 0;

        /// <summary>自检专用：并行版的原始输出（不比对、不回退）。</summary>
        public static string SerializeParallelForTests(CampaignState state) => TrySerializeParallel(state);

        private static void Verify(CampaignState state, string parallel)
        {
            string serial = JsonUtility.ToJson(state);
            if (parallel != null && string.Equals(serial, parallel, StringComparison.Ordinal))
            {
                _verified = 1;
                return;
            }
            _verified = -1;
            TEngine.Log.Error($"[CampaignPayloadJson] 并行序列化与整份序列化不一致（{parallel?.Length ?? -1} / {serial.Length} 字符），本进程改回整份序列化。");
        }

        private sealed class Part
        {
            public int Field;
            public object Value;
            public bool Chunk;
            public string Json;
            public int Start;
            public int Length;
        }

        private static FieldInfo[] Fields()
        {
            if (_fields == null)
            {
                var list = new List<FieldInfo>();
                foreach (FieldInfo f in typeof(CampaignState).GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (f.IsNotSerialized || f.IsInitOnly || f.FieldType == typeof(string) || f.FieldType.IsValueType)
                    {
                        continue;
                    }
                    // 泛型集合（List 等）不能单独做顶层 ToJson：留在骨架里
                    if (f.FieldType.IsArray ? f.FieldType.GetArrayRank() == 1 : f.FieldType.IsClass && f.FieldType.IsSerializable && !f.FieldType.IsGenericType)
                    {
                        list.Add(f);
                    }
                }
                _fields = list.ToArray();
            }
            return _fields;
        }

        private static string TrySerializeParallel(CampaignState state)
        {
            List<(string src, int start, int length)> pieces = TryPieces(state);
            return pieces == null ? null : Join(pieces);
        }

        private static List<(string src, int start, int length)> TryPieces(CampaignState state)
        {
            try
            {
                return Pieces(state);
            }
            catch (Exception e)
            {
                _verified = -1;
                TEngine.Log.Error($"[CampaignPayloadJson] 并行序列化出错，本进程改回整份序列化：{e.Message}");
                return null;
            }
        }

        /// <summary>正文的各段（源串、起点、长度），按顺序首尾相接 = 整份正文；源串都是序列化出来的不可变字符串，不再引用 <paramref name="state"/>。</summary>
        private static List<(string src, int start, int length)> Pieces(CampaignState state)
        {
            FieldInfo[] fields = Fields();
            var values = new object[fields.Length];
            var parts = new List<Part>();
            for (int i = 0; i < fields.Length; i++)
            {
                object v = fields[i].GetValue(state);
                if (v == null)
                {
                    continue;
                }
                if (v is Array arr)
                {
                    if (arr.Length < MinArrayElements)
                    {
                        continue;
                    }
                    Type elem = arr.GetType().GetElementType();
                    for (int start = 0; start < arr.Length; start += ChunkElements)
                    {
                        int n = Math.Min(ChunkElements, arr.Length - start);
                        Array sub = Array.CreateInstance(elem, n);
                        Array.Copy(arr, start, sub, 0, n);
                        var holder = new CampaignState(); // 只放这一块：序列化后取第一层这个键的值
                        fields[i].SetValue(holder, sub);
                        parts.Add(new Part { Field = i, Value = holder, Chunk = true });
                    }
                }
                else
                {
                    parts.Add(new Part { Field = i, Value = v });
                }
                values[i] = v;
            }
            LastParts = parts.Count;
            if (parts.Count == 0)
            {
                string whole = JsonUtility.ToJson(state);
                return new List<(string src, int start, int length)> { (whole, 0, whole.Length) };
            }

            string skeleton;
            try
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    if (values[i] != null)
                    {
                        fields[i].SetValue(state, null);
                    }
                }
                skeleton = JsonUtility.ToJson(state);
            }
            finally
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    if (values[i] != null)
                    {
                        fields[i].SetValue(state, values[i]);
                    }
                }
            }

            Parallel.For(0, parts.Count, k =>
            {
                Part p = parts[k];
                p.Json = JsonUtility.ToJson(p.Value);
                p.Start = 0;
                p.Length = p.Json.Length;
                if (p.Chunk)
                {
                    TopLevelValue(p.Json, fields[p.Field].Name, out int s, out int e);
                    p.Start = s + 1; // 去掉数组的 [ ]（不另拷子串，拼接时直接按区间复制）
                    p.Length = e - s - 2;
                }
            });

            var spans = new List<(int start, int end, int field)>();
            for (int i = 0; i < fields.Length; i++)
            {
                if (values[i] != null)
                {
                    TopLevelValue(skeleton, fields[i].Name, out int s, out int e);
                    spans.Add((s, e, i));
                }
            }
            spans.Sort((x, y) => x.start.CompareTo(y.start));
            var pieces = new List<(string src, int start, int length)>(parts.Count * 2 + spans.Count * 3 + 1);
            int at = 0;
            foreach ((int s, int e, int field) in spans)
            {
                pieces.Add((skeleton, at, s - at));
                if (values[field] is Array)
                {
                    pieces.Add(("[", 0, 1));
                    bool first = true;
                    foreach (Part p in parts) // 同一字段的块在列表里按块序排着
                    {
                        if (p.Field != field)
                        {
                            continue;
                        }
                        if (!first)
                        {
                            pieces.Add((",", 0, 1));
                        }
                        pieces.Add((p.Json, p.Start, p.Length));
                        first = false;
                    }
                    pieces.Add(("]", 0, 1));
                }
                else
                {
                    Part p = PartOf(parts, field);
                    pieces.Add((p.Json, p.Start, p.Length));
                }
                at = e;
            }
            pieces.Add((skeleton, at, skeleton.Length - at));
            return pieces;
        }

        private static string Join(List<(string src, int start, int length)> pieces)
        {
            int total = 0;
            foreach ((string _, int _, int len) in pieces)
            {
                total += len;
            }
            // 只分配结果这一份（不经 StringBuilder 再拷一次）：15 MB 正文少分配约 60 MB，主线程少等 GC
            return string.Create(total, pieces, (dst, ps) =>
            {
                int w = 0;
                foreach ((string src, int start, int len) in ps)
                {
                    src.AsSpan(start, len).CopyTo(dst.Slice(w));
                    w += len;
                }
            });
        }

        private static Part PartOf(List<Part> parts, int field)
        {
            foreach (Part p in parts)
            {
                if (p.Field == field)
                {
                    return p;
                }
            }
            throw new InvalidOperationException("part missing");
        }

        /// <summary>在对象 JSON 的第一层找键 <paramref name="key"/> 的值，[start, end)。JsonUtility 的紧凑输出没有空白。</summary>
        private static void TopLevelValue(string json, string key, out int start, out int end)
        {
            int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    int close = SkipString(json, i);
                    if (depth == 1 && close + 1 < json.Length && json[close + 1] == ':')
                    {
                        int valueStart = close + 2;
                        int valueEnd = SkipValue(json, valueStart);
                        if (close - i - 1 == key.Length && string.CompareOrdinal(json, i + 1, key, 0, key.Length) == 0)
                        {
                            start = valueStart;
                            end = valueEnd;
                            return;
                        }
                        i = valueEnd - 1;
                        continue;
                    }
                    i = close;
                }
                else if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                }
            }
            throw new InvalidOperationException($"key {key} not found");
        }

        /// <summary>从开引号起，返回闭引号的下标。</summary>
        private static int SkipString(string json, int open)
        {
            for (int i = open + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    return i;
                }
            }
            throw new InvalidOperationException("unterminated string");
        }

        /// <summary>从值的第一个字符起，返回值结束后的下标。</summary>
        private static int SkipValue(string json, int pos)
        {
            char c = json[pos];
            if (c == '"')
            {
                return SkipString(json, pos) + 1;
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                for (int i = pos; i < json.Length; i++)
                {
                    char d = json[i];
                    if (d == '"')
                    {
                        i = SkipString(json, i);
                    }
                    else if (d == '{' || d == '[')
                    {
                        depth++;
                    }
                    else if ((d == '}' || d == ']') && --depth == 0)
                    {
                        return i + 1;
                    }
                }
                throw new InvalidOperationException("unterminated value");
            }
            int j = pos;
            while (j < json.Length && json[j] != ',' && json[j] != '}' && json[j] != ']')
            {
                j++;
            }
            return j;
        }
    }
}
