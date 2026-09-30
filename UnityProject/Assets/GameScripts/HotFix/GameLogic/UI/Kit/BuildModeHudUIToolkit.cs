using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-ARCH-04 原型 → FG3-LOG-01 正式版（FGR-LOG-002～004、007、008；FG13 FGU-07 建造菜单与快捷栏、FGU-08 建造模式 HUD；
    /// FG00 B01/B02/B05/B06/B12/B13/B15/B16/B22）：家园建造模式的入口按钮、建造菜单、建造模式 HUD 与底部快捷栏（UI Toolkit）。
    /// - 入口：家园战略视角下左侧“建造（B）”按钮（发现性，B01）；按键同样可进入（B02，可重绑）。
    /// - 建造菜单：十二个分类页签（写明条目数，空分类有空状态说明，B12）；顶部搜索框按名称与用途跨分类搜索（打字不触发快捷键）；
    ///   条目写明成本与工期 / 每格成本、已有数量 / 上限，未解锁写明解锁条件（灰色 + 文字，B06/B15）；悬停说明用途、端口、快捷栏用法。
    /// - 建造模式 HUD：模式（拆除 / 搬迁）、旋转 / 拆除 / 搬迁 / 格线开关按钮（写明当前按键）、按当前状态的操作提示、成本与库存（不够时写还差多少）、
    ///   拖拽时的长度与总成本、框选时的建筑数、状态行（虚影校验原因、只警告的提示、最近一次操作的结果）、撤销提示。
    /// - 快捷栏：底部 10 格；把菜单条目拖到格子上（或选中后点空格子）放入，左键 / 快捷键选取，右键清空；内容进存档。
    /// - 只读 <see cref="HomeValleyBuildMode.Current"/> / <see cref="BuildCatalog"/>；刷新键不变的帧 O(1)（B18）。
    /// </summary>
    public sealed class BuildModeHudUIToolkit : UiKitPanelHost
    {
        public const int Order = 30030;

        public static BuildModeHudUIToolkit Instance { get; private set; }

        private Button _entry;
        private VisualElement _panel;
        private Label _title;
        private Label _mode;
        private Button _close;
        private VisualElement _categories;
        private Label _caption;
        private VisualElement _list;
        private Label _empty;
        private Button _rotate;
        private Button _demolish;
        private Button _relocate;
        private Button _gridToggle;
        private Button _prioritize;
        private Button _queue;
        private Button _clearBelt;
        private Label _hint;
        private Label _cost;
        private Label _dragInfo;
        private Label _status;
        private Label _placeholder;
        private Label _undoHint;
        private Label _generating;
        private VisualElement _hotbar;
        private UiSearchBox _search;
        private readonly List<Button> _items = new List<Button>();
        private readonly List<string> _itemIds = new List<string>();
        private readonly List<Button> _catButtons = new List<Button>();
        private readonly List<string> _catIds = new List<string>();
        private readonly List<Button> _slots = new List<Button>();
        private readonly List<BuildEntry> _entries = new List<BuildEntry>();
        private string _lastKey;
        private string _category;
        private string _searchText = string.Empty;
        private int _viewRevision;
        private string _dragEntryId;
        private int _dropSlot = -1;

        protected override string UxmlLocation => "BuildModeHud";
        protected override int SortingOrder => Order;

        /// <summary>自检可读：当前列表条目数、入口与面板可见性、状态行文字等。</summary>
        public int ItemCount => _itemIds.Count;
        public bool EntryVisible => _entry != null && !_entry.ClassListContains("uk-hidden");
        public bool PanelVisible => _panel != null && !_panel.ClassListContains("uk-hidden");
        public bool HotbarVisible => _hotbar != null && !_hotbar.ClassListContains("uk-hidden");
        public string StatusLabelText => _status?.text ?? string.Empty;
        public string CostLabelText => _cost != null && !_cost.ClassListContains("uk-hidden") ? _cost.text : string.Empty;
        public string DragInfoText => _dragInfo != null && !_dragInfo.ClassListContains("uk-hidden") ? _dragInfo.text : string.Empty;
        public string CaptionText => _caption?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string ModeText => _mode?.text ?? string.Empty;
        public string GridToggleText => _gridToggle?.text ?? string.Empty;
        public string UndoHintText => _undoHint?.text ?? string.Empty;
        public string SelectedCategoryId => _category;
        public int CategoryCount => _catIds.Count;
        public string ItemId(int index) => index >= 0 && index < _itemIds.Count ? _itemIds[index] : null;
        public string ItemText(int index) => index >= 0 && index < _itemIds.Count ? _items[index].text : string.Empty;
        public string CategoryText(int index) => index >= 0 && index < _catButtons.Count ? _catButtons[index].text : string.Empty;
        public string HotbarSlotText(int slot) => slot >= 0 && slot < _slots.Count ? _slots[slot].text : string.Empty;
        /// <summary>FG0-ARCH-05：“正在生成地形”提示（镜头周围有区块还没生成好时可见）。</summary>
        public bool GeneratingVisible => _generating != null && !_generating.ClassListContains("uk-hidden");
        public string GeneratingLabelText => _generating?.text ?? string.Empty;

        private void Awake()
        {
            Instance = this;
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
        }

        public void BindView(VisualElement root)
        {
            _entry = root.Q<Button>("BuildEntryButton");
            _panel = root.Q<VisualElement>("BuildPanel");
            _title = root.Q<Label>("BuildTitle");
            _mode = root.Q<Label>("BuildMode");
            _close = root.Q<Button>("BuildClose");
            _categories = root.Q<VisualElement>("BuildCategories");
            _caption = root.Q<Label>("BuildListCaption");
            _list = root.Q<VisualElement>("BuildList");
            _empty = root.Q<Label>("BuildEmpty");
            _rotate = root.Q<Button>("BuildRotate");
            _demolish = root.Q<Button>("BuildDemolish");
            _relocate = root.Q<Button>("BuildRelocate");
            _gridToggle = root.Q<Button>("BuildGridToggle");
            _prioritize = root.Q<Button>("BuildPrioritize");
            _queue = root.Q<Button>("BuildQueue");
            _clearBelt = root.Q<Button>("BuildClearBelt");
            _hint = root.Q<Label>("BuildHint");
            _cost = root.Q<Label>("BuildCost");
            _dragInfo = root.Q<Label>("BuildDragInfo");
            _status = root.Q<Label>("BuildStatus");
            _placeholder = root.Q<Label>("BuildPlaceholder");
            _undoHint = root.Q<Label>("BuildUndoHint");
            _generating = root.Q<Label>("BuildGenerating");
            _hotbar = root.Q<VisualElement>("BuildHotbar");
            _items.Clear();
            _itemIds.Clear();
            _catButtons.Clear();
            _catIds.Clear();
            _slots.Clear();

            _entry.clicked += () => HomeValleyBuildMode.Current?.Open();
            _close.clicked += () => HomeValleyBuildMode.Current?.Close();
            _rotate.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                if (m == null)
                {
                    return;
                }
                if (m.SelectedEntryId != null || m.CarryBuildingId != null)
                {
                    m.RotateGhost();
                }
                else
                {
                    m.RotateHovered(CampaignSession.Current);
                }
            };
            _demolish.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetDemolishMode(!m.DemolishMode);
            };
            _relocate.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetRelocateMode(!m.RelocateMode);
            };
            _gridToggle.clicked += () => HomeValleyBuildMode.Current?.ToggleGridLines();
            _prioritize.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetPrioritizeMode(!m.PrioritizeMode);
            };
            _queue.clicked += ConstructionQueuePanelUIToolkit.Toggle;
            _clearBelt.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetClearMode(!m.ClearMode);
            };
            _search = new UiSearchBox(root.Q<TextField>("BuildSearch"), root.Q<Label>("BuildSearchPlaceholder"), root.Q<Button>("BuildSearchClear"),
                "ui.build.search_placeholder", text =>
                {
                    _searchText = text ?? string.Empty;
                    _viewRevision++;
                    _lastKey = null;
                });

            for (int i = 0; i < BuildCatalog.HotbarSlots; i++)
            {
                int slot = i;
                Button b = root.Q<Button>("HotbarSlot" + i);
                if (b == null)
                {
                    continue;
                }
                _slots.Add(b);
                b.clicked += () => ClickSlot(slot);
                b.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 1)
                    {
                        ClearSlot(slot);
                        e.StopPropagation();
                    }
                }, TrickleDown.TrickleDown);
                UiTooltip.Attach(b, () => SlotTooltip(slot));
            }
            // 条目拖到快捷栏：按下条目时记下它，松开时看指针落在哪个格子上（条目按钮会捕获指针，所以在根上用 TrickleDown 接收移动与松开）。
            root.RegisterCallback<PointerMoveEvent>(OnRootPointerMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnRootPointerUp, TrickleDown.TrickleDown);

            UiTooltip.Attach(_entry, () => new TooltipContent
            {
                Title = GameText.Get("ui.build.title"),
                Body = GameText.Format("ui.build.hint_idle", InputDisplay.ForAction(GameActionId.Rotate), InputDisplay.ForAction(GameActionId.DemolishMode)),
                Shortcut = GameActionId.OpenBuildMenu,
            });
            UiTooltip.Attach(_rotate, () => new TooltipContent { Title = GameText.Get("input.action.rotate.name"), Shortcut = GameActionId.Rotate });
            UiTooltip.Attach(_demolish, () => new TooltipContent { Title = GameText.Get("input.action.demolish_mode.name"), Body = GameText.Get("ui.build.hint_demolish_box"), Shortcut = GameActionId.DemolishMode });
            UiTooltip.Attach(_relocate, () => new TooltipContent { Title = GameText.Get("input.action.relocate_mode.name"), Body = GameText.Format("ui.build.hint_relocate", InputDisplay.ForAction(GameActionId.Rotate)), Shortcut = GameActionId.RelocateMode });
            UiTooltip.Attach(_gridToggle, () => new TooltipContent { Title = GameText.Get("input.action.toggle_grid_lines.name"), Shortcut = GameActionId.ToggleGridLines });
            UiTooltip.Attach(_prioritize, () => new TooltipContent { Title = GameText.Get("input.action.prioritize_area.name"), Body = GameText.Get("ui.build.prioritize_mode"), Shortcut = GameActionId.PrioritizeArea });
            UiTooltip.Attach(_clearBelt, () => new TooltipContent { Title = GameText.Get("input.action.clear_belt_mode.name"), Body = GameText.Get("ui.build.clear_mode"),
                Shortcut = GameActionId.ClearBeltMode, CodexEntryId = "codex.logistics.belt" });
            UiTooltip.Attach(_queue, () => new TooltipContent { Title = GameText.Get("input.action.construction_queue.name"),
                Body = InputDisplay.ExpandActionTokens(GameText.Get("build.queue.hint")), Shortcut = GameActionId.ConstructionQueue, CodexEntryId = "codex.build.construction" });
            _lastKey = null;
        }

        private void Update()
        {
            if (Root == null)
            {
                return;
            }
            Refresh();
        }

        // ── 自检也走的界面入口 ─────────────────────────────────────────────────────

        /// <summary>点分类页签（清掉搜索，回到分类浏览）。</summary>
        public void SelectCategory(string categoryId)
        {
            _category = categoryId;
            if (!string.IsNullOrEmpty(_searchText))
            {
                _search?.SetText(string.Empty);
                _searchText = string.Empty;
            }
            _viewRevision++;
            _lastKey = null;
            Refresh();
        }

        /// <summary>搜索框输入（与在框里打字同一回调）。</summary>
        public void SetSearch(string text)
        {
            if (_search != null)
            {
                _search.SetText(text ?? string.Empty);
            }
            _searchText = text ?? string.Empty;
            _viewRevision++;
            _lastKey = null;
            Refresh();
        }

        /// <summary>把条目拖到快捷栏第 <paramref name="slot"/> 格（与真实拖放的松开同一路径）。</summary>
        public bool DragEntryToSlot(string entryId, int slot)
        {
            _dragEntryId = entryId;
            return DropOnSlot(slot);
        }

        /// <summary>点快捷栏格子：有选中的条目且格子空着 → 放进去；否则选取格子里的条目（空格子给提示）。</summary>
        public void ClickSlot(int slot)
        {
            HomeValleyBuildMode m = HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            if (m == null || state == null)
            {
                return;
            }
            if (BuildCatalog.HotbarId(state, slot) == null && m.IsOpen && m.SelectedEntryId != null)
            {
                m.AssignHotbar(state, slot, m.SelectedEntryId);
            }
            else
            {
                m.PressHotbar(state, slot);
            }
            _lastKey = null;
        }

        public void ClearSlot(int slot)
        {
            HomeValleyBuildMode m = HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            if (m != null && state != null && BuildCatalog.HotbarId(state, slot) != null)
            {
                m.AssignHotbar(state, slot, null);
                _lastKey = null;
            }
        }

        private bool DropOnSlot(int slot)
        {
            string id = _dragEntryId;
            _dragEntryId = null;
            SetDropHighlight(-1);
            HomeValleyBuildMode m = HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            if (id == null || m == null || state == null || slot < 0)
            {
                return false;
            }
            bool ok = m.AssignHotbar(state, slot, id);
            _lastKey = null;
            return ok;
        }

        private int SlotAt(Vector2 panelPosition)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].worldBound.Contains(panelPosition))
                {
                    return i;
                }
            }
            return -1;
        }

        private void OnRootPointerMove(PointerMoveEvent e)
        {
            if (_dragEntryId != null)
            {
                SetDropHighlight(SlotAt(e.position));
            }
        }

        private void OnRootPointerUp(PointerUpEvent e)
        {
            if (_dragEntryId == null)
            {
                return;
            }
            int slot = SlotAt(e.position);
            if (slot >= 0)
            {
                DropOnSlot(slot);
            }
            else
            {
                _dragEntryId = null;
                SetDropHighlight(-1);
            }
        }

        private void SetDropHighlight(int slot)
        {
            if (_dropSlot == slot)
            {
                return;
            }
            _dropSlot = slot;
            for (int i = 0; i < _slots.Count; i++)
            {
                _slots[i].EnableInClassList("bm-slot-drop", i == slot);
            }
        }

        // ── 刷新 ────────────────────────────────────────────────────────────────

        /// <summary>按当前建造模式刷新（自检可直接调用）。</summary>
        public void Refresh()
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            CampaignState state = CampaignSession.Current;
            // FG0-ARCH-01：家园在远征期间照常运行（建造模式仍绑定着），但镜头不在家园时不给建造入口——建造只对眼前的家园。
            bool homeObserved = Stage.GameRoot.HomeValley != null && Stage.GameRoot.HomeValley.IsActive;
            bool available = mode != null && state != null && homeObserved && InputRouter.Scope == InputScope.Strategy && !InputRouter.ModalUiOpen;
            bool open = mode != null && mode.IsOpen;
            SetVisible(_entry, available && !open);
            SetVisible(_panel, open);
            SetVisible(_hotbar, (available || open) && _slots.Count > 0);
            if (mode == null || state == null)
            {
                _lastKey = null;
                return;
            }

            string key = string.Concat(mode.Revision.ToString(), "|", Mathf.FloorToInt(state.Scrap).ToString(), "|",
                (state.BuildingRecords?.Length ?? 0).ToString(), "|", ((int)GameText.Language).ToString(), "|", GameSettings.Revision.ToString(),
                "|", open ? "1" : "0", "|", BuildCatalog.Revision.ToString(), "|", _viewRevision.ToString(), "|", available ? "1" : "0",
                // FG3-LOG-02：施工状态（虚影进度、缺料）变化时也刷新；每 0.25 秒一档，状态行的“施工中 N%”跟得上。
                "|", HomeValleyConstruction.Revision.ToString(), "|", open ? Mathf.FloorToInt(Time.unscaledTime * 4f).ToString() : "0");
            if (key == _lastKey)
            {
                return;
            }
            _lastKey = key;

            _entry.text = GameText.Format("ui.build.entry", InputDisplay.ForAction(GameActionId.OpenBuildMenu));
            RefreshHotbar(mode, state);
            if (!open)
            {
                return;
            }
            _title.text = GameText.Get("ui.build.title");
            _mode.text = mode.DemolishMode ? GameText.Format("ui.build.demolish", InputDisplay.ForAction(GameActionId.DemolishMode))
                : mode.RelocateMode ? GameText.Get("ui.build.relocate_mode")
                : mode.PrioritizeMode ? GameText.Get("ui.build.prioritize_mode")
                : mode.ClearMode ? GameText.Format("ui.build.btn_clear", InputDisplay.ForAction(GameActionId.ClearBeltMode))
                : string.Empty;
            _close.text = GameText.Get("ui.build.exit");
            _rotate.text = GameText.Format("ui.build.rotate", InputDisplay.ForAction(GameActionId.Rotate));
            _demolish.text = GameText.Format("ui.build.demolish", InputDisplay.ForAction(GameActionId.DemolishMode));
            _demolish.EnableInClassList("bm-tool-active", mode.DemolishMode);
            _relocate.text = GameText.Format("ui.build.relocate", InputDisplay.ForAction(GameActionId.RelocateMode));
            _relocate.EnableInClassList("bm-tool-active", mode.RelocateMode);
            _gridToggle.text = GameText.Format(GameSettings.BuildGridLinesEnabled ? "ui.build.grid_on" : "ui.build.grid_off",
                InputDisplay.ForAction(GameActionId.ToggleGridLines));
            _gridToggle.EnableInClassList("bm-tool-active", GameSettings.BuildGridLinesEnabled);
            _prioritize.text = GameText.Format("ui.build.btn_prioritize", InputDisplay.ForAction(GameActionId.PrioritizeArea));
            _prioritize.EnableInClassList("bm-tool-active", mode.PrioritizeMode);
            _queue.text = GameText.Format("ui.build.btn_queue", InputDisplay.ForAction(GameActionId.ConstructionQueue));
            _clearBelt.text = GameText.Format("ui.build.btn_clear", InputDisplay.ForAction(GameActionId.ClearBeltMode));
            _clearBelt.EnableInClassList("bm-tool-active", mode.ClearMode);
            _placeholder.text = GameText.Get("ui.build.placeholder_note");
            _undoHint.text = GameText.Format("ui.build.undo_hint", InputDisplay.ForAction(GameActionId.Undo), InputDisplay.ForAction(GameActionId.Redo));

            if (_category == null || !GridContent.TryGetCategory(_category, out _))
            {
                _category = BuildCatalog.FirstNonEmptyCategory();
            }
            SyncCategories();
            BuildCatalog.List(_category, _searchText, _entries);
            SyncItems(mode, state);
            bool searching = !string.IsNullOrWhiteSpace(_searchText);
            string categoryName = GridContent.TryGetCategory(_category, out BuildCategory cat) ? GameText.Get(cat.NameKey) : string.Empty;
            _caption.text = searching ? GameText.Format("ui.build.search_results", _searchText.Trim(), _entries.Count) : categoryName;
            SetVisible(_empty, _entries.Count == 0);
            _empty.text = searching ? GameText.Format("ui.build.search_empty", _searchText.Trim()) : GameText.Format("ui.build.category_empty", categoryName);

            _hint.text = HintFor(mode);
            RefreshCost(mode, state);
            RefreshDrag(mode);
            RefreshStatus(mode, state);

            int generating = mode.GeneratingChunkCount;
            SetVisible(_generating, generating > 0);
            if (_generating != null)
            {
                _generating.text = generating > 0 ? GameText.Format("ui.world.generating", generating) : string.Empty;
            }
        }

        private static string HintFor(HomeValleyBuildMode mode)
        {
            string rotate = InputDisplay.ForAction(GameActionId.Rotate);
            if (mode.DemolishMode)
            {
                return GameText.Get("ui.build.hint_demolish_box");
            }
            if (mode.RelocateMode)
            {
                return GameText.Format("ui.build.hint_relocate", rotate);
            }
            if (mode.PrioritizeMode)
            {
                return GameText.Get("ui.build.prioritize_mode");
            }
            if (mode.ClearMode)
            {
                return GameText.Get("ui.build.clear_mode");
            }
            if (mode.SelectedToolId != null)
            {
                return GameText.Format("ui.build.hint_tool", rotate);
            }
            if (mode.SelectedTypeId != null)
            {
                return GameText.Format("ui.build.hint_place", rotate);
            }
            return GameText.Format("ui.build.hint_idle", rotate, InputDisplay.ForAction(GameActionId.DemolishMode));
        }

        private void RefreshCost(HomeValleyBuildMode mode, CampaignState state)
        {
            int cost = -1;
            if (mode.SelectedTypeId != null && HomeValleyLayout.BuildProfile.TryGetValue(mode.SelectedTypeId, out (int ScrapCost, float Seconds) p))
            {
                cost = p.ScrapCost;
            }
            else if (mode.SelectedToolId != null && GridContent.TryGetTool(mode.SelectedToolId, out BuildTool tool))
            {
                cost = tool.ScrapPerCell;
            }
            if (cost < 0)
            {
                SetVisible(_cost, false);
                return;
            }
            SetVisible(_cost, true);
            int stock = state.Scrap;
            bool short_ = stock < cost;
            _cost.text = short_ ? GameText.Format("ui.build.cost_short", cost, stock, cost - stock) : GameText.Format("ui.build.cost_line", cost, stock);
            _cost.EnableInClassList("bm-cost-short", short_);
        }

        private void RefreshDrag(HomeValleyBuildMode mode)
        {
            BeltPathPlan belt = mode.BeltPlan;
            DemolishBoxPlan box = mode.BoxPlan;
            if (belt != null)
            {
                SetVisible(_dragInfo, true);
                int shortBy = belt.TotalCost - belt.Stock;
                _dragInfo.text = shortBy > 0
                    ? GameText.Format("ui.build.drag_info_short", belt.Length, belt.TotalCost, belt.Stock, shortBy)
                    : GameText.Format("ui.build.drag_info", belt.Length, belt.TotalCost, belt.Stock);
            }
            else if (box != null)
            {
                SetVisible(_dragInfo, true);
                _dragInfo.text = GameText.Format("ui.build.box_info", box.Max.X - box.Min.X + 1, box.Max.Y - box.Min.Y + 1, box.BuildingCount, box.Belts.Count);
            }
            else if (mode.ClearPlan != null)
            {
                SetVisible(_dragInfo, true);
                BeltClearPlan cp = mode.ClearPlan;
                _dragInfo.text = GameText.Format("ui.build.clear_box", cp.Max.X - cp.Min.X + 1, cp.Max.Y - cp.Min.Y + 1, cp.Cells.Count, cp.Items);
            }
            else if (mode.Drag == HomeValleyBuildMode.DragKind.PrioritizeBox && mode.PrioritizeBoxCount >= 0)
            {
                SetVisible(_dragInfo, true);
                _dragInfo.text = GameText.Format("ui.build.prioritize_box", mode.PrioritizeBoxMax.X - mode.PrioritizeBoxMin.X + 1,
                    mode.PrioritizeBoxMax.Y - mode.PrioritizeBoxMin.Y + 1, mode.PrioritizeBoxCount);
            }
            else
            {
                SetVisible(_dragInfo, false);
            }
        }

        private void RefreshStatus(HomeValleyBuildMode mode, CampaignState state)
        {
            string status = mode.StatusText;
            bool error = mode.StatusIsError;
            bool warning = false;
            GridPlacementResult preview = mode.Preview;
            BeltPathPlan belt = mode.BeltPlan;
            if (belt != null)
            {
                if (!belt.Ok)
                {
                    status = GameText.Format("ui.build.invalid", belt.Describe());
                    error = true;
                }
            }
            else if (preview != null)
            {
                string facing = GameText.Get(GridMath.DirTextKey(GridMath.FacingOf(preview.Rotation)));
                var ports = new List<PortPlacement>(4);
                HomeGridService.PortsFor(preview.TypeId, preview.Pivot, preview.Rotation, ports);
                string failKey = mode.CarryBuildingId != null || mode.Drag == HomeValleyBuildMode.DragKind.Relocate ? "ui.build.invalid_relocate" : "ui.build.invalid";
                status = (preview.Ok
                    ? GameText.Format("ui.build.valid", HomeGridService.DisplayName(preview.TypeId), facing)
                    : GameText.Format(failKey, preview.Describe())) + "\n" + HomeValleyBuildMode.DescribePorts(ports);
                error = !preview.Ok;
                // 只警告不阻止的提示（机器将无法到达某建筑、轻度污染）另起一行，用警告样式（不只靠颜色：文字以“注意：”开头）。
                foreach (string w in preview.Warnings)
                {
                    status += "\n" + w;
                }
                warning = preview.Ok && preview.Warnings.Count > 0;
            }
            else if (string.IsNullOrEmpty(status) && mode.HoverBuildingId != null)
            {
                BuildingRecord hovered = HomeGridService.FindBuilding(state, mode.HoverBuildingId);
                status = hovered != null ? HomeValleyBuildMode.DescribeBuilding(state, hovered) : string.Empty;
                // FG3-LOG-03：有端口的建筑提示“点一下查看端口、设置输出过滤”。
                if (hovered != null && GridContent.PortsOf(hovered.BuildingTypeId).Count > 0 && !mode.DemolishMode && !mode.RelocateMode)
                {
                    status += "\n" + GameText.Get("logistics.port.open_hint");
                }
                // FG3-LOG-02：指着施工虚影时，另起两行写施工状态（缺什么、进度、优先级），与悬停提示同一写法。
                if (mode.HasHover && HomeValleyConstruction.TryDescribeSite(state, mode.HoverCell, out _, out string site))
                {
                    status += "\n" + site;
                }
            }
            else if (string.IsNullOrEmpty(status) && mode.HasHover && HomeValleyConstruction.TryDescribeSite(state, mode.HoverCell, out string beltTitle, out string beltSite))
            {
                status = beltTitle + "\n" + beltSite; // 规划中的传送带格。
            }
            else if ((string.IsNullOrEmpty(status) || mode.ClearMode) && mode.HasHover
                     && Campaign.Logistics.BeltNetworkService.TryDescribeHover(state, mode.HoverCell, out string hoverTitle, out string hoverBody))
            {
                // FG3-LOG-03（FGR-LOG-081）：建造模式里指着已建成的传送带，状态行写悬停读数（战略视角由世界悬停提示显示同一份）。
                status = (string.IsNullOrEmpty(status) ? string.Empty : status + "\n") + hoverTitle + "\n" + hoverBody;
            }
            _status.text = status;
            _status.EnableInClassList("bm-status-error", error);
            _status.EnableInClassList("bm-status-warning", warning);
        }

        private void SyncCategories()
        {
            IReadOnlyList<BuildCategory> cats = GridContent.Categories;
            while (_catButtons.Count < cats.Count)
            {
                int index = _catButtons.Count;
                var b = new Button { name = "BuildCat" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("bm-cat");
                b.clicked += () =>
                {
                    if (index < _catIds.Count)
                    {
                        SelectCategory(_catIds[index]);
                    }
                };
                _categories.Add(b);
                _catButtons.Add(b);
            }
            _catIds.Clear();
            bool searching = !string.IsNullOrWhiteSpace(_searchText);
            for (int i = 0; i < _catButtons.Count; i++)
            {
                Button b = _catButtons[i];
                if (i >= cats.Count)
                {
                    SetVisible(b, false);
                    continue;
                }
                BuildCategory c = cats[i];
                _catIds.Add(c.Id);
                SetVisible(b, true);
                int n = BuildCatalog.CountInCategory(c.Id);
                b.text = GameText.Format("build.category.tab", GameText.Get(c.NameKey), n);
                b.EnableInClassList("bm-cat-selected", !searching && c.Id == _category);
                b.EnableInClassList("bm-cat-empty", n == 0);
            }
        }

        private void SyncItems(HomeValleyBuildMode mode, CampaignState state)
        {
            while (_items.Count < _entries.Count)
            {
                int index = _items.Count;
                var b = new Button { name = "BuildItem" + index };
                b.AddToClassList("mw-btn");
                b.AddToClassList("bm-item");
                b.clicked += () =>
                {
                    if (index < _itemIds.Count)
                    {
                        HomeValleyBuildMode.Current?.Select(_itemIds[index]);
                    }
                };
                b.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 0 && index < _itemIds.Count)
                    {
                        _dragEntryId = _itemIds[index];
                    }
                }, TrickleDown.TrickleDown);
                UiTooltip.Attach(b, () => ItemTooltip(index));
                _list.Add(b);
                _items.Add(b);
            }
            _itemIds.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                Button b = _items[i];
                if (i >= _entries.Count)
                {
                    SetVisible(b, false);
                    continue;
                }
                BuildEntry e = _entries[i];
                _itemIds.Add(e.Id);
                SetVisible(b, true);
                bool unlocked = BuildCatalog.IsUnlocked(state, e);
                string line2;
                if (!unlocked)
                {
                    line2 = GameText.Format("ui.build.locked_tip", GameText.Get(e.UnlockHintKey));
                }
                else if (e.IsTool)
                {
                    line2 = GameText.Format("ui.build.tool_cost", e.Tool.ScrapPerCell);
                }
                else
                {
                    string cost = HomeValleyLayout.BuildProfile.TryGetValue(e.Id, out (int ScrapCost, float Seconds) p)
                        ? GameText.Format("ui.build.cost", p.ScrapCost, Mathf.RoundToInt(p.Seconds))
                        : string.Empty;
                    int count = HomeGridService.CountOfType(state, e.Id);
                    string countText = e.Building.MaxCount > 0 ? GameText.Format("ui.build.count_limited", count, e.Building.MaxCount) : GameText.Format("ui.build.count_unlimited", count);
                    line2 = cost + "  " + countText;
                }
                b.text = e.Name + "\n" + line2;
                b.EnableInClassList("bm-item-locked", !unlocked);
                b.EnableInClassList("bm-item-selected", mode.SelectedEntryId == e.Id);
            }
        }

        private void RefreshHotbar(HomeValleyBuildMode mode, CampaignState state)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                Button b = _slots[i];
                string key = InputDisplay.ForAction(HotbarAction(i));
                BuildEntry e = BuildCatalog.HotbarEntry(state, i);
                b.text = e != null ? GameText.Format("ui.hotbar.slot", key, e.Name) : GameText.Format("ui.hotbar.slot_empty", key);
                b.EnableInClassList("bm-slot-empty", e == null);
                b.EnableInClassList("bm-slot-selected", e != null && mode.IsOpen && mode.SelectedEntryId == e.Id);
                b.EnableInClassList("bm-item-locked", e != null && !BuildCatalog.IsUnlocked(state, e));
            }
        }

        private static GameActionId HotbarAction(int slot) => (GameActionId)((int)GameActionId.Hotbar1 + slot);

        private TooltipContent SlotTooltip(int slot)
        {
            CampaignState state = CampaignSession.Current;
            BuildEntry e = BuildCatalog.HotbarEntry(state, slot);
            string key = InputDisplay.ForAction(HotbarAction(slot));
            return e == null
                ? new TooltipContent { Title = InputActionCatalog.TryGet(HotbarAction(slot), out InputActionDef def) ? GameText.Get(def.NameKey) : key,
                    Body = GameText.Format("ui.hotbar.tip_empty", key), Shortcut = HotbarAction(slot) }
                : new TooltipContent { Title = e.Name, Body = GameText.Format("ui.hotbar.tip_filled", e.Name, key), Shortcut = HotbarAction(slot) };
        }

        private TooltipContent ItemTooltip(int index)
        {
            if (index >= _itemIds.Count || !BuildCatalog.TryGet(_itemIds[index], out BuildEntry e))
            {
                return null;
            }
            CampaignState state = CampaignSession.Current;
            string body = GameText.Get(e.DescKey);
            if (state != null && !BuildCatalog.IsUnlocked(state, e))
            {
                body += "\n" + GameText.Format("ui.build.locked_tip", GameText.Get(e.UnlockHintKey));
            }
            if (!e.IsTool)
            {
                var ports = new List<PortPlacement>(4);
                HomeGridService.PortsFor(e.Id, new GridCell(0, 0), 0, ports);
                body += "\n" + HomeValleyBuildMode.DescribePorts(ports);
            }
            body += "\n" + GameText.Get("ui.build.item_tip_hotbar");
            return new TooltipContent { Title = e.Name, Body = body, Shortcut = GameActionId.Rotate };
        }

        private static void SetVisible(VisualElement e, bool visible)
        {
            if (e != null)
            {
                e.EnableInClassList("uk-hidden", !visible);
            }
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }
}
