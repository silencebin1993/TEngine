using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// FG0-UX-01：界面调参常量的唯一入口，数据源 fg.TbUiTuning（tools/cell_tables/fgdata_ux.py 的 UI_TUNING）。
    /// 提示延迟、通知历史上限、UI 缩放范围等都从这里取，不在代码里写死。
    /// 查不到参数时抛出带参数名的异常（与 FgContentTables 同一失败策略：配置坏了不能用 0 悄悄跑）。
    /// </summary>
    public static class UiTuningValues
    {
        private static readonly Dictionary<string, float> Values = new Dictionary<string, float>(StringComparer.Ordinal);
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return Values.Count;
            }
        }

        public static bool TryGet(string id, out float value)
        {
            EnsureLoaded();
            return Values.TryGetValue(id ?? string.Empty, out value);
        }

        public static float Get(string id)
        {
            if (!TryGet(id, out float value))
            {
                throw new KeyNotFoundException($"界面调参表 fg.TbUiTuning 缺少参数 {id ?? "null"}（改 tools/cell_tables/fgdata_ux.py 后重新生成）"
                                               + (_loadError != null ? "；" + _loadError : string.Empty));
            }
            return value;
        }

        public static int GetInt(string id) => (int)Math.Round(Get(id));

        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            EnsureLoaded();
        }

        /// <summary>测试注入。null 表示模拟“表没加载上”。</summary>
        public static void OverrideForTests(IDictionary<string, float> values)
        {
            _overridden = true;
            _loaded = true;
            Values.Clear();
            _loadError = values == null ? "测试注入：界面调参表为空" : null;
            if (values != null)
            {
                foreach (KeyValuePair<string, float> kv in values)
                {
                    Values[kv.Key] = kv.Value;
                }
            }
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            Values.Clear();
            _loadError = null;
            try
            {
                TbUiTuning table = ConfigSystem.Instance.Tables?.TbUiTuning;
                if (table == null)
                {
                    _loadError = "配置表 fg.TbUiTuning 不存在";
                }
                else
                {
                    foreach (UiTuning row in table.DataList)
                    {
                        Values[row.Id] = row.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表 fg.TbUiTuning 读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[UiTuningValues] {_loadError}");
            }
            Revision++;
        }
    }
}
