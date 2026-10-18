using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG8-DROP-01（FG13 FGU-46 投送站：投送舱数量、目标选择、飞行时间、燃油、防空警告；FG08 FGR-EXP-013～016）：投送站面板（UI Toolkit）。
    /// - 状态：舱（在站 / 冷却 / 在飞 / 容纳）、燃油、投送距离、自动补满；“装填”把仓库里的投送舱装进站。
    /// - 装载：站旁的机器（点选装舱，每舱 1 台连同货舱）、货物（下拉选仓库里的固体 + 数量，每舱 drop.cargo_units 个）。
    /// - 落点：“在地图上选落点…”打开战略地图选点（红色区域 = 敌方防空覆盖；悬停就写距离 / 飞行时间 / 燃油 / 能不能降落），预览写同一份，“发射”按预览校验。
    /// - 回收：“在地图上选回收点…”，预览写接几台 / 地上货物 / 装不下几台，“呼叫回收”。
    /// - 在飞的投送舱：每舱一行（去程 / 返程 / 冷却，载荷、剩余时间、改降 / 中止原因）。
    /// 入口：投送站的建筑面板“投送站…”。模态；Esc / 关闭 / 点遮罩关闭。刷新按投送 / 库存 / 名册版本与游戏秒，O(行数)。
    /// </summary>
    public sealed class DropStationPanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30058;

        public static DropStationPanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static bool _pendingOpen;
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Button _help;
        private Button _close;
        private Label _secStatus;
        private Label _status;
        private Button _loadPods;
        private Button _autoFill;
        private Label _secPayload;
        private Label _machinesHint;
        private ScrollView _machineList;
        private Label _machineEmpty;
        private DropdownField _cargo;
        private Button _cargoMinus;
        private Label _cargoAmount;
        private Button _cargoPlus;
        private Label _secTarget;
        private Button _pickTarget;
        private Label _targetLabel;
        private Label _preview;
        private Button _launch;
        private Label _secRecall;
        private Button _pickRecall;
        private Label _recallLabel;
        private Label _recallPreview;
        private Button _recallGo;
        private Label _secFlying;
        private Label _flying;
        private Label _message;
        private Label _footer;
        private VisualTreeAsset _rowTemplate;
        private bool _templateLoading;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<int> _rowIds = new List<int>();
        private readonly HashSet<int> _selected = new HashSet<int>();
        private readonly List<string> _cargoIds = new List<string>();
        private readonly List<MachineRecord> _machineScratch = new List<MachineRecord>(16);
        private readonly List<ItemDef> _itemScratch = new List<ItemDef>(32);
        private readonly List<string> _lineScratch = new List<string>(8);
        private string _cargoItem = string.Empty;
        private int _cargoAmountValue;
        private Vector2? _target;
        private Vector2? _recall;
        private DropPreview _lastPreview;
        private DropPreview _lastRecallPreview;
        private bool _suppress;
        private int _key;

        protected override string UxmlLocation => "DropStationPanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string StatusText => _status?.text ?? string.Empty;
        public string PreviewLabel => _preview?.text ?? string.Empty;
        public string RecallPreviewLabel => _recallPreview?.text ?? string.Empty;
        public string FlyingText => _flying?.text ?? string.Empty;
        public string MessageText => _message?.text ?? string.Empty;
        public string TargetText => _targetLabel?.text ?? string.Empty;
        public int MachineRowCount => _rowIds.Count;
        public int MachineRowId(int i) => i >= 0 && i < _rowIds.Count ? _rowIds[i] : 0;
        public string MachineRowText(int i) => i >= 0 && i < _rowIds.Count ? _rows[i].text : string.Empty;
        public IReadOnlyCollection<int> Selected => _selected;
        public string CargoItem => _cargoItem;
        public int CargoAmount => _cargoAmountValue;
        public Vector2? Target => _target;
        public Vector2? RecallPoint => _recall;
        public DropPreview LastPreview => _lastPreview;
        public DropPreview LastRecallPreview => _lastRecallPreview;
        public Button LaunchButton => _launch;
        public Button RecallButton => _recallGo;
        public Button PickTargetButton => _pickTarget;
        public Button PickRecallButton => _pickRecall;
        public Button LoadPodsButton => _loadPods;
        public Button AutoFillButton => _autoFill;
        public Button CloseButton => _close;
        public DropdownField CargoDropdown => _cargo;
        public Button CargoPlusButton => _cargoPlus;
        public Button CargoMinusButton => _cargoMinus;
        public Button MachineRowButton(int i) => i >= 0 && i < _rowIds.Count ? _rows[i] : null;

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

        /// <summary>打开某座投送站的面板（换了站就清掉上一次的装载与落点）。</summary>
        public static void Open(string buildingId)
        {
            if (Instance == null || Instance._root == null)
            {
                _pendingOpen = true;
                _pendingId = buildingId;
                return;
            }
            if (BuildingId != buildingId)
            {
                Instance.ResetSelection();
            }
            BuildingId = buildingId;
            if (!IsOpen)
            {
                Instance.SetOpen(true);
            }
            else
            {
                Instance.Refresh(force: true);
            }
        }

        public static void Close()
        {
            _pendingOpen = false;
            Instance?.SetOpen(false);
        }

        protected override void OnReady(VisualElement root)
        {
            BindView(root);
            LoadRowTemplate().Forget();
            if (_pendingOpen)
            {
                _pendingOpen = false;
                Open(_pendingId);
            }
        }

        private async UniTaskVoid LoadRowTemplate()
        {
            if (_templateLoading || _rowTemplate != null)
            {
                return;
            }
            _templateLoading = true;
            VisualTreeAsset t = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("DropStationRow");
            _templateLoading = false;
            if (this == null)
            {
                if (t != null)
                {
                    GameModule.Resource.UnloadAsset(t);
                }
                return;
            }
            _rowTemplate = t;
            if (t == null)
            {
                Log.Error("[DropStationPanelUIToolkit] 加载 DropStationRow 失败，机器列表行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplateForTests(VisualTreeAsset row)
        {
            _rowTemplate = row;
            _key = 0;
        }

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("DropRoot");
            _title = root.Q<Label>("DropTitle");
            _help = root.Q<Button>("DropHelp");
            _close = root.Q<Button>("DropClose");
            _secStatus = root.Q<Label>("DropSecStatus");
            _status = root.Q<Label>("DropStatus");
            _loadPods = root.Q<Button>("DropLoadPods");
            _autoFill = root.Q<Button>("DropAutoFill");
            _secPayload = root.Q<Label>("DropSecPayload");
            _machinesHint = root.Q<Label>("DropMachinesHint");
            _machineList = root.Q<ScrollView>("DropMachineList");
            _machineEmpty = root.Q<Label>("DropMachineEmpty");
            _cargo = root.Q<DropdownField>("DropCargoItem");
            _cargoMinus = root.Q<Button>("DropCargoMinus");
            _cargoAmount = root.Q<Label>("DropCargoAmount");
            _cargoPlus = root.Q<Button>("DropCargoPlus");
            _secTarget = root.Q<Label>("DropSecTarget");
            _pickTarget = root.Q<Button>("DropPickTarget");
            _targetLabel = root.Q<Label>("DropTargetLabel");
            _preview = root.Q<Label>("DropPreview");
            _launch = root.Q<Button>("DropLaunch");
            _secRecall = root.Q<Label>("DropSecRecall");
            _pickRecall = root.Q<Button>("DropPickRecall");
            _recallLabel = root.Q<Label>("DropRecallLabel");
            _recallPreview = root.Q<Label>("DropRecallPreview");
            _recallGo = root.Q<Button>("DropRecallGo");
            _secFlying = root.Q<Label>("DropSecFlying");
            _flying = root.Q<Label>("DropFlying");
            _message = root.Q<Label>("DropMessage");
            _footer = root.Q<Label>("DropFooter");
            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _loadPods.clicked += ClickLoadPods;
            _autoFill.clicked += ClickAutoFill;
            _cargoMinus.clicked += () => StepCargo(-1);
            _cargoPlus.clicked += () => StepCargo(+1);
            _pickTarget.clicked += ClickPickTarget;
            _launch.clicked += ClickLaunch;
            _pickRecall.clicked += ClickPickRecall;
            _recallGo.clicked += ClickRecall;
            _cargo.RegisterValueChangedCallback(_ => OnCargoChanged());
            UiTooltip.Attach(_preview, () => new TooltipContent { Title = GameText.Get("drop.panel.title"), Body = GameText.Get("codex.expedition.drop.body") });
            _root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == _root)
                {
                    SetOpen(false);
                }
            });
            _key = 0;
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
                GuidanceHooks.Raise(GuidanceHooks.DropPanelFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
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
            Refresh(force: false);
        }

        public void OpenCodex() => MechanicCodex.Open("codex.expedition.drop");

        private void ResetSelection()
        {
            _selected.Clear();
            _cargoItem = string.Empty;
            _cargoAmountValue = 0;
            _target = null;
            _recall = null;
            _lastPreview = null;
            _lastRecallPreview = null;
            if (_message != null)
            {
                _message.text = string.Empty;
            }
        }

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        public void ToggleMachine(int logicId)
        {
            if (!_selected.Remove(logicId))
            {
                _selected.Add(logicId);
            }
            _key = 0;
            Refresh(force: true);
        }

        public void ClickMachineRow(int i)
        {
            int id = MachineRowId(i);
            if (id > 0)
            {
                ToggleMachine(id);
            }
        }

        /// <summary>货物下拉选第 <paramref name="index"/> 项（与玩家点选同一回调：选中即生效；第 0 项 = 不带货物）。</summary>
        public void SelectCargo(int index)
        {
            if (_cargo != null && index >= 0 && index < _cargo.choices.Count)
            {
                _cargo.index = index;
                OnCargoChanged();
            }
        }

        private void OnCargoChanged()
        {
            if (_suppress || _cargo == null)
            {
                return;
            }
            int i = _cargo.index;
            string id = i > 0 && i - 1 < _cargoIds.Count ? _cargoIds[i - 1] : string.Empty;
            if (id != _cargoItem)
            {
                _cargoItem = id;
                _cargoAmountValue = string.IsNullOrEmpty(id) ? 0 : Math.Min(DropPodService.CargoUnits, HomeInventory.Stock(CampaignSession.Current, id));
            }
            _key = 0;
            Refresh(force: true);
        }

        /// <summary>货物数量按一舱（drop.cargo_units）增减，最多仓库里有的。</summary>
        public void StepCargo(int dir)
        {
            if (string.IsNullOrEmpty(_cargoItem))
            {
                return;
            }
            int stock = HomeInventory.Stock(CampaignSession.Current, _cargoItem);
            int unit = DropPodService.CargoUnits;
            int v = dir > 0 ? _cargoAmountValue + unit : _cargoAmountValue - unit;
            if (dir > 0 && v > stock)
            {
                v = stock;
            }
            _cargoAmountValue = Mathf.Clamp(v, 0, stock);
            _key = 0;
            Refresh(force: true);
        }

        public void ClickLoadPods()
        {
            CampaignState s = CampaignSession.Current;
            DropStationRecord st = DropPodService.FindStation(s, BuildingId);
            BuildingRecord b = HomeGridService.FindBuilding(s, BuildingId);
            int n = DropPodService.LoadPods(s, st, b, int.MaxValue);
            Show(n > 0 ? GameText.Format("drop.panel.loaded", n) : GameText.Get(HomeInventory.Stock(s, DropPodService.PodItemId) > 0 ? "drop.panel.load_full" : "drop.panel.load_none"), n == 0);
        }

        public void ClickAutoFill()
        {
            DropStationRecord st = DropPodService.FindStation(CampaignSession.Current, BuildingId);
            if (st == null)
            {
                return;
            }
            st.AutoFill = !st.AutoFill;
            Show(GameText.Get(st.AutoFill ? "drop.panel.autofill_turned_on" : "drop.panel.autofill_turned_off"), false);
        }

        /// <summary>“在地图上选落点…”：关面板、打开战略地图选点（悬停写预览）；选好回到面板，取消也回到面板。</summary>
        public void ClickPickTarget()
        {
            string id = BuildingId;
            SetOpen(false);
            BuildingRecord b = HomeGridService.FindBuilding(CampaignSession.Current, id);
            StrategicMapUIToolkit.BeginPick("ui.map.pick.drop", b != null ? b.Position : Vector2.zero,
                cell => PickPreview(DropPodService.Preview(CampaignSession.Current, id, cell, new List<int>(_selected), _cargoItem, _cargoAmountValue)),
                cell => { Open(id); SetTarget(cell); },
                () => Open(id));
        }

        public void ClickPickRecall()
        {
            string id = BuildingId;
            SetOpen(false);
            BuildingRecord b = HomeGridService.FindBuilding(CampaignSession.Current, id);
            StrategicMapUIToolkit.BeginPick("ui.map.pick.recall", b != null ? b.Position : Vector2.zero,
                cell => PickPreview(DropPodService.PreviewRecall(CampaignSession.Current, id, cell)),
                cell => { Open(id); SetRecallPoint(cell); },
                () => Open(id));
        }

        /// <summary>选点悬停的预览：被防空拒绝时“瞄准即探明”那处防空（地图下一次刷新画出它的红色区域，拒绝与红区同一口径）。</summary>
        private static string PickPreview(DropPreview p)
        {
            DropPodService.RevealAntiAir(CampaignSession.Current, p);
            return DropPodService.PreviewText(p);
        }

        public void SetTarget(Vector2 cell)
        {
            _target = new Vector2(Mathf.Round(cell.x), Mathf.Round(cell.y));
            _key = 0;
            Refresh(force: true);
        }

        public void SetRecallPoint(Vector2 cell)
        {
            _recall = new Vector2(Mathf.Round(cell.x), Mathf.Round(cell.y));
            _key = 0;
            Refresh(force: true);
        }

        public void ClickLaunch()
        {
            CampaignState s = CampaignSession.Current;
            if (_target == null)
            {
                Show(GameText.Get("drop.panel.need_target"), true);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, GameText.Get("drop.panel.need_target"));
                return;
            }
            bool ok = DropPodService.TryLaunch(s, BuildingId, _target.Value, new List<int>(_selected), _cargoItem, _cargoAmountValue, out DropPreview p);
            _lastPreview = p;
            if (!ok && p.AntiAir)
            {
                GuidanceHooks.Raise(GuidanceHooks.DropFirstAntiAirRejected);
            }
            if (ok)
            {
                // 发射成功：清掉这一批的装载（落点留着，方便连发同一个落点）。
                _selected.Clear();
                _cargoItem = string.Empty;
                _cargoAmountValue = 0;
            }
            Show(ok ? GameText.Format("drop.panel.launched", p.PodsNeeded, DropPodService.Duration(p.LegSeconds)) : p.Reason, !ok);
        }

        public void ClickRecall()
        {
            CampaignState s = CampaignSession.Current;
            if (_recall == null)
            {
                Show(GameText.Get("drop.panel.need_recall"), true);
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, GameText.Get("drop.panel.need_recall"));
                return;
            }
            bool ok = DropPodService.TryRecall(s, BuildingId, _recall.Value, out DropPreview p);
            _lastRecallPreview = p;
            if (!ok && p.AntiAir)
            {
                GuidanceHooks.Raise(GuidanceHooks.DropFirstAntiAirRejected);
            }
            Show(ok ? GameText.Format("drop.panel.recalled", p.PodsNeeded, DropPodService.Duration(p.LegSeconds)) : p.Reason, !ok);
        }

        private void Show(string text, bool warn)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("dp-message-warn", warn);
            _key = 0;
            Refresh(force: true);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            int key = HashCode.Combine(DropPodService.Revision, HomeInventory.Revision, MachineRegistry.RosterRevision, (int)GameText.Language,
                HashCode.Combine(GameSettings.Revision, BuildingId, GameClock.Ticks / Math.Max(1, GameClock.StepHz), _rowTemplate != null ? 1 : 0, _selected.Count, _cargoAmountValue));
            if (!force && key == _key)
            {
                return;
            }
            _key = key;
            BuildingRecord b = HomeGridService.FindBuilding(s, BuildingId);
            DropStationRecord st = DropPodService.FindStation(s, BuildingId);
            _title.text = b != null ? GameText.Format("drop.panel.title_named", BuildingOps.NameOf(b)) : GameText.Get("drop.panel.title");
            _help.text = GameText.Get("drop.panel.help");
            _close.text = GameText.Get("drop.panel.close");
            _secStatus.text = GameText.Get("drop.panel.sec.status");
            _secPayload.text = GameText.Format("drop.panel.sec.payload", DropPodService.CargoUnits);
            _secTarget.text = GameText.Get("drop.panel.sec.target");
            _secRecall.text = GameText.Get("drop.panel.sec.recall");
            _secFlying.text = GameText.Get("drop.panel.sec.flying");
            _footer.text = GameText.Get("drop.panel.footer");
            if (s == null || b == null || st == null)
            {
                _status.text = GameText.Get("drop.panel.not_ready");
                return;
            }
            _status.text = DropPodService.StatusOf(s, b).Reason;
            int stock = HomeInventory.Stock(s, DropPodService.PodItemId);
            _loadPods.text = GameText.Format("drop.panel.load", stock);
            _autoFill.text = GameText.Get(st.AutoFill ? "drop.panel.autofill_on" : "drop.panel.autofill_off");

            // 装载：机器。
            DropPodService.LoadableMachines(s, b, _machineScratch);
            _selected.RemoveWhere(id => !_machineScratch.Exists(m => m.LogicId == id));
            _machinesHint.text = GameText.Format("drop.panel.machines_hint", Mathf.RoundToInt(DropPodService.LoadRadius));
            RebuildRows(s);
            _machineEmpty.EnableInClassList("uk-hidden", _machineScratch.Count > 0);
            _machineEmpty.text = GameText.Format("drop.panel.machines_empty", Mathf.RoundToInt(DropPodService.LoadRadius));
            _machineScratch.Clear();
            // 装载：货物。
            DropPodService.CargoChoices(s, _itemScratch);
            _cargoIds.Clear();
            var choices = new List<string> { GameText.Get("drop.panel.cargo_none") };
            int sel = 0;
            foreach (ItemDef d in _itemScratch)
            {
                _cargoIds.Add(d.Id);
                choices.Add(GameText.Format("drop.panel.cargo_item", GameText.Get(d.NameKey), HomeInventory.Stock(s, d)));
                if (d.Id == _cargoItem)
                {
                    sel = _cargoIds.Count;
                }
            }
            _itemScratch.Clear();
            if (sel == 0 && !string.IsNullOrEmpty(_cargoItem))
            {
                _cargoItem = string.Empty;
                _cargoAmountValue = 0;
            }
            _suppress = true;
            DropdownChoices.Apply(_cargo, choices, GameText.Get("drop.panel.cargo_none"));
            _cargo.index = sel;
            _suppress = false;
            _cargoAmount.text = GameText.Format("drop.panel.cargo_amount", _cargoAmountValue, (_cargoAmountValue + DropPodService.CargoUnits - 1) / DropPodService.CargoUnits);
            _cargoMinus.text = GameText.Format("drop.panel.cargo_minus", DropPodService.CargoUnits);
            _cargoPlus.text = GameText.Format("drop.panel.cargo_plus", DropPodService.CargoUnits);
            _cargoMinus.SetEnabled(_cargoAmountValue > 0);
            _cargoPlus.SetEnabled(!string.IsNullOrEmpty(_cargoItem) && _cargoAmountValue < HomeInventory.Stock(s, _cargoItem));

            // 落点与预览。
            _pickTarget.text = GameText.Get("drop.panel.pick_target");
            _launch.text = GameText.Get("drop.panel.launch");
            if (_target.HasValue)
            {
                DropPreview p = DropPodService.Preview(s, BuildingId, _target.Value, new List<int>(_selected), _cargoItem, _cargoAmountValue);
                DropPodService.RevealAntiAir(s, p);
                _lastPreview = p;
                _targetLabel.text = GameText.Format("drop.panel.target", DropPodService.Coord(_target.Value.x, _target.Value.y));
                _preview.text = DropPodService.PreviewText(p);
                _preview.EnableInClassList("dp-preview-bad", !p.Ok);
                _launch.SetEnabled(p.Ok);
            }
            else
            {
                _targetLabel.text = GameText.Get("drop.panel.target_none");
                _preview.text = GameText.Format("drop.panel.range_hint", Mathf.RoundToInt(DropPodService.RangeCells));
                _preview.EnableInClassList("dp-preview-bad", false);
                _launch.SetEnabled(false);
            }
            // 回收。
            _pickRecall.text = GameText.Get("drop.panel.pick_recall");
            _recallGo.text = GameText.Get("drop.panel.recall");
            if (_recall.HasValue)
            {
                DropPreview r = DropPodService.PreviewRecall(s, BuildingId, _recall.Value);
                DropPodService.RevealAntiAir(s, r);
                _lastRecallPreview = r;
                _recallLabel.text = GameText.Format("drop.panel.recall_point", DropPodService.Coord(_recall.Value.x, _recall.Value.y));
                _recallPreview.text = DropPodService.PreviewText(r);
                _recallPreview.EnableInClassList("dp-preview-bad", !r.Ok);
                _recallGo.SetEnabled(r.Ok);
            }
            else
            {
                _recallLabel.text = GameText.Get("drop.panel.recall_none");
                _recallPreview.text = GameText.Format("drop.panel.recall_hint", Mathf.RoundToInt(DropPodService.RecallRadius));
                _recallPreview.EnableInClassList("dp-preview-bad", false);
                _recallGo.SetEnabled(false);
            }
            // 在飞的舱。
            DropPodService.PodLines(s, BuildingId, _lineScratch);
            _flying.text = _lineScratch.Count > 0 ? string.Join("\n", _lineScratch) : GameText.Get("drop.panel.flying_none");
            _lineScratch.Clear();
        }

        private void RebuildRows(CampaignState s)
        {
            while (_rowTemplate != null && _rows.Count < _machineScratch.Count)
            {
                TemplateContainer host = _rowTemplate.CloneTree();
                host.AddToClassList("dp-row-host");
                _machineList.Add(host);
                Button btn = host.Q<Button>("DpRow");
                int index = _rows.Count;
                btn.clicked += () => ClickMachineRow(index);
                _rows.Add(btn);
            }
            _rowIds.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool shown = i < _machineScratch.Count;
                Button row = _rows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                MachineRecord m = _machineScratch[i];
                _rowIds.Add(m.LogicId);
                bool on = _selected.Contains(m.LogicId);
                string extra = CargoSummary(m) + (DropPodService.IsBusy(s, m.LogicId) ? GameText.Get("drop.panel.machine_busy") : string.Empty);
                row.text = GameText.Format(on ? "drop.panel.machine_on" : "drop.panel.machine_off", MachineNaming.Short(m), MachineNaming.Model(m), extra);
                row.EnableInClassList("dp-row-selected", on);
            }
        }

        private static string CargoSummary(MachineRecord m)
        {
            int n = 0;
            if (m.Cargo != null)
            {
                foreach (CargoEntry c in m.Cargo)
                {
                    n += c != null ? c.Amount : 0;
                }
            }
            return n > 0 ? GameText.Format("drop.panel.machine_cargo", n) : string.Empty;
        }
    }
}
