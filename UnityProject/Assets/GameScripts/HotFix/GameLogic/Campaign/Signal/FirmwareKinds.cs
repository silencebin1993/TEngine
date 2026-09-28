using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Content;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Signal
{
    /// <summary>固件种类（FG02 FGR-FW-001“种类：常规 / 核心”、FGR-FW-003）。</summary>
    public enum FirmwareKind : byte
    {
        /// <summary>不是固件（例如 3×3 电路板用的基元芯片“聚焦镜”），不能放进信号核。</summary>
        NotFirmware = 0,
        /// <summary>常规固件：可以装在机器上，也可以放进信号核（FGR-SIG-012 第 3 条）。</summary>
        Regular = 1,
        /// <summary>核心固件：只能由信号携带，放不进机器电路（FGR-SIG-012）。</summary>
        Core = 2,
    }

    /// <summary>
    /// FG1-SIG-01：固件种类的唯一真相——fg.TbFirmwareKind（数据源 tools/cell_tables/fgdata_signal.py）。
    /// 机器电路的两个入口（蓝图固件槽 <see cref="Blueprint.BlueprintCircuitBoard.TrySetFirmware"/>、3×3 电路格
    /// <see cref="Blueprint.BlueprintCircuitBoard.TryPlaceChip"/>、保存校验）与信号核都只读这里，不按固件 ID 写特例（IC-REQ-012）。
    ///
    /// 是不是固件：内容在 <see cref="FirmwareCatalog"/> 里。表里查不到种类的固件按“常规”处理并记 Error
    /// （自检保证目录里每条固件都有一行，漏登记是开发错误，不能让玩家卡住）。
    /// 名称：表里的 nameKey → <see cref="GameText"/>；表不可用时回退 Demo 目录的 DisplayName。
    /// </summary>
    public static class FirmwareKinds
    {
        private static TbFirmwareKind _table;
        private static bool _loaded;
        private static string _loadError;
        private static Dictionary<string, FirmwareKind> _override;
        private static readonly HashSet<string> WarnedMissing = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>重载 / 测试注入时 +1，界面据此刷新。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        /// <summary>表里的全部行（自检比对源数据用）。表不可用时为空。</summary>
        public static IReadOnlyList<GameConfig.fg.FirmwareKind> Rows
        {
            get
            {
                EnsureLoaded();
                return _table?.DataList ?? (IReadOnlyList<GameConfig.fg.FirmwareKind>)Array.Empty<GameConfig.fg.FirmwareKind>();
            }
        }

        public static bool IsFirmware(string contentId) =>
            !string.IsNullOrEmpty(contentId) && FirmwareCatalog.TryGet(contentId, out _);

        public static FirmwareKind KindOf(string contentId)
        {
            if (!IsFirmware(contentId))
            {
                return FirmwareKind.NotFirmware;
            }
            if (_override != null)
            {
                return _override.TryGetValue(contentId, out FirmwareKind forced) ? forced : FirmwareKind.Regular;
            }
            EnsureLoaded();
            if (_table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null)
            {
                return row.Kind == "core" ? FirmwareKind.Core : FirmwareKind.Regular;
            }
            if (WarnedMissing.Add(contentId))
            {
                Log.Error($"[FirmwareKinds] 固件 {contentId} 在 fg.TbFirmwareKind 里没有种类（改 tools/cell_tables/fgdata_signal.py 后重新生成），暂按常规处理。");
            }
            return FirmwareKind.Regular;
        }

        public static bool IsCore(string contentId) => KindOf(contentId) == FirmwareKind.Core;

        /// <summary>
        /// FG1-SIG-05（FGR-SIG-090）：AI 永远不使用核心固件。机器电路自己的固件里，只有这里返回 true 的才参与 AI 驾驶时的编译
        /// （反应、热量、伤害）；核心固件只能经信号带进接入口。空串 / null 返回 false。
        /// </summary>
        public static bool IsAiUsable(string contentId) => !string.IsNullOrEmpty(contentId) && !IsCore(contentId);

        /// <summary>FG1-SIG-05：从机器电路的固件里剔掉核心固件（顺序不变，空位丢弃）——AI 驾驶时真正生效的那几枚。</summary>
        public static string[] AiUsable(IEnumerable<string> firmwareIds)
        {
            if (firmwareIds == null)
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(2);
            foreach (string id in firmwareIds)
            {
                if (IsAiUsable(id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        /// <summary>FG1-SIG-05：机器电路里残留的核心固件（旧草稿 / 旧档）——AI 不用它们，界面据此说明原因。</summary>
        public static string[] InertCore(IEnumerable<string> firmwareIds)
        {
            if (firmwareIds == null)
            {
                return Array.Empty<string>();
            }
            var list = new List<string>(1);
            foreach (string id in firmwareIds)
            {
                if (!string.IsNullOrEmpty(id) && IsCore(id))
                {
                    list.Add(id);
                }
            }
            return list.ToArray();
        }

        /// <summary>FG1-SIG-05（FGR-FW-003）：表里种类为核心的全部固件 ID（含固件本体尚未进目录、由 FG2-FW-01 补上的 4 条）。
        /// 表不可用时为空。check_luban R22 保证它恰好是 6 条核心固件名单。</summary>
        public static IReadOnlyList<string> CoreRosterIds
        {
            get
            {
                EnsureLoaded();
                var ids = new List<string>(6);
                if (_table != null)
                {
                    foreach (GameConfig.fg.FirmwareKind row in _table.DataList)
                    {
                        if (row != null && row.Kind == "core")
                        {
                            ids.Add(row.Id);
                        }
                    }
                }
                return ids;
            }
        }

        /// <summary>FG1-SIG-03（FGR-SIG-033）：核心固件发动后的冷却秒数（表 cooldown 列，按游戏时间）。不是核心固件、表里没有这一行时为 0
        /// （常规固件没有“发动”，也就没有冷却）。测试注入种类时冷却仍读正式表，改表不需要改代码。</summary>
        public static float CoreCooldownSeconds(string contentId)
        {
            if (!IsCore(contentId))
            {
                return 0f;
            }
            EnsureLoaded();
            return _table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null
                ? Math.Max(0f, row.Cooldown)
                : 0f;
        }

        /// <summary>玩家可见的固件名（文本键）；不是固件时返回 null。</summary>
        public static string DisplayName(string contentId)
        {
            if (!FirmwareCatalog.TryGet(contentId, out MechanicalContentDef def))
            {
                return null;
            }
            EnsureLoaded();
            if (_table != null && _table.DataMap.TryGetValue(contentId, out GameConfig.fg.FirmwareKind row) && row != null
                && !string.IsNullOrEmpty(row.NameKey))
            {
                return GameText.Get(row.NameKey);
            }
            return def.DisplayName;
        }

        /// <summary>“◆ 核心 / ● 常规 / ■ 基元芯片”——形状 + 文字，不只靠颜色（FG00 B15）。</summary>
        public static string KindLabel(FirmwareKind kind) => GameText.Get(kind switch
        {
            FirmwareKind.Core => "signal.core.kind.core",
            FirmwareKind.Regular => "signal.core.kind.regular",
            _ => "signal.core.kind.chip",
        });

        public static string KindTip(FirmwareKind kind) => GameText.Get(kind switch
        {
            FirmwareKind.Core => "signal.core.kind.core_tip",
            FirmwareKind.Regular => "signal.core.kind.regular_tip",
            _ => "signal.core.kind.chip_tip",
        });

        public static void Reload()
        {
            _loaded = false;
            _table = null;
            _loadError = null;
            WarnedMissing.Clear();
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：用给定的“固件 ID → 种类”替换表（没列出的固件按常规）。用完必须 <see cref="ResetForTests"/>。
        /// FG1-SIG-05 起正式表里过载、标记跳转已是核心；注入只用于“种类改变时规则跟着变”的对照与内核机制回归。</summary>
        public static void OverrideForTests(IReadOnlyDictionary<string, FirmwareKind> kinds)
        {
            _override = kinds == null ? null : new Dictionary<string, FirmwareKind>(kinds, StringComparer.Ordinal);
            Revision++;
        }

        public static void ResetForTests()
        {
            _override = null;
            Reload();
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
                _table = ConfigSystem.Instance.Tables?.TbFirmwareKind;
                if (_table == null)
                {
                    _loadError = "配置表 fg.TbFirmwareKind 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[FirmwareKinds] {_loadError}");
            }
        }
    }
}
