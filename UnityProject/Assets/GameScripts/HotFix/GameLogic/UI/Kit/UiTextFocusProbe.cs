using System.Collections.Generic;
using GameLogic.Core;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-012）：“文本框有焦点时快捷键让位”的全局判定。
    /// 不要求每个文本框各自接 FocusIn / FocusOut（Demo 的电路板蓝图命名框就没接）：登记过的 UI Toolkit 面板里，
    /// 当前拿着键盘焦点的元素（或它的祖先）是可编辑的文本输入框，就视为正在打字，<see cref="InputRouter.KeyboardSuppressed"/> 为真。
    /// 项目里的 UI Toolkit 文档共用 <c>BattleHudPanelSettings</c>，实际只有一个面板；这里按面板去重，个位数循环。
    /// </summary>
    public static class UiTextFocusProbe
    {
        private static readonly List<VisualElement> Roots = new List<VisualElement>();
        private static readonly List<IPanel> Seen = new List<IPanel>(4);
        private static bool _installed;

        /// <summary>登记一个面板根（面板宿主就绪时调）。首次登记时把探针装到 <see cref="InputRouter"/>。</summary>
        public static void Register(VisualElement root)
        {
            if (root == null)
            {
                return;
            }
            if (!_installed)
            {
                _installed = true;
                InputRouter.SetTextFocusProbe(AnyTextInputFocused);
            }
            for (int i = Roots.Count - 1; i >= 0; i--)
            {
                if (Roots[i] == null || ReferenceEquals(Roots[i], root))
                {
                    Roots.RemoveAt(i);
                }
            }
            Roots.Add(root);
        }

        public static void Unregister(VisualElement root)
        {
            Roots.Remove(root);
        }

        /// <summary>任一登记面板里，拿着焦点的是可编辑文本框。</summary>
        public static bool AnyTextInputFocused()
        {
            Seen.Clear();
            for (int i = 0; i < Roots.Count; i++)
            {
                IPanel panel = Roots[i]?.panel;
                if (panel == null || Seen.Contains(panel))
                {
                    continue;
                }
                Seen.Add(panel);
                if (IsEditableText(panel.focusController?.focusedElement as VisualElement))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>让拿着焦点的文本框交出焦点（玩家点回世界时调用）。返回是否真的失焦了一个。</summary>
        public static bool BlurFocusedText()
        {
            bool blurred = false;
            Seen.Clear();
            for (int i = 0; i < Roots.Count; i++)
            {
                IPanel panel = Roots[i]?.panel;
                if (panel == null || Seen.Contains(panel))
                {
                    continue;
                }
                Seen.Add(panel);
                if (panel.focusController?.focusedElement is VisualElement focused && IsEditableText(focused))
                {
                    focused.Blur();
                    blurred = true;
                }
            }
            return blurred;
        }

        /// <summary>元素或它的祖先是可编辑的文本输入框（TextField 及其它 TextInputBaseField 派生类；只读的不算）。</summary>
        public static bool IsEditableText(VisualElement element)
        {
            for (VisualElement e = element; e != null; e = e.parent)
            {
                if (e is TextField textField)
                {
                    return !textField.isReadOnly && textField.enabledInHierarchy;
                }
                if (e.ClassListContains(TextField.ussClassName) || e.ClassListContains("unity-base-text-field"))
                {
                    return e.enabledInHierarchy;
                }
            }
            return false;
        }

        public static void ResetForTests()
        {
            Roots.Clear();
            Seen.Clear();
        }
    }
}
