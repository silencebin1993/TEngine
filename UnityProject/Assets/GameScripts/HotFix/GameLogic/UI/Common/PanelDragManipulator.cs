using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
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
        private Vector2 _pointerStartPos;
        private float _startLeft;
        private float _startTop;
        private int _activePointerId = -1;

        public PanelDragManipulator(VisualElement dragHandle, VisualElement movedTarget = null, string prefsKey = null)
        {
            target = dragHandle;
            _movedTarget = movedTarget ?? dragHandle;
            _prefsKey = prefsKey;
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
            _movedTarget.style.translate = new StyleTranslate(new Translate(0, 0));
            _movedTarget.style.left = PlayerPrefs.GetFloat(leftKey);
            _movedTarget.style.top = PlayerPrefs.GetFloat(topKey);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_activePointerId != -1)
            {
                return;
            }

            Rect resolved = _movedTarget.worldBound;
            Vector2 parentLocal = _movedTarget.parent != null
                ? _movedTarget.parent.WorldToLocal(new Vector2(resolved.x, resolved.y))
                : new Vector2(resolved.x, resolved.y);

            _movedTarget.style.translate = new StyleTranslate(new Translate(0, 0));
            _movedTarget.style.left = parentLocal.x;
            _movedTarget.style.top = parentLocal.y;

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
            _movedTarget.style.left = _startLeft + delta.x;
            _movedTarget.style.top = _startTop + delta.y;
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
    }
}
