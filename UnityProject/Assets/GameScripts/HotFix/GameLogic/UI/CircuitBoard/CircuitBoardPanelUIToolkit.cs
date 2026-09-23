using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.Stage;
using TEngine;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.CircuitBoard
{
    /// <summary>ER4-PRIM-02 STORY-EXECUTION-CARDS.md 第2条："正式电路板 UI 可装/移芯片、画/删四邻有向边、
    /// 撤销/重做20步，显示空槽被动、源汇、路径/效果；非法边、重复边、环、不可达、孤立芯片、>4路径、
    /// 越边帽/负载逐项高亮并保留草稿。"结构落在 UXML/USS（<c>unity-ui-toolkit.md</c> 硬规则），C# 只做
    /// 数据绑定与事件，与 <see cref="Factory.FactoryPanelUIToolkit"/>/<see cref="WorkOrder.WorkOrderPanelUIToolkit"/>
    /// 同一套刷新降频/行池写法。
    ///
    /// 面板开关状态由 <see cref="HomeValleyController.IsCircuitBoardPanelOpen"/> 持有，但本类自带一个
    /// 常驻切换按钮（<c>EntryToggleButton</c>）驱动它——正式"家园蓝图"容器入口留 ER4-BLP-01，本 Story
    /// 提供一个独立可达的入口，不占用装配站建筑点选路由（避免与 <see cref="Factory.FactoryPanelUIToolkit"/>
    /// 的既有点选路由冲突）。所有装/卸/画边/固件/撤销重做操作都通过 <see cref="BlueprintCircuitBoard"/>
    /// 完成——本类不直接改 <c>BlueprintVersionRecord</c> 字段。</summary>
    public sealed class CircuitBoardPanelUIToolkit : MonoBehaviour
    {
        private const int MaxIssueRows = 10;
        private const int MaxPathRows = BlueprintCircuitLayout.MaxPaths;
        private const float RefreshIntervalSeconds = 0.2f;

        private UIDocument _document;
        private VisualTreeAsset _visualTree;
        private VisualTreeAsset _issueRowTemplate;
        private VisualTreeAsset _pathRowTemplate;
        private PanelSettings _panelSettings;

        private VisualElement _root;
        private Button _entryToggleButton;
        private VisualElement _panel;

        private readonly Dictionary<string, Button> _bpButtons = new Dictionary<string, Button>();
        private Label _activeBlueprintLabel;

        private readonly Button[] _slotButtons = new Button[BlueprintCircuitLayout.SlotCount];
        private Label _selectedSlotLabel;

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

        /// <summary>ER4-PRIM-03：<see cref="_bagChipDropdown"/>/<see cref="_pendingChipDropdown"/> 的
        /// choices 是展示文本（人读，含 PartId 短形式方便区分同名芯片），这两张表把下拉框选中索引换回
        /// 真正的 PartId——DropdownField 本身不支持"显示名/取值"分离，这是最小代价的绑定写法。</summary>
        private readonly List<string> _bagPartIdsByDropdownIndex = new List<string>();
        private readonly List<string> _pendingPartIdsByDropdownIndex = new List<string>();

        private TextField _edgeFromField;
        private TextField _edgeToField;
        private Button _addEdgeButton;
        private Button _removeEdgeButton;
        private Label _edgeListLabel;

        private TextField _firmware0Field;
        private TextField _firmware1Field;
        private Button _setFirmware0Button;
        private Button _clearFirmware0Button;
        private Button _setFirmware1Button;
        private Button _clearFirmware1Button;

        private Button _undoButton;
        private Button _redoButton;
        private Label _historyDepthLabel;

        private ScrollView _issuesList;
        private Label _issuesEmptyLabel;
        private Label _previewSummaryLabel;
        private ScrollView _pathList;

        private Button _saveButton;
        private Button _closeButton;
        private Label _saveResultLabel;

        private readonly List<TemplateContainer> _issueRowPool = new List<TemplateContainer>(MaxIssueRows);
        private readonly List<TemplateContainer> _pathRowPool = new List<TemplateContainer>(MaxPathRows);

        private static readonly string[] BlueprintOrder =
        {
            HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.BlueprintHaulerId,
            HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.BlueprintHoverId,
        };

        private string _selectedBlueprintId;
        private BlueprintCircuitBoard _board;
        private int? _selectedSlot;
        private CircuitValidationResult _lastValidation;
        private BlueprintCircuitPreview _lastPreview;
        private float _refreshTimer;

        private async void Start()
        {
            _visualTree = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitBoardPanel");
            _issueRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitIssueRow");
            _pathRowTemplate = await GameModule.Resource.LoadAssetAsync<VisualTreeAsset>("CircuitPathRow");
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

            _bpButtons[HomeValleyLayout.BlueprintErc001Id] = _root.Q<Button>("BpBtn_Erc001");
            _bpButtons[HomeValleyLayout.BlueprintHaulerId] = _root.Q<Button>("BpBtn_Hauler");
            _bpButtons[HomeValleyLayout.BlueprintErc003Id] = _root.Q<Button>("BpBtn_Erc003");
            _bpButtons[HomeValleyLayout.BlueprintHoverId] = _root.Q<Button>("BpBtn_Hover");
            _activeBlueprintLabel = _root.Q<Label>("ActiveBlueprintLabel");

            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                _slotButtons[i] = _root.Q<Button>("Slot" + i);
            }
            _selectedSlotLabel = _root.Q<Label>("SelectedSlotLabel");

            _bagChipDropdown = _root.Q<DropdownField>("BagChipDropdown");
            _equipChipButton = _root.Q<Button>("EquipChipButton");
            _removeChipButton = _root.Q<Button>("RemoveChipButton");
            _sourceLabel = _root.Q<Label>("SourceLabel");
            _bagCapacityLabel = _root.Q<Label>("BagCapacityLabel");
            _pendingLabel = _root.Q<Label>("PendingLabel");
            _pendingChipDropdown = _root.Q<DropdownField>("PendingChipDropdown");
            _claimPendingButton = _root.Q<Button>("ClaimPendingButton");
            _printChipButton = _root.Q<Button>("PrintChipButton");
            _bagResultLabel = _root.Q<Label>("BagResultLabel");

            _edgeFromField = _root.Q<TextField>("EdgeFromField");
            _edgeToField = _root.Q<TextField>("EdgeToField");
            _addEdgeButton = _root.Q<Button>("AddEdgeButton");
            _removeEdgeButton = _root.Q<Button>("RemoveEdgeButton");
            _edgeListLabel = _root.Q<Label>("EdgeListLabel");

            _firmware0Field = _root.Q<TextField>("Firmware0Field");
            _firmware1Field = _root.Q<TextField>("Firmware1Field");
            _setFirmware0Button = _root.Q<Button>("SetFirmware0Button");
            _clearFirmware0Button = _root.Q<Button>("ClearFirmware0Button");
            _setFirmware1Button = _root.Q<Button>("SetFirmware1Button");
            _clearFirmware1Button = _root.Q<Button>("ClearFirmware1Button");

            _undoButton = _root.Q<Button>("UndoButton");
            _redoButton = _root.Q<Button>("RedoButton");
            _historyDepthLabel = _root.Q<Label>("HistoryDepthLabel");

            _issuesList = _root.Q<ScrollView>("IssuesList");
            _issuesEmptyLabel = _root.Q<Label>("IssuesEmptyLabel");
            _previewSummaryLabel = _root.Q<Label>("PreviewSummaryLabel");
            _pathList = _root.Q<ScrollView>("PathList");

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
        }

        private void WireEvents()
        {
            _entryToggleButton.clicked += () => SetPanelOpen(!(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false));

            foreach (KeyValuePair<string, Button> kv in _bpButtons)
            {
                string id = kv.Key;
                kv.Value.clicked += () => SelectBlueprint(id);
            }

            for (int i = 0; i < BlueprintCircuitLayout.SlotCount; i++)
            {
                int slot = i;
                _slotButtons[i].clicked += () => SelectSlot(slot);
            }

            _equipChipButton.clicked += () => RunBagOp(() =>
            {
                if (_selectedSlot == null)
                {
                    return CircuitOpResult.Fail("no-slot-selected", "请先点选一个 1～7 号槽。");
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

            _addEdgeButton.clicked += () =>
            {
                if (int.TryParse(_edgeFromField.value, out int from) && int.TryParse(_edgeToField.value, out int to))
                {
                    RunOp(() => _board.TryAddEdge(from, to));
                }
                else
                {
                    _saveResultLabel.text = "请输入合法的槽号（0～8）。";
                }
            };
            _removeEdgeButton.clicked += () =>
            {
                if (int.TryParse(_edgeFromField.value, out int from) && int.TryParse(_edgeToField.value, out int to))
                {
                    RunOp(() => _board.TryRemoveEdge(from, to));
                }
            };

            _setFirmware0Button.clicked += () => RunOp(() => _board.TrySetFirmware(0, _firmware0Field.value));
            _clearFirmware0Button.clicked += () => RunOp(() => _board.TryClearFirmware(0));
            _setFirmware1Button.clicked += () => RunOp(() => _board.TrySetFirmware(1, _firmware1Field.value));
            _clearFirmware1Button.clicked += () => RunOp(() => _board.TryClearFirmware(1));

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

            _saveButton.clicked += OnSaveClicked;
            _closeButton.clicked += () => SetPanelOpen(false);
        }

        private void RunOp(System.Func<CircuitOpResult> op)
        {
            if (_board == null)
            {
                return;
            }
            CircuitOpResult r = op();
            _saveResultLabel.text = r.Success ? string.Empty : $"操作失败：{r.Message}";
            RefreshAll();
        }

        /// <summary>同 <see cref="RunOp"/>，但结果文本写进基元仓自己的
        /// <see cref="_bagResultLabel"/>（STORY-EXECUTION-CARDS.md 第2条"显示……所有失败码"，与电路
        /// 校验/保存的失败提示分开陈列，不混在同一行）。</summary>
        private void RunBagOp(System.Func<CircuitOpResult> op)
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
                // ER4-PRIM-03：关闭面板＝离开当前蓝图的编辑会话，未保存的实例装/卸改动释放回仓
                // （"退出……时资源与实例守恒"）。仅回滚仓账本还不够——execute_code 实测发现的真实
                // 缺陷：本类的 _board 字段此前在这里不会被清空/重建，玩家关闭再重新打开面板会看到
                // 一块"槽位视觉上仍装着芯片，但该实例其实已经在 ReconcileCurrentBlueprintDrafts 里
                // 放回仓"的鬼画面（_board.SlotContentIds/SlotPartIds 与仓账本真实状态脱节），下一次
                // 装卸操作还会因为 _board 仍认为槽位"已占用"而被 TryMoveToDraft/TryPlaceChip 误拒。
                // 修复：关闭时把 _board 重新从当前已保存版本加载一次，与账本保持同步。
                ReconcileCurrentBlueprintDrafts();
                ReloadBoardFromSaved();
            }
            if (open && _board == null)
            {
                SelectBlueprint(HomeValleyLayout.BlueprintErc003Id);
            }
            RefreshAll();
        }

        private void SelectBlueprint(string blueprintId)
        {
            // ER4-PRIM-03："跨草稿"守恒：离开上一个正在编辑的蓝图前，把它名下未落进已保存版本的
            // Draft 实例释放回仓，不让切换蓝图偷偷丢/复制实例。
            ReconcileCurrentBlueprintDrafts();

            _selectedBlueprintId = blueprintId;
            _selectedSlot = null;
            ReloadBoardFromSaved();
            RefreshAll();
        }

        /// <summary>把 <see cref="_board"/> 从 <see cref="_selectedBlueprintId"/> 当前的已保存活跃版本
        /// 重新加载——<see cref="SelectBlueprint"/>（切换蓝图）与 <see cref="SetPanelOpen"/>（关闭面板后
        /// 回滚未保存改动）共用，保证 <c>_board</c> 与 <see cref="PrimitiveInventory"/> 账本、与磁盘上
        /// 真正保存过的内容三者随时一致，不留"仓账本已回滚但板面显示没跟着回滚"的视觉/逻辑鬼影。</summary>
        private void ReloadBoardFromSaved()
        {
            if (string.IsNullOrEmpty(_selectedBlueprintId))
            {
                _board = null;
                return;
            }
            CampaignState state = CampaignSession.Current;
            BlueprintVersionRecord version = FindActiveVersion(state, _selectedBlueprintId);
            _board = BlueprintCircuitBoard.FromVersion(version);
            if (version == null && state != null)
            {
                // 蓝图记录尚未播种（例如未经 HomeValleyController.Enter 的独立测试场景）——
                // 仍给出可编辑的默认草稿，不阻断面板本身可用性。
                _board.ChassisId = null;
            }
        }

        /// <summary>把 <see cref="_selectedBlueprintId"/> 名下、不属于其最后一次真实保存版本的
        /// Draft 实例释放回仓。<see cref="SelectBlueprint"/>（切换到别的蓝图前）与
        /// <see cref="SetPanelOpen"/>（关闭面板）两处调用，逻辑完全一致，抽成共享方法防止漏调一处。</summary>
        private void ReconcileCurrentBlueprintDrafts()
        {
            if (string.IsNullOrEmpty(_selectedBlueprintId))
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            BlueprintVersionRecord savedVersion = FindActiveVersion(state, _selectedBlueprintId);
            PrimitiveInventory.ReconcileBlueprintDrafts(state, _selectedBlueprintId, savedVersion?.CircuitSlotPartIds);
        }

        private static BlueprintVersionRecord FindActiveVersion(CampaignState state, string blueprintId)
        {
            BlueprintRecord record = state?.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == blueprintId);
            return record?.Versions?.FirstOrDefault(v => v.Version == record.ActiveVersion);
        }

        private void SelectSlot(int slot)
        {
            _selectedSlot = slot;
            RefreshAll();
        }

        private void OnSaveClicked()
        {
            if (_board == null || string.IsNullOrEmpty(_selectedBlueprintId))
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                _saveResultLabel.text = "没有活动战役，无法保存。";
                return;
            }

            CircuitValidationResult validation = _board.Validate();
            if (!validation.IsValid)
            {
                _saveResultLabel.text = $"保存被拒绝：{validation.Issues.Count} 个问题未解决，见上方列表。";
                RefreshAll();
                return;
            }

            state.BlueprintRecords ??= System.Array.Empty<BlueprintRecord>();
            BlueprintRecord record = state.BlueprintRecords.FirstOrDefault(b => b.BlueprintId == _selectedBlueprintId);
            if (record == null)
            {
                _saveResultLabel.text = "找不到对应的蓝图记录，无法保存（请先进入归还谷地播种默认蓝图）。";
                return;
            }

            int nextVersion = (record.Versions?.Length > 0 ? record.Versions.Max(v => v.Version) : 0) + 1;
            BlueprintVersionRecord newVersion = _board.ToVersion(nextVersion, state.PlaySeconds);
            record.Versions = (record.Versions ?? System.Array.Empty<BlueprintVersionRecord>())
                .Append(newVersion).ToArray();
            record.ActiveVersion = nextVersion;

            _saveResultLabel.text = $"已保存为版本 {nextVersion}（签名 {newVersion.CompileSignature.Substring(0, System.Math.Min(24, newVersion.CompileSignature.Length))}…）。";
            RefreshAll();
        }

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

            // 面板开关状态是公开字段（HomeValleyController.SetCircuitBoardPanelOpen），不保证只有本类
            // 自己的 EntryToggleButton 会翻它（未来 ER4-BLP-01 的"家园蓝图"容器很可能从别处调用同一个
            // 入口）——因此默认蓝图的懒加载必须放在 Update 里而不是只在按钮点击回调里，否则外部直接翻开
            // 关会看到空白"未选择蓝图"面板（Play Mode 实测发现的真实 bug，已修复）。
            if (_board == null)
            {
                SelectBlueprint(HomeValleyLayout.BlueprintErc003Id);
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
            RefreshBlueprintBar();
            RefreshGrid();
            RefreshBag();
            RefreshEdgeAndFirmware();
            RefreshHistoryLabel();
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
            RefreshIssues();
            RefreshPreview();
        }

        private void RefreshBlueprintBar()
        {
            foreach (KeyValuePair<string, Button> kv in _bpButtons)
            {
                kv.Value.EnableInClassList("cb-bp-btn-active", kv.Key == _selectedBlueprintId);
            }
            _activeBlueprintLabel.text = _board == null
                ? "未选择蓝图"
                : $"{_selectedBlueprintId}（底盘 {_board.ChassisId ?? "-"}｜主组件 {_board.PrimaryId ?? "-"}）";
        }

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
                string label;
                if (!string.IsNullOrEmpty(content))
                {
                    label = $"{i}\n{BlueprintCircuitChipCatalog.DisplayNameFor(content)}";
                }
                else if (BlueprintCircuitLayout.IsFixedSlot(i))
                {
                    // 0 号无合法源/8 号主组件无攻击输出时留空——中性提示，不是校验问题（Validate 不对
                    // 固定槽本身报 IsolatedChip/NoValidPath 以外的错）。
                    label = $"{i}\n（空）";
                }
                else
                {
                    // STORY-EXECUTION-CARDS.md ER4-PRIM-02 第2条"显示空槽被动"：未装芯片的自由槽
                    // 仍要展示其固定槽类型与机械化被动文案（PRIMITIVE-FULL-DEMO-SPEC.md §3.3 第3条）。
                    SlotType slotType = BlueprintCircuitLayout.SlotTypeAt(i);
                    label = $"{i}\n{BlueprintCircuitLayout.SlotTypeDisplayName(slotType)}\n{BlueprintCircuitLayout.SlotPassiveDisplay(slotType)}";
                }
                btn.text = label;
                btn.EnableInClassList("cb-slot-selected", _selectedSlot.HasValue && _selectedSlot.Value == i);
                btn.EnableInClassList("cb-slot-issue", issueSlots.Contains(i));
            }
            _selectedSlotLabel.text = _selectedSlot.HasValue ? $"已选中 {_selectedSlot.Value} 号槽" : "未选中槽位";

            string sourceName = _board != null
                ? BlueprintCircuitChipCatalog.DisplayNameFor(_board.SlotContentIds[BlueprintCircuitLayout.SourceSlot])
                : null;
            _sourceLabel.text = $"0 号源槽（不可拆，由底盘电源固定决定）：{sourceName ?? "-"}";
        }

        /// <summary>ER4-PRIM-03 STORY-EXECUTION-CARDS.md 第2条："正式 UI 显示容量、实例来源、合法目标
        /// 和所有失败码"——容量=<see cref="_bagCapacityLabel"/>，实例来源见每个下拉选项文本
        /// （PartId 短形式区分同名芯片，来自解析还是补印看 <see cref="PrimitiveChipRecord.SourceSalvageId"/>
        /// 是否为空），合法目标由 <see cref="PrimitiveInventory.TryMoveToDraft"/> 内部复用
        /// <see cref="BlueprintCircuitBoard.TryPlaceChip"/> 校验、失败码通过 <see cref="RunBagOp"/>
        /// 写入 <see cref="_bagResultLabel"/>。</summary>
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
                bagChoices.Add($"{BlueprintCircuitChipCatalog.DisplayNameFor(item.CardDefId)}［{source}·{item.PartId.Substring(0, System.Math.Min(10, item.PartId.Length))}］");
                _bagPartIdsByDropdownIndex.Add(item.PartId);
            }
            _bagChipDropdown.choices = bagChoices;
            if (bagChoices.Count == 0)
            {
                _bagChipDropdown.SetValueWithoutNotify(string.Empty);
            }
            else if (_bagChipDropdown.index < 0 || _bagChipDropdown.index >= bagChoices.Count)
            {
                _bagChipDropdown.index = 0;
            }

            IReadOnlyList<PrimitiveChipRecord> pending = PrimitiveInventory.PendingItems(state);
            _pendingLabel.text = pending.Count == 0 ? "待领取：无" : $"待领取：{pending.Count} 件（仓满时新实例排队于此，不丢失）";
            _pendingPartIdsByDropdownIndex.Clear();
            var pendingChoices = new List<string>();
            foreach (PrimitiveChipRecord item in pending)
            {
                pendingChoices.Add($"{BlueprintCircuitChipCatalog.DisplayNameFor(item.CardDefId)}［{item.PartId.Substring(0, System.Math.Min(10, item.PartId.Length))}］");
                _pendingPartIdsByDropdownIndex.Add(item.PartId);
            }
            _pendingChipDropdown.choices = pendingChoices;
            if (pendingChoices.Count == 0)
            {
                _pendingChipDropdown.SetValueWithoutNotify(string.Empty);
            }
            else if (_pendingChipDropdown.index < 0 || _pendingChipDropdown.index >= pendingChoices.Count)
            {
                _pendingChipDropdown.index = 0;
            }
        }

        private void RefreshEdgeAndFirmware()
        {
            if (_board == null)
            {
                _edgeListLabel.text = string.Empty;
                _firmware0Field.SetValueWithoutNotify(string.Empty);
                _firmware1Field.SetValueWithoutNotify(string.Empty);
                return;
            }
            _edgeListLabel.text = _board.Edges.Count == 0
                ? "无导线"
                : string.Join(", ", _board.Edges.OrderBy(e => e.From).ThenBy(e => e.To).Select(e => $"{e.From}→{e.To}"));
            _firmware0Field.SetValueWithoutNotify(_board.FirmwareSlots[0] ?? string.Empty);
            _firmware1Field.SetValueWithoutNotify(_board.FirmwareSlots[1] ?? string.Empty);
        }

        private void RefreshHistoryLabel()
        {
            _historyDepthLabel.text = _board == null ? "撤销0/重做0" : $"撤销{_board.UndoDepth}/重做{_board.RedoDepth}";
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
                    $"有效路径 {_lastPreview.PathCount} 条｜归一化总伤害 {_lastPreview.TotalNormalizedDamage:F1}｜{_lastPreview.ReactionHint}";
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
            if (_panelSettings != null)
            {
                GameModule.Resource.UnloadAsset(_panelSettings);
                _panelSettings = null;
            }
        }
    }
}
