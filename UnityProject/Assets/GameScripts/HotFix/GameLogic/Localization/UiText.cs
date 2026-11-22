using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.UIElements;

namespace GameLogic.Localization
{
    /// <summary>
    /// FG15-UX-03（FGR-ARC-006 / FGT-UX-002）：UXML 静态文本本地化。UXML 里玩家可见的 text / label / tooltip 写成
    /// <c>@文本键</c>（例：<c>text="@uxml.shell.new_game"</c>），面板拿到根节点（或克隆完模板）后调一次 <see cref="Localize"/>，
    /// 把整棵树里以 @ 开头的值换成当前语言文本；键记在元素上，<see cref="RefreshAll"/>（切换语言时由 GameSettings.SetLanguage 调）
    /// 按新语言重写所有还活着的根。代码随后用 .text = … 动态改写的元素不受影响（只替换仍以 @ 开头、或本表登记过的元素）。
    /// 缺键走 GameText 的 ⟦key⟧ 标记，不静默。
    /// </summary>
    public static class UiText
    {
        public const char KeyPrefix = '@';

        private sealed class Keys
        {
            public string Text;
            public string Label;
            public string Tooltip;
        }

        private static readonly ConditionalWeakTable<VisualElement, Keys> _keys = new ConditionalWeakTable<VisualElement, Keys>();
        private static readonly List<WeakReference<VisualElement>> _roots = new List<WeakReference<VisualElement>>();

        /// <summary>本次运行里替换过的元素数（自检用）。</summary>
        public static int LocalizedCount { get; private set; }

        public static bool IsKey(string value) => !string.IsNullOrEmpty(value) && value.Length > 1 && value[0] == KeyPrefix;

        /// <summary>替换 root 及其全部后代里的 @键；同一个 root 重复调用无副作用。</summary>
        public static void Localize(VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            bool known = false;
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (!_roots[i].TryGetTarget(out VisualElement r))
                {
                    _roots.RemoveAt(i);
                }
                else if (r == root)
                {
                    known = true;
                }
            }
            if (!known)
            {
                _roots.Add(new WeakReference<VisualElement>(root));
            }
            Apply(root, false);
            root.Query<VisualElement>().ForEach(e => Apply(e, false));
        }

        /// <summary>切换语言后重写所有登记过的元素（只重写当初来自 @键、之后没被代码改写过的值）。</summary>
        /// <summary>
        /// 只替换不登记根：给克隆出来、随后挂到已 <see cref="Localize"/> 过的面板根下的行模板用（切语言时由那个根的 RefreshAll 一并重写）；
        /// 也给每次刷新都重新克隆的子树用，避免往根表里堆弱引用。
        /// </summary>
        public static void LocalizeRow(VisualElement row)
        {
            if (row == null)
            {
                return;
            }
            Apply(row, false);
            row.Query<VisualElement>().ForEach(e => Apply(e, false));
        }

        public static void RefreshAll()
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (!_roots[i].TryGetTarget(out VisualElement r))
                {
                    _roots.RemoveAt(i);
                    continue;
                }
                Apply(r, true);
                r.Query<VisualElement>().ForEach(e => Apply(e, true));
            }
        }

        private static void Apply(VisualElement e, bool refresh)
        {
            Keys keys = _keys.TryGetValue(e, out Keys k) ? k : null;
            if (e is TextElement te && e.GetFirstAncestorOfType<TextField>() == null) // 输入框里是玩家打的字，不当键
            {
                Swap(te.text, ref keys, e, 0, v => te.text = v, refresh);
            }
            if (e is Foldout fo)
            {
                Swap(fo.text, ref keys, e, 0, v => fo.text = v, refresh);
            }
            if (e is Toggle tg)
            {
                Swap(tg.text, ref keys, e, 0, v => tg.text = v, refresh);
            }
            if (e is BaseField<string> sf)
            {
                Swap(sf.label, ref keys, e, 1, v => sf.label = v, refresh);
            }
            else if (e is BaseField<bool> bf)
            {
                Swap(bf.label, ref keys, e, 1, v => bf.label = v, refresh);
            }
            else if (e is BaseField<int> inf)
            {
                Swap(inf.label, ref keys, e, 1, v => inf.label = v, refresh);
            }
            else if (e is BaseField<float> ff)
            {
                Swap(ff.label, ref keys, e, 1, v => ff.label = v, refresh);
            }
            Swap(e.tooltip, ref keys, e, 2, v => e.tooltip = v, refresh);
            if (!refresh && keys?.Tooltip != null && !UI.Kit.UiTooltip.IsAttached(e))
            {
                // FG15-UX-03（DEBT-FG0UX01-05 / FG-GAP-003）：UXML 写的 tooltip="@键" 走运行时悬停提示（运行时面板不显示原生 tooltip）。
                // 显示那一刻现取标题与正文，切换语言后 RefreshAll 已改写 e.tooltip，提示跟着变。
                UI.Kit.UiTooltip.Attach(e, () => new UI.Kit.TooltipContent { Title = VisibleName(e), Body = e.tooltip });
            }
        }

        /// <summary>提示标题：按钮 / 开关的字、字段的标签；没有字的图标按钮为空（只显示正文）。</summary>
        private static string VisibleName(VisualElement e)
        {
            string s = e is BaseField<string> sf ? sf.label : e is Toggle tg ? tg.text : e is TextElement te ? te.text : null;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static void Swap(string current, ref Keys keys, VisualElement e, int slot, Action<string> set, bool refresh)
        {
            // 刷新时只重写登记过的键：之后才出现的“@开头”文字（玩家起的名字等）原样保留（修复轮，审查 P2）。
            if (!refresh && IsKey(current))
            {
                string key = current.Substring(1);
                if (keys == null)
                {
                    keys = new Keys();
                    _keys.Add(e, keys);
                }
                if (slot == 0) keys.Text = key;
                else if (slot == 1) keys.Label = key;
                else keys.Tooltip = key;
                set(GameText.Get(key));
                LocalizedCount++;
                return;
            }
            if (!refresh || keys == null)
            {
                return;
            }
            string known = slot == 0 ? keys.Text : slot == 1 ? keys.Label : keys.Tooltip;
            if (known == null)
            {
                return;
            }
            // 只重写仍是“某语言下这个键的文本”的值：代码改写过的动态文本保持原样。
            if (current == GameText.Get(known, GameLanguage.ZhCn) || current == GameText.Get(known, GameLanguage.En))
            {
                set(GameText.Get(known));
            }
        }
    }
}
