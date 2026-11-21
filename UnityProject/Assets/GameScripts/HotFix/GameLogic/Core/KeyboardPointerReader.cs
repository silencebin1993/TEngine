using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.Core
{
    /// <summary>FG15-UX-02（FGR-UX-060 可访问性；DEBT-FG2FW03-04 / FG3LOG01-07 / FG3LOG07-05 / FG3LOG08-05 / FG6DEF03-04 / FG6LOG10-03）：
    /// 键盘指针。设置“键盘指针”开着时，小键盘 8 / 2 / 4 / 6（可重绑，<see cref="GameActionId.PointerUp"/> 等）移动一个虚拟指针，
    /// 小键盘 5 = 左键（按住 = 拖动 / 拉框）、小键盘 0 = 右键（命令 / 取消）。
    /// 世界一侧：装饰在 <see cref="UnityInputReader"/> 外面，<see cref="InputRouter"/> 读到的指针位置与鼠标键就是虚拟指针的——
    /// 悬停看读数（单位标签、液洼）、框选复制 / 圈重建区域、点格子放快捷栏，全部走原有鼠标路径，不另写一套。
    /// 界面一侧：在虚拟指针位置向 UI Toolkit 面板派发 PointerMove / Down / Up，按钮、列表行、拖放与真鼠标一样响应。
    /// 真鼠标一动，虚拟指针就跳回真鼠标处（两者可以混用）。关着时零开销直通。</summary>
    public sealed class KeyboardPointerReader : IInputReader
    {
        public static readonly KeyboardPointerReader Default = new KeyboardPointerReader(UnityInputReader.Instance);

        private readonly IInputReader _inner;
        private Vector2 _pos;
        private Vector3 _lastReal;
        private bool _init;
        private int _frame = -1;
        private bool _leftHeld, _leftDown, _leftUp, _rightDown, _rightHeld, _rightUp;
        private bool _uiPress;

        /// <summary>自检注入：屏幕尺寸（编辑器 batchmode 下 Screen 可能是 0 或很小）。</summary>
        public Vector2Int ScreenSize = new Vector2Int(0, 0);

        /// <summary>自检注入：当前要派发界面指针事件的面板根（为空时找场景里的 UIDocument）。</summary>
        public VisualElement UiRootOverride;

        public KeyboardPointerReader(IInputReader inner)
        {
            _inner = inner;
        }

        public static bool Enabled => Settings.GameSettings.KeyboardPointerEnabled;

        /// <summary>虚拟指针当前位置（屏幕像素，左下原点）。</summary>
        public Vector2 Position => _pos;

        /// <summary>虚拟指针是否在画（用过键盘指针之后为真，真鼠标一动就为假）。</summary>
        public bool Active { get; private set; }

        public bool GetKey(KeyCode key)
        {
            Sync();
            return _inner.GetKey(key) || (key == KeyCode.Mouse0 && _leftHeld && !_uiPress) || (key == KeyCode.Mouse1 && _rightHeld);
        }

        public bool GetKeyDown(KeyCode key)
        {
            Sync();
            return _inner.GetKeyDown(key) || (key == KeyCode.Mouse0 && _leftDown && !_uiPress) || (key == KeyCode.Mouse1 && _rightDown);
        }

        public bool GetMouseButtonDown(int button)
        {
            Sync();
            return _inner.GetMouseButtonDown(button) || (button == 0 && _leftDown && !_uiPress) || (button == 1 && _rightDown);
        }

        public bool GetMouseButtonUp(int button)
        {
            Sync();
            return _inner.GetMouseButtonUp(button) || (button == 0 && _leftUp && !_uiPress) || (button == 1 && _rightUp);
        }

        public Vector3 MousePosition
        {
            get
            {
                Sync();
                return Active ? new Vector3(_pos.x, _pos.y, 0f) : _inner.MousePosition;
            }
        }

        public float MouseScrollDelta => _inner.MouseScrollDelta;

        private void Sync()
        {
            if (!Application.isPlaying || _frame == Time.frameCount)
            {
                return;
            }
            _frame = Time.frameCount;
            Step(Time.unscaledDeltaTime);
        }

        /// <summary>推进一帧（运行时每帧第一次读取时自动调；自检直接调）。</summary>
        public void Step(float dt)
        {
            _leftDown = _leftUp = _rightDown = _rightUp = false;
            if (!Enabled)
            {
                if (_leftHeld && _uiPress)
                {
                    SendUi(UiPointer.Up);
                }
                Active = false;
                _leftHeld = _rightHeld = _uiPress = false;
                if (_marker != null)
                {
                    _marker.style.display = DisplayStyle.None;
                }
                return;
            }
            Vector3 real = _inner.MousePosition;
            if (!_init || (real - _lastReal).sqrMagnitude > 1f)
            {
                _pos = real; // 真鼠标动了：虚拟指针跟到真鼠标处。
                _init = true;
                Active = false;
            }
            _lastReal = real;

            Vector2 dir = Vector2.zero;
            if (Held(GameActionId.PointerUp)) dir.y += 1f;
            if (Held(GameActionId.PointerDown)) dir.y -= 1f;
            if (Held(GameActionId.PointerLeft)) dir.x -= 1f;
            if (Held(GameActionId.PointerRight)) dir.x += 1f;
            if (dir != Vector2.zero)
            {
                float speed = UiTuningValues.Get("access.kb_pointer_speed");
                bool fast = _inner.GetKey(KeyCode.LeftShift) || _inner.GetKey(KeyCode.RightShift);
                Vector2 size = ScreenDims();
                _pos += dir.normalized * speed * (fast ? 3f : 1f) * Mathf.Max(0f, dt);
                _pos = new Vector2(Mathf.Clamp(_pos.x, 0f, size.x - 1f), Mathf.Clamp(_pos.y, 0f, size.y - 1f));
                Active = true;
                SendUi(UiPointer.Move);
            }

            bool left = Held(GameActionId.PointerClick);
            if (left && !_leftHeld)
            {
                Active = true;
                _leftDown = true;
                // 指针下面是界面元素：这一下只给界面（与真鼠标点在面板上时世界收不到点击一致）。
                _uiPress = SendUi(UiPointer.Down);
            }
            else if (!left && _leftHeld)
            {
                _leftUp = true;
                if (_uiPress)
                {
                    SendUi(UiPointer.Up);
                }
            }
            _leftHeld = left;
            if (!left && !_leftUp)
            {
                _uiPress = false;
            }

            bool right = Held(GameActionId.PointerRightClick);
            _rightDown = right && !_rightHeld;
            _rightUp = !right && _rightHeld;
            _rightHeld = right;
            if (_rightDown)
            {
                Active = true;
            }
            UpdateMarker();
        }

        private VisualElement _marker;

        /// <summary>自检用：画在面板上的指针标记（没画时为 null 或隐藏）。</summary>
        public VisualElement DebugMarker => _marker;

        /// <summary>虚拟指针没有系统光标可借（工程未引用 Input System，不能挪真光标），在面板最上层画一个十字圈标记。</summary>
        private void UpdateMarker()
        {
            VisualElement root = UiRootOverride ?? FindUiRoot();
            if (root?.panel == null)
            {
                return;
            }
            if (_marker == null || _marker.panel != root.panel)
            {
                _marker?.RemoveFromHierarchy();
                _marker = new VisualElement { name = "KbPointerMarker", pickingMode = PickingMode.Ignore };
                // 标记挂在面板根（visualTree）上，各面板 UXML 引用的 UiKit.uss 管不到这一层，外观只能写行内样式；
                // 位置（left / top）是随指针变化的运行时数值。
                _marker.style.position = UnityEngine.UIElements.Position.Absolute;
                _marker.style.width = 18f;
                _marker.style.height = 18f;
                _marker.style.borderTopWidth = _marker.style.borderBottomWidth = _marker.style.borderLeftWidth = _marker.style.borderRightWidth = 2f;
                Color c = new Color(1f, 0.9f, 0f);
                _marker.style.borderTopColor = _marker.style.borderBottomColor = _marker.style.borderLeftColor = _marker.style.borderRightColor = c;
                _marker.style.borderTopLeftRadius = _marker.style.borderTopRightRadius = _marker.style.borderBottomLeftRadius = _marker.style.borderBottomRightRadius = 9f;
                _marker.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
                root.Add(_marker);
            }
            _marker.style.display = Active ? DisplayStyle.Flex : DisplayStyle.None;
            if (Active)
            {
                Vector2 p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(_pos.x, ScreenDims().y - _pos.y));
                _marker.style.left = p.x - 9f;
                _marker.style.top = p.y - 9f;
                _marker.BringToFront();
            }
        }

        /// <summary>自检用：把虚拟指针放到某处（当作用过一次键盘指针）。</summary>
        public void DebugWarp(Vector2 screenPos)
        {
            _pos = screenPos;
            _init = true;
            _lastReal = _inner.MousePosition;
            Active = true;
        }

        private bool Held(GameActionId action)
        {
            KeyCode key = Settings.GameSettings.KeyBindings.GetChord(action).Key;
            // 文本框有焦点 / 正在改键时让位（在搜索框里用小键盘打数字不能挪指针、按 5 也不能点击）。
            return key != KeyCode.None && !InputRouter.KeyboardSuppressed && _inner.GetKey(key);
        }

        private Vector2 ScreenDims() => ScreenSize.x > 0
            ? new Vector2(ScreenSize.x, ScreenSize.y)
            : new Vector2(Mathf.Max(1, UnityEngine.Screen.width), Mathf.Max(1, UnityEngine.Screen.height));

        private enum UiPointer { Move, Down, Up }

        /// <summary>在虚拟指针处向面板派发一条指针事件；返回这一下是否该被界面吞掉（与真鼠标同一判定
        /// <see cref="UI.Common.UiWindowFocus.BlocksWorldPointerAt"/>：穿透类 HUD 文字、无背景的透明容器放行给世界）。
        /// 事件坐标直接用换算好的面板坐标（GetPooled(Event) 把 Event.mousePosition 原样当作面板坐标）。</summary>
        private bool SendUi(UiPointer kind)
        {
            VisualElement root = UiRootOverride ?? FindUiRoot();
            if (root?.panel == null)
            {
                return false;
            }
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(_pos.x, ScreenDims().y - _pos.y));
            VisualElement picked = root.panel.Pick(panelPos);
            if (picked == null || picked == root.panel.visualTree)
            {
                return false;
            }
            var e = new Event
            {
                type = kind == UiPointer.Down ? EventType.MouseDown : kind == UiPointer.Up ? EventType.MouseUp : _leftHeld ? EventType.MouseDrag : EventType.MouseMove,
                button = 0,
                clickCount = 1,
                mousePosition = panelPos,
            };
            using (EventBase evt = kind == UiPointer.Down ? PointerDownEvent.GetPooled(e) : kind == UiPointer.Up ? PointerUpEvent.GetPooled(e) : (EventBase)PointerMoveEvent.GetPooled(e))
            {
                evt.target = picked;
                picked.SendEvent(evt);
            }
            return UI.Common.UiWindowFocus.BlocksWorldPointerAt(root.panel, panelPos);
        }

        private static VisualElement _cachedRoot;
        private static float _nextFindAt;

        private static VisualElement FindUiRoot()
        {
            if ((_cachedRoot == null || _cachedRoot.panel == null) && Time.unscaledTime >= _nextFindAt)
            {
                _nextFindAt = Time.unscaledTime + 1f;
                UIDocument doc = Object.FindAnyObjectByType<UIDocument>();
                _cachedRoot = doc != null ? doc.rootVisualElement?.panel?.visualTree : null;
            }
            return _cachedRoot;
        }
    }
}
