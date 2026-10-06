using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG6-DEF-08（FG06 FGR-DEF-050 突袭结算；第 4 节“突袭历史：列出每次突袭的时间、阵营、结果，可以回看结算”；FGR-DEF-051 残骸去向）：突袭历史与结算面板。
    /// - 选突袭：有结算的（展开过攻城）与只有导演历史的（没到家园就撤 / 取消 / 合并）合在一起，进行中的排最前、其余最新在前。
    /// - 结算内容：过程时间线、伤害与击毁、反应贡献、损失（逐条可定位）、谁贡献最大（点击定位炮塔 / 打开名册里这台机器）、战利品、残骸处理状态。
    /// - “残骸去向”下拉框（选中即生效，红线 8）：先送解析台 / 先送回收站 / 留在仓库 / 不自动搬运——机器据此搬运残骸（工单写“突袭残骸”，FGR-BASE-020）。
    /// 入口：“突袭结算”通知（点开看最新一份）、防御总览“突袭历史”、暂停菜单“突袭历史”、离家报告突袭段的标题行。模态（Esc / 关闭 / 点遮罩关闭）。
    /// 行复用离家报告的行模板与样式（AwayReportRow / ar-*）；只在数据版本、选中、语言变化时重建（O(行数)），进行中的突袭另外每 raid.result.panel_refresh_seconds 真实秒重读。
    /// </summary>
    public sealed class RaidResultPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>离家报告 30078 之上、按键面板 30080 之下（从离家报告点开时盖在它上面；防御总览 30062 / 暂停菜单 30070 打开时也在上面）。</summary>
        public const int Order = 30079;

        public static RaidResultPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static string _pendingKey = string.Empty;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _root;
        private Label _title, _count, _pickLabel, _wreckLabel, _summary, _message, _empty, _footer;
        private Button _close;
        private DropdownField _pick;
        private DropdownField _wreck;
        private ScrollView _list;
        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<AwayLine> _lines = new List<AwayLine>();
        private readonly List<RaidHistoryEntry> _entries = new List<RaidHistoryEntry>();
        private int? _lastKey;
        private double _nextReread;
        private string _messageText = string.Empty;
        private bool _listening;

        protected override string UxmlLocation => "RaidResultPanel";
        protected override int SortingOrder => Order;

        // ── 当前视图（自检读写）──
        /// <summary>选中的一项（空 = 默认：进行中的或最新一项）。</summary>
        public string SelectedKey { get; private set; } = string.Empty;
        public string ShownKey { get; private set; } = string.Empty;
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public AwayLine Line(int i) => i >= 0 && i < VisibleRowCount ? _lines[i] : null;
        public string RowText(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Button>("ArRow").text : string.Empty;
        public string SummaryText => _summary?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string MessageText => _messageText;
        public DropdownField PickField => _pick;
        public DropdownField WreckField => _wreck;
        public int ChoiceCount => _entries.Count;
        public AwayLine LastClicked { get; private set; }

        private void Awake()
        {
            Instance = this;
            Listen();
        }

        private void Listen()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            // “突袭结算”通知（弹出条或通知中心的“打开”）：打开最新一份结算。
            NotificationCenter.RegisterOpenHandler("raid_result", OpenFromNotification);
        }

        protected override void OnDestroy()
        {
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

        public static void Open() => OpenKey(string.Empty);

        /// <summary>打开并选中某一份结算（0 = 默认）。</summary>
        public static void OpenResult(int serial) => OpenKey(serial > 0 ? "R" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);

        private static void OpenKey(string key)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingKey = key ?? string.Empty;
                return;
            }
            Instance.SelectedKey = key ?? string.Empty;
            Instance._messageText = string.Empty;
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
            Instance?.SetOpen(false);
        }

        private static bool OpenFromNotification(NotificationEntry entry)
        {
            RaidResultRecord latest = RaidResultService.LatestEnded(CampaignSession.Current);
            OpenResult(latest?.Serial ?? 0);
            return true;
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                SelectedKey = _pendingKey;
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
                Log.Error("[RaidResultPanelUIToolkit] 加载 AwayReportRow 失败，突袭历史的行不可用。");
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
            _root = root.Q<VisualElement>("RaidResultRoot");
            _title = root.Q<Label>("RaidResultTitle");
            _count = root.Q<Label>("RaidResultCount");
            _close = root.Q<Button>("RaidResultClose");
            _pickLabel = root.Q<Label>("RaidResultPickLabel");
            _pick = root.Q<DropdownField>("RaidResultPick");
            _wreckLabel = root.Q<Label>("RaidResultWreckLabel");
            _wreck = root.Q<DropdownField>("RaidResultWreck");
            _summary = root.Q<Label>("RaidResultSummary");
            _message = root.Q<Label>("RaidResultMessage");
            _empty = root.Q<Label>("RaidResultEmpty");
            _list = root.Q<ScrollView>("RaidResultList");
            _footer = root.Q<Label>("RaidResultFooter");
            _close.clicked += () => SetOpen(false);
            _pick.RegisterValueChangedCallback(evt => OnPicked(evt.newValue));
            _wreck.RegisterValueChangedCallback(evt => OnWreckPicked(evt.newValue));
            UiTooltip.Attach(_wreck, () => new TooltipContent
            {
                Title = GameText.Get("raid.result.panel.wreck_label"),
                Body = GameText.Get("raid.result.panel.wreck_tip"),
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
                GuidanceHooks.Raise(GuidanceHooks.RaidResultFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
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
            h.Add(RaidResultService.Revision);
            h.Add(RaidDirectorService.Revision);
            h.Add((int)GameText.Language);
            h.Add(SelectedKey);
            h.Add(_rowTemplate != null);
            h.Add(_messageText);
            h.Add(state);
            int key = h.ToHashCode();
            bool live = RaidResultService.PrimaryOpen(state) != null;
            if (key == _lastKey && (!live || now < _nextReread))
            {
                return;
            }
            _lastKey = key;
            _nextReread = now + RaidResultService.PanelRefreshSeconds;

            _title.text = GameText.Get("raid.result.panel.title");
            _close.text = GameText.Get("raid.result.panel.close");
            _pickLabel.text = GameText.Get("raid.result.panel.pick");
            _wreckLabel.text = GameText.Get("raid.result.panel.wreck_label");
            _footer.text = GameText.Get("raid.result.panel.footer");
            _message.text = _messageText;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));

            var routes = new List<string>(RaidResultService.RouteCount);
            for (int i = 0; i < RaidResultService.RouteCount; i++)
            {
                routes.Add(RaidResultService.RouteText(i));
            }
            DropdownChoices.Apply(_wreck, routes, string.Empty);
            int route = RaidResultService.StateOf(state)?.WreckRouting ?? RaidResultService.RouteBench;
            if (route >= 0 && route < _wreck.choices.Count)
            {
                _wreck.SetValueWithoutNotify(_wreck.choices[route]);
            }
            _wreck.SetEnabled(state != null);

            _entries.Clear();
            _entries.AddRange(RaidResultService.HistoryEntries(state));
            _count.text = GameText.Format("raid.result.panel.count", _entries.Count, Campaign.Defense.RaidCatalog.HistoryMax);
            var labels = new List<string>(_entries.Count);
            int at = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                labels.Add(RaidResultService.ChoiceText(_entries[i]));
                if (at < 0 && !string.IsNullOrEmpty(SelectedKey) && _entries[i].Key == SelectedKey)
                {
                    at = i;
                }
            }
            if (at < 0 && _entries.Count > 0)
            {
                at = 0;
            }
            DropdownChoices.Apply(_pick, labels, GameText.Get("raid.result.panel.none"));
            if (at >= 0 && at < _pick.choices.Count)
            {
                _pick.SetValueWithoutNotify(_pick.choices[at]);
            }
            _pick.SetEnabled(labels.Count > 1);

            bool none = at < 0;
            ShownKey = none ? string.Empty : _entries[at].Key;
            _empty.EnableInClassList("uk-hidden", !none);
            _empty.text = none ? GameText.Get("raid.result.panel.none") : string.Empty;
            _summary.EnableInClassList("uk-hidden", none);
            _summary.text = none ? string.Empty : RaidResultService.SummaryText(_entries[at]);
            if (none)
            {
                _lines.Clear();
            }
            else
            {
                RaidResultService.BuildDetail(state, _entries[at], _lines);
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
            if (at >= 0 && at < _entries.Count)
            {
                SelectedKey = _entries[at].Key;
                _messageText = string.Empty;
                _lastKey = null;
                Refresh();
            }
        }

        /// <summary>“残骸去向”选中即生效（红线 8）。</summary>
        private void OnWreckPicked(string label)
        {
            int mode = _wreck.choices.IndexOf(label);
            if (mode >= 0 && RaidResultService.SetWreckRouting(CampaignSession.Current, mode))
            {
                Say(GameText.Format("raid.result.panel.wreck_set", RaidResultService.RouteText(mode)));
            }
        }

        /// <summary>自检：按下标选“残骸去向”（走与玩家选择同一个回调）。</summary>
        public bool PickWreckForTests(int mode) => RulesPanelUIToolkit.PickForTests(_wreck, mode);

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
            if (l.HasPos)
            {
                located = AwayReportPanelUIToolkit.TryLocate(l, out failure);
            }
            if (l.Action == AwayLineAction.Locate && !located)
            {
                Say(GameText.Format("raid.result.panel.locate_failed", GameText.Get(failure ?? "ui.notify.no_location")));
                return false;
            }
            SetOpen(false);
            if (PauseMenuUIToolkit.IsOpen)
            {
                PauseMenuUIToolkit.Close();
            }
            if (DefenseOverviewPanelUIToolkit.IsOpen)
            {
                DefenseOverviewPanelUIToolkit.Close();
            }
            if (l.Action == AwayLineAction.Machine)
            {
                RosterPanelUIToolkit.OpenDetail(l.LogicId);
            }
            return true;
        }

        private void Say(string text)
        {
            _messageText = text ?? string.Empty;
            _lastKey = null;
            Refresh();
        }
    }
}
