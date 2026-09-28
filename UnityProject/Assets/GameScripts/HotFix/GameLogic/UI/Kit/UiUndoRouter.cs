using System;
using GameLogic.Core;

namespace GameLogic.UI.Kit
{
    /// <summary>FG1-SIG-02（FG01 第 4 章“电路编辑器支持撤销和重做”；FG00 B02 常用操作有快捷键且可重绑）：
    /// 撤销 / 重做快捷键（fg.TbInputAction Undo = Ctrl+Z、Redo = Ctrl+Y，可重绑）交给当前打开的编辑器。
    ///
    /// 同一时刻只有一个编辑器占用（后打开的覆盖先打开的；关闭时只清自己的登记）。没有编辑器占用时不消费按键——
    /// 建造模式的撤销仍是 FG3-LOG-07 的“尚未开放”动作，由 <see cref="UiKitInputPump"/> 的保留动作提示接住，行为不变。
    /// 文本框打字时 <see cref="InputRouter"/> 整体让位，Ctrl+Z 留给文本框自己。</summary>
    public static class UiUndoRouter
    {
        private static object _owner;
        private static Func<bool> _undo;
        private static Func<bool> _redo;

        public static bool HasTarget => _owner != null;
        public static object Owner => _owner;

        /// <summary>最近一次经快捷键执行的结果（自检与冒烟读）：0 = 没执行，1 = 撤销，2 = 重做；负数 = 没有可撤销 / 可重做的步骤。</summary>
        public static int LastResult { get; private set; }

        /// <summary><see cref="LastResult"/>：按了撤销 / 重做，但编辑器上面压着别的层，没有执行。</summary>
        public const int BlockedByLayer = 3;

        /// <summary>占用者不是最上层：确认框开着，或取消栈最上面是别的层（信号核面板等）。</summary>
        public static bool IsCoveredByOtherLayer()
        {
            if (_owner == null)
            {
                return false;
            }
            if (UiConfirmDialog.IsOpen)
            {
                return true;
            }
            object top = UiEscapeStack.Top;
            return top != null && !ReferenceEquals(top, _owner);
        }

        public static void Claim(object owner, Func<bool> undo, Func<bool> redo)
        {
            _owner = owner;
            _undo = undo;
            _redo = redo;
        }

        public static void Release(object owner)
        {
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
                _undo = null;
                _redo = null;
            }
        }

        /// <summary>每帧由 <see cref="UiKitInputPump.ProcessWorldKeys"/> 调一次（在保留动作提示之前）。</summary>
        public static void Process()
        {
            if (_owner == null)
            {
                return;
            }
            // 编辑器上面还压着别的层（确认框、信号核面板……）：按键归上层，不在玩家看不见的地方改下面的草稿。
            // 键照样吃掉，免得被当成建造撤销（FG3-LOG-07）弹“尚未开放”——那条提示在这里是误导。
            if (IsCoveredByOtherLayer())
            {
                if (InputRouter.ConsumeGlobalAction(GameActionId.Undo, allowDuringModal: true)
                    || InputRouter.ConsumeGlobalAction(GameActionId.Redo, allowDuringModal: true))
                {
                    LastResult = BlockedByLayer;
                }
                return;
            }
            if (InputRouter.ConsumeGlobalAction(GameActionId.Undo, allowDuringModal: true))
            {
                LastResult = _undo != null && _undo() ? 1 : -1;
            }
            else if (InputRouter.ConsumeGlobalAction(GameActionId.Redo, allowDuringModal: true))
            {
                LastResult = _redo != null && _redo() ? 2 : -2;
            }
        }

        public static void ResetForTests()
        {
            _owner = null;
            _undo = null;
            _redo = null;
            LastResult = 0;
        }
    }
}
