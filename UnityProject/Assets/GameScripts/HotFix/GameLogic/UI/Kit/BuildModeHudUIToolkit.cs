using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
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

        /// <summary>
        /// FG3-E2E-01（FGJ-M1 / FGJ-M2 旅程抓到）：建造模式关着时，这份文档里只剩入口按钮与底部快捷栏——它们是 HUD，要排在所有窗口之下
        /// （叠加层 HUD 4 之上、家园装配站生产面板 6 / 蓝图编辑器 8 / 浮动窗口 33～30000 之下）。此前整份文档常驻 30030，
        /// 蓝图编辑器“保存”等落在快捷栏位置的按钮被快捷栏盖住、点不到。建造模式开着时回到建造栏层 <see cref="Order"/>。
        /// </summary>
        public const int ClosedHudOrder = 5;

        /// <summary>建造模式开 / 关时这份文档的分层（自检读）。</summary>
        public static int LayerFor(bool buildModeOpen) => buildModeOpen ? Order : ClosedHudOrder;

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
        private Button _power;
        private Button _clearBelt;
        // FG3-LOG-07：规划工具按钮
        private Button _copy;
        private Button _paste;
        private Button _layouts;
        private Button _upgrade;
        private Button _eyedrop;
        private Button _settings;
        private Button _undo;
        private Button _redo;
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
        /// <summary>FG3-LOG-04：提示行（选中分流器 / 合流器 / 地下传送带时各有自己的操作说明；自检读）。</summary>
        public string HintLabelText => _hint?.text ?? string.Empty;
        public string CostLabelText => _cost != null && !_cost.ClassListContains("uk-hidden") ? _cost.text : string.Empty;
        public string DragInfoText => _dragInfo != null && !_dragInfo.ClassListContains("uk-hidden") ? _dragInfo.text : string.Empty;
        public string CaptionText => _caption?.text ?? string.Empty;
        public string EmptyText => _empty != null && !_empty.ClassListContains("uk-hidden") ? _empty.text : string.Empty;
        public string ModeText => _mode?.text ?? string.Empty;
        public string GridToggleText => _gridToggle?.text ?? string.Empty;
        public string UndoHintText => _undoHint?.text ?? string.Empty;
        /// <summary>FG3-LOG-07：规划工具按钮（自检 / 冒烟点它们）。</summary>
        public Button CopyButton => _copy;
        public Button PasteButton => _paste;
        public Button LayoutsButton => _layouts;
        public Button UpgradeButton => _upgrade;
        public Button EyedropButton => _eyedrop;
        public Button SettingsButton => _settings;
        public Button UndoButton => _undo;
        public Button RedoButton => _redo;
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
            _power = root.Q<Button>("BuildPowerGrid");
            _clearBelt = root.Q<Button>("BuildClearBelt");
            _copy = root.Q<Button>("BuildCopy");
            _paste = root.Q<Button>("BuildPaste");
            _layouts = root.Q<Button>("BuildLayouts");
            _upgrade = root.Q<Button>("BuildUpgrade");
            _eyedrop = root.Q<Button>("BuildEyedrop");
            _settings = root.Q<Button>("BuildSettings");
            _undo = root.Q<Button>("BuildUndo");
            _redo = root.Q<Button>("BuildRedo");
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
            _power.clicked += PowerPanelUIToolkit.Toggle; // FG3-LOG-06：电网面板（按钮与 Alt+G 同一路径）。
            _clearBelt.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetClearMode(!m.ClearMode);
            };
            // FG3-LOG-07：规划工具（按钮与快捷键同一路径）。
            _copy.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetCopyMode(!m.CopyMode);
            };
            _paste.clicked += () => HomeValleyBuildMode.Current?.StartPaste(CampaignSession.Current, HomeValleyBuildMode.Clipboard, null);
            _layouts.clicked += LayoutLibraryPanelUIToolkit.Toggle;
            _upgrade.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetUpgradeMode(!m.UpgradeMode);
            };
            _eyedrop.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                if (m != null && m.HasHover)
                {
                    m.Eyedrop(CampaignSession.Current, m.HoverCell); // 按钮：吸最后指着的那一格（按键 Q 更顺手，按钮给发现性）
                }
            };
            _settings.clicked += () =>
            {
                HomeValleyBuildMode m = HomeValleyBuildMode.Current;
                m?.SetSettingsMode(!m.SettingsMode);
            };
            _undo.clicked += () => HomeValleyBuildMode.Current?.Undo(CampaignSession.Current);
            _redo.clicked += () => HomeValleyBuildMode.Current?.Redo(CampaignSession.Current);
            UiTooltip.Attach(_copy, () => new TooltipContent { Title = GameText.Get("input.action.copy.name"), Body = GameText.Format("plan.copy.mode", InputDisplay.ForAction(GameActionId.LayoutLibrary)),
                Shortcut = GameActionId.Copy, CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_paste, () => new TooltipContent { Title = GameText.Get("input.action.paste.name"), Body = GameText.Get("plan.paste.tip"), Shortcut = GameActionId.Paste,
                CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_layouts, () => new TooltipContent { Title = GameText.Get("input.action.layout_library.name"), Body = GameText.Get("plan.library.tip"),
                Shortcut = GameActionId.LayoutLibrary, CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_upgrade, () => new TooltipContent { Title = GameText.Get("input.action.upgrade_plan.name"), Body = GameText.Get("plan.upgrade.mode"),
                Shortcut = GameActionId.UpgradePlan, CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_eyedrop, () => new TooltipContent { Title = GameText.Get("input.action.eyedropper.name"), Body = GameText.Get("plan.eyedrop.tip"),
                Shortcut = GameActionId.Eyedropper, CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_settings, () => new TooltipContent { Title = GameText.Get("input.action.copy_settings.name"),
                Body = GameText.Format("plan.settings.tip", InputDisplay.ForAction(GameActionId.CopySettings), InputDisplay.ForAction(GameActionId.PasteSettings)),
                Shortcut = GameActionId.CopySettings, CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_undo, () => new TooltipContent { Title = GameText.Get("input.action.undo.name"), Body = GameText.Get("plan.undo.tip"), Shortcut = GameActionId.Undo,
                CodexEntryId = "codex.build.planning" });
            UiTooltip.Attach(_redo, () => new TooltipContent { Title = GameText.Get("input.action.redo.name"), Body = GameText.Get("plan.undo.tip"), Shortcut = GameActionId.Redo });
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
            UiTooltip.Attach(_power, () => new TooltipContent { Title = GameText.Get("input.action.open_power_grid.name"), Body = GameText.Get("power.overlay.legend"),
                Shortcut = GameActionId.OpenPowerGrid, CodexEntryId = "codex.logistics.power" });
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
            int layer = LayerFor(open);
            if (Document != null && Document.sortingOrder != layer)
            {
                Document.sortingOrder = layer;
            }
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
                "|", HomeValleyConstruction.Revision.ToString(), "|", open ? Mathf.FloorToInt(Time.unscaledTime * 4f).ToString() : "0",
                // FG3-LOG-07：撤销栈 / 布局库变化时也刷新（撤销按钮写下一步是什么）。
                "|", PlanHistory.Revision.ToString(), "|", LayoutLibrary.Revision.ToString());
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
                : mode.CopyMode ? GameText.Get("plan.mode.copy")
                : mode.PasteMode ? GameText.Format("plan.mode.paste", PlanEntries.CountOf(mode.PasteSource))
                : mode.UpgradeMode ? GameText.Get("plan.mode.upgrade")
                : mode.SettingsMode ? GameText.Get("plan.mode.settings")
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
            _power.text = GameText.Format("power.panel.hud_button", InputDisplay.ForAction(GameActionId.OpenPowerGrid));
            _clearBelt.text = GameText.Format("ui.build.btn_clear", InputDisplay.ForAction(GameActionId.ClearBeltMode));
            _clearBelt.EnableInClassList("bm-tool-active", mode.ClearMode);
            _placeholder.text = GameText.Get("ui.build.placeholder_note");
            RefreshPlanTools(mode, state);

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

        /// <summary>FG3-LOG-07：规划工具按钮（写明按键、当前模式高亮、没有可撤销 / 可重做时灰掉）与撤销提示行（下一步撤销 / 重做的是什么）。</summary>
        private void RefreshPlanTools(HomeValleyBuildMode mode, CampaignState state)
        {
            _copy.text = GameText.Format("plan.btn.copy", InputDisplay.ForAction(GameActionId.Copy));
            _copy.EnableInClassList("bm-tool-active", mode.CopyMode);
            _paste.text = GameText.Format("plan.btn.paste", InputDisplay.ForAction(GameActionId.Paste));
            _paste.EnableInClassList("bm-tool-active", mode.PasteMode);
            _paste.SetEnabled(PlanEntries.CountOf(HomeValleyBuildMode.Clipboard) > 0 || mode.PasteMode);
            _layouts.text = GameText.Format("plan.btn.layouts", InputDisplay.ForAction(GameActionId.LayoutLibrary), LayoutLibrary.Count);
            _layouts.EnableInClassList("bm-tool-active", LayoutLibraryPanelUIToolkit.IsOpen);
            _upgrade.text = GameText.Format("plan.btn.upgrade", InputDisplay.ForAction(GameActionId.UpgradePlan));
            _upgrade.EnableInClassList("bm-tool-active", mode.UpgradeMode);
            _eyedrop.text = GameText.Format("plan.btn.eyedrop", InputDisplay.ForAction(GameActionId.Eyedropper));
            _settings.text = GameText.Format("plan.btn.settings", InputDisplay.ForAction(GameActionId.CopySettings), InputDisplay.ForAction(GameActionId.PasteSettings));
            _settings.EnableInClassList("bm-tool-active", mode.SettingsMode);
            PlanStepKind nextUndo = PlanHistory.PeekUndo(state);
            PlanStepKind nextRedo = PlanHistory.PeekRedo(state);
            _undo.text = GameText.Format("plan.btn.undo", InputDisplay.ForAction(GameActionId.Undo), PlanHistory.UndoSteps(state));
            _undo.SetEnabled(nextUndo != PlanStepKind.None);
            _redo.text = GameText.Format("plan.btn.redo", InputDisplay.ForAction(GameActionId.Redo), PlanHistory.RedoSteps(state));
            _redo.SetEnabled(nextRedo != PlanStepKind.None);
            _undoHint.text = GameText.Format("plan.undo.hint", InputDisplay.ForAction(GameActionId.Undo),
                nextUndo != PlanStepKind.None ? PlanHistory.StepName(nextUndo) : GameText.Get("plan.undo.nothing"), InputDisplay.ForAction(GameActionId.Redo),
                nextRedo != PlanStepKind.None ? PlanHistory.StepName(nextRedo) : GameText.Get("plan.undo.nothing"), PlanHistory.Depth);
        }

        private static string HintFor(HomeValleyBuildMode mode)
        {
            string rotate = InputDisplay.ForAction(GameActionId.Rotate);
            if (mode.PasteMode)
            {
                return GameText.Format("plan.hint.paste", rotate);
            }
            if (mode.CopyMode)
            {
                return GameText.Get("plan.hint.copy");
            }
            if (mode.UpgradeMode)
            {
                return GameText.Get("plan.hint.upgrade");
            }
            if (mode.SettingsMode)
            {
                return GameText.Get("plan.hint.settings");
            }
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
                // FG3-LOG-04：分流器 / 合流器单击放置、地下传送带从入口拖到出口，各有自己的提示。
                return GridContent.TryGetTool(mode.SelectedToolId, out BuildTool selected) ? ToolHint(selected, rotate) : GameText.Format("ui.build.hint_tool", rotate);
            }
            if (mode.SelectedTypeId != null)
            {
                return GameText.Format("ui.build.hint_place", rotate);
            }
            return GameText.Format("ui.build.hint_idle", rotate, InputDisplay.ForAction(GameActionId.DemolishMode));
        }

        /// <summary>FG3-LOG-04：选中工具时的操作提示（传送带拖拽 / 分流器、合流器单击 / 地下传送带从入口拖到出口）。</summary>
        public static string ToolHint(BuildTool tool, string rotate)
        {
            switch (tool.Kind)
            {
                case "splitter":
                    return GameText.Format("ui.build.hint_splitter", rotate);
                case "merger":
                    return GameText.Format("ui.build.hint_merger", rotate);
                case "underground":
                    return GameText.Format("ui.build.hint_underground", GameText.Get(tool.NameKey), BeltNetworkService.UndergroundSpan(tool.Tier));
                // FG3-LOG-05：管线拖拽 / 泵放在水源或油井上 / 储罐 / 阀门（旋转定流向）。
                case "pipe":
                    return GameText.Get("ui.build.hint_pipe");
                case "pump":
                    return GameText.Get("ui.build.hint_pump");
                case "tank":
                    return GameText.Get("ui.build.hint_tank");
                case "valve":
                    return GameText.Format("ui.build.hint_valve", rotate);
                // FG4-ECO-04（FG-GAP-082）：地下管线口单击放置，旋转定朝地下的方向。
                case "pipe_underground":
                    return GameText.Format("ui.build.hint_pipe_underground", rotate, GameText.Get(tool.NameKey), Campaign.Logistics.PipeNetworkService.UndergroundSpan(tool.Tier));
                default:
                    return GameText.Format("ui.build.hint_tool", rotate);
            }
        }

        /// <summary>FG3-LOG-04：建造菜单里工具条目的第二行（传送带每格 / 分流器、合流器每座 / 地下传送带每端与最大跨度）。</summary>
        public static string ToolCostLine(BuildTool tool)
        {
            switch (tool.Kind)
            {
                case "splitter":
                case "merger":
                case "pump":
                case "tank":
                case "valve":
                case "pipe_underground":
                    return GameText.Format("ui.build.tool_cost_node", tool.ScrapPerCell);
                case "underground":
                    return GameText.Format("ui.build.tool_cost_under", tool.ScrapPerCell, tool.ScrapPerCell * 2, BeltNetworkService.UndergroundSpan(tool.Tier));
                default:
                    return GameText.Format("ui.build.tool_cost", tool.ScrapPerCell);
            }
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
                cost = tool.Kind == "underground" ? tool.ScrapPerCell * 2 : tool.ScrapPerCell;
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
            // FG4-ECO-11（DEBT-FG3LOG02-02）：废料之外的材料（超控阵列的监听阵列核）——列出库存，缺货时逐条写明从哪里获得；研究节点也写出来。
            if (mode.SelectedTypeId != null)
            {
                IReadOnlyList<BuildMaterialNeed> extras = BuildMaterials.NewBuild(mode.SelectedTypeId);
                if (extras.Count > 0)
                {
                    _cost.text += "\n" + GameText.Format("build.cost.extra", BuildMaterials.DescribeList(state, extras));
                    string shortfall = BuildMaterials.DescribeShortfall(state, extras);
                    if (shortfall != null)
                    {
                        _cost.text += "\n" + shortfall + "\n" + GameText.Get("build.cost.can_place");
                        short_ = true;
                    }
                }
                string research = GridContent.TryGetBuilding(mode.SelectedTypeId, out BuildingGrid g) ? ResearchGate.Describe(state, g.UnlockRule) : string.Empty;
                if (research.Length > 0)
                {
                    _cost.text += "\n" + research;
                }
            }
            _cost.EnableInClassList("bm-cost-short", short_);
        }

        private void RefreshDrag(HomeValleyBuildMode mode)
        {
            BeltPathPlan belt = mode.BeltPlan;
            DemolishBoxPlan box = mode.BoxPlan;
            _dragInfo.EnableInClassList("bm-cost-short", false);
            if (belt != null && belt.Kind == BeltNodeKind.UndergroundIn)
            {
                // FG3-LOG-04（FGR-LOG-023）：拖地下传送带时写这次的跨度、上限与两端的成本。
                SetVisible(_dragInfo, true);
                int shortBy = belt.TotalCost - belt.Stock;
                int span = Math.Max(0, belt.Distance - 1);
                _dragInfo.text = shortBy > 0
                    ? GameText.Format("ui.build.drag_info_under_short", span, belt.MaxSpan, belt.TotalCost, belt.Stock, shortBy)
                    : GameText.Format("ui.build.drag_info_under", span, belt.MaxSpan, belt.TotalCost, belt.Stock);
            }
            else if (belt != null && belt.Kind == BeltNodeKind.Belt)
            {
                SetVisible(_dragInfo, true);
                int shortBy = belt.TotalCost - belt.Stock;
                _dragInfo.text = shortBy > 0
                    ? GameText.Format("ui.build.drag_info_short", belt.Length, belt.TotalCost, belt.Stock, shortBy)
                    : GameText.Format("ui.build.drag_info", belt.Length, belt.TotalCost, belt.Stock);
                if (belt.IsPipe && belt.Ok && belt.Pipe == BinGames.Sim.Logistics.PipePieceKind.Underground && belt.Cells.Count == 1)
                {
                    // FG4-ECO-04（FG-GAP-082）：地下管线口放下后和哪一口配对、跨几格；还没配对写明在哪放另一口。
                    _dragInfo.text += "\n" + (Campaign.Logistics.PipeNetworkService.TryPreviewUnderground(belt.Cells[0], (int)belt.Dirs[0], belt.Tier, out Campaign.Grid.GridCell mate)
                        ? GameText.Format("ui.build.preview_underground_linked", mate.X, mate.Y, Math.Abs(mate.X - belt.Cells[0].X) + Math.Abs(mate.Y - belt.Cells[0].Y) - 1)
                        : GameText.Format("ui.build.preview_underground_unlinked", Campaign.Logistics.PipeNetworkService.UndergroundSpan(belt.Tier)));
                }
                if (belt.IsPipe && belt.Ok)
                {
                    // FG3-LOG-05（FG03 第 4 节“放置时预览流体连接”）：放下后接入哪种流体的网络；泵写它抽的流体。
                    _dragInfo.text += "\n" + (belt.Pipe == BinGames.Sim.Logistics.PipePieceKind.Pump
                        ? GameText.Format("ui.build.pipe_pump_source", Campaign.Logistics.PipeNetworkService.FluidName(belt.PipeFluid))
                        : belt.JoinFluid > 0
                            ? GameText.Format("ui.build.pipe_joins", Campaign.Logistics.PipeNetworkService.FluidName(belt.JoinFluid))
                            : GameText.Get("ui.build.pipe_new_network"));
                }
            }
            else if (belt != null)
            {
                SetVisible(_dragInfo, false); // 分流器 / 合流器一次放一座：成本在成本行。
            }
            else if (box != null)
            {
                SetVisible(_dragInfo, true);
                _dragInfo.text = box.Pipes > 0
                    ? GameText.Format("ui.build.box_info_pipes", box.Max.X - box.Min.X + 1, box.Max.Y - box.Min.Y + 1, box.BuildingCount, box.Belts.Count - box.Pipes, box.Pipes)
                    : GameText.Format("ui.build.box_info", box.Max.X - box.Min.X + 1, box.Max.Y - box.Min.Y + 1, box.BuildingCount, box.Belts.Count);
            }
            else if (mode.Drag == HomeValleyBuildMode.DragKind.CopyBox)
            {
                SetVisible(_dragInfo, true);
                _dragInfo.text = GameText.Format("plan.copy.box", mode.CopyBoxMax.X - mode.CopyBoxMin.X + 1, mode.CopyBoxMax.Y - mode.CopyBoxMin.Y + 1);
            }
            else if (mode.UpgradePreview != null)
            {
                // FG3-LOG-07：拖升级框时写能升几件、差额多少、库存多少；不能升的写第一条原因。
                SetVisible(_dragInfo, true);
                UpgradeBoxPlan up = mode.UpgradePreview;
                _dragInfo.text = GameText.Format("plan.upgrade.box", up.Max.X - up.Min.X + 1, up.Max.Y - up.Min.Y + 1, up.Count, up.Cost, CampaignSession.Current?.Scrap ?? 0)
                                 + (up.Extras.Any ? "\n" + up.Extras.Describe(CampaignSession.Current) : string.Empty)
                                 + (up.Refused > 0 && up.FirstRefusal != null ? "\n" + GameText.Format("plan.upgrade.some_refused", up.Refused, up.FirstRefusal.Value.Describe()) : string.Empty);
            }
            else if (mode.PasteMode && mode.PastePreview != null)
            {
                // FG3-LOG-07：粘贴预览——能放几件、不能放几件（红叉）与第一处原因、成本与库存。
                SetVisible(_dragInfo, true);
                PastePlan pp = mode.PastePreview;
                string text = GameText.Format("plan.paste.preview", pp.OkCount, pp.BadCount, pp.Cost, pp.Stock);
                if (pp.Extras.Any)
                {
                    text += "\n" + pp.Extras.Describe(CampaignSession.Current); // FG4-ECO-11：粘贴造价里的额外材料
                }
                if (pp.BadCount > 0)
                {
                    text += "\n" + pp.DescribeFirstBad();
                }
                if (pp.LockedCount + pp.UnknownCount > 0)
                {
                    text += "\n" + GameText.Format("plan.paste.locked_note", pp.LockedCount, pp.UnknownCount);
                }
                if (mode.PasteTilesHidden > 0)
                {
                    text += "\n" + GameText.Format("plan.paste.tiles_hidden", mode.PasteTilesHidden);
                }
                _dragInfo.text = text;
                _dragInfo.EnableInClassList("bm-cost-short", pp.BadCount > 0);
            }
            else if (mode.ClearPlan != null)
            {
                SetVisible(_dragInfo, true);
                BeltClearPlan cp = mode.ClearPlan;
                _dragInfo.text = GameText.Format("ui.build.clear_box", cp.Max.X - cp.Min.X + 1, cp.Max.Y - cp.Min.Y + 1, cp.SurfaceCells, cp.Items);
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
            // FG4-ECO-05（FG-GAP-091）：鼠标已经指向别处时，状态行改写指着的对象（建筑 / 传送带 / 管线 / 施工虚影）的读数；
            // 上一步的结果在 ui.build.result_seconds 秒内另起一行“上一步：……”，之后消失。
            string lastResult = null;
            if (mode.StatusIsStale && mode.Drag == HomeValleyBuildMode.DragKind.None)
            {
                if (Time.unscaledTime - mode.StatusSetAt < Mathf.Max(0f, GridContent.Tuning("ui.build.result_seconds")))
                {
                    lastResult = mode.StatusText.Split('\n')[0];
                }
                status = string.Empty;
                error = false;
            }
            GridPlacementResult preview = mode.Preview;
            // FG3-LOG-04：没在拖的时候，选中的工具也有指着哪一格的预览（放置前就能看到能不能放、朝向与进出口）。
            BeltPathPlan belt = mode.BeltPlan ?? mode.ToolPreview;
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
                // FG3-LOG-06：放置预览的说明行（会接入哪个电网、覆盖多少座建筑），不是警告。
                foreach (string note in preview.Notes)
                {
                    status += "\n" + note;
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
            else if (string.IsNullOrEmpty(status) && mode.HasHover
                     && Campaign.Logistics.PipeNetworkService.TryDescribeHover(state, mode.HoverCell, out string pipeTitle, out string pipeBody))
            {
                // FG3-LOG-05（FGR-LOG-042）：建造模式里指着已建成的管线件，状态行写网络读数（战略视角由世界悬停提示显示同一份）。
                status = pipeTitle + "\n" + pipeBody;
            }
            else if ((string.IsNullOrEmpty(status) || mode.ClearMode) && mode.HasHover
                     && Campaign.Logistics.BeltNetworkService.TryDescribeHover(state, mode.HoverCell, out string hoverTitle, out string hoverBody))
            {
                // FG3-LOG-03（FGR-LOG-081）：建造模式里指着已建成的传送带，状态行写悬停读数（战略视角由世界悬停提示显示同一份）。
                status = (string.IsNullOrEmpty(status) ? string.Empty : status + "\n") + hoverTitle + "\n" + hoverBody;
            }
            if (!string.IsNullOrEmpty(lastResult))
            {
                status = (string.IsNullOrEmpty(status) ? string.Empty : status + "\n") + GameText.Format("ui.build.last_result", lastResult);
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
                    line2 = ToolCostLine(e.Tool);
                }
                else
                {
                    string cost = HomeValleyLayout.BuildProfile.TryGetValue(e.Id, out (int ScrapCost, float Seconds) p)
                        ? GameText.Format("ui.build.cost", p.ScrapCost, Mathf.RoundToInt(p.Seconds))
                        : string.Empty;
                    IReadOnlyList<BuildMaterialNeed> extras = BuildMaterials.NewBuild(e.Id);
                    if (extras.Count > 0)
                    {
                        cost += " " + GameText.Format("build.cost.extra", BuildMaterials.DescribeList(state, extras)); // FG4-ECO-11：关键材料与库存
                    }
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
