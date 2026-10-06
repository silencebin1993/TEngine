using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG4-ECO-09（FG04 FGR-ECO-060 离家报告；FG13 FGU-30；FG04 第 4 节“离家报告里的每一条都可以点击定位”）：离家报告面板。
    /// - 选报告：最近 away.reports_keep（3）份已结算的报告，最新的在前；远征还在外时另有一份“进行中”（到目前为止的记录）。
    /// - 每一条都是按钮：点击后定位（镜头飞过去）或打开对应面板（统计面板该物品 / 建筑面板 / 名册里这台机器 / 规则面板 / 电网面板 / 通知中心），面板随之收起；
    ///   定位失败（不在那个地点、没有镜头）时面板不收，写明原因（不静默，B06）。
    /// - “远征回来时自动打开”开关（<see cref="GameSettings.AwayReportAutoOpen"/>，暂停菜单里也有同一个开关）。
    /// 入口：远征回来自动打开（订阅 <see cref="AwayReportService.ReportReadyEvent"/>，下一帧在家园里打开）、暂停菜单“离家报告”、快捷键（默认 Alt+H）。任何地点都能打开。
    /// 模态（Esc / 关闭 / 点遮罩关闭）；只在报告版本、选中、语言、设置变化时重建行（O(行数)），进行中的报告另外每 1 真实秒重读一次。
    /// </summary>
    public sealed class AwayReportPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>统计 30076、反应记录 30077 之上，按键面板 30080 之下（“?”打开的图鉴 30075 不会盖住它，所以条目不带“?”）。</summary>
        public const int Order = 30078;

        public static AwayReportPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static int _pendingSerial;
        private static bool _pendingAuto;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _root;
        private Label _title, _count, _pickLabel, _summary, _message, _empty, _footer;
        private Button _close;
        private DropdownField _pick;
        private Toggle _autoOpen;
        private ScrollView _list;
        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<AwayLine> _lines = new List<AwayLine>();
        private readonly List<int> _pickSerials = new List<int>();
        private int? _lastKey;
        private double _nextReread;
        private string _messageText = string.Empty;

        protected override string UxmlLocation => "AwayReportPanel";
        protected override int SortingOrder => Order;

        // ── 当前视图（自检读写）──
        /// <summary>选中的报告序号（0 = 默认：最新一份；没有已结算的报告时取进行中的那份）。</summary>
        public int SelectedSerial { get; private set; }
        public int ShownSerial { get; private set; }
        public bool ShowingEmptyReport { get; private set; }
        public int AutoOpenCount { get; private set; }
        public AwayLine LastClicked { get; private set; }
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public AwayLine Line(int i) => i >= 0 && i < VisibleRowCount ? _lines[i] : null;
        public Button RowButton(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Button>("ArRow") : null;
        public string RowText(int i) => RowButton(i)?.text ?? string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string MessageText => _messageText;
        public DropdownField PickField => _pick;
        public Toggle AutoOpenToggle => _autoOpen;
        public Button CloseButton => _close;
        public int ChoiceCount => _pickSerials.Count;

        private bool _listening;

        private void Awake()
        {
            Instance = this;
            Listen();
        }

        /// <summary>订阅“报告已结算”（Awake 与 BindView 都会调；编辑模式下 AddComponent 不跑 Awake，自检经 BindView 接上）。只订一次。</summary>
        private void Listen()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            GameEvent.AddEventListener<int>(AwayReportService.ReportReadyEvent, OnReportReady);
            // 修复轮（审查 P2，ADR-ECO-009 §3 入口之一）：点“离家报告”通知（弹出条或通知中心的“打开”）直接打开最新一份。
            NotificationCenter.RegisterOpenHandler("away_report", OpenFromNotification);
        }

        protected override void OnDestroy()
        {
            if (_listening)
            {
                _listening = false;
                GameEvent.RemoveEventListener<int>(AwayReportService.ReportReadyEvent, OnReportReady);
            }
            if (IsOpen && Instance == this)
            {
                SetOpen(false);
            }
            if (_rowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_rowTemplate);
                _rowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        // ── 打开 / 关闭 ──────────────────────────────────────────────────────

        public static void Open() => OpenReport(0);

        /// <summary>打开并选中某一份（0 = 最新）。</summary>
        public static void OpenReport(int serial)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingSerial = serial;
                return;
            }
            Instance.SelectedSerial = serial;
            Instance._lastKey = null;
            if (IsOpen)
            {
                Instance.Refresh();
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
            _pendingAuto = false;
            Instance?.SetOpen(false);
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        /// <summary>点“离家报告”通知：打开最新一份（报告还没结算 / 面板还没挂上时按待打开处理，面板就绪后打开）。</summary>
        private static bool OpenFromNotification(NotificationEntry entry)
        {
            Open();
            return true;
        }

        /// <summary>报告结算（撤离 / 放弃）：设置允许时在下一帧打开这一份（等撤离面板收起、回到家园）。</summary>
        private void OnReportReady(int serial)
        {
            if (!GameSettings.AwayReportAutoOpen)
            {
                return;
            }
            _pendingAuto = true;
            _pendingSerial = serial;
        }

        /// <summary>自检 / 冒烟：立即处理待自动打开（正式流程在 Update 里处理）。</summary>
        public bool TryAutoOpenNow()
        {
            if (!_pendingAuto)
            {
                return false;
            }
            _pendingAuto = false;
            AutoOpenCount++;
            GuidanceHooks.Raise(GuidanceHooks.AwayFirstAutoOpen);
            OpenReport(_pendingSerial);
            return true;
        }

        public static bool AutoOpenPending => _pendingAuto;

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SelectedSerial = _pendingSerial;
                SetOpen(true);
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_rowTemplate != null || _rowTemplateLoading)
            {
                return;
            }
            _rowTemplateLoading = true;
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("AwayReportRow");
            _rowTemplateLoading = false;
            if (this == null)
            {
                if (asset != null)
                {
                    GameModule.Resource.UnloadAsset(asset);
                }
                return;
            }
            _rowTemplate = asset;
            if (_rowTemplate == null)
            {
                Log.Error("[AwayReportPanelUIToolkit] 加载 AwayReportRow 失败，离家报告的行不可用。");
                return;
            }
            _lastKey = null;
            Refresh();
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset template)
        {
            _rowTemplate = template;
            _lastKey = null;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            Listen();
            _root = root.Q<VisualElement>("AwayReportRoot");
            _title = root.Q<Label>("AwayReportTitle");
            _count = root.Q<Label>("AwayReportCount");
            _close = root.Q<Button>("AwayReportClose");
            _pickLabel = root.Q<Label>("AwayReportPickLabel");
            _pick = root.Q<DropdownField>("AwayReportPick");
            _autoOpen = root.Q<Toggle>("AwayReportAutoOpen");
            _summary = root.Q<Label>("AwayReportSummary");
            _message = root.Q<Label>("AwayReportMessage");
            _empty = root.Q<Label>("AwayReportEmpty");
            _list = root.Q<ScrollView>("AwayReportList");
            _footer = root.Q<Label>("AwayReportFooter");
            _close.clicked += () => SetOpen(false);
            _pick.RegisterValueChangedCallback(evt => OnPicked(evt.newValue));
            _autoOpen.RegisterValueChangedCallback(evt =>
            {
                GameSettings.SetAwayReportAutoOpen(evt.newValue);
                _lastKey = null;
            });
            UiTooltip.Attach(_autoOpen, () => new TooltipContent
            {
                Title = GameText.Get("away.panel.auto_open"),
                Body = InputDisplay.ExpandActionTokens(GameText.Get("away.panel.auto_open_tip")),
            });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _rows.Clear();
            _lastKey = null;
        }

        public void SetOpen(bool open)
        {
            if (_root == null || open == IsOpen)
            {
                return;
            }
            IsOpen = open;
            _root.EnableInClassList("uk-hidden", !open);
            if (open)
            {
                GuidanceHooks.Raise(GuidanceHooks.AwayReportFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _messageText = string.Empty;
                _lastKey = null;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                UiTooltip.Hide();
            }
        }

        private void Update()
        {
            if (_pendingAuto && CampaignSession.Current != null && GameRoot.AnyRegionActive && !InputRouter.PanelModalOpen)
            {
                TryAutoOpenNow();
            }
            if (!IsOpen)
            {
                return;
            }
            if (!(CampaignSession.Current != null && GameRoot.AnyRegionActive) && !InWorldOverrideForTests)
            {
                SetOpen(false);
                return;
            }
            Refresh();
        }

        // ── 刷新 ─────────────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            double now = Clock();
            var h = new HashCode();
            h.Add(AwayReportService.Revision);
            h.Add((int)GameText.Language);
            h.Add(GameSettings.Revision);
            h.Add(SelectedSerial);
            h.Add(_rowTemplate != null);
            h.Add(_messageText);
            h.Add(state);
            int key = h.ToHashCode();
            bool openReport = AwayReportService.IsOpen(state);
            if (key == _lastKey && (!openReport || now < _nextReread))
            {
                return;
            }
            _lastKey = key;
            _nextReread = now + 1.0;

            _title.text = GameText.Get("away.panel.title");
            _close.text = GameText.Get("away.panel.close");
            _pickLabel.text = GameText.Get("away.panel.pick");
            _autoOpen.label = GameText.Get("away.panel.auto_open");
            _autoOpen.SetValueWithoutNotify(GameSettings.AwayReportAutoOpen);
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("away.panel.footer"));
            _message.text = _messageText;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));

            List<AwayReportRecord> recent = AwayReportService.Recent(state);
            AwayReportRecord current = AwayReportService.Current(state);
            _count.text = GameText.Format("away.panel.count", AwayReportService.ReportsKeep, recent.Count);
            var labels = new List<string>();
            _pickSerials.Clear();
            if (current != null)
            {
                _pickSerials.Add(current.Serial);
                labels.Add(GameText.Format("away.panel.choice_open", AwayReportService.ShortTitle(current),
                    GameClock.FormatDayTime(current.StartTick / (double)Math.Max(1, GameClock.StepHz))));
            }
            for (int i = 0; i < recent.Count; i++)
            {
                AwayReportRecord r = recent[i];
                _pickSerials.Add(r.Serial);
                double end = r.EndTick / (double)Math.Max(1, GameClock.StepHz);
                labels.Add(GameText.Format("away.panel.choice", r.Serial, AwayReportService.ShortTitle(r), GameClock.FormatDayTime(end)));
            }
            AwayReportRecord shown = null;
            int at = SelectedSerial == 0 ? -1 : _pickSerials.IndexOf(SelectedSerial);
            if (at < 0)
            {
                // 默认：最新一份已结算的报告；没有时看进行中的那份。
                at = recent.Count > 0 ? (current != null ? 1 : 0) : (current != null ? 0 : -1);
            }
            if (at >= 0)
            {
                shown = AwayReportService.Find(state, _pickSerials[at]);
            }
            DropdownChoices.Apply(_pick, labels, GameText.Get("away.panel.none"));
            if (at >= 0 && at < _pick.choices.Count)
            {
                _pick.SetValueWithoutNotify(_pick.choices[at]);
            }
            _pick.SetEnabled(labels.Count > 1);
            ShownSerial = shown?.Serial ?? 0;

            bool none = shown == null;
            _empty.EnableInClassList("uk-hidden", !none);
            _empty.text = none ? GameText.Get("away.panel.none") : string.Empty;
            _summary.EnableInClassList("uk-hidden", none);
            _summary.text = none ? string.Empty : AwayReportView.Summary(state, shown);
            ShowingEmptyReport = !none && AwayReportView.Build(state, shown, _lines);
            if (none)
            {
                _lines.Clear();
            }
            RefreshRows();
        }

        private void RefreshRows()
        {
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < _lines.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                row.Q<Button>("ArRow").clicked += () => Click(index);
                _list.Add(row);
                _rows.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shownRow = i < _lines.Count;
                row.EnableInClassList("uk-hidden", !shownRow);
                if (!shownRow)
                {
                    continue;
                }
                AwayLine l = _lines[i];
                Button b = row.Q<Button>("ArRow");
                b.text = l.Text;
                b.EnableInClassList("ar-row-title", l.Cls == "ar-row-title");
                b.EnableInClassList("ar-row-dim", l.Cls == "ar-row-dim");
                b.EnableInClassList("ar-row-warn", l.Cls == "ar-row-warn");
                b.SetEnabled(l.IsEntry);
            }
            VisibleRowCount = _lines.Count;
        }

        private void OnPicked(string label)
        {
            int at = _pick.choices.IndexOf(label);
            if (at >= 0 && at < _pickSerials.Count)
            {
                SelectedSerial = _pickSerials[at];
                _messageText = string.Empty;
                _lastKey = null;
                Refresh();
            }
        }

        // ── 点击 ─────────────────────────────────────────────────────────────

        /// <summary>点第 <paramref name="index"/> 行（自检直接调；正式流程由行按钮触发）。返回是否执行了动作。</summary>
        public bool Click(int index)
        {
            AwayLine l = Line(index);
            if (l == null || !l.IsEntry)
            {
                return false;
            }
            LastClicked = l;
            string failure = null;
            bool located = false;
            if (l.HasPos && (l.Action == AwayLineAction.Locate || l.Action == AwayLineAction.Building || l.Action == AwayLineAction.Machine || l.Action == AwayLineAction.Rules))
            {
                located = TryLocate(l, out failure);
            }
            if (l.Action == AwayLineAction.Locate && !located)
            {
                Say(GameText.Format("away.panel.locate_failed", GameText.Get(failure ?? "ui.notify.no_location")));
                return false;
            }
            SetOpen(false);
            if (PauseMenuUIToolkit.IsOpen)
            {
                PauseMenuUIToolkit.Close();
            }
            switch (l.Action)
            {
                case AwayLineAction.StatsItem:
                    StatsPanelUIToolkit.OpenTab(StatsTab.Production, l.Arg);
                    break;
                case AwayLineAction.StatsTab:
                    StatsPanelUIToolkit.OpenTab(Enum.TryParse(l.Arg, out StatsTab tab) ? tab : StatsTab.Production);
                    break;
                case AwayLineAction.Building:
                    ProductionPanelUIToolkit.Open(l.Arg);
                    break;
                case AwayLineAction.Machine:
                    RosterPanelUIToolkit.OpenDetail(l.LogicId);
                    break;
                case AwayLineAction.Rules:
                    RulesPanelUIToolkit.Open();
                    break;
                case AwayLineAction.PowerPanel:
                    PowerPanelUIToolkit.Open();
                    break;
                case AwayLineAction.Roster:
                    RosterPanelUIToolkit.Open();
                    break;
                case AwayLineAction.Notifications:
                    NotificationHudUIToolkit.OpenCenter();
                    break;
                case AwayLineAction.RaidResult:
                    // FG6-DEF-08：突袭段的标题 / 损失行 → 突袭历史面板里这一份结算。
                    RaidResultPanelUIToolkit.OpenResult(int.TryParse(l.Arg, out int serial) ? serial : 0);
                    break;
            }
            return true;
        }

        /// <summary>镜头定位（与通知定位同一个入口）。</summary>
        public static bool TryLocate(AwayLine l, out string failureKey)
        {
            failureKey = "ui.notify.no_location";
            if (l == null || !l.HasPos)
            {
                return false;
            }
            if (NotificationCenter.LocateHandler == null)
            {
                failureKey = "ui.notify.no_camera";
                return false;
            }
            return NotificationCenter.LocateHandler(l.RegionId, l.Pos, out failureKey);
        }

        private void Say(string text)
        {
            _messageText = text ?? string.Empty;
            _lastKey = null;
            Refresh();
        }

        public static bool PickForTests(DropdownField field, int index) => RulesPanelUIToolkit.PickForTests(field, index);
    }
}
