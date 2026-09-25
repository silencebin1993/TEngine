using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>一个玩家动作的静态定义（来自 fg.TbInputAction 一行）。</summary>
    public sealed class InputActionDef
    {
        public GameActionId Action;
        public string NameKey;
        public string CategoryKey;
        public InputContext Contexts;
        public InputChord DefaultChord;
        public InputActionKind Kind;
        /// <summary>必须保留按键：覆盖冲突时不能把它变成未绑定（取消 / 主动作）。</summary>
        public bool Required;
        public InputActionStatus Status;
        /// <summary>reserved 时的承接 Story（只给开发看，不显示给玩家）。</summary>
        public string Owner;
        public int SortOrder;

        /// <summary>默认键是鼠标按键的动作（主动作 / 功能动作）只能重绑到鼠标按键：世界层按下 / 抬起要成对读，
        /// 键盘键没有“抬起”读法（<see cref="IInputReader"/> 只有 GetKey / GetKeyDown）。</summary>
        public bool MouseOnly => VirtualKeys.IsMouseButton(DefaultChord.Key);

        public string DisplayName => GameText.Get(NameKey);
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-012 / FG13 第 5 节）：全部玩家动作的唯一登记表，数据源 fg.TbInputAction
    /// （tools/cell_tables/fgdata_ux.py）。默认键、上下文、是否已接入玩法都只在这里；
    /// <see cref="InputBindingSet"/> 只存玩家改过的键，其余一律按本表的默认键解析。
    ///
    /// 失败策略（IC-REQ-013）：表缺失或某行解析不了时记 Error 并把原因放进 <see cref="LoadError"/> /
    /// <see cref="ValidationErrors"/>；自检把它们当失败。解析不了的行不进表（该动作表现为未绑定），
    /// 不用猜一个默认键悄悄跑下去。
    /// </summary>
    public static class InputActionCatalog
    {
        private const int Capacity = 256;

        private static readonly InputActionDef[] ByAction = new InputActionDef[Capacity];
        private static readonly List<InputActionDef> Ordered = new List<InputActionDef>();
        private static readonly List<string> Errors = new List<string>();
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        /// <summary>每次重载 / 测试注入 +1；缓存了定义的地方据此判断是否需要重建。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        /// <summary>逐行解析时发现的问题（未知动作名、按键名、上下文……）。正常为空。</summary>
        public static IReadOnlyList<string> ValidationErrors
        {
            get
            {
                EnsureLoaded();
                return Errors;
            }
        }

        /// <summary>按 sortOrder 排好的全部动作。</summary>
        public static IReadOnlyList<InputActionDef> All
        {
            get
            {
                EnsureLoaded();
                return Ordered;
            }
        }

        public static bool TryGet(GameActionId action, out InputActionDef def)
        {
            EnsureLoaded();
            int index = (int)action;
            def = index >= 0 && index < Capacity ? ByAction[index] : null;
            return def != null;
        }

        public static InputChord DefaultChord(GameActionId action) =>
            TryGet(action, out InputActionDef def) ? def.DefaultChord : InputChord.Unbound;

        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            EnsureLoaded();
        }

        /// <summary>测试注入：用构造出来的定义替换真实表。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(IEnumerable<InputActionDef> defs, string loadError = null)
        {
            _overridden = true;
            _loaded = true;
            Clear();
            _loadError = loadError;
            if (defs != null)
            {
                foreach (InputActionDef def in defs)
                {
                    Add(def);
                }
            }
            Sort();
            Revision++;
        }

        public static void ResetForTests() => Reload();

        /// <summary>把表行解析成定义。独立成公开方法，自检可以用它验证“坏行会被拒绝并给出原因”。</summary>
        public static bool TryParseRow(InputAction row, out InputActionDef def, out string error)
        {
            def = null;
            error = null;
            if (row == null)
            {
                error = "空行";
                return false;
            }
            if (!Enum.TryParse(row.Id, false, out GameActionId action) || !Enum.IsDefined(typeof(GameActionId), action))
            {
                error = $"动作 {row.Id} 不是 GameActionId 的成员";
                return false;
            }
            if (!TryParseContexts(row.Contexts, out InputContext contexts))
            {
                error = $"动作 {row.Id} 的上下文 “{row.Contexts}” 无法解析";
                return false;
            }
            if (!VirtualKeys.TryParse(row.DefaultBinding, out KeyCode key))
            {
                error = $"动作 {row.Id} 的默认键 “{row.DefaultBinding}” 不是 KeyCode";
                return false;
            }
            if (!InputChord.TryParseMods(row.DefaultMods, out InputModifier mods))
            {
                error = $"动作 {row.Id} 的修饰键 “{row.DefaultMods}” 无法解析";
                return false;
            }
            InputActionKind kind;
            switch (row.Kind)
            {
                case "press": kind = InputActionKind.Press; break;
                case "hold": kind = InputActionKind.Hold; break;
                default:
                    error = $"动作 {row.Id} 的 kind “{row.Kind}” 无法解析";
                    return false;
            }
            InputActionStatus status;
            switch (row.Status)
            {
                case "wired": status = InputActionStatus.Wired; break;
                case "reserved": status = InputActionStatus.Reserved; break;
                default:
                    error = $"动作 {row.Id} 的 status “{row.Status}” 无法解析";
                    return false;
            }
            def = new InputActionDef
            {
                Action = action,
                NameKey = row.NameKey,
                CategoryKey = row.CategoryKey,
                Contexts = contexts,
                DefaultChord = new InputChord(key, mods),
                Kind = kind,
                Required = row.Required == 1,
                Status = status,
                Owner = row.Owner,
                SortOrder = row.SortOrder,
            };
            return true;
        }

        public static bool TryParseContexts(string text, out InputContext contexts)
        {
            contexts = InputContext.None;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            foreach (string raw in text.Split(','))
            {
                switch (raw.Trim())
                {
                    case "strategy": contexts |= InputContext.Strategy; break;
                    case "build": contexts |= InputContext.Build; break;
                    case "uplink": contexts |= InputContext.Uplink; break;
                    case "interface": contexts |= InputContext.Interface; break;
                    default: return false;
                }
            }
            return contexts != InputContext.None;
        }

        /// <summary>上下文的玩家可见名（“战略、接入”）。</summary>
        public static string ContextsDisplay(InputContext contexts)
        {
            var parts = new List<string>(4);
            if ((contexts & InputContext.Strategy) != 0) parts.Add(GameText.Get("input.context.strategy"));
            if ((contexts & InputContext.Build) != 0) parts.Add(GameText.Get("input.context.build"));
            if ((contexts & InputContext.Uplink) != 0) parts.Add(GameText.Get("input.context.uplink"));
            if ((contexts & InputContext.Interface) != 0) parts.Add(GameText.Get("input.context.interface"));
            return string.Join("、", parts);
        }

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            Clear();
            _loadError = null;
            try
            {
                TbInputAction table = ConfigSystem.Instance.Tables?.TbInputAction;
                if (table == null)
                {
                    _loadError = "配置表 fg.TbInputAction 不存在";
                }
                else
                {
                    foreach (InputAction row in table.DataList)
                    {
                        if (TryParseRow(row, out InputActionDef def, out string error))
                        {
                            if (!Add(def))
                            {
                                Errors.Add($"动作 {row.Id} 在表里出现了两次");
                            }
                        }
                        else
                        {
                            Errors.Add(error);
                        }
                    }
                    Sort();
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表 fg.TbInputAction 读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[InputActionCatalog] {_loadError}");
            }
            foreach (string error in Errors)
            {
                Log.Error($"[InputActionCatalog] {error}");
            }
            Revision++;
        }

        private static void Clear()
        {
            Array.Clear(ByAction, 0, ByAction.Length);
            Ordered.Clear();
            Errors.Clear();
        }

        private static bool Add(InputActionDef def)
        {
            int index = (int)def.Action;
            if (index < 0 || index >= Capacity || ByAction[index] != null)
            {
                return false;
            }
            ByAction[index] = def;
            Ordered.Add(def);
            return true;
        }

        private static void Sort()
        {
            Ordered.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : ((int)a.Action).CompareTo((int)b.Action));
        }
    }
}
