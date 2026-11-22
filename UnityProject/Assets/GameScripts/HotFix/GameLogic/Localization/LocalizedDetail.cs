using System;
using System.Collections.Generic;
using System.Text;

namespace GameLogic.Localization
{
    /// <summary>
    /// FG15-UX-03（DEBT-FG0UX01-11）：会落盘、之后按当前语言重新显示的文字（通知历史的细节）存成“文本键 + 参数”，不存当时语言的成句。
    ///
    /// 产生细节的系统（两百多处反馈时刻）照旧传 <see cref="GameText.Format"/> / <see cref="GameText.Get"/> 的结果；
    /// <see cref="GameText.Format"/> 把最近的“成句 → 键 + 参数”记在一个定长环里（<see cref="RingSize"/> 条，O(1)），
    /// <see cref="Capture"/> 在通知入库时把成句换回编码；参数本身是某个键的当前语言文本（例如建筑名）时也换成键，显示时一并按新语言解析。
    /// 认不出来的（玩家起的名字、数字拼出的串）原样保留。编码：<c>\u0001键\u001f参数…</c>，参数以 <c>\u0002</c> 开头表示“这是文本键”。
    /// </summary>
    public static class LocalizedDetail
    {
        public const int RingSize = 128;
        private const char FormatTag = '\u0001';
        private const char KeyTag = '\u0002';
        private const char Sep = '\u001f';
        /// <summary>参数以它开头 = 数值：<c>\u0003类型码:不变区域文本</c>，显示时还原原类型，模式里的 {1:0} / {0:F0} 等格式说明符照常生效。</summary>
        private const char NumTag = '\u0003';

        private readonly struct Entry
        {
            public readonly string Key;
            public readonly object[] Args;

            public Entry(string key, object[] args)
            {
                Key = key;
                Args = args;
            }
        }

        private static readonly Dictionary<string, Entry> _recent = new Dictionary<string, Entry>(RingSize);
        private static readonly string[] _ring = new string[RingSize];
        private static int _ringPos;
        private static Dictionary<string, string> _reverse;
        private static int _reverseRevision = -1;

        /// <summary>由 <see cref="GameText.Format"/> 在成功格式化后调用。</summary>
        internal static void Remember(string result, string key, object[] args)
        {
            if (string.IsNullOrEmpty(result) || _recent.ContainsKey(result))
            {
                return;
            }
            string old = _ring[_ringPos];
            if (old != null)
            {
                _recent.Remove(old);
            }
            _ring[_ringPos] = result;
            _ringPos = (_ringPos + 1) % RingSize;
            _recent[result] = new Entry(key, args);
        }

        public static bool IsEncoded(string s) => !string.IsNullOrEmpty(s) && (s[0] == FormatTag || s[0] == KeyTag);

        /// <summary>把一段刚由 GameText 产生的文字换成可随语言切换的编码；认不出来就原样返回。</summary>
        public static string Capture(string text)
        {
            if (string.IsNullOrEmpty(text) || IsEncoded(text))
            {
                return text;
            }
            if (GameText.Has(text))
            {
                return text; // 调用方直接传了文本键（旧约定），Render 照旧解析
            }
            if (_recent.TryGetValue(text, out Entry e))
            {
                return Encode(e.Key, e.Args);
            }
            string key = ReverseKey(text);
            return key != null ? KeyTag + key : text;
        }

        /// <summary>显式编码（新代码可以直接用）。</summary>
        public static string Encode(string key, params object[] args)
        {
            var sb = new StringBuilder();
            sb.Append(FormatTag).Append(key);
            foreach (object a in args ?? Array.Empty<object>())
            {
                sb.Append(Sep);
                TypeCode tc = Convert.GetTypeCode(a);
                if (tc >= TypeCode.SByte && tc <= TypeCode.Decimal)
                {
                    string num = a is double d ? d.ToString("R", Inv) : a is float f ? f.ToString("R", Inv) : Convert.ToString(a, Inv);
                    sb.Append(NumTag).Append((int)tc).Append(':').Append(num);
                    continue;
                }
                string s = Convert.ToString(a, Inv) ?? string.Empty;
                string k = a is string ? (IsEncoded(s) ? null : ReverseKey(s)) : null;
                if (a is string && IsEncoded(s))
                {
                    sb.Append(s.Replace(Sep, ' ')); // 嵌套编码：只保留一层，按成句显示
                }
                else
                {
                    sb.Append(k != null ? KeyTag + k : s.Replace(Sep, ' '));
                }
            }
            return sb.ToString();
        }

        /// <summary>按当前语言显示：编码 → 现取键文本再格式化；文本键 → 现取；其余原样。</summary>
        public static string Render(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }
            if (stored[0] == KeyTag)
            {
                return GameText.Get(stored.Substring(1));
            }
            if (stored[0] != FormatTag)
            {
                return GameText.Has(stored) ? GameText.Get(stored) : stored;
            }
            string[] parts = stored.Substring(1).Split(Sep);
            var args = new object[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                args[i - 1] = p.Length > 0 && p[0] == KeyTag ? GameText.Get(p.Substring(1)) : p.Length > 0 && p[0] == NumTag ? Num(p) : p;
            }
            return GameText.Format(parts[0], args);
        }

        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        private static object Num(string p)
        {
            int colon = p.IndexOf(':');
            if (colon > 1 && int.TryParse(p.Substring(1, colon - 1), out int tc))
            {
                try
                {
                    return Convert.ChangeType(p.Substring(colon + 1), (TypeCode)tc, Inv);
                }
                catch (Exception)
                {
                    // 坏数据：按文本显示，不让通知历史崩掉
                }
            }
            return p.Substring(colon + 1);
        }

                /// <summary>当前语言文本 → 键（两种语言都收）；文本相同的键取第一个（同一文本在另一语言下的译法可能不同，取哪个都已是正确语言的写法之一）。</summary>
        private static string ReverseKey(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 64)
            {
                return null; // 名字、短语才反查；长句只走 Format 环
            }
            if (_reverse == null || _reverseRevision != GameText.Revision)
            {
                _reverseRevision = GameText.Revision;
                _reverse = new Dictionary<string, string>();
                foreach (string k in GameText.AllKeys())
                {
                    foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                    {
                        if (GameText.TryGet(k, lang, out string t) && t.Length <= 64 && !_reverse.ContainsKey(t))
                        {
                            _reverse[t] = k;
                        }
                    }
                }
            }
            return _reverse.TryGetValue(text, out string key) ? key : null;
        }

        /// <summary>测试用：清空最近记录。</summary>
        public static void ResetForTests()
        {
            _recent.Clear();
            Array.Clear(_ring, 0, _ring.Length);
            _ringPos = 0;
            _reverse = null;
        }
    }
}
