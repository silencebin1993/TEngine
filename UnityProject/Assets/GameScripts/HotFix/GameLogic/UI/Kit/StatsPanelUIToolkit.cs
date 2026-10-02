using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>统计面板的页签。</summary>
    public enum StatsTab
    {
        Production = 0,
        Buildings = 1,
        Bottlenecks = 2,
        Planner = 3,
        Combat = 4,
        /// <summary>物流与能源（电网发电 / 缺电 / 储能 / 燃油、传送带吞吐与清带、分流器分出、管线累计）。</summary>
        Flow = 5,
    }

    /// <summary>
    /// 统计面板。
    /// - FG4-ECO-08（FG04 FGR-ECO-050 / 052；FG13 FGU-13“速率曲线、效率、瓶颈”、FGU-14“目标产量 → 需求计算”）：
    ///   · 生产：四个时间窗口（1 分钟 / 10 分钟 / 1 小时 / 10 小时）里每种物品的产量、消耗与净速率，持续赤字在最上面单列；点一行看它的速率曲线；每行可“固定 / 取消固定”到资源顶栏。
    ///   · 建筑：每座生产建筑最近 10 分钟的效率与产出（效率低的在前），“打开面板”直接打开该建筑的面板（任何地点都能打开，不必回家园：DEBT-FG0ARCH01-07）。
    ///   · 瓶颈：最近 10 分钟最常缺的物品与缺它的建筑（缺得最久的在前），同样可打开建筑面板。
    ///   · 规划：选目标物品与每分钟产量 → 需要的建筑（精确值与要建的座数）、原料与采集建筑、副产品、电力；可以逐物品换配方；“固定到顶栏作为目标”。只计算，不建造。
    ///   数据全部来自 <see cref="ProductionStats"/> 的窗口桶与 <see cref="ProductionPlanner"/>（纯计算）；面板打开时按 eco.stats.panel_refresh_seconds 真实秒节流重建，不打开时每帧 O(1)。
    /// - FG2-FW-04：战斗 · 反应伤害归因（累计与每场明细，可按“全部 / 远征 / 突袭”筛选）。
    /// 入口：暂停菜单“统计”、快捷键（默认 Alt+T）、资源顶栏的固定物品。Esc / 关闭按钮 / 点遮罩关闭；模态。
    /// </summary>
    public sealed class StatsPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30076;

        public static StatsPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        private static bool _pendingOpen;
        private static StatsTab? _pendingTab;
        private static string _pendingItem;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        private sealed class RowView
        {
            public VisualElement Root;
            public Label Text;
            public Button B1;
            public Button B2;
            public Action Act1;
            public Action Act2;
            public Action Click;
            public string Cls;
        }

        private struct RowSpec
        {
            public string Text;
            public string Cls;
            public string B1Text;
            public Action B1;
            public string B2Text;
            public Action B2;
            public Action Click;
            public bool Selected;
        }

        private VisualElement _root;
        private Label _title;
        private Label _count;
        private Label _section;
        private Button _close;
        private readonly Button[] _tabs = new Button[6];
        private readonly Button[] _filters = new Button[3];
        private readonly Button[] _windows = new Button[ProductionStats.TierCount];
        private VisualElement _filterBar, _windowBar, _curveBox, _plannerBar;
        private Label _curveTitle, _curveLegend, _message, _rateUnit;
        private VisualElement _curve;
        private DropdownField _plannerTarget;
        private TextField _plannerRate;
        private Button _allItems;
        private TextField _itemSearch;
        private VisualElement _searchRow;
        private static bool _pendingAllItems;
        private Button _plannerPin;
        private ScrollView _list;
        private Label _empty;
        private Label _footer;
        private readonly List<RowView> _rows = new List<RowView>();
        private readonly List<RowSpec> _specs = new List<RowSpec>(32);
        private int _key;
        private double _nextReread;
        private string _messageText = string.Empty;
        private bool _messageError;

        private readonly float[] _seriesP = new float[64];
        private readonly float[] _seriesC = new float[64];
        private int _seriesN;

        private readonly List<ItemDef> _targets = new List<ItemDef>(24);
        private readonly Dictionary<string, string> _recipeChoices = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<ProductionStats.ItemRate> _rates = new List<ProductionStats.ItemRate>(32);
        private readonly List<ProductionStats.BuildingEfficiency> _effs = new List<ProductionStats.BuildingEfficiency>(32);
        private readonly List<ProductionStats.Bottleneck> _bns = new List<ProductionStats.Bottleneck>(8);

        public ReactionLogFilter CurrentFilter { get; private set; } = ReactionLogFilter.All;
        public StatsTab CurrentTab { get; private set; } = StatsTab.Production;
        public int CurrentWindow { get; private set; } = 1;
        /// <summary>生产页选中的物品（速率曲线画它）。</summary>
        public string SelectedItemId { get; private set; }
        /// <summary>审查修复（FGR-ECO-051“任意物品”）：生产页列出物品表里的全部物品（含从没有收支、不能生产的），可搜索、逐行固定。</summary>
        public bool ShowAllItems { get; private set; }
        /// <summary>“全部物品”的搜索词（名字或编号包含，不区分大小写；空 = 不过滤）。</summary>
        public string ItemSearch { get; private set; } = string.Empty;
        /// <summary>规划页的目标与产量（自检读写）。</summary>
        public string PlannerTargetId { get; private set; }
        public float PlannerRate { get; private set; }
        private bool _plannerRateSet;
        public ProductionPlanner.Result LastPlan { get; private set; }
        /// <summary>“打开面板”最后跳到的建筑（自检读）。</summary>
        public string LastJumpBuildingId { get; private set; }
        /// <summary>重建次数（自检：节流内不重建）。</summary>
        public int Rebuilds { get; private set; }

        protected override string UxmlLocation => "StatsPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public int VisibleRowCount { get; private set; }
        public string RowText(int i) => i >= 0 && i < VisibleRowCount && i < _rows.Count ? _rows[i].Text.text : string.Empty;
        public Button RowButton(int i, int which) => i >= 0 && i < VisibleRowCount ? (which == 0 ? _rows[i].B1 : _rows[i].B2) : null;
        public bool RowButtonVisible(int i, int which) => RowButton(i, which) is Button b && !b.ClassListContains("uk-hidden");
        public string RowClass(int i) => i >= 0 && i < VisibleRowCount ? _rows[i].Cls ?? string.Empty : string.Empty;
        public void ClickRow(int i)
        {
            if (i >= 0 && i < VisibleRowCount)
            {
                _rows[i].Click?.Invoke();
            }
        }
        public int FindRow(string contains)
        {
            for (int i = 0; i < VisibleRowCount; i++)
            {
                if (_rows[i].Text.text.Contains(contains))
                {
                    return i;
                }
            }
            return -1;
        }
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string CountText => _count?.text ?? string.Empty;
        public string SectionText => _section?.text ?? string.Empty;
        public string MessageText => _messageText;
        public string CurveTitleText => _curveTitle?.text ?? string.Empty;
        public string CurveLegendText => _curveLegend?.text ?? string.Empty;
        public bool CurveVisible => _curveBox != null && !_curveBox.ClassListContains("uk-hidden");
        public int CurvePoints => _seriesN;
        public float CurveProduced(int i) => i >= 0 && i < _seriesN ? _seriesP[i] : 0f;
        public float CurveConsumed(int i) => i >= 0 && i < _seriesN ? _seriesC[i] : 0f;
        public Button FilterButton(ReactionLogFilter f) => _filters[(int)f];
        public Button TabButton(StatsTab t) => _tabs[(int)t];
        public Button WindowButton(int tier) => tier >= 0 && tier < _windows.Length ? _windows[tier] : null;
        public Button CloseButton => _close;
        public DropdownField PlannerTargetField => _plannerTarget;
        public TextField PlannerRateField => _plannerRate;
        public Button PlannerPinButton => _plannerPin;
        public Button AllItemsButton => _allItems;
        public TextField ItemSearchField => _itemSearch;
        public bool ItemSearchVisible => _searchRow != null && !_searchRow.ClassListContains("uk-hidden");

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

        /// <summary>打开到某个页签（顶栏、通知等入口）；<paramref name="itemId"/> 不为空时生产页选中它、规划页以它为目标。</summary>
        public static void OpenTab(StatsTab tab, string itemId = null)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingTab = tab;
                _pendingItem = itemId;
                return;
            }
            Instance.SetOpen(true);
            if (itemId != null)
            {
                if (tab == StatsTab.Planner)
                {
                    Instance.PlannerTargetId = itemId;
                }
                else
                {
                    Instance.SelectedItemId = itemId;
                }
            }
            Instance.SelectTab(tab);
        }

        /// <summary>顶栏“+ 固定物品”：打开生产页的“全部物品”列表（任意物品都能搜索并固定，FGR-ECO-051）。</summary>
        public static void OpenAllItems()
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingTab = StatsTab.Production;
                _pendingItem = null;
                _pendingAllItems = true;
                return;
            }
            OpenTab(StatsTab.Production);
            Instance.SetShowAllItems(true);
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
            if (_pendingOpen)
            {
                _pendingOpen = false;
                if (_pendingTab.HasValue)
                {
                    StatsTab t = _pendingTab.Value;
                    string item = _pendingItem;
                    _pendingTab = null;
                    _pendingItem = null;
                    OpenTab(t, item);
                    if (_pendingAllItems)
                    {
                        _pendingAllItems = false;
                        SetShowAllItems(true);
                    }
                }
                else
                {
                    SetOpen(true);
                }
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("StatsPanelRoot");
            _title = root.Q<Label>("StatsPanelTitle");
            _count = root.Q<Label>("StatsPanelCount");
            _close = root.Q<Button>("StatsPanelClose");
            _section = root.Q<Label>("StatsSectionCombat");
            _tabs[(int)StatsTab.Production] = root.Q<Button>("StatsTabProduction");
            _tabs[(int)StatsTab.Buildings] = root.Q<Button>("StatsTabBuildings");
            _tabs[(int)StatsTab.Bottlenecks] = root.Q<Button>("StatsTabBottleneck");
            _tabs[(int)StatsTab.Planner] = root.Q<Button>("StatsTabPlanner");
            _tabs[(int)StatsTab.Combat] = root.Q<Button>("StatsTabCombat");
            _tabs[(int)StatsTab.Flow] = root.Q<Button>("StatsTabFlow");
            _filterBar = root.Q<VisualElement>("StatsFilterBar");
            _filters[(int)ReactionLogFilter.All] = root.Q<Button>("StatsFilterAll");
            _filters[(int)ReactionLogFilter.Expedition] = root.Q<Button>("StatsFilterExpedition");
            _filters[(int)ReactionLogFilter.Raid] = root.Q<Button>("StatsFilterRaid");
            _windowBar = root.Q<VisualElement>("StatsWindowBar");
            for (int i = 0; i < _windows.Length; i++)
            {
                _windows[i] = root.Q<Button>("StatsWindow" + i.ToString(CultureInfo.InvariantCulture));
                int tier = i;
                _windows[i].clicked += () => SelectWindow(tier);
            }
            _curveBox = root.Q<VisualElement>("StatsCurveBox");
            _curveTitle = root.Q<Label>("StatsCurveTitle");
            _curveLegend = root.Q<Label>("StatsCurveLegend");
            _curve = root.Q<VisualElement>("StatsCurve");
            _curve.generateVisualContent += DrawCurve;
            _plannerBar = root.Q<VisualElement>("StatsPlannerBar");
            _plannerTarget = root.Q<DropdownField>("PlannerTarget");
            _plannerRate = root.Q<TextField>("PlannerRate");
            _rateUnit = root.Q<Label>("PlannerRateUnit");
            _plannerPin = root.Q<Button>("PlannerPin");
            _allItems = root.Q<Button>("StatsAllItems");
            _itemSearch = root.Q<TextField>("StatsItemSearch");
            _searchRow = root.Q<VisualElement>("StatsSearchRow");
            _allItems.clicked += () => SetShowAllItems(!ShowAllItems);
            // 搜索：输入即过滤（只改本面板的列表，O(物品种类)）。
            _itemSearch.RegisterValueChangedCallback(evt => SetItemSearch(evt.newValue));
            _message = root.Q<Label>("StatsMessage");
            _list = root.Q<ScrollView>("StatsList");
            _empty = root.Q<Label>("StatsEmpty");
            _footer = root.Q<Label>("StatsFooter");
            _close.clicked += () => SetOpen(false);
            for (int t = 0; t < _tabs.Length; t++)
            {
                StatsTab captured = (StatsTab)t;
                _tabs[t].clicked += () => SelectTab(captured);
            }
            for (int f = 0; f < _filters.Length; f++)
            {
                ReactionLogFilter captured = (ReactionLogFilter)f;
                _filters[f].clicked += () => SelectFilter(captured);
            }
            _plannerTarget.RegisterValueChangedCallback(evt => OnPlannerTargetPicked(evt.newValue));
            // 产量输入：回车或失去焦点时生效（输入中途不反复重算）。
            _plannerRate.RegisterCallback<FocusOutEvent>(_ => SetPlannerRateText(_plannerRate.value));
            _plannerRate.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    SetPlannerRateText(_plannerRate.value);
                }
            });
            _plannerPin.clicked += PinPlannerTarget;
            UiTooltip.Attach(_plannerPin, () => new TooltipContent
            {
                Title = GameText.Get("planner.pin"),
                Body = GameText.Get("planner.pin.tip"),
            });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
        }

        // ── 操作（按钮与自检同一入口）──────────────────────────────────────────

        public void SelectTab(StatsTab tab)
        {
            CurrentTab = tab;
            _messageText = string.Empty;
            if (tab != StatsTab.Combat)
            {
                GuidanceHooks.Raise(GuidanceHooks.StatsProductionFirstOpen);
            }
            _key = 0;
            Refresh(force: true);
        }

        public void SelectFilter(ReactionLogFilter filter)
        {
            CurrentFilter = filter;
            _key = 0;
            Refresh(force: true);
        }

        public void SelectWindow(int tier)
        {
            CurrentWindow = Math.Max(0, Math.Min(ProductionStats.TierCount - 1, tier));
            _key = 0;
            Refresh(force: true);
        }

        public void SelectItem(string itemId)
        {
            SelectedItemId = itemId;
            _key = 0;
            Refresh(force: true);
        }

        /// <summary>生产页“全部物品 / 只看有收支的”切换（按钮与自检同一入口）。</summary>
        public void SetShowAllItems(bool on)
        {
            ShowAllItems = on;
            _messageText = string.Empty;
            _key = 0;
            Refresh(force: true);
        }

        public void SetItemSearch(string text)
        {
            ItemSearch = (text ?? string.Empty).Trim();
            if (_itemSearch != null && _itemSearch.value != (text ?? string.Empty))
            {
                _itemSearch.SetValueWithoutNotify(text ?? string.Empty);
            }
            _key = 0;
            Refresh(force: true);
        }

        public void TogglePin(string itemId)
        {
            bool ok = ResourcePins.Toggle(CampaignSession.Current, itemId, out string msg);
            Say(ok, msg);
        }

        /// <summary>“打开面板”：收起统计面板（与暂停菜单），打开这座建筑的面板。不移动镜头、不要求在家园（远程查看）。</summary>
        public void JumpToBuilding(string buildingId)
        {
            LastJumpBuildingId = buildingId;
            SetOpen(false);
            if (PauseMenuUIToolkit.IsOpen)
            {
                PauseMenuUIToolkit.Close();
            }
            ProductionPanelUIToolkit.Open(buildingId);
        }

        public void SetPlannerTarget(string itemId)
        {
            PlannerTargetId = itemId;
            _key = 0;
            Refresh(force: true);
        }

        public void SetPlannerRateText(string text)
        {
            string t = (text ?? string.Empty).Trim().Replace('，', '.').Replace(',', '.');
            _plannerRateSet = true;
            PlannerRate = float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : -1f;
            _key = 0;
            Refresh(force: true);
        }

        /// <summary>换配方：这一物品的配方换成下一条可选配方（循环）。</summary>
        public void CycleRecipe(string itemId)
        {
            List<RecipeDef> options = ProductionPlanner.RecipesFor(itemId);
            if (options.Count < 2)
            {
                return;
            }
            _recipeChoices.TryGetValue(itemId, out string cur);
            int at = Math.Max(0, options.FindIndex(r => r.Id == cur));
            if (cur == null)
            {
                at = 0;
            }
            RecipeDef next = options[(at + 1) % options.Count];
            _recipeChoices[itemId] = next.Id;
            Say(true, GameText.Format("planner.recipe_changed", ItemCatalog.NameOf(itemId), next.Name));
            _key = 0;
            Refresh(force: true);
        }

        public void PinPlannerTarget()
        {
            ProductionPlanner.Result r = LastPlan;
            if (r == null || !r.Ok)
            {
                Say(false, r?.Error ?? GameText.Get("planner.error.no_target"));
                return;
            }
            bool ok = ResourcePins.TryPin(CampaignSession.Current, r.Target.Id, (float)r.TargetPerMinute, out string msg);
            Say(ok, msg);
        }

        private void OnPlannerTargetPicked(string label)
        {
            int at = _plannerTarget.choices.IndexOf(label);
            if (at >= 0 && at < _targets.Count)
            {
                SetPlannerTarget(_targets[at].Id);
            }
        }

        private void Say(bool ok, string message)
        {
            _messageText = message ?? string.Empty;
            _messageError = !ok;
            _key = 0;
            Refresh(force: true);
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
                GuidanceHooks.Raise(GuidanceHooks.StatsPanelFirstOpen);
                if (CurrentTab != StatsTab.Combat)
                {
                    GuidanceHooks.Raise(GuidanceHooks.StatsProductionFirstOpen);
                }
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                _messageText = string.Empty;
                Refresh(force: true);
            }
            else
            {
                InputRouter.PopModal(this);
                UiEscapeStack.Remove(this);
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

        // ── 重建 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 按页签重建。战斗页：数据版本 / 筛选 / 语言变化时重建（键不变时 O(1)）。生产类页签：键（页签、窗口、选中项、语言、固定版本、统计版本）变化时，
        /// 或每 eco.stats.panel_refresh_seconds 真实秒重读一次（速率随时间变化）；<paramref name="force"/> = 立刻重建。
        /// </summary>
        public void Refresh(bool force = false)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            int key = HashCode.Combine(HashCode.Combine(ReactionAttribution.Revision, ReactionFeedback.Revision, (int)CurrentFilter, (int)GameText.Language, GameSettings.Revision),
                HashCode.Combine((int)CurrentTab, CurrentWindow, SelectedItemId, PlannerTargetId, PlannerRate, ResourcePins.Revision, _messageText),
                HashCode.Combine(state != null ? state.GetHashCode() : 0, ShowAllItems, ItemSearch, HomeInventory.Revision));
            double now = Clock();
            bool timed = CurrentTab != StatsTab.Combat && now >= _nextReread;
            if (!force && key == _key && !timed)
            {
                return;
            }
            _key = key;
            _nextReread = now + Math.Max(0.1f, GridContent.Tuning("eco.stats.panel_refresh_seconds"));
            Rebuilds++;

            _title.text = GameText.Get("stats.panel.title");
            _close.text = GameText.Get("codex.panel.close");
            string[] tabKeys = { "stats.tab.production", "stats.tab.buildings", "stats.tab.bottleneck", "stats.tab.planner", "stats.tab.combat", "stats.tab.flow" };
            for (int t = 0; t < _tabs.Length; t++)
            {
                bool cur = t == (int)CurrentTab;
                _tabs[t].text = cur ? GameText.Format("ui.world.focus.current", GameText.Get(tabKeys[t])) : GameText.Get(tabKeys[t]);
                _tabs[t].EnableInClassList("rl-selected", cur);
            }
            SetVisible(_filterBar, CurrentTab == StatsTab.Combat);
            SetVisible(_windowBar, CurrentTab == StatsTab.Production);
            SetVisible(_searchRow, CurrentTab == StatsTab.Production && ShowAllItems);
            SetVisible(_plannerBar, CurrentTab == StatsTab.Planner);
            SetVisible(_curveBox, false);

            _specs.Clear();
            string empty = null;
            switch (CurrentTab)
            {
                case StatsTab.Production:
                    empty = BuildProduction(state);
                    break;
                case StatsTab.Buildings:
                    empty = BuildBuildings(state);
                    break;
                case StatsTab.Bottlenecks:
                    empty = BuildBottlenecks(state);
                    break;
                case StatsTab.Planner:
                    empty = BuildPlanner(state);
                    break;
                case StatsTab.Flow:
                    empty = BuildFlow(state);
                    break;
                default:
                    empty = BuildCombat(state);
                    break;
            }
            Apply();
            bool isEmpty = empty != null && _specs.Count == 0;
            _empty.EnableInClassList("uk-hidden", !isEmpty);
            _empty.text = isEmpty ? empty : string.Empty;
            bool hasMsg = !string.IsNullOrEmpty(_messageText);
            SetVisible(_message, hasMsg);
            _message.text = _messageText;
            _message.EnableInClassList("st-message-error", hasMsg && _messageError);
        }

        private string BuildCombat(CampaignState state)
        {
            _section.text = GameText.Get("stats.panel.section_combat");
            _filters[(int)ReactionLogFilter.All].text = GameText.Get("reaction.panel.filter_all");
            _filters[(int)ReactionLogFilter.Expedition].text = GameText.Get("reaction.panel.filter_expedition");
            _filters[(int)ReactionLogFilter.Raid].text = GameText.Get("reaction.panel.filter_raid");
            for (int f = 0; f < _filters.Length; f++)
            {
                _filters[f].EnableInClassList("rl-selected", f == (int)CurrentFilter);
            }
            var lines = new List<(string Text, string Cls)>();
            int sessions = ReactionAttributionView.AppendTotals(state, CurrentFilter, lines);
            if (sessions > 0)
            {
                lines.Add((GameText.Get("stats.panel.sessions_title"), "rl-row-title"));
                ReactionAttributionView.AppendSessions(state, CurrentFilter, lines);
            }
            foreach ((string text, string cls) in lines)
            {
                _specs.Add(new RowSpec { Text = text, Cls = cls?.Replace("rl-row", "st-row") });
            }
            _count.text = GameText.Format("stats.panel.count", sessions);
            _footer.text = Hint("stats.panel.hint", ReactionAttribution.SessionCapacity);
            return GameText.Get("stats.panel.empty");
        }

        /// <summary>
        /// 生产页“全部物品”：物品表里的每一种（按表顺序），显示库存（流体没有家园库存读数 = “—”）与当前窗口的产出 / 消耗速率；
        /// 每行“固定 / 取消固定”，能生产或采集的物品另有“规划”。搜索按名字或编号包含（不区分大小写）。O(物品种类 × 桶数)，只在面板打开时节流重建。
        /// </summary>
        private string BuildAllItems(CampaignState state)
        {
            if (state == null)
            {
                _count.text = GameText.Format("stats.prod.count", 0);
                return GameText.Get("stats.prod.empty");
            }
            string q = ItemSearch ?? string.Empty;
            int shown = 0;
            _specs.Add(new RowSpec { Text = GameText.Format("stats.prod.all_title", ItemCatalog.Items.Count), Cls = "st-row-title" });
            bool keepSelected = false;
            List<ItemDef> plannable = ProductionPlanner.Targets();
            foreach (ItemDef item in ItemCatalog.Items)
            {
                if (item == null)
                {
                    continue;
                }
                if (q.Length > 0 && (item.Name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && item.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                shown++;
                string id = item.Id;
                bool pinned = ResourcePins.IsPinned(state, id);
                ProductionStats.ItemRate r = ProductionStats.RateOf(state, CurrentWindow, item);
                string stock = HomeInventory.IsStorable(item)
                    ? ProductionStats.Amount(item, HomeInventory.Stock(state, item))
                    : GameText.Get("stats.prod.stock_none");
                string text = GameText.Format("stats.prod.all_row", item.Name, stock, ProductionStats.Rate(item, r.ProducedPerMinute), ProductionStats.Rate(item, r.ConsumedPerMinute));
                if (pinned)
                {
                    text += GameText.Get("stats.prod.tag_pinned");
                }
                bool selected = id == SelectedItemId;
                keepSelected |= selected;
                bool canPlan = plannable.Contains(item);
                _specs.Add(new RowSpec
                {
                    Text = selected ? GameText.Format("ui.world.focus.current", text) : text,
                    Selected = selected,
                    Click = () => SelectItem(id),
                    B1Text = GameText.Get(pinned ? "stats.pin.unpin" : "stats.pin.pin"),
                    B1 = () => TogglePin(id),
                    B2Text = canPlan ? GameText.Get("stats.prod.plan") : null,
                    B2 = canPlan ? () => OpenTab(StatsTab.Planner, id) : (Action)null,
                });
            }
            _count.text = GameText.Format("stats.prod.count", shown);
            if (shown == 0)
            {
                _specs.Add(new RowSpec { Text = GameText.Format("stats.prod.search_none", q), Cls = "st-row-dim" });
            }
            if (!keepSelected)
            {
                SelectedItemId = null;
            }
            return null;
        }

        private string BuildProduction(CampaignState state)
        {
            _section.text = GameText.Get("stats.section.production");
            for (int i = 0; i < _windows.Length; i++)
            {
                string label = ProductionStats.WindowName(i);
                bool cur = i == CurrentWindow;
                _windows[i].text = cur ? GameText.Format("ui.world.focus.current", label) : label;
                _windows[i].EnableInClassList("rl-selected", cur);
            }
            _footer.text = Hint("stats.prod.footer", ProductionStats.UiNumber(ProductionStats.DeficitMinutes));
            _allItems.text = GameText.Get(ShowAllItems ? "stats.prod.all_items_back" : "stats.prod.all_items");
            _allItems.EnableInClassList("rl-selected", ShowAllItems);
            _itemSearch.label = GameText.Get("stats.prod.search");
            if (ShowAllItems)
            {
                // 审查修复（FGR-ECO-051）：不论有没有产线，都能从这里固定任意物品（新游戏的空状态也不挡住）。
                return BuildAllItems(state);
            }
            if (state == null || (!ProductionStats.HasAnyLine(state) && !AnyRecorded(state)))
            {
                _count.text = GameText.Format("stats.prod.count", 0);
                SelectedItemId = null;
                return GameText.Get("stats.prod.empty");
            }
            ProductionStats.CollectRates(state, CurrentWindow, _rates);
            // 固定了但这一窗口没有收支的物品也列出来（显示 0），方便对照目标。
            foreach (PinnedItemRecord pin in ResourcePins.Pins(state))
            {
                if (pin != null && ItemCatalog.TryGet(pin.ItemId, out ItemDef d) && _rates.FindIndex(r => r.Item == d) < 0)
                {
                    _rates.Add(ProductionStats.RateOf(state, CurrentWindow, d));
                }
            }
            _count.text = GameText.Format("stats.prod.count", _rates.Count);
            float eff = ProductionStats.EffectiveSeconds(state, CurrentWindow);
            _specs.Add(new RowSpec
            {
                Text = GameText.Format("stats.prod.window_line", ProductionStats.WindowName(CurrentWindow), Minutes(eff)),
                Cls = "st-row-dim",
            });
            IReadOnlyList<DeficitRecord> deficits = ProductionStats.Deficits(state);
            bool anyDeficit = false;
            foreach (DeficitRecord d in deficits)
            {
                if (d == null || !ItemCatalog.TryGet(d.ItemId, out ItemDef item))
                {
                    continue;
                }
                if (!anyDeficit)
                {
                    _specs.Add(new RowSpec { Text = GameText.Get("stats.deficit.title"), Cls = "st-row-title" });
                    anyDeficit = true;
                }
                ProductionStats.ItemRate r = ProductionStats.RateOf(state, 1, item);
                string id = d.ItemId;
                _specs.Add(new RowSpec
                {
                    Text = GameText.Format(d.Warned ? "stats.deficit.row_warned" : "stats.deficit.row", ProductionStats.DeficitText(state, d, item, r.Produced, r.Consumed)),
                    Cls = "st-row-warn",
                    Click = () => SelectItem(id),
                    B1Text = GameText.Get("stats.bn.title_short"),
                    B1 = () => SelectTab(StatsTab.Bottlenecks),
                });
            }
            if (_rates.Count == 0)
            {
                _specs.Add(new RowSpec { Text = GameText.Get("stats.prod.no_activity"), Cls = "st-row-dim" });
            }
            else
            {
                _specs.Add(new RowSpec { Text = GameText.Get("stats.prod.items_title"), Cls = "st-row-title" });
            }
            if (SelectedItemId == null || !_rates.Exists(r => r.Item.Id == SelectedItemId))
            {
                SelectedItemId = _rates.Count > 0 ? _rates[0].Item.Id : null;
            }
            foreach (ProductionStats.ItemRate r in _rates)
            {
                ItemDef item = r.Item;
                string id = item.Id;
                bool pinned = ResourcePins.IsPinned(state, id);
                bool deficit = ProductionStats.IsDeficit(state, id, out bool warned);
                bool selected = id == SelectedItemId;
                string text = GameText.Format("stats.prod.row", item.Name, ProductionStats.Rate(item, r.ProducedPerMinute), ProductionStats.Rate(item, r.ConsumedPerMinute),
                    Signed(item, r.NetPerMinute), ProductionStats.Amount(item, r.Produced), ProductionStats.Amount(item, r.Consumed));
                if (deficit)
                {
                    text += GameText.Get(warned ? "stats.prod.tag_deficit_warned" : "stats.prod.tag_deficit");
                }
                if (pinned)
                {
                    text += GameText.Get("stats.prod.tag_pinned");
                }
                _specs.Add(new RowSpec
                {
                    Text = selected ? GameText.Format("ui.world.focus.current", text) : text,
                    Cls = deficit ? "st-row-warn" : null,
                    Selected = selected,
                    Click = () => SelectItem(id),
                    B1Text = GameText.Get(pinned ? "stats.pin.unpin" : "stats.pin.pin"),
                    B1 = () => TogglePin(id),
                    B2Text = GameText.Get("stats.prod.plan"),
                    B2 = () => OpenTab(StatsTab.Planner, id),
                });
            }
            // 速率曲线（选中物品，当前窗口每桶的产量 / 消耗）。
            if (SelectedItemId != null && ItemCatalog.TryGet(SelectedItemId, out ItemDef sel))
            {
                SetVisible(_curveBox, true);
                _seriesN = ProductionStats.Series(state, CurrentWindow, sel, _seriesP, _seriesC);
                float max = 0f;
                for (int i = 0; i < _seriesN; i++)
                {
                    max = Math.Max(max, Math.Max(_seriesP[i], _seriesC[i]));
                }
                _curveMax = max;
                _curveTitle.text = GameText.Format("stats.curve.title", sel.Name, ProductionStats.WindowName(CurrentWindow),
                    ProductionStats.UiNumber(ProductionStats.TierBucketSeconds(CurrentWindow)), ProductionStats.Amount(sel, sel.Form == ItemForm.Fluid ? (long)(max * 1000f) : (long)max));
                _curveLegend.text = GameText.Get("stats.curve.legend");
                _curve.MarkDirtyRepaint();
            }
            else
            {
                _seriesN = 0;
            }
            return null;
        }

        private float _curveMax;

        private static bool AnyRecorded(CampaignState state)
        {
            ProductionStatsState st = state?.Stats?.Production;
            if (st?.Tiers == null)
            {
                return false;
            }
            foreach (ProdStatTierRecord t in st.Tiers)
            {
                foreach (ProdStatBucketRecord b in t?.Buckets ?? Array.Empty<ProdStatBucketRecord>())
                {
                    if (b != null && b.Index >= 0 && ((b.Produced?.Length ?? 0) > 0 || (b.Consumed?.Length ?? 0) > 0))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private string BuildBuildings(CampaignState state)
        {
            _section.text = GameText.Format("stats.section.buildings", ProductionStats.UiNumber(GridContent.Tuning("building.stats.window_minutes")));
            _footer.text = Hint("stats.buildings.footer");
            ProductionStats.CollectEfficiencies(state, _effs);
            _count.text = GameText.Format("stats.buildings.count", _effs.Count);
            foreach (ProductionStats.BuildingEfficiency e in _effs)
            {
                string id = e.Building.BuildingId;
                string eff = e.Efficiency < 0f ? GameText.Get("stats.buildings.na") : GameText.Format("stats.buildings.eff", Mathf.RoundToInt(e.Efficiency * 100f));
                string outs = Outputs(e.Outputs);
                string status = ProductionService.ReasonText(state, e.Producer);
                _specs.Add(new RowSpec
                {
                    Text = GameText.Format("stats.buildings.row", BuildingOps.IdentLine(e.Building), eff, e.Done, outs, status),
                    Cls = e.Efficiency >= 0f && e.Efficiency < 0.5f ? "st-row-warn" : null,
                    B1Text = GameText.Get("stats.open_building"),
                    B1 = () => JumpToBuilding(id),
                });
            }
            return GameText.Get("stats.prod.empty");
        }

        private string BuildBottlenecks(CampaignState state)
        {
            _section.text = GameText.Format("stats.section.bottleneck", ProductionStats.UiNumber(GridContent.Tuning("building.stats.window_minutes")));
            _footer.text = Hint("stats.bn.footer");
            if (!ProductionStats.HasAnyLine(state))
            {
                _count.text = GameText.Format("stats.bn.count", 0);
                return GameText.Get("stats.prod.empty");
            }
            ProductionStats.CollectBottlenecks(state, _bns);
            _count.text = GameText.Format("stats.bn.count", _bns.Count);
            int top = Math.Max(1, GridContent.TuningInt("eco.stats.bottleneck_top"));
            for (int i = 0; i < _bns.Count && i < top; i++)
            {
                ProductionStats.Bottleneck bn = _bns[i];
                string itemId = bn.Item.Id;
                _specs.Add(new RowSpec
                {
                    Text = GameText.Format("stats.bn.item", i + 1, bn.Item.Name, Minutes(bn.Seconds), bn.Buildings.Count),
                    Cls = "st-row-title",
                    B1Text = GameText.Get("stats.prod.plan"),
                    B1 = () => OpenTab(StatsTab.Planner, itemId),
                });
                foreach (ProductionStats.StarvedBuilding sb in bn.Buildings)
                {
                    string id = sb.Building.BuildingId;
                    _specs.Add(new RowSpec
                    {
                        Text = GameText.Format("stats.bn.building", BuildingOps.IdentLine(sb.Building), Minutes(sb.Seconds)),
                        Cls = "st-row-sub",
                        B1Text = GameText.Get("stats.open_building"),
                        B1 = () => JumpToBuilding(id),
                    });
                }
            }
            return GameText.Get("stats.bn.none");
        }

        private readonly List<(string Text, string Cls)> _flowLines = new List<(string Text, string Cls)>(24);

        private string BuildFlow(CampaignState state)
        {
            _section.text = GameText.Get("stats.section.flow");
            _footer.text = Hint("stats.flow.footer");
            FlowEnergyStats.Build(state, _flowLines);
            _count.text = string.Empty;
            foreach ((string text, string cls) in _flowLines)
            {
                _specs.Add(new RowSpec { Text = text, Cls = cls });
            }
            return null;
        }

        private string BuildPlanner(CampaignState state)
        {
            _section.text = GameText.Get("stats.section.planner");
            _footer.text = Hint("planner.footer");
            _plannerTarget.label = GameText.Get("planner.target");
            _plannerRate.label = GameText.Get("planner.rate");
            _plannerPin.text = GameText.Get("planner.pin");
            _targets.Clear();
            _targets.AddRange(ProductionPlanner.Targets());
            var labels = new List<string>(_targets.Count);
            foreach (ItemDef d in _targets)
            {
                labels.Add(d.Name);
            }
            if (PlannerTargetId == null || _targets.FindIndex(d => d.Id == PlannerTargetId) < 0)
            {
                PlannerTargetId = _targets.Count > 0 ? _targets[0].Id : null;
            }
            if (!_plannerRateSet)
            {
                PlannerRate = ProductionPlanner.DefaultRate;
            }
            DropdownChoices.Apply(_plannerTarget, labels, GameText.Get("planner.empty"));
            int at = _targets.FindIndex(d => d.Id == PlannerTargetId);
            if (at >= 0 && at < _plannerTarget.choices.Count)
            {
                _plannerTarget.SetValueWithoutNotify(_plannerTarget.choices[at]);
            }
            if (_plannerRate.focusController?.focusedElement != _plannerRate)
            {
                _plannerRate.SetValueWithoutNotify(PlannerRate > 0f ? ProductionStats.UiNumber(PlannerRate) : _plannerRate.value);
            }
            ItemCatalog.TryGet(PlannerTargetId, out ItemDef target);
            _rateUnit.text = GameText.Get(target != null && target.Form == ItemForm.Fluid ? "planner.unit_lpm" : "planner.unit_pm");
            _plannerPin.SetEnabled(target != null);
            ProductionPlanner.Result r = ProductionPlanner.Plan(target, PlannerRate, _recipeChoices);
            LastPlan = r;
            _count.text = r.Ok ? GameText.Format("planner.count", r.Rows.Count) : string.Empty;
            if (!r.Ok)
            {
                _specs.Add(new RowSpec { Text = r.Error, Cls = "st-row-warn" });
                return GameText.Get("planner.empty");
            }
            GuidanceHooks.Raise(GuidanceHooks.PlannerFirstPlan);
            _specs.Add(new RowSpec { Text = GameText.Format("planner.summary", target.Name, ProductionStats.Rate(target, (float)r.TargetPerMinute)), Cls = "st-row-dim" });
            if (r.Rows.Count > 0)
            {
                _specs.Add(new RowSpec { Text = GameText.Get("planner.buildings_title"), Cls = "st-row-title" });
            }
            foreach (ProductionPlanner.RecipeRow row in r.Rows)
            {
                string forId = row.ForItem.Id;
                string needs = ProductionPlanner.ResearchNote(state, row.BuildingType);
                _specs.Add(new RowSpec
                {
                    Text = GameText.Format("planner.row", HomeGridService.DisplayName(row.BuildingType), Num(row.Buildings), row.BuildingsToBuild, row.Recipe.Name,
                        ProductionStats.Rate(row.ForItem, (float)row.ForItemPerMinute), Mathf.RoundToInt((float)row.Utilization * 100f),
                        ProductionPlanner.PowerOf(row.BuildingType) > 0f ? GameText.Format("planner.row_power", Num(row.BuildingsToBuild * row.PowerEach)) : string.Empty) + needs,
                    Cls = needs.Length > 0 ? "st-row-warn" : null,
                    B1Text = row.Alternatives.Count > 0 ? GameText.Get("planner.cycle_recipe") : null,
                    B1 = row.Alternatives.Count > 0 ? () => CycleRecipe(forId) : (Action)null,
                });
            }
            if (r.Raws.Count > 0)
            {
                _specs.Add(new RowSpec { Text = GameText.Get("planner.raw_title"), Cls = "st-row-title" });
            }
            foreach (ProductionPlanner.RawRow raw in r.Raws)
            {
                string ex = raw.ExtractorType != null
                    ? GameText.Format("planner.raw_extract", Num(raw.Extractors), raw.ExtractorsToBuild, HomeGridService.DisplayName(raw.ExtractorType))
                      + ProductionPlanner.ResearchNote(state, raw.ExtractorType)
                    : string.Empty;
                _specs.Add(new RowSpec
                {
                    Text = GameText.Format("planner.raw_row", raw.Item.Name, ProductionStats.Rate(raw.Item, (float)raw.PerMinute), ex, GameText.Get(raw.NoteKey ?? "planner.raw.source_other")),
                });
            }
            if (r.Byproducts.Count > 0)
            {
                _specs.Add(new RowSpec { Text = GameText.Get("planner.byproduct_title"), Cls = "st-row-title" });
                foreach (ProductionPlanner.Byproduct b in r.Byproducts)
                {
                    _specs.Add(new RowSpec { Text = GameText.Format("planner.byproduct_row", b.Item.Name, ProductionStats.Rate(b.Item, (float)b.PerMinute)), Cls = "st-row-warn" });
                }
            }
            _specs.Add(new RowSpec { Text = GameText.Get("planner.power_title"), Cls = "st-row-title" });
            _specs.Add(new RowSpec { Text = GameText.Format("planner.power_row", Num(r.Power), Num(r.PowerToBuild), Num(r.ExtractorPower)) });
            _specs.Add(new RowSpec
            {
                Text = GameText.Format("planner.capacity", target.Name, ProductionStats.Rate(target, (float)r.TargetCapacityPerMinute)),
                Cls = "st-row-dim",
            });
            _specs.Add(new RowSpec { Text = GameText.Get("planner.no_auto_build"), Cls = "st-row-dim" });
            return null;
        }

        private void Apply()
        {
            while (_rows.Count < _specs.Count)
            {
                var rv = new RowView { Root = new VisualElement(), Text = new Label(), B1 = new Button(), B2 = new Button() };
                rv.Root.AddToClassList("st-row");
                rv.Text.AddToClassList("st-row-text");
                rv.B1.AddToClassList("mw-btn");
                rv.B1.AddToClassList("st-row-btn");
                rv.B2.AddToClassList("mw-btn");
                rv.B2.AddToClassList("st-row-btn");
                rv.Root.Add(rv.Text);
                rv.Root.Add(rv.B1);
                rv.Root.Add(rv.B2);
                rv.B1.clicked += () => rv.Act1?.Invoke();
                rv.B2.clicked += () => rv.Act2?.Invoke();
                rv.Text.RegisterCallback<ClickEvent>(_ => rv.Click?.Invoke());
                _list.Add(rv.Root);
                _rows.Add(rv);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                RowView rv = _rows[i];
                bool shown = i < _specs.Count;
                rv.Root.EnableInClassList("uk-hidden", !shown);
                if (rv.Cls != null)
                {
                    rv.Root.RemoveFromClassList(rv.Cls);
                    rv.Cls = null;
                }
                rv.Root.RemoveFromClassList("st-row-selected");
                if (!shown)
                {
                    rv.Act1 = rv.Act2 = rv.Click = null;
                    continue;
                }
                RowSpec s = _specs[i];
                rv.Text.text = s.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(s.Cls))
                {
                    rv.Root.AddToClassList(s.Cls);
                    rv.Cls = s.Cls;
                }
                rv.Root.EnableInClassList("st-row-selected", s.Selected);
                rv.Act1 = s.B1;
                rv.Act2 = s.B2;
                rv.Click = s.Click;
                rv.B1.text = s.B1Text ?? string.Empty;
                rv.B2.text = s.B2Text ?? string.Empty;
                SetVisible(rv.B1, s.B1 != null);
                SetVisible(rv.B2, s.B2 != null);
            }
            VisibleRowCount = _specs.Count;
        }

        // ── 速率曲线 ─────────────────────────────────────────────────────────

        private static readonly Color ProducedColor = new Color(0.45f, 0.85f, 0.55f);
        private static readonly Color ConsumedColor = new Color(1f, 0.62f, 0.35f);

        private void DrawCurve(MeshGenerationContext ctx)
        {
            Rect r = _curve.contentRect;
            if (_seriesN < 2 || r.width < 4f || r.height < 4f)
            {
                return;
            }
            Painter2D p = ctx.painter2D;
            float max = Math.Max(1e-3f, _curveMax);
            p.lineWidth = 1f;
            p.strokeColor = new Color(1f, 1f, 1f, 0.15f);
            p.BeginPath();
            p.MoveTo(new Vector2(0f, r.height - 0.5f));
            p.LineTo(new Vector2(r.width, r.height - 0.5f));
            p.Stroke();
            Stroke(p, _seriesP, ProducedColor, r, max, squareMarkers: false);
            Stroke(p, _seriesC, ConsumedColor, r, max, squareMarkers: true);
        }

        private void Stroke(Painter2D p, float[] s, Color c, Rect r, float max, bool squareMarkers)
        {
            float step = r.width / Math.Max(1, _seriesN - 1);
            p.strokeColor = c;
            p.fillColor = c;
            p.lineWidth = 2f;
            p.BeginPath();
            for (int i = 0; i < _seriesN; i++)
            {
                var pt = new Vector2(i * step, r.height - 2f - (r.height - 4f) * Mathf.Clamp01(s[i] / max));
                if (i == 0)
                {
                    p.MoveTo(pt);
                }
                else
                {
                    p.LineTo(pt);
                }
            }
            p.Stroke();
            // 形状标记（颜色之外再用形状区分：产出圆点、消耗方块；B15）。
            for (int i = 0; i < _seriesN; i++)
            {
                var pt = new Vector2(i * step, r.height - 2f - (r.height - 4f) * Mathf.Clamp01(s[i] / max));
                p.BeginPath();
                if (squareMarkers)
                {
                    p.MoveTo(pt + new Vector2(-2f, -2f));
                    p.LineTo(pt + new Vector2(2f, -2f));
                    p.LineTo(pt + new Vector2(2f, 2f));
                    p.LineTo(pt + new Vector2(-2f, 2f));
                    p.ClosePath();
                }
                else
                {
                    p.Arc(pt, 2f, 0f, 360f);
                }
                p.Fill();
            }
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        private static string Outputs(List<ItemStackRecord> outs)
        {
            if (outs == null || outs.Count == 0)
            {
                return GameText.Get("stats.buildings.no_output");
            }
            var parts = new List<string>(outs.Count);
            foreach (ItemStackRecord o in outs)
            {
                ItemCatalog.TryGet(o.ItemId, out ItemDef d);
                parts.Add(GameText.Format("stats.buildings.output", d != null ? d.Name : ItemCatalog.NameOf(o.ItemId),
                    d != null && d.Form == ItemForm.Fluid ? GameText.Format("stats.unit.liters", o.Amount) : o.Amount.ToString(CultureInfo.InvariantCulture)));
            }
            return string.Join(GameText.Get("stats.list_sep"), parts);
        }

        private static string Signed(ItemDef item, float v) =>
            (v > 0.005f ? "+" : string.Empty) + ProductionStats.Rate(item, v);

        private static string Minutes(float seconds) =>
            seconds < 60f
                ? GameText.Format("stats.unit.seconds", ProductionStats.UiNumber((float)Math.Round(seconds)))
                : GameText.Format("stats.unit.minutes", ProductionStats.UiNumber(seconds / 60f));

        private static string Num(double v) => ProductionStats.UiNumber((float)v);

        private static void SetVisible(VisualElement e, bool visible)
        {
            e?.EnableInClassList("uk-hidden", !visible);
        }

        /// <summary>脚注：先把 {act:动作名} 换成当前按键，再填数字占位（按键文字里不会有花括号）。</summary>
        private static string Hint(string key, params object[] args)
        {
            string pattern = InputDisplay.ExpandActionTokens(GameText.Get(key));
            try
            {
                return string.Format(pattern, args);
            }
            catch (FormatException)
            {
                return pattern;
            }
        }
    }
}
