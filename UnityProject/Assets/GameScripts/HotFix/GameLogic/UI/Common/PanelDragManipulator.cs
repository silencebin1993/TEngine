using System.Collections.Generic;
using GameLogic.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// 共用 PanelSettings 的多个 UIDocument 没有天然的「最后点击窗口在最上层」规则。
    /// 统一把获得焦点的窗口提到浮动层，避免两个可拖动窗口重叠后无法操作下层窗口。
    /// </summary>
    public static class UiWindowFocus
    {
        private const int FirstFloatingOrder = 32;
        private const int LastFloatingOrder = 30000;
        private static int _nextSortingOrder = FirstFloatingOrder;
        private static readonly List<VisualElement> RegisteredWindows = new List<VisualElement>();

        public static void BringToFront(UIDocument document, VisualElement window)
        {
            window?.BringToFront();
            if (document == null)
            {
                return;
            }

            if (_nextSortingOrder >= LastFloatingOrder)
            {
                _nextSortingOrder = FirstFloatingOrder;
            }

            document.sortingOrder = ++_nextSortingOrder;
        }

        public static void Attach(UIDocument document, VisualElement window, VisualElement dragHandle, string prefsKey)
        {
            if (window == null)
            {
                return;
            }

            if (!RegisteredWindows.Contains(window))
            {
                RegisteredWindows.Add(window);
            }
            InputRouter.SetUiPointerBlocker(IsPointerOverRegisteredWindow);

            // 使用 TrickleDown，先于子按钮及其默认行为确认 UI 命中；它只封锁世界输入，
            // 不 StopPropagation，因此按钮自己的 ClickEvent 不会被吞掉。
            window.RegisterCallback<PointerDownEvent>(_ => BringToFront(document, window), TrickleDown.TrickleDown);
            // 整个窗口都可拖动；拖拽操纵器会自行跳过按钮、滚动区和表单控件。
            // 保留 dragHandle 参数是为了兼容所有既有调用点，不再限制拖动起点。
            var drag = new PanelDragManipulator(window, window, prefsKey, document);
            window.AddManipulator(drag);
            drag.ApplyPersistedPosition();
        }

        private static bool IsPointerOverRegisteredWindow()
        {
            Vector2 panelPosition = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            for (int i = RegisteredWindows.Count - 1; i >= 0; i--)
            {
                VisualElement window = RegisteredWindows[i];
                if (window == null || window.panel == null)
                {
                    RegisteredWindows.RemoveAt(i);
                    continue;
                }
                if (!IsDisplayedInHierarchy(window) ||
                    window.worldBound.width <= 0f || window.worldBound.height <= 0f)
                {
                    continue;
                }
                if (window.worldBound.Contains(panelPosition))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// UI Toolkit 的子节点可在父节点 display:none 后保留自己的旧 resolvedStyle/worldBound。
        /// 命中判定必须沿祖先链检查，不能只看窗口本身，否则隐藏面板会永久吃掉战场鼠标。
        /// </summary>
        private static bool IsDisplayedInHierarchy(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.parent)
            {
                if (current.resolvedStyle.display == DisplayStyle.None)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 通用 UI Toolkit 面板拖拽 Manipulator。挂给整个面板，空白区域与普通文字均可拖动；
    /// 按钮、滚动区和表单控件优先处理自身交互。实际移动的对象是 movedTarget（默认等于 target）。假定 movedTarget 已是
    /// position:absolute（本项目全部面板 uxml 均如此）。首次 PointerDown 时把 translate
    /// 居中（如 ShopUI 的 left:50%;top:50%;translate:-50%-50%）换算成显式 left/top 后清除
    /// translate，避免第一次拖拽发生跳变。prefsKey 非空时拖拽结束落盘到 PlayerPrefs。
    /// </summary>
    public sealed class PanelDragManipulator : PointerManipulator
    {
        private readonly VisualElement _movedTarget;
        private readonly string _prefsKey;
        private readonly UIDocument _ownerDocument;
        private Vector2 _pointerStartPos;
        private float _startLeft;
        private float _startTop;
        private int _activePointerId = -1;
        private bool _isDragging;
        private bool _hasPendingPersistedPosition;
        private float _persistedLeft;
        private float _persistedTop;

        public PanelDragManipulator(VisualElement dragHandle, VisualElement movedTarget = null, string prefsKey = null,
            UIDocument ownerDocument = null)
        {
            target = dragHandle;
            _movedTarget = movedTarget ?? dragHandle;
            _prefsKey = prefsKey;
            _ownerDocument = ownerDocument;
        }

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(OnPointerDown);
            target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
        }

        /// <summary>若存档坐标存在则应用（面板首次可见、worldBound 有效后调用一次）。</summary>
        public void ApplyPersistedPosition()
        {
            if (string.IsNullOrEmpty(_prefsKey))
            {
                return;
            }
            // v2 重新开始记录位置：旧版本允许窗口只露出 96×48 像素，已经留下了
            // 大量看似“界面丢失”的坐标。保留旧键不删除，避免影响其它存档数据。
            string leftKey = GetPreferenceKey("left");
            string topKey = GetPreferenceKey("top");
            if (!PlayerPrefs.HasKey(leftKey) || !PlayerPrefs.HasKey(topKey))
            {
                return;
            }

            _persistedLeft = PlayerPrefs.GetFloat(leftKey);
            _persistedTop = PlayerPrefs.GetFloat(topKey);
            _hasPendingPersistedPosition = true;
            _movedTarget.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            TryApplyPersistedPosition();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_activePointerId != -1)
            {
                return;
            }

            if (IsInteractiveTarget(evt.target as VisualElement))
            {
                // 按钮点击、列表滚动、滑条拖动和输入编辑优先；它们不能被窗口拖拽抢走。
                return;
            }

            UiWindowFocus.BringToFront(_ownerDocument, _movedTarget);
            Rect resolved = _movedTarget.worldBound;
            Vector2 parentLocal = _movedTarget.parent != null
                ? _movedTarget.parent.WorldToLocal(new Vector2(resolved.x, resolved.y))
                : new Vector2(resolved.x, resolved.y);
            _startLeft = parentLocal.x;
            _startTop = parentLocal.y;
            _pointerStartPos = evt.position;
            _activePointerId = evt.pointerId;
            _isDragging = false;
            // 普通点击并不是拖窗：只有越过阈值后才独占指针。
            // 否则一次 UI 内的短按会在同一帧取消已经开始的地图框选。
            // 仍保留 UI Toolkit 的事件捕获，保证从窗口内起拖后可移出窗口继续拖动。
            target.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private bool IsInteractiveTarget(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.parent)
            {
                if (current is Button || current is Toggle || current is Slider ||
                    current is TextField || current is ScrollView)
                {
                    return true;
                }
                if (current == target)
                {
                    break;
                }
            }
            return false;
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_activePointerId != evt.pointerId)
            {
                return;
            }
            Vector2 delta = (Vector2)evt.position - _pointerStartPos;
            if (!_isDragging)
            {
                // 普通点击标题不再触发布局转换；越过很小阈值后才进入拖动，避免“按一下就跳”。
                if (delta.sqrMagnitude < 9f)
                {
                    return;
                }
                Vector2 floatingStart = MakeFloatingAndGetLocalPosition();
                _startLeft = floatingStart.x;
                _startTop = floatingStart.y;
                _isDragging = true;
                InputRouter.CaptureUiPointer(evt.pointerId);
                target.CapturePointer(evt.pointerId);
            }
            Vector2 clamped = ClampToParent(new Vector2(_startLeft + delta.x, _startTop + delta.y));
            _movedTarget.style.left = clamped.x;
            _movedTarget.style.top = clamped.y;
            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_activePointerId != evt.pointerId)
            {
                return;
            }
            int pointerId = _activePointerId;
            _activePointerId = -1;
            bool moved = _isDragging;
            _isDragging = false;
            target.ReleasePointer(pointerId);
            if (moved)
            {
                InputRouter.ReleaseUiPointer(pointerId);
            }

            if (moved && !string.IsNullOrEmpty(_prefsKey))
            {
                PlayerPrefs.SetFloat(GetPreferenceKey("left"), _movedTarget.style.left.value.value);
                PlayerPrefs.SetFloat(GetPreferenceKey("top"), _movedTarget.style.top.value.value);
                PlayerPrefs.Save();
            }
            evt.StopPropagation();
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (_activePointerId == evt.pointerId)
            {
                InputRouter.ReleaseUiPointer(_activePointerId);
                _activePointerId = -1;
                _isDragging = false;
            }
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            TryApplyPersistedPosition();
        }

        private void TryApplyPersistedPosition()
        {
            if (!_hasPendingPersistedPosition || _movedTarget.worldBound.width <= 0f || _movedTarget.worldBound.height <= 0f)
            {
                return;
            }

            MakeFloatingAndGetLocalPosition();
            Vector2 clamped = ClampToParent(new Vector2(_persistedLeft, _persistedTop));
            _movedTarget.style.left = clamped.x;
            _movedTarget.style.top = clamped.y;
            _hasPendingPersistedPosition = false;
            _movedTarget.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        private Vector2 MakeFloatingAndGetLocalPosition()
        {
            Rect resolved = _movedTarget.worldBound;
            Vector2 parentLocal = _movedTarget.parent != null
                ? _movedTarget.parent.WorldToLocal(new Vector2(resolved.x, resolved.y))
                : new Vector2(resolved.x, resolved.y);

            // 居中布局、right/bottom 定位和普通 flex 子项都统一转成绝对坐标，
            // 否则第一次拖动会跳位或仍受两侧约束而被压缩。
            _movedTarget.style.position = Position.Absolute;
            _movedTarget.style.width = resolved.width;
            _movedTarget.style.height = resolved.height;
            _movedTarget.style.right = StyleKeyword.Auto;
            _movedTarget.style.bottom = StyleKeyword.Auto;
            _movedTarget.style.translate = new StyleTranslate(new Translate(0, 0));
            Vector2 clamped = ClampToParent(parentLocal);
            _movedTarget.style.left = clamped.x;
            _movedTarget.style.top = clamped.y;
            return clamped;
        }

        private Vector2 ClampToParent(Vector2 position)
        {
            VisualElement parent = _movedTarget.parent;
            if (parent == null || parent.worldBound.width <= 0f || parent.worldBound.height <= 0f)
            {
                return position;
            }

            // 保留足够大的标题区和正文，用户仍可自由摆放，但无法再把窗口拖到只剩一小条。
            float visibleWidth = Mathf.Min(300f, _movedTarget.resolvedStyle.width);
            float visibleHeight = Mathf.Min(112f, _movedTarget.resolvedStyle.height);
            float minX = -Mathf.Max(0f, _movedTarget.resolvedStyle.width - visibleWidth);
            float minY = -Mathf.Max(0f, _movedTarget.resolvedStyle.height - visibleHeight);
            float maxX = Mathf.Max(minX, parent.worldBound.width - visibleWidth);
            float maxY = Mathf.Max(minY, parent.worldBound.height - visibleHeight);
            return new Vector2(Mathf.Clamp(position.x, minX, maxX), Mathf.Clamp(position.y, minY, maxY));
        }

        private string GetPreferenceKey(string axis)
        {
            return "ui_layout_v2_" + _prefsKey + "_" + axis;
        }
    }
}
