using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG5-RND-03（FG05 FGR-RND-030～033；FG13 FGU-24 靶场：投影、靶子、读数、对比）：靶场面板。
    /// - 投影：选一张已保存的蓝图按“投影”（不消耗资源，测试随之开始）；每个投影一行，可以接入 / 退出接入、移除（全部靶场合计至多 6 个）。
    /// - 靶子：8 个靶位下拉（空位 + 已解锁的靶子，新解锁的标“新”）、清空、预设应用 / 保存 / 删除；未解锁的靶子列出解锁条件（B01 / B06）。测试进行中不能改布置（原因写明）。
    /// - 读数：测试进行中实时刷新（节流 0.25 秒）——时长、每秒伤害、击毁与发数、能耗、热量（现在 / 峰值 / 过热次数）与热量曲线、反应次数、标签覆盖率；“结束测试”。
    /// - 对比：记录 A / B 两个下拉（默认最近两条），逐项并排写出差值。
    /// 入口：靶场建筑面板“靶场…”、蓝图编辑器 / 固件库“送到靶场测试”（<see cref="Open"/>）。模态；Esc / 关闭 / 点遮罩关闭。
    /// 刷新：结构（下拉选项、行数）只在 <see cref="TestRangeService.Revision"/> / 语言 / 建筑变化时重建；读数节流刷新。O(投影数 + 靶位数 + 记录数)。
    /// </summary>
    public sealed class TestRangePanelUIToolkit : UiKitPanelHost
    {
        public const int Order = 30068;
        private const float ReadingsInterval = 0.25f;

        public static TestRangePanelUIToolkit Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public static string BuildingId { get; private set; }
        private static string _pendingId;

        /// <summary>自检：编辑模式下没有载入的地点，打开后不自动收起。</summary>
        public static bool InWorldOverrideForTests;

        private VisualElement _root;
        private Label _title;
        private Label _state;
        private Button _help;
        private Button _close;
        private Label _message;
        private Label _secProj;
        private DropdownField _blueprint;
        private Button _project;
        private Label _projEmpty;
        private VisualElement _projList;
        private Label _secTargets;
        private VisualElement _slots;
        private Button _clearSlots;
        private DropdownField _preset;
        private Button _presetApply;
        private Button _presetDelete;
        private TextField _presetName;
        private Button _presetSave;
        private Label _lockedTitle;
        private Label _locked;
        private Label _secRead;
        private Label _runState;
        private Button _end;
        private Label _readings;
        private Label _secCompare;
        private DropdownField _compareA;
        private DropdownField _compareB;
        private Label _compare;
        private Label _footer;

        private readonly List<string> _blueprintIds = new List<string>();
        private readonly List<string> _presetIds = new List<string>();
        private readonly List<int> _historySerials = new List<int>();
        /// <summary>这次打开时新解锁、还没看过的靶子（整个打开期间都标“新”；打开即记为看过，下次打开不再标）。</summary>
        private readonly HashSet<string> _newAtOpen = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<(VisualElement Row, Label Label, Button Uplink, Button Remove)> _projRows = new List<(VisualElement, Label, Button, Button)>();
        private readonly List<int> _projSerials = new List<int>();
        private readonly List<(Label Label, DropdownField Field)> _slotRows = new List<(Label, DropdownField)>();
        private readonly List<List<string>> _slotIds = new List<List<string>>();
        private int _key;
        private float _nextReadings;
        private bool _suppress;
        private VisualTreeAsset _projRowTemplate;
        private VisualTreeAsset _slotRowTemplate;
        private bool _templatesLoading;

        protected override string UxmlLocation => "TestRangePanel";
        protected override int SortingOrder => Order;

        // ── 自检读点 ──
        public bool PanelVisible => _root != null && !_root.ClassListContains("uk-hidden");
        public string TitleText => _title?.text ?? string.Empty;
        public string StateText => _state?.text ?? string.Empty;
        public string MessageText => _message != null && !_message.ClassListContains("uk-hidden") ? _message.text : string.Empty;
        public string ReadingsText => _readings?.text ?? string.Empty;
        public string CompareText => _compare?.text ?? string.Empty;
        public string RunStateText => _runState?.text ?? string.Empty;
        public string LockedText => _locked?.text ?? string.Empty;
        public string FooterText => _footer?.text ?? string.Empty;
        public string ProjectionsEmptyText => _projEmpty != null && !_projEmpty.ClassListContains("uk-hidden") ? _projEmpty.text : string.Empty;
        public IReadOnlyList<string> BlueprintChoices => _blueprint?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public int ProjectionRowCount => _projSerials.Count;
        public string ProjectionRowText(int i) => i >= 0 && i < _projSerials.Count ? _projRows[i].Label.text : string.Empty;
        public Button ProjectionUplinkButton(int i) => i >= 0 && i < _projSerials.Count ? _projRows[i].Uplink : null;
        public Button ProjectionRemoveButton(int i) => i >= 0 && i < _projSerials.Count ? _projRows[i].Remove : null;
        public int SlotRowCount => _slotRows.Count;
        public IReadOnlyList<string> SlotChoices(int slot) => slot >= 0 && slot < _slotRows.Count ? _slotRows[slot].Field.choices : (IReadOnlyList<string>)Array.Empty<string>();
        public string SlotValue(int slot) => slot >= 0 && slot < _slotRows.Count ? _slotRows[slot].Field.value : string.Empty;
        public bool SlotEnabled(int slot) => slot >= 0 && slot < _slotRows.Count && _slotRows[slot].Field.enabledSelf;
        public IReadOnlyList<string> PresetChoices => _preset?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<string> CompareChoices => _compareA?.choices ?? (IReadOnlyList<string>)Array.Empty<string>();
        public Button ProjectButton => _project;
        public Button EndButton => _end;
        public Button CloseButton => _close;
        public Button HelpButton => _help;
        public VisualElement RootElement => _root;

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
            if (_projRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_projRowTemplate);
                _projRowTemplate = null;
            }
            if (_slotRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_slotRowTemplate);
                _slotRowTemplate = null;
            }
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>打开某座靶场的面板（已开着就切到这座）。</summary>
        public static void Open(string buildingId)
        {
            TestRangeService.LastRangeId = buildingId;
            if (Instance == null || Instance._root == null)
            {
                _pendingId = buildingId;
                BuildingId = buildingId;
                return;
            }
            BuildingId = buildingId;
            Instance._key = 0;
            if (IsOpen)
            {
                Instance.Refresh(force: true);
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
            LoadRowTemplates().Forget();
            if (_pendingId != null)
            {
                string id = _pendingId;
                _pendingId = null;
                Open(id);
            }
        }

        /// <summary>投影行 / 靶位行模板（可变数量的行由模板克隆，红线 5；资源成对释放见 <see cref="OnDestroy"/>）。</summary>
        private async UniTaskVoid LoadRowTemplates()
        {
            if (_templatesLoading || (_projRowTemplate != null && _slotRowTemplate != null))
            {
                return;
            }
            _templatesLoading = true;
            VisualTreeAsset proj = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("TestRangeProjRow");
            VisualTreeAsset slot = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("TestRangeSlotRow");
            _templatesLoading = false;
            if (this == null)
            {
                if (proj != null)
                {
                    GameModule.Resource.UnloadAsset(proj);
                }
                if (slot != null)
                {
                    GameModule.Resource.UnloadAsset(slot);
                }
                return;
            }
            _projRowTemplate = proj;
            _slotRowTemplate = slot;
            if (proj == null || slot == null)
            {
                Log.Error("[TestRangePanelUIToolkit] 加载 TestRangeProjRow / TestRangeSlotRow 失败，投影行 / 靶位行不可用。");
                return;
            }
            _key = 0;
            if (IsOpen)
            {
                Refresh(force: true);
            }
        }

        /// <summary>自检：编辑模式下直接给行模板（正式流程由 YooAsset 异步加载）。</summary>
        public void SetRowTemplatesForTests(VisualTreeAsset projRow, VisualTreeAsset slotRow)
        {
            _projRowTemplate = projRow;
            _slotRowTemplate = slotRow;
            _key = 0;
        }

        /// <summary>自检：按选项序号选下拉框（与玩家点选同一回调：选中即生效）。</summary>
        public static bool PickForTests(DropdownField field, int index)
        {
            if (field == null || index < 0 || index >= field.choices.Count)
            {
                return false;
            }
            field.value = field.choices[index];
            return true;
        }

        public DropdownField SlotField(int slot) => slot >= 0 && slot < _slotRows.Count ? _slotRows[slot].Field : null;
        public DropdownField BlueprintField => _blueprint;
        public DropdownField PresetField => _preset;
        public DropdownField CompareField(bool slotA) => slotA ? _compareA : _compareB;

        public void BindView(VisualElement root)
        {
            Instance = this;
            _root = root.Q<VisualElement>("TestRangeRoot");
            _title = root.Q<Label>("TestRangeTitle");
            _state = root.Q<Label>("TestRangeState");
            _help = root.Q<Button>("TestRangeHelp");
            _close = root.Q<Button>("TestRangeClose");
            _message = root.Q<Label>("TestRangeMessage");
            _secProj = root.Q<Label>("TestRangeSecProj");
            _blueprint = root.Q<DropdownField>("TestRangeBlueprint");
            _project = root.Q<Button>("TestRangeProject");
            _projEmpty = root.Q<Label>("TestRangeProjEmpty");
            _projList = root.Q<VisualElement>("TestRangeProjList");
            _secTargets = root.Q<Label>("TestRangeSecTargets");
            _slots = root.Q<VisualElement>("TestRangeSlots");
            _clearSlots = root.Q<Button>("TestRangeClearSlots");
            _preset = root.Q<DropdownField>("TestRangePreset");
            _presetApply = root.Q<Button>("TestRangePresetApply");
            _presetDelete = root.Q<Button>("TestRangePresetDelete");
            _presetName = root.Q<TextField>("TestRangePresetName");
            _presetSave = root.Q<Button>("TestRangePresetSave");
            _lockedTitle = root.Q<Label>("TestRangeLockedTitle");
            _locked = root.Q<Label>("TestRangeLocked");
            _secRead = root.Q<Label>("TestRangeSecRead");
            _runState = root.Q<Label>("TestRangeRunState");
            _end = root.Q<Button>("TestRangeEnd");
            _readings = root.Q<Label>("TestRangeReadings");
            _secCompare = root.Q<Label>("TestRangeSecCompare");
            _compareA = root.Q<DropdownField>("TestRangeCompareA");
            _compareB = root.Q<DropdownField>("TestRangeCompareB");
            _compare = root.Q<Label>("TestRangeCompare");
            _footer = root.Q<Label>("TestRangeFooter");

            _close.clicked += () => SetOpen(false);
            _help.clicked += OpenCodex;
            _project.clicked += ClickProject;
            _end.clicked += ClickEnd;
            _clearSlots.clicked += ClickClearSlots;
            _presetApply.clicked += ClickApplyPreset;
            _presetDelete.clicked += ClickDeletePreset;
            _presetSave.clicked += ClickSavePreset;
            _compareA.RegisterValueChangedCallback(_ => OnCompareChanged(true));
            _compareB.RegisterValueChangedCallback(_ => OnCompareChanged(false));
            UiTooltip.Attach(_project, () => new TooltipContent { Title = _project.text, Body = GameText.Get("range.send_tip") });
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
                GuidanceHooks.Raise(GuidanceHooks.RangeFirstOpen);
                InputRouter.PushModal(this);
                UiEscapeStack.Push(this, () => SetOpen(false));
                _key = 0;
                SetMessage(string.Empty, false);
                _newAtOpen.Clear();
                foreach (RangeTargetDef d in TestRangeCatalog.Targets)
                {
                    if (TestRangeService.IsNewTarget(CampaignSession.Current, d))
                    {
                        _newAtOpen.Add(d.Id);
                    }
                }
                TestRangeService.MarkTargetsSeen(CampaignSession.Current); // 记为看过（这次打开期间仍标“新”，下次打开不再标）。
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

        // ─────────────────────────────── 操作（按钮 / 下拉，自检直接调）───────────────────────────────

        public void SelectBlueprint(int index)
        {
            if (index >= 0 && index < _blueprint.choices.Count)
            {
                _blueprint.index = index;
            }
        }

        public void ClickProject()
        {
            int i = _blueprint.index;
            string id = i >= 0 && i < _blueprintIds.Count ? _blueprintIds[i] : null;
            Show(TestRangeService.ProjectBlueprint(CampaignSession.Current, BuildingId, id));
        }

        public void ClickUplink(int row)
        {
            if (row < 0 || row >= _projSerials.Count)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            bool uplinked = TestRangeService.TryGetUplinkedProjection(out string bid, out int serial, out _) && bid == BuildingId && serial == _projSerials[row];
            Show(uplinked ? TestRangeService.LeaveUplink(s) : TestRangeService.Uplink(s, BuildingId, _projSerials[row]));
        }

        public void ClickRemove(int row)
        {
            if (row >= 0 && row < _projSerials.Count)
            {
                Show(TestRangeService.RemoveProjection(CampaignSession.Current, BuildingId, _projSerials[row]));
            }
        }

        public void ClickEnd() => Show(TestRangeService.EndTest(CampaignSession.Current, BuildingId));

        public void ClickClearSlots() => Show(TestRangeService.ClearSlots(CampaignSession.Current, BuildingId));

        /// <summary>靶位下拉选第 <paramref name="index"/> 项（0 = 空位）。</summary>
        public void ChooseSlot(int slot, int index)
        {
            if (slot < 0 || slot >= _slotRows.Count || index < 0 || index >= _slotIds[slot].Count)
            {
                return;
            }
            Show(TestRangeService.SetSlot(CampaignSession.Current, BuildingId, slot, _slotIds[slot][index]));
        }

        public void SelectPreset(int index)
        {
            if (index >= 0 && index < _preset.choices.Count)
            {
                _preset.index = index;
            }
        }

        public void SetPresetName(string name) => _presetName.value = name ?? string.Empty;

        public void ClickSavePreset()
        {
            RangeOpResult r = TestRangeService.SavePreset(CampaignSession.Current, BuildingId, _presetName.value);
            if (r.Success)
            {
                _presetName.SetValueWithoutNotify(string.Empty);
            }
            Show(r);
        }

        public void ClickApplyPreset()
        {
            int i = _preset.index;
            Show(TestRangeService.ApplyPreset(CampaignSession.Current, BuildingId, i >= 0 && i < _presetIds.Count ? _presetIds[i] : null));
        }

        public void ClickDeletePreset()
        {
            int i = _preset.index;
            Show(TestRangeService.DeletePreset(CampaignSession.Current, i >= 0 && i < _presetIds.Count ? _presetIds[i] : null));
        }

        public void SelectCompare(bool slotA, int index)
        {
            DropdownField f = slotA ? _compareA : _compareB;
            if (index >= 0 && index < f.choices.Count)
            {
                f.index = index;
            }
        }

        private void OnCompareChanged(bool slotA)
        {
            if (_suppress)
            {
                return;
            }
            DropdownField f = slotA ? _compareA : _compareB;
            int i = f.index;
            if (i >= 0 && i < _historySerials.Count)
            {
                Show(TestRangeService.SetCompare(CampaignSession.Current, slotA, _historySerials[i]));
            }
        }

        public void OpenCodex() => MechanicCodex.Open("codex.research.range");

        private void Show(RangeOpResult r)
        {
            SetMessage(r.Text, !r.Success);
            if (!r.Success)
            {
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied);
            }
            _key = 0;
            Refresh(force: true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null)
            {
                return;
            }
            _message.text = text ?? string.Empty;
            _message.EnableInClassList("uk-hidden", string.IsNullOrEmpty(text));
            _message.EnableInClassList("tr-message-error", error);
        }

        // ─────────────────────────────── 刷新 ───────────────────────────────

        public void Refresh(bool force)
        {
            if (_root == null)
            {
                return;
            }
            CampaignState s = CampaignSession.Current;
            BuildingRecord b = TestRangeService.FindRange(s, BuildingId);
            bool running = TestRangeService.IsRunning(BuildingId);
            int key = HashCode.Combine(TestRangeService.Revision, (int)GameText.Language, GameSettings.Revision, BuildingId, b?.ConstructionState ?? 0, b?.PowerState ?? 0,
                running, s != null ? s.GetHashCode() : 0);
            if (force || key != _key)
            {
                _key = key;
                RebuildStructure(s, b, running);
                _nextReadings = 0f;
            }
            float now = Time.unscaledTime;
            if (force || now >= _nextReadings || !Application.isPlaying)
            {
                _nextReadings = now + ReadingsInterval;
                RefreshReadings(s, running);
            }
        }

        private void RebuildStructure(CampaignState s, BuildingRecord b, bool running)
        {
            _suppress = true;
            try
            {
                string name = b != null ? Campaign.Economy.BuildingOps.NameOf(b) : GameText.Get("building.test_range.name");
                _title.text = GameText.Format("range.panel.title", name);
                _close.text = GameText.Get("range.panel.close");
                _help.text = GameText.Get("prod.panel.help");
                bool usable = TestRangeService.IsUsable(s, b, out string why);
                _state.text = usable ? BuildingStatusService.Evaluate(s, b).Reason : GameText.Format("range.panel.state_unusable", why);
                _secProj.text = GameText.Format("range.panel.sec.projections", TestRangeService.ProjectionCount, TestRangeCatalog.ProjectionCap);
                _secTargets.text = GameText.Get("range.panel.sec.targets");
                _secRead.text = GameText.Get("range.panel.sec.readings");
                _secCompare.text = GameText.Get("range.panel.sec.compare");
                _project.text = GameText.Get("range.panel.project");
                _end.text = GameText.Get("range.panel.end");
                _clearSlots.text = GameText.Get("range.panel.clear_slots");
                _presetApply.text = GameText.Get("range.panel.preset_apply");
                _presetDelete.text = GameText.Get("range.panel.preset_delete");
                _presetSave.text = GameText.Get("range.panel.preset_save");
                _presetName.label = GameText.Get("range.panel.preset_name");
                _lockedTitle.text = GameText.Get("range.panel.locked_title");
                _footer.text = InputDisplay.ExpandActionTokens(GameText.Get("range.footer"));
                _compareA.label = GameText.Get("range.compare.a");
                _compareB.label = GameText.Get("range.compare.b");
                _blueprint.label = GameText.Get("range.panel.blueprint");
                _preset.label = GameText.Get("range.panel.preset");

                // 蓝图下拉
                string keep = _blueprint.index >= 0 && _blueprint.index < _blueprintIds.Count ? _blueprintIds[_blueprint.index] : null;
                _blueprintIds.Clear();
                var bpChoices = new List<string>();
                foreach (BlueprintRecord bp in TestRangeService.SavedBlueprints(s))
                {
                    _blueprintIds.Add(bp.BlueprintId);
                    bpChoices.Add(TestRangeService.BlueprintName(bp));
                }
                int bi = keep != null ? _blueprintIds.IndexOf(keep) : -1;
                DropdownChoices.Apply(_blueprint, bpChoices, GameText.Get("range.panel.blueprint_none"));
                if (bpChoices.Count > 0)
                {
                    _blueprint.SetValueWithoutNotify(_blueprint.choices[Math.Max(0, bi)]);
                }
                _project.SetEnabled(usable && bpChoices.Count > 0);

                // 投影行
                RebuildProjections(s);

                // 靶位
                string[] layout = TestRangeService.Layout(s, BuildingId);
                while (_slotRowTemplate != null && _slotRows.Count < layout.Length)
                {
                    int slot = _slotRows.Count;
                    TemplateContainer host = _slotRowTemplate.CloneTree();
                    host.AddToClassList("tr-row-host");
                    var label = host.Q<Label>("TrsLabel");
                    var field = host.Q<DropdownField>("TrsField");
                    field.RegisterValueChangedCallback(_ =>
                    {
                        if (!_suppress)
                        {
                            ChooseSlot(slot, field.index);
                        }
                    });
                    _slots.Add(host);
                    _slotRows.Add((label, field));
                    var slotIds = new List<string>();
                    _slotIds.Add(slotIds);
                    // 悬停靶位：这种靶子的说明（B13 tooltip；按当前选中项现取）。
                    UiTooltip.Attach(field, () =>
                    {
                        int k = field.index;
                        string id = k >= 0 && k < slotIds.Count ? slotIds[k] : null;
                        return TestRangeCatalog.TryGet(id, out RangeTargetDef d) ? new TooltipContent { Title = d.Name, Body = d.Description } : new TooltipContent { Title = field.value };
                    });
                }
                for (int i = 0; i < _slotRows.Count; i++)
                {
                    bool shown = i < layout.Length;
                    _slotRows[i].Label.parent.parent.EnableInClassList("uk-hidden", !shown);
                    if (!shown)
                    {
                        continue;
                    }
                    _slotRows[i].Label.text = GameText.Format("range.panel.slot", i + 1);
                    List<string> ids = _slotIds[i];
                    ids.Clear();
                    ids.Add(string.Empty);
                    var choices = new List<string> { GameText.Get("range.panel.slot_empty") };
                    foreach (RangeTargetDef d in TestRangeCatalog.Targets)
                    {
                        if (TestRangeService.IsUnlocked(s, d))
                        {
                            ids.Add(d.Id);
                            choices.Add(_newAtOpen.Contains(d.Id) ? GameText.Format("range.panel.new", d.Name) : d.Name);
                        }
                    }
                    DropdownField f = _slotRows[i].Field;
                    DropdownChoices.Apply(f, choices, GameText.Get("range.panel.slot_empty"));
                    int sel = ids.IndexOf(layout[i] ?? string.Empty);
                    f.SetValueWithoutNotify(f.choices[sel >= 0 ? sel : 0]);
                    f.SetEnabled(!running);
                }
                _clearSlots.SetEnabled(!running);

                // 未解锁
                var locked = new StringBuilder();
                foreach (RangeTargetDef d in TestRangeCatalog.Targets)
                {
                    if (!TestRangeService.IsUnlocked(s, d))
                    {
                        if (locked.Length > 0)
                        {
                            locked.Append('\n');
                        }
                        locked.Append(GameText.Format("range.panel.locked_line", d.Name, TestRangeService.UnlockText(d)));
                    }
                }
                _locked.text = locked.Length > 0 ? locked.ToString() : GameText.Get("range.panel.locked_none");

                // 预设
                _presetIds.Clear();
                var pChoices = new List<string>();
                foreach (RangePresetRecord p in TestRangeService.Presets(s))
                {
                    if (p != null)
                    {
                        _presetIds.Add(p.PresetId);
                        pChoices.Add(p.Name);
                    }
                }
                int pi = Math.Min(Math.Max(0, _preset.index), Math.Max(0, pChoices.Count - 1));
                DropdownChoices.Apply(_preset, pChoices, GameText.Get("range.panel.preset_none"));
                if (pChoices.Count > 0)
                {
                    _preset.SetValueWithoutNotify(_preset.choices[pi]);
                }
                _presetApply.SetEnabled(pChoices.Count > 0 && !running);
                _presetDelete.SetEnabled(pChoices.Count > 0);
                _presetSave.SetEnabled(b != null);

                // 对比
                RebuildCompare(s);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void RebuildProjections(CampaignState s)
        {
            List<RangeProjectionView> views = TestRangeService.ProjectionsOf(BuildingId);
            while (_projRowTemplate != null && _projRows.Count < views.Count)
            {
                int rowIndex = _projRows.Count;
                TemplateContainer host = _projRowTemplate.CloneTree();
                host.AddToClassList("tr-row-host");
                var row = host.Q<VisualElement>("TrpRow");
                var label = host.Q<Label>("TrpLabel");
                var up = host.Q<Button>("TrpUplink");
                var rm = host.Q<Button>("TrpRemove");
                up.clicked += () => ClickUplink(rowIndex);
                rm.clicked += () => ClickRemove(rowIndex);
                _projList.Add(host);
                _projRows.Add((row, label, up, rm));
            }
            _projSerials.Clear();
            for (int i = 0; i < _projRows.Count; i++)
            {
                bool shown = i < views.Count;
                (VisualElement row, Label label, Button up, Button rm) = _projRows[i];
                row.parent.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                RangeProjectionView v = views[i];
                _projSerials.Add(v.Serial);
                string text = GameText.Format("range.panel.projection_row", v.Serial, v.Label);
                if (v.Uplinked)
                {
                    text = GameText.Format("range.panel.projection_uplinked", text);
                }
                else if (v.CoreRig)
                {
                    text = GameText.Format("range.panel.projection_rig", text);
                }
                label.text = text;
                row.EnableInClassList("tr-prow-uplinked", v.Uplinked);
                up.text = GameText.Get(v.Uplinked ? "range.panel.leave" : "range.panel.uplink");
                up.SetEnabled(!v.CoreRig);
                rm.text = GameText.Get("range.panel.remove");
            }
            bool empty = views.Count == 0;
            _projEmpty.EnableInClassList("uk-hidden", !empty);
            _projEmpty.text = empty ? GameText.Get("range.panel.projections_empty") : string.Empty;
        }

        private void RebuildCompare(CampaignState s)
        {
            IReadOnlyList<RangeResultRecord> history = TestRangeService.History(s);
            _historySerials.Clear();
            var choices = new List<string>();
            for (int i = history.Count - 1; i >= 0; i--)
            {
                RangeResultRecord h = history[i];
                if (h == null)
                {
                    continue;
                }
                _historySerials.Add(h.Serial);
                choices.Add(GameText.Format("range.compare.option", h.Serial, GameClock.FormatDayTime(h.StartTick / (double)Math.Max(1, GameClock.StepHz)),
                    TestRangeService.ResultSummary(h), TestRangeService.Num(h.Dps)));
            }
            bool ok = TestRangeService.ComparePair(s, out RangeResultRecord a, out RangeResultRecord b);
            DropdownChoices.Apply(_compareA, choices, GameText.Get("range.compare.none"));
            DropdownChoices.Apply(_compareB, new List<string>(choices), GameText.Get("range.compare.none"));
            if (ok)
            {
                _compareA.SetValueWithoutNotify(_compareA.choices[Math.Max(0, _historySerials.IndexOf(a.Serial))]);
                _compareB.SetValueWithoutNotify(_compareB.choices[Math.Max(0, _historySerials.IndexOf(b.Serial))]);
            }
            _compareA.SetEnabled(ok);
            _compareB.SetEnabled(ok);
            _compare.text = ok ? CompareLines(a, b) : GameText.Get("range.compare.empty");
        }

        private void RefreshReadings(CampaignState s, bool running)
        {
            if (running)
            {
                _runState.text = GameText.Format("range.panel.running", Mathf.FloorToInt(TestRangeService.ElapsedSeconds(BuildingId)), Mathf.RoundToInt(TestRangeCatalog.MaxTestSeconds));
                RangeResultRecord live = TestRangeService.LiveReadings(BuildingId);
                List<RangeProjectionView> views = TestRangeService.ProjectionsOf(BuildingId);
                float heatNow = 0f;
                foreach (RangeProjectionView v in views)
                {
                    heatNow = Mathf.Max(heatNow, v.Heat);
                }
                _readings.text = ReadingLines(live, heatNow);
                _end.SetEnabled(true);
                _secProj.text = GameText.Format("range.panel.sec.projections", TestRangeService.ProjectionCount, TestRangeCatalog.ProjectionCap);
            }
            else
            {
                _runState.text = GameText.Get("range.panel.idle");
                IReadOnlyList<RangeResultRecord> history = TestRangeService.History(s);
                RangeResultRecord last = history.Count > 0 ? history[history.Count - 1] : null;
                _readings.text = last != null && last.BuildingId == BuildingId ? ReadingLines(last, 0f) : string.Empty;
                _end.SetEnabled(false);
            }
        }

        /// <summary>一次测试的读数（实时与记录同一种写法）。</summary>
        public static string ReadingLines(RangeResultRecord r, float heatNow)
        {
            if (r == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            sb.AppendLine(GameText.Format("range.read.time", TestRangeService.Num(r.Seconds)));
            sb.AppendLine(GameText.Format("range.read.dps", TestRangeService.Num(r.Dps), TestRangeService.Num(r.Damage)));
            sb.AppendLine(GameText.Format("range.read.kills", r.Kills, r.Shots));
            sb.AppendLine(GameText.Format("range.read.energy", TestRangeService.Num(r.Energy)));
            sb.AppendLine(GameText.Format("range.read.heat", TestRangeService.Num(heatNow), TestRangeService.Num(r.PeakHeat), r.Overheats));
            sb.AppendLine(GameText.Format("range.read.curve", Sparkline(r.HeatCurve)));
            sb.AppendLine(ReactionsLine(r));
            sb.Append(CoverageLine(r));
            return sb.ToString();
        }

        public static string ReactionsLine(RangeResultRecord r)
        {
            if (r?.Reactions == null || r.Reactions.Length == 0)
            {
                return GameText.Get("range.read.reactions_none");
            }
            var parts = new List<string>(r.Reactions.Length);
            foreach (RangeCountRecord c in r.Reactions)
            {
                parts.Add(GameText.Format("range.read.reaction_item", TestRangeService.ReactionName(c.Id), c.Value));
            }
            return GameText.Format("range.read.reactions", string.Join(GameText.Get("range.read.sep"), parts));
        }

        public static string CoverageLine(RangeResultRecord r)
        {
            if (r?.Coverage == null || r.Coverage.Length == 0)
            {
                return GameText.Get("range.read.coverage_none");
            }
            var parts = new List<string>(r.Coverage.Length);
            foreach (RangeCountRecord c in r.Coverage)
            {
                parts.Add(GameText.Format("range.read.coverage_item", TestRangeService.TagName(c.Id), (c.Value / 10f).ToString("0.#", CultureInfo.InvariantCulture)));
            }
            return GameText.Format("range.read.coverage", string.Join(GameText.Get("range.read.sep"), parts));
        }

        private static readonly char[] Bars = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        /// <summary>热量曲线（最近 40 个点；满格 = 过热线）。形状表达高低，不靠颜色（B15）。</summary>
        public static string Sparkline(float[] curve)
        {
            if (curve == null || curve.Length == 0)
            {
                return "—";
            }
            float max = Mathf.Max(1f, FracturedCityLayout.WeaponHeatOverheatThreshold);
            int start = Math.Max(0, curve.Length - 40);
            var sb = new StringBuilder(curve.Length - start);
            for (int i = start; i < curve.Length; i++)
            {
                int k = Mathf.Clamp(Mathf.FloorToInt(curve[i] / max * (Bars.Length - 1) + 0.5f), 0, Bars.Length - 1);
                sb.Append(Bars[k]);
            }
            return sb.ToString();
        }

        /// <summary>两次测试并排：每项“A ｜ B（差值）”。</summary>
        public static string CompareLines(RangeResultRecord a, RangeResultRecord b)
        {
            var sb = new StringBuilder();
            Row(sb, "range.compare.projections", TestRangeService.ResultSummary(a), TestRangeService.ResultSummary(b), null);
            Row(sb, "range.compare.targets", TargetsSummary(a), TargetsSummary(b), null);
            Row(sb, "range.compare.time", TestRangeService.Num(a.Seconds), TestRangeService.Num(b.Seconds), b.Seconds - a.Seconds);
            Row(sb, "range.compare.dps", TestRangeService.Num(a.Dps), TestRangeService.Num(b.Dps), b.Dps - a.Dps);
            Row(sb, "range.compare.damage", TestRangeService.Num(a.Damage), TestRangeService.Num(b.Damage), b.Damage - a.Damage);
            Row(sb, "range.compare.kills", a.Kills.ToString(CultureInfo.InvariantCulture), b.Kills.ToString(CultureInfo.InvariantCulture), b.Kills - a.Kills);
            Row(sb, "range.compare.energy", TestRangeService.Num(a.Energy), TestRangeService.Num(b.Energy), b.Energy - a.Energy);
            Row(sb, "range.compare.heat", TestRangeService.Num(a.PeakHeat), TestRangeService.Num(b.PeakHeat), b.PeakHeat - a.PeakHeat);
            Row(sb, "range.compare.overheats", a.Overheats.ToString(CultureInfo.InvariantCulture), b.Overheats.ToString(CultureInfo.InvariantCulture), b.Overheats - a.Overheats);
            Row(sb, "range.compare.reactions", ReactionCount(a).ToString(CultureInfo.InvariantCulture), ReactionCount(b).ToString(CultureInfo.InvariantCulture),
                ReactionCount(b) - ReactionCount(a));
            Row(sb, "range.compare.coverage", TopCoverage(a), TopCoverage(b), null);
            Row(sb, "range.compare.ended", TestRangeService.EndReasonText(a.EndReason), TestRangeService.EndReasonText(b.EndReason), null);
            return sb.ToString().TrimEnd('\n', '\r');
        }

        private static void Row(StringBuilder sb, string labelKey, string a, string b, float? delta)
        {
            string d = delta == null ? (a == b ? GameText.Get("range.compare.same") : "—")
                : Mathf.Abs(delta.Value) < 0.05f ? GameText.Get("range.compare.same")
                : (delta.Value > 0 ? "+" : "−") + TestRangeService.Num(Mathf.Abs(delta.Value));
            sb.AppendLine(GameText.Format("range.compare.row", GameText.Get(labelKey), a, b, d));
        }

        private static int ReactionCount(RangeResultRecord r)
        {
            int n = 0;
            if (r?.Reactions != null)
            {
                foreach (RangeCountRecord c in r.Reactions)
                {
                    n += c.Value;
                }
            }
            return n;
        }

        private static string TopCoverage(RangeResultRecord r)
        {
            if (r?.Coverage == null || r.Coverage.Length == 0)
            {
                return GameText.Get("range.compare.none");
            }
            RangeCountRecord c = r.Coverage[0];
            return GameText.Format("range.read.coverage_item", TestRangeService.TagName(c.Id), (c.Value / 10f).ToString("0.#", CultureInfo.InvariantCulture));
        }

        /// <summary>“标准靶×4、重甲靶（铸造）×1”。</summary>
        public static string TargetsSummary(RangeResultRecord r)
        {
            if (r?.Targets == null)
            {
                return GameText.Get("range.compare.none");
            }
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (string id in r.Targets)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!counts.ContainsKey(id))
                {
                    counts[id] = 0;
                    order.Add(id);
                }
                counts[id]++;
            }
            if (order.Count == 0)
            {
                return GameText.Get("range.compare.none");
            }
            var parts = new List<string>(order.Count);
            foreach (string id in order)
            {
                string name = TestRangeCatalog.TryGet(id, out RangeTargetDef d) ? d.Name : id;
                parts.Add(GameText.Format("range.read.reaction_item", name, counts[id]));
            }
            return string.Join(GameText.Get("range.read.sep"), parts);
        }
    }
}
