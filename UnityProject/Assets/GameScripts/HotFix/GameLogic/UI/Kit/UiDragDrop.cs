using System;
using GameLogic.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>放置目标对一次拖动的裁决：能不能放、不能放的原因（FG00 B06）。</summary>
    public readonly struct DropVerdict
    {
        public readonly bool Accept;
        public readonly string Reason;

        public DropVerdict(bool accept, string reason = null)
        {
            Accept = accept;
            Reason = reason;
        }
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 拖放）：同一面板内的拖放。
    /// - 源元素按下主动作键并移动超过阈值（fg.TbUiTuning drag.start_distance）才开始拖动，单击不会被误判；
    /// - 拖动时指针被 UI 独占（<see cref="InputRouter.CaptureUiPointer"/>），世界不会收到点击；浮层显示拖影；
    /// - 悬停在目标上时按裁决标绿 / 标红；松开在可接受的目标上才落下，否则取消并报告原因；
    /// - Esc 或功能动作键（右键）取消拖动，什么都不改。
    /// </summary>
    public static class UiDragDrop
    {
        private sealed class Source
        {
            public Func<object> Payload;
            public Func<string> Label;
        }

        private sealed class Target
        {
            public Func<object, DropVerdict> Judge;
            public Action<object> OnDrop;
        }

        private static readonly System.Collections.Generic.Dictionary<VisualElement, Source> Sources =
            new System.Collections.Generic.Dictionary<VisualElement, Source>();
        private static readonly System.Collections.Generic.Dictionary<VisualElement, Target> Targets =
            new System.Collections.Generic.Dictionary<VisualElement, Target>();
        private static readonly object EscOwner = new object();

        private static VisualElement _ghost;
        private static Label _ghostLabel;

        private static VisualElement _pressedSource;
        private static Vector2 _pressedAt;
        private static int _pointerId = -1;
        private static VisualElement _hover;

        public static bool IsDragging { get; private set; }
        public static object Payload { get; private set; }
        /// <summary>最近一次拖放的结果说明（落下 / 被拒绝原因 / 已取消），供界面反馈与自检。</summary>
        public static string LastResult { get; private set; }
        public static bool LastDropAccepted { get; private set; }

        public static void BindView(VisualElement root)
        {
            _ghost = root.Q<VisualElement>("DragGhost");
            _ghostLabel = root.Q<Label>("DragGhostLabel");
            _ghost?.EnableInClassList("uk-hidden", true);
        }

        public static void UnbindView()
        {
            _ghost = null;
            _ghostLabel = null;
        }

        public static void MakeSource(VisualElement element, Func<object> payload, Func<string> label)
        {
            if (element == null || Sources.ContainsKey(element))
            {
                return;
            }
            Sources[element] = new Source { Payload = payload, Label = label };
            element.RegisterCallback<PointerDownEvent>(OnSourceDown);
            element.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            element.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        public static void MakeTarget(VisualElement element, Func<object, DropVerdict> judge, Action<object> onDrop)
        {
            if (element == null)
            {
                return;
            }
            Targets[element] = new Target { Judge = judge, OnDrop = onDrop };
        }

        private static void OnSourceDown(PointerDownEvent evt)
        {
            if (evt.button == InputRouter.PhysicalButton(1) && IsDragging)
            {
                Cancel();
                evt.StopPropagation();
                return;
            }
            if (evt.button != InputRouter.PhysicalButton(0))
            {
                return;
            }
            _pressedSource = evt.currentTarget as VisualElement;
            _pressedAt = evt.position;
            _pointerId = evt.pointerId;
        }

        private static void OnPointerMove(PointerMoveEvent evt)
        {
            if (_pressedSource == null)
            {
                return;
            }
            Vector2 pos = evt.position;
            if (!IsDragging && (pos - _pressedAt).magnitude >= UiTuningValues.Get("drag.start_distance"))
            {
                Begin(_pressedSource, evt.pointerId);
            }
            if (IsDragging)
            {
                MoveTo(pos, _pressedSource.panel?.Pick(pos));
            }
        }

        private static void OnPointerUp(PointerUpEvent evt)
        {
            if (IsDragging)
            {
                Drop(_pressedSource?.panel?.Pick(evt.position));
            }
            _pressedSource = null;
        }

        /// <summary>开始拖动（指针事件与自检共用）。</summary>
        public static void Begin(VisualElement source, int pointerId)
        {
            if (source == null || !Sources.TryGetValue(source, out Source s))
            {
                return;
            }
            _pressedSource = source;
            _pointerId = pointerId;
            IsDragging = true;
            Payload = s.Payload?.Invoke();
            source.AddToClassList("uk-drag-source-active");
            if (source.panel != null && pointerId >= 0)
            {
                source.CapturePointer(pointerId);
            }
            InputRouter.CaptureUiPointer(pointerId);
            UiEscapeStack.Push(EscOwner, Cancel);
            if (_ghost != null)
            {
                _ghost.EnableInClassList("uk-hidden", false);
                if (_ghostLabel != null)
                {
                    _ghostLabel.text = s.Label?.Invoke() ?? string.Empty;
                }
            }
        }

        /// <summary>拖影移到面板坐标 <paramref name="panelPos"/>，悬停元素 <paramref name="picked"/>（沿祖先链找目标）。</summary>
        public static void MoveTo(Vector2 panelPos, VisualElement picked)
        {
            if (!IsDragging)
            {
                return;
            }
            if (_ghost != null)
            {
                Rect parent = _ghost.parent != null ? _ghost.parent.worldBound : new Rect(0, 0, 0, 0);
                _ghost.style.left = panelPos.x - parent.xMin + 12f;
                _ghost.style.top = panelPos.y - parent.yMin + 12f;
            }
            VisualElement target = FindTarget(picked);
            if (target == _hover)
            {
                return;
            }
            ClearHover();
            _hover = target;
            if (_hover != null && Targets.TryGetValue(_hover, out Target t))
            {
                DropVerdict v = t.Judge != null ? t.Judge(Payload) : new DropVerdict(true);
                _hover.AddToClassList(v.Accept ? "uk-drop-ok" : "uk-drop-bad");
            }
        }

        /// <summary>在 <paramref name="picked"/> 处松开。返回是否真的落下。</summary>
        public static bool Drop(VisualElement picked)
        {
            if (!IsDragging)
            {
                return false;
            }
            VisualElement target = FindTarget(picked);
            bool accepted = false;
            if (target != null && Targets.TryGetValue(target, out Target t))
            {
                DropVerdict v = t.Judge != null ? t.Judge(Payload) : new DropVerdict(true);
                if (v.Accept)
                {
                    object payload = Payload;
                    End();
                    t.OnDrop?.Invoke(payload);
                    LastDropAccepted = true;
                    LastResult = string.Empty;
                    return true;
                }
                LastResult = v.Reason ?? string.Empty;
            }
            else
            {
                LastResult = Localization.GameText.Get("ui.drag.no_target");
            }
            LastDropAccepted = accepted;
            End();
            return false;
        }

        /// <summary>取消拖动（Esc、右键）。什么都不改。</summary>
        public static void Cancel()
        {
            if (!IsDragging)
            {
                return;
            }
            LastDropAccepted = false;
            LastResult = Localization.GameText.Get("ui.drag.cancelled");
            End();
        }

        private static void End()
        {
            ClearHover();
            if (_pressedSource != null)
            {
                _pressedSource.RemoveFromClassList("uk-drag-source-active");
                if (_pointerId >= 0 && _pressedSource.HasPointerCapture(_pointerId))
                {
                    _pressedSource.ReleasePointer(_pointerId);
                }
            }
            InputRouter.ReleaseUiPointer(_pointerId);
            UiEscapeStack.Remove(EscOwner);
            _ghost?.EnableInClassList("uk-hidden", true);
            IsDragging = false;
            Payload = null;
            _pressedSource = null;
            _pointerId = -1;
        }

        private static void ClearHover()
        {
            if (_hover != null)
            {
                _hover.RemoveFromClassList("uk-drop-ok");
                _hover.RemoveFromClassList("uk-drop-bad");
                _hover = null;
            }
        }

        private static VisualElement FindTarget(VisualElement picked)
        {
            for (VisualElement e = picked; e != null; e = e.parent)
            {
                if (Targets.ContainsKey(e))
                {
                    return e;
                }
            }
            return null;
        }

        public static void ResetForTests()
        {
            Cancel();
            LastResult = null;
            LastDropAccepted = false;
        }
    }
}
