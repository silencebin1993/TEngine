using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
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
    /// FG4-ECO-07（FG04 FGR-ECO-040 / 041；FG13 FGU-16 机器名册“排序、筛选、批量改岗位”、FGU-17 机器详情“经历、装配、改名、接入记录”）：机器名册面板。
    /// - 列表页：排序（编号 / 名字 / 岗位 / 底盘 / 状态 / 伤势 / 经历，升降序）、筛选（岗位、底盘、状态）；每行勾选、名字、底盘、状态、安全模式标记（DEBT-FG1SIG04-04）、
    ///   岗位下拉框（选中即生效，红线 8）、伤势与经历、详情。勾选几台后在“批量改为”里选岗位一次改完。远征中的机器岗位下拉框禁用并写明原因（UI 拦截点；逻辑校验在 <see cref="MachineRoster.TrySetRole"/>）。
    /// - 详情页：改名（文本框 + 改名 / 恢复默认名，空 / 过长 / 非法字符拒绝并说明）、岗位、驻防点、镜头飞过去，以及装配、经历、接入次数与时长、击杀、受伤记录。
    /// 全部操作走 <see cref="MachineRoster"/> / <see cref="MachineNaming"/>（与自检同一入口）。入口：暂停菜单“机器名册”、顶栏劳动力、快捷键（默认 N）。任何地点都能打开（不必回家园）。
    /// 模态（Esc / 关闭 / 点遮罩关闭）；只在名册 / 岗位 / 名字版本、语言、筛选或选中项变化时重建（O(机器数 log 机器数)），另外每 roster.labor_refresh_seconds 真实秒刷新一次状态。
    /// </summary>
    public sealed class RosterPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>暂停菜单 30070 之上、固件库 30072 之下（“?”打开的图鉴盖在它上面）。</summary>
        public const int Order = 30071;

        public static RosterPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private VisualElement _root;
        private Label _title, _count, _footer, _message, _empty;
        private Button _close, _sortDir, _selectAll, _selectNone;
        private Label _sortLabel, _roleLabel, _chassisLabel, _statusLabel, _batchLabel;
        private DropdownField _sort, _filterRole, _filterChassis, _filterStatus, _batchRole;
        private VisualElement _pageList, _pageDetail;
        private ScrollView _list;
        private Button _detailBack, _detailJump, _rename, _resetName;
        private Label _detailTitle, _detailMessage, _nameLabel, _detailRoleLabel, _detailRoleNote, _detailPointLabel, _detailInfo;
        private TextField _nameField;
        private DropdownField _detailRole, _detailPoint;
        private VisualElement _detailPointRow;

        private VisualTreeAsset _rowTemplate;
        private bool _rowTemplateLoading;
        private readonly List<TemplateContainer> _rows = new List<TemplateContainer>();
        private readonly List<int> _rowIds = new List<int>();
        private readonly List<List<MachineRole>> _rowRoleChoices = new List<List<MachineRole>>();
        private readonly HashSet<int> _selected = new HashSet<int>();

        private readonly List<MachineRole?> _roleFilterIds = new List<MachineRole?>();
        private readonly List<string> _chassisIds = new List<string>();
        private readonly List<MachineRole> _batchIds = new List<MachineRole>();
        private readonly List<MachineRole> _detailRoleIds = new List<MachineRole>();
        private readonly List<string> _pointIds = new List<string>();

        private int? _lastKey;
        private double _nextReread;
        private string _messageText = string.Empty;
        private bool _messageError;
        private string _detailMessageText = string.Empty;
        private bool _detailMessageError;
        private int _lastDetailId;

        protected override string UxmlLocation => "RosterPanel";
        protected override int SortingOrder => Order;

        // ── 当前视图状态（自检读写）──
        public MachineRoster.SortKey Sort { get; private set; } = MachineRoster.SortKey.Number;
        public bool Descending { get; private set; }
        public MachineRole? RoleFilter { get; private set; }
        public string ChassisFilter { get; private set; }
        public MachineRoster.StatusFilter StatusFilterValue { get; private set; } = MachineRoster.StatusFilter.Active;
        public int DetailLogicId { get; private set; }

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public bool ShowingDetail => _pageDetail != null && !_pageDetail.ClassListContains("uk-hidden");
        // FG6-DEF-01：炮塔分类（自检读点）。
        public bool ShowingTurrets => _pageTurrets != null && !_pageTurrets.ClassListContains("uk-hidden");
        public Button TurretsButton => _turretsBtn;
        public int TurretRowCount => _turretRows.Count;
        public string TurretRowText(int i) => i >= 0 && i < _turretRows.Count ? _turretRows[i].text : string.Empty;
        public Button TurretRowButton(int i) => i >= 0 && i < _turretRows.Count ? _turretRows[i] : null;
        public string TurretNoteText => _turretNote?.text ?? string.Empty;
        public string TurretEmptyText => _turretEmpty != null && !_turretEmpty.ClassListContains("uk-hidden") ? _turretEmpty.text : string.Empty;
        private VisualElement _pageTurrets;
        private Button _turretsBtn, _turretsBack;
        private Label _turretCount, _turretNote, _turretEmpty;
        private ScrollView _turretList;
        private readonly List<Button> _turretRows = new List<Button>();
        private readonly List<string> _turretRowIds = new List<string>();
        public bool TurretMode { get; private set; }
        public int VisibleRowCount { get; private set; }
        public int RowLogicId(int i) => i >= 0 && i < VisibleRowCount ? _rowIds[i] : 0;
        public string RowText(int i, string name) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Label>(name)?.text ?? string.Empty : string.Empty;
        public bool RowSafeVisible(int i) => i >= 0 && i < VisibleRowCount && !_rows[i].Q<Label>("RoSafe").ClassListContains("uk-hidden");
        public DropdownField RowRoleField(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<DropdownField>("RoRole") : null;
        public Toggle RowSelect(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Toggle>("RoSelect") : null;
        public Button RowDetailButton(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Q<Button>("RoDetail") : null;
        public int RowIndexOf(int logicId)
        {
            int at = _rowIds.IndexOf(logicId);
            return at >= 0 && at < VisibleRowCount ? at : -1;
        }
        public string MessageText => _messageText;
        public string DetailMessageText => _detailMessageText;
        public string CountText => _count?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string DetailTitleText => _detailTitle?.text ?? string.Empty;
        public string DetailInfoText => _detailInfo?.text ?? string.Empty;
        public string DetailRoleNoteText => _detailRoleNote?.text ?? string.Empty;
        public bool DetailRoleEnabled => _detailRole != null && _detailRole.enabledSelf;
        public bool DetailPointVisible => _detailPointRow != null && !_detailPointRow.ClassListContains("uk-hidden");
        public DropdownField SortField => _sort;
        public DropdownField RoleFilterField => _filterRole;
        public DropdownField ChassisFilterField => _filterChassis;
        public DropdownField StatusFilterField => _filterStatus;
        public DropdownField BatchRoleField => _batchRole;
        public DropdownField DetailRoleField => _detailRole;
        public DropdownField DetailPointField => _detailPoint;
        public TextField NameField => _nameField;
        public Button RenameButton => _rename;
        public Button ResetNameButton => _resetName;
        public Button SortDirButton => _sortDir;
        public Button SelectAllButton => _selectAll;
        public Button SelectNoneButton => _selectNone;
        /// <summary>FG5-RND-06（FGU-40）：“纪念墙…”——关掉名册、打开陈列馆面板的纪念墙页（不需要先建陈列馆）。</summary>
        public Button MemorialButton => _memorial;
        private Button _memorial;
        public Button DetailBackButton => _detailBack;
        public Button DetailJumpButton => _detailJump;
        public Button CloseButton => _close;
        public int SelectedCount => _selected.Count;
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

        /// <summary>打开并直接进入某台机器的详情页（命令栏 / 接入 HUD 等入口可调用）。</summary>
        public static void OpenDetail(int logicId)
        {
            Open();
            Instance?.ShowDetail(logicId);
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
            VisualTreeAsset asset = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("RosterRow");
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
                Log.Error("[RosterPanelUIToolkit] 加载 RosterRow 失败，名册行不可用。");
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
            _root = root.Q<VisualElement>("RosterPanelRoot");
            _title = root.Q<Label>("RosterPanelTitle");
            _count = root.Q<Label>("RosterPanelCount");
            _close = root.Q<Button>("RosterPanelClose");
            _footer = root.Q<Label>("RosterPanelFooter");
            _pageList = root.Q<VisualElement>("RosterPageList");
            _pageDetail = root.Q<VisualElement>("RosterPageDetail");
            _sortLabel = root.Q<Label>("RosterSortLabel");
            _sort = root.Q<DropdownField>("RosterSort");
            _sortDir = root.Q<Button>("RosterSortDir");
            _roleLabel = root.Q<Label>("RosterRoleLabel");
            _filterRole = root.Q<DropdownField>("RosterFilterRole");
            _chassisLabel = root.Q<Label>("RosterChassisLabel");
            _filterChassis = root.Q<DropdownField>("RosterFilterChassis");
            _statusLabel = root.Q<Label>("RosterStatusLabel");
            _filterStatus = root.Q<DropdownField>("RosterFilterStatus");
            _selectAll = root.Q<Button>("RosterSelectAll");
            _selectNone = root.Q<Button>("RosterSelectNone");
            _memorial = root.Q<Button>("RosterMemorial");
            _batchLabel = root.Q<Label>("RosterBatchLabel");
            _batchRole = root.Q<DropdownField>("RosterBatchRole");
            _message = root.Q<Label>("RosterMessage");
            _empty = root.Q<Label>("RosterEmpty");
            _list = root.Q<ScrollView>("RosterList");
            _detailBack = root.Q<Button>("RosterDetailBack");
            _detailTitle = root.Q<Label>("RosterDetailTitle");
            _detailJump = root.Q<Button>("RosterDetailJump");
            _detailMessage = root.Q<Label>("RosterDetailMessage");
            _nameLabel = root.Q<Label>("RosterNameLabel");
            _nameField = root.Q<TextField>("RosterNameField");
            _rename = root.Q<Button>("RosterRename");
            _resetName = root.Q<Button>("RosterResetName");
            _detailRoleLabel = root.Q<Label>("RosterDetailRoleLabel");
            _detailRole = root.Q<DropdownField>("RosterDetailRole");
            _detailRoleNote = root.Q<Label>("RosterDetailRoleNote");
            _detailPointRow = root.Q<VisualElement>("RosterDetailPointRow");
            _detailPointLabel = root.Q<Label>("RosterDetailPointLabel");
            _detailPoint = root.Q<DropdownField>("RosterDetailPoint");
            _detailInfo = root.Q<Label>("RosterDetailInfo");

            _close.clicked += () => SetOpen(false);
            _sortDir.clicked += ToggleSortDirection;
            _selectAll.clicked += SelectAllShown;
            _selectNone.clicked += SelectNone;
            _memorial.clicked += OpenMemorial;
            _pageTurrets = root.Q<VisualElement>("RosterPageTurrets");
            _turretsBtn = root.Q<Button>("RosterTurrets");
            _turretsBack = root.Q<Button>("RosterTurretsBack");
            _turretCount = root.Q<Label>("RosterTurretCount");
            _turretNote = root.Q<Label>("RosterTurretNote");
            _turretEmpty = root.Q<Label>("RosterTurretEmpty");
            _turretList = root.Q<ScrollView>("RosterTurretList");
            if (_turretsBtn != null)
            {
                _turretsBtn.clicked += () => ShowTurrets(true);
            }
            if (_turretsBack != null)
            {
                _turretsBack.clicked += () => ShowTurrets(false);
            }
            _detailBack.clicked += () => ShowDetail(0);
            _detailJump.clicked += () => JumpTo(DetailLogicId);
            _rename.clicked += () => Rename(_nameField.value);
            _resetName.clicked += ResetName;
            _sort.RegisterValueChangedCallback(evt => OnSortPicked(evt.newValue));
            _filterRole.RegisterValueChangedCallback(evt => OnRoleFilterPicked(evt.newValue));
            _filterChassis.RegisterValueChangedCallback(evt => OnChassisFilterPicked(evt.newValue));
            _filterStatus.RegisterValueChangedCallback(evt => OnStatusFilterPicked(evt.newValue));
            _batchRole.RegisterValueChangedCallback(evt => OnBatchPicked(evt.newValue));
            _detailRole.RegisterValueChangedCallback(evt => OnDetailRolePicked(evt.newValue));
            _detailPoint.RegisterValueChangedCallback(evt => OnPointPicked(evt.newValue));
            _nameField.maxLength = MachineNaming.MaxLength + 8; // 多留几个字：超长时给出“最多 N 个字”的说明，而不是静默截断。
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _rows.Clear();
            _rowIds.Clear();
            _rowRoleChoices.Clear();
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
                GuidanceHooks.Raise(GuidanceHooks.RosterPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _messageText = string.Empty;
                _detailMessageText = string.Empty;
                _lastKey = null;
                Refresh();
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
                UiTooltip.Hide();
                UiTextFocusProbe.BlurFocusedText();
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

        // ── 刷新 ─────────────────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = Session;
            double now = Clock();
            // 审查修复（P2）：变化键改为整数哈希（打开时每帧调用，不再每帧拼长字符串）；碰撞最多晚一次重建，另有每 roster.labor_refresh_seconds 秒的定时重读兜底。
            var h = new HashCode();
            h.Add(MachineRegistry.RosterRevision);
            h.Add(MachineRoster.Revision);
            h.Add(MachineNaming.Revision);
            h.Add((int)GameText.Language);
            h.Add(GameSettings.Revision);
            h.Add((int)Sort);
            h.Add(Descending);
            h.Add(RoleFilter.HasValue ? (int)RoleFilter.Value : -1);
            h.Add(ChassisFilter);
            h.Add((int)StatusFilterValue);
            h.Add(DetailLogicId);
            h.Add(_selected.Count);
            h.Add(_rowTemplate != null);
            h.Add(_messageText);
            h.Add(_detailMessageText);
            h.Add(state);
            h.Add(TurretMode);
            h.Add(Campaign.Defense.TurretService.Revision);
            int key = h.ToHashCode();
            if (key == _lastKey && now < _nextReread)
            {
                return;
            }
            _lastKey = key;
            double every = Math.Max(0.1, Campaign.Grid.GridContent.TryGetTuning("roster.labor_refresh_seconds", out float v) ? v : 1.0);
            _nextReread = now + every;

            _title.text = GameText.Get("roster.panel.title");
            _close.text = GameText.Get("roster.panel.close");
            _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("roster.panel.footer"));
            RefreshCount(state);
            bool detail = DetailLogicId != 0 && MachineRegistry.TryGetRecord(DetailLogicId, out _);
            if (!detail)
            {
                DetailLogicId = 0;
            }
            // FG6-DEF-01：炮塔分类页（固定底盘的机器）。
            bool turrets = !detail && TurretMode && _pageTurrets != null;
            _pageTurrets?.EnableInClassList("uk-hidden", !turrets);
            if (turrets)
            {
                _pageList.EnableInClassList("uk-hidden", true);
                _pageDetail.EnableInClassList("uk-hidden", true);
                RefreshTurrets(state);
                return;
            }
            _pageList.EnableInClassList("uk-hidden", detail);
            _pageDetail.EnableInClassList("uk-hidden", !detail);
            if (detail)
            {
                RefreshDetail(state);
                return;
            }
            RefreshToolbar();
            _message.text = _messageText;
            _message.EnableInClassList("ro-message-error", _messageError);
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_messageText));
            RefreshRows(state);
        }

        /// <summary>FG6-DEF-01（FGR-DEF-001“出现在机器名册的‘炮塔’分类里，不能参加远征”）：切到 / 离开炮塔分类（与按钮同一路径，自检也调）。</summary>
        public void ShowTurrets(bool on)
        {
            TurretMode = on;
            DetailLogicId = 0;
            _lastKey = null;
            Refresh();
        }

        /// <summary>炮塔分类：每座炮塔一行（名字 · 状态 · 目标模式 · 击毁），点一行打开那座炮塔的面板。行数随炮塔数（ScrollView；按钮按数量增减，不重建整页）。</summary>
        private void RefreshTurrets(CampaignState state)
        {
            _turretsBack.text = GameText.Get("roster.detail.back");
            IReadOnlyList<TurretRecord> all = Campaign.Defense.TurretService.All(state);
            _turretCount.text = GameText.Format("roster.turret.count", all.Count);
            _turretNote.text = GameText.Get("roster.turret.note");
            _turretEmpty.text = GameText.Get("roster.turret.empty");
            _turretEmpty.EnableInClassList("uk-hidden", all.Count > 0);
            _turretRowIds.Clear();
            foreach (TurretRecord r in all)
            {
                if (r != null)
                {
                    _turretRowIds.Add(r.BuildingId);
                }
            }
            while (_turretRows.Count < _turretRowIds.Count)
            {
                int index = _turretRows.Count;
                var b = new Button();
                b.AddToClassList("mw-btn");
                b.AddToClassList("ro-turret-row");
                b.clicked += () => OpenTurretRow(index);
                _turretList.Add(b);
                _turretRows.Add(b);
            }
            while (_turretRows.Count > _turretRowIds.Count)
            {
                Button last = _turretRows[_turretRows.Count - 1];
                last.RemoveFromHierarchy();
                _turretRows.RemoveAt(_turretRows.Count - 1);
            }
            for (int i = 0; i < _turretRowIds.Count; i++)
            {
                if (Campaign.Defense.TurretService.TryGetReadout(state, _turretRowIds[i], out Campaign.Defense.TurretReadout ro))
                {
                    _turretRows[i].text = GameText.Format("roster.turret.row", ro.Name, GameText.Get(Campaign.Economy.BuildingOps.UiStatusNameKey(ro.Status.Kind)),
                        Campaign.Defense.TurretCatalog.ModeName(ro.TargetMode), ro.Kills);
                }
            }
        }

        /// <summary>点炮塔分类的第 <paramref name="index"/> 行：打开那座炮塔的面板（名册关上）。</summary>
        public void OpenTurretRow(int index)
        {
            if (index < 0 || index >= _turretRowIds.Count)
            {
                return;
            }
            string id = _turretRowIds[index];
            SetOpen(false);
            TurretPanelUIToolkit.Open(id);
        }

        private void RefreshCount(CampaignState state)
        {
            int total = 0, alive = 0, away = 0, dead = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null)
                {
                    continue;
                }
                total++;
                if (!m.IsAlive)
                {
                    dead++;
                }
                else
                {
                    alive++;
                    if (MachineRoster.IsAway(m))
                    {
                        away++;
                    }
                }
            }
            _count.text = GameText.Format("roster.panel.count", total, VisibleRowCount, alive, away, dead);
        }

        private void RefreshToolbar()
        {
            _sortLabel.text = GameText.Get("roster.panel.sort");
            _roleLabel.text = GameText.Get("roster.panel.filter_role");
            _chassisLabel.text = GameText.Get("roster.panel.filter_chassis");
            _statusLabel.text = GameText.Get("roster.panel.filter_status");
            _sortDir.text = GameText.Get(Descending ? "roster.panel.desc" : "roster.panel.asc");
            _selectAll.text = GameText.Get("roster.panel.select_all");
            _selectNone.text = GameText.Get("roster.panel.select_none");
            _memorial.text = GameText.Get("blackbox.panel.memorial_open");
            if (_turretsBtn != null)
            {
                _turretsBtn.text = GameText.Format("roster.tab.turrets_count", Campaign.Defense.TurretService.All(Session).Count);
            }
            _batchLabel.text = GameText.Format("roster.panel.batch_label", _selected.Count);

            var sorts = new List<string>();
            foreach (MachineRoster.SortKey k in MachineRoster.AllSortKeys)
            {
                sorts.Add(MachineRoster.SortKeyName(k));
            }
            Choose(_sort, sorts, Array.IndexOf(MachineRoster.AllSortKeys, Sort));

            var roles = new List<string> { GameText.Get("roster.panel.all") };
            _roleFilterIds.Clear();
            _roleFilterIds.Add(null);
            foreach (MachineRole r in MachineRoster.AllRoles)
            {
                _roleFilterIds.Add(r);
                roles.Add(MachineRoster.RoleName(r));
            }
            Choose(_filterRole, roles, _roleFilterIds.IndexOf(RoleFilter));

            var chassis = new List<string> { GameText.Get("roster.panel.all") };
            _chassisIds.Clear();
            _chassisIds.Add(null);
            foreach (string c in MachineRoster.ChassisIds())
            {
                _chassisIds.Add(c);
                chassis.Add(MachineRoster.ChassisLabel(c));
            }
            Choose(_filterChassis, chassis, Math.Max(0, _chassisIds.IndexOf(ChassisFilter)));

            var statuses = new List<string>();
            foreach (MachineRoster.StatusFilter f in MachineRoster.AllStatusFilters)
            {
                statuses.Add(MachineRoster.StatusFilterName(f));
            }
            Choose(_filterStatus, statuses, Array.IndexOf(MachineRoster.AllStatusFilters, StatusFilterValue));

            var batch = new List<string> { GameText.Get("roster.panel.batch_pick") };
            _batchIds.Clear();
            foreach (MachineRole r in MachineRoster.AllRoles)
            {
                if (MachineRoster.IsSettable(r))
                {
                    _batchIds.Add(r);
                    batch.Add(MachineRoster.RoleName(r));
                }
            }
            Choose(_batchRole, batch, 0);
            _batchRole.SetEnabled(_selected.Count > 0);
        }

        /// <summary>写入选项并选中第 <paramref name="index"/> 项（不触发回调）。</summary>
        private static void Choose(DropdownField field, List<string> labels, int index)
        {
            DropdownChoices.Apply(field, labels, labels.Count > 0 ? labels[0] : string.Empty);
            if (index >= 0 && index < field.choices.Count)
            {
                field.SetValueWithoutNotify(field.choices[index]);
            }
        }

        private void RefreshRows(CampaignState state)
        {
            List<MachineRecord> list = MachineRoster.Query(state, RoleFilter, ChassisFilter, StatusFilterValue, Sort, Descending);
            _selected.RemoveWhere(id => !MachineRegistry.TryGetRecord(id, out MachineRecord r) || r == null || !r.IsAlive);
            bool empty = list.Count == 0;
            _empty.EnableInClassList("uk-hidden", !empty);
            _empty.text = empty ? GameText.Get("roster.panel.empty") : string.Empty;
            if (_rowTemplate == null)
            {
                VisibleRowCount = 0;
                return;
            }
            while (_rows.Count < list.Count)
            {
                TemplateContainer row = _rowTemplate.CloneTree();
                int index = _rows.Count;
                row.Q<Toggle>("RoSelect").RegisterValueChangedCallback(evt => OnRowSelected(index, evt.newValue));
                row.Q<DropdownField>("RoRole").RegisterValueChangedCallback(evt => OnRowRolePicked(index, evt.newValue));
                row.Q<Button>("RoDetail").clicked += () => ShowDetail(RowLogicId(index));
                _list.Add(row);
                _rows.Add(row);
                _rowIds.Add(0);
                _rowRoleChoices.Add(new List<MachineRole>());
            }
            string sep = GameText.Get("rules.sep_dot");
            for (int i = 0; i < _rows.Count; i++)
            {
                TemplateContainer row = _rows[i];
                bool shown = i < list.Count;
                row.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    _rowIds[i] = 0;
                    continue;
                }
                MachineRecord m = list[i];
                _rowIds[i] = m.LogicId;
                MachineRoster.Status st = MachineRoster.StatusOf(state, m);
                MachineRole effective = MachineRoster.EffectiveRole(state, m);
                VisualElement box = row.Q<VisualElement>("RoRow");
                box.EnableInClassList("ro-row-dead", !m.IsAlive);
                box.EnableInClassList("ro-row-away", st == MachineRoster.Status.Away);
                Toggle select = row.Q<Toggle>("RoSelect");
                select.SetEnabled(m.IsAlive);
                select.SetValueWithoutNotify(_selected.Contains(m.LogicId));
                row.Q<Label>("RoName").text = MachineNaming.Long(m);
                row.Q<Label>("RoChassis").text = MachineRoster.ChassisLabel(m.ChassisId);
                row.Q<Label>("RoStatus").text = MachineRoster.StatusName(st);
                bool safe = m.IsAlive && SignalLinkService.IsInSafeMode(state, m.LogicId);
                Label safeLabel = row.Q<Label>("RoSafe");
                safeLabel.text = safe ? GameText.Get("roster.row.safe_mode") : string.Empty;
                safeLabel.tooltip = safe ? SignalLinkService.SafeModeTooltip(state, m.LogicId) : string.Empty;
                safeLabel.EnableInClassList("uk-hidden", !safe);
                string where = MachineRoster.IsHome(m) ? GameText.Get("roster.place.home") : GameText.Get("roster.place.away");
                row.Q<Label>("RoInfo").text = GameText.Format("roster.row.injury", MachineRoster.InjuryPercent(m)) + sep
                    + GameText.Format("roster.row.exp", m.ExperienceFlags?.Length ?? 0, m.JobsCompleted, m.KillCount) + sep
                    + GameText.Format("roster.row.where", where)
                    + (st == MachineRoster.Status.Away ? sep + GameText.Get("roster.row.role_locked") : string.Empty);
                FillRowRole(row.Q<DropdownField>("RoRole"), _rowRoleChoices[i], m, effective);
                Button detail = row.Q<Button>("RoDetail");
                detail.text = GameText.Get("roster.row.detail");
            }
            VisibleRowCount = list.Count;
            _count.text = GameText.Format("roster.panel.count", MachineRegistry.RecordCount, VisibleRowCount,
                CountWhere(m => m.IsAlive), CountWhere(MachineRoster.IsAway), CountWhere(m => !m.IsAlive));
        }

        private static int CountWhere(Func<MachineRecord, bool> pred)
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && pred(m))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>岗位下拉框：可设定的六种；当前岗位是“远征中”时只列它并禁用（UI 拦截点：远征中的机器不能改岗位）；阵亡禁用。</summary>
        private static void FillRowRole(DropdownField field, List<MachineRole> ids, MachineRecord m, MachineRole effective)
        {
            ids.Clear();
            var labels = new List<string>();
            bool locked = !m.IsAlive || effective == MachineRole.OnExpedition;
            if (locked)
            {
                ids.Add(effective);
                labels.Add(m.IsAlive ? MachineRoster.RoleName(effective) : GameText.Get("roster.status.dead"));
            }
            else
            {
                foreach (MachineRole r in MachineRoster.AllRoles)
                {
                    if (MachineRoster.IsSettable(r))
                    {
                        ids.Add(r);
                        labels.Add(MachineRoster.RoleName(r));
                    }
                }
            }
            DropdownChoices.Apply(field, labels, labels.Count > 0 ? labels[0] : string.Empty);
            int at = ids.IndexOf(effective);
            if (at >= 0 && at < field.choices.Count)
            {
                field.SetValueWithoutNotify(field.choices[at]);
            }
            field.SetEnabled(!locked);
            field.tooltip = !m.IsAlive ? string.Empty : effective == MachineRole.OnExpedition
                ? GameText.Format("roster.err.on_expedition", MachineNaming.Short(m))
                : MachineRoster.RoleDesc(effective);
        }

        private void RefreshDetail(CampaignState state)
        {
            MachineRegistry.TryGetRecord(DetailLogicId, out MachineRecord m);
            _detailBack.text = GameText.Get("roster.detail.back");
            _detailJump.text = GameText.Get("roster.detail.jump");
            _detailJump.SetEnabled(m.IsAlive);
            _detailTitle.text = GameText.Format("roster.detail.title", MachineNaming.Long(m));
            _detailMessage.text = _detailMessageText;
            _detailMessage.EnableInClassList("ro-message-error", _detailMessageError);
            _detailMessage.EnableInClassList("uk-hidden", string.IsNullOrEmpty(_detailMessageText));
            _nameLabel.text = GameText.Get("roster.detail.name_label");
            _rename.text = GameText.Get("roster.detail.rename");
            _resetName.text = GameText.Get("roster.detail.reset_name");
            _resetName.SetEnabled(MachineNaming.HasCustomName(m));
            if (_lastDetailId != m.LogicId)
            {
                _lastDetailId = m.LogicId;
                _nameField.SetValueWithoutNotify(m.CustomName ?? string.Empty);
            }
            _detailRoleLabel.text = GameText.Get("roster.detail.role_label");
            MachineRole effective = MachineRoster.EffectiveRole(state, m);
            FillRowRole(_detailRole, _detailRoleIds, m, effective);
            string note = !m.IsAlive ? string.Empty
                : effective == MachineRole.OnExpedition ? GameText.Format("roster.err.on_expedition", MachineNaming.Short(m))
                : MachineRoster.GarrisonSuspended(state, m) ? GameText.Get("roster.garrison.suspended")
                : MachineRoster.RoleDesc(effective);
            _detailRoleNote.text = note;
            bool garrison = m.IsAlive && m.Role == MachineRole.Garrison && MachineRoster.IsHome(m);
            _detailPointRow.EnableInClassList("uk-hidden", !garrison);
            if (garrison)
            {
                _detailPointLabel.text = GameText.Get("roster.detail.point_label");
                FillPoints(state, m);
            }
            _detailInfo.text = DetailText(state, m);
        }

        private void FillPoints(CampaignState state, MachineRecord m)
        {
            var labels = new List<string> { Campaign.Grid.HomeGridService.DisplayName(HomeValleyLayout.BuildingTypeCore) };
            _pointIds.Clear();
            _pointIds.Add(string.Empty);
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore
                    || Campaign.Grid.HomeGridService.IsRelocationGhost(b) || HomeValleyController.IsPlannedGhost(b))
                {
                    continue;
                }
                _pointIds.Add(b.BuildingId);
                labels.Add(StandingRuleService.BuildingLabel(state, b.BuildingId));
            }
            DropdownChoices.Apply(_detailPoint, labels, labels[0]);
            int at = _pointIds.IndexOf(m.RolePointId ?? string.Empty);
            _detailPoint.SetValueWithoutNotify(_detailPoint.choices[at >= 0 ? at : 0]);
        }

        /// <summary>详情正文（FGU-17：装配、经历、接入记录、击杀、受伤记录；FGR-ECO-041）。</summary>
        public static string DetailText(CampaignState state, MachineRecord m)
        {
            string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);
            string blueprint = string.IsNullOrEmpty(m.BlueprintId) ? GameText.Get("roster.detail.loadout_none")
                : (Campaign.Blueprint.BlueprintEditorService.Find(state, m.BlueprintId)?.DisplayName ?? m.BlueprintId);
            string port = GameText.Get(UplinkHudModel.HasUplinkPort(state, m.LogicId) ? "machine.detail.port_yes" : "machine.detail.port_no");
            MachineRoster.Status st = MachineRoster.StatusOf(state, m);
            string where = MachineRoster.IsHome(m) ? GameText.Get("roster.place.home") : GameText.Get("roster.place.away");
            string injuries = m.InjuryFlags != null && m.InjuryFlags.Length > 0 ? MachineInjury.DescribeAll(m.InjuryFlags) : GameText.Get("machine.detail.injury_none");
            var lines = new List<string>
            {
                GameText.Format("roster.detail.line_id", m.DisplayNumber, MachineRoster.ChassisLabel(m.ChassisId), blueprint, m.BlueprintVersion, port),
                GameText.Format("roster.detail.line_state", MachineRoster.StatusName(st), where, MachineRoster.RoleName(MachineRoster.EffectiveRole(state, m))),
                GameText.Format("roster.detail.line_vitals", F0(m.Health), F0(m.MaxHealth), MachineRoster.InjuryPercent(m), F0(m.Battery)),
                GameText.Format("roster.detail.line_order", MachineRoster.CurrentWorkText(state, m)),
                GameText.Format("machine.detail.line_morph", UplinkHudModel.MorphText(state, m.LogicId)),
                GameText.Format("roster.detail.line_exp", MachineExperienceFlags.Join(m.ExperienceFlags)),
                GameText.Format("roster.detail.line_uplink", MachineSignalExperience.Describe(state, m)),
                GameText.Format("roster.detail.line_stats", m.JobsCompleted, m.ExpeditionsCompleted, m.KillCount),
                GameText.Format("roster.detail.line_injuries", injuries),
            };
            if (m.IsAlive && SignalLinkService.IsInSafeMode(state, m.LogicId))
            {
                lines.Add(GameText.Get("roster.row.safe_mode") + " " + SignalLinkService.SafeModeTooltip(state, m.LogicId));
            }
            return string.Join("\n", lines);
        }

        // ── 操作（与自检同一入口）──────────────────────────────────────────────

        public void ShowDetail(int logicId)
        {
            DetailLogicId = logicId;
            _detailMessageText = string.Empty;
            _lastDetailId = 0;
            _lastKey = null;
            Refresh();
        }

        public void ToggleSortDirection()
        {
            Descending = !Descending;
            _lastKey = null;
            Refresh();
        }

        public void SetSort(MachineRoster.SortKey key, bool descending)
        {
            Sort = key;
            Descending = descending;
            _lastKey = null;
            Refresh();
        }

        public void SetFilters(MachineRole? role, string chassisId, MachineRoster.StatusFilter status)
        {
            RoleFilter = role;
            ChassisFilter = string.IsNullOrEmpty(chassisId) ? null : chassisId;
            StatusFilterValue = status;
            _lastKey = null;
            Refresh();
        }

        private void OnSortPicked(string label)
        {
            int at = _sort.choices.IndexOf(label);
            if (at >= 0 && at < MachineRoster.AllSortKeys.Length)
            {
                SetSort(MachineRoster.AllSortKeys[at], Descending);
            }
        }

        private void OnRoleFilterPicked(string label)
        {
            int at = _filterRole.choices.IndexOf(label);
            if (at >= 0 && at < _roleFilterIds.Count)
            {
                SetFilters(_roleFilterIds[at], ChassisFilter, StatusFilterValue);
            }
        }

        private void OnChassisFilterPicked(string label)
        {
            int at = _filterChassis.choices.IndexOf(label);
            if (at >= 0 && at < _chassisIds.Count)
            {
                SetFilters(RoleFilter, _chassisIds[at], StatusFilterValue);
            }
        }

        private void OnStatusFilterPicked(string label)
        {
            int at = _filterStatus.choices.IndexOf(label);
            if (at >= 0 && at < MachineRoster.AllStatusFilters.Length)
            {
                SetFilters(RoleFilter, ChassisFilter, MachineRoster.AllStatusFilters[at]);
            }
        }

        private void OnRowSelected(int index, bool on)
        {
            int id = RowLogicId(index);
            if (id == 0)
            {
                return;
            }
            if (on)
            {
                _selected.Add(id);
            }
            else
            {
                _selected.Remove(id);
            }
            _lastKey = null;
            Refresh();
        }

        public void SelectAllShown()
        {
            for (int i = 0; i < VisibleRowCount; i++)
            {
                if (MachineRegistry.TryGetRecord(_rowIds[i], out MachineRecord m) && m.IsAlive)
                {
                    _selected.Add(_rowIds[i]);
                }
            }
            _lastKey = null;
            Refresh();
        }

        public void SelectNone()
        {
            _selected.Clear();
            _lastKey = null;
            Refresh();
        }

        /// <summary>FG5-RND-06（FGU-40）：关掉名册，打开陈列馆面板的纪念墙页（阵亡机器的名字、编号、经历、阵亡地点；按时间 / 地点排序）。</summary>
        public void OpenMemorial()
        {
            SetOpen(false);
            BlackBoxPanelUIToolkit.Open(BlackBoxPanelUIToolkit.TabMemorial);
        }

        private void OnRowRolePicked(int index, string label)
        {
            int id = RowLogicId(index);
            int at = RowRoleField(index)?.choices.IndexOf(label) ?? -1;
            List<MachineRole> ids = index >= 0 && index < _rowRoleChoices.Count ? _rowRoleChoices[index] : null;
            if (id == 0 || ids == null || at < 0 || at >= ids.Count)
            {
                return;
            }
            bool ok = MachineRoster.TrySetRole(Session, id, ids[at], out string message);
            Say(ok, message);
        }

        private void OnBatchPicked(string label)
        {
            int at = _batchRole.choices.IndexOf(label) - 1;
            if (at < 0 || at >= _batchIds.Count)
            {
                return;
            }
            var ids = new List<int>(_selected);
            int changed = MachineRoster.TrySetRoles(Session, ids, _batchIds[at], out string message);
            Say(changed > 0, message);
        }

        private void OnDetailRolePicked(string label)
        {
            int at = _detailRole.choices.IndexOf(label);
            if (DetailLogicId == 0 || at < 0 || at >= _detailRoleIds.Count)
            {
                return;
            }
            bool ok = MachineRoster.TrySetRole(Session, DetailLogicId, _detailRoleIds[at], out string message);
            SayDetail(ok, message);
        }

        private void OnPointPicked(string label)
        {
            int at = _detailPoint.choices.IndexOf(label);
            if (DetailLogicId == 0 || at < 0 || at >= _pointIds.Count)
            {
                return;
            }
            bool ok = MachineRoster.TrySetGarrisonPoint(Session, DetailLogicId, _pointIds[at], out string message);
            SayDetail(ok, message);
        }

        public bool Rename(string raw)
        {
            bool ok = MachineNaming.TryRename(DetailLogicId, raw, out string message);
            if (ok && MachineRegistry.TryGetRecord(DetailLogicId, out MachineRecord m))
            {
                _nameField.SetValueWithoutNotify(m.CustomName ?? string.Empty);
            }
            SayDetail(ok, message);
            return ok;
        }

        public void ResetName()
        {
            bool ok = MachineNaming.TryResetName(DetailLogicId, out string message);
            if (ok)
            {
                _nameField.SetValueWithoutNotify(string.Empty);
            }
            SayDetail(ok, message);
        }

        public bool JumpTo(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null || !m.IsAlive)
            {
                return false;
            }
            Vector2 at = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : m.WorldPosition;
            LastJumpPosition = at;
            Campaign.WorldSim.WorldView.FlyTo(m.RegionId, at);
            SetOpen(false);
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

        private void SayDetail(bool ok, string message)
        {
            _detailMessageText = message ?? string.Empty;
            _detailMessageError = !ok;
            _lastKey = null;
            Refresh();
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, _detailMessageText);
        }

        /// <summary>自检：按选项下标选下拉框（与玩家点选同一回调）。</summary>
        public static bool PickForTests(DropdownField field, int index) => RulesPanelUIToolkit.PickForTests(field, index);
    }
}
