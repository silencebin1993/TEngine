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
            InputRouter.DebugSetReader(new ScriptReader
            {
                MouseA = m,
                MouseB = m,
                MouseButton = button,
                DownFrame = Time.frameCount + 1,
                UpFrame = Time.frameCount + 2,
            });
        }

        /// <summary>FG2-E2E-01：按住 Shift 左键点世界里一点（加选 / 减选：Shift 从按下那一帧起按住 0.4 真实秒，覆盖按下与抬起两帧）。</summary>
        public static void ShiftClickWorld(Vector3 world)
        {
            Vector3 m = ScreenOfWorld(world);
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
            if (e == null)
            {
                LastUiFailure = "控件不存在";
                return false;
            }
            if (!IsClickable(e))
            {
                LastUiFailure = $"控件 {e.name} 被禁用、隐藏或不在面板上";
                return false;
            }
            IPanel panel = e.panel;
            Vector2 center = e.worldBound.center;
            VisualElement top = panel.Pick(center);
            if (top == null || (top != e && !e.Contains(top)))
            {
                LastUiFailure = $"控件 {e.name} 被同一面板上的 {Describe(top)} 挡住（面板点 {center}）";
                return false;
            }
            string cover = CoveredByOtherPanel(panel, center);
            if (cover != null)
            {
                LastUiFailure = $"控件 {e.name} 被排序更高的面板上的 {cover} 挡住";
                return false;
            }
            SendPointer(panel, center, EventType.MouseDown);
            SendPointer(panel, center, EventType.MouseUp);
            UitkClicks++;
            LastUiFailure = string.Empty;
            return true;
        }

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

        private static string Describe(VisualElement e) => e == null ? "（无）" : string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name;

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

            public bool GetKeyDown(KeyCode key) =>
                key != KeyCode.None && ((key == Key && Time.frameCount == KeyFrame) || (Held.Contains(key) && Time.frameCount == HeldDownFrame));
            public bool GetMouseButtonDown(int button) => button == MouseButton && Time.frameCount == DownFrame;
            public bool GetMouseButtonUp(int button) => button == MouseButton && Time.frameCount == UpFrame;
            public Vector3 MousePosition => SwitchFrame >= 0 && Time.frameCount >= SwitchFrame ? MouseB : MouseA;
            public float MouseScrollDelta => 0f;
        }
    }
}
