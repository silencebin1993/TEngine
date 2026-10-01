using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG4-ECO-05（FGR-ECO-010～013；FG13 FGU-09 建筑面板通用模板；卡片“建筑改名；启用和禁用；批量修改优先级；面板跳到上下游建筑；‘?’帮助”）：
    /// 所有建筑共用的面板（FG4-ECO-02 起的生产建筑面板扩成通用模板，生产建筑的配方 / 进度 / 缓存 / 流体口一节照旧）。
    /// - 身份行（类型 · 编号 · 等级）、状态（形状标记 + 文字）与原因（写明是什么、怎么办）；
    /// - 名称（改名 / 默认名）、启用 / 禁用、电力优先级、同类批量（全部启用 / 全部禁用 / 全部设为这一级，先确认）；
    /// - 耐久与维修（受损派维修单，摧毁派重建单）、等级与原地升级（差额、工期、效果；升级中可取消）、仓库“只存哪些物品”；
    /// - 效率与最近 10 分钟产出、上游 / 下游建筑（点一下镜头跳过去并打开它的面板）、端口… / 电网… / 为什么不工作 / 清空缓存到仓库、“?”图鉴。
    /// 入口：建造模式里点一下建筑。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 真实秒刷新，O(这座建筑的缓存与端口数 + 生产建筑数)。
    /// </summary>
    public sealed class ProductionPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>通知 30040 之上、诊断面板 30043 之下（点“端口…”“为什么不工作”时本面板先收起，再打开那两个面板）。</summary>
        public const int Order = 30042;

        private const float RefreshSeconds = 0.25f;
        private const int LinkSlots = 4;

        public static ProductionPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _close;
        private Button _help;
        private Label _ident;
        private VisualElement _statusIcon;
        private Label _state;
        private Label _reason;
        private Label _ruleLine;
        private Label _nameLabel;
        private TextField _name;
        private Button _rename;
        private Button _renameReset;
        private Button _enable;
        private Label _priorityLabel;
        private DropdownField _priority;
        private Label _priorityNote;
        private VisualElement _batchRow;
        private Label _batchLabel;
        private DropdownField _batch;
        private Label _durability;
        private VisualElement _durabilityFill;
        private Button _repair;
        private Button _repairCancel;
        private VisualElement _tierBox;
        private Label _tier;
        private Label _upgradeLine;
        private Button _upgrade;
        private Button _upgradeCancel;
        private VisualElement _storeBox;
        private Label _storeLine;
        private Label _storeLabel;
        private DropdownField _store;
        private VisualElement _recipeBox;
        private VisualElement _recipeRow;
        private Label _recipeLabel;
        private DropdownField _recipe;
        private Label _recipeLine;
        private VisualElement _burnRow;
        private Label _burnLabel;
        private DropdownField _burn;
        private Label _memory;
        private Button _copy;
        private readonly List<string> _burnChoices = new List<string>(16);
        private VisualElement _progressBox;
        private Label _progress;
        private VisualElement _fill;
        private VisualElement _bufferBox;
        private Label _inputs;
        private Label _outputs;
        private Label _fluids;
        private Label _efficiency;
        private Label _output10;
        private Label _upLabel;
        private Label _downLabel;
        private Label _upMore;
        private Label _downMore;
        private readonly Button[] _up = new Button[LinkSlots];
        private readonly Button[] _down = new Button[LinkSlots];
        private readonly string[] _upIds = new string[LinkSlots];
        private readonly string[] _downIds = new string[LinkSlots];
        private Label _detail;
        private Label _power;
        private Button _ports;
        private Button _grid;
        private Button _diagnose;
        private Button _clear;
        private Label _message;
        private Label _hint;
        private Label _placeholder;
        private float _timer;
        private string _nameShownFor;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly List<ProductionService.Producer> _sources = new List<ProductionService.Producer>(4);
        private readonly List<RecipeDef> _recipeChoices = new List<RecipeDef>(4);
        private readonly List<string> _storeChoices = new List<string>(32);
        private readonly List<BuildingRecord> _links = new List<BuildingRecord>(LinkSlots);
        private readonly List<ItemStackRecord> _window = new List<ItemStackRecord>(4);

        protected override string UxmlLocation => "ProductionPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string IdentText => _ident?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string ReasonText => _reason?.text ?? string.Empty;
        public BuildingStatusKind ShownStatus { get; private set; }
        public string StatusShapeClass { get; private set; }
        public string RecipeLineText => _recipeLine?.text ?? string.Empty;
        public string ProgressText => _progress?.text ?? string.Empty;
        public string InputsText => _inputs?.text ?? string.Empty;
        public string OutputsText => _outputs?.text ?? string.Empty;
        public string FluidsText => _fluids?.text ?? string.Empty;
        public string DetailText => _detail?.text ?? string.Empty;
        public string PowerText => _power?.text ?? string.Empty;
        public string MessageText => _message?.text ?? string.Empty;
        public string HintText => _hint?.text ?? string.Empty;
        public string PlaceholderText => _placeholder?.text ?? string.Empty;
        public string DurabilityText => _durability?.text ?? string.Empty;
        public string TierText => _tier?.text ?? string.Empty;
        public string UpgradeLineText => _upgradeLine?.text ?? string.Empty;
        public string StoreLineText => _storeLine?.text ?? string.Empty;
        public string EfficiencyText => _efficiency?.text ?? string.Empty;
        public string Output10Text => _output10?.text ?? string.Empty;
        public string UpLabelText => _upLabel?.text ?? string.Empty;
        public string DownLabelText => _downLabel?.text ?? string.Empty;
        public string PriorityNoteText => _priorityNote?.text ?? string.Empty;
        public bool RecipeDropdownVisible => _recipeRow != null && !_recipeRow.ClassListContains("bn-hidden");
        public bool RecipeBoxVisible => _recipeBox != null && !_recipeBox.ClassListContains("bn-hidden");
        public bool TierBoxVisible => _tierBox != null && !_tierBox.ClassListContains("bn-hidden");
        public bool StoreBoxVisible => _storeBox != null && !_storeBox.ClassListContains("bn-hidden");
        public DropdownField RecipeField => _recipe;
        public DropdownField BurnField => _burn;
        public DropdownField PriorityField => _priority;
        public DropdownField BatchField => _batch;
        public DropdownField StoreField => _store;
        public TextField NameField => _name;
        public bool BurnRowVisible => _burnRow != null && !_burnRow.ClassListContains("bn-hidden");
        public string MemoryText => _memory?.text ?? string.Empty;
        public Button CopyButton => _copy;
        public bool CopyVisible => _copy != null && _copy.style.display != DisplayStyle.None;
        public float ProgressFillPercent { get; private set; }
        public float DurabilityFillPercent { get; private set; }
        public Button PortsButton => _ports;
        public Button GridButton => _grid;
        public Button DiagnoseButton => _diagnose;
        public Button ClearButton => _clear;
        public Button HelpButton => _help;
        public Button CloseButton => _close;
        public Button RenameButton => _rename;
        public Button RenameResetButton => _renameReset;
        public Button EnableButton => _enable;
        public Button RepairButton => _repair;
        public Button RepairCancelButton => _repairCancel;
        public Button UpgradeButton => _upgrade;
        public Button UpgradeCancelButton => _upgradeCancel;
        public Button UpLink(int i) => i >= 0 && i < LinkSlots ? _up[i] : null;
        public Button DownLink(int i) => i >= 0 && i < LinkSlots ? _down[i] : null;
        public string UpLinkId(int i) => i >= 0 && i < LinkSlots ? _upIds[i] : null;
        public string DownLinkId(int i) => i >= 0 && i < LinkSlots ? _downIds[i] : null;
        public static bool Visible(VisualElement e) => e != null && e.style.display != DisplayStyle.None && !e.ClassListContains("bn-hidden");

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

        /// <summary>最近一次请求打开的建筑（自检：编辑模式下面板宿主没就绪时也能核对“点一下打开了哪座的面板”）。</summary>
        public static string LastRequestedId { get; private set; }

        public static void Open(string buildingId)
        {
            LastRequestedId = buildingId;
            if (Instance == null || Instance._root == null)
            {
                _pendingId = buildingId;
                return;
            }
            BuildingId = buildingId;
            Instance._nameShownFor = null;
            if (IsOpen)
            {
                Instance._message.text = string.Empty;
                Instance.Refresh();
                return;
            }
            Instance.SetOpen(true);
        }

        public static void Close()
        {
            _pendingId = null;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            if (_pendingId != null)
            {
                string id = _pendingId;
                _pendingId = null;
                Open(id);
            }
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("ProductionPanelRoot");
            _title = root.Q<Label>("ProductionPanelTitle");
            _close = root.Q<Button>("ProductionPanelClose");
            _help = root.Q<Button>("ProductionPanelHelp");
            _ident = root.Q<Label>("BpIdent");
            _statusIcon = root.Q<VisualElement>("BpStatusIcon");
            _state = root.Q<Label>("PrState");
            _reason = root.Q<Label>("PrReason");
            _ruleLine = root.Q<Label>("BpRuleLine");
            _nameLabel = root.Q<Label>("BpNameLabel");
            _name = root.Q<TextField>("BpName");
            _rename = root.Q<Button>("BpRename");
            _renameReset = root.Q<Button>("BpRenameReset");
            _enable = root.Q<Button>("BpEnable");
            _priorityLabel = root.Q<Label>("BpPriorityLabel");
            _priority = root.Q<DropdownField>("BpPriority");
            _priorityNote = root.Q<Label>("BpPriorityNote");
            _batchRow = root.Q<VisualElement>("BpBatchRow");
            _batchLabel = root.Q<Label>("BpBatchLabel");
            _batch = root.Q<DropdownField>("BpBatch");
            _durability = root.Q<Label>("BpDurability");
            _durabilityFill = root.Q<VisualElement>("BpDurabilityFill");
            _repair = root.Q<Button>("BpRepair");
            _repairCancel = root.Q<Button>("BpRepairCancel");
            _tierBox = root.Q<VisualElement>("BpTierBox");
            _tier = root.Q<Label>("BpTier");
            _upgradeLine = root.Q<Label>("BpUpgradeLine");
            _upgrade = root.Q<Button>("BpUpgrade");
            _upgradeCancel = root.Q<Button>("BpUpgradeCancel");
            _storeBox = root.Q<VisualElement>("BpStoreBox");
            _storeLine = root.Q<Label>("BpStoreLine");
            _storeLabel = root.Q<Label>("BpStoreLabel");
            _store = root.Q<DropdownField>("BpStore");
            _recipeBox = root.Q<VisualElement>("PrRecipeBox");
            _recipeRow = root.Q<VisualElement>("PrRecipeRow");
            _recipeLabel = root.Q<Label>("PrRecipeLabel");
            _recipe = root.Q<DropdownField>("PrRecipe");
            _recipeLine = root.Q<Label>("PrRecipeLine");
            _burnRow = root.Q<VisualElement>("PrBurnRow");
            _burnLabel = root.Q<Label>("PrBurnLabel");
            _burn = root.Q<DropdownField>("PrBurn");
            _memory = root.Q<Label>("PrMemory");
            _copy = root.Q<Button>("PrCopy");
            _progressBox = root.Q<VisualElement>("PrProgressBox");
            _progress = root.Q<Label>("PrProgress");
            _fill = root.Q<VisualElement>("PrProgressFill");
            _bufferBox = root.Q<VisualElement>("PrBufferBox");
            _inputs = root.Q<Label>("PrInputs");
            _outputs = root.Q<Label>("PrOutputs");
            _fluids = root.Q<Label>("PrFluids");
            _efficiency = root.Q<Label>("BpEfficiency");
            _output10 = root.Q<Label>("BpOutput10");
            _upLabel = root.Q<Label>("BpUpLabel");
            _downLabel = root.Q<Label>("BpDownLabel");
            _upMore = root.Q<Label>("BpUpMore");
            _downMore = root.Q<Label>("BpDownMore");
            for (int i = 0; i < LinkSlots; i++)
            {
                int slot = i;
                _up[i] = root.Q<Button>("BpUp" + i);
                _down[i] = root.Q<Button>("BpDown" + i);
                _up[i].clicked += () => JumpTo(_upIds[slot]);
                _down[i].clicked += () => JumpTo(_downIds[slot]);
            }
            _detail = root.Q<Label>("PrDetail");
            _power = root.Q<Label>("PrPower");
            _ports = root.Q<Button>("PrPorts");
            _grid = root.Q<Button>("BpGrid");
            _diagnose = root.Q<Button>("PrDiagnose");
            _clear = root.Q<Button>("BpClear");
            _message = root.Q<Label>("PrMessage");
            _hint = root.Q<Label>("ProductionPanelHint");
            _placeholder = root.Q<Label>("ProductionPanelPlaceholder");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _ports.clicked += OpenPorts;
            _grid.clicked += OpenGrid;
            _diagnose.clicked += OpenDiagnosis;
            _clear.clicked += () => ClearBuffers();
            _copy.clicked += AskCopySettings;
            _rename.clicked += () => Rename(_name.value);
            _renameReset.clicked += () => Rename(string.Empty);
            _enable.clicked += ToggleEnabled;
            _repair.clicked += () => OrderRepair();
            _repairCancel.clicked += () => CancelRepair();
            _upgrade.clicked += () => Upgrade();
            _upgradeCancel.clicked += () => CancelUpgrade();
            _name.maxLength = BuildingOps.MaxNameChars;
            _name.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Rename(_name.value);
                    evt.StopPropagation();
                }
            });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            // UI Toolkit 红线 8：下拉框选中即生效。
            _recipe.RegisterValueChangedCallback(evt =>
            {
                int i = _recipe.choices.IndexOf(evt.newValue);
                SelectRecipe(i <= 0 || i - 1 >= _recipeChoices.Count ? null : _recipeChoices[i - 1].Id);
            });
            _burn.RegisterValueChangedCallback(evt =>
            {
                int i = _burn.choices.IndexOf(evt.newValue);
                SelectBurnTarget(i <= 0 || i - 1 >= _burnChoices.Count ? null : _burnChoices[i - 1]);
            });
            _priority.RegisterValueChangedCallback(evt =>
            {
                int i = _priority.choices.IndexOf(evt.newValue);
                if (i >= 0)
                {
                    SetPriority(i + 1);
                }
            });
            _batch.RegisterValueChangedCallback(evt =>
            {
                int i = _batch.choices.IndexOf(evt.newValue);
                if (i > 0)
                {
                    AskBatch(i == 1 ? BuildingOps.BatchOp.Enable : i == 2 ? BuildingOps.BatchOp.Disable : BuildingOps.BatchOp.Priority);
                }
            });
            _store.RegisterValueChangedCallback(evt =>
            {
                int i = _store.choices.IndexOf(evt.newValue);
                if (i >= 0 && i < _storeChoices.Count)
                {
                    SetStoreFilter(_storeChoices[i]);
                }
            });
            UiStatusIcon.Build(_statusIcon);
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
                GuidanceHooks.Raise(GuidanceHooks.BuildingPanelFirstOpen);
                if (ProductionService.TryGet(CampaignSession.Current, BuildingId, out _))
                {
                    GuidanceHooks.Raise(GuidanceHooks.EconomyProductionPanelFirstOpen);
                }
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                _message.text = string.Empty;
                _nameShownFor = null;
                Refresh();
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
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f)
            {
                return;
            }
            _timer = RefreshSeconds;
            Refresh();
        }

        /// <summary>按建筑与各服务的读数刷新。建筑不在了（被拆）就收起。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            ApplyStaticTexts();
            BuildingRecord b = state != null ? HomeGridService.FindBuilding(state, BuildingId) : null;
            if (b == null)
            {
                if (IsOpen)
                {
                    SetOpen(false);
                }
                return;
            }
            bool isProducer = ProductionService.TryGet(state, b.BuildingId, out ProductionService.Producer p);
            _title.text = GameText.Format("prod.panel.title", BuildingOps.NameOf(b), b.GridX, b.GridY);
            _ident.text = BuildingOps.IdentLine(b);
            RefreshStatus(state, b, isProducer ? p : null);
            RefreshControls(state, b);
            RefreshDurability(state, b);
            RefreshTier(state, b);
            RefreshStore(state, b);
            _recipeBox.EnableInClassList("bn-hidden", !isProducer);
            _progressBox.EnableInClassList("bn-hidden", !isProducer);
            if (isProducer)
            {
                RefreshRecipe(p);
                RefreshBurnAndMemory(state, p);
                RefreshProgress(p);
                RefreshBuffers(state, p);
                RefreshDetail(state, p);
            }
            else
            {
                RefreshNonProducer(state, b);
            }
            RefreshStats(isProducer ? p : null);
            RefreshLinks(state, b);
            RefreshPower(b);
            _ports.SetEnabled(GridContent.PortsOf(b.BuildingTypeId).Count > 0);
            _grid.EnableInClassList("bn-hidden", !HomeValleyPowerGrid.IsPowerRelevantType(b.BuildingTypeId));
            _clear.SetEnabled(BuildingOps.BufferedCount(state, b) > 0);
        }

        private void ApplyStaticTexts()
        {
            _close.text = GameText.Get("prod.panel.close");
            _help.text = GameText.Get("prod.panel.help");
            _ports.text = GameText.Get("prod.panel.ports");
            _grid.text = GameText.Get("bp.open_grid");
            _diagnose.text = GameText.Get("prod.panel.diagnose");
            _clear.text = GameText.Get("bp.clear");
            _hint.text = GameText.Get("prod.panel.hint");
            _placeholder.text = GameText.Get("prod.panel.placeholder");
            _nameLabel.text = GameText.Get("bp.name_label");
            _rename.text = GameText.Get("bp.rename");
            _renameReset.text = GameText.Get("bp.rename_reset");
            _priorityLabel.text = GameText.Get("bp.priority_label");
            _batchLabel.text = GameText.Get("bp.batch_label");
            _storeLabel.text = GameText.Get("bp.store_label");
            _repairCancel.text = GameText.Get("bp.repair_cancel");
            _upgradeCancel.text = GameText.Get("bp.upgrade_cancel");
            _durability.tooltip = GameText.Get("bp.durability_tip");
            _efficiency.tooltip = GameText.Get("bp.efficiency_tip");
        }

        private void RefreshStatus(CampaignState state, BuildingRecord b, ProductionService.Producer p)
        {
            BuildingStatus st = BuildingStatusService.Evaluate(state, b);
            ShownStatus = st.Kind;
            var ui = (UiEntityStatus)(byte)st.Kind;
            UiStatusIcon.Set(_statusIcon, ui);
            StatusShapeClass = UiStatusIcon.ShapeOf(ui);
            // 生产建筑的功能状态沿用它的状态文字（带形状符号）；受损 / 升级中 / 禁用 / 摧毁等通用状态用通用名称。
            bool prodText = p != null && BuildingStatusService.FromProd(p.State) == st.Kind && st.ReasonCode != null && st.ReasonCode.StartsWith("prod.", System.StringComparison.Ordinal);
            _state.text = prodText ? ProductionService.StateText(p) : GameText.Get(BuildingOps.UiStatusNameKey(st.Kind));
            _reason.text = st.Reason;
            // FG4-ECO-06：“由规则 R3 触发：排产「维修件」”（没有规则在管它就隐藏）。
            string rule = StandingRuleService.DescribeBuilding(state, b.BuildingId);
            if (_ruleLine != null)
            {
                _ruleLine.text = rule ?? string.Empty;
                _ruleLine.EnableInClassList("uk-hidden", rule == null);
            }
            RuleLineText = rule ?? string.Empty;
        }

        /// <summary>自检：面板上的规则追溯行（没有 = 空）。</summary>
        public string RuleLineText { get; private set; } = string.Empty;

        private void RefreshControls(CampaignState state, BuildingRecord b)
        {
            // 名称：输入框有焦点时不覆盖玩家正在打的字。
            bool editing = _name.panel?.focusController?.focusedElement == _name;
            if (!editing && _nameShownFor != b.BuildingId + "|" + b.CustomName)
            {
                _name.SetValueWithoutNotify(b.CustomName ?? string.Empty);
                _nameShownFor = b.BuildingId + "|" + b.CustomName;
            }
            _name.tooltip = GameText.Get("bp.rename_placeholder");
            bool canToggle = BuildingOps.CanToggle(state, b, out string why);
            _enable.text = GameText.Get(BuildingOps.IsDisabled(b) ? "bp.enable" : "bp.disable");
            _enable.SetEnabled(canToggle);
            _enable.tooltip = canToggle ? string.Empty : why;
            bool hasPriority = BuildingOps.HasPriority(b.BuildingTypeId);
            var prio = new List<string>(4);
            for (int i = 1; i <= 4; i++)
            {
                prio.Add(GameText.Format("bp.priority_value", i));
            }
            DropdownChoices.Apply(_priority, prio, prio[0]);
            _priority.SetValueWithoutNotify(_priority.choices[Mathf.Clamp(b.PowerPriority - 1, 0, 3)]);
            _priority.SetEnabled(hasPriority);
            _priorityNote.text = hasPriority ? string.Empty : GameText.Get("bp.priority_none");
            _priorityNote.EnableInClassList("bn-hidden", hasPriority);
            // 同类批量：选项里写明会影响几座。
            int sameType = CountSameType(state, b);
            var batch = new List<string>(4)
            {
                GameText.Get("bp.batch_choose"),
                GameText.Format("bp.batch_enable_all", sameType),
                GameText.Format("bp.batch_disable_all", sameType),
            };
            if (hasPriority)
            {
                batch.Add(GameText.Format("bp.batch_priority_all", Mathf.Clamp(b.PowerPriority, 1, 4), sameType));
            }
            DropdownChoices.Apply(_batch, batch, batch[0]);
            _batch.SetValueWithoutNotify(_batch.choices[0]);
            _batchRow.EnableInClassList("bn-hidden", !BuildingOps.CanDisableType(b.BuildingTypeId));
        }

        private static int CountSameType(CampaignState state, BuildingRecord b)
        {
            int n = 0;
            foreach (BuildingRecord o in state.BuildingRecords ?? System.Array.Empty<BuildingRecord>())
            {
                if (o != null && o.RegionId == b.RegionId && o.BuildingTypeId == b.BuildingTypeId && !HomeGridService.IsRelocationGhost(o))
                {
                    n++;
                }
            }
            return n;
        }

        private void RefreshDurability(CampaignState state, BuildingRecord b)
        {
            float max = BuildingOps.MaxDurability(b.BuildingTypeId);
            float dur = BuildingOps.Durability(b);
            bool destroyed = b.ConstructionState == BuildingConstructionState.Damaged;
            if (destroyed)
            {
                dur = 0f;
            }
            _durability.text = GameText.Format("bp.durability", Mathf.RoundToInt(dur), Mathf.RoundToInt(max));
            DurabilityFillPercent = Mathf.Clamp01(dur / max) * 100f;
            _durabilityFill.style.width = Length.Percent(DurabilityFillPercent);
            WorkOrderRecord active = HomeValleyWorkOrders.FindActiveRepair(state, b.BuildingId);
            if (destroyed)
            {
                int scrap = BuildingOps.RebuildScrapFor(b);
                _repair.text = scrap >= 0 ? GameText.Format("bp.rebuild", scrap) : GameText.Get("bp.repair_cannot");
                _repair.SetEnabled(scrap >= 0 && active == null);
            }
            else
            {
                int kits = BuildingOps.RepairKitsFor(b);
                _repair.text = GameText.Format("bp.repair", kits);
                _repair.SetEnabled(active == null && BuildingOps.IsWorn(b) && kits > 0);
                _repair.tooltip = BuildingOps.IsWorn(b) ? string.Empty : GameText.Get("bp.repair_full");
            }
            _repairCancel.EnableInClassList("bn-hidden", active == null);
        }

        private void RefreshTier(CampaignState state, BuildingRecord b)
        {
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, b.BuildingId);
            bool upgrading = ghost != null && HomeGridService.IsUpgradeGhost(ghost);
            bool hasTiers = BuildingOps.HasTiers(b.BuildingTypeId);
            int cur = BuildingOps.TierOf(b);
            _tier.text = hasTiers
                ? GameText.Format("bp.tier_line", cur, BuildingOps.MaxTier(b.BuildingTypeId), BuildingOps.TierEffect(b.BuildingTypeId, cur) ?? string.Empty)
                : string.Empty;
            _tier.EnableInClassList("bn-hidden", !hasTiers);
            _upgradeCancel.EnableInClassList("bn-hidden", !upgrading);
            if (upgrading)
            {
                WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(state, ghost.BuildingId);
                _upgradeLine.text = GameText.Format("bp.upgrade_progress", order != null ? HomeValleyConstruction.DescribeStatus(state, order) : string.Empty);
                _upgrade.text = GameText.Format("bp.upgrade", ghost.Tier > 0 ? BuildingOps.TierName(b.BuildingTypeId, ghost.Tier) : HomeGridService.DisplayName(ghost.BuildingTypeId));
                _upgrade.SetEnabled(false);
                return;
            }
            if (HomeGridService.CanUpgradeNow(state, b, out string toType, out int toTier, out GridReason why))
            {
                int diff = HomeGridService.UpgradeCost(b, toType, toTier, out float seconds);
                string toName = toTier > 0 ? BuildingOps.TierName(toType, toTier) : HomeGridService.DisplayName(toType);
                string effect = toTier > 0
                    ? GameText.Format("bp.upgrade_effect", BuildingOps.TierEffect(b.BuildingTypeId, cur), BuildingOps.TierEffect(toType, toTier))
                    : GameText.Get("bp.upgrade_effect_none");
                _upgrade.text = GameText.Format("bp.upgrade", toName);
                _upgradeLine.text = GameText.Format("bp.upgrade_line", toName, diff, seconds.ToString("0.#", CultureInfo.InvariantCulture), effect);
                _upgrade.SetEnabled(true);
                _upgrade.EnableInClassList("bn-hidden", false);
                return;
            }
            bool noRoute = why.Code == GridBlockReason.NoUpgrade;
            _upgrade.text = GameText.Format("bp.upgrade", "…");
            _upgrade.SetEnabled(false);
            _upgrade.EnableInClassList("bn-hidden", noRoute);
            _upgradeLine.text = noRoute ? GameText.Get("bp.tier_none")
                : why.Code == GridBlockReason.UpgradeLocked ? GameText.Format("bp.upgrade_locked", why.Describe())
                : GameText.Format("bp.upgrade_refused", why.Describe());
        }

        private void RefreshStore(CampaignState state, BuildingRecord b)
        {
            bool wh = BuildingOps.IsWarehouse(b);
            _storeBox.EnableInClassList("bn-hidden", !wh);
            if (!wh)
            {
                return;
            }
            BuildingOps.StoreFilterChoices(_storeChoices);
            var names = new List<string>(_storeChoices.Count);
            foreach (string f in _storeChoices)
            {
                names.Add(BuildingOps.StoreFilterName(f));
            }
            DropdownChoices.Apply(_store, names, names[0]);
            int sel = _storeChoices.IndexOf(b.StoreFilter ?? string.Empty);
            _store.SetValueWithoutNotify(_store.choices[Mathf.Clamp(sel, 0, _store.choices.Count - 1)]);
            _storeLine.text = GameText.Format("bp.store_line", BuildingOps.WarehouseTierCapacity(b), BuildingOps.TierName(b.BuildingTypeId, BuildingOps.TierOf(b)),
                BuildingOps.StoreFilterName(b.StoreFilter));
        }

        private void RefreshNonProducer(CampaignState state, BuildingRecord b)
        {
            // 装配站：材料缓存（FG-GAP-095 清空缓存的对象）。其余建筑没有物品缓存。
            bool station = AssemblyMaterials.IsStation(b);
            _bufferBox.EnableInClassList("bn-hidden", !station);
            if (station)
            {
                var parts = new List<string>(4);
                foreach (ItemStackRecord s in AssemblyMaterials.Buffer(state))
                {
                    if (s != null && s.Amount > 0 && ItemCatalog.TryGet(s.ItemId, out ItemDef d))
                    {
                        parts.Add(GameText.Format("prod.panel.stack", d.Name, s.Amount, AssemblyMaterials.BufferCap));
                    }
                }
                _inputs.text = GameText.Format("prod.panel.inputs", parts.Count == 0 ? GameText.Get("prod.panel.empty") : string.Join(UiSep, parts));
                _outputs.text = string.Empty;
                _fluids.text = string.Empty;
                _fluids.EnableInClassList("bn-hidden", true);
            }
            _detail.text = (b.ConstructionState == BuildingConstructionState.Operational || b.ConstructionState == BuildingConstructionState.Disabled)
                           && HomeValleyPowerGrid.TryDescribeBuilding(state, b, out string power) ? power : string.Empty;
        }

        private static string UiSep => GameText.Language == GameLanguage.En ? ", " : "、";

        private void RefreshStats(ProductionService.Producer p)
        {
            int minutes = Mathf.Max(1, GridContent.TuningInt("building.stats.window_minutes"));
            if (p == null || !ProductionService.TryWindowStats(p, _window, out int done, out long theory))
            {
                _efficiency.text = GameText.Get("bp.efficiency_na");
                _output10.text = string.Empty;
                _output10.EnableInClassList("bn-hidden", true);
                return;
            }
            _output10.EnableInClassList("bn-hidden", false);
            if (theory < 1000)
            {
                _efficiency.text = GameText.Get("bp.efficiency_new");
            }
            else
            {
                int pct = Mathf.RoundToInt(Mathf.Clamp(done * 1000f / theory, 0f, 9.99f) * 100f);
                _efficiency.text = GameText.Format("bp.efficiency", minutes, pct, done, (theory / 1000.0).ToString("0.#", CultureInfo.InvariantCulture));
            }
            if (_window.Count == 0)
            {
                _output10.text = GameText.Format("bp.output10_none", minutes);
                return;
            }
            var parts = new List<string>(_window.Count);
            foreach (ItemStackRecord s in _window)
            {
                if (ItemCatalog.TryGet(s.ItemId, out ItemDef d))
                {
                    parts.Add(d.Name + " " + RecipeBook.Amount(d, s.Amount));
                }
            }
            _output10.text = GameText.Format("bp.output10", minutes, string.Join(UiSep, parts));
        }

        private void RefreshLinks(CampaignState state, BuildingRecord b)
        {
            bool upOk = BuildingOps.CollectLinks(state, b, true, _links, out int upMore);
            FillLinks(_up, _upIds, _links, upOk);
            _upLabel.text = !upOk ? GameText.Get("bp.link_na") : _links.Count == 0 ? GameText.Get("bp.link_none_up") : GameText.Get("bp.upstream");
            _upMore.text = upMore > 0 ? GameText.Format("bp.link_more", upMore) : string.Empty;
            bool downOk = BuildingOps.CollectLinks(state, b, false, _links, out int downMore);
            FillLinks(_down, _downIds, _links, downOk);
            _downLabel.text = !upOk && !downOk ? string.Empty : _links.Count == 0 ? GameText.Get("bp.link_none_down") : GameText.Get("bp.downstream");
            _downLabel.EnableInClassList("bn-hidden", !upOk && !downOk);
            _downMore.text = downMore > 0 ? GameText.Format("bp.link_more", downMore) : string.Empty;
        }

        private static void FillLinks(Button[] slots, string[] ids, List<BuildingRecord> links, bool ok)
        {
            for (int i = 0; i < LinkSlots; i++)
            {
                bool shown = ok && i < links.Count;
                slots[i].EnableInClassList("bn-hidden", !shown);
                ids[i] = shown ? links[i].BuildingId : null;
                if (shown)
                {
                    BuildingStatus st = BuildingStatusService.Evaluate(CampaignSession.Current, links[i]);
                    slots[i].text = GameText.Format("bp.link", BuildingOps.NameOf(links[i]), GameText.Get(BuildingOps.UiStatusNameKey(st.Kind)));
                    slots[i].tooltip = st.Reason;
                }
            }
        }

        /// <summary>FG4-ECO-03：刻录台的刻录目标下拉框；配方记忆说明（新建的同类建筑沿用 / 这座是沿用来的）；“复制设置到同类建筑”按钮。</summary>
        private void RefreshBurnAndMemory(CampaignState state, ProductionService.Producer p)
        {
            bool burner = p.IsBurner;
            _burnRow.EnableInClassList("bn-hidden", !burner);
            if (burner)
            {
                _burnLabel.text = GameText.Get("prod.panel.burn_target");
                _burnChoices.Clear();
                var names = new List<string>(16) { GameText.Get("prod.panel.burn_none") };
                foreach (string id in Campaign.Signal.SignalCoreService.PrintableFirmware(state))
                {
                    _burnChoices.Add(id);
                    names.Add(Campaign.Signal.FirmwareKinds.DisplayName(id) ?? id);
                }
                // 选中的目标已经不能刻（例如读档后内容变了）：仍列出来，原因行写明为什么不刻。
                string cur = p.Rec.BurnTarget;
                if (!string.IsNullOrEmpty(cur) && !_burnChoices.Contains(cur))
                {
                    _burnChoices.Add(cur);
                    names.Add(Campaign.Signal.FirmwareKinds.DisplayName(cur) ?? cur);
                }
                DropdownChoices.Apply(_burn, names, names[0]);
                int sel = string.IsNullOrEmpty(cur) ? 0 : _burnChoices.IndexOf(cur) + 1;
                _burn.SetValueWithoutNotify(_burn.choices[Mathf.Clamp(sel, 0, _burn.choices.Count - 1)]);
                string target = string.IsNullOrEmpty(cur) ? GameText.Get("prod.panel.setting_none") : Campaign.Signal.FirmwareKinds.DisplayName(cur) ?? cur;
                _recipeLine.text = GameText.Format("prod.panel.burn_line", target, p.Def.FixedRecipe.Seconds.ToString("0.#", CultureInfo.InvariantCulture));
            }
            bool copyable = ProductionService.HasCopyableSettings(p);
            string typeName = HomeGridService.DisplayName(p.Building.BuildingTypeId);
            if (copyable)
            {
                string setting = ProductionService.SettingText(p);
                _memory.text = p.Rec.Inherited
                    ? GameText.Format("prod.panel.inherited", typeName, setting)
                    : GameText.Format("prod.panel.remembered", typeName, setting);
            }
            else
            {
                _memory.text = string.Empty;
            }
            _memory.EnableInClassList("bn-hidden", !copyable);
            _copy.text = GameText.Get("prod.panel.copy");
            _copy.style.display = copyable ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshRecipe(ProductionService.Producer p)
        {
            bool recipeMode = p.Def.Mode == ProducerMode.Recipe;
            _recipeBox.EnableInClassList("bn-hidden", !recipeMode);
            if (!recipeMode)
            {
                return;
            }
            RecipeDef fixedRecipe = p.Def.FixedRecipe;
            _recipeRow.EnableInClassList("bn-hidden", fixedRecipe != null);
            _recipeLabel.text = GameText.Get("prod.panel.recipe");
            if (fixedRecipe == null)
            {
                _recipeChoices.Clear();
                var names = new List<string>(p.Def.Recipes.Count + 1) { GameText.Get("prod.panel.recipe_none") };
                foreach (RecipeDef r in p.Def.Recipes)
                {
                    _recipeChoices.Add(r);
                    names.Add(r.Name);
                }
                DropdownChoices.Apply(_recipe, names, names[0]);
                int sel = p.Recipe != null ? _recipeChoices.IndexOf(p.Recipe) + 1 : 0;
                _recipe.SetValueWithoutNotify(_recipe.choices[Mathf.Clamp(sel, 0, _recipe.choices.Count - 1)]);
            }
            RecipeDef shown = p.Recipe ?? fixedRecipe;
            if (shown != null)
            {
                string line = GameText.Format("prod.panel.recipe_line", shown.Name, RecipeBook.Describe(shown, RecipeRole.In),
                    Outputs(shown), shown.Seconds.ToString("0.#", CultureInfo.InvariantCulture));
                _recipeLine.text = fixedRecipe != null ? GameText.Format("prod.panel.recipe_fixed", shown.Name) + "\n" + line : line;
            }
            else
            {
                _recipeLine.text = GameText.Get("prod.reason.no_recipe");
            }
        }

        private static string Outputs(RecipeDef r)
        {
            string outs = RecipeBook.Describe(r, RecipeRole.Out);
            string by = RecipeBook.Describe(r, RecipeRole.Byproduct);
            return string.IsNullOrEmpty(by) ? outs : outs + (GameText.Language == GameLanguage.En ? ", " : "、") + by;
        }

        private void RefreshProgress(ProductionService.Producer p)
        {
            // 废液池、流体泵是连续工作（没有“一个周期”），不显示进度条。
            bool show = p.Def.Mode != ProducerMode.Waste && p.Def.Mode != ProducerMode.Pump && p.Def.Mode != ProducerMode.Generator;
            _progressBox.EnableInClassList("bn-hidden", !show);
            if (!show)
            {
                return;
            }
            ProducerRecord r = p.Rec;
            float hz = Mathf.Max(1, GameClock.StepHz);
            if (r.Running && r.Duration > 0)
            {
                float pct = Mathf.Clamp01((float)r.Progress / r.Duration);
                ProgressFillPercent = pct * 100f;
                _progress.text = GameText.Format("prod.panel.progress", Mathf.RoundToInt(pct * 100f), (r.Progress / hz).ToString("0.0", CultureInfo.InvariantCulture),
                    (r.Duration / hz).ToString("0.0", CultureInfo.InvariantCulture));
            }
            else
            {
                ProgressFillPercent = 0f;
                _progress.text = GameText.Get("prod.panel.progress_idle");
            }
            _fill.style.width = Length.Percent(ProgressFillPercent);
        }

        private void RefreshBuffers(CampaignState state, ProductionService.Producer p)
        {
            _bufferBox.EnableInClassList("bn-hidden", false);
            _inputs.text = GameText.Format("prod.panel.inputs", Stacks(p, p.Rec.In, input: true));
            _outputs.text = GameText.Format("prod.panel.outputs", Stacks(p, p.Rec.Out, input: false));
            _sb.Clear();
            for (int i = 0; i < p.Fluids.Length; i++)
            {
                ProductionService.FluidRt f = p.Fluids[i];
                string fluidName = f.Fluid?.Name ?? GameText.Get("prod.panel.fluid_any");
                string title = GameText.Format(f.Def.IsOutput ? "prod.panel.fluid_out" : "prod.panel.fluid_in", fluidName);
                string status = FluidStatus(p, i, out string amount);
                if (_sb.Length > 0)
                {
                    _sb.Append('\n');
                }
                _sb.Append(GameText.Format("prod.panel.fluid_port", title, GameText.Get(GridMath.DirTextKey(f.Face)), f.PipeCell.X, f.PipeCell.Y, status));
                if (!string.IsNullOrEmpty(amount))
                {
                    _sb.Append(" · ").Append(amount);
                }
            }
            _fluids.text = _sb.ToString();
            _fluids.EnableInClassList("bn-hidden", p.Fluids.Length == 0);
        }

        private string Stacks(ProductionService.Producer p, ItemStackRecord[] stacks, bool input)
        {
            var parts = new List<string>(3);
            if (stacks != null)
            {
                foreach (ItemStackRecord s in stacks)
                {
                    if (s == null || s.Amount <= 0 || !ItemCatalog.TryGet(s.ItemId, out ItemDef d))
                    {
                        continue;
                    }
                    int cap = input ? ProductionService.InCapacity(p, d) : ProductionService.OutCapacity(p, d);
                    parts.Add(GameText.Format("prod.panel.stack", d.Name, s.Amount, cap));
                }
            }
            return parts.Count == 0 ? GameText.Get("prod.panel.empty") : string.Join(UiSep, parts);
        }

        private static string FluidStatus(ProductionService.Producer p, int i, out string amount)
        {
            amount = null;
            ProductionService.FluidRt f = p.Fluids[i];
            BinGames.Sim.Logistics.PipeKernel k = PipeNetworkService.IsRunning ? PipeNetworkService.Kernel : null;
            int h = p.Rec.FluidHandles != null && i < p.Rec.FluidHandles.Length ? p.Rec.FluidHandles[i] : -1;
            int net = -1;
            if (k != null && h >= 0)
            {
                string name = f.Fluid?.Name ?? GameText.Get("prod.panel.fluid_any");
                if (f.Def.IsOutput && k.TryGetProducer(h, out BinGames.Sim.Logistics.PipeProducerInfo pi))
                {
                    net = pi.Network;
                    amount = GameText.Format("prod.panel.stack_fluid", name, (pi.StockMl / 1000).ToString(CultureInfo.InvariantCulture),
                        (pi.CapacityMl / 1000).ToString(CultureInfo.InvariantCulture));
                }
                else if (!f.Def.IsOutput && k.TryGetConsumer(h, out BinGames.Sim.Logistics.PipeConsumerInfo ci))
                {
                    net = ci.Network;
                    if (ci.CapacityMl > 0)
                    {
                        amount = GameText.Format("prod.panel.stack_fluid", name, (ci.BufferMl / 1000).ToString(CultureInfo.InvariantCulture),
                            (ci.CapacityMl / 1000).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            if (k == null || net < 0 || !k.TryGetNetworkInfo(net, out BinGames.Sim.Logistics.PipeNetInfo n))
            {
                return GameText.Get("prod.panel.fluid_disconnected");
            }
            return GameText.Format("prod.panel.fluid_connected", net, PipeNetworkService.FluidName(n.Fluid));
        }

        private void RefreshDetail(CampaignState state, ProductionService.Producer p)
        {
            _sb.Clear();
            switch (p.Def.Mode)
            {
                case ProducerMode.Recycler:
                {
                    int left = ProductionService.RuinLeft(state, p, out int cells);
                    _sb.Append(cells > 0
                        ? GameText.Format("prod.panel.ruin", cells, left, ProductionService.RuinScrapPerCell)
                        : GameText.Format("prod.panel.ruin_done", p.Rec.RuinRecovered));
                    _sb.Append('\n').Append(GameText.Get("prod.panel.recycle_note"));
                    break;
                }
                case ProducerMode.Drill:
                {
                    if (p.VeinOre != null)
                    {
                        _sb.Append(GameText.Format("prod.panel.vein", GameText.Get(p.VeinTerrainKey), p.VeinCells, p.VeinOre.Name)).Append('\n');
                    }
                    _sb.Append(GameText.Format("prod.panel.vibration", p.Def.VibrationPerMinute.ToString("0.#", CultureInfo.InvariantCulture),
                        ProductionService.Vibration(state).ToString("0.#", CultureInfo.InvariantCulture),
                        ProductionService.VibrationMax.ToString("0", CultureInfo.InvariantCulture)));
                    ProductionService.CollectVibrationSources(_sources, Mathf.Max(1, GridContent.TuningInt("eco.prod.sources_shown")));
                    if (_sources.Count > 0)
                    {
                        var parts = new List<string>(_sources.Count);
                        foreach (ProductionService.Producer s in _sources)
                        {
                            parts.Add(GameText.Format("prod.panel.vibration_source", BuildingOps.NameOf(s.Building), s.Building.GridX, s.Building.GridY,
                                s.Def.VibrationPerMinute.ToString("0.#", CultureInfo.InvariantCulture)));
                        }
                        _sb.Append('\n').Append(GameText.Format("prod.panel.vibration_sources", string.Join(GameText.Language == GameLanguage.En ? "; " : "；", parts)));
                    }
                    break;
                }
                case ProducerMode.Waste:
                {
                    long destroyed = 0;
                    int h = p.Rec.FluidHandles != null && p.Rec.FluidHandles.Length > 0 ? p.Rec.FluidHandles[0] : -1;
                    if (h >= 0 && PipeNetworkService.IsRunning && PipeNetworkService.Kernel.TryGetConsumer(h, out BinGames.Sim.Logistics.PipeConsumerInfo ci))
                    {
                        destroyed = ci.TotalDeliveredMl / 1000;
                    }
                    _sb.Append(GameText.Format("prod.panel.waste", Mathf.RoundToInt(p.Def.FluidLpm), destroyed));
                    break;
                }
                case ProducerMode.Generator:
                {
                    // FG4-ECO-04：燃油发电机——满负荷供电、烧油速率、机内燃油、累计烧掉；下一行写电网里的发电读数（负载 / 没油 / 未接入）。
                    float full = HomeValleyLayout.PowerSupplyProfile.TryGetValue(p.Def.TypeId, out float sup) ? sup : 0f;
                    _sb.Append(GameText.Format("prod.panel.generator", HomeValleyPowerGrid.Num(full), Mathf.RoundToInt(p.Def.FluidLpm),
                        (ProductionService.GeneratorFuelMl(p) / 1000).ToString(CultureInfo.InvariantCulture),
                        (ProductionService.GeneratorBufferMl(p) / 1000).ToString(CultureInfo.InvariantCulture),
                        (p.Rec.FuelBurnedMl / 1000).ToString(CultureInfo.InvariantCulture)));
                    if (HomeValleyPowerGrid.TryDescribeBuilding(state, p.Building, out string power))
                    {
                        _sb.Append('\n').Append(power);
                    }
                    break;
                }
                case ProducerMode.Pump:
                {
                    _sb.Append(p.SourceFluid != null
                        ? GameText.Format("prod.panel.pump", GameText.Get(p.SourceTerrainKey ?? string.Empty), p.SourceCells, p.SourceFluid.Name,
                            Mathf.RoundToInt(p.Def.FluidLpm), (p.Rec.PumpedMl / 1000).ToString(CultureInfo.InvariantCulture))
                        : GameText.Get("prod.panel.pump_none"));
                    break;
                }
            }
            if (p.IsBurner)
            {
                // FG4-ECO-03：刻好的芯片进固件库：写明存放了多少 / 能放多少（满了刻录台会停下）。
                if (_sb.Length > 0)
                {
                    _sb.Append('\n');
                }
                _sb.Append(GameText.Format("prod.panel.burn_storage", Campaign.Primitive.PrimitiveInventory.BagCount(state),
                    Campaign.Primitive.PrimitiveInventory.CapacityOf(state)));
            }
            if (p.Rec.Completed > 0 && p.Def.Mode != ProducerMode.Waste)
            {
                if (_sb.Length > 0)
                {
                    _sb.Append('\n');
                }
                _sb.Append(GameText.Format("prod.panel.completed", p.Rec.Completed));
            }
            _detail.text = _sb.ToString();
        }

        private void RefreshPower(BuildingRecord b)
        {
            if (!HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof))
            {
                _power.text = string.Empty;
                return;
            }
            _power.text = GameText.Format("prod.panel.power", prof.PowerDemand.ToString("0.#", CultureInfo.InvariantCulture), b.PowerPriority,
                GameText.Get(b.PowerState == BuildingPowerState.Powered ? "prod.panel.power_ok" : "prod.panel.power_off"));
        }

        // ── 操作（控件与自检同一入口）──────────────────────────────────────────────

        private void Say(bool ok, string message)
        {
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
        }

        public bool Rename(string name)
        {
            bool ok = BuildingOps.TryRename(CampaignSession.Current, BuildingId, name, out string message);
            Say(ok, message);
            _nameShownFor = null;
            Refresh();
            return ok;
        }

        public void ToggleEnabled()
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = HomeGridService.FindBuilding(state, BuildingId);
            SetEnabled(b == null || BuildingOps.IsDisabled(b));
        }

        public bool SetEnabled(bool enabled)
        {
            bool ok = BuildingOps.TrySetEnabled(CampaignSession.Current, BuildingId, enabled, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        public bool SetPriority(int priority)
        {
            bool ok = BuildingOps.TrySetPriority(CampaignSession.Current, BuildingId, priority, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        /// <summary>同类批量：只改本座时直接改；会改到别的建筑时先确认（写明会改几座、几座已经是这样、几座不能改，B04）。</summary>
        public void AskBatch(BuildingOps.BatchOp op)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = HomeGridService.FindBuilding(state, BuildingId);
            if (b == null)
            {
                return;
            }
            BuildingOps.BatchPlan plan = BuildingOps.PlanBatch(state, BuildingId, op, b.PowerPriority);
            string typeName = HomeGridService.DisplayName(b.BuildingTypeId);
            int others = plan.Change.Count - (plan.Change.Contains(BuildingId) ? 1 : 0);
            if (others < GridContent.TuningInt("building.batch.confirm"))
            {
                BatchNow(plan);
                return;
            }
            var req = new ConfirmRequest
            {
                Title = GameText.Format("bp.batch_confirm_title", typeName),
                ConfirmText = GameText.Get("bp.batch_confirm_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () => BatchNow(plan),
                OnCancel = () => Refresh(),
            };
            req.Consequences.Add(GameText.Format("bp.batch_confirm_line", BuildingOps.BatchOpName(plan), plan.Change.Count, plan.Same, plan.Refused));
            UiConfirmDialog.Show(req);
            PendingBatch = plan;
        }

        /// <summary>批量确认框正在询问的规划（自检用）。</summary>
        public BuildingOps.BatchPlan PendingBatch { get; private set; }

        public int BatchNow(BuildingOps.BatchPlan plan)
        {
            PendingBatch = null;
            int n = BuildingOps.ApplyBatch(CampaignSession.Current, plan, out string message);
            Say(n > 0, message);
            Refresh();
            return n;
        }

        public bool OrderRepair()
        {
            bool ok = BuildingOps.TryOrderRepair(CampaignSession.Current, BuildingId, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        public bool CancelRepair()
        {
            bool ok = BuildingOps.TryCancelRepair(CampaignSession.Current, BuildingId, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        /// <summary>原地升级一级（与升级规划同一入口：一步撤销）。</summary>
        public bool Upgrade()
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = HomeGridService.FindBuilding(state, BuildingId);
            if (b == null)
            {
                return false;
            }
            if (!HomeGridService.CanUpgradeNow(state, b, out string toType, out int toTier, out GridReason why))
            {
                Say(false, GameText.Format("bp.upgrade_refused", why.Describe()));
                Refresh();
                return false;
            }
            var plan = new UpgradeBoxPlan();
            plan.Buildings.Add(b.BuildingId);
            plan.BuildingTargets.Add(toType);
            string fromName = BuildingOps.TierName(b.BuildingTypeId, BuildingOps.TierOf(b));
            int done = PlanHistory.Upgrade(state, plan, out GridReason? first);
            bool ok = done > 0;
            Say(ok, ok
                ? GameText.Format("bp.upgrade_queued", fromName, toTier > 0 ? BuildingOps.TierName(toType, toTier) : HomeGridService.DisplayName(toType))
                : GameText.Format("bp.upgrade_refused", first?.Describe() ?? string.Empty));
            Refresh();
            return ok;
        }

        public bool CancelUpgrade()
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(state, BuildingId);
            if (ghost == null || !HomeGridService.IsUpgradeGhost(ghost))
            {
                return false;
            }
            GridOpResult r = PlanHistory.ToggleDemolish(state, BuildingId);
            Say(r.Success, r.Success ? GameText.Get("bp.upgrade_cancelled") : r.Describe());
            Refresh();
            return r.Success;
        }

        public bool SetStoreFilter(string filter)
        {
            bool ok = BuildingOps.TrySetStoreFilter(CampaignSession.Current, BuildingId, filter, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        public int ClearBuffers()
        {
            int n = BuildingOps.ClearBuffers(CampaignSession.Current, BuildingId, out string message);
            Say(n > 0, message);
            Refresh();
            return n;
        }

        /// <summary>上下游链接：镜头飞到那座建筑并打开它的面板（FG04 第 4 节“面板可以直接跳到上游 / 下游建筑”）。</summary>
        public bool JumpTo(string buildingId)
        {
            CampaignState state = CampaignSession.Current;
            BuildingRecord b = buildingId != null ? HomeGridService.FindBuilding(state, buildingId) : null;
            if (b == null)
            {
                return false;
            }
            Campaign.WorldSim.WorldView.FlyTo(HomeValleyLayout.RegionId, b.Position);
            LastJumpPosition = b.Position;
            BuildingId = buildingId;
            _nameShownFor = null;
            Refresh();
            Say(true, GameText.Format("bp.link_jumped", BuildingOps.NameOf(b)));
            return true;
        }

        public Vector2 LastJumpPosition { get; private set; }

        public bool SelectRecipe(string recipeId)
        {
            bool ok = ProductionService.TrySetRecipe(CampaignSession.Current, BuildingId, recipeId, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        /// <summary>FG4-ECO-03：选刻录目标（控件与自检同一入口）。</summary>
        public bool SelectBurnTarget(string firmwareId)
        {
            bool ok = ProductionService.TrySetBurnTarget(CampaignSession.Current, BuildingId, firmwareId, out string message);
            Say(ok, message);
            Refresh();
            return ok;
        }

        /// <summary>FG4-ECO-03（卡片“复制设置到同类建筑”）：先确认（写明会改几座、正在做的那份作废料退回），再应用。没有别的同类建筑时直接说明。</summary>
        public void AskCopySettings()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !ProductionService.TryGet(state, BuildingId, out ProductionService.Producer p))
            {
                return;
            }
            var others = new List<ProductionService.Producer>(8);
            ProductionService.CollectSameType(state, p, others);
            string typeName = HomeGridService.DisplayName(p.Building.BuildingTypeId);
            if (others.Count == 0)
            {
                _message.text = GameText.Format("prod.panel.copy_none", typeName);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, _message.text);
                return;
            }
            string id = BuildingId;
            var req = new ConfirmRequest
            {
                Title = GameText.Get("prod.panel.copy_confirm_title"),
                ConfirmText = GameText.Get("prod.panel.copy_confirm_ok"),
                CancelText = GameText.Get("ui.build.confirm_cancel"),
                OnConfirm = () => CopySettingsNow(id),
                OnCancel = () => Refresh(),
            };
            req.Consequences.Add(GameText.Format("prod.panel.copy_confirm_body", typeName, ProductionService.SettingText(p), others.Count));
            UiConfirmDialog.Show(req);
        }

        /// <summary>确认后应用（自检也直接调它）。</summary>
        public int CopySettingsNow(string buildingId)
        {
            int n = ProductionService.CopySettingsToSameType(CampaignSession.Current, buildingId, out _, out string message);
            Say(n >= 0, message);
            Refresh();
            return n;
        }

        public void OpenPorts()
        {
            string id = BuildingId;
            SetOpen(false);
            BeltPortPanelUIToolkit.Open(id);
        }

        /// <summary>电力相关建筑：打开电网面板并选中它所在的电网（储能站的充放电设置在那里）。</summary>
        public void OpenGrid()
        {
            string id = BuildingId;
            SetOpen(false);
            PowerPanelUIToolkit.OpenFor(id);
        }

        public void OpenDiagnosis()
        {
            SetOpen(false);
            DiagnosisPanelUIToolkit.Open();
        }

        /// <summary>“?”：打开这类建筑的图鉴条目（fg.TbBuildingService.codexId）。从机制界面上打开 = 顺带解锁（与暴露面板、上行 HUD 的“?”一致）：
        /// 开局点预置的发电机 / 装配站时条目还没被别的触发解锁，不解锁就只能看到“未解锁”（审查 P2）。</summary>
        public void OpenCodex()
        {
            BuildingRecord b = HomeGridService.FindBuilding(CampaignSession.Current, BuildingId);
            if (b == null)
            {
                return;
            }
            LastCodexId = BuildingOps.CodexIdOf(b.BuildingTypeId);
            MechanicCodex.Open(LastCodexId);
        }

        public string LastCodexId { get; private set; }
    }
}
