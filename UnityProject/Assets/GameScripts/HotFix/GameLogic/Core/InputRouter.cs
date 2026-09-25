using System;
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

        /// <summary>硬件输入后端。默认真读 <see cref="UnityEngine.Input"/>；
        /// <see cref="DebugSetReader"/> 供测试注入按帧回放实现（`DEBT-M4R02-INPUT-SIM-01`）。</summary>
        public static IInputReader Reader { get; private set; } = UnityInputReader.Instance;

        /// <summary>
        /// 输入是否被"抢焦点"的东西占着。两个来源**故意分开存**：
        /// 面板由 UI 层在开关时写，玩法暂停由阶段每帧同步。
        ///
        /// 合成一个字段会互相覆盖——选卡走的是 <c>CellStageFlow._paused</c> 直写、
        /// 根本不经过面板开关，而卡组/商店面板打开时 <c>_paused</c> 又是 false。
        /// 谁后写谁赢的话，总有一条路径会把另一条的状态抹掉。
        /// </summary>
        public static bool ModalUiOpen => _modalUi || ModalOwners.Count > 0 || _gameplayPaused;

        private static bool _modalUi;
        /// <summary>FG0-UX-01：UI 基础件（确认框、按键面板、暂停菜单、通知中心）各自登记的模态占用。
        /// 与旧的单个布尔 <see cref="_modalUi"/> 分开存：多个面板叠开时，关掉上面一个不会把下面那个的占用抹掉。</summary>
        private static readonly HashSet<object> ModalOwners = new HashSet<object>();
        private static bool _buildMode;
        private static bool _textInputFocused;
        private static bool _gameplayPaused;
        private static bool _strategicPause;
        private static Func<bool> _uiPointerBlocker;
        private static readonly HashSet<int> UiCapturedPointers = new HashSet<int>();
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

        /// <summary>FG0-UX-01：按占用者登记 / 释放模态（可叠加，互不覆盖）。</summary>
        public static void PushModal(object owner)
        {
            if (owner != null)
            {
                ModalOwners.Add(owner);
            }
        }

        public static void PopModal(object owner)
        {
            if (owner != null)
            {
                ModalOwners.Remove(owner);
            }
        }

        public static bool IsModalOwner(object owner) => owner != null && ModalOwners.Contains(owner);

        /// <summary>FG0-UX-01（FGR-ARC-012）：建造模式开关。战略视角下开着建造模式时，生效上下文是“建造”。
        /// 建造模式本身由格网建造（FG3-LOG-01 / FG0-ARCH-04）进入；本 Story 只提供上下文切换点。</summary>
        public static void SetBuildMode(bool on)
        {
            _buildMode = on;
        }

        public static bool BuildMode => _buildMode;

        /// <summary>FG0-UX-01：文本框（搜索框、改名框）获得键盘焦点时，所有快捷键让位，打字不会触发玩法动作。
        /// 由 UI 基础件的搜索框在 FocusIn / FocusOut 时写。</summary>
        public static void SetTextInputFocused(bool focused)
        {
            _textInputFocused = focused;
        }

        /// <summary>文本输入是否占着键盘：显式登记的搜索框，或由 UI 层探针查到的任意可编辑文本框
        /// （例如电路板的蓝图命名框——不必每个 Demo 文本框各自接 FocusIn / FocusOut）。</summary>
        public static bool TextInputFocused => _textInputFocused || (_textFocusProbe != null && _textFocusProbe());

        private static Func<bool> _textFocusProbe;

        /// <summary>FG0-UX-01：注册“当前是否有可编辑文本框拿着键盘焦点”的探针（UI Toolkit 层实现，核心输入层不依赖 UI 框架）。</summary>
        public static void SetTextFocusProbe(Func<bool> probe)
        {
            _textFocusProbe = probe;
        }

        private static int _swallowFrame = -1;

        /// <summary>FG0-UX-01：本帧键盘整体让位——文本框用 Esc 失焦的那一帧，Esc 只失焦，不再被 Esc 栈拿去关面板。</summary>
        public static void SwallowKeyboardThisFrame()
        {
            _swallowFrame = Time.frameCount;
        }

        private static bool _captureActive;
        private static int _captureReleasedFrame = -1;

        /// <summary>FG0-UX-01：改键采集进行中（按键面板 / 主菜单在等玩家按新键）。采集期间所有动作让位；
        /// 采集结束的那一帧也继续让位——否则用来结束采集的那个键（例如 Esc 取消）会在同一帧被别的系统再读一次。</summary>
        public static void SetRebindCapture(bool active)
        {
            if (_captureActive && !active)
            {
                _captureReleasedFrame = Time.frameCount;
            }
            _captureActive = active;
        }

        /// <summary>键盘快捷键当前是否整体让位（文本框有焦点、正在改键、或改键刚在本帧结束）。</summary>
        public static bool KeyboardSuppressed =>
            TextInputFocused || _captureActive || (_captureReleasedFrame >= 0 && Time.frameCount <= _captureReleasedFrame)
            || (_swallowFrame >= 0 && Time.frameCount <= _swallowFrame);

        /// <summary>
        /// FG0-UX-01（FGR-ARC-012）：当前生效的输入上下文。
        /// 模态界面（含非战略暂停）→ 界面；过渡中 → 无；接入（直控）视角 → 接入；战略视角 → 战略，开着建造模式时为建造。
        /// </summary>
        public static InputContext ActiveContext
        {
            get
            {
                if (_modalUi || ModalOwners.Count > 0 || (_gameplayPaused && !_strategicPause))
                {
                    return InputContext.Interface;
                }
                switch (Scope)
                {
                    case InputScope.Direct: return InputContext.Uplink;
                    case InputScope.Strategy: return _buildMode ? InputContext.Build : InputContext.Strategy;
                    default: return InputContext.None;
                }
            }
        }

        /// <summary>
        /// 由 UI Toolkit 注册的指针命中检测。核心输入层不依赖具体 UI 框架，只询问“这一帧鼠标是否落在可见 UI 上”。
        /// </summary>
        public static void SetUiPointerBlocker(Func<bool> blocker)
        {
            _uiPointerBlocker = blocker;
        }

        /// <summary>是否有 UI 正在拖动并独占指针。拖动跨出窗口范围后仍保持 true，直到释放。</summary>
        public static bool UiPointerCaptured => UiCapturedPointers.Count > 0;

        /// <summary>UI 标题栏开始拖动时调用；只锁指针，不改变键盘输入域。</summary>
        public static void CaptureUiPointer(int pointerId)
        {
            UiCapturedPointers.Add(pointerId);
        }

        /// <summary>UI 指针释放或意外失去捕获时调用。</summary>
        public static void ReleaseUiPointer(int pointerId)
        {
            UiCapturedPointers.Remove(pointerId);
        }

        /// <summary>世界层是否必须让出鼠标：指针命中任意可见 UI，或正在拖动 UI 窗口。</summary>
        public static bool IsUiPointerBlocked()
        {
            return UiPointerCaptured || (_uiPointerBlocker?.Invoke() ?? false);
        }

        /// <summary>
        /// 玩法是否处于暂停。由阶段**每帧**同步——<c>_paused</c> 有选卡/商店/暂停菜单/GM 调试
        /// 多个写入点，逐个去接线必然漏一个，漏掉的那个会让输入永久卡在让位状态。
        /// </summary>
        /// <param name="strategic">
        /// M2-02 战略暂停：玩法冻结，但**保留战略域输入**。
        /// 「暂停下选择单位、排队下令」正是战略暂停存在的理由，一刀切夺走输入等于取消这个功能。
        /// 普通暂停（选卡、商店、暂停菜单）传 false，它们都伴随模态面板，本来就该全部让位。
        /// </param>
        public static void SetGameplayPaused(bool paused, bool strategic = false)
        {
            _gameplayPaused = paused;
            _strategicPause = paused && strategic;
        }

        /// <summary>当前是否处于战略暂停（玩法冻结但可以继续选人下令）。</summary>
        public static bool StrategicPause => _strategicPause;

        /// <summary>离开本局时复位，避免上一局的模态状态粘到下一局。</summary>
        public static void Reset()
        {
            Scope = InputScope.Direct;
            _modalUi = false;
            ModalOwners.Clear();
            _buildMode = false;
            _textInputFocused = false;
            _captureActive = false;
            _captureReleasedFrame = -1;
            _swallowFrame = -1;
            _gameplayPaused = false;
            _strategicPause = false;
            UiCapturedPointers.Clear();
            _frame = -1;
            ConsumedKeys.Clear();
            Reader = UnityInputReader.Instance;
        }

        /// <summary>测试注入硬件输入后端（`DEBT-M4R02-INPUT-SIM-01`）。传 null 恢复生产实现。
        /// 不清空 <see cref="ConsumedKeys"/>——调用方按需配合 <see cref="DebugClearConsumedKeys"/>
        /// 模拟帧边界，Editor 测试之间 <see cref="Time.frameCount"/> 往往不会真的变化。</summary>
        public static void DebugSetReader(IInputReader reader)
        {
            Reader = reader ?? UnityInputReader.Instance;
        }

        /// <summary>测试模拟"进入下一帧"：清空同帧按键消费记录，不影响 Scope/模态/暂停状态。
        /// 仅供 Editor 自检使用（`DEBT-M4R02-INPUT-SIM-01`）。</summary>
        public static void DebugClearConsumedKeys()
        {
            ConsumedKeys.Clear();
            _swallowFrame = -1; // 模拟进入下一帧：“本帧吞键”只管一帧。
        }

        /// <summary>指定域这一帧是否持有输入所有权。</summary>
        public static bool Owns(InputScope scope)
        {
            // 模态面板压倒一切：面板开着时连战略暂停也得让位，否则 WASD 会一边翻卡组一边推镜头。
            // 文本框有焦点时同理：打字不能触发玩法动作。
            if (_modalUi || ModalOwners.Count > 0 || KeyboardSuppressed)
            {
                return false;
            }
            // 玩法暂停默认夺走全部输入；战略暂停是唯一例外，且只放行战略域——
            // 直控输入在冻结的世界里没有意义，放行它只会让玩家以为操作生效了。
            if (_gameplayPaused && !(_strategicPause && scope == InputScope.Strategy))
            {
                return false;
            }
            return Scope == scope;
        }

        /// <summary>
        /// 消费一次按键按下。**同一帧同一个键只有第一个调用者拿到 true**，
        /// 这正是"不产生双重输入"这条验收从结构上成立的地方。
        /// </summary>
        public static bool ConsumeKeyDown(KeyCode key, InputScope scope)
        {
            if (!Owns(scope) || (IsMouseButton(key) && IsUiPointerBlocked()) || !Reader.GetKeyDown(key))
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
            if (KeyboardSuppressed || (ModalUiOpen && !allowDuringModal) || !Reader.GetKeyDown(key))
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
            return Owns(scope) && Reader.GetKey(key);
        }

        // ── ER2-INPUT-01 / FG0-UX-01：按逻辑动作消费（键位重绑落点）──────────────────────
        // 玩法/UI 层新代码一律用这组 Consume*Action，不再写字面量 KeyCode——
        // 物理键→逻辑动作的唯一解析点是 Settings.GameSettings.KeyBindings（FGR-ARC-012）。
        // 组合键按“主键按下 + Ctrl/Alt 精确匹配”判定；Shift 只在组合要求时检查（Shift 是追加选择 / 排队的限定键）。

        /// <summary>当前按住的修饰键（读 <see cref="Reader"/>，测试可注入）。</summary>
        public static InputModifier HeldModifiers()
        {
            InputModifier mods = InputModifier.None;
            if (Reader.GetKey(KeyCode.LeftControl) || Reader.GetKey(KeyCode.RightControl)) mods |= InputModifier.Ctrl;
            if (Reader.GetKey(KeyCode.LeftAlt) || Reader.GetKey(KeyCode.RightAlt)) mods |= InputModifier.Alt;
            if (Reader.GetKey(KeyCode.LeftShift) || Reader.GetKey(KeyCode.RightShift)) mods |= InputModifier.Shift;
            return mods;
        }

        /// <summary>组合的修饰部分此刻是否满足。修饰键本身、鼠标键、滚轮不比较修饰（修饰是点击的限定词）。</summary>
        public static bool ModifiersMatch(InputChord chord)
        {
            if (VirtualKeys.IsModifierKey(chord.Key) || VirtualKeys.IsMouseButton(chord.Key) || VirtualKeys.IsWheel(chord.Key))
            {
                return true;
            }
            InputModifier held = HeldModifiers();
            const InputModifier significant = InputModifier.Ctrl | InputModifier.Alt;
            if ((held & significant) != (chord.Mods & significant))
            {
                return false;
            }
            return (chord.Mods & InputModifier.Shift) == 0 || (held & InputModifier.Shift) != 0;
        }

        /// <summary>主键这一帧是否按下（不看修饰、不看所有权）。滚轮按方向读，鼠标键走鼠标接口。</summary>
        public static bool RawKeyDown(KeyCode key)
        {
            if (key == KeyCode.None)
            {
                return false;
            }
            if (key == VirtualKeys.WheelUp)
            {
                return Reader.MouseScrollDelta > 0.001f;
            }
            if (key == VirtualKeys.WheelDown)
            {
                return Reader.MouseScrollDelta < -0.001f;
            }
            if (VirtualKeys.IsMouseButton(key))
            {
                return Reader.GetMouseButtonDown(key - KeyCode.Mouse0);
            }
            return Reader.GetKeyDown(key);
        }

        /// <summary>主键此刻是否按住。</summary>
        public static bool RawKeyHeld(KeyCode key)
        {
            if (key == KeyCode.None)
            {
                return false;
            }
            if (VirtualKeys.IsWheel(key))
            {
                return RawKeyDown(key);
            }
            return Reader.GetKey(key);
        }

        /// <summary>组合这一帧是否按下（主键按下 + 修饰匹配）。</summary>
        public static bool ChordDown(InputChord chord) => chord.IsBound && RawKeyDown(chord.Key) && ModifiersMatch(chord);

        private static bool ConsumeChord(InputChord chord, InputScope scope)
        {
            if (!chord.IsBound || !Owns(scope) || (IsPointerKey(chord.Key) && IsUiPointerBlocked()) || !ChordDown(chord))
            {
                return false;
            }
            SyncFrame();
            return ConsumedKeys.Add(chord.Key);
        }

        private static bool ConsumeGlobalChord(InputChord chord, bool allowDuringModal)
        {
            if (!chord.IsBound || KeyboardSuppressed || (ModalUiOpen && !allowDuringModal) || !ChordDown(chord))
            {
                return false;
            }
            SyncFrame();
            return ConsumedKeys.Add(chord.Key);
        }

        /// <summary>按逻辑动作消费一次按下，域内互斥、同帧唯一。FG0-UX-01：动作还必须属于当前生效的上下文
        /// （同一个键在战略里是“移动命令”、在建造里是“旋转”，建造模式下不会再触发移动命令）。</summary>
        public static bool ConsumeAction(GameActionId action, InputScope scope)
        {
            return ActionInActiveContext(action) && ConsumeChord(Settings.GameSettings.KeyBindings.GetChord(action), scope);
        }

        /// <summary>动作登记的上下文是否包含当前生效的上下文。表里查不到的动作一律 false（不猜）。</summary>
        public static bool ActionInActiveContext(GameActionId action)
        {
            return InputActionCatalog.TryGet(action, out InputActionDef def) && (def.Contexts & ActiveContext) != 0;
        }

        /// <summary>按逻辑动作消费一次全局按下（不受 Scope 限制，见 <see cref="ConsumeGlobalKeyDown"/>）。</summary>
        public static bool ConsumeGlobalAction(GameActionId action, bool allowDuringModal = false)
        {
            return ConsumeGlobalChord(Settings.GameSettings.KeyBindings.GetChord(action), allowDuringModal);
        }

        /// <summary>FG0-UX-01：按上下文消费——动作登记的上下文包含 <see cref="ActiveContext"/> 时才读，
        /// 同帧唯一。给没有 Scope 概念的 UI 基础件与“尚未开放”提示用。</summary>
        public static bool ConsumeContextAction(GameActionId action)
        {
            if (KeyboardSuppressed || !InputActionCatalog.TryGet(action, out InputActionDef def)
                || (def.Contexts & ActiveContext) == 0)
            {
                return false;
            }
            InputChord chord = Settings.GameSettings.KeyBindings.GetChord(action);
            if (!chord.IsBound || (IsPointerKey(chord.Key) && IsUiPointerBlocked()) || !ChordDown(chord))
            {
                return false;
            }
            SyncFrame();
            return ConsumedKeys.Add(chord.Key);
        }

        /// <summary>按逻辑动作读取持续按住状态（按住类动作不比较修饰）。</summary>
        public static bool GetActionKey(GameActionId action, InputScope scope)
        {
            return Owns(scope) && ActionInActiveContext(action) && RawKeyHeld(Settings.GameSettings.KeyBindings.GetChord(action).Key);
        }

        /// <summary>不看所有权的按住读取（固定悬停提示这类纯界面动作用）。文本框有焦点时一律 false。</summary>
        public static bool IsActionHeld(GameActionId action)
        {
            return !KeyboardSuppressed && RawKeyHeld(Settings.GameSettings.KeyBindings.GetChord(action).Key);
        }

        /// <summary>FG0-UX-01：缩放量（正 = 拉近）。缩放动作绑在滚轮上时按滚动量给；绑在键盘键上时每按一次给 ±1 步。
        /// 鼠标停在界面上时滚轮归界面。</summary>
        public static float GetZoomDelta(InputScope scope)
        {
            if (!Owns(scope))
            {
                return 0f;
            }
            return ZoomContribution(GameActionId.ZoomIn, 1f) + ZoomContribution(GameActionId.ZoomOut, -1f);
        }

        private static float ZoomContribution(GameActionId action, float sign)
        {
            InputChord chord = Settings.GameSettings.KeyBindings.GetChord(action);
            if (!chord.IsBound)
            {
                return 0f;
            }
            if (VirtualKeys.IsWheel(chord.Key))
            {
                if (IsUiPointerBlocked())
                {
                    return 0f;
                }
                float scroll = Reader.MouseScrollDelta;
                float along = chord.Key == VirtualKeys.WheelUp ? Mathf.Max(0f, scroll) : Mathf.Max(0f, -scroll);
                return sign * along;
            }
            return ChordDown(chord) ? sign : 0f;
        }

        /// <summary>指针位置。过渡期间一律不给——镜头在动，屏幕坐标反投影出来的世界点没有意义。</summary>
        public static bool TryGetPointer(InputScope scope, out Vector3 screenPosition)
        {
            if (Owns(scope) && !IsUiPointerBlocked())
            {
                screenPosition = Reader.MousePosition;
                return true;
            }

            screenPosition = Vector3.zero;
            return false;
        }

        /// <summary>滚轮原始增量（界面滚动用）。镜头缩放请用 <see cref="GetZoomDelta"/>（可重绑）。</summary>
        public static float GetScrollDelta(InputScope scope)
        {
            return Owns(scope) && !IsUiPointerBlocked() ? Reader.MouseScrollDelta : 0f;
        }

        /// <summary>世界层读取鼠标按下的唯一入口；UI 覆盖区域和拖动期间一律不给下层玩法。
        /// FG0-UX-01：逻辑键 0 = 主动作、1 = 功能动作，按玩家绑定映射到物理鼠标键（左右手互换即改绑定）；
        /// 2 = 中键，不参与重绑。</summary>
        public static bool GetMouseButtonDown(int button, InputScope scope)
        {
            int physical = PhysicalButton(button);
            return physical >= 0 && Owns(scope) && !IsUiPointerBlocked() && Reader.GetMouseButtonDown(physical);
        }

        /// <summary>释放不按 UI 命中拦截，保证已在世界中开始的合法框选能正常收尾。</summary>
        public static bool GetMouseButtonUp(int button, InputScope scope)
        {
            int physical = PhysicalButton(button);
            return physical >= 0 && Owns(scope) && Reader.GetMouseButtonUp(physical);
        }

        /// <summary>逻辑鼠标键 → 物理鼠标键序号。主 / 功能动作只能绑鼠标键（<see cref="InputActionDef.MouseOnly"/>），
        /// 未绑定时返回 -1（读不到任何按下）。</summary>
        public static int PhysicalButton(int logicalButton)
        {
            GameActionId action;
            switch (logicalButton)
            {
                case 0: action = GameActionId.PrimaryAction; break;
                case 1: action = GameActionId.SecondaryAction; break;
                default: return logicalButton;
            }
            KeyCode key = Settings.GameSettings.KeyBindings.GetChord(action).Key;
            return VirtualKeys.IsMouseButton(key) ? key - KeyCode.Mouse0 : -1;
        }

        private static bool IsMouseButton(KeyCode key)
        {
            return VirtualKeys.IsMouseButton(key);
        }

        private static bool IsPointerKey(KeyCode key)
        {
            return VirtualKeys.IsMouseButton(key) || VirtualKeys.IsWheel(key);
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
