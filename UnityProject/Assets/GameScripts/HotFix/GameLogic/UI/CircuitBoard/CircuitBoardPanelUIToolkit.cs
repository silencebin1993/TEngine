using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.Stage;
using GameLogic.UI.Common;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.CircuitBoard
{
    /// <summary>ER4-BLP-01 STORY-EXECUTION-CARDS.md："编辑器提供新建/打开/复制/另存/保存/取消/恢复草稿/
    /// 归档入口；外层底盘1、主组件1、功能0～1、结构0～1、有序固件2槽，另有已接好的3×3电路板、基元仓与
    /// 合成台入口"。本类是 ER4-PRIM-02 起就存在的电路板面板的正式升级——原面板只覆盖 3×3 电路内层，
    /// 外层槽（底盘/主组件/功能/结构）此前只能由 <c>BlueprintCircuitDefaults</c> 预置四条固定蓝图，玩家
    /// 无法真正新建/复制/改外层装配；本 Story 补齐这一层，CRUD 编排交给
    /// <see cref="BlueprintEditorService"/>，本类只做数据绑定与事件（UI Toolkit 硬规则）。
    ///
    /// 面板开关状态仍由 <see cref="HomeValleyController.IsCircuitBoardPanelOpen"/> 持有；所有装/卸/画边/
    /// 固件/外层槽/撤销重做/CRUD 操作都通过 <see cref="BlueprintCircuitBoard"/>/<see cref="BlueprintEditorService"/>
    /// 完成——本类不直接改 <c>BlueprintRecord</c>/<c>BlueprintVersionRecord</c> 字段。</summary>
    public sealed class CircuitBoardPanelUIToolkit : MonoBehaviour
    {
        private const int MaxIssueRows = 10;
        private const int MaxPathRows = BlueprintCircuitLayout.MaxPaths;
        private const int MaxBlueprintRows = 24;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _issueRowTemplate;
        private VisualTreeAsset _pathRowTemplate;
        private VisualTreeAsset _blueprintRowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Button _entryToggleButton;
        private VisualElement _panel;

        // ── 蓝图列表 / CRUD ──────────────────────────────────────────────────────
        private ScrollView _blueprintListScroll;
        private readonly List<Button> _blueprintRowPool = new List<Button>(MaxBlueprintRows);
        private readonly List<string> _blueprintIdsByRowIndex = new List<string>(MaxBlueprintRows);
        private TextField _newBlueprintNameField;
        private Button _newBlueprintButton;
        private Button _duplicateBlueprintButton;
        private Button _saveAsButton;
        private Button _archiveButton;
        private Button _cancelDraftButton;
        private Button _restoreDraftButton;
        private Label _activeBlueprintLabel;

        // ── 外层槽 ───────────────────────────────────────────────────────────────
        private DropdownField _chassisDropdown;
        private DropdownField _primaryDropdown;
        private DropdownField _utilityDropdown;
        private DropdownField _structureDropdown;
        private Label _lockedContentHintLabel;
        private readonly List<string> _chassisIdsByIndex = new List<string>();
        private readonly List<string> _primaryIdsByIndex = new List<string>();
        private readonly List<string> _utilityIdsByIndex = new List<string>();
        private readonly List<string> _structureIdsByIndex = new List<string>();

        private readonly Button[] _slotButtons = new Button[BlueprintCircuitLayout.SlotCount];
        private Label _selectedSlotLabel;
        private Label _selectedSlotDetailLabel;

        /// <summary>网格上两格之间的导线接口：横向 EdgeH_行_列 连 (行,列)-(行,列+1)，纵向 EdgeV_行_列 连
        /// (行,列)-(行+1,列)。A 恒为左/上格、B 恒为右/下格，点击在 无→A→B→B→A→双向→无 之间循环。</summary>
        private readonly List<(Button Button, int A, int B, bool Horizontal)> _edgeButtons = new List<(Button, int, int, bool)>();

        private DropdownField _bagChipDropdown;
        private Button _equipChipButton;
        private Button _removeChipButton;
        private Label _sourceLabel;
        private Label _bagCapacityLabel;
        private Label _pendingLabel;
        private DropdownField _pendingChipDropdown;
        private Button _claimPendingButton;
        private Button _printChipButton;
        private Label _bagResultLabel;

        private readonly List<string> _bagPartIdsByDropdownIndex = new List<string>();
        private readonly List<string> _pendingPartIdsByDropdownIndex = new List<string>();

        private Label _edgeListLabel;

        private DropdownField _firmware0Dropdown;
        private DropdownField _firmware1Dropdown;
        private readonly List<string> _firmware0IdsByIndex = new List<string>();
        private readonly List<string> _firmware1IdsByIndex = new List<string>();

        private Button _undoButton;
        private Button _redoButton;
        private Label _historyDepthLabel;

        private VisualElement _issuesList;
        private Label _issuesEmptyLabel;
        private Label _previewSummaryLabel;
        private VisualElement _pathList;
        private Label _costSummaryLabel;
        /// <summary>ER4-PRIM-05 STORY-EXECUTION-CARDS.md 第2条"UI/VFX/SFX/日志与同一事件匹配"——
        /// 展示 <see cref="Campaign.Regions.HomeValleyCombatTargets.RecentEvents"/> 最新一条，
        /// 与日志（<c>Log.Info</c>）读的是同一份 <see cref="Campaign.Regions.HomeValleyCombatTargets.HitResult"/>
        /// 数据，不是 UI 自己另算一份摘要。</summary>
        private Label _lastCombatResultLabel;

        private Button _saveButton;
        private Button _closeButton;
        private Label _saveResultLabel;
        private VisualElement _pendingRow;

        private readonly List<TemplateContainer> _issueRowPool = new List<TemplateContainer>(MaxIssueRows);
        private readonly List<TemplateContainer> _pathRowPool = new List<TemplateContainer>(MaxPathRows);

        private string _selectedBlueprintId;
        private BlueprintCircuitBoard _board;
        private int? _selectedSlot;
        private CircuitValidationResult _lastValidation;
        private BlueprintCircuitPreview _lastPreview;
        private float _refreshTimer;

        /// <summary>ER4-BLP-01 STORY-EXECUTION-CARDS.md 第1条"恢复草稿"入口的落点：离开某蓝图（切换/关闭
        /// 面板）前若草稿签名与已保存版本不同，把完整内容（不含 <c>CircuitSlotPartIds</c> 实例绑定，见
        /// <see cref="SnapshotCurrentDraftIfDirty"/> 注释）存进本字典；玩家未做任何保存就切回同一蓝图时，
        /// 用<see cref="_restoreDraftButton"/>取回，不必重新画一遍电路。只存内存，不落盘——关掉游戏/切换
        /// 战役即丢失，这是"恢复"而非"自动持久化草稿"的既定范围。</summary>
        private readonly Dictionary<string, BlueprintVersionRecord> _draftSnapshots = new Dictionary<string, BlueprintVersionRecord>();

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitBoardPanel");
            _issueRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitIssueRow");
            _pathRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitPathRow");
            _blueprintRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitBlueprintRow");
            _panelSettings = await GameModule.Resource.LoadAssetAsync<PanelSettings>("BattleHudPanelSettings");
            if (this == null)
            {
                return;
            }

            _document = gameObject.AddComponent<UIDocument>();
            _document.visualTreeAsset = _visualTree;
            _document.panelSettings = _panelSettings;
            // UI_WORKFLOW_GUIDE.md 分层表：ER4-PRIM-02 新增，取萌生(7)/覆盖面板(10)之间的 8。
            _document.sortingOrder = 8;

            for (int guard = 0; guard < 10 && _document.rootVisualElement == null; guard++)
            {
                await UniTask.Yield();
            }

            _root = _document.rootVisualElement;
            if (_root == null)
            {
                Log.Error("[CircuitBoardPanelUIToolkit] rootVisualElement 等待超时，电路板面板未初始化。");
                return;
            }

            BindElements();
            WireEvents();
            SetPanelOpen(false);
        }

        private void BindElements()
        {
            _entryToggleButton = _root.Q<Button>("EntryToggleButton");
            _panel = _root.Q<VisualElement>("CircuitBoardPanelRoot");

            _blueprintListScroll = _root.Q<ScrollView>("BlueprintList");
            _newBlueprintNameField = _root.Q<TextField>("NewBlueprintNameField");
            _newBlueprintButton = _root.Q<Button>("NewBlueprintButton");
            _duplicateBlueprintButton = _root.Q<Button>("DuplicateBlueprintButton");
            _saveAsButton = _root.Q<Button>("SaveAsButton");
            _archiveButton = _root.Q<Button>("ArchiveButton");
            _cancelDraftButton = _root.Q<Button>("CancelDraftButton");
            _restoreDraftButton = _root.Q<Button>("RestoreDraftButton");
            _activeBlueprintLabel = _root.Q<Label>("ActiveBlueprintLabel");

            _chassisDropdown = _root.Q<DropdownField>("ChassisDropdown");
            _primaryDropdown = _root.Q<DropdownField>("PrimaryDropdown");
            _utilityDropdown = _root.Q<DropdownField>("UtilityDropdown");
            _structureDropdown = _root.Q<DropdownField>("StructureDropdown");
            _lockedContentHintLabel = _root.Q<Label>("LockedContentHintLabel");

            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                _slotButtons[i] = _root.Q<Button>("Slot" + i);
            }
            _selectedSlotLabel = _root.Q<Label>("SelectedSlotLabel");
            _selectedSlotDetailLabel = _root.Q<Label>("SelectedSlotDetailLabel");
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int slot = row * 3 + col;
                    if (col < 2)
                    {
                        _edgeButtons.Add((_root.Q<Button>($"EdgeH_{row}_{col}"), slot, slot + 1, true));
                    }
                    if (row < 2)
                    {
                        _edgeButtons.Add((_root.Q<Button>($"EdgeV_{row}_{col}"), slot, slot + 3, false));
                    }
                }
            }

            _bagChipDropdown = _root.Q<DropdownField>("BagChipDropdown");
            _equipChipButton = _root.Q<Button>("EquipChipButton");
            _removeChipButton = _root.Q<Button>("RemoveChipButton");
            _sourceLabel = _root.Q<Label>("SourceLabel");
            _bagCapacityLabel = _root.Q<Label>("BagCapacityLabel");
            _pendingLabel = _root.Q<Label>("PendingLabel");
            _pendingChipDropdown = _root.Q<DropdownField>("PendingChipDropdown");
            _claimPendingButton = _root.Q<Button>("ClaimPendingButton");
            _pendingRow = _root.Q<VisualElement>("PendingRow");
            _printChipButton = _root.Q<Button>("PrintChipButton");
            _bagResultLabel = _root.Q<Label>("BagResultLabel");

            _edgeListLabel = _root.Q<Label>("EdgeListLabel");

            _firmware0Dropdown = _root.Q<DropdownField>("Firmware0Dropdown");
            _firmware1Dropdown = _root.Q<DropdownField>("Firmware1Dropdown");

            _undoButton = _root.Q<Button>("UndoButton");
            _redoButton = _root.Q<Button>("RedoButton");
            _historyDepthLabel = _root.Q<Label>("HistoryDepthLabel");

            _issuesList = _root.Q<VisualElement>("IssuesList");
            _issuesEmptyLabel = _root.Q<Label>("IssuesEmptyLabel");
            _previewSummaryLabel = _root.Q<Label>("PreviewSummaryLabel");
            _pathList = _root.Q<VisualElement>("PathList");
            _costSummaryLabel = _root.Q<Label>("CostSummaryLabel");
            _lastCombatResultLabel = _root.Q<Label>("LastCombatResultLabel");

            _saveButton = _root.Q<Button>("SaveButton");
            _closeButton = _root.Q<Button>("CloseButton");
            _saveResultLabel = _root.Q<Label>("SaveResultLabel");

            for (int i = 0; i < MaxIssueRows; i++)
            {
                TemplateContainer row = _issueRowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _issuesList.Add(row);
                _issueRowPool.Add(row);
            }
            for (int i = 0; i < MaxPathRows; i++)
            {
                TemplateContainer row = _pathRowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _pathList.Add(row);
                _pathRowPool.Add(row);
            }
            for (int i = 0; i < MaxBlueprintRows; i++)
            {
                TemplateContainer row = _blueprintRowTemplate.CloneTree();
                row.style.display = DisplayStyle.None;
                _blueprintListScroll.Add(row);
                Button btn = row.Q<Button>("OpenButton");
                int capturedIndex = i;
                btn.clicked += () => OnBlueprintRowClicked(capturedIndex);
                _blueprintRowPool.Add(btn);
                _blueprintIdsByRowIndex.Add(null);
            }
        }

        private void WireEvents()
        {
            _entryToggleButton.clicked += () => SetPanelOpen(!(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false));

            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                int slot = i;
                _slotButtons[i].clicked += () => SelectSlot(slot);
            }

            // ── CRUD：新建/打开/复制/另存/保存/取消/恢复草稿/归档 ──────────────────
            _newBlueprintButton.clicked += OnNewBlueprintClicked;
            _duplicateBlueprintButton.clicked += OnDuplicateBlueprintClicked;
            _saveAsButton.clicked += () => DoSave(saveAsNewRecord: true);
            _saveButton.clicked += () => DoSave(saveAsNewRecord: false);
            _archiveButton.clicked += OnArchiveClicked;
            _cancelDraftButton.clicked += OnCancelDraftClicked;
            _restoreDraftButton.clicked += OnRestoreDraftClicked;

            // ── 外层槽：选中即生效（校验/解锁不通过则保留原值并给出失败原因）────────
            _chassisDropdown.RegisterValueChangedCallback(_ => RunOuterOp(() =>
            {
                int idx = _chassisDropdown.index;
                if (idx < 0 || idx >= _chassisIdsByIndex.Count) return CircuitOpResult.Fail("no-selection", "未选中底盘。");
                return _board.TrySetChassis(CampaignSession.Current, _chassisIdsByIndex[idx]);
            }));
            _primaryDropdown.RegisterValueChangedCallback(_ => RunOuterOp(() =>
            {
                int idx = _primaryDropdown.index;
                if (idx < 0 || idx >= _primaryIdsByIndex.Count) return CircuitOpResult.Fail("no-selection", "未选中主组件。");
                return _board.TrySetPrimary(CampaignSession.Current, _primaryIdsByIndex[idx]);
            }));
            _utilityDropdown.RegisterValueChangedCallback(_ => RunOuterOp(() =>
            {
                int idx = _utilityDropdown.index;
                if (idx < 0 || idx >= _utilityIdsByIndex.Count) return CircuitOpResult.Fail("no-selection", "未选中功能组件。");
                return _board.TrySetUtility(CampaignSession.Current, _utilityIdsByIndex[idx]);
            }));
            _structureDropdown.RegisterValueChangedCallback(_ => RunOuterOp(() =>
            {
                int idx = _structureDropdown.index;
                if (idx < 0 || idx >= _structureIdsByIndex.Count) return CircuitOpResult.Fail("no-selection", "未选中结构。");
                return _board.TrySetStructure(CampaignSession.Current, _structureIdsByIndex[idx]);
            }));

            _equipChipButton.clicked += () => RunBagOp(() =>
            {
                if (_selectedSlot == null)
                {
                    return CircuitOpResult.Fail("no-slot-selected", "请先在电路板上点选一个 1～7 号槽。");
                }
                int index = _bagChipDropdown.index;
                if (index < 0 || index >= _bagPartIdsByDropdownIndex.Count)
                {
                    return CircuitOpResult.Fail("no-chip-selected", "仓中没有可装的芯片，或未选中。");
                }
                string partId = _bagPartIdsByDropdownIndex[index];
                return PrimitiveInventory.TryMoveToDraft(CampaignSession.Current, _board, _selectedBlueprintId, _selectedSlot.Value, partId);
            });
            _removeChipButton.clicked += () => RunBagOp(() =>
                PrimitiveInventory.TryMoveToBag(CampaignSession.Current, _board, _selectedSlot ?? -1));
            _claimPendingButton.clicked += () => RunBagOp(() =>
            {
                int index = _pendingChipDropdown.index;
                if (index < 0 || index >= _pendingPartIdsByDropdownIndex.Count)
                {
                    return CircuitOpResult.Fail("no-pending-selected", "待领取队列为空，或未选中。");
                }
                string partId = _pendingPartIdsByDropdownIndex[index];
                return PrimitiveInventory.TryClaimPending(CampaignSession.Current, partId);
            });
            _printChipButton.clicked += () => RunBagOp(() =>
                PrimitiveInventory.TryPrintChip(CampaignSession.Current, PrimitiveInventory.DefaultChipContentId));

            foreach ((Button button, int a, int b, bool _) in _edgeButtons)
            {
                if (button != null)
                {
                    button.clicked += () => RunOp(() => CycleEdge(a, b));
                }
            }

            // 固件与外层槽一致：选中即生效。原先"下拉选好再点设置"的两步式会被 0.2 秒一次的刷新把下拉值
            // 重置回当前固件，玩家的选择等不到点按钮就被覆盖。选"（空）"即清空该位。
            _firmware0Dropdown.RegisterValueChangedCallback(_ => RunOp(() =>
                _board.TrySetFirmware(CampaignSession.Current, 0, IdAt(_firmware0IdsByIndex, _firmware0Dropdown.index))));
            _firmware1Dropdown.RegisterValueChangedCallback(_ => RunOp(() =>
                _board.TrySetFirmware(CampaignSession.Current, 1, IdAt(_firmware1IdsByIndex, _firmware1Dropdown.index))));

            _undoButton.clicked += () =>
            {
                _board?.Undo();
                RefreshAll();
            };
            _redoButton.clicked += () =>
            {
                _board?.Redo();
                RefreshAll();
            };

            _closeButton.clicked += () => SetPanelOpen(false);
        }

        private void OnBlueprintRowClicked(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _blueprintIdsByRowIndex.Count)
            {
                return;
            }
            string blueprintId = _blueprintIdsByRowIndex[rowIndex];
            if (string.IsNullOrEmpty(blueprintId))
            {
                return;
            }
            SelectBlueprint(blueprintId);
        }

        private void OnNewBlueprintClicked()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                _saveResultLabel.text = "没有活动战役，无法新建。";
                return;
            }
            SnapshotCurrentDraftIfDirty();
            ReconcileCurrentBlueprintDrafts();
            BlueprintRecord record = BlueprintEditorService.CreateNew(state, _newBlueprintNameField.value);
            _saveResultLabel.text = $"已新建蓝图“{record.DisplayName}”，请选择底盘/主组件后保存。";
            SelectBlueprint(record.BlueprintId);
        }

        private void OnDuplicateBlueprintClicked()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || string.IsNullOrEmpty(_selectedBlueprintId))
            {
                _saveResultLabel.text = "请先在列表中打开一个蓝图再复制。";
                return;
            }
            SnapshotCurrentDraftIfDirty();
            ReconcileCurrentBlueprintDrafts();
            BlueprintRecord record = BlueprintEditorService.Duplicate(state, _selectedBlueprintId, _newBlueprintNameField.value);
            if (record == null)
            {
                _saveResultLabel.text = "复制失败：来源蓝图尚无已保存版本。";
                return;
            }
            _saveResultLabel.text = $"已复制为“{record.DisplayName}”（独立新记录，未装的基元芯片实例需重新装配）。";
            SelectBlueprint(record.BlueprintId);
        }

        private void OnArchiveClicked()
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || string.IsNullOrEmpty(_selectedBlueprintId))
            {
                return;
            }
            CircuitOpResult r = BlueprintEditorService.TryArchive(state, _selectedBlueprintId);
            _saveResultLabel.text = r.Success
                ? "已归档：仍可被机器/队列引用读取，不再默认出现在新的打开列表首屏。"
                : $"归档失败[{r.Code}]：{r.Message}";
            RefreshAll();
        }

        private void OnCancelDraftClicked()
        {
            // AC-BLP-002/DEMO-IMPLEMENTATION-SPEC.md："任何失败保持草稿且不扣资源"；取消同样不扣废料/
            // 技术数据——本操作从不触碰 CampaignEconomyLedger，只回滚编辑模型与基元仓账本。先快照
            // （见 <see cref="_draftSnapshots"/> 类注释），让"取消草稿"与"恢复草稿"互为可逆操作——玩家
            // 手滑点了取消，还能用恢复草稿要回来，不是单向不可逆的丢弃。
            SnapshotCurrentDraftIfDirty();
            ReconcileCurrentBlueprintDrafts();
            ReloadBoardFromSaved();
            _saveResultLabel.text = "已取消草稿，恢复到最近保存版本（未扣废料/技术数据；如需要可点“恢复草稿”取回）。";
            RefreshAll();
        }

        private void OnRestoreDraftClicked()
        {
            if (string.IsNullOrEmpty(_selectedBlueprintId) || !_draftSnapshots.TryGetValue(_selectedBlueprintId, out BlueprintVersionRecord snapshot))
            {
                _saveResultLabel.text = "没有可恢复的未保存草稿。";
                return;
            }
            _board = BlueprintCircuitBoard.FromVersion(snapshot);
            _saveResultLabel.text = "已恢复未保存草稿的槽位/导线/固件内容（基元仓实例绑定需重新从仓装入，避免同一实例被复制）。";
            RefreshAll();
        }

        private void DoSave(bool saveAsNewRecord)
        {
            if (_board == null)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                _saveResultLabel.text = "没有活动战役，无法保存。";
                return;
            }

            string targetBlueprintId = saveAsNewRecord ? null : _selectedBlueprintId;
            string displayName = null;
            if (saveAsNewRecord || string.IsNullOrEmpty(_selectedBlueprintId))
            {
                displayName = string.IsNullOrEmpty(_newBlueprintNameField.value) ? null : _newBlueprintNameField.value;
            }

            BlueprintSaveResult result = BlueprintEditorService.TrySave(state, _board, targetBlueprintId, displayName, saveAsNewRecord);
            if (!result.Success)
            {
                _saveResultLabel.text = $"保存被拒绝：{result.FailureReason}";
                // ER8-CONTENT-01 AC-AUD-001 拒绝：面板文字之外补声音与字幕条。
                Campaign.Feedback.FeedbackCues.Raise(Campaign.Feedback.FeedbackCueId.Denied, _saveResultLabel.text);
                RefreshAll();
                return;
            }

            _draftSnapshots.Remove(result.BlueprintId);
            string chargeNote = result.TechDataCharged > 0
                ? $"；首次保存跨派系反应“{ReactionDisplayName(result.ReactionId)}”，已扣技术数据 {result.TechDataCharged}"
                : (result.ReactionId != null ? $"；触发反应“{ReactionDisplayName(result.ReactionId)}”（已在本战役扣过费，本次免费）" : string.Empty);
            // ER8-CONTENT-01 AC-THEME-001：此前显示 BlueprintId（bp_erc003 等内部 ID），改为蓝图展示名。
            string savedName = state.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == result.BlueprintId)?.DisplayName;
            _saveResultLabel.text = $"已保存“{(string.IsNullOrEmpty(savedName) ? "蓝图" : savedName)}”为版本 {result.Version}{chargeNote}。";
            _selectedBlueprintId = result.BlueprintId;
            ReloadBoardFromSaved();
            RefreshAll();
        }

        private static string ReactionDisplayName(string reactionId) =>
            reactionId != null && MechanicalReactionCatalog.TryGet(reactionId, out MechanicalContentDef def) ? def.DisplayName : reactionId;

        private void RunOp(Func<CircuitOpResult> op)
        {
            if (_board == null)
            {
                return;
            }
            CircuitOpResult r = op();
            _saveResultLabel.text = r.Success ? string.Empty : $"操作失败：{r.Message}";
            RefreshAll();
        }

        /// <summary>外层槽操作的结果文本走 <see cref="_lockedContentHintLabel"/> 旁的
        /// <see cref="_saveResultLabel"/>，与内层电路操作共用同一提示位置——玩家关心的是"这次点击成不成功"，
        /// 不需要为外层/内层分别开两条提示。</summary>
        private void RunOuterOp(Func<CircuitOpResult> op)
        {
            if (_board == null)
            {
                return;
            }
            CircuitOpResult r = op();
            _saveResultLabel.text = r.Success ? string.Empty : $"操作失败[{r.Code}]：{r.Message}";
            RefreshAll();
        }

        private void RunBagOp(Func<CircuitOpResult> op)
        {
            if (_board == null)
            {
                return;
            }
            CircuitOpResult r = op();
            _bagResultLabel.text = r.Success ? string.Empty : $"操作失败[{r.Code}]：{r.Message}";
            RefreshAll();
        }

        private void SetPanelOpen(bool open)
        {
            GameRoot.HomeValley?.SetCircuitBoardPanelOpen(open);
            if (!open)
            {
                // ER4-PRIM-03/ER4-BLP-01：关闭面板＝离开当前蓝图的编辑会话。未保存的实例装/卸改动释放
                // 回仓之前先快照内容（恢复草稿用），再回滚账本、重新从已保存版本加载 _board，避免槽位
                // 视觉与仓账本真实状态脱节的鬼影（ER4-PRIM-03 实测过的真实缺陷，同一套修复继续沿用）。
                SnapshotCurrentDraftIfDirty();
                ReconcileCurrentBlueprintDrafts();
                ReloadBoardFromSaved();
            }
            if (open && _board == null)
            {
                SelectFirstAvailableBlueprint();
            }
            RefreshAll();
        }

        private void SelectFirstAvailableBlueprint()
        {
            CampaignState state = CampaignSession.Current;
            BlueprintRecord first = state?.BlueprintRecords?
                .Where(b => b != null)
                .OrderBy(b => b.Archived)
                .ThenBy(b => b.BlueprintId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (first != null)
            {
                SelectBlueprint(first.BlueprintId);
            }
            else
            {
                SelectBlueprint(HomeValleyLayout.BlueprintErc003Id);
            }
        }

        private void SelectBlueprint(string blueprintId)
        {
            // ER4-PRIM-03/ER4-BLP-01："跨草稿"守恒：离开上一个正在编辑的蓝图前，先快照未保存内容
            // （恢复草稿用），再把它名下未落进已保存版本的 Draft 实例释放回仓，不让切换蓝图偷偷丢/
            // 复制实例。
            SnapshotCurrentDraftIfDirty();
            ReconcileCurrentBlueprintDrafts();

            _selectedBlueprintId = blueprintId;
            _selectedSlot = null;
            ReloadBoardFromSaved();
            RefreshAll();
        }

        /// <summary>把 <see cref="_board"/> 从 <see cref="_selectedBlueprintId"/> 当前的已保存活跃版本
        /// 重新加载。</summary>
        private void ReloadBoardFromSaved()
        {
            if (string.IsNullOrEmpty(_selectedBlueprintId))
            {
                _board = null;
                return;
            }
            CampaignState state = CampaignSession.Current;
            BlueprintVersionRecord version = BlueprintEditorService.FindActiveVersion(state, _selectedBlueprintId);
            _board = BlueprintCircuitBoard.FromVersion(version);
        }

        private void ReconcileCurrentBlueprintDrafts()
        {
            if (string.IsNullOrEmpty(_selectedBlueprintId))
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BlueprintVersionRecord savedVersion = BlueprintEditorService.FindActiveVersion(state, _selectedBlueprintId);
            PrimitiveInventory.ReconcileBlueprintDrafts(state, _selectedBlueprintId, savedVersion?.CircuitSlotPartIds);
        }

        /// <summary>见 <see cref="_draftSnapshots"/> 类注释：只在草稿签名与已保存版本不同（真的有未保存
        /// 改动）时才存快照，避免"从未改过就点了一下切换"也占一条记录（无害但没必要）。不存
        /// <see cref="BlueprintVersionRecord.CircuitSlotPartIds"/>（实例绑定）——那部分已经被
        /// <see cref="ReconcileCurrentBlueprintDrafts"/> 回收，"恢复草稿"只恢复内容结构，不臆造仓内
        /// 实例仍然存在。</summary>
        private void SnapshotCurrentDraftIfDirty()
        {
            if (_board == null || string.IsNullOrEmpty(_selectedBlueprintId))
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BlueprintVersionRecord saved = BlueprintEditorService.FindActiveVersion(state, _selectedBlueprintId);
            string currentSignature = _board.ComputeSignature();
            if (saved != null && saved.CompileSignature == currentSignature)
            {
                return;
            }
            BlueprintVersionRecord snapshot = _board.ToVersion(-1, state?.PlaySeconds ?? 0f);
            snapshot.CircuitSlotPartIds = null;
            _draftSnapshots[_selectedBlueprintId] = snapshot;
        }

        private void SelectSlot(int slot)
        {
            _selectedSlot = slot;
            RefreshAll();
        }

        private bool HasEdge(int from, int to)
        {
            if (_board == null)
            {
                return false;
            }
            foreach ((int From, int To) e in _board.Edges)
            {
                if (e.From == from && e.To == to)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>接口循环：无 → A→B → B→A → 双向 → 无。每一步都经 <see cref="BlueprintCircuitBoard"/>
        /// 的画/删边入口，四邻、软帽等校验与失败文案保持原样。某一步加边被拒（如已达边数软帽）时退到"无"
        /// 并说明原因，保证任何状态下连点都能把这条导线删掉，不会卡在某个方向上。</summary>
        private CircuitOpResult CycleEdge(int a, int b)
        {
            bool forward = HasEdge(a, b);
            bool reverse = HasEdge(b, a);
            if (!forward && !reverse)
            {
                return _board.TryAddEdge(a, b);
            }
            if (forward && !reverse)
            {
                CircuitOpResult removed = _board.TryRemoveEdge(a, b);
                if (!removed.Success)
                {
                    return removed;
                }
                CircuitOpResult flipped = _board.TryAddEdge(b, a);
                return flipped.Success ? flipped : CircuitOpResult.Fail(flipped.Code, $"已删除 {a}→{b}；无法改为 {b}→{a}：{flipped.Message}");
            }
            if (!forward)
            {
                CircuitOpResult both = _board.TryAddEdge(a, b);
                if (both.Success)
                {
                    return both;
                }
                CircuitOpResult cleared = _board.TryRemoveEdge(b, a);
                return cleared.Success ? CircuitOpResult.Fail(both.Code, $"已删除 {b}→{a}；无法改为双向：{both.Message}") : cleared;
            }
            CircuitOpResult first = _board.TryRemoveEdge(a, b);
            return first.Success ? _board.TryRemoveEdge(b, a) : first;
        }

        private static string IdAt(List<string> ids, int index) =>
            index >= 0 && index < ids.Count ? ids[index] : null;

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            bool regionActive = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive;
            _entryToggleButton.parent.EnableInClassList("cb-hidden", !regionActive);
            if (!regionActive)
            {
                _panel.EnableInClassList("cb-hidden", true);
                return;
            }

            bool open = GameRoot.HomeValley.IsCircuitBoardPanelOpen;
            _panel.EnableInClassList("cb-hidden", !open);
            if (!open)
            {
                return;
            }

            // 面板开关状态是公开字段，不保证只有本类自己的 EntryToggleButton 会翻它——默认蓝图的懒加载
            // 必须放在 Update 里而不是只在按钮点击回调里，否则外部直接翻开关会看到空白面板
            // （ER4-PRIM-02 Play Mode 实测发现的真实 bug，修复方式沿用）。
            if (_board == null)
            {
                SelectFirstAvailableBlueprint();
            }

            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
            {
                return;
            }
            _refreshTimer = RefreshIntervalSeconds;
            RefreshAll();
        }

        private void RefreshAll()
        {
            if (_root == null)
            {
                return;
            }
            // 先算校验与预览，网格的问题高亮才与本次草稿一致（原先顺序会滞后一次刷新）。
            if (_board != null)
            {
                _lastValidation = _board.Validate();
                _lastPreview = BlueprintCircuitCompiler.CompilePreview(_board);
            }
            else
            {
                _lastValidation = null;
                _lastPreview = null;
            }
            RefreshBlueprintList();
            RefreshOuterSlots();
            RefreshGrid();
            RefreshBag();
            RefreshEdgeAndFirmware();
            RefreshHistoryLabel();
            RefreshIssues();
            RefreshPreview();
            RefreshCostSummary();
            RefreshLastCombat();
        }

        /// <summary>ER4-PRIM-05：显示最近一次真实命中/未命中事件——与 <see cref="RefreshPreview"/>
        /// 的区别是后者是"如果打会怎样"的编译预览，本方法是"真的打过一次之后发生了什么"
        /// （<see cref="Campaign.Regions.HomeValleyCombatTargets.TryAttack"/> 唯一写入口产出）。</summary>
        private void RefreshLastCombat()
        {
            if (_lastCombatResultLabel == null)
            {
                return;
            }
            IReadOnlyList<HomeValleyCombatTargets.HitResult> events = HomeValleyCombatTargets.RecentEvents;
            _lastCombatResultLabel.text = events.Count == 0
                ? "尚无实战记录。"
                : events[events.Count - 1].Summarize();
        }

        private void RefreshBlueprintList()
        {
            CampaignState state = CampaignSession.Current;
            List<BlueprintRecord> records = (state?.BlueprintRecords ?? Array.Empty<BlueprintRecord>())
                .Where(b => b != null)
                .OrderBy(b => b.Archived)
                .ThenBy(b => b.BlueprintId, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < MaxBlueprintRows; i++)
            {
                Button btn = _blueprintRowPool[i];
                if (i >= records.Count)
                {
                    btn.parent.style.display = DisplayStyle.None;
                    _blueprintIdsByRowIndex[i] = null;
                    continue;
                }
                BlueprintRecord r = records[i];
                btn.parent.style.display = DisplayStyle.Flex;
                _blueprintIdsByRowIndex[i] = r.BlueprintId;
                string versionTag = r.Versions?.Length > 0 ? $"v{r.ActiveVersion}" : "未保存";
                string archivedTag = r.Archived ? "（已归档）" : string.Empty;
                btn.text = $"{r.DisplayName}  {versionTag}{archivedTag}";
                btn.EnableInClassList("cb-bp-list-btn-active", r.BlueprintId == _selectedBlueprintId);
                btn.EnableInClassList("cb-bp-list-btn-archived", r.Archived);
            }

            _activeBlueprintLabel.text = DescribeActiveBlueprint(state, records);
        }

        private string DescribeActiveBlueprint(CampaignState state, List<BlueprintRecord> records)
        {
            if (_board == null)
            {
                return "未选择蓝图";
            }
            BlueprintRecord record = records.FirstOrDefault(r => r.BlueprintId == _selectedBlueprintId);
            string name = record?.DisplayName ?? "新蓝图";
            BlueprintVersionRecord saved = BlueprintEditorService.FindActiveVersion(state, _selectedBlueprintId);
            string version = saved != null ? $"已保存 v{record?.ActiveVersion}" : "尚未保存";
            bool dirty = saved == null || saved.CompileSignature != _board.ComputeSignature();
            return dirty ? $"正在编辑：{name}（{version}）· 有未保存改动" : $"正在编辑：{name}（{version}）";
        }

        // ── 外层槽：只展示已解锁选项（ER4-BLP-01 第1条"未解锁不可选"）──────────────

        private void RefreshOuterSlots()
        {
            CampaignState state = CampaignSession.Current;

            PopulateDropdown(_chassisDropdown, _chassisIdsByIndex,
                ChassisCatalog.All.Values, state,
                _board != null ? (ChassisCatalog.ResolveArchetype(_board.ChassisId) ?? _board.ChassisId) : null,
                includeEmptyOption: false);

            PopulateDropdown(_primaryDropdown, _primaryIdsByIndex,
                ComponentCatalog.All.Values.Where(d => d.Category == MechanicalContentCategory.MainComponent), state,
                _board?.PrimaryId, includeEmptyOption: false);

            PopulateDropdown(_utilityDropdown, _utilityIdsByIndex,
                ComponentCatalog.All.Values.Where(d => d.Category == MechanicalContentCategory.FunctionComponent), state,
                _board?.UtilityId, includeEmptyOption: true);

            PopulateDropdown(_structureDropdown, _structureIdsByIndex,
                ComponentCatalog.All.Values.Where(d => d.Category == MechanicalContentCategory.Structure), state,
                _board?.StructureId, includeEmptyOption: true);

            RefreshLockedContentHint(state);
        }

        private static void PopulateDropdown(DropdownField dropdown, List<string> idsByIndex,
            IEnumerable<MechanicalContentDef> candidates, CampaignState state, string currentSelectedId, bool includeEmptyOption)
        {
            idsByIndex.Clear();
            var choices = new List<string>();
            if (includeEmptyOption)
            {
                choices.Add("（空）");
                idsByIndex.Add(null);
            }
            else if (string.IsNullOrEmpty(currentSelectedId))
            {
                // 必填槽（底盘/主组件）尚未真正设过值时，用一个不对应任何合法内容 ID 的占位项忠实展示
                // "未选择"，不能默认选中列表第一项——那会让下拉框显示一个内容，但 board 字段其实仍是
                // null，造成"UI 看着选好了、保存却报缺底盘"的视觉与数据不一致。
                choices.Add("（请选择）");
                idsByIndex.Add(null);
            }
            foreach (MechanicalContentDef def in candidates.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                if (!MechanicalContentUnlock.IsUnlocked(state, def.Id))
                {
                    continue;
                }
                // 选项文本里不能出现 "/"：下拉菜单会把它当子菜单分隔符，把一项拆成两级菜单。
                choices.Add($"{def.DisplayName}（{def.ScrapCost} 废料 · 负载 {def.Load}）".Replace('/', '／'));
                idsByIndex.Add(def.Id);
            }
            dropdown.choices = choices;
            if (choices.Count == 0)
            {
                dropdown.SetValueWithoutNotify(string.Empty);
                return;
            }
            int selectedIndex = string.IsNullOrEmpty(currentSelectedId) ? 0 : idsByIndex.IndexOf(currentSelectedId);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }
            dropdown.SetValueWithoutNotify(choices[selectedIndex]);
        }

        private void RefreshLockedContentHint(CampaignState state)
        {
            var lines = new List<string>();
            foreach (MechanicalContentDef def in MechanicalContentFacade.All.Values)
            {
                if (!IsEquipCategory(def.Category) || MechanicalContentUnlock.IsUnlocked(state, def.Id))
                {
                    continue;
                }
                ContentUnlockState cls = MechanicalContentUnlock.Classify(state, def.Id);
                string tag = cls == ContentUnlockState.RetrievedPendingAnalysis ? "已携回待解析" : "未知";
                lines.Add($"{def.DisplayName}[{tag}]");
            }
            _lockedContentHintLabel.text = lines.Count == 0
                ? "全部外层内容已解锁。"
                : $"未解锁（不可选）：{string.Join("、", lines.OrderBy(s => s, StringComparer.Ordinal))}";
        }

        private static bool IsEquipCategory(MechanicalContentCategory category) =>
            category == MechanicalContentCategory.Chassis
            || category == MechanicalContentCategory.MainComponent
            || category == MechanicalContentCategory.FunctionComponent
            || category == MechanicalContentCategory.Structure
            || category == MechanicalContentCategory.Firmware;

        private void RefreshGrid()
        {
            var issueSlots = new HashSet<int>();
            if (_lastValidation != null)
            {
                foreach (CircuitIssue issue in _lastValidation.Issues)
                {
                    if (issue.Slots == null)
                    {
                        continue;
                    }
                    foreach (int s in issue.Slots)
                    {
                        issueSlots.Add(s);
                    }
                }
            }

            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                Button btn = _slotButtons[i];
                string content = _board != null ? _board.SlotContentIds[i] : null;
                string head = i == BlueprintCircuitLayout.SourceSlot ? $"{i} · 源"
                    : i == BlueprintCircuitLayout.SinkSlot ? $"{i} · 汇"
                    : $"{i} · {BlueprintCircuitLayout.SlotTypeDisplayName(BlueprintCircuitLayout.SlotTypeAt(i))}";
                string body = string.IsNullOrEmpty(content) ? "（空）" : BlueprintCircuitChipCatalog.DisplayNameFor(content);
                btn.text = $"{head}\n{body}";
                btn.EnableInClassList("cb-slot-filled", !string.IsNullOrEmpty(content));
                btn.EnableInClassList("cb-slot-selected", _selectedSlot.HasValue && _selectedSlot.Value == i);
                btn.EnableInClassList("cb-slot-issue", issueSlots.Contains(i));
            }

            foreach ((Button button, int a, int b, bool horizontal) in _edgeButtons)
            {
                if (button == null)
                {
                    continue;
                }
                bool forward = HasEdge(a, b);
                bool reverse = HasEdge(b, a);
                button.text = forward && reverse ? (horizontal ? "⇄" : "⇅")
                    : forward ? (horizontal ? "→" : "↓")
                    : reverse ? (horizontal ? "←" : "↑")
                    : "·";
                button.EnableInClassList("cb-edge-on", forward || reverse);
                button.tooltip = $"{a} 号与 {b} 号之间的导线";
            }

            RefreshSlotInspector();
        }

        private void RefreshSlotInspector()
        {
            string sourceName = _board != null
                ? BlueprintCircuitChipCatalog.DisplayNameFor(_board.SlotContentIds[BlueprintCircuitLayout.SourceSlot])
                : null;
            _sourceLabel.text = $"电源（0 号源槽，由底盘决定）：{sourceName ?? "-"}";

            if (!_selectedSlot.HasValue)
            {
                _selectedSlotLabel.text = "未选中槽位";
                _selectedSlotDetailLabel.text = "点击左侧电路板上的格子查看和装配。";
                _equipChipButton.SetEnabled(false);
                _removeChipButton.SetEnabled(false);
                return;
            }

            int slot = _selectedSlot.Value;
            SlotType slotType = BlueprintCircuitLayout.SlotTypeAt(slot);
            string content = _board?.SlotContentIds[slot];
            string contentName = string.IsNullOrEmpty(content) ? "空" : BlueprintCircuitChipCatalog.DisplayNameFor(content);
            bool fixedSlot = BlueprintCircuitLayout.IsFixedSlot(slot);
            _selectedSlotLabel.text = $"{slot} 号槽 · {BlueprintCircuitLayout.SlotTypeDisplayName(slotType)}";
            _selectedSlotDetailLabel.text = fixedSlot
                ? $"当前：{contentName}\n{(slot == BlueprintCircuitLayout.SourceSlot ? "源槽" : "汇槽")}固定，不可拆装。"
                : $"当前：{contentName}\n{BlueprintCircuitLayout.SlotPassiveDisplay(slotType)}";
            _equipChipButton.SetEnabled(!fixedSlot);
            _removeChipButton.SetEnabled(!fixedSlot && !string.IsNullOrEmpty(content));
        }

        private void RefreshBag()
        {
            CampaignState state = CampaignSession.Current;
            int bagCount = PrimitiveInventory.BagCount(state);
            _bagCapacityLabel.text = $"仓 {bagCount}/{PrimitiveInventory.Capacity}";

            _bagPartIdsByDropdownIndex.Clear();
            var bagChoices = new List<string>();
            foreach (PrimitiveChipRecord item in PrimitiveInventory.BagItems(state))
            {
                string source = string.IsNullOrEmpty(item.SourceSalvageId) ? "补印" : "解析";
                bagChoices.Add($"{BlueprintCircuitChipCatalog.DisplayNameFor(item.CardDefId)}（{source} #{DropdownChoices.ShortId(item.PartId)}）");
                _bagPartIdsByDropdownIndex.Add(item.PartId);
            }
            DropdownChoices.Apply(_bagChipDropdown, bagChoices, "仓内没有芯片");

            IReadOnlyList<PrimitiveChipRecord> pending = PrimitiveInventory.PendingItems(state);
            _pendingLabel.text = pending.Count == 0 ? string.Empty : $"待领取 {pending.Count} 件（仓满时新芯片在此排队，不会丢失）";
            _pendingLabel.EnableInClassList("cb-hidden", pending.Count == 0);
            _pendingRow.EnableInClassList("cb-hidden", pending.Count == 0);
            _pendingPartIdsByDropdownIndex.Clear();
            var pendingChoices = new List<string>();
            foreach (PrimitiveChipRecord item in pending)
            {
                pendingChoices.Add($"{BlueprintCircuitChipCatalog.DisplayNameFor(item.CardDefId)} #{DropdownChoices.ShortId(item.PartId)}");
                _pendingPartIdsByDropdownIndex.Add(item.PartId);
            }
            DropdownChoices.Apply(_pendingChipDropdown, pendingChoices, "无");
        }

        private void RefreshEdgeAndFirmware()
        {
            CampaignState state = CampaignSession.Current;
            if (_board == null)
            {
                _edgeListLabel.text = string.Empty;
                _firmware0Dropdown.choices = new List<string>();
                _firmware1Dropdown.choices = new List<string>();
                _firmware0Dropdown.SetValueWithoutNotify(string.Empty);
                _firmware1Dropdown.SetValueWithoutNotify(string.Empty);
                return;
            }
            _edgeListLabel.text = _board.Edges.Count == 0
                ? "导线：无"
                : "导线：" + string.Join("，", _board.Edges.OrderBy(e => e.From).ThenBy(e => e.To).Select(e => $"{e.From}→{e.To}"));

            PopulateDropdown(_firmware0Dropdown, _firmware0IdsByIndex, FirmwareCatalog.All.Values, state,
                _board.FirmwareSlots[0], includeEmptyOption: true);
            PopulateDropdown(_firmware1Dropdown, _firmware1IdsByIndex, FirmwareCatalog.All.Values, state,
                _board.FirmwareSlots[1], includeEmptyOption: true);
        }

        private void RefreshHistoryLabel()
        {
            _historyDepthLabel.text = _board == null ? string.Empty : $"可撤销 {_board.UndoDepth} · 可重做 {_board.RedoDepth}";
            _undoButton.SetEnabled(_board != null && _board.UndoDepth > 0);
            _redoButton.SetEnabled(_board != null && _board.RedoDepth > 0);
        }

        private void RefreshIssues()
        {
            List<CircuitIssue> issues = _lastValidation?.Issues ?? new List<CircuitIssue>();
            _issuesEmptyLabel.style.display = issues.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            for (int i = 0; i < MaxIssueRows; i++)
            {
                TemplateContainer row = _issueRowPool[i];
                if (i >= issues.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                CircuitIssue issue = issues[i];
                row.Q<Label>("Code").text = issue.Code.ToString();
                row.Q<Label>("Message").text = issue.Message;
            }
        }

        private void RefreshPreview()
        {
            if (_lastPreview == null)
            {
                _previewSummaryLabel.text = string.Empty;
                for (int i = 0; i < MaxPathRows; i++)
                {
                    _pathRowPool[i].style.display = DisplayStyle.None;
                }
                return;
            }

            if (!_lastPreview.HasCombatOutput)
            {
                _previewSummaryLabel.text = _lastPreview.NoteText;
            }
            else
            {
                _previewSummaryLabel.text =
                    $"有效路径 {_lastPreview.PathCount} 条 · 归一化总伤害 {_lastPreview.TotalNormalizedDamage:F1}\n{_lastPreview.ReactionHint}";
            }

            for (int i = 0; i < MaxPathRows; i++)
            {
                TemplateContainer row = _pathRowPool[i];
                if (i >= _lastPreview.Paths.Count)
                {
                    row.style.display = DisplayStyle.None;
                    continue;
                }
                row.style.display = DisplayStyle.Flex;
                BlueprintCircuitPathPreview p = _lastPreview.Paths[i];
                row.Q<Label>("Path").text = string.Join("→", p.SlotPath);
                row.Q<Label>("Damage").text = $"{p.Damage:F1}";
            }
        }

        /// <summary>STORY-EXECUTION-CARDS.md ER4-BLP-01 第2条："每次改槽重算废料成本、负载已用/上限、
        /// 伤害、能耗、带宽、热量、派系标签、反应和敌方对策"——伤害已经在 <see cref="RefreshPreview"/>，
        /// 其余维度集中在本行。技术数据费用预览用 <see cref="BlueprintEditorService.ReactionTechDataCost"/>
        /// + <see cref="BlueprintEditorService.IsReactionCharged"/> 判断"这次保存是否真的要扣钱"，与
        /// <see cref="DoSave"/> 实际保存时走的同一对静态方法，不会出现预览说要扣费、实际保存不扣（或反之）
        /// 的不一致。</summary>
        private void RefreshCostSummary()
        {
            if (_board == null)
            {
                _costSummaryLabel.text = string.Empty;
                return;
            }
            CampaignState state = CampaignSession.Current;
            int scrap = _board.ComputeScrapCost();
            _board.TryComputeLoadPreview(out int load, out int? capacity);
            int bandwidth = _board.ComputeBandwidthCost();
            float heat = _board.ComputeHeatBudget();
            string[] factions = _board.ComputeFactionTags();
            string reactionId = BlueprintCircuitCompiler.DetectReactionId(_board);

            string factionText = factions.Length == 0 ? "无" : string.Join("+", factions);
            string crossFactionNote = factions.Length >= 2 ? "（跨派系）" : string.Empty;

            string reactionNote;
            string counterNote;
            if (reactionId == null)
            {
                reactionNote = "无具名反应";
                counterNote = "无（通用组合无固定敌方对策）";
            }
            else
            {
                MechanicalReactionCatalog.TryGet(reactionId, out MechanicalContentDef reactionDef);
                int cost = BlueprintEditorService.ReactionTechDataCost(reactionId);
                bool charged = BlueprintEditorService.IsReactionCharged(state, reactionId);
                reactionNote = charged
                    ? $"{reactionDef?.DisplayName}（本战役已扣过技术数据，再次保存免费）"
                    : $"{reactionDef?.DisplayName}（首次保存将扣技术数据 {cost}，当前 {state?.TechData ?? 0}）";
                counterNote = reactionDef?.ValuesSummary ?? "-";
            }

            _costSummaryLabel.text =
                $"废料成本 {scrap}\n负载 {load}/{(capacity.HasValue ? capacity.Value.ToString() : "-")} · 带宽 +{bandwidth} · 热量 {heat:F0}\n" +
                $"派系：{factionText}{crossFactionNote}\n反应：{reactionNote}\n" +
                $"敌方对策：{counterNote}";
        }

        private void OnDestroy()
        {
            if (_visualTree != null)
            {
                GameModule.Resource.UnloadAsset(_visualTree);
                _visualTree = null;
            }
            if (_issueRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_issueRowTemplate);
                _issueRowTemplate = null;
            }
            if (_pathRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_pathRowTemplate);
                _pathRowTemplate = null;
            }
            if (_blueprintRowTemplate != null)
            {
                GameModule.Resource.UnloadAsset(_blueprintRowTemplate);
                _blueprintRowTemplate = null;
            }
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
