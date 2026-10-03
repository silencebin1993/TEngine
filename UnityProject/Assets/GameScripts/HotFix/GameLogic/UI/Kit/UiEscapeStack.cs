using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-001）：Esc 按层级逐层返回——关闭最上层面板 → 退出当前模式 → 打开暂停菜单。
    /// 同级页面互斥；子页隐藏父页并保留状态，关闭后逐级恢复。临时浮层仍叠在当前页之上。
    /// 取消键由 <see cref="UiKitInputPump"/> 在 LateUpdate 统一处理：各玩法系统在 Update 里先消费取消键
    /// （例如战略命令“武装待命”的取消、任务日志的关闭），没人消费才轮到本栈；栈空且在区域里时打开暂停菜单。
    /// </summary>
    public static class UiEscapeStack
    {
        private sealed class Layer
        {
            public object Owner;
            public Action Close;
            public bool Blocking;
            public VisualElement View;
            public Layer Parent;
        }

        private static readonly List<Layer> Layers = new List<Layer>();
        private static readonly Dictionary<object, VisualElement> PageViews = new Dictionary<object, VisualElement>();
        private static object _requestedParent;

        /// <summary>登记整页根节点；隐藏父页不改变业务状态、滚动位置或暂停所有权。</summary>
        public static void RegisterPage(object owner, VisualElement view)
        {
            if (owner != null && view != null) PageViews[owner] = view;
        }

        public static void UnregisterPage(object owner)
        {
            Remove(owner);
            if (owner != null) PageViews.Remove(owner);
        }

        /// <summary>进入子页，返回或 Esc 关闭后恢复父页。只对本次同步打开有效。</summary>
        public static void OpenChild(object parent, Action open)
        {
            object previous = _requestedParent;
            _requestedParent = parent;
            try { open?.Invoke(); }
            finally { _requestedParent = previous; }
        }

        public static object CurrentPage
        {
            get
            {
                for (int i = Layers.Count - 1; i >= 0; i--)
                    if (Layers[i].View != null) return Layers[i].Owner;
                return null;
            }
        }

        public static bool IsPageSuspended(object owner)
        {
            Layer layer = Find(owner);
            return layer?.View != null && !ReferenceEquals(CurrentPage, owner);
        }

        private static Layer Find(object owner) => Layers.Find(layer => ReferenceEquals(layer.Owner, owner));

        private static VisualElement ResolveView(object owner)
        {
            if (PageViews.TryGetValue(owner, out VisualElement view)) return view;
            if (owner is Component component && component != null)
                return component.GetComponent<UIDocument>()?.rootVisualElement;
            return null; // 确认框、右键菜单、拖放与过渡令牌是当前页上的临时层。
        }

        private static bool IsAncestor(Layer ancestor, Layer descendant)
        {
            for (Layer current = descendant; current != null; current = current.Parent)
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }

        private static void CloseLayer(Layer layer)
        {
            if (!Layers.Contains(layer)) return;
            layer.Close?.Invoke();
            if (Layers.Contains(layer)) Remove(layer.Owner); // 保留关闭回调中新打开的同令牌临时层。
        }

        private static void RefreshPages()
        {
            object current = CurrentPage;
            foreach (Layer layer in Layers)
                if (layer.View != null)
                {
                    bool visible = ReferenceEquals(layer.Owner, current);
                    if (!visible && layer.View.panel?.focusController?.focusedElement is VisualElement focused
                        && (ReferenceEquals(focused, layer.View) || layer.View.Contains(focused))) focused.Blur();
                    layer.View.visible = visible;
                }
        }

        public static int Count => Layers.Count;

        public static void Push(object owner, Action close)
        {
            if (owner == null || close == null)
            {
                return;
            }
            Layer existing = Find(owner);
            if (existing != null)
            {
                existing.Close = close;
                if (existing.View != null)
                {
                    foreach (Layer child in Layers.ToArray())
                        if (!ReferenceEquals(child, existing) && IsAncestor(existing, child)) CloseLayer(child);
                    RefreshPages();
                }
                return;
            }
            VisualElement view = ResolveView(owner);
            if (view != null && Layers.Exists(layer => layer.Blocking))
            {
                close(); // 失败或胜利页期间不在遮罩下面打开不可见的业务页。
                return;
            }
            Layer parent = null;
            if (view != null)
            {
                parent = Find(_requestedParent);
                // 图鉴属于当前页；暂停菜单入口属于菜单的同级子页。
                if (parent == null && owner is MechanicCodexPanelUIToolkit)
                    parent = Find(CurrentPage);
                if (parent == null && !(owner is PauseMenuUIToolkit))
                    parent = Layers.Find(layer => layer.Owner is PauseMenuUIToolkit);
                foreach (Layer layer in Layers.ToArray())
                    if (layer.View != null && !IsAncestor(layer, parent)) CloseLayer(layer);
            }
            else
            {
                // 模式层没有页面根节点。保留建造等模式，只让确认框/菜单跟随当前页关闭。
                parent = Find(CurrentPage);
            }
            Layers.Add(new Layer { Owner = owner, Close = close, View = view, Parent = parent });
            RefreshPages();
        }

        /// <summary>FG0-UX-01：开关状态由别处持有的面板（Demo 的生产 / 电路板 / 解析 / 信标 / 远征面板等）每帧同步一次：
        /// 看到“开着且不在栈里”压一层，看到“关着还在栈里”移除。<paramref name="close"/> 请传缓存好的委托（不要每帧新建闭包）。
        /// O(层数)，层数是个位数。</summary>
        public static void Sync(object owner, bool open, Action close)
        {
            bool has = Contains(owner);
            if (open && !has)
            {
                Push(owner, close);
            }
            else if (!open && has)
            {
                Remove(owner);
            }
        }

        /// <summary>FG0-UX-01：不能用 Esc 关掉、也不许 Esc 穿透到暂停菜单的整页（核心被毁失败页、胜利页）。
        /// 在它上面的层照常逐层关闭；轮到它时 Esc 被吞掉，什么也不发生——玩家只能用页面上的按钮离开。</summary>
        public static void SyncBlocking(object owner, bool shown)
        {
            bool has = Contains(owner);
            if (shown && !has)
            {
                Remove(owner);
                Layers.Add(new Layer { Owner = owner, Close = null, Blocking = true });
            }
            else if (!shown && has)
            {
                Remove(owner);
            }
        }

        public static void Remove(object owner)
        {
            Layer removed = Find(owner);
            if (removed == null) return;
            // 关闭父页时一起关闭后代，清理隐藏页面持有的输入锁。
            Layer child;
            while ((child = Layers.FindLast(layer => !ReferenceEquals(layer, removed) && IsAncestor(removed, layer))) != null)
                CloseLayer(child);
            if (removed.View != null) removed.View.visible = true;
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Layers[i].Owner, owner))
                {
                    Layers.RemoveAt(i);
                }
            }
            RefreshPages();
        }

        public static bool Contains(object owner)
        {
            for (int i = 0; i < Layers.Count; i++)
            {
                if (ReferenceEquals(Layers[i].Owner, owner))
                {
                    return true;
                }
            }
            return false;
        }

        public static object Top => Layers.Count > 0 ? Layers[Layers.Count - 1].Owner : null;

        /// <summary>关闭最上层。返回是否真的关了一层。关闭回调负责把自己从栈里移除（通常经由面板自己的 Close）。</summary>
        public static bool CloseTop()
        {
            if (Layers.Count == 0)
            {
                return false;
            }
            Layer top = Layers[Layers.Count - 1];
            if (top.Blocking)
            {
                return true; // 吞掉这次 Esc：不关页面，也不打开暂停菜单。
            }
            CloseLayer(top);
            return true;
        }

        /// <summary>离开世界时清空（各面板已先各自关闭；这里兜底清掉关闭回调里没移除自己的层）。</summary>
        public static void Clear()
        {
            foreach (Layer layer in Layers)
                if (layer.View != null) layer.View.visible = true;
            Layers.Clear();
            _requestedParent = null;
        }

        public static void ResetForTests()
        {
            Clear();
            PageViews.Clear();
        }
    }
}
