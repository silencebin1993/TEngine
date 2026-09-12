using GameLogic.Battle;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>镜头状态。M2-01 只有这三个，不做电影级轨迹。</summary>
    public enum ViewMode : byte
    {
        /// <summary>战略视角：自由平移与缩放，不跟随任何单位。</summary>
        Strategy = 0,
        /// <summary>过渡：镜头正在两个视角之间移动，此期间冻结全部玩法输入。</summary>
        Transition = 1,
        /// <summary>直控：跟随当前受控实体。</summary>
        Direct = 2,
    }

    /// <summary>
    /// 战略相机状态机（M2-01）。
    ///
    /// 三条设计要点，每条都对应一个验收项：
    /// <list type="number">
    /// <item><b>不传送实体、不重载场景。</b>本类只写 <c>Camera.transform</c>，
    /// 一个字都不碰模拟状态——切视角是镜头的事，跟单位在哪、归谁控制无关。</item>
    /// <item><b>不产生双重输入。</b>每帧第一件事就是把 <see cref="InputRouter.Scope"/>
    /// 设成当前状态对应的域，过渡期设成 <see cref="InputScope.None"/>。
    /// 玩法层与 UI 都从 <see cref="InputRouter"/> 取输入，所以"冻结"是结构性的。</item>
    /// <item><b>无效目标回退战略视角。</b>直控状态每帧校验锚点，受控实体没了就自动转战略，
    /// 而不是把镜头留在一个已经不存在的东西上。</item>
    /// </list>
    ///
    /// **不是 GameModule**：它必须在暂停期间继续运行（M2-01 要求暂停时能选择目标），
    /// 而 <c>CellStageFlow.Update</c> 的暂停早退会把整个 <c>_hub</c> 一起冻住。
    /// 因此由 CellStageFlow 在早退**之前**显式驱动，与它之前内联 <c>FollowCamera</c> 的位置一致。
    /// </summary>
    public sealed class CameraDirector
    {
        // ── 跟随 ──
        /// <summary>直控跟随的指数平滑系数。沿用改造前 FollowCamera 的手感，不借机改数值。</summary>
        private const float DirectFollowLambda = 8f;

        /// <summary>过渡时长。够看清"镜头在移动"，又不至于让人等——M2-01 非目标里写明不做电影级轨迹。</summary>
        private const float TransitionSeconds = 0.35f;

        /// <summary>单帧步长上限（约 20fps 的一帧）。见 <see cref="Tick"/> 里的说明。</summary>
        private const float MaxStepSeconds = 0.05f;

        // ── 战略视角 ──
        private const float StrategyPanSpeed = 28f;
        private const float StrategyEdgePanMargin = 8f;
        private const float MinOrthographicSize = 8f;
        private const float MaxOrthographicSize = 46f;
        private const float ZoomStep = 3.5f;
        /// <summary>战略平移允许越出场地边界的余量，让玩家能看清贴边的单位。</summary>
        private const float StrategyBoundsPadding = 6f;

        /// <summary>切换战略/直控的按键。Tab 已经归"切换控制目标"（M1 核心机制），不再复用。</summary>
        public const KeyCode ToggleViewKey = KeyCode.M;

        private Camera _camera;
        private SimBridge _sim;
        private Vector3 _followOffset;
        private float _arenaHalfExtent = 40f;
        private float _directOrthographicSize = 16f;

        private ViewMode _mode = ViewMode.Direct;
        /// <summary>过渡结束后要进入的状态。过渡本身不是稳定态，一定有去处。</summary>
        private ViewMode _pendingMode = ViewMode.Direct;
        private float _transitionRemaining;
        private Vector3 _transitionFrom;
        private Vector3 _transitionTo;
        private float _transitionFromSize;
        private float _transitionToSize;

        /// <summary>战略视角的注视点（世界 XZ）。进入战略时从当前镜头继承，之后由玩家平移。</summary>
        private float2 _strategyFocus;
        private float _strategyOrthographicSize = 28f;

        public ViewMode Mode => _mode;
        public bool InTransition => _mode == ViewMode.Transition;
        public float2 StrategyFocus => _strategyFocus;
        public float OrthographicSize => _camera != null ? _camera.orthographicSize : 0f;

        /// <summary>本局累计的模式切换次数。验收用，确认"一次请求只切一次"。</summary>
        public int ModeChangeCount { get; private set; }

        public void Bind(Camera camera, SimBridge sim, Vector3 followOffset, float arenaHalfExtent)
        {
            _camera = camera;
            _sim = sim;
            _followOffset = followOffset;
            _arenaHalfExtent = math.max(1f, arenaHalfExtent);
            if (_camera != null)
            {
                _directOrthographicSize = _camera.orthographicSize;
            }

            _mode = ViewMode.Direct;
            _pendingMode = ViewMode.Direct;
            _transitionRemaining = 0f;
            ModeChangeCount = 0;
            _strategyFocus = float2.zero;
            InputRouter.SetScope(InputScope.Direct);
        }

        public void Unbind()
        {
            _camera = null;
            _sim = null;
            InputRouter.Reset();
        }

        /// <summary>
        /// 每帧驱动。<paramref name="paused"/> 为 true 时玩法冻结，但镜头照常响应——
        /// 暂停下选择目标正是战略视角存在的意义之一。
        ///
        /// 用非缩放时间：调试加速或慢放时镜头手感不该跟着变。
        /// </summary>
        public void Tick(bool paused)
        {
            if (_camera == null || _sim == null)
            {
                return;
            }

            // 钳制单帧步长。一次卡顿、一个断点、或加载后的第一帧都可能给出很大的 dt，
            // 不钳的话整段过渡会被**一帧吃完**——玩家看到的是镜头闪现，而不是移动过去。
            // （这条不是假想：Editor 非 Play 下 unscaledDeltaTime 实测就远大于 TransitionSeconds，
            // 回归断言里的过渡一次 Tick 就收敛，正是同一个现象。）
            float dt = math.min(Time.unscaledDeltaTime, MaxStepSeconds);
            PublishInputScope();
            ReadModeRequests();

            switch (_mode)
            {
                case ViewMode.Transition:
                    TickTransition(dt);
                    break;
                case ViewMode.Strategy:
                    TickStrategy(dt);
                    break;
                default:
                    TickDirect(dt, paused);
                    break;
            }
        }

        /// <summary>把输入所有权按当前状态发布出去。这是"过渡期间冻结冲突输入"的落点。</summary>
        private void PublishInputScope()
        {
            switch (_mode)
            {
                case ViewMode.Transition:
                    InputRouter.SetScope(InputScope.None);
                    break;
                case ViewMode.Strategy:
                    InputRouter.SetScope(InputScope.Strategy);
                    break;
                default:
                    InputRouter.SetScope(InputScope.Direct);
                    break;
            }
        }

        private void ReadModeRequests()
        {
            // 切视角是全局操作：战略下要能回直控，直控下要能拉远。过渡期间不接受
            // （ConsumeGlobalKeyDown 仍受模态 UI 约束，但不看 Scope，所以这里自己挡一次）。
            if (_mode == ViewMode.Transition)
            {
                return;
            }

            if (InputRouter.ConsumeGlobalKeyDown(ToggleViewKey))
            {
                if (_mode == ViewMode.Direct)
                {
                    RequestStrategy();
                }
                else
                {
                    RequestDirect();
                }
            }
        }

        /// <summary>请求进入战略视角。已在战略或正在过渡时忽略，不会叠加。</summary>
        public bool RequestStrategy()
        {
            if (_camera == null || _mode == ViewMode.Strategy || _mode == ViewMode.Transition)
            {
                return false;
            }

            // 从当前镜头继承注视点，避免"拉远的瞬间画面跳到别处"。
            Vector3 pos = _camera.transform.position;
            _strategyFocus = new float2(pos.x - _followOffset.x, pos.z - _followOffset.z);
            _directOrthographicSize = _camera.orthographicSize;
            ClampStrategyFocus();
            BeginTransition(ViewMode.Strategy, StrategyCameraPosition(), _strategyOrthographicSize);
            return true;
        }

        /// <summary>
        /// 请求回到直控视角。没有有效受控实体时**拒绝**并停在战略视角——
        /// 这正是"无效目标回退战略视角"的另一半：不只是自动退出，也不许手动切回一个不存在的目标。
        /// </summary>
        public bool RequestDirect()
        {
            if (_camera == null || _mode == ViewMode.Direct || _mode == ViewMode.Transition)
            {
                return false;
            }
            if (!TryGetDirectAnchor(out float2 anchor))
            {
                return false;
            }

            BeginTransition(ViewMode.Direct, CameraPositionFor(anchor), _directOrthographicSize);
            return true;
        }

        /// <summary>把战略视角的注视点对准某个世界坐标（选中单位、事件提示等）。</summary>
        public void FocusStrategyOn(float2 worldPosition)
        {
            _strategyFocus = worldPosition;
            ClampStrategyFocus();
        }

        private void BeginTransition(ViewMode target, Vector3 targetPosition, float targetSize)
        {
            _pendingMode = target;
            _mode = ViewMode.Transition;
            _transitionRemaining = TransitionSeconds;
            _transitionFrom = _camera.transform.position;
            _transitionTo = targetPosition;
            _transitionFromSize = _camera.orthographicSize;
            _transitionToSize = targetSize;
            // 过渡一开始就把输入收走，不等下一帧——否则按下切换键的那一帧仍会漏一次玩法输入。
            InputRouter.SetScope(InputScope.None);
        }

        private void TickTransition(float dt)
        {
            _transitionRemaining -= dt;
            float t = _transitionRemaining <= 0f
                ? 1f
                : 1f - math.saturate(_transitionRemaining / TransitionSeconds);
            // smoothstep：两端速度为零，不做更花的曲线（非目标：不做电影级轨迹）。
            float eased = t * t * (3f - 2f * t);

            // 目标端持续重算：过渡到直控的这 0.35 秒里受控实体还在动，
            // 用固定终点会让镜头落地时再"抽"一下。
            if (_pendingMode == ViewMode.Direct && TryGetDirectAnchor(out float2 anchor))
            {
                _transitionTo = CameraPositionFor(anchor);
            }

            _camera.transform.position = Vector3.Lerp(_transitionFrom, _transitionTo, eased);
            _camera.orthographicSize = Mathf.Lerp(_transitionFromSize, _transitionToSize, eased);

            if (_transitionRemaining > 0f)
            {
                return;
            }

            // 过渡落地。目标若在这期间失效（受控实体死了），直接落到战略视角，不留悬空状态。
            if (_pendingMode == ViewMode.Direct && !TryGetDirectAnchor(out _))
            {
                _pendingMode = ViewMode.Strategy;
                _strategyOrthographicSize = math.clamp(
                    _strategyOrthographicSize, MinOrthographicSize, MaxOrthographicSize);
                _camera.orthographicSize = _strategyOrthographicSize;
            }

            _mode = _pendingMode;
            ModeChangeCount++;
            PublishInputScope();
        }

        private void TickDirect(float dt, bool paused)
        {
            if (!TryGetDirectAnchor(out float2 anchor))
            {
                // 受控实体没了：自动退回战略视角，镜头停在最后的有效位置而不是跟着一个空目标。
                RequestStrategyFromLostTarget();
                return;
            }

            Vector3 want = CameraPositionFor(anchor);
            // 暂停时不做平滑推进——dt 照常流逝会让镜头在"冻结的世界"里继续爬。
            _camera.transform.position = paused
                ? want
                : Vector3.Lerp(_camera.transform.position, want, 1f - math.exp(-DirectFollowLambda * dt));
            _directOrthographicSize = _camera.orthographicSize;
        }

        private void RequestStrategyFromLostTarget()
        {
            Vector3 pos = _camera.transform.position;
            _strategyFocus = new float2(pos.x - _followOffset.x, pos.z - _followOffset.z);
            ClampStrategyFocus();
            BeginTransition(ViewMode.Strategy, StrategyCameraPosition(), _strategyOrthographicSize);
        }

        private void TickStrategy(float dt)
        {
            float2 pan = ReadPanInput();
            if (math.lengthsq(pan) > 0f)
            {
                // 平移速度随视野缩放：拉得越远，同样一次推屏移动的世界距离越大，否则远景下挪不动。
                float speed = StrategyPanSpeed * (_strategyOrthographicSize / 16f);
                _strategyFocus += math.normalize(pan) * speed * dt;
                ClampStrategyFocus();
            }

            float scroll = InputRouter.GetScrollDelta(InputScope.Strategy);
            if (math.abs(scroll) > 0.001f)
            {
                _strategyOrthographicSize = math.clamp(
                    _strategyOrthographicSize - scroll * ZoomStep,
                    MinOrthographicSize, MaxOrthographicSize);
                // 缩放会改变可视范围，边界要重新钳一次，否则拉远后能把镜头推出场地。
                ClampStrategyFocus();
            }

            _camera.transform.position = StrategyCameraPosition();
            _camera.orthographicSize = _strategyOrthographicSize;
        }

        private float2 ReadPanInput()
        {
            float x = 0f;
            float y = 0f;
            if (InputRouter.GetKey(KeyCode.A, InputScope.Strategy) ||
                InputRouter.GetKey(KeyCode.LeftArrow, InputScope.Strategy)) { x -= 1f; }
            if (InputRouter.GetKey(KeyCode.D, InputScope.Strategy) ||
                InputRouter.GetKey(KeyCode.RightArrow, InputScope.Strategy)) { x += 1f; }
            if (InputRouter.GetKey(KeyCode.S, InputScope.Strategy) ||
                InputRouter.GetKey(KeyCode.DownArrow, InputScope.Strategy)) { y -= 1f; }
            if (InputRouter.GetKey(KeyCode.W, InputScope.Strategy) ||
                InputRouter.GetKey(KeyCode.UpArrow, InputScope.Strategy)) { y += 1f; }

            if (x != 0f || y != 0f)
            {
                return new float2(x, y);
            }

            // 屏幕边缘推屏。只在指针确实在窗口内时生效，否则 Alt-Tab 出去镜头会自己一直飘。
            if (!InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer) ||
                pointer.x < 0f || pointer.y < 0f ||
                pointer.x > Screen.width || pointer.y > Screen.height)
            {
                return float2.zero;
            }

            if (pointer.x <= StrategyEdgePanMargin) { x -= 1f; }
            else if (pointer.x >= Screen.width - StrategyEdgePanMargin) { x += 1f; }
            if (pointer.y <= StrategyEdgePanMargin) { y -= 1f; }
            else if (pointer.y >= Screen.height - StrategyEdgePanMargin) { y += 1f; }
            return new float2(x, y);
        }

        private void ClampStrategyFocus()
        {
            float limit = _arenaHalfExtent + StrategyBoundsPadding;
            _strategyFocus = math.clamp(_strategyFocus, new float2(-limit, -limit), new float2(limit, limit));
        }

        private Vector3 StrategyCameraPosition()
        {
            return CameraPositionFor(_strategyFocus);
        }

        private Vector3 CameraPositionFor(float2 focus)
        {
            return new Vector3(focus.x + _followOffset.x, _followOffset.y, focus.y + _followOffset.z);
        }

        /// <summary>直控跟随目标。只认"确实有受控实体"，回退锚点不算——那是战略视角的活。</summary>
        private bool TryGetDirectAnchor(out float2 anchor)
        {
            if (_sim != null && _sim.Running &&
                _sim.TryGetPresentationAnchor(out anchor, out bool hasControlled) && hasControlled)
            {
                return true;
            }

            anchor = float2.zero;
            return false;
        }
    }
}
