using GameLogic.Battle;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Settings;
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
        // FG0-ARCH-01 修复：战略缩放范围入表（fg.TbHomeTuning camera.zoom_*），缺表时回落 Demo 初值。
        // 旧细胞阶段等没加载 fg 表的场合静默用初值（不刷告警；表内有没有这三行由 FgWorldSimSelfCheck A 段断言）。
        private static float MinOrthographicSize => Tune("camera.zoom_min_ortho", 8f);
        private static float MaxOrthographicSize => math.max(MinOrthographicSize, Tune("camera.zoom_max_ortho", 46f));
        private static float ZoomStep => Tune("camera.zoom_step", 3.5f);

        private static float Tune(string id, float fallback) =>
            GameLogic.Campaign.Grid.GridContent.TryGetTuning(id, out float v) ? v : fallback;
        /// <summary>战略平移允许越出场地边界的余量，让玩家能看清贴边的单位。</summary>
        private const float StrategyBoundsPadding = 6f;

        /// <summary>
        /// ER2-INPUT-01：直控锚点来源。原先硬编码问 <c>SimBridge</c>，只有细胞阶段能用；
        /// 归还谷地没有 SimBridge（ER2-SCENE-01 明确裁决不接内核，见 <c>HomeValleyController</c>
        /// 类注释），改成委托后两边可以共用同一套镜头状态机而不互相耦合——CameraDirector
        /// 仍然"一个字都不碰模拟状态"（类注释设计要点第 1 条），只是把"哪个模拟"外部化。
        /// 返回 true 且给出锚点＝有可跟随的直控目标；false＝当前没有（回退战略视角）。
        /// </summary>
        public delegate bool DirectAnchorProvider(out float2 anchor);

        private Camera _camera;
        private DirectAnchorProvider _anchorProvider;
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
        /// <summary>FG0-ARCH-01：本次过渡的时长（视角切换 0.35 秒；镜头飞跃 camera.fly_seconds）。</summary>
        private float _transitionDuration = TransitionSeconds;

        /// <summary>FG0-ARCH-01：矩形平移边界（星球表面按已探索范围给出）。未设置时沿用以原点为中心的方形 <see cref="_arenaHalfExtent"/>。</summary>
        private bool _hasRectBounds;
        private float2 _boundsMin;
        private float2 _boundsMax;

        /// <summary>战略视角的注视点（世界 XZ）。进入战略时从当前镜头继承，之后由玩家平移。</summary>
        private float2 _strategyFocus;
        private float _strategyOrthographicSize = 28f;

        /// <summary>上一帧叠加在相机上的震屏偏移，下一帧开头先撤掉。</summary>
        private Vector3 _appliedShake;

        public ViewMode Mode => _mode;
        public bool InTransition => _mode == ViewMode.Transition;
        public float2 StrategyFocus => _strategyFocus;
        public float OrthographicSize => _camera != null ? _camera.orthographicSize : 0f;
        /// <summary>FG0-ARCH-01：战略视角的缩放（全局镜头按地点记忆 / 恢复）。</summary>
        public float StrategyOrthographicSize => _strategyOrthographicSize;
        public bool IsBound => _camera != null;
        /// <summary>FG0-ARCH-01：当前生效的平移边界（自检用）。</summary>
        public float2 BoundsMin => _hasRectBounds ? _boundsMin : new float2(-_arenaHalfExtent - StrategyBoundsPadding);
        public float2 BoundsMax => _hasRectBounds ? _boundsMax : new float2(_arenaHalfExtent + StrategyBoundsPadding);
        public int FlightCount { get; private set; }

        /// <summary>FG0-ARCH-01：镜头用的真实帧时间来源（默认 <see cref="Time.unscaledDeltaTime"/>）。batchmode 自检在同一编辑器帧里
        /// 连续驱动几千帧，Unity 的 unscaledDeltaTime 不变，注入固定值才能让过渡按帧真实走完。</summary>
        public static System.Func<float> RealDeltaTime = () => Time.unscaledDeltaTime;

        /// <summary>本局累计的模式切换次数。验收用，确认"一次请求只切一次"。</summary>
        public int ModeChangeCount { get; private set; }

        /// <summary>细胞阶段既有调用点：直接传 SimBridge，内部包一层委托。行为与改造前逐字节一致
        /// （起始态仍是 Direct）——不改动 CellStageFlow 的调用现场。</summary>
        public void Bind(Camera camera, SimBridge sim, Vector3 followOffset, float arenaHalfExtent)
        {
            Bind(camera,
                (out float2 anchor) =>
                {
                    if (sim != null && sim.Running &&
                        sim.TryGetPresentationAnchor(out anchor, out bool hasControlled) && hasControlled)
                    {
                        return true;
                    }
                    anchor = float2.zero;
                    return false;
                },
                followOffset, arenaHalfExtent, startInStrategy: false);
        }

        /// <summary>ER2-INPUT-01 通用入口：不依赖 SimBridge，任何"能报出一个直控锚点"的场景
        /// 都能接（归还谷地的 <see cref="GameLogic.Campaign.Regions.HomeValleyMachineMarker"/> 即是一例）。
        /// <paramref name="startInStrategy"/>：归还谷地没有"默认应该直控谁"的天然答案（多台平等机器，
        /// 不是单一玩家本体），一进场从 Strategy 开始是确定性安全态，与"无效目标回退 Strategy"
        /// 同一原则（AC-CTL-006）；细胞阶段沿用原有"进场即直控玩家本体"，见上方重载。</summary>
        public void Bind(Camera camera, DirectAnchorProvider anchorProvider, Vector3 followOffset,
            float arenaHalfExtent, bool startInStrategy, float? initialDirectOrthographicSize = null)
        {
            _camera = camera;
            _anchorProvider = anchorProvider;
            _appliedShake = Vector3.zero; // 新绑定的相机上没有本类叠过的偏移。
            _followOffset = followOffset;
            _arenaHalfExtent = math.max(1f, arenaHalfExtent);
            _hasRectBounds = false;
            if (_camera != null)
            {
                // 归还谷地进场时相机是战略远景尺寸（见 startInStrategy 分支），直接拿来当"直控该用
                // 多大视野"没有意义——显式给一个贴身尺寸；细胞阶段不传，沿用改造前"就用当前相机尺寸"
                // 的行为（进场即直控玩家本体，此刻相机尺寸本来就是直控惯用值）。
                _directOrthographicSize = initialDirectOrthographicSize ?? _camera.orthographicSize;
            }

            _mode = startInStrategy ? ViewMode.Strategy : ViewMode.Direct;
            _pendingMode = _mode;
            _transitionRemaining = 0f;
            ModeChangeCount = 0;
            _strategyFocus = float2.zero;
            if (startInStrategy && _camera != null)
            {
                _strategyOrthographicSize = math.clamp(_camera.orthographicSize, MinOrthographicSize, MaxOrthographicSize);
                ClampStrategyFocus();
                _camera.transform.position = StrategyCameraPosition();
                _camera.orthographicSize = _strategyOrthographicSize;
            }
            InputRouter.SetScope(_mode == ViewMode.Strategy ? InputScope.Strategy : InputScope.Direct);
        }

        public void Unbind() => Unbind(resetInput: true);

        /// <summary>FG0-ARCH-01：全局镜头在地点之间切换时解绑但不复位输入（模态 / 建造上下文属于玩家当前的界面状态，不因镜头换地点而丢）。</summary>
        public void Unbind(bool resetInput)
        {
            if (_camera != null)
            {
                _camera.transform.position -= _appliedShake;
            }
            _appliedShake = Vector3.zero;
            _camera = null;
            _anchorProvider = null;
            _hasRectBounds = false;
            if (resetInput)
            {
                InputRouter.Reset();
            }
        }

        /// <summary>FG0-ARCH-01：设置矩形平移边界（世界坐标 XZ），立即把焦点钳进去。</summary>
        public void SetStrategyBounds(Vector2 min, Vector2 max)
        {
            _hasRectBounds = true;
            _boundsMin = new float2(math.min(min.x, max.x), math.min(min.y, max.y));
            _boundsMax = new float2(math.max(min.x, max.x), math.max(min.y, max.y));
            ClampStrategyFocus();
        }

        /// <summary>FG0-ARCH-01：直接设定战略视角（地点记忆恢复用，不做过渡）。</summary>
        public void SetStrategyView(float2 focus, float orthographicSize)
        {
            _strategyFocus = focus;
            _strategyOrthographicSize = math.clamp(orthographicSize, MinOrthographicSize, MaxOrthographicSize);
            ClampStrategyFocus();
            if (_camera != null && _mode == ViewMode.Strategy)
            {
                _camera.transform.position = StrategyCameraPosition();
                _camera.orthographicSize = _strategyOrthographicSize;
            }
        }

        /// <summary>FG0-ARCH-01（FG17 FGR-GEN-080“点击任意位置或通知，镜头飞过去”）：用 <paramref name="seconds"/> 平滑飞到
        /// <paramref name="focus"/>。接入视角下先拉回战略并以目标为终点；过渡中改终点。返回 false = 没有绑定镜头。</summary>
        public bool FlyStrategyTo(float2 focus, float seconds)
        {
            if (_camera == null)
            {
                return false;
            }
            FlightCount++;
            if (_mode == ViewMode.Direct)
            {
                return RequestStrategy(focus);
            }
            _strategyFocus = focus;
            ClampStrategyFocus();
            BeginTransition(ViewMode.Strategy, StrategyCameraPosition(), _strategyOrthographicSize, math.max(0.01f, seconds));
            return true;
        }

        /// <summary>
        /// 每帧驱动。<paramref name="paused"/> 为 true 时玩法冻结，但镜头照常响应——
        /// 暂停下选择目标正是战略视角存在的意义之一。
        ///
        /// 用非缩放时间：调试加速或慢放时镜头手感不该跟着变。
        /// </summary>
        public void Tick(bool paused)
        {
            if (_camera == null || _anchorProvider == null)
            {
                return;
            }

            // ER8-CONTENT-01 屏幕震动：先撤掉上一帧叠上去的偏移，下面三种模式都读写“干净”的相机位置
            // （直控跟随从当前位置做平滑，偏移不撤掉会被当成起点吃进去，越积越歪）。
            _camera.transform.position -= _appliedShake;
            _appliedShake = Vector3.zero;

            // 钳制单帧步长。一次卡顿、一个断点、或加载后的第一帧都可能给出很大的 dt，
            // 不钳的话整段过渡会被**一帧吃完**——玩家看到的是镜头闪现，而不是移动过去。
            // （这条不是假想：Editor 非 Play 下 unscaledDeltaTime 实测就远大于 TransitionSeconds，
            // 回归断言里的过渡一次 Tick 就收敛，正是同一个现象。）
            float dt = math.min(RealDeltaTime(), MaxStepSeconds);
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

            // 最后统一叠加本帧震屏偏移（设置关闭时恒为零）。
            _appliedShake = ScreenShake.Sample(dt, _camera.orthographicSize, _camera.transform);
            _camera.transform.position += _appliedShake;
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

            if (InputRouter.ConsumeGlobalAction(GameActionId.ToggleCameraView))
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
        public bool RequestStrategy() => RequestStrategy(null);

        /// <summary>请求进入战略视角，并让过渡直接落到 <paramref name="focus"/>（FG0-UX-01 通知定位：
        /// 接入视角下点通知，镜头要拉回战略并飞到事件位置，而不是停在自己机器上方）。
        /// <paramref name="focus"/> 为空时从当前镜头继承注视点。</summary>
        public bool RequestStrategy(float2? focus)
        {
            if (_camera == null || _mode == ViewMode.Strategy || _mode == ViewMode.Transition)
            {
                return false;
            }

            // 从当前镜头继承注视点，避免"拉远的瞬间画面跳到别处"；指定了目标焦点时直接过渡到目标。
            Vector3 pos = _camera.transform.position;
            _strategyFocus = focus ?? new float2(pos.x - _followOffset.x, pos.z - _followOffset.z);
            _directOrthographicSize = _camera.orthographicSize;
            ClampStrategyFocus();
            BeginTransition(ViewMode.Strategy, StrategyCameraPosition(), _strategyOrthographicSize);
            return true;
        }

        /// <summary>
        /// 请求回到直控视角。没有有效受控实体时**拒绝**并停在战略视角——
        /// 这正是"无效目标回退战略视角"的另一半：不只是自动退出，也不许手动切回一个不存在的目标。
        /// </summary>
        /// <summary>
        /// 回直控前的"重新拿一具身体"钩子（2026-09-14）。由 <c>CellStageFlow</c> 注入。
        ///
        /// 战略视角下玩家是**放下意识**的（场上没有任何 <c>IntentSource.Player</c> 单位，
        /// 这样本体也能被框选和下令）。于是按 M 回直控时必须先重新接管一具，
        /// 否则 <see cref="TryGetDirectAnchor"/> 找不到锚点，直接被拒。
        ///
        /// 钩子放在这里而不是让镜头自己去碰模拟：本类的既定纪律是"一个字都不碰模拟状态"
        /// （M2-01 设计要点第 1 条）。它只负责问一句"能给我一个目标吗"，怎么拿是玩法层的事。
        /// 返回 false = 真的没有可接管的身体，照常拒绝并停在战略视角。
        /// </summary>
        public System.Func<bool> EnsureDirectTarget;

        public bool RequestDirect()
        {
            if (_camera == null || _mode == ViewMode.Direct || _mode == ViewMode.Transition)
            {
                return false;
            }
            if (!TryGetDirectAnchor(out float2 anchor))
            {
                // 放下意识之后没有受控实体是**正常状态**，不是异常——先请玩法层接管一具再试。
                if (EnsureDirectTarget == null || !EnsureDirectTarget() ||
                    !TryGetDirectAnchor(out anchor))
                {
                    return false;
                }
            }

            BeginTransition(ViewMode.Direct, CameraPositionFor(anchor), _directOrthographicSize);
            return true;
        }

        /// <summary>把战略视角的注视点对准某个世界坐标（选中单位、事件提示等）。</summary>
        public void FocusStrategyOn(float2 worldPosition)
        {
            _strategyFocus = worldPosition;
            ClampStrategyFocus();
            // 正在拉回战略视角的过渡里改焦点：过渡终点跟着改，不在落地时再“跳”一下。
            if (_camera != null && _mode == ViewMode.Transition && _pendingMode == ViewMode.Strategy)
            {
                _transitionTo = StrategyCameraPosition();
            }
        }

        private void BeginTransition(ViewMode target, Vector3 targetPosition, float targetSize, float duration = TransitionSeconds)
        {
            _pendingMode = target;
            _mode = ViewMode.Transition;
            _transitionDuration = duration;
            _transitionRemaining = duration;
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
                : 1f - math.saturate(_transitionRemaining / _transitionDuration);
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
            float2 pan = ReadPanInput(out bool fromEdge);
            if (math.lengthsq(pan) > 0f)
            {
                // 平移速度随视野缩放：拉得越远，同样一次推屏移动的世界距离越大，否则远景下挪不动。
                // ER8-CONTENT-01：设置里的“镜头速度”与“边缘平移速度”此前零消费方，在这里生效。
                float speed = StrategyPanSpeed * (_strategyOrthographicSize / 16f) * GameSettings.CameraSpeedMultiplier
                              * (fromEdge ? GameSettings.EdgePanSpeedMultiplier : 1f);
                _strategyFocus += math.normalize(pan) * speed * dt;
                ClampStrategyFocus();
            }

            // FG0-UX-01：缩放走可重绑的“拉近 / 拉远”动作（默认滚轮上 / 下，可改成键盘键）。
            float scroll = InputRouter.GetZoomDelta(InputScope.Strategy);
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

        private float2 ReadPanInput(out bool fromEdge)
        {
            fromEdge = false;
            float x = 0f;
            float y = 0f;
            // FG0-UX-01：平移键全部走可重绑动作（WASD 与方向键两组），不再写字面量 KeyCode。
            if (InputRouter.GetActionKey(GameActionId.MoveLeft, InputScope.Strategy) ||
                InputRouter.GetActionKey(GameActionId.StrategyPanLeft, InputScope.Strategy)) { x -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveRight, InputScope.Strategy) ||
                InputRouter.GetActionKey(GameActionId.StrategyPanRight, InputScope.Strategy)) { x += 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveBack, InputScope.Strategy) ||
                InputRouter.GetActionKey(GameActionId.StrategyPanDown, InputScope.Strategy)) { y -= 1f; }
            if (InputRouter.GetActionKey(GameActionId.MoveForward, InputScope.Strategy) ||
                InputRouter.GetActionKey(GameActionId.StrategyPanUp, InputScope.Strategy)) { y += 1f; }

            if (x != 0f || y != 0f)
            {
                return new float2(x, y);
            }

            // 屏幕边缘推屏。只在指针确实在窗口内时生效，否则 Alt-Tab 出去镜头会自己一直飘。
            // 设置“边缘平移”关闭时完全不推（此前该开关无人读取，边缘平移永远开着）。
            if (!GameSettings.EdgePanEnabled ||
                !InputRouter.TryGetPointer(InputScope.Strategy, out Vector3 pointer) ||
                pointer.x < 0f || pointer.y < 0f ||
                pointer.x > Screen.width || pointer.y > Screen.height)
            {
                return float2.zero;
            }

            if (pointer.x <= StrategyEdgePanMargin) { x -= 1f; }
            else if (pointer.x >= Screen.width - StrategyEdgePanMargin) { x += 1f; }
            if (pointer.y <= StrategyEdgePanMargin) { y -= 1f; }
            else if (pointer.y >= Screen.height - StrategyEdgePanMargin) { y += 1f; }
            fromEdge = x != 0f || y != 0f;
            return new float2(x, y);
        }

        private void ClampStrategyFocus()
        {
            if (_hasRectBounds)
            {
                _strategyFocus = math.clamp(_strategyFocus, _boundsMin, _boundsMax);
                return;
            }
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
            if (_anchorProvider != null && _anchorProvider(out anchor))
            {
                return true;
            }

            anchor = float2.zero;
            return false;
        }
    }
}
