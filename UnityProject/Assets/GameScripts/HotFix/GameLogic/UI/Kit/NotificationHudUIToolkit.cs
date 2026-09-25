using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-020/021/022、FG00 B08、FGT-UX-004）：通知弹出条 + 通知中心窗口。只读 <see cref="NotificationCenter"/>，
    /// 按它的版本号刷新（没有变化的帧 O(1)）。
    /// - 弹出条：屏幕右侧（FGR-UX-005），等级图标（形状 + 颜色）、正文、聚合次数；点击定位到实体，没有位置时打开通知中心并选中这条；
    /// - 通知中心：等级标签页 + 类型下拉筛选、虚拟化历史列表（最新在前）、聚合展开看每一条成员并逐条定位、
    ///   设置页（停留时长、按类型勾选“触发时自动暂停”）；
    /// - 入口：屏幕右侧“通知中心”按钮（有未读紧急通知时显示条数并变红）、快捷键（默认 `）、暂停菜单。
    /// 通知中心不是模态窗口（玩家可以边看边操作），但压一层 Esc 栈，Esc 关闭。
    /// </summary>
    public sealed class NotificationHudUIToolkit : UiKitPanelHost
    {
        public const int Order = 30040;

        private static readonly NotifyLevel?[] TabLevels = { null, NotifyLevel.Urgent, NotifyLevel.Warning, NotifyLevel.Info };

        public static NotificationHudUIToolkit Instance { get; private set; }
        public static bool CenterOpen { get; private set; }

        private readonly List<NotificationEntry> _rows = new List<NotificationEntry>();
        private readonly List<VisualElement> _toastPool = new List<VisualElement>();
        private readonly List<string> _typeIds = new List<string>();

        private VisualElement _column;
        private VisualElement _toastList;
        private Button _entryButton;
        private VisualElement _center;
        private Label _title;
        private Label _status;
        private Button _settingsToggle;
        private Button _close;
        private UiTabs _tabs;
        private DropdownField _typeFilter;
        private UiVirtualList _list;
        private VisualElement _detail;
        private Label _detailTitle;
        private VisualElement _detailMembers;
        private VisualElement _settings;
        private Label _durationLabel;
        private Slider _duration;
        private Label _autoPauseTitle;
        private VisualElement _autoPauseList;
        private NotificationEntry _selected;
        private bool _settingsOpen;
        private int _seenRevision = -1;
        private int _seenSettings = -1;
        private string _statusOverride;

        public IReadOnlyList<NotificationEntry> VisibleRows => _rows;
        public int VisibleToastCount
        {
            get
            {
                int n = 0;
                foreach (VisualElement t in _toastPool)
                {
                    if (!t.ClassListContains("uk-hidden"))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        protected override string UxmlLocation => "NotificationHud";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        public static void ToggleCenter()
        {
            if (Instance != null)
            {
                Instance.SetCenterOpen(!CenterOpen);
            }
        }

        public static void OpenCenter() => Instance?.SetCenterOpen(true);

        public static void CloseCenter()
        {
            if (CenterOpen)
            {
                Instance?.SetCenterOpen(false);
            }
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            _column = root.Q<VisualElement>("ToastColumn");
            _toastList = root.Q<VisualElement>("ToastList");
            _entryButton = root.Q<Button>("NotifyCenterButton");
            _center = root.Q<VisualElement>("NotifyCenter");
            _title = root.Q<Label>("NotifyTitle");
            _status = root.Q<Label>("NotifyStatus");
            _settingsToggle = root.Q<Button>("NotifySettingsToggle");
            _close = root.Q<Button>("NotifyClose");
            _typeFilter = root.Q<DropdownField>("NotifyTypeFilter");
            _detail = root.Q<VisualElement>("NotifyDetail");
            _detailTitle = root.Q<Label>("NotifyDetailTitle");
            _detailMembers = root.Q<VisualElement>("NotifyDetailMembers");
            _settings = root.Q<VisualElement>("NotifySettings");
            _durationLabel = root.Q<Label>("NotifyDurationLabel");
            _duration = root.Q<Slider>("NotifyDuration");
            _autoPauseTitle = root.Q<Label>("NotifyAutoPauseTitle");
            _autoPauseList = root.Q<VisualElement>("NotifyAutoPauseList");

            _entryButton.clicked += ToggleCenter;
            _close.clicked += () => SetCenterOpen(false);
            _settingsToggle.clicked += () =>
            {
                _settingsOpen = !_settingsOpen;
                RenderCenter();
            };
            _tabs = new UiTabs(new[]
            {
                root.Q<Button>("NotifyTabAll"), root.Q<Button>("NotifyTabUrgent"), root.Q<Button>("NotifyTabWarning"), root.Q<Button>("NotifyTabInfo"),
            }, _ => RenderCenter());
            _typeFilter.RegisterValueChangedCallback(_ => RenderCenter());
            _list = new UiVirtualList(root.Q<ListView>("NotifyList"), root.Q<Label>("NotifyEmpty"), MakeRow, BindRow);
            _duration.lowValue = UiTuningValues.Get("notify.toast_scale_min");
            _duration.highValue = UiTuningValues.Get("notify.toast_scale_max");
            _duration.RegisterValueChangedCallback(e =>
            {
                GameSettings.SetNotificationToastScale(e.newValue);
                RenderSettings();
            });
            UiTooltip.Attach(_entryButton, () => new TooltipContent
            {
                Title = GameText.Get("ui.notify.title"),
                Shortcut = GameActionId.ToggleNotificationCenter,
            });
            ApplyTexts();
            _tabs.Select(0);
        }

        private void ApplyTexts()
        {
            _title.text = GameText.Get("ui.notify.title");
            _close.text = GameText.Get("ui.common.close");
            _settingsToggle.text = GameText.Get("ui.notify.settings");
            _tabs.SetLabels(new[]
            {
                GameText.Get("ui.notify.filter_all"), GameText.Get("notify.tier.urgent"), GameText.Get("notify.tier.warning"), GameText.Get("notify.tier.info"),
            });
            _autoPauseTitle.text = GameText.Get("ui.notify.auto_pause");
            RebuildTypeChoices();
            _seenSettings = GameSettings.Revision;
        }

        private void RebuildTypeChoices()
        {
            _typeIds.Clear();
            var choices = new List<string> { GameText.Get("ui.notify.filter_type_all") };
            _typeIds.Add(null);
            foreach (NotifyTypeDef def in NotificationCatalog.AllTypes)
            {
                if (!def.KeepInHistory)
                {
                    continue;
                }
                choices.Add(GameText.Get(def.NameKey));
                _typeIds.Add(def.Id);
            }
            DropdownChoices.Apply(_typeFilter, choices, GameText.Get("ui.common.empty"));
        }

        public void SetCenterOpen(bool open)
        {
            if (_center == null)
            {
                return;
            }
            CenterOpen = open;
            _center.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.NotificationCenterFirstOpen);
                NotificationCenter.MarkAllRead();
                UiEscapeStack.Push(this, () => SetCenterOpen(false));
                _statusOverride = null;
                RenderCenter();
            }
            else
            {
                UiEscapeStack.Remove(this);
                _selected = null;
                _settingsOpen = false;
            }
        }

        /// <summary>按等级标签页选中项与类型下拉筛选（自检也调用）。</summary>
        public void SelectFilters(int levelTab, string typeId)
        {
            int typeIndex = Mathf.Max(0, _typeIds.IndexOf(typeId));
            if (_typeFilter.choices != null && typeIndex < _typeFilter.choices.Count)
            {
                _typeFilter.SetValueWithoutNotify(_typeFilter.choices[typeIndex]);
            }
            _tabs.Select(levelTab);
        }

        private string SelectedTypeId()
        {
            int i = _typeFilter.choices?.IndexOf(_typeFilter.value) ?? 0;
            return i > 0 && i < _typeIds.Count ? _typeIds[i] : null;
        }

        private void Update()
        {
            if (_column == null)
            {
                return;
            }
            bool inWorld = CampaignSession.Current != null && GameRoot.AnyRegionActive;
            _column.EnableInClassList("uk-hidden", !inWorld);
            if (!inWorld)
            {
                if (CenterOpen)
                {
                    SetCenterOpen(false);
                }
                return;
            }
            if (_seenSettings != GameSettings.Revision)
            {
                ApplyTexts();
                _seenRevision = -1;
            }
            if (_seenRevision == NotificationCenter.Revision)
            {
                return;
            }
            _seenRevision = NotificationCenter.Revision;
            RenderToasts();
            RenderEntryButton();
            if (CenterOpen)
            {
                RenderCenter();
            }
        }

        // ── 弹出条 ───────────────────────────────────────────────────────

        private void RenderEntryButton()
        {
            int unread = NotificationCenter.UnreadUrgent;
            _entryButton.text = unread > 0
                ? GameText.Format("ui.notify.entry_unread", GameText.Get("ui.notify.title"), unread.ToString(CultureInfo.InvariantCulture))
                : GameText.Get("ui.notify.title");
            _entryButton.EnableInClassList("uk-notify-entry-urgent", unread > 0);
        }

        /// <summary>按 <see cref="NotificationCenter.Toasts"/> 重画弹出条（元素复用，不随通知数量增长）。</summary>
        public void RenderToasts()
        {
            IReadOnlyList<NotificationEntry> toasts = NotificationCenter.Toasts;
            while (_toastPool.Count < toasts.Count)
            {
                _toastPool.Add(MakeToast());
            }
            for (int i = 0; i < _toastPool.Count; i++)
            {
                VisualElement toast = _toastPool[i];
                bool show = i < toasts.Count;
                toast.EnableInClassList("uk-hidden", !show);
                if (!show)
                {
                    toast.userData = null;
                    continue;
                }
                NotificationEntry e = toasts[i];
                toast.userData = e;
                toast.EnableInClassList("uk-toast-urgent", e.Level == NotifyLevel.Urgent);
                toast.EnableInClassList("uk-toast-warning", e.Level == NotifyLevel.Warning);
                toast.EnableInClassList("uk-toast-info", e.Level == NotifyLevel.Info);
                UiStatusIcon.SetLevel(toast.Q<VisualElement>("ToastIcon"), e.Level);
                toast.Q<Label>("ToastText").text = e.Text;
                toast.Q<Label>("ToastMeta").text = e.Count > 1
                    ? GameText.Format("ui.notify.times", e.Count.ToString(CultureInfo.InvariantCulture))
                    : string.Empty;
            }
        }

        private VisualElement MakeToast()
        {
            var toast = new VisualElement();
            toast.AddToClassList("uk-toast");
            var icon = new VisualElement { name = "ToastIcon", pickingMode = PickingMode.Ignore };
            UiStatusIcon.Build(icon);
            var text = new Label { name = "ToastText", pickingMode = PickingMode.Ignore };
            text.AddToClassList("uk-toast-text");
            var meta = new Label { name = "ToastMeta", pickingMode = PickingMode.Ignore };
            meta.AddToClassList("uk-toast-meta");
            toast.Add(icon);
            toast.Add(text);
            toast.Add(meta);
            toast.RegisterCallback<ClickEvent>(_ => OnToastClicked(toast.userData as NotificationEntry));
            UiTooltip.Attach(toast, () =>
            {
                var e = toast.userData as NotificationEntry;
                if (e == null)
                {
                    return null;
                }
                return new TooltipContent
                {
                    Title = GameText.Get(e.Type.NameKey),
                    Body = e.Text + (string.IsNullOrEmpty(e.SourceText) ? string.Empty : "\n" + e.SourceText),
                    Shortcut = GameActionId.ToggleNotificationCenter,
                };
            });
            _toastList.Add(toast);
            return toast;
        }

        /// <summary>点弹出条：有位置就定位，没有位置（或定位失败）就打开通知中心并选中这条、显示原因。</summary>
        public void OnToastClicked(NotificationEntry e)
        {
            if (e == null)
            {
                return;
            }
            if (e.HasAnyLocation && NotificationCenter.Locate(e, -1, out _))
            {
                NotificationCenter.DismissToast(e);
                return;
            }
            SetCenterOpen(true);
            Select(e);
        }

        // ── 通知中心 ─────────────────────────────────────────────────────

        private void RenderCenter()
        {
            if (_center == null || !CenterOpen)
            {
                return;
            }
            _settings.EnableInClassList("uk-hidden", !_settingsOpen);
            _center.Q<VisualElement>("NotifyListBody").EnableInClassList("uk-hidden", _settingsOpen);
            _center.Q<VisualElement>("NotifyToolbar").EnableInClassList("uk-hidden", _settingsOpen);
            if (_settingsOpen)
            {
                _detail.EnableInClassList("uk-hidden", true);
                RenderSettings();
                return;
            }
            int tab = Mathf.Clamp(_tabs.Selected, 0, TabLevels.Length - 1);
            NotificationCenter.Query(TabLevels[tab], SelectedTypeId(), _rows);
            _list.SetItems(_rows, GameText.Get("ui.notify.empty"));
            _status.text = _statusOverride ?? GameText.Format("ui.notify.count",
                NotificationCenter.History.Count.ToString(CultureInfo.InvariantCulture), _rows.Count.ToString(CultureInfo.InvariantCulture));
            RenderDetail();
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("uk-notify-row");
            var icon = new VisualElement { name = "RowIcon" };
            UiStatusIcon.Build(icon);
            var time = new Label { name = "RowTime" };
            time.AddToClassList("uk-list-cell-fixed");
            var text = new Label { name = "RowText" };
            text.AddToClassList("uk-notify-row-text");
            var locate = new Button { name = "RowLocate" };
            locate.AddToClassList("mw-btn");
            locate.AddToClassList("uk-notify-row-btn");
            locate.clicked += () => LocateRow(locate.userData as NotificationEntry, -1);
            var expand = new Button { name = "RowExpand" };
            expand.AddToClassList("mw-btn");
            expand.AddToClassList("uk-notify-row-btn");
            expand.clicked += () => Select(expand.userData as NotificationEntry);
            row.Add(icon);
            row.Add(time);
            row.Add(text);
            row.Add(locate);
            row.Add(expand);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _rows.Count)
            {
                return;
            }
            NotificationEntry e = _rows[index];
            UiStatusIcon.SetLevel(row.Q<VisualElement>("RowIcon"), e.Level);
            row.Q<Label>("RowTime").text = NotificationCenter.TimeText(e.Latest);
            row.Q<Label>("RowText").text = string.IsNullOrEmpty(e.SourceText) ? e.Text : e.Text + " · " + e.SourceText;
            var locate = row.Q<Button>("RowLocate");
            locate.text = GameText.Get("ui.notify.locate");
            locate.userData = e;
            locate.SetEnabled(e.HasAnyLocation);
            var expand = row.Q<Button>("RowExpand");
            expand.text = e == _selected
                ? GameText.Get("ui.notify.collapse")
                : GameText.Format("ui.notify.expand", e.Members.Count.ToString(CultureInfo.InvariantCulture));
            expand.userData = e;
        }

        /// <summary>选中（展开）一条聚合通知，再点一次收起。</summary>
        public void Select(NotificationEntry e)
        {
            _selected = _selected == e ? null : e;
            RenderCenter();
        }

        private void RenderDetail()
        {
            bool show = _selected != null;
            _detail.EnableInClassList("uk-hidden", !show);
            _detailMembers.Clear();
            if (!show)
            {
                return;
            }
            _detailTitle.text = _selected.Text;
            for (int i = _selected.Members.Count - 1; i >= 0; i--)
            {
                NotificationMember m = _selected.Members[i];
                var row = new VisualElement();
                row.AddToClassList("uk-notify-row");
                var time = new Label(NotificationCenter.TimeText(m));
                time.AddToClassList("uk-list-cell-fixed");
                var text = new Label(string.IsNullOrEmpty(m.DetailText) ? GameText.Get(_selected.Type.NameKey) : m.DetailText);
                text.AddToClassList("uk-notify-row-text");
                var locate = new Button { text = GameText.Get("ui.notify.locate") };
                locate.AddToClassList("mw-btn");
                locate.AddToClassList("uk-notify-row-btn");
                int captured = i;
                NotificationEntry entry = _selected;
                locate.clicked += () => LocateRow(entry, captured);
                locate.SetEnabled(m.HasLocation);
                row.Add(time);
                row.Add(text);
                row.Add(locate);
                _detailMembers.Add(row);
            }
        }

        private void LocateRow(NotificationEntry e, int member)
        {
            if (e == null)
            {
                return;
            }
            _statusOverride = NotificationCenter.Locate(e, member, out string failureKey) ? null : GameText.Get(failureKey);
            RenderCenter();
        }

        private void RenderSettings()
        {
            float scale = GameSettings.NotificationToastScale;
            _duration.SetValueWithoutNotify(scale);
            _durationLabel.text = GameText.Format("ui.notify.duration", "×" + scale.ToString("0.0", CultureInfo.InvariantCulture));
            if (_autoPauseList.childCount == 0)
            {
                foreach (NotifyTypeDef def in NotificationCatalog.AllTypes)
                {
                    if (!def.KeepInHistory || def.Id == "auto_paused")
                    {
                        continue;
                    }
                    var toggle = new Toggle { userData = def };
                    toggle.AddToClassList("uk-toggle-row");
                    toggle.RegisterValueChangedCallback(evt =>
                    {
                        var d = (NotifyTypeDef)toggle.userData;
                        GameSettings.SetNotifyAutoPause(d.Id, evt.newValue);
                    });
                    _autoPauseList.Add(toggle);
                }
            }
            foreach (VisualElement child in _autoPauseList.Children())
            {
                if (child is Toggle t && t.userData is NotifyTypeDef d)
                {
                    t.label = GameText.Get(d.NameKey);
                    t.SetValueWithoutNotify(GameSettings.IsNotifyAutoPauseEnabled(d.Id, d.AutoPauseDefault));
                }
            }
        }

        protected override void OnDestroy()
        {
            if (CenterOpen)
            {
                SetCenterOpen(false);
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
