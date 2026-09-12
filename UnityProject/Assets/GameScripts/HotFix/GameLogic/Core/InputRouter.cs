using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.Core
{
    /// <summary>
    /// 输入所有权域。M2-01：镜头状态决定这一帧的输入归谁，而不是各个消费者自己猜。
    /// </summary>
    public enum InputScope : byte
    {
        /// <summary>过渡中。谁都读不到输入——镜头正在移动时接受操作，玩家必然打空。</summary>
        None = 0,
        /// <summary>直控：移动、瞄准、器官技能、切换控制目标。</summary>
        Direct = 1,
        /// <summary>战略视角：平移、缩放、选择目标、下达命令。</summary>
        Strategy = 2,
    }

    /// <summary>
    /// 输入所有权的**单一真相**（M2-01）。
    ///
    /// 立这一层的直接原因是仓库里已经出现了真实的双重输入：`Tab` 同时被
    /// <c>CellPlayerController</c>（切换控制目标）和 <c>BattleOverlayUIToolkit</c>（开卡组面板）
    /// 监听，两个消费者互不知情，一次按键触发两件事。随着战略视角进来，这类冲突只会更多
    /// （平移的 WASD 与直控的 WASD 是同一组键）。
    ///
    /// 纪律：
    /// <list type="bullet">
    /// <item>玩法层与 UI 层**不得**再直接调 <c>Input.GetKeyDown</c> 读功能键，一律走本类；</item>
    /// <item>每个功能键在**同一帧只会被消费一次**——第二个消费者拿到 false，
    /// 这样"双重输入"是结构上不可能，而不是靠各处自觉；</item>
    /// <item>模态 UI（选卡、商店）打开时全局夺走输入，玩法层与镜头一起让位。</item>
    /// </list>
    ///
    /// 每帧开销 O(1)：只在真的按下某个键时才碰一次哈希集合，没有逐帧遍历。
    /// </summary>
    public static class InputRouter
    {
        /// <summary>当前输入归属。默认 <see cref="InputScope.Direct"/>，与既有单人直控行为一致。</summary>
        public static InputScope Scope { get; private set; } = InputScope.Direct;

        /// <summary>
        /// 输入是否被"抢焦点"的东西占着。两个来源**故意分开存**：
        /// 面板由 UI 层在开关时写，玩法暂停由阶段每帧同步。
        ///
        /// 合成一个字段会互相覆盖——选卡走的是 <c>CellStageFlow._paused</c> 直写、
        /// 根本不经过面板开关，而卡组/商店面板打开时 <c>_paused</c> 又是 false。
        /// 谁后写谁赢的话，总有一条路径会把另一条的状态抹掉。
        /// </summary>
        public static bool ModalUiOpen => _modalUi || _gameplayPaused;

        private static bool _modalUi;
        private static bool _gameplayPaused;
        private static int _frame = -1;
        private static readonly HashSet<KeyCode> ConsumedKeys = new HashSet<KeyCode>();

        public static void SetScope(InputScope scope)
        {
            Scope = scope;
        }

        /// <summary>模态面板开关（卡组、商店、图鉴、暂停菜单）。由 UI 层在切换面板时写。</summary>
        public static void SetModalUi(bool open)
        {
            _modalUi = open;
        }

        /// <summary>
        /// 玩法是否处于暂停。由阶段**每帧**同步——<c>_paused</c> 有选卡/商店/暂停菜单/GM 调试
        /// 多个写入点，逐个去接线必然漏一个，漏掉的那个会让输入永久卡在让位状态。
        ///
        /// M2-02 引入"战略暂停下达命令"后，这里要按暂停原因区分：那条路径必须**保留**
        /// 战略域输入，否则暂停下根本没法选单位。
        /// </summary>
        public static void SetGameplayPaused(bool paused)
        {
            _gameplayPaused = paused;
        }

        /// <summary>离开本局时复位，避免上一局的模态状态粘到下一局。</summary>
        public static void Reset()
        {
            Scope = InputScope.Direct;
            _modalUi = false;
            _gameplayPaused = false;
            _frame = -1;
            ConsumedKeys.Clear();
        }

        /// <summary>指定域这一帧是否持有输入所有权。</summary>
        public static bool Owns(InputScope scope)
        {
            return !ModalUiOpen && Scope == scope;
        }

        /// <summary>
        /// 消费一次按键按下。**同一帧同一个键只有第一个调用者拿到 true**，
        /// 这正是"不产生双重输入"这条验收从结构上成立的地方。
        /// </summary>
        public static bool ConsumeKeyDown(KeyCode key, InputScope scope)
        {
            if (!Owns(scope) || !Input.GetKeyDown(key))
            {
                return false;
            }

            SyncFrame();
            return ConsumedKeys.Add(key);
        }

        /// <summary>
        /// 消费一次全局按键（不受 <see cref="Scope"/> 限制，但仍受模态 UI 与同帧唯一性约束）。
        /// 给面板开关这类"任何视角下都该响应"的键用。
        /// </summary>
        public static bool ConsumeGlobalKeyDown(KeyCode key, bool allowDuringModal = false)
        {
            if ((ModalUiOpen && !allowDuringModal) || !Input.GetKeyDown(key))
            {
                return false;
            }

            SyncFrame();
            return ConsumedKeys.Add(key);
        }

        /// <summary>持续按住类输入（移动、平移）。不做同帧唯一性——持续键本来就允许多方读，
        /// 互斥由 <see cref="Scope"/> 保证：Direct 与 Strategy 不可能同时成立。</summary>
        public static bool GetKey(KeyCode key, InputScope scope)
        {
            return Owns(scope) && Input.GetKey(key);
        }

        /// <summary>指针位置。过渡期间一律不给——镜头在动，屏幕坐标反投影出来的世界点没有意义。</summary>
        public static bool TryGetPointer(InputScope scope, out Vector3 screenPosition)
        {
            if (Owns(scope))
            {
                screenPosition = Input.mousePosition;
                return true;
            }

            screenPosition = Vector3.zero;
            return false;
        }

        /// <summary>滚轮增量。缩放归战略视角，直控下不改视距。</summary>
        public static float GetScrollDelta(InputScope scope)
        {
            return Owns(scope) ? Input.mouseScrollDelta.y : 0f;
        }

        private static void SyncFrame()
        {
            if (_frame != Time.frameCount)
            {
                _frame = Time.frameCount;
                ConsumedKeys.Clear();
            }
        }
    }
}
