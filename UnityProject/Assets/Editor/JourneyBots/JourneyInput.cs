using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG0-QA-01：旅程走正式输入的工具。
    ///
    /// 三条输入通道，都是玩家输入进入游戏的那一层，不调业务方法：
    /// - **世界层键鼠**：替换 <see cref="InputRouter"/> 的硬件读取后端（本仓库读键鼠的唯一入口，生产实现只是转发 UnityEngine.Input），
    ///   按帧报告按键 / 按住 / 鼠标；输入上下文、快捷键绑定（按当前绑定取键位）、点击穿透规则照常生效。
    /// - **uGUI 按钮**（主菜单）：FG1-E2E-01 起走 EventSystem——按按钮中心构造指针事件，EventSystem.RaycastAll 取最上层命中物
    ///   （被别的界面挡住就点不到，报原因），再按输入模块的顺序派发 按下 → 抬起 → 点击。batchmode 不渲染时 Graphic 没有深度、
    ///   射线可能一个都打不中：这时直接对按钮派发同一串指针事件（仍经按钮自己的 IPointerClickHandler，禁用 / 不可交互照样点不了），
    ///   并在报告里计数“未做遮挡检查”的次数（<see cref="UguiUnpickedClicks"/>），不静默。
    /// - **UI Toolkit 控件**：FG1-E2E-01 起向控件所在面板派发真实的 PointerDown / PointerUp 事件（面板坐标 = 控件中心），由面板自己的
    ///   事件派发按位置拾取目标——被同一面板上的别的元素挡住就落不到控件上；还核对排序更高的其他面板在该屏幕点有没有可见控件或窗口
    ///   （与世界点击拦截 <see cref="UiWindowFocus.BlocksWorldPointerAt"/> 同一判据），挡住就报原因、不点（DEBT-FG0QA01-07）。
    ///
    /// 光标默认放在窗口外：batchmode 下光标在左下角 = 屏幕边缘，会触发战略镜头的边缘推屏。
    /// </summary>
    public static class JourneyInput
    {
        public static readonly Vector3 OffScreen = new Vector3(-10f, -10f, 0f);

        /// <summary>本次会话里 uGUI 点击时射线一个都没打中、按按钮自身派发（未做遮挡检查）的次数。旅程报告里写出。</summary>
        public static int UguiUnpickedClicks { get; private set; }
        /// <summary>本次会话里经射线确认最上层就是该按钮后点下的次数。</summary>
        public static int UguiPickedClicks { get; private set; }
        /// <summary>本次会话里 UI Toolkit 控件按拾取确认没被挡住、派发指针事件点下的次数。</summary>
        public static int UitkClicks { get; private set; }
        /// <summary>最近一次 UI 点击失败的原因（没失败为空串）。</summary>
        public static string LastUiFailure { get; private set; } = string.Empty;

        public static void ResetCounters()
        {
            UguiUnpickedClicks = 0;
            UguiPickedClicks = 0;
            UitkClicks = 0;
            TooltipRetreats = 0;
            AlternatePointClicks = 0;
            UitkDrags = 0;
            ToastWaits = 0;
            DeferredPanClicks = 0;
            LastUiFailure = string.Empty;
        }

        public static Camera Cam => WorldView.Camera != null ? WorldView.Camera : Camera.main;

        /// <summary>地面点（格坐标 x, y = 世界 x, z）的屏幕位置。</summary>
        public static Vector3 ScreenOf(Vector2 ground)
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return OffScreen;
            }
            Vector3 s = cam.WorldToScreenPoint(new Vector3(ground.x, 0f, ground.y));
            return new Vector3(s.x, s.y, 0f);
        }

        /// <summary>世界点（含高度）的屏幕位置。</summary>
        public static Vector3 ScreenOfWorld(Vector3 world)
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return OffScreen;
            }
            Vector3 s = cam.WorldToScreenPoint(world);
            return new Vector3(s.x, s.y, 0f);
        }

        /// <summary>地面点是否在画面内（离边缘至少 <paramref name="margin"/> 个视口比例）。</summary>
        public static bool OnScreen(Vector2 ground, float margin = 0.08f)
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return false;
            }
            Vector3 v = cam.WorldToViewportPoint(new Vector3(ground.x, 0f, ground.y));
            return v.z > 0f && v.x >= margin && v.x <= 1f - margin && v.y >= margin && v.y <= 1f - margin;
        }

        /// <summary>交还真键盘 / 鼠标。</summary>
        public static void Release() => InputRouter.DebugSetReader(null);

        /// <summary>
        /// 旅程进行中，输入后端始终是旅程自己的（没有按键时光标在窗口外）。载入 / 离开世界会 InputRouter.Reset() 把后端复位成真键盘鼠标：
        /// batchmode 下真鼠标停在 (0,0) = 窗口左下角，战略镜头会被边缘推屏一路推到可平移范围的左下角，核心就不在画面里了。宿主每帧调用。
        /// </summary>
        public static void KeepScripted()
        {
            if (!(InputRouter.Reader is ScriptReader))
            {
                InputRouter.DebugSetReader(new ScriptReader());
            }
        }

        /// <summary>光标停在地面一点（不按键）。</summary>
        public static void Hover(Vector2 ground)
        {
            Vector3 m = ScreenOf(ground);
            InputRouter.DebugSetReader(new ScriptReader { MouseA = m, MouseB = m });
        }

        /// <summary>按一次键：只在下一帧报告按下。光标默认在窗口外；建造模式里旋转要让光标留在虚影上，传 <paramref name="mouse"/>。</summary>
        public static void PressKey(KeyCode key, Vector3? mouse = null)
        {
            if (key == KeyCode.None)
            {
                // 动作没有绑定按键：按“无”会让所有没绑定的动作在同一帧一起触发。当作旅程失败报出来（宿主会把 Error 计入报错）。
                Debug.LogError("[Journey] 要按的动作没有绑定按键（KeyCode.None）");
                return;
            }
            Vector3 m = mouse ?? OffScreen;
            InputRouter.DebugSetReader(new ScriptReader { Key = key, KeyFrame = Time.frameCount + 1, MouseA = m, MouseB = m });
        }

        /// <summary>按一次组合键（例如 Alt+P / Ctrl+Z）：下一帧修饰键按住、主键按下。没有修饰键时等同 <see cref="PressKey"/>。</summary>
        public static void PressChord(InputChord chord, Vector3? mouse = null)
        {
            if (!chord.IsBound)
            {
                Debug.LogError("[Journey] 要按的组合键没有绑定按键");
                return;
            }
            KeyCode held = (chord.Mods & InputModifier.Ctrl) != 0 ? KeyCode.LeftControl
                : (chord.Mods & InputModifier.Alt) != 0 ? KeyCode.LeftAlt
                : (chord.Mods & InputModifier.Shift) != 0 ? KeyCode.LeftShift
                : KeyCode.None;
            Vector3 m = mouse ?? OffScreen;
            var r = new ScriptReader { Key = chord.Key, KeyFrame = Time.frameCount + 1, MouseA = m, MouseB = m };
            if (held != KeyCode.None)
            {
                r.ChordHeld = held;
            }
            InputRouter.DebugSetReader(r);
        }

        /// <summary>按一次动作键（按当前绑定取键位与修饰键，与玩家重绑后的按法一致）。</summary>
        public static void PressAction(GameActionId action, Vector3? mouse = null) => PressChord(GameSettings.KeyBindings.GetChord(action), mouse);

        /// <summary>
        /// 切换类按键“先读状态再决定按不按”（DEBT-FG0QA01-07）：已经是想要的状态就不按（返回 false），否则按一次（返回 true）。
        /// 步骤重试时再次调用也安全——第一次按键延迟生效时，重试不会把状态又切回去。
        /// </summary>
        public static bool PressToggleTo(GameActionId action, System.Func<bool> isOn, bool want, Vector3? mouse = null) =>
            EnsureToggle(isOn, want, () => PressAction(action, mouse));

        /// <summary><see cref="PressToggleTo"/> 的判定本体（自检用假开关驱动）：状态已经是 <paramref name="want"/> 就不按，否则按一次。返回是否按了。</summary>
        public static bool EnsureToggle(System.Func<bool> isOn, bool want, System.Action press)
        {
            if (isOn() == want)
            {
                return false;
            }
            press();
            return true;
        }

        /// <summary>按住若干键 <paramref name="seconds"/> 真实秒（例如 WASD 驾驶、按住 E 交互），期间光标停在 <paramref name="mouse"/>（默认窗口外）。</summary>
        public static void HoldKeys(IEnumerable<KeyCode> keys, double seconds, Vector3? mouse = null)
        {
            Vector3 m = mouse ?? OffScreen;
            // 和人按键一样：下一帧报告“按下”，之后一直“按住”到时长结束（按住 E 交互要先有按下那一帧）。
            var r = new ScriptReader { MouseA = m, MouseB = m, HoldUntil = Time.realtimeSinceStartup + (float)seconds, HeldDownFrame = Time.frameCount + 1 };
            foreach (KeyCode k in keys)
            {
                if (k != KeyCode.None)
                {
                    r.Held.Add(k);
                }
            }
            InputRouter.DebugSetReader(r);
        }

        /// <summary>按住一个动作键（按当前绑定取键位）。</summary>
        public static void HoldAction(GameActionId action, double seconds, Vector3? mouse = null) =>
            HoldKeys(new[] { GameSettings.KeyBindings.GetKey(action) }, seconds, mouse);

        /// <summary>松开所有按住的键（光标回到窗口外）。</summary>
        public static void ReleaseKeys() => InputRouter.DebugSetReader(new ScriptReader());

        /// <summary>是否还有键被按住（按住时长没到）。</summary>
        public static bool Holding => InputRouter.Reader is ScriptReader r && r.Held.Count > 0 && Time.realtimeSinceStartup < r.HoldUntil;

        /// <summary>鼠标点地面一点：光标移过去，下一帧按下、再下一帧抬起（和人点一次一样）。<paramref name="button"/> 1 = 右键。</summary>
        public static void Click(Vector2 ground, int button = 0) => ClickScreen(ScreenOf(ground), button);

        /// <summary>鼠标点世界里一点（含高度，例如机器模型的位置）。</summary>
        public static void ClickWorld(Vector3 world, int button = 0) => ClickScreen(ScreenOfWorld(world), button);

        public static void ClickScreen(Vector3 m, int button = 0)
        {
            ClickTrace.Clear();
            _clickTraceFrame = -1;
            _hostWatchFrame = -1;
            _lastWorldClickAt = Time.realtimeSinceStartup;
            ClickTrace.Add($"发出（帧 {Time.frameCount}）：光标 ({m.x:F0},{m.y:F0})、镜头 {CamPose()}");
            InputRouter.DebugSetReader(new ScriptReader
            {
                MouseA = m,
                MouseB = m,
                MouseButton = button,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
                Trace = true,
            });
        }

        // ── FG5-E2E-01 修复轮：世界点击 / 按键诊断（只读）────────────────────────────

        /// <summary>
        /// 最近一次脚本鼠标点击（<see cref="ClickScreen"/>）在按下 / 抬起那一帧，世界层读到鼠标时看到的输入状态：战略域有没有输入所有权、
        /// 界面拦截（<see cref="InputRouter.IsUiPointerBlocked"/>，与世界点选同一判据）与拦下它的元素、射线最先打到什么、建造模式与武装命令。
        /// 点建筑没开面板时写进失败说明，用来区分“产品把这次点击拦下了”与“注入时序”（审查 P1：首次点击不开面板）。
        /// </summary>
        public static string LastWorldClickTrace => ClickTrace.Count == 0 ? "这一次尝试里没有发出鼠标点击（目标整座被界面挡住 / 不在画面里时先平移镜头）" : string.Join("；", ClickTrace);

        // ── 修复轮（审查 P1“左键点装配站第一次常不开面板”）：先平移镜头、平移完在同一次尝试里补点 ──────────────
        // 诊断轨迹（上面的 LastWorldClickTrace）证实：那一次根本没有发出点击——建筑整座被左下角的区域指挥栏挡住，ClickBuildingVisible 按设计先平移镜头、
        // 不点；原来要等步骤判“面板没开”用掉一次重试，下一次尝试才点（日志看起来像“点了没开”）。玩家也是先挪镜头再点：平移结束后在同一次尝试里补点。

        private static System.Action _deferredWorldClick;
        private static int _deferredPans;
        private static float _lastWorldClickAt = -100f;

        /// <summary>这次没点、先平移了镜头：平移结束后由 <see cref="WorldClickSettled"/> 补点（再调一次 <paramref name="retry"/>）。</summary>
        public static void DeferWorldClick(System.Action retry)
        {
            _deferredWorldClick = retry;
            _deferredPans++;
            DeferredPanClicks++;
        }

        /// <summary>本次会话里“目标被界面挡住 / 不在画面里，先平移镜头再在同一次尝试里补点”的次数（报告里写出）。</summary>
        public static int DeferredPanClicks { get; private set; }

        /// <summary>
        /// 步骤看结果之前调：先平移了镜头的，等平移结束补点；点击发出后至少过 <paramref name="settle"/> 真实秒才返回 true。
        /// 平移 6 次还点不到就不再补点、返回 true，交给步骤判失败（照常算一次重试）。
        /// </summary>
        public static bool WorldClickSettled(double stepElapsed, double settle)
        {
            if (stepElapsed < settle)
            {
                return false;
            }
            if (_deferredWorldClick != null)
            {
                if (Holding)
                {
                    return false;
                }
                System.Action retry = _deferredWorldClick;
                _deferredWorldClick = null;
                if (_deferredPans > 6)
                {
                    return true;
                }
                retry();
                return false;
            }
            return Time.realtimeSinceStartup - _lastWorldClickAt >= settle;
        }

        private static readonly List<string> ClickTrace = new List<string>(2);
        private static int _clickTraceFrame = -1;

        /// <summary>最近一次脚本按键（<see cref="PressChord"/>）在按下那一帧被读到时的输入上下文（只读诊断）。</summary>
        public static string LastKeyTrace { get; private set; } = "还没按过键";

        private static int _keyTraceFrame = -1;

        private static void TraceWorldClick(string phase, Vector3 mouse)
        {
            if (_clickTraceFrame == Time.frameCount)
            {
                return;
            }
            _clickTraceFrame = Time.frameCount; // 先占位：下面的判定会再读鼠标位置，不能重入
            bool owns = InputRouter.Owns(InputScope.Strategy);
            bool blocked = InputRouter.IsUiPointerBlocked();
            string cover = blocked ? UiCoverAt(mouse) ?? "全面板拾取没有命中：可拖动窗口的边界或界面捕获了指针" : null;
            Camera cam = Cam;
            string hit = cam == null ? "（没有镜头）"
                : Physics.Raycast(cam.ScreenPointToRay(mouse), out RaycastHit h, 500f) ? $"{h.collider.name}#{h.collider.GetInstanceID()}（{h.point:F1}）" : "什么都没打到";
            var home = GameLogic.Stage.GameRoot.HomeValley;
            string extra = home != null
                ? $"、建造模式{(home.BuildMode != null && home.BuildMode.IsOpen ? "开" : "关")}、武装命令 {(home.SquadCommands?.ArmedKind?.ToString() ?? "无")}"
                : string.Empty;
            ClickTrace.Add($"{phase}帧 {Time.frameCount}：战略输入{(owns ? "有" : $"无（域 {InputRouter.Scope}、模态 {InputRouter.ModalUiOpen}、键盘让位 {InputRouter.KeyboardSuppressed}）")}、" +
                           $"界面拦截{(blocked ? "是：" + cover : "否")}、射线先打到 {hit}{extra}、光标 ({mouse.x:F0},{mouse.y:F0})、镜头 {CamPose()}");
        }

        private static int _hostWatchFrame = -1;

        /// <summary>
        /// 旅程宿主每次驱动调用（与游戏读输入的时机无关）：脚本点击的按下 / 抬起帧里，从宿主这一侧再记一次输入状态——
        /// 游戏在抬起帧根本没读鼠标时（战略域没有所有权、镜头在过渡），只有这一条能说明原因。
        /// </summary>
        public static void WatchClickFrames()
        {
            // 按键同理：按下那一帧游戏没读这个键（输入上下文不收，例如镜头在飞的过渡期 = None）时，宿主这一侧记下当时的上下文。
            if (InputRouter.Reader is ScriptReader kr && kr.Key != KeyCode.None && Time.frameCount == kr.KeyFrame && _keyTraceFrame != Time.frameCount)
            {
                LastKeyTrace = $"{kr.Key} 按下帧 {Time.frameCount}（宿主看，游戏这一帧还没读到它）：输入上下文 {InputRouter.ActiveContext}（域 {InputRouter.Scope}、模态 {InputRouter.ModalUiOpen}、" +
                               $"键盘让位 {InputRouter.KeyboardSuppressed}）";
            }
            if (!(InputRouter.Reader is ScriptReader r) || !r.Trace || _hostWatchFrame == Time.frameCount
                || Time.frameCount < r.DownFrame || Time.frameCount > r.UpFrame + 1 || ClickTrace.Count > 12)
            {
                return;
            }
            _hostWatchFrame = Time.frameCount;
            ClickTrace.Add($"宿主看帧 {Time.frameCount}：战略输入{(InputRouter.Owns(InputScope.Strategy) ? "有" : "无")}（域 {InputRouter.Scope}、模态 {InputRouter.ModalUiOpen}、" +
                           $"键盘让位 {InputRouter.KeyboardSuppressed}、上下文 {InputRouter.ActiveContext}）、镜头 {CamPose()}");
        }

        private static string CamPose()
        {
            Camera cam = Cam;
            if (cam == null)
            {
                return "（没有镜头）";
            }
            Vector3 p = cam.transform.position;
            Vector3 e = cam.transform.eulerAngles;
            return $"({p.x:F2},{p.y:F2},{p.z:F2}) 俯仰 {e.x:F1} 偏航 {e.y:F1} 视野 {cam.fieldOfView:F1}";
        }

        private static void TraceKey(KeyCode key)
        {
            if (_keyTraceFrame == Time.frameCount)
            {
                return;
            }
            _keyTraceFrame = Time.frameCount;
            LastKeyTrace = $"{key} 按下帧 {Time.frameCount}：输入上下文 {InputRouter.ActiveContext}（域 {InputRouter.Scope}、模态 {InputRouter.ModalUiOpen}、键盘让位 {InputRouter.KeyboardSuppressed}）";
        }

        /// <summary>FG2-E2E-01：按住 Shift 左键点世界里一点（加选 / 减选：Shift 从按下那一帧起按住 0.4 真实秒，覆盖按下与抬起两帧）。</summary>
        public static void ShiftClickWorld(Vector3 world) => ShiftClickScreen(ScreenOfWorld(world));

        /// <summary>FG5-E2E-01：按住 Shift 左键点屏幕一点（物体上看得见的那一处，见 <see cref="TryVisiblePointOf"/>）。</summary>
        public static void ShiftClickScreen(Vector3 m)
        {
            var r = new ScriptReader
            {
                MouseA = m,
                MouseB = m,
                MouseButton = 0,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
                HoldUntil = Time.realtimeSinceStartup + 0.4f,
                HeldDownFrame = Time.frameCount + 1,
            };
            r.Held.Add(KeyCode.LeftShift);
            InputRouter.DebugSetReader(r);
        }

        /// <summary>左键从 <paramref name="a"/> 拖到 <paramref name="b"/>（框选）：下一帧在 a 按下，再下一帧光标移到 b，第三帧在 b 抬起。</summary>
        public static void Drag(Vector2 a, Vector2 b)
        {
            int f = Time.frameCount;
            InputRouter.DebugSetReader(new ScriptReader
            {
                MouseA = ScreenOf(a),
                MouseB = ScreenOf(b),
                SwitchFrame = f + 2,
                DownFrame = f + 1,
                UpFrame = f + 3,
            });
        }

        // ── uGUI（EventSystem）──────────────────────────────────────────────────

        public static UnityEngine.UI.Button FindActiveButton(string name) =>
            Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(b => b.name == name && b.isActiveAndEnabled && b.interactable);

        /// <summary>按名字找到可点的 uGUI 按钮并经 EventSystem 点一下；失败返回 false，原因在 <see cref="LastUiFailure"/>。</summary>
        public static bool ClickUgui(string name)
        {
            UnityEngine.UI.Button b = FindActiveButton(name);
            if (b == null)
            {
                LastUiFailure = $"找不到可点的按钮 {name}";
                return false;
            }
            return ClickUgui(b);
        }

        /// <summary>经 EventSystem 点一个 uGUI 按钮（见类注释）。</summary>
        public static bool ClickUgui(UnityEngine.UI.Button b)
        {
            if (b == null || !b.isActiveAndEnabled || !b.interactable)
            {
                LastUiFailure = "按钮不存在、未启用或不可交互";
                return false;
            }
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                LastUiFailure = "场景里没有 EventSystem";
                return false;
            }
            var rt = b.transform as RectTransform;
            Canvas canvas = b.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, (corners[0] + corners[2]) * 0.5f);
            var ped = new PointerEventData(es)
            {
                position = screen,
                pressPosition = screen,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                eligibleForClick = true,
                pointerId = -1,
            };
            var hits = new List<RaycastResult>();
            es.RaycastAll(ped, hits);
            GameObject target = b.gameObject;
            if (hits.Count > 0)
            {
                GameObject top = hits[0].gameObject;
                GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(top);
                if (handler != b.gameObject)
                {
                    LastUiFailure = $"按钮 {b.name} 被 {(top != null ? top.name : "?")} 挡住（屏幕点 {screen}）";
                    return false;
                }
                ped.pointerCurrentRaycast = hits[0];
                ped.pointerPressRaycast = hits[0];
                target = top;
                UguiPickedClicks++;
            }
            else
            {
                UguiUnpickedClicks++;
            }
            GameObject pressed = ExecuteEvents.ExecuteHierarchy(target, ped, ExecuteEvents.pointerDownHandler);
            ped.pointerPress = pressed != null ? pressed : b.gameObject;
            ped.rawPointerPress = target;
            ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(b.gameObject, ped, ExecuteEvents.pointerClickHandler);
            LastUiFailure = string.Empty;
            return true;
        }

        // ── UI Toolkit（面板指针事件）────────────────────────────────────────────

        /// <summary>某个宿主（UIDocument 所在对象名）面板上的控件。</summary>
        public static T FindUitk<T>(string hostName, string elementName) where T : VisualElement
        {
            GameObject host = GameObject.Find(hostName);
            UIDocument doc = host != null ? host.GetComponent<UIDocument>() : null;
            return doc?.rootVisualElement?.Q<T>(elementName);
        }

        /// <summary>点一个 UI Toolkit 按钮（向面板派发真实指针事件，见类注释）。按钮被禁用、隐藏、不在面板上或被挡住都点不了。</summary>
        public static bool ClickUitk(string hostName, string buttonName) => ClickElement(FindUitk<VisualElement>(hostName, buttonName));

        /// <summary>向控件所在面板派发一次左键按下 + 抬起（控件中心）。失败返回 false，原因在 <see cref="LastUiFailure"/>。</summary>
        public static bool ClickElement(VisualElement e)
        {
            LastUiTransient = false;
            if (e == null)
            {
                LastUiFailure = "控件不存在";
                return false;
            }
            // FG5-E2E-01：batchmode 不渲染，面板的样式 / 排版不一定每帧刷新——同一帧里刚改过位置（研发树按分支筛选后节点换了位置）时，
            // 拾取看到的是旧排版、派发指针事件时面板才重新排版，点到的是另一个节点。玩家点的永远是渲染出来的那一帧，这里先把排版刷到最新再拾取。
            SyncLayout(e);
            if (!IsClickable(e))
            {
                LastUiFailure = $"控件 {e.name} 被禁用、隐藏或不在面板上（{WhyNotClickable(e)}）";
                return false;
            }
            IPanel panel = e.panel;
            Vector2 center = e.worldBound.center;
            VisualElement top = panel.Pick(center);
            if (top == null || (top != e && !e.Contains(top)))
            {
                if (IsTooltip(top))
                {
                    // FG3-E2E-01：挡住它的是世界悬停提示——脚本光标还停在上一步点过的地面上（玩家去点按钮时光标就在按钮上，世界悬停提示不会出现）。
                    // 把脚本光标移离世界、面板指针移离提示框，提示按离开宽限收起；这次不点，调用方下一次再点（步骤重试）。
                    RetreatFromTooltip(panel);
                    MarkTransient();
                    LastUiFailure = $"控件 {e.name} 被世界悬停提示挡着：光标移离世界，下一帧再点（{TooltipState()}）";
                    return false;
                }
                if (IsToast(top))
                {
                    // FG5-E2E-01：通知弹出条（右侧一列，约 4 秒后收起）正好盖在面板的按钮上——玩家会等它收起再点，算暂时的。
                    MarkTransient();
                    ToastWaits++;
                    LastUiFailure = $"控件 {e.name} 被通知弹出条挡着（{(top as VisualElement)?.Q<Label>("ToastText")?.text ?? Describe(top)}），等它收起再点";
                    return false;
                }
                LastUiFailure = $"控件 {e.name} 被同一面板上的 {Describe(top)} 挡住（面板点 {center}）";
                return false;
            }
            string cover = CoveredByOtherPanel(panel, center);
            if (cover != null)
            {
                LastUiFailure = $"控件 {e.name} 被排序更高的面板上的 {cover} 挡住";
                return false;
            }
            // 玩家点之前光标先移到控件上（悬停）：悬停会改界面的（研发树悬停节点换详情）先在这一帧生效、刷新排版，再确认光标下还是它。
            SendPointer(panel, center, EventType.MouseMove);
            SyncLayout(e);
            VisualElement afterHover = panel.Pick(center);
            if (afterHover == null || (afterHover != e && !e.Contains(afterHover)))
            {
                MarkTransient();
                LastUiFailure = $"光标移到控件 {e.name} 上之后界面排版变了，光标下变成 {Describe(afterHover)}（控件框 {e.worldBound}）";
                return false;
            }
            Rect before = e.worldBound;
            SendPointer(panel, center, EventType.MouseDown);
            SyncLayout(e);
            SendPointer(panel, center, EventType.MouseUp);
            SyncLayout(e);
            // 点完光标就离开（玩家点完就去点下一个控件）：这个控件的悬停提示按离开宽限收起，不会挡住紧挨着的下一个按钮。
            SendPointer(panel, new Vector2(-100f, -100f), EventType.MouseMove);
            LastClickNote = e.panel != null && e.worldBound != before ? $"点击后控件 {e.name} 从 {before} 移到 {e.worldBound}" : string.Empty;
            UitkClicks++;
            LastUiFailure = string.Empty;
            return true;
        }

        /// <summary>本次会话里在 UI Toolkit 面板上按住左键拖动（拖画布平移）的次数。</summary>
        public static int UitkDrags { get; private set; }

        /// <summary>
        /// 像玩家一样在 <paramref name="area"/> 里找一处空白（拾取到的是 <paramref name="isEmpty"/> 认可的元素，不是按钮），按住左键拖动 <paramref name="delta"/> 再松开
        /// （研发树画布：拖空白处平移）。找不到空白或终点出了面板返回 false 与原因。
        /// </summary>
        public static bool DragUitk(VisualElement area, Vector2 delta, System.Func<VisualElement, bool> isEmpty, out string why)
        {
            why = null;
            if (area?.panel == null)
            {
                why = "拖动区域不在面板上";
                return false;
            }
            SyncLayout(area);
            IPanel panel = area.panel;
            Rect r = area.worldBound;
            for (int iy = 1; iy <= 7; iy++)
            {
                for (int ix = 1; ix <= 9; ix++)
                {
                    var from = new Vector2(r.xMin + r.width * ix / 10f, r.yMin + r.height * iy / 8f);
                    VisualElement top = panel.Pick(from);
                    if (top == null || !isEmpty(top) || CoveredByOtherPanel(panel, from) != null)
                    {
                        continue;
                    }
                    Vector2 to = from + delta;
                    SendPointer(panel, from, EventType.MouseMove);
                    SendPointer(panel, from, EventType.MouseDown);
                    SendPointer(panel, Vector2.Lerp(from, to, 0.5f), EventType.MouseMove);
                    SendPointer(panel, to, EventType.MouseMove);
                    SendPointer(panel, to, EventType.MouseUp);
                    SyncLayout(area);
                    UitkDrags++;
                    return true;
                }
            }
            why = $"{area.name} 里找不到可以按住拖动的空白处";
            return false;
        }

        /// <summary>最近一次 UI Toolkit 点击失败是暂时的（悬停提示 / 通知弹出条正在收起、悬停后界面排版刚变）：过一会儿再点就行，不必算一次重试。</summary>
        public static bool LastUiTransient { get; private set; }

        /// <summary>上一次暂时失败的真实时刻（秒）；旅程框架只把 2 秒内的暂时失败算作“这一步的重试是暂时的”。</summary>
        public static float LastUiTransientAt { get; private set; } = -100f;

        /// <summary>本次会话里因为通知弹出条盖住按钮而等它收起的次数。</summary>
        public static int ToastWaits { get; private set; }

        private static void MarkTransient()
        {
            LastUiTransient = true;
            LastUiTransientAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// FG5-E2E-01 修复轮（审查 P2）：旅程框架在每一步的每次尝试开始时调用——上一步 / 上一次尝试留下的“暂时失败”不算到这一次头上
        /// （原来只在 <see cref="ClickElement"/> 开头清零，同一两秒内与点击无关的失败也会被当成暂时遮挡免费重试）。
        /// </summary>
        public static void BeginStepAttempt()
        {
            LastUiTransient = false;
            LastUiFailure = string.Empty;
            _deferredWorldClick = null; // 上一步没补完的点击不带进这一步
            _deferredPans = 0;
            ClickTrace.Clear();
        }

        /// <summary>自检用：模拟一次暂时的 UI 失败（通知弹出条挡着之类），不碰任何面板。</summary>
        internal static void DebugMarkTransient(string failure)
        {
            MarkTransient();
            LastUiFailure = failure ?? string.Empty;
        }

        /// <summary>元素属于通知弹出条（NotificationHud 的 uk-toast）。</summary>
        private static bool IsToast(VisualElement e)
        {
            for (VisualElement p = e; p != null; p = p.parent)
            {
                if (p.ClassListContains("uk-toast") || p.name == "ToastList")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>最近一次 UI Toolkit 点击之后控件有没有挪位置（诊断用；没挪是空串）。</summary>
        public static string LastClickNote { get; private set; } = string.Empty;

        /// <summary>本次会话里为了让控件进入可见区发出的滚轮次数。</summary>
        public static int WheelScrolls { get; private set; }

        /// <summary>
        /// 列表里的控件不在可见区时（被滚出视口、被下面的文字盖住），像玩家一样在列表上滚一下滚轮：向面板派发一次真实的滚轮事件（位置 = 列表视口中心）。
        /// 返回 true = 控件已经完整在可见区（不用滚）；false = 这次滚了一下，等下一帧布局更新后再看。
        /// </summary>
        public static bool ScrollIntoView(ScrollView list, VisualElement e)
        {
            if (list?.panel == null || e == null)
            {
                return true;
            }
            Rect vp = list.contentViewport.worldBound;
            Rect r = e.worldBound;
            bool inY = r.yMin >= vp.yMin - 0.5f && r.yMax <= vp.yMax + 0.5f;
            bool inX = r.xMin >= vp.xMin - 0.5f && r.xMax <= vp.xMax + 0.5f;
            if (inY && inX)
            {
                return true;
            }
            // 滚轮落点（列表视口中心）被世界悬停提示挡着：脚本光标还停在上一步点过的地面上（玩家滚列表时光标就在列表上，不会有世界悬停提示）。
            // 先把光标移离世界，提示收起后下一帧再滚（与 ClickElement 同一处理）；被别的东西挡着就照实报出来，不隔着它滚。
            VisualElement at = list.panel.Pick(vp.center);
            if (at != null && at != list && !list.Contains(at))
            {
                if (IsTooltip(at))
                {
                    RetreatFromTooltip(list.panel);
                    LastUiFailure = $"列表 {list.name} 被世界悬停提示挡着：光标移离世界，下一帧再滚（{TooltipState()}）";
                    return false;
                }
                LastUiFailure = $"列表 {list.name} 的滚轮落点被 {Describe(at)} 挡住";
                return false;
            }
            // 在视口下方 / 右边 → 向下 / 向右滚（只能横向滚的列表，滚轮的纵向量也按横向滚）。
            float dir = !inY ? (r.yMax > vp.yMax ? 1f : -1f) : (r.xMax > vp.xMax ? 1f : -1f);
            Vector2 delta = !inY ? new Vector2(0f, dir * 3f) : new Vector2(dir * 3f, dir * 3f);
            var ime = new Event { type = EventType.ScrollWheel, delta = delta, mousePosition = vp.center, modifiers = EventModifiers.None };
            using (WheelEvent w = WheelEvent.GetPooled(ime))
            {
                list.panel.visualTree.SendEvent(w);
            }
            WheelScrolls++;
            return false;
        }

        private static void SendPointer(IPanel panel, Vector2 panelPoint, EventType type)
        {
            var ime = new Event
            {
                type = type,
                mousePosition = panelPoint,
                button = 0,
                clickCount = 1,
                modifiers = EventModifiers.None,
            };
            if (type == EventType.MouseDown)
            {
                using (PointerDownEvent down = PointerDownEvent.GetPooled(ime))
                {
                    panel.visualTree.SendEvent(down);
                }
            }
            else if (type == EventType.MouseMove)
            {
                using (PointerMoveEvent move = PointerMoveEvent.GetPooled(ime))
                {
                    panel.visualTree.SendEvent(move);
                }
            }
            else
            {
                using (PointerUpEvent up = PointerUpEvent.GetPooled(ime))
                {
                    panel.visualTree.SendEvent(up);
                }
            }
        }

        /// <summary>排序更高的其他 UI Toolkit 面板在同一屏幕点有没有可见控件 / 窗口（与世界点击拦截同一判据）。有就返回挡住它的元素描述。</summary>
        private static string CoveredByOtherPanel(IPanel panel, Vector2 panelPoint)
        {
            UIDocument own = null;
            UIDocument[] docs = Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (UIDocument d in docs)
            {
                if (d != null && d.rootVisualElement?.panel == panel)
                {
                    own = d;
                    break;
                }
            }
            if (own == null || own.panelSettings == null || !TryPanelToScreen(panel, panelPoint, out Vector2 screenTopLeft))
            {
                return null;
            }
            float order = own.panelSettings.sortingOrder;
            foreach (UIDocument d in docs)
            {
                IPanel other = d != null ? d.rootVisualElement?.panel : null;
                if (other == null || other == panel || d.panelSettings == null || d.panelSettings.sortingOrder <= order)
                {
                    continue;
                }
                Vector2 p = RuntimePanelUtils.ScreenToPanel(other, screenTopLeft);
                if (UiWindowFocus.BlocksWorldPointerAt(other, p))
                {
                    return $"{d.gameObject.name}/{Describe(other.Pick(p))}";
                }
            }
            return null;
        }

        /// <summary>
        /// FG3-E2E-01：屏幕点（左下角原点，与 <see cref="ScreenOf"/> 同一口径）上有没有可见的 UI Toolkit 控件 / 窗口挡住世界点击
        /// （与世界点击拦截 <see cref="UiWindowFocus.BlocksWorldPointerAt"/> 同一判据）。挡住返回挡住它的元素描述，没挡住返回 null。
        /// 运行时的世界点击拦截读的是真实鼠标位置（batchmode 下固定在窗口角上），旅程点地面之前用它按脚本光标的位置核对一遍：
        /// 目标在建造栏、左侧停靠面板等界面下面时，像玩家一样先平移镜头把目标带出来再点，不“隔着界面点地面”。
        /// </summary>
        public static string UiCoverAt(Vector3 screen)
        {
            var topLeft = new Vector2(screen.x, Screen.height - screen.y);
            foreach (UIDocument d in Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                IPanel p = d != null ? d.rootVisualElement?.panel : null;
                if (p == null)
                {
                    continue;
                }
                Vector2 pp = RuntimePanelUtils.ScreenToPanel(p, topLeft);
                if (UiWindowFocus.BlocksWorldPointerAt(p, pp))
                {
                    return $"{d.gameObject.name}/{Describe(p.Pick(pp))}";
                }
            }
            return null;
        }

        /// <summary>本次会话里因为物体中心被界面挡住 / 被别的东西挡住、改点它身上另一处看得见的地方的次数（报告里写出）。</summary>
        public static int AlternatePointClicks { get; set; }

        /// <summary>最近一次 <see cref="TryVisiblePointOf"/> 选点的说明（中心点为什么点不到、改点了哪里；诊断用）。</summary>
        public static string LastVisiblePointNote { get; private set; } = string.Empty;

        /// <summary>
        /// FG5-E2E-01：物体（建筑、机器模型）上一个“玩家此刻点得到”的屏幕点——在画面里（离边缘至少 3%）、没被界面挡住（与世界点击拦截同一判据 <see cref="UiCoverAt"/>）、
        /// 从镜头沿这一点的射线最先打到的就是它自己（或它的子物体）。按碰撞体包围盒顶面 5×5 采样，从中心往外找。
        /// 背景：镜头改成倾斜透视（FG3-GEN-01 后续）后，开局建筑的中心常落在左下角的区域指挥栏下面——玩家会点建筑露出来的那一部分，旅程同样如此，而不是隔着界面点。
        /// 找不到返回 false（<paramref name="why"/> 写原因，调用方先平移镜头再试）。
        /// </summary>
        public static bool TryVisiblePointOf(Transform target, out Vector3 screen, out string why)
        {
            screen = OffScreen;
            why = null;
            Camera cam = Cam;
            if (target == null || cam == null)
            {
                why = "没有目标或镜头";
                return false;
            }
            Collider[] cols = target.GetComponentsInChildren<Collider>();
            Bounds b = new Bounds(target.position, Vector3.zero);
            bool any = false;
            foreach (Collider col in cols)
            {
                if (col == null || !col.enabled)
                {
                    continue;
                }
                if (!any)
                {
                    b = col.bounds;
                    any = true;
                }
                else
                {
                    b.Encapsulate(col.bounds);
                }
            }
            if (!any)
            {
                why = "目标没有碰撞体";
                return false;
            }
            const int n = 5;
            var samples = new List<(float d, Vector3 p)>(n * n);
            for (int ix = 0; ix < n; ix++)
            {
                for (int iz = 0; iz < n; iz++)
                {
                    float fx = (ix + 0.5f) / n;
                    float fz = (iz + 0.5f) / n;
                    var p = new Vector3(Mathf.Lerp(b.min.x, b.max.x, fx), b.max.y - 0.01f, Mathf.Lerp(b.min.z, b.max.z, fz));
                    samples.Add(((fx - 0.5f) * (fx - 0.5f) + (fz - 0.5f) * (fz - 0.5f), p));
                }
            }
            samples.Sort((x, y) => x.d.CompareTo(y.d));
            string firstWhy = null;
            foreach ((float _, Vector3 p) in samples)
            {
                Vector3 s = ScreenOfWorld(p);
                if (s.x < Screen.width * 0.03f || s.x > Screen.width * 0.97f || s.y < Screen.height * 0.03f || s.y > Screen.height * 0.97f)
                {
                    firstWhy ??= "不在画面里";
                    continue;
                }
                string cover = UiCoverAt(s);
                if (cover != null)
                {
                    firstWhy ??= "被界面挡住（" + cover + "）";
                    continue;
                }
                if (!Physics.Raycast(cam.ScreenPointToRay(s), out RaycastHit hit, 1000f) || hit.collider == null
                    || (hit.collider.transform != target && !hit.collider.transform.IsChildOf(target)))
                {
                    firstWhy ??= "被别的东西挡着（" + (hit.collider != null ? hit.collider.name : "射线没打到") + "）";
                    continue;
                }
                if (samples.Count > 0 && (p - samples[0].p).sqrMagnitude > 1e-4f)
                {
                    AlternatePointClicks++;
                }
                LastVisiblePointNote = (firstWhy == null ? "中心点就点得到" : "中心点" + firstWhy) + $"，点 ({s.x:F0},{s.y:F0})；目标 {target.name}#{target.GetInstanceID()} 位置 {target.position}、" +
                                       $"碰撞体 {cols.Length} 个 [{string.Join("、", cols.Where(x => x != null).Take(6).Select(x => x.name + (x.isTrigger ? "(触发)" : string.Empty) + x.bounds.center.ToString("F1")))}]、" +
                                       $"包围盒中心 {b.center:F1} 尺寸 {b.size:F1}、射线先打到 {hit.collider.name}#{hit.collider.GetInstanceID()}（{hit.point:F1}）";
                screen = s;
                return true;
            }
            why = firstWhy ?? "没有点得到的地方";
            return false;
        }

        /// <summary>面板坐标 → 屏幕坐标（左上角原点，与 <see cref="RuntimePanelUtils.ScreenToPanel"/> 的输入同一口径）：按两点反解线性映射。</summary>
        private static bool TryPanelToScreen(IPanel panel, Vector2 panelPoint, out Vector2 screenTopLeft)
        {
            Vector2 p0 = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
            Vector2 p1 = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(100f, 100f));
            Vector2 scale = (p1 - p0) / 100f;
            if (Mathf.Abs(scale.x) < 1e-6f || Mathf.Abs(scale.y) < 1e-6f)
            {
                screenTopLeft = Vector2.zero;
                return false;
            }
            screenTopLeft = new Vector2((panelPoint.x - p0.x) / scale.x, (panelPoint.y - p0.y) / scale.y);
            return true;
        }

        /// <summary>挡住控件的元素：自己与上面三层（名字，没名字写类型与第一个样式类），诊断用。</summary>
        private static string Describe(VisualElement e)
        {
            if (e == null)
            {
                return "（无）";
            }
            var parts = new List<string>(4);
            for (VisualElement p = e; p != null && parts.Count < 4; p = p.parent)
            {
                string cls = p.GetClasses().FirstOrDefault();
                parts.Add(!string.IsNullOrEmpty(p.name) ? p.name : p.GetType().Name + (cls != null ? "." + cls : string.Empty));
            }
            return string.Join(" < ", parts);
        }

        /// <summary>本次会话里因为世界悬停提示挡着、先把光标移离世界再点的次数（报告里写出）。</summary>
        public static int TooltipRetreats { get; private set; }

        /// <summary>
        /// 光标离开世界悬停提示：脚本光标移到窗口外（世界悬停源收到“离开”），面板指针也移到面板外——
        /// 之前派发到面板上的点击会把面板指针留在那一点，提示框后来出现在那一点下面时会被当成“指针停在提示框上”而一直不收起。
        /// 玩家的鼠标会从提示框上移开，这里补上这次移动。
        /// </summary>
        private static float _lastRetreat = -10f;

        private static void RetreatFromTooltip(IPanel panel)
        {
            // 一次移开就够了：提示按离开宽限（0.25 真实秒）自己收起。限频是为了不逐帧重建脚本输入（batchmode 不渲染，一帧不到 1 毫秒）；调用方按真实时间等提示收起。
            if (Time.realtimeSinceStartup - _lastRetreat < 0.6f)
            {
                return;
            }
            _lastRetreat = Time.realtimeSinceStartup;
            ReleaseKeys();
            if (panel?.visualTree != null)
            {
                var ime = new Event { type = EventType.MouseMove, mousePosition = new Vector2(-100f, -100f), modifiers = EventModifiers.None };
                using (PointerMoveEvent mv = PointerMoveEvent.GetPooled(ime))
                {
                    panel.visualTree.SendEvent(mv);
                }
            }
            TooltipRetreats++;
        }

        /// <summary>失败说明里写出挡着的是哪条提示、指针是否已离开、是否固定（诊断用，只读公开状态）。</summary>
        private static string TooltipState() =>
            $"提示“{GameLogic.UI.Kit.UiTooltip.Content?.Title}”，世界对象 {GameLogic.UI.Kit.UiTooltip.WorldKey}，悬停在世界上 {GameLogic.UI.Kit.UiTooltip.HoveringWorld}，固定 {GameLogic.UI.Kit.UiTooltip.IsPinned}";

        /// <summary>元素属于悬停提示（UiKitOverlay 的 “Tooltip” 节点或其子节点）。</summary>
        private static bool IsTooltip(VisualElement e)
        {
            for (VisualElement p = e; p != null; p = p.parent)
            {
                if (p.name == "Tooltip")
                {
                    return true;
                }
            }
            return false;
        }

        private static System.Reflection.MethodInfo _validateLayout;

        /// <summary>把控件所在面板的样式与排版刷到最新（等同渲染前的那次排版）；batchmode 不渲染时拾取 / 读位置前先调。</summary>
        public static void SyncLayout(VisualElement e)
        {
            IPanel panel = e?.panel;
            if (panel == null)
            {
                return;
            }
            if (_validateLayout == null || _validateLayout.DeclaringType == null || !_validateLayout.DeclaringType.IsInstanceOfType(panel))
            {
                // 整棵树的更新（样式 → 排版 → 变换与裁剪 → 绑定），等同渲染前那一次；只刷排版（ValidateLayout）时变换缓存可能还是旧的，
                // 按钮在抬起时判断“光标还在不在我上面”用的是旧变换，点击就丢了（研发树筛选后实测）。
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                _validateLayout = panel.GetType().GetMethod("UpdateWithoutRepaint", all, null, System.Type.EmptyTypes, null)
                                  ?? panel.GetType().GetMethod("ValidateLayout", all, null, System.Type.EmptyTypes, null);
            }
            _validateLayout?.Invoke(panel, null);
        }

        /// <summary>控件为什么点不了：不在面板上 / 哪一层被禁用 / 哪一层隐藏。只用于失败说明。</summary>
        public static string WhyNotClickable(VisualElement e)
        {
            if (e == null)
            {
                return "控件不存在";
            }
            if (e.panel == null)
            {
                return "不在面板上";
            }
            for (VisualElement p = e; p != null; p = p.parent)
            {
                string who = string.IsNullOrEmpty(p.name) ? p.GetType().Name : p.name;
                if (!p.enabledSelf)
                {
                    return who + " 被禁用";
                }
                if (p.resolvedStyle.display == DisplayStyle.None)
                {
                    return who + " display:none";
                }
                if (p.resolvedStyle.visibility == Visibility.Hidden)
                {
                    return who + " visibility:hidden";
                }
            }
            return e.visible ? "原因不明" : "visible=false";
        }

        /// <summary>玩家此刻能不能点到它：在面板上、启用（含祖先）、自己和祖先都没有 display:none / visibility:hidden。</summary>
        public static bool IsClickable(VisualElement e)
        {
            if (e == null || e.panel == null || !e.enabledInHierarchy || !e.visible)
            {
                return false;
            }
            for (VisualElement p = e; p != null; p = p.parent)
            {
                if (p.resolvedStyle.display == DisplayStyle.None || p.resolvedStyle.visibility == Visibility.Hidden)
                {
                    return false;
                }
            }
            return true;
        }

        private sealed class ScriptReader : IInputReader
        {
            public KeyCode Key = KeyCode.None;
            public int KeyFrame = -1;
            /// <summary>组合键的修饰键（与 <see cref="Key"/> 同一帧按住）。</summary>
            public KeyCode ChordHeld = KeyCode.None;
            /// <summary>持续按住的键（到 <see cref="HoldUntil"/> 真实时刻为止）。</summary>
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public float HoldUntil = -1f;
            /// <summary>按住的键在哪一帧报告“按下”（之后才算按住）。</summary>
            public int HeldDownFrame = -1;
            public Vector3 MouseA = OffScreen;
            public Vector3 MouseB = OffScreen;
            public int SwitchFrame = -1;
            public int DownFrame = -1;
            public int UpFrame = -1;
            public int MouseButton;

            public bool GetKey(KeyCode key) =>
                key != KeyCode.None && ((ChordHeld == key && Time.frameCount == KeyFrame)
                                        || (Held.Contains(key) && Time.frameCount >= HeldDownFrame && Time.realtimeSinceStartup < HoldUntil));

            /// <summary>FG5-E2E-01 修复轮：这次点击在按下 / 抬起那一帧记下世界层的输入状态（<see cref="LastWorldClickTrace"/>）。</summary>
            public bool Trace;

            public bool GetKeyDown(KeyCode key)
            {
                if (key == KeyCode.None)
                {
                    return false;
                }
                if (key == Key && Time.frameCount == KeyFrame)
                {
                    TraceKey(key);
                    return true;
                }
                return Held.Contains(key) && Time.frameCount == HeldDownFrame;
            }

            public bool GetMouseButtonDown(int button)
            {
                bool down = button == MouseButton && Time.frameCount == DownFrame;
                if (down && Trace)
                {
                    TraceWorldClick("按下", MousePosition);
                }
                return down;
            }

            public bool GetMouseButtonUp(int button)
            {
                bool up = button == MouseButton && Time.frameCount == UpFrame;
                if (up && Trace)
                {
                    TraceWorldClick("抬起", MousePosition);
                }
                return up;
            }
            public Vector3 MousePosition => SwitchFrame >= 0 && Time.frameCount >= SwitchFrame ? MouseB : MouseA;
            public float MouseScrollDelta => 0f;
        }
    }
}
