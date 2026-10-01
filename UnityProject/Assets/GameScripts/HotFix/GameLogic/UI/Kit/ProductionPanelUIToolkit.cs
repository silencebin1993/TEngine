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
    /// FG4-ECO-02（FG04 FGR-ECO-010 / 011 的本 Story 部分；卡片“每座建筑的通用面板”；FG00 B05 / B06 / B13 / B14）：采集与加工建筑的通用面板。
    /// - 状态（形状符号 + 文字）与原因（写明是什么、怎么办）；配方（多配方建筑下拉框选中即生效；单配方写固定功能）；进度条；输入 / 输出缓存；流体口；
    ///   建筑特有读数（回收站废墟储量、提取钻矿脉与震动、废液池销毁速率）；电力；累计完成；按钮：端口… / 为什么不工作；“?”打开图鉴。
    /// - 改名、启用 / 禁用、升级、效率与 10 分钟统计属于 FG4-ECO-05 / 08（页脚写明“后续版本加入”）。
    /// 入口：建造模式里点一下生产建筑。模态（Esc / 关闭 / 点遮罩关闭）；打开时每 0.25 真实秒刷新，O(这座建筑的缓存与端口数)。
    /// </summary>
    public sealed class ProductionPanelUIToolkit : UiKitPanelHost
    {
        /// <summary>通知 30040 之上、诊断面板 30043 之下（点“端口…”“为什么不工作”时本面板先收起，再打开那两个面板）。</summary>
        public const int Order = 30042;

        private const float RefreshSeconds = 0.25f;

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
        private Label _state;
        private Label _reason;
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
        private Label _inputs;
        private Label _outputs;
        private Label _fluids;
        private Label _detail;
        private Label _power;
        private Button _ports;
        private Button _diagnose;
        private Label _message;
        private Label _hint;
        private Label _placeholder;
        private float _timer;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly List<ProductionService.Producer> _sources = new List<ProductionService.Producer>(4);
        private readonly List<RecipeDef> _recipeChoices = new List<RecipeDef>(4);

        protected override string UxmlLocation => "ProductionPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string ReasonText => _reason?.text ?? string.Empty;
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
        public bool RecipeDropdownVisible => _recipeRow != null && !_recipeRow.ClassListContains("bn-hidden");
        public bool RecipeBoxVisible => _recipeBox != null && !_recipeBox.ClassListContains("bn-hidden");
        public DropdownField RecipeField => _recipe;
        public DropdownField BurnField => _burn;
        public bool BurnRowVisible => _burnRow != null && !_burnRow.ClassListContains("bn-hidden");
        public string MemoryText => _memory?.text ?? string.Empty;
        public Button CopyButton => _copy;
        public bool CopyVisible => _copy != null && _copy.style.display != DisplayStyle.None;
        public float ProgressFillPercent { get; private set; }
        public Button PortsButton => _ports;
        public Button DiagnoseButton => _diagnose;
        public Button HelpButton => _help;
        public Button CloseButton => _close;

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

        public static void Open(string buildingId)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingId = buildingId;
                return;
            }
            BuildingId = buildingId;
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
            _state = root.Q<Label>("PrState");
            _reason = root.Q<Label>("PrReason");
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
            _inputs = root.Q<Label>("PrInputs");
            _outputs = root.Q<Label>("PrOutputs");
            _fluids = root.Q<Label>("PrFluids");
            _detail = root.Q<Label>("PrDetail");
            _power = root.Q<Label>("PrPower");
            _ports = root.Q<Button>("PrPorts");
            _diagnose = root.Q<Button>("PrDiagnose");
            _message = root.Q<Label>("PrMessage");
            _hint = root.Q<Label>("ProductionPanelHint");
            _placeholder = root.Q<Label>("ProductionPanelPlaceholder");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _ports.clicked += OpenPorts;
            _diagnose.clicked += OpenDiagnosis;
            _copy.clicked += AskCopySettings;
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
            // FG4-ECO-03：刻录目标下拉框选中即生效。
            _burn.RegisterValueChangedCallback(evt =>
            {
                int i = _burn.choices.IndexOf(evt.newValue);
                SelectBurnTarget(i <= 0 || i - 1 >= _burnChoices.Count ? null : _burnChoices[i - 1]);
            });
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
                GuidanceHooks.Raise(GuidanceHooks.EconomyProductionPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _timer = 0f;
                _message.text = string.Empty;
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

        /// <summary>按生产服务的读数刷新。建筑不在了（被拆）就收起。</summary>
        public void Refresh()
        {
            if (_root == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            _close.text = GameText.Get("prod.panel.close");
            _help.text = GameText.Get("prod.panel.help");
            _ports.text = GameText.Get("prod.panel.ports");
            _diagnose.text = GameText.Get("prod.panel.diagnose");
            _hint.text = GameText.Get("prod.panel.hint");
            _placeholder.text = GameText.Get("prod.panel.placeholder");
            if (state == null || !ProductionService.TryGet(state, BuildingId, out ProductionService.Producer p))
            {
                if (IsOpen)
                {
                    SetOpen(false);
                }
                return;
            }
            BuildingRecord b = p.Building;
            _title.text = GameText.Format("prod.panel.title", HomeGridService.DisplayName(b.BuildingTypeId), b.GridX, b.GridY);
            ProductionService.TryDescribe(state, b, out _); // 还没推进过的建筑先按建筑状态给出状态（不推进任何东西）
            _state.text = ProductionService.StateText(p);
            _reason.text = ProductionService.ReasonText(state, p);
            RefreshRecipe(p);
            RefreshBurnAndMemory(state, p);
            RefreshProgress(p);
            RefreshBuffers(state, p);
            RefreshDetail(state, p);
            RefreshPower(p);
            _ports.SetEnabled(GridContent.PortsOf(b.BuildingTypeId).Count > 0);
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
                    _sb.Append(GameText.Language == GameLanguage.En ? " · " : " · ").Append(amount);
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
            return parts.Count == 0 ? GameText.Get("prod.panel.empty") : string.Join(GameText.Language == GameLanguage.En ? ", " : "、", parts);
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
                            parts.Add(GameText.Format("prod.panel.vibration_source", HomeGridService.DisplayName(s.Building.BuildingTypeId), s.Building.GridX, s.Building.GridY,
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

        private void RefreshPower(ProductionService.Producer p)
        {
            BuildingRecord b = p.Building;
            if (!HomeValleyLayout.PowerProfile.TryGetValue(b.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof))
            {
                _power.text = string.Empty;
                return;
            }
            _power.text = GameText.Format("prod.panel.power", prof.PowerDemand.ToString("0.#", CultureInfo.InvariantCulture), b.PowerPriority,
                GameText.Get(b.PowerState == BuildingPowerState.Powered ? "prod.panel.power_ok" : "prod.panel.power_off"));
        }

        // ── 操作（控件与自检同一入口）──────────────────────────────────────────────

        public bool SelectRecipe(string recipeId)
        {
            bool ok = ProductionService.TrySetRecipe(CampaignSession.Current, BuildingId, recipeId, out string message);
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
            Refresh();
            return ok;
        }

        /// <summary>FG4-ECO-03：选刻录目标（控件与自检同一入口）。</summary>
        public bool SelectBurnTarget(string firmwareId)
        {
            bool ok = ProductionService.TrySetBurnTarget(CampaignSession.Current, BuildingId, firmwareId, out string message);
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(ok ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
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
            _message.text = message ?? string.Empty;
            Campaign.Feedback.FeedbackCues.Raise(n >= 0 ? Campaign.Feedback.FeedbackCueId.CommandAck : Campaign.Feedback.FeedbackCueId.Denied, message);
            Refresh();
            return n;
        }

        public void OpenPorts()
        {
            string id = BuildingId;
            SetOpen(false);
            BeltPortPanelUIToolkit.Open(id);
        }

        public void OpenDiagnosis()
        {
            SetOpen(false);
            DiagnosisPanelUIToolkit.Open();
        }

        public void OpenCodex()
        {
            if (!ProductionService.TryGet(CampaignSession.Current, BuildingId, out ProductionService.Producer p))
            {
                return;
            }
            bool gathering = p.Def.Mode == ProducerMode.Recycler || p.Def.Mode == ProducerMode.Drill || p.Def.Mode == ProducerMode.Pump;
            if (p.Def.Mode == ProducerMode.Generator)
            {
                MechanicCodex.Open("codex.economy.energy", unlock: false);
                return;
            }
            MechanicCodex.Open(gathering ? "codex.economy.gathering"
                : ProductionService.IsManufacturing(p.Def.TypeId) ? "codex.economy.manufacturing" : "codex.economy.processing", unlock: false);
        }
    }
}
