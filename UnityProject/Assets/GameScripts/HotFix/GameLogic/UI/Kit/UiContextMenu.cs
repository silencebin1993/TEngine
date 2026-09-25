using System;
using System.Collections.Generic;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>右键菜单的一项。不可用的项必须给出原因（FG00 B06：任何“不能做”都要说为什么）。</summary>
    public sealed class ContextMenuItem
    {
        public string Label;
        public bool Enabled = true;
        public string DisabledReason;
        public Action Action;

        public ContextMenuItem(string label, Action action)
        {
            Label = label;
            Action = action;
        }

        public static ContextMenuItem Disabled(string label, string reason) =>
            new ContextMenuItem(label, null) { Enabled = false, DisabledReason = reason };
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 右键菜单）：对任意元素 <see cref="Attach"/> 一个菜单提供者，右键（功能动作的物理键）弹出。
    /// 不可用项置灰并在同一行写明原因；Esc、点菜单外、选中一项都会关闭；菜单画在浮层最上面。
    /// </summary>
    public static class UiContextMenu
    {
        private static readonly object Owner = new object();
        private static readonly Dictionary<VisualElement, Func<List<ContextMenuItem>>> Providers =
            new Dictionary<VisualElement, Func<List<ContextMenuItem>>>();

        private static VisualElement _scrim;
        private static VisualElement _menu;
        private static readonly List<ContextMenuItem> Items = new List<ContextMenuItem>();

        public static bool IsOpen { get; private set; }
        public static IReadOnlyList<ContextMenuItem> CurrentItems => Items;

        public static void BindView(VisualElement root)
        {
            _scrim = root.Q<VisualElement>("MenuScrim");
            _menu = root.Q<VisualElement>("ContextMenu");
            _scrim?.RegisterCallback<PointerDownEvent>(OnScrimDown);
            Render(Vector2.zero);
        }

        public static void UnbindView()
        {
            _scrim?.UnregisterCallback<PointerDownEvent>(OnScrimDown);
            _scrim = _menu = null;
        }

        public static void Attach(VisualElement target, Func<List<ContextMenuItem>> provider)
        {
            if (target == null || provider == null)
            {
                return;
            }
            if (!Providers.ContainsKey(target))
            {
                target.RegisterCallback<PointerDownEvent>(OnTargetPointerDown);
            }
            Providers[target] = provider;
        }

        private static void OnTargetPointerDown(PointerDownEvent evt)
        {
            // 右键 = 功能动作绑定的物理鼠标键（左右手互换后跟着换）。
            if (evt.button != Core.InputRouter.PhysicalButton(1))
            {
                return;
            }
            var target = evt.currentTarget as VisualElement;
            if (target != null && Providers.TryGetValue(target, out Func<List<ContextMenuItem>> provider))
            {
                Show(evt.position, provider());
                evt.StopPropagation();
            }
        }

        /// <summary>在面板坐标 <paramref name="panelPosition"/> 处打开菜单。空列表不打开。</summary>
        public static void Show(Vector2 panelPosition, List<ContextMenuItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }
            Items.Clear();
            Items.AddRange(items);
            IsOpen = true;
            UiEscapeStack.Push(Owner, Close);
            Render(panelPosition);
        }

        /// <summary>选第 <paramref name="index"/> 项（按钮点击与自检共用）。不可用的项不执行、菜单保持打开。</summary>
        public static bool Invoke(int index)
        {
            if (!IsOpen || index < 0 || index >= Items.Count || !Items[index].Enabled)
            {
                return false;
            }
            Action action = Items[index].Action;
            Close();
            action?.Invoke();
            return true;
        }

        public static void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            IsOpen = false;
            Items.Clear();
            UiEscapeStack.Remove(Owner);
            Render(Vector2.zero);
        }

        private static void OnScrimDown(PointerDownEvent evt)
        {
            if (evt.target == _scrim)
            {
                Close();
                evt.StopPropagation();
            }
        }

        private static void Render(Vector2 at)
        {
            if (_menu == null)
            {
                return;
            }
            _scrim?.EnableInClassList("uk-hidden", !IsOpen);
            _menu.EnableInClassList("uk-hidden", !IsOpen);
            _menu.Clear();
            if (!IsOpen)
            {
                return;
            }
            for (int i = 0; i < Items.Count; i++)
            {
                ContextMenuItem item = Items[i];
                int captured = i;
                string text = item.Enabled || string.IsNullOrEmpty(item.DisabledReason)
                    ? item.Label
                    : item.Label + "\n" + GameText.Format("ui.common.disabled_reason", item.DisabledReason);
                var button = new Button(() => Invoke(captured)) { text = text };
                button.AddToClassList("uk-menu-item");
                button.SetEnabled(item.Enabled);
                _menu.Add(button);
            }
            // 位置是运行时数据（鼠标所在处），允许直接写 style；靠右 / 靠下时由面板边界钳住。
            Rect bounds = _menu.parent != null ? _menu.parent.worldBound : new Rect(0, 0, 1920, 1080);
            float x = Mathf.Min(at.x, bounds.xMax - 200f);
            float y = Mathf.Min(at.y, bounds.yMax - 40f * Items.Count - 8f);
            _menu.style.left = Mathf.Max(0f, x - bounds.xMin);
            _menu.style.top = Mathf.Max(0f, y - bounds.yMin);
        }

        public static void ResetForTests()
        {
            Close();
        }
    }
}
