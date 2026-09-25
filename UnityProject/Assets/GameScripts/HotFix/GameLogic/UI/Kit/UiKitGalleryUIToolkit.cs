using System.Collections.Generic;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-ARC-007“12 类基础件都有可交互样例”）：开发用样例页。每一节接的都是真实服务：
    /// 通知中心（含 1 秒 50 条压测）、确认框、悬停提示（数值来源展开）、右键菜单（含不可用原因）、拖放（含拒绝原因）、
    /// 搜索框 + 1 万条虚拟化列表、标签页、折线 / 柱状图、快捷键提示（跟随改键）、进度条（含受阻态）、状态图标（含灰度对照）。
    /// 入口：开发构建的暂停菜单“界面基础件样例”；自检把同一份 UXML 挂到临时面板上逐节驱动。
    /// </summary>
    public sealed class UiKitGalleryUIToolkit : UiKitPanelHost
    {
        public const int Order = 30090;
        public const int ListSize = 10000;

        public static UiKitGalleryUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private readonly List<string> _allItems = new List<string>();
        private readonly List<string> _shownItems = new List<string>();
        private VisualElement _window;
        private Label _confirmResult;
        private Label _menuResult;
        private Label _dragResult;
        private Label _tabBody;
        private UiTabs _tabs;
        private UiSearchBox _search;
        private UiVirtualList _list;
        private UiLineChart _line;
        private UiBarChart _bar;
        private UiProgressBar _progress;
        private VisualElement _statusRow;
        private bool _grayscale;

        public UiVirtualList List => _list;
        public UiLineChart LineChart => _line;
        public UiBarChart BarChart => _bar;
        public UiProgressBar Progress => _progress;
        public UiTabs Tabs => _tabs;
        public UiSearchBox Search => _search;
        public IReadOnlyList<string> ShownItems => _shownItems;
        public VisualElement TooltipTarget { get; private set; }
        public VisualElement MenuTarget { get; private set; }
        public VisualElement DragSource { get; private set; }
        public VisualElement DropA { get; private set; }
        public VisualElement DropLocked { get; private set; }
        public string LastDropped { get; private set; }

        protected override string UxmlLocation => "UiKitGallery";
        protected override int SortingOrder => Order;

        private void Awake()
        {
            Instance = this;
        }

        public static void Open() => Instance?.SetOpen(true);

        public static void Close()
        {
            if (IsOpen)
            {
                Instance?.SetOpen(false);
            }
        }

        protected override void OnReady(VisualElement root) => BindView(root);

        public void BindView(VisualElement root)
        {
            _window = root.Q<VisualElement>("GalleryRoot");
            root.Q<Label>("GalleryTitle").text = GameText.Get("ui.gallery.title");
            Button close = root.Q<Button>("GalleryClose");
            close.text = GameText.Get("ui.common.close");
            close.clicked += () => SetOpen(false);

            Title(root, "GallerySecNotify", "ui.gallery.section_notify");
            Title(root, "GallerySecConfirm", "ui.gallery.section_confirm");
            Title(root, "GallerySecTooltip", "ui.gallery.section_tooltip");
            Title(root, "GallerySecMenu", "ui.gallery.section_menu");
            Title(root, "GallerySecDrag", "ui.gallery.section_drag");
            Title(root, "GallerySecSearch", "ui.gallery.section_search");
            Title(root, "GallerySecList", "ui.gallery.section_list");
            Title(root, "GallerySecTabs", "ui.gallery.section_tabs");
            Title(root, "GallerySecChart", "ui.gallery.section_chart");
            Title(root, "GallerySecHint", "ui.gallery.section_hint");
            Title(root, "GallerySecProgress", "ui.gallery.section_progress");
            Title(root, "GallerySecStatus", "ui.gallery.section_status");

            // 1 通知中心
            Btn(root, "GalleryPostUrgent", "ui.gallery.post_urgent", () => NotificationCenter.Post("machine_destroyed", "#7", new Vector3(10f, 0f, 12f)));
            Btn(root, "GalleryPostWarning", "ui.gallery.post_warning", () => NotificationCenter.Post("power_lost", "building.generator.name", new Vector3(4f, 0f, -6f)));
            Btn(root, "GalleryPostInfo", "ui.gallery.post_info", () => NotificationCenter.Post("research_done", "ui.gallery.tab_a"));
            Btn(root, "GalleryPostBurst", "ui.gallery.post_burst", PostBurst);

            // 2 确认框
            _confirmResult = root.Q<Label>("GalleryConfirmResult");
            Btn(root, "GalleryOpenConfirm", "ui.gallery.open_confirm", OpenSampleConfirm);

            // 3 悬停提示（数值来源展开）
            TooltipTarget = root.Q<Label>("GalleryTooltipTarget");
            ((Label)TooltipTarget).text = GameText.Get("ui.gallery.tooltip_target");
            UiTooltip.Attach(TooltipTarget, SampleTooltip);

            // 4 右键菜单
            MenuTarget = root.Q<Label>("GalleryMenuTarget");
            ((Label)MenuTarget).text = GameText.Get("ui.gallery.menu_target");
            _menuResult = root.Q<Label>("GalleryMenuResult");
            UiContextMenu.Attach(MenuTarget, SampleMenu);

            // 5 拖放
            DragSource = root.Q<Label>("GalleryDragSource");
            ((Label)DragSource).text = GameText.Get("ui.gallery.drag_source");
            DropA = root.Q<Label>("GalleryDropA");
            ((Label)DropA).text = GameText.Format("ui.gallery.drag_slot", "1");
            Label dropB = root.Q<Label>("GalleryDropB");
            dropB.text = GameText.Format("ui.gallery.drag_slot", "2");
            DropLocked = root.Q<Label>("GalleryDropLocked");
            ((Label)DropLocked).text = GameText.Get("ui.gallery.drag_slot_locked");
            _dragResult = root.Q<Label>("GalleryDragResult");
            UiDragDrop.MakeSource(DragSource, () => "chip", () => GameText.Get("ui.gallery.drag_source"));
            UiDragDrop.MakeTarget(DropA, _ => new DropVerdict(true), p => OnDropped(((Label)DropA).text));
            UiDragDrop.MakeTarget(dropB, _ => new DropVerdict(true), p => OnDropped(dropB.text));
            UiDragDrop.MakeTarget(DropLocked, _ => new DropVerdict(false, GameText.Get("ui.gallery.drag_locked_reason")), null);

            // 6 搜索框 + 8 虚拟化列表（1 万条）
            _allItems.Clear();
            for (int i = 1; i <= ListSize; i++)
            {
                _allItems.Add(GameText.Format("ui.gallery.list_row", i.ToString(CultureInfo.InvariantCulture)));
            }
            _list = new UiVirtualList(root.Q<ListView>("GalleryList"), root.Q<Label>("GalleryListEmpty"), () =>
            {
                var l = new Label();
                l.AddToClassList("uk-list-cell");
                var row = new VisualElement();
                row.Add(l);
                return row;
            }, (row, i) => ((Label)row[0]).text = i < _shownItems.Count ? _shownItems[i] : string.Empty);
            _search = new UiSearchBox(root.Q<TextField>("GallerySearch"), root.Q<Label>("GallerySearchPlaceholder"),
                root.Q<Button>("GallerySearchClear"), "ui.keybind.search_placeholder", ApplySearch);
            ApplySearch(string.Empty);

            // 7 标签页
            _tabBody = root.Q<Label>("GalleryTabBody");
            _tabs = new UiTabs(new[] { root.Q<Button>("GalleryTabA"), root.Q<Button>("GalleryTabB"), root.Q<Button>("GalleryTabC") },
                i => _tabBody.text = GameText.Format("ui.gallery.tab_body", GameText.Get(i == 0 ? "ui.gallery.tab_a" : i == 1 ? "ui.gallery.tab_b" : "ui.gallery.tab_c")));
            _tabs.SetLabels(new[] { GameText.Get("ui.gallery.tab_a"), GameText.Get("ui.gallery.tab_b"), GameText.Get("ui.gallery.tab_c") });
            _tabs.Select(0);

            // 9 图表
            root.Q<Label>("GalleryLineCaption").text = GameText.Get("ui.gallery.chart_line");
            root.Q<Label>("GalleryBarCaption").text = GameText.Get("ui.gallery.chart_bar");
            _line = new UiLineChart(root.Q<VisualElement>("GalleryLineChart"));
            var values = new List<float>();
            for (int i = 0; i < 60; i++)
            {
                values.Add(20f + 8f * Mathf.Sin(i * 0.3f) + i * 0.2f);
            }
            _line.SetValues(values);
            _bar = new UiBarChart(root.Q<VisualElement>("GalleryBarChart"));
            _bar.SetBars(new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>(GameText.Get("ui.gallery.tab_a"), 30f),
                new KeyValuePair<string, float>(GameText.Get("ui.gallery.tab_b"), 12f),
                new KeyValuePair<string, float>(GameText.Get("ui.gallery.tab_c"), 21f),
            });

            // 10 快捷键提示
            root.Q<Label>("GalleryHintPauseName").text = KeyBindingFlow.ActionName(GameActionId.TogglePause);
            root.Q<Label>("GalleryHintNotifyName").text = KeyBindingFlow.ActionName(GameActionId.ToggleNotificationCenter);
            root.Q<Label>("GalleryHintUndoName").text = KeyBindingFlow.ActionName(GameActionId.Undo);
            UiShortcutHint.Bind(root.Q<Label>("GalleryHintPause"), GameActionId.TogglePause);
            UiShortcutHint.Bind(root.Q<Label>("GalleryHintNotify"), GameActionId.ToggleNotificationCenter);
            UiShortcutHint.Bind(root.Q<Label>("GalleryHintUndo"), GameActionId.Undo);

            // 11 进度条
            _progress = new UiProgressBar(root.Q<VisualElement>("GalleryProgress"));
            _progress.Set(0.3f);
            Btn(root, "GalleryProgressStep", "ui.gallery.progress_step", () => _progress.Set(_progress.Value >= 1f ? 0f : _progress.Value + 0.1f, _progress.Blocked, GameText.Get("status.no_power.name")));
            Btn(root, "GalleryProgressBlock", "ui.gallery.progress_block", () => _progress.Set(_progress.Value, !_progress.Blocked, GameText.Get("status.no_power.name")));

            // 12 状态图标
            _statusRow = root.Q<VisualElement>("GalleryStatusRow");
            for (int i = 0; i < UiStatusIcon.StatusCount; i++)
            {
                var status = (UiEntityStatus)i;
                UiStatusIcon.Set(root.Q<VisualElement>("GalleryStatus" + i), status);
                root.Q<Label>("GalleryStatusName" + i).text = GameText.Get(UiStatusIcon.NameKey(status));
            }
            Btn(root, "GalleryColorblind", "ui.gallery.colorblind", () =>
            {
                _grayscale = !_grayscale;
                _statusRow.EnableInClassList("uk-grayscale", _grayscale);
            });
        }

        private static void Title(VisualElement root, string name, string key)
        {
            Label l = root.Q<Label>(name);
            if (l != null)
            {
                l.text = GameText.Get(key);
            }
        }

        private static void Btn(VisualElement root, string name, string key, System.Action onClick)
        {
            Button b = root.Q<Button>(name);
            if (b == null)
            {
                return;
            }
            b.text = GameText.Get(key);
            b.clicked += onClick;
        }

        /// <summary>FG13 第 12 节负向：1 秒内 50 条通知。5 类各 10 条，应聚合成 5 条、弹出条不超过上限。</summary>
        public static void PostBurst()
        {
            string[] types = { "power_lost", "storage_full", "signal_lost", "machine_destroyed", "production_complete" };
            for (int i = 0; i < 50; i++)
            {
                NotificationCenter.Post(types[i % types.Length], "#" + i.ToString(CultureInfo.InvariantCulture),
                    new Vector3(i, 0f, -i));
            }
        }

        private void OpenSampleConfirm()
        {
            var request = new ConfirmRequest
            {
                Title = GameText.Get("ui.gallery.confirm_title"),
                Irreversible = true,
                OnConfirm = () => _confirmResult.text = GameText.Get("ui.common.confirm"),
                OnCancel = () => _confirmResult.text = GameText.Get("ui.common.cancel"),
            };
            request.Consequences.Add(GameText.Format("ui.gallery.confirm_line", "7"));
            UiConfirmDialog.Show(request);
        }

        public static TooltipContent SampleTooltip()
        {
            var content = new TooltipContent
            {
                Title = GameText.Get("ui.gallery.tooltip_title"),
                Body = GameText.Get("ui.gallery.tooltip_body"),
                Total = GameText.Format("ui.tooltip.total", UiFormat.PerMinute(8.4)),
                Shortcut = GameActionId.ToggleNotificationCenter,
                CodexEntry = GameText.Get("building.assembly_station.name"),
            };
            content.Sources.Add(new TooltipSource(GameText.Get("ui.gallery.source_base"), UiFormat.Signed(12, UiFormat.PerMinute(12))));
            content.Sources.Add(new TooltipSource(GameText.Get("ui.gallery.source_power"), UiFormat.Signed(-0.3, UiFormat.Percent(-0.3))));
            content.Sources.Add(new TooltipSource(GameText.Get("ui.gallery.source_research"), UiFormat.Signed(0.05, UiFormat.Percent(0.05))));
            return content;
        }

        private List<ContextMenuItem> SampleMenu()
        {
            string inspect = GameText.Get("ui.gallery.menu_inspect");
            string rename = GameText.Get("ui.gallery.menu_rename");
            return new List<ContextMenuItem>
            {
                new ContextMenuItem(inspect, () => _menuResult.text = GameText.Format("ui.gallery.menu_result", inspect)),
                new ContextMenuItem(rename, () => _menuResult.text = GameText.Format("ui.gallery.menu_result", rename)),
                ContextMenuItem.Disabled(GameText.Get("ui.gallery.menu_demolish"), GameText.Get("ui.gallery.menu_demolish_reason")),
            };
        }

        private void OnDropped(string where)
        {
            LastDropped = where;
            _dragResult.text = GameText.Format("ui.gallery.drag_result", where);
        }

        private void ApplySearch(string query)
        {
            _shownItems.Clear();
            string q = (query ?? string.Empty).Trim();
            foreach (string item in _allItems)
            {
                if (q.Length == 0 || item.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _shownItems.Add(item);
                }
            }
            _list.SetItems(_shownItems, GameText.Format("ui.keybind.empty", q));
        }

        public void SetOpen(bool open)
        {
            if (_window == null)
            {
                return;
            }
            IsOpen = open;
            _window.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
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
