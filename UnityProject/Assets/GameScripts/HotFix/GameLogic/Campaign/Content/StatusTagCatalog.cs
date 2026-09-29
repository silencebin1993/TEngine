using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG2-FW-01（FG02 FGR-FW-030）：状态标签的机械名——旧引擎（ComposeEngine HitEvent.Tags）里的标签字符串是内部 ID，
    /// 玩家看到的名字、头顶图标的形状与颜色都来自 Luban 表 fg.TbStatusTag（数据源 tools/cell_tables/fgdata_firmware.py）。
    ///
    /// - 旧引擎有两套并存的写法（Burning / Fire、Oiled / Oil、Shocked / Shock，处决与偏折各两个字符串）：同义标签指向主标签，
    ///   显示时按主标签合并（<see cref="Canonical"/>），不会出现两个“燃烧”。
    /// - 查不到的标签返回 null：调用方不显示它，也不会把内部字符串漏给玩家（自检扫描固件表里的全部标签都能查到）。
    /// - 表加载失败时记 Error 并视为空表（IC-REQ-013：可见失败，不崩）。
    /// 头顶标签图标（FGR-FW-031）与反应弹字（FGR-FW-043）读这里，分别由 FG2-FW-03 / FG2-FW-04 接到界面。
    /// </summary>
    public static class StatusTagCatalog
    {
        private static TbStatusTag _table;
        private static bool _loaded;
        private static string _loadError;

        /// <summary>FG2-FW-02：重载时 +1（读法翻译缓存的标签位 / 效果据此重建）。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        /// <summary>表里的全部行（含同义标签）。表不可用时为空。</summary>
        public static IReadOnlyList<StatusTag> Rows
        {
            get
            {
                EnsureLoaded();
                return _table?.DataList ?? (IReadOnlyList<StatusTag>)Array.Empty<StatusTag>();
            }
        }

        public static bool TryGet(string engineTag, out StatusTag row)
        {
            EnsureLoaded();
            row = null;
            return !string.IsNullOrEmpty(engineTag) && _table != null && _table.DataMap.TryGetValue(engineTag, out row) && row != null;
        }

        /// <summary>同义标签 → 主标签 ID；本身是主标签或查不到时原样返回。</summary>
        public static string Canonical(string engineTag) =>
            TryGet(engineTag, out StatusTag row) && !string.IsNullOrEmpty(row.AliasOf) && row.AliasOf != "none" ? row.AliasOf : engineTag;

        /// <summary>玩家可见的机械名（当前语言）；查不到时 null（调用方不显示）。</summary>
        public static string NameOf(string engineTag) =>
            TryGet(engineTag, out StatusTag row) ? GameText.Get(row.NameKey) : null;

        /// <summary>头顶图标的形状字形（色盲安全：形状为主）；同义标签取主标签的。查不到时 null。</summary>
        public static string ShapeOf(string engineTag) =>
            TryGet(Canonical(engineTag), out StatusTag row) && row.Shape != "none" ? row.Shape : null;

        /// <summary>头顶图标颜色 #RRGGBB（辅助通道）；同义标签取主标签的。查不到时 null。</summary>
        public static string ColorOf(string engineTag) =>
            TryGet(Canonical(engineTag), out StatusTag row) && row.Color != "none" ? row.Color : null;

        /// <summary>是不是机制标记（旧引擎模块给能量包打的行为记号，不是作用在单位上的状态；不在头顶显示）。</summary>
        public static bool IsMarker(string engineTag) => TryGet(engineTag, out StatusTag row) && row.Kind == "marker";

        /// <summary>是不是状态标签（单位头顶显示的那种，含同义写法）。</summary>
        public static bool IsStatus(string engineTag) => TryGet(engineTag, out StatusTag row) && row.Kind == "status";

        /// <summary>一组标签 → 去重后的状态标签机械名列表（按主标签合并、保持首次出现的顺序；机制标记与查不到的跳过）。</summary>
        public static List<string> DisplayNames(IEnumerable<string> engineTags)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (engineTags == null)
            {
                return names;
            }
            foreach (string tag in engineTags)
            {
                string canonical = Canonical(tag);
                if (!IsStatus(canonical) || !seen.Add(canonical))
                {
                    continue;
                }
                string name = NameOf(canonical);
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
            return names;
        }

        public static void Reload()
        {
            _loaded = false;
            _table = null;
            _loadError = null;
            Revision++;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                _table = ConfigSystem.Instance.Tables?.TbStatusTag;
                if (_table == null)
                {
                    _loadError = "配置表 fg.TbStatusTag 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[StatusTagCatalog] {_loadError}");
            }
        }
    }
}
