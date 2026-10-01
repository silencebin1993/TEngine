using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG4-ECO-06（FG04 FGR-ECO-030 / 031；FG13 FGU-15“列表、编辑、冲突、日志”；卡片“规则的复制和常用预设”）：常驻规则面板。
    /// - 规则页：按类型 / 从常用预设新建；每行一条规则（R 编号与类型、启用状态（符号 + 文字，不只靠颜色）、优先级、条件、动作、最近触发与次数、
    ///   冲突警告（配置层面两条规则要同一座建筑 / 同一台机器，或这一刻真的让出了）与没能执行的原因）；行内 启用 / 停用、上移 / 下移、编辑、复制、删除（先确认，B04）。
    /// - 编辑区：按类型显示对应设置；下拉框选中即生效（UI Toolkit 红线 8），数值用 ±1 / ±10 按钮（不需要键盘输入）。
    /// - 日志页：最近 rules.log_max 条（新的在上），点一条镜头飞到对应的建筑 / 机器。
    /// 全部操作走 <see cref="StandingRuleService"/>（与自检同一入口）。入口：暂停菜单“常驻规则”、快捷键（默认 Alt+R）。
    /// 模态（Esc / 关闭 / 点遮罩关闭）；只在规则版本 / 语言 / 设置 / 选中项变化时重建（O(规则数 + 候选目标数)），另外每 0.5 真实秒刷新一次“最近触发 / 下次检查”。
    /// </summary>
    public sealed class RulesPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>固件库 30072 之上、物资 30074 与图鉴 30075 之下（“?”打开的图鉴盖在它上面）。</summary>
        public const int Order = 30073;

        public static RulesPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        /// <summary>真实时间来源（定时刷新；自检可注入）。</summary>
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Button _close;
        private Button _tabRules;
        private Button _tabLog;
        private Label _newLabel;
        private DropdownField _newKind;
        private DropdownField _newPreset;
        private Label _message;
        private VisualElement _pageRules;
        private VisualElement _pageLog;
        private Label _empty;
        private ScrollView _list;
        private ScrollView _editor;
        private Label _editTitle;
        private Label _editDesc;
        private Label _editInfo;
        private VisualElement _rowItem, _rowFactory, _rowRecipe, _rowThreshold, _rowBatch, _rowTargets, _rowMachines, _rowPoint, _rowBoost;
        private Label _itemLabel, _factoryLabel, _recipeLabel, _thresholdLabel, _batchLabel, _targetsLabel, _machinesLabel, _pointLabel;
        private DropdownField _item, _factory, _recipe, _targetAdd, _targetRemove, _machineAdd, _machineRemove, _point;
        private Label _thValue, _batchValue, _targetsValue, _machinesValue;
        private Button _thMinus10, _thMinus1, _thPlus1, _thPlus10, _batchMinus, _batchPlus, _boost;
        private Label _logCount;
        private Label _logEmpty;
        private ScrollView _logList;
        private Label _footer;

        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<int> _rowSerials = new List<int>();
        private readonly List<Label> _logLabels = new List<Label>();

        private readonly List<string> _kindIds = new List<string>();
        private readonly List<string> _presetIds = new List<string>();
        private readonly List<string> _itemIds = new List<string>();
        private readonly List<string> _factoryIds = new List<string>();
        private readonly List<string> _recipeIds = new List<string>();
        private readonly List<string> _targetAddIds = new List<string>();
        private readonly List<string> _targetRemoveIds = new List<string>();
        private readonly List<int> _machineAddIds = new List<int>();
        private readonly List<int> _machineRemoveIds = new List<int>();
        private readonly List<string> _pointIds = new List<string>();

        private string _lastKey;
        private double _nextReread;
        private string _messageText = string.Empty;
        private bool _messageError;

        protected override string UxmlLocation => "RulesPanel";
        protected override int SortingOrder => Order;

        /// <summary>编辑区当前的规则（0 = 没有选中）。</summary>
        public int SelectedSerial { get; private set; }

        /// <summary>当前是日志页。</summary>
        public bool ShowingLog { get; private set; }

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public int RowSerial(int i) => i >= 0 && i < VisibleRowCount ? _rowSerials[i] : 0;
        public string RowText(int i, string name) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Label>(name)?.text ?? string.Empty : string.Empty;
        public bool RowWarnVisible(int i) => i >= 0 && i < VisibleRowCount && !_rows[i].Q<Label>("RrWarn").ClassListContains("uk-hidden");
        public Button RowButton(int i, string name) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Button>(name) : null;
        public string MessageText => _messageText;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string EditTitleText => _editTitle?.text ?? string.Empty;
        public string EditInfoText => _editInfo?.text ?? string.Empty;
        public bool EditorRowVisible(string name) => _root?.Q<VisualElement>(name) is VisualElement v && !v.ClassListContains("uk-hidden");
        public DropdownField NewKindField => _newKind;
        public DropdownField NewPresetField => _newPreset;
        public DropdownField ItemField => _item;
        public DropdownField FactoryField => _factory;
        public DropdownField RecipeField => _recipe;
        public DropdownField TargetAddField => _targetAdd;
        public DropdownField TargetRemoveField => _targetRemove;
        public DropdownField MachineAddField => _machineAdd;
        public DropdownField MachineRemoveField => _machineRemove;
        public DropdownField PointField => _point;
        public Button ThresholdPlus1 => _thPlus1;
        public Button ThresholdMinus10 => _thMinus10;
        public Button BoostButton => _boost;
        public Button TabLogButton => _tabLog;
        public Button TabRulesButton => _tabRules;
        public Button CloseButton => _close;
        public string ThresholdText => _thValue?.text ?? string.Empty;
        public string TargetsText => _targetsValue?.text ?? string.Empty;
        public string MachinesText => _machinesValue?.text ?? string.Empty;
        public int LogLabelCount => _logLabels.Count;
        public string LogLabelText(int i) => i >= 0 && i < _logLabels.Count ? _logLabels[i].text : string.Empty;
        public string LogCountText => _logCount?.text ?? string.Empty;
        public string LogEmptyText => _logEmpty != null && !_logEmpty.ClassListContains("uk-hidden") ? _logEmpty.text : string.Empty;
        public bool PendingDeleteConfirm { get; private set; }
        public Vector2 LastJumpPosition { get; private set; }

        private void Awake()
        {
            Instance = this;
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

        public static void Open()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingOpen = false;
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

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
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
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("RulesRow");
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
                Log.Error("[RulesPanelUIToolkit] 加载 RulesRow 失败，规则行不可用。");
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
            _root = root.Q<VisualElement>("RulesPanelRoot");
            _title = root.Q<Label>("RulesPanelTitle");
            _count = root.Q<Label>("RulesPanelCount");
            _close = root.Q<Button>("RulesPanelClose");
            _tabRules = root.Q<Button>("RulesTabRules");
            _tabLog = root.Q<Button>("RulesTabLog");
            _newLabel = root.Q<Label>("RulesNewLabel");
            _newKind = root.Q<DropdownField>("RulesNewKind");
            _newPreset = root.Q<DropdownField>("RulesNewPreset");
            _message = root.Q<Label>("RulesMessage");
            _pageRules = root.Q<VisualElement>("RulesPageRules");
            _pageLog = root.Q<VisualElement>("RulesPageLog");
            _empty = root.Q<Label>("RulesEmpty");
            _list = root.Q<ScrollView>("RulesList");
            _editor = root.Q<ScrollView>("RulesEditor");
            _editTitle = root.Q<Label>("RulesEditTitle");
            _editDesc = root.Q<Label>("RulesEditDesc");
            _editInfo = root.Q<Label>("RulesEditInfo");
            _rowItem = root.Q<VisualElement>("RulesRowItem");
            _rowFactory = root.Q<VisualElement>("RulesRowFactory");
            _rowRecipe = root.Q<VisualElement>("RulesRowRecipe");
            _rowThreshold = root.Q<VisualElement>("RulesRowThreshold");
            _rowBatch = root.Q<VisualElement>("RulesRowBatch");
            _rowTargets = root.Q<VisualElement>("RulesRowTargets");
            _rowMachines = root.Q<VisualElement>("RulesRowMachines");
            _rowPoint = root.Q<VisualElement>("RulesRowPoint");
            _rowBoost = root.Q<VisualElement>("RulesRowBoost");
            _itemLabel = root.Q<Label>("RulesItemLabel");
            _factoryLabel = root.Q<Label>("RulesFactoryLabel");
            _recipeLabel = root.Q<Label>("RulesRecipeLabel");
            _thresholdLabel = root.Q<Label>("RulesThresholdLabel");
            _batchLabel = root.Q<Label>("RulesBatchLabel");
            _targetsLabel = root.Q<Label>("RulesTargetsLabel");
            _machinesLabel = root.Q<Label>("RulesMachinesLabel");
            _pointLabel = root.Q<Label>("RulesPointLabel");
            _item = root.Q<DropdownField>("RulesItem");
            _factory = root.Q<DropdownField>("RulesFactory");
            _recipe = root.Q<DropdownField>("RulesRecipe");
            _targetAdd = root.Q<DropdownField>("RulesTargetAdd");
            _targetRemove = root.Q<DropdownField>("RulesTargetRemove");
            _machineAdd = root.Q<DropdownField>("RulesMachineAdd");
            _machineRemove = root.Q<DropdownField>("RulesMachineRemove");
            _point = root.Q<DropdownField>("RulesPoint");
            _thValue = root.Q<Label>("RulesThValue");
            _batchValue = root.Q<Label>("RulesBatchValue");
            _targetsValue = root.Q<Label>("RulesTargetsValue");
            _machinesValue = root.Q<Label>("RulesMachinesValue");
            _thMinus10 = root.Q<Button>("RulesThMinus10");
            _thMinus1 = root.Q<Button>("RulesThMinus1");
            _thPlus1 = root.Q<Button>("RulesThPlus1");
            _thPlus10 = root.Q<Button>("RulesThPlus10");
            _batchMinus = root.Q<Button>("RulesBatchMinus");
            _batchPlus = root.Q<Button>("RulesBatchPlus");
            _boost = root.Q<Button>("RulesBoost");
            _logCount = root.Q<Label>("RulesLogCount");
            _logEmpty = root.Q<Label>("RulesLogEmpty");
            _logList = root.Q<ScrollView>("RulesLogList");
            _footer = root.Q<Label>("RulesPanelFooter");

            _close.clicked += () => SetOpen(false);
            _tabRules.clicked += () => ShowLog(false);
            _tabLog.clicked += () => ShowLog(true);
            _thMinus10.clicked += () => StepThreshold(-10);
            _thMinus1.clicked += () => StepThreshold(-1);
            _thPlus1.clicked += () => StepThreshold(1);
            _thPlus10.clicked += () => StepThreshold(10);
            _batchMinus.clicked += () => StepBatch(-1);
            _batchPlus.clicked += () => StepBatch(1);
            _boost.clicked += ToggleBoost;
            _newKind.RegisterValueChangedCallback(evt => OnPick(_newKind, _kindIds, evt.newValue, id => CreateKind(id)));
            _newPreset.RegisterValueChangedCallback(evt => OnPick(_newPreset, _presetIds, evt.newValue, id => CreatePreset(id)));
            _item.RegisterValueChangedCallback(evt => OnPick(_item, _itemIds, evt.newValue, id => Do(StandingRuleService.TrySetItem(Session, SelectedSerial, id, out string m), m)));
            _factory.RegisterValueChangedCallback(evt => OnPick(_factory, _factoryIds, evt.newValue, id => Do(StandingRuleService.TrySetFactory(Session, SelectedSerial, id, out string m), m)));
            _recipe.RegisterValueChangedCallback(evt => OnPick(_recipe, _recipeIds, evt.newValue, id => Do(StandingRuleService.TrySetRecipe(Session, SelectedSerial, id, out string m), m)));
            _targetAdd.RegisterValueChangedCallback(evt => OnPick(_targetAdd, _targetAddIds, evt.newValue, AddTarget));
            _targetRemove.RegisterValueChangedCallback(evt => OnPick(_targetRemove, _targetRemoveIds, evt.newValue, RemoveTarget));
            _machineAdd.RegisterValueChangedCallback(evt => OnPickInt(_machineAdd, _machineAddIds, evt.newValue, AddMachine));
            _machineRemove.RegisterValueChangedCallback(evt => OnPickInt(_machineRemove, _machineRemoveIds, evt.newValue, RemoveMachine));
            _point.RegisterValueChangedCallback(evt => OnPick(_point, _pointIds, evt.newValue, id => Do(StandingRuleService.TrySetPoint(Session, SelectedSerial, id, out string m), m)));
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _rows.Clear();
            _rowSerials.Clear();
            _logLabels.Clear();
            _lastKey = null;
        }

        private static CampaignState Session => CampaignSession.Current;

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
                GuidanceHooks.Raise(GuidanceHooks.RulesPanelFirstOpen);
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
                PendingDeleteConfirm = false;
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

        public void ShowLog(bool log)
        {
            ShowingLog = log;
            _lastKey = null;
            Refresh();
        }

        /// <summary>选中一条规则在编辑区修改（0 = 取消选中）。</summary>
        public void Select(int serial)
        {
            SelectedSerial = serial;
            _lastKey = null;
            Refresh();
        }

        // ── 刷新 ─────────────────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = Session;
            double now = Clock();
            string key = string.Concat(StandingRuleService.Revision.ToString(CultureInfo.InvariantCulture), "|", ((int)GameText.Language).ToString(CultureInfo.InvariantCulture), "|",
                GameSettings.Revision.ToString(CultureInfo.InvariantCulture), "|", SelectedSerial.ToString(CultureInfo.InvariantCulture), "|", ShowingLog ? "L" : "R", "|",
                _rowTemplate != null ? "1" : "0", "|", _messageText, "|", BuildingOps.Revision.ToString(CultureInfo.InvariantCulture), "|", (state?.BuildingRecords?.Length ?? 0).ToString(CultureInfo.InvariantCulture), "|",
                MachineRegistry.RosterRevision.ToString(CultureInfo.InvariantCulture), "|", state != null ? state.GetHashCode().ToString(CultureInfo.InvariantCulture) : "0");
            if (key == _lastKey && now < _nextReread)
            {
                return;
            }
            _lastKey = key;
            _nextReread = now + 0.5;

            List<StandingRuleRecord> rules = StandingRuleService.Ordered(state);
            if (SelectedSerial != 0 && StandingRuleService.Find(state, SelectedSerial) == null)
            {
                SelectedSerial = 0;
            }
            int enabled = 0;
            foreach (StandingRuleRecord r in rules)
            {
                if (r.Enabled)
                {
                    enabled++;
                }
            }
            _title.text = GameText.Get("rules.panel.title");
            _close.text = GameText.Get("rules.panel.close");
            _count.text = GameText.Format("rules.panel.count", rules.Count, enabled, StandingRuleService.MaxRules);
            _tabRules.text = GameText.Get("rules.panel.tab_rules");
            _tabLog.text = GameText.Get("rules.panel.tab_log");
            _tabRules.EnableInClassList("rp-tab-on", !ShowingLog);
            _tabLog.EnableInClassList("rp-tab-on", ShowingLog);
            _newLabel.text = GameText.Get("rules.panel.new_label");
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("rules.panel.footer"));
            _message.text = _messageText;
            _message.EnableInClassList("rp-message-error", _messageError);
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            _pageRules.EnableInClassList("uk-hidden", ShowingLog);
            _pageLog.EnableInClassList("uk-hidden", !ShowingLog);
            FillNewMenus();
            if (ShowingLog)
            {
                RefreshLog(state);
                return;
            }
            RefreshRows(state, rules);
            RefreshEditor(state);
        }

        private void FillNewMenus()
        {
            var kinds = new List<string> { GameText.Get("rules.panel.new_kind") };
            _kindIds.Clear();
            _kindIds.Add(null);
            foreach (string k in StandingRuleService.AllKinds)
            {
                _kindIds.Add(k);
                kinds.Add(StandingRuleService.KindName(k));
            }
            DropdownChoices.Apply(_newKind, kinds, kinds[0]);
            _newKind.SetValueWithoutNotify(_newKind.choices[0]);
            var presets = new List<string> { GameText.Get("rules.panel.new_preset") };
            _presetIds.Clear();
            _presetIds.Add(null);
            foreach (RulePreset p in StandingRuleService.Presets)
            {
                _presetIds.Add(p.Id);
                presets.Add(GameText.Format("rules.preset_option", StandingRuleService.KindName(p.Kind), GameText.Get(p.NameKey)));
            }
            DropdownChoices.Apply(_newPreset, presets, presets[0]);
            _newPreset.SetValueWithoutNotify(_newPreset.choices[0]);
        }

        private void RefreshRows(CampaignState state, List<StandingRuleRecord> rules)
        {
            bool empty = rules.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("rules.panel.empty") : string.Empty;
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < rules.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                row.Q<Button>("RrToggle").clicked += () => ToggleRow(index);
                row.Q<Button>("RrUp").clicked += () => Move(index, -1);
                row.Q<Button>("RrDown").clicked += () => Move(index, 1);
                row.Q<Button>("RrEdit").clicked += () => Select(RowSerial(index));
                row.Q<Button>("RrCopy").clicked += () => Copy(index);
                row.Q<Button>("RrDelete").clicked += () => AskDelete(index);
                _list.Add(row);
                _rows.Add(row);
                _rowSerials.Add(0);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shown = i < rules.Count;
                row.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    _rowSerials[i] = 0;
                    continue;
                }
                StandingRuleRecord r = rules[i];
                _rowSerials[i] = r.Serial;
                VisualElement box = row.Q<VisualElement>("RrRow");
                box.EnableInClassList("rp-row-selected", r.Serial == SelectedSerial);
                box.EnableInClassList("rp-row-off", !r.Enabled);
                row.Q<Label>("RrId").text = StandingRuleService.LabelWithKind(r);
                Label st = row.Q<Label>("RrState");
                st.text = GameText.Get(!r.Enabled ? "rules.row.state_off" : r.Active ? "rules.row.state_active" : "rules.row.state_on");
                st.EnableInClassList("rp-row-state-off", !r.Enabled);
                st.EnableInClassList("rp-row-state-active", r.Enabled && r.Active);
                row.Q<Label>("RrPriority").text = GameText.Format("rules.row.priority", r.Priority);
                row.Q<Label>("RrWhen").text = GameText.Format("rules.row.when", StandingRuleService.ConditionText(state, r));
                row.Q<Label>("RrThen").text = GameText.Format("rules.row.then", StandingRuleService.ActionText(state, r));
                row.Q<Label>("RrLast").text = r.LastFiredTick >= 0
                    ? GameText.Format("rules.row.last", GameClock.FormatDayTime(r.LastFiredTick / (double)Math.Max(1, GameClock.StepHz)), r.FireCount)
                    : GameText.Get("rules.row.never");
                string warn = Warning(state, r);
                Label w = row.Q<Label>("RrWarn");
                w.text = warn ?? string.Empty;
                w.EnableInClassList("uk-hidden", warn == null);
                row.Q<Button>("RrToggle").text = GameText.Get(r.Enabled ? "rules.row.disable" : "rules.row.enable");
                row.Q<Button>("RrUp").text = GameText.Get("rules.row.up");
                row.Q<Button>("RrDown").text = GameText.Get("rules.row.down");
                row.Q<Button>("RrEdit").text = GameText.Get("rules.row.edit");
                row.Q<Button>("RrCopy").text = GameText.Get("rules.row.copy");
                row.Q<Button>("RrDelete").text = GameText.Get("rules.row.delete");
                row.Q<Button>("RrUp").SetEnabled(i > 0);
                row.Q<Button>("RrDown").SetEnabled(i < rules.Count - 1);
            }
            VisibleRowCount = rules.Count;
        }

        /// <summary>行上的警告：这一刻真的让出了 > 配置层面的冲突 > 没能执行的原因 > 玩家改动过它控制的东西。</summary>
        private static string Warning(CampaignState state, StandingRuleRecord r)
        {
            string conflict = StandingRuleService.ConflictText(state, r) ?? StandingRuleService.StaticConflictText(state, r);
            if (conflict != null)
            {
                return conflict;
            }
            string issue = r.Enabled ? StandingRuleService.IssueText(r) : StandingRuleService.ConfigIssue(state, r, out _, out _);
            if (issue != null)
            {
                return GameText.Format("rules.row.issue", issue);
            }
            foreach (RuleHoldRecord h in StandingRuleService.Holds(state))
            {
                if (h.Rule == r.Serial && h.Overridden)
                {
                    return GameText.Format("rules.row.override", StandingRuleService.ResolveArg(state, h.EntityId.StartsWith("o:", StringComparison.Ordinal) ? string.Empty : "@" + h.EntityId));
                }
            }
            return null;
        }

        private void RefreshEditor(CampaignState state)
        {
            StandingRuleRecord r = StandingRuleService.Find(state, SelectedSerial);
            bool has = r != null;
            foreach (VisualElement v in new[] { _rowItem, _rowFactory, _rowRecipe, _rowThreshold, _rowBatch, _rowTargets, _rowMachines, _rowPoint, _rowBoost })
            {
                v.EnableInClassList("uk-hidden", true);
            }
            if (!has)
            {
                _editTitle.text = GameText.Get("rules.edit.none_selected");
                _editDesc.text = GameText.Get("rules.panel.select_hint");
                _editInfo.text = string.Empty;
                return;
            }
            RuleKind k = StandingRuleService.KindRow(r.Kind);
            _editTitle.text = GameText.Format("rules.edit.title", StandingRuleService.LabelWithKind(r));
            _editDesc.text = k != null ? GameText.Get(k.DescKey) : string.Empty;
            string kind = r.Kind;
            bool stock = kind == StandingRuleService.KindStock;
            bool supply = kind == StandingRuleService.KindSupply;
            if (stock || supply)
            {
                Show(_rowItem);
                _itemLabel.text = GameText.Get("rules.edit.item");
                FillItems(r, stock);
            }
            if (stock)
            {
                Show(_rowFactory);
                Show(_rowRecipe);
                _factoryLabel.text = GameText.Get("rules.edit.factory");
                _recipeLabel.text = GameText.Get("rules.edit.recipe");
                FillFactories(state, r);
                FillRecipes(state, r);
            }
            if (k != null && k.ThresholdKey != "none")
            {
                Show(_rowThreshold);
                _thresholdLabel.text = GameText.Get("rules.edit.threshold");
                _thValue.text = GameText.Format(k.ThresholdKey, r.Threshold);
                _thMinus10.text = "-10";
                _thMinus1.text = "-1";
                _thPlus1.text = "+1";
                _thPlus10.text = "+10";
            }
            if (supply)
            {
                Show(_rowBatch);
                _batchLabel.text = GameText.Get("rules.edit.batch");
                _batchValue.text = GameText.Format("rules.edit.batch_value", r.Batch);
                _batchMinus.text = "-1";
                _batchPlus.text = "+1";
            }
            if (supply || kind == StandingRuleService.KindWar || kind == StandingRuleService.KindSilent || kind == StandingRuleService.KindRebuild)
            {
                Show(_rowTargets);
                _targetsLabel.text = GameText.Get(kind == StandingRuleService.KindWar ? "rules.edit.targets_pause"
                    : kind == StandingRuleService.KindSilent ? "rules.edit.targets_storage"
                    : kind == StandingRuleService.KindRebuild ? "rules.edit.targets_types" : "rules.edit.targets");
                FillTargets(state, r);
            }
            if (kind == StandingRuleService.KindSilent || kind == StandingRuleService.KindRepair)
            {
                Show(_rowMachines);
                _machinesLabel.text = GameText.Get(kind == StandingRuleService.KindRepair ? "rules.edit.machines_scope" : "rules.edit.machines");
                FillMachines(r);
            }
            if (kind == StandingRuleService.KindSilent || kind == StandingRuleService.KindUnload)
            {
                Show(_rowPoint);
                _pointLabel.text = GameText.Get(kind == StandingRuleService.KindSilent ? "rules.edit.point" : "rules.edit.destination");
                FillPoints(state, r);
            }
            if (kind == StandingRuleService.KindWar)
            {
                Show(_rowBoost);
                _boost.text = GameText.Get(r.BoostRepair ? "rules.edit.boost_on" : "rules.edit.boost_off");
            }
            _editInfo.text = InfoLine(state, r);
        }

        private static void Show(VisualElement v) => v.EnableInClassList("uk-hidden", false);

        private static string InfoLine(CampaignState state, StandingRuleRecord r)
        {
            string check = GameText.Format("rules.panel.check_info", Mathf.RoundToInt((float)StandingRuleService.CheckSeconds),
                GameClock.FormatDayTime((StandingRuleService.Domain(state)?.NextCheckTick ?? 0) / (double)Math.Max(1, GameClock.StepHz)));
            switch (r.Kind)
            {
                case StandingRuleService.KindSilent:
                    return (StandingRuleService.TrySilentWindow(state, out long start, out _)
                        ? GameText.Format("rules.edit.silent_next", GameClock.FormatDayTime(start / (double)Math.Max(1, GameClock.StepHz)))
                        : GameText.Get("rules.edit.silent_unknown")) + "\n" + check;
                case StandingRuleService.KindWar:
                    return GameText.Get(StandingRuleService.RaidActive(state) ? "rules.cond.war_plan" : "rules.edit.raid_none") + "\n" + check;
                default:
                    return check;
            }
        }

        // ── 下拉框候选 ─────────────────────────────────────────────────────────────

        private void FillItems(StandingRuleRecord r, bool produced)
        {
            var labels = new List<string>();
            _itemIds.Clear();
            foreach (ItemDef item in ItemCatalog.Items)
            {
                if (!HomeInventory.IsStorable(item) || item.Form == ItemForm.Fluid)
                {
                    continue;
                }
                if (produced ? ItemCatalog.ProducedBy(item.Id).Count == 0 : ItemCatalog.ConsumedBy(item.Id).Count == 0)
                {
                    continue;
                }
                _itemIds.Add(item.Id);
                labels.Add(item.Name);
            }
            ApplyChoices(_item, labels, _itemIds, r.ItemId, GameText.Get("rules.none"));
        }

        private void FillFactories(CampaignState state, StandingRuleRecord r)
        {
            var labels = new List<string>();
            _factoryIds.Clear();
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b) || !ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def)
                    || def.Mode != ProducerMode.Recipe || def.FixedRecipe != null)
                {
                    continue;
                }
                bool makes = string.IsNullOrEmpty(r.ItemId);
                foreach (RecipeDef rd in def.Recipes)
                {
                    foreach (RecipeLine l in rd.Lines)
                    {
                        makes |= l.Role == RecipeRole.Out && l.Item.Id == r.ItemId;
                    }
                }
                if (!makes)
                {
                    continue;
                }
                _factoryIds.Add(b.BuildingId);
                labels.Add(StandingRuleService.BuildingLabel(state, b.BuildingId));
            }
            ApplyChoices(_factory, labels, _factoryIds, r.Targets.Length > 0 ? r.Targets[0] : null, GameText.Get("rules.edit.no_choice"));
        }

        private void FillRecipes(CampaignState state, StandingRuleRecord r)
        {
            var labels = new List<string>();
            _recipeIds.Clear();
            BuildingRecord b = r.Targets.Length > 0 ? HomeGridService.FindBuilding(state, r.Targets[0]) : null;
            if (b != null && ProducerCatalog.TryGet(b.BuildingTypeId, out ProducerDef def))
            {
                foreach (RecipeDef rd in def.Recipes)
                {
                    _recipeIds.Add(rd.Id);
                    labels.Add(rd.Name);
                }
            }
            ApplyChoices(_recipe, labels, _recipeIds, r.RecipeId, GameText.Get("rules.edit.no_choice"));
        }

        private void FillTargets(CampaignState state, StandingRuleRecord r)
        {
            var current = new List<string>();
            _targetRemoveIds.Clear();
            _targetRemoveIds.Add(null);
            var removeLabels = new List<string> { GameText.Get("rules.edit.remove") };
            bool types = r.Kind == StandingRuleService.KindRebuild;
            foreach (string t in r.Targets)
            {
                string name = types ? HomeGridService.DisplayName(t) : StandingRuleService.BuildingLabel(state, t);
                current.Add(name);
                _targetRemoveIds.Add(t);
                removeLabels.Add(name);
            }
            _targetsValue.text = current.Count > 0 ? string.Join(StandingRuleService.ListSeparator, current)
                : GameText.Get(types ? "rules.all_buildings" : "rules.none");
            var addLabels = new List<string> { GameText.Get("rules.edit.add") };
            _targetAddIds.Clear();
            _targetAddIds.Add(null);
            if (types)
            {
                var seen = new HashSet<string>();
                foreach (BuildingService s in ConfigSystem.Instance.Tables.TbBuildingService.DataList)
                {
                    if (s.TypeId == HomeValleyLayout.BuildingTypeCore || Array.IndexOf(r.Targets, s.TypeId) >= 0 || !seen.Add(s.TypeId))
                    {
                        continue;
                    }
                    _targetAddIds.Add(s.TypeId);
                    addLabels.Add(HomeGridService.DisplayName(s.TypeId));
                }
            }
            else
            {
                foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
                {
                    if (b == null || HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b) || Array.IndexOf(r.Targets, b.BuildingId) >= 0
                        || !Candidate(r, b))
                    {
                        continue;
                    }
                    _targetAddIds.Add(b.BuildingId);
                    addLabels.Add(StandingRuleService.BuildingLabel(state, b.BuildingId));
                }
            }
            DropdownChoices.Apply(_targetAdd, addLabels, addLabels[0]);
            _targetAdd.SetValueWithoutNotify(_targetAdd.choices[0]);
            DropdownChoices.Apply(_targetRemove, removeLabels, removeLabels[0]);
            _targetRemove.SetValueWithoutNotify(_targetRemove.choices[0]);
            _targetRemove.SetEnabled(removeLabels.Count > 1);
        }

        private static bool Candidate(StandingRuleRecord r, BuildingRecord b)
        {
            switch (r.Kind)
            {
                case StandingRuleService.KindSupply:
                    return ProducerCatalog.IsProducer(b.BuildingTypeId);
                case StandingRuleService.KindWar:
                    return b.BuildingTypeId != HomeValleyLayout.BuildingTypeCore && BuildingOps.CanDisableType(b.BuildingTypeId) && ProducerCatalog.IsProducer(b.BuildingTypeId);
                case StandingRuleService.KindSilent:
                    return HomeValleyPowerGrid.IsStorageType(b.BuildingTypeId);
                default:
                    return false;
            }
        }

        private void FillMachines(StandingRuleRecord r)
        {
            var current = new List<string>();
            var removeLabels = new List<string> { GameText.Get("rules.edit.remove") };
            _machineRemoveIds.Clear();
            _machineRemoveIds.Add(0);
            foreach (int id in r.Machines)
            {
                string name = StandingRuleService.MachineLabel(id);
                current.Add(name);
                _machineRemoveIds.Add(id);
                removeLabels.Add(name);
            }
            _machinesValue.text = current.Count > 0 ? string.Join(StandingRuleService.ListSeparator, current)
                : GameText.Get(r.Kind == StandingRuleService.KindRepair ? "rules.all_machines" : "rules.none");
            var addLabels = new List<string> { GameText.Get("rules.edit.add") };
            _machineAddIds.Clear();
            _machineAddIds.Add(0);
            var ids = new List<int>();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && Array.IndexOf(r.Machines, m.LogicId) < 0)
                {
                    ids.Add(m.LogicId);
                }
            }
            ids.Sort();
            foreach (int id in ids)
            {
                _machineAddIds.Add(id);
                addLabels.Add(StandingRuleService.MachineLabel(id));
            }
            DropdownChoices.Apply(_machineAdd, addLabels, addLabels[0]);
            _machineAdd.SetValueWithoutNotify(_machineAdd.choices[0]);
            DropdownChoices.Apply(_machineRemove, removeLabels, removeLabels[0]);
            _machineRemove.SetValueWithoutNotify(_machineRemove.choices[0]);
            _machineRemove.SetEnabled(removeLabels.Count > 1);
        }

        private void FillPoints(CampaignState state, StandingRuleRecord r)
        {
            bool unload = r.Kind == StandingRuleService.KindUnload;
            var labels = new List<string>();
            _pointIds.Clear();
            _pointIds.Add(string.Empty);
            labels.Add(unload ? GameText.Get("rules.shared_storage") : HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeCore));
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                bool storage = b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore;
                if (unload ? !storage : b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    continue;
                }
                _pointIds.Add(b.BuildingId);
                labels.Add(StandingRuleService.BuildingLabel(state, b.BuildingId));
            }
            ApplyChoices(_point, labels, _pointIds, r.PointId ?? string.Empty, labels[0]);
        }

        /// <summary>写入选项并选中当前值（不触发回调）；当前值不在候选里时显示“（未选）”。</summary>
        private static void ApplyChoices(DropdownField field, List<string> labels, List<string> ids, string current, string emptyText)
        {
            DropdownChoices.Apply(field, labels, emptyText);
            int at = current != null ? ids.IndexOf(current) : -1;
            if (at >= 0 && at < field.choices.Count)
            {
                field.SetValueWithoutNotify(field.choices[at]);
            }
            else if (field.choices.Count > 0)
            {
                // 当前值不在候选里（还没选 / 目标没了）：加一个占位项显示“（未选）”，选别的才生效。
                var list = new List<string>(field.choices);
                list.Insert(0, GameText.Get("rules.none"));
                ids.Insert(0, null);
                DropdownChoices.Apply(field, list, emptyText);
                field.SetValueWithoutNotify(field.choices[0]);
            }
        }

        // ── 操作 ─────────────────────────────────────────────────────────────────

        private static void OnPick(DropdownField field, List<string> ids, string label, Action<string> act)
        {
            int at = field.choices.IndexOf(label);
            if (at < 0 || at >= ids.Count || ids[at] == null)
            {
                return;
            }
            act(ids[at]);
        }

        private static void OnPickInt(DropdownField field, List<int> ids, string label, Action<int> act)
        {
            int at = field.choices.IndexOf(label);
            if (at <= 0 || at >= ids.Count)
            {
                return;
            }
            act(ids[at]);
        }

        /// <summary>自检：按选项文字选下拉框（与玩家点选同一回调）。</summary>
        public static bool PickForTests(DropdownField field, int index)
        {
            if (field == null || index < 0 || index >= field.choices.Count)
            {
                return false;
            }
            field.value = field.choices[index];
            return true;
        }

        private void Say(bool ok, string message)
        {
            _messageText = message ?? string.Empty;
            _messageError = !ok;
            _lastKey = null;
            Refresh();
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, _messageText);
        }

        private void Do(bool ok, string message) => Say(ok, message);

        public bool CreateKind(string kind)
        {
            bool ok = StandingRuleService.TryCreate(Session, kind, out StandingRuleRecord r, out string message);
            if (ok)
            {
                SelectedSerial = r.Serial;
            }
            Say(ok, message);
            return ok;
        }

        public bool CreatePreset(string presetId)
        {
            bool ok = StandingRuleService.TryCreateFromPreset(Session, presetId, out StandingRuleRecord r, out string message);
            if (ok)
            {
                SelectedSerial = r.Serial;
            }
            Say(ok, message);
            return ok;
        }

        public void ToggleRow(int index)
        {
            StandingRuleRecord r = StandingRuleService.Find(Session, RowSerial(index));
            if (r == null)
            {
                return;
            }
            bool ok = StandingRuleService.TrySetEnabled(Session, r.Serial, !r.Enabled, out string message);
            Say(ok, message);
        }

        public void Move(int index, int delta)
        {
            bool ok = StandingRuleService.TryMove(Session, RowSerial(index), delta, out string message);
            Say(ok, message);
        }

        public void Copy(int index)
        {
            bool ok = StandingRuleService.TryCopy(Session, RowSerial(index), out StandingRuleRecord copy, out string message);
            if (ok)
            {
                SelectedSerial = copy.Serial;
            }
            Say(ok, message);
        }

        /// <summary>删除：不可逆，先确认（B04）。</summary>
        public void AskDelete(int index)
        {
            StandingRuleRecord r = StandingRuleService.Find(Session, RowSerial(index));
            if (r == null)
            {
                return;
            }
            int serial = r.Serial;
            string label = StandingRuleService.LabelWithKind(r);
            var req = new ConfirmRequest
            {
                Title = GameText.Format("rules.delete.title", label),
                Irreversible = true,
                ConfirmText = GameText.Get("rules.row.delete"),
                CancelText = GameText.Get("rules.delete.cancel"),
                OnConfirm = () =>
                {
                    PendingDeleteConfirm = false;
                    bool ok = StandingRuleService.TryDelete(Session, serial, out string message);
                    Say(ok, message);
                },
                OnCancel = () => PendingDeleteConfirm = false,
            };
            req.Consequences.Add(GameText.Get("rules.delete.line"));
            UiConfirmDialog.Show(req);
            PendingDeleteConfirm = true;
        }

        public void StepThreshold(int delta)
        {
            StandingRuleRecord r = StandingRuleService.Find(Session, SelectedSerial);
            RuleKind k = r != null ? StandingRuleService.KindRow(r.Kind) : null;
            if (r == null || k == null)
            {
                return;
            }
            int v = Mathf.Clamp(r.Threshold + delta, k.ThresholdMin, k.ThresholdMax);
            bool ok = StandingRuleService.TrySetThreshold(Session, r.Serial, v, out string message);
            Say(ok, message);
        }

        public void StepBatch(int delta)
        {
            StandingRuleRecord r = StandingRuleService.Find(Session, SelectedSerial);
            if (r == null)
            {
                return;
            }
            int v = Mathf.Clamp(r.Batch + delta, 1, StandingRuleService.SupplyBatchMax);
            bool ok = StandingRuleService.TrySetBatch(Session, r.Serial, v, out string message);
            Say(ok, message);
        }

        public void ToggleBoost()
        {
            StandingRuleRecord r = StandingRuleService.Find(Session, SelectedSerial);
            if (r == null)
            {
                return;
            }
            bool ok = StandingRuleService.TrySetBoost(Session, r.Serial, !r.BoostRepair, out string message);
            Say(ok, message);
        }

        private void AddTarget(string id) => Do(StandingRuleService.TryAddTarget(Session, SelectedSerial, id, out string m), m);
        private void RemoveTarget(string id) => Do(StandingRuleService.TryRemoveTarget(Session, SelectedSerial, id, out string m), m);
        private void AddMachine(int id) => Do(StandingRuleService.TryAddMachine(Session, SelectedSerial, id, out string m), m);
        private void RemoveMachine(int id) => Do(StandingRuleService.TryRemoveMachine(Session, SelectedSerial, id, out string m), m);

        // ── 日志页 ───────────────────────────────────────────────────────────────

        private void RefreshLog(CampaignState state)
        {
            IReadOnlyList<RuleLogRecord> log = StandingRuleService.LogEntries(state);
            _logCount.text = GameText.Format("rules.panel.log_count", log.Count, StandingRuleService.LogMax);
            bool empty = log.Count == 0;
            _logEmpty.EnableInClassList("uk-hidden", !empty);
            _logEmpty.text = empty ? GameText.Get("rules.panel.log_empty") : string.Empty;
            while (_logLabels.Count < log.Count)
            {
                var label = new Label();
                label.AddToClassList("rp-log-entry");
                int index = _logLabels.Count;
                label.RegisterCallback<ClickEvent>(_ => JumpToLog(index));
                _logList.Add(label);
                _logLabels.Add(label);
            }
            for (int i = 0; i < _logLabels.Count; i++)
            {
                bool shown = i < log.Count;
                _logLabels[i].EnableInClassList("uk-hidden", !shown);
                if (shown)
                {
                    // 新的在上。
                    _logLabels[i].text = StandingRuleService.LogText(state, log[log.Count - 1 - i]);
                }
            }
        }

        /// <summary>日志第 <paramref name="index"/> 条（新的在上）：镜头飞到它的建筑 / 机器（实体现在的位置；没了用记下时的位置）。</summary>
        public bool JumpToLog(int index)
        {
            CampaignState state = Session;
            IReadOnlyList<RuleLogRecord> log = StandingRuleService.LogEntries(state);
            if (index < 0 || index >= log.Count)
            {
                return false;
            }
            RuleLogRecord e = log[log.Count - 1 - index];
            Vector3? now = StandingRuleService.PositionOf(state, e.EntityId);
            Vector2 at;
            if (now.HasValue)
            {
                at = new Vector2(now.Value.x, now.Value.z);
            }
            else if (e.HasPos)
            {
                at = new Vector2(e.X, e.Y);
            }
            else
            {
                return false;
            }
            LastJumpPosition = at;
            Campaign.WorldSim.WorldView.FlyTo(HomeValleyLayout.RegionId, at);
            return true;
        }
    }
}
