using System;
using System.Collections.Generic;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-001）：Esc 按层级逐层返回——关闭最上层面板 → 退出当前模式 → 打开暂停菜单。
    /// UI 基础件（确认框、右键菜单、按键面板、通知中心、暂停菜单……）打开时压一层，关闭时弹出。
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
        }

        private static readonly List<Layer> Layers = new List<Layer>();

        public static int Count => Layers.Count;

        public static void Push(object owner, Action close)
        {
            if (owner == null || close == null)
            {
                return;
            }
            Remove(owner);
            Layers.Add(new Layer { Owner = owner, Close = close });
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
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Layers[i].Owner, owner))
                {
                    Layers.RemoveAt(i);
                }
            }
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
            Layers.RemoveAt(Layers.Count - 1);
            top.Close();
            return true;
        }

        /// <summary>离开世界时清空（各面板已先各自关闭；这里兜底清掉关闭回调里没移除自己的层）。</summary>
        public static void Clear() => Layers.Clear();

        public static void ResetForTests() => Layers.Clear();
    }
}
