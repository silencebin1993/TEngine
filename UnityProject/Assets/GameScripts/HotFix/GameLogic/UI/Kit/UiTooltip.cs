using System;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>复合数值的一项来源（“基础产量 +12/分钟”“电力不足 -30%”）。文字已本地化。</summary>
    public readonly struct TooltipSource
    {
        public readonly string Label;
        public readonly string Value;

        public TooltipSource(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    /// <summary>一次悬停提示的内容。所有文字由调用方按文本键取好。</summary>
    public sealed class TooltipContent
    {
        public string Title;
        /// <summary>正文，支持富文本（&lt;b&gt;、&lt;color&gt;）。</summary>
        public string Body;
        /// <summary>数值来源（FGR-UX-030 / FG00 B13）。为空表示不是复合数值。</summary>
        public readonly List<TooltipSource> Sources = new List<TooltipSource>();
        /// <summary>来源合计（“合计 +8.4/分钟”）。</summary>
        public string Total;
        /// <summary>相关快捷键（显示当前绑定，改键后自动跟着变）。</summary>
        public GameActionId? Shortcut;
        /// <summary>相关图鉴条目名（图鉴本身由 FG2-FW-05 做，这里先显示链接文字）。</summary>
        public string CodexEntry;
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 悬停提示、FGR-UX-030、FG00 B13）：
    /// - 悬停约 0.4 秒（fg.TbUiTuning tooltip.delay_seconds，真实时间，不受暂停 / 倍速影响）后出现；
    /// - 按住“固定悬停提示”键（默认左 Alt，可重绑）把提示固定住：移开鼠标也不消失，数值来源自动展开，可以点“展开 / 收起来源”；
    /// - 复合数值可展开来源；附带相关快捷键与图鉴链接文字；
    /// - 不固定时，鼠标离开目标且不在提示上超过一小段宽限（真实时间）才隐藏，方便把鼠标移到提示上点按钮（只用鼠标也能展开）。
    /// 任意面板的元素都能 <see cref="Attach"/>；提示本身画在最上层的浮层（UiKitOverlay.uxml）里。
    /// 所有 UI 基础件面板共用同一个 PanelSettings，面板坐标一致，目标的 worldBound 可以直接用来摆提示。
    /// </summary>
    public static class UiTooltip
    {
        private sealed class Binding
        {
            public Func<TooltipContent> Provider;
        }

        private static readonly Dictionary<VisualElement, Binding> Bindings = new Dictionary<VisualElement, Binding>();

        private static VisualElement _view;
        private static Label _title;
        private static Label _body;
        private static VisualElement _breakdown;
        private static Label _total;
        private static Label _shortcut;
        private static Label _codex;
        private static Label _pinHint;
        private static Button _expand;

        private static VisualElement _hoverTarget;
        private static float _hoverSince;
        private static float _leftAt = float.NegativeInfinity;
        private static bool _pointerOnTooltip;
        private static bool _expanded;
        private static TooltipContent _content;

        /// <summary>真实时间来源，测试可注入。</summary>
        public static Func<float> Clock = () => Time.realtimeSinceStartup;

        /// <summary>“固定”是否按住。默认读 <see cref="GameActionId.PinTooltip"/> 的当前绑定；测试可注入。</summary>
        public static Func<bool> PinHeld = () => InputRouter.IsActionHeld(GameActionId.PinTooltip);

        public static bool IsVisible { get; private set; }
        public static bool IsPinned { get; private set; }
        public static bool IsExpanded => _expanded;
        public static TooltipContent Content => IsVisible ? _content : null;
        public static VisualElement Target => _hoverTarget;

        /// <summary>给任意元素挂上提示。<paramref name="provider"/> 在提示显示的那一刻才调用（拿到最新数值）。</summary>
        public static void Attach(VisualElement target, Func<TooltipContent> provider)
        {
            if (target == null || provider == null)
            {
                return;
            }
            if (!Bindings.ContainsKey(target))
            {
                target.RegisterCallback<PointerEnterEvent>(OnEnter);
                target.RegisterCallback<PointerLeaveEvent>(OnLeave);
                target.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            }
            Bindings[target] = new Binding { Provider = provider };
        }

        public static void Detach(VisualElement target)
        {
            if (target == null || !Bindings.Remove(target))
            {
                return;
            }
            target.UnregisterCallback<PointerEnterEvent>(OnEnter);
            target.UnregisterCallback<PointerLeaveEvent>(OnLeave);
            target.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            if (_hoverTarget == target)
            {
                Hide();
            }
        }

        public static void BindView(VisualElement root)
        {
            _view = root.Q<VisualElement>("Tooltip");
            _title = root.Q<Label>("TooltipTitle");
            _body = root.Q<Label>("TooltipBody");
            _breakdown = root.Q<VisualElement>("TooltipBreakdown");
            _total = root.Q<Label>("TooltipTotal");
            _shortcut = root.Q<Label>("TooltipShortcut");
            _codex = root.Q<Label>("TooltipCodex");
            _pinHint = root.Q<Label>("TooltipPinHint");
            _expand = root.Q<Button>("TooltipExpand");
            if (_expand != null)
            {
                _expand.clicked -= ToggleExpanded;
                _expand.clicked += ToggleExpanded;
            }
            if (_view != null)
            {
                _view.RegisterCallback<PointerEnterEvent>(_ => _pointerOnTooltip = true);
                _view.RegisterCallback<PointerLeaveEvent>(_ => _pointerOnTooltip = false);
            }
            Render();
        }

        public static void UnbindView()
        {
            if (_expand != null) _expand.clicked -= ToggleExpanded;
            _view = _breakdown = null;
            _title = _body = _total = _shortcut = _codex = _pinHint = null;
            _expand = null;
        }

        /// <summary>模拟 / 转发“鼠标进入目标”（自检直接调；运行时由 PointerEnterEvent 调用）。</summary>
        public static void NotifyEnter(VisualElement target)
        {
            if (!Bindings.ContainsKey(target))
            {
                return;
            }
            if (IsPinned && _hoverTarget != null && _hoverTarget != target)
            {
                return; // 固定期间不被别的目标抢走。
            }
            if (_hoverTarget != target)
            {
                _hoverTarget = target;
                _hoverSince = Clock();
                _expanded = false;
                if (IsVisible)
                {
                    Hide();
                    _hoverTarget = target;
                }
            }
            _leftAt = float.PositiveInfinity;
        }

        public static void NotifyLeave(VisualElement target)
        {
            if (_hoverTarget == target)
            {
                _leftAt = Clock();
            }
        }

        public static void ToggleExpanded()
        {
            _expanded = !_expanded;
            Render();
        }

        /// <summary>每帧由浮层宿主调用。O(1)：只看当前悬停目标。</summary>
        public static void Tick()
        {
            float now = Clock();
            bool pinHeld = PinHeld();
            if (_hoverTarget == null)
            {
                return;
            }
            if (_hoverTarget.panel == null)
            {
                Hide();
                return;
            }
            bool pointerAway = !float.IsPositiveInfinity(_leftAt) && !_pointerOnTooltip;
            if (IsVisible)
            {
                bool wasPinned = IsPinned;
                IsPinned = pinHeld;
                if (IsPinned != wasPinned)
                {
                    Render();
                }
                if (!IsPinned && pointerAway && now - _leftAt >= UiTuningValues.Get("tooltip.hide_grace_seconds"))
                {
                    Hide();
                }
                return;
            }
            if (pointerAway)
            {
                _hoverTarget = null;
                return;
            }
            if (now - _hoverSince >= UiTuningValues.Get("tooltip.delay_seconds"))
            {
                Show();
            }
        }

        private static void Show()
        {
            if (_hoverTarget == null || !Bindings.TryGetValue(_hoverTarget, out Binding binding))
            {
                return;
            }
            _content = binding.Provider();
            if (_content == null)
            {
                return;
            }
            IsVisible = true;
            IsPinned = PinHeld();
            Render();
            Place();
        }

        public static void Hide()
        {
            IsVisible = false;
            IsPinned = false;
            _hoverTarget = null;
            _content = null;
            _expanded = false;
            _pointerOnTooltip = false;
            _leftAt = float.NegativeInfinity;
            Render();
        }

        private static void OnEnter(PointerEnterEvent evt) => NotifyEnter(evt.currentTarget as VisualElement);

        private static void OnLeave(PointerLeaveEvent evt) => NotifyLeave(evt.currentTarget as VisualElement);

        private static void OnDetach(DetachFromPanelEvent evt)
        {
            if (evt.currentTarget == _hoverTarget)
            {
                Hide();
            }
        }

        private static void Render()
        {
            if (_view == null)
            {
                return;
            }
            _view.EnableInClassList("uk-hidden", !IsVisible);
            _view.EnableInClassList("uk-tooltip-pinned", IsPinned);
            // 固定时可以点里面的按钮；不固定时不拦截点击（提示不该挡住下面的按钮）。
            _view.pickingMode = IsVisible ? PickingMode.Position : PickingMode.Ignore;
            if (!IsVisible || _content == null)
            {
                return;
            }
            SetText(_title, _content.Title);
            SetText(_body, _content.Body);
            bool hasSources = _content.Sources.Count > 0;
            bool showSources = hasSources && (_expanded || IsPinned);
            _breakdown?.EnableInClassList("uk-hidden", !showSources);
            if (_breakdown != null && showSources)
            {
                _breakdown.Clear();
                foreach (TooltipSource s in _content.Sources)
                {
                    var row = new VisualElement();
                    row.AddToClassList("uk-breakdown-row");
                    var l = new Label(s.Label);
                    l.AddToClassList("uk-breakdown-label");
                    var v = new Label(s.Value);
                    v.AddToClassList("uk-breakdown-value");
                    row.Add(l);
                    row.Add(v);
                    _breakdown.Add(row);
                }
            }
            SetText(_total, hasSources ? _content.Total : null);
            SetText(_shortcut, _content.Shortcut.HasValue
                ? GameText.Format("ui.common.shortcut", InputDisplay.ForAction(_content.Shortcut.Value))
                : null);
            SetText(_codex, string.IsNullOrEmpty(_content.CodexEntry) ? null : GameText.Format("ui.common.codex_link", _content.CodexEntry));
            string pinKey = InputDisplay.ForAction(GameActionId.PinTooltip);
            SetText(_pinHint, IsPinned ? GameText.Format("ui.tooltip.pinned", pinKey) : GameText.Format("ui.tooltip.pin_hint", pinKey));
            if (_expand != null)
            {
                _expand.text = GameText.Get(showSources ? "ui.tooltip.collapse_sources" : "ui.tooltip.expand_sources");
                _expand.EnableInClassList("uk-hidden", !hasSources);
            }
        }

        private static void SetText(Label label, string text)
        {
            if (label == null)
            {
                return;
            }
            label.text = text ?? string.Empty;
            label.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
        }

        /// <summary>摆在目标右下方；靠近屏幕右 / 下边缘时翻到另一侧。位置是运行时数据，允许直接写 style（红线 2 的例外）。</summary>
        private static void Place()
        {
            if (_view == null || _hoverTarget == null)
            {
                return;
            }
            Rect target = _hoverTarget.worldBound;
            Rect screen = _view.parent != null ? _view.parent.worldBound : new Rect(0, 0, 1920, 1080);
            float width = Mathf.Max(_view.resolvedStyle.width, 200f);
            float height = Mathf.Max(_view.resolvedStyle.height, 60f);
            float x = target.xMax + 8f;
            float y = target.yMin;
            if (x + width > screen.xMax)
            {
                x = Mathf.Max(screen.xMin, target.xMin - width - 8f);
            }
            if (y + height > screen.yMax)
            {
                y = Mathf.Max(screen.yMin, screen.yMax - height - 4f);
            }
            _view.style.left = x - screen.xMin;
            _view.style.top = y - screen.yMin;
        }

        public static void ResetForTests()
        {
            Hide();
            Clock = () => Time.realtimeSinceStartup;
            PinHeld = () => InputRouter.IsActionHeld(GameActionId.PinTooltip);
        }
    }
}
