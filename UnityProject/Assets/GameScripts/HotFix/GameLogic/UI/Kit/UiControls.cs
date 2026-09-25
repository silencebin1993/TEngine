using System;
using System.Collections;
using System.Collections.Generic;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 搜索框）：UXML 里的 TextField + 占位 Label + 清空按钮。
    /// 获得焦点时通知 <see cref="InputRouter.SetTextInputFocused"/>，打字不会触发任何快捷键；
    /// Esc 先让输入框失焦（不关闭窗口）；文字变化立即回调（选中即生效，不做“再点搜索”的两步式）。
    /// </summary>
    public sealed class UiSearchBox
    {
        private readonly TextField _field;
        private readonly Label _placeholder;
        private readonly Button _clear;
        private readonly Action<string> _onChanged;

        public string Text => _field?.value ?? string.Empty;

        public UiSearchBox(TextField field, Label placeholder, Button clear, string placeholderKey, Action<string> onChanged)
        {
            _field = field;
            _placeholder = placeholder;
            _clear = clear;
            _onChanged = onChanged;
            if (_placeholder != null)
            {
                _placeholder.text = GameText.Get(placeholderKey);
                _placeholder.pickingMode = PickingMode.Ignore;
            }
            if (_clear != null)
            {
                _clear.text = GameText.Get("ui.common.clear");
                _clear.clicked += () => SetText(string.Empty);
            }
            if (_field != null)
            {
                _field.RegisterValueChangedCallback(e => OnChanged(e.newValue));
                _field.RegisterCallback<FocusInEvent>(_ => InputRouter.SetTextInputFocused(true));
                _field.RegisterCallback<FocusOutEvent>(_ => InputRouter.SetTextInputFocused(false));
                _field.RegisterCallback<DetachFromPanelEvent>(_ => InputRouter.SetTextInputFocused(false));
                _field.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            }
            RefreshPlaceholder();
        }

        public void SetText(string text)
        {
            if (_field == null)
            {
                OnChanged(text);
                return;
            }
            _field.value = text ?? string.Empty;
            RefreshPlaceholder();
        }

        private void OnChanged(string text)
        {
            RefreshPlaceholder();
            _onChanged?.Invoke(text ?? string.Empty);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                _field.Blur();
                InputRouter.SetTextInputFocused(false);
                // Esc 先失焦：同一帧的 Esc 不再被 Esc 栈拿去关掉面板（再按一次才关）。
                InputRouter.SwallowKeyboardThisFrame();
                evt.StopPropagation();
            }
        }

        private void RefreshPlaceholder()
        {
            bool empty = string.IsNullOrEmpty(_field?.value);
            _placeholder?.EnableInClassList("uk-hidden", !empty);
            _clear?.SetEnabled(!empty);
        }
    }

    /// <summary>FG0-UX-01（FGR-ARC-007 标签页）：一组 UXML 按钮当标签，选中态用 <c>uk-tab-selected</c>。</summary>
    public sealed class UiTabs
    {
        private readonly List<Button> _tabs;
        private readonly Action<int> _onSelected;

        public int Selected { get; private set; } = -1;
        public int Count => _tabs.Count;

        public UiTabs(IEnumerable<Button> tabs, Action<int> onSelected)
        {
            _tabs = new List<Button>(tabs);
            _onSelected = onSelected;
            for (int i = 0; i < _tabs.Count; i++)
            {
                int captured = i;
                _tabs[i].AddToClassList("uk-tab");
                _tabs[i].clicked += () => Select(captured);
            }
        }

        public void SetLabels(IList<string> labels)
        {
            for (int i = 0; i < _tabs.Count && i < labels.Count; i++)
            {
                _tabs[i].text = labels[i];
            }
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _tabs.Count)
            {
                return;
            }
            Selected = index;
            for (int i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].EnableInClassList("uk-tab-selected", i == index);
            }
            _onSelected?.Invoke(index);
        }
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 虚拟化列表、FGR-UX-070）：固定行高的 ListView，只为可见行建元素，数据上万条也不卡；
    /// 空列表显示空状态文字（FG00 B12）。行高来自 fg.TbUiTuning list.row_height。
    /// </summary>
    public sealed class UiVirtualList
    {
        private readonly ListView _list;
        private readonly Label _empty;

        public ListView View => _list;

        public UiVirtualList(ListView list, Label emptyLabel, Func<VisualElement> makeRow, Action<VisualElement, int> bindRow)
        {
            _list = list;
            _empty = emptyLabel;
            _list.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
            _list.fixedItemHeight = UiTuningValues.Get("list.row_height");
            _list.selectionType = SelectionType.Single;
            _list.makeItem = () =>
            {
                VisualElement row = makeRow();
                row.AddToClassList("uk-list-row");
                return row;
            };
            _list.bindItem = bindRow;
            _list.AddToClassList("uk-list");
        }

        public void SetItems(IList items, string emptyText)
        {
            _list.itemsSource = items;
            _list.RefreshItems();
            bool empty = items == null || items.Count == 0;
            if (_empty != null)
            {
                _empty.text = emptyText ?? GameText.Get("ui.common.empty");
                _empty.EnableInClassList("uk-hidden", !empty);
            }
            _list.EnableInClassList("uk-hidden", empty);
        }

        public void Refresh() => _list.RefreshItems();

        /// <summary>当前实际存在的行元素数（自检用：证明虚拟化生效，数量与数据条数无关）。</summary>
        public int RealizedRowCount => _list.Query(className: "uk-list-row").ToList().Count;
    }

    /// <summary>FG0-UX-01（FGR-ARC-007 进度条）：UXML 里 .uk-progress > .uk-progress-track > .uk-progress-fill + .uk-progress-label。
    /// 受阻状态同时换颜色和在文字里写原因（不只靠颜色，FG00 B05）。</summary>
    public sealed class UiProgressBar
    {
        private readonly VisualElement _root;
        private readonly VisualElement _fill;
        private readonly Label _label;

        public float Value { get; private set; }
        public bool Blocked { get; private set; }

        public UiProgressBar(VisualElement root)
        {
            _root = root;
            _fill = root?.Q<VisualElement>(className: "uk-progress-fill");
            _label = root?.Q<Label>(className: "uk-progress-label");
        }

        public void Set(float value01, bool blocked = false, string blockedReason = null)
        {
            Value = Mathf.Clamp01(value01);
            Blocked = blocked;
            if (_fill != null)
            {
                _fill.style.width = Length.Percent(Value * 100f); // 数据驱动的宽度百分比，允许直接写 style。
            }
            _root?.EnableInClassList("uk-progress-blocked", blocked);
            if (_label != null)
            {
                _label.text = blocked && !string.IsNullOrEmpty(blockedReason) ? blockedReason : UiFormat.Percent(Value);
            }
        }
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 快捷键提示）：把 Label 绑到一个动作，显示它当前的按键；改键、换语言后自动刷新；
    /// 未绑定时显示“未绑定”并换成警示样式（不显示空白，也不显示过期的默认键）。
    /// 刷新由浮层宿主每帧调用 <see cref="RefreshIfChanged"/>：只比较两个整数版本号，O(1)；真的变了才重写已绑定的标签。
    /// </summary>
    public static class UiShortcutHint
    {
        private static readonly List<KeyValuePair<Label, GameActionId>> Bound = new List<KeyValuePair<Label, GameActionId>>();
        private static int _seenBindings = -1;
        private static int _seenSettings = -1;

        public static void Bind(Label label, GameActionId action)
        {
            if (label == null)
            {
                return;
            }
            for (int i = Bound.Count - 1; i >= 0; i--)
            {
                if (Bound[i].Key == label)
                {
                    Bound.RemoveAt(i);
                }
            }
            label.AddToClassList("uk-keycap");
            Bound.Add(new KeyValuePair<Label, GameActionId>(label, action));
            Apply(label, action);
        }

        public static void RefreshIfChanged()
        {
            int bindings = Settings.GameSettings.KeyBindings.Revision;
            int settings = Settings.GameSettings.Revision;
            if (bindings == _seenBindings && settings == _seenSettings)
            {
                return;
            }
            _seenBindings = bindings;
            _seenSettings = settings;
            for (int i = Bound.Count - 1; i >= 0; i--)
            {
                Label label = Bound[i].Key;
                if (label == null)
                {
                    Bound.RemoveAt(i);
                    continue;
                }
                Apply(label, Bound[i].Value);
            }
        }

        public static void Apply(Label label, GameActionId action)
        {
            InputChord chord = Settings.GameSettings.KeyBindings.GetChord(action);
            label.text = InputDisplay.Chord(chord);
            label.EnableInClassList("uk-keycap-unbound", !chord.IsBound);
        }

        public static void Unbind(Label label)
        {
            for (int i = Bound.Count - 1; i >= 0; i--)
            {
                if (Bound[i].Key == label)
                {
                    Bound.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 图表：折线）：在一个 UXML 元素上画折线。颜色取元素的 USS color（不写死在代码里）；
    /// 点数上限来自 fg.TbUiTuning chart.max_points（超出丢最旧）；没有数据时由调用方显示空状态文字。
    /// </summary>
    public sealed class UiLineChart
    {
        private readonly VisualElement _host;
        private readonly List<float> _values = new List<float>();

        public IReadOnlyList<float> Values => _values;
        public float Min { get; private set; }
        public float Max { get; private set; }

        public UiLineChart(VisualElement host)
        {
            _host = host;
            _host.generateVisualContent += Draw;
        }

        public void SetValues(IEnumerable<float> values)
        {
            _values.Clear();
            if (values != null)
            {
                _values.AddRange(values);
            }
            Trim();
            Recalc();
            _host.MarkDirtyRepaint();
        }

        public void Push(float value)
        {
            _values.Add(value);
            Trim();
            Recalc();
            _host.MarkDirtyRepaint();
        }

        private void Trim()
        {
            int cap = Math.Max(2, UiTuningValues.GetInt("chart.max_points"));
            if (_values.Count > cap)
            {
                _values.RemoveRange(0, _values.Count - cap);
            }
        }

        private void Recalc()
        {
            Min = float.MaxValue;
            Max = float.MinValue;
            foreach (float v in _values)
            {
                Min = Mathf.Min(Min, v);
                Max = Mathf.Max(Max, v);
            }
            if (_values.Count == 0)
            {
                Min = Max = 0f;
            }
        }

        /// <summary>第 i 个点在元素内的坐标（归一化到内容矩形），自检用来核对绘制与数据一致。</summary>
        public Vector2 PointAt(int i, Rect rect)
        {
            float span = Max - Min;
            float t = _values.Count <= 1 ? 0f : (float)i / (_values.Count - 1);
            float n = span <= 1e-6f ? 0.5f : (_values[i] - Min) / span;
            return new Vector2(rect.xMin + t * rect.width, rect.yMax - n * rect.height);
        }

        private void Draw(MeshGenerationContext ctx)
        {
            if (_values.Count < 2)
            {
                return;
            }
            Rect rect = _host.contentRect;
            rect = new Rect(rect.x + 4f, rect.y + 4f, Mathf.Max(1f, rect.width - 8f), Mathf.Max(1f, rect.height - 8f));
            Painter2D p = ctx.painter2D;
            p.strokeColor = _host.resolvedStyle.color;
            p.lineWidth = 2f;
            p.BeginPath();
            p.MoveTo(PointAt(0, rect));
            for (int i = 1; i < _values.Count; i++)
            {
                p.LineTo(PointAt(i, rect));
            }
            p.Stroke();
        }
    }

    /// <summary>FG0-UX-01（FGR-ARC-007 图表：柱状）：每根柱子是一个带类的子元素，高度按数据写百分比（数据驱动 style）。
    /// 柱下写标签；负数按 0 画（柱状图只表达“量”）。</summary>
    public sealed class UiBarChart
    {
        private readonly VisualElement _host;
        private readonly List<VisualElement> _bars = new List<VisualElement>();

        public IReadOnlyList<VisualElement> Bars => _bars;

        public UiBarChart(VisualElement host)
        {
            _host = host;
            _host.AddToClassList("uk-chart-bars");
        }

        public void SetBars(IList<KeyValuePair<string, float>> data)
        {
            _host.Clear();
            _bars.Clear();
            if (data == null || data.Count == 0)
            {
                return;
            }
            float max = 0f;
            foreach (KeyValuePair<string, float> d in data)
            {
                max = Mathf.Max(max, d.Value);
            }
            foreach (KeyValuePair<string, float> d in data)
            {
                var slot = new VisualElement();
                slot.AddToClassList("uk-chart-bar-slot");
                var bar = new VisualElement();
                bar.AddToClassList("uk-chart-bar");
                // 最高的柱子占槽位高度的 70%，给下方标签留位置（布局比例，不是玩法数值）。
                float pct = max <= 0f ? 0f : Mathf.Clamp01(d.Value / max) * 70f;
                bar.style.height = Length.Percent(pct);
                bar.tooltip = d.Key + " " + UiFormat.Number(d.Value);
                var label = new Label(d.Key);
                label.AddToClassList("uk-chart-bar-label");
                slot.Add(bar);
                slot.Add(label);
                _host.Add(slot);
                _bars.Add(bar);
            }
        }
    }
}
