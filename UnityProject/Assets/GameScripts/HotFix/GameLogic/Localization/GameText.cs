using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Settings;
using TEngine;

namespace GameLogic.Localization
{
    /// <summary>FG0-DATA-01（FGR-ARC-006）：界面语言。新增语言时同时给 fg.TbLocText 加一列，
    /// 并在 <see cref="GameLanguageCodes"/> 与 <see cref="GameText"/> 的取列处各加一个分支。</summary>
    public enum GameLanguage
    {
        ZhCn = 0,
        En = 1,
    }

    /// <summary>语言代码与枚举互转。设置里存代码（"zh-CN"/"en"），不存枚举整数。</summary>
    public static class GameLanguageCodes
    {
        public const string ZhCn = "zh-CN";
        public const string En = "en";

        public static string ToCode(GameLanguage language) => language == GameLanguage.En ? En : ZhCn;

        /// <summary>读不懂的代码（手改的设置、以后删掉的语言）一律回落简体中文——游戏的基准语言，文本最全。</summary>
        public static GameLanguage Parse(string code)
        {
            return string.Equals(code, En, StringComparison.OrdinalIgnoreCase) ? GameLanguage.En : GameLanguage.ZhCn;
        }
    }

    /// <summary>
    /// FG0-DATA-01（FGR-ARC-006）：文本键 → 当前语言文本的唯一查询入口。数据来自 Luban 表 fg.TbLocText
    /// （源头 tools/cell_tables/fgdata.py，键 → 简体中文 + 英文）。
    ///
    /// 规则：
    /// - 从 FG-M0 起，所有新增的玩家可见文本都走 <see cref="Get"/>/<see cref="Format"/>，禁止硬编码。
    /// - 缺失的键、当前语言那一列为空、表没加载上，一律返回醒目的 <c>⟦key⟧</c>（<see cref="Marker"/>），
    ///   绝不静默返回空串（IC-REQ-013：安全拒绝也要可见）。第一次缺失时记一条 Warning，并进 <see cref="MissingKeys"/>。
    /// - 语言由 <see cref="GameSettings.Language"/> 决定，每次调用现读，切换语言后下一次刷新界面即生效。
    /// - 命中路径只做一次字典查找、零分配；缺失标记按键缓存，每帧重复查同一个缺失键也不产生垃圾。
    /// </summary>
    public static class GameText
    {
        public const string MarkerOpen = "⟦";
        public const string MarkerClose = "⟧";

        private static IReadOnlyDictionary<string, LocText> _map;
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;
        private static readonly HashSet<string> _missing = new HashSet<string>();
        private static readonly Dictionary<string, string> _markerCache = new Dictionary<string, string>();

        /// <summary>表加载失败的原因；null 表示正常。</summary>
        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        /// <summary>已加载的文本条数（失败时为 0）。</summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _map?.Count ?? 0;
            }
        }

        /// <summary>本次运行里被查过但缺失（或当前语言为空）的键，供调试面板与自检核对。</summary>
        public static IReadOnlyCollection<string> MissingKeys => _missing;

        public static GameLanguage Language => GameSettings.Language;

        /// <summary>缺失标记 <c>⟦key⟧</c>。</summary>
        public static string Marker(string key)
        {
            string k = key ?? "null";
            if (!_markerCache.TryGetValue(k, out string marker))
            {
                marker = MarkerOpen + k + MarkerClose;
                _markerCache[k] = marker;
            }
            return marker;
        }

        /// <summary>这段文本是不是缺失标记（界面扫描、冒烟测试用）。</summary>
        public static bool ContainsMarker(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(MarkerOpen, StringComparison.Ordinal) >= 0;

        public static bool Has(string key)
        {
            EnsureLoaded();
            return key != null && _map != null && _map.ContainsKey(key);
        }

        /// <summary>当前语言的文本；缺失返回 <c>⟦key⟧</c>。</summary>
        public static string Get(string key) => Get(key, GameSettings.Language);

        /// <summary>指定语言的文本；缺失返回 <c>⟦key⟧</c>。</summary>
        public static string Get(string key, GameLanguage language)
        {
            return TryGet(key, language, out string text) ? text : MarkMissing(key, language);
        }

        /// <summary>取文本；键不存在、该语言为空或表未加载时返回 false（不记缺失，调用方自己决定怎么降级）。</summary>
        public static bool TryGet(string key, GameLanguage language, out string text)
        {
            EnsureLoaded();
            text = null;
            if (key == null || _map == null || !_map.TryGetValue(key, out LocText row) || row == null)
            {
                return false;
            }
            text = language == GameLanguage.En ? row.En : row.Zh;
            return !string.IsNullOrEmpty(text);
        }

        /// <summary>带参数的文本（占位符 {0}{1}…，与 string.Format 一致）。键缺失返回标记；
        /// 占位符与参数对不上时也返回标记并记 Warning，不抛异常打断界面刷新。</summary>
        public static string Format(string key, params object[] args)
        {
            GameLanguage language = GameSettings.Language;
            if (!TryGet(key, language, out string pattern))
            {
                return MarkMissing(key, language);
            }
            try
            {
                return string.Format(pattern, args ?? Array.Empty<object>());
            }
            catch (FormatException ex)
            {
                if (_missing.Add(key + "#format"))
                {
                    Log.Warning($"[GameText] 文本键 {key} 的占位符与参数不匹配（{language}）：{ex.Message}");
                }
                return Marker(key);
            }
        }

        /// <summary>重新从 <see cref="ConfigSystem"/> 读表（热更新了配置包之后调用）。</summary>
        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            _map = null;
            _loadError = null;
            _missing.Clear();
            EnsureLoaded();
        }

        /// <summary>测试注入：用指定的表替换真实表（负向矩阵用它构造缺键 / 缺列 / 重复键的表）。
        /// 传 null 模拟"表没加载上"。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbLocText table, string loadError = null)
        {
            _overridden = true;
            _loaded = true;
            _map = table?.DataMap;
            _loadError = table == null ? (loadError ?? "测试注入：文本表为空") : loadError;
            _missing.Clear();
        }

        public static void ResetForTests()
        {
            Reload();
        }

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            try
            {
                TbLocText table = ConfigSystem.Instance.Tables?.TbLocText;
                if (table == null)
                {
                    _loadError = "配置表 fg.TbLocText 不存在（ConfigSystem.Tables 为空）";
                }
                else
                {
                    _map = table.DataMap;
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表 fg.TbLocText 读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[GameText] {_loadError}。所有文本将显示为 ⟦key⟧。");
            }
        }

        private static string MarkMissing(string key, GameLanguage language)
        {
            string k = key ?? "null";
            if (_missing.Add(k))
            {
                bool exists = _map != null && key != null && _map.ContainsKey(key);
                Log.Warning(exists
                    ? $"[GameText] 文本键 {k} 缺少 {GameLanguageCodes.ToCode(language)} 文本，界面显示 {Marker(k)}"
                    : $"[GameText] 文本键 {k} 不存在，界面显示 {Marker(k)}");
            }
            return Marker(k);
        }
    }
}
