using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Defense;
using GameLogic.Localization;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-09（FGR-DEF-060）：难度选择的视图逻辑——新游戏界面与游戏中的难度面板共用（两份 UXML 里各有一组同名前缀的元素）。
    /// - 预设按钮一排（按 fg.TbRaidDifficulty 的顺序由代码建，数据驱动；当前选中的除颜色外还有“▸”前缀，B15）；
    /// - 选中“自定义”时显示三个滑条（突袭频率 / 规模 / 预警时间；范围、步长来自调参，拖动即按步长取整），从当前选中的预设倍率出发微调；
    /// - 说明标签：<see cref="DifficultyService.DescribeLines"/> 逐行公开写出对突袭与敌人的全部影响（FGR-FAC-003）。
    /// 元素名：{前缀}Presets / {前缀}CustomBox / {前缀}FreqLabel / {前缀}Freq / {前缀}ScaleLabel / {前缀}Scale / {前缀}WarnLabel / {前缀}Warn / {前缀}Desc。
    /// </summary>
    public sealed class DifficultyPickerView
    {
        private VisualElement _presets;
        private VisualElement _customBox;
        private Label _freqLabel, _scaleLabel, _warnLabel, _desc;
        private Slider _freq, _scale, _warn;
        private readonly Dictionary<string, Button> _buttons = new Dictionary<string, Button>(StringComparer.Ordinal);
        private DifficultyChoice _choice = new DifficultyChoice(DifficultyService.Standard);

        /// <summary>选择变了（按钮 / 滑条）。</summary>
        public event Action Changed;

        public DifficultyChoice Choice => _choice;
        public bool Bound => _presets != null;
        public string DescriptionText => _desc?.text ?? string.Empty;
        public bool CustomVisible => _customBox != null && !_customBox.ClassListContains("uk-hidden");
        public Slider FrequencySlider => _freq;
        public Slider ScaleSlider => _scale;
        public Slider WarningSlider => _warn;
        public string FrequencyLabelText => _freqLabel?.text ?? string.Empty;
        public int PresetCount => _buttons.Count;

        public Button PresetButton(string id) => id != null && _buttons.TryGetValue(id, out Button b) ? b : null;

        public void Bind(VisualElement root, string prefix)
        {
            _presets = root.Q<VisualElement>(prefix + "Presets");
            _customBox = root.Q<VisualElement>(prefix + "CustomBox");
            _freqLabel = root.Q<Label>(prefix + "FreqLabel");
            _scaleLabel = root.Q<Label>(prefix + "ScaleLabel");
            _warnLabel = root.Q<Label>(prefix + "WarnLabel");
            _desc = root.Q<Label>(prefix + "Desc");
            _freq = root.Q<Slider>(prefix + "Freq");
            _scale = root.Q<Slider>(prefix + "Scale");
            _warn = root.Q<Slider>(prefix + "Warn");
            foreach (Slider s in new[] { _freq, _scale, _warn })
            {
                if (s == null)
                {
                    continue;
                }
                s.RegisterValueChangedCallback(_ => OnSlider());
                UiTooltip.Attach(s, () => new TooltipContent
                {
                    Title = GameText.Get("difficulty.custom.name"),
                    Body = GameText.Format("ui.difficulty.slider_tip", DifficultyService.Num(DifficultyService.CustomMin), DifficultyService.Num(DifficultyService.CustomMax),
                        DifficultyService.Num(DifficultyService.CustomStep)),
                });
            }
            BuildPresets();
        }

        /// <summary>按难度表建预设按钮（语言切换 / 表重载后可再调）。</summary>
        public void BuildPresets()
        {
            if (_presets == null)
            {
                return;
            }
            // UiTooltip 的绑定表是静态的、OnDetach 不移除：重建前先解绑旧按钮（复审 P2：资源成对释放，每次打开面板不再多留 4 个）。
            foreach (Button old in _buttons.Values)
            {
                UiTooltip.Detach(old);
            }
            _presets.Clear();
            _buttons.Clear();
            foreach (RaidDifficulty row in RaidCatalog.Difficulties)
            {
                string id = row.Id;
                var b = new Button(() => SelectPreset(id)) { name = "DifficultyPreset_" + id };
                b.AddToClassList("mw-btn");
                b.AddToClassList("wg-level");
                string descKey = row.DescKey;
                UiTooltip.Attach(b, () => new TooltipContent { Title = DifficultyService.Name(id), Body = GameText.Get(descKey) });
                _presets.Add(b);
                _buttons[id] = b;
            }
            Refresh();
        }

        /// <summary>整体设成某个选择（打开面板 / 新游戏重置时）。</summary>
        public void Set(DifficultyChoice c)
        {
            _choice = DifficultyService.Normalize(c);
            Refresh();
        }

        /// <summary>点预设按钮（按钮与自检同一入口）。选“自定义”时三个滑条从当前选中的倍率出发。</summary>
        public void SelectPreset(string id)
        {
            DifficultyChoice next = DifficultyService.IsCustomId(id)
                ? new DifficultyChoice(id, _choice.Frequency, _choice.Scale, _choice.Warning)
                : DifficultyService.Preset(id);
            next = DifficultyService.Normalize(next);
            if (next.Id == _choice.Id && Math.Abs(next.Frequency - _choice.Frequency) < 1e-4f && Math.Abs(next.Scale - _choice.Scale) < 1e-4f
                && Math.Abs(next.Warning - _choice.Warning) < 1e-4f)
            {
                return;
            }
            _choice = next;
            Refresh();
            Changed?.Invoke();
        }

        /// <summary>自定义滑条的三个值（滑条与自检同一入口；按步长取整）。不是自定义时先切到自定义。</summary>
        public void SetCustom(float frequency, float scale, float warning)
        {
            string id = DifficultyService.IsCustomId(_choice.Id) ? _choice.Id : DifficultyService.Custom;
            _choice = DifficultyService.Normalize(new DifficultyChoice(id, frequency, scale, warning));
            Refresh();
            Changed?.Invoke();
        }

        private void OnSlider()
        {
            if (!DifficultyService.IsCustomId(_choice.Id) || _freq == null || _scale == null || _warn == null)
            {
                return;
            }
            SetCustom(_freq.value, _scale.value, _warn.value);
        }

        public void Refresh()
        {
            if (_presets == null)
            {
                return;
            }
            foreach (KeyValuePair<string, Button> kv in _buttons)
            {
                bool selected = kv.Key == _choice.Id;
                string name = DifficultyService.Name(kv.Key);
                kv.Value.text = selected ? "▸ " + name : name;
                kv.Value.EnableInClassList("wg-level-selected", selected);
            }
            bool custom = DifficultyService.IsCustomId(_choice.Id);
            _customBox?.EnableInClassList("uk-hidden", !custom);
            SyncSlider(_freq, _choice.Frequency);
            SyncSlider(_scale, _choice.Scale);
            SyncSlider(_warn, _choice.Warning);
            if (_freqLabel != null)
            {
                _freqLabel.text = GameText.Format("ui.difficulty.slider.frequency", DifficultyService.Num(_choice.Frequency));
            }
            if (_scaleLabel != null)
            {
                _scaleLabel.text = GameText.Format("ui.difficulty.slider.scale", DifficultyService.Num(_choice.Scale));
            }
            if (_warnLabel != null)
            {
                _warnLabel.text = GameText.Format("ui.difficulty.slider.warning", DifficultyService.Num(_choice.Warning));
            }
            if (_desc != null)
            {
                _desc.text = DifficultyService.Describe(_choice);
            }
        }

        private static void SyncSlider(Slider s, float v)
        {
            if (s == null)
            {
                return;
            }
            s.lowValue = DifficultyService.CustomMin;
            s.highValue = DifficultyService.CustomMax;
            s.SetValueWithoutNotify(v);
        }
    }
}
