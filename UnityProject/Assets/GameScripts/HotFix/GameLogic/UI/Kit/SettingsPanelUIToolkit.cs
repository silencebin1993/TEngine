using System.Collections.Generic;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG15-UX-01（FGR-SYS-020 / 021、FGT-SYS-005）：设置全集面板（UI Toolkit）。
    /// 入口：游戏内暂停菜单“设置…”、主菜单设置页“全部设置…”——同一个面板、同一份 <see cref="SettingsCatalog"/>，主菜单与游戏内天然一致。
    /// - 标签页 = 分类；行按登记表生成（开关 / 滑条 / 下拉 / 打开子面板 / 只读）；改了立即生效并写本机设置（难度类写存档）；
    /// - 底部“恢复本类默认”；改分辨率 / 窗口模式后顶部出现确认条：保留 / 还原，按真实时间倒数 15 秒，到点自动还原；
    /// - 关闭面板（关闭按钮 / Esc / 点遮罩）时还有没确认的显示设置 → 还原（不让玩家带着看不见倒数的改动离开）；
    /// - 显示设置失败（显示器不支持、引擎拒绝）在确认条位置给出原因（负向）。
    /// 模态：打开时输入上下文为“界面”。按键面板 / 难度面板盖在它上面，关掉回到这里。
    /// </summary>
    public sealed class SettingsPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30079;

        private static readonly string[] TabNames =
        {
            "SettingsTabDisplay", "SettingsTabAudio", "SettingsTabControls", "SettingsTabGameplay", "SettingsTabDifficulty", "SettingsTabAccessibility",
        };

        public static SettingsPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _openRequested;

        private VisualElement _window;
        private Label _title;
        private Button _close;
        private Button _resetCategory;
        private Label _feedback;
        private ScrollView _body;
        private UiTabs _tabs;
        private VisualElement _confirm;
        private Label _confirmText;
        private Button _keep;
        private Button _revert;
        private readonly List<SettingItem> _items = new List<SettingItem>();
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private int _seenSettings = -1;
        private int _seenDisplay = -1;
        private int _shownSeconds = -1;

        public SettingsCategory Category => SettingsCatalog.Categories[Mathf.Clamp(_tabs?.Selected ?? 0, 0, SettingsCatalog.Categories.Length - 1)];
        public IReadOnlyList<SettingItem> VisibleItems => _items;
        public IReadOnlyList<VisualElement> Rows => _rows;
        public bool ConfirmVisible => _confirm != null && !_confirm.ClassListContains("uk-hidden");
        public string ConfirmText => _confirmText?.text ?? string.Empty;
        public Button KeepButton => _keep;
        public Button RevertButton => _revert;
        public Button ResetCategoryButton => _resetCategory;
        public Button CloseButton => _close;
        public string FeedbackText => _feedback?.text ?? string.Empty;

        protected override string UxmlLocation => "SettingsPanel";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
            SettingsCatalog.OpenKeyBindings = KeyBindingsPanelUIToolkit.Open;
            SettingsCatalog.OpenDifficulty = DifficultyPanelUIToolkit.Open;
        }

        public static void Open()
        {
            if (Instance == null || !Instance.IsReady)
            {
                _openRequested = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _openRequested = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_openRequested)
            {
                _openRequested = false;
                SetOpen(true);
            }
        }

        public void BindView(VisualElement root)
        {
            // 反应记录 / 通知中心的层级低于设置面板：作为设置的子页打开（设置暂时隐藏，Esc / 关闭后回到设置），否则会开在设置窗口下面看不见。
            SettingsCatalog.OpenReactionLog = () => UiEscapeStack.OpenChild(this, ReactionLogPanelUIToolkit.Open);
            SettingsCatalog.OpenAutoPauseRules = () => UiEscapeStack.OpenChild(this, NotificationHudUIToolkit.OpenAutoPauseSettings);
            _window = root.Q<VisualElement>("SettingsRoot");
            _title = root.Q<Label>("SettingsTitle");
            _close = root.Q<Button>("SettingsClose");
            _resetCategory = root.Q<Button>("SettingsResetCategory");
            _feedback = root.Q<Label>("SettingsFeedback");
            _body = root.Q<ScrollView>("SettingsBody");
            _confirm = root.Q<VisualElement>("SettingsConfirm");
            _confirmText = root.Q<Label>("SettingsConfirmText");
            _keep = root.Q<Button>("SettingsConfirmKeep");
            _revert = root.Q<Button>("SettingsConfirmRevert");
            _close.clicked += () => SetOpen(false);
            _resetCategory.clicked += ResetCurrentCategory;
            _keep.clicked += () =>
            {
                DisplayModeService.Keep();
                _feedback.text = GameText.Get("settings.display.kept");
                RefreshAll();
            };
            _revert.clicked += () =>
            {
                DisplayModeService.Revert();
                _feedback.text = GameText.Get("settings.display.reverted");
                RefreshAll();
            };
            var tabs = new List<Button>();
            foreach (string n in TabNames)
            {
                tabs.Add(root.Q<Button>(n));
            }
            _tabs = new UiTabs(tabs, _ => Rebuild());
            ApplyTexts();
            _tabs.Select(0);
        }

        private void ApplyTexts()
        {
            _title.text = GameText.Get("settings.title");
            _close.text = GameText.Get("ui.common.close");
            _resetCategory.text = GameText.Get("settings.reset_category");
            _keep.text = GameText.Get("settings.display.keep");
            _revert.text = GameText.Get("settings.display.revert");
            var labels = new List<string>();
            foreach (SettingsCategory c in SettingsCatalog.Categories)
            {
                labels.Add(GameText.Get(SettingsCatalog.CategoryKey(c)));
            }
            _tabs.SetLabels(labels);
        }

        public void SetOpen(bool open)
        {
            if (_window == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _window.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _feedback.text = string.Empty;
                Rebuild();
            }
            else
            {
                if (DisplayModeService.Pending)
                {
                    DisplayModeService.Revert(); // 带着看不见倒数的改动离开 = 还原（FGR-SYS-021 的安全侧）。
                }
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
            }
        }

        public void SelectCategory(SettingsCategory c) => _tabs.Select(System.Array.IndexOf(SettingsCatalog.Categories, c));

        private void ResetCurrentCategory()
        {
            SettingsCatalog.ResetCategory(Category);
            _feedback.text = GameText.Format(Category == SettingsCategory.Controls ? "settings.reset_done_controls" : "settings.reset_done", GameText.Get(SettingsCatalog.CategoryKey(Category)));
            RefreshAll();
        }

        /// <summary>按当前分类重建行（行数随分类变，放滚动区）。</summary>
        public void Rebuild()
        {
            if (_body == null)
            {
                return;
            }
            SettingsCatalog.Items(Category, _items);
            _body.Clear();
            _rows.Clear();
            foreach (SettingItem item in _items)
            {
                VisualElement row = MakeRow(item);
                _body.Add(row);
                _rows.Add(row);
            }
            RefreshAll();
        }

        private VisualElement MakeRow(SettingItem item)
        {
            var row = new VisualElement { userData = item };
            row.AddToClassList("uk-settings-row");
            var name = new Label { name = "Name" };
            name.AddToClassList("uk-settings-name");
            row.Add(name);
            var value = new Label { name = "Value" };
            value.AddToClassList("uk-settings-value");
            row.Add(value);
            switch (item.Kind)
            {
                case SettingKind.Toggle:
                {
                    var b = new Button { name = "Control" };
                    b.AddToClassList("mw-btn");
                    b.AddToClassList("uk-settings-toggle");
                    b.clicked += () =>
                    {
                        item.SetBool(!item.GetBool());
                        RefreshAll();
                    };
                    row.Add(b);
                    break;
                }
                case SettingKind.Slider:
                {
                    var s = new Slider(item.Min, item.Max) { name = "Control" };
                    s.AddToClassList("uk-settings-slider");
                    s.RegisterValueChangedCallback(e =>
                    {
                        item.SetFloat(e.newValue);
                        row.Q<Label>("Value").text = item.ValueText;
                    });
                    row.Add(s);
                    break;
                }
                case SettingKind.Choice:
                {
                    var d = new DropdownField { name = "Control" };
                    d.AddToClassList("uk-settings-choice");
                    d.RegisterValueChangedCallback(_ =>
                    {
                        if (d.index >= 0 && d.index != item.GetIndex())
                        {
                            item.SetIndex(d.index);
                            AfterChoice(item);
                        }
                    });
                    row.Add(d);
                    break;
                }
                case SettingKind.Action:
                {
                    var b = new Button { name = "Control" };
                    b.AddToClassList("mw-btn");
                    b.AddToClassList("uk-settings-action");
                    b.clicked += () => item.Invoke?.Invoke();
                    row.Add(b);
                    break;
                }
            }
            return row;
        }

        private void AfterChoice(SettingItem item)
        {
            if (!string.IsNullOrEmpty(DisplayModeService.LastError) && (item.Id == "display.resolution" || item.Id == "display.window_mode"))
            {
                _feedback.text = DisplayModeService.LastError;
            }
            RefreshAll();
        }

        /// <summary>把每行的值、可用性刷新成设置层的当前值（设置在别处被改了也会跟上）。</summary>
        public void RefreshAll()
        {
            foreach (VisualElement row in _rows)
            {
                RefreshRow(row);
            }
            RefreshConfirm();
            _seenSettings = GameSettings.Revision;
            _seenDisplay = DisplayModeService.Revision;
        }

        private static void RefreshRow(VisualElement row)
        {
            var item = (SettingItem)row.userData;
            bool available = item.Available == null || item.Available();
            row.Q<Label>("Name").text = item.Name;
            row.Q<Label>("Value").text = available ? item.ValueText : GameText.Get(item.UnavailableKey ?? "settings.need_campaign");
            row.EnableInClassList("uk-settings-disabled", !available);
            VisualElement control = row.Q<VisualElement>("Control");
            switch (control)
            {
                case Slider s:
                    s.SetValueWithoutNotify(item.GetFloat());
                    break;
                case DropdownField d:
                {
                    List<string> choices = item.Choices();
                    DropdownChoices.Apply(d, choices, GameText.Get("settings.none"));
                    int i = item.GetIndex();
                    if (i >= 0 && i < choices.Count)
                    {
                        d.SetValueWithoutNotify(choices[i]);
                    }
                    break;
                }
                case Button b when item.Kind == SettingKind.Toggle:
                    b.text = item.ValueText;
                    b.EnableInClassList("uk-settings-toggle-on", item.GetBool());
                    row.Q<Label>("Value").text = string.Empty;
                    break;
                case Button b when item.Kind == SettingKind.Action:
                    b.text = GameText.Get("settings.open");
                    row.Q<Label>("Value").text = available ? string.Empty : GameText.Get(item.UnavailableKey ?? "settings.need_campaign");
                    break;
            }
            if (!available)
            {
                control?.SetEnabled(false); // 放在最后：DropdownChoices.Apply 会按选项数重新启用下拉框。
            }
            else if (!(control is DropdownField))
            {
                control?.SetEnabled(true);
            }
        }

        private void RefreshConfirm()
        {
            bool pending = DisplayModeService.Pending;
            _confirm.EnableInClassList("uk-hidden", !pending);
            if (pending)
            {
                int left = Mathf.CeilToInt(DisplayModeService.SecondsLeft);
                _shownSeconds = left;
                _confirmText.text = GameText.Format("settings.display.confirm", left.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void Update()
        {
            if (!IsOpen || _window == null)
            {
                return;
            }
            Tick();
        }

        /// <summary>每帧（自检直接调）：设置或显示状态变了就刷新；倒数秒数变了就改确认条文字；倒数到点被还原时提示一次。</summary>
        public void Tick()
        {
            if (_seenDisplay != DisplayModeService.Revision)
            {
                bool wasPending = ConfirmVisible;
                RefreshAll();
                if (wasPending && !DisplayModeService.Pending && string.IsNullOrEmpty(_feedback.text))
                {
                    _feedback.text = GameText.Get("settings.display.reverted");
                }
                if (!string.IsNullOrEmpty(DisplayModeService.LastError))
                {
                    _feedback.text = DisplayModeService.LastError;
                }
                return;
            }
            if (_seenSettings != GameSettings.Revision)
            {
                ApplyTexts();
                RefreshAll();
                return;
            }
            if (DisplayModeService.Pending && Mathf.CeilToInt(DisplayModeService.SecondsLeft) != _shownSeconds)
            {
                RefreshConfirm();
            }
        }

        protected override void OnDestroy()
        {
            if (IsOpen)
            {
                SetOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
