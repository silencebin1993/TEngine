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
            if (window == null || dragHandle == null)
            {
                return;
            }

            // 点击窗口内任意控件即可获得焦点；标题栏只是移动把手，不是唯一置顶入口。
            window.RegisterCallback<PointerDownEvent>(_ => BringToFront(document, window));
            var drag = new PanelDragManipulator(dragHandle, window, prefsKey, document);
            dragHandle.AddManipulator(drag);
            drag.ApplyPersistedPosition();
        }
    }

    /// <summary>
    /// 通用 UI Toolkit 面板拖拽 Manipulator。挂给面板的标题栏/拖拽把手元素（dragHandle），
    /// 实际移动的对象是 movedTarget（默认等于 dragHandle）。假定 movedTarget 已是
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
            string leftKey = _prefsKey + "_left";
            string topKey = _prefsKey + "_top";
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

            UiWindowFocus.BringToFront(_ownerDocument, _movedTarget);

            Vector2 parentLocal = MakeFloatingAndGetLocalPosition();

            _startLeft = parentLocal.x;
            _startTop = parentLocal.y;
            _pointerStartPos = evt.position;
            _activePointerId = evt.pointerId;
            target.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_activePointerId != evt.pointerId)
            {
                return;
            }
            Vector2 delta = (Vector2)evt.position - _pointerStartPos;
            Vector2 clamped = ClampToParent(new Vector2(_startLeft + delta.x, _startTop + delta.y));
            _movedTarget.style.left = clamped.x;
            _movedTarget.style.top = clamped.y;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_activePointerId != evt.pointerId)
            {
                return;
            }
            target.ReleasePointer(_activePointerId);
            _activePointerId = -1;

            if (!string.IsNullOrEmpty(_prefsKey))
            {
                PlayerPrefs.SetFloat(_prefsKey + "_left", _movedTarget.style.left.value.value);
                PlayerPrefs.SetFloat(_prefsKey + "_top", _movedTarget.style.top.value.value);
            }
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            _activePointerId = -1;
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

            float visibleWidth = Mathf.Min(96f, _movedTarget.resolvedStyle.width);
            float visibleHeight = Mathf.Min(48f, _movedTarget.resolvedStyle.height);
            float minX = -Mathf.Max(0f, _movedTarget.resolvedStyle.width - visibleWidth);
            float minY = -Mathf.Max(0f, _movedTarget.resolvedStyle.height - visibleHeight);
            float maxX = Mathf.Max(minX, parent.worldBound.width - visibleWidth);
            float maxY = Mathf.Max(minY, parent.worldBound.height - visibleHeight);
            return new Vector2(Mathf.Clamp(position.x, minX, maxX), Mathf.Clamp(position.y, minY, maxY));
        }
    }
}
