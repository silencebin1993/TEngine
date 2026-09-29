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
        /// <summary>相关图鉴条目名（只显示链接文字；能跳转的用 <see cref="CodexEntryId"/>）。</summary>
        public string CodexEntry;
        /// <summary>FG2-FW-05（FGR-UX-030 / 051“从悬停提示按一个键跳到对应条目”）：悬停时按图鉴键跳到这条机制图鉴条目（<see cref="Progression.MechanicCodex"/> 的条目 ID）。</summary>
        public string CodexEntryId;
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

        // FG2-FW-03（FGR-FW-031）：世界里的对象（单位头顶的状态标签）悬停。用一个不拦截点击的代理元素摆在光标处，
        // 与界面元素走同一套“延迟出现 / 宽限隐藏 / 固定 / 摆放”规则。
        private static VisualElement _worldProxy;
        private static Func<TooltipContent> _worldProvider;
        private static int _worldKey;
        private static float _worldRefreshAt;
        private const float WorldProxySize = 24f;
        /// <summary>世界对象的提示内容刷新间隔（真实秒）：读数按 0.1 秒粒度走，不必每帧重拼文字。</summary>
        public const float WorldRefreshSeconds = 0.1f;

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
            _worldProxy = null;
            Render();
        }

        /// <summary>FG2-FW-03：光标停在世界里的对象上（<paramref name="screenPosition"/> = 屏幕像素，左下原点）。
        /// <paramref name="key"/> 区分对象：换了对象就重新计时（已显示的先收起，“固定”期间不被别的对象抢走）；
        /// 同一对象悬停期间按 <see cref="WorldRefreshSeconds"/> 的间隔刷新内容（剩余时间在走）。</summary>
        public static void HoverWorld(int key, Vector2 screenPosition, Func<TooltipContent> provider)
        {
            if (_view?.parent == null || _view.panel == null || provider == null)
            {
                return;
            }
            EnsureWorldProxy();
            bool onWorld = _hoverTarget == _worldProxy;
            bool sameObject = onWorld && _worldKey == key;
            if (!sameObject && onWorld)
            {
                if (IsPinned)
                {
                    return; // 固定期间保持原来那个对象的读数。
                }
                // 换了对象：已显示的收起，没显示的放弃原来的计时——下面 NotifyEnter 重新开始延迟。
                if (IsVisible)
                {
                    Hide();
                }
                else
                {
                    _hoverTarget = null;
                }
            }
            Vector2 panelPoint = RuntimePanelUtils.ScreenToPanel(_view.panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
            Vector2 origin = _view.parent.worldBound.position;
            _worldProxy.style.left = panelPoint.x - origin.x - WorldProxySize * 0.5f;
            _worldProxy.style.top = panelPoint.y - origin.y - WorldProxySize * 0.5f;
            _worldProvider = provider;
            _worldKey = key;
            NotifyEnter(_worldProxy);
            if (sameObject && IsVisible)
            {
                float now = Clock();
                if (now >= _worldRefreshAt)
                {
                    _worldRefreshAt = now + WorldRefreshSeconds;
                    _content = provider() ?? _content;
                    Render();
                }
            }
            else
            {
                _worldRefreshAt = 0f;
            }
        }

        /// <summary>光标离开世界里的对象。</summary>
        public static void LeaveWorld()
        {
            if (_worldProxy != null)
            {
                NotifyLeave(_worldProxy);
            }
        }

        /// <summary>当前悬停 / 显示的是不是世界对象（自检用）。</summary>
        public static bool HoveringWorld => _worldProxy != null && _hoverTarget == _worldProxy;

        /// <summary>FG2-FW-05：鼠标正停在的目标的图鉴条目（提示已显示时取显示的内容；还在 0.4 秒延迟里时现取一次，按键不必等提示出现）。没有返回 null。</summary>
        public static string HoveredCodexEntryId()
        {
            // 指针已经离开目标（提示只是在离开宽限期里还挂着），且没固定、也不在提示上：不算悬停。
            if (!IsPinned && !_pointerOnTooltip && !float.IsPositiveInfinity(_leftAt))
            {
                return null;
            }
            if (IsVisible && _content != null)
            {
                return _content.CodexEntryId;
            }
            if (_hoverTarget == null)
            {
                return null;
            }
            if (_hoverTarget == _worldProxy)
            {
                return _worldProvider?.Invoke()?.CodexEntryId;
            }
            return Bindings.TryGetValue(_hoverTarget, out Binding b) ? b.Provider?.Invoke()?.CodexEntryId : null;
        }

        public static int WorldKey => _worldKey;

        private static void EnsureWorldProxy()
        {
            if (_worldProxy != null && _worldProxy.parent == _view.parent)
            {
                return;
            }
            _worldProxy = new VisualElement { name = "TooltipWorldProxy", pickingMode = PickingMode.Ignore };
            _worldProxy.style.position = Position.Absolute;
            _worldProxy.style.width = WorldProxySize;
            _worldProxy.style.height = WorldProxySize;
            _view.parent.Insert(0, _worldProxy);
            Attach(_worldProxy, () => _worldProvider?.Invoke());
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
            SetText(_codex, !string.IsNullOrEmpty(_content.CodexEntryId)
                ? GameText.Format("ui.common.codex_jump", InputDisplay.ForAction(GameActionId.OpenCodex), CodexHoverLink.EntryTitle(_content.CodexEntryId))
                : string.IsNullOrEmpty(_content.CodexEntry) ? null : GameText.Format("ui.common.codex_link", _content.CodexEntry));
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
            _worldRefreshAt = 0f;
            Clock = () => Time.realtimeSinceStartup;
            PinHeld = () => InputRouter.IsActionHeld(GameActionId.PinTooltip);
        }
    }
}
